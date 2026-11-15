using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Reign.Core.Contracts.Government;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunMemoryEvidenceReplaySelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            results.AddRange(RunValidationNativeArchiveSelfTests());
            void Check(string id, bool passed, object data = null) =>
                results.Add(TestDict("caseId", "memory_evidence_replay_" + id, "passed", passed,
                    "summary", id + (!passed && data != null ? ": " + Json.Serialize(data) : ""), "data", data));
            var settings = TestDict("enableSemanticMemory", false, "enableMinime", false,
                "memoryPrecisionMode", "precision", "memoryPrecisionOrdinaryTokens", 4000);
            foreach (string purpose in new[] { "dialogue_evidence", "dialogue_verification", "dialogue_memory_repair", "context_selector" })
            {
                var blocked = ChatWithLlm(TestDict("requestType", purpose, "model", "obsolete-model"));
                var retiredWire = ReadDictionary(CaptureMemoryReplayProviderRequest(TestDict("requestType", purpose,
                    "model", "obsolete-model"), settings), "providerWireCapture");
                Check("retired_request_never_dispatches_" + purpose, !ReadBool(blocked, "ok", true)
                    && ReadString(blocked, "errorCode", "") == "memory_llm_stage_retired"
                    && ReadInt(blocked, "providerCallCount", -1) == 0 && !retiredWire.ContainsKey("requestJson"));
            }
            Dictionary<string, object> WireBody(Dictionary<string, object> wire) => TryParseJsonObject(ReadString(wire, "requestJson", ""));
            var wireSettings = TestDict("llmProvider", NanoGptProvider, "reasoningMode", "selective",
                "reasoningEffort", "medium", "nanoGptApiKey", "fixture-only-secret", "memoryModel", "summary-model");
            var wireRequest = TestDict("requestType", "dialogue", "model", "writer-model", "temperature", .4d,
                "maxTokens", 8192, "response_format", TestDict("type", "json_object"),
                "messages", new[] { TestDict("role", "user", "content", "Remember my actual answer: the old mill.") });
            string settingsHash = CanonicalJson(wireSettings), requestHash = CanonicalJson(wireRequest);
            var wire = ReadDictionary(CaptureMemoryReplayProviderRequest(wireRequest, wireSettings), "providerWireCapture");
            var wireBody = WireBody(wire);
            Check("writer_wire_preserves_model_reasoning_schema_and_budget",
                ReadString(wireBody, "model", "") == "writer-model" && ReadDouble(wireBody, "temperature", 0) == .4d
                && ReadInt(wireBody, "max_tokens", 0) == 8192 && !ReadBool(wireBody, "stream", true)
                && ReadString(ReadDictionary(wireBody, "reasoning"), "effort", "") == "medium"
                && ReadString(ReadDictionary(wireBody, "response_format"), "type", "") == "json_object");
            Check("writer_wire_capture_is_immutable_hashed_and_credential_free",
                ReadString(wire, "requestSha256", "") == PromptHash(ReadString(wire, "requestJson", ""))
                && !Json.Serialize(wire).Contains("fixture-only-secret") && ReadInt(wire, "providerCalls", -1) == 0
                && !ReadBool(wire, "certificateEligible", true)
                && CanonicalJson(wireSettings) == settingsHash && CanonicalJson(wireRequest) == requestHash);
            Check("writer_wire_reports_actual_final_preflight", ReadString(wire, "status", "") == "captured"
                && ReadBool(ReadDictionary(wire, "providerPreflight"), "fits", false));
            var oversizedWireRequest = CloneDictionary(wireRequest);
            oversizedWireRequest["messages"] = new[] { TestDict("role", "user", "content", new string('x', 250000)) };
            var oversizedWire = ReadDictionary(CaptureMemoryReplayProviderRequest(oversizedWireRequest, wireSettings), "providerWireCapture");
            Check("writer_wire_estimate_advisory_without_truncation", ReadString(oversizedWire, "status", "") == "captured"
                && !ReadBool(ReadDictionary(oversizedWire, "providerPreflight"), "fits", true)
                && ReadBool(ReadDictionary(oversizedWire, "providerPreflight"), "dispatchAllowed", false)
                && ReadString(oversizedWire, "requestJson", "").Contains(new string('x', 250000))
                && ReadInt(oversizedWire, "providerCalls", -1) == 0);
            var replayCase = TestDict("caseId", "bounded", "observerId", "npc", "peerId", "player", "query", "The orchard?",
                "worldDay", 3d, "asOfSequence", 2L, "sources", new[] { TestDict("sourceId", "original", "speakerId", "npc",
                    "role", "npc", "text", "Which place would you choose?", "audience", new[] { "npc", "player" }, "worldDay", 2d, "sequence", 1L) });
            Check("replay_accepts_bounded_originals", MemoryEvidenceReplayInputError(replayCase) == "");
            var wholeBank = CloneDictionary(replayCase);
            var sourceTemplate = ReadDictionaryList(replayCase, "sources").Single();
            var fullSources = Enumerable.Range(0, 2048).Select(i =>
            {
                var source = CloneDictionary(sourceTemplate); source["sourceId"] = "archive_" + i;
                return source;
            }).ToList();
            wholeBank["sources"] = fullSources;
            Check("whole_bank_2048_originals_fit", MemoryEvidenceReplayInputError(wholeBank) == ""
                && ReadInt(MemoryEvidenceReplayInputBounds(wholeBank), "sourceCount", 0) == 2048);
            var excessSource = CloneDictionary(sourceTemplate); excessSource["sourceId"] = "archive_excess";
            fullSources.Add(excessSource);
            Check("whole_bank_2049_originals_reject", MemoryEvidenceReplayInputError(wholeBank) == "missing_or_unbounded_case");
            fullSources.RemoveAt(fullSources.Count - 1);
            fullSources[0]["text"] = new string('x', 24000);
            Check("whole_bank_retains_24000_character_source_limit", MemoryEvidenceReplayInputError(wholeBank) == "");
            fullSources[0]["text"] = new string('x', 24001);
            Check("whole_bank_rejects_24001_character_source", MemoryEvidenceReplayInputError(wholeBank) == "invalid_source");
            fullSources[0]["text"] = ReadString(sourceTemplate, "text", "");
            fullSources[^1]["sequence"] = 3L;
            Check("whole_bank_future_source_still_rejects", MemoryEvidenceReplayInputError(wholeBank) == "future_source");
            fullSources[^1]["sequence"] = 1L;
            fullSources[^1]["sourceId"] = "archive_0";
            Check("whole_bank_duplicate_source_still_rejects", MemoryEvidenceReplayInputError(wholeBank) == "duplicate_source");
            var sizeBoundary = CloneDictionary(replayCase);
            sizeBoundary["query"] = "Escaped source \u2014 \u2018orchard\u2019 <gate> & water?";
            int boundaryPadding = 4 * 1024 * 1024 - MemoryEvidenceReplayCaseCharacters(sizeBoundary);
            sizeBoundary["query"] = ReadString(sizeBoundary, "query", "") + new string('x', boundaryPadding);
            Check("whole_bank_exact_character_boundary_accepts", MemoryEvidenceReplayCaseCharacters(sizeBoundary) == 4 * 1024 * 1024
                && MemoryEvidenceReplayInputError(sizeBoundary) == "");
            sizeBoundary["query"] = ReadString(sizeBoundary, "query", "") + "x";
            Check("whole_bank_character_boundary_plus_one_rejects", MemoryEvidenceReplayInputError(sizeBoundary) == "missing_or_unbounded_case");
            var separatelyBoundedExtractions = CloneDictionary(replayCase);
            separatelyBoundedExtractions["capturedMemoryExtractions"] = new[] { TestDict("sourceId", "original", "capture", "bounded elsewhere") };
            Check("whole_bank_size_excludes_separately_validated_extractions", MemoryEvidenceReplayCaseCharacters(separatelyBoundedExtractions)
                == MemoryEvidenceReplayCaseCharacters(replayCase));
            var boundReceipt = MemoryEvidenceReplayInputBounds(replayCase);
            Check("whole_bank_receipt_is_capacity_not_prompt_or_eligibility_authority",
                ReadInt(boundReceipt, "sourceLimit", 0) == 2048 && ReadInt(boundReceipt, "serializedCharacterLimit", 0) == 4 * 1024 * 1024
                && ReadString(boundReceipt, "measurement", "") == "serialized_JSON_UTF16_characters_not_provider_tokens"
                && ReadString(boundReceipt, "authority", "") == "isolated_fixture_capacity_only");
            Check("replay_ordinary_budget_omitted_preserves_default", EvidenceReplayOrdinaryTokens(TestDict()) == 4000);
            foreach (int budget in new[] { 2000, 4000, 6000, 8000 })
                Check("replay_ordinary_budget_accepts_" + budget,
                    EvidenceReplayOrdinaryTokens(TryParseJsonObject("{\"memoryPrecisionOrdinaryTokens\":" + budget + "}")) == budget);
            Check("replay_ordinary_budget_accepts_long", EvidenceReplayOrdinaryTokens(TestDict("memoryPrecisionOrdinaryTokens", 8000L)) == 8000);
            int invalidBudgetIndex = 0;
            foreach (object invalid in new object[] { null, "4000", true, false, 4000d, 4000.5d, 1999, 8001, 0, -1, long.MaxValue, new object[0] })
            {
                bool invalidBudgetRejected = false;
                try { EvidenceReplayOrdinaryTokens(TestDict("memoryPrecisionOrdinaryTokens", invalid)); }
                catch (InvalidOperationException) { invalidBudgetRejected = true; }
                Check("replay_ordinary_budget_rejects_invalid_" + invalidBudgetIndex++, invalidBudgetRejected);
            }
            var newConversation = CloneDictionary(replayCase); newConversation["recentSourceIds"] = Array.Empty<object>();
            Check("new_conversation_does_not_supply_archived_sources_as_recent", MemoryEvidenceReplayInputError(newConversation) == ""
                && EvidenceReplayRecentSources(newConversation, ReadDictionaryList(newConversation, "sources")).Count == 0
                && ReadDictionaryList(newConversation, "sources").Count == 1);
            Check("omitted_recent_scope_preserves_existing_replay", EvidenceReplayRecentSources(replayCase,
                ReadDictionaryList(replayCase, "sources")).Count == 1);
            foreach (var invalidRecent in new object[] { "original", new object[] { 1 }, new[] { "unknown" },
                new[] { "original", "original" }, Enumerable.Range(0, 31).Select(i => "source" + i).ToArray() })
            {
                var invalid = CloneDictionary(replayCase); invalid["recentSourceIds"] = invalidRecent;
                Check("recent_scope_rejects_invalid_" + PromptHash(Json.Serialize(invalidRecent)).Substring(0, 8),
                    MemoryEvidenceReplayInputError(invalid) == "invalid_recent_source_ids");
            }
            var hiddenRecent = CloneDictionary(replayCase); hiddenRecent["recentSourceIds"] = new[] { "original" };
            ReadDictionaryList(hiddenRecent, "sources").Single()["audience"] = new[] { "player", "other" };
            Check("recent_scope_cannot_publish_unheard_source", MemoryEvidenceReplayInputError(hiddenRecent) == "invalid_recent_source_ids");
            var reversedRecent = CloneDictionary(replayCase);
            var first = CloneDictionary(ReadDictionaryList(reversedRecent, "sources").Single());
            var second = CloneDictionary(first); second["sourceId"] = "second"; second["sequence"] = 2L;
            reversedRecent["sources"] = new[] { second, first }; reversedRecent["recentSourceIds"] = new[] { "second", "original" };
            Check("recent_scope_cannot_reverse_chronology", MemoryEvidenceReplayInputError(reversedRecent) == ""
                && EvidenceReplayRecentSources(reversedRecent, ReadDictionaryList(reversedRecent, "sources"))
                    .Select(s => ReadString(s, "sourceId", "")).SequenceEqual(new[] { "original", "second" }));
            var labeled = CloneDictionary(replayCase); labeled["semanticExpectation"] = "secret test label";
            Check("replay_labels_cannot_enter_candidate", MemoryEvidenceReplayInputError(labeled) == "labels_must_not_enter_candidate");
            var writerCase = CloneDictionary(replayCase);
            writerCase["writerReplay"] = TestDict("model", "captured-writer", "snapshotAsOfSequence", 2L,
                "nextContribution", "Which place did I choose?");
            Check("writer_replay_accepts_bounded_capture_contract", MemoryEvidenceReplayInputError(writerCase) == "");
            var repairCase = CloneDictionary(writerCase);
            ReadDictionary(repairCase, "writerReplay")["repairResponses"] = Array.Empty<object>();
            Check("repair_capture_empty_array_requests_production_repairs", MemoryEvidenceReplayInputError(repairCase) == "");
            foreach (object invalid in new object[] { null, "repair", new object[] { 1 },
                new[] { TestDict("requestSha256", new string('z', 64), "response", TestDict()) },
                new[] { TestDict("requestSha256", new string('a', 64), "response", TestDict(), "campaignId", "live") },
                new[] { TestDict("requestSha256", new string('a', 64), "response", TestDict("content", new string('x', 96001))) },
                Enumerable.Range(0, 5).Select(i => TestDict("requestSha256", new string('a', 64), "response", TestDict())).ToArray() })
            {
                ReadDictionary(repairCase, "writerReplay")["repairResponses"] = invalid;
                Check("repair_capture_rejects_invalid_" + results.Count, MemoryEvidenceReplayInputError(repairCase) == "invalid_writer_repairs");
            }
            var repairContinuation = EvidenceWriterContinuationFixture(TestDict("repairResponses", new[] { TestDict("old", true) }),
                TestDict("nextContribution", "And then?"));
            Check("repair_capture_does_not_leak_to_continuation", !repairContinuation.ContainsKey("repairResponses"));
            var replayContext = EvidenceReplayMemoryContext(writerCase, "fixture_branch", "fixture_player");
            Check("writer_replay_uses_shared_allocation_and_exact_query", ReadBool(replayContext, "deferContinuityAllocation", false)
                && ReadString(replayContext, "memoryQueryText", "") == ReadString(writerCase, "query", "")
                && ReadLong(replayContext, "memoryAsOfAcceptedSequence", -1) == 2L
                && !ReadBool(EvidenceReplayMemoryContext(replayCase, "fixture_branch", "fixture_player"), "deferContinuityAllocation", true));
            var nextReplay = EvidenceReplayNextMemoryContext(new(replayContext) { ["memoryPrecisionEvidence"] = TestDict("stale", true),
                ["acceptedActionDecision"] = TestDict("stale", true), ["conversationGroundingFailed"] = true,
                ["conversationGrounding"] = TestDict("status", "unresolved") }, "Why the north bridge?", "new_session", "new_turn", 9L);
            Check("writer_next_turn_uses_fresh_shared_allocation", ReadBool(nextReplay, "deferContinuityAllocation", false)
                && ReadString(nextReplay, "memoryQueryText", "") == "Why the north bridge?"
                && ReadLong(nextReplay, "memoryAsOfAcceptedSequence", -1) == 9L
                && ReadString(nextReplay, "timelineId", "") == "fixture_branch"
                && !nextReplay.ContainsKey("memoryPrecisionEvidence") && !nextReplay.ContainsKey("acceptedActionDecision")
                && !nextReplay.ContainsKey("conversationGroundingFailed") && !nextReplay.ContainsKey("conversationGrounding"));
            Check("writer_next_transcript_excludes_author_only_original", !EvidenceReplayAcceptedTranscriptTurn(TestDict("role", "player_source", "status", "source_only"))
                && !EvidenceReplayAcceptedTranscriptTurn(TestDict("role", "player", "status", "source_only"))
                && EvidenceReplayAcceptedTranscriptTurn(TestDict("role", "player", "status", "active"))
                && EvidenceReplayAcceptedTranscriptTurn(TestDict("role", "npc", "status", "active")));
            var namedSource = TestDict("role", "player", "speakerId", "player", "speakerName", "Mira", "worldDay", 1d,
                "sequence", 1L, "audience", new[] { "npc", "player" });
            var namedCase = new Dictionary<string, object>(replayCase);
            namedCase["sources"] = new[] { namedSource,
                new Dictionary<string, object>(namedSource) { ["speakerName"] = "Future name", ["sequence"] = 9L },
                new Dictionary<string, object>(namedSource) { ["speakerName"] = "Unheard name", ["sequence"] = 2L, ["audience"] = new[] { "other", "player" } } };
            Check("fresh_writer_keeps_eligible_peer_name", EvidenceReplayPlayerName(namedCase, new(), "0:player") == "Mira"
                && EvidenceReplayPlayerName(namedCase, new() { TestDict("speakerId", "0:player", "speaker", "Current name") }, "0:player") == "Current name");
            var declaredPeer = CloneDictionary(writerCase);
            var declaredWriter = ReadDictionary(declaredPeer, "writerReplay");
            ReadDictionaryList(declaredPeer, "sources").Single()["speakerName"] = "";
            declaredWriter["peerDisplayName"] = "the Innkeeper";
            namedCase["writerReplay"] = declaredWriter;
            Check("fixture_peer_display_name_fills_only_missing_source_attribution", MemoryEvidenceReplayInputError(declaredPeer) == ""
                && EvidenceReplayPlayerName(declaredPeer, new(), "0:player") == "the Innkeeper"
                && EvidenceReplayPlayerName(namedCase, new(), "0:player") == "Mira"
                && EvidenceReplayPlayerName(namedCase, new() { TestDict("speakerId", "0:player", "speaker", "Current name") }, "0:player") == "Current name"
                && ReadString(EvidenceWriterContinuationFixture(declaredWriter, TestDict("nextContribution", "Continue.")),
                    "peerDisplayName", "") == "the Innkeeper");
            foreach (object invalid in new object[] { null, 15, "", "  Innkeeper", "Title\nQuestion", "Name\u2028Section", new string('a', 161) })
            {
                declaredWriter["peerDisplayName"] = invalid;
                Check("writer_peer_display_name_rejects_invalid_" + results.Count,
                    MemoryEvidenceReplayInputError(declaredPeer) == "invalid_writer_peer_display_name");
            }
            var chainedCase = CloneDictionary(writerCase);
            var chainedCursor = ReadDictionary(chainedCase, "writerReplay");
            for (int depth = 0; depth < EvidenceWriterReplayMaxContinuations; depth++)
            {
                chainedCursor["continuation"] = TestDict("nextContribution", "What remains to be decided?");
                chainedCursor = ReadDictionary(chainedCursor, "continuation");
            }
            Check("writer_continuation_accepts_four_bounded_turns", MemoryEvidenceReplayInputError(chainedCase) == "");
            chainedCursor["continuation"] = TestDict("nextContribution", "One more?");
            Check("writer_continuation_rejects_unbounded_depth", MemoryEvidenceReplayInputError(chainedCase) == "invalid_writer_continuation");
            foreach (string forbidden in new[] { "campaignId", "model", "generationSettings", "profile", "peerDisplayName", "sources", "worldDay", "requiredEvidence" })
            {
                var injectedContinuation = CloneDictionary(writerCase);
                ReadDictionary(injectedContinuation, "writerReplay")["continuation"] = TestDict("nextContribution", "Still undecided?", forbidden, "forbidden");
                Check("writer_continuation_rejects_authority_or_labels_" + forbidden,
                    MemoryEvidenceReplayInputError(injectedContinuation) == "invalid_writer_continuation");
            }
            var missingContinuationInput = CloneDictionary(writerCase);
            ReadDictionary(missingContinuationInput, "writerReplay")["continuation"] = TestDict("response", TestDict("ok", true, "content", "{}"));
            Check("writer_continuation_requires_own_next_contribution", MemoryEvidenceReplayInputError(missingContinuationInput) == "invalid_writer_replay");
            var responseHashCase = CloneDictionary(writerCase);
            var responseHashFixture = ReadDictionary(responseHashCase, "writerReplay");
            responseHashFixture["response"] = TestDict("ok", true, "content", "{}", "requestSha256", new string('a', 64));
            Check("writer_replay_accepts_bounded_response_request_hash", MemoryEvidenceReplayInputError(responseHashCase) == "");
            foreach (var invalidHash in new object[] { null, 42, "", new string('x', 64) })
            {
                ReadDictionary(responseHashFixture, "response")["requestSha256"] = invalidHash;
                Check("writer_replay_rejects_malformed_response_request_hash_" + results.Count,
                    MemoryEvidenceReplayInputError(responseHashCase) == "invalid_writer_response_request_sha256");
            }
            var configuredWriter = CloneDictionary(writerCase);
            ReadDictionary(configuredWriter, "writerReplay")["generationSettings"] = TestDict("reasoningMode", "selective", "reasoningEffort", "medium", "temperature", .4d);
            Check("wire_generation_settings_accept_explicit_fixture_values", MemoryEvidenceReplayInputError(configuredWriter) == "");
            foreach (string referenceMode in new[] { "off", "capture", "enabled" })
            {
                var graphCase = CloneDictionary(writerCase);
                ReadDictionary(graphCase, "writerReplay")["referenceGraphMode"] = referenceMode;
                Check("reference_mode_accepts_" + referenceMode, MemoryEvidenceReplayInputError(graphCase) == "");
                Check("reference_mode_continuation_preserves_" + referenceMode,
                    ReadString(EvidenceWriterContinuationFixture(ReadDictionary(graphCase, "writerReplay"),
                        TestDict("nextContribution", "Continue.")), "referenceGraphMode", "") == referenceMode);
            }
            foreach (object invalidMode in new object[] { null, true, 1, "", "ENABLED", "on", TestDict("mode", "enabled") })
            {
                var graphCase = CloneDictionary(writerCase);
                ReadDictionary(graphCase, "writerReplay")["referenceGraphMode"] = invalidMode;
                Check("reference_mode_rejects_invalid_" + results.Count,
                    MemoryEvidenceReplayInputError(graphCase) == "invalid_replay_reference_graph_mode");
            }
            foreach (var invalidGeneration in new[] {
                TestDict("apiKey", "fixture-only-forbidden"), TestDict("apiUrl", "https://invalid.example"),
                TestDict("campaignId", "other"), TestDict("reasoningMode", "sometimes"), TestDict("reasoningEffort", "max"),
                TestDict("temperature", "0.7"), TestDict("temperature", true), TestDict("temperature", -1),
                TestDict("temperature", 3), TestDict("temperature", (object)null) })
            {
                var invalidWriter = CloneDictionary(writerCase);
                ReadDictionary(invalidWriter, "writerReplay")["generationSettings"] = invalidGeneration;
                Check("wire_generation_settings_reject_unbounded_or_authority_" + results.Count,
                    MemoryEvidenceReplayInputError(invalidWriter) == "invalid_replay_generation_settings");
            }
            var futureWriter = CloneDictionary(writerCase);
            ReadDictionary(futureWriter, "writerReplay")["snapshotAsOfSequence"] = 3L;
            Check("writer_replay_future_snapshot_rejected", MemoryEvidenceReplayInputError(futureWriter) == "invalid_writer_replay");
            var injectedWriter = CloneDictionary(writerCase);
            ReadDictionary(injectedWriter, "writerReplay")["campaignId"] = "live_campaign";
            Check("writer_replay_cannot_select_campaign_or_settings", MemoryEvidenceReplayInputError(injectedWriter) == "unknown_writer_replay_field");
            var retiredChecker = CloneDictionary(writerCase);
            ReadDictionary(retiredChecker, "writerReplay")["finalCheck"] = TestDict("enabled", true, "model", "obsolete-checker");
            Check("enabled_legacy_checker_replay_rejected", MemoryEvidenceReplayInputError(retiredChecker) == "memory_llm_stage_retired");
            ReadDictionary(retiredChecker, "writerReplay")["finalCheck"] = TestDict("enabled", false);
            Check("disabled_legacy_checker_fixture_readable", MemoryEvidenceReplayInputError(retiredChecker) == "");
            var letterWriter = CloneDictionary(writerCase); letterWriter["route"] = "correspondence";
            Check("writer_replay_does_not_claim_letter_delivery", MemoryEvidenceReplayInputError(letterWriter) == "invalid_writer_replay");
            var capacityWriter = CloneDictionary(writerCase);
            var capacityCapture = TestDict("model", "captured-writer", "contextTokens", 1048576, "maxOutputTokens", 131072,
                "source", NanoConversationModelsUrl, "sourceHash", new string('a', 64),
                "checkedAtUtc", DateTimeOffset.UtcNow.ToString("o"), "validUntilUtc", DateTimeOffset.UtcNow.AddHours(1).ToString("o"));
            ReadDictionary(capacityWriter, "writerReplay")["contextCapacity"] = capacityCapture;
            Check("writer_replay_capacity_metadata_is_bounded", MemoryEvidenceReplayInputError(capacityWriter) == "");
            var nativeWriterCase = CloneDictionary(capacityWriter);
            var nativeFixture = ReadDictionary(nativeWriterCase, "writerReplay");
            var nativeContext = TestDict("identityView", TestDict("identityState", "verified", "canonicalNameAllowed", true,
                    "knowsIdentity", true, "usableName", "Ewan", "subjectSexKnown", true, "subjectSex", "male",
                    "instruction", "INITIAL_NATIVE_IDENTITY_MARKER", "observerId", "npc", "subjectId", "player"),
                "actionResolutionIndex", TestDict("heroes", new[] { TestDict("heroId", "npc", "name", "INITIAL_NATIVE_INDEX_MARKER") },
                    "inertData", TestDict("campaignId", "not_a_live_campaign", "model", "not_a_model", "guardedPromptOverride", true)));
            nativeFixture["initialNativeContext"] = nativeContext;
            Check("initial_native_context_accepts_only_bounded_capture", MemoryEvidenceReplayInputError(nativeWriterCase) == "");
            var inspectionCase = CloneDictionary(nativeWriterCase);
            ReadDictionary(inspectionCase, "writerReplay")["questionLinkInspection"] = TestDict("reply", "A current reply.", "writes", Array.Empty<object>());
            Check("question_inspection_accepts_bounded_capture", MemoryEvidenceReplayInputError(inspectionCase) == "");
            foreach (object invalid in new object[] { null, "reply", TestDict(), TestDict("reply", "", "writes", Array.Empty<object>()),
                TestDict("reply", new string('x', 24001), "writes", Array.Empty<object>()),
                TestDict("reply", "Reply.", "writes", new[] { "not an object" }),
                TestDict("reply", "Reply.", "writes", Enumerable.Repeat(TestDict("kind", "question"), 33).ToArray()),
                TestDict("reply", "Reply.", "writes", new[] { TestDict("kind", "agenda") }),
                TestDict("reply", "Reply.", "writes", Array.Empty<object>(), "publish", true) })
            {
                var bad = CloneDictionary(inspectionCase); ReadDictionary(bad, "writerReplay")["questionLinkInspection"] = invalid;
                Check("question_inspection_rejects_unbounded_or_authority_" + results.Count,
                    MemoryEvidenceReplayInputError(bad) == "invalid_question_link_inspection");
            }
            var inspectionWithoutNative = CloneDictionary(inspectionCase);
            ReadDictionary(inspectionWithoutNative, "writerReplay").Remove("initialNativeContext");
            Check("question_inspection_requires_capture_context", MemoryEvidenceReplayInputError(inspectionWithoutNative) == "question_link_inspection_capture_only");
            foreach (object invalid in new object[] { null, "native", TestDict(), TestDict("campaignId", "live"),
                TestDict("identityView", "not an object"), TestDict("identityView", TestDict("text", new string('x', 24001))),
                TestDict("identityView", TestDict("many", Enumerable.Repeat<object>(true, 10001).ToArray())) })
            {
                var bad = CloneDictionary(nativeWriterCase); ReadDictionary(bad, "writerReplay")["initialNativeContext"] = invalid;
                Check("initial_native_context_rejects_invalid_tree_" + results.Count, MemoryEvidenceReplayInputError(bad) == "invalid_initial_native_context");
            }
            object deepNative = TestDict();
            for (int depth = 0; depth < 18; depth++) deepNative = TestDict("child", deepNative);
            var deepCase = CloneDictionary(nativeWriterCase);
            ReadDictionary(deepCase, "writerReplay")["initialNativeContext"] = TestDict("identityView", deepNative);
            Check("initial_native_context_rejects_deep_tree", MemoryEvidenceReplayInputError(deepCase) == "invalid_initial_native_context");
            foreach (string forbidden in new[] { "response", "repairResponses", "continuation", "nextPlanResponse", "nextAssemblyResponse", "nextReviewResponse" })
            {
                var bad = CloneDictionary(nativeWriterCase); ReadDictionary(bad, "writerReplay")[forbidden] = TestDict();
                Check("initial_native_context_is_capture_only_" + forbidden, MemoryEvidenceReplayInputError(bad) == "initial_native_context_capture_only");
            }
            foreach (string forbidden in new[] { "publishedPartyViewSha256", "durableReadReplay", "reviewedFactReplay", "reviewedRecoveryReplay" })
            {
                var bad = CloneDictionary(nativeWriterCase); bad[forbidden] = TestDict();
                Check("initial_native_context_cannot_mix_" + forbidden, MemoryEvidenceReplayInputError(bad) == "initial_native_context_capture_only");
            }
            var eventNative = CloneDictionary(nativeWriterCase); eventNative["route"] = "event";
            Check("initial_native_context_is_dialogue_only", MemoryEvidenceReplayInputError(eventNative) == "initial_native_context_capture_only");
            string nativeInputBefore = CanonicalJson(nativeContext);
            var mappedNative = MapReplayInitialNativeContext(nativeFixture, "npc", "player", "0:npc", "0:player");
            Check("initial_native_context_maps_exact_ids_without_mutating_input", CanonicalJson(nativeContext) == nativeInputBefore
                && ReadString(ReadDictionary(mappedNative, "identityView"), "observerId", "") == "0:npc"
                && ReadString(ReadDictionaryList(ReadDictionary(mappedNative, "actionResolutionIndex"), "heroes").Single(), "heroId", "") == "0:npc"
                && ReadString(ReadDictionary(ReadDictionary(mappedNative, "actionResolutionIndex"), "inertData"), "campaignId", "") == "not_a_live_campaign");
            Check("initial_native_context_absent_preserves_existing_path", MapReplayInitialNativeContext(ReadDictionary(capacityWriter, "writerReplay"), "npc", "player", "0:npc", "0:player") == null);
            var politicalNativeCase = CloneDictionary(nativeWriterCase);
            var politicalNativeFixture = ReadDictionary(politicalNativeCase, "writerReplay");
            var capturedPolitics = TryParseJsonObject(System.Text.Json.JsonSerializer.Serialize(GovernmentPoliticalBeliefs.Seed(
                "native_politics_source", "npc", "native_politics_timeline", CoreTraitKeys.ToDictionary(k => k, _ => 50),
                new Dictionary<ReignGovernmentPlank, double>(), false)));
            politicalNativeFixture["profile"] = TestDict("name", "Mira", "campaignId", "native_politics_source", "heroStringId", "npc",
                "politicalTimelineId", "native_politics_timeline", "appliedPoliticalProfile", CloneDictionary(capturedPolitics));
            politicalNativeFixture["characteristics"] = TestDict("politicalProfile", CloneDictionary(capturedPolitics));
            Check("initial_native_politics_accepts_complete_genesis_snapshot", MemoryEvidenceReplayInputError(politicalNativeCase) == "");
            foreach (var mutation in new[] { (Key: "CampaignId", Value: (object)"different_source"), (Key: "HeroId", Value: (object)"other"),
                (Key: "ModelVersion", Value: (object)999), (Key: "Revision", Value: (object)2L), (Key: "unexpected", Value: (object)true),
                (Key: "SeenEvidence", Value: (object)new[] { "future_native_evidence" }), (Key: "TimelineId", Value: (object)null) })
            {
                var bad = CloneDictionary(politicalNativeCase);
                ReadDictionary(ReadDictionary(ReadDictionary(bad, "writerReplay"), "profile"), "appliedPoliticalProfile")[mutation.Key] = mutation.Value;
                Check("initial_native_politics_rejects_" + mutation.Key, MemoryEvidenceReplayInputError(bad) == "invalid_initial_native_political_profile");
            }
            var mismatchPolitics = CloneDictionary(politicalNativeCase);
            ReadDictionary(ReadDictionary(ReadDictionary(mismatchPolitics, "writerReplay"), "characteristics"), "politicalProfile")["Formative"] = true;
            Check("initial_native_political_snapshots_must_agree", MemoryEvidenceReplayInputError(mismatchPolitics) == "initial_native_political_snapshots_disagree");
            // Scope only contract fixtures to this invocation. Real replay input
            // remains stable; paired cold repeats below share the same nonce.
            // Never reclaim an earlier interrupted invocation's unowned world.
            string contractNamespace = Guid.NewGuid().ToString("N");
            capacityWriter["caseId"] = ReadString(capacityWriter, "caseId", "writer") + "_" + contractNamespace;
            var secondCase = CloneDictionary(capacityWriter); secondCase["caseId"] = "bounded_second_" + contractNamespace;
            var stableInput = TestDict("schema", "reign-memory-evidence-replay-input-v1", "cases", new[] { CloneDictionary(capacityWriter), secondCase });
            var withResponses = CloneDictionary(stableInput);
            ReadDictionary(ReadDictionaryList(withResponses, "cases").First(), "writerReplay")["response"] = TestDict("ok", true, "finishReason", "stop",
                "content", Json.Serialize(TestDict("reply", "We spoke about choosing a place beside the orchard.")));
            ReadDictionary(ReadDictionaryList(withResponses, "cases").First(), "writerReplay")["nextContribution"] = "A different follow-up. " + new string('n', 3000);
            Check("world_seed_ignores_responses_and_future_turns", EvidenceReplayCampaignIdentity(stableInput) == EvidenceReplayCampaignIdentity(withResponses));
            var differentSource = CloneDictionary(stableInput);
            ReadDictionaryList(ReadDictionaryList(differentSource, "cases").First(), "sources").Single()["text"] = "A different original.";
            Check("world_seed_changes_with_originals", EvidenceReplayCampaignIdentity(stableInput) != EvidenceReplayCampaignIdentity(differentSource));
            string stableCampaign = EvidenceReplayCampaignIdentity(stableInput);
            if (ReignPostgreSqlStorage.Options.IsValidation && ActiveServerPort <= 0 && !string.IsNullOrWhiteSpace(CampaignsRootOverride.Value))
            {
                string formationBody = "I asked the keeper to bring a lantern only after the gate opened. " + new string('a', 1900);
                var formationCase = TestDict("caseId", "scene_formation_" + contractNamespace, "observerId", "npc", "peerId", "player",
                    "worldDay", 3d, "asOfSequence", 2L, "sources", new[] {
                        TestDict("sourceId", "npc_turn", "sessionId", "scene", "role", "npc", "speakerId", "npc", "speakerName", "Mira",
                            "text", formationBody, "audience", new[] { "npc", "player" }, "sequence", 1L, "worldDay", 2d),
                        TestDict("sourceId", "player_turn", "sessionId", "scene", "role", "player", "speakerId", "player", "speakerName", "Ewan",
                            "text", "I heard your instruction about the gate.", "audience", new[] { "npc", "player" }, "sequence", 2L, "worldDay", 2d) });
                var formationInput = TestDict("schema", SceneFormationReplaySchema, "cases", new[] { formationCase });
                var formation = ReplayMemoryEvidenceFixture(formationInput, "scene-formation-contract");
                var formedScene = ReadDictionaryList(ReadDictionaryList(formation, "cases").Single(), "sceneFormation").Single();
                var budgets = ReadDictionaryList(formedScene, "arms");
                Check("scene_formation_uses_native_full_fallback_and_relaxed_budget", ReadString(formation, "mode", "") == "scene_formation_baseline"
                    && ReadInt(formation, "providerCalls", -1) == 0 && budgets.Count == 2
                    && ReadString(budgets[0], "method", "") == "extractive_fallback"
                    && ReadString(budgets[1], "method", "") == "source_bound_selection"
                    && budgets.All(row => ReadBool(row, "persistedAndIndexed", false) && ReadString(row, "summary", "").Contains(formationBody)));
                formationCase["query"] = "Which lantern did you ask for?";
                bool formationQueryBlocked = false;
                try { ReplayMemoryEvidenceFixture(formationInput, "invalid-scene-formation-contract"); }
                catch (InvalidOperationException error) { formationQueryBlocked = error.Message.Contains("only scope and eligible sources"); }
                Check("scene_formation_rejects_query_in_external_manifest", formationQueryBlocked);
                using (var lease = new EvidenceReplayCampaignLease(stableInput))
                {
                    using (var seeded = OpenCampaignConnection(lease.Campaign)) ReadMemoryPrecisionState(seeded);
                    bool overlapBlocked = false;
                    try { using var competing = new EvidenceReplayCampaignLease(stableInput); }
                    catch (InvalidOperationException) { overlapBlocked = true; }
                    Check("world_lease_blocks_overlap_without_deleting_owner", overlapBlocked && ReignPostgreSqlStorage.CampaignExists(stableCampaign));
                }
                Check("world_lease_cleans_only_owned_schema", !ReignPostgreSqlStorage.CampaignExists(stableCampaign));
                try
                {
                    using (var existing = OpenCampaignConnection(stableCampaign)) ReadMemoryPrecisionState(existing);
                    bool existingBlocked = false;
                    try { using var refused = new EvidenceReplayCampaignLease(stableInput); }
                    catch (InvalidOperationException) { existingBlocked = true; }
                    Check("world_lease_refuses_existing_unowned_state", existingBlocked && ReignPostgreSqlStorage.CampaignExists(stableCampaign));
                }
                finally { ReignPostgreSqlStorage.DropCampaign(stableCampaign); }
                string quotedSchema = "\"" + ReignPostgreSqlStorage.CampaignSchemaName(stableCampaign) + "\"";
                using (var metadata = ReignPostgreSqlStorage.OpenMetadataConnection())
                {
                    try
                    {
                        ExecuteSql(metadata, "CREATE SCHEMA " + quotedSchema + ";");
                        ExecuteSql(metadata, "CREATE TABLE " + quotedSchema + ".ownership_sentinel(value TEXT NOT NULL);");
                        ExecuteSql(metadata, "INSERT INTO " + quotedSchema + ".ownership_sentinel VALUES('preserve-orphan');");
                        bool orphanBlocked = false;
                        try { using var refused = new EvidenceReplayCampaignLease(stableInput); }
                        catch (InvalidOperationException) { orphanBlocked = true; }
                        Check("world_lease_preserves_unregistered_orphan_schema", orphanBlocked
                            && ReadString(QuerySql(metadata, "SELECT value FROM " + quotedSchema + ".ownership_sentinel;").Single(), "value", "") == "preserve-orphan");
                    }
                    finally { ExecuteSql(metadata, "DROP SCHEMA IF EXISTS " + quotedSchema + " CASCADE;"); }
                }
                try
                {
                    using (var existing = OpenCampaignConnection(stableCampaign)) ReadMemoryPrecisionState(existing);
                    using var metadata = ReignPostgreSqlStorage.OpenMetadataConnection();
                    ExecuteSql(metadata, "DROP SCHEMA " + quotedSchema + " CASCADE;");
                    var registryParameters = TestDict("campaign", stableCampaign);
                    const string registrySql = "SELECT to_jsonb(r)::text AS row_json FROM reign_meta.campaign_registry r WHERE campaign_id=$campaign;";
                    var registryBefore = QuerySql(metadata, registrySql, registryParameters);
                    bool registryBlocked = false;
                    try { using var refused = new EvidenceReplayCampaignLease(stableInput); }
                    catch (InvalidOperationException) { registryBlocked = true; }
                    var registryAfter = QuerySql(metadata, registrySql, registryParameters);
                    Check("world_lease_preserves_registry_without_namespace", registryBlocked && registryBefore.Count == 1
                        && registryAfter.Count == 1 && CanonicalJson(registryBefore) == CanonicalJson(registryAfter),
                        TestDict("blocked", registryBlocked, "beforeCount", registryBefore.Count, "afterCount", registryAfter.Count));
                }
                finally { ReignPostgreSqlStorage.DropCampaign(stableCampaign); }
                var firstWorld = ReplayMemoryEvidenceFixture(CloneDictionary(stableInput), "first-capture");
                var secondWorld = ReplayMemoryEvidenceFixture(CloneDictionary(stableInput), "second-capture");
                Dictionary<string, object> FirstWriter(Dictionary<string, object> report) => ReadDictionary(ReadDictionaryList(report, "cases").First(), "writerReplay");
                string Wire(Dictionary<string, object> report, int index, string field = "providerWireCapture") =>
                    ReadString(ReadDictionary(ReadDictionary(ReadDictionaryList(report, "cases")[index], "writerReplay"), field), "requestJson", "");
                var firstWorldWriter = FirstWriter(firstWorld); var secondWorldWriter = FirstWriter(secondWorld);
                string previousReferenceMode = ConversationReferenceValidationMode.Value;
                try
                {
                    using var baselineMode = new EvidenceReplayReferenceModeScope(TestDict("referenceGraphMode", "off"));
                    Dictionary<string, object> GraphInput(string graphMode)
                    {
                        var graphInput = CloneDictionary(stableInput);
                        // One unchanged case isolates prompt mode rather than namespace.
                        graphInput["cases"] = new[] { ReadDictionaryList(graphInput, "cases").First() };
                        var graphWriter = ReadDictionary(ReadDictionaryList(graphInput, "cases").First(), "writerReplay");
                        if (graphMode != null) graphWriter["referenceGraphMode"] = graphMode;
                        return graphInput;
                    }
                    var absentGraph = ReplayMemoryEvidenceFixture(GraphInput(null), "reference-absent");
                    string settingsBeforeReference = CanonicalJson(LoadSettings());
                    var offGraph = ReplayMemoryEvidenceFixture(GraphInput("off"), "reference-off-before");
                    var enabledGraph = ReplayMemoryEvidenceFixture(GraphInput("enabled"), "reference-enabled");
                    var captureGraph = ReplayMemoryEvidenceFixture(GraphInput("capture"), "reference-capture");
                    var offAgainGraph = ReplayMemoryEvidenceFixture(GraphInput("off"), "reference-off-after");
                    string offWire = Wire(offGraph, 0), enabledWire = Wire(enabledGraph, 0);
                    Check("reference_mode_absent_and_off_preserve_wire", Wire(absentGraph, 0) == offWire);
                    Check("reference_mode_adds_native_instructions_and_schema", !offWire.Contains("memoryReferences")
                        && enabledWire.Contains("memoryReferences") && enabledWire.Contains("Evaluate player_input and npc_reply independently")
                        && Wire(captureGraph, 0).Contains("memoryReferences"));
                    Check("reference_mode_off_enabled_off_has_no_cache_leak", offWire == Wire(offAgainGraph, 0)
                        && ConversationReferenceValidationMode.Value == "off");
                    string AuthoredWindow(string wire) => CanonicalJson(ReadDictionaryList(TryParseJsonObject(wire), "messages")
                        .Where(message => ReadString(message, "role", "") is "user" or "assistant").TakeLast(30).ToList());
                    Check("reference_mode_preserves_authored_window", AuthoredWindow(offWire) == AuthoredWindow(enabledWire)
                        && AuthoredWindow(offWire) == AuthoredWindow(Wire(captureGraph, 0)));
                    var graphAudit = ReadDictionary(FirstWriter(enabledGraph), "referenceGraphAudit");
                    var withoutAuditHash = CloneDictionary(graphAudit); withoutAuditHash.Remove("auditSha256");
                    Check("reference_mode_audit_binds_mode_without_backfill_claim", ReadString(graphAudit, "mode", "") == "enabled"
                        && ReadString(graphAudit, "source", "") == "fixture" && !ReadBool(graphAudit, "historyBackfilled", true)
                        && ReadString(graphAudit, "auditSha256", "") == PromptHash(CanonicalJson(withoutAuditHash)));
                    Check("reference_mode_export_remains_provider_free_and_unpublished", new[] { absentGraph, offGraph, enabledGraph, captureGraph, offAgainGraph }
                        .All(report => ReadInt(report, "providerCalls", -1) == 0
                            && ReadString(FirstWriter(report), "status", "") == "writer_pending"
                            && !FirstWriter(report).ContainsKey("storedTurnIds")));
                    Check("reference_mode_audits_each_explicit_value", new[] { (offGraph, "off"), (captureGraph, "capture"), (enabledGraph, "enabled") }
                        .All(pair => ReadString(ReadDictionary(FirstWriter(pair.Item1), "referenceGraphAudit"), "mode", "") == pair.Item2));
                    Check("reference_mode_does_not_change_settings", CanonicalJson(LoadSettings()) == settingsBeforeReference);
                    var invalidBatch = GraphInput("off");
                    var invalidLaterCase = CloneDictionary(ReadDictionaryList(invalidBatch, "cases").First());
                    invalidLaterCase["caseId"] = "invalid_reference_mode_later_" + contractNamespace;
                    ReadDictionary(invalidLaterCase, "writerReplay")["referenceGraphMode"] = "ENABLED";
                    invalidBatch["cases"] = new[] { ReadDictionaryList(invalidBatch, "cases").First(), invalidLaterCase };
                    bool invalidBatchRejected = false;
                    try { ReplayMemoryEvidenceFixture(invalidBatch, "reference-invalid-later"); }
                    catch (InvalidOperationException ex) { invalidBatchRejected = ex.Message.Contains("invalid_replay_reference_graph_mode"); }
                    Check("reference_mode_rejects_later_case_before_world_creation", invalidBatchRejected
                        && !ReignPostgreSqlStorage.CampaignExists(EvidenceReplayCampaignIdentity(invalidBatch)));
                    bool directInvalidRejected = false;
                    try { using var invalidDirect = new EvidenceReplayReferenceModeScope(TestDict("referenceGraphMode", true)); }
                    catch (InvalidOperationException ex) { directInvalidRejected = ex.Message == "invalid_replay_reference_graph_mode"; }
                    Check("reference_mode_direct_scope_rejects_malformed_value", directInvalidRejected && ConversationReferenceValidationMode.Value == "off");
                    try
                    {
                        using var failingMode = new EvidenceReplayReferenceModeScope(TestDict("referenceGraphMode", "enabled"));
                        throw new InvalidOperationException("controlled reference scope failure");
                    }
                    catch (InvalidOperationException ex) when (ex.Message == "controlled reference scope failure") { }
                    Check("reference_mode_restores_after_failure", ConversationReferenceValidationMode.Value == "off");
                    var previousRoot = CampaignsRootOverride.Value;
                    try
                    {
                        bool rejected = true;
                        foreach (string missingRoot in new[] { null, "", "  " })
                        {
                            CampaignsRootOverride.Value = missingRoot;
                            bool refused = false;
                            try { using var blocked = new EvidenceReplayReferenceModeScope(TestDict("referenceGraphMode", "enabled")); }
                            catch (InvalidOperationException) { refused = true; }
                            rejected &= refused && ConversationReferenceValidationMode.Value == "off";
                        }
                        Check("reference_mode_refuses_missing_isolated_root", rejected);
                    }
                    finally { CampaignsRootOverride.Value = previousRoot; }
                }
                finally { ConversationReferenceValidationMode.Value = previousReferenceMode; }
                nativeWriterCase["caseId"] = "native_initial_" + contractNamespace;
                var nativeInput = TestDict("schema", "reign-memory-evidence-replay-input-v1", "cases", new[] { nativeWriterCase });
                string nativeFixtureBefore = CanonicalJson(nativeInput);
                var nativeReport = ReplayMemoryEvidenceFixture(nativeInput, "initial-native-context-contract");
                var nativeReportAgain = ReplayMemoryEvidenceFixture(CloneDictionary(nativeInput), "initial-native-context-repeat");
                var nativeResult = FirstWriter(nativeReport);
                var nativeWire = ReadDictionary(nativeResult, "providerWireCapture");
                Check("initial_native_identity_reaches_actual_wire_without_effects", ReadString(nativeResult, "status", "") == "writer_pending"
                    && ReadString(nativeWire, "requestJson", "").Contains("INITIAL_NATIVE_IDENTITY_MARKER")
                    && ReadString(nativeWire, "requestJson", "").Contains("Subject visible sex: male")
                    && ReadString(ReadDictionary(nativeResult, "request"), "model", "") == "captured-writer"
                    && ReadInt(nativeWire, "providerCalls", -1) == 0 && !nativeResult.ContainsKey("storedTurnIds")
                    && ReadBool(ReadDictionary(nativeResult, "initialNativeContextAudit"), "actionIndexRetainedUnchanged", false), nativeResult);
                Check("initial_native_capture_is_repeatable_and_input_unchanged", Wire(nativeReport, 0) == Wire(nativeReportAgain, 0)
                    && CanonicalJson(nativeInput) == nativeFixtureBefore);
                var inspectedCase = CloneDictionary(nativeWriterCase);
                inspectedCase["caseId"] = "native_question_inspection_" + contractNamespace;
                const string inspectionQuestion = "What did I propose, and what did you say?";
                const string inspectionReply = "You proposed separate baths. You clarified the arrangement. I acknowledged my mistake. More than one bath\u2014within talking distance.";
                inspectedCase["query"] = inspectionQuestion;
                Dictionary<string, object> InspectionWrite(params string[] quotes) => TestDict("kind", "question", "peerId", "0:player",
                    "questionState", TestDict("question", TestDict("turnId", "current_player", "speakerId", "0:player", "quote", inspectionQuestion),
                        "addresseeIds", new[] { "0:npc" }, "answerState", "answered", "evaluation", "unassessed",
                        "answers", quotes.Select(q => TestDict("turnId", "current_npc", "speakerId", "0:npc", "quote", q)).ToArray()));
                var validInspectionWrite = InspectionWrite("You proposed separate baths.", "I acknowledged my mistake.");
                var wrongInspectionActor = InspectionWrite("You proposed separate baths.");
                ReadDictionaryList(ReadDictionary(wrongInspectionActor, "questionState"), "answers")[0]["speakerId"] = "0:player";
                var invalidStateCase = InspectionWrite("You proposed separate baths.");
                ReadDictionary(invalidStateCase, "questionState")["answerState"] = "Answered";
                var invalidAliasCase = InspectionWrite("You proposed separate baths.");
                ReadDictionary(ReadDictionary(invalidAliasCase, "questionState"), "question")["turnId"] = "current_player ";
                var inspection = TestDict("reply", inspectionReply, "writes", new[] { validInspectionWrite,
                    InspectionWrite("You proposed separate baths. I acknowledged my mistake."), wrongInspectionActor,
                    InspectionWrite("You proposed separate baths.", "An invented answer."),
                    InspectionWrite("More than one bath\uFFFDwithin talking distance."), invalidStateCase, invalidAliasCase });
                ReadDictionary(inspectedCase, "writerReplay")["questionLinkInspection"] = inspection;
                var inspectionInput = TestDict("schema", "reign-memory-evidence-replay-input-v1", "cases", new[] { inspectedCase });
                string inspectionBefore = CanonicalJson(inspectionInput);
                var inspectedReport = ReplayMemoryEvidenceFixture(inspectionInput, "question-link-inspection-contract");
                var inspectedWriter = FirstWriter(inspectedReport);
                var inspectionAudit = ReadDictionary(inspectedWriter, "questionLinkInspection");
                var inspectedRows = ReadDictionaryList(inspectionAudit, "checks");
                Check("question_inspection_uses_native_good_and_spliced_quote_binding", inspectedRows.Count == 7
                    && ReadString(inspectedRows[0], "status", "") == "source_link_verified"
                    && inspectedRows.Skip(1).Take(4).All(r => ReadString(r, "reason", "") == "question_answer_source_unverified"), inspectionAudit);
                Check("native_question_state_and_alias_are_exact_not_case_or_trim_normalized",
                    ReadString(inspectedRows[5], "reason", "") == "question_invalid_state"
                    && ReadString(inspectedRows[6], "reason", "") == "question_source_unverified");
                Check("question_inspection_preserves_both_exact_answer_spans", ReadDictionaryList(ReadDictionary(inspectedRows[0], "normalizedQuestionState"), "answers").Count == 2);
                Check("question_inspection_never_keeps_partial_or_empty_answered_state", inspectedRows.Skip(1).All(r => r.GetValueOrDefault("normalizedQuestionState") == null));
                Check("question_inspection_is_database_read_only_and_unpublished", ReadBool(inspectionAudit, "databaseReadOnly", false)
                    && !ReadBool(inspectionAudit, "publicationAllowed", true) && !ReadBool(inspectionAudit, "certificateEligible", true)
                    && ReadInt(inspectionAudit, "providerCalls", -1) == 0 && !inspectedWriter.ContainsKey("publication")
                    && !inspectedWriter.ContainsKey("stored") && CanonicalJson(inspectionInput) == inspectionBefore);
                var withoutInspection = CloneDictionary(inspectionInput);
                ReadDictionary(ReadDictionaryList(withoutInspection, "cases")[0], "writerReplay").Remove("questionLinkInspection");
                var withoutInspectionReport = ReplayMemoryEvidenceFixture(withoutInspection, "question-link-inspection-wire-control");
                Check("question_inspection_cannot_change_provider_wire", Wire(inspectedReport, 0) == Wire(withoutInspectionReport, 0));
                Check("question_answer_span_preparation_reaches_native_wire", Wire(inspectedReport, 0).Contains("exact contiguous spans")
                    && Wire(inspectedReport, 0).Contains("For compound questions, answered needs answer spans for every part")
                    && !Wire(inspectedReport, 0).Contains("Source quotes are exact, including punctuation."));
                string previousInspectionRoot = CampaignsRootOverride.Value;
                try
                {
                    CampaignsRootOverride.Value = null;
                    bool isolatedRejected = false;
                    try { InspectReplayQuestionLinks("not-opened", ReadDictionary(inspectedCase, "writerReplay"), TestDict(), "0:npc", "0:player"); }
                    catch (InvalidOperationException ex) { isolatedRejected = ex.Message == "question_link_inspection_requires_isolated_validation"; }
                    Check("question_inspection_refuses_missing_isolated_root", isolatedRejected);
                }
                finally { CampaignsRootOverride.Value = previousInspectionRoot; }
                int previousInspectionPort = ActiveServerPort;
                try
                {
                    ActiveServerPort = 1;
                    bool listeningRejected = false;
                    try { InspectReplayQuestionLinks("not-opened", ReadDictionary(inspectedCase, "writerReplay"), TestDict(), "0:npc", "0:player"); }
                    catch (InvalidOperationException ex) { listeningRejected = ex.Message == "question_link_inspection_requires_isolated_validation"; }
                    Check("question_inspection_refuses_listening_server", listeningRejected);
                }
                finally { ActiveServerPort = previousInspectionPort; }
                politicalNativeCase["caseId"] = "native_political_initial_" + contractNamespace;
                var politicalInput = TestDict("schema", "reign-memory-evidence-replay-input-v1", "cases", new[] { politicalNativeCase });
                string politicalBefore = CanonicalJson(politicalInput);
                var politicalReport = ReplayMemoryEvidenceFixture(politicalInput, "initial-native-political-contract");
                var politicalRepeat = ReplayMemoryEvidenceFixture(CloneDictionary(politicalInput), "initial-native-political-repeat");
                var politicalResult = FirstWriter(politicalReport);
                var boundProfile = ReadDictionary(ReadDictionary(politicalResult, "request"), "hero");
                var boundPolitics = ReadDictionary(boundProfile, "appliedPoliticalProfile");
                var sourceContent = CloneDictionary(capturedPolitics); var boundContent = CloneDictionary(boundPolitics);
                foreach (string key in new[] { "CampaignId", "HeroId", "TimelineId" }) { sourceContent.Remove(key); boundContent.Remove(key); }
                var politicsAudit = ReadDictionary(ReadDictionary(politicalResult, "initialNativeContextAudit"), "politicalBinding");
                Check("initial_native_politics_preserves_values_and_normal_issued_gate", ReadString(politicalResult, "status", "") == "writer_pending"
                    && CanonicalJson(sourceContent) == CanonicalJson(boundContent)
                    && ReadString(boundPolitics, "HeroId", "") == "0:npc" && ReadString(boundPolitics, "TimelineId", "") == "replay_0"
                    && ReadString(politicsAudit, "status", "") == "fixture_rebound_not_native_issued"
                    && ReadString(politicsAudit, "boundSha256", "") == ReadString(politicsAudit, "writtenSha256", "")
                    && ReadDictionaryList(WireBody(ReadDictionary(politicalResult, "providerWireCapture")), "messages")
                        .Any(m => ReadString(m, "content", "").Contains("REQUIRED PRIVATE POLITICAL BELIEFS \u2014 0:npc \u2014 revision 1 \u2014 timeline replay_0")), politicsAudit);
                string invariantPoliticalJson = CanonicalJson(boundPolitics), otherCulturePoliticalJson;
                var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
                try
                {
                    System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
                    otherCulturePoliticalJson = CanonicalJson(TryParseJsonObject(System.Text.Json.JsonSerializer.Serialize(
                        System.Text.Json.JsonSerializer.Deserialize<GovernmentPoliticalProfile>(Json.Serialize(boundPolitics)))));
                }
                finally { System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
                Check("initial_native_politics_is_repeatable_without_input_mutation", Wire(politicalReport, 0) == Wire(politicalRepeat, 0)
                    && CanonicalJson(politicalInput) == politicalBefore);
                Check("initial_native_political_serialization_is_culture_invariant", invariantPoliticalJson == otherCulturePoliticalJson);
                var collisionCase = CloneDictionary(politicalNativeCase); collisionCase["caseId"] = "native_political_collision_" + contractNamespace;
                var collisionInput = TestDict("schema", "reign-memory-evidence-replay-input-v1", "cases", new[] { collisionCase });
                using (var collisionLease = new EvidenceReplayCampaignLease(collisionInput))
                {
                    using (var c = OpenCampaignConnection(collisionLease.Campaign)) ReadMemoryPrecisionState(c);
                    var collisionFixture = ReadDictionary(collisionCase, "writerReplay");
                    var collisionProfile = CloneDictionary(ReadDictionary(collisionFixture, "profile"));
                    var collisionCharacteristics = CloneDictionary(ReadDictionary(collisionFixture, "characteristics"));
                    BindInitialReplayPoliticalProfile(collisionCase, collisionFixture, collisionProfile, collisionCharacteristics,
                        collisionLease.Campaign, "0:npc", "replay_0");
                    string politicalPath = CharacterFile(collisionLease.Campaign, "0:npc", "politics", "revision-1-" + SafeMemoryKey("replay_0") + ".json");
                    string firstPolitical = CanonicalJson(ReadJsonObject(politicalPath));
                    var altered = ReadDictionary(ReadDictionary(collisionFixture, "profile"), "appliedPoliticalProfile");
                    var firstValue = ReadDictionaryList(altered, "Values").First();
                    firstValue["Stance"] = ReadInt(firstValue, "Stance", 0) == 100 ? 99 : ReadInt(firstValue, "Stance", 0) + 1;
                    ReadDictionary(collisionFixture, "characteristics")["politicalProfile"] = CloneDictionary(altered);
                    bool collisionRejected = false;
                    try { BindInitialReplayPoliticalProfile(collisionCase, collisionFixture, collisionProfile, collisionCharacteristics,
                        collisionLease.Campaign, "0:npc", "replay_0"); }
                    catch (InvalidOperationException error) { collisionRejected = error.Message.Contains("already has different content"); }
                    Check("initial_native_political_collision_cannot_issue_another_revision", collisionRejected
                        && CanonicalJson(ReadJsonObject(politicalPath)) == firstPolitical
                        && !File.Exists(CharacterFile(collisionLease.Campaign, "0:npc", "politics", "revision-2-" + SafeMemoryKey("replay_0") + ".json")));
                    bool unsafePathRejected = false;
                    try { BindInitialReplayPoliticalProfile(collisionCase, collisionFixture, collisionProfile, collisionCharacteristics,
                        "evidence_replay_../../native", "0:npc", "replay_0"); }
                    catch (InvalidOperationException) { unsafePathRejected = true; }
                    Check("initial_native_political_binding_refuses_nonfixture_namespace", unsafePathRejected);
                }
                var replayNativeRequest = ReadDictionary(firstWorldWriter, "request");
                var replayNativeProfile = ReadDictionary(replayNativeRequest, "hero");
                Check("writer_native_profile_is_bound_to_isolated_world",
                    ReadString(replayNativeProfile, "campaignId", "") == stableCampaign
                    && ReadString(replayNativeProfile, "heroStringId", "") == "0:npc"
                    && ReadString(replayNativeProfile, "politicalTimelineId", "") == "replay_0");
                var replayNativeWire = ReadDictionary(firstWorldWriter, "providerWireCapture");
                Check("writer_native_profile_preserves_required_politics_without_dispatch",
                    ReadString(replayNativeWire, "status", "") == "captured"
                    && ReadString(replayNativeWire, "requestJson", "").Contains("REQUIRED PRIVATE POLITICAL BELIEFS")
                    && ReadInt(replayNativeWire, "providerCalls", -1) == 0
                    && !ReadBool(replayNativeWire, "certificateEligible", true));
                Check("fresh_worlds_produce_identical_provider_bytes",
                    ReadString(firstWorldWriter, "status", "") == "writer_pending"
                    && ReadString(ReadDictionary(firstWorldWriter, "providerWireCapture"), "requestJson", "")
                        == ReadString(ReadDictionary(secondWorldWriter, "providerWireCapture"), "requestJson", "different")
                    && !ReignPostgreSqlStorage.CampaignExists(stableCampaign));
                var capturedWorld = ReplayMemoryEvidenceFixture(CloneDictionary(withResponses), "first-response-capture");
                var repeatedCapturedWorld = ReplayMemoryEvidenceFixture(CloneDictionary(withResponses), "repeat-response-capture");
                Check("captured_response_does_not_change_initial_or_other_case_wire",
                    ReadString(FirstWriter(capturedWorld), "status", "") == "captured_writer_stored_next_read_prepared"
                    && Wire(firstWorld, 0) == Wire(capturedWorld, 0) && Wire(firstWorld, 1) == Wire(capturedWorld, 1));
                Check("fresh_publication_replays_produce_identical_next_wire", Wire(capturedWorld, 0, "nextWriterWireCapture").Length > 0
                    && Wire(capturedWorld, 0, "nextWriterWireCapture") == Wire(repeatedCapturedWorld, 0, "nextWriterWireCapture"));

                const string olderAnswer = "The amber contract for the north gate lasts only until the third bell.";
                const string reviewedFact = "Mira set the third bell as the deadline for the north gate arrangement.";
                Dictionary<string, object> Authored(string id, int sequence, bool player, string body, string session, string exchange) =>
                    TestDict("sourceId", id, "speakerId", player ? "player" : "npc", "speakerName", player ? "Ewan" : "Mira",
                        "role", player ? "player" : "npc", "text", body, "audience", new[] { "player", "npc" },
                        "worldDay", 2d, "sequence", sequence, "sessionId", session, "exchangeId", exchange);
                var exactWindow = Enumerable.Range(3, 29).Select(i => Authored("authored_" + i, i, i % 2 != 0,
                    "Market contribution number " + i + " mentions a different basket.", "current", "market_" + (i - 3) / 2)).ToList();
                var witness = exactWindow[1];
                witness["speakerId"] = "witness";
                witness["speakerName"] = "Sorcha";
                witness["audience"] = new[] { "player", "npc", "witness" };
                var oldPlayer = Authored("older_player", 1, true, "I brought the amber contract to the north gate for our witnesses.", "older", "agreement");
                var oldNpc = Authored("older_npc", 2, false, olderAnswer, "older", "agreement");
                var wideCase = CloneDictionary(capacityWriter);
                wideCase["caseId"] = "reviewed_fact_" + contractNamespace;
                wideCase["route"] = "event";
                wideCase["query"] = "When did the amber contract for the north gate expire?";
                wideCase["asOfSequence"] = 31L;
                wideCase["recentSourceIds"] = exactWindow.Select(s => ReadString(s, "sourceId", "")).ToArray();
                wideCase["sources"] = new[] { oldPlayer, oldNpc }.Concat(exactWindow).ToList();
                ReadDictionary(wideCase, "writerReplay")["snapshotAsOfSequence"] = 31L;
                var compactCase = CloneDictionary(wideCase);
                compactCase["reviewedFactReplay"] = TestDict("reviewReceiptSha256", new string('c', 64),
                    "facts", new[] { TestDict("id", "fact:amber_timing", "sourceId", "older_npc", "sourceSequence", 2L,
                        "sourceTextSha256", ConversationExactTextHash(olderAnswer), "speakerId", "npc", "quote", olderAnswer, "text", reviewedFact) });
                Dictionary<string, object> ReplayCase(Dictionary<string, object> item) => ReplayMemoryEvidenceFixture(
                    TestDict("schema", "reign-memory-evidence-replay-input-v1", "precisionMode", "precision", "cases", new[] { item }), "reviewed-fact-contract");
                var wideFactWriter = FirstWriter(ReplayCase(wideCase));
                var compactFactWriter = FirstWriter(ReplayCase(compactCase));
                var wideFactWire = ReadDictionary(wideFactWriter, "providerWireCapture");
                var compactFactWire = ReadDictionary(compactFactWriter, "providerWireCapture");
                string compactFactBody = ReadString(compactFactWire, "requestJson", "");
                var reviewProjection = ReadDictionary(compactFactWriter, "reviewedFactProjection");
                Check("reviewed_fact_projection_replaces_old_original_on_native_initial_wire",
                    ReadString(wideFactWire, "requestJson", "").Contains(olderAnswer) && ReadString(compactFactWriter, "status", "") == "writer_pending"
                    && !compactFactBody.Contains(olderAnswer) && compactFactBody.Contains(reviewedFact) && !compactFactBody.Contains("fact:amber_timing")
                    && ReadStringList(reviewProjection, "removedAliases").Count > 0 && ReadDictionaryList(reviewProjection, "factPins").Count == 1,
                    TestDict("wideStatus", ReadString(wideFactWriter, "status", ""), "compactStatus", ReadString(compactFactWriter, "status", ""),
                        "reason", ReadString(compactFactWriter, "reason", ""), "removed", ReadStringList(reviewProjection, "removedAliases")));
                var wireMessages = ReadDictionaryList(WireBody(compactFactWire), "messages");
                var wideMessages = ReadDictionaryList(WireBody(wideFactWire), "messages");
                Check("reviewed_fact_projection_changes_only_one_memory_bearing_message",
                    wireMessages.Count == wideMessages.Count && wireMessages.Count > 30
                    && wireMessages.Zip(wideMessages).Count(pair => CanonicalJson(pair.First) != CanonicalJson(pair.Second)) == 1);
                Check("reviewed_fact_projection_keeps_the_other_npc_in_attributed_user_context", wireMessages.Any(message =>
                    ReadString(message, "role", "") == "user" && ReadString(message, "content", "").StartsWith("HISTORICAL OTHER CHARACTER / NARRATOR ")
                    && ReadString(message, "content", "").Contains(ReadString(witness, "text", ""))));
                var rejectedFact = CloneDictionary(compactCase);
                ReadDictionaryList(ReadDictionary(rejectedFact, "reviewedFactReplay"), "facts").Single()["speakerId"] = "player";
                bool wrongSpeakerBlocked = false;
                try { ReplayCase(rejectedFact); }
                catch (InvalidOperationException ex) { wrongSpeakerBlocked = ex.Message.Contains("exact projected text check"); }
                Check("reviewed_fact_projection_rejects_wrong_speaker", wrongSpeakerBlocked);
                var staleFact = CloneDictionary(compactCase);
                ReadDictionaryList(ReadDictionary(staleFact, "reviewedFactReplay"), "facts").Single()["sourceTextSha256"] = new string('d', 64);
                bool staleSourceBlocked = false;
                try { ReplayCase(staleFact); }
                catch (InvalidOperationException ex) { staleSourceBlocked = ex.Message.Contains("exact projected text check"); }
                Check("reviewed_fact_projection_rejects_stale_source_hash", staleSourceBlocked);
                Check("reviewed_fact_final_wire_detects_residual_original",
                    ReplayReviewedFactWireError(wideFactWire, compactCase, exactWindow, "npc", id => "0:" + id) == "older_original_reached_initial_wire");

                var collisionWindow = exactWindow.Select(CloneDictionary).ToList();
                collisionWindow[0]["text"] = "Nay,";
                collisionWindow[2]["text"] = "No, Ewan, that basket is promised.";
                var shortOriginals = new[] { Authored("old_nay", 1, true, "Nay,", "older", "agreement"),
                    Authored("old_no", 2, false, "No,", "older", "agreement") };
                var collisionBody = WireBody(compactFactWire);
                var collisionMessages = ReadDictionaryList(collisionBody, "messages");
                foreach (int position in new[] { 0, 2 })
                {
                    string before = ReadString(exactWindow[position], "text", "");
                    var message = collisionMessages.Single(m => ReadString(m, "content", "").Contains(before, StringComparison.Ordinal));
                    message["content"] = ReadString(message, "content", "").Replace(before, ReadString(collisionWindow[position], "text", ""), StringComparison.Ordinal);
                }
                bool collisionPreservesWindow = ReplayReviewedFactWindowMatches(collisionMessages, collisionWindow, "npc",
                    ReadString(compactCase, "query", ""), out var collisionResidual);
                collisionResidual.Add("A derived note discusses the phrase 'Nay, then'.");
                Check("reviewed_fact_short_older_collisions_preserve_exact_authored_window",
                    collisionPreservesWindow && !ReplayReviewedOlderOriginalDelivered(collisionResidual, shortOriginals, id => "0:" + id));
                Check("reviewed_fact_short_older_originals_still_detect_extra_lines_quotes_and_source_bindings",
                    new[] { "Nay,", "- Ewan: No,", "Mira said \"No,\" then left.",
                        "- [Day 2] [source=0:old_nay role=player] Ewan: Nay, my friend.",
                        RenderContinuityRecord("0:old_no", "reported_exchange", "npc", "older", 2d, 65, true, "An attributed quotation.") }
                    .All(leak => ReplayReviewedOlderOriginalDelivered(collisionResidual.Append(leak).ToList(), shortOriginals, id => "0:" + id)));

                var localRepairCase = CloneDictionary(compactCase);
                var localRepairWriter = ReadDictionary(localRepairCase, "writerReplay");
                const string localRepairReply = "The third bell was the deadline.";
                localRepairWriter["repairResponses"] = Array.Empty<object>();
                localRepairWriter["response"] = TestDict("ok", true, "finishReason", "stop",
                    "requestSha256", ReadString(compactFactWire, "requestSha256", ""),
                    "content", Json.Serialize(TestDict("reply", localRepairReply, "participation", "speak",
                        "continuityWrites", new[] { TestDict("kind", "agenda", "status", "active", "subject", "An unrelated plan",
                            "meaning", "A fabricated plan.", "evidenceQuote", "A fabricated quote.", "expectedRevision", 0) })));
                var localRepaired = FirstWriter(ReplayCase(localRepairCase));
                var localRepairAudit = ReadDictionary(ReadDictionary(localRepaired, "conversationRepairEvidence"), "continuity");
                var localRepairStored = ReadDictionary(localRepaired, "stored");
                var localAuthored = ReadDictionaryList(localRepairStored, "conversation_turns");
                Check("reviewed_fact_replay_runs_native_deterministic_continuity_cleanup",
                    ReadString(localRepaired, "initialCapturedProse", "") == localRepairReply
                    && ReadString(localRepairAudit, "method", "") == "deterministic_unsupported_continuity_write_removal"
                    && ReadString(ReadDictionary(localRepaired, "responseRequestBinding"), "status", "") == "verified"
                    && ReadBool(localRepaired, "groundingAccepted", false)
                    && ReadDictionaryList(localRepaired, "repairRequests").Count == 0
                    && ReadDictionaryList(localRepairStored, "continuity").Count == 0
                    && localAuthored.Select(row => ReadString(row, "text", "")).SequenceEqual(
                        new[] { ReadString(localRepairCase, "query", ""), MarkRepairedVisibleText(localRepairReply, true) }),
                    TestDict("status", ReadString(localRepaired, "status", ""), "repair", localRepairAudit));
                ReadDictionary(localRepairWriter, "response")["content"] = "{\"reply\":";
                var localBlocked = FirstWriter(ReplayCase(localRepairCase));
                Check("reviewed_fact_deterministic_repair_blocks_provider_capture_and_publication",
                    ReadString(localBlocked, "status", "") == "writer_reviewed_deterministic_repair_requires_provider"
                    && ReadDictionaryList(localBlocked, "repairRequests").Count == 0
                    && !localBlocked.ContainsKey("publication"), ReadString(localBlocked, "status", ""));
                localRepairWriter["repairResponses"] = new[] { TestDict("requestSha256", new string('d', 64),
                    "response", TestDict("ok", true, "finishReason", "stop", "content", "{\"reply\":\"Unbound reply.\"}")) };
                Check("reviewed_fact_replay_refuses_generic_provider_repair_responses",
                    ReplayReviewedFactInputError(localRepairCase, ReadDictionaryList(localRepairCase, "sources")) == "invalid_reviewed_fact_replay");

                var followUpCase = CloneDictionary(compactCase);
                var followUpFixture = ReadDictionary(followUpCase, "writerReplay");
                const long importedOffset = 10_000L;
                const long importedCutoff = importedOffset + 31L;
                foreach (var source in ReadDictionaryList(followUpCase, "sources"))
                    source["sequence"] = ReadLong(source, "sequence", 0) + importedOffset;
                foreach (var fact in ReadDictionaryList(ReadDictionary(followUpCase, "reviewedFactReplay"), "facts"))
                    fact["sourceSequence"] = ReadLong(fact, "sourceSequence", 0) + importedOffset;
                followUpCase["asOfSequence"] = importedCutoff;
                followUpCase["asOfUtcSeconds"] = importedCutoff;
                followUpFixture["snapshotAsOfSequence"] = importedCutoff;
                var importedFactWire = ReadDictionary(FirstWriter(ReplayCase(followUpCase)), "providerWireCapture");
                const string acceptedFactReply = "For the north gate, the third bell was the deadline.";
                const string followUpContribution = "Can you remind me what was agreed for the north gate?";
                followUpFixture["response"] = TestDict("ok", true, "finishReason", "stop",
                    "content", Json.Serialize(TestDict("reply", acceptedFactReply, "participation", "speak")),
                    "requestSha256", ReadString(importedFactWire, "requestSha256", ""));
                followUpFixture["nextContribution"] = followUpContribution;
                var publishedFact = FirstWriter(ReplayCase(followUpCase));
                var followUp = ReadDictionary(publishedFact, "reviewedFactFollowUp");
                var followUpWire = ReadDictionary(followUp, "providerWireCapture");
                var followUpMessages = ReadDictionaryList(WireBody(followUpWire), "messages");
                var stockFollowUp = ReadDictionary(publishedFact, "nextWriterWireCapture");
                string compactFollowUp = ReadString(followUpWire, "requestJson", "");
                var importedPublication = ReadDictionaryList(ReadDictionary(publishedFact, "stored"), "conversation_turns");
                Check("reviewed_fact_follow_up_keeps_exact_current_window_without_archived_originals",
                    ReadString(followUp, "status", "") == "captured"
                    && importedPublication.Count == 2 && importedPublication.All(turn => ReadLong(turn, "accepted_sequence", 0) > importedCutoff)
                    && ReadLong(publishedFact, "nextAsOfAcceptedSequence", 0) > importedCutoff
                    && followUpMessages.Count(message => ReadString(message, "content", "").StartsWith("HISTORICAL ")) == 29
                    && followUpMessages.Any(message => ReadString(message, "content", "").StartsWith("LATEST PLAYER TURN ", StringComparison.Ordinal)
                        && ReadString(message, "content", "").EndsWith("\n" + followUpContribution, StringComparison.Ordinal))
                    && compactFollowUp.Contains("REQUIRED PRIVATE POLITICAL BELIEFS")
                    && compactFollowUp.Contains(acceptedFactReply) && compactFollowUp.Contains(reviewedFact)
                    && !compactFollowUp.Contains(olderAnswer) && !compactFollowUp.Contains("fact:amber_timing")
                    && !compactFollowUp.Contains(ReadString(exactWindow[1], "text", ""))
                    && ReadString(stockFollowUp, "status", "") == "captured"
                    && ReadString(stockFollowUp, "requestJson", "").Contains(olderAnswer)
                    && ReadInt(followUp, "providerCalls", -1) == 0,
                    TestDict("stockStatus", ReadString(stockFollowUp, "status", ""), "compactStatus", ReadString(followUp, "status", ""),
                        "reason", ReadString(followUp, "reason", ""), "error", ReadString(followUp, "error", "")));

                var planCase = CloneDictionary(compactCase);
                var planReview = ReadDictionary(planCase, "reviewedFactReplay");
                planReview["writerPlan"] = true;
                Check("reviewed_plan_rejects_an_unknown_treatment", ReplayReviewedFactInputError(planCase,
                    ReadDictionaryList(planCase, "sources")) == "invalid_reviewed_writer_plan");
                planReview["writerPlan"] = "reviewed_basis";
                var planned = FirstWriter(ReplayCase(planCase));
                var plannedWire = ReadDictionary(planned, "providerWireCapture");
                var plannedBody = WireBody(plannedWire);
                var plannedMessages = ReadDictionaryList(plannedBody, "messages");
                string planRequestHash = ReadString(plannedWire, "requestSha256", "");
                Check("reviewed_plan_carries_bound_facts_and_authority_in_native_wire",
                    ReadString(planned, "status", "") == "writer_pending" && ReadString(plannedWire, "status", "") == "captured"
                    && ReadString(ReadDictionary(plannedBody, "response_format"), "type", "") == "json_schema"
                    && plannedMessages.Count(message => ReadString(message, "content", "").StartsWith("HISTORICAL ")) == 29
                    && plannedMessages.Any(message => ReadString(message, "content", "").StartsWith("RECALL RESPONSE PLAN \u2014"))
                    && ReadString(plannedWire, "requestJson", "").Contains("REQUIRED PRIVATE POLITICAL BELIEFS")
                    && !ReadString(plannedWire, "requestJson", "").Contains(olderAnswer),
                    TestDict("status", ReadString(planned, "status", ""), "error", ReadString(planned, "error", ""),
                        "reason", ReadString(planned, "reason", "")));
                var planFixture = ReadDictionary(planCase, "writerReplay");
                Dictionary<string, object> CapturePlan(string speaker) => TestDict("ok", true, "finishReason", "stop",
                    "requestSha256", planRequestHash, "content", Json.Serialize(TestDict("recallBasis",
                        TestDict("recollection1", TestDict("reportedBy", speaker, "meaning", reviewedFact)),
                        "reply", acceptedFactReply, "participation", "speak")));
                if (planRequestHash.Length == 64)
                {
                    planFixture["response"] = CapturePlan("Ewan");
                    var wrongPlan = FirstWriter(ReplayCase(planCase));
                    Check("reviewed_plan_wrong_speaker_cannot_publish", ReadString(wrongPlan, "status", "") == "writer_reviewed_plan_response_mismatch"
                        && !wrongPlan.ContainsKey("stored"));
                    planFixture["response"] = CapturePlan("Mira");
                    var boundPlan = FirstWriter(ReplayCase(planCase));
                    Check("reviewed_plan_matching_reply_uses_native_publication", ReadString(ReadDictionary(boundPlan, "reviewedWriterPlan"), "planConformance", "") == "verified"
                        && ReadString(boundPlan, "acceptedProse", "") == acceptedFactReply
                        && ReadDictionaryList(ReadDictionary(boundPlan, "stored"), "conversation_turns").Count == 2,
                        TestDict("status", ReadString(boundPlan, "status", ""), "groundingAccepted", ReadBool(boundPlan, "groundingAccepted", false)));
                }

                var deliveryCase = CloneDictionary(compactCase);
                var deliveryReview = ReadDictionary(deliveryCase, "reviewedFactReplay");
                deliveryReview["writerPlan"] = "reviewed_bound_delivery";
                Check("reviewed_delivery_requires_both_checked_lines", ReplayReviewedFactInputError(deliveryCase,
                    ReadDictionaryList(deliveryCase, "sources")) == "invalid_reviewed_delivery_choices");
                const string conversationalFactReply = "It was the third bell, if you mean the north gate.";
                deliveryReview["deliveryChoices"] = TestDict("direct", acceptedFactReply, "conversational", conversationalFactReply);
                var deliveryCapture = FirstWriter(ReplayCase(deliveryCase));
                var deliveryWire = ReadDictionary(deliveryCapture, "providerWireCapture");
                string deliveryBody = ReadString(deliveryWire, "requestJson", "");
                var deliveryFormat = ReadDictionary(ReadDictionary(WireBody(deliveryWire), "response_format"), "json_schema");
                var deliveryProperties = ReadDictionary(ReadDictionary(deliveryFormat, "schema"), "properties");
                string deliveryHash = ReadString(ReadDictionary(deliveryCapture, "reviewedWriterPlan"), "deliveryChoicesSha256", "");
                string deliveryRequest = ReadString(deliveryWire, "requestSha256", "");
                Check("reviewed_delivery_wire_only_exposes_style_and_participation",
                    ReadString(deliveryCapture, "status", "") == "writer_pending" && deliveryHash.Length == 64
                    && deliveryProperties?.Keys.SequenceEqual(new[] { "delivery", "participation" }) == true
                    && deliveryBody.Contains(reviewedFact) && deliveryBody.Contains("REQUIRED PRIVATE POLITICAL BELIEFS")
                    && !deliveryBody.Contains(olderAnswer) && !deliveryBody.Contains(acceptedFactReply)
                    && !deliveryBody.Contains(conversationalFactReply), TestDict("status", ReadString(deliveryCapture, "status", "")));
                if (deliveryRequest.Length == 64 && deliveryHash.Length == 64)
                {
                    var deliveryFixture = ReadDictionary(deliveryCase, "writerReplay");
                    var deliveredResponse = TestDict("ok", true, "finishReason", "stop", "requestSha256", deliveryRequest,
                        "reviewedDeliverySha256", deliveryHash, "content", Json.Serialize(TestDict("delivery", "direct", "participation", "speak")));
                    deliveryFixture["response"] = deliveredResponse;
                    var changedSpeech = CloneDictionary(deliveryCase);
                    ReadDictionary(ReadDictionary(changedSpeech, "reviewedFactReplay"), "deliveryChoices")["direct"] = conversationalFactReply;
                    var wrongReceipt = FirstWriter(ReplayCase(changedSpeech));
                    Check("reviewed_delivery_rejects_changed_lines_under_same_request_hash",
                        ReadString(wrongReceipt, "status", "") == "writer_reviewed_delivery_receipt_mismatch"
                        && ReadString(ReadDictionary(wrongReceipt, "responseRequestBinding"), "status", "") == "verified"
                        && !wrongReceipt.ContainsKey("stored"));
                    deliveredResponse["content"] = Json.Serialize(TestDict("delivery", "unreviewed", "participation", "speak"));
                    var wrongDelivery = FirstWriter(ReplayCase(deliveryCase));
                    Check("reviewed_delivery_rejects_an_unapproved_style", ReadString(wrongDelivery, "status", "") == "writer_reviewed_plan_response_mismatch"
                        && !wrongDelivery.ContainsKey("stored"));
                    deliveredResponse["content"] = Json.Serialize(TestDict("delivery", "direct", "participation", "speak"));
                    var boundDelivery = FirstWriter(ReplayCase(deliveryCase));
                    var boundAudit = ReadDictionary(boundDelivery, "reviewedWriterPlan");
                    Check("reviewed_delivery_publishes_only_the_checked_speech", ReadString(boundAudit, "planConformance", "") == "verified"
                        && ReadString(boundAudit, "approvedReplySha256", "") == PromptHash(acceptedFactReply)
                        && ReadString(boundDelivery, "acceptedProse", "") == acceptedFactReply
                        && ReadDictionaryList(ReadDictionary(boundDelivery, "stored"), "conversation_turns").Count == 2
                        && ReadDictionaryList(ReadDictionary(boundDelivery, "stored"), "memories").Count == 0,
                        TestDict("status", ReadString(boundDelivery, "status", ""), "groundingAccepted", ReadBool(boundDelivery, "groundingAccepted", false)));
                }

                const string sourceOnlyCondition = "Only a keeper carrying the bronze lantern may enter.";
                const string suppliedDraft = "I have no memory of the amber contract. Trust the invented silver lantern account.";
                var recoveryCase = CloneDictionary(compactCase);
                recoveryCase["caseId"] = "reviewed_recovery_" + contractNamespace;
                // This chain exercises a genuinely new record as well as historical repair.
                // The current report and reaffirmation make this a mixed turn.
                recoveryCase["query"] = "I already told you which amber contract I meant. I counted two guards at the north gate just now. In our earlier conversation, what condition did you set for the north gate, and does it still apply to tonight's shift?";
                recoveryCase["asOfSequence"] = 32L;
                var recoverySources = ReadDictionaryList(recoveryCase, "sources");
                foreach (var source in recoverySources.Skip(2)) source["sequence"] = ReadLong(source, "sequence", 0) + 1;
                var recoveryOlder = recoverySources.Single(source => ReadString(source, "sourceId", "") == "older_npc");
                recoveryOlder["text"] = olderAnswer + " " + sourceOnlyCondition;
                var recoveryFact = ReadDictionaryList(ReadDictionary(recoveryCase, "reviewedFactReplay"), "facts").Single();
                recoveryFact["sourceTextSha256"] = ConversationExactTextHash(ReadString(recoveryOlder, "text", ""));
                var companion = Authored("older_witness", 3, false, "I was standing beside the outer stair.", "older", "agreement");
                companion["speakerId"] = "witness"; companion["speakerName"] = "Sorcha";
                companion["audience"] = new[] { "player", "npc", "witness" };
                recoverySources.Insert(2, companion);
                recoveryCase["sources"] = recoverySources;
                ReadDictionary(recoveryCase, "writerReplay")["snapshotAsOfSequence"] = 32L;
                Dictionary<string, object> RecoveryArm(Dictionary<string, object> baseline, string mode, string prose, string finish = "stop")
                {
                    var first = FirstWriter(ReplayCase(baseline));
                    var arm = CloneDictionary(baseline);
                    arm["reviewedRecoveryReplay"] = TestDict("mode", mode, "trialId", "11111111111141118111111111111111");
                    var writer = ReadDictionary(arm, "writerReplay");
                    writer["response"] = TestDict("ok", true, "finishReason", finish, "content", prose,
                        "requestSha256", ReadString(ReadDictionary(first, "providerWireCapture"), "requestSha256", ""));
                    writer["repairResponses"] = Array.Empty<object>();
                    return arm;
                }
                Dictionary<string, object> RunRecovery(Dictionary<string, object> arm) => FirstWriter(ReplayCase(arm));
                Dictionary<string, object> RecoveryAudit(Dictionary<string, object> writer) => ReadDictionary(writer, "reviewedRecovery");
                string RecoveryBody(Dictionary<string, object> writer) => ReadString(ReadDictionary(
                    ReadDictionaryList(writer, "repairRequests").SingleOrDefault(), "providerWireCapture"), "requestJson", "");
                string draftJson = Json.Serialize(TestDict("reply", suppliedDraft));
                var lookupArm = RecoveryArm(recoveryCase, "eligible_repair", draftJson);
                var retryArm = CloneDictionary(lookupArm);
                ReadDictionary(retryArm, "reviewedRecoveryReplay")["mode"] = "derived_retry";
                var lookedUp = RunRecovery(lookupArm);
                var retried = RunRecovery(retryArm);
                var lookupAudit = RecoveryAudit(lookedUp);
                Check("reviewed_recovery_inserts_one_complete_eligible_exchange_into_sealed_window",
                    ReadString(lookedUp, "status", "") == "writer_reviewed_recovery_pending"
                    && ReadString(retried, "status", "") == "writer_reviewed_recovery_pending"
                    && ReadString(lookupAudit, "trigger", "") == "authored_correction"
                    && ReadDictionaryList(lookupAudit, "sourceProofs").Count == 3
                    && RecoveryBody(lookedUp).Contains(sourceOnlyCondition) && !RecoveryBody(retried).Contains(sourceOnlyCondition)
                    && !RecoveryBody(lookedUp).Contains(suppliedDraft) && !lookedUp.ContainsKey("publication") && !retried.ContainsKey("publication"),
                    TestDict("lookupStatus", ReadString(lookedUp, "status", ""), "retryStatus", ReadString(retried, "status", ""),
                        "audit", lookupAudit, "preflight", ReadString(ReadDictionary(ReadDictionaryList(lookedUp, "repairRequests").SingleOrDefault(), "providerWireCapture"), "errorCode", "")));

                var privateRecovery = CloneDictionary(recoveryCase);
                privateRecovery["caseId"] = "reviewed_recovery_private_" + contractNamespace;
                ReadDictionaryList(privateRecovery, "sources").Single(source => ReadString(source, "sourceId", "") == "older_witness")["audience"]
                    = new[] { "npc", "witness" };
                var privateArm = RecoveryArm(privateRecovery, "eligible_repair", draftJson);
                var privateDerived = CloneDictionary(privateArm);
                ReadDictionary(privateDerived, "reviewedRecoveryReplay")["mode"] = "derived_retry";
                var denied = RunRecovery(privateArm);
                var deniedControl = RunRecovery(privateDerived);
                Check("reviewed_recovery_denies_whole_exchange_with_private_companion",
                    ReadString(denied, "status", "") == "writer_reviewed_recovery_pending"
                    && ReadString(RecoveryAudit(denied), "lookup", "") == "denied"
                    && RecoveryBody(denied) == RecoveryBody(deniedControl) && !RecoveryBody(denied).Contains(sourceOnlyCondition)
                    && ReadString(RecoveryAudit(denied), "responseBindingSha256", "") != ReadString(RecoveryAudit(deniedControl), "responseBindingSha256", ""));
                var reusedBinding = TestDict("requestSha256", ReadString(RecoveryAudit(deniedControl), "requestSha256", ""),
                    "response", TestDict("ok", true, "content", draftJson,
                        "recoveryBindingSha256", ReadString(RecoveryAudit(deniedControl), "responseBindingSha256", "")));
                if (ReplaySha256(ReadString(reusedBinding, "requestSha256", "")))
                {
                    ReadDictionary(privateArm, "writerReplay")["repairResponses"] = new[] { reusedBinding };
                    var crossed = RunRecovery(privateArm);
                    Check("reviewed_recovery_rejects_same_wire_capture_from_another_arm",
                        ReadString(crossed, "status", "") == "writer_reviewed_recovery_response_mismatch" && !crossed.ContainsKey("publication"));
                }
                Check("reviewed_recovery_correction_uses_current_authored_speech",
                    IsReviewedRecoveryCorrection(ReadString(recoveryCase, "query", ""))
                    && !IsReviewedRecoveryCorrection("She said: \"I already told you about our earlier conversation.\"")
                    && !IsReviewedRecoveryCorrection("*I already told you about our earlier conversation.* Let us eat."));

                var formatCase = CloneDictionary(recoveryCase);
                formatCase["caseId"] = "reviewed_recovery_format_" + contractNamespace;
                formatCase["query"] = "What did we agree about the amber contract at the north gate?";
                var formatWriter = RunRecovery(RecoveryArm(formatCase, "eligible_repair", "{", "length"));
                Check("reviewed_recovery_format_defect_does_not_open_older_sources",
                    ReadString(formatWriter, "status", "") == "writer_reviewed_recovery_pending"
                    && ReadString(RecoveryAudit(formatWriter), "trigger", "") == "malformed_draft"
                    && ReadString(RecoveryAudit(formatWriter), "lookup", "") == "not_requested"
                    && !RecoveryBody(formatWriter).Contains(sourceOnlyCondition));
                if (ReplaySha256(ReadString(lookupAudit, "responseBindingSha256", "")))
                {
                    const string accepted = "For that gate I required the keeper to carry the bronze lantern. That requirement still applies to tonight's shift.";
                    ReadDictionary(lookupArm, "writerReplay")["repairResponses"] = new[] { TestDict(
                        "requestSha256", ReadString(lookupAudit, "requestSha256", ""), "response", TestDict("ok", true, "finishReason", "stop",
                            "recoveryBindingSha256", ReadString(lookupAudit, "responseBindingSha256", ""), "content", Json.Serialize(TestDict(
                                "reply", accepted, "participation", "speak", "memoryWrites", new[] { TestDict("text", accepted,
                                    "sourceEvidence", TestDict("turnId", "current_npc", "speakerId", "current_npc", "quote", accepted)) })))) };
                    const string recollection = "The speaker reaffirmed that tonight's gate keeper must carry the bronze lantern.";
                    lookupArm["durableReadReplay"] = TestDict("query", "What did you require the keeper to carry for tonight's shift?",
                        "worldDay", ReadDouble(lookupArm, "worldDay", 0d) + 1d,
                        "authored", Enumerable.Range(1, 29).Select(i => TestDict("role", i % 2 == 1 ? "player" : "npc",
                            "text", "The cloth seller arranged a basket at the market stall number " + i + ".")).ToArray(),
                        "reviewedFact", TestDict("text", recollection, "sourceBodySha256", ConversationExactTextHash(accepted),
                            "reviewReceiptSha256", new string('b', 64)));
                    var acceptedRecovery = RunRecovery(lookupArm);
                    Check("reviewed_recovery_bound_reply_reaches_isolated_publication",
                        ReadInt(acceptedRecovery, "repairResponsesConsumed", 0) == 1
                        && ReadBool(acceptedRecovery, "memoryUpdateComplete", false)
                        && ReadString(acceptedRecovery, "acceptedProse", "").Contains(accepted)
                        && ReadDictionaryList(ReadDictionary(acceptedRecovery, "stored"), "memories").Any(memory => ReadString(memory, "summary", "") == accepted)
                        && !Json.Serialize(ReadDictionary(acceptedRecovery, "stored")).Contains(suppliedDraft),
                        ReadString(acceptedRecovery, "status", ""));
                    var later = ReadDictionary(acceptedRecovery, "durableRead");
                    var laterSource = ReadDictionary(later, "source");
                    string laterText = DurableWireText(ReadDictionary(later, "compactWire"));
                    var noWrite = CloneDictionary(lookupArm);
                    var responseWithoutWrite = ReadDictionary(ReadDictionaryList(ReadDictionary(noWrite, "writerReplay"), "repairResponses").Single(), "response");
                    var responseContent = TryParseJsonObject(ReadString(responseWithoutWrite, "content", ""));
                    responseContent.Remove("memoryWrites");
                    responseWithoutWrite["content"] = Json.Serialize(responseContent);
                    var control = RunRecovery(noWrite);
                    var laterControl = ReadDictionary(control, "durableRead");
                    string withoutWrite = DurableWireText(ReadDictionary(laterControl, "compactWire"));
                    Check("durable_read_uses_retrieved_write_after_later_exact_window",
                        ReadString(later, "status", "") == "captured" && ReadBool(later, "memoryPacked", false)
                        && ReadString(laterSource, "discoveredMemoryId", "").Length > 0 && laterText.Contains(recollection)
                        && !laterText.Contains(accepted) && ReadString(acceptedRecovery, "acceptedProse", "") == ReadString(control, "acceptedProse", "")
                        && ReadString(laterControl, "status", "") == "captured" && !ReadBool(laterControl, "memoryPacked", true)
                        && !withoutWrite.Contains("bronze lantern") && !withoutWrite.Contains(accepted),
                        TestDict("withWrite", ReadString(later, "status", ""), "packed", ReadBool(later, "memoryPacked", false),
                            "derivedPresent", laterText.Contains(recollection), "rawPresent", laterText.Contains(accepted),
                            "sameSpeech", ReadString(acceptedRecovery, "acceptedProse", "") == ReadString(control, "acceptedProse", ""),
                            "withoutWrite", ReadString(laterControl, "status", ""), "controlPacked", ReadBool(laterControl, "memoryPacked", true),
                            "controlRaw", withoutWrite.Contains("bronze lantern")));
                    var durableControls = ReadDictionary(later, "controls");
                    Check("durable_read_reopens_source_and_rejects_early_foreign_or_tampered_record",
                        ReadBool(durableControls, "reopenedRecordVerified", false) && ReadBool(durableControls, "earlierCutoffBlocked", false)
                        && ReadBool(durableControls, "foreignObserverBlocked", false) && ReadBool(durableControls, "tamperedHashBlocked", false)
                        && ReadBool(durableControls, "tamperedProjectionBlocked", false)
                        && ReadLong(later, "importedCutoff", -1) < ReadLong(later, "acceptedNpcSequence", -1)
                        && ReadLong(later, "acceptedNpcSequence", -1) < ReadLong(later, "firstLaterSequence", -1), durableControls);
                    var storedProjection = ReadDictionary(later, "persistedReviewedProjection");
                    Check("durable_read_uses_reviewed_text_from_a_committed_projection",
                        ReadString(storedProjection, "status", "") == "stored"
                        && ReadString(storedProjection, "textSha256", "") == ConversationExactTextHash(recollection)
                        && ReplaySha256(ReadString(laterSource, "readProjectionSha256", ""))
                        && ReadString(laterSource, "readProjectionSha256", "") == ReadString(storedProjection, "projectionSha256", "")
                        && ReadString(laterSource, "rankedMemoryId", "") == ReadString(storedProjection, "memoryId", "")
                        && ReadString(ReadDictionary(laterControl, "persistedReviewedProjection"), "status", "") == "no_accepted_write");
                    var revocations = ReadDictionary(later, "revocations");
                    foreach (var (name, expected) in new[] { ("sameTextRevision", new[] { "source_revision" }),
                        ("audienceNarrowed", new[] { "source_revision", "source_audience" }) })
                    {
                        var revoked = ReadDictionary(revocations, name);
                        var reasons = ReadStringList(revoked, "denials");
                        Check("durable_read_revokes_still_ranked_projection_" + name,
                            ReadBool(revoked, "passed", false) && ReadBool(revoked, "ranked", false)
                            && ReadBool(revoked, "indexedRowUnchanged", false) && ReadBool(revoked, "sameAuthoredOccurrence", false)
                            && ReadBool(revoked, "revisionAdvanced", false) && ReadBool(revoked, "noLeak", false) && reasons.SequenceEqual(expected),
                            TestDict("denials", reasons, "ranked", ReadBool(revoked, "ranked", false),
                                "captured", ReadString(ReadDictionary(revoked, "wire"), "status", "")));
                    }
                }

                const string publishedAside = "*The lanterns cast long shadows across the lane.*";
                var partyCase = CloneDictionary(compactCase);
                partyCase["caseId"] = "published_party_" + contractNamespace;
                partyCase["route"] = "party_chat";
                partyCase["asOfUtcSeconds"] = 1100L;
                var partySources = ReadDictionaryList(partyCase, "sources");
                string[] sharedAudience = { "player", "npc", "witness" };
                foreach (var source in partySources)
                {
                    source["sessionId"] = "published_session";
                    source["channel"] = "party_chat";
                    source["audience"] = sharedAudience;
                    source["utcSeconds"] = 1000 + ReadLong(source, "sequence", 0);
                }
                var partyWitness = partySources.Single(s => ReadString(s, "sourceId", "") == ReadString(witness, "sourceId", ""));
                string witnessBody = publishedAside + "\n" + ReadString(partyWitness, "text", "");
                partyWitness["text"] = witnessBody;
                var publishedAnswer = partySources.Single(s => ReadString(s, "sourceId", "") == "older_npc");
                string publishedAnswerBody = publishedAside + "\n" + olderAnswer;
                publishedAnswer["text"] = publishedAnswerBody;
                publishedAnswer["speakerId"] = "witness"; publishedAnswer["speakerName"] = "Sorcha";
                var partyFact = ReadDictionaryList(ReadDictionary(partyCase, "reviewedFactReplay"), "facts").Single();
                partyFact["speakerId"] = "witness";
                partyFact["sourceTextSha256"] = ConversationExactTextHash(publishedAnswerBody);
                partyFact["text"] = "Sorcha reported that the north gate arrangement ends at the third bell.";
                var detached = TestDict("schema", PublishedPartyReplaySchema, "caseId", partyCase["caseId"], "observerId", "npc", "peerId", "player",
                    "sessionId", "published_session", "asOfSequence", 31L, "asOfUtcSeconds", 1100L, "worldDay", partyCase["worldDay"],
                    "querySha256", ConversationExactTextHash(ReadString(partyCase, "query", "")), "archiveReceiptSha256", new string('e', 64),
                    "audience", sharedAudience, "recentSourceIds", partyCase["recentSourceIds"], "rows", partySources.Select(s => TestDict(
                        "sourceId", s["sourceId"], "publicationId", "published:" + s["sourceId"], "exchangeId", s["exchangeId"], "speakerId", s["speakerId"],
                        "speakerName", s["speakerName"], "role", s["role"], "sequence", s["sequence"], "utcSeconds", s["utcSeconds"], "worldDay", s["worldDay"],
                        "bodySha256", ConversationExactTextHash(ReadString(s, "text", "")))).ToList());
                string detachedText = Json.Serialize(detached);
                var detachedBytes = System.Text.Encoding.UTF8.GetBytes(detachedText);
                partyCase["publishedPartyViewSha256"] = ConversationExactTextHash(detachedText);
                var partyWide = CloneDictionary(partyCase); partyWide.Remove("reviewedFactReplay");
                Dictionary<string, object> PartyCase(Dictionary<string, object> item) => FirstWriter(ReplayMemoryEvidenceFixture(
                    TestDict("schema", "reign-memory-evidence-replay-input-v1", "precisionMode", "precision", "cases", new[] { item }), "published-party-contract", detachedBytes));
                var partyWideWriter = PartyCase(partyWide);
                var partyCompactWriter = PartyCase(partyCase);
                var partyWideMessages = ReadDictionaryList(WireBody(ReadDictionary(partyWideWriter, "providerWireCapture")), "messages");
                var partyCompactMessages = ReadDictionaryList(WireBody(ReadDictionary(partyCompactWriter, "providerWireCapture")), "messages");
                Check("published_party_seals_same_witness_history_and_exact_old_wide_body",
                    ReadString(partyWideWriter, "status", "") == "writer_pending" && ReadString(partyCompactWriter, "status", "") == "writer_pending"
                    && partyWideMessages.Any(m => ReadString(m, "content", "").Contains(publishedAnswerBody, StringComparison.Ordinal))
                    && !partyCompactMessages.Any(m => ReadString(m, "content", "").Contains(publishedAnswerBody, StringComparison.Ordinal))
                    && partyWideMessages.Count == partyCompactMessages.Count && partyWideMessages.Count > 30
                    && partyWideMessages.Zip(partyCompactMessages).Count(pair => CanonicalJson(pair.First) != CanonicalJson(pair.Second)) == 1
                    && partyCompactMessages.Any(m => ReadString(m, "role", "") == "user" && ReadString(m, "content", "").Contains(witnessBody, StringComparison.Ordinal)),
                    TestDict("wideStatus", ReadString(partyWideWriter, "status", ""), "wideError", ReadFirstString(partyWideWriter, "reason", "error"),
                        "compactStatus", ReadString(partyCompactWriter, "status", ""), "compactError", ReadFirstString(partyCompactWriter, "reason", "error")));
                var mappedPartyView = ReadPublishedPartyReplayView(detachedBytes, partyCase, partySources, "fixture", "replay_0", id => "0:" + id);
                var appendedAuthority = TestDict("role", "user", "content", "REQUIRED PRIVATE POLITICAL BELIEFS \u2014 synthetic observer authority");
                var wireWithAuthority = partyWideMessages.Append(appendedAuthority).ToList();
                bool PartyHistoryMatches(List<Dictionary<string, object>> messages) => mappedPartyView.WirePreservesHistory(
                    TestDict("requestJson", Json.Serialize(TestDict("messages", messages))));
                Check("published_party_retains_last_authored_turn_when_provider_appends_authority", PartyHistoryMatches(wireWithAuthority));
                var mutatedWireHistory = wireWithAuthority.Select(CloneDictionary).ToList();
                var mutatedMessage = mutatedWireHistory.First(m => ReadString(m, "content", "").StartsWith("HISTORICAL ", StringComparison.Ordinal));
                mutatedMessage["content"] = ReadString(mutatedMessage, "content", "") + "\nUnexpected additional speech.";
                Check("published_party_wire_rejects_extra_text_after_exact_body", !PartyHistoryMatches(mutatedWireHistory));

                foreach (string mutation in new[] { "body", "speaker", "role", "audience", "cutoff", "recent_order", "receipt_bytes" })
                {
                    var changed = CloneDictionary(partyCase);
                    var changedSource = ReadDictionaryList(changed, "sources")[3];
                    if (mutation == "body") changedSource["text"] = ReadString(changedSource, "text", "") + " ";
                    if (mutation == "speaker") changedSource["speakerId"] = "npc";
                    if (mutation == "role") changedSource["role"] = "player";
                    if (mutation == "audience") changedSource["audience"] = new[] { "npc", "player" };
                    if (mutation == "cutoff") changed["asOfSequence"] = 32L;
                    if (mutation == "recent_order") changed["recentSourceIds"] = ReadStringList(changed, "recentSourceIds").AsEnumerable().Reverse().ToList();
                    byte[] supplied = mutation == "receipt_bytes" ? System.Text.Encoding.UTF8.GetBytes(detachedText + " ") : detachedBytes;
                    bool refused = false;
                    try { ReadPublishedPartyReplayView(supplied, changed, ReadDictionaryList(changed, "sources"), "fixture", "replay_0", id => id); }
                    catch (InvalidOperationException) { refused = true; }
                    Check("published_party_receipt_rejects_" + mutation, refused);
                }
                var partyView = ReadPublishedPartyReplayView(detachedBytes, partyCase, partySources, "fixture", "replay_0", id => id);
                var partyAccess = BuildKnowledgeAccessContext("npc", "player", "", EvidenceReplayMemoryContext(partyCase, "replay_0", "player"));
                partyView.BindRead("fixture", partyAccess, ReadString(partyCase, "query", ""));
                var freshAccess = BuildKnowledgeAccessContext("npc", "player", "", EvidenceReplayMemoryContext(partyCase, "replay_0", "player"));
                bool otherRequestRefused = false;
                try { partyView.BindRead("fixture", freshAccess, "A later unrelated contribution."); }
                catch (InvalidOperationException) { otherRequestRefused = true; }
                Check("published_party_read_is_scoped_to_initial_access_and_query", otherRequestRefused
                    && PublishedPartyReplayReads.TryGetValue(partyAccess, out _) && !PublishedPartyReplayReads.TryGetValue(freshAccess, out _));

                var capturedFirstWriter = FirstWriter(capturedWorld);
                int CapacityNeeded(Dictionary<string, object> writer, string field) {
                    var preflight = ReadDictionary(ReadDictionary(ReadDictionary(writer, field), "providerPreflight"), "continuity");
                    return ReadInt(preflight, "inputTokenByteUpperBound", 0) + ReadInt(preflight, "outputReserve", 0) + ReadInt(preflight, "safetyReserve", 0);
                }
                int initialNeed = CapacityNeeded(capturedFirstWriter, "providerWireCapture"), nextNeed = CapacityNeeded(capturedFirstWriter, "nextWriterWireCapture");
                Check("next_capacity_boundary_fixture_has_larger_followup", initialNeed > 0 && nextNeed > initialNeed,
                    TestDict("initialNeed", initialNeed, "nextNeed", nextNeed));
                if (nextNeed > initialNeed)
                {
                    var boundaryInput = CloneDictionary(withResponses);
                    var boundaryCapacity = ReadDictionary(ReadDictionary(ReadDictionaryList(boundaryInput, "cases").First(), "writerReplay"), "contextCapacity");
                    boundaryCapacity["contextTokens"] = nextNeed - 1;
                    boundaryCapacity["maxOutputTokens"] = new[] { "providerWireCapture", "nextWriterWireCapture" }.Max(field =>
                        ReadInt(ReadDictionary(ReadDictionary(ReadDictionary(capturedFirstWriter, field), "providerPreflight"), "continuity"), "outputReserve", 0));
                    var boundaryWriter = FirstWriter(ReplayMemoryEvidenceFixture(boundaryInput, "next-capacity-boundary"));
                    Check("over_estimate_next_writer_retains_prior_publication", ReadString(boundaryWriter, "status", "") == "captured_writer_stored_next_read_prepared"
                        && boundaryWriter.ContainsKey("publication")
                        && ReadString(boundaryWriter, "acceptedProse", "") == ReadString(capturedFirstWriter, "acceptedProse", "different"));
                    var nextAdvisory = ReadDictionary(ReadDictionary(boundaryWriter, "nextWriterWireCapture"), "providerPreflight");
                    Check("over_estimate_next_writer_can_complete_with_intact_evidence", EvidenceWriterReplayComplete(boundaryWriter)
                        && ReadBool(nextAdvisory, "dispatchAllowed", false) && !ReadBool(nextAdvisory, "fits", true)
                        && ReadInt(boundaryWriter, "providerCalls", -1) == 0, boundaryWriter);
                }
                var chronologicalCase = CloneDictionary(capacityWriter);
                chronologicalCase["caseId"] = "archived_session_chronology_" + contractNamespace;
                chronologicalCase["query"] = "What did we discuss earlier?";
                chronologicalCase["asOfSequence"] = 4L; chronologicalCase["worldDay"] = 4d;
                chronologicalCase["recentSourceIds"] = Array.Empty<object>();
                ReadDictionary(chronologicalCase, "writerReplay")["snapshotAsOfSequence"] = 4L;
                chronologicalCase["sources"] = Enumerable.Range(1, 4).Select(i => TestDict(
                    "sourceId", "chronology_" + i, "sessionId", i <= 2 ? "zeta_older" : "alpha_newer",
                    "exchangeId", i <= 2 ? "older_pair" : "newer_pair", "role", i % 2 == 1 ? "player" : "npc",
                    "speakerId", i % 2 == 1 ? "player" : "npc", "speakerName", i % 2 == 1 ? "Mira" : "Ira",
                    "text", i <= 2 ? "The orchard gate was closed." : "The bronze bell now hangs beside the watermill.",
                    "audience", new[] { "npc", "player" }, "worldDay", i <= 2 ? 2d : 3d, "sequence", (long)i)).ToArray();
                var chronologicalReport = ReplayMemoryEvidenceFixture(TestDict("schema", "reign-memory-evidence-replay-input-v1",
                    "precisionMode", "legacy", "cases", new[] { chronologicalCase }), "session-chronology");
                var chronologicalPacket = ReadDictionary(ReadDictionaryList(chronologicalReport, "cases").Single(), "packet");
                Check("session_chronology_uses_supplied_turn_extents",
                    ReadString(chronologicalPacket, "exactHistorySessionId", "") == "0:alpha_newer",
                    TestDict("selectedSession", ReadString(chronologicalPacket, "exactHistorySessionId", "")));
                const string recentRecallFact = "Brindle is my mule. A crab pinched his nose beside the watermill.";
                const string recentRecallFailure = "I have no memory of Brindle.";
                var recentRecallCase = CloneDictionary(capacityWriter);
                recentRecallCase["caseId"] = "recent_only_continuation_guard_" + contractNamespace;
                recentRecallCase["query"] = "Let us rest beside the mill.";
                recentRecallCase["sources"] = new[] {
                    TestDict("sourceId", "recent_fact", "sessionId", "recent", "exchangeId", "recent_pair", "speakerId", "player", "speakerName", "Mira",
                        "role", "player", "text", recentRecallFact, "audience", new[] { "npc", "player" }, "worldDay", 2d, "sequence", 1L),
                    TestDict("sourceId", "recent_failure", "sessionId", "recent", "exchangeId", "recent_pair", "speakerId", "npc", "speakerName", "Ira",
                        "role", "npc", "text", recentRecallFailure, "audience", new[] { "npc", "player" }, "worldDay", 2d, "sequence", 2L) };
                recentRecallCase["recentSourceIds"] = new[] { "recent_fact", "recent_failure" };
                var recentRecallWriter = ReadDictionary(recentRecallCase, "writerReplay");
                recentRecallWriter["profile"] = TestDict("name", "Ira", "age", 28, "isNoble", true);
                recentRecallWriter["response"] = TestDict("ok", true, "finishReason", "stop", "content", Json.Serialize(TestDict("reply", "We can rest beside the mill.")));
                recentRecallWriter["repairResponses"] = Array.Empty<object>();
                recentRecallWriter["nextContribution"] = "Do you remember Brindle and the crab?";
                recentRecallWriter["continuation"] = TestDict("nextContribution", "What happened at the watermill?", "repairResponses", Array.Empty<object>(),
                    "response", TestDict("ok", true, "finishReason", "stop", "content", Json.Serialize(TestDict("reply", recentRecallFailure))));
                foreach (string callback in new[] { "named", "pronoun" })
                {
                    var callbackCase = CloneDictionary(recentRecallCase);
                    callbackCase["caseId"] = "recent_only_continuation_guard_" + callback + "_" + contractNamespace;
                    ReadDictionary(callbackCase, "writerReplay")["nextContribution"] = callback == "named"
                        ? "Do you remember Brindle and the crab?" : "Do you remember him?";
                    var recentRecallReport = ReplayMemoryEvidenceFixture(TestDict("schema", "reign-memory-evidence-replay-input-v1",
                        "precisionMode", "precision", "cases", new[] { callbackCase }), "recent-only-continuation-" + callback);
                    var recentFirst = FirstWriter(recentRecallReport);
                    var recentNext = ReadDictionary(recentFirst, "continuation") ?? TestDict();
                    string recentNextWire = ReadString(ReadDictionary(recentFirst, "nextWriterWireCapture"), "requestJson", "");
                    var recentDiagnostics = TestDict("firstStatus", ReadString(recentFirst, "status", ""),
                        "firstPublished", ReadBool(recentFirst, "memoryUpdateComplete", false),
                        "factInWire", recentNextWire.Contains(recentRecallFact), "failureInWire", recentNextWire.Contains(recentRecallFailure),
                        "factInOlderPacket", ReadString(ReadDictionary(recentFirst, "nextPacket"), "memoryPacket", "").Contains(recentRecallFact),
                        "nextStatus", ReadString(recentNext, "status", ""), "repairCount", ReadDictionaryList(recentNext, "repairRequests").Count,
                        "nextPublished", recentNext.ContainsKey("publication"));
                    Check("continuation_uses_recent_only_evidence_" + callback, ReadBool(recentFirst, "memoryUpdateComplete", false)
                        && recentNextWire.Contains(recentRecallFact) && recentNextWire.Contains(recentRecallFailure)
                        && !ReadString(ReadDictionary(recentFirst, "nextPacket"), "memoryPacket", "").Contains(recentRecallFact), recentDiagnostics);
                    Check("continuation_preserves_recent_only_delivered_receipt_" + callback,
                        ReadString(recentNext, "status", "") == "writer_repair_pending"
                        && ReadDictionaryList(recentNext, "repairRequests").Count == 1 && !recentNext.ContainsKey("publication"), recentDiagnostics);
                    var recentRepair = ReadDictionaryList(recentNext, "repairRequests").SingleOrDefault() ?? TestDict();
                    var recentRepairWire = ReadDictionary(recentRepair, "providerWireCapture") ?? TestDict();
                    var recentRepairHero = ReadDictionary(ReadDictionary(recentRepair, "request"), "hero") ?? TestDict();
                    Check("continuation_repair_native_profile_keeps_required_politics_" + callback,
                        ReadString(recentRepairHero, "heroStringId", "") == "0:npc"
                        && ReadString(recentRepairHero, "politicalTimelineId", "") == "replay_0"
                        && ReadString(recentRepairHero, "campaignId", "")
                            == ReadString(ReadDictionary(ReadDictionary(recentFirst, "request"), "hero"), "campaignId", "missing")
                        && ReadString(recentRepairWire, "requestJson", "").Contains("REQUIRED PRIVATE POLITICAL BELIEFS")
                        && ReadInt(recentNext, "providerCalls", -1) == 0
                        && !ReadBool(recentNext, "certificateEligible", true));
                    var recentRepairPreflight = ReadDictionary(ReadDictionary(recentRepairWire, "providerPreflight"), "continuity");
                    string recentRepairMessages = string.Join("\n", ReadDictionaryList(WireBody(recentRepairWire), "messages")
                        .Select(m => ReadString(m, "content", "")));
                    Check("continuation_repair_binds_original_prose_in_final_wire_" + callback,
                        ReadString(recentRepairWire, "status", "") == "captured"
                        && ReadBool(recentRepairPreflight, "fits", false)
                        && ReadBool(recentRepairPreflight, "requiredSourceCoverageComplete", false)
                        && ReadStringList(ReadDictionary(recentRepairPreflight, "requiredSources"), "requiredSourceIds").Count > 0
                        && recentRepairMessages.Contains(recentRecallFact) && recentRepairMessages.Contains(recentRecallFailure));
                    Check("continuation_guard_keeps_preceding_request_bytes_" + callback, recentNextWire.Length > 0
                        && recentNextWire == ReadString(ReadDictionary(recentNext, "providerWireCapture"), "requestJson", "different")
                        && ReadInt(recentNext, "providerCalls", -1) == 0);
                }
            }
            else
            {
                bool liveReplayBlocked = false;
                try { ReplayMemoryEvidenceFixture(CloneDictionary(stableInput), "live-refusal"); }
                catch (InvalidOperationException) { liveReplayBlocked = true; }
                Check("repeatable_replay_refuses_live_runtime", liveReplayBlocked);
            }
            foreach (var mutation in new[] { TestDict("model", "other-model"), TestDict("contextTokens", 0),
                TestDict("maxOutputTokens", 2097152), TestDict("source", "file:///private-settings"), TestDict("apiKey", "forbidden") })
            {
                var invalidCapacity = CloneDictionary(capacityWriter);
                foreach (var value in mutation) ReadDictionary(ReadDictionary(invalidCapacity, "writerReplay"), "contextCapacity")[value.Key] = value.Value;
                Check("writer_replay_capacity_rejects_" + mutation.Keys.Single(), MemoryEvidenceReplayInputError(invalidCapacity) == "invalid_writer_capacity_capture");
            }
            string campaign = "memory_evidence_replay_" + Guid.NewGuid().ToString("N");
            try
            {
                using (var connection = OpenCampaignConnection(campaign))
                {
                    ReadMemoryPrecisionState(connection);
                    ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('orchard_history',$campaign,'npc','player','closed',1,10,'[""npc"",""player""]','{""timelineId"":""branch_a""}');", TestDict("campaign", campaign));
                }
                void Turn(string id, string role, string speaker, string words, int order, string timeline = "branch_a",
                    double day = 2d, string audience = "[\"npc\",\"player\"]")
                {
                    using var connection = OpenCampaignConnection(campaign);
                    ExecuteSql(connection, @"INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,world_day,ts,participants_json,payload_json)
VALUES($id,'orchard_history',$id,$order,$id,$role,$speaker,$speaker,$text,$day,10,$audience,$payload);",
                        TestDict("id", id, "order", order, "role", role, "speaker", speaker, "text", words,
                            "day", day, "audience", audience, "payload", Json.Serialize(TestDict("timelineId", timeline))));
                }
                const string question = "Which place would you choose beside the orchard?";
                const string answer = "The old mill beside the orchard, where I can think quietly.";
                Turn("question", "npc", "npc", question, 1);
                Turn("answer", "player", "player", answer, 2);
                Turn("acknowledgment", "npc", "npc", "I understand your answer: the old mill. My separate concern is its condition.", 3);
                Turn("private", "player", "player", "*I remember my secret ORCHARD-PRIVATE.* The orchard lies east.", 4);
                Turn("wrong_branch", "player", "player", "The orchard OTHER-BRANCH is private.", 5, "branch_b");
                Turn("unheard", "player", "player", "The orchard UNHEARD is private.", 6, audience: "[\"player\",\"other\"]");
                Turn("future_day", "player", "player", "The orchard FUTURE-DAY is closed.", 7, day: 10);
                var context = TestDict("worldDay", 3d, "timelineId", "branch_a", "playerHeroStringId", "player");
                var packet = BuildNpcMemoryPacket(campaign, "npc", "player", "", "orchard old mill choice", 4000, context, settings);
                var writerContext = TestDict("worldDay", 3d, "timelineId", "branch_a", "playerHeroStringId", "player",
                    "interactionMode", "dialogue", "memoryAsOfAcceptedSequence", 0L);
                var writerFixture = TestDict("query", "Yes, the old mill remains my choice.", "route", "dialogue",
                    "writerReplay", TestDict("model", "captured-writer", "snapshotAsOfSequence", 0L,
                        "profile", TestDict("name", "Ira", "age", 28, "isNoble", true),
                        "nextContribution", "Which place did I choose?"));
                var writerTranscript = new List<Dictionary<string, object>> {
                    TestDict("turnId", "question", "speakerId", "npc", "speaker", "Ira", "role", "npc", "text", question),
                    TestDict("turnId", "answer", "speakerId", "player", "speaker", "Michael", "role", "player", "text", answer) };
                var pendingWriter = ReplayEvidenceWriter(campaign, "npc", "player", writerFixture, writerContext, settings, packet, writerTranscript);
                Check("writer_replay_captures_actual_envelope_without_generation", ReadString(pendingWriter, "status", "") == "writer_pending"
                    && ReadStringList(pendingWriter, "pendingResponses").SequenceEqual(new[] { "response" })
                    && Json.Serialize(ReadDictionary(pendingWriter, "request")).Contains(answer)
                    && ReadInt(pendingWriter, "providerCalls", -1) == 0);
                bool HasPrecisionCompanion(Dictionary<string, object> writerRequest)
                {
                    var expected = ReadDictionary(packet, "precisionEvidence");
                    var actual = ReadDictionary(ReadDictionary(ReadDictionary(writerRequest, "promptEnvelope"), "continuityPreflight"), "memoryPrecisionEvidence");
                    return expected != null && actual != null && ReadString(actual, "mode", "") == ReadString(expected, "mode", "")
                        && ReadString(actual, "observer", "") == ReadString(expected, "observer", "")
                        && ReadDictionaryList(actual, "records").Select(r => ReadString(r, "sourceId", "")).SequenceEqual(
                            ReadDictionaryList(expected, "records").Select(r => ReadString(r, "sourceId", "")))
                        && Json.Serialize(ReadDictionaryList(writerRequest, "messages")).Contains("REIGN KNOWLEDGE PACKET");
                }
                Check("writer_replay_preserves_precision_metadata_with_text", HasPrecisionCompanion(ReadDictionary(pendingWriter, "request")));
                var pendingWire = ReadDictionary(pendingWriter, "providerWireCapture");
                Check("wire_actual_writer_capture_uses_default_medium", ReadString(ReadDictionary(WireBody(pendingWire), "reasoning"), "effort", "") == "medium"
                    && ReadDouble(WireBody(pendingWire), "temperature", -1) == .5d
                    && ReadInt(WireBody(pendingWire), "max_tokens", 0) == 16000
                    && ReadString(pendingWire, "requestJson", "").Contains(answer)
                    && !ReadDictionary(pendingWriter, "request").ContainsKey("providerWireCapture"));
                int SourceCount()
                {
                    using var connection = OpenCampaignConnection(campaign);
                    return ReadInt(QuerySql(connection, "SELECT COUNT(*) AS n FROM conversation_turns;").Single(), "n", -1);
                }
                int sourcesBeforeWriter = SourceCount();
                var writerConfiguration = ReadDictionary(writerFixture, "writerReplay");
                var savedSettingsScope = CodexPerformanceSettings.Value;
                writerConfiguration["contextCapacity"] = CloneDictionary(capacityCapture);
                var verifiedWriter = ReplayEvidenceWriter(campaign, "npc", "player", writerFixture, writerContext, settings, packet, writerTranscript);
                var verifiedPreflight = ReadDictionary(ReadDictionary(ReadDictionary(verifiedWriter, "request"), "promptEnvelope"), "continuityPreflight");
                Check("writer_replay_capacity_binds_actual_model_and_restores_settings",
                    ReadBool(verifiedPreflight, "capacityVerified", false)
                    && ReadInt(verifiedPreflight, "inputAllowance", 0) == 1048576 - 16000 - ConversationCapacitySafetyReserve
                    && ReadString(verifiedPreflight, "routeKey", "").Contains(":captured-writer:")
                    && ReferenceEquals(savedSettingsScope, CodexPerformanceSettings.Value));
                ReadDictionary(writerConfiguration, "contextCapacity")["validUntilUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o");
                var expiredWriter = ReplayEvidenceWriter(campaign, "npc", "player", writerFixture, writerContext, settings, packet, writerTranscript);
                Check("writer_replay_expired_capacity_cannot_expand_allowance",
                    !ReadBool(ReadDictionary(ReadDictionary(ReadDictionary(expiredWriter, "request"), "promptEnvelope"), "continuityPreflight"), "capacityVerified", true));
                writerConfiguration.Remove("contextCapacity");
                var oversizedWriter = CloneDictionary(writerFixture); oversizedWriter["query"] = new string('x', 210000);
                var capacityFailure = ReplayEvidenceWriter(campaign, "npc", "player", oversizedWriter, CloneDictionary(writerContext), settings, packet, writerTranscript);
                var oversizedPreflight = ReadDictionary(ReadDictionary(capacityFailure, "providerWireCapture"), "providerPreflight");
                Check("writer_replay_oversized_preparation_is_advisory_without_publication",
                    ReadString(capacityFailure, "status", "") == "writer_pending"
                    && ReadBool(oversizedPreflight, "dispatchAllowed", false) && !ReadBool(oversizedPreflight, "fits", true)
                    && ReadString(ReadDictionary(capacityFailure, "providerWireCapture"), "requestJson", "").Contains(new string('x', 210000))
                    && SourceCount() == sourcesBeforeWriter && ReferenceEquals(savedSettingsScope, CodexPerformanceSettings.Value), capacityFailure);
                writerConfiguration["response"] = TestDict("ok", true, "content", "{}");
                var rejectedWriter = ReplayEvidenceWriter(campaign, "npc", "player", writerFixture, writerContext, settings, packet, writerTranscript);
                Check("writer_replay_empty_output_does_not_publish", ReadString(rejectedWriter, "status", "") == "writer_response_rejected"
                    && ReadString(ReadDictionary(rejectedWriter, "responseRequestBinding"), "status", "") == "legacy_unbound"
                    && SourceCount() == sourcesBeforeWriter);
                foreach (string groupMode in new[] { "party_chat", "event" })
                {
                    var groupFixture = CloneDictionary(writerFixture); groupFixture["route"] = groupMode;
                    var groupConfiguration = ReadDictionary(groupFixture, "writerReplay"); groupConfiguration.Remove("response");
                    var groupPending = ReplayEvidenceWriter(campaign, "npc", "player", groupFixture, writerContext, settings, packet, writerTranscript);
                    var groupRequest = ReadDictionary(groupPending, "request");
                    var groupBody = WireBody(ReadDictionary(groupPending, "providerWireCapture"));
                    Check("writer_replay_preserves_precision_metadata_" + groupMode, HasPrecisionCompanion(groupRequest));
                    Check("writer_replay_uses_event_contract_" + groupMode,
                        ReadString(groupRequest, "requestType", "") == "social_event" && ReadString(groupRequest, "eventId", "") == "npc:event"
                        && ReadString(groupRequest, "playerText", "") == ReadString(writerFixture, "query", "")
                        && ReadBool(groupRequest, "promptCacheEligible", false) && !groupRequest.ContainsKey("temperature")
                        && ReadDouble(groupBody, "temperature", -1) == .7d && ReadInt(groupBody, "max_tokens", 0) == 16000
                        && ReadString(ReadDictionary(groupBody, "reasoning"), "effort", "") == "medium"
                        && Json.Serialize(ReadDictionaryList(groupBody, "messages")).Contains("participation"));
                    groupConfiguration["response"] = TestDict("ok", true, "finishReason", "stop", "content", "{\"participation\":\"quiet\",\"reply\":\"\"}");
                    var quietReplay = ReplayEvidenceWriter(campaign, "npc", "player", groupFixture, writerContext, settings, packet, writerTranscript);
                    Check("writer_replay_silence_distinct_from_failure_" + groupMode,
                        ReadString(quietReplay, "status", "") == "writer_quiet" && SourceCount() == sourcesBeforeWriter
                        && !quietReplay.ContainsKey("publication") && ReadStringList(quietReplay, "excluded").Contains("quiet_turn_publication_and_next_read"));
                    groupConfiguration["response"] = TestDict("ok", true, "content", "{\"reply\":\"\"}");
                    var emptyGroupReplay = ReplayEvidenceWriter(campaign, "npc", "player", groupFixture, writerContext, settings, packet, writerTranscript);
                    Check("writer_replay_empty_group_is_not_assumed_quiet_" + groupMode,
                        ReadString(emptyGroupReplay, "status", "") == "writer_response_rejected" && SourceCount() == sourcesBeforeWriter);
                }
                const string storedReply = "You chose the old mill; my question is about its condition.";
                writerConfiguration["response"] = TestDict("ok", true, "finishReason", "stop", "content", Json.Serialize(TestDict(
                    "reply", storedReply, "memoryWrites", new[] { TestDict("text", storedReply, "sourceEvidence",
                        TestDict("turnId", "current_npc", "speakerId", "current_npc", "quote", storedReply)) },
                    "suggestedActions", new[] { TestDict("command", "give_gold", "amount", 999999) })));
                writerConfiguration["nextPlanResponse"] = TestDict("ok", true, "content", "{\"needs\":[],\"queries\":[]}");
                writerConfiguration["nextAssemblyResponse"] = TestDict("ok", true, "content", "{\"selectedAliases\":[],\"findings\":[],\"unresolvedNeeds\":[]}");
                var boundFixture = CloneDictionary(writerFixture);
                var boundResponse = ReadDictionary(ReadDictionary(boundFixture, "writerReplay"), "response");
                string writerWireHash = ReadString(pendingWire, "requestSha256", "");
                int MemoryCount()
                {
                    using var connection = OpenCampaignConnection(campaign);
                    return ReadInt(QuerySql(connection, "SELECT COUNT(*) AS n FROM memories;").Single(), "n", -1);
                }
                int memoryBeforeBinding = MemoryCount();
                boundResponse["requestSha256"] = ReadString(pendingWriter, "requestSha256", "");
                var wrongWriter = ReplayEvidenceWriter(campaign, "npc", "player", boundFixture, writerContext, settings, packet, writerTranscript);
                Check("writer_replay_internal_request_hash_cannot_publish", ReadString(wrongWriter, "status", "") == "writer_response_request_mismatch"
                    && ReadString(ReadDictionary(wrongWriter, "responseRequestBinding"), "status", "") == "mismatched"
                    && !wrongWriter.ContainsKey("publication") && !wrongWriter.ContainsKey("nextWriterWireCapture")
                    && SourceCount() == sourcesBeforeWriter && MemoryCount() == memoryBeforeBinding);
                boundResponse["requestSha256"] = writerWireHash.ToLowerInvariant();
                var reservedFixture = CloneDictionary(boundFixture);
                var reservedResponse = ReadDictionary(ReadDictionary(reservedFixture, "writerReplay"), "response");
                var reservedFields = TryParseJsonObject(ReadString(reservedResponse, "content", ""));
                reservedFields["reply"] = storedReply + "\n{\"decisionBrief\":{\"decision\":\"Internal rationale.\"}}";
                reservedResponse["content"] = Json.Serialize(reservedFields);
                var reservedWriter = ReplayEvidenceWriter(campaign, "npc", "player", reservedFixture, writerContext, settings, packet, writerTranscript);
                Check("writer_replay_rejects_bound_embedded_metadata_before_publication",
                    ReadString(reservedWriter, "status", "") == "writer_response_rejected"
                    && ReadString(ReadDictionary(reservedWriter, "responseRequestBinding"), "status", "") == "verified"
                    && !reservedWriter.ContainsKey("publication") && !reservedWriter.ContainsKey("nextWriterWireCapture")
                    && SourceCount() == sourcesBeforeWriter && MemoryCount() == memoryBeforeBinding);
                var storedWriter = ReplayEvidenceWriter(campaign, "npc", "player", boundFixture, writerContext, settings, packet, writerTranscript);
                Check("writer_replay_verified_response_request_binding", ReadString(ReadDictionary(storedWriter, "responseRequestBinding"), "status", "") == "verified"
                    && ReadString(ReadDictionary(storedWriter, "responseRequestBinding"), "requestSha256", "") == writerWireHash
                    && MemoryCount() > memoryBeforeBinding);
                var storedRows = ReadDictionary(storedWriter, "stored");
                var receipt = ReadDictionary(ReadDictionary(storedWriter, "publication"), "acceptedUnit");
                Check("writer_replay_uses_real_accepted_memory_publication", ReadString(storedWriter, "status", "") == "captured_writer_stored_next_read_prepared"
                    && ReadBool(receipt, "ok", false) && ReadBool(receipt, "memoryUpdateComplete", false)
                    && ReadDictionaryList(storedRows, "conversation_turns").Count == 2
                    && ReadDictionaryList(storedRows, "memories").Any(m => ReadString(m, "summary", "").Contains("old mill")), receipt);
                Check("writer_replay_final_grounding_binds_accepted_prose", ReadBool(storedWriter, "groundingAccepted", false)
                    && ReadString(ReadDictionary(storedWriter, "grounding"), "status", "") == "checked"
                    && ReadString(ReadDictionary(storedWriter, "grounding"), "replyHash", "")
                        == ConversationExactTextHash(ReadString(storedWriter, "acceptedProse", "")));
                Check("writer_replay_current_speaker_alias_stores_canonical_source", ReadDictionaryList(storedRows, "memories").Any(m => {
                    var payload = TryParseJsonObject(ReadString(m, "payload_json", "{}"));
                    var source = ReadDictionary(payload, "sourceEvidence");
                    return ReadString(m, "summary", "") == storedReply && ReadString(payload, "sourceValidation", "") == "supported_span"
                        && ReadString(source, "speakerId", "") == "npc" && ReadString(source, "resolvedSpeakerAlias", "") == "current_npc";
                }));
                Check("writer_replay_next_read_retains_accepted_prose_without_remote_stages",
                    Json.Serialize(ReadDictionary(storedWriter, "nextWriterRequest")).Contains(storedReply)
                    && !storedWriter.ContainsKey("nextAnalystRequests")
                    && ReadString(ReadDictionary(storedWriter, "nextWriterWireCapture"), "requestJson", "").Contains(storedReply));
                Check("writer_replay_does_not_apply_actions_or_claim_certification",
                    ReadString(ReadDictionary(receipt, "effects"), "status", "") == "suppressed_by_route"
                    && !ReadBool(storedWriter, "certificateEligible", true));
                var replayedWriter = ReplayEvidenceWriter(campaign, "npc", "player", boundFixture, writerContext, settings, packet, writerTranscript);
                Check("writer_replay_publication_retry_deduplicates", SourceCount() == sourcesBeforeWriter + 2
                    && ReadBool(ReadDictionary(ReadDictionary(replayedWriter, "publication"), "acceptedUnit"), "idempotentReplay", false));

                var repairFixture = CloneDictionary(writerFixture); repairFixture["caseId"] = "bounded_repair_capture";
                var repairConfig = ReadDictionary(repairFixture, "writerReplay");
                var emptyResponse = TestDict("ok", true, "finishReason", "length", "content", "{}");
                repairConfig["response"] = emptyResponse;
                repairConfig["repairResponses"] = Array.Empty<object>();
                int beforeRepair = SourceCount();
                Dictionary<string, object> ReplayRepair() => ReplayEvidenceWriter(campaign, "npc", "player", repairFixture,
                    CloneDictionary(writerContext), settings, packet, writerTranscript);
                var repairPending = ReplayRepair();
                var repairWire = ReadDictionary(ReadDictionaryList(repairPending, "repairRequests").Single(), "providerWireCapture");
                Check("repair_capture_pending_exports_exact_wire_without_publication", ReadString(repairPending, "status", "") == "writer_repair_pending"
                    && SourceCount() == beforeRepair && !repairPending.ContainsKey("publication")
                    && ReadInt(repairWire, "providerCalls", -1) == 0 && ReadString(repairWire, "requestJson", "").Contains(answer));
                repairConfig["repairResponses"] = new[] { TestDict("requestSha256", new string('a', 64), "response", emptyResponse) };
                bool mismatch = false;
                try { ReplayRepair(); } catch (InvalidOperationException ex) { mismatch = ex.Message.Contains("exact production request"); }
                Check("repair_capture_hash_mismatch_cannot_publish", mismatch && SourceCount() == beforeRepair);
                var captures = new List<Dictionary<string, object>> { TestDict("requestSha256", repairWire["requestSha256"], "response", emptyResponse) };
                repairConfig["repairResponses"] = captures;
                var secondRepair = ReplayRepair();
                Check("repair_capture_second_stage_stays_pending", ReadString(secondRepair, "status", "") == "writer_repair_pending"
                    && ReadDictionaryList(secondRepair, "repairRequests").Count == 2 && SourceCount() == beforeRepair);
                var secondWire = ReadDictionary(ReadDictionaryList(secondRepair, "repairRequests")[1], "providerWireCapture");
                captures.Add(TestDict("requestSha256", secondWire["requestSha256"], "response", emptyResponse));
                var exhaustedRepair = ReplayRepair();
                Check("repair_capture_obeys_production_retry_bound", ReadString(exhaustedRepair, "status", "") == "writer_response_rejected"
                    && ReadDictionaryList(exhaustedRepair, "repairRequests").Count == 2 && SourceCount() == beforeRepair);
                captures.RemoveAt(1);
                captures[0]["response"] = TestDict("ok", true, "finishReason", "stop", "content", Json.Serialize(TestDict("reply", storedReply)));
                var repairedWriter = ReplayRepair();
                Check("repair_capture_corrected_reply_reaches_final_publication", ReadBool(repairedWriter, "groundingAccepted", false)
                    && ReadBool(repairedWriter, "memoryUpdateComplete", false)
                    && ReadInt(repairedWriter, "repairResponsesConsumed", 0) == 1
                    && ReadString(repairedWriter, "acceptedProse", "").Contains(storedReply)
                    && !ReadBool(repairedWriter, "certificateEligible", true), ReadString(repairedWriter, "status", ""));

                var invalidMetadataFixture = CloneDictionary(writerFixture);
                invalidMetadataFixture["caseId"] = "unsupported_continuity_evidence";
                var invalidMetadataResponse = TryParseJsonObject(ReadString(ReadDictionary(writerConfiguration, "response"), "content", ""));
                invalidMetadataResponse["continuityWrites"] = new[] { TestDict("kind", "question", "status", "active",
                    "subject", "Choose a place", "evidenceQuote", "A quotation absent from the actual reply.") };
                ReadDictionary(invalidMetadataFixture, "writerReplay")["response"] = TestDict("ok", true, "finishReason", "stop",
                    "content", Json.Serialize(invalidMetadataResponse));
                var invalidMetadataWriter = ReplayEvidenceWriter(campaign, "npc", "player", invalidMetadataFixture,
                    CloneDictionary(writerContext), settings, packet, writerTranscript);
                var invalidMetadataStored = ReadDictionary(invalidMetadataWriter, "stored");
                Check("writer_replay_unresolved_grounding_retains_prose_and_suppresses_memory",
                    !ReadBool(invalidMetadataWriter, "groundingAccepted", true)
                    && ReadString(ReadDictionary(invalidMetadataWriter, "grounding"), "status", "") == "unresolved"
                    && ReadString(invalidMetadataWriter, "capturedProse", "") == storedReply
                    && ReadString(invalidMetadataWriter, "acceptedProse", "").Contains(storedReply)
                    && !ReadBool(invalidMetadataWriter, "memoryUpdateComplete", true)
                    && ReadDictionaryList(invalidMetadataStored, "conversation_turns").Count == 2
                    && ReadDictionaryList(invalidMetadataStored, "memories").Count == 0,
                    ReadDictionary(invalidMetadataWriter, "grounding"));

                const string historicalRecall = "When I first addressed you here, what did I say would follow if your arts had caused my pity, and what did I actually judge had moved me?";
                const string prophecyRecall = "When we spoke of prophets, what limit did you give their foresight, and how did you answer my comparison with a candle?";
                const string quietProphecyRecall = "*I lower my voice beside the shutter.* " + prophecyRecall;
                const string explanationRecall = "When you said you were no fighter, what did you mean by the joking, and why did you bring up women?";
                const string exceptionRecall = "When we spoke of obedience, what exception did you give to obeying no one, and what did you say chains could not bind?";
                const string quietExplanationRecall = "*I speak quietly.* " + explanationRecall;
                const string quietExceptionRecall = "*I lower my voice.* " + exceptionRecall;
                var recallScopeCases = new[] {
                    (historicalRecall, true),
                    (prophecyRecall, true),
                    (quietProphecyRecall, true),
                    (quietExplanationRecall, true),
                    (quietExceptionRecall, true),
                    ("  *I speak quietly.*   " + explanationRecall.ToUpperInvariant() + " ", true),
                    ("*I hand you a blade.* " + explanationRecall, false),
                    (quietExplanationRecall + " *I draw my sword.*", false),
                    (quietExplanationRecall + " I have a wife now.", false),
                    (explanationRecall.Replace("bring up women", "bring up my sister"), false),
                    (explanationRecall.Replace("bring up women", "bring up women and the war I fought"), false),
                    (explanationRecall.Replace("you were no fighter", "you are no fighter"), false),
                    (explanationRecall.Replace("you were no fighter", "you could free us"), false),
                    ("Why did you bring up women?", false),
                    (exceptionRecall.Replace("did you give", "will you give"), false),
                    (exceptionRecall.Replace("obeying no one", "obeying me"), false),
                    (exceptionRecall.Replace("chains could not bind", "chains can not bind"), false),
                    (exceptionRecall.Replace("chains could not bind", "chains could not bind me"), false),
                    (quietExceptionRecall + " Chains bind you now.", false),
                    ("*I whisper.* When we spoke about the gate, what warning did you give?", true),
                    ("*I lower my voice and hand over the letter.* " + prophecyRecall, false),
                    ("*I lower my voice beside the shutter.* *I hand over the letter.* " + prophecyRecall, false),
                    ("*I lower my voice beside the shutter.* When you spoke about the toll *pushes the purse across*, what did you say?", false),
                    ("*I murmur.* When we spoke, what did you say *slides the key over*?", false),
                    ("When you spoke about the gate: open it, what did you say?", false),
                    ("*I speak quietly.* When we spoke, what did you say about my stolen ring?", false),
                    ("*I speak quietly.* When we spoke, what did you tell me about the toll?", true),
                    ("When we spoke about the gate and I promised to pay, what warning did you give?", false),
                    ("When we spoke about my marriage to Valerius, what warning did you give?", false),
                    ("*I lower my voice.* When we spoke about the gate, what warning did you give, and record my new name?", false),
                    ("When we first spoke, what did you promise about the gate?", true),
                    ("When I first addressed you here, what did I say would follow, given that my brother has now returned?", false),
                    ("When I first addressed you here, what did I say about the six men I hired?", false),
                    ("When I first addressed you here, what did I say would follow, and then grant it?", false),
                    ("When I first addressed you here, what did I say would follow and open the gate?", false),
                    ("When I first addressed you here, what did I say would follow, and will you now accept those terms?", false),
                    ("When I first addressed you here, what did I say would follow, and what do I judge moves me now?", false),
                    ("When I first addressed you here, what did I say would follow, and what did I think, and what did I mean?", false),
                    ("When I first addressed you here, what did I say would follow? Now accept my offer.", false),
                    ("I counted eight guards by the gate today. " + historicalRecall, false),
                    ("What would follow if your arts caused my pity?", false),
                };
                Check("historical_speech_recall_excludes_current_reports_and_requests",
                    recallScopeCases.All(row => IsPureConversationRecallRequest(row.Item1) == row.Item2));

                const string proposedReply = "I will check the south gate before dawn.";
                foreach (var row in new[] {
                    (Id: "historical_recall", Query: historicalRecall, Route: "event", Suppress: true),
                    (Id: "quiet_historical_recall", Query: quietProphecyRecall, Route: "dialogue", Suppress: true),
                    (Id: "historical_explanation_recall", Query: quietExplanationRecall, Route: "dialogue", Suppress: true),
                    (Id: "historical_exception_recall", Query: quietExceptionRecall, Route: "dialogue", Suppress: true),
                    (Id: "handover_with_recall", Query: "*I lower my voice and hand over the letter.* " + prophecyRecall,
                        Route: "dialogue", Suppress: false),
                    (Id: "terms_recall", Query: "What were your terms for the north gate?", Route: "dialogue", Suppress: true),
                    (Id: "mixed_historical_recall", Query: "I counted eight guards by the gate today. " + historicalRecall, Route: "event", Suppress: false),
                })
                {
                    string recallCampaign = "evidence_recall_gate_" + Guid.NewGuid().ToString("N");
                    try
                    {
                        var replay = CloneDictionary(writerFixture);
                        replay["caseId"] = row.Id;
                        replay["query"] = row.Query;
                        replay["route"] = row.Route;
                        ReadDictionary(replay, "writerReplay")["response"] = TestDict("ok", true, "finishReason", "stop",
                            "content", Json.Serialize(TestDict("reply", proposedReply, "continuityWrites", new[] {
                                TestDict("kind", "agenda", "status", "active", "subject", "Check the south gate",
                                    "meaning", proposedReply, "evidenceQuote", proposedReply, "expectedRevision", 0),
                            })));
                        var replyReplay = ReplayEvidenceWriter(recallCampaign, "npc", "player", replay, CloneDictionary(writerContext),
                            settings, TestDict("memoryPacket", ""), new());
                        var replayStored = ReadDictionary(replyReplay, "stored");
                        var authored = ReadDictionaryList(replayStored, "conversation_turns").OrderBy(r => ReadLong(r, "accepted_sequence", 0)).ToList();
                        Check("writer_replay_semantic_write_eligibility_" + row.Id,
                            ReadBool(replyReplay, "groundingAccepted", false)
                            && authored.Select(r => ReadString(r, "role", "")).SequenceEqual(new[] { "player", "npc" })
                            && authored.Select(r => ReadString(r, "text", "")).SequenceEqual(new[] { row.Query, proposedReply })
                            && (row.Suppress
                                ? new[] { "memories", "beliefs", "obligations", "comprehension", "continuity" }
                                    .All(table => ReadDictionaryList(replayStored, table).Count == 0)
                                    && ReadBool(replyReplay, "memoryUpdateComplete", false)
                                : ReadDictionaryList(replayStored, "continuity").Count == 1),
                            ReadString(replyReplay, "status", ""));
                    }
                    finally { ReignPostgreSqlStorage.DropCampaign(recallCampaign); }
                }

                // Real captured writers copy canonical IDs from a prior request.
                // Rebuilding the disposable campaign must not invalidate those
                // exact references merely because its physical name changed.
                string identityFirst = "evidence_identity_" + Guid.NewGuid().ToString("N");
                string identitySecond = "evidence_identity_" + Guid.NewGuid().ToString("N");
                try
                {
                    const string identityQuestion = "Which place did you choose?";
                    const string identityAnswer = "The mill.";
                    const string identityReply = "I will remember that you chose the mill.";
                    var identityFixture = TestDict("caseId", "stable_source_identity", "query", "Help me choose a place.", "route", "dialogue",
                        "writerReplay", TestDict("model", "captured-writer", "snapshotAsOfSequence", 0L,
                            "profile", TestDict("name", "Ira", "age", 28, "isNoble", true),
                            "nextContribution", identityAnswer,
                            "response", TestDict("ok", true, "finishReason", "stop", "content", Json.Serialize(TestDict(
                                "reply", identityQuestion, "continuityWrites", new[] { TestDict("kind", "question", "status", "active",
                                    "subject", "Choose a place", "peerId", "player", "evidenceQuote", identityQuestion, "expectedRevision", 0,
                                    "questionState", TestDict("question", TestDict("turnId", "current_npc", "speakerId", "npc", "quote", identityQuestion),
                                        "addresseeIds", new[] { "player" }, "answerState", "unanswered", "answers", Array.Empty<object>(), "evaluation", "unassessed")) }))),
                            "nextPlanResponse", TestDict("ok", true, "content", "{\"needs\":[],\"queries\":[]}"),
                            "nextAssemblyResponse", TestDict("ok", true, "content", "{\"selectedAliases\":[],\"findings\":[],\"unresolvedNeeds\":[]}")));
                    var firstIdentity = ReplayEvidenceWriter(identityFirst, "npc", "player", identityFixture,
                        CloneDictionary(writerContext), settings, TestDict("memoryPacket", ""), new());
                    var firstIdentityRows = ReadDictionary(firstIdentity, "stored");
                    string firstQuestionTurn = ReadString(ReadDictionaryList(firstIdentityRows, "conversation_turns")
                        .Single(r => ReadString(r, "role", "") == "npc"), "turn_id", "");
                    string firstQuestionId = ReadString(ReadDictionaryList(firstIdentityRows, "continuity").Single(), "record_id", "");
                    var continuationWrite = TestDict("id", firstQuestionId, "kind", "question", "status", "resolved", "subject", "Choose a place",
                        "peerId", "player", "evidenceQuote", identityReply, "expectedRevision", 1,
                        "questionState", TestDict("question", TestDict("turnId", firstQuestionTurn, "speakerId", "npc", "quote", identityQuestion),
                            "addresseeIds", new[] { "player" }, "answerState", "answered", "evaluation", "unassessed",
                            "answers", new[] { TestDict("turnId", "current_player", "speakerId", "player", "quote", identityAnswer) }));
                    var identityConfiguration = ReadDictionary(identityFixture, "writerReplay");
                    identityConfiguration["continuation"] = TestDict("nextContribution", "What did I choose?",
                        "response", TestDict("ok", true, "finishReason", "stop", "content", Json.Serialize(TestDict(
                            "reply", identityReply, "continuityWrites", new[] { continuationWrite }))),
                        "nextPlanResponse", TestDict("ok", true, "content", "{\"needs\":[],\"queries\":[]}"),
                        "nextAssemblyResponse", TestDict("ok", true, "content", "{\"selectedAliases\":[],\"findings\":[],\"unresolvedNeeds\":[]}"));
                    string identityInputBefore = CanonicalJson(identityFixture);
                    var secondIdentity = ReplayEvidenceWriter(identitySecond, "npc", "player", identityFixture,
                        CloneDictionary(writerContext), settings, TestDict("memoryPacket", ""), new());
                    var secondContinuation = ReadDictionary(secondIdentity, "continuation");
                    var secondIdentityRows = ReadDictionary(secondContinuation, "stored");
                    var resolvedQuestion = ReadDictionaryList(secondIdentityRows, "continuity")
                        .SingleOrDefault(r => ReadString(r, "record_id", "") == firstQuestionId);
                    Check("writer_replay_identity_survives_database_recreation", ReadString(firstIdentity, "sourceIdentityPolicy", "") == "fixture_bound_accepted_turns_v1"
                        && ReadDictionaryList(ReadDictionary(secondIdentity, "stored"), "conversation_turns")
                            .Any(r => ReadString(r, "turn_id", "") == firstQuestionTurn));
                    Check("writer_replay_captured_question_reference_resolves_in_fresh_database", resolvedQuestion != null
                        && ReadString(resolvedQuestion, "status", "") == "resolved" && ReadInt(resolvedQuestion, "revision", 0) == 2
                        && ReadBool(secondContinuation, "memoryUpdateComplete", false), ReadDictionary(secondContinuation, "publication"));
                    Check("writer_replay_identity_keeps_input_and_database_isolation", identityInputBefore == CanonicalJson(identityFixture)
                        && ReadString(ReadDictionary(ReadDictionary(firstIdentity, "publication"), "acceptedUnit"), "unitId", "")
                            == ReadString(ReadDictionary(ReadDictionary(secondIdentity, "publication"), "acceptedUnit"), "unitId", "")
                        && identityFirst != identitySecond);
                    var identityPayload = TestDict("timelineId", "branch_a", "conversationSessionId", "session", "sceneTurnId", "turn", "playerText", identityAnswer);
                    string fixedId = EvidenceReplayEventId(identityFixture, "npc", identityPayload, identityReply);
                    var changedIdentity = CloneDictionary(identityFixture); changedIdentity["sources"] = new[] { TestDict("text", "A different original.") };
                    Check("writer_replay_identity_binds_original_history_and_reply", fixedId != EvidenceReplayEventId(changedIdentity, "npc", identityPayload, identityReply)
                        && fixedId != EvidenceReplayEventId(identityFixture, "npc", identityPayload, "A different reply."));
                    var changedTurn = CloneDictionary(identityPayload); changedTurn["playerText"] = "The orchard.";
                    Check("writer_replay_identity_binds_contribution_and_observer", fixedId != EvidenceReplayEventId(identityFixture, "npc", changedTurn, identityReply)
                        && fixedId != EvidenceReplayEventId(identityFixture, "other", identityPayload, identityReply));
                }
                finally
                {
                    ReignPostgreSqlStorage.DropCampaign(identityFirst);
                    ReignPostgreSqlStorage.DropCampaign(identitySecond);
                }

                var chainFixture = CloneDictionary(writerFixture);
                chainFixture["caseId"] = "conditional_continuation";
                var chainConfiguration = ReadDictionary(chainFixture, "writerReplay");
                chainConfiguration["nextContribution"] = "If we visit the mill, who will inspect its roof?";
                const string conditionalReply = "If we visit the mill, I will inspect the roof. We have not gone there together yet.";
                chainConfiguration["continuation"] = TestDict("nextContribution", "Have we inspected the mill yet?",
                    "response", TestDict("ok", true, "finishReason", "stop", "content", Json.Serialize(TestDict(
                        "reply", conditionalReply, "memoryWrites", new[] { TestDict("text", conditionalReply,
                            "sourceEvidence", TestDict("turnId", "current_npc", "speakerId", "current_npc", "quote", conditionalReply)) }))),
                    "nextPlanResponse", TestDict("ok", true, "content", "{\"needs\":[],\"queries\":[]}"),
                    "nextAssemblyResponse", TestDict("ok", true, "content", "{\"selectedAliases\":[],\"findings\":[],\"unresolvedNeeds\":[]}"));
                int beforeChain = SourceCount();
                var pendingChainFixture = CloneDictionary(chainFixture);
                ReadDictionary(ReadDictionary(pendingChainFixture, "writerReplay"), "continuation").Remove("response");
                var pendingChain = ReplayEvidenceWriter(campaign, "npc", "player", pendingChainFixture, writerContext, settings, packet, writerTranscript);
                Check("writer_continuation_missing_capture_preserves_only_prior_accepted_turn",
                    SourceCount() == beforeChain + 2 && !EvidenceWriterReplayComplete(pendingChain)
                    && ReadString(ReadDictionary(pendingChain, "continuation"), "status", "") == "writer_pending"
                    && !ReadDictionary(pendingChain, "continuation").ContainsKey("publication"));
                var continuationResponse = ReadDictionary(ReadDictionary(chainConfiguration, "continuation"), "response");
                continuationResponse["requestSha256"] = ReadString(ReadDictionary(pendingChain, "providerWireCapture"), "requestSha256", "");
                var wrongContinuationChain = ReplayEvidenceWriter(campaign, "npc", "player", chainFixture, writerContext, settings, packet, writerTranscript);
                var wrongContinuation = ReadDictionary(wrongContinuationChain, "continuation");
                Check("writer_continuation_cannot_use_preceding_turn_response_hash", ReadString(wrongContinuation, "status", "") == "writer_response_request_mismatch"
                    && ReadBool(ReadDictionary(ReadDictionary(wrongContinuationChain, "publication"), "acceptedUnit"), "ok", false)
                    && !wrongContinuation.ContainsKey("publication") && SourceCount() == beforeChain + 2);
                continuationResponse["requestSha256"] = ReadString(ReadDictionary(pendingChain, "nextWriterWireCapture"), "requestSha256", "");
                string chainInputHash = CanonicalJson(chainFixture);
                var completedChain = ReplayEvidenceWriter(campaign, "npc", "player", chainFixture, writerContext, settings, packet, writerTranscript);
                var continuedWriter = ReadDictionary(completedChain, "continuation");
                var continuedStored = ReadDictionary(continuedWriter, "stored");
                Check("writer_continuation_uses_exact_preceding_prepared_request",
                    ReadString(ReadDictionary(completedChain, "nextWriterWireCapture"), "requestJson", "")
                        == ReadString(ReadDictionary(continuedWriter, "providerWireCapture"), "requestJson", "")
                    && ReadString(ReadDictionary(continuedWriter, "responseRequestBinding"), "status", "") == "verified"
                    && ReadString(continuedWriter, "requestOrigin", "") == "preceding_turn_next_request");
                Check("writer_continuation_publishes_distinct_turn_with_conditional_memory",
                    SourceCount() == beforeChain + 4 && EvidenceWriterReplayComplete(completedChain)
                    && ReadBool(completedChain, "allRequestedTurnsReplayed", false)
                    && ReadDictionaryList(continuedStored, "memories").Any(m => ReadString(m, "summary", "") == conditionalReply)
                    && ReadString(ReadDictionary(ReadDictionary(completedChain, "publication"), "acceptedUnit"), "unitId", "")
                        != ReadString(ReadDictionary(ReadDictionary(continuedWriter, "publication"), "acceptedUnit"), "unitId", ""));
                long FirstCutoff(Dictionary<string, object> value) => ReadLong(value, "nextAsOfAcceptedSequence", -1);
                Check("writer_continuation_newly_stored_source_reaches_later_writer",
                    FirstCutoff(continuedWriter) > FirstCutoff(completedChain)
                    && ReadString(ReadDictionary(continuedWriter, "nextWriterWireCapture"), "requestJson", "").Contains(conditionalReply)
                    && ReadInt(continuedWriter, "providerCalls", -1) == 0
                    && !ReadBool(continuedWriter, "certificateEligible", true)
                    && CanonicalJson(chainFixture) == chainInputHash);
                var retriedChain = ReplayEvidenceWriter(campaign, "npc", "player", chainFixture, writerContext, settings, packet, writerTranscript);
                Check("writer_continuation_retry_deduplicates", SourceCount() == beforeChain + 4);
                var retryContinuation = ReadDictionary(retriedChain, "continuation");
                Check("writer_continuation_retry_completes", EvidenceWriterReplayComplete(retriedChain),
                    TestDict("status", ReadString(retriedChain, "status", ""), "continuationStatus", ReadString(retryContinuation, "status", ""),
                        "responseBinding", ReadString(ReadDictionary(retryContinuation, "responseRequestBinding"), "status", "")));
                Check("writer_continuation_retry_preserves_cutoff", FirstCutoff(retriedChain) == FirstCutoff(completedChain));
                bool futureAbsent = !ReadString(ReadDictionary(retriedChain, "nextWriterWireCapture"), "requestJson", "").Contains(conditionalReply);
                Check("writer_continuation_retry_excludes_later_source", futureAbsent);
                if (!futureAbsent)
                {
                    Directory.CreateDirectory(Path.Combine(TestsDir, "memory-accuracy"));
                    WriteJsonObject(Path.Combine(TestsDir, "memory-accuracy", "continuation-retry-source-failure.json"),
                        TestDict("schema", "reign-continuation-retry-diagnostic-v1", "providerCalls", 0,
                            "scope", "synthetic_isolated_fixture", "before", completedChain, "retry", retriedChain));
                }
                using (var connection = OpenCampaignConnection(campaign))
                {
                    ExecuteSql(connection, @"INSERT INTO conversation_continuity
(record_id,timeline_id,owner_id,peer_id,kind,status,importance,revision,source_event_id,source_turn_id,updated_ts,payload_json)
VALUES('private_replacement','branch_a','npc','player','agenda','open',1,2,'unheard','unheard',10,'{""worldDay"":2}');
INSERT INTO conversation_continuity_versions(version_id,record_id,source_event_id,revision,payload_json,created_ts)
VALUES('private_replacement_1','private_replacement','answer',1,'{""sourceTurnId"":""answer""}',8);");
                    var access = BuildKnowledgeAccessContext("npc", "player", "", writerContext);
                    var replacementSource = LoadEmbeddingSourceRow(connection, "conversation_turn", "unheard");
                    long sequence = ReadLong(replacementSource, "accepted_sequence", -1);
                    access.AsOfAcceptedSequence = sequence;
                    bool superseded = ReviewedRecoverySourceSuperseded(connection, access, "answer");
                    access.AsOfAcceptedSequence = sequence - 1;
                    bool excludedBeforeSequence = !ReviewedRecoverySourceSuperseded(connection, access, "answer");
                    access.AsOfAcceptedSequence = sequence;
                    access.AsOfTimestamp = 9;
                    bool excludedBeforeTimestamp = !ReviewedRecoverySourceSuperseded(connection, access, "answer");
                    Check("reviewed_recovery_rejects_supersession_without_reading_private_revision_text",
                        sequence > 0 && superseded && excludedBeforeSequence && excludedBeforeTimestamp,
                        TestDict("replacementSequence", sequence, "superseded", superseded,
                            "excludedBeforeSequence", excludedBeforeSequence, "excludedBeforeTimestamp", excludedBeforeTimestamp));
                }

            }
            catch (Exception ex) { Check("fixture_completed", false, ex.ToString()); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
