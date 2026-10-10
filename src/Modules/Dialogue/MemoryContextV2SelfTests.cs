using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunMemoryPrecisionSelfTests()
        {
            if (string.IsNullOrWhiteSpace(CampaignsRootOverride.Value))
                throw new InvalidOperationException("Memory precision contracts require the isolated Verification Lab.");
            var results = new List<Dictionary<string, object>>();
            Action<string, bool, object> add = (id, pass, evidence) => results.Add(TestDict("caseId", "memory_precision_" + id,
                "passed", pass, "summary", id, "data", evidence));
            string campaign = "memory_precision_test_" + Guid.NewGuid().ToString("N");
            var observer = new KnowledgeAccessContext { NpcId = "npc_b", PlayerId = "player", TimelineId = "timeline_a", WorldDay = 30 };
            var settings = TestDict("useMemoryLlmForConsolidation", false, "enableSemanticMemory", false,
                "memoryPrecisionMode", "precision", "enableSemanticMemory", false);
            var taggedSources = new List<Dictionary<string, object>> {
                TestDict("ts", 1L, "summary", "Player said or visibly did: We discussed the emperor's passing.",
                    "tags_json", "[\"dialogue\",\"grief\",\"arenicos\"]"),
                TestDict("ts", 2L, "summary", "The speaker explained her grief and trust.",
                    "tags_json", "[\"personal_disclosure\",\"grief\",\"trust\"]") };
            string taggedOriginal = CanonicalJson(taggedSources);
            var taggedDigest = BuildMiddleTermSummary(settings, "npc_b", "conversation:tag_fixture", taggedSources);
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
            var qualified = TryExtractMinimeTopics(settings, "Not intimate.", 5);
            add("topic_hints_do_not_drop_explicit_negation", qualified.Contains("not_intimate")
                && !qualified.Contains("intimate") && !qualified.Contains("intimacy"), qualified);
            var lateQuestionTopics = TryExtractMinimeTopics(settings,
                string.Concat(Enumerable.Repeat("He sips slowly beside the fire. ", 150))
                    + "What does Empress Narea plan for the tournament?", 3);
            add("topic_query_retains_late_entities_and_subject_after_long_narration",
                lateQuestionTopics.Contains("narea") && lateQuestionTopics.Contains("tournament"), lateQuestionTopics);
            add("empty_sources_do_not_invent_topics", BuildMemorySummaryTopics(Array.Empty<Dictionary<string, object>>(), 5).Count == 0, null);
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

            using (var connection = OpenCampaignConnection(campaign))
            {
                ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('group_source',$campaign,'npc_a','player','closed',1,10,'[""npc_a"",""npc_b"",""player""]','{""timelineId"":""timeline_a""}');", TestDict("campaign", campaign));
                string[] texts = { "Would you take 900 denars?", "No. 300 denars only after the caravan arrives.", "I accept 300 denars after arrival. Nothing has been paid." };
                for (int i = 0; i < texts.Length; i++)
                {
                    ExecuteSql(connection, @"INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,world_day,ts,participants_json,payload_json)
VALUES($id,'group_source',$event,$order,'agreement_exchange',$role,$speaker,$speaker,$text,20,$ts,'[""npc_a"",""npc_b"",""player""]','{""timelineId"":""timeline_a""}');",
                        TestDict("id", "source_" + i, "event", "event_" + i, "order", i + 1, "role", i == 1 ? "npc" : "player", "speaker", i == 1 ? "npc_b" : "player", "text", texts[i], "ts", i + 1));
                    ExecuteSql(connection, "INSERT INTO conversation_turn_fts(turn_id,text,speaker,participants,channel) VALUES($id,$text,'npc_b','npc_a npc_b player','party_chat');", TestDict("id", "source_" + i, "text", texts[i]));
                }
                var plan = PlanMemoryQuery("npc_b", "player", "What price did we agree for the caravan?", context);
                var exchanges = FindPrecisionExchanges(connection, plan, observer, 8);
                add("non_primary_group_participant_exact_recall", exchanges.Any(e => e.Text.Contains("300 denars") && e.Text.Contains("Nothing has been paid")), exchanges.Select(e => e.SourceId).ToList());
                var exact = SearchExactConversationHistory(connection, "npc_b", "caravan price", TestDict(), 24000, null, "current_session", 30, observer);
                add("legacy_exact_route_includes_secondary_participant", ReadString(exact, "text", "").Contains("300 denars"), exact);
                var stranger = new KnowledgeAccessContext { NpcId = "npc_b_child", WorldDay = 30, TimelineId = "timeline_a" };
                add("similar_identifier_is_not_participant", FindPrecisionExchanges(connection, PlanMemoryQuery("npc_b_child", "player", "caravan", context), stranger, 8).Count == 0, null);
                var early = new KnowledgeAccessContext { NpcId = "npc_b", WorldDay = 19, TimelineId = "timeline_a" };
                var earlyPlan = PlanMemoryQuery("npc_b", "player", "caravan", TestDict("worldDay", 19d, "timelineId", "timeline_a"));
                add("future_exchange_unavailable", FindPrecisionExchanges(connection, earlyPlan, early, 8).Count == 0, null);
                var otherTimeline = PlanMemoryQuery("npc_b", "player", "caravan", TestDict("worldDay", 30d, "timelineId", "timeline_b"));
                add("other_timeline_unavailable", FindPrecisionExchanges(connection, otherTimeline, new KnowledgeAccessContext { NpcId = "npc_b", WorldDay = 30, TimelineId = "timeline_b" }, 8).Count == 0, null);

                ExecuteSql(connection, @"INSERT INTO memories(memory_id,owner_id,ts,summary,known_by_json,visibility,status)
SELECT 'unrelated_' || n,'npc_other',100+n,'caravan unrelated private detail','[""npc_other""]','private','active' FROM generate_series(1,2000) AS n;
INSERT INTO memories(memory_id,owner_id,ts,summary,known_by_json,visibility,status)
VALUES('eligible_old','npc_b',1,'300 denars only after arrival','[""npc_b""]','private','active');");
                add("unrelated_rows_cannot_exhaust_candidate_limit", QueryKnownRows(connection, "memories", observer, "status='active'", 4).Any(r => ReadString(r, "memory_id", "") == "eligible_old"), null);
                InsertMemoryFts(connection, "eligible_old", "300 denars caravan caravan agreement arrival", new List<string>(), new List<string>());
                add("eligible_ranked_lexical_source_found", SearchMemoryFts(connection, new List<string> { "caravan" }, 1, observer).Contains("eligible_old"), null);
                using(var fixture=connection.BeginTransaction())
                {
                    ExecuteSql(connection,@"INSERT INTO beliefs(belief_id,believer_id,claim,ts)
SELECT 'belief_unrelated_'||n,'npc_b','Unrelated apples and oranges.',100+n FROM generate_series(1,2000) n;
INSERT INTO beliefs(belief_id,believer_id,claim,ts) VALUES('old_relevant_belief','npc_b','I believe the caravan will arrive late.',1);");
                    add("owned_belief_relevance_precedes_candidate_limit",ReadString(QueryKnownRows(connection,"beliefs",observer,"",1,new List<string>{"caravan"}).Single(),"belief_id","")=="old_relevant_belief",null);
                    fixture.Rollback();
                }
                ExecuteSql(connection, "UPDATE memories SET hidden_from_json='[\"npc_b\"]' WHERE memory_id='eligible_old';");
                add("hidden_overrides_ownership", QueryKnownRows(connection, "memories", observer, "status='active'", 4).Count == 0
                    && SearchMemoryFts(connection, new List<string> { "caravan" }, 1, observer).Count == 0, null);

                var assertion = TestDict("subjectId", "npc_b", "predicate", "price", "objectId", "900", "claim", "Caravan price is 900.",
                    "believer", "npc_b", "cardinality", "one", "agreementId", "caravan", "timelineId", "timeline_a", "worldDay", 20d,
                    "sourceAcceptedText", "Caravan price is 900.", "evidenceQuote", "price is 900", "confidence", 0.8);
                string first, second;
                using (var transaction = connection.BeginTransaction()) { first = UpsertTemporalAssertion(connection, campaign, "event_1", "belief_1", "belief", assertion, 100); transaction.Commit(); }
                assertion["objectId"] = "300"; assertion["claim"] = "Caravan price is 300 only after arrival.";
                assertion["sourceAcceptedText"] = assertion["claim"]; assertion["evidenceQuote"] = "300 only after arrival"; assertion["worldDay"] = 25d;
                using (var transaction = connection.BeginTransaction()) { second = UpsertTemporalAssertion(connection, campaign, "event_2", "belief_2", "belief", assertion, 200); transaction.Commit(); }
                add("changed_terms_link_revision", ReadString(QuerySql(connection, "SELECT * FROM temporal_knowledge_assertions WHERE assertion_id=$id;", TestDict("id", second)).Single(), "supersedes_assertion_id", "") == first, null);
                add("current_terms_select_new_version", LoadTemporalKnowledgeForPrompt(connection, campaign, observer, new List<string> { "caravan" }, 4).Single().TryGetValue("assertion_id", out var latest) && Convert.ToString(latest) == second, null);
                var pastObserver = new KnowledgeAccessContext { NpcId = "npc_b", TimelineId = "timeline_a", WorldDay = 22 };
                add("as_of_query_selects_old_terms", ReadString(LoadTemporalKnowledgeForPrompt(connection, campaign, pastObserver, new List<string> { "caravan" }, 4).Single(), "assertion_id", "") == first, null);
                add("private_belief_never_shared_truth", LoadTemporalKnowledgeForPrompt(connection, campaign, new KnowledgeAccessContext { NpcId = "npc_a", TimelineId = "timeline_a", WorldDay = 30 }, new List<string> { "caravan" }, 4).Count == 0, null);
                using(var fixture=connection.BeginTransaction())
                {
                    ExecuteSql(connection,@"INSERT INTO temporal_knowledge_assertions(assertion_id,campaign_id,fact_key,claim,perspective_owner_id,assertion_kind,visibility,payload_json,timeline_id,event_day,learned_day,valid_from_ts,observed_ts)
SELECT 'regional_'||n,$campaign,'regional_'||n,'caravan caravan caravan caravan','npc_other','reported','public_settlement','{""settlementId"":""other_town""}','timeline_a',20,29,100,100 FROM generate_series(1,200) n;",TestDict("campaign",campaign));
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
                add("stale_worker_cannot_finish", ReadString(QuerySql(connection, "SELECT * FROM memory_background_jobs WHERE job_id=$id;", TestDict("id", job)).Single(), "status", "") == "running", null);
                RetryMemoryJob(connection, job, "worker_b", 4, "exhausted fixture", TestDict());
                add("exhaustion_observable_not_completed", ReadString(QuerySql(connection, "SELECT * FROM memory_background_jobs WHERE job_id=$id;", TestDict("id", job)).Single(), "status", "") == "incomplete", null);
                add("same_source_job_is_idempotent", job == EnqueueMemoryJob(connection, campaign, "scene_summary", "npc_a", "scene:group_source", "group_source"), null);
                add("exhausted_source_not_resurrected", ClaimMemoryJob(connection, job, "worker_c", now + 5000) == null, null);

                var invalidCompaction = EvaluateMemoryCompactionResponse(TestDict("ok", true, "content", "{}", "finishReason", "length"),
                    sourceRows, raw, new(), 1800, false);
                string stableJob = EnqueueMemoryJob(connection, campaign, "scene_summary", "npc_a", "scene:stable_compaction", "group_source");
                ClaimMemoryJob(connection, stableJob, "stable_worker", now);
                CompleteMemoryCompactionJob(connection, stableJob, "stable_worker", 1, "scene_summary", invalidCompaction);
                var stableRow = QuerySql(connection, "SELECT * FROM memory_background_jobs WHERE job_id=$id;", TestDict("id", stableJob)).Single();
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
                var transientRow = QuerySql(connection, "SELECT * FROM memory_background_jobs WHERE job_id=$id;", TestDict("id", transientJob)).Single();
                add("compaction_transport_failure_retains_bounded_backoff", ReadString(transientRow, "status", "") == "failed"
                    && ReadLong(transientRow, "next_attempt_ts", 0) > now
                    && ClaimMemoryJob(connection, transientJob, "early_worker", now + 1) == null, null);
                for (int attempt = 2; attempt <= 4; attempt++)
                {
                    var claimed = ClaimMemoryJob(connection, transientJob, "later_worker", now + attempt * 5000);
                    CompleteMemoryCompactionJob(connection, transientJob, "later_worker", ReadInt(claimed, "attempt_count", 0), "scene_summary", transientCompaction);
                }
                var exhaustedRow = QuerySql(connection, "SELECT * FROM memory_background_jobs WHERE job_id=$id;", TestDict("id", transientJob)).Single();
                add("compaction_transient_exhaustion_remains_incomplete", ReadString(exhaustedRow, "status", "") == "incomplete"
                    && ReadInt(exhaustedRow, "attempt_count", 0) == 4
                    && ClaimMemoryJob(connection, transientJob, "extra_worker", now + 50000) == null, null);

                var stage = BuildMemoryStagingManifest(connection, TestDict("campaignId", campaign, "timelineId", "timeline_a"));
                add("staging_preserves_complete_sources", ReadDictionaryList(stage, "operations").Count == 1
                    && ReadString(ReadDictionary(ReadDictionaryList(stage, "operations")[0], "prepared"), "summary", "").Contains(texts.Last()), stage["coverage"]);
                using (var preview = connection.BeginTransaction())
                {
                    var receipt = RepairConversationContinuity(connection, stage);
                    add("staged_apply_derivatives_only", ReadBool(receipt, "ok", false) && QuerySql(connection, "SELECT * FROM conversation_turns;").Count == 3, receipt);
                    preview.Rollback();
                }
                add("preview_rolls_back_publication", QuerySql(connection, "SELECT * FROM summaries WHERE source='precision_staging';").Count == 0, null);
                var bad = ReadDictionary(ReadDictionaryList(stage, "operations")[0], "prepared");
                bad["summary"] = "900 has been paid.";
                bool tampered = false;
                using (var preview = connection.BeginTransaction())
                { try { RepairConversationContinuity(connection, stage); } catch (InvalidOperationException) { tampered = true; } }
                add("staging_rejects_changed_claims", tampered, null);
                using(var fixture=connection.BeginTransaction())
                {
                    ExecuteSql(connection,@"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
SELECT 'recall_probe_'||n,$campaign,'npc_b','player','closed',100+n,200+n,'[""npc_b"",""player""]','{""timelineId"":""timeline_a""}' FROM generate_series(1,160) n;
INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,text,world_day,ts,participants_json,payload_json)
SELECT 'probe_turn_'||n,'recall_probe_'||n,'probe_event_'||n,1,'probe_exchange_'||n,'player','player','Recall our last conversation about the caravan.',21,200+n,'[""npc_b"",""player""]','{""timelineId"":""timeline_a""}' FROM generate_series(1,160) n;",TestDict("campaign",campaign));
                    var behindProbes=FindPrecisionExchanges(connection,plan,observer,1);
                    add("recall_probes_do_not_exhaust_exchange_limit",behindProbes.Any(e=>e.Text.Contains("Nothing has been paid")),null);
                    fixture.Rollback();
                }

                var packet = BuildPrecisionMemoryPacket(campaign,"npc_b","player","","What did we agree for the caravan?",context,settings,false);
                add("production_packet_exact_terms",ReadString(packet,"memoryPacket","").Contains("300 denars") && ReadString(packet,"memoryPacket","").Contains("Nothing has been paid"),packet);
                ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('foundation_cache_source',$campaign,'npc_b','player','closed',400,401,'[""npc_b"",""player""]','{""timelineId"":""timeline_a""}');
INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,text,world_day,ts,participants_json,payload_json)
VALUES('foundation_cache_turn','foundation_cache_source','foundation_cache_event',1,'foundation_cache_exchange','npc','npc_b',
'I trust you. That does not mean I agree to leave my family.',20,401,'[""npc_b"",""player""]','{""timelineId"":""timeline_a""}');", TestDict("campaign", campaign));
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
                    add("foundation_classification_does_not_rewrite_original", ReadString(QuerySql(connection,
                        "SELECT * FROM conversation_turns WHERE turn_id='foundation_cache_turn';").Single(), "text", "")
                        == "I trust you. That does not mean I agree to leave my family.", null);
                }
                finally
                {
                    ExecuteSql(connection, "DELETE FROM conversation_turns WHERE session_id='foundation_cache_source'; DELETE FROM conversation_sessions WHERE session_id='foundation_cache_source';");
                }
                var cacheAudit = TestDict();
                LoadPrecisionFoundationProjection(connection,campaign,plan,context,observer,false,cacheAudit);
                LoadPrecisionFoundationProjection(connection,campaign,plan,context,observer,false,cacheAudit);
                add("projection_cache_revision_bound",ReadString(cacheAudit,"foundationCache","")=="fresh",cacheAudit);
                ExecuteSql(connection,"UPDATE conversation_turns SET text=text || ' Revised boundary.' WHERE turn_id='source_1';");
                LoadPrecisionFoundationProjection(connection,campaign,plan,context,observer,false,cacheAudit);
                add("projection_catches_late_source_revision",ReadString(cacheAudit,"foundationCache","")=="caught_up",cacheAudit);
                ExecuteSql(connection,@"INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,text,world_day,ts,participants_json,payload_json)
VALUES('whisper','group_source','private_event',4,'agreement_exchange','player','player','SECRET_UNHEARD_PRICE',20,4,'[""npc_a"",""player""]','{""timelineId"":""timeline_a""}');");
                var privateSafe = PrecisionExchangeEvidence(connection,QuerySql(connection,"SELECT * FROM conversation_turns WHERE turn_id='source_1';").Single(),observer,true,100);
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
                add("empty_reply_is_not_delivery",QuerySql(connection,"SELECT * FROM group_conversation_contributions WHERE session_id='shared_beat' AND role='npc';").Count==2,null);
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
                add("save_sync_classifies_derivatives", new[] { "memory_precision_state", "memory_projections", "memory_background_jobs", "temporal_knowledge_assertions" }.All(t => SaveSyncSubsystemForTable(t) == "memories"), null);
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
                var output = TryParseJsonObject(ConversationMemoryOutputSchema(DefaultPromptText(name, DefaultPromptTemplates())));
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
            return results;
        }

        private static List<Dictionary<string, object>> RunDerivedMemoryTimeSelfTests()
        {
            if (string.IsNullOrWhiteSpace(CampaignsRootOverride.Value))
                throw new InvalidOperationException("Derived-memory temporal fixtures require isolated Verification Lab state.");
            string campaign = "derived_memory_time_test_" + Guid.NewGuid().ToString("N");
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass) => results.Add(TestDict("caseId", "memory_precision_derived_time_" + id,
                "passed", pass, "summary", id));
            try
            {
                using var connection = OpenCampaignConnection(campaign);
                var context = TestDict("timelineId", "derivative_branch", "worldDay", 20d);
                ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('source_session',$campaign,'npc','player','closed',100,100,'[""npc"",""player""]',$payload);",
                    TestDict("campaign", campaign, "payload", Json.Serialize(context)));
                const string answer = "If we visit the mill, I will inspect the roof. We have not gone there together yet.";
                InsertConversationTurn(connection, "source_question", "source_session", "source_event", 0, "source_exchange",
                    "player", "player", "Player", "If we visit the mill, who will inspect the roof?", "in_person", 20d, 100L,
                    "", new List<string> { "npc", "player" }, context);
                InsertConversationTurn(connection, "source_answer", "source_session", "source_event", 1, "source_exchange",
                    "npc", "npc", "NPC", answer, "in_person", 20d, 100L,
                    "", new List<string> { "npc", "player" }, context);
                ExecuteSql(connection, "UPDATE conversation_turns SET accepted_sequence=CASE WHEN turn_id='source_question' THEN 10 ELSE 11 END;");
                KnowledgeAccessContext Access(long cutoff = 11, double day = 20, long timestamp = 100, string branch = "derivative_branch", string npc = "npc") =>
                    new KnowledgeAccessContext { NpcId = npc, PlayerId = "player", TimelineId = branch, WorldDay = day,
                        AsOfAcceptedSequence = cutoff, AsOfTimestamp = timestamp };
                var plan = PlanMemoryQuery("npc", "player", "mill roof", context);
                foreach (var kind in new[] { ("memories", "memory_id"), ("summaries", "summary_id"),
                    ("beliefs", "belief_id"), ("comprehension", "comprehension_id") })
                {
                    var row = TestDict(kind.Item2, "derived_" + kind.Item1, "event_id", "source_event", "owner_id", "npc", "believer_id", "npc",
                        "summary", answer, "claim", answer, "text", answer, "status", "active", "ts", 1L,
                        "payload_json", Json.Serialize(TestDict("timelineId", "derivative_branch", "worldDay", 1d)));
                    string before = CanonicalJson(row);
                    Check(kind.Item1 + "_cannot_backdate_past_its_source", !KnowledgeRowVisibleToNpc(kind.Item1, row, Access(10), connection));
                    Check(kind.Item1 + "_available_after_source_acceptance", KnowledgeRowVisibleToNpc(kind.Item1, row, Access(), connection));
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
                ExecuteSql(connection, @"INSERT INTO memory_sources(document_type,document_id,source_type,source_id)
VALUES('summary','linked_summary','memory','intermediate_memory'),('memory','intermediate_memory','event','source_event');");
                Check("nested_source_lineage_keeps_accepted_boundary", !MemoryTimeVisible(linked, Access(10), connection)
                    && MemoryTimeVisible(linked, Access(), connection));
                var legacy = TestDict("memory_id", "legacy_missing", "summary", "Unverified older recollection.", "owner_id", "npc", "ts", 1L);
                Check("genuinely_missing_history_remains_explicitly_unverified", MemoryTimeVisible(legacy, Access(9), connection)
                    && ExpandPrecisionSource(connection, "memories", legacy, plan, Access(9), 70).Single().Kind == "unverified");
                var belief = TestDict("belief_id", "private_missing", "claim", "I suspect the old mill is unsafe.", "owner_id", "npc", "ts", 1L);
                Check("unknown_private_interpretation_emitted_once", ExpandPrecisionSource(connection, "beliefs", belief, plan, Access(), 70).Count == 1);
                ExecuteSql(connection, "UPDATE conversation_turns SET status='inactive' WHERE event_id='source_event';");
                Check("known_inactive_source_is_not_missing_history", ExpandPrecisionSource(connection, "summaries", linked, plan, Access(), 70).Count == 0);
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }

        private static List<Dictionary<string, object>> RunMemoryCompactionOutcomeSelfTests()
        {
            if (string.IsNullOrWhiteSpace(CampaignsRootOverride.Value))
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
            var length = BuildMiddleTermSummary(settings, "npc", "conversation:compaction_fixture", rows, responder: Respond);
            var diagnostic = ReadDictionary(length, "compactionDiagnostic");
            Check("length_failure_is_explicit_and_not_retried", calls == 1 && !ReadBool(length, "retryable", true)
                && ReadString(length, "compactionStatus", "") == "incomplete"
                && ReadString(diagnostic, "reason", "") == "provider_output_limit"
                && ReadInt(diagnostic, "reasoningTokens", 0) == 1199);
            string prompt = string.Join("\n", ReadDictionaryList(request, "messages").Select(m => ReadString(m, "content", "")));
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
                var result = BuildMiddleTermSummary(settings, "npc", "conversation:compaction_fixture", rows, responder: Respond);
                Check(item.Item1 + "_retains_originals_without_retry", ReadString(result, "summary", "") == retained
                    && ReadString(result, "compactionStatus", "") == "incomplete" && !ReadBool(result, "retryable", true)
                    && ReadString(ReadDictionary(result, "compactionDiagnostic"), "reason", "") == item.Item3
                    && ReadBool(ReadDictionary(result, "compactionDiagnostic"), "responseCacheHit", false));
            }
            response = TestDict("ok", true, "finishReason", "stop", "content", "{\"sourceIndices\":[1,3]}");
            var accepted = BuildMiddleTermSummary(settings, "npc", "conversation:compaction_fixture", rows, responder: Respond);
            Check("valid_partial_selection_preserves_answer_and_qualification", ReadString(accepted, "compactionStatus", "") == "complete"
                && !ReadBool(accepted, "coverageComplete", true) && ReadString(accepted, "summary", "").Contains("may arrive tomorrow; no payment has happened.")
                && ReadString(accepted, "summary", "").Contains("NPC asked") && ReadString(accepted, "summary", "").Length <= 1800);
            response = TestDict("ok", false, "error", "HTTP status 401 invalid API key");
            var unauthorized = BuildMiddleTermSummary(settings, "npc", "conversation:compaction_fixture", rows, responder: Respond);
            Check("provider_configuration_failure_is_not_blindly_retried", !ReadBool(unauthorized, "retryable", true)
                && ReadString(ReadDictionary(unauthorized, "compactionDiagnostic"), "reason", "") == "provider_failure");
            int beforeLocal = calls;
            var complete = BuildMiddleTermSummary(settings, "npc", "conversation:short_fixture", new() { rows[0], rows[2] }, responder: Respond);
            Check("complete_sources_fit_without_provider", calls == beforeLocal && ReadBool(complete, "coverageComplete", false)
                && ReadString(complete, "summary", "").Contains(ReadString(rows[0], "summary", ""))
                && ReadString(complete, "summary", "").Contains(ReadString(rows[2], "summary", ""))
                && ReadString(ReadDictionary(complete, "compactionDiagnostic"), "reason", "") == "complete_sources_fit_locally");
            var oversize = BuildMiddleTermSummary(settings, "npc", "conversation:oversize_fixture", new() { rows[1] }, responder: Respond);
            Check("impossible_whole_record_selection_skips_provider", calls == beforeLocal
                && ReadString(oversize, "summary", "").Contains(new string('x', 3000)) && !ReadBool(oversize, "retryable", true)
                && ReadString(ReadDictionary(oversize, "compactionDiagnostic"), "reason", "") == "no_record_fits_summary_budget");
            Check("input_rows_are_unchanged", CanonicalJson(rows) == before);
            return results;
        }

        private static List<Dictionary<string, object>> RunRecentMemoryWindowSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string name, bool passed, object data = null) => results.Add(TestDict(
                "caseId", "memory_precision_recent_window_" + name, "passed", passed,
                "summary", name + (!passed && data != null ? ": " + LimitText(data is string error ? error : Json.Serialize(data), 2400) : ""), "data", data));
            string campaign = "recent_memory_window_" + Guid.NewGuid().ToString("N");
            var context = TestDict("timelineId", "window_main", "worldDay", 30d, "playerHeroStringId", "player");
            var settings = TestDict("enableSemanticMemory", false, "enableMinime", false,
                "useMemoryLlmForConsolidation", false, "memoryPrecisionMode", "precision");
            var knowledge = BuildKnowledgeAccessContext("observer", "player", "", context);
            var plan = PlanMemoryQuery("observer", "player", "Continue our conversation", context);
            var priorSettings = CodexPerformanceSettings.Value;
            var envelopeSettings = new Dictionary<string, object>(settings)
            {
                ["llmProvider"] = NanoGptProvider, ["dialogueModel"] = "recent-window-fixture",
                ["eventsModel"] = "recent-window-fixture"
            };
            var certificates = TestDict();
            foreach (string requestType in new[] { "dialogue", "social_event", "correspondence" })
            {
                var capacity = ResolveConversationPromptCapacity(TestDict(), requestType, envelopeSettings);
                certificates[ReadString(capacity, "routeKey", "")] = TestDict(
                    "endpoint", NanoGptChatCompletionsUrl, "model", "recent-window-fixture",
                    "contextTokens", 1000000, "maxOutputTokens", 131072,
                    "source", "isolated recent-window fixture", "validUntilUtc", DateTimeOffset.UtcNow.AddHours(1).ToString("o"));
            }
            envelopeSettings["verifiedConversationContextWindows"] = certificates;
            CodexPerformanceSettings.Value = envelopeSettings;
            try
            {
                using (var connection = OpenCampaignConnection(campaign))
                {
                    ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('group_window',$campaign,'other','player','closed',1,35,'[""observer"",""other"",""player""]','{""timelineId"":""window_main""}');
INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,world_day,ts,participants_json,payload_json)
SELECT 'window_turn_'||n,'group_window','window_event_'||n,n,'window_exchange_'||n,'player','player','Michael',
CASE WHEN n=35 THEN 'The price is 300 after arrival; no payment has occurred.' ELSE 'Complete message '||n||': '||repeat('unaltered source words ',40) END,
20,n,'[""observer"",""other"",""player""]','{""timelineId"":""window_main""}' FROM generate_series(1,35) n;
INSERT INTO summaries(summary_id,owner_id,scope,summary_type,summary,start_ts,end_ts,updated_ts,known_by_json,payload_json)
SELECT 'window_summary_'||n,'observer','scene:episode_'||n,'scene',
'Complete summary '||n||': '||repeat('retained context ',80)||' END-SUMMARY-'||n,n,n,1000-n,'[""observer""]',
'{""timelineId"":""window_main"",""worldDay"":20}' FROM generate_series(1,40) n;
INSERT INTO summaries(summary_id,owner_id,scope,summary_type,summary,start_ts,end_ts,updated_ts,known_by_json,payload_json)
VALUES('duplicate_view','observer','scene:episode_40','middle_term','DUPLICATE-VIEW',40,40,9999,'[""observer""]','{""timelineId"":""window_main""}'),
('hidden_summary','observer','scene:hidden','scene','HIDDEN-SUMMARY',90,90,90,'[""observer""]','{""timelineId"":""window_main""}'),
('future_summary','observer','scene:future','scene','FUTURE-SUMMARY',91,91,91,'[""observer""]','{""timelineId"":""window_main"",""worldDay"":60}'),
('other_branch','observer','scene:branch','scene','OTHER-BRANCH',92,92,92,'[""observer""]','{""timelineId"":""different""}'),
('unheard_summary','stranger','scene:unheard','scene','UNHEARD-SUMMARY',93,93,93,'[""stranger""]','{""timelineId"":""window_main""}');
UPDATE summaries SET hidden_from_json='[""observer""]' WHERE summary_id='hidden_summary';", TestDict("campaign", campaign));
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
                            TestDict("promptEnvelope", envelope.Diagnostics), body, NanoGptChatCompletionsUrl, "recent-window-fixture");
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
                        ExecuteSql(connection, @"INSERT INTO summaries(summary_id,owner_id,scope,summary_type,summary,start_ts,end_ts,updated_ts,known_by_json,payload_json)
VALUES('oversized_duplicate','observer','scene:episode_40','scene',$archive,40,40,2000,'[""observer""]','{""timelineId"":""window_main"",""compactionStatus"":""incomplete"",""method"":""extractive_fallback""}'),
('retained_archive','observer','scene:old_caravan','middle_term',$archive,41,41,2001,'[""observer""]','{""timelineId"":""window_main"",""compactionStatus"":""incomplete"",""method"":""extractive_fallback""}');", TestDict("archive", retainedArchive));
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
                        Check("read_time_compaction_never_rewrites_archive", ReadString(QuerySql(connection,
                            "SELECT summary FROM summaries WHERE summary_id='retained_archive';").Single(), "summary", "") == retainedArchive);

                        ExecuteSql(connection, @"UPDATE conversation_turns SET exchange_id='old_caravan_terms',
role=CASE WHEN turn_order=2 THEN 'player' ELSE 'npc' END,
speaker_id=CASE WHEN turn_order=2 THEN 'player' ELSE 'observer' END,
speaker_name=CASE WHEN turn_order=2 THEN 'Michael' ELSE 'Observer' END,
text=CASE turn_order WHEN 1 THEN 'What are the caravan price and payment conditions?'
WHEN 2 THEN '300 only after arrival. No payment has occurred.' ELSE 'I acknowledge those terms and that no payment has occurred.' END
WHERE session_id='group_window' AND turn_order<=3;");
                        for (int i = 1; i <= 3; i++)
                            LinkMemorySource(connection, "summary", "retained_archive", "turn", "window_turn_" + i, i);
                        var oldPlan = PlanMemoryQuery("observer", "player", "What price did we agree for the caravan?", context);
                        var retainedRow = QuerySql(connection, "SELECT * FROM summaries WHERE summary_id='retained_archive';").Single();
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
                    var unknown = BuildKnowledgeAccessContext("observer", "player", "", context); unknown.PlayerIdentityUnknown = true;
                    Check("unknown_identity_firewall", LoadRecentConversationWindow(connection, plan, context, unknown, settings, TestDict()).Count == 0);
                    var early = BuildKnowledgeAccessContext("observer", "player", "", context); early.AsOfTimestamp = 30;
                    var historical = LoadRecentConversationWindow(connection, plan, context, early, settings, TestDict());
                    Check("asof_never_sees_later_sources_or_rewrites", historical.All(e => e.Kind != "recent_summary")
                        && !historical.Any(e => e.Text.Contains("no payment has occurred")));
                }
                foreach (string mode in new[] { "precision", "legacy" })
                {
                    var options = new Dictionary<string, object>(settings) { ["memoryPrecisionMode"] = mode };
                    var packet = BuildNpcMemoryPacket(campaign, "observer", "player", "", "Continue our conversation", 2000, context, options);
                    string text = ReadString(packet, "memoryPacket", "");
                    Check("shared_packet_window_" + mode, Enumerable.Range(11, 30).All(i => text.Contains("END-SUMMARY-" + i))
                        && text.Contains("no payment has occurred"));
                }
                using (var connection = OpenCampaignConnection(campaign))
                {
                    var priorLines = QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id='group_window' ORDER BY ts,turn_order;")
                        .Select(row => TestDict("turnId", ReadString(row, "turn_id", ""), "exchangeId", ReadString(row, "exchange_id", ""),
                            "speakerId", ReadString(row, "speaker_id", ""), "speaker", ReadString(row, "speaker_name", ""),
                            "role", ReadString(row, "role", ""), "text", ReadString(row, "text", ""))).ToList();
                    foreach (string route in new[] { "dialogue", "party_chat", "event" })
                    {
                        var payload = new Dictionary<string, object>(context) { ["interactionMode"] = route,
                            ["mainHeroStringId"] = "player", ["mainHeroName"] = "Michael", ["heroStringId"] = "observer" };
                        SetMemoryTranscriptContext(payload, priorLines);
                        var memory = BuildNpcMemoryPacket(campaign, "observer", "player", "", "Continue our conversation", 2000, payload, settings);
                        string memoryText = ReadString(memory, "memoryPacket", "");
                        var profile = TestDict("name", "Observer", "heroStringId", "observer", "age", 30, "isNoble", true);
                        var identity = TestDict("identityState", "verified", "canonicalNameAllowed", true, "usableName", "Michael");
                        var envelope = route == "event"
                            ? BuildEventPromptEnvelope(campaign, "window_event", "observer", "Observer", "Michael", "Michael",
                                "Continue our conversation", "A quiet meeting.", payload, profile, TestDict(), TestDict(), TestDict(), TestDict(),
                                priorLines, new(), new(), new(), identity, precomputedMemoryPacket: memoryText, precomputedGroupConversationPrompt: "")
                            : BuildDialoguePromptEnvelope(campaign, "observer", "Observer", "Michael", "Michael",
                                "Continue our conversation", "A quiet meeting.", profile, TestDict(), TestDict(), TestDict(), TestDict(),
                                priorLines, new(), new(), new(), identity, payload, precomputedMemoryPacket: memoryText, precomputedGroupConversationPrompt: "");
                        string actual = string.Join("\n", envelope.Messages.Select(m => ReadString(m, "content", "")));
                        Check("deduplicated_transcript_window_survives_actual_envelope_" + route,
                            priorLines.TakeLast(30).All(line => actual.Contains(NormalizePromptSegment(ReadString(line, "text", ""))))
                            && Enumerable.Range(11, 30).All(i => actual.Contains("END-SUMMARY-" + i))
                            && !memoryText.Contains("no payment has occurred"), TestDict("characters", actual.Length,
                                "missingRaw", priorLines.TakeLast(30).Where(line => !actual.Contains(NormalizePromptSegment(ReadString(line, "text", "")))).Select(line => ReadString(line, "turnId", "")).ToList(),
                                "missingSummaries", Enumerable.Range(11,30).Where(i => !actual.Contains("END-SUMMARY-" + i)).ToList(),
                                "memoryDuplicatesFinalRaw", memoryText.Contains("no payment has occurred")));
                    }
                }
            }
            catch (Exception ex) { Check("fixture_completed", false, ex.ToString()); }
            finally { CodexPerformanceSettings.Value = priorSettings; ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }

    }
}
