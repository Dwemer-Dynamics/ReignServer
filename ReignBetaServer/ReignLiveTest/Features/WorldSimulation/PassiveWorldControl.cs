using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace ReignLiveTest
{
    internal static partial class Program
    {
        // A paused fractional campaign day has not completed its current daily
        // cycle. Require rollups only through the latest fully completed day.
        private const double CampaignDayBoundaryEpsilon = 0.000001d;
        // A diplomacy evaluation can finish just after the overview is read while
        // its newly persisted announcement is still becoming visible to the next
        // overview read. Keep a real quiescence window instead of accepting two
        // adjacent samples that can straddle that completion boundary.
        private const int PassiveWorldStableObservationCount = 8;

        private static Dictionary<string, object> PassiveWorldControl(string[] args)
        {
            string operation = args.Skip(1)
                .FirstOrDefault(value => !value.StartsWith("-", StringComparison.Ordinal))
                ?? "status";
            Dictionary<string, object> runtime = Runtime(args);
            string campaignId = ResolveCampaign(args, runtime);
            string timelineId = FirstNonEmpty(
                Value(args, "--timeline", ""),
                String(ReadObject(ReadObject(runtime, "runtime"), "saveSync"), "timelineId"),
                "main");

            switch (operation.Trim().ToLowerInvariant())
            {
                case "status":
                    return CompactPassiveWorldStatus(runtime, campaignId, timelineId);
                case "snapshot":
                    return RunPassiveWorldStep(runtime, campaignId, "world_snapshot",
                        new Dictionary<string, object>(), 120);
                case "stop":
                    return RunPassiveWorldStep(runtime, campaignId, "world_time_control",
                        new Dictionary<string, object>
                        {
                            ["timeMode"] = "stop",
                            ["settlementWait"] = false,
                            ["autoAcknowledgeDiplomacyAnnouncements"] = true
                        }, 120);
                case "drain":
                    return DrainPassiveWorld(args, runtime, campaignId, timelineId);
                case "advance":
                    return AdvancePassiveWorld(args, runtime, campaignId, timelineId);
                case "checkpoint":
                    return CheckpointPassiveWorld(args, runtime, campaignId, timelineId);
                case "delete-checkpoint":
                    return DeletePassiveWorldCheckpoint(args, runtime, campaignId);
                default:
                    throw new InvalidOperationException(
                        "Unknown world-test operation '" + operation
                        + "'. Use status, snapshot, stop, drain, advance, checkpoint, or delete-checkpoint.");
            }
        }

        private static Dictionary<string, object> DrainPassiveWorld(
            string[] args,
            Dictionary<string, object> runtime,
            string campaignId,
            string timelineId)
        {
            Dictionary<string, object> stopped = RunPassiveWorldStep(runtime, campaignId,
                "world_time_control", new Dictionary<string, object>
                {
                    ["timeMode"] = "stop",
                    ["settlementWait"] = false,
                    ["autoAcknowledgeDiplomacyAnnouncements"] = true
                }, 120);
            if (!IsOk(stopped)) return stopped;
            Dictionary<string, object> latestRuntime = Runtime(
                new[] { "status", "--campaign", campaignId });
            double targetDay = ReadDoubleValue(ReadObject(latestRuntime, "runtime"),
                "worldDay", -1d);
            if (targetDay < 0d)
                throw new InvalidOperationException("The loaded campaign heartbeat does not contain a campaign day.");
            Dictionary<string, object> drained = WaitForPassiveWorldCatchUp(
                campaignId, timelineId, targetDay,
                Math.Max(60, IntValue(args, "--catch-up-timeout", 900)),
                Has(args, "--initial-baseline"));
            return new Dictionary<string, object>
            {
                ["ok"] = IsOk(drained),
                ["campaignId"] = campaignId,
                ["timelineId"] = timelineId,
                ["targetWorldDay"] = targetDay,
                ["stop"] = CompactPassiveWorldRun(stopped),
                ["catchUp"] = drained
            };
        }

        private static Dictionary<string, object> CheckpointPassiveWorld(
            string[] args,
            Dictionary<string, object> runtime,
            string campaignId,
            string timelineId)
        {
            string saveName = Value(args, "--save", "").Trim();
            if (string.IsNullOrWhiteSpace(saveName))
                throw new InvalidOperationException("world-test checkpoint requires --save <name>.");
            Dictionary<string, object> drained = DrainPassiveWorld(
                args, runtime, campaignId, timelineId);
            if (!IsOk(drained)) return drained;
            Dictionary<string, object> saved = SaveGame(new[]
            {
                "game-save", "--campaign", campaignId,
                "--save", saveName,
                "--wait", Math.Max(180, IntValue(args, "--save-timeout", 420))
                    .ToString(CultureInfo.InvariantCulture)
            });
            Dictionary<string, object> finalRuntime =
                WaitForPassiveWorldPostSaveRuntime(campaignId, saveName);
            bool postSaveRuntimeQuiesced =
                PassiveWorldPostSaveRuntimeQuiesced(finalRuntime, saveName);
            Dictionary<string, object> finalStatus = CompactPassiveWorldStatus(
                finalRuntime, campaignId, timelineId);
            return new Dictionary<string, object>
            {
                ["ok"] = IsOk(saved) && postSaveRuntimeQuiesced && IsOk(finalStatus),
                ["campaignId"] = campaignId,
                ["timelineId"] = timelineId,
                ["drain"] = drained,
                ["postSaveRuntimeQuiesced"] = postSaveRuntimeQuiesced,
                ["save"] = CompactPassiveWorldSave(saved, finalRuntime),
                ["report"] = finalStatus
            };
        }

        private static Dictionary<string, object> DeletePassiveWorldCheckpoint(
            string[] args,
            Dictionary<string, object> runtime,
            string campaignId)
        {
            string saveName = Value(args, "--save", "").Trim();
            string prefix = Value(args, "--prefix", "").Trim();
            if (string.IsNullOrWhiteSpace(saveName) || string.IsNullOrWhiteSpace(prefix))
                throw new InvalidOperationException(
                    "world-test delete-checkpoint requires --save <name> and --prefix <run-prefix>.");
            return RunPassiveWorldStep(runtime, campaignId, "world_delete_checkpoint",
                new Dictionary<string, object>
                {
                    ["saveName"] = saveName,
                    ["expectedPrefix"] = prefix,
                    ["confirmation"] = "delete Reign campaign-test checkpoint"
                }, 120);
        }

        private static Dictionary<string, object> PassiveWorldStatus(
            Dictionary<string, object> runtime,
            string campaignId,
            string timelineId)
        {
            Dictionary<string, object> overview = Get(
                "/world-test/overview?campaignId=" + Uri.EscapeDataString(campaignId)
                + "&timelineId=" + Uri.EscapeDataString(timelineId)
                + "&limit=40");
            return new Dictionary<string, object>
            {
                ["ok"] = IsOk(runtime) && IsOk(overview)
                    && string.IsNullOrEmpty(PassiveWorldCheckpointPolicy.ActionFailureError(
                        ReadLong(ReadObject(overview, "pipeline"), "failedActions", -1))),
                ["campaignId"] = campaignId,
                ["timelineId"] = timelineId,
                ["runtime"] = runtime,
                ["overview"] = overview,
                ["checkpoint"] = PassiveWorldCheckpointSummary(overview)
            };
        }

        private static Dictionary<string, object> AdvancePassiveWorld(
            string[] args,
            Dictionary<string, object> runtime,
            string campaignId,
            string timelineId)
        {
            Dictionary<string, object> live = ReadObject(runtime, "runtime");
            double startDay = ReadDoubleValue(live, "worldDay", -1d);
            if (startDay < 0d)
                throw new InvalidOperationException(
                    "The loaded campaign heartbeat does not contain a campaign day.");

            double days = DoubleValue(args, "--days", 1d);
            double explicitTarget = DoubleValue(args, "--target-day", double.NaN);
            double targetDay = double.IsNaN(explicitTarget)
                ? startDay + Math.Max(0d, days)
                : Math.Max(startDay, explicitTarget);
            int timeoutSeconds = Math.Max(120,
                Math.Min(21600, IntValue(args, "--timeout", 7200)));

            Dictionary<string, object> run = RunPassiveWorldStep(
                runtime,
                campaignId,
                "world_advance",
                new Dictionary<string, object>
                {
                    ["targetWorldDay"] = targetDay,
                    ["timeoutSeconds"] = timeoutSeconds
                },
                timeoutSeconds + 60);
            if (!IsOk(run)) return run;

            Dictionary<string, object> caughtUp = WaitForPassiveWorldCatchUp(
                campaignId, timelineId, targetDay,
                Math.Max(60, IntValue(args, "--catch-up-timeout", 900)));
            if (!IsOk(caughtUp)) return caughtUp;

            Dictionary<string, object> save = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["skipped"] = true
            };
            string saveName = string.Empty;
            if (!Has(args, "--no-save"))
            {
                saveName = Value(args, "--save",
                    "Reign_World_Day_" + Math.Floor(targetDay + 0.000001d)
                        .ToString("0", CultureInfo.InvariantCulture));
                save = SaveGame(new[]
                {
                    "game-save", "--campaign", campaignId,
                    "--save", saveName,
                    "--wait", Math.Max(180, IntValue(args, "--save-timeout", 420))
                        .ToString(CultureInfo.InvariantCulture)
                });
                if (!IsOk(save)) return save;
            }

            Dictionary<string, object> finalRuntime = string.IsNullOrWhiteSpace(saveName)
                ? Runtime(new[] { "status", "--campaign", campaignId })
                : WaitForPassiveWorldPostSaveRuntime(campaignId, saveName);
            bool postSaveRuntimeQuiesced = string.IsNullOrWhiteSpace(saveName)
                || PassiveWorldPostSaveRuntimeQuiesced(finalRuntime, saveName);
            Dictionary<string, object> finalStatus = PassiveWorldStatus(
                finalRuntime, campaignId, timelineId);
            return new Dictionary<string, object>
            {
                ["ok"] = IsOk(finalStatus) && postSaveRuntimeQuiesced,
                ["campaignId"] = campaignId,
                ["timelineId"] = timelineId,
                ["startWorldDay"] = startDay,
                ["targetWorldDay"] = targetDay,
                ["advanceRun"] = CompactPassiveWorldRun(run),
                ["catchUp"] = caughtUp,
                ["postSaveRuntimeQuiesced"] = postSaveRuntimeQuiesced,
                ["save"] = CompactPassiveWorldSave(save, finalRuntime),
                ["report"] = CompactPassiveWorldStatus(
                    finalRuntime, campaignId, timelineId)
            };
        }

        private static Dictionary<string, object> RunPassiveWorldStep(
            Dictionary<string, object> runtime,
            string campaignId,
            string operation,
            Dictionary<string, object> fields,
            int timeoutSeconds)
        {
            Dictionary<string, object> step = new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["operation"] = operation,
                ["timeoutSeconds"] = timeoutSeconds
            };
            foreach (KeyValuePair<string, object> field in fields)
                step[field.Key] = field.Value;

            Dictionary<string, object> started = Post("/tests/live/run/start",
                new Dictionary<string, object>
                {
                    ["schemaVersion"] = 2,
                    ["campaignId"] = campaignId,
                    ["gameInstanceId"] = RuntimeInstance(runtime),
                    ["mode"] = "passive_world",
                    ["presentation"] = "headless",
                    ["effects"] = "guarded",
                    ["label"] = "Passive world " + operation,
                    ["autoCompleteWhenIdle"] = true,
                    ["steps"] = new object[] { step }
                });
            if (!IsOk(started)) return started;
            return WaitForRun(campaignId, String(started, "runId"), "",
                timeoutSeconds + 30, true);
        }

        private static Dictionary<string, object> WaitForPassiveWorldCatchUp(
            string campaignId,
            string timelineId,
            double targetDay,
            int timeoutSeconds,
            bool allowMissingInitialBaselineRollup = false)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            Dictionary<string, object> latest = new Dictionary<string, object>();
            Dictionary<string, object> latestRuntime = new Dictionary<string, object>();
            int stable = 0;
            var gateTimer = System.Diagnostics.Stopwatch.StartNew();
            var blockedMs = new Dictionary<string, object>();
            long previousObservationMs = 0;
            string previousObservationId = "", previousGeneration = "";
            while (DateTime.UtcNow < deadline)
            {
                latest = Get("/world-test/overview?campaignId="
                    + Uri.EscapeDataString(campaignId)
                    + "&timelineId=" + Uri.EscapeDataString(timelineId)
                    + "&view=readiness");
                string observationId = String(latest, "observationId");
                string generation = String(latest, "campaignGeneration");
                if (!IsOk(latest) || String(latest, "schema") != "reign_world_readiness_v1"
                    || String(latest, "campaignId") != campaignId || String(latest, "timelineId") != timelineId
                    || string.IsNullOrWhiteSpace(observationId) || observationId == previousObservationId
                    || string.IsNullOrWhiteSpace(generation))
                {
                    stable = 0;
                    blockedMs["invalidReadiness"] = ReadLong(blockedMs, "invalidReadiness", 0)
                        + gateTimer.ElapsedMilliseconds - previousObservationMs;
                    previousObservationMs = gateTimer.ElapsedMilliseconds;
                    Thread.Sleep(2000);
                    continue;
                }
                if (generation != previousGeneration) stable = 0;
                previousObservationId = observationId;
                previousGeneration = generation;
                Dictionary<string, object> clocks = ReadObject(latest, "clocks");
                Dictionary<string, object> relationships = ReadObject(latest, "relationships");
                Dictionary<string, object> nativeSync =
                    ReadObject(relationships, "nativeSync");
                Dictionary<string, object> overviewDiplomacy =
                    ReadObject(latest, "diplomacy");
                Dictionary<string, object> politicalPressure =
                    ReadObject(latest, "politicalPressures");
                Dictionary<string, object> clanConflicts =
                    ReadObject(latest, "clanConflicts");
                latestRuntime = Get("/tests/live/runtime?campaignId="
                    + Uri.EscapeDataString(campaignId));
                Dictionary<string, object> nativeRuntime =
                    ReadObject(latestRuntime, "runtime");
                string actionFailure = PassiveWorldCheckpointPolicy.ActionFailureError(
                    ReadLong(ReadObject(latest, "pipeline"), "failedActions", -1));
                if (!string.IsNullOrEmpty(actionFailure))
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["status"] = "action_health_failed",
                        ["error"] = actionFailure,
                        ["targetWorldDay"] = targetDay,
                        ["runtime"] = CompactPassiveWorldRuntime(latestRuntime),
                        ["checkpoint"] = PassiveWorldCheckpointSummary(latest)
                    };
                }
                Dictionary<string, object> diplomacy =
                    ReadObject(nativeRuntime, "diplomacy");
                Dictionary<string, object> worldHistory =
                    ReadObject(nativeRuntime, "worldHistory");
                long undeliveredAnnouncements = ReadLong(
                    overviewDiplomacy, "undeliveredAnnouncements", 0);
                long unacknowledgedAnnouncements = ReadLong(
                    overviewDiplomacy, "unacknowledgedAnnouncements", 0);
                long nativeEffectsPending = ReadLong(
                    politicalPressure, "nativeEffectsPending", -1);
                long nativeEffectsFailed = ReadLong(
                    politicalPressure, "nativeEffectsFailed", -1);
                long clanConflictEffectsPending = ReadLong(
                    clanConflicts, "relationshipEffectsPending", -1);
                long clanConflictEffectsFailed = ReadLong(
                    clanConflicts, "relationshipEffectsFailed", -1);
                long clanConflictIncidentFailures = ReadLong(
                    clanConflicts, "incidentFailures", -1);
                long pendingClanConflictNotices = ReadLong(
                    clanConflicts, "pendingNotices", -1);
                if (nativeEffectsFailed > 0)
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["status"] = "political_pressure_effect_failed",
                        ["error"] = nativeEffectsFailed
                            + " political-pressure native effect(s) failed; catch-up stopped for repair.",
                        ["targetWorldDay"] = targetDay,
                        ["runtime"] = CompactPassiveWorldRuntime(latestRuntime),
                        ["checkpoint"] = PassiveWorldCheckpointSummary(latest)
                    };
                }
                if (clanConflictEffectsFailed > 0 || clanConflictIncidentFailures > 0)
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["status"] = "clan_conflict_failed",
                        ["error"] = clanConflictEffectsFailed
                            + " clan-conflict relationship effect(s) and "
                            + clanConflictIncidentFailures
                            + " incident(s) failed; catch-up stopped for repair.",
                        ["targetWorldDay"] = targetDay,
                        ["runtime"] = CompactPassiveWorldRuntime(latestRuntime),
                        ["checkpoint"] = PassiveWorldCheckpointSummary(latest)
                    };
                }
                if (nativeEffectsPending > 0 || pendingClanConflictNotices > 0)
                {
                    long drainStartedMs = gateTimer.ElapsedMilliseconds;
                    Dictionary<string, object> drained = RunPassiveWorldStep(
                        latestRuntime, campaignId,
                        "world_drain_political_pressure",
                        new Dictionary<string, object>(), 120);
                    if (!IsOk(drained))
                    {
                        return new Dictionary<string, object>
                        {
                            ["ok"] = false,
                            ["status"] = "political_pressure_drain_failed",
                            ["error"] = "The paused campaign could not drain pending political-pressure effects.",
                            ["drain"] = CompactPassiveWorldRun(drained),
                            ["targetWorldDay"] = targetDay,
                            ["runtime"] = CompactPassiveWorldRuntime(latestRuntime),
                            ["checkpoint"] = PassiveWorldCheckpointSummary(latest)
                        };
                    }
                    stable = 0;
                    Thread.Sleep(1000);
                    blockedMs["nativeDrainCommands"] = ReadLong(blockedMs, "nativeDrainCommands", 0)
                        + gateTimer.ElapsedMilliseconds - drainStartedMs;
                    previousObservationMs = gateTimer.ElapsedMilliseconds;
                    continue;
                }
                if (undeliveredAnnouncements > 0
                    || unacknowledgedAnnouncements > 0)
                {
                    long acknowledgmentStartedMs = gateTimer.ElapsedMilliseconds;
                    RunPassiveWorldStep(latestRuntime, campaignId,
                        "world_acknowledge_diplomacy_announcement",
                        new Dictionary<string, object>(), 120);
                    stable = 0;
                    Thread.Sleep(1000);
                    blockedMs["announcementCommands"] = ReadLong(blockedMs, "announcementCommands", 0)
                        + gateTimer.ElapsedMilliseconds - acknowledgmentStartedMs;
                    previousObservationMs = gateTimer.ElapsedMilliseconds;
                    continue;
                }
                double requiredDay = RequiredCompletedPassiveWorldDay(targetDay);
                double rollupDay = ReadDoubleValue(clocks, "rollupDay", -1d);
                bool initialBaselineWithoutRollup = allowMissingInitialBaselineRollup
                    && rollupDay < 0d
                    && ReadDoubleValue(latest, "latestObservedDay", -1d) >= targetDay - 0.000001d
                    && ReadDoubleValue(clocks, "nativeConfirmedDay", -1d) >= requiredDay;
                bool caughtUp = IsOk(latest)
                    && String(latest, "schema") == "reign_world_readiness_v1"
                    && ReadDoubleValue(clocks, "ingestedDay", -1d) >= requiredDay
                    && ReadDoubleValue(clocks, "completedRelationshipDay", -1d) >= requiredDay
                    && (rollupDay >= requiredDay || initialBaselineWithoutRollup)
                    && ReadLong(nativeSync, "pending", -1) == 0
                    && ReadLong(nativeSync, "failed", -1) == 0
                    && ReadLong(nativeSync, "overdue", -1) == 0
                    && ReadLong(nativeSync, "chemistryPending", -1) == 0
                    && ReadBoolean(latestRuntime, "gameOnline")
                    && CourtLifeRuntimeQuiescent(nativeRuntime)
                    && ClanAccordsRuntimeQuiescent(nativeRuntime)
                    && !ReadBoolean(diplomacy, "inFlight")
                    && nativeEffectsPending == 0
                    && nativeEffectsFailed == 0
                    && clanConflictEffectsPending == 0
                    && clanConflictEffectsFailed == 0
                    && clanConflictIncidentFailures == 0
                    && pendingClanConflictNotices == 0
                    && ReadLong(worldHistory, "pendingCount", -1) == 0
                    && !ReadBoolean(worldHistory, "uploadInFlight");
                long observationMs = gateTimer.ElapsedMilliseconds;
                long intervalMs = observationMs - previousObservationMs;
                previousObservationMs = observationMs;
                var blockers = new Dictionary<string, bool>
                {
                    ["inputIngestion"] = ReadDoubleValue(clocks, "ingestedDay", -1d) < requiredDay,
                    ["relationships"] = ReadDoubleValue(clocks, "completedRelationshipDay", -1d) < requiredDay,
                    ["rollups"] = rollupDay < requiredDay && !initialBaselineWithoutRollup,
                    ["nativeRelationships"] = ReadLong(nativeSync, "pending", -1) != 0 || ReadLong(nativeSync, "failed", -1) != 0 || ReadLong(nativeSync, "overdue", -1) != 0,
                    ["politicalPressure"] = nativeEffectsPending != 0 || nativeEffectsFailed != 0,
                    ["clanConflicts"] = clanConflictEffectsPending != 0 || clanConflictEffectsFailed != 0 || clanConflictIncidentFailures != 0 || pendingClanConflictNotices != 0,
                    ["courtLife"] = !CourtLifeRuntimeQuiescent(nativeRuntime),
                    ["clanAccords"] = !ClanAccordsRuntimeQuiescent(nativeRuntime),
                    ["diplomacy"] = ReadBoolean(diplomacy, "inFlight"),
                    ["worldHistory"] = ReadLong(worldHistory, "pendingCount", -1) != 0 || ReadBoolean(worldHistory, "uploadInFlight"),
                    ["nativeOffline"] = !ReadBoolean(latestRuntime, "gameOnline"),
                    ["stableObservations"] = caughtUp
                };
                foreach (var blocker in blockers.Where(x => x.Value))
                    blockedMs[blocker.Key] = ReadLong(blockedMs, blocker.Key, 0) + intervalMs;
                stable = caughtUp ? stable + 1 : 0;
                if (stable >= PassiveWorldStableObservationCount)
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["status"] = "caught_up",
                        ["catchUpMs"] = gateTimer.ElapsedMilliseconds,
                        ["blockedObservationMs"] = blockedMs,
                        ["requiredStableObservations"] = PassiveWorldStableObservationCount,
                        ["targetWorldDay"] = targetDay,
                        ["initialBaselineRollupExceptionApplied"] = initialBaselineWithoutRollup,
                        ["runtime"] = CompactPassiveWorldRuntime(latestRuntime),
                        ["checkpoint"] = PassiveWorldCheckpointSummary(latest)
                    };
                }
                Thread.Sleep(2000);
            }
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["status"] = "catch_up_timeout",
                ["catchUpMs"] = gateTimer.ElapsedMilliseconds,
                ["blockedObservationMs"] = blockedMs,
                ["error"] = "Passive systems and native diplomacy did not remain caught up for the required quiescence window before the timeout.",
                ["targetWorldDay"] = targetDay,
                ["runtime"] = CompactPassiveWorldRuntime(latestRuntime),
                ["checkpoint"] = PassiveWorldCheckpointSummary(latest)
            };
        }

        private static Dictionary<string, object> CompactPassiveWorldStatus(
            Dictionary<string, object> runtime,
            string campaignId,
            string timelineId)
        {
            Dictionary<string, object> overview = Get(
                "/world-test/overview?campaignId=" + Uri.EscapeDataString(campaignId)
                + "&timelineId=" + Uri.EscapeDataString(timelineId)
                + "&limit=1");
            return new Dictionary<string, object>
            {
                ["ok"] = IsOk(runtime) && IsOk(overview)
                    && string.IsNullOrEmpty(PassiveWorldCheckpointPolicy.ActionFailureError(
                        ReadLong(ReadObject(overview, "pipeline"), "failedActions", -1))),
                ["campaignId"] = campaignId,
                ["timelineId"] = timelineId,
                ["runtime"] = CompactPassiveWorldRuntime(runtime),
                ["checkpoint"] = PassiveWorldCheckpointSummary(overview)
            };
        }

        private static Dictionary<string, object> CompactPassiveWorldRuntime(
            Dictionary<string, object> value)
        {
            Dictionary<string, object> native = ReadObject(value, "runtime");
            Dictionary<string, object> saveSync = ReadObject(native, "saveSync");
            Dictionary<string, object> finalization = ReadObject(saveSync, "saveFinalization");
            Dictionary<string, object> diplomacy = ReadObject(native, "diplomacy");
            Dictionary<string, object> relationshipClient =
                ReadObject(native, "relationshipClient");
            Dictionary<string, object> history = ReadObject(native, "worldHistory");
            Dictionary<string, object> nativeSave = ReadObject(native, "nativeSave");
            return new Dictionary<string, object>
            {
                ["ok"] = IsOk(value),
                ["armed"] = ReadBoolean(value, "armed"),
                ["gameOnline"] = ReadBoolean(value, "gameOnline"),
                ["campaignId"] = String(native, "campaignId"),
                ["gameInstanceId"] = String(native, "gameInstanceId"),
                ["worldDay"] = ReadDoubleValue(native, "worldDay", -1d),
                ["activeSaveName"] = String(native, "activeSaveName"),
                ["activeRunId"] = String(native, "activeRunId"),
                ["activeMode"] = String(native, "activeMode"),
                ["busy"] = ReadBoolean(native, "busy"),
                ["courtLife"] = ReadObject(native, "courtLife"),
                ["clanAccords"] = ReadObject(native, "clanAccords"),
                ["saveSync"] = new Dictionary<string, object>
                {
                    ["alignmentPending"] = ReadBoolean(saveSync, "alignmentPending"),
                    ["ready"] = ReadBoolean(saveSync, "ready"),
                    ["timelineId"] = String(saveSync, "timelineId"),
                    ["saveFinalization"] = new Dictionary<string, object>
                    {
                        ["inFlight"] = ReadLong(finalization, "inFlight", -1),
                        ["requestedSaveName"] = String(finalization, "requestedSaveName"),
                        ["completedSaveName"] = String(finalization, "completedSaveName"),
                        ["result"] = String(finalization, "result"),
                        ["succeeded"] = ReadBoolean(finalization, "succeeded"),
                        ["uniqueStateCount"] = ReadLong(finalization, "uniqueStateCount", -1),
                        ["uniqueStateLimit"] = ReadLong(finalization, "uniqueStateLimit", -1),
                        ["remainingUniqueStates"] = ReadLong(finalization, "remainingUniqueStates", -1)
                    }
                },
                ["diplomacy"] = new Dictionary<string, object>
                {
                    ["inFlight"] = ReadBoolean(diplomacy, "inFlight")
                },
                ["relationshipClient"] = new Dictionary<string, object>
                {
                    ["clientOutboxDays"] = ReadLong(
                        relationshipClient, "clientOutboxDays", -1),
                    ["oldestClientOutboxDay"] = ReadLong(
                        relationshipClient, "oldestClientOutboxDay", -1),
                    ["latestClientOutboxDay"] = ReadLong(
                        relationshipClient, "latestClientOutboxDay", -1),
                    ["uploadInFlight"] = ReadBoolean(
                        relationshipClient, "uploadInFlight")
                },
                ["worldHistory"] = new Dictionary<string, object>
                {
                    ["pendingCount"] = ReadLong(history, "pendingCount", -1),
                    ["uploadInFlight"] = ReadBoolean(history, "uploadInFlight")
                },
                ["nativeSave"] = new Dictionary<string, object>
                {
                    ["sequence"] = ReadLong(nativeSave, "sequence", -1),
                    ["completedSequence"] = ReadLong(nativeSave, "completedSequence", -1),
                    ["stage"] = String(nativeSave, "stage"),
                    ["saveName"] = String(nativeSave, "saveName"),
                    ["succeeded"] = ReadBoolean(nativeSave, "succeeded"),
                    ["isSaving"] = ReadBoolean(nativeSave, "isSaving")
                }
            };
        }

        private static Dictionary<string, object> CompactPassiveWorldRun(
            Dictionary<string, object> value)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = IsOk(value),
                ["runId"] = String(value, "runId"),
                ["campaignId"] = String(value, "campaignId"),
                ["gameInstanceId"] = String(value, "gameInstanceId"),
                ["mode"] = String(value, "mode"),
                ["status"] = String(value, "status"),
                ["completedCommands"] = ReadLong(value, "completedCommands", -1),
                ["failedCommands"] = ReadLong(value, "failedCommands", -1),
                ["reportPath"] = String(value, "reportPath"),
                ["error"] = String(value, "error")
            };
        }

        private static Dictionary<string, object> CompactPassiveWorldSave(
            Dictionary<string, object> value,
            Dictionary<string, object> finalRuntime)
        {
            if (ReadBoolean(value, "skipped"))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = IsOk(value),
                    ["skipped"] = true
                };
            }
            Dictionary<string, object> result = LastCompletedCommandResult(value);
            if (result.Count == 0) result = value;
            return new Dictionary<string, object>
            {
                ["ok"] = IsOk(value),
                ["status"] = String(value, "status"),
                ["saveName"] = String(result, "saveName"),
                ["worldDay"] = ReadDoubleValue(result, "worldDay", -1d),
                ["error"] = FirstNonEmpty(String(result, "error"), String(value, "error")),
                ["nativeSave"] = ReadObject(result, "nativeSave"),
                ["saveFinalization"] = ReadObject(result, "saveFinalization"),
                ["runtime"] = CompactPassiveWorldRuntime(finalRuntime)
            };
        }

        private static Dictionary<string, object> LastCompletedCommandResult(
            Dictionary<string, object> value)
        {
            foreach (Dictionary<string, object> command in
                ReadObjects(value, "commands").AsEnumerable().Reverse())
            {
                Dictionary<string, object> result = ReadObject(command, "result");
                if (result.Count > 0) return result;
            }
            return new Dictionary<string, object>();
        }

        private static Dictionary<string, object> WaitForPassiveWorldPostSaveRuntime(
            string campaignId,
            string saveName)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(45);
            DateTime? quiescentSinceUtc = null;
            Dictionary<string, object> latest = new Dictionary<string, object>();
            do
            {
                latest = Runtime(new[] { "status", "--campaign", campaignId });
                if (PassiveWorldPostSaveRuntimeQuiesced(latest, saveName))
                {
                    if (!quiescentSinceUtc.HasValue)
                        quiescentSinceUtc = DateTime.UtcNow;
                    else if (DateTime.UtcNow - quiescentSinceUtc.Value
                        >= TimeSpan.FromSeconds(5))
                        return latest;
                }
                else
                {
                    quiescentSinceUtc = null;
                }
                Thread.Sleep(500);
            }
            while (DateTime.UtcNow < deadline);
            return latest;
        }

        private static bool PassiveWorldPostSaveRuntimeQuiesced(
            Dictionary<string, object> value,
            string saveName)
        {
            Dictionary<string, object> runtime = ReadObject(value, "runtime");
            Dictionary<string, object> nativeSave = ReadObject(runtime, "nativeSave");
            Dictionary<string, object> saveSync = ReadObject(runtime, "saveSync");
            Dictionary<string, object> diplomacy = ReadObject(runtime, "diplomacy");
            Dictionary<string, object> relationshipClient =
                ReadObject(runtime, "relationshipClient");
            Dictionary<string, object> worldHistory =
                ReadObject(runtime, "worldHistory");
            Dictionary<string, object> finalization =
                ReadObject(saveSync, "saveFinalization");
            return IsOk(value)
                && ReadBoolean(value, "gameOnline")
                && !ReadBoolean(runtime, "busy")
                && CourtLifeRuntimeQuiescent(runtime)
                && ClanAccordsRuntimeQuiescent(runtime)
                && !ReadBoolean(saveSync, "alignmentPending")
                && ReadBoolean(saveSync, "ready")
                && !ReadBoolean(diplomacy, "inFlight")
                && ReadLong(relationshipClient, "clientOutboxDays", -1) == 0
                && !ReadBoolean(relationshipClient, "uploadInFlight")
                && ReadLong(worldHistory, "pendingCount", -1) == 0
                && !ReadBoolean(worldHistory, "uploadInFlight")
                && !ReadBoolean(nativeSave, "isSaving")
                && (ReadLong(finalization, "inFlight", -1) == 0)
                && ReadBoolean(finalization, "succeeded")
                && string.Equals(String(finalization, "completedSaveName"),
                    saveName, StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> PassiveWorldCheckpointSummary(
            Dictionary<string, object> overview)
        {
            return new Dictionary<string, object>
            {
                ["overallStatus"] = String(overview, "overallStatus"),
                ["latestObservedDay"] = ReadDoubleValue(overview, "latestObservedDay", -1d),
                ["clocks"] = ReadObject(overview, "clocks"),
                ["relationshipStatus"] = String(ReadObject(overview, "relationships"), "status"),
                ["diplomacyStatus"] = String(ReadObject(overview, "diplomacy"), "status"),
                ["clanConflictStatus"] = String(ReadObject(overview, "clanConflicts"), "status"),
                ["rumorStatus"] = String(ReadObject(overview, "rumors"), "status"),
                ["rebellionStatus"] = String(ReadObject(overview, "rebellions"), "status"),
                ["pipelineStatus"] = String(ReadObject(overview, "pipeline"), "status"),
                ["failedActions"] = ReadLong(ReadObject(overview, "pipeline"), "failedActions", -1)
            };
        }

        private static bool CourtLifeRuntimeQuiescent(Dictionary<string, object> runtime)
        {
            Dictionary<string, object> courtLife = ReadObject(runtime, "courtLife");
            return String(courtLife, "schema") == "reign-court-life-runtime-v1"
                && courtLife.ContainsKey("deliveryPending") && !ReadBoolean(courtLife, "deliveryPending")
                && courtLife.ContainsKey("internationalPending") && !ReadBoolean(courtLife, "internationalPending")
                && !ReadBoolean(courtLife, "familyAttentionPending")
                && ReadLong(courtLife, "familyVisitOutbox", -1) == 0;
        }

        private static bool ClanAccordsRuntimeQuiescent(Dictionary<string, object> runtime)
        {
            Dictionary<string, object> accords = ReadObject(runtime, "clanAccords");
            return String(accords, "schema") == "reign-clan-accords-runtime-v1"
                && ReadBoolean(accords, "available")
                && accords.ContainsKey("pending") && !ReadBoolean(accords, "pending");
        }

        private static double DoubleValue(string[] args, string name, double fallback)
        {
            return double.TryParse(Value(args, name, ""), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double value) ? value : fallback;
        }

        private static double ReadDoubleValue(
            Dictionary<string, object> value, string key, double fallback)
        {
            if (value == null || !value.TryGetValue(key, out object raw) || raw == null)
                return fallback;
            try { return Convert.ToDouble(raw, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static double RequiredCompletedPassiveWorldDay(double targetDay)
        {
            return Math.Max(0d,
                Math.Floor(targetDay + CampaignDayBoundaryEpsilon) - 1d);
        }
    }
}
