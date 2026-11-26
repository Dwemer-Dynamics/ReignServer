using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        private static readonly string LiveTestServerProcessInstanceId = Guid.NewGuid().ToString("N");
        private const int LiveTestExecutionObservationVersion = 1;
        private const int LiveTestRetainedObservationLeases = 16;
        private static readonly string[] LiveTestObservedClientModules =
        {
            "ReignBeta", "TaleWorlds.CampaignSystem", "TaleWorlds.Core", "TaleWorlds.MountAndBlade",
            "TaleWorlds.CampaignSystem.ViewModelCollection"
        };
        private static readonly HashSet<string> LiveTestModes = new HashSet<string>(
            new[] { "individual_chat", "party_chat", "social_event", "wilderness_event", "social_balance", "passive_world", "correspondence", "court_event", "spymaster", "ambassador_official", "kingdom_event", "campaign_command", "government", "party_agency", "structured_actions", "npc_lives", "personal_gossip", NotableStartupObservationMode },
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
                "world_economy_test", "world_snapshot", NotableStartupObservationMode, "world_time_control", "world_advance", "wait_for_correspondence", "world_delete_checkpoint",
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
            lock (LiveTestLock) CampaignFileService.WriteJsonObject(LiveTestArmPath(), arm);
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
            Dictionary<string, object> arm = CampaignFileService.ReadJsonObject(LiveTestArmPath());
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
                    Dictionary<string, object> currentArm = CampaignFileService.ReadJsonObject(LiveTestArmPath());
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
                if (active != null && ExpirePaidCourtLease(active, DateTimeOffset.UtcNow)) active = null;
                string activeInstance = ReadString(active, "gameInstanceId", "");
                var paid = active == null ? null : GetPaidCourtCommand(active);
                string paidProcess = ReadString(paid, "paidCourtLeasedProcessId", "");
                bool paidOwnerReplaced = paid != null && (activeInstance != gameInstanceId
                    || (paidProcess.Length > 0 && paidProcess != ReadString(ReadLiveTestExecutionIdentity(payload), "clientProcessInstanceId", "")));
                if (paidOwnerReplaced && PaidCourtNativeDispatchAdmitted(paid))
                {
                    var currentArm = CampaignFileService.ReadJsonObject(LiveTestArmPath());
                    return TestDict("ok", false, "error", "The admitted paid Court owner retains the original lease until result or expiry.",
                        "paidCourtDispatchHeld", true, "activeRunId", ReadString(active, "runId", ""),
                        "authoritativeGameInstanceId", activeInstance, "armed", IsLiveTestArmed(currentArm),
                        "armExpiresUtc", ReadString(currentArm, "expiresUtc", ""));
                }
                if (paidOwnerReplaced || ShouldInterruptLiveTestRunForInstance(active, gameInstanceId))
                {
                    InterruptLiveTestRun(active, paidOwnerReplaced
                        ? "The paid Court owner changed before dispatch. A new paid Court run is required."
                        : "The run belonged to game instance " + activeInstance
                            + ", but the loaded campaign is now " + gameInstanceId + ".");
                }

                payload["schemaVersion"] = LiveTestSchemaVersion;
                payload["receivedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                CampaignFileService.WriteJsonObject(LiveTestRuntimePath(campaignId), payload);
                CampaignFileService.WriteJsonObject(LiveTestLatestRuntimePath(), payload);
                Dictionary<string, object> arm = CampaignFileService.ReadJsonObject(LiveTestArmPath());
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
            if (!IsLiveTestArmed(CampaignFileService.ReadJsonObject(LiveTestArmPath()))) return LiveTestError("The live-test bridge is not armed or its arm window expired.");
            Dictionary<string, object> runtime = LoadLiveTestRuntime(campaignId);
            if (!IsFreshLiveTestRuntime(runtime)) return LiveTestError("No fresh loaded-game heartbeat exists for this campaign.");
            if (ReadBool(ReadDictionary(runtime, "saveSync"), "alignmentPending", false)) return LiveTestError("Save Sync alignment is still pending.");
            string mode = NormalizeLiveTestMode(ReadString(payload, "mode", "individual_chat"));
            bool notableObservation = mode == NotableStartupObservationMode;
            if (notableObservation && !NotableStartupObservationPlanAllowed(payload, true))
                return LiveTestError("Notable startup observation requires a single owned, read-only command.");
            Dictionary<string, object> characterInitialization =
                ReadDictionary(runtime, "characterInitialization");
            if (!notableObservation && !ReadBool(characterInitialization, "notableBackgroundReady", false)
                && !IsInitializationControlRun(payload))
                return LiveTestError(
                    "Campaign day-one character initialization is still pending; "
                    + "live interactions cannot start until every profile is confirmed.");

            string requestedInstance = ReadString(payload, "gameInstanceId", "");
            string liveInstance = ReadString(runtime, "gameInstanceId", "");
            if (!string.IsNullOrWhiteSpace(requestedInstance) && !string.Equals(requestedInstance, liveInstance, StringComparison.OrdinalIgnoreCase))
                return LiveTestError("The requested game instance is stale.");
            string requestedRunId = ReadString(payload, "runId", "");
            if (!LiveTestModes.Contains(mode)) return LiveTestError("Unsupported live-test mode.");
            if (!notableObservation && !NotableStartupObservationPlanAllowed(payload, false))
                return LiveTestError("Notable startup observation requires a single owned, read-only command.");
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
                        ExpirePaidCourtLease(existing, DateTimeOffset.UtcNow);
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
                        if (notableObservation && (CanonicalJson(ReadDictionary(existing, "enrollment"))
                                != CanonicalJson(ReadDictionary(payload, "enrollment"))
                            || ReadCurrentNotableStartupBasis(existing, out _) == null))
                            return LiveTestError("The original notable startup observation no longer owns this rollback scope.");
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
                if (active != null && !ExpirePaidCourtLease(active, DateTimeOffset.UtcNow)
                    && !CloseUnavailableNotableStartupObservation(active, out _)) return new Dictionary<string, object>
                {
                    ["ok"] = false, ["error"] = "A live interaction run is already active for this campaign.", ["activeRunId"] = ReadString(active, "runId", "")
                };

                string effects = notableObservation ? "observed" : NormalizeLiveTestEffects(ReadString(payload, "effects", "guarded"));
                var enrollment = ReadDictionary(payload, "enrollment");
                Dictionary<string, object> executionStart = null;
                NotableStartupObservationBasis notableBasis = null;
                if (notableObservation)
                {
                    notableBasis = ReadNotableStartupObservationBasis(campaignId, liveInstance, enrollment, out bool timelineBusy);
                    if (notableBasis == null)
                        return LiveTestError(timelineBusy ? "The timeline is busy. Retry the notable startup observation."
                            : "The exact registered rollback or held notable reservation is unavailable.");
                }
                if (IsLiveTestExecutionEnrollment(mode, campaignId, enrollment))
                {
                    // Heartbeat writers share this lock. Observe the same heartbeat that
                    // authorizes starting the run for the already selected game instance.
                    var currentRuntime = LoadLiveTestRuntime(campaignId);
                    if (!IsFreshLiveTestRuntime(currentRuntime)
                        || ReadString(currentRuntime, "campaignId", "") != campaignId
                        || ReadString(currentRuntime, "gameInstanceId", "") != liveInstance
                        || ReadBool(ReadDictionary(currentRuntime, "saveSync"), "alignmentPending", false)
                        || (!notableObservation
                            && !ReadBool(ReadDictionary(currentRuntime, "characterInitialization"), "notableBackgroundReady", false)
                            && !IsInitializationControlRun(payload)))
                        return LiveTestError("The loaded-game heartbeat changed while starting this run. Refresh the loaded campaign and retry.");

                    executionStart = ReadLiveTestExecutionIdentity(currentRuntime);
                    if (executionStart != null)
                        executionStart["heartbeatReceivedUtc"] = ReadString(currentRuntime, "receivedUtc", "");
                }
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
                    ["effects"] = effects,
                    ["enrollment"] = enrollment,
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
                if (executionStart != null)
                {
                    executionStart["campaignId"] = campaignId;
                    executionStart["gameInstanceId"] = liveInstance;
                    executionStart["runId"] = runId;
                    run["executionStartIdentity"] = executionStart;
                    run["executionObservationVersion"] = LiveTestExecutionObservationVersion;
                }
                run["commands"] = NormalizeLiveTestScenarioCommands(run, payload);
                var normalized = ReadDictionaryList(run, "commands");
                var requestedSteps = ReadDictionaryList(payload, "steps");
                if (requestedSteps.Any(IsPaidCourtFixtureCommand) || normalized.Any(IsPaidCourtFixtureCommand))
                {
                    if (normalized.Count != 1 || requestedSteps.Count != 1 || GetPaidCourtCommand(run) == null
                        || requestedInstance != liveInstance || !ReadBool(run, "autoCompleteWhenIdle", false))
                        return LiveTestError("Paid Court acceptance requires one bound command in its own active run.");
                }
                if (notableObservation)
                {
                    if (executionStart == null || normalized.Count != 1)
                        return LiveTestError("The notable startup command could not bind the active game.");
                    run["notableStartupBasisSha256"] = notableBasis.Digest;
                    var issuedScope = CloneDictionary(enrollment);
                    issuedScope["loadSessionSha256"] = notableBasis.LoadSessionSha256;
                    issuedScope["restoreGenerationSha256"] = notableBasis.RestoreGenerationSha256;
                    normalized[0]["notableStartupScope"] = issuedScope;
                    normalized[0]["effects"] = "observed";
                    normalized[0]["timeoutSeconds"] = 30;
                }
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
                if (ReadString(run, "mode", "") == NotableStartupObservationMode
                    || IsNotableStartupObservationOperation(payload))
                    return LiveTestError("Notable startup observation cannot be enqueued.");
                Dictionary<string, object> command = NormalizeLiveTestCommand(run, payload);
                if (command == null) return LiveTestError("Unsupported or invalid live-test command.");
                List<Dictionary<string, object>> commands = ReadDictionaryList(run, "commands");
                if (IsPaidCourtFixtureCommand(command) || commands.Any(IsPaidCourtFixtureCommand))
                    return LiveTestError("Paid Court acceptance cannot enqueue additional commands.");
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
                if (GetPaidCourtCommand(run) is { } paid && !LiveTestTerminalRunStates.Contains(ReadString(run, "status", "")))
                {
                    if (ExpirePaidCourtLease(run, DateTimeOffset.UtcNow)) return LiveTestRunSummary(run, true);
                    if (PaidCourtNativeDispatchAdmitted(paid))
                        return LiveTestError("Paid Court native dispatch has already been admitted; await its receipt or expiry.");
                    if (resume) return LiveTestError("A paid Court command cannot resume an earlier lease.");
                    var now = DateTimeOffset.UtcNow;
                    InterruptPaidCourtRun(run, paid, now, "Paused before native dispatch. A new paid Court run is required.");
                    return LiveTestRunSummary(run, true);
                }
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
                        if (IsLiveTestExecutionEvidenceRun(run))
                            AdvanceLiveTestExecutionRetry(infrastructureCommand);
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
                if (GetPaidCourtCommand(run) is { } paid)
                {
                    if (LiveTestTerminalRunStates.Contains(ReadString(run, "status", "")) || ExpirePaidCourtLease(run, DateTimeOffset.UtcNow))
                        return LiveTestRunSummary(run, true);
                    if (PaidCourtNativeDispatchAdmitted(paid))
                        return LiveTestError("Paid Court native dispatch has already been admitted; await its receipt or expiry.");
                }
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
            string campaignId;
            string runId = query.TryGetValue("runId", out string requestedRun) ? requestedRun : "";
            Dictionary<string, object> run;
            Dictionary<string, object> response;
            lock (LiveTestLock)
            {
                campaignId = query.TryGetValue("campaignId", out string campaign) ? campaign : ReadString(LoadLiveTestRuntime(""), "campaignId", "");
                run = string.IsNullOrWhiteSpace(runId) ? FindLatestLiveTestRun(campaignId) : LoadLiveTestRun(campaignId, runId);
                if (run == null || run.Count == 0)
                    return new Dictionary<string, object> { ["ok"] = true, ["found"] = false, ["campaignId"] = campaignId };
                // Read and classify expiry against the same snapshot as dispatch, without writing from GET.
                var paid = GetPaidCourtCommand(run);
                var now = DateTimeOffset.UtcNow;
                string expiry = PaidCourtExpiryReason(run, paid, now);
                if (expiry != null) SetPaidCourtInterrupted(run, paid, now, expiry);
                response = LiveTestRunSummary(run, true);
                response["found"] = true;
            }
            if (includeEvidence)
            {
                response["auditEvidence"] = CollectLiveTestAuditEvidence(run);
                if (ReadString(run, "mode", "") == "party_agency" && IsLiveTestExecutionEvidenceRun(run)
                    && ReadString(run, "campaignId", "") == campaignId
                    && (string.IsNullOrWhiteSpace(runId) || ReadString(run, "runId", "") == runId))
                    response["partyAgencyCompletedSendEvidence"] = ReadCompletedPartyAgencySendEvidence(run);
            }
            return response;
        }

        internal static Dictionary<string, object> LiveTestGamePollApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            string instanceId = ReadString(payload, "gameInstanceId", "");
            if (!IsLiveTestArmed(CampaignFileService.ReadJsonObject(LiveTestArmPath()))) return new Dictionary<string, object> { ["ok"] = true, ["found"] = false, ["armed"] = false };
            lock (LiveTestLock)
            {
                Dictionary<string, object> run = FindActiveLiveTestRun(campaignId);
                if (run == null || string.Equals(ReadString(run, "status", ""), "paused", StringComparison.OrdinalIgnoreCase))
                    return new Dictionary<string, object> { ["ok"] = true, ["found"] = false, ["armed"] = true };
                if (!string.Equals(ReadString(run, "gameInstanceId", ""), instanceId, StringComparison.OrdinalIgnoreCase))
                    return LiveTestError("The polling game instance does not own this run.");
                if (CloseUnavailableNotableStartupObservation(run, out bool timelineBusy) || timelineBusy)
                    return new Dictionary<string, object> { ["ok"] = true, ["found"] = false, ["armed"] = true };

                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (ExpirePaidCourtLease(run, now))
                    return new Dictionary<string, object> { ["ok"] = true, ["found"] = false, ["armed"] = true };
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
                if (IsPaidCourtFixtureCommand(command))
                {
                    var arm = CampaignFileService.ReadJsonObject(LiveTestArmPath());
                    var runtime = LoadLiveTestRuntime(campaignId);
                    string armId = ReadString(arm, "armId", "");
                    string process = ReadString(ReadLiveTestExecutionIdentity(runtime), "clientProcessInstanceId", "");
                    if (!ReferenceEquals(GetPaidCourtCommand(run), command) || !IsLiveTestArmed(arm) || armId.Length == 0
                        || !IsFreshLiveTestRuntime(runtime) || ReadString(runtime, "campaignId", "") != campaignId
                        || ReadString(runtime, "gameInstanceId", "") != instanceId || !Guid.TryParseExact(process, "N", out _)
                        || !PaidCourtMatchesLoadedScope(command, runtime)
                        || PaidCourtString(runtime, "playerHeroId").Length == 0)
                        return LiveTestError("The paid Court lease requires the exact armed native runtime.");
                    command["paidCourtLeasedProcessId"] = process;
                    command["paidCourtLeasedPayerId"] = PaidCourtString(runtime, "playerHeroId");
                    command["paidCourtArmId"] = armId;
                }
                command["status"] = "leased";
                command["leaseId"] = Guid.NewGuid().ToString("N");
                command["leaseStartedUtc"] = now.ToString("o");
                command["leaseExpiresUtc"] = now.AddSeconds(LiveTestLeaseSeconds).ToString("o");
                command["attempt"] = ReadInt(command, "attempt", 0) + 1;
                RecordLiveTestExecutionLease(run, command, now);
                command["updatedUtc"] = now.ToString("o");
                run["updatedUtc"] = now.ToString("o");
                SaveLiveTestRun(run);
                var delivery = new Dictionary<string, object>(command);
                delivery.Remove("issuedExecutionObservations");
                if (IsLiveTestExecutionEvidenceRun(run))
                    delivery["gameInstanceId"] = ReadString(run, "gameInstanceId", "");
                return new Dictionary<string, object> { ["ok"] = true, ["found"] = true, ["run"] = LiveTestRunSummary(run, false), ["command"] = delivery };
            }
        }

        private static bool IsLiveTestExecutionEnrollment(string mode, string campaign,
            Dictionary<string, object> enrollment) =>
            !string.IsNullOrWhiteSpace(campaign) && ReadString(enrollment, "campaignId", "") == campaign
            && !string.IsNullOrWhiteSpace(ReadString(enrollment, "campaignTestRunId", ""))
            && (mode == "party_agency" && !string.IsNullOrWhiteSpace(ReadString(enrollment, "certificationRunId", ""))
                || mode == NotableStartupObservationMode && ReadString(enrollment, "source", "") == NotableStartupObservationSource);

        private static bool IsLiveTestExecutionEvidenceRun(Dictionary<string, object> run) =>
            IsLiveTestExecutionEnrollment(ReadString(run, "mode", ""), ReadString(run, "campaignId", ""),
                ReadDictionary(run, "enrollment"))
            && run.TryGetValue("executionObservationVersion", out object version)
            && version is int schema && schema == LiveTestExecutionObservationVersion;

        private static bool HasLiveTestExecutionVersion(Dictionary<string, object> observation) =>
            observation != null && observation.TryGetValue("schemaVersion", out object version)
            && version is int schema && schema == LiveTestExecutionObservationVersion;

        private static Dictionary<string, object> ReadLiveTestExecutionIdentity(Dictionary<string, object> runtime)
        {
            var observation = ReadDictionary(runtime, "executionIdentity");
            string processId = ReadString(observation, "clientProcessInstanceId", "");
            if (!HasLiveTestExecutionVersion(observation)
                || !Guid.TryParseExact(processId, "N", out _)) return null;

            var reported = ReadDictionary(observation, "moduleIds");
            var modules = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string name in LiveTestObservedClientModules)
            {
                if (Guid.TryParse(ReadString(reported, name, ""), out Guid id) && id != Guid.Empty)
                    modules[name] = id.ToString("D");
            }
            if (!modules.ContainsKey("ReignBeta")) return null;

            return new Dictionary<string, object>
            {
                ["clientProcessInstanceId"] = processId,
                ["clientModuleIds"] = modules,
                ["serverProcessInstanceId"] = LiveTestServerProcessInstanceId,
                ["serverModuleId"] = typeof(WorldSimulationService).Module.ModuleVersionId.ToString("D")
            };
        }

        // Called under LiveTestLock before the run/lease is saved and handed to the game.
        private static void AdvanceLiveTestExecutionRetry(Dictionary<string, object> command)
        {
            // Ordinary expiry/re-lease is the same attempt. Explicit infrastructure
            // recovery starts a new attempt without relabeling the old issued evidence.
            command["executionRetryGeneration"] = checked(ReadInt(command, "executionRetryGeneration", 0) + 1);
        }

        private static bool HasCurrentLiveTestExecutionRetry(Dictionary<string, object> command,
            Dictionary<string, object> issuedObservation)
        {
            return ReadInt(command, "executionRetryGeneration", 0)
                == ReadInt(issuedObservation, "executionRetryGeneration", 0);
        }

        private static void RecordLiveTestExecutionLease(Dictionary<string, object> run,
            Dictionary<string, object> command, DateTimeOffset now)
        {
            if (!IsLiveTestExecutionEvidenceRun(run)) return;

            string campaign = ReadString(run, "campaignId", "");
            string instance = ReadString(run, "gameInstanceId", "");
            var heartbeat = LoadLiveTestRuntime(campaign);
            var issued = ReadDictionaryList(command, "issuedExecutionObservations");
            if (IsFreshLiveTestRuntime(heartbeat)
                && ReadString(heartbeat, "campaignId", "") == campaign
                && ReadString(heartbeat, "gameInstanceId", "") == instance)
            {
                var evidence = ReadLiveTestExecutionIdentity(heartbeat);
                if (evidence != null)
                {
                    evidence["leaseId"] = ReadString(command, "leaseId", "");
                    evidence["campaignId"] = campaign;
                    evidence["gameInstanceId"] = instance;
                    evidence["runId"] = ReadString(run, "runId", "");
                    evidence["commandId"] = ReadString(command, "commandId", "");
                    evidence["observedUtc"] = now.ToString("o");
                    evidence["executionRetryGeneration"] = ReadInt(command, "executionRetryGeneration", 0);
                    issued.Add(evidence);
                    int excess = issued.Count - LiveTestRetainedObservationLeases;
                    if (excess > 0) issued.RemoveRange(0, excess);
                    command["issuedExecutionObservations"] = issued;
                }
            }
            command["executionObservationVersion"] = LiveTestExecutionObservationVersion;
        }

        private static bool IsPaidCourtFixtureCommand(Dictionary<string, object> command) =>
            string.Equals(ReadString(command, "operation", ReadString(command, "op", "")).Trim(), "ruler_docket_test",
                StringComparison.OrdinalIgnoreCase)
            && (ReadString(command, "phase", "") is "court_life_paid_supply_preflight"
                or "court_life_paid_supply_apply" or "court_life_paid_supply_verify");

        private static bool IsPaidCourtRunIdentifier(string value) =>
            value.Length == 43 && value.StartsWith("court_paid_", StringComparison.Ordinal)
            && Guid.TryParseExact(value.Substring(11), "N", out _);

        private static Dictionary<string, object> GetPaidCourtCommand(Dictionary<string, object> run)
        {
            var commands = ReadDictionaryList(run, "commands");
            return commands.Count == 1 && IsPaidCourtFixtureCommand(commands[0])
                && ReadString(run, "mode", "") == "spymaster" && ReadString(commands[0], "mode", "") == "spymaster"
                && IsPaidCourtRunIdentifier(ReadString(run, "runId", ""))
                && IsPaidCourtRunIdentifier(ReadString(commands[0], "commandId", ""))
                && HasPaidCourtFixtureScope(commands[0]) ? commands[0] : null;
        }

        private static bool PaidCourtNativeDispatchAdmitted(Dictionary<string, object> command) =>
            ReadString(command, "status", "") == "running" && HasPaidCourtDispatchRecord(command);

        private static bool HasPaidCourtDispatchRecord(Dictionary<string, object> command)
        {
            if (command == null || !command.TryGetValue("paidCourtNativeDispatch", out object admitted) || admitted is not true
                || !command.TryGetValue("paidCourtAuthorityVersion", out object version) || version is not 1)
                return false;
            string lease = ReadString(command, "leaseId", "");
            var dispatchUtc = ParseLiveTestUtc(ReadString(command, "paidCourtNativeDispatchUtc", ""));
            var started = ParseLiveTestUtc(ReadString(command, "leaseStartedUtc", ""));
            return Guid.TryParseExact(lease, "N", out _)
                && ReadString(command, "paidCourtNativeDispatchLeaseId", "") == lease
                && started != DateTimeOffset.MinValue && dispatchUtc >= started
                && dispatchUtc < ParseLiveTestUtc(ReadString(command, "leaseExpiresUtc", ""));
        }

        private static bool ExpirePaidCourtLease(Dictionary<string, object> run, DateTimeOffset now)
        {
            var command = GetPaidCourtCommand(run);
            string expiry = PaidCourtExpiryReason(run, command, now);
            if (expiry == null) return false;
            InterruptPaidCourtRun(run, command, now, expiry);
            return true;
        }

        private static string PaidCourtExpiryReason(Dictionary<string, object> run, Dictionary<string, object> command,
            DateTimeOffset now)
        {
            if (command == null || LiveTestTerminalRunStates.Contains(ReadString(run, "status", ""))
                || ReadString(command, "status", "") is not ("leased" or "accepted" or "running")
                || ParseLiveTestUtc(ReadString(command, "leaseExpiresUtc", "")) > now)
                return null;
            return PaidCourtNativeDispatchAdmitted(command)
                ? "Paid Court dispatch expired after admission. Reconcile the original native receipt before any retry."
                : "Paid Court lease expired before native dispatch. No native dispatch was admitted.";
        }

        private static void InterruptPaidCourtRun(Dictionary<string, object> run, Dictionary<string, object> command,
            DateTimeOffset now, string message)
        {
            SetPaidCourtInterrupted(run, command, now, message);
            SaveLiveTestRun(run);
        }

        private static void SetPaidCourtInterrupted(Dictionary<string, object> run, Dictionary<string, object> command,
            DateTimeOffset now, string message)
        {
            string stamp = now.ToString("o");
            run["status"] = command["status"] = "interrupted";
            run["message"] = command["message"] = message;
            run["completedUtc"] = command["completedUtc"] = stamp;
            run["updatedUtc"] = command["updatedUtc"] = stamp;
        }

        private static bool AdmitPaidCourtTransport(Dictionary<string, object> run, Dictionary<string, object> command,
            Dictionary<string, object> payload, bool result)
        {
            // Caller holds LiveTestLock, including the durable ACK/result write below.
            string campaign = ReadString(run, "campaignId", "");
            var active = FindActiveLiveTestRun(campaign);
            if (active == null || ReadString(active, "runId", "") != ReadString(run, "runId", ""))
                return false;
            var arm = CampaignFileService.ReadJsonObject(LiveTestArmPath());
            var runtime = LoadLiveTestRuntime(campaign);
            var now = DateTimeOffset.UtcNow;
            return IsLiveTestArmed(arm) && IsFreshLiveTestRuntime(runtime)
                && ApplyPaidCourtTransport(run, command, payload, runtime, ReadString(arm, "armId", ""), result, now);
        }

        private static bool ApplyPaidCourtTransport(Dictionary<string, object> run, Dictionary<string, object> command,
            Dictionary<string, object> payload, Dictionary<string, object> runtime, string armId, bool result, DateTimeOffset now)
        {
            string requested = ReadString(payload, "status", "");
            string phase = ReadString(command, "phase", "");
            string process = ReadString(payload, "clientProcessInstanceId", "");
            string state = ReadString(command, "status", "");
            bool dispatched = PaidCourtNativeDispatchAdmitted(command);
            if (!ReferenceEquals(GetPaidCourtCommand(run), command) || ReadString(run, "status", "") != "running"
                || ReadBool(run, "cancelRequested", false) || !MatchesLiveTestCommandTransport(run, command, payload)
                || ReadString(payload, "phase", "") != phase || string.IsNullOrWhiteSpace(armId)
                || ReadString(runtime, "campaignId", "") != ReadString(run, "campaignId", "")
                || ReadString(runtime, "gameInstanceId", "") != ReadString(run, "gameInstanceId", "")
                || !PaidCourtMatchesLoadedScope(command, runtime)
                || !PaidCourtMatches(command, "paidCourtLeasedPayerId", runtime, "playerHeroId")
                || !Guid.TryParseExact(ReadString(command, "leaseId", ""), "N", out _)
                || !Guid.TryParseExact(process, "N", out _)
                || ReadString(ReadLiveTestExecutionIdentity(runtime), "clientProcessInstanceId", "") != process
                || ReadString(command, "paidCourtLeasedProcessId", "") != process
                || ReadString(command, "paidCourtArmId", "") != armId
                || ParseLiveTestUtc(ReadString(command, "leaseExpiresUtc", "")) <= now)
                return false;

            if (!result && requested == "accepted" && state == "leased")
            {
                command["paidCourtAuthorityVersion"] = 1;
                command["paidCourtAcceptedProcessId"] = process;
                command["paidCourtNativeDispatch"] = false;
                return true;
            }

            if (ReadString(command, "paidCourtAcceptedProcessId", "") != process)
                return false;
            if (!result && requested == "running" && state == "accepted" && !dispatched
                && phase == "court_life_paid_supply_apply")
            {
                var terms = ReadDictionary(payload, "nativeTerms");
                if (ReadPaidCourtFixtureTerms(command, terms) == null) return false;
                command["paidCourtNativeDispatchTerms"] = terms;
                command["paidCourtNativeDispatch"] = true;
                command["paidCourtNativeDispatchLeaseId"] = ReadString(command, "leaseId", "");
                command["paidCourtNativeDispatchUtc"] = now.ToString("o");
                return true;
            }
            return result && (requested == "failed" || (requested == "completed" && PaidCourtCompletedEvidenceMatches(command, payload)))
                && ((state == "running" && dispatched)
                    || (state == "accepted" && !dispatched && (requested == "failed" || phase != "court_life_paid_supply_apply")));
        }

        private static bool PaidCourtCompletedEvidenceMatches(Dictionary<string, object> command, Dictionary<string, object> payload)
        {
            // Structural corroboration only. The diagnostic transport does not authenticate native origin.
            var evidence = ReadDictionary(payload, "result");
            string phase = ReadString(command, "phase", "");
            if (!PaidCourtCompletionScopeMatches(command, evidence, out long revision, out long revisionAfter))
                return false;

            if (phase == "court_life_paid_supply_preflight")
            {
                if (revisionAfter != revision || PaidCourtString(evidence, "sourceSettlementId").Length == 0
                    || !PaidCourtMatches(evidence, "payerHeroId", command, "paidCourtLeasedPayerId")
                    || PaidCourtString(evidence, "itemId") != "grain"
                    || !PaidCourtNumberIs(evidence, "itemAmount", 1) || !PaidCourtNumberIs(evidence, "goldAmount", 1)
                    || !TryPaidCourtNumber(evidence, "sourceStock", out _) || !TryPaidCourtNumber(evidence, "payerGold", out _)
                    || !evidence.TryGetValue("eligibleTargets", out object targets) || targets is not IList rows || rows.Count > 32)
                    return false;
                return rows.Cast<object>().All(row => row is Dictionary<string, object> target
                    && PaidCourtString(target, "settlementId").Length > 0
                    && TryPaidCourtNumber(target, "stock", out _) && TryPaidCourtNumber(target, "gold", out _));
            }

            var transfer = ReadPaidCourtTransferProof(command, evidence);
            if (transfer == null) return false;
            if (phase == "court_life_paid_supply_verify")
                return revisionAfter == revision && transfer.Before == transfer.Journal.AfterPayment
                    && PaidCourtTrue(evidence, "exactReloadReplay")
                    && PaidCourtOriginalDispatchMatches(command, evidence, transfer);
            return PaidCourtApplyCompletionMatches(command, evidence, transfer, revision, revisionAfter);
        }

        private static bool PaidCourtCompletionScopeMatches(Dictionary<string, object> command, Dictionary<string, object> evidence,
            out long revision, out long revisionAfter)
        {
            revision = revisionAfter = default;
            return PaidCourtTrue(evidence, "ok") && PaidCourtString(evidence, "schema") == "reign-court-life-paid-supply-v1"
                && PaidCourtMatches(evidence, "runId", command, "fixtureRunId")
                && PaidCourtMatches(evidence, "phase", command, "phase")
                && PaidCourtMatches(evidence, "campaignTestRunId", command, "campaignTestRunId")
                && PaidCourtMatches(evidence, "saveName", command, "expectedSaveName")
                && PaidCourtMatches(evidence, "campaignId", command, "campaignId")
                && PaidCourtMatches(evidence, "gameInstanceId", command, "gameInstanceId")
                && PaidCourtMatches(evidence, "dispatchRunId", command, "runId")
                && PaidCourtMatches(evidence, "savePointId", command, "expectedPaidSavePointId")
                && PaidCourtMatches(evidence, "timelineId", command, "expectedPaidTimelineId")
                && PaidCourtString(evidence, "sessionId").Length > 0
                && TryPaidCourtNumber(evidence, "nativeProcessId", out long process) && process > 0 && process <= int.MaxValue
                && PaidCourtLeasedProcessDigestMatches(command, PaidCourtString(evidence, "clientProcessInstanceSha256"))
                && TryPaidCourtNumber(evidence, "sessionRevisionBefore", out revision)
                && TryPaidCourtNumber(evidence, "sessionRevisionAfter", out revisionAfter);
        }

        private readonly record struct PaidCourtBalances(long Source, long Target, long Payer, long Recipient);
        private sealed record PaidCourtJournal(string ReceiptId, PaidCourtBalances Before, PaidCourtBalances AfterRemoval,
            PaidCourtBalances AfterDelivery, PaidCourtBalances AfterPayment);
        private sealed record PaidCourtTransferProof(PaidCourtFixtureTerms Terms, PaidCourtBalances Before, PaidCourtJournal Journal);

        private static PaidCourtTransferProof ReadPaidCourtTransferProof(Dictionary<string, object> command,
            Dictionary<string, object> evidence)
        {
            var terms = ReadPaidCourtFixtureTerms(command, ReadDictionary(evidence, "terms"));
            var receipt = ReadDictionary(evidence, "receipt");
            var observations = ReadDictionary(receipt, "observations");
            string receiptId = PaidCourtString(receipt, "receiptId");
            if (terms == null || receiptId != "supply_receipt_" + terms.CommandId
                || PaidCourtString(receipt, "phase") != "Verified" || !PaidCourtTrue(receipt, "exactVerifiedReceipt")
                || !PaidCourtJournalBindingMatches(command, terms, PaidCourtString(observations, "Binding"))
                || !PaidCourtNumberIs(observations, "Phase", 4)
                || !PaidCourtHoldings(evidence, "before", out var before) || !PaidCourtHoldings(evidence, "after", out var after)
                || !PaidCourtHoldings(observations, "Before", out var journalBefore)
                || !PaidCourtHoldings(observations, "AfterRemoval", out var removed)
                || !PaidCourtHoldings(observations, "AfterDelivery", out var delivered)
                || !PaidCourtHoldings(observations, "AfterPayment", out var journalAfter)
                || removed != new PaidCourtBalances(journalBefore.Source - 1, journalBefore.Target, journalBefore.Payer, journalBefore.Recipient)
                || delivered != new PaidCourtBalances(journalBefore.Source - 1, journalBefore.Target + 1, journalBefore.Payer, journalBefore.Recipient)
                || journalAfter != new PaidCourtBalances(journalBefore.Source - 1, journalBefore.Target + 1, journalBefore.Payer - 1, journalBefore.Recipient + 1)
                || after != journalAfter)
                return null;
            return new PaidCourtTransferProof(terms, before, new PaidCourtJournal(receiptId, journalBefore, removed, delivered, journalAfter));
        }

        private static bool PaidCourtApplyCompletionMatches(Dictionary<string, object> command, Dictionary<string, object> evidence,
            PaidCourtTransferProof transfer, long revision, long revisionAfter) =>
            revision < long.MaxValue && revisionAfter == revision + 1 && transfer.Journal.Before == transfer.Before
            && PaidCourtString(evidence, "state") == "verified"
            && transfer.Terms == ReadPaidCourtFixtureTerms(command, ReadDictionary(command, "paidCourtNativeDispatchTerms"));

        private static bool PaidCourtMatchesLoadedScope(Dictionary<string, object> command, Dictionary<string, object> runtime) =>
            PaidCourtMatches(runtime, "activeSaveName", command, "expectedSaveName")
            && PaidCourtMatches(ReadDictionary(runtime, "saveSync"), "timelineId", command, "expectedPaidTimelineId");

        private static bool HasPaidCourtFixtureScope(Dictionary<string, object> command) =>
            new[] { "campaignId", "fixtureRunId", "campaignTestRunId", "expectedPaidTimelineId" }.All(key =>
                PaidCourtString(command, key) is { Length: > 0 and <= 160 } value
                && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':'))
            && Guid.TryParseExact(PaidCourtString(command, "expectedPaidSavePointId"), "N", out _)
            && PaidCourtString(command, "expectedSaveName").Length > 0;

        // The diagnostic fixture reproduces the native formula for its four ASCII scope identifiers.
        // It does not change how ordinary Court commands or native receipts are constructed.
        private static string PaidCourtFixtureCommandId(Dictionary<string, object> command)
        {
            string campaign = PaidCourtString(command, "campaignId"), fixture = PaidCourtString(command, "fixtureRunId");
            string owner = PaidCourtString(command, "campaignTestRunId"), timeline = PaidCourtString(command, "expectedPaidTimelineId");
            return "court_paid_supply_" + Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(new { campaign, fixture, owner, timeline }))).ToLowerInvariant();
        }

        private sealed record PaidCourtFixtureTerms(string CommandId, string Source, string Target, string Payer);

        private static PaidCourtFixtureTerms ReadPaidCourtFixtureTerms(Dictionary<string, object> command, Dictionary<string, object> terms)
        {
            if (!HasPaidCourtFixtureScope(command) || terms == null)
                return null;
            var shape = new PaidCourtFixtureTerms(PaidCourtString(terms, "CommandId"), PaidCourtString(terms, "SourceSettlementStringId"),
                PaidCourtString(terms, "TargetSettlementStringId"), PaidCourtString(terms, "GoldPayerStringId"));
            return shape.CommandId == PaidCourtFixtureCommandId(command) && shape.Source.Length > 0 && shape.Source != shape.Target
                && shape.Target.Length > 0 && PaidCourtMatches(terms, "TargetSettlementStringId", command, "paidSupplyTargetSettlementId")
                && PaidCourtMatches(terms, "GoldPayerStringId", command, "paidCourtLeasedPayerId")
                && PaidCourtString(terms, "GoldRecipientStringId") == shape.Target && PaidCourtString(terms, "GoldRecipientKind") == "settlement"
                && PaidCourtString(terms, "GoldPayerKind") == "hero" && PaidCourtString(terms, "ItemStringId") == "grain"
                && PaidCourtNumberIs(terms, "ItemAmount", 1) && PaidCourtNumberIs(terms, "GoldAmount", 1)
                && terms.TryGetValue("VassalConsentGranted", out object consent) && consent is false ? shape : null;
        }

        private static bool PaidCourtJournalBindingMatches(Dictionary<string, object> command, PaidCourtFixtureTerms terms, string binding)
        {
            if (binding.Length == 0 || binding.Length > 4096) return false;
            try
            {
                using var doc = JsonDocument.Parse(binding, new JsonDocumentOptions { MaxDepth = 3 });
                var row = doc.RootElement;
                return row.ValueKind == JsonValueKind.Array && row.GetArrayLength() == 12
                    && JsonStringIs(row[0], PaidCourtString(command, "campaignId"))
                    && JsonStringIs(row[1], PaidCourtString(command, "expectedPaidTimelineId"))
                    && JsonStringIs(row[2], terms.Source) && JsonStringIs(row[3], terms.Target) && JsonStringIs(row[4], "grain")
                    && JsonOne(row[5]) && JsonOne(row[6]) && JsonStringIs(row[7], "hero") && JsonStringIs(row[8], terms.Payer)
                    && JsonStringIs(row[9], "settlement") && JsonStringIs(row[10], terms.Target) && row[11].ValueKind == JsonValueKind.False;
            }
            catch (JsonException) { return false; }

            static bool JsonStringIs(JsonElement item, string expected) => item.ValueKind == JsonValueKind.String && item.GetString() == expected;
            static bool JsonOne(JsonElement item) => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int value) && value == 1;
        }

        private static bool PaidCourtLeasedProcessDigestMatches(Dictionary<string, object> command, string digest)
        {
            string leaseProcess = PaidCourtString(command, "paidCourtLeasedProcessId");
            return Guid.TryParseExact(leaseProcess, "N", out _)
                && digest == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(leaseProcess)));
        }

        private static bool PaidCourtOriginalDispatchMatches(Dictionary<string, object> command,
            Dictionary<string, object> evidence, PaidCourtTransferProof transfer)
        {
            string originalRunId = PaidCourtString(evidence, "originalDispatchRunId");
            if (!IsPaidCourtRunIdentifier(originalRunId) || originalRunId == PaidCourtString(command, "runId")) return false;
            string campaign = PaidCourtString(command, "campaignId");
            var originalRun = LoadLiveTestRun(campaign, originalRunId);
            var original = GetPaidCourtCommand(originalRun);
            if (original == null || !HasPaidCourtDispatchRecord(original)
                || !PaidCourtMatches(originalRun, "runId", evidence, "originalDispatchRunId")
                || ReadString(originalRun, "status", "") is not ("completed" or "failed" or "interrupted")
                || !PaidCourtMatches(original, "campaignId", command, "campaignId")
                || PaidCourtString(original, "phase") != "court_life_paid_supply_apply"
                || !PaidCourtMatches(evidence, "originalGameInstanceId", original, "gameInstanceId")
                || !PaidCourtMatches(evidence, "originalSavePointId", original, "expectedPaidSavePointId")
                || PaidCourtString(evidence, "originalGameInstanceId") == PaidCourtString(evidence, "gameInstanceId")
                || PaidCourtString(evidence, "originalSavePointId") == PaidCourtString(evidence, "savePointId")
                || !PaidCourtLeasedProcessDigestMatches(original, PaidCourtString(evidence, "originalClientProcessInstanceSha256"))
                || PaidCourtString(evidence, "originalClientProcessInstanceSha256") == PaidCourtString(evidence, "clientProcessInstanceSha256"))
                return false;
            if (transfer.Terms != ReadPaidCourtFixtureTerms(original, ReadDictionary(original, "paidCourtNativeDispatchTerms")))
                return false;

            string runStatus = ReadString(originalRun, "status", ""), resultStatus = ReadString(original, "status", "");
            if (runStatus != "completed" && resultStatus != "completed")
                return true; // Failed or interrupted dispatch still permits locally observed reconciliation.
            if (runStatus != "completed" || resultStatus != "completed")
                return false;

            var originalEvidence = ReadDictionary(original, "result");
            if (!PaidCourtCompletionScopeMatches(original, originalEvidence, out long revision, out long revisionAfter))
                return false;
            var originalTransfer = ReadPaidCourtTransferProof(original, originalEvidence);
            return originalTransfer != null && PaidCourtApplyCompletionMatches(original, originalEvidence, originalTransfer, revision, revisionAfter)
                && originalTransfer.Terms == transfer.Terms && originalTransfer.Journal == transfer.Journal;
        }

        private static string PaidCourtString(Dictionary<string, object> data, string key) =>
            data != null && data.TryGetValue(key, out object raw) && raw is string value ? value : "";

        private static bool PaidCourtMatches(Dictionary<string, object> data, string key, Dictionary<string, object> expected, string expectedKey) =>
            PaidCourtString(data, key) is { Length: > 0 } actual && actual == PaidCourtString(expected, expectedKey);

        private static bool PaidCourtTrue(Dictionary<string, object> data, string key) =>
            data != null && data.TryGetValue(key, out object raw) && raw is true;

        private static bool TryPaidCourtNumber(Dictionary<string, object> data, string key, out long value)
        {
            value = data != null && data.TryGetValue(key, out object raw) ? raw switch { int i => i, long l => l, _ => -1 } : -1;
            return value >= 0;
        }

        private static bool PaidCourtNumberIs(Dictionary<string, object> data, string key, long expected) =>
            TryPaidCourtNumber(data, key, out long actual) && actual == expected;

        private static bool PaidCourtHoldings(Dictionary<string, object> root, string key,
            out PaidCourtBalances holdings)
        {
            holdings = default;
            var balances = ReadDictionary(root, key);
            if (!TryPaidCourtNumber(balances, "Source", out long source) || !TryPaidCourtNumber(balances, "Target", out long target)
                || !TryPaidCourtNumber(balances, "Payer", out long payer) || !TryPaidCourtNumber(balances, "Recipient", out long recipient)
                || Math.Max(Math.Max(source, target), Math.Max(payer, recipient)) > int.MaxValue)
                return false;
            holdings = new PaidCourtBalances(source, target, payer, recipient);
            return true;
        }

        private static bool MatchesLiveTestCommandTransport(Dictionary<string, object> run,
            Dictionary<string, object> command, Dictionary<string, object> report)
        {
            string lease = ReadString(command, "leaseId", "");
            string campaign = ReadString(run, "campaignId", "");
            string instance = ReadString(run, "gameInstanceId", "");
            string runId = ReadString(run, "runId", "");
            string commandId = ReadString(command, "commandId", "");
            return !string.IsNullOrWhiteSpace(lease)
                && !string.IsNullOrWhiteSpace(campaign) && !string.IsNullOrWhiteSpace(instance)
                && !string.IsNullOrWhiteSpace(runId) && !string.IsNullOrWhiteSpace(commandId)
                && ReadString(report, "leaseId", "") == lease
                && ReadString(report, "campaignId", "") == campaign
                && ReadString(report, "gameInstanceId", "") == instance
                && ReadString(report, "runId", "") == runId
                && ReadString(report, "commandId", "") == commandId;
        }

        private static Dictionary<string, object> BindLiveTestExecutionObservation(Dictionary<string, object> run,
            Dictionary<string, object> command, Dictionary<string, object> report)
        {
            var evidence = new Dictionary<string, object> { ["status"] = "missing" };
            var observed = ReadDictionary(report, "executionObservation");
            if (observed == null) return evidence;
            if (!HasLiveTestExecutionVersion(observed))
            {
                evidence["status"] = "unsupported_version";
                return evidence;
            }
            string activeLease = ReadString(command, "leaseId", "");
            if (activeLease.Length == 0 || ReadString(report, "leaseId", "") != activeLease)
            {
                evidence["status"] = "stale_transport";
                return evidence;
            }

            string campaign = ReadString(run, "campaignId", "");
            string instance = ReadString(run, "gameInstanceId", "");
            string runId = ReadString(run, "runId", "");
            string commandId = ReadString(command, "commandId", "");
            bool ScopeMatches(Dictionary<string, object> source) =>
                ReadString(source, "campaignId", "") == campaign
                && ReadString(source, "gameInstanceId", "") == instance
                && ReadString(source, "runId", "") == runId
                && ReadString(source, "commandId", "") == commandId;

            if (new[] { campaign, instance, runId, commandId }.Any(string.IsNullOrWhiteSpace)
                || !ScopeMatches(report) || !ScopeMatches(observed))
            {
                evidence["status"] = "scope_mismatch";
                return evidence;
            }

            string executionLease = ReadString(observed, "executionLeaseId", "");
            var original = ReadDictionaryList(command, "issuedExecutionObservations").Where(entry =>
                ScopeMatches(entry) && ReadString(entry, "leaseId", "") == executionLease).Take(2).ToList();
            if (executionLease.Length == 0 || original.Count != 1)
            {
                evidence["status"] = "unknown_original_lease";
                return evidence;
            }
            if (!HasCurrentLiveTestExecutionRetry(command, original[0]))
            {
                evidence["status"] = "stale_execution_retry";
                return evidence;
            }
            string observedProcess = ReadString(observed, "clientProcessInstanceId", "");
            if (observedProcess.Length == 0 || observedProcess != ReadString(original[0], "clientProcessInstanceId", ""))
            {
                evidence["status"] = "process_mismatch";
                return evidence;
            }

            // Compare these retained observations with reviewed artifacts before granting certification.
            // A newer heartbeat or transport lease does not establish an older result's origin.
            evidence["status"] = "pending_artifact_review";
            evidence["executionObservation"] = original[0];
            evidence["transportLeaseId"] = activeLease;
            return evidence;
        }

        private static bool IsLiveTestCommandLeaseEligible(
            Dictionary<string, object> command,
            DateTimeOffset now)
        {
            string state = ReadString(command, "status", "");
            if (state.Equals("queued", StringComparison.OrdinalIgnoreCase)) return true;
            if (IsPaidCourtFixtureCommand(command)) return false;
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
                bool paidCourt = IsPaidCourtFixtureCommand(command);
                if (paidCourt && !AdmitPaidCourtTransport(run, command, payload, result))
                    return LiveTestError("The paid Court command no longer has an active authority for this operation.");
                bool notableObservation = ReadString(run, "mode", "") == NotableStartupObservationMode;
                if (notableObservation && !result && (ReadString(run, "status", "") != "running"
                    || ExactNotableObservationText(payload, "status") != "accepted"
                    || ReadString(command, "status", "") != "leased"
                    || ParseLiveTestUtc(ReadString(command, "leaseExpiresUtc", "")) <= DateTimeOffset.UtcNow
                    || command.ContainsKey("notableStartupAcceptedExecution")))
                    return LiveTestError("The notable startup observation may accept only its first exact execution lease.");
                bool observesExecution = IsLiveTestExecutionEvidenceRun(run);
                Dictionary<string, object> observation = null;
                if (observesExecution)
                {
                    // An old request must not mutate the active assignment or reopen a terminal result.
                    if (!MatchesLiveTestCommandTransport(run, command, payload))
                        return LiveTestError("The live interaction command transport lease or game scope changed.");
                    string currentStatus = ReadString(command, "status", "");
                    if (LiveTestTerminalCommandStates.Contains(currentStatus)
                        || (currentStatus.Equals("needs_input", StringComparison.OrdinalIgnoreCase)
                            && ReadString(command, "pauseReason", "").Equals("fatal_infrastructure_failure", StringComparison.OrdinalIgnoreCase)))
                        return new Dictionary<string, object> { ["ok"] = true, ["idempotent"] = true, ["command"] = command };
                    observation = BindLiveTestExecutionObservation(run, command, payload);
                    if (ReadString(observation, "status", "") == "stale_execution_retry")
                        return LiveTestError("The original execution belongs to a superseded provider retry.");
                    if (!result)
                    {
                        var stamp = ReadDictionary(payload, "executionObservation");
                        if (ReadString(observation, "status", "") != "pending_artifact_review"
                            || ReadString(stamp, "executionLeaseId", "") != ReadString(command, "leaseId", ""))
                            return LiveTestError("The current execution lease could not be observed. Refresh the heartbeat and retry.");
                    }
                }
                if (result && LiveTestTerminalCommandStates.Contains(ReadString(command, "status", "")))
                    return new Dictionary<string, object> { ["ok"] = true, ["idempotent"] = true, ["command"] = command };
                if (result && ReadString(command, "status", "").Equals("needs_input", StringComparison.OrdinalIgnoreCase)
                    && ReadString(command, "pauseReason", "").Equals("fatal_infrastructure_failure", StringComparison.OrdinalIgnoreCase))
                    return new Dictionary<string, object> { ["ok"] = true, ["idempotent"] = true, ["command"] = command };

                string status = ReadString(payload, "status", result ? "completed" : "accepted").ToLowerInvariant();
                if (notableObservation && result && !LiveTestTerminalCommandStates.Contains(status))
                    return LiveTestError("A notable startup observation result must be terminal.");
                if (!new[] { "accepted", "running", "needs_input", "completed", "failed", "cancelled", "interrupted" }.Contains(status))
                    status = result ? "failed" : "accepted";
                string reportedError = ReadString(payload, "error", ReadString(payload, "message", "Command failed."));
                bool infrastructureFailure = result && status == "failed" && IsFatalLiveTestInfrastructureFailure(reportedError);
                bool fatalInfrastructureFailure = !paidCourt && !notableObservation && infrastructureFailure;
                bool paidDispatchAdmitted = paidCourt && PaidCourtNativeDispatchAdmitted(command);
                if (fatalInfrastructureFailure) status = "needs_input";
                if (observesExecution && !result && (status is "accepted" or "running")
                    && ReadString(command, "operation", "") == "send")
                    command["partyAgencyAcceptedSendExecution"] = PartyAgencySendAck(ReadDictionary(observation, "executionObservation"));
                if (notableObservation)
                {
                    if (!result)
                        command.Add("notableStartupAcceptedExecution", PartyAgencySendAck(ReadDictionary(observation, "executionObservation")));
                    if (result && status == "completed"
                        && !EvaluateNotableStartupObservation(run, command, observation,
                            ReadDictionary(payload, "result"), out bool timelineBusy))
                    {
                        if (timelineBusy)
                            return LiveTestError("The timeline is busy. The accepted notable startup observation remains eligible for replay.");
                        status = "failed";
                        reportedError = "The notable startup observation did not match the current rollback and original accepted execution.";
                    }
                }
                command["status"] = status;
                command["message"] = fatalInfrastructureFailure
                    ? "Paused because the configured LLM provider cannot currently accept requests. Resume after correcting provider access or balance."
                    : ReadString(payload, "message", "");
                command["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                if (result)
                {
                    if (observesExecution)
                        command["executionEvidence"] = observation;

                    command["result"] = notableObservation
                        ? NotableStartupObservationResult(run, command)
                        : fatalInfrastructureFailure
                            ? new Dictionary<string, object> { ["fatalInfrastructureFailure"] = true, ["error"] = DialogueService.LimitText(reportedError, 2000) }
                            : ReadDictionary(payload, "result") ?? new Dictionary<string, object>();
                    command["error"] = fatalInfrastructureFailure ? DialogueService.LimitText(reportedError, 2000)
                        : notableObservation && status == "failed"
                            ? reportedError : ReadString(payload, "error", "");
                    command["correlationIds"] = ReadStringList(payload, "correlationIds");
                    if (paidCourt && status == "failed")
                    {
                        var failure = ReadDictionary(command, "result");
                        failure["terminalInfrastructureFailure"] = infrastructureFailure;
                        failure["nativeDispatchAdmitted"] = paidDispatchAdmitted;
                        failure["reconciliationRequired"] = paidDispatchAdmitted;
                        failure["error"] = DialogueService.LimitText(reportedError, 2000);
                        command["result"] = failure;
                    }
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
