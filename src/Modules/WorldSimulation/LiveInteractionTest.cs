using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int LiveTestSchemaVersion = 2;
        private const int LiveTestDefaultArmMinutes = 30;
        // Backgrounded Bannerlord and large campaign-census responses can
        // legitimately stretch the native polling interval. Keep liveness
        // aligned with the controller's existing 90-second crash boundary so
        // a recently received heartbeat is not rejected between native ticks.
        private const int LiveTestHeartbeatFreshSeconds = 90;
        private const int LiveTestLeaseSeconds = 45;
        private static readonly object LiveTestLock = new object();
        private static readonly HashSet<string> LiveTestModes = new HashSet<string>(
            new[] { "individual_chat", "party_chat", "social_event", "wilderness_event", "social_balance", "passive_world", "correspondence", "court_event", "spymaster", "ambassador_official", "kingdom_event", "campaign_command", "government", "party_agency" },
            StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LiveTestOperations = new HashSet<string>(
            new[]
            {
                "resident_native_approach", "resident_native_leave", "search_targets", "open", "send", "select_participants", "wait_approach",
                "next_phase", "close", "scene_boundary", "wait_for_memory", "checkpoint",
                "save_checkpoint", "shutdown_game", "prepare_wilderness", "restore_settlement",
                "prepare_party_fixture",
                "prepare_authority_fixture",
                "prepare_manipulation_fixture", "restore_manipulation_fixture", "confirm", "deny",
                "gauntlet_snapshot", "gauntlet_apply_fixture", "gauntlet_restore_fixture",
                "gauntlet_advance_time", "gauntlet_arrive", "gauntlet_depart",
                "gauntlet_save", "gauntlet_reload", "gauntlet_fault",
                "ui_open", "ui_status", "ui_snapshot", "ui_action", "ui_back", "ui_close",
                "spymaster_test", "spymaster_organic", "arrest_test", "rebellion_test", "capital_ambassador_test", "royal_council_test", "ruler_docket_test", "kingdom_event_test", "government_test", "campaign_command_test", "party_agency_test",
                "prepare_party_agency_fixture", "party_agency_advance_time",
                "party_agency_aggregate_fixtures", "party_agency_review_provider_result",
                "party_agency_recovery_fixture", "party_agency_verify_unrelated_state",
                "party_agency_prepare_hostility", "party_agency_hostile_battle",
                "party_agency_stage_save_phase", "party_agency_verify_save_phase",
                "social_snapshot", "social_reputation_profile", "social_set_town", "social_set_village", "social_set_hero",
                "social_set_relation", "social_set_ruler", "social_change_clan",
                "social_change_owner", "social_time_control", "social_record_outcome",
                "social_acknowledge_diplomacy_announcement",
                "social_set_underlying_affinity",
                "social_queue_rebellion_roll", "social_resolve_rebellion", "social_clear_overrides",
                "world_snapshot", "world_time_control", "world_advance", "wait_for_correspondence", "world_delete_checkpoint",
                "world_acknowledge_diplomacy_announcement",
                "world_acknowledge_native_diplomacy_notices",
                "world_drain_political_pressure"
            },
            StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LiveTestTerminalCommandStates = new HashSet<string>(
            new[] { "completed", "failed", "cancelled", "interrupted" },
            StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LiveTestTerminalRunStates = new HashSet<string>(
            new[] { "completed", "failed", "cancelled", "interrupted" },
            StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, object> LiveTestArmApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (!string.Equals(ReadString(payload, "confirmation", ""), "arm", StringComparison.OrdinalIgnoreCase))
                return LiveTestError("confirmation must be 'arm'.");

            int minutes = Math.Max(1, Math.Min(1440, ReadInt(payload, "minutes", LiveTestDefaultArmMinutes)));
            DateTimeOffset expires = DateTimeOffset.UtcNow.AddMinutes(minutes);
            Dictionary<string, object> arm = new Dictionary<string, object>
            {
                ["schemaVersion"] = LiveTestSchemaVersion,
                ["armId"] = "live-arm-" + Guid.NewGuid().ToString("N"),
                ["armedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                ["expiresUtc"] = expires.ToString("o"),
                ["requestedBy"] = ReadString(payload, "requestedBy", "ReignLiveTest")
            };
            lock (LiveTestLock) WriteJsonObject(LiveTestArmPath(), arm);
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["armed"] = true, ["armId"] = arm["armId"], ["expiresUtc"] = arm["expiresUtc"]
            };
        }

        private static Dictionary<string, object> LiveTestRuntimeApi(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string campaignId = query.TryGetValue("campaignId", out string requested) ? requested : "";
            Dictionary<string, object> runtime = LoadLiveTestRuntime(campaignId);
            Dictionary<string, object> arm = ReadJsonObject(LiveTestArmPath());
            bool armed = IsLiveTestArmed(arm);
            string runtimeCampaign = ReadString(runtime, "campaignId", campaignId);
            Dictionary<string, object> run = string.IsNullOrWhiteSpace(runtimeCampaign) ? null : FindActiveLiveTestRun(runtimeCampaign);
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["armed"] = armed,
                ["arm"] = arm,
                ["runtime"] = runtime,
                ["gameOnline"] = IsFreshLiveTestRuntime(runtime),
                ["activeRun"] = run == null
                    ? new Dictionary<string, object>()
                    : LiveTestRunSummary(run, false),
                ["serverUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                ["supportedModes"] = LiveTestModes.OrderBy(x => x).ToList()
            };
        }

        private static Dictionary<string, object> LiveTestHeartbeatApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            string gameInstanceId = ReadString(payload, "gameInstanceId", "");
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(gameInstanceId))
                return LiveTestError("campaignId and gameInstanceId are required.");

            lock (LiveTestLock)
            {
                Dictionary<string, object> currentRuntime = LoadLiveTestRuntime(campaignId);
                string currentInstance = ReadString(currentRuntime, "gameInstanceId", "");
                if (IsFreshLiveTestRuntime(currentRuntime)
                    && !string.IsNullOrWhiteSpace(currentInstance)
                    && !string.Equals(currentInstance, gameInstanceId, StringComparison.OrdinalIgnoreCase)
                    && CompareLiveTestGameInstanceOrder(gameInstanceId, currentInstance) < 0)
                {
                    Dictionary<string, object> currentArm = ReadJsonObject(LiveTestArmPath());
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["staleInstance"] = true,
                        ["error"] = "An older game instance attempted to replace the current campaign heartbeat.",
                        ["gameInstanceId"] = gameInstanceId,
                        ["authoritativeGameInstanceId"] = currentInstance,
                        ["armed"] = IsLiveTestArmed(currentArm),
                        ["armExpiresUtc"] = ReadString(currentArm, "expiresUtc", "")
                    };
                }

                Dictionary<string, object> active = FindActiveLiveTestRun(campaignId);
                string activeInstance = ReadString(active, "gameInstanceId", "");
                if (ShouldInterruptLiveTestRunForInstance(active, gameInstanceId))
                {
                    InterruptLiveTestRun(active,
                        "The run belonged to game instance " + activeInstance
                        + ", but the loaded campaign is now " + gameInstanceId + ".");
                }

                payload["schemaVersion"] = LiveTestSchemaVersion;
                payload["receivedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                WriteJsonObject(LiveTestRuntimePath(campaignId), payload);
                WriteJsonObject(LiveTestLatestRuntimePath(), payload);
                Dictionary<string, object> arm = ReadJsonObject(LiveTestArmPath());
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["armed"] = IsLiveTestArmed(arm),
                    ["armExpiresUtc"] = ReadString(arm, "expiresUtc", ""),
                    ["activeRunId"] = ReadString(active, "runId", "")
                };
            }
        }

        private static int CompareLiveTestGameInstanceOrder(string left, string right)
        {
            string leftStamp = LiveTestGameInstanceTimestamp(left);
            string rightStamp = LiveTestGameInstanceTimestamp(right);
            int timestampOrder = string.Compare(leftStamp, rightStamp, StringComparison.Ordinal);
            if (timestampOrder != 0) return timestampOrder;
            return string.Compare(left ?? "", right ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static string LiveTestGameInstanceTimestamp(string value)
        {
            const string prefix = "game-";
            if (string.IsNullOrWhiteSpace(value)
                || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || value.Length < prefix.Length + 14)
                return "";
            string stamp = value.Substring(prefix.Length, 14);
            return stamp.All(char.IsDigit) ? stamp : "";
        }

        private static Dictionary<string, object> LiveTestTargetSearchApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            string mode = NormalizeLiveTestMode(ReadString(payload, "mode", "individual_chat"));
            if (!LiveTestModes.Contains(mode)) return LiveTestError("Unsupported live-test mode.");
            Dictionary<string, object> searchStep =
                new Dictionary<string, object>
                {
                    ["schemaVersion"] = LiveTestSchemaVersion,
                    ["mode"] = mode,
                    ["operation"] = "search_targets",
                    ["search"] = ReadString(payload, "search", ""),
                    ["limit"] = Math.Max(
                        1, Math.Min(
                            2000, ReadInt(payload, "limit", 25))),
                    ["timeoutSeconds"] = 120
                };
            if (payload.ContainsKey("isLord"))
                searchStep["isLord"] =
                    ReadBool(payload, "isLord", false);
            if (payload.ContainsKey("minimumAge"))
                searchStep["minimumAge"] =
                    Math.Max(
                        0d, ReadDouble(payload, "minimumAge", 0d));
            if (payload.ContainsKey("minClanTier"))
                searchStep["minClanTier"] =
                    Math.Max(
                        0, ReadInt(payload, "minClanTier", 0));
            if (payload.ContainsKey("maxClanTier"))
                searchStep["maxClanTier"] =
                    Math.Max(
                        0, ReadInt(payload, "maxClanTier", 0));
            if (payload.ContainsKey("maxHonor"))
                searchStep["maxHonor"] =
                    Math.Max(
                        -2, Math.Min(
                            2, ReadInt(
                                payload, "maxHonor", 2)));
            Dictionary<string, object> startPayload = new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["gameInstanceId"] = ReadString(payload, "gameInstanceId", ""),
                ["mode"] = mode,
                ["effects"] = "guarded",
                ["presentation"] = "headless",
                ["autoCompleteWhenIdle"] = true,
                ["label"] = "Target search",
                // Create the command in the same locked write as the run. If an
                // empty auto-completing run is saved first, a fast game poll can
                // finish it before the follow-up enqueue and intermittently turn
                // a healthy roster into an empty target result.
                ["steps"] = new List<Dictionary<string, object>>
                {
                    searchStep
                }
            };
            string requestedRunId = ReadString(payload, "runId", "");
            if (!string.IsNullOrWhiteSpace(requestedRunId))
                startPayload["runId"] = requestedRunId;
            Dictionary<string, object> started =
                LiveTestRunStartApi(startPayload);
            if (!ReadBool(started, "ok", false)) return started;
            Dictionary<string, object> command =
                ReadDictionaryList(started, "commands").FirstOrDefault()
                ?? new Dictionary<string, object>();
            started["command"] = command;
            return started;
        }

        private static Dictionary<string, object> LiveTestRunStartApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (payload.ContainsKey("schemaVersion") && !new[] { 1, LiveTestSchemaVersion }.Contains(ReadInt(payload, "schemaVersion", LiveTestSchemaVersion)))
                return LiveTestError("Unsupported live-test schemaVersion. Expected 1 or " + LiveTestSchemaVersion.ToString(CultureInfo.InvariantCulture) + ".");
            string campaignId = ReadString(payload, "campaignId", "");
            if (string.IsNullOrWhiteSpace(campaignId)) return LiveTestError("campaignId is required.");
            if (!IsLiveTestArmed(ReadJsonObject(LiveTestArmPath()))) return LiveTestError("The live-test bridge is not armed or its arm window expired.");
            Dictionary<string, object> runtime = LoadLiveTestRuntime(campaignId);
            if (!IsFreshLiveTestRuntime(runtime)) return LiveTestError("No fresh loaded-game heartbeat exists for this campaign.");
            if (ReadBool(ReadDictionary(runtime, "saveSync"), "alignmentPending", false)) return LiveTestError("Save Sync alignment is still pending.");
            Dictionary<string, object> characterInitialization =
                ReadDictionary(runtime, "characterInitialization");
            if (!ReadBool(characterInitialization, "notableBackgroundReady", false)
                && !IsInitializationControlRun(payload))
                return LiveTestError(
                    "Campaign day-one character initialization is still pending; "
                    + "live interactions cannot start until every profile is confirmed.");

            string requestedInstance = ReadString(payload, "gameInstanceId", "");
            string liveInstance = ReadString(runtime, "gameInstanceId", "");
            if (!string.IsNullOrWhiteSpace(requestedInstance) && !string.Equals(requestedInstance, liveInstance, StringComparison.OrdinalIgnoreCase))
                return LiveTestError("The requested game instance is stale.");
            string requestedRunId = ReadString(payload, "runId", "");
            string mode = NormalizeLiveTestMode(ReadString(payload, "mode", "individual_chat"));
            if (!LiveTestModes.Contains(mode)) return LiveTestError("Unsupported live-test mode.");
            if (mode.Equals("social_balance", StringComparison.OrdinalIgnoreCase)
                && !ValidateSocialBalanceLiveEnrollment(payload, campaignId, out string enrollmentError))
                return LiveTestError(enrollmentError);

            lock (LiveTestLock)
            {
                if (!string.IsNullOrWhiteSpace(requestedRunId))
                {
                    Dictionary<string, object> existing =
                        LoadLiveTestRun(campaignId, requestedRunId);
                    if (existing.Count > 0)
                    {
                        string requestedQualificationId =
                            ReadString(payload, "qualificationId", "");
                        string requestedLabel =
                            ReadString(payload, "label", requestedRunId);
                        bool sameIdentity =
                            ReadString(existing, "mode", "").Equals(
                                mode, StringComparison.OrdinalIgnoreCase)
                            && ReadString(existing, "qualificationId", "").Equals(
                                requestedQualificationId,
                                StringComparison.OrdinalIgnoreCase)
                            && ReadString(existing, "label", "").Equals(
                                requestedLabel, StringComparison.Ordinal);
                        if (!sameIdentity)
                            return LiveTestError(
                                "The requested runId already exists with a different "
                                + "mode, qualification, or scenario label.");
                        Dictionary<string, object> reconciled =
                            LiveTestRunSummary(existing, true);
                        reconciled["idempotentStart"] = true;
                        return reconciled;
                    }
                }
                Dictionary<string, object> active = FindActiveLiveTestRun(campaignId);
                if (active != null) return new Dictionary<string, object>
                {
                    ["ok"] = false, ["error"] = "A live interaction run is already active for this campaign.", ["activeRunId"] = ReadString(active, "runId", "")
                };

                string runId = FirstNonEmpty(requestedRunId, "live-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Dictionary<string, object> run = new Dictionary<string, object>
                {
                    ["schemaVersion"] = LiveTestSchemaVersion,
                    ["runId"] = runId,
                    ["campaignId"] = campaignId,
                    ["gameInstanceId"] = liveInstance,
                    ["mode"] = mode,
                    ["label"] = ReadString(payload, "label", runId),
                    ["qualificationId"] = ReadString(payload, "qualificationId", ""),
                    ["qualificationSeed"] = ReadLong(payload, "qualificationSeed", ReadLong(payload, "seed", 0)),
                    ["gauntletRunId"] = ReadString(payload, "gauntletRunId", ""),
                    ["caseInstanceId"] = ReadString(payload, "caseInstanceId", ""),
                    ["gauntletCorrelationId"] = ReadString(payload, "correlationId", ""),
                    ["buildVersion"] = CurrentConversationBuildVersion(),
                    ["seed"] = ReadLong(payload, "seed", 0),
                    ["presentation"] = NormalizeLiveTestPresentation(ReadString(payload, "presentation", "headless")),
                    ["effects"] = NormalizeLiveTestEffects(ReadString(payload, "effects", "guarded")),
                    ["enrollment"] = ReadDictionary(payload, "enrollment"),
                    ["status"] = "running",
                    ["autoCompleteWhenIdle"] = payload.ContainsKey("autoCompleteWhenIdle")
                        ? ReadBool(payload, "autoCompleteWhenIdle", false)
                        : ReadDictionaryList(payload, "steps").Count > 0,
                    ["startedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["commands"] = new List<Dictionary<string, object>>(),
                    ["assertions"] = new List<Dictionary<string, object>>(),
                    ["failures"] = new List<Dictionary<string, object>>()
                };
                run["commands"] = NormalizeLiveTestScenarioCommands(run, payload);
                string gauntletRunId = ReadString(run, "gauntletRunId", "");
                string caseInstanceId = ReadString(run, "caseInstanceId", "");
                string gauntletCorrelationId =
                    ReadString(run, "gauntletCorrelationId", "");
                if (!string.IsNullOrWhiteSpace(gauntletRunId)
                    && !string.IsNullOrWhiteSpace(caseInstanceId)
                    && !string.IsNullOrWhiteSpace(gauntletCorrelationId))
                {
                    using (ReignDbConnection connection =
                        FinalGauntletStore.OpenControlConnection())
                        FinalConversationGauntletProviderBudget
                            .RegisterCorrelation(
                                connection,
                                gauntletRunId,
                                caseInstanceId,
                                gauntletCorrelationId);
                }
                SaveLiveTestRun(run);
                return LiveTestRunSummary(run, true);
            }
        }

        private static bool IsInitializationControlRun(Dictionary<string, object> payload)
        {
            List<Dictionary<string, object>> steps =
                ReadDictionaryList(payload ?? new Dictionary<string, object>(), "steps");
            if (steps.Count == 0) return false;
            HashSet<string> allowed = new HashSet<string>(
                new[] { "save_checkpoint", "shutdown_game", "wait_for_save_sync" },
                StringComparer.OrdinalIgnoreCase);
            return steps.All(step => allowed.Contains(ReadString(step, "operation", "")));
        }

        private static Dictionary<string, object> LiveTestCommandEnqueueApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            string runId = ReadString(payload, "runId", "");
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(runId)) return LiveTestError("campaignId and runId are required.");
            lock (LiveTestLock)
            {
                Dictionary<string, object> run = LoadLiveTestRun(campaignId, runId);
                if (run.Count == 0) return LiveTestError("Live interaction run was not found.");
                if (LiveTestTerminalRunStates.Contains(ReadString(run, "status", ""))) return LiveTestError("The run is already terminal.");
                Dictionary<string, object> command = NormalizeLiveTestCommand(run, payload);
                if (command == null) return LiveTestError("Unsupported or invalid live-test command.");
                List<Dictionary<string, object>> commands = ReadDictionaryList(run, "commands");
                string commandId = ReadString(command, "commandId", "");
                Dictionary<string, object> existing = commands.FirstOrDefault(x => string.Equals(ReadString(x, "commandId", ""), commandId, StringComparison.OrdinalIgnoreCase));
                if (existing == null) commands.Add(command); else command = existing;
                run["commands"] = commands;
                run["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                SaveLiveTestRun(run);
                Dictionary<string, object> summary = LiveTestRunSummary(run, false);
                summary["command"] = command;
                return summary;
            }
        }

        private static Dictionary<string, object> LiveTestRunPauseApi(Dictionary<string, object> payload, bool resume)
        {
            payload = payload ?? new Dictionary<string, object>();
            lock (LiveTestLock)
            {
                Dictionary<string, object> run = LoadLiveTestRun(ReadString(payload, "campaignId", ""), ReadString(payload, "runId", ""));
                if (run.Count == 0) return LiveTestError("Live interaction run was not found.");
                string status = ReadString(run, "status", "");
                if (resume && !status.Equals("paused", StringComparison.OrdinalIgnoreCase)) return LiveTestError("Only a paused run can resume.");
                if (!resume && LiveTestTerminalRunStates.Contains(status)) return LiveTestError("A terminal run cannot be paused.");
                Dictionary<string, object> runtime = LoadLiveTestRuntime(ReadString(run, "campaignId", ""));
                if (resume)
                {
                    if (!IsFreshLiveTestRuntime(runtime)) return LiveTestError("The loaded game is not reporting a fresh heartbeat.");
                    Dictionary<string, object> infrastructureCommand = ReadDictionaryList(run, "commands").FirstOrDefault(x =>
                        ReadString(x, "status", "").Equals("needs_input", StringComparison.OrdinalIgnoreCase)
                        && ReadString(x, "pauseReason", "").Equals("fatal_infrastructure_failure", StringComparison.OrdinalIgnoreCase));
                    if (infrastructureCommand != null)
                    {
                        infrastructureCommand["status"] = "queued";
                        infrastructureCommand["message"] = "Retry queued after the controller resumed the run.";
                        infrastructureCommand["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                        infrastructureCommand.Remove("leaseId");
                        infrastructureCommand.Remove("leaseExpiresUtc");
                        infrastructureCommand.Remove("completedUtc");
                    }
                    Dictionary<string, object> checkpoint = ReadDictionaryList(run, "commands").FirstOrDefault(x =>
                        ReadString(x, "operation", "").Equals("checkpoint", StringComparison.OrdinalIgnoreCase)
                        && ReadString(x, "status", "").Equals("needs_input", StringComparison.OrdinalIgnoreCase));
                    if (checkpoint != null)
                    {
                        checkpoint["status"] = "completed";
                        checkpoint["message"] = "Checkpoint resumed by the test controller.";
                        checkpoint["completedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                        checkpoint["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                    }
                    run["gameInstanceId"] = ReadString(runtime, "gameInstanceId", "");
                    run["status"] = "running";
                    run["resumedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                }
                else
                {
                    run["status"] = "paused";
                    run["pausedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                }
                run["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                SaveLiveTestRun(run);
                return LiveTestRunSummary(run, true);
            }
        }

        private static Dictionary<string, object> LiveTestRunCancelApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            lock (LiveTestLock)
            {
                Dictionary<string, object> run = LoadLiveTestRun(ReadString(payload, "campaignId", ""), ReadString(payload, "runId", ""));
                if (run.Count == 0) return LiveTestError("Live interaction run was not found.");
                run["status"] = "cancelled";
                run["cancelRequested"] = true;
                run["cancelledUtc"] = DateTimeOffset.UtcNow.ToString("o");
                run["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                foreach (Dictionary<string, object> command in ReadDictionaryList(run, "commands").Where(x => !LiveTestTerminalCommandStates.Contains(ReadString(x, "status", ""))))
                {
                    command["status"] = "cancelled";
                    command["completedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                }
                SaveLiveTestRun(run);
                return LiveTestRunSummary(run, true);
            }
        }

        private static Dictionary<string, object> LiveTestRunStatusApi(Dictionary<string, string> query, bool includeEvidence)
        {
            query = query ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string campaignId = query.TryGetValue("campaignId", out string campaign) ? campaign : ReadString(LoadLiveTestRuntime(""), "campaignId", "");
            string runId = query.TryGetValue("runId", out string requestedRun) ? requestedRun : "";
            Dictionary<string, object> run = string.IsNullOrWhiteSpace(runId) ? FindLatestLiveTestRun(campaignId) : LoadLiveTestRun(campaignId, runId);
            if (run == null || run.Count == 0) return new Dictionary<string, object> { ["ok"] = true, ["found"] = false, ["campaignId"] = campaignId };
            Dictionary<string, object> response = LiveTestRunSummary(run, true);
            response["found"] = true;
            if (includeEvidence) response["auditEvidence"] = CollectLiveTestAuditEvidence(run);
            return response;
        }

        private static Dictionary<string, object> LiveTestGamePollApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            string instanceId = ReadString(payload, "gameInstanceId", "");
            if (!IsLiveTestArmed(ReadJsonObject(LiveTestArmPath()))) return new Dictionary<string, object> { ["ok"] = true, ["found"] = false, ["armed"] = false };
            lock (LiveTestLock)
            {
                Dictionary<string, object> run = FindActiveLiveTestRun(campaignId);
                if (run == null || string.Equals(ReadString(run, "status", ""), "paused", StringComparison.OrdinalIgnoreCase))
                    return new Dictionary<string, object> { ["ok"] = true, ["found"] = false, ["armed"] = true };
                if (!string.Equals(ReadString(run, "gameInstanceId", ""), instanceId, StringComparison.OrdinalIgnoreCase))
                    return LiveTestError("The polling game instance does not own this run.");

                DateTimeOffset now = DateTimeOffset.UtcNow;
                bool conditionalCommandsSkipped =
                    SkipConditionalLiveTestCommandsWithSatisfiedDialogueActions(run, now);
                Dictionary<string, object> command = FirstLiveTestCommandInSequence(run);
                if (command == null || !IsLiveTestCommandLeaseEligible(command, now))
                {
                    if (conditionalCommandsSkipped) SaveLiveTestRun(run);
                    return new Dictionary<string, object> { ["ok"] = true, ["found"] = false, ["armed"] = true, ["runId"] = ReadString(run, "runId", "") };
                }
                if (ReadString(command, "operation", "").Equals("checkpoint", StringComparison.OrdinalIgnoreCase))
                {
                    command["status"] = "needs_input";
                    command["message"] = ReadString(command, "message", "Scenario checkpoint reached. Resume the run after any requested save or reload.");
                    command["updatedUtc"] = now.ToString("o");
                    run["status"] = "paused";
                    run["pausedUtc"] = now.ToString("o");
                    run["updatedUtc"] = now.ToString("o");
                    SaveLiveTestRun(run);
                    return new Dictionary<string, object> { ["ok"] = true, ["found"] = false, ["armed"] = true, ["runId"] = ReadString(run, "runId", ""), ["needsInput"] = true, ["command"] = command };
                }
                command["status"] = "leased";
                command["leaseId"] = Guid.NewGuid().ToString("N");
                command["leaseExpiresUtc"] = now.AddSeconds(LiveTestLeaseSeconds).ToString("o");
                command["attempt"] = ReadInt(command, "attempt", 0) + 1;
                command["updatedUtc"] = now.ToString("o");
                run["updatedUtc"] = now.ToString("o");
                SaveLiveTestRun(run);
                return new Dictionary<string, object> { ["ok"] = true, ["found"] = true, ["run"] = LiveTestRunSummary(run, false), ["command"] = command };
            }
        }

        private static bool IsLiveTestCommandLeaseEligible(
            Dictionary<string, object> command,
            DateTimeOffset now)
        {
            string state = ReadString(command, "status", "");
            if (state.Equals("queued", StringComparison.OrdinalIgnoreCase)) return true;
            if (!state.Equals("leased", StringComparison.OrdinalIgnoreCase)
                && !state.Equals("accepted", StringComparison.OrdinalIgnoreCase)
                && !state.Equals("running", StringComparison.OrdinalIgnoreCase))
                return false;
            return ParseLiveTestUtc(ReadString(command, "leaseExpiresUtc", "")) <= now;
        }

        private static Dictionary<string, object> FirstLiveTestCommandInSequence(
            Dictionary<string, object> run)
        {
            return ReadDictionaryList(run, "commands").FirstOrDefault(command =>
                !LiveTestTerminalCommandStates.Contains(ReadString(command, "status", "")));
        }

        private static bool SkipConditionalLiveTestCommandsWithSatisfiedDialogueActions(
            Dictionary<string, object> run,
            DateTimeOffset now)
        {
            List<Dictionary<string, object>> commands = ReadDictionaryList(run, "commands");
            bool skippedAny = false;
            while (true)
            {
                int candidateIndex = commands.FindIndex(command =>
                    !LiveTestTerminalCommandStates.Contains(ReadString(command, "status", "")));
                if (candidateIndex < 0) return skippedAny;
                Dictionary<string, object> candidate = commands[candidateIndex];
                string expectedAction = ReadString(
                    candidate, "skipWhenPriorDialogueActionQueued", "").Trim();
                if (string.IsNullOrWhiteSpace(expectedAction)
                    || !ReadString(candidate, "status", "").Equals(
                        "queued", StringComparison.OrdinalIgnoreCase))
                    return skippedAny;

                Dictionary<string, object> priorSend = commands.Take(candidateIndex)
                    .LastOrDefault(command =>
                        ReadString(command, "operation", "").Equals(
                            "send", StringComparison.OrdinalIgnoreCase));
                if (priorSend == null
                    || !ReadString(priorSend, "status", "").Equals(
                        "completed", StringComparison.OrdinalIgnoreCase))
                    return skippedAny;
                Dictionary<string, object> priorResult =
                    ReadDictionary(priorSend, "result") ?? new Dictionary<string, object>();
                Dictionary<string, object> priorRawResponse =
                    ReadDictionary(priorResult, "rawResponse") ?? new Dictionary<string, object>();
                bool actionQueued = ReadDictionaryList(priorRawResponse, "queuedDialogueActions")
                    .Concat(ReadDictionaryList(priorResult, "queuedDialogueActions"))
                    .Any(action => ReadString(action, "command", "").Equals(
                        expectedAction, StringComparison.OrdinalIgnoreCase));
                if (!actionQueued) return skippedAny;

                candidate["status"] = "completed";
                candidate["message"] = "Conditional natural-language follow-up skipped because the immediately preceding production turn already queued the exact expected dialogue action.";
                candidate["result"] = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["skipped"] = true,
                    ["skipReason"] = "prior_dialogue_action_already_queued",
                    ["matchedPriorCommandId"] = ReadString(priorSend, "commandId", ""),
                    ["matchedDialogueAction"] = expectedAction
                };
                candidate["updatedUtc"] = now.ToString("o");
                candidate["completedUtc"] = now.ToString("o");
                run["updatedUtc"] = now.ToString("o");
                run["commands"] = commands;
                skippedAny = true;
                EvaluateLiveTestCommandAssertions(run, candidate);
                CompleteLiveTestRunIfAppropriate(run, candidate);
            }
        }

        private static Dictionary<string, object> LiveTestGameAckApi(Dictionary<string, object> payload)
        {
            return UpdateLiveTestCommandFromGame(payload, false);
        }

        private static Dictionary<string, object> LiveTestGameResultApi(Dictionary<string, object> payload)
        {
            return UpdateLiveTestCommandFromGame(payload, true);
        }

        private static Dictionary<string, object> UpdateLiveTestCommandFromGame(Dictionary<string, object> payload, bool result)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            string runId = ReadString(payload, "runId", "");
            string commandId = ReadString(payload, "commandId", "");
            lock (LiveTestLock)
            {
                Dictionary<string, object> run = LoadLiveTestRun(campaignId, runId);
                if (run.Count == 0) return LiveTestError("Live interaction run was not found.");
                Dictionary<string, object> command = ReadDictionaryList(run, "commands").FirstOrDefault(x => string.Equals(ReadString(x, "commandId", ""), commandId, StringComparison.OrdinalIgnoreCase));
                if (command == null) return LiveTestError("Live interaction command was not found.");
                if (result && LiveTestTerminalCommandStates.Contains(ReadString(command, "status", "")))
                    return new Dictionary<string, object> { ["ok"] = true, ["idempotent"] = true, ["command"] = command };
                if (result && ReadString(command, "status", "").Equals("needs_input", StringComparison.OrdinalIgnoreCase)
                    && ReadString(command, "pauseReason", "").Equals("fatal_infrastructure_failure", StringComparison.OrdinalIgnoreCase))
                    return new Dictionary<string, object> { ["ok"] = true, ["idempotent"] = true, ["command"] = command };

                string status = ReadString(payload, "status", result ? "completed" : "accepted").ToLowerInvariant();
                if (!new[] { "accepted", "running", "needs_input", "completed", "failed", "cancelled", "interrupted" }.Contains(status))
                    status = result ? "failed" : "accepted";
                string reportedError = ReadString(payload, "error", ReadString(payload, "message", "Command failed."));
                bool fatalInfrastructureFailure = result && status == "failed" && IsFatalLiveTestInfrastructureFailure(reportedError);
                if (fatalInfrastructureFailure) status = "needs_input";
                command["status"] = status;
                command["message"] = fatalInfrastructureFailure
                    ? "Paused because the configured LLM provider cannot currently accept requests. Resume after correcting provider access or balance."
                    : ReadString(payload, "message", "");
                command["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                if (result)
                {
                    command["result"] = fatalInfrastructureFailure
                        ? new Dictionary<string, object> { ["fatalInfrastructureFailure"] = true, ["error"] = LimitText(reportedError, 2000) }
                        : ReadDictionary(payload, "result") ?? new Dictionary<string, object>();
                    command["error"] = fatalInfrastructureFailure ? LimitText(reportedError, 2000) : ReadString(payload, "error", "");
                    command["correlationIds"] = ReadStringList(payload, "correlationIds");
                    if (fatalInfrastructureFailure)
                    {
                        command["pauseReason"] = "fatal_infrastructure_failure";
                        command["lastFailureUtc"] = DateTimeOffset.UtcNow.ToString("o");
                        List<Dictionary<string, object>> infrastructureFailures = ReadDictionaryList(run, "infrastructureFailures");
                        infrastructureFailures.Add(new Dictionary<string, object>
                        {
                            ["commandId"] = commandId, ["error"] = LimitText(reportedError, 2000),
                            ["utc"] = DateTimeOffset.UtcNow.ToString("o")
                        });
                        run["infrastructureFailures"] = infrastructureFailures;
                        run["status"] = "paused";
                        run["pausedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                        run["pauseReason"] = "fatal_infrastructure_failure";
                        run["message"] = "Live test paused after a fatal provider infrastructure response. Correct provider access or balance, then resume to retry the same exactly-once command.";
                    }
                    else
                    {
                        command["completedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                    }
                    if (status == "failed")
                    {
                        List<Dictionary<string, object>> failures = ReadDictionaryList(run, "failures");
                        failures.Add(new Dictionary<string, object> { ["commandId"] = commandId, ["error"] = reportedError, ["utc"] = DateTimeOffset.UtcNow.ToString("o") });
                        run["failures"] = failures;
                    }
                    else if (status == "completed")
                    {
                        EvaluateLiveTestCommandAssertions(run, command);
                    }
                }
                run["commands"] = ReadDictionaryList(run, "commands");
                run["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                if (result && !fatalInfrastructureFailure)
                {
                    RecordSocialBalanceProfileResultIfApplicable(run, command);
                    CompleteLiveTestRunIfAppropriate(run, command);
                }
                SaveLiveTestRun(run);
                return new Dictionary<string, object> { ["ok"] = true, ["command"] = command, ["run"] = LiveTestRunSummary(run, false) };
            }
        }

        private static void EvaluateLiveTestCommandAssertions(Dictionary<string, object> run, Dictionary<string, object> command)
        {
            Dictionary<string, object> requested = ReadDictionary(command, "assertions") ?? new Dictionary<string, object>();
            if (requested.Count == 0) return;
            Dictionary<string, object> result = ReadDictionary(command, "result") ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> replies = ReadDictionaryList(result, "replies");
            string individualText = FirstNonEmpty(ReadString(result, "reply", ""), ReadString(result, "text", ""));
            if (replies.Count == 0 && !string.IsNullOrWhiteSpace(individualText))
            {
                Dictionary<string, object> raw = ReadDictionary(result, "rawResponse") ?? new Dictionary<string, object>();
                replies.Add(new Dictionary<string, object>
                {
                    ["text"] = individualText,
                    ["heroId"] = FirstNonEmpty(
                        ReadFirstString(result, "heroId", "speakerHeroStringId"),
                        ReadFirstString(raw, "heroStringId", "speakerHeroStringId")),
                    ["heroName"] = ReadFirstString(result, "heroName", "speakerName"),
                    ["selectedContextPulls"] = ReadLiveTestContextPullIds(result, "selectedContextPulls"),
                    ["contextBundles"] = ReadDictionaryList(result, "contextBundles"),
                    ["queuedActionCount"] = ReadInt(result, "queuedActionCount", 0),
                    ["correlationId"] = ReadString(result, "correlationId", ""),
                    ["sessionId"] = ReadString(result, "sessionId", ""),
                    ["exchangeId"] = ReadString(result, "exchangeId", ""),
                    ["turnIds"] = ReadStringList(result, "turnIds"),
                    ["memoryWrites"] = ReadDictionaryList(result, "memoryWrites"),
                    ["dynamicCharacteristicWrites"] = ReadDictionaryList(raw, "dynamicCharacteristicWrites"),
                    ["dynamicCharacteristicsStore"] = ReadDictionary(raw, "dynamicCharacteristicsStore")
                        ?? new Dictionary<string, object>(),
                    ["relationshipEffects"] = ReadDictionaryList(raw, "conversationRelationshipEffects"),
                    ["timing"] = ReadDictionary(raw, "timing") ?? new Dictionary<string, object>(),
                    ["rawResponse"] = raw
                });
            }
            string combined = string.Join("\n", replies.Select(reply => ReadString(reply, "text", "")));
            List<Dictionary<string, object>> rows = ReadDictionaryList(run, "assertions");
            List<Dictionary<string, object>> failures = ReadDictionaryList(run, "failures");
            string commandId = ReadString(command, "commandId", "");
            bool criticalRequested = ReadBool(requested, "critical", false);
            List<Dictionary<string, object>> auditEvidence = null;
            Action<string, object, bool, string> record = (name, expected, passed, evidence) =>
            {
                Dictionary<string, object> row = new Dictionary<string, object>
                {
                    ["commandId"] = commandId, ["assertion"] = name, ["expected"] = expected,
                    ["passed"] = passed, ["evidence"] = LimitText(evidence, 1200),
                    ["utc"] = DateTimeOffset.UtcNow.ToString("o")
                };
                rows.Add(row);
                if (!passed)
                {
                    bool criticalFailure = criticalRequested && new[]
                    {
                        "minReplies", "requiresCorrelationIds", "requiresStructuralEvidence",
                        "forbidQueuedActions", "requiresGroundedGuardedActionRouting", "forbidSelfReaction",
                        "requiresRelationshipReceipt", "requiresSceneMemoryArtifacts",
                        "requiresQualificationMemoryCompletion",
                        "requiresSharedRelationshipHistoryEvidence",
                        "requiresSharedRelationshipHistoryPersistence"
                    }.Contains(name, StringComparer.OrdinalIgnoreCase);
                    failures.Add(new Dictionary<string, object>
                    {
                        ["commandId"] = commandId, ["assertion"] = name,
                        ["error"] = "Live interaction assertion failed: " + name,
                        ["evidence"] = LimitText(evidence, 1200), ["critical"] = criticalFailure,
                        ["utc"] = DateTimeOffset.UtcNow.ToString("o")
                    });
                }
            };
            if (requested.ContainsKey("minReplies"))
            {
                int expected = Math.Max(1, ReadInt(requested, "minReplies", 1));
                record("minReplies", expected, replies.Count >= expected, "Reply count: " + replies.Count);
            }
            if (ReadBool(requested, "requiresCorrelationIds", false))
            {
                List<string> correlations = ReadStringList(command, "correlationIds");
                record("requiresCorrelationIds", true, correlations.Count > 0,
                    "Correlation count: " + correlations.Count);
            }
            if (ReadBool(requested, "requiresSceneMemoryArtifacts", false))
            {
                bool valid = LiveTestSceneMemoryArtifactsAreValid(run, command, out string memoryEvidence);
                record("requiresSceneMemoryArtifacts", true, valid, memoryEvidence);
            }
            if (ReadBool(requested, "requiresQualificationMemoryCompletion", false))
            {
                bool valid = LiveTestQualificationMemoryCompletionIsValid(run, command, out string memoryEvidence);
                record("requiresQualificationMemoryCompletion", true, valid, memoryEvidence);
            }
            if (ReadBool(requested, "forbidQueuedActions", false))
            {
                int queued = replies.Sum(reply => ReadInt(reply, "queuedActionCount", 0));
                record("forbidQueuedActions", true, queued == 0, "Queued action count: " + queued);
            }
            if (ReadBool(requested, "requiresGroundedGuardedActionRouting", false))
            {
                auditEvidence = CollectLiveTestCommandAuditEvidence(run, command);
                bool safe = LiveTestGroundedGuardedActionsAreSafe(
                    run, replies, auditEvidence, out string actionEvidence);
                record("requiresGroundedGuardedActionRouting", true, safe, actionEvidence);
            }
            if (ReadBool(requested, "forbidSelfReaction", false))
            {
                bool self = replies.Any(reply =>
                {
                    string speaker = ReadFirstString(reply, "heroId", "speakerHeroStringId");
                    string target = ReadFirstString(reply, "reactionTargetHeroStringId", "reactionTarget");
                    return !string.IsNullOrWhiteSpace(speaker)
                        && speaker.Equals(target, StringComparison.OrdinalIgnoreCase);
                });
                record("forbidSelfReaction", true, !self, self ? "A reply targeted its own speaker." : "No self-reaction was present.");
            }
            if (ReadBool(requested, "requiresStructuralEvidence", false))
            {
                string mode = ReadString(command, "mode", "");
                bool replyShape = replies.Count > 0 && replies.All(reply =>
                    !string.IsNullOrWhiteSpace(ReadString(reply, "text", ""))
                    && (!string.IsNullOrWhiteSpace(ReadFirstString(reply, "heroId", "speakerHeroStringId"))
                        || mode.Equals("individual_chat", StringComparison.OrdinalIgnoreCase)));
                bool sessionShape;
                if (mode.Equals("individual_chat", StringComparison.OrdinalIgnoreCase))
                    sessionShape = !string.IsNullOrWhiteSpace(ReadString(result, "sessionId", ""))
                        && !string.IsNullOrWhiteSpace(ReadString(result, "exchangeId", ""))
                        && ReadStringList(result, "turnIds").Count == 2;
                else if (mode.Equals("party_chat", StringComparison.OrdinalIgnoreCase))
                    sessionShape = !string.IsNullOrWhiteSpace(ReadString(result, "sessionId", ""))
                        && !string.IsNullOrWhiteSpace(ReadString(result, "exchangeId", ""));
                else
                    sessionShape = !string.IsNullOrWhiteSpace(ReadString(result, "turnId", ""));
                record("requiresStructuralEvidence", true, replyShape && sessionShape,
                    "mode=" + mode + "; replies=" + replies.Count + "; session="
                    + ReadString(result, "sessionId", "") + "; exchange=" + ReadString(result, "exchangeId", "")
                    + "; turn=" + ReadString(result, "turnId", ""));
            }
            if (requested.ContainsKey("maxPromptBuildMs"))
            {
                int maximum = Math.Max(1, ReadInt(requested, "maxPromptBuildMs", 2000));
                List<long> values = replies.Select(LiveTestReplyTiming)
                    .Select(timing => ReadLong(timing, "promptBuildMs", -1)).ToList();
                record("maxPromptBuildMs", maximum,
                    values.Count > 0 && values.All(value => value >= 0 && value <= maximum),
                    "Prompt-build milliseconds: " + string.Join(", ", values));
            }
            if (requested.ContainsKey("maxPromptChars"))
            {
                int maximum = Math.Max(1000, ReadInt(requested, "maxPromptChars", 100000));
                List<long> values = replies.Select(LiveTestReplyTiming)
                    .Select(timing => ReadLong(timing, "promptChars", -1)).ToList();
                record("maxPromptChars", maximum,
                    values.Count > 0 && values.All(value => value > 0 && value <= maximum),
                    "Prompt characters: " + string.Join(", ", values));
            }
            if (ReadBool(requested, "requiresDynamicCharacteristicWrite", false))
            {
                int writes = replies.Sum(reply =>
                {
                    Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? new Dictionary<string, object>();
                    Dictionary<string, object> store = ReadDictionary(raw, "dynamicCharacteristicsStore")
                        ?? ReadDictionary(reply, "dynamicCharacteristicsStore")
                        ?? new Dictionary<string, object>();
                    return ReadInt(store, "storedCount", 0);
                });
                record("requiresDynamicCharacteristicWrite", true, writes > 0,
                    "Dynamic-characteristic writes stored: " + writes);
            }
            if (ReadBool(requested, "requiresDynamicCharacteristicIntegrity", false))
            {
                List<string> uncaptured = new List<string>();
                List<string> injectedCharacteristics = CollectLiveTestCommandAuditEvidence(run, command)
                    .Where(item => ReadString(item, "phase", "").Equals("prompt.built", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(item => LiveTestDynamicCharacteristicPromptLines(
                        ReadDictionary(item, "data") ?? new Dictionary<string, object>()))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (Dictionary<string, object> reply in replies)
                {
                    Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? reply;
                    Dictionary<string, object> store = ReadDictionary(raw, "dynamicCharacteristicsStore")
                        ?? ReadDictionary(reply, "dynamicCharacteristicsStore")
                        ?? new Dictionary<string, object>();
                    string text = FirstNonEmpty(ReadString(reply, "text", ""), ReadString(raw, "reply", ""));
                    bool reiteratesInjected = injectedCharacteristics.Any(line => HasDynamicTextEvidence(line, text));
                    if (ReadInt(store, "storedCount", 0) <= 0
                        && ReplyLikelyIntroducesDynamicCharacteristic(text)
                        && !reiteratesInjected)
                        uncaptured.Add(LimitText(text, 240));
                }
                record("requiresDynamicCharacteristicIntegrity", true, uncaptured.Count == 0,
                    uncaptured.Count == 0
                        ? "Every new eligible self-detail was stored; reiteration of injected soft canon correctly required no duplicate."
                        : "Eligible unstored self-details: " + string.Join(" | ", uncaptured));
            }
            if (ReadBool(requested, "requiresPersonalityEvidence", false))
            {
                bool grounded = replies.Count > 0 && replies.All(reply =>
                {
                    Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? reply;
                    Dictionary<string, object> brief = ReadDictionary(raw, "decisionBrief") ?? new Dictionary<string, object>();
                    Dictionary<string, object> motive = ReadDictionary(raw, "motiveDecision") ?? new Dictionary<string, object>();
                    Dictionary<string, object> motiveOutcome = ReadDictionary(raw, "motiveOutcome") ?? new Dictionary<string, object>();
                    List<string> activeDomains = ReadStringList(brief, "activeDomains");
                    bool romanceRelevant = activeDomains.Contains("romance", StringComparer.OrdinalIgnoreCase);
                    string visible = FirstNonEmpty(ReadString(reply, "text", ""), ReadString(raw, "reply", ""));
                    bool romanticLanguage = ConversationReplyContainsRomanticAction(visible);
                    bool romanticDetected = ReadBool(motiveOutcome, "romanticActionDetected", false);
                    return ReadStringList(brief, "goals").Count > 0
                        && ReadStringList(brief, "constraints").Count > 0
                        && ReadStringList(brief, "appliedEvidenceKeys").Count > 0
                        && ReadBool(brief, "evidenceComplete", false)
                        && (romanceRelevant
                            || ReadString(brief, "postureAlignment", "").Equals("not_applicable", StringComparison.OrdinalIgnoreCase))
                        && romanticDetected == romanticLanguage
                        && ReadDictionaryList(motive, "activeDomains").Count > 0
                        && (ReadDictionary(motive, "highlightedScores") ?? new Dictionary<string, object>()).Count > 0;
                });
                record("requiresPersonalityEvidence", true, grounded,
                    grounded
                        ? "Every reply carried motive-, trait-, and constraint-grounded private decision evidence."
                        : "One or more replies lacked motive-, trait-, or constraint-grounded private decision evidence.");
            }
            if (ReadBool(requested, "requiresClanTierRecognitionEvidence", false))
            {
                bool grounded = replies.Count > 0 && replies.All(reply =>
                {
                    Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? reply;
                    Dictionary<string, object> identity = ReadDictionary(raw, "identityView")
                        ?? ReadDictionary(reply, "identityView") ?? new Dictionary<string, object>();
                    Dictionary<string, object> motive = ReadDictionary(raw, "motiveDecision")
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> opportunity = ReadDictionary(motive, "opportunity")
                        ?? new Dictionary<string, object>();
                    string state = ReadString(identity, "identityState", "");
                    bool known = new[] { "verified", "known", "recognized", "introduced" }
                        .Contains(state, StringComparer.OrdinalIgnoreCase);
                    string basis = ReadString(opportunity, "identityBasis", "");
                    Dictionary<string, object> axes = ReadDictionary(opportunity, "axes")
                        ?? new Dictionary<string, object>();
                    bool targetTierKnown = ReadBool(opportunity, "targetClanTierKnown", false);
                    string tierBand = ReadString(opportunity, "relativeClanTierBand", "");
                    bool tierEvidenceConsistent;
                    if (known)
                    {
                        int observerTier = ReadInt(opportunity, "observerClanTier", -1);
                        int targetTier = ReadInt(opportunity, "targetClanTier", -1);
                        int delta = ReadInt(opportunity, "clanTierDelta", int.MinValue);
                        tierEvidenceConsistent = targetTierKnown
                            && observerTier >= 0 && observerTier <= 6
                            && targetTier >= 0 && targetTier <= 6
                            && delta == targetTier - observerTier
                            && tierBand.Equals(RelativeClanTierBand(delta), StringComparison.OrdinalIgnoreCase);
                        int expectedObserverTier = ReadInt(
                            requested,
                            "expectedObserverClanTier",
                            int.MinValue);
                        int expectedPlayerTier = ReadInt(
                            requested,
                            "expectedPlayerClanTier",
                            int.MinValue);
                        string expectedTierBand = ReadString(
                            requested,
                            "expectedRelativeClanTierBand", "");
                        tierEvidenceConsistent =
                            tierEvidenceConsistent
                            && (expectedObserverTier == int.MinValue
                                || observerTier
                                    == expectedObserverTier)
                            && (expectedPlayerTier == int.MinValue
                                || targetTier
                                    == expectedPlayerTier)
                            && (string.IsNullOrWhiteSpace(
                                    expectedTierBand)
                                || tierBand.Equals(
                                    expectedTierBand,
                                    StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        tierEvidenceConsistent = !targetTierKnown
                            && tierBand.Equals("unverified_identity", StringComparison.OrdinalIgnoreCase)
                            && !opportunity.ContainsKey("targetClanTier")
                            && !opportunity.ContainsKey("clanTierDelta");
                    }
                    return axes.Count == 5
                        && opportunity.ContainsKey("relativeBenefit")
                        && tierEvidenceConsistent
                        && (known
                            ? basis.Equals("known_public_identity", StringComparison.OrdinalIgnoreCase)
                            : basis.Equals("visible_presentation_only", StringComparison.OrdinalIgnoreCase));
                });
                record("requiresClanTierRecognitionEvidence", true, grounded,
                    grounded
                        ? "Every reply used relative status axes with the correct observer-specific identity gate."
                        : "One or more replies lacked relative clan-status evidence or exposed true status before recognition.");
            }
            if (ReadBool(requested, "requiresManipulationEvidence", false))
            {
                bool grounded = replies.Count > 0 && replies.All(reply =>
                {
                    Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? reply;
                    Dictionary<string, object> motive = ReadDictionary(raw, "motiveDecision")
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> brief = ReadDictionary(raw, "decisionBrief")
                        ?? new Dictionary<string, object>();
                    bool intrigue = ReadDictionaryList(motive, "activeDomains").Any(domain =>
                        ReadString(domain, "id", "").Equals("intrigue_manipulation", StringComparison.OrdinalIgnoreCase));
                    Dictionary<string, object> highlighted = ReadDictionary(motive, "highlightedScores")
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> opportunity = ReadDictionary(motive, "opportunity")
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> manipulation = ReadDictionary(motive, "manipulation")
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> courtCharacter =
                        ReadDictionary(motive, "courtCharacter")
                        ?? new Dictionary<string, object>();
                    bool recommended = ReadBool(manipulation, "recommended", false);
                    string preferredTactic = ReadString(manipulation, "preferredTactic", "");
                    string expectedConduct = ReadString(manipulation, "expectedConduct", "");
                    List<string> allowedCourtTactics = ReadStringList(
                        manipulation, "allowedCourtTactics");
                    string appliedCourtTactic = ReadString(
                        brief, "courtTactic", "none");
                    bool conductConsistent = recommended
                        ? !string.IsNullOrWhiteSpace(preferredTactic)
                            && preferredTactic != "none"
                            && allowedCourtTactics.Contains(
                                preferredTactic,
                                StringComparer.OrdinalIgnoreCase)
                            && expectedConduct.Equals(preferredTactic, StringComparison.OrdinalIgnoreCase)
                            && appliedCourtTactic != "none"
                            && allowedCourtTactics.Contains(
                                appliedCourtTactic,
                                StringComparer.OrdinalIgnoreCase)
                        : new[]
                        {
                            "direct_or_restrained",
                            "dismissive_boundary",
                            "cautious_deference_without_manipulation"
                        }.Contains(expectedConduct, StringComparer.OrdinalIgnoreCase)
                            && (appliedCourtTactic == "none"
                                || string.IsNullOrWhiteSpace(
                                    appliedCourtTactic));
                    bool lowHonorConsistent =
                        !ReadBool(requested,
                            "requiresLowHonorCourtCharacter", false)
                        || (ReadBool(courtCharacter,
                                "available", false)
                            && ReadBool(courtCharacter,
                                "supportsManipulation", false)
                            && ReadInt(courtCharacter,
                                "honorLevel", 0) < 0);
                    string expectedCell = ReadString(
                        requested, "expectedCourtCharacterCell", "");
                    bool cellConsistent =
                        string.IsNullOrWhiteSpace(expectedCell)
                        || (ReadString(courtCharacter, "cellId", "")
                                .Equals(expectedCell,
                                    StringComparison.OrdinalIgnoreCase)
                            && ReadInt(courtCharacter,
                                "honorLevel", int.MinValue)
                                == ReadInt(requested,
                                    "expectedCourtHonorLevel",
                                    int.MinValue)
                            && ReadInt(courtCharacter,
                                "boldnessLevel", int.MinValue)
                                == ReadInt(requested,
                                    "expectedCourtBoldnessLevel",
                                    int.MinValue));
                    string expectedTierBand = ReadString(
                        requested,
                        "expectedRelativeClanTierBand", "");
                    bool tierConsistent =
                        string.IsNullOrWhiteSpace(expectedTierBand)
                        || ReadString(opportunity,
                                "relativeClanTierBand", "")
                            .Equals(expectedTierBand,
                                StringComparison.OrdinalIgnoreCase);
                    string expectedWealthBasis = ReadString(
                        requested,
                        "expectedWealthEvidenceBasis", "");
                    bool wealthConsistent =
                        string.IsNullOrWhiteSpace(
                            expectedWealthBasis)
                        || (ReadString(opportunity,
                                "wealthEvidenceBasis", "")
                                .Equals(expectedWealthBasis,
                                    StringComparison.OrdinalIgnoreCase)
                            && (!ReadBool(requested,
                                    "hiddenWealthControl", false)
                                || !ReadBool(opportunity,
                                    "economicCapacityKnown", true)));
                    return intrigue
                        && highlighted.ContainsKey("ambition")
                        && highlighted.ContainsKey("honesty")
                        && highlighted.ContainsKey("tact")
                        && opportunity.ContainsKey("relativeBenefit")
                        && !string.IsNullOrWhiteSpace(ReadString(manipulation, "relativeClanTierBand", ""))
                        && conductConsistent
                        && lowHonorConsistent
                        && cellConsistent
                        && tierConsistent
                        && wealthConsistent
                        && ReadString(manipulation,
                            "courtCharacterCell", "")
                            .Equals(ReadString(
                                courtCharacter, "cellId", ""),
                                StringComparison.OrdinalIgnoreCase)
                        && manipulation.ContainsKey(
                            "bestOpportunityAxis")
                        && manipulation.ContainsKey(
                            "bestOpportunityGain")
                        && ReadStringList(brief, "appliedEvidenceKeys")
                            .Contains("opportunity.relative", StringComparer.OrdinalIgnoreCase);
                });
                record("requiresManipulationEvidence", true, grounded,
                    grounded
                        ? "Every reply considered ambition, scruples, tact, relationship, and relative opportunity before choosing influence or directness."
                        : "One or more replies lacked a coherent low-honor Court Character, tier/wealth opportunity, permitted tactic, or restraint decision.");
            }
            if (ReadBool(requested,
                "requiresLowHonorCourtCharacter", false))
            {
                bool lowHonor = replies.Count > 0
                    && replies.All(reply =>
                    {
                        Dictionary<string, object> raw =
                            ReadDictionary(reply, "rawResponse")
                            ?? reply;
                        Dictionary<string, object> motive =
                            ReadDictionary(raw, "motiveDecision")
                            ?? new Dictionary<string, object>();
                        Dictionary<string, object> court =
                            ReadDictionary(motive,
                                "courtCharacter")
                            ?? new Dictionary<string, object>();
                        return ReadBool(court, "available", false)
                            && ReadBool(court,
                                "supportsManipulation", false)
                            && ReadInt(court,
                                "honorLevel", 0) < 0;
                    });
                record("requiresLowHonorCourtCharacter",
                    true, lowHonor,
                    lowHonor
                        ? "Every reply used an adult low-honor Court Character cell."
                        : "At least one reply lacked an available low-honor Court Character cell.");
            }
            if (ReadBool(requested, "forbidVerifiedLiePenalty", false))
            {
                List<Dictionary<string, object>> assessments = replies.SelectMany(LiveTestReplyRelationshipAssessments).ToList();
                bool invalid = assessments.Any(assessment =>
                    !string.IsNullOrWhiteSpace(ReadString(assessment, "lieCheckId", ""))
                    || ReadString(assessment, "actKind", "").IndexOf("caught", StringComparison.OrdinalIgnoreCase) >= 0);
                record("forbidVerifiedLiePenalty", true, !invalid,
                    "Assessment count=" + assessments.Count + "; verified/caught=" + invalid);
            }
            if (ReadBool(requested, "requiresVerifiedLieCheck", false))
            {
                List<Dictionary<string, object>> assessments = replies.SelectMany(LiveTestReplyRelationshipAssessments).ToList();
                bool verified = assessments.Any(assessment =>
                    !string.IsNullOrWhiteSpace(ReadString(assessment, "lieCheckId", "")));
                record("requiresVerifiedLieCheck", true, verified,
                    "Assessment count=" + assessments.Count + "; verified=" + verified);
            }
            if (ReadBool(requested, "requiresPromptEvidence", false)
                || ReadBool(requested, "requiresRetrievalEvidence", false)
                || ReadBool(requested, "requiresDynamicCharacteristicRecall", false)
                || ReadBool(requested, "requiresDynamicCharacteristicOverflowSelection", false)
                || ReadBool(requested, "requiresRelationshipReceipt", false)
                || ReadBool(requested, "requiresSharedRelationshipHistoryEvidence", false)
                || ReadBool(requested, "requiresSharedRelationshipHistoryPersistence", false)
                || ReadBool(requested, "requiresGroundedGuardedActionRouting", false)
                || ReadStringList(requested, "promptExcludes").Count > 0
                || ReadStringList(requested, "requiredRetrievedPhrases").Count > 0
                || ReadStringList(requested, "forbiddenRetrievedPhrases").Count > 0)
            {
                auditEvidence = auditEvidence ?? CollectLiveTestCommandAuditEvidence(run, command);
            }
            if (ReadBool(requested, "requiresPromptEvidence", false))
            {
                List<Dictionary<string, object>> promptRows = (auditEvidence ?? new List<Dictionary<string, object>>())
                    .Where(row => ReadString(row, "phase", "").Equals("prompt.built", StringComparison.OrdinalIgnoreCase)).ToList();
                bool present = promptRows.Any(row =>
                {
                    Dictionary<string, object> data = ReadDictionary(row, "data") ?? new Dictionary<string, object>();
                    return ReadLong(data, "promptChars", 0) > 0
                        && (data.ContainsKey("promptEvidence") || data.ContainsKey("promptEnvelope"));
                });
                record("requiresPromptEvidence", true, present,
                    "Prompt audit rows: " + promptRows.Count);
            }
            foreach (string phrase in ReadStringList(requested, "promptExcludes"))
            {
                List<Dictionary<string, object>> promptRows = (auditEvidence ?? new List<Dictionary<string, object>>())
                    .Where(row => ReadString(row, "phase", "").Equals("prompt.built", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                string promptText = string.Join("\n", promptRows.SelectMany(row =>
                {
                    Dictionary<string, object> data = ReadDictionary(row, "data") ?? new Dictionary<string, object>();
                    return ReadDictionaryList(data, "messages")
                        .Select(message => ReadString(message, "content", ""));
                }));
                record("promptExcludes", phrase,
                    promptRows.Count > 0
                    && promptText.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) < 0,
                    "Prompt audit rows=" + promptRows.Count + "; prompt characters=" + promptText.Length);
            }
            if (ReadBool(requested, "requiresDynamicCharacteristicRecall", false))
            {
                List<string> characteristicLines = new List<string>();
                foreach (Dictionary<string, object> row in (auditEvidence ?? new List<Dictionary<string, object>>())
                    .Where(item => ReadString(item, "phase", "").Equals("prompt.built", StringComparison.OrdinalIgnoreCase)))
                {
                    Dictionary<string, object> data = ReadDictionary(row, "data") ?? new Dictionary<string, object>();
                    characteristicLines.AddRange(LiveTestDynamicCharacteristicPromptLines(data));
                }
                bool recalled = characteristicLines.Any(line => HasDynamicTextEvidence(line, combined));
                record("requiresDynamicCharacteristicRecall", true, recalled,
                    "Injected Dynamic Characteristic lines=" + characteristicLines.Count
                    + "; grounded recall=" + recalled);
            }
            if (ReadBool(requested, "requiresDynamicCharacteristicOverflowSelection", false))
            {
                int minimumActive = Math.Max(17, ReadInt(requested, "minimumActiveDynamicCharacteristics", 17));
                List<string> characteristicLines = new List<string>();
                foreach (Dictionary<string, object> row in (auditEvidence ?? new List<Dictionary<string, object>>())
                    .Where(item => ReadString(item, "phase", "").Equals("prompt.built", StringComparison.OrdinalIgnoreCase)))
                {
                    Dictionary<string, object> data = ReadDictionary(row, "data") ?? new Dictionary<string, object>();
                    characteristicLines.AddRange(LiveTestDynamicCharacteristicPromptLines(data));
                }
                List<string> heroIds = replies.Select(reply =>
                {
                    Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? reply;
                    return FirstNonEmpty(ReadString(raw, "heroStringId", ""), ReadString(reply, "heroId", ""));
                }).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                int activeCount = 0;
                try
                {
                    using (ReignDbConnection connection = OpenCampaignConnection(ReadString(run, "campaignId", "")))
                    {
                        EnsureDynamicCharacteristicsSchema(connection);
                        foreach (string heroId in heroIds)
                        {
                            Dictionary<string, object> count = QuerySql(connection,
                                "SELECT COUNT(*) AS count FROM dynamic_characteristics WHERE owner_id=$owner AND status='active';",
                                new Dictionary<string, object> { ["owner"] = heroId }).FirstOrDefault();
                            activeCount = Math.Max(activeCount, ReadInt(count, "count", 0));
                        }
                    }
                }
                catch (Exception ex)
                {
                    record("requiresDynamicCharacteristicOverflowSelection", minimumActive, false,
                        "Could not inspect active Dynamic Characteristics: " + LimitText(ex.Message, 500));
                    activeCount = -1;
                }
                if (activeCount >= 0)
                {
                    bool grounded = characteristicLines.Any(line => HasDynamicTextEvidence(line, combined));
                    record("requiresDynamicCharacteristicOverflowSelection", minimumActive,
                        activeCount >= minimumActive && characteristicLines.Count > 0 && grounded,
                        "Active characteristics=" + activeCount + "; injected selected lines="
                        + characteristicLines.Count + "; grounded selected recall=" + grounded);
                }
            }
            if (ReadBool(requested, "requiresRetrievalEvidence", false))
            {
                List<Dictionary<string, object>> contextRows = (auditEvidence ?? new List<Dictionary<string, object>>())
                    .Where(row => ReadString(row, "phase", "").Equals("context.loaded", StringComparison.OrdinalIgnoreCase)).ToList();
                bool retrieved = contextRows.Any(row =>
                {
                    Dictionary<string, object> data = ReadDictionary(row, "data") ?? new Dictionary<string, object>();
                    return ReadDictionaryList(data, "memories").Count > 0
                        || ReadDictionaryList(data, "priorLines").Count > 0
                        || !string.IsNullOrWhiteSpace(ReadString(data, "memorySummary", ""));
                });
                record("requiresRetrievalEvidence", true, retrieved,
                    "Context audit rows: " + contextRows.Count);
            }
            List<Dictionary<string, object>> retrievalAuditRows =
                (auditEvidence ?? new List<Dictionary<string, object>>())
                .Where(row => ReadString(row, "phase", "").Equals(
                    "context.loaded", StringComparison.OrdinalIgnoreCase))
                .ToList();
            string retrievedEvidenceText =
                LiveTestRetrievedEvidenceText(retrievalAuditRows);
            foreach (string phrase in ReadStringList(
                requested, "requiredRetrievedPhrases"))
                record(
                    "requiredRetrievedPhrases",
                    phrase,
                    retrievalAuditRows.Count > 0
                        && retrievedEvidenceText.IndexOf(
                            phrase,
                            StringComparison.OrdinalIgnoreCase) >= 0,
                    "Context audit rows=" + retrievalAuditRows.Count
                        + "; retrieval characters="
                        + retrievedEvidenceText.Length);
            foreach (string phrase in ReadStringList(
                requested, "forbiddenRetrievedPhrases"))
                record(
                    "forbiddenRetrievedPhrases",
                    phrase,
                    retrievalAuditRows.Count > 0
                        && retrievedEvidenceText.IndexOf(
                            phrase,
                            StringComparison.OrdinalIgnoreCase) < 0,
                    "Context audit rows=" + retrievalAuditRows.Count
                        + "; retrieval characters="
                        + retrievedEvidenceText.Length);
            if (ReadBool(requested, "requiresRelationshipReceipt", false))
            {
                bool valid = LiveTestRelationshipReceiptsAreValid(
                    run, command, replies, auditEvidence, requested, out string relationshipEvidence);
                record("requiresRelationshipReceipt", true, valid, relationshipEvidence);
            }
            if (ReadBool(requested, "requiresSharedRelationshipHistoryEvidence", false))
            {
                bool valid = LiveTestSharedRelationshipHistoryEvidenceIsValid(
                    run, command, replies, auditEvidence, requested, false,
                    out string historyEvidence);
                record("requiresSharedRelationshipHistoryEvidence", true, valid, historyEvidence);
            }
            if (ReadBool(requested, "requiresSharedRelationshipHistoryPersistence", false))
            {
                bool valid = LiveTestSharedRelationshipHistoryEvidenceIsValid(
                    run, command, replies, auditEvidence, requested, true,
                    out string historyEvidence);
                record("requiresSharedRelationshipHistoryPersistence", true, valid, historyEvidence);
            }
            foreach (string pull in ReadStringList(requested, "requiredContextPulls"))
            {
                bool selected = replies.Any(reply => ReadLiveTestContextPullIds(reply, "selectedContextPulls")
                    .Contains(pull, StringComparer.OrdinalIgnoreCase));
                record("requiredContextPulls", pull, selected,
                    "Selected pulls: " + string.Join(", ", replies
                        .SelectMany(reply => ReadLiveTestContextPullIds(reply, "selectedContextPulls"))
                        .Distinct(StringComparer.OrdinalIgnoreCase)));
            }
            if (ReadBool(requested, "requiresIdentityRecognition", false))
            {
                string expectedName = ReadString(requested, "expectedIdentityName", "");
                string expectedState = ReadString(requested, "expectedIdentityState", "");
                string expectedSource = ReadString(requested, "expectedIdentitySource", "");
                bool recognized = replies.Count > 0 && replies.All(reply =>
                    LiveTestReplyRecognizesIdentity(reply, expectedName, expectedState, expectedSource));
                record("requiresIdentityRecognition", true, recognized,
                    "Expected identity name=" + expectedName + "; state=" + expectedState
                    + "; source=" + expectedSource + "; evidence="
                    + string.Join(" | ", replies.Select(reply =>
                    {
                        Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? reply;
                        Dictionary<string, object> identity = ReadDictionary(raw, "identityView")
                            ?? ReadDictionary(reply, "identityView") ?? new Dictionary<string, object>();
                        return "usable=" + ReadString(identity, "usableName", "")
                            + ", claimed=" + ReadString(identity, "claimedName", "")
                            + ", state=" + ReadString(identity, "identityState", "")
                            + ", source=" + ReadString(identity, "knowledgeSource", "")
                            + ", knows=" + ReadBool(identity, "knowsIdentity", false);
                    })));
            }
            if (ReadBool(
                    requested,
                    "requiresPoliticalAuthorityEvidence", false))
            {
                bool hasExpectedSovereign =
                    requested.ContainsKey(
                        "expectedRealmSovereignKnown");
                bool expectSovereign = ReadBool(
                    requested,
                    "expectedRealmSovereignKnown", false);
                bool hasExpectedSettlementOwner =
                    requested.ContainsKey(
                        "expectedCurrentSettlementOwnerKnown");
                bool expectSettlementOwner = ReadBool(
                    requested,
                    "expectedCurrentSettlementOwnerKnown", false);
                bool hasExpectedAuthorityRelationship =
                    requested.ContainsKey(
                        "expectedAuthorityRelationship");
                string expectedAuthorityRelationship =
                    ReadString(
                        requested,
                        "expectedAuthorityRelationship",
                        "");
                bool hasExpectedRoles =
                    requested.ContainsKey(
                        "expectedRecognizedRoles");
                List<string> expectedRoles = ReadStringList(
                    requested,
                    "expectedRecognizedRoles");
                bool hasExpectedEnemyCount =
                    requested.ContainsKey(
                        "expectedCurrentEnemyKingdomCount");
                int expectedEnemyCount = ReadInt(
                    requested,
                    "expectedCurrentEnemyKingdomCount",
                    -1);
                bool validAuthority = replies.Count > 0
                    && replies.All(reply =>
                    {
                        Dictionary<string, object> raw =
                            ReadDictionary(reply, "rawResponse")
                            ?? reply;
                        Dictionary<string, object> identity =
                            ReadDictionary(raw, "identityView")
                            ?? ReadDictionary(
                                reply, "identityView")
                            ?? new Dictionary<string, object>();
                        Dictionary<string, object> authority =
                            ReadDictionary(
                                identity, "authorityView")
                            ?? new Dictionary<string, object>();
                        string visible = FirstNonEmpty(
                            ReadString(reply, "text", ""),
                            ReadString(raw, "reply", ""));
                        string safeLabel = ReadString(
                            identity, "safeLabel", "");
                        bool staleLabel =
                            ReadBool(
                                identity,
                                "canonicalNameAllowed", false)
                            && !string.IsNullOrWhiteSpace(safeLabel)
                            && visible.IndexOf(
                                safeLabel,
                                StringComparison.OrdinalIgnoreCase)
                                >= 0;
                        bool fieldsMatch =
                            (!hasExpectedSovereign
                                || ReadBool(
                                    authority,
                                    "realmSovereignKnown",
                                    false)
                                    == expectSovereign)
                            && (!hasExpectedSettlementOwner
                                || ReadBool(
                                    authority,
                                    "currentSettlementOwnerKnown",
                                    false)
                                    == expectSettlementOwner)
                            && (!hasExpectedAuthorityRelationship
                                || ReadString(
                                    authority,
                                    "authorityRelationship",
                                    "").Equals(
                                        expectedAuthorityRelationship,
                                        StringComparison.OrdinalIgnoreCase))
                            && (!hasExpectedRoles
                                || expectedRoles.All(role =>
                                    ReadStringList(
                                        authority,
                                        "recognizedRoles")
                                    .Contains(
                                        role,
                                        StringComparer.OrdinalIgnoreCase)))
                            && (!hasExpectedEnemyCount
                                || ReadInt(
                                    authority,
                                    "currentEnemyKingdomCount",
                                    -1) == expectedEnemyCount);
                        bool verified = ReadBool(
                            identity,
                            "canonicalNameAllowed",
                            false);
                        bool publicOfficeKnown = ReadBool(
                            authority,
                            "publicOfficeKnown",
                            false);
                        bool authorityInternallyConsistent =
                            ReadBool(
                                authority,
                                "identityVerified",
                                false) == verified
                            && (verified || publicOfficeKnown
                                ? ReadStringList(
                                    authority,
                                    "recognizedRoles").Count > 0
                                    && !ReadString(
                                        authority,
                                        "source",
                                        "").Equals(
                                            "identity_not_verified",
                                            StringComparison.OrdinalIgnoreCase)
                                : ReadStringList(
                                    authority,
                                    "recognizedRoles").Count == 0
                                    && ReadString(
                                        authority,
                                        "authorityRelationship",
                                        "").Equals(
                                            "identity_unverified",
                                            StringComparison.OrdinalIgnoreCase))
                            && (!ReadBool(
                                    authority,
                                    "subjectIsObserverSovereign",
                                    false)
                                || (ReadBool(
                                        authority,
                                        "sameKingdom",
                                        false)
                                    && ReadBool(
                                        authority,
                                        "subjectIsRuler",
                                        false)
                                    && (verified || publicOfficeKnown)))
                            && (!ReadBool(
                                    authority,
                                    "observerClanOwnsCurrentSettlement",
                                    false)
                                || (!string.IsNullOrWhiteSpace(
                                        ReadString(
                                            authority,
                                            "observerClanId",
                                            ""))
                                    && ReadString(
                                        authority,
                                        "observerClanId",
                                        "").Equals(
                                            ReadString(
                                                authority,
                                                "currentSettlementOwnerClanId",
                                                ""),
                                            StringComparison.OrdinalIgnoreCase)))
                            && ReadInt(
                                authority,
                                "currentEnemyKingdomCount",
                                -1) >= 0;
                        Dictionary<string, object> parsed =
                            LiveTestCurrentModelOutput(raw);
                        return fieldsMatch
                            && authorityInternallyConsistent
                            && !staleLabel
                            && !VerifiedIdentityGroundingContradiction(
                                parsed,
                                new Dictionary<string, object>
                                {
                                    ["identityView"] = identity,
                                    ["latestPlayerText"] = ReadString(
                                        command, "text", "")
                                });
                    });
                record(
                    "requiresPoliticalAuthorityEvidence",
                    true,
                    validAuthority,
                    validAuthority
                        ? "Every reply preserved internally consistent current native roles and political authority, honored any expected fixture values, and avoided the stale stranger label."
                        : "A reply omitted, contradicted, or internally mismatched current native identity/authority evidence, failed an expected fixture value, or reused a stale stranger label.");
            }
            if (ReadBool(requested,
                    "requiresPoliticalConductEvidence", false))
            {
                int expectedMaximum = ReadInt(requested,
                    "expectedMaximumDefianceTier", -1);
                string expectedAuthorityClass = ReadString(requested,
                    "expectedPoliticalAuthorityClass", "");
                string expectedAddressMode = ReadString(requested,
                    "expectedRequiredAddressMode", "");
                bool validConduct = replies.Count > 0
                    && replies.All(reply =>
                    {
                        Dictionary<string, object> raw =
                            ReadDictionary(reply, "rawResponse") ?? reply;
                        Dictionary<string, object> posture =
                            ReadDictionary(raw, "politicalRiskPosture")
                            ?? ReadDictionary(reply,
                                "politicalRiskPosture")
                            ?? new Dictionary<string, object>();
                        Dictionary<string, object> conduct =
                            ReadDictionary(raw, "politicalConduct")
                            ?? ReadDictionary(reply, "politicalConduct")
                            ?? new Dictionary<string, object>();
                        Dictionary<string, object> enforcement =
                            ReadDictionary(raw,
                                "politicalConductEnforcement")
                            ?? ReadDictionary(reply,
                                "politicalConductEnforcement")
                            ?? new Dictionary<string, object>();
                        int maximum = ReadInt(posture,
                            "maximumDefianceTier", -1);
                        int observed = ReadInt(conduct,
                            "defianceTier", -1);
                        return ReadString(posture, "model", "")
                                == PoliticalRiskModel
                            && maximum >= 0 && observed >= 0
                            && observed <= maximum
                            && ReadStringList(conduct,
                                "violations").Count == 0
                            && !string.IsNullOrWhiteSpace(ReadString(
                                enforcement, "outcome", ""))
                            && (expectedMaximum < 0
                                || maximum == expectedMaximum)
                            && (string.IsNullOrWhiteSpace(
                                    expectedAuthorityClass)
                                || ReadString(posture,
                                    "authorityClass", "").Equals(
                                        expectedAuthorityClass,
                                        StringComparison.OrdinalIgnoreCase))
                            && (string.IsNullOrWhiteSpace(
                                    expectedAddressMode)
                                || ReadString(posture,
                                    "requiredAddressMode", "").Equals(
                                        expectedAddressMode,
                                        StringComparison.OrdinalIgnoreCase));
                    });
                record("requiresPoliticalConductEvidence", true,
                    validConduct,
                    validConduct
                        ? "Every reply carried server-observed political posture and conduct evidence within its computed defiance ceiling."
                        : "A reply lacked political posture/conduct evidence, exceeded its computed defiance ceiling, retained a visible violation, or mismatched an expected posture value.");
            }
            foreach (string phrase in ReadStringList(requested, "replyContains"))
                record("replyContains", phrase, combined.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0,
                    "Combined reply text: " + combined);
            foreach (string phrase in ReadStringList(requested, "replyExcludes"))
                record("replyExcludes", phrase, combined.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) < 0,
                    "Combined reply text: " + combined);
            if (ReadString(
                    requested,
                    "visiblePlayerTextPolicy", "")
                .Equals(
                    "in_world_roleplay_only",
                    StringComparison.OrdinalIgnoreCase))
            {
                string playerText = ReadString(command, "text", "");
                bool adversarial = ReadStringList(
                        requested,
                        "representedRequirementIds")
                    .Any(id => id.StartsWith(
                        "ADV-", StringComparison.OrdinalIgnoreCase));
                string[] harnessPhrases =
                {
                    "production conversation path",
                    "mapped requirement",
                    "behavioral requirement",
                    "respond naturally",
                    "speak with me naturally",
                    "this fixture",
                    "this scenario",
                    "semantic rubric",
                    "test harness",
                    "explain the test"
                };
                string contamination = harnessPhrases.FirstOrDefault(
                    phrase => playerText.IndexOf(
                        phrase,
                        StringComparison.OrdinalIgnoreCase) >= 0);
                if (!adversarial
                    && playerText.IndexOf(
                        "this test",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    contamination = "this test";
                record(
                    "visiblePlayerTextPolicy",
                    "in_world_roleplay_only",
                    string.IsNullOrWhiteSpace(contamination),
                    string.IsNullOrWhiteSpace(contamination)
                        ? "The player turn is written as in-world role-play."
                        : "Harness language leaked into the visible player turn: "
                            + contamination);
            }
            if (requested.ContainsKey("requiresGroupAwareness"))
            {
                bool required = ReadBool(requested, "requiresGroupAwareness", false);
                bool aware = !required || LiveTestRepliesShowGroupAwareness(replies);
                record("requiresGroupAwareness", required, aware,
                    string.Join(" | ", replies.Select(reply => FirstNonEmpty(ReadString(reply, "heroName", ""), ReadString(reply, "heroId", ""))
                        + " -> " + ReadString(reply, "reactionTargetHeroStringId", "") + ": " + ReadString(reply, "text", ""))));
            }
            if (requested.ContainsKey("requiresGroupDivergence"))
            {
                bool required = ReadBool(
                    requested, "requiresGroupDivergence", false);
                string divergenceEvidence = "Not required.";
                bool distinct = !required
                    || LiveTestRepliesShowDistinctAgency(
                        replies, out divergenceEvidence);
                record("requiresGroupDivergence", required, distinct,
                    divergenceEvidence);
            }
            if (requested.ContainsKey("requiresActionCompletionGrounding"))
            {
                bool required = ReadBool(
                    requested, "requiresActionCompletionGrounding", false);
                List<string> premature = replies
                    .Select(reply => ReadString(reply, "text", ""))
                    .Select(text => new
                    {
                        Text = text,
                        Claims = VisibleReplyClaimsCompletedWorldAction(
                            text, out string match),
                        Match = match
                    })
                    .Where(item => item.Claims)
                    .Select(item => item.Match)
                    .ToList();
                record(
                    "requiresActionCompletionGrounding",
                    required,
                    !required || premature.Count == 0,
                    premature.Count == 0
                        ? "No reply narrated an unexecuted physical or world action as completed."
                        : "Premature completion claims: "
                            + string.Join(" | ", premature));
            }
            run["assertions"] = rows;
            run["failures"] = failures;
            if (failures.Any(row => string.Equals(ReadString(row, "commandId", ""), commandId, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (string correlationId in ReadStringList(command, "correlationIds"))
                    ScheduleReplayCorpusCapture(ReadString(run, "campaignId", ""), correlationId);
            }
        }

        private static string LiveTestRetrievedEvidenceText(
            IEnumerable<Dictionary<string, object>> contextRows)
        {
            return string.Join(
                "\n",
                (contextRows ?? Enumerable.Empty<Dictionary<string, object>>())
                .Select(row =>
                {
                    Dictionary<string, object> data =
                        ReadDictionary(row, "data")
                        ?? new Dictionary<string, object>();
                    return string.Join(
                        "\n",
                        ReadDictionaryList(data, "memories")
                            .Select(item => Json.Serialize(item))
                            .Concat(ReadDictionaryList(data, "priorLines")
                                .Select(item => Json.Serialize(item)))
                            .Concat(new[]
                            {
                                ReadString(data, "memorySummary", "")
                            }));
                }));
        }

        private static Dictionary<string, object>
            LiveTestCurrentModelOutput(
                Dictionary<string, object> raw)
        {
            raw = raw ?? new Dictionary<string, object>();
            Dictionary<string, object> parsed =
                ReadDictionary(raw, "parsed");
            if (parsed != null && parsed.Count > 0)
                return parsed;

            // The server response envelope also contains retrieved memories,
            // the shared scene transcript, prompt evidence, and native context.
            // Those are inputs to the current answer, not model output. Feeding
            // that entire envelope to the contradiction checker can mistake an
            // earlier "stranger" line for a contradiction in a later, correct
            // identity/authority response.
            Dictionary<string, object> current =
                new Dictionary<string, object>();
            string[] generatedFields =
            {
                "reply", "response", "text", "content",
                "decisionBrief", "decision_brief",
                "memoryWrites", "beliefWrites", "obligationWrites",
                "comprehensionWrites", "dynamicCharacteristicWrites",
                "stateUpdates", "identityIntroductions"
            };
            foreach (string field in generatedFields)
            {
                if (raw.TryGetValue(field, out object value)
                    && value != null)
                    current[field] = value;
            }
            return current;
        }

        private static bool LiveTestSceneMemoryArtifactsAreValid(
            Dictionary<string, object> run,
            Dictionary<string, object> command,
            out string evidence)
        {
            string campaignId = ReadString(run, "campaignId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result") ?? new Dictionary<string, object>();
            string sessionId = ReadString(result, "sessionId", "");
            string reportedSummaryId = ReadString(result, "sceneSummaryId", "");
            if (string.IsNullOrWhiteSpace(campaignId)
                || string.IsNullOrWhiteSpace(sessionId)
                || string.IsNullOrWhiteSpace(reportedSummaryId))
            {
                evidence = "campaign=" + campaignId + "; session=" + sessionId
                    + "; reportedSummary=" + reportedSummaryId;
                return false;
            }

            try
            {
                Dictionary<string, object> session;
                Dictionary<string, object> summary;
                int turnCount;
                int distinctExchanges;
                int sourceLinks;
                int ftsRows;
                int embeddingJobs;
                int consolidationJobs;
                int relatedScenes;
                int rollingArcs;
                int rollingArcSources;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    session = QuerySql(connection,
                        "SELECT * FROM conversation_sessions WHERE session_id=$id LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault();
                    string storedSummaryId = ReadString(session, "scene_summary_id", "");
                    summary = QuerySql(connection,
                        "SELECT * FROM summaries WHERE summary_id=$id LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = storedSummaryId }).FirstOrDefault();
                    turnCount = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) count FROM conversation_turns WHERE session_id=$id AND status='active';",
                        new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault(), "count", 0);
                    distinctExchanges = ReadInt(QuerySql(connection,
                        "SELECT COUNT(DISTINCT exchange_id) count FROM conversation_turns WHERE session_id=$id AND status='active' AND exchange_id<>'';",
                        new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault(), "count", 0);
                    sourceLinks = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) count FROM memory_sources WHERE document_type='summary' AND document_id=$id;",
                        new Dictionary<string, object> { ["id"] = storedSummaryId }).FirstOrDefault(), "count", 0);
                    ftsRows = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) count FROM summary_fts WHERE summary_id=$id;",
                        new Dictionary<string, object> { ["id"] = storedSummaryId }).FirstOrDefault(), "count", 0);
                    embeddingJobs = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) count FROM embedding_jobs WHERE source_type='summary' AND source_id=$id;",
                        new Dictionary<string, object> { ["id"] = storedSummaryId }).FirstOrDefault(), "count", 0);
                    consolidationJobs = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) count FROM memory_background_jobs WHERE job_type='memory_consolidation' AND session_id=$id;",
                        new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault(), "count", 0);

                    string ownerId = ReadString(summary, "owner_id", "");
                    string playerId = ReadString(session, "player_id", "");
                    string lane = ReadString(summary, "memory_lane", "");
                    relatedScenes = ReadInt(QuerySql(connection, @"SELECT COUNT(*) count FROM summaries
WHERE owner_id=$owner AND summary_type='scene' AND status='active' AND memory_lane=$lane
AND ($player='' OR participants_json LIKE $playerLike);",
                        new Dictionary<string, object>
                        {
                            ["owner"] = ownerId, ["lane"] = lane, ["player"] = playerId,
                            ["playerLike"] = "%" + playerId + "%"
                        }).FirstOrDefault(), "count", 0);
                    Dictionary<string, object> arc = QuerySql(connection, @"SELECT summary_id FROM summaries
WHERE owner_id=$owner AND summary_type='arc' AND status='active' AND memory_lane=$lane
AND ($player='' OR participants_json LIKE $playerLike)
ORDER BY updated_ts DESC LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["owner"] = ownerId, ["lane"] = lane, ["player"] = playerId,
                            ["playerLike"] = "%" + playerId + "%"
                        }).FirstOrDefault();
                    string arcId = ReadString(arc, "summary_id", "");
                    rollingArcs = string.IsNullOrWhiteSpace(arcId) ? 0 : 1;
                    rollingArcSources = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) count FROM memory_sources WHERE document_type='summary' AND document_id=$id AND source_type='summary';",
                        new Dictionary<string, object> { ["id"] = arcId }).FirstOrDefault(), "count", 0);
                }

                List<string> participants = TextListFromJson(ReadString(session, "participants_json", "[]"));
                string player = ReadString(session, "player_id", "");
                List<string> nonPlayerParticipants = participants
                    .Where(id => !string.IsNullOrWhiteSpace(id)
                        && !id.Equals(player, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                List<string> knownBy = TextListFromJson(ReadString(summary, "known_by_json", "[]"));
                string summaryId = ReadString(summary, "summary_id", "");
                bool semanticEnabled = ReadBool(LoadSettings(), "enableSemanticMemory", true);
                Dictionary<string, object> memoryResult = ReadDictionary(result, "memory")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> arcResult = ReadDictionary(memoryResult, "arc")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> sceneResult = ReadDictionary(memoryResult, "scene")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> knowledgeBoundary = ReadDictionary(sceneResult, "knowledgeBoundary")
                    ?? new Dictionary<string, object>();
                int identityBoundaryWithheldScenes = ReadInt(
                    arcResult, "identityBoundaryWithheldScenes", 0);
                bool playerIdentityKnown = ReadBool(
                    knowledgeBoundary, "playerIdentityKnown", true);
                int arcEligibleScenes = Math.Max(
                    0, relatedScenes - identityBoundaryWithheldScenes);
                bool identityBoundaryArcExemption =
                    !playerIdentityKnown
                    && identityBoundaryWithheldScenes > 0
                    && arcEligibleScenes < 4
                    && rollingArcs == 0;
                bool rollingArcRequirementSatisfied =
                    LiveTestRollingArcRequirementIsSatisfied(
                        relatedScenes,
                        identityBoundaryWithheldScenes,
                        playerIdentityKnown,
                        rollingArcs,
                        rollingArcSources);
                bool valid = session != null
                    && !ReadString(session, "status", "open").Equals("open", StringComparison.OrdinalIgnoreCase)
                    && summary != null
                    && summaryId.Equals(reportedSummaryId, StringComparison.OrdinalIgnoreCase)
                    && ReadString(summary, "summary_type", "").Equals("scene", StringComparison.OrdinalIgnoreCase)
                    && MemoryLaneIds.Contains(ReadString(summary, "memory_lane", ""), StringComparer.OrdinalIgnoreCase)
                    && turnCount > 0
                    && ReadInt(summary, "event_count", 0) == distinctExchanges
                    && sourceLinks >= turnCount + 1
                    && ftsRows == 1
                    && ReadString(summary, "visibility", "").Equals("private", StringComparison.OrdinalIgnoreCase)
                    && participants.Contains(ReadString(summary, "owner_id", ""), StringComparer.OrdinalIgnoreCase)
                    && nonPlayerParticipants.All(id => knownBy.Contains(id, StringComparer.OrdinalIgnoreCase))
                    && consolidationJobs >= nonPlayerParticipants.Count
                    && (!semanticEnabled
                        || embeddingJobs >= 1
                        || ReadString(summary, "embedding_status", "").Equals("indexed", StringComparison.OrdinalIgnoreCase))
                    && rollingArcRequirementSatisfied
                    && (!identityBoundaryArcExemption
                        || ReadString(arcResult, "reason", "")
                            .Equals("fewer_than_four_related_scenes",
                                StringComparison.OrdinalIgnoreCase));

                evidence = "session=" + sessionId
                    + "; summary=" + summaryId
                    + "; turns=" + turnCount
                    + "; exchanges=" + distinctExchanges
                    + "; sources=" + sourceLinks
                    + "; fts=" + ftsRows
                    + "; embeddingJobs=" + embeddingJobs
                    + "; consolidationJobs=" + consolidationJobs + "/" + nonPlayerParticipants.Count
                    + "; relatedScenes=" + relatedScenes
                    + "; identityBoundaryWithheldScenes=" + identityBoundaryWithheldScenes
                    + "; arcEligibleScenes=" + arcEligibleScenes
                    + "; playerIdentityKnown=" + playerIdentityKnown
                    + "; rollingArc=" + rollingArcs
                    + "; rollingArcSources=" + rollingArcSources;
                return valid;
            }
            catch (Exception ex)
            {
                evidence = "Memory artifact inspection failed: " + LimitText(ex.Message, 900);
                return false;
            }
        }

        private static bool LiveTestRollingArcRequirementIsSatisfied(
            int relatedScenes,
            int identityBoundaryWithheldScenes,
            bool playerIdentityKnown,
            int rollingArcs,
            int rollingArcSources)
        {
            int withheld = Math.Max(0, identityBoundaryWithheldScenes);
            if (withheld > 0 && playerIdentityKnown)
                return false;
            int eligibleScenes = Math.Max(0, relatedScenes - withheld);
            return eligibleScenes < 4
                || (rollingArcs == 1 && rollingArcSources >= 4);
        }

        private static bool LiveTestQualificationMemoryCompletionIsValid(
            Dictionary<string, object> run,
            Dictionary<string, object> command,
            out string evidence)
        {
            string campaignId = ReadString(run, "campaignId", "");
            string qualificationId = ReadString(run, "qualificationId", "");
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(qualificationId))
            {
                evidence = "campaign=" + campaignId + "; qualification=" + qualificationId;
                return false;
            }

            try
            {
                List<Dictionary<string, object>> qualificationRuns =
                    LoadConversationReadinessRuns(campaignId, qualificationId);
                List<string> sessionIds = qualificationRuns
                    .SelectMany(item => ReadDictionaryList(item, "commands"))
                    .Where(item =>
                        ReadString(item, "operation", "").Equals("close", StringComparison.OrdinalIgnoreCase)
                        && ReadString(item, "status", "").Equals("completed", StringComparison.OrdinalIgnoreCase))
                    .Select(item => ReadString(ReadDictionary(item, "result"), "sessionId", ""))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (sessionIds.Count < 100)
                {
                    evidence = "Only " + sessionIds.Count + " completed production sessions were available; 100 are required.";
                    return false;
                }

                Dictionary<string, object> settings = LoadSettings();
                bool semanticEnabled = ReadBool(settings, "enableSemanticMemory", true);
                int minimumItems = Math.Max(2, Math.Min(20,
                    ReadInt(settings, "memoryConsolidationMinItems", 4)));
                List<string> summaryIds = new List<string>();
                List<string> errors = new List<string>();
                int jobCount = 0;
                int completedJobs = 0;
                int eligibleConsolidations = 0;
                int verifiedConsolidations = 0;

                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureMemoryBackgroundWorkSchema(connection);
                    EnsureSemanticMemorySchema(connection);
                    foreach (string sessionId in sessionIds)
                    {
                        Dictionary<string, object> session = QuerySql(connection,
                            "SELECT * FROM conversation_sessions WHERE session_id=$id LIMIT 1;",
                            new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault();
                        string sceneId = ReadString(session, "scene_summary_id", "");
                        if (string.IsNullOrWhiteSpace(sceneId))
                        {
                            errors.Add(sessionId + ": scene summary missing");
                            continue;
                        }
                        summaryIds.Add(sceneId);
                        List<Dictionary<string, object>> jobs = QuerySql(connection, @"SELECT * FROM memory_background_jobs
WHERE job_type='memory_consolidation' AND session_id=$session ORDER BY owner_id;",
                            new Dictionary<string, object> { ["session"] = sessionId });
                        jobCount += jobs.Count;
                        foreach (Dictionary<string, object> job in jobs)
                        {
                            string ownerId = ReadString(job, "owner_id", "");
                            string status = ReadString(job, "status", "");
                            if (status.Equals("completed", StringComparison.OrdinalIgnoreCase))
                                completedJobs++;
                            else
                            {
                                errors.Add(sessionId + "/" + ownerId + ": consolidation job " + status
                                    + (string.IsNullOrWhiteSpace(ReadString(job, "last_error", ""))
                                        ? "" : " (" + LimitText(ReadString(job, "last_error", ""), 160) + ")"));
                                continue;
                            }

                            int sourceEventCount = ReadInt(QuerySql(connection, @"SELECT COUNT(DISTINCT m.event_id) count
FROM memories m JOIN conversation_turns t ON t.event_id=m.event_id
WHERE t.session_id=$session AND m.owner_id=$owner AND m.event_id<>'';",
                                new Dictionary<string, object>
                                {
                                    ["session"] = sessionId, ["owner"] = ownerId
                                }).FirstOrDefault(), "count", 0);
                            if (sourceEventCount < minimumItems) continue;
                            eligibleConsolidations++;
                            Dictionary<string, object> middle = QuerySql(connection, @"SELECT * FROM summaries
WHERE scope=$scope AND owner_id=$owner AND summary_type='middle_term'
ORDER BY updated_ts DESC LIMIT 1;",
                                new Dictionary<string, object>
                                {
                                    ["scope"] = "conversation:" + sessionId, ["owner"] = ownerId
                                }).FirstOrDefault();
                            string middleId = ReadString(middle, "summary_id", "");
                            if (string.IsNullOrWhiteSpace(middleId))
                            {
                                errors.Add(sessionId + "/" + ownerId + ": eligible middle-term summary missing");
                                continue;
                            }
                            Dictionary<string, object> middlePayload =
                                TryParseJsonObject(ReadString(middle, "payload_json", "{}"))
                                ?? new Dictionary<string, object>();
                            List<string> sourceMemoryIds = ReadStringList(middlePayload, "sourceMemoryIds");
                            int memoryLinks = ReadInt(QuerySql(connection, @"SELECT COUNT(*) count FROM memory_sources
WHERE document_type='summary' AND document_id=$id AND source_type='memory';",
                                new Dictionary<string, object> { ["id"] = middleId }).FirstOrDefault(), "count", 0);
                            int eventLinks = ReadInt(QuerySql(connection, @"SELECT COUNT(*) count FROM memory_sources
WHERE document_type='summary' AND document_id=$id AND source_type='event';",
                                new Dictionary<string, object> { ["id"] = middleId }).FirstOrDefault(), "count", 0);
                            int unconsolidated = ReadInt(QuerySql(connection, @"SELECT COUNT(*) count
FROM memory_sources s JOIN memories m ON m.memory_id=s.source_id
WHERE s.document_type='summary' AND s.document_id=$id AND s.source_type='memory'
AND m.status<>'consolidated';",
                                new Dictionary<string, object> { ["id"] = middleId }).FirstOrDefault(), "count", 0);
                            int fts = ReadInt(QuerySql(connection,
                                "SELECT COUNT(*) count FROM summary_fts WHERE summary_id=$id;",
                                new Dictionary<string, object> { ["id"] = middleId }).FirstOrDefault(), "count", 0);
                            int embeddingJobs = ReadInt(QuerySql(connection, @"SELECT COUNT(*) count FROM embedding_jobs
WHERE source_type='summary' AND source_id=$id;",
                                new Dictionary<string, object> { ["id"] = middleId }).FirstOrDefault(), "count", 0);
                            bool valid = ReadInt(middle, "event_count", 0) >= minimumItems
                                && TextListFromJson(ReadString(middle, "source_events_json", "[]")).Count >= minimumItems
                                && sourceMemoryIds.Count > 0
                                && memoryLinks == sourceMemoryIds.Count
                                && eventLinks == TextListFromJson(ReadString(middle, "source_events_json", "[]")).Count
                                && unconsolidated == 0
                                && fts == 1
                                && (!semanticEnabled
                                    || embeddingJobs >= 1
                                    || ReadString(middle, "embedding_status", "")
                                        .Equals("indexed", StringComparison.OrdinalIgnoreCase));
                            if (!valid)
                            {
                                errors.Add(sessionId + "/" + ownerId
                                    + ": middle-term lineage/index invalid (mem="
                                    + memoryLinks + "/" + sourceMemoryIds.Count
                                    + ", events=" + eventLinks
                                    + ", fts=" + fts + ", embedding=" + embeddingJobs + ")");
                                continue;
                            }
                            summaryIds.Add(middleId);
                            verifiedConsolidations++;
                        }
                    }

                    List<string> sceneIds = summaryIds
                        .Where(id => id.StartsWith("scene_", StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (Dictionary<string, object> arc in QuerySql(connection,
                        "SELECT summary_id FROM summaries WHERE summary_type='arc' AND status='active';"))
                    {
                        string arcId = ReadString(arc, "summary_id", "");
                        int matchedSources = ReadInt(QuerySql(connection, @"SELECT COUNT(*) count FROM memory_sources
WHERE document_type='summary' AND document_id=$id AND source_type='summary'
AND source_id IN (" + string.Join(",", sceneIds.Select((id, index) => "$scene" + index)) + ");",
                            sceneIds.Select((id, index) => new KeyValuePair<string, object>(
                                "scene" + index, id)).ToDictionary(pair => pair.Key, pair => pair.Value))
                            .FirstOrDefault(), "count", 0);
                        if (matchedSources > 0) summaryIds.Add(arcId);
                    }
                }

                summaryIds = summaryIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                Dictionary<string, object> semantic = semanticEnabled
                    ? WaitForNpcDialogueAuditEmbeddings(campaignId, summaryIds, 12000)
                    : new Dictionary<string, object>
                    {
                        ["disabled"] = true, ["expected"] = summaryIds.Count,
                        ["indexed"] = summaryIds.Count, ["retrievalApplied"] = true,
                        ["retrievedExpectedSummary"] = true, ["workerAvailable"] = true
                    };
                bool semanticValid = !semanticEnabled
                    || (ReadBool(semantic, "workerAvailable", false)
                        && ReadInt(semantic, "indexed", 0) == summaryIds.Count
                        && ReadBool(semantic, "retrievalApplied", false)
                        && ReadBool(semantic, "retrievedExpectedSummary", false));
                bool validOverall = errors.Count == 0
                    && jobCount > 0
                    && completedJobs == jobCount
                    && verifiedConsolidations == eligibleConsolidations
                    && semanticValid;
                evidence = "sessions=" + sessionIds.Count
                    + "; jobs=" + completedJobs + "/" + jobCount
                    + "; eligibleMiddleTerm=" + eligibleConsolidations
                    + "; verifiedMiddleTerm=" + verifiedConsolidations
                    + "; summaries=" + summaryIds.Count
                    + "; semantic=" + ReadInt(semantic, "indexed", 0)
                    + "/" + ReadInt(semantic, "expected", summaryIds.Count)
                    + "; retrieval=" + ReadBool(semantic, "retrievedExpectedSummary", false)
                    + (errors.Count == 0 ? "" : "; errors=" + string.Join(" | ", errors.Take(6)));
                return validOverall;
            }
            catch (Exception ex)
            {
                evidence = "Qualification memory completion inspection failed: "
                    + LimitText(ex.Message, 900);
                return false;
            }
        }

        private static bool LiveTestReplyRecognizesIdentity(
            Dictionary<string, object> reply,
            string expectedName,
            string expectedState,
            string expectedSource)
        {
            Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? reply;
            Dictionary<string, object> identity = ReadDictionary(raw, "identityView")
                ?? ReadDictionary(reply, "identityView") ?? new Dictionary<string, object>();
            string usableName = ReadString(identity, "usableName", "");
            string claimedName = ReadString(identity, "claimedName", "");
            string actualState = ReadString(identity, "identityState", "");
            bool verifiedCanonicalPrecedence =
                expectedState.Equals("claimed", StringComparison.OrdinalIgnoreCase)
                && actualState.Equals("verified", StringComparison.OrdinalIgnoreCase)
                && ReadBool(identity, "canonicalNameAllowed", false)
                && !string.IsNullOrWhiteSpace(claimedName)
                && claimedName.Equals(expectedName, StringComparison.OrdinalIgnoreCase)
                && !usableName.Equals(claimedName, StringComparison.OrdinalIgnoreCase);
            bool nameMatches = string.IsNullOrWhiteSpace(expectedName)
                || usableName.Equals(expectedName, StringComparison.OrdinalIgnoreCase)
                || claimedName.Equals(expectedName, StringComparison.OrdinalIgnoreCase);
            bool stateMatches = string.IsNullOrWhiteSpace(expectedState)
                || actualState.Equals(expectedState, StringComparison.OrdinalIgnoreCase)
                || verifiedCanonicalPrecedence;
            bool sourceMatches = string.IsNullOrWhiteSpace(expectedSource)
                || ReadString(identity, "knowledgeSource", "").Equals(expectedSource, StringComparison.OrdinalIgnoreCase)
                || verifiedCanonicalPrecedence;
            return ReadBool(identity, "knowsIdentity", false)
                && nameMatches && stateMatches && sourceMatches;
        }

        private static bool LiveTestGroundedGuardedActionsAreSafe(
            Dictionary<string, object> run,
            List<Dictionary<string, object>> replies,
            List<Dictionary<string, object>> auditEvidence,
            out string evidence)
        {
            List<Dictionary<string, object>> queued = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> reply in replies ?? new List<Dictionary<string, object>>())
            {
                Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? reply;
                queued.AddRange(ReadDictionaryList(raw, "queuedDialogueActions"));
                queued.AddRange(ReadDictionaryList(raw, "queuedTestDirectiveActions"));
                queued.AddRange(ReadDictionaryList(raw, "queuedActions"));
            }
            if (queued.Count == 0)
            {
                evidence = "No action was queued; the guarded production router remained idle.";
                return true;
            }

            if (!ReadString(run, "effects", "").Equals("guarded", StringComparison.OrdinalIgnoreCase))
            {
                evidence = "Queued actions were present outside guarded effects mode.";
                return false;
            }

            List<Dictionary<string, object>> autoQueueRows = (auditEvidence ?? new List<Dictionary<string, object>>())
                .Where(row => ReadString(row, "phase", "").Equals("action.auto_queue", StringComparison.OrdinalIgnoreCase))
                .ToList();
            bool grounded = autoQueueRows.Count > 0 && autoQueueRows.All(row =>
            {
                Dictionary<string, object> data = ReadDictionary(row, "data") ?? new Dictionary<string, object>();
                Dictionary<string, object> gate = ReadDictionary(data, "actionGate") ?? new Dictionary<string, object>();
                return ReadBool(gate, "needed", false)
                    && !string.IsNullOrWhiteSpace(ReadString(gate, "intent", ""))
                    && !string.IsNullOrWhiteSpace(ReadString(gate, "reason", ""))
                    && !string.IsNullOrWhiteSpace(ReadString(data, "visibleReply", ""))
                    && ReadDictionaryList(data, "queued").Count > 0;
            });
            if (!grounded)
            {
                evidence = "Queued action count=" + queued.Count
                    + ", but the correlated visible reply, accepted action gate, and queued planner record were incomplete.";
                return false;
            }

            Dictionary<string, object> requestHero = (auditEvidence ?? new List<Dictionary<string, object>>())
                .Where(row => ReadString(row, "phase", "").Equals("dialogue.request", StringComparison.OrdinalIgnoreCase))
                .Select(row => ReadDictionary(ReadDictionary(row, "data") ?? new Dictionary<string, object>(), "hero"))
                .FirstOrDefault(hero => hero != null) ?? new Dictionary<string, object>();
            string speakerHeroId = ReadString(requestHero, "heroStringId", "");
            string speakerClanId = ReadString(requestHero, "clanId", "");
            string playerHeroId = FirstNonEmpty(
                ReadFirstString(requestHero, "mainHeroStringId", "playerHeroStringId"), "main_hero");
            string playerClanId = ReadString(requestHero, "playerClanId", "");
            string speakerKingdomId = ReadString(requestHero, "kingdomId", "");
            string playerKingdomId = ReadString(requestHero, "playerKingdomId", "");

            foreach (Dictionary<string, object> queuedRow in queued)
            {
                Dictionary<string, object> action = ReadDictionary(queuedRow, "record") ?? queuedRow;
                string command = ReadString(action, "command", "");
                bool isNpcAgainstPlayer = command.Equals("attack_player_party", StringComparison.OrdinalIgnoreCase)
                    || command.Equals("duel_player", StringComparison.OrdinalIgnoreCase);
                if (!isNpcAgainstPlayer)
                    continue;
                bool directionOk =
                    ReadString(action, "actorHeroStringId", "").Equals(speakerHeroId, StringComparison.OrdinalIgnoreCase)
                    && ReadString(action, "targetHeroStringId", "").Equals(playerHeroId, StringComparison.OrdinalIgnoreCase)
                    && ReadString(action, "actorClanStringId", "").Equals(speakerClanId, StringComparison.OrdinalIgnoreCase)
                    && ReadString(action, "targetClanStringId", "").Equals(playerClanId, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(speakerKingdomId)
                        || ReadString(action, "actorKingdomStringId", "").Equals(speakerKingdomId, StringComparison.OrdinalIgnoreCase))
                    && (string.IsNullOrWhiteSpace(playerKingdomId)
                        ? string.IsNullOrWhiteSpace(ReadString(action, "targetKingdomStringId", ""))
                        : ReadString(action, "targetKingdomStringId", "").Equals(playerKingdomId, StringComparison.OrdinalIgnoreCase));
                if (!directionOk)
                {
                    evidence = "Grounded " + command + " was guarded, but entity direction was invalid: actorHero="
                        + ReadString(action, "actorHeroStringId", "") + ", targetHero="
                        + ReadString(action, "targetHeroStringId", "") + ", actorClan="
                        + ReadString(action, "actorClanStringId", "") + ", targetClan="
                        + ReadString(action, "targetClanStringId", "") + ".";
                    return false;
                }

                if (command.Equals("duel_player", StringComparison.OrdinalIgnoreCase))
                {
                    Dictionary<string, object> terms = ReadDictionary(action, "terms") ?? new Dictionary<string, object>();
                    string duelMode = ReadString(terms, "duelMode", "training").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
                    bool modeIsLethal = duelMode == "lethal" || duelMode == "death" || duelMode == "to_the_death"
                        || duelMode == "honor" || duelMode == "honour";
                    if (modeIsLethal != ReadBool(terms, "lethal", false))
                    {
                        evidence = "Grounded duel_player had contradictory duel terms: duelMode="
                            + duelMode + ", lethal=" + ReadBool(terms, "lethal", false) + ".";
                        return false;
                    }
                }
            }

            evidence = "Queued action count=" + queued.Count
                + "; every action was visibly grounded, accepted by the private gate, and retained under guarded effects"
                + (queued.Any(row => ReadString(ReadDictionary(row, "record") ?? row, "command", "")
                    .Equals("attack_player_party", StringComparison.OrdinalIgnoreCase)
                    || ReadString(ReadDictionary(row, "record") ?? row, "command", "")
                    .Equals("duel_player", StringComparison.OrdinalIgnoreCase))
                    ? " with correct NPC-to-player entity direction." : ".");
            return true;
        }

        private static List<string> LiveTestDynamicCharacteristicPromptLines(
            Dictionary<string, object> promptData)
        {
            List<string> lines = new List<string>();
            foreach (Dictionary<string, object> message in ReadDictionaryList(promptData, "messages"))
            {
                string content = ReadString(message, "content", "");
                Match section = Regex.Match(content,
                    @"(?ms)^(?:RELEVANT\s+)?DYNAMIC CHARACTERISTICS\s*\n(?<body>.*?)(?=^[A-Z][A-Z0-9 &()\-]{4,}\s*$|\z)");
                if (!section.Success) continue;
                foreach (Match match in Regex.Matches(
                    section.Groups["body"].Value, @"(?m)^- \[[^\]]+\]\s+(.+)$"))
                    lines.Add(match.Groups[1].Value.Trim());
            }
            return lines;
        }

        private static List<string> ReadLiveTestContextPullIds(
            Dictionary<string, object> source,
            string key)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null)
                return new List<string>();

            IEnumerable<object> rows;
            if (value is ArrayList array)
                rows = array.Cast<object>();
            else if (value is IEnumerable<object> enumerable)
                rows = enumerable;
            else
                rows = new[] { value };

            return rows.Select(row =>
                row is Dictionary<string, object> dictionary
                    ? ReadString(dictionary, "id", "")
                    : Convert.ToString(row))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static Dictionary<string, object> LiveTestReplyTiming(Dictionary<string, object> reply)
        {
            Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? new Dictionary<string, object>();
            return ReadDictionary(raw, "timing")
                ?? ReadDictionary(reply, "timing")
                ?? new Dictionary<string, object>();
        }

        private static List<Dictionary<string, object>> LiveTestReplyRelationshipAssessments(Dictionary<string, object> reply)
        {
            List<Dictionary<string, object>> direct = ReadDictionaryList(reply, "relationshipAssessments");
            if (direct.Count > 0) return direct;
            return ReadDictionaryList(ReadDictionary(reply, "rawResponse") ?? new Dictionary<string, object>(), "relationshipAssessments");
        }

        private static bool LiveTestRelationshipReceiptsAreValid(
            Dictionary<string, object> run,
            Dictionary<string, object> command,
            List<Dictionary<string, object>> replies,
            List<Dictionary<string, object>> auditEvidence,
            Dictionary<string, object> requested,
            out string evidence)
        {
            List<string> errors = new List<string>();
            List<Dictionary<string, object>> relationshipRows = (auditEvidence ?? new List<Dictionary<string, object>>())
                .Where(row => ReadString(row, "phase", "")
                    .Equals("relationship.conversation", StringComparison.OrdinalIgnoreCase))
                .ToList();
            List<Dictionary<string, object>> auditReceipts = relationshipRows
                .SelectMany(row => ReadDictionaryList(
                    ReadDictionary(row, "data") ?? new Dictionary<string, object>(), "receipts"))
                .ToList();
            List<Dictionary<string, object>> nativeChanges = relationshipRows
                .SelectMany(row => ReadDictionaryList(
                    ReadDictionary(row, "data") ?? new Dictionary<string, object>(), "nativeChanges"))
                .ToList();
            if (relationshipRows.Count == 0) errors.Add("No relationship.conversation audit row was linked to the command.");
            if (auditReceipts.Count == 0) errors.Add("No conversation relationship receipt was emitted.");

            Dictionary<string, List<Dictionary<string, object>>> auditByReceipt = auditReceipts
                .Where(row => !string.IsNullOrWhiteSpace(ReadString(row, "receiptId", "")))
                .GroupBy(row => ReadString(row, "receiptId", ""), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            if (auditReceipts.Any(row => string.IsNullOrWhiteSpace(ReadString(row, "receiptId", ""))))
                errors.Add("A relationship receipt omitted its stable receipt id.");
            foreach (KeyValuePair<string, List<Dictionary<string, object>>> occurrence in auditByReceipt)
            {
                int nonIdempotent = occurrence.Value.Count(row => !ReadBool(row, "idempotent", false));
                if (nonIdempotent > 1)
                    errors.Add("Receipt " + occurrence.Key + " was emitted as newly applied more than once.");
            }

            string campaignId = ReadString(run, "campaignId", "");
            List<Dictionary<string, object>> receipts = new List<Dictionary<string, object>>();
            if (!string.IsNullOrWhiteSpace(campaignId) && auditByReceipt.Count > 0)
            {
                try
                {
                    using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                    {
                        EnsureConversationRelationshipSchema(connection);
                        EnsureWorldHistoryLieDetectionSchema(connection);
                        foreach (KeyValuePair<string, List<Dictionary<string, object>>> occurrence in auditByReceipt)
                        {
                            Dictionary<string, object> stored = QuerySql(connection,
                                "SELECT * FROM conversation_relationship_receipts WHERE receipt_id=$id LIMIT 1;",
                                new Dictionary<string, object> { ["id"] = occurrence.Key }).FirstOrDefault();
                            if (stored == null)
                            {
                                errors.Add("Receipt " + occurrence.Key + " was absent from authoritative campaign storage.");
                                continue;
                            }
                            Dictionary<string, object> receipt = ConversationReceiptResponse(
                                stored, occurrence.Value.All(row => ReadBool(row, "idempotent", false)));
                            receipt["auditOccurrenceCount"] = occurrence.Value.Count;
                            receipt["auditNonIdempotentCount"] = occurrence.Value.Count(row => !ReadBool(row, "idempotent", false));

                            string lieCheckId = ReadString(receipt, "lieCheckId", "");
                            if (!string.IsNullOrWhiteSpace(lieCheckId))
                            {
                                Dictionary<string, object> lie = QuerySql(connection,
                                    "SELECT * FROM world_history_lie_checks WHERE lie_check_id=$id LIMIT 1;",
                                    new Dictionary<string, object> { ["id"] = lieCheckId }).FirstOrDefault();
                                string outcome = ReadString(lie, "outcome", "").ToLowerInvariant();
                                string verdict = ReadString(lie, "objective_verdict", "").ToLowerInvariant();
                                bool verified = lie != null
                                    && new[] { "contradicted", "false", "deceptive", "verified_lie" }.Contains(verdict)
                                    && !ReadString(lie, "knowledge_basis", "none").Equals("none", StringComparison.OrdinalIgnoreCase)
                                    && (outcome == "detected_firsthand" || outcome == "detected_secondhand")
                                    && ReadString(lie, "claimant_id", "").Equals(
                                        ReadString(receipt, "targetHeroStringId", ""), StringComparison.OrdinalIgnoreCase)
                                    && ReadString(lie, "target_id", "").Equals(
                                        ReadString(receipt, "observerHeroStringId", ""), StringComparison.OrdinalIgnoreCase);
                                receipt["verifiedLieCheck"] = verified;
                                receipt["lieCheckEvidence"] = lie == null
                                    ? new Dictionary<string, object>()
                                    : new Dictionary<string, object>
                                    {
                                        ["lieCheckId"] = lieCheckId,
                                        ["claimantId"] = ReadString(lie, "claimant_id", ""),
                                        ["observerId"] = ReadString(lie, "target_id", ""),
                                        ["objectiveVerdict"] = ReadString(lie, "objective_verdict", ""),
                                        ["knowledgeBasis"] = ReadString(lie, "knowledge_basis", "none"),
                                        ["outcome"] = ReadString(lie, "outcome", "")
                                    };
                            }

                            string benefitId = ReadString(receipt, "benefitEventId", "");
                            if (!string.IsNullOrWhiteSpace(benefitId))
                            {
                                Dictionary<string, object> benefit = QuerySql(connection,
                                    "SELECT * FROM conversation_relationship_benefits WHERE benefit_id=$id LIMIT 1;",
                                    new Dictionary<string, object> { ["id"] = benefitId }).FirstOrDefault();
                                receipt["benefitLedger"] = benefit == null
                                    ? new Dictionary<string, object>()
                                    : new Dictionary<string, object>
                                    {
                                        ["benefitId"] = ReadString(benefit, "benefit_id", ""),
                                        ["giverId"] = ReadString(benefit, "giver_id", ""),
                                        ["recipientId"] = ReadString(benefit, "recipient_id", ""),
                                        ["benefitKind"] = ReadString(benefit, "benefit_kind", ""),
                                        ["sourceEventId"] = ReadString(benefit, "source_event_id", ""),
                                        ["sourceTurnId"] = ReadString(benefit, "source_turn_id", ""),
                                        ["originalDelta"] = ReadInt(benefit, "original_delta", 0),
                                        ["retainedAppreciation"] = ReadDouble(benefit, "retained_appreciation", 1d),
                                        ["continuedUtility"] = ReadDouble(benefit, "continued_utility", 1d),
                                        ["status"] = ReadString(benefit, "status", "")
                                    };
                            }
                            receipts.Add(receipt);
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add("Could not reconcile authoritative relationship storage: " + LimitText(ex.Message, 400));
                }
            }

            Dictionary<string, object> policy = ReadDictionary(requested, "relationshipPolicy")
                ?? new Dictionary<string, object>();
            string playerId = FirstNonEmpty(
                ReadFirstString(policy, "playerHeroStringId", "playerId"),
                FindNestedLiveTestString(auditEvidence, "playerHeroStringId", "mainHeroStringId"));
            List<string> expectedNpcIds = ReadStringList(policy, "expectedNpcIds");
            List<string> replySpeakerIds = (replies ?? new List<Dictionary<string, object>>())
                .Select(reply => ReadFirstString(reply, "heroId", "speakerHeroStringId"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (expectedNpcIds.Count == 0) expectedNpcIds = replySpeakerIds;
            if (string.IsNullOrWhiteSpace(playerId))
            {
                playerId = receipts
                    .SelectMany(receipt => new[]
                    {
                        ReadString(receipt, "observerHeroStringId", ""),
                        ReadString(receipt, "targetHeroStringId", "")
                    })
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)
                        && !expectedNpcIds.Contains(value, StringComparer.OrdinalIgnoreCase));
            }

            bool valid = LiveTestRelationshipReceiptSetIsValid(
                receipts, nativeChanges, replySpeakerIds, expectedNpcIds, playerId, policy, errors);
            evidence = "rows=" + relationshipRows.Count
                + "; auditReceipts=" + auditReceipts.Count
                + "; storedReceipts=" + receipts.Count
                + "; speakers=" + replySpeakerIds.Count
                + "; player=" + playerId
                + "; scenario=" + ReadString(policy, "scenario", "general")
                + (errors.Count == 0 ? "; all directional, tier, lineage, native, and idempotency checks passed."
                    : "; failures=" + string.Join(" | ", errors.Take(12)));
            return valid && errors.Count == 0;
        }

        private static bool LiveTestRelationshipReceiptSetIsValid(
            List<Dictionary<string, object>> receipts,
            List<Dictionary<string, object>> nativeChanges,
            List<string> replySpeakerIds,
            List<string> expectedNpcIds,
            string playerId,
            Dictionary<string, object> policy,
            List<string> errors)
        {
            receipts = receipts ?? new List<Dictionary<string, object>>();
            nativeChanges = nativeChanges ?? new List<Dictionary<string, object>>();
            replySpeakerIds = replySpeakerIds ?? new List<string>();
            expectedNpcIds = expectedNpcIds ?? new List<string>();
            policy = policy ?? new Dictionary<string, object>();
            errors = errors ?? new List<string>();
            HashSet<string> validTiers = new HashSet<string>(
                new[] { "routine", "meaningful", "harmful_lie", "hostile", "severe", "transformative", "gift_witness", "gift_leverage" },
                StringComparer.OrdinalIgnoreCase);

            foreach (IGrouping<string, Dictionary<string, object>> pair in receipts.GroupBy(receipt =>
                ReadString(receipt, "exchangeId", "") + "|"
                + ReadString(receipt, "observerHeroStringId", "") + "|"
                + ReadString(receipt, "targetHeroStringId", ""), StringComparer.OrdinalIgnoreCase))
            {
                if (pair.Count() > 1)
                    errors.Add("More than one net receipt exists for observer-target key " + pair.Key + ".");
            }

            foreach (Dictionary<string, object> receipt in receipts)
            {
                string receiptId = ReadString(receipt, "receiptId", "");
                string exchangeId = ReadString(receipt, "exchangeId", "");
                string sourceTurnId = ReadString(receipt, "sourceTurnId", "");
                string observerId = ReadString(receipt, "observerHeroStringId", "");
                string targetId = ReadString(receipt, "targetHeroStringId", "");
                string tier = ReadString(receipt, "severityTier", "").ToLowerInvariant();
                string valence = ReadString(receipt, "valence", "").ToLowerInvariant();
                string status = ReadString(receipt, "status", "");
                int baseDelta = ReadInt(receipt, "baseDelta", 0);
                int modifierDelta = ReadInt(receipt, "modifierDelta", 0);
                int finalDelta = ReadInt(receipt, "finalDelta", 0);
                int priorAffinity = ReadInt(receipt, "priorAffinity", 0);
                int resultingAffinity = ReadInt(receipt, "resultingAffinity", 0);
                int priorNative = ReadInt(receipt, "priorNativeRelation", 0);
                int resultingNative = ReadInt(receipt, "resultingNativeRelation", 0);
                int nativePairDelta = ReadInt(receipt, "nativePairDelta", 0);
                if (string.IsNullOrWhiteSpace(receiptId)) errors.Add("A stored receipt has no receipt id.");
                if (string.IsNullOrWhiteSpace(exchangeId)) errors.Add(receiptId + " has no exchange id.");
                if (string.IsNullOrWhiteSpace(sourceTurnId)) errors.Add(receiptId + " has no source-turn lineage.");
                if (string.IsNullOrWhiteSpace(observerId) || string.IsNullOrWhiteSpace(targetId))
                    errors.Add(receiptId + " has an empty observer or target.");
                if (!string.IsNullOrWhiteSpace(observerId)
                    && observerId.Equals(targetId, StringComparison.OrdinalIgnoreCase))
                    errors.Add(receiptId + " is a forbidden self-directed relationship change.");
                if (!validTiers.Contains(tier)) errors.Add(receiptId + " has invalid tier " + tier + ".");
                if (finalDelta < -20 || finalDelta > 20) errors.Add(receiptId + " exceeds the absolute twenty-point cap.");
                if (Clamp(priorAffinity + finalDelta, -100, 100) != resultingAffinity)
                    errors.Add(receiptId + " has inconsistent directional affinity arithmetic.");
                if (Clamp(priorNative + nativePairDelta, -100, 100) != resultingNative)
                    errors.Add(receiptId + " has inconsistent native-relation arithmetic.");
                if (baseDelta + modifierDelta != finalDelta)
                    errors.Add(receiptId + " does not reconcile base plus modifiers to its final delta.");
                if (finalDelta > 0 && valence != "positive")
                    errors.Add(receiptId + " has a positive delta with non-positive valence.");
                if (finalDelta < 0 && valence != "negative")
                    errors.Add(receiptId + " has a negative delta with non-negative valence.");
                if (!LiveTestRelationshipTierMagnitudeIsValid(tier, finalDelta, baseDelta, status))
                    errors.Add(receiptId + " violates the " + tier + " tier anchor/range.");
                if (tier != "routine"
                    && tier != "harmful_lie"
                    && string.IsNullOrWhiteSpace(ReadString(receipt, "currentConductQuote", "")))
                    errors.Add(receiptId + " has a non-routine consequence without persisted exact current-conduct evidence.");

                string nativeStatus = ReadString(receipt, "nativeApplicationStatus", "");
                Dictionary<string, object> nativeReceipt = ReadDictionary(receipt, "nativeReceipt")
                    ?? new Dictionary<string, object>();
                List<Dictionary<string, object>> linkedChanges = nativeChanges
                    .Where(change => ReadStringList(change, "receiptIds")
                        .Contains(receiptId, StringComparer.OrdinalIgnoreCase))
                    .GroupBy(change => string.Join("|", new[]
                    {
                        ReadString(change, "subjectId", ""),
                        ReadString(change, "targetId", ""),
                        ReadInt(change, "delta", 0).ToString(CultureInfo.InvariantCulture),
                        string.Join(",", ReadStringList(change, "receiptIds").OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                    }), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
                if (finalDelta == 0)
                {
                    if (linkedChanges.Count == 0)
                    {
                        if (!nativeStatus.Equals("skipped", StringComparison.OrdinalIgnoreCase))
                            errors.Add(receiptId + " has an isolated zero delta without a skipped native application.");
                    }
                    else
                    {
                        if (linkedChanges.Count != 1
                            || !nativeStatus.Equals("applied", StringComparison.OrdinalIgnoreCase)
                            || ReadLong(receipt, "nativeAppliedTs", 0) <= 0)
                        {
                            errors.Add(receiptId + " was not atomically acknowledged with its nonzero reciprocal pair application.");
                        }
                    }
                }
                else
                {
                    if (!nativeStatus.Equals("applied", StringComparison.OrdinalIgnoreCase)
                        || ReadLong(receipt, "nativeAppliedTs", 0) <= 0)
                        errors.Add(receiptId + " lacks a confirmed native personal-relation acknowledgement.");
                    if (linkedChanges.Count != 1)
                        errors.Add(receiptId + " must belong to exactly one native pair application.");
                    string nativeSubject = ReadString(nativeReceipt, "subjectId", "");
                    string nativeTarget = ReadString(nativeReceipt, "targetId", "");
                    bool samePair = (nativeSubject.Equals(observerId, StringComparison.OrdinalIgnoreCase)
                            && nativeTarget.Equals(targetId, StringComparison.OrdinalIgnoreCase))
                        || (nativeSubject.Equals(targetId, StringComparison.OrdinalIgnoreCase)
                            && nativeTarget.Equals(observerId, StringComparison.OrdinalIgnoreCase));
                    if (!samePair
                        || (nativePairDelta != 0 && ReadInt(nativeReceipt, "delta", 0) == 0))
                        errors.Add(receiptId + " native receipt does not target the same actual hero pair.");
                }
            }

            foreach (IGrouping<string, Dictionary<string, object>> nativePair in receipts.GroupBy(receipt =>
                ReadString(receipt, "exchangeId", "") + "|"
                + AmbientPairKey(
                    ReadString(receipt, "observerHeroStringId", ""),
                    ReadString(receipt, "targetHeroStringId", "")),
                StringComparer.OrdinalIgnoreCase))
            {
                List<Dictionary<string, object>> pairReceipts = nativePair.ToList();
                int priorNative = ReadInt(pairReceipts[0], "priorNativeRelation", 0);
                int resultingNative = ReadInt(pairReceipts[0], "resultingNativeRelation", 0);
                int actualPairDelta = resultingNative - priorNative;
                int requestedPairDelta = Clamp(
                    pairReceipts.Sum(receipt => ReadInt(receipt, "finalDelta", 0)),
                    -20, 20);
                if (pairReceipts.Any(receipt =>
                        ReadInt(receipt, "priorNativeRelation", int.MinValue) != priorNative
                        || ReadInt(receipt, "resultingNativeRelation", int.MinValue) != resultingNative
                        || ReadInt(receipt, "nativePairDelta", int.MinValue) != actualPairDelta))
                {
                    errors.Add("Native pair receipts do not share one atomic prior/result/delta for " + nativePair.Key + ".");
                }

                HashSet<string> pairReceiptIds = new HashSet<string>(
                    pairReceipts.Select(receipt => ReadString(receipt, "receiptId", ""))
                        .Where(value => !string.IsNullOrWhiteSpace(value)),
                    StringComparer.OrdinalIgnoreCase);
                List<Dictionary<string, object>> pairNativeChanges = nativeChanges
                    .Where(change => ReadStringList(change, "receiptIds").Any(pairReceiptIds.Contains))
                    .GroupBy(change => string.Join("|", new[]
                    {
                        ReadString(change, "subjectId", ""),
                        ReadString(change, "targetId", ""),
                        ReadInt(change, "delta", 0).ToString(CultureInfo.InvariantCulture),
                        string.Join(",", ReadStringList(change, "receiptIds")
                            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                    }), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
                bool anyDirectionalDelta = pairReceipts.Any(receipt =>
                    ReadInt(receipt, "finalDelta", 0) != 0);
                if (anyDirectionalDelta
                    && (pairNativeChanges.Count != 1
                        || ReadInt(pairNativeChanges[0], "delta", int.MinValue) != requestedPairDelta))
                {
                    errors.Add("Native pair application did not preserve the adjudicated aggregate delta for " + nativePair.Key + ".");
                }
                if (!anyDirectionalDelta && pairNativeChanges.Count != 0)
                {
                    errors.Add("An entirely band-limited native pair queued an unnecessary application for " + nativePair.Key + ".");
                }
                if (anyDirectionalDelta && pairReceipts.Any(receipt =>
                    ReadInt(ReadDictionary(receipt, "nativeReceipt") ?? new Dictionary<string, object>(),
                        "delta", int.MinValue) != requestedPairDelta))
                {
                    errors.Add("Native pair acknowledgement did not report the requested aggregate delta for " + nativePair.Key + ".");
                }
            }

            bool requireEverySpeaker = ReadBool(policy, "requiresEverySpeaker", true);
            bool requirePlayerReaction = ReadBool(policy, "requiresPlayerReaction", true);
            if (requireEverySpeaker)
            {
                foreach (string speakerId in replySpeakerIds)
                {
                    if (!receipts.Any(receipt => ReadString(receipt, "observerHeroStringId", "")
                        .Equals(speakerId, StringComparison.OrdinalIgnoreCase)))
                        errors.Add("Speaking NPC " + speakerId + " produced no directional relationship receipt.");
                }
            }
            if (requirePlayerReaction)
            {
                if (string.IsNullOrWhiteSpace(playerId)) errors.Add("The player hero id could not be reconciled.");
                else
                {
                    foreach (string speakerId in replySpeakerIds)
                    {
                        if (!receipts.Any(receipt =>
                                ReadString(receipt, "observerHeroStringId", "").Equals(speakerId, StringComparison.OrdinalIgnoreCase)
                                && ReadString(receipt, "targetHeroStringId", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)))
                            errors.Add("Speaking NPC " + speakerId + " did not adjudicate its reaction toward the player.");
                    }
                }
            }
            if (ReadBool(policy, "requiresNpcToNpc", false))
            {
                bool npcToNpc = receipts.Any(receipt =>
                    expectedNpcIds.Contains(ReadString(receipt, "observerHeroStringId", ""), StringComparer.OrdinalIgnoreCase)
                    && expectedNpcIds.Contains(ReadString(receipt, "targetHeroStringId", ""), StringComparer.OrdinalIgnoreCase)
                    && !ReadString(receipt, "observerHeroStringId", "").Equals(
                        ReadString(receipt, "targetHeroStringId", ""), StringComparison.OrdinalIgnoreCase));
                if (!npcToNpc) errors.Add("No directional NPC-to-NPC receipt was produced for a dependent group reply.");
            }
            foreach (Dictionary<string, object> change in nativeChanges)
            {
                string subject = ReadString(change, "subjectId", "");
                string target = ReadString(change, "targetId", "");
                if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(target)
                    || subject.Equals(target, StringComparison.OrdinalIgnoreCase))
                    errors.Add("A native relationship application had an invalid actual-hero pair.");
                if (Math.Abs(ReadInt(change, "delta", 0)) > 20)
                    errors.Add("A native pair application exceeded the absolute twenty-point cap.");
                bool playerInvolved = !string.IsNullOrWhiteSpace(playerId)
                    && (subject.Equals(playerId, StringComparison.OrdinalIgnoreCase)
                        || target.Equals(playerId, StringComparison.OrdinalIgnoreCase));
                if (!playerInvolved && ReadBool(change, "showNotification", false))
                    errors.Add("An NPC-to-NPC relationship change incorrectly requested a player notification.");
            }

            string scenario = ReadString(policy, "scenario", "general").ToLowerInvariant();
            List<Dictionary<string, object>> playerReceipts = string.IsNullOrWhiteSpace(playerId)
                ? new List<Dictionary<string, object>>()
                : receipts.Where(receipt => ReadString(receipt, "targetHeroStringId", "")
                    .Equals(playerId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (scenario == "unsupported_claim")
            {
                if (playerReceipts.Any(receipt =>
                    ReadString(receipt, "severityTier", "").Equals("harmful_lie", StringComparison.OrdinalIgnoreCase)
                    || ReadBool(receipt, "verifiedLieCheck", false)))
                    errors.Add("An unsupported claim was incorrectly adjudicated as a verified harmful lie.");
            }
            else if (scenario == "verified_lie")
            {
                if (!playerReceipts.Any(receipt =>
                    ReadString(receipt, "severityTier", "").Equals("harmful_lie", StringComparison.OrdinalIgnoreCase)
                    && ReadBool(receipt, "verifiedLieCheck", false)
                    && ReadInt(receipt, "finalDelta", 0) >= -7
                    && ReadInt(receipt, "finalDelta", 0) <= -4))
                    errors.Add("No evidence-backed harmful lie receipt applied the required -4 to -7 tier.");
            }
            else if (scenario == "routine")
            {
                if (playerReceipts.Count == 0 || playerReceipts.Any(receipt =>
                    !ReadString(receipt, "severityTier", "").Equals("routine", StringComparison.OrdinalIgnoreCase)
                    || (Math.Abs(ReadInt(receipt, "finalDelta", 0)) != 1
                        && !(ReadInt(receipt, "finalDelta", 0) == 0
                            && ReadString(receipt, "status", "").Equals("routine_band_limited", StringComparison.OrdinalIgnoreCase)))))
                    errors.Add("Routine conversation did not produce exactly +/-1, except for a documented outward band limit.");
            }
            else if (scenario == "meaningful_positive")
            {
                if (!playerReceipts.Any(receipt =>
                    ReadString(receipt, "severityTier", "").Equals("meaningful", StringComparison.OrdinalIgnoreCase)
                    && ReadInt(receipt, "finalDelta", 0) >= 2
                    && ReadInt(receipt, "finalDelta", 0) <= 4))
                    errors.Add("No meaningful positive-support receipt applied the required +2 to +4 tier.");
            }
            else if (scenario == "hostile")
            {
                if (!playerReceipts.Any(receipt =>
                    ReadString(receipt, "severityTier", "").Equals("hostile", StringComparison.OrdinalIgnoreCase)
                    && ReadInt(receipt, "finalDelta", 0) >= -7
                    && ReadInt(receipt, "finalDelta", 0) <= -4))
                    errors.Add("No hostile-speech receipt applied the required -4 to -7 tier.");
            }
            else if (scenario == "severe")
            {
                if (!playerReceipts.Any(receipt =>
                    ReadString(receipt, "severityTier", "").Equals("severe", StringComparison.OrdinalIgnoreCase)
                    && ReadInt(receipt, "finalDelta", 0) >= -15
                    && ReadInt(receipt, "finalDelta", 0) <= -8))
                    errors.Add("No grave threat/abuse receipt applied the required -8 to -15 tier.");
            }
            else if (scenario == "gift_recipient")
            {
                string recipientId = ReadFirstString(policy, "expectedGiftRecipientHeroStringId", "giftRecipientHeroStringId");
                Dictionary<string, object> recipient = playerReceipts.FirstOrDefault(receipt =>
                    ReadString(receipt, "observerHeroStringId", "").Equals(recipientId, StringComparison.OrdinalIgnoreCase));
                Dictionary<string, object> ledger = ReadDictionary(recipient, "benefitLedger")
                    ?? new Dictionary<string, object>();
                bool requireTransformative = ReadBool(
                    policy, "requireTransformativeRecipient", false);
                if (recipient == null
                    || !ReadString(recipient, "giftRecipientHeroStringId", "").Equals(recipientId, StringComparison.OrdinalIgnoreCase)
                    || ReadInt(recipient, "finalDelta", 0) <= 0
                    || (!ReadString(recipient, "severityTier", "").Equals("meaningful", StringComparison.OrdinalIgnoreCase)
                        && !ReadString(recipient, "severityTier", "").Equals("transformative", StringComparison.OrdinalIgnoreCase))
                    || (requireTransformative
                        && (!ReadString(recipient, "severityTier", "").Equals("transformative", StringComparison.OrdinalIgnoreCase)
                            || ReadInt(recipient, "finalDelta", 0) < 12
                            || ReadInt(recipient, "finalDelta", 0) > 20))
                    || string.IsNullOrWhiteSpace(ReadString(recipient, "benefitEventId", ""))
                    || !ReadString(ledger, "giverId", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)
                    || !ReadString(ledger, "recipientId", "").Equals(recipientId, StringComparison.OrdinalIgnoreCase)
                    || ReadInt(ledger, "originalDelta", int.MinValue) != ReadInt(recipient, "finalDelta", 0))
                    errors.Add("The accepted gift did not create recipient-specific positive appreciation and benefit lineage.");
                foreach (Dictionary<string, object> witness in playerReceipts.Where(receipt =>
                    !ReadString(receipt, "observerHeroStringId", "").Equals(recipientId, StringComparison.OrdinalIgnoreCase)))
                {
                    if (ReadString(witness, "severityTier", "").Equals("transformative", StringComparison.OrdinalIgnoreCase)
                        || !string.IsNullOrWhiteSpace(ReadString(witness, "benefitEventId", ""))
                        || (ReadString(witness, "severityTier", "").Equals("gift_witness", StringComparison.OrdinalIgnoreCase)
                            && Math.Abs(ReadInt(witness, "baseDelta", 0)) > 2))
                        errors.Add("A gift witness received the recipient's amplified benefit instead of an independent proportionate reaction.");
                }
            }
            else if (scenario == "gift_leverage")
            {
                string recipientId = ReadFirstString(policy, "expectedGiftRecipientHeroStringId", "giftRecipientHeroStringId");
                Dictionary<string, object> leverage = playerReceipts.FirstOrDefault(receipt =>
                    ReadString(receipt, "observerHeroStringId", "").Equals(recipientId, StringComparison.OrdinalIgnoreCase)
                    && ReadString(receipt, "severityTier", "").Equals("gift_leverage", StringComparison.OrdinalIgnoreCase));
                Dictionary<string, object> ledger = ReadDictionary(leverage, "benefitLedger")
                    ?? new Dictionary<string, object>();
                if (leverage == null
                    || ReadInt(leverage, "finalDelta", 0) >= 0
                    || string.IsNullOrWhiteSpace(ReadString(leverage, "benefitEventId", ""))
                    || !ReadString(ledger, "giverId", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)
                    || !ReadString(ledger, "recipientId", "").Equals(recipientId, StringComparison.OrdinalIgnoreCase)
                    || ReadDouble(ledger, "retainedAppreciation", 1d) >= 0.999d
                    || ReadString(ledger, "status", "").Equals("retained", StringComparison.OrdinalIgnoreCase))
                    errors.Add("The later coercive demand did not link the recipient's gift, reduce retained appreciation, and apply a separate negative consequence.");
            }
            else if (scenario == "transformative")
            {
                if (!playerReceipts.Any(receipt =>
                    ReadString(receipt, "severityTier", "").Equals("transformative", StringComparison.OrdinalIgnoreCase)
                    && ReadInt(receipt, "finalDelta", 0) >= 12
                    && ReadInt(receipt, "finalDelta", 0) <= 20
                    && (ReadDictionary(receipt, "benefitLedger") ?? new Dictionary<string, object>()).Count > 0))
                    errors.Add("No verified transformative benefit applied the required +12 to +20 tier with durable lineage.");
            }
            return errors.Count == 0;
        }

        private static bool LiveTestRelationshipTierMagnitudeIsValid(
            string tier, int finalDelta, int baseDelta, string status)
        {
            int magnitude = Math.Abs(finalDelta);
            switch ((tier ?? "").ToLowerInvariant())
            {
                case "routine":
                    return magnitude == 1
                        || (finalDelta == 0
                            && string.Equals(status, "routine_band_limited", StringComparison.OrdinalIgnoreCase));
                case "meaningful":
                    return magnitude >= 2 && magnitude <= 4;
                case "harmful_lie":
                case "hostile":
                    return finalDelta <= -4 && finalDelta >= -7;
                case "severe":
                    return finalDelta <= -8 && finalDelta >= -15;
                case "transformative":
                    return finalDelta >= 12 && finalDelta <= 20;
                case "gift_witness":
                    return magnitude >= 1 && magnitude <= 20 && Math.Abs(baseDelta) <= 2;
                case "gift_leverage":
                    return finalDelta <= -1 && finalDelta >= -20;
                default:
                    return false;
            }
        }

        private static string FindNestedLiveTestString(object value, params string[] keys)
        {
            HashSet<string> wanted = new HashSet<string>(keys ?? new string[0], StringComparer.OrdinalIgnoreCase);
            Func<object, string> visit = null;
            visit = current =>
            {
                if (current is Dictionary<string, object> dictionary)
                {
                    foreach (KeyValuePair<string, object> pair in dictionary)
                    {
                        if (wanted.Contains(pair.Key))
                        {
                            string found = Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                            if (!string.IsNullOrWhiteSpace(found)) return found;
                        }
                    }
                    foreach (object nested in dictionary.Values)
                    {
                        string found = visit(nested);
                        if (!string.IsNullOrWhiteSpace(found)) return found;
                    }
                }
                else if (current is IEnumerable sequence && !(current is string))
                {
                    foreach (object nested in sequence)
                    {
                        string found = visit(nested);
                        if (!string.IsNullOrWhiteSpace(found)) return found;
                    }
                }
                return "";
            };
            return visit(value);
        }

        private static bool LiveTestSharedRelationshipHistoryEvidenceIsValid(
            Dictionary<string, object> run,
            Dictionary<string, object> command,
            List<Dictionary<string, object>> replies,
            List<Dictionary<string, object>> auditEvidence,
            Dictionary<string, object> requested,
            bool requirePersistenceReuse,
            out string evidence)
        {
            string campaignId = ReadString(run, "campaignId", "");
            int minimumTargets = Math.Max(1,
                ReadInt(requested, "minimumSharedRelationshipHistoryTargets", 1));
            string expectedMode = ReadString(
                requested, "expectedSharedRelationshipHistoryMode",
                requirePersistenceReuse ? "reuse" : "any").Trim().ToLowerInvariant();
            List<string> errors = new List<string>();
            HashSet<string> historyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> pairKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> asymmetricPairKeys =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> generatedTransitionPairKeys =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<int>> promptedChapterOrdinals =
                new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            int promptCount = 0, targetCount = 0, eligibleTargetCount = 0;
            int generated = 0, reused = 0;
            int fallback = 0, providerCalls = 0;
            long totalDurationMs = 0, providerDurationMs = 0;
            try
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureSharedRelationshipHistorySchema(connection);
                    foreach (Dictionary<string, object> reply in replies
                        ?? new List<Dictionary<string, object>>())
                    {
                        Dictionary<string, object> raw =
                            ReadDictionary(reply, "rawResponse") ?? reply;
                        string correlationId = FirstNonEmpty(
                            ReadString(raw, "correlationId", ""),
                            ReadString(reply, "correlationId", ""));
                        Dictionary<string, object> promptRow =
                            (auditEvidence ?? new List<Dictionary<string, object>>())
                            .LastOrDefault(row =>
                                ReadString(row, "phase", "")
                                    .Equals("prompt.built", StringComparison.OrdinalIgnoreCase)
                                && (string.IsNullOrWhiteSpace(correlationId)
                                    || ReadString(row, "correlationId", "")
                                        .Equals(correlationId, StringComparison.OrdinalIgnoreCase)));
                        if (promptRow == null)
                        {
                            errors.Add("prompt audit missing for " + correlationId);
                            continue;
                        }
                        promptCount++;
                        Dictionary<string, object> promptData =
                            ReadDictionary(promptRow, "data") ?? new Dictionary<string, object>();
                        Dictionary<string, object> envelope =
                            ReadDictionary(promptData, "promptEnvelope")
                            ?? new Dictionary<string, object>();
                        Dictionary<string, object> relationshipPrompt =
                            ReadDictionary(envelope, "npcRelationshipPrompt")
                            ?? new Dictionary<string, object>();
                        string relationshipBlock =
                            ReadString(relationshipPrompt, "block", "");
                        if (!ReadBool(envelope,
                                "npcRelationshipPromptIncluded", false))
                            errors.Add("relationship context was not included in the live prompt: "
                                + correlationId);
                        if (!string.Equals(
                                ReadString(envelope,
                                    "npcRelationshipPromptBlockHash", ""),
                                PromptHash(relationshipBlock),
                                StringComparison.OrdinalIgnoreCase))
                            errors.Add("relationship prompt proof hash mismatch: "
                                + correlationId);
                        Dictionary<string, object> generation =
                            ReadDictionary(relationshipPrompt, "historyGeneration")
                            ?? new Dictionary<string, object>();
                        generated += ReadInt(generation, "generatedCount", 0);
                        reused += ReadInt(generation, "reusedCount", 0);
                        fallback += ReadInt(generation, "fallbackCount", 0);
                        providerCalls += ReadInt(generation, "providerCallCount", 0);
                        totalDurationMs += ReadLong(generation, "durationMs", 0);
                        providerDurationMs += ReadLong(generation, "providerDurationMs", 0);
                        foreach (Dictionary<string, object> storedChapter in
                            ReadDictionaryList(generation, "storedChapters"))
                        {
                            string generatedPairKey =
                                ReadString(storedChapter, "pair_key", "");
                            if (!string.IsNullOrWhiteSpace(generatedPairKey)
                                && ReadInt(storedChapter, "chapter_ordinal", 0) >= 2)
                                generatedTransitionPairKeys.Add(generatedPairKey);
                        }

                        List<Dictionary<string, object>> allTargets =
                            ReadDictionaryList(relationshipPrompt, "targets");
                        List<Dictionary<string, object>> eligibleTargets =
                            allTargets.Where(target =>
                                ReadBool(
                                    target,
                                    "sharedRelationshipHistoryEligible",
                                    false)).ToList();
                        List<Dictionary<string, object>> targets =
                            allTargets
                            .Where(target =>
                                ReadStringList(target, "sharedRelationshipHistoryIds").Count > 0)
                            .ToList();
                        targetCount += targets.Count;
                        eligibleTargetCount += eligibleTargets.Count;
                        int expectedForReply = Math.Min(
                            minimumTargets, eligibleTargets.Count);
                        if (targets.Count < expectedForReply)
                            errors.Add("history targets " + targets.Count + "/"
                                + expectedForReply + " eligible for " + correlationId);

                        string speakerId = FirstNonEmpty(
                            ReadFirstString(raw, "heroStringId", "speakerHeroStringId"),
                            ReadFirstString(reply, "heroId", "speakerHeroStringId"),
                            ReadString(promptRow, "heroId", ""));
                        foreach (Dictionary<string, object> target in targets)
                        {
                            string targetId = ReadString(target, "targetHeroStringId", "");
                            foreach (string historyId in ReadStringList(
                                target, "sharedRelationshipHistoryIds"))
                            {
                                historyIds.Add(historyId);
                                Dictionary<string, object> row = QuerySql(connection,
                                    "SELECT * FROM shared_relationship_history_chapters WHERE history_id=$id LIMIT 1;",
                                    new Dictionary<string, object> { ["id"] = historyId })
                                    .FirstOrDefault();
                                if (row == null)
                                {
                                    errors.Add("stored chapter missing: " + historyId);
                                    continue;
                                }
                                string heroA = ReadString(row, "hero_a_id", "");
                                string heroB = ReadString(row, "hero_b_id", "");
                                string pairKey = ReadString(row, "pair_key", "");
                                int chapterOrdinal =
                                    ReadInt(row, "chapter_ordinal", 0);
                                if (!string.IsNullOrWhiteSpace(pairKey))
                                {
                                    pairKeys.Add(pairKey);
                                    HashSet<int> ordinals;
                                    if (!promptedChapterOrdinals.TryGetValue(
                                            pairKey, out ordinals))
                                    {
                                        ordinals = new HashSet<int>();
                                        promptedChapterOrdinals[pairKey] = ordinals;
                                    }
                                    ordinals.Add(chapterOrdinal);
                                    Dictionary<string, object> chemistry = QuerySql(
                                        connection,
                                        "SELECT affinity_a_to_b,affinity_b_to_a FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                                        new Dictionary<string, object>
                                        {
                                            ["pair"] = pairKey
                                        }).FirstOrDefault();
                                    if (chemistry != null
                                        && Math.Abs(
                                            ReadInt(chemistry, "affinity_a_to_b", 0)
                                            - ReadInt(chemistry, "affinity_b_to_a", 0))
                                            >= 20)
                                        asymmetricPairKeys.Add(pairKey);
                                }
                                bool speakerIsA = heroA.Equals(
                                    speakerId, StringComparison.OrdinalIgnoreCase);
                                bool pairValid = (speakerIsA
                                        && heroB.Equals(targetId, StringComparison.OrdinalIgnoreCase))
                                    || (heroB.Equals(speakerId, StringComparison.OrdinalIgnoreCase)
                                        && heroA.Equals(targetId, StringComparison.OrdinalIgnoreCase));
                                if (!pairValid)
                                    errors.Add("wrong pair for " + historyId + ": "
                                        + speakerId + "->" + targetId);
                                string objective = ReadString(row, "objective_summary", "");
                                string allowed = ReadString(row,
                                    speakerIsA ? "a_interpretation" : "b_interpretation", "");
                                string forbidden = ReadString(row,
                                    speakerIsA ? "b_interpretation" : "a_interpretation", "");
                                if (string.IsNullOrWhiteSpace(objective)
                                    || relationshipBlock.IndexOf(
                                        objective, StringComparison.Ordinal) < 0)
                                    errors.Add("objective absent from prompt: " + historyId);
                                if (string.IsNullOrWhiteSpace(allowed)
                                    || relationshipBlock.IndexOf(
                                        allowed, StringComparison.Ordinal) < 0)
                                    errors.Add("speaker interpretation absent from prompt: " + historyId);
                                if (!string.IsNullOrWhiteSpace(forbidden)
                                    && !forbidden.Equals(allowed, StringComparison.Ordinal)
                                    && relationshipBlock.IndexOf(
                                        forbidden, StringComparison.Ordinal) >= 0)
                                    errors.Add("other private interpretation leaked into prompt: "
                                        + historyId);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add("inspection exception: " + LimitText(ex.Message, 600));
            }

            if (expectedMode == "cold" && (providerCalls < 1 || generated + fallback < 1))
                errors.Add("cold generation did not make a provider-backed generation attempt");
            if (eligibleTargetCount < minimumTargets)
                errors.Add("eligible history targets " + eligibleTargetCount + "/"
                    + minimumTargets + " across the conversation");
            if ((expectedMode == "reuse" || requirePersistenceReuse)
                && (providerCalls != 0 || reused < 1 || generated + fallback != 0))
                errors.Add("reuse path generated another chapter or did not report reuse");
            if (expectedMode == "transition"
                && (providerCalls < 1 || generated + fallback < 1))
                errors.Add("transition did not generate an appended chapter");
            if (expectedMode == "transition"
                && generatedTransitionPairKeys.Count == 0)
                errors.Add("transition generation did not report a new chapter ordinal");
            if (expectedMode == "transition")
            {
                foreach (string pairKey in generatedTransitionPairKeys)
                {
                    HashSet<int> ordinals;
                    promptedChapterOrdinals.TryGetValue(pairKey, out ordinals);
                    if (!TransitionHistoryWasAppended(ordinals))
                        errors.Add("transition prompt did not retain both prior and newly appended chapters for "
                            + pairKey);
                }
            }

            bool valid = promptCount == (replies?.Count ?? 0)
                && promptCount > 0 && historyIds.Count > 0 && errors.Count == 0;
            evidence = "prompts=" + promptCount
                + "; targets=" + targetCount
                + "; eligibleTargets=" + eligibleTargetCount
                + "; histories=" + historyIds.Count
                + "; generated=" + generated
                + "; reused=" + reused
                + "; fallback=" + fallback
                + "; providerCalls=" + providerCalls
                + "; historyMs=" + totalDurationMs
                + "; providerMs=" + providerDurationMs
                + "; expectedMode=" + expectedMode
                + (errors.Count == 0 ? "" : "; errors=" + string.Join(" | ", errors.Take(5)));
            command["sharedRelationshipHistoryEvidence"] =
                new Dictionary<string, object>
                {
                    ["valid"] = valid,
                    ["replyCount"] = replies?.Count ?? 0,
                    ["promptCount"] = promptCount,
                    ["targetCount"] = targetCount,
                    ["eligibleTargetCount"] = eligibleTargetCount,
                    ["historyIds"] = historyIds.ToList(),
                    ["pairKeys"] = pairKeys.ToList(),
                    ["asymmetricPairKeys"] = asymmetricPairKeys.ToList(),
                    ["generatedCount"] = generated,
                    ["reusedCount"] = reused,
                    ["fallbackCount"] = fallback,
                    ["providerCallCount"] = providerCalls,
                    ["durationMs"] = totalDurationMs,
                    ["providerDurationMs"] = providerDurationMs,
                    ["expectedMode"] = expectedMode,
                    ["persistenceReuse"] = requirePersistenceReuse,
                    ["privacyLeakCount"] = errors.Count(item =>
                        item.IndexOf("private interpretation leaked",
                            StringComparison.OrdinalIgnoreCase) >= 0),
                    ["errors"] = errors
                };
            return valid;
        }

        private static bool TransitionHistoryWasAppended(HashSet<int> ordinals)
        {
            if (ordinals == null || ordinals.Count < 2) return false;
            int newest = ordinals.Max();
            return newest >= 2 && ordinals.Any(value => value < newest);
        }

        private static List<Dictionary<string, object>> CollectLiveTestCommandAuditEvidence(
            Dictionary<string, object> run, Dictionary<string, object> command)
        {
            List<Dictionary<string, object>> evidence = new List<Dictionary<string, object>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string correlation in ReadStringList(command, "correlationIds")
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                Dictionary<string, object> audit = AuditQuery(new Dictionary<string, string>
                {
                    ["campaignId"] = ReadString(run, "campaignId", ""),
                    ["correlationId"] = correlation,
                    ["limit"] = "500",
                    ["oldestFirst"] = "true"
                });
                foreach (Dictionary<string, object> row in ReadDictionaryList(audit, "entries"))
                {
                    string auditId = ReadString(row, "auditId", "");
                    if (string.IsNullOrWhiteSpace(auditId) || seen.Add(auditId)) evidence.Add(row);
                }
            }
            return evidence;
        }

        private static bool LiveTestRepliesShowGroupAwareness(List<Dictionary<string, object>> replies)
        {
            if (replies == null || replies.Count < 2) return false;
            for (int index = 1; index < replies.Count; index++)
            {
                Dictionary<string, object> reply = replies[index];
                string speakerId = ReadFirstString(reply, "heroId", "speakerHeroStringId");
                List<Dictionary<string, object>> earlierReplies =
                    replies.Take(index).ToList();
                HashSet<string> earlierIds = new HashSet<string>(
                    earlierReplies.Select(earlier =>
                            ReadFirstString(
                                earlier, "heroId",
                                "speakerHeroStringId"))
                        .Where(id => !string.IsNullOrWhiteSpace(id)),
                    StringComparer.OrdinalIgnoreCase);
                string target = ReadFirstString(reply, "reactionTargetHeroStringId", "reactionTargetHeroId");
                if (!string.IsNullOrWhiteSpace(target) && earlierIds.Contains(target)
                    && !target.Equals(speakerId, StringComparison.OrdinalIgnoreCase)) return true;
                if (LiveTestReplyRelationshipAssessments(reply).Any(
                    assessment =>
                    {
                        string assessedTarget = ReadFirstString(
                            assessment, "targetHeroStringId",
                            "targetId");
                        return !string.IsNullOrWhiteSpace(
                                assessedTarget)
                            && earlierIds.Contains(assessedTarget)
                            && !assessedTarget.Equals(
                                speakerId,
                                StringComparison.OrdinalIgnoreCase);
                    }))
                    return true;
                string text = ReadString(reply, "text", "");
                Dictionary<string, List<string>> earlierNames =
                    earlierReplies
                        .Select(earlier => ReadFirstString(
                            earlier, "heroName", "speakerName"))
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Select(name => new
                        {
                            Full = name,
                            First = name.Trim().Split(
                                new[] { ' ' },
                                StringSplitOptions.RemoveEmptyEntries)
                                .FirstOrDefault() ?? ""
                        })
                        .GroupBy(
                            name => name.First,
                            StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            group => group.Key,
                            group => group.Select(name => name.Full).ToList(),
                            StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, List<string>> nameGroup in earlierNames)
                {
                    if (nameGroup.Value.Any(name =>
                            text.IndexOf(
                                name,
                                StringComparison.OrdinalIgnoreCase) >= 0))
                        return true;
                    if (nameGroup.Key.Length >= 4
                        && nameGroup.Value.Count == 1
                        && Regex.IsMatch(
                            text,
                            @"(?<![\p{L}\p{N}])"
                                + Regex.Escape(nameGroup.Key)
                                + @"(?![\p{L}\p{N}])",
                            RegexOptions.IgnoreCase
                                | RegexOptions.CultureInvariant))
                        return true;
                }
            }
            return false;
        }

        private static bool LiveTestRepliesShowDistinctAgency(
            List<Dictionary<string, object>> replies,
            out string evidence)
        {
            List<Dictionary<string, object>> active = (replies
                    ?? new List<Dictionary<string, object>>())
                .Where(reply => !string.IsNullOrWhiteSpace(
                    ReadString(reply, "text", "")))
                .ToList();
            if (active.Count < 2)
            {
                evidence = "Fewer than two speaking NPC replies were available.";
                return false;
            }
            for (int left = 0; left < active.Count; left++)
            {
                for (int right = left + 1;
                    right < active.Count; right++)
                {
                    string leftText = ReadString(active[left], "text", "");
                    string rightText = ReadString(active[right], "text", "");
                    double similarity = RoleplayTextSimilarity(
                        leftText, rightText);
                    if (similarity < 0.72d) continue;
                    evidence = FirstNonEmpty(
                            ReadString(active[left], "heroName", ""),
                            ReadString(active[left], "heroId", ""))
                        + " and " + FirstNonEmpty(
                            ReadString(active[right], "heroName", ""),
                            ReadString(active[right], "heroId", ""))
                        + " converged at similarity "
                        + similarity.ToString("0.00", CultureInfo.InvariantCulture)
                        + ".";
                    return false;
                }
            }
            int distinctAgency = active
                .Select(reply =>
                {
                    Dictionary<string, object> brief =
                        ReadDictionary(reply, "decisionBrief")
                        ?? ReadDictionary(reply, "decision_brief")
                        ?? new Dictionary<string, object>();
                    string signature = string.Join(" ",
                        new[]
                        {
                            ReadString(brief, "decision", ""),
                            string.Join(" ", ReadStringList(
                                brief, "goals")),
                            string.Join(" ", ReadStringList(
                                brief, "constraints")),
                            ReadFirstString(
                                reply,
                                "reactionTargetHeroStringId",
                                "reactionTargetHeroId")
                        });
                    if (string.IsNullOrWhiteSpace(signature))
                        signature = ReadString(reply, "text", "");
                    return Regex.Replace(
                        signature.ToLowerInvariant(),
                        @"\s+", " ").Trim();
                })
                .Distinct(StringComparer.Ordinal)
                .Count();
            evidence = distinctAgency >= 2
                ? active.Count + " speakers advanced the beat with distinct wording and agency evidence."
                : "The speaking NPCs exposed no distinct decision, goal, constraint, target, or substantive wording.";
            return distinctAgency >= 2;
        }

        private static Dictionary<string, object> NormalizeLiveTestCommand(Dictionary<string, object> run, Dictionary<string, object> input)
        {
            input = input ?? new Dictionary<string, object>();
            if (input.ContainsKey("schemaVersion") && !new[] { 1, LiveTestSchemaVersion }.Contains(ReadInt(input, "schemaVersion", LiveTestSchemaVersion))) return null;
            string operation = ReadString(input, "operation", ReadString(input, "op", "")).Trim().ToLowerInvariant();
            if (!LiveTestOperations.Contains(operation)) return null;
            string mode = NormalizeLiveTestMode(ReadString(input, "mode", ReadString(run, "mode", "individual_chat")));
            if (!LiveTestModes.Contains(mode)) return null;
            string commandId = FirstNonEmpty(ReadString(input, "commandId", ""), ReadString(run, "runId", "run") + "-cmd-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            int maximumTimeoutSeconds = mode.Equals("passive_world", StringComparison.OrdinalIgnoreCase)
                && operation.Equals("world_advance", StringComparison.OrdinalIgnoreCase)
                    ? 21600
                    : mode.Equals("social_balance", StringComparison.OrdinalIgnoreCase)
                        && operation.Equals("social_reputation_profile", StringComparison.OrdinalIgnoreCase)
                            ? 3600
                            : 1800;
            Dictionary<string, object> command = new Dictionary<string, object>(input, StringComparer.OrdinalIgnoreCase)
            {
                ["schemaVersion"] = LiveTestSchemaVersion,
                ["commandId"] = commandId,
                ["runId"] = ReadString(run, "runId", ""),
                ["campaignId"] = ReadString(run, "campaignId", ""),
                ["gameInstanceId"] = ReadString(run, "gameInstanceId", ""),
                ["mode"] = mode,
                ["operation"] = operation,
                ["presentation"] = NormalizeLiveTestPresentation(ReadString(input, "presentation", ReadString(run, "presentation", "headless"))),
                ["effects"] = NormalizeLiveTestEffects(ReadString(input, "effects", ReadString(run, "effects", "guarded"))),
                ["status"] = "queued",
                ["timeoutSeconds"] = Math.Max(10, Math.Min(maximumTimeoutSeconds, ReadInt(input, "timeoutSeconds", 300))),
                ["queuedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o")
            };
            if (mode.Equals("social_balance", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> enrollment = ReadDictionary(input, "enrollment");
                if (enrollment == null || enrollment.Count == 0)
                    enrollment = ReadDictionary(run, "enrollment");
                if (enrollment == null || enrollment.Count == 0) return null;
                command["enrollment"] = enrollment;
            }
            if (operation == "send" && string.IsNullOrWhiteSpace(ReadString(command, "text", ""))) return null;
            return command;
        }

        private static List<Dictionary<string, object>> NormalizeLiveTestScenarioCommands(
            Dictionary<string, object> run, Dictionary<string, object> payload)
        {
            List<Dictionary<string, object>> commands = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> step in ReadDictionaryList(payload, "steps"))
            {
                Dictionary<string, object> command = NormalizeLiveTestCommand(run, step);
                if (command != null) commands.Add(command);
            }
            return commands;
        }

        private static void CompleteLiveTestRunIfAppropriate(Dictionary<string, object> run, Dictionary<string, object> completedCommand)
        {
            string operation = ReadString(completedCommand, "operation", "");
            List<Dictionary<string, object>> commands = ReadDictionaryList(run, "commands");
            bool pending = commands.Any(x => !LiveTestTerminalCommandStates.Contains(ReadString(x, "status", "")) && !ReadString(x, "status", "").Equals("needs_input", StringComparison.OrdinalIgnoreCase));
            bool failedSetupWithoutRecoveryStep = operation == "open"
                && ReadString(completedCommand, "status", "").Equals("failed", StringComparison.OrdinalIgnoreCase)
                && !pending;
            bool shouldComplete = failedSetupWithoutRecoveryStep
                || (operation == "close" && !pending)
                || (ReadBool(run, "autoCompleteWhenIdle", false) && !pending);
            if (!shouldComplete) return;
            run["status"] = ReadDictionaryList(run, "failures").Count == 0 ? "completed" : "failed";
            run["completedUtc"] = DateTimeOffset.UtcNow.ToString("o");
        }

        private static void InterruptLiveTestRun(Dictionary<string, object> run, string reason)
        {
            if (run == null || run.Count == 0) return;
            run["status"] = "interrupted";
            run["interruptedUtc"] = DateTimeOffset.UtcNow.ToString("o");
            run["message"] = reason ?? "Run interrupted.";
            foreach (Dictionary<string, object> command in ReadDictionaryList(run, "commands").Where(x => !LiveTestTerminalCommandStates.Contains(ReadString(x, "status", ""))))
            {
                command["status"] = "interrupted";
                command["error"] = reason ?? "Run interrupted.";
            }
            SaveLiveTestRun(run);
        }

        private static Dictionary<string, object> LiveTestRunSummary(Dictionary<string, object> run, bool includeCommands)
        {
            List<Dictionary<string, object>> commands = ReadDictionaryList(run, "commands");
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["runId"] = ReadString(run, "runId", ""),
                ["campaignId"] = ReadString(run, "campaignId", ""),
                ["gameInstanceId"] = ReadString(run, "gameInstanceId", ""),
                ["mode"] = ReadString(run, "mode", ""),
                ["status"] = ReadString(run, "status", ""),
                ["presentation"] = ReadString(run, "presentation", "headless"),
                ["effects"] = ReadString(run, "effects", "guarded"),
                ["enrollment"] = ReadDictionary(run, "enrollment")
                    ?? new Dictionary<string, object>(),
                ["startedUtc"] = ReadString(run, "startedUtc", ""),
                ["updatedUtc"] = ReadString(run, "updatedUtc", ""),
                ["completedUtc"] = ReadString(run, "completedUtc", ""),
                ["commandCount"] = commands.Count,
                ["completedCommands"] = commands.Count(x => ReadString(x, "status", "").Equals("completed", StringComparison.OrdinalIgnoreCase)),
                ["failedCommands"] = commands.Count(x => ReadString(x, "status", "").Equals("failed", StringComparison.OrdinalIgnoreCase)),
                ["reportPath"] = LiveTestRunPath(ReadString(run, "campaignId", ""), ReadString(run, "runId", ""))
            };
            if (includeCommands)
            {
                result["commands"] = commands;
                result["assertions"] = ReadDictionaryList(run, "assertions");
                result["failures"] = ReadDictionaryList(run, "failures");
            }
            return result;
        }

        private static List<Dictionary<string, object>> CollectLiveTestAuditEvidence(Dictionary<string, object> run)
        {
            string campaignId = ReadString(run, "campaignId", "");
            List<Dictionary<string, object>> evidence = new List<Dictionary<string, object>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string correlation in ReadDictionaryList(run, "commands").SelectMany(x => ReadStringList(x, "correlationIds")).Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                Dictionary<string, object> audit = AuditQuery(new Dictionary<string, string> { ["campaignId"] = campaignId, ["correlationId"] = correlation, ["limit"] = "500", ["oldestFirst"] = "true" });
                foreach (Dictionary<string, object> row in ReadDictionaryList(audit, "entries"))
                {
                    if (seen.Add(ReadString(row, "auditId", Guid.NewGuid().ToString("N")))) evidence.Add(row);
                }
            }
            return evidence;
        }

        private static List<Dictionary<string, object>> RunLiveInteractionTestSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["id"] = "live_interaction_" + id, ["suite"] = "live_interaction", ["passed"] = passed, ["summary"] = summary
            });
            Dictionary<string, object> fakeRun = new Dictionary<string, object>
            {
                ["runId"] = "self", ["campaignId"] = "self", ["gameInstanceId"] = "game", ["mode"] = "individual_chat", ["presentation"] = "headless", ["effects"] = "guarded"
            };
            Dictionary<string, object> first = NormalizeLiveTestCommand(fakeRun, new Dictionary<string, object> { ["commandId"] = "stable", ["operation"] = "send", ["text"] = "hello" });
            Dictionary<string, object> invalid = NormalizeLiveTestCommand(fakeRun, new Dictionary<string, object> { ["operation"] = "send", ["text"] = "" });
            Dictionary<string, object> wrongVersion = NormalizeLiveTestCommand(fakeRun, new Dictionary<string, object> { ["schemaVersion"] = 999, ["operation"] = "open" });
            add("command_schema", first != null && ReadString(first, "status", "") == "queued" && invalid == null && wrongVersion == null, "Command normalization requires the supported schema, valid operations, and non-empty dialogue text.");
            Dictionary<string, object> correspondenceQuiescence = NormalizeLiveTestCommand(
                fakeRun,
                new Dictionary<string, object>
                {
                    ["operation"] = "wait_for_correspondence",
                    ["stableMilliseconds"] = 25000,
                    ["timeoutSeconds"] = 600
                });
            add("correspondence_quiescence_command",
                correspondenceQuiescence != null
                && ReadInt(correspondenceQuiescence, "stableMilliseconds", 0) == 25000
                && ReadInt(correspondenceQuiescence, "timeoutSeconds", 0) == 600,
                "Correspondence certification preserves the bounded client-observed idle barrier instead of silently dropping the command.");
            Dictionary<string, object> socialEnrollment = new Dictionary<string, object>
            {
                ["campaignId"] = "self",
                ["timelineId"] = "main_self",
                ["savePrefix"] = "self_",
                ["mainHeroId"] = "main_hero",
                ["runId"] = "social-self"
            };
            Dictionary<string, object> socialRun = new Dictionary<string, object>(fakeRun, StringComparer.OrdinalIgnoreCase)
            {
                ["mode"] = "social_balance",
                ["enrollment"] = socialEnrollment
            };
            Dictionary<string, object> socialTimeControl = NormalizeLiveTestCommand(
                socialRun,
                new Dictionary<string, object>
                {
                    ["operation"] = "social_time_control",
                    ["timeMode"] = "fast"
                });
            add("social_balance_run_enrollment_fallback",
                socialTimeControl != null
                && object.ReferenceEquals(ReadDictionary(socialTimeControl, "enrollment"), socialEnrollment),
                "Social-balance scenario steps inherit the validated run enrollment when the individual step does not redundantly repeat it.");
            Dictionary<string, object> socialSummary =
                LiveTestRunSummary(socialRun, true);
            add("run_summary_preserves_enrollment",
                object.ReferenceEquals(ReadDictionary(socialSummary, "enrollment"), socialEnrollment),
                "Live-run reports preserve the validated enrollment and immutable evidence fingerprints needed by feature readiness evaluators.");
            Dictionary<string, object> socialProfileTimeout = NormalizeLiveTestCommand(
                socialRun,
                new Dictionary<string, object>
                {
                    ["operation"] = "social_reputation_profile",
                    ["profile"] = "player_favoring_dialogue",
                    ["timeoutSeconds"] = 3600
                });
            Dictionary<string, object> unrelatedSocialTimeout = NormalizeLiveTestCommand(
                socialRun,
                new Dictionary<string, object>
                {
                    ["operation"] = "social_time_control",
                    ["timeMode"] = "fast",
                    ["timeoutSeconds"] = 3600
                });
            add("social_profile_timeout_scope",
                ReadInt(socialProfileTimeout, "timeoutSeconds", 0) == 3600
                && ReadInt(unrelatedSocialTimeout, "timeoutSeconds", 0) == 1800,
                "Only a Social Reputation profile command may retain the one-hour deadline; unrelated social commands keep the ordinary thirty-minute ceiling.");
            add("modes", LiveTestModes.SetEquals(new[] { "individual_chat", "party_chat", "social_event", "wilderness_event", "social_balance", "passive_world", "correspondence", "court_event", "spymaster", "ambassador_official", "kingdom_event", "campaign_command", "government", "party_agency" }), "Only implemented production interaction adapters and isolated deterministic harness adapters are registered in the transport contract.");
            string retrievalFixtureText = LiveTestRetrievedEvidenceText(
                new[]
                {
                    new Dictionary<string, object>
                    {
                        ["data"] = new Dictionary<string, object>
                        {
                            ["memories"] = new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object>
                                {
                                    ["text"] = "Alder-31 beneath the cedar coffer"
                                }
                            },
                            ["priorLines"] = new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object>
                                {
                                    ["text"] = "The western road account was fulfilled"
                                }
                            },
                            ["memorySummary"] = "Selca-49 remains a keepsake."
                        }
                    }
                });
            add("retrieval_phrase_evidence_scope",
                retrievalFixtureText.IndexOf(
                    "Alder-31", StringComparison.OrdinalIgnoreCase) >= 0
                && retrievalFixtureText.IndexOf(
                    "western road", StringComparison.OrdinalIgnoreCase) >= 0
                && retrievalFixtureText.IndexOf(
                    "Selca-49", StringComparison.OrdinalIgnoreCase) >= 0,
                "Witness and non-witness gauntlet checks inspect only retrieved memories, prior lines, and summaries—not the current player prompt.");
            add("exactly_once_states", LiveTestTerminalCommandStates.Contains("completed") && !LiveTestTerminalCommandStates.Contains("leased"), "Exactly-once terminal and retryable states are distinct.");
            Dictionary<string, object> relationshipAuditFixture =
                new Dictionary<string, object>
                {
                    ["promptEnvelope"] = new Dictionary<string, object>
                    {
                        ["npcRelationshipPrompt"] = new Dictionary<string, object>
                        {
                            ["targets"] = new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object>
                                {
                                    ["targetHeroStringId"] = "target",
                                    ["sharedRelationshipHistoryIds"] =
                                        new List<string> { "history-regression-id" }
                                }
                            }
                        }
                    }
                };
            Dictionary<string, object> sanitizedRelationshipAudit =
                SanitizeAuditValue(relationshipAuditFixture, 0)
                    as Dictionary<string, object>;
            Dictionary<string, object> sanitizedRelationshipEnvelope =
                ReadDictionary(sanitizedRelationshipAudit, "promptEnvelope");
            Dictionary<string, object> sanitizedRelationshipPrompt =
                ReadDictionary(sanitizedRelationshipEnvelope, "npcRelationshipPrompt");
            Dictionary<string, object> sanitizedRelationshipTarget =
                ReadDictionaryList(sanitizedRelationshipPrompt, "targets")
                    .FirstOrDefault();
            add("audit_relationship_lineage_depth",
                sanitizedRelationshipTarget != null
                && ReadStringList(sanitizedRelationshipTarget,
                    "sharedRelationshipHistoryIds")
                    .SequenceEqual(new[] { "history-regression-id" }),
                "The real prompt-audit nesting path preserves Shared Relationship History lineage IDs instead of replacing them with a depth-limit placeholder.");
            add("relationship_history_transition_append_evidence",
                TransitionHistoryWasAppended(new HashSet<int> { 1, 2 })
                && TransitionHistoryWasAppended(new HashSet<int> { 2, 3 })
                && !TransitionHistoryWasAppended(new HashSet<int> { 1 })
                && !TransitionHistoryWasAppended(new HashSet<int> { 2 }),
                "A transition proves append semantics by retaining a prior chapter beside a newer ordinal; neither an old-only nor new-only prompt qualifies.");
            DateTimeOffset leaseNow = DateTimeOffset.UtcNow;
            Dictionary<string, object> expiredAccepted = new Dictionary<string, object>
            {
                ["status"] = "accepted",
                ["leaseExpiresUtc"] = leaseNow.AddSeconds(-1).ToString("o")
            };
            Dictionary<string, object> liveAccepted = new Dictionary<string, object>
            {
                ["status"] = "accepted",
                ["leaseExpiresUtc"] = leaseNow.AddSeconds(30).ToString("o")
            };
            add("accepted_result_recovery",
                IsLiveTestCommandLeaseEligible(expiredAccepted, leaseNow)
                && !IsLiveTestCommandLeaseEligible(liveAccepted, leaseNow),
                "An expired accepted command is re-leased to the same game instance so its cached result can be reported after a server restart; an active lease is never duplicated.");
            Dictionary<string, object> orderedRun = new Dictionary<string, object>
            {
                ["commands"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["commandId"] = "open", ["status"] = "accepted",
                        ["leaseExpiresUtc"] = leaseNow.AddSeconds(30).ToString("o")
                    },
                    new Dictionary<string, object>
                    {
                        ["commandId"] = "send", ["status"] = "queued"
                    },
                    new Dictionary<string, object>
                    {
                        ["commandId"] = "close", ["status"] = "queued"
                    }
                }
            };
            add("strict_command_sequence",
                ReadString(FirstLiveTestCommandInSequence(orderedRun), "commandId", "") == "open"
                && !IsLiveTestCommandLeaseEligible(FirstLiveTestCommandInSequence(orderedRun), leaseNow),
                "A queued send or close can never leap over a still-running open command; the bridge leases exactly one command in scenario order.");
            Dictionary<string, object> consentSend = new Dictionary<string, object>
            {
                ["commandId"] = "consent-send", ["operation"] = "send", ["status"] = "completed",
                ["result"] = new Dictionary<string, object>
                {
                    ["rawResponse"] = new Dictionary<string, object>
                    {
                        ["queuedDialogueActions"] = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["command"] = "consent_government_reduction"
                            }
                        }
                    }
                }
            };
            Dictionary<string, object> conditionalFollowUp = new Dictionary<string, object>
            {
                ["commandId"] = "conditional-follow-up", ["operation"] = "send", ["status"] = "queued",
                ["skipWhenPriorDialogueActionQueued"] = "consent_government_reduction"
            };
            Dictionary<string, object> conditionalClose = new Dictionary<string, object>
            {
                ["commandId"] = "conditional-close", ["operation"] = "close", ["status"] = "queued"
            };
            Dictionary<string, object> conditionalRun = new Dictionary<string, object>
            {
                ["commands"] = new List<Dictionary<string, object>>
                {
                    consentSend, conditionalFollowUp, conditionalClose
                },
                ["assertions"] = new List<Dictionary<string, object>>(),
                ["failures"] = new List<Dictionary<string, object>>(),
                ["status"] = "running",
                ["autoCompleteWhenIdle"] = true
            };
            bool conditionalSkipped =
                SkipConditionalLiveTestCommandsWithSatisfiedDialogueActions(
                    conditionalRun, leaseNow);
            add("structured_dialogue_action_conditional_follow_up",
                conditionalSkipped
                && ReadString(conditionalFollowUp, "status", "") == "completed"
                && ReadBool(ReadDictionary(conditionalFollowUp, "result")
                    ?? new Dictionary<string, object>(), "skipped", false)
                && ReadString(FirstLiveTestCommandInSequence(conditionalRun), "commandId", "")
                    == "conditional-close",
                "A natural follow-up is skipped only when the immediately preceding completed production send exposes the exact expected structured dialogue action; prose is never treated as consent.");
            Dictionary<string, object> proseOnlySend = new Dictionary<string, object>
            {
                ["commandId"] = "prose-only-send", ["operation"] = "send", ["status"] = "completed",
                ["result"] = new Dictionary<string, object>
                {
                    ["text"] = "[Bannerlord Reign action queued: consent_government_reduction.]",
                    ["rawResponse"] = new Dictionary<string, object>()
                }
            };
            Dictionary<string, object> proseOnlyFollowUp = new Dictionary<string, object>
            {
                ["commandId"] = "prose-only-follow-up", ["operation"] = "send", ["status"] = "queued",
                ["skipWhenPriorDialogueActionQueued"] = "consent_government_reduction"
            };
            Dictionary<string, object> proseOnlyRun = new Dictionary<string, object>
            {
                ["commands"] = new List<Dictionary<string, object>>
                {
                    proseOnlySend, proseOnlyFollowUp
                },
                ["assertions"] = new List<Dictionary<string, object>>(),
                ["failures"] = new List<Dictionary<string, object>>(),
                ["status"] = "running",
                ["autoCompleteWhenIdle"] = true
            };
            add("conditional_follow_up_ignores_action_like_prose",
                !SkipConditionalLiveTestCommandsWithSatisfiedDialogueActions(
                    proseOnlyRun, leaseNow)
                && ReadString(proseOnlyFollowUp, "status", "") == "queued",
                "Action-like reply prose cannot suppress a natural follow-up without an exact structured queued dialogue action.");
            add("safe_defaults", NormalizeLiveTestEffects("") == "guarded" && NormalizeLiveTestPresentation("") == "headless", "Headless presentation and guarded native actions are the defaults.");
            add("checkpoint_contract", LiveTestOperations.Contains("checkpoint"), "Versioned scenarios can pause at an explicit reload-safe checkpoint.");
            Dictionary<string, object> legacy = NormalizeLiveTestCommand(fakeRun, new Dictionary<string, object>
            {
                ["schemaVersion"] = 1, ["operation"] = "open"
            });
            Dictionary<string, object> current = NormalizeLiveTestCommand(fakeRun, new Dictionary<string, object>
            {
                ["schemaVersion"] = 2, ["operation"] = "save_checkpoint", ["saveName"] = "qualification_A"
            });
            add("schema_v2_compatibility", legacy != null && current != null,
                "Scenario schema v2 adds lifecycle assertions while schema v1 remains accepted for existing MCM and audit callers.");
            add("native_lifecycle_operations",
                LiveTestOperations.Contains("save_checkpoint")
                && LiveTestOperations.Contains("shutdown_game")
                && LiveTestOperations.Contains("social_acknowledge_diplomacy_announcement")
                && LiveTestOperations.Contains("prepare_party_fixture")
                && LiveTestOperations.Contains("prepare_authority_fixture")
                && LiveTestOperations.Contains("prepare_wilderness")
                && LiveTestOperations.Contains("restore_settlement"),
                "Unattended save, shutdown, diplomacy-announcement acknowledgement, disposable party and sovereign-authority preparation, reversible wilderness preparation, and settlement restoration use the exactly-once game command queue.");
            add("ui_calibration_operations",
                LiveTestOperations.Contains("ui_open")
                && LiveTestOperations.Contains("ui_status")
                && LiveTestOperations.Contains("ui_snapshot")
                && LiveTestOperations.Contains("ui_action")
                && LiveTestOperations.Contains("ui_back")
                && LiveTestOperations.Contains("ui_close"),
                "UI calibration navigation, status, snapshot, return, and close actions use the exactly-once game command queue.");
            add("spymaster_operations",
                LiveTestOperations.Contains("spymaster_test")
                && LiveTestOperations.Contains("spymaster_organic")
                && LiveTestModes.Contains("spymaster"),
                "Deterministic and organic in-game Spymaster profiles use the exactly-once game command queue.");
            add("arrest_operations",
                LiveTestOperations.Contains("arrest_test")
                && LiveTestModes.Contains("individual_chat"),
                "Deterministic and organic arrest profiles use the exactly-once game command queue.");
            add("capital_ambassador_operations",
                LiveTestOperations.Contains("capital_ambassador_test")
                && LiveTestModes.Contains("ambassador_official"),
                "Capital/ambassador fixture phases and production official-envoy dialogue use the exactly-once game command queue.");
            add("kingdom_event_operations",
                LiveTestOperations.Contains("kingdom_event_test") && LiveTestModes.Contains("kingdom_event"),
                "Forced production catastrophe/boon acceptance phases use the exactly-once game command queue.");
            add("government_operations",
                LiveTestOperations.Contains("government_test") && LiveTestModes.Contains("government"),
                "Government feature-harness profiles use the exactly-once game command queue and an explicitly advertised transport mode.");
            add("party_agency_operations",
                LiveTestOperations.Contains("party_agency_test") && LiveTestModes.Contains("party_agency"),
                "Temporary noble guest contract and runtime snapshots use the exactly-once game command queue.");
            Dictionary<string, object> partyAgencyTargetedReview = NormalizeLiveTestCommand(
                fakeRun,
                new Dictionary<string, object>
                {
                    ["operation"] = "open",
                    ["mode"] = "party_chat",
                    ["targetedReview"] = true,
                    ["reviewContext"] = "The guest speaks first about whether the journey should continue."
                });
            add("party_agency_natural_language_route",
                partyAgencyTargetedReview != null
                && ReadBool(partyAgencyTargetedReview, "targetedReview", false)
                && !string.IsNullOrWhiteSpace(ReadString(partyAgencyTargetedReview,
                    "reviewContext", "")),
                "One-NPC guest reviews retain their production Party Chat context while player decisions enter only through natural-language send commands.");
            add("passive_world_drain_operations",
                LiveTestOperations.Contains("world_acknowledge_diplomacy_announcement")
                && LiveTestOperations.Contains("world_drain_political_pressure"),
                "Passive-world checkpoints can explicitly drain diplomacy announcements and political-pressure effects while paused.");
            add("identity_boundary_arc_threshold",
                LiveTestRollingArcRequirementIsSatisfied(
                    relatedScenes: 4,
                    identityBoundaryWithheldScenes: 4,
                    playerIdentityKnown: false,
                    rollingArcs: 0,
                    rollingArcSources: 0)
                && !LiveTestRollingArcRequirementIsSatisfied(
                    relatedScenes: 4,
                    identityBoundaryWithheldScenes: 0,
                    playerIdentityKnown: false,
                    rollingArcs: 0,
                    rollingArcSources: 0)
                && LiveTestRollingArcRequirementIsSatisfied(
                    relatedScenes: 4,
                    identityBoundaryWithheldScenes: 0,
                    playerIdentityKnown: true,
                    rollingArcs: 1,
                    rollingArcSources: 4),
                "Rolling-arc qualification counts only identity-safe related scenes: four withheld unknown-identity scenes require no arc, while four eligible scenes require a four-source arc.");
            add("reload_stale_run_reconciliation",
                ShouldInterruptLiveTestRunForInstance(new Dictionary<string, object>
                {
                    ["gameInstanceId"] = "old-instance", ["status"] = "running"
                }, "new-instance")
                && !ShouldInterruptLiveTestRunForInstance(new Dictionary<string, object>
                {
                    ["gameInstanceId"] = "old-instance", ["status"] = "paused"
                }, "new-instance"),
                "A restored non-terminal run from an older game instance is interrupted, while an explicit reload checkpoint remains resumable.");
            add("heartbeat_instance_ordering",
                CompareLiveTestGameInstanceOrder(
                    "game-20260724163141-newer",
                    "game-20260724162958-older") > 0
                && CompareLiveTestGameInstanceOrder(
                    "game-20260724162958-older",
                    "game-20260724163141-newer") < 0
                && LiveTestGameInstanceTimestamp("game-20260724163141-anything") == "20260724163141",
                "A fresh newer campaign host owns the heartbeat lease; delayed heartbeats from an older overlapping launch cannot replace it or interrupt its commands.");
            add("readiness_gates",
                ConversationReadinessGates.ContainsKey("structural_pipeline")
                && ConversationReadinessGates.Values.All(gate => gate.Item1 <= 40)
                && ConversationReadinessGates["structural_pipeline"].Item1 == 40
                && Math.Abs(ConversationReadinessGates["structural_pipeline"].Item2 - 1d) < 0.0001
                && ConversationReadinessGates.ContainsKey("dynamic_characteristics")
                && ConversationReadinessGates["dynamic_characteristics"].Item2 >= 0.95
                && ConversationReadinessGates.ContainsKey("clan_tier_recognition")
                && ConversationReadinessGates["clan_tier_recognition"].Item1 == 30
                && ConversationReadinessGates.ContainsKey("manipulation_capabilities")
                && ConversationReadinessGates["manipulation_capabilities"].Item1 == 40,
                "Readiness promotion requires the declared fixed samples and deterministic or 95-percent pass gates, including 30 clan-tier probes.");
            add("readiness_completed_run_eligibility",
                ConversationReadinessRunIsCompleted(new Dictionary<string, object> { ["status"] = "completed" })
                && !ConversationReadinessRunIsCompleted(new Dictionary<string, object> { ["status"] = "running" })
                && ConversationReadinessRunIsTerminalFailure(new Dictionary<string, object> { ["status"] = "cancelled" })
                && ConversationReadinessRunIsTerminalFailure(new Dictionary<string, object> { ["status"] = "interrupted" })
                && ConversationReadinessRunIsTerminalFailure(new Dictionary<string, object> { ["status"] = "failed" })
                && !ConversationReadinessRunIsTerminalFailure(new Dictionary<string, object> { ["status"] = "running" }),
                "Only completed qualification runs contribute samples; cancelled, interrupted, and failed runs remain explicit clean-window failures.");
            List<Dictionary<string, object>> judgeExpected = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["caseId"] = "C1" },
                new Dictionary<string, object> { ["caseId"] = "C2" }
            };
            List<Dictionary<string, object>> judgeRecovered = new List<Dictionary<string, object>>();
            HashSet<string> judgeExpectedIds = new HashSet<string>(
                new[] { "C1", "C2" }, StringComparer.OrdinalIgnoreCase);
            MergeConversationReadinessJudgeCases(judgeRecovered,
                new[] { new Dictionary<string, object> { ["caseId"] = "C2", ["personalityConsistency"] = 0.9d } },
                judgeExpectedIds);
            List<Dictionary<string, object>> judgeMissing =
                MissingConversationReadinessJudgeCases(judgeExpected, judgeRecovered);
            MergeConversationReadinessJudgeCases(judgeRecovered,
                new[] { new Dictionary<string, object> { ["caseId"] = "C1", ["personalityConsistency"] = 0.95d } },
                judgeExpectedIds);
            add("readiness_judge_partial_batch_recovery",
                judgeMissing.Count == 1
                && ReadString(judgeMissing[0], "caseId", "") == "C1"
                && judgeRecovered.Count == 2
                && judgeRecovered.Select(row => ReadString(row, "caseId", ""))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
                "A valid blinded judgment survives an incomplete provider batch and only missing case IDs require individual recovery.");
            Dictionary<string, object> categorizedCommand = new Dictionary<string, object>
            {
                ["commandId"] = "categorized", ["operation"] = "send", ["status"] = "completed",
                ["result"] = new Dictionary<string, object> { ["text"] = "A grounded reply." }
            };
            List<Dictionary<string, object>> categorizedAssertions = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["commandId"] = "categorized", ["assertion"] = "requiresDynamicCharacteristicWrite", ["passed"] = false },
                new Dictionary<string, object> { ["commandId"] = "categorized", ["assertion"] = "requiresPersonalityEvidence", ["passed"] = true },
                new Dictionary<string, object> { ["commandId"] = "categorized", ["assertion"] = "personalityRubric", ["passed"] = true }
            };
            add("readiness_assertions_are_category_specific",
                !ConversationReadinessCommandPassed(categorizedCommand, categorizedAssertions, new List<Dictionary<string, object>>(), "dynamic_characteristics")
                && ConversationReadinessCommandPassed(categorizedCommand, categorizedAssertions, new List<Dictionary<string, object>>(), "personality_consistency"),
                "A Dynamic Characteristics miss does not incorrectly lower the personality score or another unrelated readiness category.");
            add("readiness_blinded_rubric_is_mandatory",
                !ConversationReadinessCommandPassed(categorizedCommand,
                    categorizedAssertions.Where(row => !ReadString(row, "assertion", "")
                        .Equals("personalityRubric", StringComparison.OrdinalIgnoreCase)).ToList(),
                    new List<Dictionary<string, object>>(), "personality_consistency"),
                "Personality, factual-accuracy, world-knowledge, and group-awareness categories cannot promote when their blinded rubric evaluation is absent.");
            List<Dictionary<string, object>> relationshipHistoryAssertions =
                new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["commandId"] = "categorized",
                        ["assertion"] = "requiresSharedRelationshipHistoryEvidence",
                        ["passed"] = true
                    }
                };
            add("readiness_relationship_history_rubric_is_mandatory",
                !ConversationReadinessCommandPassed(
                    categorizedCommand,
                    relationshipHistoryAssertions,
                    new List<Dictionary<string, object>>(),
                    "shared_relationship_history"),
                "Shared Relationship History cannot promote from structural prompt evidence alone when its blinded quality and privacy rubric is absent.");
            add("dynamic_characteristic_integrity_distinguishes_refusal",
                !ReplyLikelyIntroducesDynamicCharacteristic(
                    "No. I am not a well you draw from. State your business or go.")
                && !ReplyLikelyIntroducesDynamicCharacteristic(
                    "I mentioned no preference. You want me to sit and tell you what I like and dislike, but I will not trade personal details with you.")
                && !ReplyLikelyIntroducesDynamicCharacteristic(
                    "You ask about my preferences as though I keep a ledger of personal tastes for unaffiliated strangers. I do not owe you a preference.")
                && ReplyLikelyIntroducesDynamicCharacteristic(
                    "I cannot stand courtiers who talk around a thing instead of through it."),
                "Qualification permits direct, metalinguistic, and counterfactual refusals while still detecting an unstored explicit stable preference or aversion.");
            List<Dictionary<string, object>> inferredKeepsake = NormalizeDynamicCharacteristicWrites(
                new Dictionary<string, object>(), "lord_6_12",
                new Dictionary<string, object> { ["name"] = "Abagai" },
                new Dictionary<string, object>(),
                "I keep a river stone. Smooth, grey, fits in the palm. I picked it up from a council tent when I was seventeen and have carried it since.");
            List<Dictionary<string, object>> quotedPlayerKeepsake = NormalizeDynamicCharacteristicWrites(
                new Dictionary<string, object>(), "lord_6_12",
                new Dictionary<string, object> { ["name"] = "Abagai" },
                new Dictionary<string, object>(),
                "You told me, \"I keep a blue glass token beneath a cedar box.\" I will remember what you said.");
            add("dynamic_characteristic_keepsake_fallback",
                inferredKeepsake.Count == 1
                && ReadString(inferredKeepsake[0], "text", "").Contains("river stone", StringComparison.OrdinalIgnoreCase)
                && ReadString(inferredKeepsake[0], "category", "") == "personal_history"
                && ReadString(inferredKeepsake[0], "status", "") == "active"
                && quotedPlayerKeepsake.Count == 0,
                "An explicit NPC-owned keepsake receives a minimal grounded companion write, while a quoted player-owned keepsake is not misattributed."
                + " inferred=" + Json.Serialize(inferredKeepsake) + " quoted=" + Json.Serialize(quotedPlayerKeepsake));
            add("dynamic_characteristic_recall_accepts_grounded_paraphrase",
                HasDynamicTextEvidence(
                    "Abalytos has a strong aversion to people who verbally label their own generosity or fairness before acting, seeing it as calculated self-interest.",
                    "Abalytos calls it an aversion: when people tell him they are fair before they have acted, he stops listening to what they say and watches what they take.")
                && HasDynamicTextEvidence(
                    "As a child, Abagai formed the habit of checking a saddle's girth strap before every mount, after a boy she knew was killed when his loose girth caused him to fall under his horse at age seven. The habit persists into adulthood as a principle against dying carelessly.",
                    "Then my saddle strap. I told you the strap was loose and I was not getting on.")
                && !HasDynamicTextEvidence(
                    "Abalytos keeps a small clay disc from his betrothal.",
                    "Abalytos discussed roads, garrisons, and nearby parties.")
                && !HasDynamicTextEvidence(
                    "As a child, Abagai formed the habit of checking a saddle's girth strap before every mount, after a boy she knew was killed when his loose girth caused him to fall under his horse at age seven.",
                    "A childhood habit may persist for years, but I will not offer a personal detail."),
                "Dynamic Characteristic recall accepts visibly grounded grammatical and terse provenance-rich paraphrases without accepting unrelated or generic personal-history language.");
            Dictionary<string, object> identityEvidenceReply = new Dictionary<string, object>
            {
                ["text"] = "I know your name, but I will not indulge this question.",
                ["rawResponse"] = new Dictionary<string, object>
                {
                    ["identityView"] = new Dictionary<string, object>
                    {
                        ["knowsIdentity"] = true,
                        ["usableName"] = "Rhovarion",
                        ["claimedName"] = "Rhovarion",
                        ["identityState"] = "claimed",
                        ["knowledgeSource"] = "self_introduction"
                    }
                }
            };
            add("identity_recognition_uses_private_production_evidence",
                LiveTestReplyRecognizesIdentity(identityEvidenceReply, "Rhovarion", "claimed", "self_introduction"),
                "A personality-grounded refusal need not repeat the player's name aloud when the private production identity view proves correct recognition and provenance.");
            Dictionary<string, object> canonicalPrecedenceReply =
                new Dictionary<string, object>
                {
                    ["rawResponse"] = new Dictionary<string, object>
                    {
                        ["identityView"] = new Dictionary<string, object>
                        {
                            ["knowsIdentity"] = true,
                            ["canonicalNameAllowed"] = true,
                            ["usableName"] = "Calytos",
                            ["claimedName"] = "Rhovarion",
                            ["identityState"] = "verified",
                            ["knowledgeSource"] =
                                "charm_clan_recognition_roll_v3"
                        }
                    }
                };
            add("verified_canonical_identity_outranks_preserved_claimed_alias",
                LiveTestReplyRecognizesIdentity(
                    canonicalPrecedenceReply,
                    "Rhovarion",
                    "claimed",
                    "self_introduction"),
                "A test expecting an introduced alias accepts stronger verified canonical identity only when the claimed alias remains preserved as a claim.");
            Dictionary<string, object> authorityEvidenceReply =
                new Dictionary<string, object>
                {
                    ["text"] =
                        "Gorigos, I recognize your authority, though I oppose this demand.",
                    ["rawResponse"] =
                        new Dictionary<string, object>
                        {
                            ["reply"] =
                                "Gorigos, I recognize your authority, though I oppose this demand.",
                            ["identityView"] =
                                new Dictionary<string, object>
                                {
                                    ["knowsIdentity"] = true,
                                    ["canonicalNameAllowed"] = true,
                                    ["usableName"] = "Gorigos",
                                    ["safeLabel"] =
                                        "the armed stranger",
                                    ["identityState"] = "verified",
                                    ["knowledgeSource"] =
                                        "realm_sovereign",
                                    ["authorityView"] =
                                        new Dictionary<string, object>
                                        {
                                            ["realmSovereignKnown"] =
                                                true,
                                            ["currentSettlementOwnerKnown"] =
                                                true,
                                            ["currentSettlementName"] =
                                                "Zeonica"
                                        }
                                }
                        }
                };
            add("political_authority_assertion_fixture_is_grounded",
                !VerifiedIdentityGroundingContradiction(
                    LiveTestCurrentModelOutput(
                        ReadDictionary(
                            authorityEvidenceReply,
                            "rawResponse")),
                    new Dictionary<string, object>
                    {
                        ["identityView"] =
                            ReadDictionary(
                                ReadDictionary(
                                    authorityEvidenceReply,
                                    "rawResponse"),
                                "identityView")
                    }),
                "Live political-authority evidence can preserve verified sovereign and settlement status while allowing personality-grounded opposition.");
            Dictionary<string, object> verifiedAuthorityContext =
                new Dictionary<string, object>
                {
                    ["identityView"] =
                        ReadDictionary(
                            ReadDictionary(
                                authorityEvidenceReply,
                                "rawResponse"),
                            "identityView")
                };
            string[] groundedRoomHeadings =
            {
                "Zeonica, Lords Hall \u2014 Private Study\n\nWe can speak plainly here.",
                "Zeonica, Lords Hall \u2014 Small Study\n\nWe can speak plainly here.",
                "Zeonica, Lords Hall \u2014 Fercread's Study\n\nWe can speak plainly here.",
                "1084 Summer, day 1 - Zeonica, Small Study\n\nWe can speak plainly here."
            };
            add("political_authority_accepts_settlement_room_headings",
                groundedRoomHeadings.All(reply =>
                    !VerifiedIdentityGroundingContradiction(
                        new Dictionary<string, object>
                        {
                            ["reply"] = reply
                        },
                        verifiedAuthorityContext)),
                "A heading that names the verified settlement remains grounded when a room or sub-location follows a dash or comma.");
            add("political_authority_rejects_conflicting_settlement_heading",
                VerifiedIdentityGroundingContradiction(
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "1084 Summer, day 1 - Amitatys\n\nWe can speak plainly here."
                    },
                    verifiedAuthorityContext),
                "A heading that unambiguously names a different settlement still contradicts the verified current location.");
            add("political_authority_accepts_no_one_as_zero_wars",
                !ContainsContradictoryCurrentWarCount(
                    "Battania is at war with the Western Empire; fen Niabar is at war with no one.",
                    0)
                && !ContainsContradictoryCurrentWarCount(
                    "Fen Niabar is at war with none.",
                    0)
                && ContainsContradictoryCurrentWarCount(
                    "Fen Niabar is at war with one kingdom.",
                    0),
                "Natural zero-war phrases must not be parsed as one war, while a real one-war contradiction still fails.");
            Dictionary<string, object> historicalIdentityEnvelope =
                ReadDictionary(
                    authorityEvidenceReply,
                    "rawResponse");
            historicalIdentityEnvelope["conversationSceneState"] =
                "Earlier in this scene, before the introduction, the observer called the player the armed stranger and could not verify a sovereign office.";
            add("political_authority_assertion_ignores_historical_input_context",
                !VerifiedIdentityGroundingContradiction(
                    LiveTestCurrentModelOutput(
                        historicalIdentityEnvelope),
                    new Dictionary<string, object>
                    {
                        ["identityView"] =
                            ReadDictionary(
                                historicalIdentityEnvelope,
                                "identityView")
                    })
                && VerifiedIdentityGroundingContradiction(
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "You are still the armed stranger. Prove you are sovereign."
                    },
                    new Dictionary<string, object>
                    {
                        ["identityView"] =
                            ReadDictionary(
                                historicalIdentityEnvelope,
                                "identityView")
                    }),
                "Live authority grading inspects only the current model output: historical prompt inputs cannot create false failures, while a current contradiction still fails.");
            Dictionary<string, object> restartedJson = TryParseJsonObject(
                "{\"reply\":\"abandoned prefix "
                + "{\"reply\":\"complete answer\",\"decisionBrief\":{\"goals\":[\"g\"],\"constraints\":[\"c\"]},"
                + "\"actionGate\":{\"needed\":false,\"commitment\":\"roleplay_only\"}}");
            add("provider_restarted_json_prefers_complete_object",
                restartedJson != null
                && ReadString(restartedJson, "reply", "") == "complete answer"
                && ReadDictionary(restartedJson, "actionGate") != null,
                "When a provider abandons a partial JSON object and restarts, the later complete object is parsed instead of clipping the abandoned reply.");
            add("fatal_provider_pause_contract",
                IsFatalLiveTestInfrastructureFailure("{\"code\":\"insufficient_balance\",\"status\":402}")
                && IsFatalLiveTestInfrastructureFailure("HTTP 401 unauthorized API key")
                && !IsFatalLiveTestInfrastructureFailure("HTTP 429 rate limited; retry later"),
                "Fatal billing or authentication responses pause a run for an exactly-once retry, while transient throttling remains provider-middleware work.");
            Dictionary<string, object> close = new Dictionary<string, object> { ["commandId"] = "close", ["operation"] = "close", ["status"] = "completed" };
            Dictionary<string, object> later = new Dictionary<string, object> { ["commandId"] = "later", ["operation"] = "open", ["status"] = "queued" };
            fakeRun["commands"] = new List<Dictionary<string, object>> { close, later };
            fakeRun["failures"] = new List<Dictionary<string, object>>();
            fakeRun["status"] = "running";
            CompleteLiveTestRunIfAppropriate(fakeRun, close);
            bool stayedOpen = ReadString(fakeRun, "status", "") == "running";
            later["status"] = "completed";
            CompleteLiveTestRunIfAppropriate(fakeRun, close);
            add("multi_scene_close", stayedOpen && ReadString(fakeRun, "status", "") == "completed", "An intermediate close cannot finish a scenario while later commands remain queued.");
            Dictionary<string, object> failedOpen = new Dictionary<string, object> { ["commandId"] = "failed_open", ["operation"] = "open", ["status"] = "failed" };
            fakeRun["commands"] = new List<Dictionary<string, object>> { failedOpen };
            fakeRun["failures"] = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["commandId"] = "failed_open" } };
            fakeRun["autoCompleteWhenIdle"] = false;
            fakeRun["status"] = "running";
            CompleteLiveTestRunIfAppropriate(fakeRun, failedOpen);
            add("failed_open_terminal_when_idle", ReadString(fakeRun, "status", "") == "failed",
                "A rejected one-off setup command becomes terminal immediately instead of retaining the campaign's single-run lock.");
            Dictionary<string, object> closeThenWait = new Dictionary<string, object>
            {
                ["commandId"] = "wait", ["operation"] = "wait_for_memory", ["status"] = "completed"
            };
            fakeRun["commands"] = new List<Dictionary<string, object>> { close, closeThenWait };
            fakeRun["failures"] = new List<Dictionary<string, object>>();
            fakeRun["autoCompleteWhenIdle"] = true;
            fakeRun["status"] = "running";
            CompleteLiveTestRunIfAppropriate(fakeRun, closeThenWait);
            add("post_close_wait_completion", ReadString(fakeRun, "status", "") == "completed",
                "A scenario automatically completes when a final memory wait follows the production close command.");

            List<Dictionary<string, object>> awareReplies = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["heroId"] = "npc_a", ["heroName"] = "Aren", ["text"] = "I noticed the rain.", ["reactionTargetHeroStringId"] = "npc_b" },
                new Dictionary<string, object> { ["heroId"] = "npc_b", ["heroName"] = "Bora", ["text"] = "Aren is right about the rain.", ["reactionTargetHeroStringId"] = "npc_a" }
            };
            Dictionary<string, object> assertionRun = new Dictionary<string, object>
            {
                ["assertions"] = new List<Dictionary<string, object>>(), ["failures"] = new List<Dictionary<string, object>>()
            };
            Dictionary<string, object> assertionCommand = new Dictionary<string, object>
            {
                ["commandId"] = "assert", ["assertions"] = new Dictionary<string, object>
                {
                    ["replyContains"] = new List<string> { "rain" }, ["replyExcludes"] = new List<string> { "attack" },
                    ["requiresGroupAwareness"] = true,
                    ["requiresGroupDivergence"] = true,
                    ["requiresActionCompletionGrounding"] = true
                },
                ["result"] = new Dictionary<string, object> { ["replies"] = awareReplies }
            };
            EvaluateLiveTestCommandAssertions(assertionRun, assertionCommand);
            add("scenario_assertions", ReadDictionaryList(assertionRun, "assertions").Count == 5
                && ReadDictionaryList(assertionRun, "assertions").All(row => ReadBool(row, "passed", false))
                && ReadDictionaryList(assertionRun, "failures").Count == 0,
                "Scenario reply and group-awareness assertions are evaluated and persisted in the run report.");
            add("group_divergence_rejects_cloned_replies",
                !LiveTestRepliesShowDistinctAgency(
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["heroId"] = "npc_a",
                            ["text"] = "The northern road is safer because the villages can shelter us."
                        },
                        new Dictionary<string, object>
                        {
                            ["heroId"] = "npc_b",
                            ["text"] = "The northern road is safer because the villages can shelter us."
                        }
                    }, out _),
                "Group qualification rejects cloned conclusions instead of crediting mere multiple-speaker output.");
            List<Dictionary<string, object>> assessmentAwareReplies =
                new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["heroId"] = "npc_a",
                        ["heroName"] = "Aren Valant",
                        ["text"] =
                            "I answer the player while watching the room.",
                        ["reactionTargetHeroStringId"] = "player"
                    },
                    new Dictionary<string, object>
                    {
                        ["heroId"] = "npc_b",
                        ["heroName"] = "Bora Meroc",
                        ["text"] =
                            "Lord Valant's warning was right.",
                        ["reactionTargetHeroStringId"] = "player",
                        ["relationshipAssessments"] =
                            new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object>
                                {
                                    ["observerHeroStringId"] =
                                        "npc_b",
                                    ["targetHeroStringId"] =
                                        "npc_a",
                                    ["severityTier"] =
                                        "meaningful"
                                }
                            }
                    }
                };
            add("group_awareness_accepts_directional_npc_evidence",
                LiveTestRepliesShowGroupAwareness(
                    assessmentAwareReplies),
                "A later NPC's directional assessment toward an earlier speaker proves group awareness even when visible dialogue uses a title or surname instead of the full canonical name.");

            Dictionary<string, object> guardedAttackRecord = new Dictionary<string, object>
            {
                ["command"] = "attack_player_party",
                ["actorHeroStringId"] = "npc_a",
                ["targetHeroStringId"] = "main_hero",
                ["actorClanStringId"] = "npc_clan",
                ["targetClanStringId"] = "player_clan",
                ["actorKingdomStringId"] = "npc_kingdom",
                ["targetKingdomStringId"] = ""
            };
            List<Dictionary<string, object>> guardedReplies = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["text"] = "Guards, stop the player now.",
                    ["rawResponse"] = new Dictionary<string, object>
                    {
                        ["queuedDialogueActions"] = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object> { ["record"] = guardedAttackRecord }
                        }
                    }
                }
            };
            List<Dictionary<string, object>> guardedAudit = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["phase"] = "dialogue.request",
                    ["data"] = new Dictionary<string, object>
                    {
                        ["hero"] = TestDict(
                            "heroStringId", "npc_a", "clanId", "npc_clan", "kingdomId", "npc_kingdom",
                            "mainHeroStringId", "main_hero", "playerClanId", "player_clan", "playerKingdomId", "")
                    }
                },
                new Dictionary<string, object>
                {
                    ["phase"] = "action.auto_queue",
                    ["data"] = new Dictionary<string, object>
                    {
                        ["visibleReply"] = "Guards, stop the player now.",
                        ["actionGate"] = TestDict("needed", true, "intent", "Stop the player.", "reason", "The NPC ordered the guards."),
                        ["queued"] = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["record"] = guardedAttackRecord } }
                    }
                }
            };
            bool guardedPass = LiveTestGroundedGuardedActionsAreSafe(
                TestDict("effects", "guarded"), guardedReplies, guardedAudit, out _);
            guardedAttackRecord["actorClanStringId"] = "player_clan";
            guardedAttackRecord["targetClanStringId"] = "npc_clan";
            bool reversedPass = LiveTestGroundedGuardedActionsAreSafe(
                TestDict("effects", "guarded"), guardedReplies, guardedAudit, out _);
            guardedAttackRecord["actorClanStringId"] = "npc_clan";
            guardedAttackRecord["targetClanStringId"] = "player_clan";
            guardedAttackRecord["command"] = "duel_player";
            guardedAttackRecord["terms"] = TestDict("duelMode", "to_the_death", "lethal", true);
            bool duelPass = LiveTestGroundedGuardedActionsAreSafe(
                TestDict("effects", "guarded"), guardedReplies, guardedAudit, out _);
            ((Dictionary<string, object>)guardedAttackRecord["terms"])["lethal"] = false;
            bool contradictoryDuelPass = LiveTestGroundedGuardedActionsAreSafe(
                TestDict("effects", "guarded"), guardedReplies, guardedAudit, out _);
            add("grounded_guarded_action_direction", guardedPass && !reversedPass && duelPass && !contradictoryDuelPass,
                "Grounded attack and duel actions require correct NPC-to-player direction, and lethal duel terms cannot contradict their mode.");

            Func<string, string, string, string, int, Dictionary<string, object>> relationshipReceipt =
                (id, observer, target, tier, delta) =>
                {
                    int baseDelta = tier == "meaningful" ? (delta < 0 ? -3 : 3)
                        : tier == "harmful_lie" || tier == "hostile" ? -5
                        : tier == "severe" ? -10
                        : tier == "transformative" ? 15
                        : tier == "gift_witness" ? (delta < 0 ? -2 : 1)
                        : tier == "gift_leverage" ? 0
                        : delta;
                    return new Dictionary<string, object>
                    {
                        ["receiptId"] = id, ["exchangeId"] = "relationship_assertion_exchange",
                        ["sourceTurnId"] = "relationship_assertion_turn",
                        ["observerHeroStringId"] = observer, ["targetHeroStringId"] = target,
                        ["actKind"] = tier, ["severityTier"] = tier,
                        ["valence"] = delta < 0 ? "negative" : "positive",
                        ["baseDelta"] = baseDelta, ["modifierDelta"] = delta - baseDelta,
                        ["finalDelta"] = delta, ["priorAffinity"] = 0, ["resultingAffinity"] = delta,
                        ["priorNativeRelation"] = 0, ["resultingNativeRelation"] = delta,
                        ["nativePairDelta"] = delta,
                         ["status"] = "applied", ["nativeApplicationStatus"] = "applied",
                         ["nativeAppliedTs"] = 1L,
                         ["currentConductQuote"] = tier == "routine" || tier == "harmful_lie"
                             ? ""
                             : "exact current conduct",
                         ["nativeReceipt"] = TestDict(
                            "subjectId", observer, "targetId", target, "delta", delta, "nativeRelation", delta),
                        ["idempotent"] = false
                    };
                };
            Func<Dictionary<string, object>, bool, Dictionary<string, object>> relationshipNativeChange =
                (receipt, show) => new Dictionary<string, object>
                {
                    ["subjectId"] = ReadString(receipt, "observerHeroStringId", ""),
                    ["targetId"] = ReadString(receipt, "targetHeroStringId", ""),
                    ["delta"] = ReadInt(receipt, "finalDelta", 0),
                    ["showNotification"] = show,
                    ["receiptIds"] = new List<string> { ReadString(receipt, "receiptId", "") }
                };
            List<Dictionary<string, object>> groupRelationshipReceipts = new List<Dictionary<string, object>>
            {
                relationshipReceipt("rel_a_player", "npc_a", "main_hero", "routine", 1),
                relationshipReceipt("rel_b_player", "npc_b", "main_hero", "routine", -1),
                relationshipReceipt("rel_b_a", "npc_b", "npc_a", "routine", 1)
            };
            List<Dictionary<string, object>> groupNativeChanges = new List<Dictionary<string, object>>
            {
                relationshipNativeChange(groupRelationshipReceipts[0], true),
                relationshipNativeChange(groupRelationshipReceipts[1], true),
                relationshipNativeChange(groupRelationshipReceipts[2], false)
            };
            List<string> groupRelationshipErrors = new List<string>();
            bool groupRelationshipValid = LiveTestRelationshipReceiptSetIsValid(
                groupRelationshipReceipts, groupNativeChanges,
                new List<string> { "npc_a", "npc_b" }, new List<string> { "npc_a", "npc_b" },
                "main_hero", TestDict(
                    "scenario", "general", "requiresEverySpeaker", true,
                    "requiresPlayerReaction", true, "requiresNpcToNpc", true),
                groupRelationshipErrors);
            add("relationship_receipt_direction_and_native_ack",
                groupRelationshipValid && groupRelationshipErrors.Count == 0,
                "Every speaking NPC judges the player, dependent replies can judge an earlier NPC, and each directional receipt has one acknowledged personal native application.");

            Dictionary<string, object> driftReceiptA =
                relationshipReceipt("rel_drift_a", "npc_a", "npc_b", "routine", 1);
            Dictionary<string, object> driftReceiptB =
                relationshipReceipt("rel_drift_b", "npc_b", "npc_a", "routine", 1);
            foreach (Dictionary<string, object> receipt in new[] { driftReceiptA, driftReceiptB })
            {
                receipt["priorNativeRelation"] = -1;
                receipt["resultingNativeRelation"] = 2;
                receipt["nativePairDelta"] = 3;
                receipt["nativeReceipt"] = TestDict(
                    "subjectId", "npc_a", "targetId", "npc_b",
                    "delta", 2, "priorNativeRelation", -1, "nativeRelation", 2);
            }
            Dictionary<string, object> driftNativeChange = TestDict(
                "subjectId", "npc_a", "targetId", "npc_b", "delta", 2,
                "receiptIds", new List<string> { "rel_drift_a", "rel_drift_b" });
            List<string> driftRelationshipErrors = new List<string>();
            bool driftRelationshipValid = LiveTestRelationshipReceiptSetIsValid(
                new List<Dictionary<string, object>> { driftReceiptA, driftReceiptB },
                new List<Dictionary<string, object>> { driftNativeChange },
                new List<string> { "npc_a", "npc_b" }, new List<string> { "npc_a", "npc_b" },
                "main_hero", TestDict(
                    "scenario", "general", "requiresEverySpeaker", true,
                    "requiresPlayerReaction", false, "requiresNpcToNpc", true),
                driftRelationshipErrors);
            add("relationship_receipt_separates_requested_delta_from_observed_native_transition",
                driftRelationshipValid && driftRelationshipErrors.Count == 0,
                "The qualification gate proves the requested aggregate conversation delta from the native command while separately preserving the observed before/after relation, which may also include a concurrent passive projection.");

            Dictionary<string, object> bandLimitedReceipt =
                relationshipReceipt("rel_band_limited", "npc_a", "npc_b", "routine", 0);
            bandLimitedReceipt["status"] = "routine_band_limited";
            bandLimitedReceipt["priorAffinity"] = 10;
            bandLimitedReceipt["resultingAffinity"] = 10;
            bandLimitedReceipt["priorNativeRelation"] = 10;
            bandLimitedReceipt["resultingNativeRelation"] = 9;
            bandLimitedReceipt["nativePairDelta"] = -1;
            bandLimitedReceipt["nativeReceipt"] = TestDict(
                "subjectId", "npc_b", "targetId", "npc_a",
                "delta", -1, "priorNativeRelation", 10, "nativeRelation", 9);
            Dictionary<string, object> inwardReceipt =
                relationshipReceipt("rel_inward", "npc_b", "npc_a", "routine", -1);
            inwardReceipt["priorNativeRelation"] = 10;
            inwardReceipt["resultingNativeRelation"] = 9;
            inwardReceipt["nativePairDelta"] = -1;
            inwardReceipt["nativeReceipt"] = TestDict(
                "subjectId", "npc_b", "targetId", "npc_a",
                "delta", -1, "priorNativeRelation", 10, "nativeRelation", 9);
            Dictionary<string, object> bandLimitedNativeChange = TestDict(
                "subjectId", "npc_b", "targetId", "npc_a", "delta", -1,
                "receiptIds", new List<string> { "rel_band_limited", "rel_inward" });
            List<string> bandLimitedRelationshipErrors = new List<string>();
            bool bandLimitedRelationshipValid = LiveTestRelationshipReceiptSetIsValid(
                new List<Dictionary<string, object>> { bandLimitedReceipt, inwardReceipt },
                new List<Dictionary<string, object>> { bandLimitedNativeChange },
                new List<string> { "npc_a", "npc_b" }, new List<string> { "npc_a", "npc_b" },
                "main_hero", TestDict(
                    "scenario", "general", "requiresEverySpeaker", true,
                    "requiresPlayerReaction", false, "requiresNpcToNpc", true),
                bandLimitedRelationshipErrors);
            add("relationship_receipt_links_band_limited_zero_to_reciprocal_pair",
                bandLimitedRelationshipValid && bandLimitedRelationshipErrors.Count == 0,
                "A band-limited zero directional judgment is still acknowledged with the reciprocal nonzero native pair application, while contributing zero to the requested aggregate.");

            Dictionary<string, object> isolatedBandLimitedReceipt =
                relationshipReceipt("rel_isolated_band_limited", "npc_a", "npc_b", "routine", 0);
            isolatedBandLimitedReceipt["status"] = "routine_band_limited";
            isolatedBandLimitedReceipt["priorAffinity"] = 10;
            isolatedBandLimitedReceipt["resultingAffinity"] = 10;
            isolatedBandLimitedReceipt["priorNativeRelation"] = 14;
            isolatedBandLimitedReceipt["resultingNativeRelation"] = 14;
            isolatedBandLimitedReceipt["nativePairDelta"] = 0;
            isolatedBandLimitedReceipt["nativeApplicationStatus"] = "skipped";
            isolatedBandLimitedReceipt["nativeAppliedTs"] = 0L;
            isolatedBandLimitedReceipt["nativeReceipt"] = new Dictionary<string, object>();
            List<string> isolatedBandLimitedErrors = new List<string>();
            bool isolatedBandLimitedValid = LiveTestRelationshipReceiptSetIsValid(
                new List<Dictionary<string, object>> { isolatedBandLimitedReceipt },
                new List<Dictionary<string, object>>(),
                new List<string> { "npc_a" }, new List<string> { "npc_a", "npc_b" },
                "main_hero", TestDict(
                    "scenario", "general", "requiresEverySpeaker", true,
                    "requiresPlayerReaction", false, "requiresNpcToNpc", true),
                isolatedBandLimitedErrors);
            add("relationship_receipt_accepts_isolated_band_limited_zero",
                isolatedBandLimitedValid && isolatedBandLimitedErrors.Count == 0,
                "An isolated outward routine judgment at the familiarity boundary remains a documented zero with no unnecessary native pair command.");

            List<Dictionary<string, object>> invalidRelationshipReceipts =
                groupRelationshipReceipts.Select(row => new Dictionary<string, object>(row)).ToList();
            invalidRelationshipReceipts.Add(new Dictionary<string, object>(groupRelationshipReceipts[0])
            {
                ["receiptId"] = "duplicate_pair"
            });
            invalidRelationshipReceipts.Add(relationshipReceipt("self_pair", "npc_a", "npc_a", "routine", 1));
            List<string> invalidRelationshipErrors = new List<string>();
            bool invalidRelationshipAccepted = LiveTestRelationshipReceiptSetIsValid(
                invalidRelationshipReceipts, groupNativeChanges,
                new List<string> { "npc_a", "npc_b" }, new List<string> { "npc_a", "npc_b" },
                "main_hero", TestDict(
                    "scenario", "general", "requiresEverySpeaker", true,
                    "requiresPlayerReaction", true, "requiresNpcToNpc", true),
                invalidRelationshipErrors);
            add("relationship_receipt_rejects_duplicate_and_self",
                !invalidRelationshipAccepted
                && invalidRelationshipErrors.Any(error => error.IndexOf("More than one net receipt", StringComparison.OrdinalIgnoreCase) >= 0)
                && invalidRelationshipErrors.Any(error => error.IndexOf("self-directed", StringComparison.OrdinalIgnoreCase) >= 0),
                "The qualification gate rejects stacked observer-target judgments and every self-directed relationship change.");

            Dictionary<string, object> verifiedLieReceipt =
                relationshipReceipt("verified_lie", "npc_a", "main_hero", "harmful_lie", -5);
            verifiedLieReceipt["lieCheckId"] = "lie_verified";
            verifiedLieReceipt["verifiedLieCheck"] = true;
            List<string> verifiedLieErrors = new List<string>();
            bool verifiedLieValid = LiveTestRelationshipReceiptSetIsValid(
                new List<Dictionary<string, object>> { verifiedLieReceipt },
                new List<Dictionary<string, object>> { relationshipNativeChange(verifiedLieReceipt, true) },
                new List<string> { "npc_a" }, new List<string> { "npc_a" }, "main_hero",
                TestDict("scenario", "verified_lie", "requiresEverySpeaker", true, "requiresPlayerReaction", true),
                verifiedLieErrors);
            Dictionary<string, object> unsupportedReceipt =
                relationshipReceipt("unsupported_claim", "npc_a", "main_hero", "meaningful", -3);
            unsupportedReceipt["verifiedLieCheck"] = false;
            List<string> unsupportedErrors = new List<string>();
            bool unsupportedValid = LiveTestRelationshipReceiptSetIsValid(
                new List<Dictionary<string, object>> { unsupportedReceipt },
                new List<Dictionary<string, object>> { relationshipNativeChange(unsupportedReceipt, true) },
                new List<string> { "npc_a" }, new List<string> { "npc_a" }, "main_hero",
                TestDict("scenario", "unsupported_claim", "requiresEverySpeaker", true, "requiresPlayerReaction", true),
                unsupportedErrors);
            add("relationship_lie_evidence_policy",
                verifiedLieValid && unsupportedValid
                && verifiedLieErrors.Count == 0 && unsupportedErrors.Count == 0,
                "Evidence-backed harmful lies require a verified lie receipt and -4..-7 consequence, while unsupported claims cannot use that tier.");

            Dictionary<string, object> missingCurrentConduct =
                relationshipReceipt("missing_current_conduct", "npc_a", "main_hero", "meaningful", -3);
            missingCurrentConduct["currentConductQuote"] = "";
            List<string> missingCurrentConductErrors = new List<string>();
            bool missingCurrentConductAccepted = LiveTestRelationshipReceiptSetIsValid(
                new List<Dictionary<string, object>> { missingCurrentConduct },
                new List<Dictionary<string, object>> { relationshipNativeChange(missingCurrentConduct, true) },
                new List<string> { "npc_a" }, new List<string> { "npc_a" }, "main_hero",
                TestDict("scenario", "general", "requiresEverySpeaker", true, "requiresPlayerReaction", true),
                missingCurrentConductErrors);
            add("relationship_non_routine_requires_persisted_current_conduct",
                !missingCurrentConductAccepted
                && missingCurrentConductErrors.Any(error =>
                    error.IndexOf("current-conduct evidence", StringComparison.OrdinalIgnoreCase) >= 0),
                "The live qualification gate rejects a non-routine consequence whose exact current-conduct proof was not persisted.");

            Dictionary<string, object> recipientGift =
                relationshipReceipt("gift_recipient", "npc_b", "main_hero", "meaningful", 3);
            recipientGift["actKind"] = "accepted_material_gift";
            recipientGift["giftRecipientHeroStringId"] = "npc_b";
            recipientGift["benefitEventId"] = "benefit_gift";
            recipientGift["benefitLedger"] = TestDict(
                "benefitId", "benefit_gift", "giverId", "main_hero", "recipientId", "npc_b",
                "originalDelta", 3, "retainedAppreciation", 1d, "status", "retained");
            Dictionary<string, object> giftWitness =
                relationshipReceipt("gift_witness", "npc_a", "main_hero", "gift_witness", -2);
            giftWitness["giftRecipientHeroStringId"] = "npc_b";
            List<Dictionary<string, object>> giftReceipts =
                new List<Dictionary<string, object>> { recipientGift, giftWitness };
            List<string> giftErrors = new List<string>();
            bool giftValid = LiveTestRelationshipReceiptSetIsValid(
                giftReceipts,
                giftReceipts.Select(receipt => relationshipNativeChange(receipt, true)).ToList(),
                new List<string> { "npc_a", "npc_b" }, new List<string> { "npc_a", "npc_b" },
                "main_hero", TestDict(
                    "scenario", "gift_recipient", "requiresEverySpeaker", true,
                    "requiresPlayerReaction", true, "expectedGiftRecipientHeroStringId", "npc_b"),
                giftErrors);
            Dictionary<string, object> leverageReceipt =
                relationshipReceipt("gift_leverage", "npc_b", "main_hero", "gift_leverage", -15);
            leverageReceipt["benefitEventId"] = "benefit_gift";
            leverageReceipt["benefitLedger"] = TestDict(
                "benefitId", "benefit_gift", "giverId", "main_hero", "recipientId", "npc_b",
                "originalDelta", 3, "retainedAppreciation", 0.25d, "status", "diminished");
            List<string> leverageErrors = new List<string>();
            bool leverageValid = LiveTestRelationshipReceiptSetIsValid(
                new List<Dictionary<string, object>> { leverageReceipt },
                new List<Dictionary<string, object>> { relationshipNativeChange(leverageReceipt, true) },
                new List<string> { "npc_b" }, new List<string> { "npc_b" }, "main_hero",
                TestDict(
                    "scenario", "gift_leverage", "requiresEverySpeaker", true,
                    "requiresPlayerReaction", true, "expectedGiftRecipientHeroStringId", "npc_b"),
                leverageErrors);
            add("relationship_gift_recipient_witness_and_leverage",
                giftValid && leverageValid && giftErrors.Count == 0 && leverageErrors.Count == 0,
                "The named recipient owns the durable benefit, witnesses retain independent proportionate reactions, and a later coercive demand links and diminishes appreciation.");

            string sourceRoot = FindVerificationSourceRoot();
            Func<string, string> clientSourcePath = fileName => string.IsNullOrWhiteSpace(sourceRoot)
                ? "" : VerificationSourceLocator.ResolveUnique(Path.Combine(sourceRoot, "ReignBeta"), fileName, "src");
            Func<string, string> serverSourcePath = fileName => string.IsNullOrWhiteSpace(sourceRoot)
                ? "" : VerificationSourceLocator.ResolveUnique(Path.Combine(sourceRoot, "ReignServer"), fileName, "src");
            string hostPath = clientSourcePath("ReignLiveInteractionTestHost.cs");
            string eligibilityPath = clientSourcePath("ReignConversationEligibility.cs");
            string partySessionPath = clientSourcePath("ReignPartyChatSession.cs");
            string individualManagerPath = clientSourcePath("ReignIndividualChatScreenManager.cs");
            string individualVmPath = clientSourcePath("ReignIndividualChatScreenVM.cs");
            string uiCalibrationPath = clientSourcePath("ReignLiveInteractionUiCalibrationHost.cs");
            string auditRunnerPath = clientSourcePath("ReignNpcDialogueAuditRunner.cs");
            string correspondencePath = clientSourcePath("ReignCorrespondenceClient.cs");
            string socialEventClientPath = clientSourcePath("ReignSocialEventClient.cs");
            string actionValidatorPath = clientSourcePath("ReignActionValidator.cs");
            string identitySystemPath = serverSourcePath("IdentitySystem.cs");
            string socialReputationPath = serverSourcePath("SocialReputation.cs");
            string worldRelationshipPath = serverSourcePath("WorldRelationshipModel.cs");
            string aiBehaviorPath = clientSourcePath("ReignAICampaignBehavior.cs");
            string settingsPath = clientSourcePath("ReignBetaSettings.cs");
            string socialVmPath = clientSourcePath("ReignSocialEventScreenVM.cs");
            string subModulePath = clientSourcePath("SubModule.cs");
            string liveClientPath = clientSourcePath("ReignLiveTestClient.cs");
            string diplomacyBehaviorPath = clientSourcePath("ReignWorldDiplomacyCampaignBehavior.cs");
            string passiveWorldControlPath = string.IsNullOrWhiteSpace(sourceRoot) ? "" :
                VerificationSourceLocator.ResolveUnique(
                    Path.Combine(sourceRoot, "ReignServer"),
                    "PassiveWorldControl.cs",
                    "ReignLiveTest/Features/WorldSimulation");
            string initializationGatePath = clientSourcePath("ReignCampaignInitializationGate.cs");
            string campaignPreparationPath = clientSourcePath("ReignCampaignPreparationCampaignBehavior.cs");
            string characterInitializationPath = clientSourcePath("ReignCharacterEditorCampaignBehavior.cs");
            string notablePopupPath = clientSourcePath("ReignNotableGenerationPopupManager.cs");
            string notableMbtiPath = serverSourcePath("NotableMbtiProfiles.cs");
            string serverProgramPath = serverSourcePath("Program.cs");
            string qualificationRunnerPath = string.IsNullOrWhiteSpace(sourceRoot) ? "" :
                VerificationSourceLocator.ResolveUnique(
                    Path.Combine(sourceRoot, "ReignServer"),
                    "QualificationRunner.cs",
                    "ReignLiveTest/Features/Dialogue");
            string serverLiveTestPath = serverSourcePath("LiveInteractionTest.cs");
            string lifecycleCliPath = string.IsNullOrWhiteSpace(sourceRoot) ? "" :
                VerificationSourceLocator.ResolveUnique(
                    Path.Combine(sourceRoot, "ReignServer"),
                    "Program.cs",
                    "ReignLiveTest");
            string continuousRelationshipPath = serverSourcePath("ContinuousRelationshipWorker.cs");
            string saveSyncPath = serverSourcePath("SaveSync.cs");
            string postgreSqlSaveSyncPath = serverSourcePath("PostgreSqlSaveSync.cs");
            string campaignBackupsPath = serverSourcePath("CampaignBackups.cs");
            string worldDiplomacyDirectorPath = serverSourcePath("WorldDiplomacyDirector.cs");
            string kingdomLeaderDiplomacyPath = serverSourcePath("KingdomLeaderDiplomacy.cs");
            string saveBehaviorPath = clientSourcePath("ReignSaveSyncCampaignBehavior.cs");
            string saveCoordinatorPath = clientSourcePath("ReignSaveSyncCoordinator.cs");
            string mainThreadPath = clientSourcePath("ReignMainThread.cs");
            string nativeUiCalibrationPath = clientSourcePath("ReignLiveInteractionNativeUiCalibrationHost.cs");
            string relationshipBehaviorPath = clientSourcePath("ReignRelationshipCampaignBehavior.cs");
            string rebellionBehaviorPath = clientSourcePath("ReignRebellionCampaignBehavior.cs");
            string rulerBehaviorPath = clientSourcePath("ReignRulerReputationCampaignBehavior.cs");
            string debugActionsPath = clientSourcePath("ReignBetaDebugActions.cs");
            string eventBehaviorPath = clientSourcePath("ReignSocialEventsCampaignBehavior.cs");
            string familyBehaviorPath = clientSourcePath("ReignFamilyCampaignBehavior.cs");
            string hostSource = File.Exists(hostPath) ? File.ReadAllText(hostPath) : "";
            string eligibilitySource = File.Exists(eligibilityPath) ? File.ReadAllText(eligibilityPath) : "";
            string partySessionSource = File.Exists(partySessionPath) ? File.ReadAllText(partySessionPath) : "";
            string individualManagerSource = File.Exists(individualManagerPath) ? File.ReadAllText(individualManagerPath) : "";
            string individualVmSource = File.Exists(individualVmPath) ? File.ReadAllText(individualVmPath) : "";
            string uiCalibrationSource = File.Exists(uiCalibrationPath) ? File.ReadAllText(uiCalibrationPath) : "";
            string auditRunnerSource = File.Exists(auditRunnerPath) ? File.ReadAllText(auditRunnerPath) : "";
            string correspondenceSource = File.Exists(correspondencePath) ? File.ReadAllText(correspondencePath) : "";
            string socialEventClientSource = File.Exists(socialEventClientPath) ? File.ReadAllText(socialEventClientPath) : "";
            string actionValidatorSource = File.Exists(actionValidatorPath) ? File.ReadAllText(actionValidatorPath) : "";
            string identitySystemSource = File.Exists(identitySystemPath) ? File.ReadAllText(identitySystemPath) : "";
            string socialReputationSource = File.Exists(socialReputationPath) ? File.ReadAllText(socialReputationPath) : "";
            string worldRelationshipSource = File.Exists(worldRelationshipPath) ? File.ReadAllText(worldRelationshipPath) : "";
            string aiBehaviorSource = File.Exists(aiBehaviorPath) ? File.ReadAllText(aiBehaviorPath) : "";
            string settingsSource = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "";
            string socialVmSource = File.Exists(socialVmPath) ? File.ReadAllText(socialVmPath) : "";
            string subModuleSource = File.Exists(subModulePath) ? File.ReadAllText(subModulePath) : "";
            string liveClientSource = File.Exists(liveClientPath) ? File.ReadAllText(liveClientPath) : "";
            string diplomacyBehaviorSource = File.Exists(diplomacyBehaviorPath) ? File.ReadAllText(diplomacyBehaviorPath) : "";
            string passiveWorldControlSource = File.Exists(passiveWorldControlPath) ? File.ReadAllText(passiveWorldControlPath) : "";
            string initializationGateSource = File.Exists(initializationGatePath) ? File.ReadAllText(initializationGatePath) : "";
            string campaignPreparationSource = File.Exists(campaignPreparationPath) ? File.ReadAllText(campaignPreparationPath) : "";
            string characterInitializationSource = File.Exists(characterInitializationPath) ? File.ReadAllText(characterInitializationPath) : "";
            string notablePopupSource = File.Exists(notablePopupPath) ? File.ReadAllText(notablePopupPath) : "";
            string notableMbtiSource = File.Exists(notableMbtiPath) ? File.ReadAllText(notableMbtiPath) : "";
            string serverProgramSource = File.Exists(serverProgramPath) ? File.ReadAllText(serverProgramPath) : "";
            string qualificationRunnerSource = File.Exists(qualificationRunnerPath) ? File.ReadAllText(qualificationRunnerPath) : "";
            string serverLiveTestSource = File.Exists(serverLiveTestPath) ? File.ReadAllText(serverLiveTestPath) : "";
            string lifecycleCliSource = File.Exists(lifecycleCliPath) ? File.ReadAllText(lifecycleCliPath) : "";
            string continuousRelationshipSource = File.Exists(continuousRelationshipPath) ? File.ReadAllText(continuousRelationshipPath) : "";
            string saveSyncSource = File.Exists(saveSyncPath) ? File.ReadAllText(saveSyncPath) : "";
            string postgreSqlSaveSyncSource = File.Exists(postgreSqlSaveSyncPath) ? File.ReadAllText(postgreSqlSaveSyncPath) : "";
            string campaignBackupsSource = File.Exists(campaignBackupsPath) ? File.ReadAllText(campaignBackupsPath) : "";
            string worldDiplomacyDirectorSource = File.Exists(worldDiplomacyDirectorPath) ? File.ReadAllText(worldDiplomacyDirectorPath) : "";
            string kingdomLeaderDiplomacySource = File.Exists(kingdomLeaderDiplomacyPath) ? File.ReadAllText(kingdomLeaderDiplomacyPath) : "";
            string saveBehaviorSource = File.Exists(saveBehaviorPath) ? File.ReadAllText(saveBehaviorPath) : "";
            string saveCoordinatorSource = File.Exists(saveCoordinatorPath) ? File.ReadAllText(saveCoordinatorPath) : "";
            string mainThreadSource = File.Exists(mainThreadPath) ? File.ReadAllText(mainThreadPath) : "";
            string nativeUiCalibrationSource = File.Exists(nativeUiCalibrationPath) ? File.ReadAllText(nativeUiCalibrationPath) : "";
            string relationshipBehaviorSource = File.Exists(relationshipBehaviorPath) ? File.ReadAllText(relationshipBehaviorPath) : "";
            string rebellionBehaviorSource = File.Exists(rebellionBehaviorPath) ? File.ReadAllText(rebellionBehaviorPath) : "";
            string rulerBehaviorSource = File.Exists(rulerBehaviorPath) ? File.ReadAllText(rulerBehaviorPath) : "";
            string debugActionsSource = File.Exists(debugActionsPath) ? File.ReadAllText(debugActionsPath) : "";
            string eventBehaviorSource = File.Exists(eventBehaviorPath) ? File.ReadAllText(eventBehaviorPath) : "";
            string familyBehaviorSource = File.Exists(familyBehaviorPath) ? File.ReadAllText(familyBehaviorPath) : "";
            bool adapterParity = hostSource.Contains("ReignIndividualChatScreenVM")
                && hostSource.Contains("ReignPartyChatScreenVM")
                && hostSource.Contains("ReignSocialEventScreenVM")
                && hostSource.Contains("StartLiveTestWildernessEventAsync")
                && hostSource.Contains("SendAutomationLineAsync")
                && !hostSource.Contains("McmTestModeEnabled");
            add("production_adapter_parity", adapterParity, "All live adapters delegate to production controllers without an MCM test-mode dependency.");
            bool wildernessGroupFixture =
                hostSource.Contains(
                    "minimumWildernessParticipants")
                && hostSource.Contains(
                    "StartLiveTestWildernessEventAsync(")
                && qualificationRunnerSource.Contains(
                    "partyGroup.Take(2).ToList()")
                && qualificationRunnerSource.Contains(
                    "[\"minimumParticipants\"]");
            add(
                "wilderness_group_fixture",
                wildernessGroupFixture,
                "Focused wilderness qualification requests two eligible production party participants and refuses to grade a single-NPC event as a group.");
            add("typed_injected_parity", socialVmSource.Contains("SendAutomationLineAsync(InputText, string.Empty)"), "Typed social-event input delegates to the same send method used by injected input.");
            bool adultEligibility = eligibilitySource.Contains("HeroComesOfAge")
                && eligibilitySource.Contains("!hero.IsChild")
                && hostSource.Contains("ReignConversationEligibility.IsAdultLivingNpc")
                && partySessionSource.Contains("ReignConversationEligibility.IsAdultLivingNpc")
                && individualManagerSource.Contains("TryValidateAdultConversationHero")
                && auditRunnerSource.Contains("ReignConversationEligibility.IsAdultLivingNpc")
                && correspondenceSource.Contains("ReignConversationEligibility.IsAdultLivingNpc");
            add("adult_conversation_eligibility", adultEligibility,
                "Every player-facing conversation and automation target path shares Bannerlord's native adulthood invariant.");
            bool conversationViolenceGates = actionValidatorSource.Contains("NpcAttackPlayerRelationThreshold = -40")
                && actionValidatorSource.Contains("LethalDuelPlayerRelationThreshold = -80")
                && actionValidatorSource.Contains("ReignConversationEligibility.IsAdultLivingNpc(actorHero)")
                && actionValidatorSource.Contains("IsLethalDuel(action)")
                && settingsSource.Contains("ExecuteConversationActions")
                && aiBehaviorSource.Contains("return settings.ExecuteConversationActions;")
                && hostSource.Contains("settings.ExecuteConversationActions = false")
                && hostSource.Contains("settings.ExecuteConversationActions = _oldConversationActions");
            add("conversation_violence_execution_gates", conversationViolenceGates,
                "Normal conversation actions execute independently of strategy settings, while attacks and lethal duels enforce adult actors and distinct personal-relation thresholds.");
            bool lifecycle = subModuleSource.Contains("ReignLiveInteractionTestHost.ApplicationTick(dt)")
                && hostSource.Contains("CompletedCommandResults.TryGetValue")
                && hostSource.Contains("RememberCommandResult")
                && hostSource.Contains("RestoreSettings();");
            add("host_lifecycle", lifecycle, "The campaign host is registered, caches completed results for transport reconciliation, and restores guarded settings.");
            bool pregnancyWarningCalibration =
                individualManagerSource.Contains("case \"show-pregnancy-warning\"")
                && individualManagerSource.Contains("AutomationModalFocusOwned")
                && individualManagerSource.Contains("AutomationActiveStateBlocked")
                && individualVmSource.Contains("TryShowPregnancyWarningFixture")
                && individualVmSource.Contains("restricted to provider-free UI calibration")
                && individualVmSource.Contains("provider-free-ui-calibration-pregnancy-warning")
                && individualVmSource.Contains("TaskCompletionSource<PregnancyDecisionReceipt>")
                && individualVmSource.Contains("FinishPregnancyDecision(\"proceed\", true, true")
                && individualVmSource.Contains("FinishPregnancyDecision(\"pull_out\", true, true")
                && hostSource.Contains("Task.WhenAny(completion, boundedDeadline)")
                && hostSource.Contains("Task.Delay(TimeSpan.FromSeconds(30))")
                && hostSource.Contains("if (finished != completion)")
                && hostSource.Contains("await completion.ConfigureAwait(false)")
                && uiCalibrationSource.Contains("[\"individualChatModalFocusOwned\"]")
                && uiCalibrationSource.Contains("[\"individualChatActiveStateBlocked\"]")
                && uiCalibrationSource.Contains("[\"individualChatPregnancy\"]")
                && lifecycleCliSource.Contains("show-pregnancy-warning")
                && lifecycleCliSource.Contains("control --op confirm|deny");
            add("individual_chat_provider_free_pregnancy_warning_completion",
                pregnancyWarningCalibration,
                "Provider-free Individual Chat calibration presents production pregnancy-warning bindings, reports modal focus/input blocking, and awaits real terminal receipts instead of a fixed delay.");
            add("qualification_party_fixture",
                LiveTestOperations.Contains("prepare_party_fixture")
                && hostSource.Contains("case \"prepare_party_fixture\"")
                && hostSource.Contains("Hero.AllAliveHeroes")
                && hostSource.Contains("AddHeroToPartyAction.Apply(hero, party, false)")
                && hostSource.Contains("[\"disposableSaveFixture\"] = true")
                && qualificationRunnerSource.Contains("EnsureQualificationPartyFixture(")
                && qualificationRunnerSource.IndexOf(
                    "EnsureQualificationPartyFixture(",
                    StringComparison.Ordinal)
                    < qualificationRunnerSource.IndexOf(
                        "Qualification requires five party-chat-eligible heroes",
                        StringComparison.Ordinal),
                "A disposable native party roster is prepared automatically before the group matrix when a clean baseline has fewer than five eligible companions.");
            add("qualification_authority_fixture",
                LiveTestOperations.Contains(
                    "prepare_authority_fixture")
                && hostSource.Contains(
                    "case \"prepare_authority_fixture\"")
                && hostSource.Contains(
                    "confirmDisposableCampaign")
                && hostSource.Contains(
                    "AdoptHeroAction.Apply(familyWitness)")
                && hostSource.Contains(
                    "ChangeGovernorAction.Apply(")
                && hostSource.Contains(
                    "[\"currentEnemyKingdomCount\"]")
                && qualificationRunnerSource.Contains(
                    "--prepare-authority-fixture")
                && qualificationRunnerSource.Contains(
                    "EnsureQualificationAuthorityFixture("),
                "A confirmed disposable native fixture makes the player ruler and settlement owner while separately materializing same-clan, immediate-family, governor, and subject-vassal observers with exact diplomacy evidence.");
            add("identity_public_standing_foreground_scope",
                identitySystemSource.Contains(
                    "ReconcileSocialRelationshipForObserverSubject(")
                && socialReputationSource.Contains(
                    "private static void ReconcileSocialRelationshipForObserverSubject(")
                && socialReputationSource.Contains(
                    "new[] { subjectId }, worldDay);")
                && worldRelationshipSource.Contains(
                    "private static Dictionary<string, object> ResolveEffectiveAttitude(")
                && worldRelationshipSource.Contains(
                    "int effective = hasPair ? Clamp(personal + standingValue"),
                "A verified foreground encounter may refresh only the subject's indexed Public Standing; effective attitude remains a pure directional read and no observer projection is copied into relationship state.");
            add("ruler_diplomacy_uses_personal_affinity_only",
                worldRelationshipSource.Contains(
                    "private static Dictionary<string, object> ResolveRulerDiplomaticAttitude(")
                && worldRelationshipSource.Contains(
                    "attitude[\"effectiveAttitude\"] = personal;")
                && kingdomLeaderDiplomacySource.Contains(
                    "ResolveRulerDiplomaticAttitude(connection")
                && worldDiplomacyDirectorSource.Contains(
                    "ResolveRulerDiplomaticAttitude(connection")
                && kingdomLeaderDiplomacySource.Contains(
                    "observer + \"|\" + target + \"|magnitude\") - 1) % 3")
                && kingdomLeaderDiplomacySource.Contains(
                    "DailyRulerRelationshipRollsEnabled = false")
                && kingdomLeaderDiplomacySource.Contains(
                    "if (!DailyRulerRelationshipRollsEnabled)")
                && kingdomLeaderDiplomacySource.Contains(
                    "legacyRemoteRollsToday")
                && !kingdomLeaderDiplomacySource.Contains(
                    "disabled_daily_rolls_observed")
                && !kingdomLeaderDiplomacySource.Contains("remote 1d5"),
                "Ruler diplomacy and breakthroughs use directional personal affinity without Public Standing, while the independent daily ruler dice pass is explicitly disabled.");
            add("rebellion_relationship_threshold_is_inclusive_negative_thirty_five",
                rebellionBehaviorSource.Contains(
                    "private const int RelationshipThreshold = -35;")
                && rebellionBehaviorSource.Contains(
                    "relation <= RelationshipThreshold"),
                "NPC rebellion eligibility includes directional effective attitude -35 and every lower value.");
            add("diplomacy_narration_follows_authoritative_roll",
                worldDiplomacyDirectorSource.Contains(
                    "private static string SelectDiplomaticOutcomeReason(")
                && worldDiplomacyDirectorSource.Contains("acceptReason")
                && worldDiplomacyDirectorSource.Contains("refuseReason")
                && worldDiplomacyDirectorSource.Contains(
                    "legacy_mismatched_narration_fails_closed"),
                "Diplomatic narration is selected after the deterministic willingness roll and cannot announce the opposite outcome.");
            add("graceful_shutdown_return_code",
                hostSource.Contains("Utilities.DoDelayedexit(0)")
                && !hostSource.Contains("Utilities.DoDelayedexit(delayMilliseconds)")
                && lifecycleCliSource.Contains("StopGame(args.Concat(new[] { \"--force\" }).ToArray())")
                && lifecycleCliSource.Contains("force_stop_save_sync_not_ready")
                && lifecycleCliSource.Contains("native_save_completed")
                && lifecycleCliSource.Contains("saveFinalizationComplete")
                && lifecycleCliSource.Contains("active_run_not_reconciled"),
                "Graceful BLSE shutdown passes a real return code, and unattended restart has a reconciled force fallback that remains gated on terminal commands plus durable native-save and Save Sync completion even while a stale heartbeat is present.");
            add("save_sync_aware_game_restart_timeout",
                lifecycleCliSource.Contains("IntValue(args, \"--save-sync-grace\", 420)")
                && lifecycleCliSource.Contains("requestedWaitSeconds + saveSyncGraceSeconds")
                && lifecycleCliSource.Contains("Save-Sync-aligned campaign heartbeat"),
                "Unattended /continuesave readiness has a separate bounded Save Sync restoration allowance, so large campaign snapshots cannot cause a false game-load timeout.");
            add("save_sync_load_retry_receipt_idempotency",
                saveCoordinatorSource.Contains("[\"loadSessionId\"] = Guid.NewGuid().ToString(\"N\")")
                && saveCoordinatorSource.Contains("RunLoadHandshakeAsync(generation, loadPayload)")
                && postgreSqlSaveSyncSource.Contains("TryReplaySaveSyncLoadReceipt")
                && postgreSqlSaveSyncSource.Contains("StoreSaveSyncLoadReceipt")
                && postgreSqlSaveSyncSource.Contains("[\"idempotentReplay\"] = true"),
                "One native load generation keeps a stable identity across HTTP retries, and the server durably replays its completed receipt instead of repeating a destructive PostgreSQL rollback after a client timeout.");
            add("live_test_startup_prerequisite_logging_is_null_safe",
                hostSource.Contains("bool playerReady = campaign != null")
                && hostSource.Contains("campaign == null || !playerReady")
                && hostSource.Contains("(playerReady ? \"ready\" : \"missing\")"),
                "The pre-campaign heartbeat diagnostic never dereferences PlayerCharacter while Campaign.Current is absent.");
            add("native_ui_augmentation_calibration_contract",
                lifecycleCliSource.Contains("case \"start-menu\": return StartGameMainMenu(args);")
                && lifecycleCliSource.Contains("Arguments = \"/singleplayer \\\"\" + moduleArgument + \"\\\"\"")
                && lifecycleCliSource.Contains("stableInitialScreenSeconds")
                && nativeUiCalibrationSource.Contains("NativeUiCalibrationTargets")
                && nativeUiCalibrationSource.Contains("CampaignMapConversation.OpenConversation")
                && nativeUiCalibrationSource.Contains("CraftingHelper.OpenCrafting")
                && nativeUiCalibrationSource.Contains("CurrentMenuContext?.OpenRecruitVolunteers()")
                && nativeUiCalibrationSource.Contains("nativeUiCalibrationProviderFree")
                && nativeUiCalibrationSource.Contains("nativeUiCalibrationSavedCampaign"),
                "Every Reign native-prefab augmentation has a bounded production-screen calibration route; InitialScreen uses a visible no-campaign launch, while campaign targets open and close without saving through the live bridge.");
            add("notable_initialization_precondition",
                characterInitializationSource.Contains("NotableBackgroundInitializationComplete =>")
                && liveClientSource.Contains("[\"characterInitialization\"]")
                && liveClientSource.Contains("[\"notableBackgroundReady\"]")
                && qualificationRunnerSource.Contains("notableBackgroundReady")
                && qualificationRunnerSource.Contains("campaign-day-one")
                && serverLiveTestSource.Contains("Campaign day-one character initialization is still pending"),
                "Qualification and the live bridge refuse to start interactions until the gated campaign-day-one background-character pass is complete.");
            add("day_one_initialization_gate",
                !campaignPreparationSource.Contains("_mapScreenStableSeconds < 0.5f")
                && campaignPreparationSource.Contains("ReignCampaignPreparationStartPolicy.CanStart(")
                && campaignPreparationSource.Contains("ReignNotableGenerationPopupManager.RequireUntilReady()")
                && campaignPreparationSource.Contains("RunInitializationRequestAsync")
                && campaignPreparationSource.Contains("EnsureInitialPersonalitiesAsync")
                && campaignPreparationSource.Contains("SynchronizeIdentityNetworkAsync")
                && campaignPreparationSource.Contains("IngestDailyWorldSnapshotAsync")
                && campaignPreparationSource.Contains("StartInitialRelationshipRunAsync")
                && campaignPreparationSource.Contains("FlushAndWait(TimeSpan.FromMinutes(10))")
                && campaignPreparationSource.Contains("AcknowledgeSealAsync")
                && campaignPreparationSource.Contains("TryCreateReleaseToken")
                && campaignPreparationSource.Contains("TrySealAndMarkReady")
                && campaignPreparationSource.Contains("IsCurrentCampaignSealed")
                && !characterInitializationSource.Contains("TrySealAndMarkReady")
                && notablePopupSource.Contains("public static void RequireUntilReady()")
                && notablePopupSource.Contains("_keepOpenRequested")
                && notablePopupSource.Contains("restoring it.")
                && notablePopupSource.Contains("ExecuteRetry")
                && subModuleSource.Contains("\"campaign preparation\"")
                && subModuleSource.Contains("ReignCampaignPreparationCampaignBehavior.Instance?.ApplicationTick(dt)")
                && subModuleSource.Contains("if (ReignCampaignInitializationGate.IsPending)")
                && subModuleSource.Contains("RunTickStage(\"live interaction host\"")
                && subModuleSource.Contains("RunTickStage(\"notable generation popup\"")
                && serverLiveTestSource.Contains("IsInitializationControlRun(payload)")
                && serverLiveTestSource.Contains("\"save_checkpoint\", \"shutdown_game\", \"wait_for_save_sync\"")
                && initializationGateSource.Contains("campaign_day_1_before_reign_systems")
                && initializationGateSource.Contains("InitializationRequestGeneration")
                && initializationGateSource.Contains("/tests/live/game/"),
                "Campaign preparation starts before the map exists, owns one generation-scoped readiness gate, restores a retryable blocker if the map appears early, and releases gameplay only after critical native/server state is quiescent and durably acknowledged.");
            add("day_one_initialization_defers_session_systems",
                campaignPreparationSource.Contains("PrepareNativeFoundations")
                && campaignPreparationSource.Contains("ReignAICampaignBehavior.Instance")
                && campaignPreparationSource.Contains("PrepareInitialBaselines")
                && campaignPreparationSource.Contains("ReignRulerReputationCampaignBehavior.Instance")
                && campaignPreparationSource.Contains("ReignRebellionCampaignBehavior.Instance")
                && campaignPreparationSource.Contains("ReignWorldDiplomacyCampaignBehavior.Instance")
                && campaignPreparationSource.Contains("ReignSocialEventsCampaignBehavior.Instance")
                && campaignPreparationSource.Contains("ReignFamilyCampaignBehavior.Instance")
                && campaignPreparationSource.Contains("ReignCourtPersonalityReputationCampaignBehavior.Instance")
                && aiBehaviorSource.Contains("internal void PrepareInitialState()")
                && relationshipBehaviorSource.Contains("internal void PrepareInitialBaselines()")
                && rebellionBehaviorSource.Contains("internal void PrepareInitialState()")
                && rulerBehaviorSource.Contains("internal void PrepareInitialState()")
                && eventBehaviorSource.Contains("internal void PrepareInitialState()")
                && familyBehaviorSource.Contains("internal void PrepareInitialState()"),
                "Calendar, relationship, family, rebellion, reputation, social-event, court, and diplomacy foundations are owned by the blocking preparation coordinator instead of racing as deferred post-release session work.");
            add("ruler_reputation_sparse_weekly_windows",
                rulerBehaviorSource.Split(new[] { "List<IGrouping<string, JObject>> seven = groups.SelectMany" }, StringSplitOptions.None).Length == 3
                && rulerBehaviorSource.Split(new[] { "if (seven.Count == 0) return;" }, StringSplitOptions.None).Length >= 3
                && !rulerBehaviorSource.Contains(".GroupBy(x => g.Key).First()"),
                "Weekly loyalty and security reputation evaluation safely skips kingdoms whose fourteen-day settlement history has no observations in the latest seven-day window.");
            add("debug_know_everyone_native_and_reign_parity",
                debugActionsSource.Contains("hero.IsKnownToPlayer = true;")
                && debugActionsSource.Contains("MakePlayerKnowEveryoneAsync(livingHeroes.Count, nativeKnown)")
                && debugActionsSource.Contains("ReignMainThread.InvokeAsync")
                && correspondenceSource.Contains("hero.IsKnownToPlayer")
                && identitySystemSource.Contains("[\"complete\"] = created + upgraded + unchanged == heroes.Count"),
                "Know Everyone synchronizes Bannerlord's native player-knowledge flag and Reign's directional identity ledger, verifies both counts, and reports completion on the game thread.");
            add("live_checkpoint_native_phase_and_finalize_evidence",
                hostSource.Contains("AutomationSaveQuietPeriod = TimeSpan.FromSeconds(20)")
                && hostSource.Contains("AutomationSaveCompletionReserve = TimeSpan.FromSeconds(75)")
                && hostSource.Contains("FlushAndWaitThroughSequence(")
                && hostSource.Contains("history_preflight_timeout")
                && hostSource.Contains("native_start_timeout")
                && hostSource.Contains("save_sync_finalize_failed")
                && hostSource.Contains("SaveDiagnostics(saveName")
                && hostSource.Contains("TryAcknowledgeForLiveHarness(")
                && hostSource.Contains("acknowledgedDiplomacyAnnouncement")
                && hostSource.Contains("acknowledgedDiplomacyEventId")
                && saveBehaviorSource.Contains("NativeSaveObservation()")
                && saveBehaviorSource.Contains("registering_save_sync_snapshot")
                && saveBehaviorSource.Contains("serializing_native_save")
                && saveCoordinatorSource.Contains("SaveFinalizationSnapshot()")
                && saveCoordinatorSource.Contains("_saveFinalizationsInFlight")
                && saveCoordinatorSource.Contains("SetAutomationSaveNotificationSuppressed")
                && saveCoordinatorSource.Contains("recorded without opening a modal during an automated checkpoint")
                && hostSource.Contains("SetAutomationSaveNotificationSuppressed(true)")
                && hostSource.Contains("SetAutomationSaveNotificationSuppressed(false)")
                && liveClientSource.Contains("[\"nativeSave\"] = BuildNativeSaveDiagnostics()")
                && liveClientSource.Contains("[\"mainThreadDispatch\"]")
                && mainThreadSource.Contains("public static int PendingCount")
                && mainThreadSource.Contains("public static DateTime LastDrainUtc"),
                "Checkpoint automation closes and acknowledges any Reign diplomacy card that would block SaveAs, spaces consecutive saves, drains World History before starting the native save, proves the native start/completion callback and Save Sync finalization separately, and reports native phase plus main-thread dispatch liveness on every heartbeat and failure.");
            add("live_checkpoint_waits_for_paused_diplomacy",
                diplomacyBehaviorSource.Contains("TaleWorlds.CampaignSystem.Campaign.Current?.TimeControlMode == CampaignTimeControlMode.Stop")
                && diplomacyBehaviorSource.Contains("public bool HasInFlightRequest")
                && liveClientSource.Contains("[\"diplomacy\"] = BuildDiplomacyDiagnostics()")
                && hostSource.Contains("campaign.TimeControlMode = CampaignTimeControlMode.Stop")
                && hostSource.Contains("diplomacy_preflight_timeout")
                && hostSource.Contains("HasInFlightRequest == true")
                && passiveWorldControlSource.Contains("ReadObject(nativeRuntime, \"diplomacy\")")
                && passiveWorldControlSource.Contains("!ReadBoolean(diplomacy, \"inFlight\")"),
                "Passive-world catch-up and checkpoint saving pause native time, expose every in-flight diplomacy request, and wait for those requests to drain before starting Bannerlord's native save.");
            add("passive_world_checkpoint_requires_all_native_queues",
                liveClientSource.Contains("[\"worldHistory\"] = new JObject")
                && liveClientSource.Contains("[\"pendingCount\"] = ReignWorldHistoryTransport.PendingCount()")
                && passiveWorldControlSource.Contains("ReadObject(relationships, \"nativeSync\")")
                && passiveWorldControlSource.Contains("ReadLong(nativeSync, \"pending\", -1) == 0")
                && passiveWorldControlSource.Contains("ReadLong(nativeSync, \"overdue\", -1) == 0")
                && passiveWorldControlSource.Contains("ReadLong(worldHistory, \"pendingCount\", -1) == 0")
                && passiveWorldControlSource.Contains("!ReadBoolean(worldHistory, \"uploadInFlight\")")
                && passiveWorldControlSource.Contains("RequiredCompletedPassiveWorldDay(targetDay)")
                && passiveWorldControlSource.Contains("Math.Floor(targetDay + CampaignDayBoundaryEpsilon) - 1d")
                && lifecycleCliSource.Contains("string.Equals(status, \"failed\", StringComparison.OrdinalIgnoreCase)"),
                "Passive-world catch-up fails closed on missing relationship telemetry, waits for native projections and World History upload to drain, requires daily clocks only through the latest fully completed campaign day, and never treats a failed terminal run as successful.");
            add("passive_world_checkpoint_drains_clan_conflict_notices",
                passiveWorldControlSource.Contains("ReadObject(latest, \"clanConflicts\")")
                && passiveWorldControlSource.Contains("clanConflicts, \"pendingNotices\", -1")
                && passiveWorldControlSource.Contains("clanConflicts, \"relationshipEffectsPending\", -1")
                && passiveWorldControlSource.Contains("clanConflicts, \"relationshipEffectsFailed\", -1")
                && passiveWorldControlSource.Contains("clanConflicts, \"incidentFailures\", -1")
                && passiveWorldControlSource.Contains("nativeEffectsPending > 0 || pendingClanConflictNotices > 0")
                && passiveWorldControlSource.Contains("pendingClanConflictNotices == 0"),
                "Passive-world catch-up invokes the shared pressure/notice drain for final-boundary clan-conflict notices, fails closed on clan-conflict failures, and requires effects plus notices to remain drained before saving.");
            add("passive_world_save_receipt_projects_completed_command_result",
                passiveWorldControlSource.Contains("LastCompletedCommandResult(value)")
                && passiveWorldControlSource.Contains("return CompactPassiveWorldStatus(runtime, campaignId, timelineId);")
                && passiveWorldControlSource.Contains("WaitForPassiveWorldPostSaveRuntime(campaignId, saveName)")
                && passiveWorldControlSource.Contains("PassiveWorldPostSaveRuntimeQuiesced(finalRuntime, saveName)")
                && passiveWorldControlSource.Contains("DateTime.UtcNow.AddSeconds(45)")
                && passiveWorldControlSource.Contains("TimeSpan.FromSeconds(5)")
                && passiveWorldControlSource.Contains("ReadLong(relationshipClient, \"clientOutboxDays\", -1) == 0")
                && passiveWorldControlSource.Contains("!ReadBoolean(relationshipClient, \"uploadInFlight\")")
                && passiveWorldControlSource.Contains("ReadLong(worldHistory, \"pendingCount\", -1) == 0")
                && liveClientSource.Contains("[\"relationshipClient\"] = BuildRelationshipOutboxDiagnostics()")
                && passiveWorldControlSource.Contains("String(result, \"saveName\")")
                && passiveWorldControlSource.Contains("ReadDoubleValue(result, \"worldDay\", -1d)")
                && passiveWorldControlSource.Contains("ReadObject(result, \"nativeSave\")")
                && passiveWorldControlSource.Contains("ReadObject(result, \"saveFinalization\")")
                && passiveWorldControlSource.Contains("CompactPassiveWorldSave(save, finalRuntime)"),
                "Passive-world status and save receipts stay bounded. Save receipts project the completed save command's nested result and require five stable seconds of current post-save runtime telemetry with Save Sync, diplomacy, relationship input, World History, and native saving all idle, so a successful rolling checkpoint cannot report an empty save name, invalid day, offline placeholder runtime, stale in-flight save state, or a late final-day relationship outbox capture.");
            add("save_sync_background_writer_barrier_and_bounded_manifest",
                continuousRelationshipSource.Contains("TryProcessNextRelationshipDayUnderCampaignGate()")
                && continuousRelationshipSource.Contains("CampaignDataGate.EnterReadLock()")
                && continuousRelationshipSource.Contains("CampaignDataGate.ExitReadLock()")
                && saveSyncSource.Contains("return SaveSyncLoadPostgreSql(payload, dryRun)")
                && postgreSqlSaveSyncSource.Contains("phase = \"move_active_campaign\"")
                && postgreSqlSaveSyncSource.Contains("phase = \"install_target_campaign\"")
                && postgreSqlSaveSyncSource.Contains("[\"failedPhase\"] = phase")
                && campaignBackupsSource.Contains("Parallel.For(")
                && campaignBackupsSource.Contains("MaxDegreeOfParallelism = Math.Max(1, Math.Min(8, Environment.ProcessorCount))"),
                "The continuous relationship writer participates in Save Sync's global campaign barrier, failures retain their exact swap phase and stack, and exact manifest hashing uses bounded parallelism for large character stores.");
            add("diplomacy_feedback_avoids_file_relationship_lock_inversion",
                worldDiplomacyDirectorSource.Contains(
                    "List<Dictionary<string, object>> pendingRelationshipFeedback")
                && worldDiplomacyDirectorSource.Contains(
                    "List<Dictionary<string, object>> pendingRulerActivity")
                && worldDiplomacyDirectorSource.Contains(
                    "foreach (Dictionary<string, object> item in pendingRelationshipFeedback)")
                && worldDiplomacyDirectorSource.Contains(
                    "foreach (Dictionary<string, object> item in pendingRulerActivity)")
                && worldDiplomacyDirectorSource.Contains(
                    "MarkDiplomaticRelationshipFeedbackRecorded(campaignId, actionId")
                && kingdomLeaderDiplomacySource.IndexOf(
                    "lock (CampaignRelationshipWriteLock(campaignId))",
                    StringComparison.Ordinal)
                    < kingdomLeaderDiplomacySource.IndexOf(
                        "SELECT COUNT(*) AS count FROM ruler_diplomatic_incidents",
                        StringComparison.Ordinal),
                "Diplomacy action receipts release FileLock before database activity or relationship feedback, then durably mark feedback after its idempotent incident application.");
            add("notable_initialization_bulk_io",
                notableMbtiSource.Contains("connection.BeginTransaction()")
                && notableMbtiSource.Contains("UpdateCharacterIndexBatch(campaignId, indexProfiles)")
                && notableMbtiSource.Contains("notable_mbti.initialize.performance")
                && notableMbtiSource.Contains("notable_mbti.confirm.performance")
                && serverProgramSource.Contains("private static void UpdateCharacterIndexBatch"),
                "Large profile and confirmation sets use transactional database writes, one character-index rewrite, and phase-level timing evidence.");
            add("relationship_history_qualification_uses_dependent_followups",
                qualificationRunnerSource.Contains("\"cold_initial\"")
                && qualificationRunnerSource.Contains("\"cold_followup\"")
                && qualificationRunnerSource.Contains("\"transition_initial\"")
                && qualificationRunnerSource.Contains("\"transition_followup\"")
                && qualificationRunnerSource.Contains(
                    "If the same choice faced you tomorrow")
                && qualificationRunnerSource.Contains(
                    "what should the two of you do next"),
                "Shared Relationship History qualification uses distinct dependent follow-ups instead of repeatedly demanding the same closed conversation.");
            add("relationship_history_qualification_stabilizes_reuse_fixture",
                qualificationRunnerSource.Contains("[\"affinityAtoB\"] = 1")
                && qualificationRunnerSource.Contains("[\"affinityBtoA\"] = -1")
                && qualificationRunnerSource.Contains(
                    "was not stabilized at the requested neutral affinities")
                && !qualificationRunnerSource.Contains(
                    "[\"preserveIfExists\"] = true"),
                "The cold/reuse matrix forces every selected pair into a stable neutral band and verifies the applied values, so pre-existing campaign chemistry cannot turn the reuse probe into an accidental transition.");
            add("qualification_scenario_start_is_idempotent_and_fail_stop",
                qualificationRunnerSource.Contains(
                    "QualificationScenarioRunId(")
                && qualificationRunnerSource.Contains(
                    "StartQualificationScenarioRunWithReconciliation(")
                && qualificationRunnerSource.Contains(
                    "return \"live-q-\" + hex.Substring(0, 20)")
                && qualificationRunnerSource.Contains(
                    "Qualification scenario did not start after bounded")
                && qualificationRunnerSource.Contains(
                    "[\"boundedAttempts\"] = 30")
                && serverLiveTestSource.Contains(
                    "LoadLiveTestRun(campaignId, requestedRunId)")
                && serverLiveTestSource.Contains(
                    "[\"idempotentStart\"] = true")
                && serverLiveTestSource.Contains(
                    "already exists with a different"),
                "Every qualification scenario has a stable run id, reconciles ambiguous or transient start responses without duplication, and stops instead of silently omitting a required sample.");
            add("qualification_heartbeat_has_bounded_transient_grace",
                qualificationRunnerSource.Contains(
                    "QualificationRuntimeWithHeartbeatGrace")
                && qualificationRunnerSource.Contains(
                    "DateTimeOffset.UtcNow.AddMinutes(5)")
                && qualificationRunnerSource.Contains("Thread.Sleep(2000)")
                && qualificationRunnerSource.Contains(
                    "rejected a stale campaign heartbeat")
                && qualificationRunnerSource.Contains(
                    "bounded five-minute recovery window")
                && qualificationRunnerSource.Contains(
                    "qualification checks before its bounded five-minute deadline")
                && serverLiveTestSource.Contains(
                    "LiveTestHeartbeatFreshSeconds = 90"),
                "The unattended runner tolerates a bounded five-minute native-host recovery window while still rejecting stale campaigns and sustained outages.");
            add("qualification_evaluation_timeout_and_reconciliation",
                lifecycleCliSource.Contains(
                    "ReadinessEvaluationTimeoutSeconds = 900")
                && lifecycleCliSource.Contains(
                    "PostWithTimeout(")
                && qualificationRunnerSource.Contains(
                    "EvaluateQualificationRunWithReconciliation")
                && qualificationRunnerSource.Contains(
                    "QualificationRunEvaluationComplete")
                && qualificationRunnerSource.Contains(
                    "evaluationCompletedAfterAmbiguousTransport")
                && qualificationRunnerSource.Contains(
                    "The completed dialogue run was not replayed"),
                "Blinded rubric evaluation has an accuracy-preserving long timeout and reconciles durable correlation evidence after ambiguous transport without resubmitting dialogue.");
            add("qualification_resume_uses_completed_evidence_cursor",
                qualificationRunnerSource.Contains(
                    "QualificationRunsToSkip = freshQualification")
                && qualificationRunnerSource.Contains(
                    "ReadLong(scorecard, \"completedRunCount\", 0)")
                && qualificationRunnerSource.Contains(
                    "[\"resumedSkip\"] = true")
                && qualificationRunnerSource.Contains(
                    "QualificationReloadsToSkip")
                && qualificationRunnerSource.Contains(
                    "QualificationRollbacksToSkip"),
                "A restarted qualification consumes its same-build completed-run, checkpoint-reload, and rollback cursors instead of duplicating already-qualified dialogue or lifecycle work.");
            add("qualification_resume_persists_relationship_pair_roster",
                qualificationRunnerSource.Contains(
                    "ReadRosterPairs(")
                && qualificationRunnerSource.Contains(
                    "\"relationshipHistoryPairs\"")
                && qualificationRunnerSource.Contains(
                    "Could not persist the immutable relationship-history"),
                "Shared Relationship History qualification persists its exact pair roster, so usage-aware selection cannot reorder a resumed same-build scenario matrix.");
            add("qualification_target_discovery_uses_heartbeat_grace",
                qualificationRunnerSource.Contains(
                    "QualificationRuntimeWithHeartbeatGrace(campaignId)")
                && qualificationRunnerSource.Contains(
                    "Qualification target discovery failed for ")
                && qualificationRunnerSource.Contains(
                    "for (int attempt = 1; attempt <= 3; attempt++)"),
                "Read-only target discovery waits through a complete backgrounded-game heartbeat interval and retries only a rejected stale-heartbeat lease.");
            add("zero_sum_native_relationship_acknowledgement",
                correspondenceSource.Contains("if (subject != null && target != null && subject != target)")
                && !correspondenceSource.Contains("subject != target && delta != 0")
                && socialEventClientSource.Contains("if (subject != null && target != null)")
                && !socialEventClientSource.Contains("subject != null && target != null && delta != 0")
                && correspondenceSource.Contains("/relationships/conversation/native-receipt")
                && socialEventClientSource.Contains("/relationships/conversation/native-receipt"),
                "Opposite directional judgments that net to zero still acknowledge the atomic native hero-pair receipt without applying a redundant native relation change.");
            add("native_relationship_acknowledgement_captures_before_and_after",
                correspondenceSource.Contains("int priorNativeRelation = subject.GetRelation(target);")
                && correspondenceSource.Contains("[\"priorNativeRelation\"] = priorNativeRelation")
                && correspondenceSource.Contains("[\"nativeRelation\"] = subject.GetRelation(target)")
                && socialEventClientSource.Contains("int priorNativeRelation = subject.GetRelation(target);")
                && socialEventClientSource.Contains("[\"priorNativeRelation\"] = priorNativeRelation")
                && socialEventClientSource.Contains("[\"nativeRelation\"] = subject.GetRelation(target)"),
                "Every production conversation path acknowledges the authoritative native relationship before and after application, so a stale request snapshot cannot distort the applied turn delta.");
            return results;
        }

        private static string NormalizeLiveTestMode(string value) { return (value ?? "").Trim().ToLowerInvariant().Replace('-', '_'); }
        private static string NormalizeLiveTestPresentation(string value) { return string.Equals(value, "visible", StringComparison.OrdinalIgnoreCase) ? "visible" : "headless"; }
        private static string NormalizeLiveTestEffects(string value) { return string.Equals(value, "full", StringComparison.OrdinalIgnoreCase) ? "full" : "guarded"; }
        private static Dictionary<string, object> LiveTestError(string error) { return new Dictionary<string, object> { ["ok"] = false, ["error"] = error ?? "Live interaction request failed." }; }

        private static bool IsFatalLiveTestInfrastructureFailure(string error)
        {
            string normalized = (error ?? "").ToLowerInvariant();
            return normalized.Contains("insufficient_balance")
                || normalized.Contains("insufficient balance")
                || normalized.Contains("account quota is exhausted")
                || normalized.Contains("quota exhausted")
                || normalized.Contains("billing hard limit")
                || normalized.Contains("payment required")
                || normalized.Contains("status 402")
                || normalized.Contains("http 402")
                || normalized.Contains("status 401")
                || normalized.Contains("http 401")
                || normalized.Contains("unauthorized api key")
                || normalized.Contains("invalid api key");
        }

        private static bool IsLiveTestArmed(Dictionary<string, object> arm) { return ParseLiveTestUtc(ReadString(arm, "expiresUtc", "")) > DateTimeOffset.UtcNow; }
        private static bool ShouldInterruptLiveTestRunForInstance(Dictionary<string, object> run, string gameInstanceId)
        {
            if (run == null || string.IsNullOrWhiteSpace(gameInstanceId)
                || string.Equals(ReadString(run, "status", ""), "paused", StringComparison.OrdinalIgnoreCase))
                return false;
            string activeInstance = ReadString(run, "gameInstanceId", "");
            return !string.IsNullOrWhiteSpace(activeInstance)
                && !string.Equals(activeInstance, gameInstanceId, StringComparison.OrdinalIgnoreCase);
        }
        private static bool IsFreshLiveTestRuntime(Dictionary<string, object> runtime) { return runtime != null && runtime.Count > 0 && ParseLiveTestUtc(ReadString(runtime, "receivedUtc", "")) >= DateTimeOffset.UtcNow.AddSeconds(-LiveTestHeartbeatFreshSeconds); }
        private static DateTimeOffset ParseLiveTestUtc(string value) { return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed) ? parsed.ToUniversalTime() : DateTimeOffset.MinValue; }
        private static string LiveTestRoot() { return Path.Combine(TestsDir, "live-interaction"); }
        private static string LiveTestArmPath() { return Path.Combine(LiveTestRoot(), "arm.json"); }
        private static string LiveTestLatestRuntimePath() { return Path.Combine(LiveTestRoot(), "runtime-latest.json"); }
        private static string LiveTestRuntimePath(string campaignId) { return Path.Combine(LiveTestRoot(), "runtime", SafePathSegment(campaignId, "unknown") + ".json"); }
        private static string LiveTestRunsRoot(string campaignId) { return CampaignFile(campaignId, "tests", "live-interaction", "runs"); }
        private static string LiveTestActiveRunPath(string campaignId) { return CampaignFile(campaignId, "tests", "live-interaction", "active-run.json"); }
        private static string LiveTestRunPath(string campaignId, string runId) { return Path.Combine(LiveTestRunsRoot(campaignId), SafePathSegment(runId, "run") + ".json"); }
        private static Dictionary<string, object> LoadLiveTestRuntime(string campaignId) { return string.IsNullOrWhiteSpace(campaignId) ? ReadJsonObject(LiveTestLatestRuntimePath()) : ReadJsonObject(LiveTestRuntimePath(campaignId)); }
        private static Dictionary<string, object> LoadLiveTestRun(string campaignId, string runId) { return string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(runId) ? new Dictionary<string, object>() : ReadJsonObject(LiveTestRunPath(campaignId, runId)); }
        private static void SaveLiveTestRun(Dictionary<string, object> run)
        {
            string campaignId = ReadString(run, "campaignId", "");
            string runId = ReadString(run, "runId", "");
            WriteJsonObject(LiveTestRunPath(campaignId, runId), run);
            string indexPath = LiveTestActiveRunPath(campaignId);
            if (!LiveTestTerminalRunStates.Contains(ReadString(run, "status", "")))
            {
                WriteJsonObject(indexPath, new Dictionary<string, object>
                {
                    ["runId"] = runId,
                    ["status"] = ReadString(run, "status", ""),
                    ["updatedUtc"] = ReadString(run, "updatedUtc", "")
                });
                return;
            }
            Dictionary<string, object> indexed = ReadJsonObject(indexPath);
            if (ReadString(indexed, "runId", "").Equals(
                    runId, StringComparison.OrdinalIgnoreCase))
            {
                WriteIdleLiveTestRunIndex(indexPath);
            }
        }

        private static Dictionary<string, object> FindLatestLiveTestRun(string campaignId)
        {
            string root = LiveTestRunsRoot(campaignId);
            if (!Directory.Exists(root)) return null;
            string path = Directory.GetFiles(root, "*.json", SearchOption.TopDirectoryOnly).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            return string.IsNullOrWhiteSpace(path) ? null : ReadJsonObject(path);
        }

        private static Dictionary<string, object> FindActiveLiveTestRun(string campaignId)
        {
            string indexPath = LiveTestActiveRunPath(campaignId);
            Dictionary<string, object> indexed = ReadJsonObject(indexPath);
            string indexedRunId = ReadString(indexed, "runId", "");
            if (string.IsNullOrWhiteSpace(indexedRunId)
                && ReadBool(indexed, "scanComplete", false))
                return null;
            if (!string.IsNullOrWhiteSpace(indexedRunId))
            {
                Dictionary<string, object> indexedRun =
                    LoadLiveTestRun(campaignId, indexedRunId);
                if (indexedRun.Count > 0
                    && !LiveTestTerminalRunStates.Contains(
                        ReadString(indexedRun, "status", "")))
                    return indexedRun;
                WriteIdleLiveTestRunIndex(indexPath);
                return null;
            }

            // One backward-compatibility scan upgrades campaigns created before
            // the active-run index existed. Normal heartbeats never deserialize
            // the historical report corpus.
            string root = LiveTestRunsRoot(campaignId);
            if (!Directory.Exists(root))
            {
                WriteIdleLiveTestRunIndex(indexPath);
                return null;
            }
            foreach (string path in Directory.GetFiles(root, "*.json", SearchOption.TopDirectoryOnly).OrderByDescending(File.GetLastWriteTimeUtc))
            {
                Dictionary<string, object> run = ReadJsonObject(path);
                if (!LiveTestTerminalRunStates.Contains(ReadString(run, "status", "")))
                {
                    WriteJsonObject(indexPath, new Dictionary<string, object>
                    {
                        ["runId"] = ReadString(run, "runId", ""),
                        ["status"] = ReadString(run, "status", ""),
                        ["updatedUtc"] = ReadString(run, "updatedUtc", "")
                    });
                    return run;
                }
            }
            WriteIdleLiveTestRunIndex(indexPath);
            return null;
        }

        private static void WriteIdleLiveTestRunIndex(string indexPath)
        {
            WriteJsonObject(indexPath, new Dictionary<string, object>
            {
                ["runId"] = string.Empty,
                ["status"] = "idle",
                ["scanComplete"] = true,
                ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o")
            });
        }
    }
}
