using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunMemoryEvidenceReplaySelfTests()
        {
            var results = new List<Dictionary<string, object>>();
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
            Check("writer_wire_blocks_hard_limit_without_truncation_or_dispatch", ReadString(oversizedWire, "status", "") == "blocked"
                && !ReadBool(ReadDictionary(oversizedWire, "providerPreflight"), "fits", true)
                && ReadString(oversizedWire, "requestJson", "").Contains(new string('x', 250000))
                && ReadInt(oversizedWire, "providerCalls", -1) == 0);
            var replayCase = TestDict("caseId", "bounded", "observerId", "npc", "peerId", "player", "query", "The orchard?",
                "worldDay", 3d, "asOfSequence", 2L, "sources", new[] { TestDict("sourceId", "original", "speakerId", "npc",
                    "role", "npc", "text", "Which place would you choose?", "audience", new[] { "npc", "player" }, "worldDay", 2d, "sequence", 1L) });
            Check("replay_accepts_bounded_originals", MemoryEvidenceReplayInputError(replayCase) == "");
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
            foreach (string forbidden in new[] { "campaignId", "model", "generationSettings", "profile", "sources", "worldDay", "requiredEvidence" })
            {
                var injectedContinuation = CloneDictionary(writerCase);
                ReadDictionary(injectedContinuation, "writerReplay")["continuation"] = TestDict("nextContribution", "Still undecided?", forbidden, "forbidden");
                Check("writer_continuation_rejects_authority_or_labels_" + forbidden,
                    MemoryEvidenceReplayInputError(injectedContinuation) == "invalid_writer_continuation");
            }
            var missingContinuationInput = CloneDictionary(writerCase);
            ReadDictionary(missingContinuationInput, "writerReplay")["continuation"] = TestDict("response", TestDict("ok", true, "content", "{}"));
            Check("writer_continuation_requires_own_next_contribution", MemoryEvidenceReplayInputError(missingContinuationInput) == "invalid_writer_replay");
            var configuredWriter = CloneDictionary(writerCase);
            ReadDictionary(configuredWriter, "writerReplay")["generationSettings"] = TestDict("reasoningMode", "selective", "reasoningEffort", "medium", "temperature", .4d);
            Check("wire_generation_settings_accept_explicit_fixture_values", MemoryEvidenceReplayInputError(configuredWriter) == "");
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
            var secondCase = CloneDictionary(capacityWriter); secondCase["caseId"] = "bounded_second";
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
                    Check("blocked_next_writer_preserves_only_prior_publication", ReadString(boundaryWriter, "status", "") == "next_writer_preflight_blocked"
                        && boundaryWriter.ContainsKey("publication") && !boundaryWriter.ContainsKey("continuation")
                        && ReadString(boundaryWriter, "acceptedProse", "") == ReadString(capturedFirstWriter, "acceptedProse", "different"));
                    Check("blocked_next_writer_cannot_count_as_complete", !EvidenceWriterReplayComplete(boundaryWriter)
                        && !ReadBool(boundaryWriter, "allRequestedTurnsReplayed", true) && ReadInt(boundaryWriter, "providerCalls", -1) == 0);
                }
                var chronologicalCase = CloneDictionary(capacityWriter);
                chronologicalCase["caseId"] = "archived_session_chronology";
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
                recentRecallCase["caseId"] = "recent_only_continuation_guard";
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
                    callbackCase["caseId"] = "recent_only_continuation_guard_" + callback;
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
                    && ReadInt(WireBody(pendingWire), "max_tokens", 0) == 8000
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
                    && ReadInt(verifiedPreflight, "inputAllowance", 0) == 1048576 - 8000 - ConversationCapacitySafetyReserve
                    && ReadString(verifiedPreflight, "routeKey", "").Contains(":captured-writer:")
                    && ReferenceEquals(savedSettingsScope, CodexPerformanceSettings.Value));
                ReadDictionary(writerConfiguration, "contextCapacity")["validUntilUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o");
                var expiredWriter = ReplayEvidenceWriter(campaign, "npc", "player", writerFixture, writerContext, settings, packet, writerTranscript);
                Check("writer_replay_expired_capacity_cannot_expand_allowance",
                    !ReadBool(ReadDictionary(ReadDictionary(ReadDictionary(expiredWriter, "request"), "promptEnvelope"), "continuityPreflight"), "capacityVerified", true));
                writerConfiguration.Remove("contextCapacity");
                var oversizedWriter = CloneDictionary(writerFixture); oversizedWriter["query"] = new string('x', 210000);
                var capacityFailure = ReplayEvidenceWriter(campaign, "npc", "player", oversizedWriter, CloneDictionary(writerContext), settings, packet, writerTranscript);
                Check("writer_replay_preparation_failure_is_retained_without_publication",
                    ReadString(capacityFailure, "status", "") == "writer_preparation_failed"
                    && SourceCount() == sourcesBeforeWriter && ReferenceEquals(savedSettingsScope, CodexPerformanceSettings.Value));
                writerConfiguration["response"] = TestDict("ok", true, "content", "{}");
                var rejectedWriter = ReplayEvidenceWriter(campaign, "npc", "player", writerFixture, writerContext, settings, packet, writerTranscript);
                Check("writer_replay_empty_output_does_not_publish", ReadString(rejectedWriter, "status", "") == "writer_response_rejected"
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
                        && ReadDouble(groupBody, "temperature", -1) == .7d && ReadInt(groupBody, "max_tokens", 0) == 8000
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
                var storedWriter = ReplayEvidenceWriter(campaign, "npc", "player", writerFixture, writerContext, settings, packet, writerTranscript);
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
                var replayedWriter = ReplayEvidenceWriter(campaign, "npc", "player", writerFixture, writerContext, settings, packet, writerTranscript);
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
                string chainInputHash = CanonicalJson(chainFixture);
                var pendingChainFixture = CloneDictionary(chainFixture);
                ReadDictionary(ReadDictionary(pendingChainFixture, "writerReplay"), "continuation").Remove("response");
                var pendingChain = ReplayEvidenceWriter(campaign, "npc", "player", pendingChainFixture, writerContext, settings, packet, writerTranscript);
                Check("writer_continuation_missing_capture_preserves_only_prior_accepted_turn",
                    SourceCount() == beforeChain + 2 && !EvidenceWriterReplayComplete(pendingChain)
                    && ReadString(ReadDictionary(pendingChain, "continuation"), "status", "") == "writer_pending"
                    && !ReadDictionary(pendingChain, "continuation").ContainsKey("publication"));
                var completedChain = ReplayEvidenceWriter(campaign, "npc", "player", chainFixture, writerContext, settings, packet, writerTranscript);
                var continuedWriter = ReadDictionary(completedChain, "continuation");
                var continuedStored = ReadDictionary(continuedWriter, "stored");
                Check("writer_continuation_uses_exact_preceding_prepared_request",
                    ReadString(ReadDictionary(completedChain, "nextWriterWireCapture"), "requestJson", "")
                        == ReadString(ReadDictionary(continuedWriter, "providerWireCapture"), "requestJson", "")
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
                Check("writer_continuation_retry_completes", EvidenceWriterReplayComplete(retriedChain));
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

            }
            catch (Exception ex) { Check("fixture_completed", false, ex.ToString()); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
