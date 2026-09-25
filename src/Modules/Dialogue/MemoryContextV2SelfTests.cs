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
                "memoryPrecisionMode", "precision", "enableMemoryPrecisionHelper", false);
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
            MemoryPrecisionHelperForTests = _ => TestDict("ok", true, "content", "{\"selectedAliases\":[\"c999\"],\"unresolvedNeeds\":[]}");
            try { ResolvePrecisionAmbiguity(helperPlan, helperCandidates, TestDict(), true, helperNeeds, helperDiagnostics); }
            finally { MemoryPrecisionHelperForTests = null; }
            add("helper_cannot_invent_source", ReadString(helperDiagnostics, "helperOutcome", "") == "invalid_source_reference" && helperNeeds.Count > 0, helperDiagnostics);
            add("helper_one_call_bound", ReadInt(helperDiagnostics, "helperCalls", 0) == 1, null);
            string schema = "{\n  \"required\": [\"reply\"], \"description\": \"Preserve exact terms and conditions\"\n}";
            add("compact_schema_semantic_parity",CanonicalJson(TryParseJsonObject(schema))==CanonicalJson(TryParseJsonObject(CompactPrecisionSchema(schema))) && CompactPrecisionSchema(schema).Length<schema.Length,null);
            return results;
        }
    }
}
