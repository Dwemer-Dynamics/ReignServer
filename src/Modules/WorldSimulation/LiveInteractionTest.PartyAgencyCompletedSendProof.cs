using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Npgsql;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationService
    {
        private sealed record CompletedPartyAgencySendSelector(PartyAgencySendClaim Execution, string Mode,
            string Speaker, string Correlation, string UnitId, string PlayerHash, string SpeechHash,
            string NormalizedSpeechHash);

        private static Dictionary<string, object> ReadCompletedPartyAgencySendEvidence(Dictionary<string, object> run)
        {
            int limit = ReignPostgreSqlStorage.PartyAgencyAcceptedReceiptReadLimit;
            var selectors = EnumerateCompletedPartyAgencySendSelectors(run).Take(limit + 1).ToList();
            var evidence = new List<Dictionary<string, object>>();
            var report = TestDict("schema", "reign-party-agency-completed-send-evidence-v1",
                "status", "no_bound_receipts", "receipts", evidence);
            if (selectors.Count == 0) return report;
            if (selectors.Count > limit)
            {
                report["status"] = "selector_limit_exceeded";
                return report;
            }

            // A selector is a lookup hint. Duplicated unit or speaker/correlation selections are ambiguous.
            var duplicateUnits = selectors.GroupBy(row => row.UnitId, StringComparer.Ordinal)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
            var duplicateTurns = selectors.GroupBy(row => (row.Execution.CommandId, row.Correlation))
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
            var valid = selectors.Where(row => !duplicateUnits.Contains(row.UnitId)
                && !duplicateTurns.Contains((row.Execution.CommandId, row.Correlation))).ToList();
            if (valid.Count == 0) return report;

            List<ReignPostgreSqlStorage.PartyAgencyAcceptedSendRow> rows;
            try
            {
                rows = ReignPostgreSqlStorage.ReadRegisteredPartyAgencyAcceptedReceipts(
                    ExactSendText(run, "campaignId"), valid.Select(row => row.UnitId).ToArray());
            }
            catch (Exception error) when (error is NpgsqlException or InvalidOperationException or TimeoutException
                or ArgumentException or System.IO.IOException)
            {
                report["status"] = "registered_receipts_unavailable";
                return report;
            }

            var byUnit = rows.GroupBy(row => row.UnitId, StringComparer.Ordinal)
                .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            foreach (var selector in valid)
            {
                if (!byUnit.TryGetValue(selector.UnitId, out var row)) continue;
                var bound = BindCompletedPartyAgencyAcceptedReceipt(run, selector, row);
                if (bound != null) evidence.Add(bound);
            }
            if (evidence.Count > 0) report["status"] = "bound_receipts_read";
            return report;
        }

        private static IEnumerable<CompletedPartyAgencySendSelector> EnumerateCompletedPartyAgencySendSelectors(
            Dictionary<string, object> run)
        {
            if (!IsLiveTestExecutionEvidenceRun(run)) yield break;
            foreach (var command in ReadDictionaryList(run, "commands"))
            {
                string mode = ExactSendText(command, "mode");
                if (mode is not ("individual_chat" or "party_chat")) continue;
                var execution = ReadCompletedPartyAgencyOriginalExecution(run, command);
                if (execution == null) continue;
                var result = ReadDictionary(command, "result");
                string playerText = ExactSendText(result, "playerText");
                if (!ExactProofTrue(result, "ok") || string.IsNullOrWhiteSpace(playerText)) continue;
                string playerHash = DialogueService.ConversationExactTextHash(playerText);

                var replies = mode == "party_chat" ? ReadDictionaryList(result, "replies")
                    : new List<Dictionary<string, object>> { result };
                var correlations = ReadStringList(command, "correlationIds");
                foreach (var reply in replies)
                {
                    if (!ExactProofTrue(reply, "ok") || ExactSendText(reply, "participation") == "quiet") continue;
                    string speech = ExactSendText(reply, "text");
                    if (string.IsNullOrWhiteSpace(speech)) continue;
                    var raw = ReadDictionary(reply, "rawResponse");
                    string speakerHint = ExactSendText(raw, "heroStringId");
                    string speaker = mode == "party_chat" ? ExactSendText(reply, "heroId") : speakerHint;
                    string correlation = ExactSendText(reply, "correlationId");
                    if (!ExactProofTrue(raw, "ok") || string.IsNullOrWhiteSpace(speaker) || speaker.Length > 160
                        || speakerHint != speaker || ExactSendText(raw, "correlationId") != correlation
                        || !MatchesPartyAgencyOpenTarget(run, execution, mode, speaker)
                        || correlations.Count(value => value == correlation) != 1)
                        continue;

                    string expectedCorrelation = "live-" + execution.CommandId;
                    if (mode == "party_chat")
                    {
                        int scene = ReadInt(command, "sceneIndex", 0);
                        int turn = ReadInt(command, "turnIndex", 0);
                        if (scene < 0 || turn < 0) continue;
                        expectedCorrelation += "-s" + (scene + 1L).ToString(CultureInfo.InvariantCulture)
                            + "-t" + (turn + 1L).ToString(CultureInfo.InvariantCulture) + "-npc-" + speaker;
                    }
                    if (correlation != expectedCorrelation) continue;

                    // Group exchangeId is the shared beat. Only the exact direct accepted-unit id selects a row.
                    var unit = ReadDictionary(ReadDictionary(raw, "conversationExchange"), "acceptedUnit");
                    string unitId = ExactSendText(unit, "unitId");
                    if (unitId.Length != 41 || !unitId.StartsWith("accepted_", StringComparison.Ordinal)
                        || !unitId.Skip(9).All(char.IsAsciiHexDigit))
                        continue;
                    yield return new CompletedPartyAgencySendSelector(execution, mode, speaker, correlation,
                        unitId, playerHash, DialogueService.ConversationExactTextHash(speech), DialogueService.PromptHash(speech));
                }
            }
        }

        private static PartyAgencySendClaim ReadCompletedPartyAgencyOriginalExecution(Dictionary<string, object> run,
            Dictionary<string, object> command)
        {
            if (ExactSendText(command, "status") != "completed" || ExactSendText(command, "operation") != "send")
                return null;
            string campaign = ExactSendText(run, "campaignId");
            string instance = ExactSendText(run, "gameInstanceId");
            string runId = ExactSendText(run, "runId");
            string commandId = ExactSendText(command, "commandId");
            var evidence = ReadDictionary(command, "executionEvidence");
            if (ExactSendText(evidence, "status") != "pending_artifact_review") return null;
            var observed = ReadDictionary(evidence, "executionObservation");
            if (ExactSendText(observed, "campaignId") != campaign || ExactSendText(observed, "runId") != runId
                || ExactSendText(observed, "commandId") != commandId || ExactSendText(observed, "gameInstanceId") != instance)
                return null;
            string originalLease = ExactSendText(observed, "leaseId");
            string process = ExactSendText(observed, "clientProcessInstanceId");
            string transport = ExactSendText(evidence, "transportLeaseId");
            if (!Guid.TryParseExact(originalLease, "N", out _) || !Guid.TryParseExact(process, "N", out _)
                || !Guid.TryParseExact(transport, "N", out _)
                || !observed.TryGetValue("executionRetryGeneration", out var retry) || retry is not int generation || generation < 0
                || ReadInt(command, "executionRetryGeneration", 0) != generation)
                return null;

            var scope = TestDict("campaignId", campaign, "runId", runId, "commandId", commandId,
                "gameInstanceId", instance, "leaseId", transport,
                "executionObservation", TestDict("schemaVersion", LiveTestExecutionObservationVersion,
                    "campaignId", campaign, "runId", runId, "commandId", commandId,
                    "gameInstanceId", instance, "clientProcessInstanceId", process, "executionLeaseId", originalLease));
            if (!MatchesLiveTestCommandTransport(run, command, scope)) return null;
            var rebound = BindLiveTestExecutionObservation(run, command, scope);
            if (ExactSendText(rebound, "status") != "pending_artifact_review") return null;
            var ack = ReadDictionary(command, "partyAgencyAcceptedSendExecution");
            if (ExactSendText(ack, "leaseId") != originalLease || ExactSendText(ack, "clientProcessInstanceId") != process
                || !ack.TryGetValue("retryGeneration", out var acceptedRetry) || acceptedRetry is not int acceptedGeneration
                || acceptedGeneration != generation)
                return null;
            return new PartyAgencySendClaim(runId, commandId, instance, process, originalLease, generation);
        }

        private static Dictionary<string, object> BindCompletedPartyAgencyAcceptedReceipt(Dictionary<string, object> run,
            CompletedPartyAgencySendSelector selector, ReignPostgreSqlStorage.PartyAgencyAcceptedSendRow row)
        {
            if (row.UnitId != selector.UnitId || !IsCompletedSendDigest(row.ResponseHash)
                || string.IsNullOrWhiteSpace(row.RestoreGeneration) || row.RestoreGeneration.Length > 160)
                return null;
            var receipt = TryParseStrictJsonObject(row.ProofJson);
            string status = ExactSendText(receipt, "status");
            if (!ExactProofTrue(receipt, "ok") || status is not ("committed" or "committed_memory_incomplete")
                || ExactSendText(receipt, "unitId") != row.UnitId || ExactSendText(receipt, "responseHash") != row.ResponseHash
                || ExactSendText(receipt, "restoreGeneration") != row.RestoreGeneration
                || ExactSendText(receipt, "acceptedReplyHash") != selector.NormalizedSpeechHash)
                return null;
            var origin = ReadDictionary(receipt, PartyAgencySendOriginField);
            var enrollment = ReadDictionary(run, "enrollment");
            var execution = selector.Execution;
            if (ExactSendText(origin, "schema") != "reign-party-agency-send-origin-v1"
                || ExactSendText(origin, "status") != "execution_observed"
                || ExactSendText(origin, "campaignId") != ExactSendText(run, "campaignId")
                || ExactSendText(origin, "campaignTestRunId") != ExactSendText(enrollment, "campaignTestRunId")
                || ExactSendText(origin, "certificationRunId") != ExactSendText(enrollment, "certificationRunId")
                || ExactSendText(origin, "runId") != execution.RunId || ExactSendText(origin, "commandId") != execution.CommandId
                || ExactSendText(origin, "gameInstanceId") != execution.GameInstanceId
                || ExactSendText(origin, "clientProcessInstanceId") != execution.ClientProcessInstanceId
                || ExactSendText(origin, "executionLeaseId") != execution.ExecutionLeaseId
                || !origin.TryGetValue("retryGeneration", out var retry) || retry is not int generation
                || generation != execution.RetryGeneration
                || ExactSendText(origin, "mode") != selector.Mode || ExactSendText(origin, "speakerHeroStringId") != selector.Speaker
                || ExactSendText(origin, "correlationId") != selector.Correlation
                || ExactSendText(origin, "playerContributionSha256") != selector.PlayerHash
                || ExactSendText(origin, "acceptedUnitId") != row.UnitId
                || ExactSendText(origin, "acceptedResponseHash") != row.ResponseHash)
                return null;
            var provider = ReadDictionary(receipt, LlmProviderService.ServingProviderAttempt.ReceiptField);
            if (ExactSendText(provider, "schema") != "reign-party-agency-serving-provider-attempt-v1"
                || ExactSendText(provider, "acceptedUnitId") != row.UnitId
                || ExactSendText(provider, "acceptedResponseHash") != row.ResponseHash
                || ExactSendText(provider, "acceptedSpeechSha256") != selector.SpeechHash)
                return null;

            var projectedProvider = ProjectCompletedProviderEvidence(provider, selector.SpeechHash);
            if (projectedProvider == null) return null;
            return TestDict("runId", execution.RunId, "commandId", execution.CommandId,
                "gameInstanceId", execution.GameInstanceId, "clientProcessInstanceId", execution.ClientProcessInstanceId,
                "executionLeaseId", execution.ExecutionLeaseId, "retryGeneration", execution.RetryGeneration,
                "mode", selector.Mode, "speakerHeroStringId", selector.Speaker, "correlationId", selector.Correlation,
                "acceptedUnitId", row.UnitId, "acceptedResponseHash", row.ResponseHash,
                "restoreGeneration", row.RestoreGeneration, "playerContributionSha256", selector.PlayerHash,
                "acceptedSpeechSha256", selector.SpeechHash, "provider", projectedProvider);
        }

        private static Dictionary<string, object> ProjectCompletedProviderEvidence(Dictionary<string, object> provider,
            string expectedSpeechHash)
        {
            string status = ExactSendText(provider, "status");
            if (status == "diagnostic")
            {
                string reason = ExactSendText(provider, "reason");
                return reason is "no_transport_observation" or "transport_scope_mismatch" or "accepted_speech_differs_from_provider"
                    or "injected_transport" or "cached_response_origin_unavailable" or "stream_response_origin_unavailable"
                    or "transport_unconfirmed" or "winning_request_digest_unavailable" or "requested_model_unavailable"
                    or "served_model_unavailable" or "provider_response_identity_unavailable" or "provider_authored_speech_unavailable"
                    ? TestDict("status", status, "reason", reason) : null;
            }
            if (status != "serving_transport_matches_accepted_speech"
                || ExactSendText(provider, "providerAuthoredSpeechSha256") != expectedSpeechHash) return null;
            string adapter = ExactSendText(provider, "adapter");
            bool codex = adapter == LlmProviderService.CodexSubscriptionProvider;
            if (!codex && adapter != LlmProviderService.OpenAiCompatibleProvider
                && adapter != LlmProviderService.OpenRouterProvider && adapter != LlmProviderService.OllamaCloudProvider)
                return null;
            string requestKind = ExactSendText(provider, "requestKind");
            string modelEvidence = ExactSendText(provider, "servedModelEvidence");
            if (requestKind != (codex ? "codex_turn_start_parameters" : "http_post_body")
                || modelEvidence != (codex ? "codex_model_rerouted" : "http_response_model"))
                return null;
            string requested = ExactSendText(provider, "requestedModel");
            string served = ExactSendText(provider, "servedModel");
            if (!IsBoundedProviderModel(requested) || !IsBoundedProviderModel(served)) return null;

            var digests = new[] { "requestSha256", "responseSha256", "responseIdentitySha256", "providerAssistantContentSha256" };
            if (digests.Any(field => !IsCompletedSendDigest(ExactSendText(provider, field)))) return null;
            string fallback = ExactSendText(provider, "fallbackDisposition");
            bool knownFallback = codex ? fallback is "codex_fast_to_standard" or "no_service_tier_fallback"
                : fallback is "cache_and_reasoning_removed" or "cache_removed" or "reasoning_removed" or "no_outer_fallback";
            if (!knownFallback || !provider.TryGetValue("httpRetriesInServingRound", out var rawHttpAttempts)
                || rawHttpAttempts is not int httpRetries || !provider.TryGetValue("codexTerminalAttempts", out var rawCodexAttempts)
                || rawCodexAttempts is not int codexAttempts
                || (codex ? httpRetries != 0 || codexAttempts < 1 || codexAttempts > 2
                    : codexAttempts != 0 || httpRetries < 0 || httpRetries > 3))
                return null;

            var result = TestDict("status", status, "adapter", adapter, "requestedModel", requested,
                "servedModel", served, "servedModelEvidence", modelEvidence, "requestKind", requestKind,
                "fallbackDisposition", fallback, "httpRetriesInServingRound", httpRetries, "codexTerminalAttempts", codexAttempts,
                "providerAuthoredSpeechSha256", expectedSpeechHash);
            foreach (string field in digests) result[field] = ExactSendText(provider, field);
            return result;
        }

        private static bool IsBoundedProviderModel(string model) =>
            model.Length > 0 && model.Length <= 180 && !model.StartsWith("sk-", StringComparison.OrdinalIgnoreCase)
            && !model.Contains("://", StringComparison.Ordinal)
            && model.All(character => char.IsAsciiLetterOrDigit(character) || "-._:/@+".Contains(character));

        private static bool IsCompletedSendDigest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);

        private static bool ExactProofTrue(Dictionary<string, object> source, string key) =>
            source != null && source.TryGetValue(key, out var raw) && raw is bool value && value;
    }
}
