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
#if !REIGN_EXCLUDE_VERIFICATION
        private static void AddLiveTestExecutionObservationChecks(Action<string, bool, string> add)
        {
            var scope = TestDict("campaignId", "execution-fixture", "gameInstanceId", "native-game",
                "runId", "execution-run", "commandId", "observe");
            var start = CloneDictionary(scope);
            start.Remove("commandId");
            var run = new Dictionary<string, object>(start)
            {
                ["mode"] = "party_agency",
                ["enrollment"] = TestDict("campaignId", "execution-fixture", "campaignTestRunId", "owned-run",
                    "certificationRunId", "certification"),
                ["executionObservationVersion"] = LiveTestExecutionObservationVersion,
                ["executionStartIdentity"] = start
            };
            const string process = "f0c845c1a0134b49baa0218998fa6998";
            var issued = new Dictionary<string, object>(scope)
            {
                ["leaseId"] = "execution-lease",
                ["clientProcessInstanceId"] = process,
                ["clientModuleIds"] = TestDict("ReignBeta", "103843aa-4a34-41fb-a155-6fd7188293db"),
                ["executionRetryGeneration"] = 0
            };
            var command = TestDict("commandId", "observe", "leaseId", "execution-lease",
                "issuedExecutionObservations", new List<Dictionary<string, object>> { issued });
            var observation = new Dictionary<string, object>(scope)
            {
                ["schemaVersion"] = LiveTestExecutionObservationVersion,
                ["executionLeaseId"] = "execution-lease",
                ["clientProcessInstanceId"] = process
            };
            var report = new Dictionary<string, object>(scope)
            {
                ["leaseId"] = "execution-lease", ["executionObservation"] = observation
            };

            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            var storedRun = serializer.Deserialize<Dictionary<string, object>>(serializer.Serialize(run));
            var storedCommand = serializer.Deserialize<Dictionary<string, object>>(serializer.Serialize(command));
            var decodedReport = serializer.Deserialize<Dictionary<string, object>>(serializer.Serialize(report));
            var evidence = BindLiveTestExecutionObservation(storedRun, storedCommand, decodedReport);
            var issuedEvidence = ReadDictionary(evidence, "executionObservation");
            add("execution_observation_json_binding",
                IsLiveTestExecutionEvidenceRun(storedRun) && MatchesLiveTestCommandTransport(storedRun, storedCommand, decodedReport)
                && ReadString(evidence, "status", "") == "pending_artifact_review"
                && ReadString(ReadDictionary(issuedEvidence, "clientModuleIds"), "ReignBeta", "")
                    == "103843aa-4a34-41fb-a155-6fd7188293db",
                "Persisted native observations preserve the exact issued module and scope for later artifact review.");

            command["leaseId"] = "replacement-lease";
            bool oldTransportRejected = !MatchesLiveTestCommandTransport(run, command, report)
                && ReadString(BindLiveTestExecutionObservation(run, command, report), "status", "") == "stale_transport";
            report["leaseId"] = "replacement-lease";
            var resumed = BindLiveTestExecutionObservation(run, command, report);
            add("execution_observation_transport_replacement",
                oldTransportRejected && MatchesLiveTestCommandTransport(run, command, report)
                && ReadString(resumed, "status", "") == "pending_artifact_review"
                && ReadString(ReadDictionary(resumed, "executionObservation"), "leaseId", "") == "execution-lease"
                && ReadString(resumed, "transportLeaseId", "") == "replacement-lease",
                "An expired transport lease cannot report, and its same-generation replacement retains the original execution lease.");

            var wrongScope = new Dictionary<string, object>(observation) { ["gameInstanceId"] = "previous-game" };
            var wrongProcess = new Dictionary<string, object>(observation) { ["clientProcessInstanceId"] = "previous-process" };
            var unknownLease = new Dictionary<string, object>(observation) { ["executionLeaseId"] = "unknown-lease" };
            string StatusWith(Dictionary<string, object> candidate) =>
                ReadString(BindLiveTestExecutionObservation(run, command,
                    new Dictionary<string, object>(report) { ["executionObservation"] = candidate }), "status", "");
            add("execution_observation_rejects_wrong_identity",
                StatusWith(wrongScope) == "scope_mismatch" && StatusWith(wrongProcess) == "process_mismatch"
                && StatusWith(unknownLease) == "unknown_original_lease" && StatusWith(null) == "missing",
                "An unrelated game, process, unknown lease or missing observation cannot establish an execution identity.");

            AdvanceLiveTestExecutionRetry(command);
            add("execution_observation_explicit_retry_excludes_prior_result",
                MatchesLiveTestCommandTransport(run, command, report)
                && ReadString(BindLiveTestExecutionObservation(run, command, report), "status", "") == "stale_execution_retry",
                "An explicit infrastructure retry supersedes earlier observations even on the current transport lease.");

            var full = LiveTestRunSummary(storedRun, true);
            var poll = LiveTestRunSummary(storedRun, false);
            run.Remove("executionObservationVersion");
            var legacy = LiveTestRunSummary(run, true);
            add("execution_observation_report_scope",
                ReadInt(full, "executionObservationVersion", 0) == LiveTestExecutionObservationVersion
                && ReadString(ReadDictionary(full, "executionStartIdentity"), "gameInstanceId", "") == "native-game"
                && !poll.ContainsKey("executionStartIdentity") && !poll.ContainsKey("executionObservationVersion")
                && !legacy.ContainsKey("executionStartIdentity") && !legacy.ContainsKey("executionObservationVersion"),
                "Only a full report for a guarded enrollment exposes the server-recorded start identity.");
        }

        private static void AddPartyAgencySendAuthorityChecks(Action<string, bool, string> add)
        {
            const string process = "f0c845c1a0134b49baa0218998fa6998";
            const string lease = "66fe555aa9d47bf95ccfa0211b237357";
            const string campaign = "send-campaign";
            const string runId = "send-run";
            const string game = "send-game";
            const string speaker = "fixture-npc";
            var wire = TestDict("schemaVersion", 1, "runId", runId, "commandId", "send-command",
                "gameInstanceId", game, "clientProcessInstanceId", process, "executionLeaseId", lease,
                "retryGeneration", 0);
            var payload = TestDict("campaignId", campaign, "heroStringId", speaker,
                "dialogueAuditRunId", runId, "correlationId", "live-send-command",
                "playerText", "Original turn", PartyAgencySendOriginField, wire);
            var claim = TakePartyAgencySendClaim(payload);
            var duplicate = TestDict(PartyAgencySendOriginField, wire, "PARTYAGENCYSENDORIGIN", wire);
            bool clearedDuplicates = TakePartyAgencySendClaim(duplicate) == null && duplicate.Count == 0;
            var coercion = TestDict(PartyAgencySendOriginField, new Dictionary<string, object>(wire) { ["retryGeneration"] = "0" });
            add("party_agency_send_claim_is_strict_and_removed", claim != null
                && !payload.ContainsKey(PartyAgencySendOriginField) && clearedDuplicates
                && TakePartyAgencySendClaim(coercion) == null && coercion.Count == 0,
                "Only a versioned typed claim survives parsing, and no raw claim reaches dialogue processing.");
            if (claim == null) return;

            var observed = TestDict("campaignId", campaign, "gameInstanceId", game, "runId", runId,
                "commandId", "send-command", "leaseId", lease, "clientProcessInstanceId", process,
                "executionRetryGeneration", 0);
            var openObservation = new Dictionary<string, object>(observed) { ["commandId"] = "open-command" };
            var openResult = TestDict("runId", runId, "mode", "individual_chat", "targets", new[] { TestDict("heroId", speaker) });
            var open = TestDict("commandId", "open-command", "operation", "open", "mode", "individual_chat",
                "status", "completed", "executionEvidence", TestDict("status", "pending_artifact_review",
                    "executionObservation", openObservation), "result", openResult);
            var send = TestDict("campaignId", campaign, "runId", runId, "gameInstanceId", game,
                "commandId", "send-command", "operation", "send", "mode", "individual_chat", "leaseId", lease,
                "status", "accepted", "text", "Original turn", "issuedExecutionObservations", new[] { observed },
                "partyAgencyAcceptedSendExecution", PartyAgencySendAck(observed));
            var run = TestDict("campaignId", campaign, "runId", runId, "gameInstanceId", game, "status", "running",
                "mode", "party_agency", "executionObservationVersion", 1,
                "enrollment", TestDict("campaignId", campaign, "campaignTestRunId", "owned-campaign-test",
                    "certificationRunId", "party-agency-test"), "commands", new[] { open, send });
            var original = BindPartyAgencySendOrigin(run, payload, "individual_chat", claim);
            var exported = original?.AcceptedReceipt("accepted-turn", "final-response-hash");
            add("party_agency_send_binds_accepted_scope", original != null && original.SpeakerId == speaker
                && ReadString(exported, "status", "") == "execution_observed"
                && ReadString(exported, "acceptedResponseHash", "") == "final-response-hash"
                && !Json.Serialize(exported).Contains("Original turn"),
                "The receipt binds only the original send identity and accepted response digest.");

            bool Reject(PartyAgencySendClaim attempt) =>
                BindPartyAgencySendOrigin(run, payload, "individual_chat", attempt) == null;
            var ack = send["partyAgencyAcceptedSendExecution"];
            send.Remove("partyAgencyAcceptedSendExecution");
            bool needsAck = Reject(claim);
            send["partyAgencyAcceptedSendExecution"] = ack;
            send["status"] = "leased";
            bool needsAccepted = Reject(claim);
            send["status"] = "running";
            send["executionRetryGeneration"] = 1;
            bool stale = Reject(claim);
            send["executionRetryGeneration"] = 0;
            add("party_agency_send_requires_exact_ack_scope", needsAck && needsAccepted && stale
                && Reject(claim with { ClientProcessInstanceId = "other-process" })
                && Reject(claim with { ExecutionLeaseId = "other-lease" })
                && Reject(claim with { GameInstanceId = "other-load" })
                && BindPartyAgencySendOrigin(run, payload, "individual_chat", claim) != null,
                "Unaccepted, superseded, replacement lease, wrong load and wrong process claims confer no authority.");

            var fakeSpeaker = new Dictionary<string, object>(payload) { ["heroStringId"] = "unopened-npc" };
            var wrongOrigin = new Dictionary<string, object>(payload) { ["correlationId"] = "another-command" };
            var contextual = new Dictionary<string, object>(payload) { ["playerText"] = "Additional words. Original turn" };
            bool exactText = BindPartyAgencySendOrigin(run, contextual, "individual_chat", claim) == null;
            send["contextualPartyAgencyResponse"] = true;
            bool allowsContext = BindPartyAgencySendOrigin(run, contextual, "individual_chat", claim) != null;
            add("party_agency_send_binds_actual_individual_turn", exactText && allowsContext
                && BindPartyAgencySendOrigin(run, fakeSpeaker, "individual_chat", claim) == null
                && BindPartyAgencySendOrigin(run, wrongOrigin, "individual_chat", claim) == null,
                "The actual individual turn must be the planned text or its authorized contextual form, with the exact opened speaker and correlation.");

            open["mode"] = openResult["mode"] = send["mode"] = "party_chat";
            var group = TestDict("campaignId", campaign, "speakerHeroStringId", speaker, "auditRunId", runId,
                "mode", "party_chat", "partySpeakerIndex", 0, "groupTurnPlan", TestDict("responseOrder", new[] { speaker }),
                "activeHeroIds", new[] { speaker }, "correlationId", "live-send-command-s1-t1-npc-" + speaker,
                "playerText", "Original turn");
            bool partyBound = BindPartyAgencySendOrigin(run, group, "party_chat", claim) != null;
            group["partySpeakerIndex"] = -1;
            bool noFloor = BindPartyAgencySendOrigin(run, group, "party_chat", claim) == null;
            group["partySpeakerIndex"] = 0;
            group["correlationId"] = "live-send-command-s1-t1-npc-other";
            bool bindsSpeaker = BindPartyAgencySendOrigin(run, group, "party_chat", claim) == null;
            add("party_agency_send_binds_each_party_speaker", partyBound && noFloor && bindsSpeaker
                && BindPartyAgencySendOrigin(run, payload, "individual_chat", claim) == null,
                "Party authority requires the admitted mode, server floor and exact speaker-specific correlation.");
        }

        private static void AddCompletedPartyAgencySendEvidenceChecks(Action<string, bool, string> add)
        {
            const string campaign = "report-campaign";
            const string runId = "report-run";
            const string instance = "native-instance";
            const string process = "f0c845c1a0134b49baa0218998fa6998";
            const string original = "66fe555aa9d47bf95ccfa0211b237357";
            const string replacement = "05e8b2d6d24a47b1a9f741431a096d62";
            const string speaker = "npc-a";
            const string correlation = "live-send-command";
            const string unitId = "accepted_0123456789ABCDEF0123456789ABCDEF";
            const string speech = "NPC accepted private words.";
            const string playerText = "Exact player contribution.";
            var native = TestDict("campaignId", campaign, "gameInstanceId", instance, "runId", runId,
                "commandId", "send-command", "clientProcessInstanceId", process, "leaseId", original,
                "executionRetryGeneration", 0);
            var opened = TestDict("commandId", "open-command", "operation", "open", "mode", "individual_chat",
                "status", "completed", "executionEvidence", TestDict("status", "pending_artifact_review",
                    "executionObservation", new Dictionary<string, object>(native) { ["commandId"] = "open-command" }),
                "result", TestDict("runId", runId, "mode", "individual_chat", "targets", new[] { TestDict("heroId", speaker) }));
            var raw = TestDict("ok", true, "heroStringId", speaker, "correlationId", correlation,
                "conversationExchange", TestDict("exchangeId", "arbitrary-shared-beat",
                    "acceptedUnit", TestDict("unitId", unitId, LlmProviderService.ServingProviderAttempt.ReceiptField,
                        TestDict("servedModel", "forged-model"))));
            var result = TestDict("ok", true, "playerText", playerText, "text", speech, "correlationId", correlation,
                "rawResponse", raw);
            var send = TestDict("commandId", "send-command", "operation", "send", "mode", "individual_chat",
                "status", "completed", "leaseId", replacement, "result", result, "correlationIds", new[] { correlation },
                "issuedExecutionObservations", new[] { native },
                "partyAgencyAcceptedSendExecution", PartyAgencySendAck(native),
                "executionEvidence", TestDict("status", "pending_artifact_review",
                    "executionObservation", native, "transportLeaseId", replacement));
            var run = TestDict("campaignId", campaign, "runId", runId, "gameInstanceId", instance,
                "mode", "party_agency", "executionObservationVersion", 1, "commands", new[] { opened, send },
                "enrollment", TestDict("campaignId", campaign, "campaignTestRunId", "owned-test", "certificationRunId", "certification"));
            var candidates = EnumerateCompletedPartyAgencySendSelectors(run).ToList();
            add("party_agency_report_selects_exact_completed_native_origin", candidates.Count == 1
                && candidates[0].UnitId == unitId && candidates[0].Execution.ExecutionLeaseId == original,
                "The retained result can select the accepted row by unit id and the original lease survives a replacement transport.");
            if (candidates.Count != 1) return;
            var selected = candidates[0];
            string responseHash = DialogueService.PromptHash("receipt response");
            string witnessHash = DialogueService.ConversationExactTextHash("byte evidence");
            var canonicalOrigin = new PartyAgencySendOrigin(selected.Execution, campaign, "owned-test", "certification",
                "individual_chat", speaker, correlation, selected.PlayerHash).AcceptedReceipt(unitId, responseHash);
            var provider = TestDict("schema", "reign-party-agency-serving-provider-attempt-v1",
                "status", "serving_transport_matches_accepted_speech", "acceptedUnitId", unitId,
                "acceptedResponseHash", responseHash, "acceptedSpeechSha256", selected.SpeechHash,
                "providerAuthoredSpeechSha256", selected.SpeechHash, "adapter", LlmProviderService.OpenRouterProvider,
                "requestedModel", "configured/model", "servedModel", "provider/real", "servedModelEvidence", "http_response_model",
                "requestKind", "http_post_body", "requestSha256", witnessHash, "responseSha256", witnessHash,
                "responseIdentitySha256", witnessHash, "providerAssistantContentSha256", witnessHash,
                "fallbackDisposition", "reasoning_removed", "httpRetriesInServingRound", 1, "codexTerminalAttempts", 0);
            var durable = TestDict("ok", true, "status", "committed", "unitId", unitId, "responseHash", responseHash,
                "acceptedReplyHash", selected.NormalizedSpeechHash, "restoreGeneration", "saved-generation",
                PartyAgencySendOriginField, canonicalOrigin, LlmProviderService.ServingProviderAttempt.ReceiptField, provider);
            ReignPostgreSqlStorage.PartyAgencyAcceptedSendRow Row(string generation = "saved-generation") =>
                new(unitId, responseHash, generation, Json.Serialize(durable));
            var accepted = BindCompletedPartyAgencyAcceptedReceipt(run, selected, Row());
            var encoded = Json.Serialize(accepted);
            add("party_agency_report_reads_only_authoritative_provider_receipt", accepted != null
                && ExactSendText(ReadDictionary(accepted, "provider"), "servedModel") == "provider/real"
                && !encoded.Contains("forged-model") && !encoded.Contains(speech) && !encoded.Contains(playerText),
                "Only the registered accepted receipt supplies provider evidence; report output contains neither the raw hint nor dialogue.");

            var missing = Row() with { ProofJson = "{}" };
            var wrongGeneration = Row("another-generation");
            bool missingProof = BindCompletedPartyAgencyAcceptedReceipt(run, selected, missing) == null;
            bool clashesWithDbGeneration = BindCompletedPartyAgencyAcceptedReceipt(run, selected, wrongGeneration) == null;
            bool wrongSpeaker = BindCompletedPartyAgencyAcceptedReceipt(run, selected with { Speaker = "npc-b" }, Row()) == null;
            bool wrongCorrelation = BindCompletedPartyAgencyAcceptedReceipt(run, selected with { Correlation = "other-command" }, Row()) == null;
            bool wrongLease = BindCompletedPartyAgencyAcceptedReceipt(run, selected with
                { Execution = selected.Execution with { ExecutionLeaseId = replacement } }, Row()) == null;
            bool wrongReply = BindCompletedPartyAgencyAcceptedReceipt(run, selected with { SpeechHash = witnessHash }, Row()) == null;
            provider["providerAuthoredSpeechSha256"] = witnessHash;
            bool wrongProviderSpeech = BindCompletedPartyAgencyAcceptedReceipt(run, selected, Row()) == null;
            provider["providerAuthoredSpeechSha256"] = selected.SpeechHash;
            provider["responseIdentitySha256"] = "not-a-digest";
            bool wrongProviderIdDigest = BindCompletedPartyAgencyAcceptedReceipt(run, selected, Row()) == null;
            add("party_agency_report_rejects_mismatched_scope_and_digests", missingProof && clashesWithDbGeneration
                && wrongSpeaker && wrongCorrelation && wrongLease && wrongReply && wrongProviderSpeech && wrongProviderIdDigest,
                "Missing database evidence, changed load scope, wrong speaker/lease/turn and invalid exact digests fail closed.");

            provider["status"] = "diagnostic";
            provider["reason"] = "served_model_unavailable";
            var diagnostic = ReadDictionary(BindCompletedPartyAgencyAcceptedReceipt(run, selected, Row()), "provider");
            add("party_agency_report_keeps_transport_diagnostic_nonpositive",
                ExactSendText(diagnostic, "status") == "diagnostic" && diagnostic.Count == 2,
                "An originally diagnostic provider receipt exposes only its bounded diagnostic disposition.");

            send["executionRetryGeneration"] = 1;
            bool stale = !EnumerateCompletedPartyAgencySendSelectors(run).Any();
            send["executionRetryGeneration"] = 0;
            send["status"] = "accepted";
            bool incomplete = !EnumerateCompletedPartyAgencySendSelectors(run).Any();
            add("party_agency_report_excludes_incomplete_and_superseded_sends", stale && incomplete,
                "No selector survives a newer retry generation or an unfinished send.");

            string partyCorrelation = correlation + "-s1-t1-npc-" + speaker;
            raw["correlationId"] = partyCorrelation;
            var speakerReply = TestDict("ok", true, "heroId", speaker, "participation", "speak",
                "text", speech, "correlationId", partyCorrelation, "rawResponse", raw);
            opened["mode"] = ReadDictionary(opened, "result")["mode"] = send["mode"] = "party_chat";
            send["status"] = "completed";
            send["correlationIds"] = new[] { partyCorrelation };
            send["result"] = TestDict("ok", true, "playerText", playerText, "replies", new[] { speakerReply });
            var partyCandidates = EnumerateCompletedPartyAgencySendSelectors(run).ToList();
            speakerReply["heroId"] = "different-speaker";
            bool mismatchedPartySpeaker = !EnumerateCompletedPartyAgencySendSelectors(run).Any();
            add("party_agency_report_selects_each_speaker_unit_not_shared_exchange", partyCandidates.Count == 1
                && partyCandidates[0].UnitId == unitId && partyCandidates[0].Correlation == partyCorrelation
                && partyCandidates[0].Speaker == speaker && mismatchedPartySpeaker,
                "Party results select each exact accepted speaker unit, independent of the shared group exchange.");
        }

        internal static List<Dictionary<string, object>> RunLiveInteractionTestSelfTests()
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
            AddLiveTestExecutionObservationChecks(add);
            AddPartyAgencySendAuthorityChecks(add);
            AddCompletedPartyAgencySendEvidenceChecks(add);
            var economyRun = new Dictionary<string, object>(fakeRun) { ["mode"] = "passive_world" };
            foreach (string profile in new[] { "civilian_crew_probe", "production_probe", "relief_probe", "market_food_probe", "household_probe" })
            {
                var probe = TestDict("operation", "world_economy_test", "profile", profile,
                    "campaignTestRunId", "owned-run", "expectedSaveName", "Current", "settlementId", "fixture-location");
                var accepted = NormalizeLiveTestCommand(economyRun, probe);
                bool rejectsOtherMode = NormalizeLiveTestCommand(fakeRun, probe) == null;
                probe.Remove("settlementId");
                add("economy_" + profile, accepted != null && ReadString(accepted, "profile", "") == profile
                    && ReadString(accepted, "settlementId", "") == "fixture-location" && rejectsOtherMode
                    && NormalizeLiveTestCommand(economyRun, probe) == null,
                    "The server admits the exact bounded economy probe only in passive-world mode with a locality.");
            }
            var shipFunding = TestDict("operation", "world_economy_test", "profile", "ship_funding_probe", "partyId", "real-npc-party",
                "settlementId", "friendly-port", "campaignTestRunId", "owned-run", "expectedSaveName", "Current");
            var normalizedShip = NormalizeLiveTestCommand(economyRun, shipFunding);
            bool shipAccepted = normalizedShip != null && ReadString(normalizedShip, "profile", "") == "ship_funding_probe"
                && ReadString(normalizedShip, "partyId", "") == "real-npc-party"
                && ReadString(normalizedShip, "settlementId", "") == "friendly-port"
                && ReadString(normalizedShip, "campaignTestRunId", "") == "owned-run";
            var wrongShipRun = CloneDictionary(economyRun); wrongShipRun["mode"] = "individual_chat";
            var wrongShipInput = CloneDictionary(shipFunding); wrongShipInput["mode"] = "individual_chat";
            bool shipRejectsMode = NormalizeLiveTestCommand(wrongShipRun, shipFunding) == null
                && NormalizeLiveTestCommand(economyRun, wrongShipInput) == null;
            bool shipRejectsMissing = true;
            foreach (string required in new[] { "partyId", "settlementId", "campaignTestRunId", "expectedSaveName" })
            {
                var incomplete = CloneDictionary(shipFunding); incomplete.Remove(required);
                shipRejectsMissing &= NormalizeLiveTestCommand(economyRun, incomplete) == null;
                foreach (string blank in new[] { "", "   " })
                {
                    var empty = CloneDictionary(shipFunding); empty[required] = blank;
                    shipRejectsMissing &= NormalizeLiveTestCommand(economyRun, empty) == null;
                }
            }
            shipFunding["source"] = "gold_barter";
            add("economy_ship_funding_probe", shipAccepted && shipRejectsMode && shipRejectsMissing
                && NormalizeLiveTestCommand(economyRun, shipFunding) == null,
                "The fixed ship fixture requires passive-world enrollment, exact NPC party/port and no selectable money source.");
            foreach (string source in new[] { "lord_purse", "kingdom_support", "market_balance", "loot_sale", "horse_sale", "prisoner_sale", "gold_barter", "market_purchase" })
            {
                var probe = TestDict("operation", "world_economy_test", "profile", "funding_probe", "source", source,
                    "partyId", "npc-party", "settlementId", "safe-town", "campaignTestRunId", "owned-run", "expectedSaveName", "Current");
                bool accepted = NormalizeLiveTestCommand(economyRun, probe) != null;
                probe["source"] = "reset_treasury";
                add("economy_funding_" + source, accepted && NormalizeLiveTestCommand(economyRun, probe) == null,
                    "Funding probes admit only fixed native money routes, never arbitrary treasury mutation.");
            }
            foreach (string source in new[] { "cohort_preview", "cohort_enable", "cohort_disable" })
            {
                var control = TestDict("operation", "world_economy_test", "profile", "cohort_control", "source", source,
                    "campaignTestRunId", "owned-run", "expectedSaveName", "Current");
                bool accepted = NormalizeLiveTestCommand(economyRun, control) != null;
                control["source"] = "reset_treasury";
                bool rejected = NormalizeLiveTestCommand(economyRun, control) == null;
                control["source"] = source; control.Remove("expectedSaveName");
                add("economy_" + source, accepted && rejected && NormalizeLiveTestCommand(economyRun, control) == null,
                    "Session-only cohort isolation retains exact enrollment and admits no resource mutation source.");
            }
            var configuredCohort = TestDict("operation", "world_economy_test", "profile", "cohort_control", "source", "cohort_enable",
                "campaignTestRunId", "owned-run", "expectedSaveName", "Current", "cohortDurationDays", 100d,
                "peaceKingdomIds", "aserai,empire_s", "warPairs", "empire:khuzait,empire_w:battania");
            bool configurableAccepted = NormalizeLiveTestCommand(economyRun, configuredCohort) != null;
            configuredCohort["cohortDurationDays"] = "100";
            bool stringDurationRejected = NormalizeLiveTestCommand(economyRun, configuredCohort) == null;
            configuredCohort["cohortDurationDays"] = double.NaN;
            bool nanRejected = NormalizeLiveTestCommand(economyRun, configuredCohort) == null;
            configuredCohort["cohortDurationDays"] = 6301d;
            bool excessRejected = NormalizeLiveTestCommand(economyRun, configuredCohort) == null;
            configuredCohort["cohortDurationDays"] = 100d; configuredCohort["cohortSuppressRebellions"] = "false";
            bool stringFlagRejected = NormalizeLiveTestCommand(economyRun, configuredCohort) == null;
            add("economy_configurable_cohorts", configurableAccepted && stringDurationRejected && nanRejected && excessRejected && stringFlagRejected,
                "Cohort horizon and pair selection are bounded, typed and provider-free; malformed requests never dispatch.");
            var replacement = TestDict("operation", "world_economy_test", "profile", "replacement_setup", "partyId", "real-npc-party",
                "campaignTestRunId", "owned-run", "expectedSaveName", "Current");
            bool replacementAccepted = NormalizeLiveTestCommand(economyRun, replacement) != null;
            replacement.Remove("partyId");
            bool replacementNeedsParty = NormalizeLiveTestCommand(economyRun, replacement) == null;
            replacement["partyId"] = "real-npc-party"; replacement["source"] = "gold_barter";
            bool replacementRejectsSource = NormalizeLiveTestCommand(economyRun, replacement) == null;
            replacement["source"] = ""; replacement.Remove("expectedSaveName");
            add("economy_replacement_setup", replacementAccepted && replacementNeedsParty && replacementRejectsSource
                && NormalizeLiveTestCommand(economyRun, replacement) == null,
                "Bounded field-loss scenario requires an exact enrolled Current and NPC party; no arbitrary funding source.");
            var raidSetup = TestDict("operation", "world_economy_test", "profile", "raid_setup", "partyId", "real-enemy-party",
                "settlementId", "one-npc-village", "campaignTestRunId", "owned-run", "expectedSaveName", "Current");
            bool raidAccepted = NormalizeLiveTestCommand(economyRun, raidSetup) != null;
            raidSetup.Remove("partyId");
            bool raidNeedsActor = NormalizeLiveTestCommand(economyRun, raidSetup) == null;
            raidSetup["partyId"] = "real-enemy-party"; raidSetup["source"] = "gold_barter";
            add("economy_raid_setup", raidAccepted && raidNeedsActor && NormalizeLiveTestCommand(economyRun, raidSetup) == null,
                "One-village raid setup requires an enrolled paused save, exact locality and existing enemy actor; no arbitrary resource source.");
            var partialFood=TestDict("operation","world_economy_test","profile","raid_partial_setup","partyId","real-enemy-party",
                "settlementId","one-npc-village","damagePercent",10,"campaignTestRunId","owned-run","expectedSaveName","Current");
            bool partialAccepted=NormalizeLiveTestCommand(economyRun,partialFood)!=null;
            partialFood["damagePercent"]=10.5;bool fractionRejected=NormalizeLiveTestCommand(economyRun,partialFood)==null;
            partialFood["damagePercent"]="10";bool stringRejected=NormalizeLiveTestCommand(economyRun,partialFood)==null;
            partialFood["damagePercent"]=100;bool excessiveRejected=NormalizeLiveTestCommand(economyRun,partialFood)==null;
            add("economy_partial_food_bounds",partialAccepted&&fractionRejected&&stringRejected&&excessiveRejected,
                "Native partial raid setup accepts fixed integer applied-buffer cases only; no string, fraction or excessive damage.");
            var structuredEnrollment = new Dictionary<string, object> { ["source"] = "reign_mcp_structured_actions", ["campaignId"] = "self",
                ["campaignTestRunId"] = "owned-run", ["timelineId"] = "test", ["disposableSaveName"] = "Current", ["protectedBaselineSaveName"] = "Baseline" };
            var structuredRun = new Dictionary<string, object>(fakeRun) { ["mode"] = "structured_actions", ["enrollment"] = structuredEnrollment };
            var structuredInput = new Dictionary<string, object> { ["operation"] = "structured_action_test", ["enrollment"] = new Dictionary<string, object>() };
            var structuredCommand = NormalizeLiveTestCommand(structuredRun, structuredInput);
            var lifeEnrollment = CloneDictionary(structuredEnrollment);
            lifeEnrollment["source"] = "reign_mcp_npc_lives"; lifeEnrollment["recoveryCheckpointVerified"] = true;
            var lifeRun = new Dictionary<string, object>(fakeRun) { ["mode"] = "npc_lives", ["enrollment"] = lifeEnrollment };
            var lifeInput = TestDict("operation", "npc_lives_test", "enrollment", TestDict("source", "forged"));
            var lifeCommand = NormalizeLiveTestCommand(lifeRun, lifeInput);
            add("npc_lives_enrollment", lifeCommand != null && ReferenceEquals(lifeCommand["enrollment"], lifeEnrollment)
                && NormalizeLiveTestCommand(fakeRun, lifeInput) == null, "NPC native checks inherit enrollment from their isolated run.");
            lifeEnrollment["recoveryCheckpointVerified"] = false;
            add("npc_lives_checkpoint", NormalizeLiveTestCommand(lifeRun, lifeInput) == null, "Native combat controls require a retained recovery checkpoint.");
            var gossipEnrollment = CloneDictionary(structuredEnrollment);
            gossipEnrollment["source"] = "reign_mcp_personal_gossip"; gossipEnrollment["recoveryCheckpointVerified"] = true;
            var gossipRun = new Dictionary<string, object>(fakeRun) { ["mode"] = "personal_gossip", ["enrollment"] = gossipEnrollment };
            var gossipInput = TestDict("operation", "personal_gossip_test", "enrollment", TestDict("source", "forged"));
            var gossipCommand = NormalizeLiveTestCommand(gossipRun, gossipInput);
            add("personal_gossip_enrollment", gossipCommand != null && ReferenceEquals(gossipCommand["enrollment"], gossipEnrollment)
                && NormalizeLiveTestCommand(fakeRun, gossipInput) == null, "Gossip leases inherit exact run enrollment and reject a normal dialogue route.");
            gossipEnrollment["recoveryCheckpointVerified"] = false;
            add("personal_gossip_checkpoint", NormalizeLiveTestCommand(gossipRun, gossipInput) == null, "Gossip setup requires retained recovery.");
            gossipEnrollment["recoveryCheckpointVerified"] = true; gossipEnrollment["disposableSaveName"] = "Baseline";
            add("personal_gossip_baseline", NormalizeLiveTestCommand(gossipRun, gossipInput) == null, "Protected baseline cannot be enrolled as gossip Current.");
            bool badPhase = false;
            try { DialogueService.ValidatePersonalGossipTestCase(TestDict("caseId", "self", "phase", "snapshot", "draws", TestDict("share", 1))); }
            catch (ArgumentException) { badPhase = true; }
            add("personal_gossip_wrong_phase", badPhase, "Observation cannot smuggle fixture controls.");
            add("personal_gossip_draw_default", DialogueService.GossipDecisionRoll(TestDict(), "self", "share") == DialogueService.GossipRoll("self", "share")
                && DialogueService.GossipDecisionDraw(TestDict(), "self", "friendly_recipient") == DialogueService.GossipDraw("self", "friendly_recipient"), "No fixture preserves the exact production draw.");
            add("personal_gossip_saved_control", DialogueService.GossipDecisionRoll(TestDict("operatorTestControls", TestDict("draws", TestDict("share", 1))), "self", "share") == 1
                && DialogueService.GossipDecisionRoll(TestDict("operatorTestControls", TestDict("draws", TestDict("share", 0))), "self", "share") == DialogueService.GossipRoll("self", "share"), "Only bounded persisted stage values are accepted.");
            var gossipNow = DateTimeOffset.UtcNow;
            var gossipJournal = TestDict("activeSaveName", "Current", "gameInstanceId", "one", "restoreGeneration", 0L, "memoryRestoreGeneration", "before");
            var gossipRuntime = TestDict("activeSaveName", "Current", "gameInstanceId", "one");
            add("personal_gossip_control_generation", DialogueService.GossipTestControlsMatch(gossipJournal, gossipRuntime, 0, "before", gossipNow, gossipNow)
                && !DialogueService.GossipTestControlsMatch(gossipJournal, gossipRuntime, 0, "after", gossipNow, gossipNow)
                && !DialogueService.GossipTestControlsMatch(gossipJournal, gossipRuntime, 1, "before", gossipNow, gossipNow),
                "A server restart cannot resurrect uncaptured controls after a persisted restore, even if the process counter returns to zero.");
            add("personal_gossip_control_scope", !DialogueService.GossipTestControlsMatch(gossipJournal, TestDict("activeSaveName", "Baseline", "gameInstanceId", "one"), 0, "before", gossipNow, gossipNow)
                && !DialogueService.GossipTestControlsMatch(gossipJournal, TestDict("activeSaveName", "Current", "gameInstanceId", "other"), 0, "before", gossipNow, gossipNow)
                && !DialogueService.GossipTestControlsMatch(gossipJournal, gossipRuntime, 0, "before", gossipNow.AddHours(-13), gossipNow),
                "Captured saved decisions are separate; uncaptured controls expire and cannot cross Current or native instance.");
            add("structured_action_enrollment", structuredCommand != null && ReferenceEquals(structuredCommand["enrollment"], structuredEnrollment)
                && NormalizeLiveTestCommand(fakeRun, structuredInput) == null,
                "Structured commands inherit the verified run enrollment; a per-command replacement or dialogue-mode route cannot bypass isolation.");
            structuredEnrollment["disposableSaveName"] = "Baseline";
            add("structured_action_baseline_protected", NormalizeLiveTestCommand(structuredRun, structuredInput) == null,
                "A structured test cannot target its protected baseline even with a valid run identity.");
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
            add("modes", LiveTestModes.SetEquals(new[] { "individual_chat", "party_chat", "social_event", "wilderness_event", "social_balance", "passive_world", "correspondence", "court_event", "spymaster", "ambassador_official", "kingdom_event", "campaign_command", "government", "party_agency", "structured_actions", "npc_lives", "personal_gossip" }), "Only implemented production interaction adapters and isolated deterministic harness adapters are registered in the transport contract.");
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
                OperationsService.SanitizeAuditValue(relationshipAuditFixture, 0)
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
            var clockEnrollment = new Dictionary<string, object>
            {
                ["source"] = "reign_mcp_conversation_clock", ["campaignId"] = "clock-campaign",
                ["timelineId"] = "clock-timeline", ["campaignTestRunId"] = "clock-test",
                ["disposableSaveName"] = "ReignTest_Clock_Current", ["protectedBaselineSaveName"] = "Baseline",
                ["recoveryCheckpointVerified"] = true
            };
            var clockRun = new Dictionary<string, object>
            {
                ["mode"] = "individual_chat", ["campaignId"] = "clock-campaign", ["enrollment"] = clockEnrollment
            };
            var clockStep = new Dictionary<string, object>
            {
                ["operation"] = "conversation_clock_probe", ["profile"] = "pulse", ["probeId"] = "clock-one",
                ["expectedSaveName"] = "ReignTest_Clock_Current", ["confirmation"] = "probe Reign conversation clock on disposable save"
            };
            var clockAccepted = NormalizeLiveTestCommand(clockRun, clockStep);
            add("clock_probe_enrolled", clockAccepted != null && ReferenceEquals(clockAccepted["enrollment"], clockEnrollment), "Clock diagnostic keeps the verified run enrollment.");
            foreach (string key in new[] { "source", "campaignId", "timelineId", "campaignTestRunId", "disposableSaveName", "protectedBaselineSaveName", "recoveryCheckpointVerified" })
            {
                var invalidEnrollment = CloneDictionary(clockEnrollment); invalidEnrollment.Remove(key);
                var invalidRun = CloneDictionary(clockRun); invalidRun["enrollment"] = invalidEnrollment;
                add("clock_probe_missing_" + key, NormalizeLiveTestCommand(invalidRun, clockStep) == null, "Missing clock enrollment evidence is rejected.");
            }
            foreach (string key in new[] { "minutes", "days", "ticks" })
            {
                var invalidStep = CloneDictionary(clockStep); invalidStep[key] = 1;
                add("clock_probe_duration_" + key, NormalizeLiveTestCommand(clockRun, invalidStep) == null, "The bounded clock probe cannot select an arbitrary time jump.");
            }
            foreach (string key in new[] { "confirmation", "probeId", "expectedSaveName", "profile" })
            {
                var invalidStep = CloneDictionary(clockStep); invalidStep[key] = "";
                add("clock_probe_invalid_" + key, NormalizeLiveTestCommand(clockRun, invalidStep) == null, "Missing clock operation identity is rejected.");
            }
            var baselineEnrollment = CloneDictionary(clockEnrollment); baselineEnrollment["disposableSaveName"] = "Baseline";
            var baselineRun = CloneDictionary(clockRun); baselineRun["enrollment"] = baselineEnrollment;
            var baselineStep = CloneDictionary(clockStep); baselineStep["expectedSaveName"] = "Baseline";
            add("clock_probe_protected_baseline", NormalizeLiveTestCommand(baselineRun, baselineStep) == null, "The immutable baseline cannot become a clock fixture.");
            var wrongClockMode = CloneDictionary(clockRun); wrongClockMode["mode"] = "correspondence";
            add("clock_probe_letters_rejected", NormalizeLiveTestCommand(wrongClockMode, clockStep) == null, "The diagnostic never runs in correspondence.");
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
                DialogueService.ConversationReadinessGates.ContainsKey("structural_pipeline")
                && DialogueService.ConversationReadinessGates.Values.All(gate => gate.Item1 <= 40)
                && DialogueService.ConversationReadinessGates["structural_pipeline"].Item1 == 40
                && Math.Abs(DialogueService.ConversationReadinessGates["structural_pipeline"].Item2 - 1d) < 0.0001
                && DialogueService.ConversationReadinessGates.ContainsKey("dynamic_characteristics")
                && DialogueService.ConversationReadinessGates["dynamic_characteristics"].Item2 >= 0.95
                && DialogueService.ConversationReadinessGates.ContainsKey("clan_tier_recognition")
                && DialogueService.ConversationReadinessGates["clan_tier_recognition"].Item1 == 30
                && DialogueService.ConversationReadinessGates.ContainsKey("manipulation_capabilities")
                && DialogueService.ConversationReadinessGates["manipulation_capabilities"].Item1 == 40,
                "Readiness promotion requires the declared fixed samples and deterministic or 95-percent pass gates, including 30 clan-tier probes.");
            add("readiness_completed_run_eligibility",
                DialogueService.ConversationReadinessRunIsCompleted(new Dictionary<string, object> { ["status"] = "completed" })
                && !DialogueService.ConversationReadinessRunIsCompleted(new Dictionary<string, object> { ["status"] = "running" })
                && DialogueService.ConversationReadinessRunIsTerminalFailure(new Dictionary<string, object> { ["status"] = "cancelled" })
                && DialogueService.ConversationReadinessRunIsTerminalFailure(new Dictionary<string, object> { ["status"] = "interrupted" })
                && DialogueService.ConversationReadinessRunIsTerminalFailure(new Dictionary<string, object> { ["status"] = "failed" })
                && !DialogueService.ConversationReadinessRunIsTerminalFailure(new Dictionary<string, object> { ["status"] = "running" }),
                "Only completed qualification runs contribute samples; cancelled, interrupted, and failed runs remain explicit clean-window failures.");
            List<Dictionary<string, object>> judgeExpected = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["caseId"] = "C1" },
                new Dictionary<string, object> { ["caseId"] = "C2" }
            };
            List<Dictionary<string, object>> judgeRecovered = new List<Dictionary<string, object>>();
            HashSet<string> judgeExpectedIds = new HashSet<string>(
                new[] { "C1", "C2" }, StringComparer.OrdinalIgnoreCase);
            DialogueService.MergeConversationReadinessJudgeCases(judgeRecovered,
                new[] { new Dictionary<string, object> { ["caseId"] = "C2", ["personalityConsistency"] = 0.9d } },
                judgeExpectedIds);
            List<Dictionary<string, object>> judgeMissing =
                DialogueService.MissingConversationReadinessJudgeCases(judgeExpected, judgeRecovered);
            DialogueService.MergeConversationReadinessJudgeCases(judgeRecovered,
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
                !DialogueService.ConversationReadinessCommandPassed(categorizedCommand, categorizedAssertions, new List<Dictionary<string, object>>(), "dynamic_characteristics")
                && DialogueService.ConversationReadinessCommandPassed(categorizedCommand, categorizedAssertions, new List<Dictionary<string, object>>(), "personality_consistency"),
                "A Dynamic Characteristics miss does not incorrectly lower the personality score or another unrelated readiness category.");
            add("readiness_blinded_rubric_is_mandatory",
                !DialogueService.ConversationReadinessCommandPassed(categorizedCommand,
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
                !DialogueService.ConversationReadinessCommandPassed(
                    categorizedCommand,
                    relationshipHistoryAssertions,
                    new List<Dictionary<string, object>>(),
                    "shared_relationship_history"),
                "Shared Relationship History cannot promote from structural prompt evidence alone when its blinded quality and privacy rubric is absent.");
            add("dynamic_characteristic_integrity_distinguishes_refusal",
                !CharactersService.ReplyLikelyIntroducesDynamicCharacteristic(
                    "No. I am not a well you draw from. State your business or go.")
                && !CharactersService.ReplyLikelyIntroducesDynamicCharacteristic(
                    "I mentioned no preference. You want me to sit and tell you what I like and dislike, but I will not trade personal details with you.")
                && !CharactersService.ReplyLikelyIntroducesDynamicCharacteristic(
                    "You ask about my preferences as though I keep a ledger of personal tastes for unaffiliated strangers. I do not owe you a preference.")
                && CharactersService.ReplyLikelyIntroducesDynamicCharacteristic(
                    "I cannot stand courtiers who talk around a thing instead of through it."),
                "Qualification permits direct, metalinguistic, and counterfactual refusals while still detecting an unstored explicit stable preference or aversion.");
            const string keepsakeSpeaker = "lord_6_12";
            const string keepsakeQuote = "I keep a river stone";
            List<Dictionary<string, object>> inferredKeepsake = CharactersService.NormalizeDynamicCharacteristicWrites(
                new Dictionary<string, object>(), keepsakeSpeaker,
                new Dictionary<string, object> { ["name"] = "Abagai" },
                new Dictionary<string, object>(),
                keepsakeQuote + ". Smooth, grey, fits in the palm. I picked it up from a council tent when I was seventeen and have carried it since.");
            List<Dictionary<string, object>> quotedPlayerKeepsake = CharactersService.NormalizeDynamicCharacteristicWrites(
                new Dictionary<string, object>(), keepsakeSpeaker,
                new Dictionary<string, object> { ["name"] = "Abagai" },
                new Dictionary<string, object>(),
                "You told me, \"I keep a blue glass token beneath a cedar box.\" I will remember what you said.");
            var keepsakeCitation = ReadDictionary(inferredKeepsake.FirstOrDefault(), "proposedCitation");
            add("dynamic_characteristic_keepsake_fallback",
                inferredKeepsake.Count == 1
                && ReadString(inferredKeepsake[0], "text", "").Contains("river stone", StringComparison.OrdinalIgnoreCase)
                && ReadString(inferredKeepsake[0], "category", "") == "personal_history"
                && ReadString(inferredKeepsake[0], "status", "") == "proposed"
                && ReadString(keepsakeCitation, "turnId", "") == "current_npc"
                && ReadString(keepsakeCitation, "speakerId", "") == keepsakeSpeaker
                && ReadString(keepsakeCitation, "quote", "") == keepsakeQuote
                && quotedPlayerKeepsake.Count == 0,
                "Keepsake normalization proposes a characteristic with exact NPC speech evidence and excludes quoted player-owned keepsakes."
                + " inferred=" + Json.Serialize(inferredKeepsake) + " quoted=" + Json.Serialize(quotedPlayerKeepsake));
            add("dynamic_characteristic_recall_accepts_grounded_paraphrase",
                CharactersService.HasDynamicTextEvidence(
                    "Abalytos has a strong aversion to people who verbally label their own generosity or fairness before acting, seeing it as calculated self-interest.",
                    "Abalytos calls it an aversion: when people tell him they are fair before they have acted, he stops listening to what they say and watches what they take.")
                && CharactersService.HasDynamicTextEvidence(
                    "As a child, Abagai formed the habit of checking a saddle's girth strap before every mount, after a boy she knew was killed when his loose girth caused him to fall under his horse at age seven. The habit persists into adulthood as a principle against dying carelessly.",
                    "Then my saddle strap. I told you the strap was loose and I was not getting on.")
                && !CharactersService.HasDynamicTextEvidence(
                    "Abalytos keeps a small clay disc from his betrothal.",
                    "Abalytos discussed roads, garrisons, and nearby parties.")
                && !CharactersService.HasDynamicTextEvidence(
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
                !DialogueService.VerifiedIdentityGroundingContradiction(
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
                    !DialogueService.VerifiedIdentityGroundingContradiction(
                        new Dictionary<string, object>
                        {
                            ["reply"] = reply
                        },
                        verifiedAuthorityContext)),
                "A heading that names the verified settlement remains grounded when a room or sub-location follows a dash or comma.");
            add("political_authority_rejects_conflicting_settlement_heading",
                DialogueService.VerifiedIdentityGroundingContradiction(
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "1084 Summer, day 1 - Amitatys\n\nWe can speak plainly here."
                    },
                    verifiedAuthorityContext),
                "A heading that unambiguously names a different settlement still contradicts the verified current location.");
            add("political_authority_accepts_no_one_as_zero_wars",
                !DialogueService.ContainsContradictoryCurrentWarCount(
                    "Battania is at war with the Western Empire; fen Niabar is at war with no one.",
                    0)
                && !DialogueService.ContainsContradictoryCurrentWarCount(
                    "Fen Niabar is at war with none.",
                    0)
                && DialogueService.ContainsContradictoryCurrentWarCount(
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
                !DialogueService.VerifiedIdentityGroundingContradiction(
                    LiveTestCurrentModelOutput(
                        historicalIdentityEnvelope),
                    new Dictionary<string, object>
                    {
                        ["identityView"] =
                            ReadDictionary(
                                historicalIdentityEnvelope,
                                "identityView")
                    })
                && DialogueService.VerifiedIdentityGroundingContradiction(
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
            add("provider_ambiguous_restart_cannot_promote_nested_object",
                restartedJson == null,
                "An ambiguous restart inside an unterminated reply is rejected; nested or quoted objects cannot replace the outer response.");
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

            string sourceRoot = VerificationService.FindVerificationSourceRoot();
            string clientSourceRoot = string.IsNullOrWhiteSpace(sourceRoot) ? "" : Path.Combine(sourceRoot, "ReignBeta");
            Func<string, string> serverSourcePath = fileName => string.IsNullOrWhiteSpace(sourceRoot)
                ? "" : VerificationSourceLocator.ResolveUnique(Path.Combine(sourceRoot, "ReignServer"), fileName, "src");
            Func<string, string> serverSplitSource = fileName => string.IsNullOrWhiteSpace(sourceRoot)
                ? "" : VerificationSourceLocator.ReadServerSplitSourceText(Path.Combine(sourceRoot, "ReignServer"), fileName, "src");
            string passiveWorldControlPath = string.IsNullOrWhiteSpace(sourceRoot) ? "" :
                VerificationSourceLocator.ResolveUnique(
                    Path.Combine(sourceRoot, "ReignServer"),
                    "PassiveWorldControl.cs",
                    "ReignLiveTest/Features/WorldSimulation");
            string notableMbtiPath = serverSourcePath("NotableMbtiProfiles.cs");
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
            string postgreSqlSaveSyncPath = serverSourcePath("PostgreSqlSaveSync.cs");
            string kingdomLeaderDiplomacyPath = serverSourcePath("KingdomLeaderDiplomacy.cs");
            string hostSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string hostSourceScoped = VerificationSourceLocator.ReadClientTypeSourceText(clientSourceRoot, "ReignLiveInteractionTestHost");
            string eligibilitySource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string partySessionSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string individualManagerSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string individualVmSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string uiCalibrationSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string auditRunnerSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string correspondenceSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string correspondenceSourceScoped = VerificationSourceLocator.ReadClientTypeSourceText(clientSourceRoot, "ReignServerClient");
            string socialEventClientSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string socialEventClientSourceScoped = VerificationSourceLocator.ReadClientTypeSourceText(clientSourceRoot, "ReignServerClient");
            string actionValidatorSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string identitySystemSource = serverSplitSource("IdentitySystem.cs");
            string socialReputationSource = serverSplitSource("SocialReputation.cs");
            string worldRelationshipSource = serverSplitSource("WorldRelationshipModel.cs");
            string aiBehaviorSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string settingsSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string socialVmSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string subModuleSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string liveClientSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string diplomacyBehaviorSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string passiveWorldControlSource = File.Exists(passiveWorldControlPath) ? File.ReadAllText(passiveWorldControlPath) : "";
            string initializationGateSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string campaignPreparationSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string campaignPreparationSourceScoped = VerificationSourceLocator.ReadClientTypeSourceText(clientSourceRoot, "ReignCampaignPreparationCampaignBehavior");
            string characterInitializationSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string characterInitializationSourceScoped = VerificationSourceLocator.ReadClientTypeSourceText(clientSourceRoot, "ReignCharacterEditorCampaignBehavior");
            string notablePopupSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string notableMbtiSource = File.Exists(notableMbtiPath) ? File.ReadAllText(notableMbtiPath) : "";
            string serverProgramSource = string.IsNullOrWhiteSpace(sourceRoot)
                ? "" : VerificationSourceLocator.ReadServerProgramAreaSourceText(Path.Combine(sourceRoot, "ReignServer"));
            string qualificationRunnerSource = File.Exists(qualificationRunnerPath)
                ? VerificationSourceLocator.ReadServerSplitSourceText(Path.Combine(sourceRoot, "ReignServer"), "QualificationRunner.cs", "ReignLiveTest/Features/Dialogue")
                : "";
            string serverLiveTestSource = File.Exists(serverLiveTestPath) ? serverSplitSource("LiveInteractionTest.cs") : "";
            string lifecycleCliSource = File.Exists(lifecycleCliPath)
                ? VerificationSourceLocator.ReadServerSplitSourceText(Path.Combine(sourceRoot, "ReignServer"), "Program.cs", "ReignLiveTest")
                : "";
            string continuousRelationshipSource = File.Exists(continuousRelationshipPath) ? File.ReadAllText(continuousRelationshipPath) : "";
            string saveSyncSource = serverSplitSource("SaveSync.cs");
            string saveSyncApplySource = string.IsNullOrWhiteSpace(sourceRoot)
                ? "" : VerificationSourceLocator.ReadServerSplitMemberSourceText(
                    Path.Combine(sourceRoot, "ReignServer"), "SaveSync.cs", "ApplyPostgreSqlSaveSync", "src");
            string postgreSqlSaveSyncSource = File.Exists(postgreSqlSaveSyncPath) ? File.ReadAllText(postgreSqlSaveSyncPath) : "";
            string campaignBackupsSource = serverSplitSource("CampaignBackups.cs");
            string worldDiplomacyDirectorSource = serverSplitSource("WorldDiplomacyDirector.cs");
            string kingdomLeaderDiplomacySource = File.Exists(kingdomLeaderDiplomacyPath) ? File.ReadAllText(kingdomLeaderDiplomacyPath) : "";
            string saveBehaviorSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string saveCoordinatorSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string mainThreadSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string nativeUiCalibrationSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string relationshipBehaviorSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string rebellionBehaviorSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string rulerBehaviorSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string rulerBehaviorSourceScoped = VerificationSourceLocator.ReadClientTypeSourceText(clientSourceRoot, "ReignRulerReputationCampaignBehavior");
            string debugActionsSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string eventBehaviorSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            string familyBehaviorSource = VerificationSourceLocator.ReadClientSourceText(clientSourceRoot);
            bool adapterParity = hostSource.Contains("ReignIndividualChatScreenVM")
                && hostSource.Contains("ReignPartyChatScreenVM")
                && hostSource.Contains("ReignSocialEventScreenVM")
                && hostSource.Contains("StartLiveTestWildernessEventAsync")
                && hostSource.Contains("SendAutomationLineAsync")
                && !hostSourceScoped.Contains("McmTestModeEnabled");
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
            bool typedInjectedParity = socialVmSource.Contains("await SendLineAsync(InputText, string.Empty, ActionDraft == null ? null : ReignBeta.Actions.ReignActionService.Copy(ActionDraft));")
                && socialVmSource.Contains("SendAutomationLineAsync(string text, string correlationId) => SendLineAsync(text, correlationId, null);")
                && socialVmSource.Contains("SendLineAsync(string text, string correlationId, Reign.Core.Contracts.Actions.ActionAgreement actionDraft)")
                // The send keeps its copied draft and now also carries the screen's load ownership token.
                && socialVmSource.Contains("return await RequestGroupTurnAsync(activeHeroes, text, correlationId, actionDraft, owner)");
            add("typed_injected_parity", typedInjectedParity, "Typed and injected social-event input share the production send pipeline; typed input preserves its copied action draft and ordinary injected input supplies none.");
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
                    "internal static void ReconcileSocialRelationshipForObserverSubject(")
                && socialReputationSource.Contains(
                    "new[] { subjectId }, worldDay);")
                && worldRelationshipSource.Contains(
                    "internal static Dictionary<string, object> ResolveEffectiveAttitude(")
                && worldRelationshipSource.Contains(
                    "int effective = hasPair ? Clamp(personal + standingValue"),
                "A verified foreground encounter may refresh only the subject's indexed Public Standing; effective attitude remains a pure directional read and no observer projection is copied into relationship state.");
            add("ruler_diplomacy_uses_personal_affinity_only",
                worldRelationshipSource.Contains(
                    "internal static Dictionary<string, object> ResolveRulerDiplomaticAttitude(")
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
                && !hostSourceScoped.Contains("Utilities.DoDelayedexit(delayMilliseconds)")
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
                && saveCoordinatorSource.Contains("RunLoadHandshakeAsync(generation, loadPayload, campaign, scope)")
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
                !campaignPreparationSourceScoped.Contains("_mapScreenStableSeconds < 0.5f")
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
                && !characterInitializationSourceScoped.Contains("TrySealAndMarkReady")
                && notablePopupSource.Contains("public static void RequireUntilReady()")
                && notablePopupSource.Contains("_keepOpenRequested")
                && notablePopupSource.Contains("restoring it.")
                && notablePopupSource.Contains("ExecuteRetry")
                && subModuleSource.Contains("\"campaign preparation\"")
                && (subModuleSource.Contains("ReignCampaignPreparationCampaignBehavior.Instance?.ApplicationTick(dt)") || subModuleSource.Contains("ReignServices.CampaignPreparation?.ApplicationTick(dt)"))
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
                && (campaignPreparationSource.Contains("ReignAICampaignBehavior.Instance") || campaignPreparationSource.Contains("ReignServices.AI"))
                && campaignPreparationSource.Contains("PrepareInitialBaselines")
                && (campaignPreparationSource.Contains("ReignRulerReputationCampaignBehavior.Instance") || campaignPreparationSource.Contains("ReignServices.RulerReputation"))
                && (campaignPreparationSource.Contains("ReignRebellionCampaignBehavior.Instance") || campaignPreparationSource.Contains("ReignServices.Rebellion"))
                && (campaignPreparationSource.Contains("ReignWorldDiplomacyCampaignBehavior.Instance") || campaignPreparationSource.Contains("ReignServices.WorldDiplomacy"))
                && (campaignPreparationSource.Contains("ReignSocialEventsCampaignBehavior.Instance") || campaignPreparationSource.Contains("ReignServices.SocialEvents"))
                && (campaignPreparationSource.Contains("ReignFamilyCampaignBehavior.Instance") || campaignPreparationSource.Contains("ReignServices.Family"))
                && (campaignPreparationSource.Contains("ReignCourtPersonalityReputationCampaignBehavior.Instance") || campaignPreparationSource.Contains("ReignServices.CourtPersonalityReputation"))
                && aiBehaviorSource.Contains("internal void PrepareInitialState()")
                && relationshipBehaviorSource.Contains("internal void PrepareInitialBaselines()")
                && rebellionBehaviorSource.Contains("internal void PrepareInitialState()")
                && rulerBehaviorSource.Contains("internal void PrepareInitialState()")
                && eventBehaviorSource.Contains("internal void PrepareInitialState()")
                && familyBehaviorSource.Contains("internal void PrepareInitialState()"),
                "Calendar, relationship, family, rebellion, reputation, social-event, court, and diplomacy foundations are owned by the blocking preparation coordinator instead of racing as deferred post-release session work.");
            add("ruler_reputation_sparse_weekly_windows",
                rulerBehaviorSourceScoped.Split(new[] { "List<IGrouping<string, JObject>> seven = groups.SelectMany" }, StringSplitOptions.None).Length == 3
                && rulerBehaviorSourceScoped.Split(new[] { "if (seven.Count == 0) return;" }, StringSplitOptions.None).Length >= 3
                && !rulerBehaviorSourceScoped.Contains(".GroupBy(x => g.Key).First()"),
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
                && liveClientSource.Contains("[\"nativeSave\"] = HeartbeatSection(sections, \"nativeSave\", () => BuildNativeSaveDiagnostics())")
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
                && passiveWorldControlSource.Contains("WaitForPassiveWorldPostSaveRuntime(campaignId, timelineId, saveName)")
                && passiveWorldControlSource.Contains("PassiveWorldPostSaveRuntimeQuiesced(finalRuntime, campaignId, timelineId, saveName)")
                && passiveWorldControlSource.Contains("PassiveWorldNpcLivesBlocker(runtime, campaignId, timelineId).Length == 0")
                && passiveWorldControlSource.Contains("DateTime.UtcNow.AddSeconds(45)")
                && passiveWorldControlSource.Contains("TimeSpan.FromSeconds(5)")
                && passiveWorldControlSource.Contains("ReadLong(relationshipClient, \"clientOutboxDays\", -1) == 0")
                && passiveWorldControlSource.Contains("!ReadBoolean(relationshipClient, \"uploadInFlight\")")
                && passiveWorldControlSource.Contains("ReadLong(worldHistory, \"pendingCount\", -1) == 0")
                && liveClientSource.Contains("[\"relationshipClient\"] = HeartbeatSection(sections, \"relationships\", () => BuildRelationshipOutboxDiagnostics())")
                && passiveWorldControlSource.Contains("String(result, \"saveName\")")
                && passiveWorldControlSource.Contains("ReadDoubleValue(result, \"worldDay\", -1d)")
                && passiveWorldControlSource.Contains("ReadObject(result, \"nativeSave\")")
                && passiveWorldControlSource.Contains("ReadObject(result, \"saveFinalization\")")
                && passiveWorldControlSource.Contains("CompactPassiveWorldSave(save, finalRuntime)"),
                "Passive-world status and save receipts stay bounded. Save receipts project the completed save command's nested result and require five stable seconds of current post-save runtime telemetry with NPC Lives, Save Sync, diplomacy, relationship input, World History, and native saving all idle, so a successful rolling checkpoint cannot report an empty save name, invalid day, offline placeholder runtime, stale in-flight save state, or a late final-day relationship outbox capture.");
            add("save_sync_background_writer_barrier_and_bounded_manifest",
                continuousRelationshipSource.Contains("TryProcessNextRelationshipDayUnderCampaignGate()")
                && continuousRelationshipSource.Contains("CampaignDataGate.EnterReadLock()")
                && continuousRelationshipSource.Contains("CampaignDataGate.ExitReadLock()")
                && saveSyncSource.Contains("return SaveSyncLoadPostgreSql(payload, dryRun)")
                && postgreSqlSaveSyncSource.Contains("return ApplyPostgreSqlSaveSync(")
                && saveSyncApplySource.Contains("PublishSaveSyncPhase(op, \"moving_active_files\")")
                && saveSyncApplySource.Contains("PublishSaveSyncPhase(op, \"installing_target_files\")")
                && saveSyncApplySource.Contains("op.FailedPhase = op.Phase")
                && saveSyncApplySource.Contains("[\"failedPhase\"] = op.FailedPhase")
                && campaignBackupsSource.Contains("Parallel.For(")
                && campaignBackupsSource.Contains("MaxDegreeOfParallelism = Math.Max(1, Math.Min(8, Environment.ProcessorCount))"),
                "The continuous relationship writer participates in Save Sync's global campaign barrier, failures report their exact journaled swap phase, and exact manifest hashing uses bounded parallelism for large character stores.");
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
                    "lock (RelationshipsService.CampaignRelationshipWriteLock(campaignId))",
                    StringComparison.Ordinal)
                    < kingdomLeaderDiplomacySource.IndexOf(
                        "DiplomacyStore.CountRulerDiplomaticIncidents2(connection",
                        StringComparison.Ordinal),
                "Diplomacy action receipts release FileLock before database activity or relationship feedback, then durably mark feedback after its idempotent incident application.");
            add("notable_initialization_bulk_io",
                notableMbtiSource.Contains("connection.BeginTransaction()")
                && notableMbtiSource.Contains("UpdateCharacterIndexBatch(campaignId, indexProfiles)")
                && notableMbtiSource.Contains("notable_mbti.initialize.performance")
                && notableMbtiSource.Contains("notable_mbti.confirm.performance")
                && serverProgramSource.Contains("internal static void UpdateCharacterIndexBatch"),
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
            // Other ReignServerClient members may gate on a non-zero delta; the social-event receipt block must not.
            string socialEventNativeReceiptSource = VerificationSourceLocator.ReadClientMemberSourceText(
                clientSourceRoot, "ReignServerClient", "RequestSocialEventTurnAsync");
            add("zero_sum_native_relationship_acknowledgement",
                correspondenceSource.Contains("if (subject != null && target != null && subject != target)")
                && !correspondenceSourceScoped.Contains("subject != target && delta != 0")
                // The game-thread block refuses only a missing participant; it never gates on the delta.
                && socialEventClientSource.Contains("if (subject == null || target == null) return false;")
                && !socialEventNativeReceiptSource.Contains("subject != null && target != null && delta != 0")
                && !socialEventClientSourceScoped.Contains("|| delta == 0) return false;")
                && correspondenceSource.Contains("/relationships/conversation/native-receipt")
                && socialEventClientSource.Contains("/relationships/conversation/native-receipt"),
                "Opposite directional judgments that net to zero still acknowledge the atomic native hero-pair receipt without applying a redundant native relation change.");
            add("native_relationship_acknowledgement_captures_before_and_after",
                correspondenceSource.Contains("ApplyConversationRelationOnce(receiptIds.Values<string>()")
                && socialEventClientSource.Contains("ApplyConversationRelationOnce(adjudicationReceiptIds.Values<string>()")
                && correspondenceSource.Contains("reign-native-relationship-receipt-v2")
                && socialEventClientSource.Contains("reign-native-relationship-receipt-v2")
                && relationshipBehaviorSource.Contains("NativeRelationEffectLedger.Apply(")
                && relationshipBehaviorSource.Contains("() => subject.GetRelation(target)")
                && relationshipBehaviorSource.Contains("[\"priorNativeRelation\"] = receipt.Before")
                && relationshipBehaviorSource.Contains("[\"nativeRelation\"] = receipt.After")
                && relationshipBehaviorSource.Contains("_reignRelationship_nativeConversationEffectJournal"),
                "Conversation clients use the save-persisted native journal's before/after observations. This checks wiring; native_relation_effect contracts exercise modifiers, caps, partial callback failures and retry behavior.");
            return results;
        }
#endif
    }
}
