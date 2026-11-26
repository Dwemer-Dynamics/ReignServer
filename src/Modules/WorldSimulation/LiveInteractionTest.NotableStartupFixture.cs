#if !REIGN_EXCLUDE_VERIFICATION
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Reign.Core.Contracts.Actions;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationService
    {
        private const string NotableStartupFixtureAction = "fixture.notable_startup_reservation";

        internal static Dictionary<string, object> LiveTestNotableStartupFixtureApi(Dictionary<string, object> payload)
        {
            string campaign = ReadString(payload, "campaignId", "");
            string phase = ReadString(payload, "phase", "");
            if (!TerminalActionReportProtocol.IsTrustedIdentifier(campaign) || phase is not ("admit" or "reserve"))
                return LiveTestError("The notable fixture requires a trusted campaign and an exact fixture phase.");

            lock (LiveTestLock)
            {
                var run = FindActiveLiveTestRun(campaign);
                var enrollment = ReadDictionary(run, "enrollment");
                var commands = ReadDictionaryList(run, "commands");
                var command = commands.Count == 1 ? commands[0] : null;
                var testCase = ReadDictionary(command, "testCase");
                string instance = ReadString(payload, "gameInstanceId", "");
                string process = ReadString(payload, "clientProcessInstanceId", "");
                string lease = ReadString(payload, "leaseId", "");
                string point = ReadString(enrollment, "checkpointSavePointId", "");
                string save = ReadString(enrollment, "disposableSaveName", "");
                string timeline = ReadString(enrollment, "timelineId", "");
                var runtime = LoadLiveTestRuntime(campaign);
                var sync = ReadDictionary(runtime, "saveSync");
                if (!IsLiveTestArmed(CampaignFileService.ReadJsonObject(LiveTestArmPath()))
                    || ReadString(run, "mode", "") != "structured_actions"
                    || ReadString(run, "runId", "") != ReadString(payload, "runId", "")
                    || !StructuredActionEnrollmentValid(enrollment, campaign)
                    || !ReadBool(enrollment, "recoveryCheckpointVerified", false)
                    || !Guid.TryParseExact(point, "N", out _)
                    || !IsNotableObservationDigest(ReadString(enrollment, "checkpointSha256", "").ToLowerInvariant())
                    || ReadString(testCase, "actionId", "") != NotableStartupFixtureAction
                    || ReadString(testCase, "phase", "") != "fixture" || ReadString(testCase, "kind", "") != "fixture"
                    || ReadString(command, "operation", "") != "structured_action_test"
                    || ReadString(command, "status", "") is not ("accepted" or "running")
                    || ReadString(command, "commandId", "") != ReadString(payload, "commandId", "")
                    || !Guid.TryParseExact(lease, "N", out _) || ReadString(command, "leaseId", "") != lease
                    || ParseLiveTestUtc(ReadString(command, "leaseExpiresUtc", "")) <= DateTimeOffset.UtcNow
                    || instance.Length == 0 || ReadString(run, "gameInstanceId", "") != instance
                    || !IsFreshLiveTestRuntime(runtime) || ReadString(runtime, "gameInstanceId", "") != instance
                    || !Guid.TryParseExact(process, "N", out _)
                    || ReadString(ReadLiveTestExecutionIdentity(runtime), "clientProcessInstanceId", "") != process
                    || ReadString(runtime, "activeSaveName", "") != save || ReadString(sync, "timelineId", "") != timeline
                    || !ReadBool(sync, "ready", false) || ReadBool(sync, "alignmentPending", true)
                    || !NotableFixtureProviderGuardMatches(enrollment))
                    return LiveTestError("The fixture requires the active accepted lease, verified Current, and the matching zero-provider guard.");

                // Read and mutate only under the actual timeline. Avoid blocking
                // behind another owner while the live-test ledger is locked.
                var gate = WorldHistoryTimelineLocks.GetOrAdd(WorldHistoryTimelineScopeKey(campaign), _ => new object());
                if (!Monitor.TryEnter(gate)) return LiveTestError("The campaign timeline is busy. Restore the fixture checkpoint before another attempt.");
                try
                {
                    var owner = PersistenceService.ReadRegisteredEditorNativeOwner(campaign);
                    if (owner == null || owner.TimelineId != timeline || owner.LoadPointId != point)
                        return LiveTestError("Load the registered run-owned recovery checkpoint before starting the fixture.");
                    string generationSha256 = NotableObservationDigest(owner.RestoreGeneration);
                    var load = ReadDictionary(enrollment, "checkpointLoadOwner");
                    if (ReadString(load, "schema", "") != "reign-native-verified-current-observation-v1"
                        || ReadString(load, "campaignId", "") != campaign || ReadString(load, "timelineId", "") != timeline
                        || ReadString(load, "saveName", "") != save || ReadString(load, "savePointId", "") != point
                        || ReadString(load, "gameInstanceId", "") != instance || ReadString(load, "clientProcessInstanceId", "") != process
                        || !string.Equals(ReadString(load, "loadSessionSha256", ""), NotableObservationDigest(owner.LoadSessionId), StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(ReadString(load, "restoreGenerationSha256", ""), generationSha256, StringComparison.OrdinalIgnoreCase))
                        return LiveTestError("The verified native load owner changed after the controller observation.");

                    string admitted = ReadString(command, "notableFixtureLease", "");
                    if (phase == "admit")
                    {
                        if (admitted.Length != 0) return LiveTestError("The fixture already admitted a spawn. Restore the checkpoint before retrying.");
                        command["notableFixtureLease"] = lease;
                        command["notableFixtureGenerationSha256"] = generationSha256;
                        SaveLiveTestRun(run);
                        return TestDict("ok", true);
                    }

                    string hero = ReadString(payload, "heroId", "");
                    if (admitted != lease || ReadString(command, "notableFixtureGenerationSha256", "") != generationSha256
                        || ReadBool(command, "notableFixtureReservationAttempted", false)
                        || !TerminalActionReportProtocol.IsTrustedIdentifier(hero))
                        return LiveTestError("The fixture has no unused admission for this lease and generation.");
                    command["notableFixtureReservationAttempted"] = true;
                    SaveLiveTestRun(run);
                    var result = RelationshipsService.StageNotableStartupFixture(campaign, hero, ReadDouble(payload, "worldDay", double.NaN),
                        timeline, owner.RestoreGeneration, Guid.NewGuid().ToString("N"));
                    if (result == null) return LiveTestError("No fresh, unowned Notable reservation was made. Restore the verified checkpoint.");
                    result["ok"] = true;
                    return result;
                }
                finally { Monitor.Exit(gate); }
            }
        }

        private static bool NotableFixtureProviderGuardMatches(Dictionary<string, object> enrollment)
        {
            var snapshot = Program.ProviderTestGuard.Snapshot();
            if (!ReadBool(snapshot, "cleanProviderFreeRun", false)
                || snapshot.GetValueOrDefault("owner") is not Dictionary<string, string> guard) return false;
            return guard.GetValueOrDefault("campaignId") == ReadString(enrollment, "campaignId", "")
                && guard.GetValueOrDefault("runId") == ReadString(enrollment, "campaignTestRunId", "")
                && guard.GetValueOrDefault("timelineId") == ReadString(enrollment, "timelineId", "")
                && guard.GetValueOrDefault("currentSaveName") == ReadString(enrollment, "disposableSaveName", "")
                && guard.GetValueOrDefault("baselineSaveName") == ReadString(enrollment, "protectedBaselineSaveName", "")
                && IsNotableObservationDigest(guard.GetValueOrDefault("baselineSha256") ?? "");
        }
    }
}
#endif
