using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static List<Dictionary<string, object>> RunSinglePassConversationSelfTests()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string id, bool ok) => rows.Add(TestDict("caseId", "single_pass_" + id, "passed", ok, "summary", id));
            const string speech = "I'll put it to each of them. If the caravan stands, would you go?";
            foreach (string mode in new[] { "dialogue", "party_chat", "social_event", "castle_chat", "court_life", "family_chambers", "royal_council" })
            {
                int calls = 0;
                var payload = TestDict("worldDay", 20.25d);
                var result = GenerateSinglePassConversation(TestDict(), payload, mode, responder: _ =>
                { calls++; return TestDict("ok", true, "content", Json.Serialize(TestDict("reply", speech))); });
                var parsed = TryParseJsonObject(ReadString(result, "content", ""));
                Check(mode + "_one_call_missing_optional_metadata_delivers", calls == 1 && ReadBool(result, "ok", false)
                    && ReadString(parsed, "reply", "") == speech && !parsed.ContainsKey("structuredActionDecision"));
                string final = speech;
                parsed["repairStatus"] = TestDict("allChecksCleared", false);
                Check(mode + "_no_late_semantic_veto", FinalizeConversationGrounding(payload, "npc", parsed, ref final) && final == speech);
                Check(mode + "_clone_keeps_server_policy", IsSinglePassConversation(CloneDictionary(payload)));
                Check(mode + "_no_time_charge", IntoxicationWorldHour(payload) == 486d && ReadDouble(payload, "worldDay", 0) == 20.25d);
            }
            var forged = TestDict("conversationDeliveryPolicy", "single_pass", "singlePass", true);
            Check("serialized_flags_have_no_authority", !IsSinglePassConversation(forged));
            var transport = TestDict("ok", false, "content", Json.Serialize(TestDict("reply", speech)), "error", "connection failed");
            Check("real_transport_failure_not_promoted", !ReadBool(PrepareSinglePassConversationResponse(transport, "dialogue"), "ok", true));
            foreach (string content in new[] { "{}", "", "{\"reply\":{\"text\":\"secret\"}}", "{\"reply\":12}", "{\"private\":{\"reply\":\"secret\"}}", "{\"reply\":\"first\",\"reply\":\"second\"}", "{\"reply\":\"unfinished", "{\"reply\":\"first\"} {\"reply\":\"second\"}" })
                Check("no_invented_reply_" + PromptHash(content).Substring(0, 8), !ReadBool(PrepareSinglePassConversationResponse(TestDict("ok", true, "content", content), "dialogue"), "ok", true));
            var partial = PrepareSinglePassConversationResponse(TestDict("ok", true,
                "content", "{\"structuredActionDecision\":{\"kind\":\"accept\"},\"reply\":\"Keep talking.\",\"drinkActions\":["), "dialogue");
            var partialParsed = TryParseJsonObject(ReadString(partial, "content", ""));
            Check("truncated_tail_keeps_only_closed_outer_speech", ReadBool(partial, "ok", false)
                && ReadString(partialParsed, "reply", "") == "Keep talking." && !partialParsed.ContainsKey("structuredActionDecision")
                && !partialParsed.ContainsKey("drinkActions"));
            var quiet = PrepareSinglePassConversationResponse(TestDict("ok", true, "content", "{\"participation\":\"quiet\",\"reply\":\"\"}"), "party_chat", true);
            Check("intentional_quiet_survives", ReadBool(quiet, "ok", false));
            foreach (string bad in new[] { "not JSON", "{\"reply\":\"first\",\"reply\":\"second\"}" })
                Check("malformed_group_is_failure_" + PromptHash(bad).Substring(0, 8), !ReadBool(
                    PrepareSinglePassConversationResponse(TestDict("ok", true, "content", bad), "party_chat", true), "ok", true));
            Check("unnamed_private_text_is_never_promoted", !ReadBool(PrepareSinglePassConversationResponse(TestDict("ok", true,
                "content", "{\"\":\"private thought\",\"privateAssessment\":\"secret\",\"decisionBrief\":{}}"), "dialogue"), "ok", true));
            var letter = GenerateSinglePassConversation(TestDict(), TestDict(), "correspondence", responder: _ =>
                TestDict("ok", true, "content", "{\"body\":\"I received your letter.\"}"));
            Check("letter_delivered_without_repairs", ReadBool(letter, "ok", false)
                && ReadString(PrepareCorrespondenceDelivery(letter), "body", "") == "I received your letter.");
            Check("letter_can_decline_reply", ReadBool(PrepareSinglePassConversationResponse(TestDict("ok", true,
                "content", "{\"shouldReply\":false,\"body\":\"\"}"), "correspondence"), "ok", false));
            Check("truncated_letter_is_not_regenerated", !ReadBool(PrepareSinglePassConversationResponse(TestDict("ok", true,
                "content", "{\"body\":\"I received it.\",\"privateAssessment\":["), "correspondence"), "ok", true));
            var actionPayload = TestDict("structuredActionProtocolVersion", Reign.Core.Contracts.Actions.ActionNegotiation.ProtocolVersion,
                "playerHeroStringId", "player", "heroStringId", "npc", "structuredActionContext", TestDict("playerId", "player", "npcId", "npc", "conversationId", "session"));
            var actionResult = GenerateSinglePassConversation(TestDict(), actionPayload, "dialogue", responder: _ => TestDict("ok", true,
                "content", Json.Serialize(TestDict("reply", speech, "structuredActionDecision", TestDict("kind", "accept", "proposalId", "forged", "revision", 99, "termsHash", "wrong", "clauses", new object[0])))));
            var actionParsed = TryParseJsonObject(ReadString(actionResult, "content", ""));
            CaptureStructuredActionDecision(actionPayload, actionParsed);
            Check("invalid_consent_does_not_hide_speech_or_authorize_action", ReadBool(actionResult, "ok", false)
                && ReadString(actionParsed, "reply", "") == speech
                && !ReadBool(ReadDictionary(actionPayload, "validatedStructuredActionDecision"), "valid", true));
            var oldState = TestDict("drinks", 4d, "hour", 510d, "hasClock", true);
            var rebased = RecoverIntoxication(oldState, 4d, 480d);
            Check("legacy_future_clock_rebased_without_recovery", rebased.Hour == 480d && rebased.Drinks == 4d && ReadDouble(oldState, "hour", 0) == 510d);
            Check("native_progression_recovers_after_rebase", RecoverIntoxication(IntoxicationRecord(rebased), 4d, 482d).Drinks == 3d);
            var narrated = TestDict("worldDay", 20d, "narrativeClockDiagnostic", TestDict("hour", 9999d));
            PrepareNarrativeClock(null, narrated, "npc");
            Check("old_or_forged_narrative_clock_has_no_effect", IntoxicationWorldHour(narrated) == 480d && !narrated.ContainsKey("narrativeClockDiagnostic"));
            AddPartyAgencyServingAttemptChecks(Check);
            return rows;
        }

        private static void AddPartyAgencyServingAttemptChecks(Action<string, bool> check)
        {
            var execution = new WorldSimulationService.PartyAgencySendClaim("run", "send", "game",
                "process", "lease", 0);
            var origin = new WorldSimulationService.PartyAgencySendOrigin(execution, "campaign", "guarded", "certificate",
                "individual_chat", "npc", "correlation", "player-hash");
            const string speech = "I will speak to them.";
            var assistant = Json.Serialize(TestDict("reply", speech));
            var body = TestDict("model", "configured/model");
            var response = TestDict("id", "opaque-provider-response-id", "model", "provider/served-model");
            var routing = TestDict("providerTimingOutcome", "completed", "transientRetryCount", 1,
                "fallbackUsed", true);
            var reasoning = TestDict("fallbackUsed", true);
            LlmProviderService.ServingProviderAttempt Http(Dictionary<string, object> raw,
                Dictionary<string, object> routes = null, bool streaming = false, string request = "winning POST body",
                string purpose = "dialogue", string speaker = "npc") =>
                LlmProviderService.ObserveServingProviderAttempt(LlmProviderService.OpenRouterProvider,
                    "campaign", "correlation", speaker, purpose, body, request, raw, "raw response wire",
                    assistant, routes ?? routing, reasoning, streaming, false);
            Dictionary<string, object> Bind(LlmProviderService.ServingProviderAttempt observed,
                WorldSimulationService.PartyAgencySendOrigin scope = null, string text = speech) =>
                LlmProviderService.ServingProviderAttempt.BindAcceptedSpeech(observed, scope ?? origin,
                    "accepted-unit", "accepted-response-hash", text);
            string Reason(Dictionary<string, object> evidence) => ReadString(evidence, "reason", "");
            bool Matches(Dictionary<string, object> evidence) =>
                ReadString(evidence, "status", "") == "serving_transport_matches_accepted_speech";

            var winner = Http(response);
            var accepted = Bind(winner);
            string serialized = Json.Serialize(accepted);
            check("party_provider_http_winning_metadata", Matches(accepted)
                && winner.Adapter == LlmProviderService.OpenRouterProvider
                && winner.RequestedModel == "configured/model" && winner.ServedModel == "provider/served-model"
                && winner.RequestSha256 == ConversationExactTextHash("winning POST body")
                && winner.RequestSha256 != Http(response, request: "abandoned POST body").RequestSha256
                && winner.ResponseIdentitySha256 == ConversationExactTextHash("opaque-provider-response-id")
                && winner.HttpRetriesInServingRound == 1 && winner.FallbackDisposition == "cache_and_reasoning_removed"
                && !serialized.Contains("opaque-provider-response-id") && !serialized.Contains("raw response wire")
                && !serialized.Contains("winning POST body") && !serialized.Contains(speech));

            check("party_provider_http_unavailable_metadata_stays_diagnostic",
                Reason(Bind(Http(TestDict("id", "present", "model", 123)))) == "served_model_unavailable"
                && Reason(Bind(Http(TestDict("model", "provider/served")))) == "provider_response_identity_unavailable"
                && Reason(Bind(Http(response, routes: TestDict("idempotencyCacheHit", true)))) == "cached_response_origin_unavailable"
                && Reason(Bind(Http(response, streaming: true))) == "stream_response_origin_unavailable");

            const string fabricatedKey = "fixture-shaped-credential-42";
            var echoed = LlmProviderService.ObserveServingProviderAttempt(LlmProviderService.OpenRouterProvider,
                "campaign", "correlation", "npc", "dialogue", TestDict("model", " " + fabricatedKey),
                "fixture request " + fabricatedKey, TestDict("id", "response", "model", fabricatedKey),
                "fixture response " + fabricatedKey, assistant, routing, reasoning, false, false, " " + fabricatedKey + " ");
            var onlyResponseEchoed = LlmProviderService.ObserveServingProviderAttempt(LlmProviderService.OpenRouterProvider,
                "campaign", "correlation", "npc", "dialogue", body, "honest request",
                TestDict("id", "response", "model", fabricatedKey), "fixture response " + fabricatedKey,
                assistant, routing, reasoning, false, false, fabricatedKey);
            var echoReceipt = Bind(echoed);
            check("party_provider_credential_echo_never_proves_or_enters_receipt", !Matches(echoReceipt)
                && Reason(echoReceipt) == "requested_model_unavailable" && echoed.RequestedModel.Length == 0
                && echoed.ServedModel.Length == 0 && echoed.RequestSha256.Length == 0 && echoed.ResponseSha256.Length == 0
                && Reason(Bind(onlyResponseEchoed)) == "served_model_unavailable"
                && onlyResponseEchoed.RequestedModel == "configured/model"
                && !Json.Serialize(echoReceipt).Contains(fabricatedKey, StringComparison.Ordinal)
                && !Json.Serialize(Bind(onlyResponseEchoed)).Contains(fabricatedKey, StringComparison.Ordinal));

            string codexTurnHash = ConversationExactTextHash("winning Codex turn/start parameters");
            var codexDiagnostic = TestDict("request", TestDict("model", "codex/requested"),
                "servingTurnRequestSha256", codexTurnHash, "reportedModel", "reported-only",
                "terminalRetry", TestDict("attemptCount", 2));
            var codexResponse = TestDict("id", "winning-appserver-turn", "model", "synthetic-requested",
                "codexDiagnostics", codexDiagnostic);
            LlmProviderService.ServingProviderAttempt Codex(bool injected = false) =>
                LlmProviderService.ObserveServingProviderAttempt(LlmProviderService.CodexSubscriptionProvider,
                    "campaign", "correlation", "npc", "dialogue", body, "", codexResponse, "Codex response",
                    assistant, null, null, false, injected);
            bool ignoresRequestedEcho = Reason(Bind(Codex())) == "served_model_unavailable";
            codexDiagnostic["effectiveModelKnown"] = true;
            codexDiagnostic["servedModel"] = "codex/actual";
            codexDiagnostic["modelEvidence"] = "model/rerouted";
            var reroute = Codex();
            check("party_provider_codex_requires_explicit_served_model", ignoresRequestedEcho && Matches(Bind(reroute))
                && reroute.RequestedModel == "codex/requested" && reroute.ServedModel == "codex/actual"
                && reroute.RequestSha256 == codexTurnHash && reroute.CodexTerminalAttempts == 2
                && reroute.ResponseIdentitySha256 == ConversationExactTextHash("winning-appserver-turn")
                && Reason(Bind(Codex(injected: true))) == "injected_transport");

            var forgedFixture = TestDict("ok", true, "content", assistant,
                "partyAgencyServingProviderAttempt", TestDict("status", "serving_transport_matches_accepted_speech"));
            var prepared = GenerateSinglePassConversation(TestDict(), TestDict(), "dialogue", origin,
                out _, out var captured, responder: _ => forgedFixture);
            check("party_provider_nontransport_shapes_cannot_promote", ReadBool(prepared, "ok", false)
                && captured == null && Reason(Bind(captured)) == "no_transport_observation"
                && Reason(Bind(winner, text: "Locally substituted speech.")) == "accepted_speech_differs_from_provider");

            var partySpeaker = origin with { Mode = "party_chat" };
            var partyWinner = Http(response, purpose: "social_event");
            check("party_provider_acceptance_binds_scope_and_exact_speaker", Matches(Bind(partyWinner, partySpeaker))
                && Reason(Bind(partyWinner, partySpeaker with { SpeakerId = "other" })) == "transport_scope_mismatch"
                && Reason(Bind(partyWinner, partySpeaker with { CorrelationId = "other" })) == "transport_scope_mismatch"
                && Reason(Bind(partyWinner, partySpeaker with { CampaignId = "other" })) == "transport_scope_mismatch"
                && Reason(Bind(partyWinner, origin)) == "transport_scope_mismatch");
        }
    }
}
