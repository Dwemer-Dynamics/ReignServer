using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        internal static List<Dictionary<string, object>> RunMemoryPrecisionSelfTests()
        {
            if (string.IsNullOrWhiteSpace(Program.CampaignsRootOverride.Value))
                throw new InvalidOperationException("Memory precision contracts require the isolated Verification Lab.");
            var results = new List<Dictionary<string, object>>();
            Action<string, bool, object> add = (id, pass, evidence) => results.Add(TestDict("caseId", "memory_precision_" + id,
                "passed", pass, "summary", id, "data", evidence));
            var collisionLines = Enumerable.Range(0, 14).Select(i => TestDict("id", "accepted_" + i,
                "turnId", "exchange_" + (i / 3), "exchangeId", "exchange_" + (i / 3), "role", i % 3 == 0 ? "player" : "npc",
                "speakerId", i % 3 == 0 ? "player" : "npc_" + (i % 3), "text", "The same words can recur.")).ToList();
            var collisionContext = TestDict(); SetMemoryTranscriptContext(collisionContext, collisionLines);
            add("exchange_ids_do_not_overwrite_distinct_authors_or_turns", ReadDictionary(collisionContext, "memoryPromptSourceHashes").Count == 14
                && DialogueService.CanonicalizePromptTranscript(collisionLines, 30).Count == 14, null);
            var durable = TestDict("turn_id", "durable_npc_ira", "exchange_id", "wager", "session_id", "scene", "role", "npc", "speaker_id", "ira",
                "text", "If you put me down, I will announce my defeat.");
            var eventLine = TestDict("id", "event_file_row", "turnId", "wager", "role", "npc", "speakerHeroStringId", "ira", "text", ReadString(durable, "text", ""));
            add("event_adapter_binds_full_source_without_guessing_suffix", MatchPromptSource(eventLine, new[] { durable }) == durable, null);
            var clipped = CloneDictionary(eventLine); clipped["text"] = "If you put me down";
            var wrongSpeaker = CloneDictionary(eventLine); wrongSpeaker["speakerHeroStringId"] = "player";
            var duplicateSource = CloneDictionary(durable); duplicateSource["turn_id"] = "another_turn";
            var staleSource = CloneDictionary(eventLine); staleSource["sourceTurnId"] = "stale_durable_id";
            add("ambiguous_clipped_or_wrong_author_never_suppresses_source", MatchPromptSource(clipped, new[] { durable }) == null
                && MatchPromptSource(wrongSpeaker, new[] { durable }) == null
                && MatchPromptSource(eventLine, new[] { durable, duplicateSource }) == null
                && MatchPromptSource(staleSource, new[] { durable }) == null, null);
            var correctedLines = new List<Dictionary<string, object>> {
                TestDict("id", "offer", "role", "npc", "speakerId", "ira", "text", "I offered to announce my defeat to the whole column."),
                TestDict("id", "misattribution", "role", "npc", "speakerId", "ira", "text", "You offered to announce my defeat to the whole column."),
                TestDict("id", "correction", "role", "player", "speakerId", "player", "text", "No, you offered that. I asked you to serve me food.") };
            add("similarity_never_erases_attributed_error_or_correction", CompactRoleplayTranscriptForPrompt(correctedLines).Count == 3, null);
            var repeatedUnbound = new List<Dictionary<string, object>> {
                TestDict("turnId", "same_exchange", "sequence", 1, "role", "player", "text", "Yes."),
                TestDict("turnId", "same_exchange", "sequence", 2, "role", "player", "text", "Yes.") };
            add("unbound_exchange_id_is_not_individual_identity", DialogueService.CanonicalizePromptTranscript(repeatedUnbound, 30).Count == 2, null);
            var privateLines = new List<Dictionary<string, object>> { TestDict("text", "OTHER_OBSERVER_SECRET") };
            add("projection_failure_never_exposes_raw_history", !TryProjectBoundTranscript(privateLines,
                () => throw new InvalidOperationException("injected projection failure")) && privateLines.Count == 0, null);
            var suppliedHalf = new Dictionary<string, string> { ["a"] = "hash_a" };
            var chain = new MemoryEvidence { Kind = "active_conversation", SourceId = "chain", Text = "Speaker A: question.\nSpeaker B: answer.",
                SourceTurns = new() { TestDict("id", "a", "hash", "hash_a", "text", "Speaker A: question."), TestDict("id", "b", "hash", "hash_b", "text", "Speaker B: answer.") } };
            var remainingChain = RemoveSuppliedMemoryTurns(chain, suppliedHalf);
            add("partially_supplied_continuation_keeps_only_missing_original", remainingChain != null && remainingChain.SourceTurns.Count == 1
                && remainingChain.Text == "Speaker B: answer." && chain.SourceTurns.Count == 2, null);
            var incompleteDiagnostics = MemoryIncompleteDiagnostics(new List<Dictionary<string, object>> {
                TestDict("status", "incomplete", "result_json", Json.Serialize(TestDict("retryable", false,
                    "summary", "PRIVATE_SOURCE_SENTINEL", "compactionDiagnostic", TestDict("reason", "invalid_source_selection")))),
                TestDict("status", "incomplete", "result_json", Json.Serialize(TestDict("retryable", false,
                    "summaries", new[] { TestDict("compactionDiagnostic", TestDict("reason", "no_record_fits_summary_budget")) }))),
                TestDict("status", "completed", "result_json", "{}") }, 3);
            add("incomplete_job_diagnostics_are_bounded_and_source_free",
                ReadInt(incompleteDiagnostics, "inspectedIncompleteJobs", 0) == 2
                && !ReadBool(incompleteDiagnostics, "coverageComplete", true)
                && ReadInt(incompleteDiagnostics, "nonRetryableInspected", 0) == 2
                && ReadInt(ReadDictionary(incompleteDiagnostics, "reasons"), "invalid_source_selection", 0) == 1
                && ReadInt(ReadDictionary(incompleteDiagnostics, "reasons"), "no_record_fits_summary_budget", 0) == 1
                && !Json.Serialize(incompleteDiagnostics).Contains("PRIVATE_SOURCE_SENTINEL"), null);
            string campaign = "memory_precision_test_" + Guid.NewGuid().ToString("N");
            var defaultPolicy = SettingsService.DefaultSettings();
            add("new_memory_is_default", !ReadBool(defaultPolicy, "legacyMemoryMode", true)
                && !defaultPolicy.ContainsKey("memoryPrecisionMode") && !defaultPolicy.ContainsKey("memoryReferenceGraphMode")
                && ResolveMemoryMode(defaultPolicy) == "precision" && ResolveConversationReferenceMode(defaultPolicy) == "enabled"
                && ReadInt(defaultPolicy, "conversationInputTokenLimit", 0) == 120000
                && ReadInt(defaultPolicy, "memoryReferenceGraphMaxNodes", 0) == 32
                && ReadInt(defaultPolicy, "memoryReferenceGraphHops", 0) == 32, null);
            foreach (string oldMode in new[] { "shadow", "legacy", "precision" })
            {
                var migrated = TestDict("memoryPrecisionMode", oldMode, "memoryReferenceGraphMode", "off");
                add("old_" + oldMode + "_flags_cannot_disable_new_default", ResolveMemoryMode(migrated) == "precision"
                    && ResolveConversationReferenceMode(migrated) == "enabled", null);
                migrated["legacyMemoryMode"] = true;
                add("legacy_toggle_selects_old_path_from_" + oldMode, ResolveMemoryMode(migrated) == "legacy"
                    && ResolveConversationReferenceMode(migrated) == "off", null);
                migrated["legacyMemoryMode"] = false;
                add("legacy_toggle_can_return_to_new_from_" + oldMode, ResolveMemoryMode(migrated) == "precision"
                    && ResolveConversationReferenceMode(migrated) == "enabled", null);
            }
            var malformedPolicy = TestDict("legacyMemoryMode", "invalid", "temperature", .7d, "dialogueModel", "fixture/model", "apiKey", "fixture-key");
            SettingsService.NormalizeMemorySettings(malformedPolicy);
            add("missing_or_malformed_toggle_defaults_new_without_changing_provider", malformedPolicy["legacyMemoryMode"] is false
                && ResolveMemoryMode(null) == "precision" && ReadDouble(malformedPolicy, "temperature", 0) == .7d
                && ReadString(malformedPolicy, "dialogueModel", "") == "fixture/model"
                && ReadString(malformedPolicy, "apiKey", "") == "fixture-key", null);
            var observer = new DialogueService.KnowledgeAccessContext { NpcId = "npc_b", PlayerId = "player", TimelineId = "timeline_a", WorldDay = 30 };
            var settings = TestDict("useMemoryLlmForConsolidation", false, "enableSemanticMemory", false,
                "memoryPrecisionMode", "precision", "enableSemanticMemory", false);
            var taggedSources = new List<Dictionary<string, object>> {
                TestDict("ts", 1L, "summary", "Player said or visibly did: We discussed the emperor's passing.",
                    "tags_json", "[\"dialogue\",\"grief\",\"arenicos\"]"),
                TestDict("ts", 2L, "summary", "The speaker explained her grief and trust.",
                    "tags_json", "[\"personal_disclosure\",\"grief\",\"trust\"]") };
            string taggedOriginal = CanonicalJson(taggedSources);
            var taggedDigest = DialogueService.BuildMiddleTermSummary(settings, "npc_b", "conversation:tag_fixture", taggedSources);
            var digestTags = ReadStringList(taggedDigest, "tags");
            add("summary_topics_use_content_tags_not_provenance_wrapper", digestTags.Contains("grief")
                && digestTags.Contains("arenicos") && digestTags.Contains("trust")
                && !digestTags.Any(MemoryTopicMetadata.Contains) && digestTags.Count <= 5
                && CanonicalJson(taggedSources) == taggedOriginal, digestTags);
            var polluted = BuildMemorySummaryTopics(new[] { TestDict("summary",
                "[DIALOGUE-DERIVED REPORTED SPEECH; NOT INDEPENDENT WORLD-FACT EVIDENCE] \nPlayer said or visibly did: Orchards supply apples. Orchards supply apples.",
                "tags_json", "[\"reported\",\"speech\",\"independent\",\"middle_term\"]") }, 5);
            add("legacy_topic_wrapper_is_not_reintroduced", polluted.Contains("orchards")
                && !polluted.Any(MemoryTopicMetadata.Contains) && !polluted.Any(MemoryTopicProvenanceWords.Contains), polluted);
            var meaningfulNoticeWords = BuildMemorySummaryTopics(new[] { TestDict("summary",
                "[DIALOGUE-DERIVED REPORTED SPEECH; NOT INDEPENDENT WORLD-FACT EVIDENCE] \nPlayer said or visibly did: We plan an independent nation. The speech presents evidence of support.",
                "tags_json", "[\"independent\",\"speech\",\"evidence\",\"dialogue\"]") }, 3);
            add("provenance_words_remain_topics_when_supported_by_actual_content",
                meaningfulNoticeWords.Count == 3 && meaningfulNoticeWords.Contains("independent")
                && meaningfulNoticeWords.Contains("speech") && meaningfulNoticeWords.Contains("evidence"), meaningfulNoticeWords);
            var lateTags = BuildMemorySummaryTopics(new[] {
                TestDict("summary", new string('x', 9000), "tags_json", "[\"dialogue\"]"),
                TestDict("summary", "The caravan price remains conditional on arrival.", "tags_json", "[\"caravan_terms\"]") }, 1);
            add("summary_topics_include_final_record_without_extra_calls", lateTags.SequenceEqual(new[] { "caravan_terms" }), lateTags);
            var qualified = DialogueService.TryExtractMinimeTopics(settings, "Not intimate.", 5);
            add("topic_hints_do_not_drop_explicit_negation", qualified.Contains("not_intimate")
                && !qualified.Contains("intimate") && !qualified.Contains("intimacy"), qualified);
            var lateQuestionTopics = DialogueService.TryExtractMinimeTopics(settings,
                string.Concat(Enumerable.Repeat("He sips slowly beside the fire. ", 150))
                    + "What does Empress Narea plan for the tournament?", 3);
            add("topic_query_retains_late_entities_and_subject_after_long_narration",
                lateQuestionTopics.Contains("narea") && lateQuestionTopics.Contains("tournament"), lateQuestionTopics);
            add("empty_sources_do_not_invent_topics", BuildMemorySummaryTopics(Array.Empty<Dictionary<string, object>>(), 5).Count == 0, null);
            var stagedOriginals = new List<Dictionary<string, object>> {
                TestDict("turn_id", "old_player_source", "speaker_id", "player", "role", "player", "world_day", 20d,
                    "text", "The caravan promise requires arrival."),
                TestDict("turn_id", "old_npc_source", "speaker_id", "npc_b", "role", "npc", "world_day", 20d,
                    "text", "*I imagine walking beside her* I agree. The caravan promise requires arrival; nothing is paid yet.") };
            string stagedOriginalHash = MemorySourceHash(stagedOriginals);
            var stagedLegacy = PrepareStagedSceneMemory(TestDict("npc_id", "npc_b"), stagedOriginals, "timeline_a");
            string stagedText = ReadString(stagedLegacy, "summary", "");
            add("legacy_staging_links_tags_and_public_source_views", stagedText.Contains("source=old_player_source; speaker=player; role=player")
                && stagedText.Contains("source=old_npc_source; speaker=npc_b; role=npc")
                && stagedText.Contains("nothing is paid yet") && !stagedText.Contains("imagine walking")
                && ReadStringList(stagedLegacy, "tags").Contains("caravan")
                && ReadDictionaryList(stagedLegacy, "sourceReferences").All(r => ReadString(r, "sourceHash", "").Length == 64
                    && ReadString(r, "viewHash", "").Length == 64)
                && MemorySourceHash(stagedOriginals) == stagedOriginalHash
                && CanonicalJson(stagedLegacy) == CanonicalJson(PrepareStagedSceneMemory(TestDict("npc_id", "npc_b"), stagedOriginals, "timeline_a")), stagedLegacy);
            foreach (double sourceDay in new[] { 20d, 20.25d, 91089.14008449652d })
            {
                var numericSources = stagedOriginals.Select(t => new Dictionary<string, object>(t) { ["world_day"] = sourceDay }).ToList();
                var numericPrepared = PrepareStagedSceneMemory(TestDict("npc_id", "npc_b"), numericSources, "timeline_a");
                add("legacy_staging_manifest_numeric_roundtrip_" + sourceDay.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    CanonicalJson(numericPrepared) == CanonicalJson(TryParseJsonObject(Json.Serialize(numericPrepared))), null);
            }
            var oversizedSource = new List<Dictionary<string, object>> {
                TestDict("turn_id", "large_old_source", "speaker_id", "npc_b", "role", "npc", "text", new string('z', 390000)) };
            var oversizedStaged = PrepareStagedSceneMemory(TestDict("npc_id", "npc_b"), oversizedSource, "timeline_a");
            string oversizedView = CompactRecentSummaryView(ReadString(oversizedStaged, "summary", ""), oversizedStaged,
                new[] { "caravan" }, 1800, out var oversizedAudit);
            add("legacy_staging_large_original_gets_bounded_view", oversizedView.Length <= 1800
                && !oversizedView.Contains(new string('z', 20)) && ReadString(oversizedStaged, "summary", "").Contains(new string('z', 390000)), oversizedAudit);
            var context = TestDict("timelineId", "timeline_a", "worldDay", 30d, "playerHeroStringId", "player",
                "sceneTurnId", "current_beat", "conversationSessionId", "current_session");
            var raw = new List<string> { "Player proposed 900 denars, payable immediately.",
                new string('x', 2100), "NPC corrected the amount to 300 denars, only after the caravan arrives.",
                "Player accepted 300 after arrival. No payment has happened." };
            var fallback = ExtractiveMemoryFallback(raw, new List<string>());
            add("fallback_preserves_late_correction_and_outcome", ReadString(fallback, "summary", "").Contains(raw.Last())
                && ReadString(fallback, "summary", "").Contains(raw[2]) && ReadString(fallback, "compactionStatus", "") == "incomplete", fallback);
            var selectedWhole = SelectCompleteMemoryRecords(raw, 200);
            add("complete_records_never_prefix_clipped", selectedWhole.Count == 3 && selectedWhole.All(raw.Contains), selectedWhole);
            add("summary_id_is_not_coverage", !MemorySummaryCoversSources("memory_id_1", new[] { TestDict("summary", raw.Last()) }), null);
            add("complete_extract_covers_sources", MemorySummaryCoversSources(string.Join("\n", raw), raw.Select(t => TestDict("summary", t))), null);
            var sourceRows = raw.Select(t => TestDict("summary", t)).ToList();
            var inventedSummary = AcceptMemorySummaryCandidate("They agreed to 900 denars and paid immediately.", sourceRows, raw, new List<string>());
            add("unsupported_summary_never_published_as_fact", ReadString(inventedSummary, "summary", "").Contains(raw.Last())
                && !ReadString(inventedSummary, "summary", "").Contains("paid immediately.")
                && ReadString(inventedSummary, "compactionStatus", "") == "incomplete"
                && !ReadBool(inventedSummary, "retryable", true), inventedSummary);
            var appended = AcceptMemorySummaryCandidate(string.Join("\n", raw) + "\nThe payment was completed.", sourceRows, raw, new List<string>());
            add("full_coverage_does_not_validate_invented_appendix", ReadString(appended, "candidatePublication", "") == "withheld_pending_semantic_source_validation"
                && !ReadString(appended, "summary", "").Contains("payment was completed"), null);
            var bound = AcceptMemorySummaryCandidate(string.Join("\n---\n", raw), sourceRows, raw, new List<string>());
            add("only_supported_complete_extract_can_complete_locally", ReadString(bound, "compactionStatus", "") == "complete"
                && ReadString(bound, "method", "") == "source_bound_extract", null);
            var missingAnswer = AcceptMemorySummaryCandidate(string.Join("\n", raw.Take(2)), sourceRows, raw, new List<string>());
            add("missing_answer_and_correction_coverage_reported", ReadStringList(missingAnswer, "uncoveredSourceHashes").Count == 2
                && ReadString(missingAnswer, "summary", "").Contains(raw.Last()), null);
            var compactSelection = AcceptMemorySummarySelection(TestDict("sourceIndices", new[] { 4, 1, 3 },
                "summary", "They paid 900 immediately."), sourceRows, raw, new List<string>(), 1800);
            string selectionText = ReadString(compactSelection, "summary", "");
            add("source_selection_compacts_without_publishing_model_prose", compactSelection != null
                && selectionText.Contains(raw[2]) && selectionText.Contains(raw[3]) && !selectionText.Contains("They paid 900")
                && selectionText.IndexOf(raw[0], StringComparison.Ordinal) < selectionText.IndexOf(raw[2], StringComparison.Ordinal)
                && selectionText.Length <= 1800 && !ReadBool(compactSelection, "coverageComplete", true)
                && ReadString(compactSelection, "sourceCoverage", "") == "selected_records", compactSelection);
            add("source_selection_rejects_invented_duplicate_fractional_and_oversized_records",
                AcceptMemorySummarySelection(TestDict("sourceIndices", new[] { 5 }), sourceRows, raw, new(), 1800) == null
                && AcceptMemorySummarySelection(TestDict("sourceIndices", new[] { 1, 1 }), sourceRows, raw, new(), 1800) == null
                && AcceptMemorySummarySelection(TestDict("sourceIndices", new[] { 1.5d }), sourceRows, raw, new(), 1800) == null
                && AcceptMemorySummarySelection(TestDict("sourceIndices", new[] { 2 }), sourceRows, raw, new(), 1800) == null, null);
            string archive = string.Join("\n---\n", raw);
            string compactView = CompactRecentSummaryView(archive, fallback, new[] { "caravan" }, 1800, out var compactViewAudit);
            add("incomplete_archive_has_bounded_whole_record_view", compactView.Length <= 1800
                && compactView.Contains(raw[2]) && compactView.Contains(raw[3]) && !compactView.Contains(new string('x', 80))
                && compactView.StartsWith("COMPACT SOURCE VIEW", StringComparison.Ordinal)
                && RecentSummaryPromptPolicy.Contains("coverage may be partial") && ReadString(compactViewAudit, "sourceCoverage", "") == "partial"
                && ReadString(fallback, "summary", "") == archive, compactViewAudit);
            string indexOnly = CompactRecentSummaryView(new string('z', 29510), TestDict("compactionStatus", "incomplete"),
                new[] { "caravan" }, 1800, out var indexAudit);
            add("oversized_single_record_retained_without_prefix_clipping", indexOnly.Length < 600
                && !indexOnly.Contains(new string('z', 20)) && indexOnly.Contains("original source remains stored")
                && ReadString(indexAudit, "view", "") == "source_index_only", indexAudit);
            string correctCompact = "NPC asked the price. Player answered 300 after arrival. NPC acknowledged it. No payment occurred.";
            add("complete_compact_summary_keeps_every_character", CompactRecentSummaryView(correctCompact,
                TestDict("compactionStatus", "complete"), Array.Empty<string>(), 1800, out _) == correctCompact, null);
            string singular1 = StableMemoryFactIdentity(TestDict("cardinality", "one", "agreementId", "service"), "npc_b", "price", "900", "price is 900");
            string singular2 = StableMemoryFactIdentity(TestDict("cardinality", "one", "agreementId", "service"), "npc_b", "price", "300", "price is 300");
            add("singular_term_revision_keeps_identity", singular1 == singular2, null);
            add("independent_agreements_stay_distinct", singular1 != StableMemoryFactIdentity(TestDict("cardinality", "one", "agreementId", "horse"), "npc_b", "price", "300", "price is 300"), null);
            add("multivalued_facts_do_not_overwrite", StableMemoryFactIdentity(TestDict(), "npc_b", "owns", "horse_a", "owns horse a")
                != StableMemoryFactIdentity(TestDict(), "npc_b", "owns", "horse_b", "owns horse b"), null);

            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
            {
                DialogueSelfTestStore.InsertConversationSessions14(connection, TestDict("campaign", campaign));
                string[] texts = { "Would you take 900 denars?", "No. 300 denars only after the caravan arrives.", "I accept 300 denars after arrival. Nothing has been paid." };
                for (int i = 0; i < texts.Length; i++)
                {
                    DialogueSelfTestStore.InsertConversationTurns(connection, TestDict("id", "source_" + i, "event", "event_" + i, "order", i + 1, "role", i == 1 ? "npc" : "player", "speaker", i == 1 ? "npc_b" : "player", "text", texts[i], "ts", i + 1));
                    DialogueSelfTestStore.InsertConversationTurnFts(connection, TestDict("id", "source_" + i, "text", texts[i]));
                }
                var plan = PlanMemoryQuery("npc_b", "player", "What price did we agree for the caravan?", context);
                var exchanges = FindPrecisionExchanges(connection, plan, observer, 8);
                add("non_primary_group_participant_exact_recall", exchanges.Any(e => e.Text.Contains("300 denars") && e.Text.Contains("Nothing has been paid")), exchanges.Select(e => e.SourceId).ToList());
                var exact = SearchExactConversationHistory(connection, "npc_b", "caravan price", TestDict(), 24000, null, "current_session", 30, observer);
                add("legacy_exact_route_includes_secondary_participant", ReadString(exact, "text", "").Contains("300 denars"), exact);
                var stranger = new DialogueService.KnowledgeAccessContext { NpcId = "npc_b_child", WorldDay = 30, TimelineId = "timeline_a" };
                add("similar_identifier_is_not_participant", FindPrecisionExchanges(connection, PlanMemoryQuery("npc_b_child", "player", "caravan", context), stranger, 8).Count == 0, null);
                var early = new DialogueService.KnowledgeAccessContext { NpcId = "npc_b", WorldDay = 19, TimelineId = "timeline_a" };
                var earlyPlan = PlanMemoryQuery("npc_b", "player", "caravan", TestDict("worldDay", 19d, "timelineId", "timeline_a"));
                add("future_exchange_unavailable", FindPrecisionExchanges(connection, earlyPlan, early, 8).Count == 0, null);
                var otherTimeline = PlanMemoryQuery("npc_b", "player", "caravan", TestDict("worldDay", 30d, "timelineId", "timeline_b"));
                add("other_timeline_unavailable", FindPrecisionExchanges(connection, otherTimeline, new DialogueService.KnowledgeAccessContext { NpcId = "npc_b", WorldDay = 30, TimelineId = "timeline_b" }, 8).Count == 0, null);

                DialogueSelfTestStore.InsertMemories4(connection);
                add("unrelated_rows_cannot_exhaust_candidate_limit", DialogueService.QueryKnownRows(connection, "memories", observer, "status='active'", 4).Any(r => ReadString(r, "memory_id", "") == "eligible_old"), null);
                using (var obligationFixture = connection.BeginTransaction())
                {
                    void SaveObligation(string id, long timestamp, string text, string validation = "", string spanStatus = "", string evidenceType = "", bool hidden = false)
                    {
                        var obligation = TestDict("obligation_id", id, "description", text, "owed_by", "npc_b", "owed_to", "player",
                            "ownerId", "npc_b", "status", "unresolved", "campaignId", campaign, "timelineId", "timeline_a",
                            "worldDay", 20d, "created_day", 20d, "visibility", "private", "known_by", new[] { "npc_b", "player" },
                            "participants", new[] { "npc_b", "player" });
                        if (validation.Length > 0) obligation["sourceValidation"] = validation;
                        if (spanStatus.Length > 0) obligation["sourceEvidence"] = TestDict("status", spanStatus, "quote", text, "speakerId", "npc_b");
                        if (evidenceType.Length > 0) obligation["evidenceType"] = evidenceType;
                        if (hidden) obligation["hidden_from"] = new[] { "npc_b" };
                        DialogueService.InsertObligation(connection, "obligation_event_" + id, obligation, timestamp, "dialogue");
                    }
                    const string supported = "I promise to inspect the markerproof only if the caravan arrives.";
                    const string court = "Native ledger payment for the markerproof.";
                    SaveObligation("supported_obligation", 21, supported, "supported_span", "supported_span", "spoken_statement");
                    SaveObligation("native_obligation", 22, court);
                    SaveObligation("incomplete_obligation", 31, "Markerproof unclear summary.", "incomplete", "incomplete", "unverified_summary");
                    SaveObligation("conditional_hypothesis", 32, "Maybe the markerproof is owed.", "incomplete", "supported_span", "hypothesis");
                    SaveObligation("marked_hypothesis", 33, "The markerproof might be owed.", "supported_span", "supported_span", "hypothesis");
                    SaveObligation("unverified_type_only", 34, "A supposed markerproof without evidence.", evidenceType: "unverified_summary");
                    SaveObligation("incomplete_span_only", 35, "Another markerproof missing its source.", spanStatus: "incomplete");
                    SaveObligation("hidden_obligation", 36, "Hidden markerproof obligation.", "supported_span", "supported_span", "spoken_statement", hidden: true);
                    var promptObligations = DialogueService.QueryKnownRows(connection, "obligations", observer, PromptActiveObligationSql, 2);
                    add("prompt_obligation_budget_excludes_incomplete_and_hypothetical_rows",
                        promptObligations.Select(r => ReadString(r, "description", "")).SequenceEqual(new[] { court, supported }), null);
                    var editorObligations = DialogueService.QueryKnownRows(connection, "obligations", observer, "", 20);
                    add("prompt_obligation_guard_preserves_stored_rows_for_review",
                        editorObligations.Count == 7 && editorObligations.Any(r => ReadString(r, "obligation_id", "") == "incomplete_obligation"), null);
                    var temporalObligations = LoadTemporalKnowledgeForPrompt(connection, campaign, observer, new() { "markerproof" }, 2);
                    add("temporal_obligation_prompt_keeps_own_revision_evidence",
                        temporalObligations.Select(r => ReadString(r, "claim", "")).ToHashSet().SetEquals(new[] { court, supported }), null);
                    obligationFixture.Rollback();
                }
                DialogueService.InsertMemoryFts(connection, "eligible_old", "300 denars caravan caravan agreement arrival", new List<string>(), new List<string>());
                add("eligible_ranked_lexical_source_found", DialogueService.SearchMemoryFts(connection, new List<string> { "caravan" }, 1, observer).Contains("eligible_old"), null);
                using(var fixture=connection.BeginTransaction())
                {
                    DialogueSelfTestStore.InsertBeliefs(connection);
                    add("owned_belief_relevance_precedes_candidate_limit",ReadString(DialogueService.QueryKnownRows(connection,"beliefs",observer,"",1,new List<string>{"caravan"}).Single(),"belief_id","")=="old_relevant_belief",null);
                    fixture.Rollback();
                }
                DialogueSelfTestStore.UpdateMemories2(connection);
                add("hidden_overrides_ownership", DialogueService.QueryKnownRows(connection, "memories", observer, "status='active'", 4).Count == 0
                    && DialogueService.SearchMemoryFts(connection, new List<string> { "caravan" }, 1, observer).Count == 0, null);

                var assertion = TestDict("subjectId", "npc_b", "predicate", "price", "objectId", "900", "claim", "Caravan price is 900.",
                    "believer", "npc_b", "cardinality", "one", "agreementId", "caravan", "timelineId", "timeline_a", "worldDay", 20d,
                    "sourceAcceptedText", "Caravan price is 900.", "evidenceQuote", "price is 900", "confidence", 0.8);
                string first, second;
                using (var transaction = connection.BeginTransaction()) { first = UpsertTemporalAssertion(connection, campaign, "event_1", "belief_1", "belief", assertion, 100); transaction.Commit(); }
                assertion["objectId"] = "300"; assertion["claim"] = "Caravan price is 300 only after arrival.";
                assertion["sourceAcceptedText"] = assertion["claim"]; assertion["evidenceQuote"] = "300 only after arrival"; assertion["worldDay"] = 25d;
                using (var transaction = connection.BeginTransaction()) { second = UpsertTemporalAssertion(connection, campaign, "event_2", "belief_2", "belief", assertion, 200); transaction.Commit(); }
                add("changed_terms_link_revision", ReadString(DialogueSelfTestStore.SelectTemporalKnowledgeAssertions2(connection, TestDict("id", second)).Single(), "supersedes_assertion_id", "") == first, null);
                add("current_terms_select_new_version", LoadTemporalKnowledgeForPrompt(connection, campaign, observer, new List<string> { "caravan" }, 4).Single().TryGetValue("assertion_id", out var latest) && Convert.ToString(latest) == second, null);
                var pastObserver = new DialogueService.KnowledgeAccessContext { NpcId = "npc_b", TimelineId = "timeline_a", WorldDay = 22 };
                add("as_of_query_selects_old_terms", ReadString(LoadTemporalKnowledgeForPrompt(connection, campaign, pastObserver, new List<string> { "caravan" }, 4).Single(), "assertion_id", "") == first, null);
                add("private_belief_never_shared_truth", LoadTemporalKnowledgeForPrompt(connection, campaign, new DialogueService.KnowledgeAccessContext { NpcId = "npc_a", TimelineId = "timeline_a", WorldDay = 30 }, new List<string> { "caravan" }, 4).Count == 0, null);
                using(var fixture=connection.BeginTransaction())
                {
                    DialogueSelfTestStore.InsertTemporalKnowledgeAssertions2(connection, TestDict("campaign",campaign));
                    add("regional_assertions_cannot_leak_or_displace_known_fact",ReadString(LoadTemporalKnowledgeForPrompt(connection,campaign,observer,new List<string>{"caravan"},1).Single(),"assertion_id","")==second,null);
                    fixture.Rollback();
                }
                bool staleRejected = false;
                using (var transaction = connection.BeginTransaction())
                {
                    assertion["expectedRevision"] = 1;
                    try { UpsertTemporalAssertion(connection, campaign, "event_2", "belief_2", "belief", assertion, 300); }
                    catch (InvalidOperationException) { staleRejected = true; }
                }
                add("stale_assertion_write_rejected", staleRejected, null);

                string job = EnqueueMemoryJob(connection, campaign, "scene_summary", "npc_a", "scene:group_source", "group_source");
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                add("job_first_claim_succeeds", ClaimMemoryJob(connection, job, "worker_a", now) != null, null);
                add("job_second_claim_refused", ClaimMemoryJob(connection, job, "worker_b", now + 1) == null, null);
                add("job_expired_lease_reclaimed", ClaimMemoryJob(connection, job, "worker_b", now + 121) != null, null);
                FinishMemoryJob(connection, job, "worker_a", "completed", TestDict());
                add("stale_worker_cannot_finish", ReadString(DialogueSelfTestStore.SelectMemoryBackgroundJobs(connection, TestDict("id", job)).Single(), "status", "") == "running", null);
                RetryMemoryJob(connection, job, "worker_b", 4, "exhausted fixture", TestDict());
                add("exhaustion_observable_not_completed", ReadString(DialogueSelfTestStore.SelectMemoryBackgroundJobs(connection, TestDict("id", job)).Single(), "status", "") == "incomplete", null);
                add("same_source_job_is_idempotent", job == EnqueueMemoryJob(connection, campaign, "scene_summary", "npc_a", "scene:group_source", "group_source"), null);
                add("exhausted_source_not_resurrected", ClaimMemoryJob(connection, job, "worker_c", now + 5000) == null, null);

                var invalidCompaction = EvaluateMemoryCompactionResponse(TestDict("ok", true, "content", "{}", "finishReason", "length"),
                    sourceRows, raw, new(), 1800, false);
                string stableJob = EnqueueMemoryJob(connection, campaign, "scene_summary", "npc_a", "scene:stable_compaction", "group_source");
                ClaimMemoryJob(connection, stableJob, "stable_worker", now);
                CompleteMemoryCompactionJob(connection, stableJob, "stable_worker", 1, "scene_summary", invalidCompaction);
                var stableRow = DialogueSelfTestStore.SelectMemoryBackgroundJobs(connection, TestDict("id", stableJob)).Single();
                var storedResult = TryParseJsonObject(ReadString(stableRow, "result_json", "{}"));
                add("compaction_limit_stops_after_first_attempt_with_diagnostic", ReadString(stableRow, "status", "") == "incomplete"
                    && ReadInt(stableRow, "attempt_count", 0) == 1 && ReadString(storedResult, "summary", "") == string.Join("\n---\n", raw)
                    && ReadString(ReadDictionary(storedResult, "compactionDiagnostic"), "reason", "") == "provider_output_limit"
                    && ClaimMemoryJob(connection, stableJob, "retry_worker", now + 5000) == null, null);
                var transientCompaction = EvaluateMemoryCompactionResponse(TestDict("ok", false, "error", "HTTP status 503 temporarily unavailable"),
                    sourceRows, raw, new(), 1800, false);
                string transientJob = EnqueueMemoryJob(connection, campaign, "scene_summary", "npc_a", "scene:transient_compaction", "group_source");
                ClaimMemoryJob(connection, transientJob, "transient_worker", now);
                CompleteMemoryCompactionJob(connection, transientJob, "transient_worker", 1, "scene_summary", transientCompaction);
                var transientRow = DialogueSelfTestStore.SelectMemoryBackgroundJobs(connection, TestDict("id", transientJob)).Single();
                add("compaction_transport_failure_retains_bounded_backoff", ReadString(transientRow, "status", "") == "failed"
                    && ReadLong(transientRow, "next_attempt_ts", 0) > now
                    && ClaimMemoryJob(connection, transientJob, "early_worker", now + 1) == null, null);
                for (int attempt = 2; attempt <= 4; attempt++)
                {
                    var claimed = ClaimMemoryJob(connection, transientJob, "later_worker", now + attempt * 5000);
                    CompleteMemoryCompactionJob(connection, transientJob, "later_worker", ReadInt(claimed, "attempt_count", 0), "scene_summary", transientCompaction);
                }
                var exhaustedRow = DialogueSelfTestStore.SelectMemoryBackgroundJobs(connection, TestDict("id", transientJob)).Single();
                add("compaction_transient_exhaustion_remains_incomplete", ReadString(exhaustedRow, "status", "") == "incomplete"
                    && ReadInt(exhaustedRow, "attempt_count", 0) == 4
                    && ClaimMemoryJob(connection, transientJob, "extra_worker", now + 50000) == null, null);

                var stage = BuildMemoryStagingManifest(connection, TestDict("campaignId", campaign, "timelineId", "timeline_a"));
                add("staging_preserves_complete_sources", ReadDictionaryList(stage, "operations").Count == 1
                    && ReadString(ReadDictionary(ReadDictionaryList(stage, "operations")[0], "prepared"), "summary", "").Contains(texts.Last()), stage["coverage"]);
                using (var preview = connection.BeginTransaction())
                {
                    var receipt = RepairConversationContinuity(connection, stage);
                    add("staged_apply_derivatives_only", ReadBool(receipt, "ok", false) && DialogueSelfTestStore.SelectConversationTurns54(connection).Count == 3, receipt);
                    var published = DialogueSelfTestStore.SelectSummaries(connection).Single();
                    string originalTurns = MemorySourceHash(DialogueSelfTestStore.SelectConversationTurns55(connection));
                    var duplicate = RepairConversationContinuity(connection, stage);
                    var freshStage = BuildMemoryStagingManifest(connection, TestDict("campaignId", campaign, "timelineId", "timeline_a"));
                    RepairConversationContinuity(connection, freshStage);
                    add("legacy_staging_repeat_preserves_ids_tags_and_originals", ReadBool(duplicate, "idempotent", false)
                        && DialogueSelfTestStore.SelectSummaries(connection).Count == 1
                        && DialogueService.TextListFromJson(ReadString(published, "tags_json", "[]")).Contains("denars")
                        && DialogueSelfTestStore.SelectMemorySources2(connection, TestDict("id", ReadString(published, "summary_id", ""))).Count == 3
                        && MemorySourceHash(DialogueSelfTestStore.SelectConversationTurns55(connection)) == originalTurns, null);
                    preview.Rollback();
                }
                add("preview_rolls_back_publication", DialogueSelfTestStore.SelectSummaries(connection).Count == 0, null);
                using (var upgrade = connection.BeginTransaction())
                {
                    DialogueSelfTestStore.InsertSummaries(connection);
                    DialogueService.InsertSummaryFts(connection, "old_staged_scene", "Earlier derived scene text.", new() { "scene" }, new() { "npc_a", "npc_b", "player" });
                    var upgradeStage = BuildMemoryStagingManifest(connection, TestDict("campaignId", campaign, "timelineId", "timeline_a"));
                    RepairConversationContinuity(connection, upgradeStage);
                    var repeatedUpgrade = RepairConversationContinuity(connection, upgradeStage);
                    add("legacy_staging_version_upgrade_supersedes_previous", ReadBool(repeatedUpgrade, "idempotent", false)
                        && DialogueSelfTestStore.SelectSummaries2(connection).Count == 1
                        && ReadString(DialogueSelfTestStore.SelectSummaries3(connection).Single(), "status", "") == "superseded"
                        && DialogueSelfTestStore.SelectSummaryFts(connection).Count == 1, null);
                    upgrade.Rollback();
                }
                var bad = ReadDictionary(ReadDictionaryList(stage, "operations")[0], "prepared");
                bad["summary"] = "900 has been paid.";
                bool tampered = false;
                using (var preview = connection.BeginTransaction())
                { try { RepairConversationContinuity(connection, stage); } catch (InvalidOperationException) { tampered = true; } }
                add("staging_rejects_changed_claims", tampered, null);
                using(var fixture=connection.BeginTransaction())
                {
                    DialogueSelfTestStore.InsertConversationSessions15(connection, TestDict("campaign",campaign));
                    var behindProbes=FindPrecisionExchanges(connection,plan,observer,1);
                    add("recall_probes_do_not_exhaust_exchange_limit",behindProbes.Any(e=>e.Text.Contains("Nothing has been paid")),null);
                    fixture.Rollback();
                }

                var packet = BuildPrecisionMemoryPacket(campaign,"npc_b","player","","What did we agree for the caravan?",context,settings,false);
                add("production_packet_exact_terms",ReadString(packet,"memoryPacket","").Contains("300 denars") && ReadString(packet,"memoryPacket","").Contains("Nothing has been paid"),packet);
                DialogueSelfTestStore.InsertConversationSessions16(connection, TestDict("campaign", campaign));
                try
                {
                    var foundationAudit = TestDict();
                    var rebuiltFoundation = LoadPrecisionFoundationProjection(connection, campaign, plan, context, observer, false, foundationAudit);
                    var cachedFoundation = LoadPrecisionFoundationProjection(connection, campaign, plan, context, observer, false, foundationAudit);
                    var rebuiltPair = rebuiltFoundation.Single(e => e.SourceTurns.Any(t => ReadString(t, "id", "") == "foundation_cache_turn"));
                    var cachedPair = cachedFoundation.Single(e => e.SourceId == rebuiltPair.SourceId);
                    add("raw_relationship_foundation_reader_priority_cache_roundtrip", ReadString(foundationAudit, "foundationCache", "") == "fresh"
                        && rebuiltPair.RelationshipFoundation && cachedPair.RelationshipFoundation
                        && rebuiltPair.Required && cachedPair.Required && rebuiltPair.SourceHash == cachedPair.SourceHash
                        && CanonicalJson(rebuiltPair.SourceTurns) == CanonicalJson(cachedPair.SourceTurns), null);
                    add("cached_relationship_foundation_keeps_writer_boundary", SelectPrecisionEvidence(new[] { cachedPair }, 1).Text
                        .Contains("That does not mean I agree to leave my family."), null);
                    add("foundation_classification_does_not_rewrite_original", ReadString(DialogueSelfTestStore.SelectConversationTurns56(connection).Single(), "text", "")
                        == "I trust you. That does not mean I agree to leave my family.", null);
                }
                finally
                {
                    DialogueSelfTestStore.DeleteConversationTurns2(connection);
                }
                var cacheAudit = TestDict();
                LoadPrecisionFoundationProjection(connection,campaign,plan,context,observer,false,cacheAudit);
                LoadPrecisionFoundationProjection(connection,campaign,plan,context,observer,false,cacheAudit);
                add("projection_cache_revision_bound",ReadString(cacheAudit,"foundationCache","")=="fresh",cacheAudit);
                DialogueSelfTestStore.UpdateConversationTurns46(connection);
                LoadPrecisionFoundationProjection(connection,campaign,plan,context,observer,false,cacheAudit);
                add("projection_catches_late_source_revision",ReadString(cacheAudit,"foundationCache","")=="caught_up",cacheAudit);
                DialogueSelfTestStore.InsertConversationTurns2(connection);
                using (var privateStage = connection.BeginTransaction())
                {
                    var whisperedStage = BuildMemoryStagingManifest(connection, TestDict("campaignId", campaign, "timelineId", "timeline_a"));
                    RepairConversationContinuity(connection, whisperedStage);
                    var whisperedSummary = DialogueSelfTestStore.SelectSummaries2(connection).Single();
                    add("legacy_staging_whisper_excludes_nonwitness", !DialogueService.TextListFromJson(ReadString(whisperedSummary, "known_by_json", "[]")).Contains("npc_b")
                        && !DialogueService.KnowledgeRowVisibleToNpc("summaries", whisperedSummary, observer, connection), null);
                    privateStage.Rollback();
                }
                var privateSafe = PrecisionExchangeEvidence(connection,DialogueSelfTestStore.SelectConversationTurns57(connection).Single(),observer,true,100);
                add("exchange_expansion_filters_whispers",!privateSafe.Text.Contains("SECRET_UNHEARD_PRICE"),null);
                var recentSafe=LoadMostRecentClosedConversation(connection,"npc_b","",24000,30,observer);
                add("recent_history_filters_whispers",!ReadString(recentSafe,"text","").Contains("SECRET_UNHEARD_PRICE"),null);
                var beforeMode=ReadMemoryPrecisionState(connection);
                using(var preview=connection.BeginTransaction())
                {
                    var modeReceipt=RepairConversationContinuity(connection,TestDict("schema","reign-continuity-repair-v1","campaignId",campaign,"timelineId","timeline_a",
                        "operations",new List<Dictionary<string,object>> { TestDict("kind","set_memory_mode","id","current","mode","precision","reviewReason","isolated rollout check","expectedState",beforeMode) }));
                    add("mode_activation_increments_projection_generation",ReadInt(ReadMemoryPrecisionState(connection),"projection_generation",0)==ReadInt(beforeMode,"projection_generation",0)+1,modeReceipt);
                    preview.Rollback();
                }
                add("mode_preview_preserves_campaign",ReadString(ReadMemoryPrecisionState(connection),"mode","")==ReadString(beforeMode,"mode",""),null);

                var groupPayload=TestDict("mode","party_chat","conversationSessionId","shared_beat","sceneTurnId","beat_1",
                    "playerHeroStringId","player","playerText","What are the terms?","participantHeroIds",new List<string>{"npc_a","npc_b","npc_c","player"});
                UpdateSharedGroupConversationState(campaign,groupPayload,"npc_a","First speaker","Initial amount 900.","shared_beat",100);
                UpdateSharedGroupConversationState(campaign,groupPayload,"npc_b","Second speaker","Correction: 300 only after arrival.","shared_beat",100);
                string shared=BuildSharedGroupConversationPrompt(campaign,groupPayload,"npc_c",new List<Dictionary<string,object>>(),"What are the terms?");
                add("same_beat_actual_delivery_order",shared.IndexOf("Initial amount 900.",StringComparison.Ordinal)<shared.IndexOf("Correction: 300 only after arrival.",StringComparison.Ordinal),shared);
                UpdateSharedGroupConversationState(campaign,groupPayload,"npc_c","Third speaker","","shared_beat",101);
                add("empty_reply_is_not_delivery",DialogueSelfTestStore.SelectGroupConversationContributions5(connection).Count==2,null);
                groupPayload["participantHeroIds"]=new List<string>{"npc_a","player"};
                UpdateSharedGroupConversationState(campaign,groupPayload,"npc_a","First speaker","PRIVATE_QUESTION?","shared_beat",102);
                string privateGroup=BuildSharedGroupConversationPrompt(campaign,groupPayload,"npc_c",new List<Dictionary<string,object>>(),"What are the terms?");
                add("group_private_topics_never_leak",!privateGroup.Contains("PRIVATE_QUESTION"),privateGroup);
                var epochPacket=ReadDictionary(packet,"precisionEvidence");
                add("request_generation_current_before_restore",PrecisionEvidenceGenerationCurrent(epochPacket),null);

                string oldGeneration = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                // Run outside this connection's transaction: mirrors post-restore fencing.
                AdvanceMemoryRestoreGeneration(campaign);
                add("restore_changes_generation", oldGeneration != ReadString(ReadMemoryPrecisionState(connection), "restore_generation", ""), null);
                add("request_generation_rejected_after_restore",!PrecisionEvidenceGenerationCurrent(epochPacket),null);
                add("save_sync_classifies_derivatives", new[] { "memory_precision_state", "memory_projections", "memory_background_jobs", "temporal_knowledge_assertions" }.All(t => PersistenceService.SaveSyncSubsystemForTable(t) == "memories"), null);
            }

            foreach (int turns in new[] { 10, 30, 100, 200 })
            {
                var candidates = Enumerable.Range(0, turns).Select(i => new MemoryEvidence { SourceId = "turn_" + i, SourceHash = "hash_" + i,
                    Text = i == turns - 1 ? "Accepted correction: 300 denars only after arrival. No payment completed." : "Unrelated complete historical exchange " + i + ": " + new string('x', 600),
                    Required = i == turns - 1, Score = i == turns - 1 ? 100 : 1, Owner = "npc_b" }).ToList();
                var packet = SelectPrecisionEvidence(candidates);
                var audit = PrecisionEvidenceAudit(packet, PlanMemoryQuery("npc_b", "player", "price", context), "precision");
                add("scale_" + turns + "_required_terms_and_budget", packet.Text.Contains("300 denars only after arrival") && packet.Tokens <= 4000, TestDict("tokens", packet.Tokens, "records", packet.Selected.Count));
                add("scale_" + turns + "_id_without_content_rejected", MissingPrecisionEvidence("m1", audit).Count > 0, null);
                add("scale_" + turns + "_final_content_verified", MissingPrecisionEvidence(packet.Text, audit).Count == 0, null);
            }
            var helperCandidates = new List<MemoryEvidence> { new MemoryEvidence { SourceId = "trusted", Text = "Only 300 after arrival.", Score = 1 } };
            var helperPlan = PlanMemoryQuery("npc_b", "player", "How much did we agree?", context);
            var helperDiagnostics = TestDict();
            var helperNeeds = new List<string>();
            ResolvePrecisionAmbiguity(helperPlan, helperCandidates, helperNeeds, helperDiagnostics);
            add("local_ambiguity_keeps_unresolved_needs_without_model_calls", helperNeeds.Count > 0
                && ReadInt(helperDiagnostics, "helperCalls", -1) == 0, helperDiagnostics);
            string schema = "{\n  \"required\": [\"reply\"], \"description\": \"Preserve exact terms and conditions\"\n}";
            add("compact_schema_semantic_parity",CanonicalJson(TryParseJsonObject(schema))==CanonicalJson(TryParseJsonObject(CompactPrecisionSchema(schema))) && CompactPrecisionSchema(schema).Length<schema.Length,null);
            foreach (string custom in new[] { "Preserve my custom rule.\n" + schema, schema + "\nKeep my additional condition.", "```json\n" + schema + "\n```" })
                add("schema_compaction_preserves_custom_instructions_" + PromptHash(custom).Substring(0, 8), CompactPrecisionSchema(custom) == custom, null);
            foreach (string name in new[] { "dialogue_output_schema.json", "event_output_schema.json" })
            {
                var output = TryParseJsonObject(ConversationMemoryOutputSchema(PromptService.DefaultPromptText(name, PromptService.DefaultPromptTemplates())));
                add("ordinary_json_example_source_fields_" + name, new[] { "memoryWrites", "beliefWrites", "obligationWrites", "comprehensionWrites" }
                    .All(field => ReadDictionaryList(output, field).Count > 0 && ReadDictionaryList(output, field)
                        .All(write => ReadDictionary(write, "sourceEvidence")?.ContainsKey("quote") == true)), null);
                var question = ReadDictionaryList(output, "continuityWrites").Single();
                add("ordinary_json_example_question_answer_fields_" + name, ReadDictionary(question, "questionState")?.ContainsKey("answers") == true
                    && ReadDictionary(question, "questionState")?.ContainsKey("evaluation") == true, null);
            }
            const string customExample = "{\"reply\":\"Preserve my long custom reply guidance exactly as written by the owner.\",\"memoryWrites\":[{\"text\":\"Preserve my long custom memory guidance exactly as written by the owner.\"}],\"myField\":true}";
            var expandedCustom = TryParseJsonObject(ConversationMemoryOutputSchema(customExample));
            add("memory_schema_extension_preserves_custom_values", ReadString(expandedCustom, "reply", "") == ReadString(TryParseJsonObject(customExample), "reply", "")
                && ReadBool(expandedCustom, "myField", false)
                && ReadString(ReadDictionaryList(expandedCustom, "memoryWrites").Single(), "text", "")
                    == ReadString(ReadDictionaryList(TryParseJsonObject(customExample), "memoryWrites").Single(), "text", ""), null);
            string wrappedExample = "Do not remove this prefix.\n" + customExample + "\nDo not remove this suffix.";
            add("memory_schema_extension_preserves_wrapped_custom_instructions", ConversationMemoryOutputSchema(wrappedExample).StartsWith(wrappedExample, StringComparison.Ordinal), null);
            foreach (string actionExample in new[] { customExample, wrappedExample })
            {
                string actionRendered = ConversationActionOutputSchema(actionExample, true);
                add("action_schema_extension_preserves_custom_instructions_" + PromptHash(actionExample).Substring(0, 8),
                    actionRendered.Contains("Preserve my long custom reply guidance exactly as written by the owner.")
                    && actionRendered.Contains("\"structuredActionDecision\"")
                    && (actionExample == customExample || actionRendered.StartsWith(wrappedExample, StringComparison.Ordinal)), null);
            }
            results.AddRange(RunRecentMemoryWindowSelfTests());
            results.AddRange(RunMemorySelectionHelperSelfTests());
            results.AddRange(RunMemorySnapshotSourceAssertions());
            results.AddRange(RunMemoryCompactionOutcomeSelfTests());
            results.AddRange(RunDerivedMemoryTimeSelfTests());
            results.AddRange(RunConversationArchiveStorageSelfTests());
            return results;
        }

        private static List<Dictionary<string, object>> RunConversationArchiveStorageSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object evidence = null) => results.Add(TestDict(
                "caseId", "memory_precision_archive_" + id, "passed", pass, "summary", id, "data", evidence));
            string campaign = "memory_archive_test_" + Guid.NewGuid().ToString("N");
            string original = "ORIGINAL_BEGIN " + new string('x', 6200) + char.ConvertFromUtf32(0x1F33F) + " ORIGINAL_END";
            const string lastSource = "Correction: the proposed visit has not happened. We can visit tomorrow only after the caravan arrives. FINAL_ARCHIVE_SOURCE";
            string historical = original + " Lucon departed and declared his withdrawal permanent. He did not re-engage on the same terms.";
            string archive = NormalizeHistoricalRoleplayContinuityText(historical, 1800, true, HistoricalTextPurpose.StoredArchive);
            Check("historical_archive_preserves_whole_quoted_source_and_idempotent_scope", archive.Contains(historical, StringComparison.Ordinal)
                && archive.Contains("historical scene", StringComparison.Ordinal)
                && NormalizeHistoricalRoleplayContinuityText(archive, 1800, true, HistoricalTextPurpose.StoredArchive) == archive
                && NormalizeHistoricalRoleplayContinuityText(archive, 1800, false, HistoricalTextPurpose.StoredArchive) == archive
                && NormalizeHistoricalRoleplayContinuityText(NormalizeHistoricalRoleplayContinuityText(archive, 1800, false,
                    HistoricalTextPurpose.StoredArchive), 1800, true, HistoricalTextPurpose.StoredArchive) == archive);
            Check("prompt_projection_remains_bounded", NormalizeHistoricalRoleplayContinuityText(historical, 1800, true).Length <= 1800);
            string legacyFramedArchive = "A speaker ended an earlier historical conversation. " + HistoricalConversationScopeNote(true);
            Check("legacy_scope_framing_is_retained_without_rewriting", NormalizeHistoricalRoleplayContinuityText(
                legacyFramedArchive, 1800, false, HistoricalTextPurpose.StoredArchive) == legacyFramedArchive);
            var response = TestDict("ok", true, "finishReason", "stop", "content", "{\"sourceIndices\":[1,2]}");
            var rejected = EvaluateMemoryCompactionResponse(response, new(), new() { new string('a', 1800), new string('b', 1800) }, new(), 1800, true);
            var selection = ReadDictionary(ReadDictionary(rejected, "compactionDiagnostic"), "selectionValidation");
            Check("oversized_valid_selection_reports_exact_budget_failure", ReadString(selection, "reason", "") == "selection_exceeds_character_budget"
                && ReadInt(selection, "selectedCount", 0) == 2 && ReadInt(selection, "selectedCharactersWithFraming", 0) > 1800
                && ReadString(rejected, "sourceCoverage", "") == "all_records"
                && ReadString(rejected, "compactionStatus", "") == "incomplete");
            try
            {
                var context = TestDict("campaignId", campaign, "timelineId", "archive_branch", "worldDay", 1d,
                    "playerHeroStringId", "archive_player", "conversationSessionId", "archive_session");
                ConversationStartApi(TestDict("campaignId", campaign, "timelineId", "archive_branch", "sessionId", "archive_session",
                    "npcId", "archive_npc", "playerId", "archive_player", "worldDay", 1d, "ts", 1L,
                    "participants", new[] { "archive_npc", "archive_player" }));
                using var c = ReignDatabase.OpenCampaignConnection(campaign);
                CharactersService.EnsureIdentitySchema(c);
                DialogueSelfTestStore.InsertAcquaintances2(c);
                var audience = new List<string> { "archive_npc", "archive_player" };
                InsertConversationTurn(c, "archive_first", "archive_session", "archive_event", 0, "archive_exchange", "player",
                    "archive_player", "Player", original, "in_person", 1d, 1L, "", audience, context);
                InsertConversationTurn(c, "archive_last", "archive_session", "archive_event", 1, "archive_exchange", "npc",
                    "archive_npc", "NPC", lastSource, "in_person", 1d, 2L, "", audience, context);
                var session = DialogueSelfTestStore.SelectConversationSessions3(c).Single();
                var turns = DialogueSelfTestStore.SelectConversationTurns58(c);
                var sources = new List<string>();
                var result = CreateConversationSceneSummary(campaign, session, turns, 3L,
                    TestDict("useMemoryLlmForConsolidation", false, "enableSemanticMemory", false, "memoryConsolidationMaxSummaryChars", 500),
                    captureSourceRecords: records => sources.AddRange(records));
                var row = DialogueSelfTestStore.SelectSummaries4(c, TestDict("id", ReadString(result, "summaryId", ""))).Single();
                string stored = ReadString(row, "summary", "");
                Check("scene_fallback_roundtrips_beyond_old_storage_ceiling", stored.Length > 6200
                    && sources.All(source => stored.Contains(source, StringComparison.Ordinal))
                    && stored.Contains(original, StringComparison.Ordinal) && stored.Contains(lastSource, StringComparison.Ordinal),
                    TestDict("storedCharacters", stored.Length, "sourceCount", sources.Count));
                Check("canonical_turns_are_unchanged", ReadString(DialogueSelfTestStore.SelectConversationTurns59(c).Single(), "text", "") == original);
                var metadata = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                string view = CompactRecentSummaryView(stored, metadata, new[] { "FINAL_ARCHIVE_SOURCE" }, 1800, out _);
                Check("bounded_view_selects_complete_sources_without_clipping_archive", view.Length <= 1800
                    && view.Contains(lastSource, StringComparison.Ordinal) && !view.Contains("ORIGINAL_BEGIN", StringComparison.Ordinal));

                var candidate = TestDict("digest", new[] {
                    TestDict("text", "The player spoke about the proposed visit.", "sourceIndices", new[] { 1 }),
                    TestDict("text", new string('x', 500), "sourceIndices", new[] { 2 }) });
                var partial = CreateConversationSceneSummary(campaign, session, turns, 4L,
                    TestDict("useMemoryLlmForConsolidation", true, "enableSemanticMemory", false, "memoryConsolidationMaxSummaryChars", 500),
                    responder: _ => TestDict("ok", true, "content", Json.Serialize(candidate)));
                string summaryId = ReadString(partial, "summaryId", "");
                var partialRow = DialogueSelfTestStore.SelectSummaries4(c, TestDict("id", summaryId)).Single();
                var payload = TryParseJsonObject(ReadString(partialRow, "payload_json", "{}"));
                var packing = ReadDictionary(ReadDictionary(payload, "compactionDiagnostic"), "packing");
                string packedText = ReadString(partialRow, "summary", "");
                Check("partial_scene_persists_measured_text_and_complete_originals",
                    ReadString(payload, "compactionStatus", "") == "partial" && ReadString(payload, "interpretationStatus", "") == "unverified"
                    && Json.Serialize(packedText).Length == ReadInt(packing, "serializedCharacters", 0)
                    && Json.Serialize(packedText).Length <= 500 && !packedText.Contains("FINAL_ARCHIVE_SOURCE")
                    && OriginalCompactionRecords(partialRow, true).SequenceEqual(sources)
                    && ReadString(DialogueSelfTestStore.SelectConversationTurns59(c).Single(), "text", "") == original,
                    packing);
                var recallPlan = PlanMemoryQuery("archive_npc", "archive_player", "When can we visit after the caravan arrives?", context);
                var knowledge = new KnowledgeAccessContext { NpcId = "archive_npc", TimelineId = "archive_branch", WorldDay = 1d };
                var recalled = ExpandPrecisionSource(c, "summaries", partialRow, recallPlan, knowledge, 99);
                Check("linked_recall_recovers_omitted_correction_and_final_answer", recalled
                    .SelectMany(e => e.SourceTurns)
                    .Any(t => ReadString(t, "id", "") == "archive_last" && ReadString(t, "text", "").Contains(lastSource, StringComparison.Ordinal)));

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string job = EnqueueMemoryJob(c, campaign, "scene_summary", "archive_npc", "scene:archive_session", "archive_session");
                ClaimMemoryJob(c, job, "archive_worker", now);
                CompleteMemoryCompactionJob(c, job, "archive_worker", 1, "scene_summary", partial);
                var background = MemoryBackgroundStatusApi(new Dictionary<string, string> { ["campaignId"] = campaign });
                var jobRow = ReadDictionaryList(background, "jobs").SingleOrDefault(j => ReadString(j, "job_id", "") == job);
                var jobResult = TryParseJsonObject(ReadString(jobRow, "result_json", "{}"));
                Check("partial_job_terminal_and_distinct_from_completed",
                    ReadInt(background, "partial", 0) == 1 && ReadInt(background, "completed", -1) == 0
                    && ReadString(jobRow, "status", "") == "partial" && ReadInt(jobRow, "attempt_count", 0) == 1
                    && !ReadBool(jobResult, "retryable", true)
                    && ReadString(ReadDictionary(jobResult, "compactionDiagnostic"), "reason", "") == "attributed_digest_partial"
                    && ClaimMemoryJob(c, job, "retry_worker", now + 5000) == null);
                Check("consolidation_propagates_partial_and_prioritizes_incomplete",
                    ConsolidatedMemoryCompactionStatus(new() { partial }) == "partial"
                    && ConsolidatedMemoryCompactionStatus(new() { partial, result }) == "incomplete");
            }
            catch (Exception ex) { Check("suite_exception", false, ex.ToString()); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }

        private static List<Dictionary<string, object>> RunDerivedMemoryTimeSelfTests()
        {
            if (string.IsNullOrWhiteSpace(Program.CampaignsRootOverride.Value))
                throw new InvalidOperationException("Derived-memory temporal fixtures require isolated Verification Lab state.");
            string campaign = "derived_memory_time_test_" + Guid.NewGuid().ToString("N");
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass) => results.Add(TestDict("caseId", "memory_precision_derived_time_" + id,
                "passed", pass, "summary", id));
            try
            {
                using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                var context = TestDict("timelineId", "derivative_branch", "worldDay", 20d);
                DialogueSelfTestStore.InsertConversationSessions17(connection, TestDict("campaign", campaign, "payload", Json.Serialize(context)));
                const string answer = "If we visit the mill, I will inspect the roof. We have not gone there together yet.";
                InsertConversationTurn(connection, "source_question", "source_session", "source_event", 0, "source_exchange",
                    "player", "player", "Player", "If we visit the mill, who will inspect the roof?", "in_person", 20d, 100L,
                    "", new List<string> { "npc", "player" }, context);
                InsertConversationTurn(connection, "source_answer", "source_session", "source_event", 1, "source_exchange",
                    "npc", "npc", "NPC", answer, "in_person", 20d, 100L,
                    "", new List<string> { "npc", "player" }, context);
                DialogueSelfTestStore.UpdateConversationTurns47(connection);
                DialogueService.KnowledgeAccessContext Access(long cutoff = 11, double day = 20, long timestamp = 100, string branch = "derivative_branch", string npc = "npc") =>
                    new DialogueService.KnowledgeAccessContext { NpcId = npc, PlayerId = "player", TimelineId = branch, WorldDay = day,
                        AsOfAcceptedSequence = cutoff, AsOfTimestamp = timestamp };
                var plan = PlanMemoryQuery("npc", "player", "mill roof", context);
                foreach (var kind in new[] { ("memories", "memory_id"), ("summaries", "summary_id"),
                    ("beliefs", "belief_id"), ("comprehension", "comprehension_id") })
                {
                    var row = TestDict(kind.Item2, "derived_" + kind.Item1, "event_id", "source_event", "owner_id", "npc", "believer_id", "npc",
                        "summary", answer, "claim", answer, "text", answer, "status", "active", "ts", 1L,
                        "payload_json", Json.Serialize(TestDict("timelineId", "derivative_branch", "worldDay", 1d)));
                    string before = CanonicalJson(row);
                    Check(kind.Item1 + "_cannot_backdate_past_its_source", !DialogueService.KnowledgeRowVisibleToNpc(kind.Item1, row, Access(10), connection));
                    Check(kind.Item1 + "_available_after_source_acceptance", DialogueService.KnowledgeRowVisibleToNpc(kind.Item1, row, Access(), connection));
                    Check(kind.Item1 + "_source_day_timestamp_and_branch_control", !MemoryTimeVisible(row, Access(day: 19), connection)
                        && !MemoryTimeVisible(row, Access(timestamp: 99), connection)
                        && !MemoryTimeVisible(row, Access(branch: "another_branch"), connection));
                    Check(kind.Item1 + "_known_future_source_is_not_legacy", ExpandPrecisionSource(connection, kind.Item1, row, plan, Access(9), 70).Count == 0);
                    Check(kind.Item1 + "_partial_sources_do_not_reveal_later_claim", ExpandPrecisionSource(connection, kind.Item1, row, plan, Access(10), 70)
                        .All(e => !e.Text.Contains(answer) && e.Kind != "unverified" && e.Kind != "belief" && e.Kind != "interpretation"));
                    var strangerPlan = PlanMemoryQuery("stranger", "player", "mill roof", context);
                    Check(kind.Item1 + "_hidden_original_does_not_enable_fallback", ExpandPrecisionSource(connection, kind.Item1, row, strangerPlan, Access(npc: "stranger"), 70).Count == 0);
                    Check(kind.Item1 + "_read_leaves_record_unchanged", CanonicalJson(row) == before);
                }
                var linked = TestDict("summary_id", "linked_summary", "summary", answer, "owner_id", "npc", "ts", 1L);
                DialogueSelfTestStore.InsertMemorySources2(connection);
                Check("nested_source_lineage_keeps_accepted_boundary", !MemoryTimeVisible(linked, Access(10), connection)
                    && MemoryTimeVisible(linked, Access(), connection));
                var legacy = TestDict("memory_id", "legacy_missing", "summary", "Unverified older recollection.", "owner_id", "npc", "ts", 1L);
                Check("genuinely_missing_history_remains_explicitly_unverified", MemoryTimeVisible(legacy, Access(9), connection)
                    && ExpandPrecisionSource(connection, "memories", legacy, plan, Access(9), 70).Single().Kind == "unverified");
                var belief = TestDict("belief_id", "private_missing", "claim", "I suspect the old mill is unsafe.", "owner_id", "npc", "ts", 1L);
                Check("unknown_private_interpretation_emitted_once", ExpandPrecisionSource(connection, "beliefs", belief, plan, Access(), 70).Count == 1);
                DialogueSelfTestStore.UpdateConversationTurns48(connection);
                Check("known_inactive_source_is_not_missing_history", ExpandPrecisionSource(connection, "summaries", linked, plan, Access(), 70).Count == 0);
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }

        private static List<Dictionary<string, object>> RunMemoryCompactionOutcomeSelfTests()
        {
            if (string.IsNullOrWhiteSpace(Program.CampaignsRootOverride.Value))
                throw new InvalidOperationException("Compaction fixtures require isolated Verification Lab state.");
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass) => results.Add(TestDict("caseId", "memory_precision_compaction_" + id,
                "passed", pass, "summary", id));
            var rows = new List<Dictionary<string, object>> {
                TestDict("summary", "NPC asked whether the caravan had arrived.", "ts", 1L),
                TestDict("summary", new string('x', 3000), "ts", 2L),
                TestDict("summary", "Player answered: it may arrive tomorrow; no payment has happened.", "ts", 3L) };
            string before = CanonicalJson(rows);
            var settings = TestDict("useMemoryLlmForConsolidation", true, "memoryConsolidationMaxSummaryChars", 1800);
            int calls = 0;
            Dictionary<string, object> request = null;
            Dictionary<string, object> response = TestDict("ok", true, "finishReason", "length", "content", "{}", "model", "fixture",
                "raw", TestDict("usage", TestDict("completion_tokens", 1200, "completion_tokens_details", TestDict("reasoning_tokens", 1199))));
            Dictionary<string, object> Respond(Dictionary<string, object> input) { calls++; request = input; return response; }
            var length = DialogueService.BuildMiddleTermSummary(settings, "npc", "conversation:compaction_fixture", rows, responder: Respond, campaignId: "isolated_compaction_campaign");
            var diagnostic = ReadDictionary(length, "compactionDiagnostic");
            Check("length_failure_is_explicit_and_not_retried", calls == 1 && !ReadBool(length, "retryable", true)
                && ReadString(length, "compactionStatus", "") == "incomplete"
                && ReadString(diagnostic, "reason", "") == "provider_output_limit"
                && ReadInt(diagnostic, "reasoningTokens", 0) == 1199);
            string prompt = string.Join("\n", ReadDictionaryList(request, "messages").Select(m => ReadString(m, "content", "")));
            Check("request_is_attributed_to_exact_campaign", ReadString(request, "campaignId", "") == "isolated_compaction_campaign");
            Check("request_has_single_attempt_and_complete_final_source", ReadBool(request, "singleProviderAttempt", false)
                && rows.All(r => prompt.Contains(ReadString(r, "summary", ""), StringComparison.Ordinal)));
            string retained = ReadString(length, "summary", "");
            foreach (var item in new[] {
                ("empty_selection", "{\"sourceIndices\":[]}", "no_safe_selection"),
                ("oversized_selection", "{\"sourceIndices\":[2]}", "invalid_source_selection"),
                ("duplicate_selection", "{\"sourceIndices\":[1,1]}", "invalid_source_selection"),
                ("missing_selection", "{}", "missing_source_selection"),
                ("malformed_selection", "{\"sourceIndices\":[", "malformed_response") })
            {
                response = TestDict("ok", true, "finishReason", "stop", "content", item.Item2,
                    "cache", TestDict("idempotencyCacheHit", true));
                var result = DialogueService.BuildMiddleTermSummary(settings, "npc", "conversation:compaction_fixture", rows, responder: Respond);
                Check(item.Item1 + "_retains_originals_without_retry", ReadString(result, "summary", "") == retained
                    && ReadString(result, "compactionStatus", "") == "incomplete" && !ReadBool(result, "retryable", true)
                    && ReadString(ReadDictionary(result, "compactionDiagnostic"), "reason", "") == item.Item3
                    && ReadBool(ReadDictionary(result, "compactionDiagnostic"), "responseCacheHit", false));
            }
            response = TestDict("ok", true, "finishReason", "stop", "content", "{\"sourceIndices\":[1,3]}");
            var accepted = DialogueService.BuildMiddleTermSummary(settings, "npc", "conversation:compaction_fixture", rows, responder: Respond);
            Check("valid_partial_selection_preserves_answer_and_qualification", ReadString(accepted, "compactionStatus", "") == "partial"
                && !ReadBool(accepted, "coverageComplete", true) && ReadString(accepted, "summary", "").Contains("may arrive tomorrow; no payment has happened.")
                && ReadString(accepted, "summary", "").Contains("NPC asked") && ReadString(accepted, "summary", "").Length <= 1800);
            response["content"] = "{\"sourceIndices\":[3,2,1]}";
            var packedSelection = DialogueService.BuildMiddleTermSummary(settings, "npc", "conversation:compaction_fixture", rows, responder: Respond);
            Check("validated_source_selection_packs_complete_records", ReadString(packedSelection, "summary", "") == ReadString(accepted, "summary", "")
                && Json.Serialize(ReadDictionary(ReadDictionary(packedSelection, "compactionDiagnostic"), "packing")["omittedSourceIndices"]) == "[2]"
                && ReadStringList(packedSelection, "originalSourceRecords").Count == 3);
            response = TestDict("ok", false, "error", "HTTP status 401 invalid API key");
            var unauthorized = DialogueService.BuildMiddleTermSummary(settings, "npc", "conversation:compaction_fixture", rows, responder: Respond);
            Check("provider_configuration_failure_is_not_blindly_retried", !ReadBool(unauthorized, "retryable", true)
                && ReadString(ReadDictionary(unauthorized, "compactionDiagnostic"), "reason", "") == "provider_failure");
            int beforeLocal = calls;
            var complete = DialogueService.BuildMiddleTermSummary(settings, "npc", "conversation:short_fixture", new() { rows[0], rows[2] }, responder: Respond);
            Check("complete_sources_fit_without_provider", calls == beforeLocal && ReadBool(complete, "coverageComplete", false)
                && ReadString(complete, "summary", "").Contains(ReadString(rows[0], "summary", ""))
                && ReadString(complete, "summary", "").Contains(ReadString(rows[2], "summary", ""))
                && ReadString(ReadDictionary(complete, "compactionDiagnostic"), "reason", "") == "complete_sources_fit_locally");
            response = TestDict("ok", true, "finishReason", "stop", "content", "{\"digest\":[{\"text\":\"A source with no asserted events remains uninformative.\",\"sourceIndices\":[1]}]}");
            var oversize = DialogueService.BuildMiddleTermSummary(settings, "npc", "conversation:oversize_fixture", new() { rows[1] }, responder: Respond);
            Check("oversized_original_can_be_compressed_in_one_call", calls == beforeLocal + 1
                && ReadString(oversize, "compactionStatus", "") == "complete" && !ReadBool(oversize, "coverageComplete", true)
                && ReadString(oversize, "interpretationStatus", "") == "unverified" && ReadStringList(oversize, "originalSourceRecords").Single().Contains(new string('x', 3000))
                && ReadString(oversize, "summary", "").Length < 1800 && ReadInt(request, "maxTokens", 0) >= 4096);
            response["content"] = "{\"digest\":[{\"text\":\"Wrong reference\",\"sourceIndices\":[999]}]}";
            var wrongReference = DialogueService.BuildMiddleTermSummary(settings, "npc", "conversation:fixture", rows, responder: Respond);
            Check("digest_bad_reference_retains_originals", ReadString(wrongReference, "compactionStatus", "") == "incomplete"
                && ReadString(ReadDictionary(wrongReference, "compactionDiagnostic"), "reason", "") == "digest_invalid_source_reference");
            string unicode = string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1F33F), 140));
            var entries = new[] {
                TestDict("text", "NPC asked whether the caravan arrived.", "sourceIndices", new[] { 1 }),
                TestDict("text", unicode, "sourceIndices", new[] { 2, 3 }),
                TestDict("text", "Player said: \"tomorrow\" is possible.", "sourceIndices", new[] { 3 }) };
            response["content"] = Json.Serialize(TestDict("digest", entries));
            string Frame(string text) => "Published scene framing: " + text;
            var packed = DialogueService.BuildMiddleTermSummary(settings, "npc", "conversation:fixture", rows, responder: Respond, publicationText: Frame);
            var packing = ReadDictionary(ReadDictionary(packed, "compactionDiagnostic"), "packing");
            string packedText = ReadString(packed, "summary", "");
            var repeated = DialogueService.BuildMiddleTermSummary(settings, "npc", "conversation:fixture", rows, responder: Respond, publicationText: Frame);
            Check("serialized_unicode_framing_packs_deterministically", packedText.Contains("[records 1] NPC asked")
                && packedText.Contains("[records 3] Player said: \"tomorrow\" is possible.") && !packedText.Contains(unicode)
                && packedText == ReadString(repeated, "summary", "")
                && Json.Serialize(packing) == Json.Serialize(ReadDictionary(ReadDictionary(repeated, "compactionDiagnostic"), "packing"))
                && Json.Serialize(packing["selectedSourceIndices"]) == "[1,3]" && Json.Serialize(packing["omittedSourceIndices"]) == "[2]"
                && Json.Serialize(packing["omittedEntryIndices"]) == "[2]" && ReadInt(packing, "publishedEntryCount", 0) == 2
                && ReadInt(packing, "serializedCharacters", 0) == Json.Serialize(Frame(packedText)).Length
                && Json.Serialize(Frame(packedText)).Length <= 1800 && ReadString(packed, "interpretationStatus", "") == "unverified");
            // Framing alone can push otherwise admissible text over the exact boundary.
            var onlyFirst = TestDict("digest", new[] { entries[0] });
            int boundary = Json.Serialize(Frame(packedText.Split('\n')[0] + "\n[records 1] NPC asked whether the caravan arrived.")).Length;
            var exact = AcceptAttributedMemoryDigest(onlyFirst, rows, new() { new string('a', 3000) }, new(), boundary, out _, Frame);
            var overByOne = AcceptAttributedMemoryDigest(onlyFirst, rows, new() { new string('a', 3000) }, new(), boundary - 1, out _, Frame);
            Check("final_framed_json_exact_boundary", exact != null && overByOne == null);

            response["content"] = Json.Serialize(TestDict("digest", new[] { entries[0],
                TestDict("text", new string('z', 4000), "sourceIndices", new[] { 2, 2 }), entries[2] }));
            var malformedOversized = DialogueService.BuildMiddleTermSummary(settings, "npc", "conversation:fixture", rows, responder: Respond);
            Check("malformed_would_be_omission_rejects_entire_candidate", ReadString(malformedOversized, "summary", "") == retained
                && ReadString(ReadDictionary(malformedOversized, "compactionDiagnostic"), "reason", "") == "digest_invalid_source_reference");
            response["content"] = "{\"digest\":[{\"text\":\"Short valid entry\",\"sourceIndices\":[1]},"
                + "{\"text\":\"" + new string('z', 4000) + "\",\"sourceIndices\":[0],\"sourceIndices\":[2]}]}";
            var duplicateMember = DialogueService.BuildMiddleTermSummary(settings, "npc", "conversation:fixture", rows, responder: Respond);
            Check("raw_duplicate_digest_member_rejects_entire_candidate",
                ReadString(duplicateMember, "summary", "") == retained
                && ReadString(duplicateMember, "compactionStatus", "") == "incomplete"
                && ReadString(ReadDictionary(duplicateMember, "compactionDiagnostic"), "reason", "") == "duplicate_digest_member");
            response["content"] = Json.Serialize(TestDict("digest", new[] { TestDict("text", new string('z', 4000), "sourceIndices", new[] { 2 }) }));
            var sourceExtract = DialogueService.BuildMiddleTermSummary(settings, "npc", "conversation:fixture", rows, responder: Respond);
            Check("oversized_digest_can_fall_back_to_complete_source_records", ReadString(sourceExtract, "summary", "") == ReadString(accepted, "summary", "")
                && Json.Serialize(ReadDictionary(ReadDictionary(sourceExtract, "compactionDiagnostic"), "packing")["omittedEntryIndices"]) == "[1]");
            string omittedArcSource = new string('a', 65000);
            var arcScenes = new List<Dictionary<string, object>>
            {
                TestDict("summary_id", "scene_one", "ts", 1L, "summary", "Prior short selection.",
                    "payload_json", Json.Serialize(TestDict("method", "source_bound_selection",
                        "originalSourceRecords", new[] { "An exact original record.", omittedArcSource }))),
                TestDict("summary_id", "scene_two", "ts", 2L, "summary", "Second record."),
                TestDict("summary_id", "scene_three", "ts", 3L, "summary", "Third record."),
                TestDict("summary_id", "scene_four", "ts", 4L, "summary", "Fourth record.")
            };
            int beforeArc = calls;
            string arcScope = "arc:npc:player:fixture";
            var arc = DialogueService.BuildMiddleTermSummary(settings, "npc", arcScope, arcScenes,
                responder: Respond, publicationText: Frame);
            string arcText = ReadString(arc, "summary", "");
            Check("archive_above_provider_input_limit_keeps_previously_local_arc_provider_free",
                calls == beforeArc && ReadString(arc, "compactionStatus", "") == "partial"
                && ReadString(ReadDictionary(arc, "compactionDiagnostic"), "reason", "") == "expanded_arc_sources_previously_fit_locally"
                && arcText.Contains("An exact original record.") && !arcText.Contains("Prior short selection.")
                && ReadStringList(arc, "originalSourceRecords").Contains(omittedArcSource)
                && Json.Serialize(Frame(arcText)).Length <= 1800);
            var disabledArcSettings = new Dictionary<string, object>(settings) { ["useMemoryLlmForConsolidation"] = false };
            var disabledLocalArc = DialogueService.BuildMiddleTermSummary(disabledArcSettings, "npc", arcScope, arcScenes,
                responder: Respond, publicationText: Frame);
            Check("disabled_generation_preserves_bounded_local_arc",
                calls == beforeArc && ReadString(disabledLocalArc, "summary", "") == arcText
                && ReadString(disabledLocalArc, "compactionStatus", "") == "partial");
            var oversizedArcScenes = Enumerable.Range(1, 4).Select(index =>
                TestDict("summary_id", "scene_" + index, "ts", (long)index, "summary", "Prior short selection.",
                    "payload_json", Json.Serialize(TestDict("method", "source_bound_selection",
                        "originalSourceRecords", new[] { new string('a', 2000 + index) })))).ToList();
            var noArc = DialogueService.BuildMiddleTermSummary(settings, "npc", arcScope, oversizedArcScenes,
                responder: Respond, publicationText: Frame);
            Check("previously_local_arc_without_fitting_records_preserves_archive_without_publication",
                calls == beforeArc && ReadString(noArc, "summary", "") == ""
                && ReadString(noArc, "compactionStatus", "") == "incomplete"
                && ReadString(ReadDictionary(noArc, "compactionDiagnostic"), "reason", "") == "expanded_local_arc_has_no_fitting_records");
            var disabledNoArc = DialogueService.BuildMiddleTermSummary(disabledArcSettings, "npc", arcScope, oversizedArcScenes,
                responder: Respond, publicationText: Frame);
            Check("disabled_generation_leaves_arc_when_no_complete_record_fits",
                calls == beforeArc && ReadString(disabledNoArc, "summary", "") == ""
                && ReadString(disabledNoArc, "compactionStatus", "") == "incomplete");
            arcScenes[0]["summary"] = new string('b', 4000);
            arcScenes[0]["payload_json"] = Json.Serialize(TestDict("method", "source_bound_selection",
                "originalSourceRecords", new[] { "An exact original record.", new string('a', 4000) }));
            response["content"] = "{\"sourceIndices\":[1]}";
            var providerArc = DialogueService.BuildMiddleTermSummary(settings, "npc", arcScope, arcScenes,
                responder: Respond, publicationText: Frame);
            Check("arc_that_already_needed_provider_keeps_normal_dispatch",
                calls == beforeArc + 1
                && ReadBool(ReadDictionary(providerArc, "compactionDiagnostic"), "providerAttempted", false));
            var legacySceneText = Enumerable.Range(1, 4)
                .Select(index => "Displayed scene " + index + " " + new string((char)('a' + index), 400)).ToList();
            var legacySceneRows = legacySceneText.Select((value, index) =>
                TestDict("summary_id", "legacy_scene_" + index, "ts", (long)index, "summary", value)).ToList();
            string missingLegacyOriginal = "Uncited original: " + new string('z', 4000);
            legacySceneRows[0]["payload_json"] = Json.Serialize(TestDict("method", "source_bound_selection",
                "originalSourceRecords", new[] { legacySceneText[0], missingLegacyOriginal }));
            response["content"] = Json.Serialize(TestDict("summary", string.Join("\n---\n", legacySceneText)));
            int beforeLegacyArc = calls;
            var legacyArc = DialogueService.BuildMiddleTermSummary(settings, "npc", arcScope, legacySceneRows,
                responder: Respond, publicationText: Frame);
            Check("legacy_arc_reply_cannot_certify_display_rows_in_place_of_reopened_originals",
                calls == beforeLegacyArc + 1
                && ReadString(legacyArc, "compactionStatus", "") == "incomplete"
                && !ReadBool(legacyArc, "candidateCoverageComplete", true)
                && ReadString(ReadDictionary(legacyArc, "compactionDiagnostic"), "reason", "") == "unsupported_summary"
                && ReadString(legacyArc, "summary", "").Contains(missingLegacyOriginal, StringComparison.Ordinal));

            const string protectedName = "\u015Aeris";
            const string protectedId = "fixture_private_hero";
            const string privateStatus = "The stranger's wealth is the secret vault keyword.";
            var unknownIdentity = BuildKnowledgeAccessContext("npc", protectedId, "",
                TestDict("mainHeroStringId", protectedId, "mainHeroName", protectedName,
                    "identityView", TestDict("knowsIdentity", false)));
            string escapedArchive = Json.Serialize(TestDict("method", "source_bound_selection",
                "originalSourceRecords", new[] { "A known public comment.", new string('p', 4000),
                    protectedName + " is the hidden canonical name.", privateStatus }))
                .Replace(protectedName, "\\u015Aeris", StringComparison.Ordinal);
            arcScenes[0]["payload_json"] = escapedArchive;
            arcScenes[0]["participants_json"] = Json.Serialize(new[] { "npc", protectedId });
            response["content"] = "{\"sourceIndices\":[1]}";
            int beforePrivateArc = calls;
            var privateArc = DialogueService.BuildMiddleTermSummary(settings, "npc", "arc:npc:" + protectedId + ":fixture",
                arcScenes, responder: Respond, publicationText: Frame, sourceKnowledge: unknownIdentity);
            string projectedPrompt = string.Join("\n", ReadDictionaryList(request, "messages")
                .Select(message => ReadString(message, "content", "")));
            Check("unknown_identity_arc_filters_decoded_archive_and_group_before_dispatch",
                calls == beforePrivateArc + 1 && unknownIdentity.PlayerIdentityUnknown
                && escapedArchive.Contains("\\u015A", StringComparison.OrdinalIgnoreCase)
                && projectedPrompt.Contains("A known public comment.", StringComparison.Ordinal)
                && !projectedPrompt.Contains(protectedName, StringComparison.OrdinalIgnoreCase)
                && !projectedPrompt.Contains(protectedId, StringComparison.OrdinalIgnoreCase)
                && !projectedPrompt.Contains("secret vault keyword", StringComparison.Ordinal)
                && !ReadStringList(privateArc, "originalSourceRecords").Contains(privateStatus)
                && ReadString(arcScenes[0], "payload_json", "") == escapedArchive);
            int beforeDisabled = calls;
            var disabled = DialogueService.BuildMiddleTermSummary(TestDict("useMemoryLlmForConsolidation", false),
                "npc", "conversation:fixture", rows, responder: Respond);
            Check("disabled_provider_retains_originals_without_packing", calls == beforeDisabled
                && ReadString(disabled, "summary", "") == retained && ReadString(disabled, "compactionStatus", "") == "incomplete");
            string localUnicode = unicode + unicode;
            var localOversize = DialogueService.BuildMiddleTermSummary(settings, "npc", "conversation:local_unicode",
                new() { TestDict("summary", localUnicode) }, responder: Respond);
            Check("serialized_local_overflow_never_triggers_a_new_provider_call", calls == beforeDisabled
                && ReadString(localOversize, "summary", "").Contains(localUnicode, StringComparison.Ordinal)
                && ReadString(ReadDictionary(localOversize, "compactionDiagnostic"), "reason", "") == "local_sources_exceed_serialized_budget");
            var derivedRow = TestDict("summary", "DO_NOT_TREAT_THIS_DIGEST_AS_AN_ORIGINAL", "payload_json", Json.Serialize(oversize));
            Check("later_compaction_uses_originals_not_previous_model_digest", !string.Join("", OriginalCompactionRecords(derivedRow, true)).Contains("DO_NOT_TREAT")
                && string.Join("", OriginalCompactionRecords(derivedRow, true)).Contains(new string('x', 3000)));
            foreach (var invalidDigest in new[] {
                TestDict("digest", new object[] { TestDict("text", "Text", "sourceIndices", new[] { 1 }), "untyped item" }),
                TestDict("digest", new[] { TestDict("text", TestDict("claim", "Object is not a claim string"), "sourceIndices", new[] { 1 }) }),
                TestDict("digest", new[] { TestDict("text", "Claim\n[records 999] Forged citation", "sourceIndices", new[] { 1 }) }),
                TestDict("digest", new[] { TestDict("text", new string('y', 2000), "sourceIndices", new[] { 1 }) }) })
                Check("malformed_digest_keeps_original_" + PromptHash(Json.Serialize(invalidDigest)).Substring(0, 8),
                    AcceptAttributedMemoryDigest(invalidDigest, rows, new() { new string('x', 3000) }, new(), 1800, out _) == null);
            var missingArchive = TestDict("summary", "Retain the available older digest.", "payload_json", Json.Serialize(TestDict("method", "model_attributed_digest")));
            int beforeMissing = calls;
            var unavailable = DialogueService.BuildMiddleTermSummary(settings, "npc", "conversation:fixture", new() { rows[0], missingArchive }, responder: Respond);
            Check("missing_digest_archive_never_silently_disappears_or_completes", calls == beforeMissing
                && ReadString(unavailable, "summary", "").Contains("Retain the available older digest.")
                && ReadString(unavailable, "compactionStatus", "") == "incomplete");
            Check("legacy_summary_does_not_become_original_speech", OriginalCompactionRecords(TestDict("summary_id", "legacy", "summary", "A prior model claim."), true)
                .Single().Contains("NOT AN ORIGINAL STATEMENT"));
            Check("input_rows_are_unchanged", CanonicalJson(rows) == before);
            return results;
        }

        private static List<Dictionary<string, object>> RunRecentMemoryWindowSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string name, bool passed, object data = null) => results.Add(TestDict(
                "caseId", "memory_precision_recent_window_" + name, "passed", passed,
                "summary", name + (!passed && data != null ? ": " + DialogueService.LimitText(data is string error ? error : Json.Serialize(data), 2400) : ""), "data", data));
            string campaign = "recent_memory_window_" + Guid.NewGuid().ToString("N");
            var context = TestDict("timelineId", "window_main", "worldDay", 30d, "playerHeroStringId", "player");
            var settings = TestDict("enableSemanticMemory", false, "enableMinime", false,
                "useMemoryLlmForConsolidation", false, "memoryPrecisionMode", "precision");
            var knowledge = DialogueService.BuildKnowledgeAccessContext("observer", "player", "", context);
            var plan = PlanMemoryQuery("observer", "player", "Continue our conversation", context);
            var priorSettings = LlmProviderService.CodexPerformanceSettings.Value;
            var envelopeSettings = new Dictionary<string, object>(settings)
            {
                ["llmProvider"] = LlmProviderService.NanoGptProvider, ["dialogueModel"] = "recent-window-fixture",
                ["eventsModel"] = "recent-window-fixture"
            };
            var certificates = TestDict();
            foreach (string requestType in new[] { "dialogue", "social_event", "correspondence" })
            {
                var capacity = ResolveConversationPromptCapacity(TestDict(), requestType, envelopeSettings);
                certificates[ReadString(capacity, "routeKey", "")] = TestDict(
                    "endpoint", PortraitsService.NanoGptChatCompletionsUrl, "model", "recent-window-fixture",
                    "contextTokens", 1000000, "maxOutputTokens", 131072,
                    "source", "isolated recent-window fixture", "validUntilUtc", DateTimeOffset.UtcNow.AddHours(1).ToString("o"));
            }
            envelopeSettings["verifiedConversationContextWindows"] = certificates;
            LlmProviderService.CodexPerformanceSettings.Value = envelopeSettings;
            try
            {
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    DialogueSelfTestStore.InsertConversationSessions18(connection, TestDict("campaign", campaign));
                    var diagnostics = TestDict();
                    var window = LoadRecentConversationWindow(connection, plan, context, knowledge, settings, diagnostics);
                    var raw = window.Where(e => e.Kind == "reported_exchange").ToList();
                    var summaries = window.Where(e => e.Kind == "recent_summary").ToList();
                    Check("latest_thirty_individual_messages", raw.Count == 30
                        && raw.First().SourceId == "window_turn_6" && raw.Last().SourceId == "window_turn_35");
                    Check("thirty_summaries_by_source_chronology", summaries.Count == 30
                        && summaries.First().SourceId == "window_summary_11" && summaries.Last().SourceId == "window_summary_40");
                    Check("duplicate_episode_does_not_consume_slot", summaries.All(e => e.SourceId != "duplicate_view")
                        && ReadInt(ReadDictionary(diagnostics, "recentMemoryWindow"), "duplicateViewsSkipped", 0) == 1);
                    string whole = string.Join("\n", window.Select(e => e.Text));
                    Check("audience_timeline_hidden_and_future_filters", !new[] { "HIDDEN-SUMMARY", "FUTURE-SUMMARY", "OTHER-BRANCH", "UNHEARD-SUMMARY" }.Any(whole.Contains));
                    Check("summaries_keep_uncertainty_and_original_authority", summaries.Count(e => e.Text.Contains(RecentSummaryPromptPolicy)) == 1
                        && RecentSummaryPromptPolicy.Contains("derived recollections, not independent proof")
                        && RecentSummaryPromptPolicy.Contains("later acknowledged corrections take precedence")
                        && summaries.All(e => e.Required));
                    var packet = SelectPrecisionEvidence(window, 2000);
                    var allocated = SelectSharedPromptRecords(packet.Text, "", 1, new List<object>());
                    string request = Json.Serialize(TestDict("messages", new[] { TestDict("role", "user", "content", NormalizePromptSegment(allocated.Context)) }));
                    Check("all_sixty_complete_records_survive_allocation", packet.Selected.Count == 60
                        && Enumerable.Range(11, 30).All(i => request.Contains("END-SUMMARY-" + i))
                        && packet.Selected.All(e => allocated.Context.Contains(e.Text)), ReadDictionary(diagnostics, "recentMemoryWindow"));
                    var profile = TestDict("name", "Observer", "heroStringId", "observer", "age", 30, "isNoble", true);
                    var identity = TestDict("identityState", "verified", "canonicalNameAllowed", true, "usableName", "Michael");
                    foreach (string route in new[] { "dialogue", "party_chat", "event", "correspondence" })
                    {
                        var payload = new Dictionary<string, object>(context) { ["interactionMode"] = route,
                            ["mainHeroStringId"] = "player", ["mainHeroName"] = "Michael", ["heroStringId"] = "observer" };
                        PromptEnvelope envelope;
                        if (route == "correspondence")
                            envelope = BuildCorrespondencePromptEnvelope(campaign, "observer", "Observer", "player", "Michael", 30d,
                                "Continue our conversation", profile, TestDict(), TestDict(), packet.Text, payload);
                        else if (route == "event")
                            envelope = BuildEventPromptEnvelope(campaign, "window_event", "observer", "Observer", "Michael", "Michael",
                                "Continue our conversation", "A quiet meeting.", payload, profile, TestDict(), TestDict(), TestDict(), TestDict(),
                                new(), new(), new(), new(), identity, precomputedMemoryPacket: packet.Text, precomputedGroupConversationPrompt: "");
                        else
                            envelope = BuildDialoguePromptEnvelope(campaign, "observer", "Observer", "Michael", "Michael",
                                "Continue our conversation", "A quiet meeting.", profile, TestDict(), TestDict(), TestDict(), TestDict(),
                                new(), new(), new(), new(), identity, payload, precomputedMemoryPacket: packet.Text, precomputedGroupConversationPrompt: "");
                        string actual = string.Join("\n", envelope.Messages.Select(m => ReadString(m, "content", "")));
                        Check("actual_provider_envelope_complete_" + route, packet.Selected.All(e => actual.Contains(NormalizePromptSegment(e.Text))),
                            TestDict("characters", actual.Length, "missing", packet.Selected.Where(e => !actual.Contains(NormalizePromptSegment(e.Text))).Select(e => e.SourceId).ToList()));
                        var preflight = ReadDictionary(envelope.Diagnostics, "continuityPreflight");
                        var body = TestDict("model", "recent-window-fixture", "messages", envelope.Messages,
                            "max_tokens", ReadInt(preflight, "outputReserve", 8192));
                        var final = BuildContinuityProviderPreflight(envelopeSettings,
                            TestDict("promptEnvelope", envelope.Diagnostics), body, PortraitsService.NanoGptChatCompletionsUrl, "recent-window-fixture");
                        Check("final_provider_capacity_and_content_" + route, ReadBool(final, "fits", false)
                            && ReadBool(final, "capacityVerified", false) && packet.Selected.All(e => actual.Contains(NormalizePromptSegment(e.Text))), final);
                    }
                    var tooSmall = CreatePromptEnvelope("dialogue", "recent_window_capacity", "rules", "character", packet.Text);
                    string beforeCapacityCheck = Json.Serialize(tooSmall.Messages);
                    FinalizeContinuityPromptBudget(tooSmall, TestDict(), TestDict("inputAllowance", 2000, "outputFits", true));
                    var smallPreflight = ReadDictionary(tooSmall.Diagnostics, "continuityPreflight");
                    Check("insufficient_capacity_never_silently_drops_window", !ReadBool(smallPreflight, "fits", true)
                        && ReadBool(smallPreflight, "capacityRejectionDeferredToProviderPreflight", false)
                        && Json.Serialize(tooSmall.Messages) == beforeCapacityCheck
                        && packet.Selected.All(e => ReadString(tooSmall.Messages.Last(), "content", "").Contains(NormalizePromptSegment(e.Text))));
                    var audit = PrecisionEvidenceAudit(packet, plan, "precision");
                    Check("actual_serialized_content_not_loaded_ids", MissingPrecisionEvidence(request, audit, true).Count == 0
                        && MissingPrecisionEvidence(Json.Serialize(window.Select(e => e.SourceId)), audit, true).Count == 60);
                    var supplied = raw.ToDictionary(e => e.SourceId, e => ReadString(e.SourceTurns.Single(), "hash", ""), StringComparer.Ordinal);
                    var deduped = SelectPrecisionEvidence(window, 2000, supplied);
                    Check("already_present_exact_raw_not_duplicated", deduped.Selected.Count == 30 && deduped.Selected.All(e => e.Kind == "recent_summary"));
                    supplied["window_turn_35"] = "changed-text";
                    Check("changed_raw_hash_cannot_hide_correction", SelectPrecisionEvidence(window, 2000, supplied).Text.Contains("no payment has occurred"));
                    using (var archiveFixture = connection.BeginTransaction())
                    {
                        string retainedArchive = "Michael: I first suggested 900 for the caravan.\n---\n"
                            + new string('x', 29510) + "\n---\nObserver: 300, only after arrival.\n---\nMichael: I accept 300 only after arrival. No payment has occurred.";
                        DialogueSelfTestStore.InsertSummaries2(connection, TestDict("archive", retainedArchive));
                        var compactDiagnostics = TestDict();
                        var compactWindow = LoadRecentConversationWindow(connection, plan, context, knowledge, settings, compactDiagnostics);
                        var archived = compactWindow.Single(e => e.SourceId == "retained_archive");
                        Check("large_fallback_does_not_replace_compact_episode_or_fill_prompt", compactWindow.Any(e => e.SourceId == "window_summary_40")
                            && compactWindow.All(e => e.SourceId != "oversized_duplicate")
                            && archived.Text.Length < 2600 && !archived.Text.Contains(new string('x', 80))
                            && archived.Text.Contains("300 only after arrival") && archived.Text.Contains("No payment has occurred")
                            && compactWindow.Count(e => e.Kind == "recent_summary") == 30, compactDiagnostics);
                        var compactPacket = SelectPrecisionEvidence(compactWindow, 2000);
                        var compactAllocated = SelectSharedPromptRecords(compactPacket.Text, "", 1, new List<object>());
                        Check("compact_summary_source_view_survives_final_allocation", compactAllocated.Context.Contains(archived.Text)
                            && !compactAllocated.Context.Contains(new string('x', 80))
                            && compactPacket.Selected.All(e => compactAllocated.Context.Contains(e.Text)));
                        Check("read_time_compaction_never_rewrites_archive", ReadString(DialogueSelfTestStore.SelectSummaries5(connection).Single(), "summary", "") == retainedArchive);

                        DialogueSelfTestStore.UpdateConversationTurns49(connection);
                        for (int i = 1; i <= 3; i++)
                            LinkMemorySource(connection, "summary", "retained_archive", "turn", "window_turn_" + i, i);
                        var oldPlan = PlanMemoryQuery("observer", "player", "What price did we agree for the caravan?", context);
                        var retainedRow = DialogueSelfTestStore.SelectSummaries6(connection).Single();
                        var expanded = ExpandPrecisionSource(connection, "summaries", retainedRow, oldPlan, knowledge, 99);
                        var linkedPacket = SelectPrecisionEvidence(compactWindow.Concat(expanded), 2000);
                        Check("compact_archive_still_resolves_original_old_question_answer_acknowledgment", expanded.Count > 0
                            && linkedPacket.Text.Contains("What are the caravan price and payment conditions?")
                            && linkedPacket.Text.Contains("300 only after arrival. No payment has occurred.")
                            && linkedPacket.Text.Contains("I acknowledge those terms")
                            && expanded.SelectMany(e => e.SourceTurns).Any(t => ReadString(t, "speakerId", "") == "player")
                            && expanded.SelectMany(e => e.SourceTurns).Any(t => ReadString(t, "speakerId", "") == "observer"));

                        var olderAnswer = new MemoryEvidence { SourceId = "old_verified_answer", SourceHash = "old-source",
                            Kind = "reported_exchange", Owner = "observer", Score = 99,
                            Text = "Observer asked the caravan price. Michael answered 300 only after arrival. Observer acknowledged this; no payment occurred." };
                        var withOlder = SelectPrecisionEvidence(compactWindow.Concat(new[] { olderAnswer }), 2000);
                        Check("protected_history_does_not_starve_relevant_older_answer", withOlder.Tokens > 8000
                            && withOlder.Selected.Any(e => e.SourceId == olderAnswer.SourceId)
                            && withOlder.Text.Contains(olderAnswer.Text));
                        archiveFixture.Rollback();
                    }
                    var restricted = new Dictionary<string, object>(context) { ["officialMemoryFirewall"] = true };
                    Check("official_route_firewall", LoadRecentConversationWindow(connection, plan, restricted, knowledge, settings, TestDict()).Count == 0);
                    var unknown = DialogueService.BuildKnowledgeAccessContext("observer", "player", "", context); unknown.PlayerIdentityUnknown = true;
                    Check("unknown_identity_firewall", LoadRecentConversationWindow(connection, plan, context, unknown, settings, TestDict()).Count == 0);
                    var early = DialogueService.BuildKnowledgeAccessContext("observer", "player", "", context); early.AsOfTimestamp = 30;
                    var historical = LoadRecentConversationWindow(connection, plan, context, early, settings, TestDict());
                    Check("asof_never_sees_later_sources_or_rewrites", historical.All(e => e.Kind != "recent_summary")
                        && !historical.Any(e => e.Text.Contains("no payment has occurred")));
                }
                foreach (string mode in new[] { "precision", "legacy" })
                {
                    var options = new Dictionary<string, object>(settings) { ["memoryPrecisionMode"] = mode };
                    var packet = DialogueService.BuildNpcMemoryPacket(campaign, "observer", "player", "", "Continue our conversation", 2000, context, options);
                    string text = ReadString(packet, "memoryPacket", "");
                    Check("shared_packet_window_" + mode, Enumerable.Range(11, 30).All(i => text.Contains("END-SUMMARY-" + i))
                        && text.Contains("no payment has occurred"));
                }
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var priorLines = DialogueSelfTestStore.SelectConversationTurns60(connection)
                        .Select(row => TestDict("turnId", ReadString(row, "turn_id", ""), "exchangeId", ReadString(row, "exchange_id", ""),
                            "speakerId", ReadString(row, "speaker_id", ""), "speaker", ReadString(row, "speaker_name", ""),
                            "role", ReadString(row, "role", ""), "text", ReadString(row, "text", ""))).ToList();
                    foreach (string route in new[] { "dialogue", "party_chat", "event" })
                    {
                        var payload = new Dictionary<string, object>(context) { ["interactionMode"] = route,
                            ["mainHeroStringId"] = "player", ["mainHeroName"] = "Michael", ["heroStringId"] = "observer" };
                        // Exercise the actual adapter shapes: event JSONL id is not
                        // the durable turn key, and turnId is the shared exchange.
                        var routedLines = priorLines.Select(CloneDictionary).ToList();
                        if (route != "dialogue")
                        {
                            foreach (var line in routedLines)
                            {
                                line["id"] = "jsonl_" + ReadString(line, "turnId", "");
                                line["turnId"] = ReadString(line, "exchangeId", "");
                                line["speakerHeroStringId"] = ReadString(line, "speakerId", "");
                                line["sequence"] = routedLines.IndexOf(line) + 1;
                            }
                            if (route == "party_chat")
                            {
                                payload["groupTranscript"] = routedLines;
                                routedLines = DialogueService.BuildPartyChatPromptTranscript(payload, "Continue our conversation");
                            }
                            else routedLines = DialogueService.BuildSocialEventPromptTranscript(routedLines, 30);
                        }
                        BindPromptTranscriptSources(campaign, "observer", payload, routedLines);
                        var memory = DialogueService.BuildNpcMemoryPacket(campaign, "observer", "player", "", "Continue our conversation", 2000, payload, settings);
                        string memoryText = ReadString(memory, "memoryPacket", "");
                        var profile = TestDict("name", "Observer", "heroStringId", "observer", "age", 30, "isNoble", true);
                        var foundation = TestDict("background", TestDict("centralWound", "PRIVATE_WOUND_FIXTURE", "publicReputation", "REPUTATION_FIXTURE"));
                        var identity = TestDict("identityState", "verified", "canonicalNameAllowed", true, "usableName", "Michael");
                        var envelope = route == "event"
                            ? BuildEventPromptEnvelope(campaign, "window_event", "observer", "Observer", "Michael", "Michael",
                                "Continue our conversation", "A quiet meeting.", payload, profile, foundation, TestDict(), TestDict(), TestDict(),
                                routedLines, new(), new(), new(), identity, precomputedMemoryPacket: memoryText, precomputedGroupConversationPrompt: "")
                            : BuildDialoguePromptEnvelope(campaign, "observer", "Observer", "Michael", "Michael",
                                "Continue our conversation", "A quiet meeting.", profile, foundation, TestDict(), TestDict(), TestDict(),
                                routedLines, new(), new(), new(), identity, payload, precomputedMemoryPacket: memoryText, precomputedGroupConversationPrompt: "");
                        string actual = string.Join("\n", envelope.Messages.Select(m => ReadString(m, "content", "")));
                        Check("own_foundation_reaches_actual_envelope_once_" + route,
                            actual.Split("PRIVATE_WOUND_FIXTURE").Length == 2 && actual.Split("REPUTATION_FIXTURE").Length == 2);
                        Check("deduplicated_transcript_window_survives_actual_envelope_" + route,
                            routedLines.TakeLast(30).All(line => actual.Contains(NormalizePromptSegment(ReadString(line, "text", ""))))
                            && Enumerable.Range(11, 30).All(i => actual.Contains("END-SUMMARY-" + i))
                            && !memoryText.Contains("no payment has occurred"), TestDict("characters", actual.Length,
                                "missingRaw", routedLines.TakeLast(30).Where(line => !actual.Contains(NormalizePromptSegment(ReadString(line, "text", "")))).Select(line => ReadString(line, "turnId", "")).ToList(),
                                "missingSummaries", Enumerable.Range(11,30).Where(i => !actual.Contains("END-SUMMARY-" + i)).ToList(),
                                "memoryDuplicatesFinalRaw", memoryText.Contains("no payment has occurred")));
                    }
                    using var bindingFixture = connection.BeginTransaction();
                    DialogueSelfTestStore.InsertConversationSessions19(connection, TestDict("campaign", campaign));
                    // Bind through a second connection only after publishing the fixture.
                    bindingFixture.Commit();
                    var bindingContext = CloneDictionary(context);
                    bindingContext["playerHeroStringId"] = "player";
                    bindingContext["sceneTurnId"] = "binding_exchange";
                    bindingContext["identityView"] = TestDict("knowsIdentity", true);
                    PlayerInputSources.Add(bindingContext, new AcceptedPlayerInputSource { Original = "Yes.", SourceId = "input_current" });
                    var repeatedReplies = Enumerable.Range(489, 32).Select(n => TestDict("id", "jsonl_" + n,
                        "sourceTurnId", "binding_" + n, "turnId", "binding_exchange", "role", "player", "speakerId", "player", "text", "Yes.")).ToList();
                    repeatedReplies.Add(TestDict("id", "binding_520", "sourceTurnId", "stale_durable", "turnId", "binding_exchange",
                        "role", "player", "speakerId", "player", "text", "Yes."));
                    BindPromptTranscriptSources(campaign, "observer", bindingContext, repeatedReplies);
                    Check("repeated_reply_uses_stored_input_link_not_equal_text", repeatedReplies.Count == 32
                        && repeatedReplies.Any(line => ReadString(line, "sourceTurnId", "") == "binding_519")
                        && repeatedReplies.All(line => ReadString(line, "sourceTurnId", "") != "binding_520")
                        && PlannedPromptSources(bindingContext).ContainsKey("binding_520"));
                    Check("newest_sources_survive_more_than_512_candidates", ReadInt(ReadDictionary(bindingContext, "promptSourceBinding"), "boundTranscriptTurns", 0) == 31
                        && ReadInt(ReadDictionary(bindingContext, "promptSourceBinding"), "unboundTranscriptTurns", -1) == 1);
                    Check("final_tail_after_input_removal_is_fully_covered", repeatedReplies.TakeLast(RequiredConversationTailCount)
                        .Where(line => ReadString(line, "sourceTurnId", "").StartsWith("binding_"))
                        .All(line => PlannedPromptSources(bindingContext).ContainsKey(ReadString(line, "sourceTurnId", ""))));
                    Check("storage_ids_survive_binding_and_unbound_collision_is_not_removed", ReadString(repeatedReplies[0], "id", "") == "jsonl_489"
                        && repeatedReplies.Any(line => ReadString(line, "id", "") == "binding_520"));
                    var unknownBinding = CloneDictionary(bindingContext); unknownBinding["identityView"] = TestDict("knowsIdentity", false);
                    BindPromptTranscriptSources(campaign, "observer", unknownBinding, repeatedReplies);
                    Check("unknown_identity_does_not_export_raw_statement_ids", PlannedPromptSources(unknownBinding).Count == 0
                        && PromptStatementSources.TryGetValue(unknownBinding, out var unknownStatements) && unknownStatements.Count == 0);
                }
            }
            catch (Exception ex) { Check("fixture_completed", false, ex.ToString()); }
            finally { LlmProviderService.CodexPerformanceSettings.Value = priorSettings; ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }

    }
}
