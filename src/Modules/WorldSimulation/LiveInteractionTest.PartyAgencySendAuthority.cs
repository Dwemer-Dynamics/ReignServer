using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationService
    {
        internal const string PartyAgencySendOriginField = "partyAgencySendOrigin";

        internal sealed record PartyAgencySendClaim(string RunId, string CommandId, string GameInstanceId,
            string ClientProcessInstanceId, string ExecutionLeaseId, int RetryGeneration);

        // Holds an observed execution scope only. It carries no provider or loaded artifact verdict.
        internal sealed record PartyAgencySendOrigin(PartyAgencySendClaim Execution, string CampaignId,
            string CampaignTestRunId, string CertificationRunId, string Mode, string SpeakerId,
            string CorrelationId, string PlayerContributionSha256)
        {
            internal Dictionary<string, object> AcceptedReceipt(string unitId, string responseHash) =>
                TestDict("schema", "reign-party-agency-send-origin-v1", "status", "execution_observed",
                    "campaignId", CampaignId, "campaignTestRunId", CampaignTestRunId,
                    "certificationRunId", CertificationRunId, "runId", Execution.RunId,
                    "commandId", Execution.CommandId, "gameInstanceId", Execution.GameInstanceId,
                    "clientProcessInstanceId", Execution.ClientProcessInstanceId,
                    "executionLeaseId", Execution.ExecutionLeaseId, "retryGeneration", Execution.RetryGeneration,
                    "mode", Mode, "speakerHeroStringId", SpeakerId, "correlationId", CorrelationId,
                    "playerContributionSha256", PlayerContributionSha256, "acceptedUnitId", unitId,
                    "acceptedResponseHash", responseHash);
        }

        private static string ExactSendText(Dictionary<string, object> data, string key) =>
            data != null && data.TryGetValue(key, out var value) && value is string text ? text : "";

        internal static PartyAgencySendClaim TakePartyAgencySendClaim(Dictionary<string, object> payload)
        {
            if (payload == null) return null;
            var names = payload.Keys.Where(key => key.Equals(PartyAgencySendOriginField,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            var wire = names.Length == 1 && names[0] == PartyAgencySendOriginField
                ? ReadDictionary(payload, names[0]) : null;
            foreach (var name in names) payload.Remove(name);

            if (wire == null || wire.Count != 7 || !HasLiveTestExecutionVersion(wire)
                || !wire.TryGetValue("retryGeneration", out var attempt) || attempt is not int generation || generation < 0)
                return null;
            string runId = ExactSendText(wire, "runId");
            string commandId = ExactSendText(wire, "commandId");
            string instance = ExactSendText(wire, "gameInstanceId");
            string process = ExactSendText(wire, "clientProcessInstanceId");
            string lease = ExactSendText(wire, "executionLeaseId");
            if (new[] { runId, commandId, instance }.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 160)
                || !Guid.TryParseExact(process, "N", out _) || !Guid.TryParseExact(lease, "N", out _))
                return null;
            return new PartyAgencySendClaim(runId, commandId, instance, process, lease, generation);
        }

        internal static PartyAgencySendOrigin AdmitPartyAgencySendOrigin(Dictionary<string, object> payload,
            string routeMode, PartyAgencySendClaim claim)
        {
            string campaign = ExactSendText(payload, "campaignId");
            if (claim == null || string.IsNullOrWhiteSpace(campaign) || campaign.Length > 160) return null;
            lock (LiveTestLock)
            {
                if (!IsLiveTestArmed(CampaignFileService.ReadJsonObject(LiveTestArmPath()))
                    || !IsCurrentPartyAgencySendRuntime(campaign, claim))
                    return null;
                return BindPartyAgencySendOrigin(LoadLiveTestRun(campaign, claim.RunId), payload, routeMode, claim);
            }
        }

        internal static bool IsCurrentPartyAgencySendOrigin(PartyAgencySendOrigin origin)
        {
            if (origin == null) return false;
            lock (LiveTestLock)
            {
                if (!IsLiveTestArmed(CampaignFileService.ReadJsonObject(LiveTestArmPath()))
                    || !IsCurrentPartyAgencySendRuntime(origin.CampaignId, origin.Execution))
                    return false;
                var run = LoadLiveTestRun(origin.CampaignId, origin.Execution.RunId);
                var enrollment = ReadDictionary(run, "enrollment");
                return FindAcceptedPartyAgencySend(run, origin.CampaignId, origin.Mode, origin.Execution) != null
                    && ReadString(enrollment, "campaignTestRunId", "") == origin.CampaignTestRunId
                    && ReadString(enrollment, "certificationRunId", "") == origin.CertificationRunId
                    && MatchesPartyAgencyOpenTarget(run, origin.Execution, origin.Mode, origin.SpeakerId);
            }
        }

        private static bool IsCurrentPartyAgencySendRuntime(string campaign, PartyAgencySendClaim claim)
        {
            var runtime = LoadLiveTestRuntime(campaign);
            return IsFreshLiveTestRuntime(runtime) && ReadString(runtime, "campaignId", "") == campaign
                && ReadString(runtime, "gameInstanceId", "") == claim.GameInstanceId
                && ReadString(ReadLiveTestExecutionIdentity(runtime), "clientProcessInstanceId", "") == claim.ClientProcessInstanceId;
        }

        private static Dictionary<string, object> PartyAgencySendAck(Dictionary<string, object> issued) =>
            TestDict("leaseId", ReadString(issued, "leaseId", ""),
                "clientProcessInstanceId", ReadString(issued, "clientProcessInstanceId", ""),
                "retryGeneration", ReadInt(issued, "executionRetryGeneration", 0));

        private static Dictionary<string, object> FindAcceptedPartyAgencySend(Dictionary<string, object> run,
            string campaign, string routeMode, PartyAgencySendClaim claim)
        {
            if (!IsLiveTestExecutionEvidenceRun(run)
                || ReadString(run, "status", "") != "running"
                || ReadString(run, "campaignId", "") != campaign
                || ReadString(run, "runId", "") != claim.RunId
                || ReadString(run, "gameInstanceId", "") != claim.GameInstanceId)
                return null;

            var commands = ReadDictionaryList(run, "commands");
            var matches = commands.Where(row => ReadString(row, "commandId", "") == claim.CommandId).Take(2).ToList();
            if (matches.Count != 1 || ReadString(FirstLiveTestCommandInSequence(run), "commandId", "") != claim.CommandId)
                return null;
            var command = matches[0];
            string status = ReadString(command, "status", "");
            if (ReadString(command, "operation", "") != "send"
                || ReadString(command, "mode", "") != routeMode || (status != "accepted" && status != "running")
                || ReadInt(command, "executionRetryGeneration", 0) != claim.RetryGeneration)
                return null;

            var scope = TestDict("campaignId", campaign, "runId", claim.RunId, "commandId", claim.CommandId,
                "gameInstanceId", claim.GameInstanceId, "leaseId", claim.ExecutionLeaseId,
                "executionObservation", TestDict("schemaVersion", LiveTestExecutionObservationVersion,
                    "campaignId", campaign, "runId", claim.RunId, "commandId", claim.CommandId,
                    "gameInstanceId", claim.GameInstanceId, "clientProcessInstanceId", claim.ClientProcessInstanceId,
                    "executionLeaseId", claim.ExecutionLeaseId));
            if (!MatchesLiveTestCommandTransport(run, command, scope)) return null;
            var observation = BindLiveTestExecutionObservation(run, command, scope);
            var ack = ReadDictionary(command, "partyAgencyAcceptedSendExecution");
            return ReadString(observation, "status", "") == "pending_artifact_review"
                && ReadString(ack, "leaseId", "") == claim.ExecutionLeaseId
                && ReadString(ack, "clientProcessInstanceId", "") == claim.ClientProcessInstanceId
                && ReadInt(ack, "retryGeneration", -1) == claim.RetryGeneration ? command : null;
        }

        private static bool MatchesPartyAgencyOpenTarget(Dictionary<string, object> run,
            PartyAgencySendClaim claim, string routeMode, string speaker)
        {
            var open = ReadDictionaryList(run, "commands")
                .TakeWhile(row => ReadString(row, "commandId", "") != claim.CommandId)
                .LastOrDefault(row => ReadString(row, "operation", "") is "open" or "close");
            if (ReadString(open, "operation", "") != "open"
                || ReadString(open, "mode", "") != routeMode || ReadString(open, "status", "") != "completed")
                return false;
            var evidence = ReadDictionary(open, "executionEvidence");
            var observed = ReadDictionary(evidence, "executionObservation");
            var result = ReadDictionary(open, "result");
            return ReadString(evidence, "status", "") == "pending_artifact_review"
                && ReadString(observed, "campaignId", "") == ReadString(run, "campaignId", "")
                && ReadString(observed, "runId", "") == claim.RunId
                && ReadString(observed, "commandId", "") == ReadString(open, "commandId", "")
                && ReadString(observed, "gameInstanceId", "") == claim.GameInstanceId
                && ReadString(observed, "clientProcessInstanceId", "") == claim.ClientProcessInstanceId
                && ReadString(result, "runId", "") == claim.RunId && ReadString(result, "mode", "") == routeMode
                && ReadDictionaryList(result, "targets").Count(target => ExactSendText(target, "heroId") == speaker) == 1;
        }

        private static PartyAgencySendOrigin BindPartyAgencySendOrigin(Dictionary<string, object> run,
            Dictionary<string, object> payload, string routeMode, PartyAgencySendClaim claim)
        {
            bool group = routeMode == "party_chat";
            string campaign = ExactSendText(payload, "campaignId");
            if ((!group && routeMode != "individual_chat") || claim == null) return null;
            var command = FindAcceptedPartyAgencySend(run, campaign, routeMode, claim);
            if (command == null) return null;

            string speaker = ExactSendText(payload, group ? "speakerHeroStringId" : "heroStringId");
            string correlation = ExactSendText(payload, "correlationId");
            string expectedCorrelation = "live-" + claim.CommandId;
            if (group)
            {
                int sceneIndex = ReadInt(command, "sceneIndex", 0);
                int turnIndex = ReadInt(command, "turnIndex", 0);
                var order = ReadStringList(ReadDictionary(payload, "groupTurnPlan"), "responseOrder");
                int index = ReadInt(payload, "partySpeakerIndex", -1);
                if (sceneIndex < 0 || turnIndex < 0 || index < 0 || index >= order.Count || order[index] != speaker
                    || ReadStringList(payload, "activeHeroIds").Count == 0
                    || ExactSendText(payload, "mode") != routeMode || ReadBool(payload, "castleOpening", false))
                    return null;
                expectedCorrelation += "-s" + (sceneIndex + 1L).ToString(CultureInfo.InvariantCulture)
                    + "-t" + (turnIndex + 1L).ToString(CultureInfo.InvariantCulture) + "-npc-" + speaker;
            }
            if (string.IsNullOrWhiteSpace(speaker) || correlation != expectedCorrelation
                || ExactSendText(payload, group ? "auditRunId" : "dialogueAuditRunId") != claim.RunId
                || ReadBool(payload, "approachOpening", false) || ExactSendText(payload, "turnType") == "npc_approach_opening"
                || !MatchesPartyAgencyOpenTarget(run, claim, routeMode, speaker))
                return null;

            string plannedText = ExactSendText(command, "text").Trim();
            string actualText = ExactSendText(payload, "playerText");
            bool contextual = ReadBool(command, "contextualPartyAgencyResponse", false);
            if (plannedText.Length == 0 || !(actualText == plannedText
                || contextual && actualText.EndsWith(" " + plannedText, StringComparison.Ordinal)))
                return null;
            var enrollment = ReadDictionary(run, "enrollment");
            return new PartyAgencySendOrigin(claim, campaign, ReadString(enrollment, "campaignTestRunId", ""),
                ReadString(enrollment, "certificationRunId", ""), routeMode, speaker, correlation,
                DialogueService.ConversationExactTextHash(actualText));
        }
    }
}
