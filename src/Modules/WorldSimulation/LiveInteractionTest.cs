using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationService
    {
        private const int LiveTestSchemaVersion = 2;
        private const int LiveTestDefaultArmMinutes = 30;
        // Backgrounded Bannerlord and large campaign-census responses can
        // legitimately stretch the native polling interval. Keep liveness
        // aligned with the controller's existing 90-second crash boundary so
        // a recently received heartbeat is not rejected between native ticks.
        internal const int LiveTestHeartbeatFreshSeconds = 90;
        internal const int LiveTestLeaseSeconds = 45;
        private static readonly object LiveTestLock = new object();
        private static readonly HashSet<string> LiveTestModes = new HashSet<string>(
            new[] { "individual_chat", "party_chat", "social_event", "wilderness_event", "social_balance", "passive_world", "correspondence", "court_event", "spymaster", "ambassador_official", "kingdom_event", "campaign_command", "government", "party_agency", "structured_actions", "npc_lives", "personal_gossip" },
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
                "action_probe_wait", "structured_action_test", "npc_lives_test", "personal_gossip_test", "conversation_clock_probe",
                "gauntlet_advance_time", "gauntlet_arrive", "gauntlet_depart",
                "gauntlet_save", "gauntlet_reload", "gauntlet_fault",
                "ui_open", "ui_status", "ui_snapshot", "ui_action", "ui_back", "ui_close",
                "spymaster_test", "spymaster_organic", "arrest_test", "rebellion_test", "capital_ambassador_test", "royal_council_test", "ruler_docket_test", "kingdom_event_test", "government_test", "clan_accords_test", "campaign_command_test", "party_agency_test",
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
                "world_economy_test", "world_snapshot", "world_time_control", "world_advance", "wait_for_correspondence", "world_delete_checkpoint",
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

        internal static Dictionary<string, object> LiveTestArmApi(Dictionary<string, object> payload)
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
            lock (LiveTestLock) Program.WriteJsonObject(LiveTestArmPath(), arm);
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["armed"] = true, ["armId"] = arm["armId"], ["expiresUtc"] = arm["expiresUtc"]
            };
        }

        internal static Dictionary<string, object> LiveTestRuntimeApi(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string campaignId = query.TryGetValue("campaignId", out string requested) ? requested : "";
            Dictionary<string, object> runtime = LoadLiveTestRuntime(campaignId);
            Dictionary<string, object> arm = Program.ReadJsonObject(LiveTestArmPath());
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

        internal static Dictionary<string, object> LiveTestHeartbeatApi(Dictionary<string, object> payload)
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
                    Dictionary<string, object> currentArm = Program.ReadJsonObject(LiveTestArmPath());
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
                Program.WriteJsonObject(LiveTestRuntimePath(campaignId), payload);
                Program.WriteJsonObject(LiveTestLatestRuntimePath(), payload);
                Dictionary<string, object> arm = Program.ReadJsonObject(LiveTestArmPath());
                if (DialogueService.TryRenewPersonalGossipConversationLease(active, payload, DateTimeOffset.UtcNow, IsLiveTestArmed(arm)))
                    SaveLiveTestRun(active);
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

        internal static Dictionary<string, object> LiveTestTargetSearchApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            string mode = NormalizeLiveTestMode(ReadString(payload, "mode", "individual_chat"));
            if (!LiveTestModes.Contains(mode)) return LiveTestError("Unsupported live-test mode.");
            if (mode == "structured_actions" && !StructuredActionEnrollmentValid(ReadDictionary(payload, "enrollment"), campaignId))
                return LiveTestError("Structured action tests require a verified campaign-test enrollment with separate Current and protected baseline identities.");
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

        internal static Dictionary<string, object> LiveTestRunStartApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (payload.ContainsKey("schemaVersion") && !new[] { 1, LiveTestSchemaVersion }.Contains(ReadInt(payload, "schemaVersion", LiveTestSchemaVersion)))
                return LiveTestError("Unsupported live-test schemaVersion. Expected 1 or " + LiveTestSchemaVersion.ToString(CultureInfo.InvariantCulture) + ".");
            string campaignId = ReadString(payload, "campaignId", "");
            if (string.IsNullOrWhiteSpace(campaignId)) return LiveTestError("campaignId is required.");
            if (!IsLiveTestArmed(Program.ReadJsonObject(LiveTestArmPath()))) return LiveTestError("The live-test bridge is not armed or its arm window expired.");
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
                && !ReputationService.ValidateSocialBalanceLiveEnrollment(payload, campaignId, out string enrollmentError))
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
                    ["buildVersion"] = DialogueService.CurrentConversationBuildVersion(),
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
                if (ReadDictionaryList(payload, "steps").Any(step =>
                        string.Equals(ReadString(step, "operation", ReadString(step, "op", "")).Trim(),
                            "world_economy_test", StringComparison.OrdinalIgnoreCase))
                    && ReadDictionaryList(run, "commands").Count != ReadDictionaryList(payload, "steps").Count)
                    return LiveTestError("Invalid war economy scenario command; no run was created.");
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

        internal static Dictionary<string, object> LiveTestCommandEnqueueApi(Dictionary<string, object> payload)
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

        internal static Dictionary<string, object> LiveTestRunPauseApi(Dictionary<string, object> payload, bool resume)
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

        internal static Dictionary<string, object> LiveTestRunCancelApi(Dictionary<string, object> payload)
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

        internal static Dictionary<string, object> LiveTestRunStatusApi(Dictionary<string, string> query, bool includeEvidence)
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

        internal static Dictionary<string, object> LiveTestGamePollApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            string instanceId = ReadString(payload, "gameInstanceId", "");
            if (!IsLiveTestArmed(Program.ReadJsonObject(LiveTestArmPath()))) return new Dictionary<string, object> { ["ok"] = true, ["found"] = false, ["armed"] = false };
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
                command["leaseStartedUtc"] = now.ToString("o");
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

        internal static Dictionary<string, object> FirstLiveTestCommandInSequence(
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

        internal static Dictionary<string, object> LiveTestGameAckApi(Dictionary<string, object> payload)
        {
            return UpdateLiveTestCommandFromGame(payload, false);
        }

        internal static Dictionary<string, object> LiveTestGameResultApi(Dictionary<string, object> payload)
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
                        ? new Dictionary<string, object> { ["fatalInfrastructureFailure"] = true, ["error"] = DialogueService.LimitText(reportedError, 2000) }
                        : ReadDictionary(payload, "result") ?? new Dictionary<string, object>();
                    command["error"] = fatalInfrastructureFailure ? DialogueService.LimitText(reportedError, 2000) : ReadString(payload, "error", "");
                    command["correlationIds"] = ReadStringList(payload, "correlationIds");
                    if (fatalInfrastructureFailure)
                    {
                        command["pauseReason"] = "fatal_infrastructure_failure";
                        command["lastFailureUtc"] = DateTimeOffset.UtcNow.ToString("o");
                        List<Dictionary<string, object>> infrastructureFailures = ReadDictionaryList(run, "infrastructureFailures");
                        infrastructureFailures.Add(new Dictionary<string, object>
                        {
                            ["commandId"] = commandId, ["error"] = DialogueService.LimitText(reportedError, 2000),
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
                    ReputationService.RecordSocialBalanceProfileResultIfApplicable(run, command);
                    CompleteLiveTestRunIfAppropriate(run, command);
                }
                SaveLiveTestRun(run);
                return new Dictionary<string, object> { ["ok"] = true, ["command"] = command, ["run"] = LiveTestRunSummary(run, false) };
            }
        }
    }
}
