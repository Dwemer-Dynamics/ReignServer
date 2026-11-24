using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class LlmProviderService
    {
        // This is an observation made by the selected transport. No caller packet,
        // provider-visible generated field or response dictionary can create one.
        internal sealed record ServingProviderAttempt(string Adapter, string CampaignId,
            string CorrelationId, string SpeakerId, string RequestType, string RequestedModel,
            string ServedModel, string ServedModelEvidence, string RequestKind, string RequestSha256,
            string ResponseSha256, string ResponseIdentitySha256, string AssistantContentSha256,
            string AuthoredSpeechSha256, string FallbackDisposition, int HttpRetriesInServingRound,
            int CodexTerminalAttempts, string Gap)
        {
            internal const string ReceiptField = "partyAgencyServingProviderAttempt";

            internal static Dictionary<string, object> BindAcceptedSpeech(ServingProviderAttempt observed,
                WorldSimulationService.PartyAgencySendOrigin origin, string unitId, string responseHash, string speech)
            {
                var receipt = TestDict("schema", "reign-party-agency-serving-provider-attempt-v1",
                    "status", "diagnostic", "acceptedUnitId", unitId, "acceptedResponseHash", responseHash,
                    "acceptedSpeechSha256", DialogueService.ConversationExactTextHash(speech ?? ""));
                if (observed == null)
                {
                    receipt["reason"] = "no_transport_observation";
                    return receipt;
                }

                string expectedRequest = origin?.Mode == "party_chat" ? "social_event" : "dialogue";
                if (origin == null || observed.CampaignId != origin.CampaignId
                    || observed.CorrelationId != origin.CorrelationId || observed.SpeakerId != origin.SpeakerId
                    || observed.RequestType != expectedRequest)
                {
                    receipt["reason"] = "transport_scope_mismatch";
                    return receipt;
                }
                AddPairs(receipt, "adapter", observed.Adapter, "requestedModel", observed.RequestedModel,
                    "servedModel", observed.ServedModel, "servedModelEvidence", observed.ServedModelEvidence,
                    "requestKind", observed.RequestKind, "requestSha256", observed.RequestSha256,
                    "responseSha256", observed.ResponseSha256, "responseIdentitySha256", observed.ResponseIdentitySha256,
                    "providerAssistantContentSha256", observed.AssistantContentSha256,
                    "providerAuthoredSpeechSha256", observed.AuthoredSpeechSha256,
                    "fallbackDisposition", observed.FallbackDisposition,
                    "httpRetriesInServingRound", observed.HttpRetriesInServingRound,
                    "codexTerminalAttempts", observed.CodexTerminalAttempts);
                if (observed.Gap.Length > 0)
                    receipt["reason"] = observed.Gap;
                else if (observed.AuthoredSpeechSha256 != ReadString(receipt, "acceptedSpeechSha256", ""))
                    receipt["reason"] = "accepted_speech_differs_from_provider";
                else
                    receipt["status"] = "serving_transport_matches_accepted_speech";
                return receipt;
            }
        }

        internal static ServingProviderAttempt ObserveServingProviderAttempt(string adapter, string campaign,
            string correlation, string speaker, string requestType, Dictionary<string, object> requestBody,
            string sentBody, Dictionary<string, object> raw, string responseText, string rawAssistantContent,
            Dictionary<string, object> httpRouting, Dictionary<string, object> reasoningRouting,
            bool streaming, bool injectedCodexTransport, string applicableApiKey = "")
        {
            bool codex = adapter == CodexSubscriptionProvider;
            var diagnostic = codex ? ReadDictionary(raw, "codexDiagnostics") : null;
            var codexRequest = ReadDictionary(diagnostic, "request");
            var requestedSource = codex ? codexRequest : requestBody;
            var servedSource = codex ? diagnostic : raw;
            string servedField = codex ? "servedModel" : "model";
            // Use the key already resolved for this dispatch only to reject echoes.
            // Never retain it or the wire digest containing the echoed metadata.
            string credential = (applicableApiKey ?? "").Trim();
            bool requestEcho = ProviderMetadataEchoesCredential(requestedSource, "model", credential);
            bool servedEcho = ProviderMetadataEchoesCredential(servedSource, servedField, credential);
            bool identityEcho = ProviderMetadataEchoesCredential(raw, "id", credential);
            string requestedModel = requestEcho ? "" : ReadProviderEvidenceIdentifier(requestedSource, "model");
            bool codexModelObserved = ReadBool(diagnostic, "effectiveModelKnown", false)
                && ReadString(diagnostic, "modelEvidence", "") == "model/rerouted";
            string servedModel = servedEcho || (codex && !codexModelObserved)
                ? "" : ReadProviderEvidenceIdentifier(servedSource, servedField);
            string modelEvidence = servedModel.Length == 0 ? "" : codex ? "codex_model_rerouted" : "http_response_model";
            string requestDigest = requestEcho ? "" : codex
                ? ReadString(diagnostic, "servingTurnRequestSha256", "")
                : DigestWhenAvailable(sentBody);
            if (requestDigest.Length != 64 || !requestDigest.All(char.IsAsciiHexDigit)) requestDigest = "";
            string identityDigest = !identityEcho && raw != null && raw.TryGetValue("id", out object identity)
                && identity is string id && !string.IsNullOrWhiteSpace(id) ? DigestWhenAvailable(id) : "";
            var providerDocument = TryParseStrictJsonObject(rawAssistantContent);
            string authored = new[] { "reply", "response", "text", "content" }
                .Select(key => providerDocument != null && providerDocument.TryGetValue(key, out var value) && value is string text ? text : "")
                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? "";
            string authoredSpeech = DigestWhenAvailable(authored);
            bool cached = !codex && ReadBool(httpRouting, "idempotencyCacheHit", false);
            string gap = injectedCodexTransport ? "injected_transport"
                : cached ? "cached_response_origin_unavailable"
                : streaming ? "stream_response_origin_unavailable"
                : !codex && ReadString(httpRouting, "providerTimingOutcome", "") != "completed" ? "transport_unconfirmed"
                : requestEcho ? "requested_model_unavailable"
                : servedEcho ? "served_model_unavailable"
                : identityEcho ? "provider_response_identity_unavailable"
                : requestDigest.Length == 0 ? "winning_request_digest_unavailable"
                : requestedModel.Length == 0 ? "requested_model_unavailable"
                : servedModel.Length == 0 ? "served_model_unavailable"
                : identityDigest.Length == 0 ? "provider_response_identity_unavailable"
                : authoredSpeech.Length == 0 ? "provider_authored_speech_unavailable"
                : "";
            if (cached || streaming || injectedCodexTransport)
            {
                servedModel = "";
                modelEvidence = "";
                identityDigest = "";
            }

            bool cacheFallback = !codex && ReadBool(httpRouting, "fallbackUsed", false);
            bool reasoningFallback = !codex && ReadBool(reasoningRouting, "fallbackUsed", false);
            string httpFallback = cacheFallback && reasoningFallback ? "cache_and_reasoning_removed"
                : cacheFallback ? "cache_removed" : reasoningFallback ? "reasoning_removed" : "no_outer_fallback";
            int codexAttempts = ReadInt(ReadDictionary(diagnostic, "terminalRetry"), "attemptCount", 0);
            var fast = ReadDictionary(codexRequest, "fastMode");
            string codexFallback = ReadBool(fast, "requested", false) && ReadString(fast, "applied", "") == "standard"
                ? "codex_fast_to_standard" : "no_service_tier_fallback";
            bool responseEcho = servedEcho || identityEcho;
            return new ServingProviderAttempt(adapter, campaign, correlation, speaker, requestType, requestedModel,
                servedModel, modelEvidence, codex ? "codex_turn_start_parameters" : "http_post_body", requestDigest,
                responseEcho ? "" : DigestWhenAvailable(responseText), identityDigest,
                responseEcho ? "" : DigestWhenAvailable(rawAssistantContent), responseEcho ? "" : authoredSpeech,
                codex ? codexFallback : httpFallback, codex ? 0 : ReadInt(httpRouting, "transientRetryCount", 0),
                codexAttempts, gap);
        }

        private static bool ProviderMetadataEchoesCredential(Dictionary<string, object> source, string key, string credential) =>
            credential.Length > 0 && source != null && source.TryGetValue(key, out var value)
            && value is string text && string.Equals(text.Trim(), credential, StringComparison.Ordinal);

        private static string ReadProviderEvidenceIdentifier(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out var value) || value is not string text
                || text.Length == 0 || text.Length > 180 || text.StartsWith("sk-", StringComparison.OrdinalIgnoreCase)
                || !text.All(character => char.IsAsciiLetterOrDigit(character) || "-._:/@+".Contains(character))
                || text.Contains("://", StringComparison.Ordinal))
                return "";
            return text;
        }

        private static string DigestWhenAvailable(string text) => string.IsNullOrWhiteSpace(text)
            ? "" : DialogueService.ConversationExactTextHash(text);
    }
}
