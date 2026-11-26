using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Reign.Core.Contracts.Actions;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationService
    {
        private const string NotableStartupObservationMode = "notable_startup_observation";
        private const string NotableStartupObservationSource = "reign_mcp_notable_startup_observation";
        private sealed record NotableStartupObservationBasis(string Digest, string ExpectedTraits,
            string LoadSessionSha256, string RestoreGenerationSha256);
        private static readonly HashSet<string> NotableStartupEnvelopeFields = new(StringComparer.Ordinal)
        {
            "schemaVersion", "campaignId", "gameInstanceId", "runId", "mode", "presentation",
            "effects", "autoCompleteWhenIdle", "enrollment", "steps"
        };
#if !REIGN_EXCLUDE_VERIFICATION
        [ThreadStatic] private static Func<string, NotableStartupObservationBasis> NotableStartupBasisForTests;
#endif

        private static bool IsNotableStartupObservationOperation(Dictionary<string, object> command) =>
            ReadString(command, "operation", ReadString(command, "op", "")).Trim()
                .Equals(NotableStartupObservationMode, StringComparison.OrdinalIgnoreCase);

        private static bool NotableStartupObservationPlanAllowed(Dictionary<string, object> payload, bool isObservation)
        {
            if (!isObservation)
                return !ReadDictionaryList(payload, "steps").Any(IsNotableStartupObservationOperation);
            return payload.Keys.All(NotableStartupEnvelopeFields.Contains)
                && ExactNotableObservationText(payload, "mode") == NotableStartupObservationMode
                && ExactNotableObservationText(payload, "effects") == "observed"
                && ExactNotableObservationText(payload, "presentation") == "headless"
                && payload.TryGetValue("autoCompleteWhenIdle", out var complete) && complete is true
                && payload.TryGetValue("steps", out var raw) && raw is IList { Count: 1 } steps
                && steps[0] is Dictionary<string, object> { Count: 1 } command
                && ExactNotableObservationText(command, "operation") == NotableStartupObservationMode;
        }

        private static NotableStartupObservationBasis ReadNotableStartupObservationBasis(string campaign,
            string instance, Dictionary<string, object> scope, out bool timelineBusy)
        {
            timelineBusy = false;
            if (!(Program.CampaignDataGate.IsReadLockHeld || Program.CampaignDataGate.IsWriteLockHeld))
                return null;
            string save = ExactNotableObservationText(scope, "disposableSaveName");
            string baseline = ExactNotableObservationText(scope, "protectedBaselineSaveName");
            string point = ExactNotableObservationText(scope, "savePointId");
            string timeline = ExactNotableObservationText(scope, "timelineId");
            string hero = ExactNotableObservationText(scope, "heroId");
            string client = ExactNotableObservationText(scope, "clientProcessInstanceId");
            string reportedOrigin = ExactNotableObservationText(scope, "origin");
            if (scope?.Count != 12 || ExactNotableObservationText(scope, "source") != NotableStartupObservationSource
                || ExactNotableObservationText(scope, "campaignId") != campaign || instance.Length == 0
                || instance != ExactNotableObservationText(scope, "gameInstanceId")
                || !TerminalActionReportProtocol.IsTrustedIdentifier(ExactNotableObservationText(scope, "campaignTestRunId"))
                || !TerminalActionReportProtocol.IsTrustedIdentifier(hero)
                || !TerminalActionReportProtocol.IsTrustedIdentifier(timeline)
                || !Guid.TryParseExact(point, "N", out _) || !Guid.TryParseExact(client, "N", out _)
                || !Uri.TryCreate(reportedOrigin, UriKind.Absolute, out Uri origin) || !origin.IsLoopback
                || origin.Scheme != Uri.UriSchemeHttp || origin.GetLeftPart(UriPartial.Authority) != reportedOrigin
                || !IsNotableObservationDigest(ExactNotableObservationText(scope, "checkpointSha256"))
                || save.Length > 64 || !save.StartsWith("ReignTest_", StringComparison.Ordinal)
                || !save.EndsWith("_Current", StringComparison.Ordinal) || baseline.Length == 0
                || save.Equals(baseline, StringComparison.OrdinalIgnoreCase))
                return null;

            var runtime = LoadLiveTestRuntime(campaign);
            var sync = ReadDictionary(runtime, "saveSync");
            if (!IsFreshLiveTestRuntime(runtime)
                || ReadString(runtime, "campaignId", "") != campaign || ReadString(runtime, "gameInstanceId", "") != instance
                || ReadString(runtime, "activeSaveName", "") != save || ReadString(sync, "timelineId", "") != timeline
                || !ReadBool(sync, "ready", false) || ReadBool(sync, "alignmentPending", true)
                || ReadString(ReadDictionary(runtime, "executionIdentity"), "clientProcessInstanceId", "") != client)
                return null;

            // LiveTestLock is already held. Do not suspend the campaign gate while
            // waiting for another timeline owner. A busy timeline yields no evidence.
            PersistenceService.ThrowIfCampaignRequestReplaced();
            var gate = WorldHistoryTimelineLocks.GetOrAdd(WorldHistoryTimelineScopeKey(campaign), _ => new object());
            if (!System.Threading.Monitor.TryEnter(gate))
            {
                timelineBusy = true;
                return null;
            }
            try
            {
#if !REIGN_EXCLUDE_VERIFICATION
                if (NotableStartupBasisForTests != null && ReignPostgreSqlStorage.Options.IsValidation
                    && CampaignFileService.IsInternalCampaignId(campaign))
                    return NotableStartupBasisForTests(campaign);
#endif
                var rollback = PersistenceService.ReadRegisteredEditorNativeRollback(campaign, save);
                if (rollback == null || rollback.TimelineId != timeline || rollback.LoadPointId != point)
                    return null;
                var retained = RelationshipsService.ReadRetainedNotableStartupObservation(
                    campaign, hero, timeline, rollback.RestoreGeneration);
                if (retained == null) return null;
                string session = NotableObservationDigest(rollback.LoadSessionId);
                string restore = NotableObservationDigest(rollback.RestoreGeneration);
                return new NotableStartupObservationBasis(
                    NotableObservationDigest(Json.Serialize(new[] {
                        retained.BasisSha256, rollback.LineageId, session, restore, CanonicalJson(scope)
                    })), retained.ExpectedTraits, session, restore);
            }
            finally { System.Threading.Monitor.Exit(gate); }
        }

        private static NotableStartupObservationBasis ReadCurrentNotableStartupBasis(
            Dictionary<string, object> run, out bool timelineBusy)
        {
            timelineBusy = false;
            if (!IsLiveTestArmed(CampaignFileService.ReadJsonObject(LiveTestArmPath()))) return null;
            var basis = ReadNotableStartupObservationBasis(ReadString(run, "campaignId", ""),
                ReadString(run, "gameInstanceId", ""), ReadDictionary(run, "enrollment"), out timelineBusy);
            return basis?.Digest == ReadString(run, "notableStartupBasisSha256", "") ? basis : null;
        }

        private static bool CloseUnavailableNotableStartupObservation(Dictionary<string, object> run, out bool timelineBusy)
        {
            timelineBusy = false;
            if (run == null || ReadString(run, "mode", "") != NotableStartupObservationMode
                || LiveTestTerminalRunStates.Contains(ReadString(run, "status", ""))
                || !(Program.CampaignDataGate.IsReadLockHeld || Program.CampaignDataGate.IsWriteLockHeld))
                return false;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            bool expired = ParseLiveTestUtc(ReadString(run, "startedUtc", ""))
                .AddSeconds(2 * LiveTestLeaseSeconds + 30) <= now;
            if (!expired && (ReadCurrentNotableStartupBasis(run, out timelineBusy) != null || timelineBusy))
                return false;

            string utc = now.ToString("o");
            const string message = "The exact notable startup observation expired or lost its registered rollback basis.";
            var command = FirstLiveTestCommandInSequence(run);
            if (command != null)
            {
                command["status"] = "interrupted";
                command["message"] = message;
                command["updatedUtc"] = utc;
                command["completedUtc"] = utc;
                command["result"] = NotableStartupObservationResult(run, command);
            }
            run["status"] = "interrupted";
            run["message"] = message;
            run["updatedUtc"] = utc;
            run["completedUtc"] = utc;
            SaveLiveTestRun(run);
            return true;
        }

        private static bool EvaluateNotableStartupObservation(Dictionary<string, object> run,
            Dictionary<string, object> command, Dictionary<string, object> execution,
            Dictionary<string, object> result, out bool timelineBusy)
        {
            var basis = ReadCurrentNotableStartupBasis(run, out timelineBusy);
            var scope = ReadDictionary(command, "notableStartupScope");
            var issued = ReadDictionary(execution, "executionObservation");
            if (basis == null || !MatchesNotableObservationExecution(command, execution)
                || ExactNotableObservationText(scope, "loadSessionSha256") != basis.LoadSessionSha256
                || ExactNotableObservationText(scope, "restoreGenerationSha256") != basis.RestoreGenerationSha256
                || ExactNotableObservationText(issued, "serverProcessInstanceId") != LiveTestServerProcessInstanceId
                || ExactNotableObservationText(issued, "clientProcessInstanceId") != ExactNotableObservationText(scope, "clientProcessInstanceId")
                || !MatchesNotableStartupNativeReadback(scope, result, basis.ExpectedTraits))
                return false;
            command["notableStartupObservationVerified"] = true;
            return true;
        }

        private static Dictionary<string, object> NotableStartupObservationResult(
            Dictionary<string, object> run, Dictionary<string, object> command) =>
            TestDict("disposition", ReadBool(command, "notableStartupObservationVerified", false) ? "current_traits_match" : "unverified",
                "diagnosticOnly", true, "heroId", ReadString(ReadDictionary(run, "enrollment"), "heroId", ""));

        private static bool MatchesNotableObservationExecution(Dictionary<string, object> command,
            Dictionary<string, object> execution)
        {
            var accepted = ReadDictionary(command, "notableStartupAcceptedExecution");
            var issued = ReadDictionary(execution, "executionObservation");
            string lease = ExactNotableObservationText(issued, "leaseId");
            string client = ExactNotableObservationText(issued, "clientProcessInstanceId");
            return ExactNotableObservationText(execution, "status") == "pending_artifact_review"
                && Guid.TryParseExact(lease, "N", out _) && Guid.TryParseExact(client, "N", out _)
                && ExactNotableObservationText(accepted, "leaseId") == lease
                && ExactNotableObservationText(accepted, "clientProcessInstanceId") == client
                && TryNotableObservationInt(accepted, "retryGeneration", out int acceptedRetry)
                && TryNotableObservationInt(issued, "executionRetryGeneration", out int issuedRetry)
                && acceptedRetry == issuedRetry && issuedRetry >= 0;
        }

        private static bool MatchesNotableStartupNativeReadback(Dictionary<string, object> scope,
            Dictionary<string, object> result, string expected)
        {
            if (result?.Count != 11 || ExactNotableObservationText(result, "loadResult") != "rolled_back"
                || ExactNotableObservationText(result, "timeMode") != "Stop"
                || !TryNotableObservationInt(result, "coordinatorGeneration", out int observed) || observed < 1)
                return false;
            string[] fields = { "heroId", "gameInstanceId", "clientProcessInstanceId", "savePointId",
                "loadSessionSha256", "restoreGenerationSha256", "origin" };
            if (fields.Any(key => ExactNotableObservationText(result, key).Length == 0
                || ExactNotableObservationText(result, key) != ExactNotableObservationText(scope, key)))
                return false;

            var traits = ReadDictionary(result, "traits");
            return CharactersService.HasExactAutomaticNotableTraits(traits) && CanonicalJson(traits) == expected;
        }

        private static string ExactNotableObservationText(Dictionary<string, object> scope, string key) =>
            scope?.TryGetValue(key, out var raw) == true && raw is string text ? text : "";

        private static bool TryNotableObservationInt(Dictionary<string, object> scope, string key, out int value)
        {
            value = 0;
            if (scope == null || !scope.TryGetValue(key, out var raw)) return false;
            if (raw is int number) value = number;
            else if (raw is long integer && integer >= int.MinValue && integer <= int.MaxValue) value = (int)integer;
            else return false;
            return true;
        }

        private static bool IsNotableObservationDigest(string value) =>
            value.Length == 64 && value.All(ch => ch is >= 'a' and <= 'f' or >= '0' and <= '9');

        private static string NotableObservationDigest(string value) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
