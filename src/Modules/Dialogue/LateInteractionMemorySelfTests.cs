using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunLateInteractionMemorySelfTests()
        {
            if (string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)) throw new InvalidOperationException("ColBERT contracts require isolated Verification Lab.");
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed) => results.Add(TestDict("caseId", "late_interaction_" + id, "passed", passed, "summary", id));
            string campaign = "late_interaction_test_" + Guid.NewGuid().ToString("N");
            var prior = LateInteractionReplay.Value;
            var priorAudit = LateInteractionReplayAudit.Value;
            var priorRecovery = LateInteractionReplayRecovery.Value;
            LateInteractionReplayAudit.Value = new();
            try
            {
                var certificate = TestDict("schema", "reign-colbert-qualification-v1", "indexSchema", LateInteractionSchema,
                    "modelHash", LateInteractionModelHash, "corpusSha256", new string('a',64), "heldOut", true,
                    "independentlyLabeled", true, "caseCount", 100, "criticalErrors", 0, "requiredEvidenceCoverage", .99,
                    "answerCoverage", .99, "baselineAnswerCoverage", .90, "quality", .99, "baselineQuality", .90, "retrievalP95Ms", 120d);
                Check("qualification_requires_accuracy_and_answer_coverage", LateInteractionQualified(TestDict("memoryLateInteractionQualification", certificate)));
                foreach (var change in TestDict("heldOut", false, "independentlyLabeled", false, "caseCount", 99,
                    "criticalErrors", 1, "requiredEvidenceCoverage", .97, "answerCoverage", .97, "quality", .80,
                    "modelHash", new string('b',64), "indexSchema", "old", "retrievalP95Ms", 2001d))
                {
                    var invalid = CloneDictionary(certificate); invalid[change.Key] = change.Value;
                    Check("qualification_rejects_" + change.Key, !LateInteractionQualified(TestDict("memoryLateInteractionQualification", invalid)));
                }
                certificate["answerCoverage"] = double.PositiveInfinity;
                Check("qualification_rejects_nonfinite_metrics", !LateInteractionQualified(TestDict("memoryLateInteractionQualification", certificate)));

                var recovery = new LateInteractionIndexRecovery();
                var clock = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
                foreach (int delay in new[] { 5, 15, 60, 300, 300 })
                {
                    Check("index_backoff_" + delay, recovery.Defer("worker", clock) == delay
                        && !recovery.Ready("worker", clock.AddSeconds(delay - 1))
                        && recovery.Ready("worker", clock.AddSeconds(delay)));
                }
                Check("new_worker_endpoint_is_not_stuck_in_old_backoff", recovery.Ready("replacement", clock));
                recovery.Complete("worker");
                Check("index_success_resets_backoff", recovery.Ready("worker", clock) && recovery.Defer("worker", clock) == 5);
                string privateError = "private original dialogue api_key=secret https://private.invalid";
                var failure = LateInteractionFailureDetails(new InvalidOperationException(privateError,
                    new System.Net.WebException(privateError, System.Net.WebExceptionStatus.Timeout)));
                Check("index_diagnostic_preserves_typed_timeout_not_private_message", ReadString(failure, "cause", "") == "worker_timeout"
                    && ReadString(failure, "transportStatus", "") == "Timeout" && !Json.Serialize(failure).Contains(privateError));
                Check("untrusted_worker_outcome_is_not_logged", LateInteractionIndexOutcome(privateError) == "invalid_response");
                var responseFailure = LateInteractionWorkerFailureDetails(TestDict("errorType", privateError, "reason", privateError));
                Check("untrusted_worker_failure_details_are_redacted", !Json.Serialize(responseFailure).Contains(privateError)
                    && ReadString(LateInteractionWorkerFailureDetails(TestDict("errorType", "TimeoutError", "reason", "colbert_inference_busy")), "workerReason", "") == "colbert_inference_busy");
                Check("transient_index_failure_requires_worker_busy_or_retryable_timeout",
                    LateInteractionTransientIndexResult(TestDict("status", "index_busy"))
                    && LateInteractionTransientIndexResult(TestDict("status", "index_unavailable", "retryable", true))
                    && !LateInteractionTransientIndexResult(TestDict("status", "index_unavailable", "retryable", false)));

                using var connection = OpenCampaignConnection(campaign);
                Dictionary<string, object> Add(string id, string speaker, string text, long ts, string session = "", string[] audience = null,
                    double day = 10, string timeline = "main")
                {
                    var row = SpecialistFixtureTurn(connection, campaign, id, speaker, text, day, ts, timeline, audience, session,
                        speaker == "player" ? "player" : "npc");
                    ExecuteSql(connection, "UPDATE conversation_turns SET accepted_sequence=$sequence WHERE turn_id=$id;", TestDict("sequence", ts, "id", id));
                    row = QuerySql(connection, "SELECT *,xmin::text AS source_revision FROM conversation_turns WHERE turn_id=$id;", TestDict("id", id)).Single();
                    foreach (var view in new[] { "self", "public" }.Select(v => LateInteractionView(row, v)).Where(v => v != null))
                        StoreLateInteractionReceipt(connection, row, view, "fixture-epoch");
                    return row;
                }
                var question = Add("question", "npc", "Where did you find the rare plant?", 10, "qa");
                Add("answer", "player", "Beside the northern stream.", 11, "qa");
                Add("ack", "npc", "Then we will look there together.", 12, "qa");
                Add("private", "npc", "*I secretly remember the stolen kiss.* The evening was pleasant.", 20);
                Add("future", "npc", "The rare plant grows on a faraway island.", 30, day:40);
                Add("other-timeline", "npc", "Another branch has a garden.", 21, timeline:"other");
                Add("unheard", "outsider", "I saw the rare plant.", 22, audience:new[]{"outsider","player"});
                Add("similar-id", "outsider", "The garden belongs to my friend.", 23, audience:new[]{"npc_child","player"});
                var knowledge = new KnowledgeAccessContext { NpcId="npc", PlayerId="player", TimelineId="main", WorldDay=20, AsOfTimestamp=25, AsOfAcceptedSequence=25 };
                var plan = new MemoryQueryPlan { Observer="npc", Peer="player", Timeline="main", WorldDay=20, Query="What hobby did we share?" };
                Dictionary<string, object> lastRequest = null;
                Dictionary<string, object> Answer(Dictionary<string, object> request, string id)
                {
                    lastRequest = request;
                    var allowed = ReadDictionaryList(request,"eligible");
                    var picked = allowed.FirstOrDefault(r=>ReadString(r,"sourceId","")==id);
                    var hits = new List<Dictionary<string,object>>();
                    if(picked!=null) { var hit=CloneDictionary(picked); hit["score"]=18d; hits.Add(hit); }
                    return TestDict("ok",true,"status","ready","schema",LateInteractionSchema,"modelHash",LateInteractionModelHash,
                        "indexedCount",allowed.Count,"results",hits);
                }
                LateInteractionReplay.Value = request => Answer(request,"question");
                var found = FindLateInteractionTurns(connection,plan,knowledge,4,requirePlayerSource:false);
                Check("discovers_original_outside_lexical_pool", found.Any(r=>ReadString(r,"turn_id","")=="question"));
                var eligibleIds = ReadDictionaryList(lastRequest,"eligible").Select(r=>ReadString(r,"sourceId","")).ToList();
                Check("privacy_time_timeline_and_exact_identity_before_scoring", !new[]{"future","other-timeline","unheard","similar-id"}.Any(eligibleIds.Contains));
                Check("search_sends_hashes_not_archive_text", ReadDictionaryList(lastRequest,"eligible").All(r=>!r.ContainsKey("text")));
                var bundle=FindPrecisionExchanges(connection,plan,knowledge,4,includeRecentFloor:false);
                Check("semantic_anchor_delivers_question_answer_ack", bundle.Any(e=>e.Text.Contains("northern stream") && e.Text.Contains("look there together") && e.Text.Contains("rare plant")));
                var legacy=SearchExactConversationHistory(connection,"npc",plan.Query,TestDict(),12000,null,"",30,knowledge);
                Check("legacy_anchor_uses_colbert_when_lexical_has_no_match",ReadString(legacy,"text","").Contains("northern stream")
                    && ReadString(legacy,"text","").Contains("rare plant"));
                plan.SuppliedSources["question"] = MemoryTranscriptSourceHash(question);
                LateInteractionReplay.Value = request => Answer(request,"answer");
                found = FindLateInteractionTurns(connection,plan,knowledge,1,false);
                Check("protected_transcript_excluded_before_top_k", !ReadDictionaryList(lastRequest,"eligible").Any(r=>ReadString(r,"sourceId","")=="question")
                    && found.Any(r=>ReadString(r,"turn_id","")=="answer"));
                plan.SuppliedSources["question"] = "clipped-or-other-speaker";
                FindLateInteractionTurns(connection,plan,knowledge,1,false);
                Check("unverified_transcript_hash_cannot_suppress_original", ReadDictionaryList(lastRequest,"eligible").Any(r=>ReadString(r,"sourceId","")=="question"));
                plan.SuppliedSources.Clear();

                var replayHit=TestDict("sourceId","question","view","self","textHash",new string('a',64),"score",18d);
                var replayCapture=TestDict("query",plan.Query,"schema",LateInteractionSchema,"modelHash",LateInteractionModelHash,"results",new[]{replayHit});
                var replayItem=TestDict("sources",new[]{TestDict("sourceId","question")},"capturedLateInteractionSearches",new[]{replayCapture});
                Check("replay_accepts_bound_numeric_capture",LateInteractionReplayInputError(replayItem)=="");
                replayCapture["results"]="malformed";
                Check("replay_rejects_malformed_results",LateInteractionReplayInputError(replayItem)=="invalid_colbert_capture_hits");
                replayCapture["results"]=new[]{replayHit};
                foreach(var invalidScore in new object[]{"18",true,double.NaN,double.PositiveInfinity})
                { replayHit["score"]=invalidScore; Check("replay_rejects_score_"+invalidScore,LateInteractionReplayInputError(replayItem)=="invalid_colbert_capture_source"); }
                replayHit["score"]=18d;
                replayCapture["modelHash"]=new string('b',64);
                Check("replay_rejects_changed_model",LateInteractionReplayInputError(replayItem)=="invalid_colbert_capture_identity");
                replayCapture["modelHash"]=LateInteractionModelHash;
                var replayEligible=TestDict("sourceId","mapped_question","view","self","textHash",new string('a',64),"sourceHash",new string('c',64));
                var replayRequest=TestDict("query",plan.Query,"eligible",new[]{replayEligible});
                var pending=new List<string>(); var captures=new List<Dictionary<string,object>>();
                var replayed=ReplayLateInteractionSearch(replayItem,replayRequest,id=>"mapped_"+id,new(){replayEligible},pending,captures);
                Check("replay_rebinds_only_verified_original_projection",ReadBool(replayed,"ok",false)
                    && ReadString(ReadDictionaryList(replayed,"results").Single(),"sourceHash","")==new string('c',64) && pending.Count==0);
                replayHit["textHash"]=new string('b',64);
                Check("replay_rejects_changed_projection",!ReadBool(ReplayLateInteractionSearch(replayItem,replayRequest,id=>"mapped_"+id,new(){replayEligible},pending,captures),"ok",false)
                    && pending.Contains("colbert_source_mismatch"));
                replayRequest["query"]="A different search.";
                Check("replay_missing_query_stays_pending",ReadString(ReplayLateInteractionSearch(replayItem,replayRequest,id=>"mapped_"+id,new(){replayEligible},pending,captures),"status","")=="capture_pending");

                var listener = new KnowledgeAccessContext { NpcId="listener",PlayerId="player",TimelineId="main",WorldDay=20,AsOfTimestamp=25,AsOfAcceptedSequence=25 };
                var listenerPlan = new MemoryQueryPlan { Observer="listener",Peer="player",Timeline="main",WorldDay=20,Query="the evening" };
                LateInteractionReplay.Value = request => Answer(request,"private");
                found = FindLateInteractionTurns(connection,listenerPlan,listener,4,requirePlayerSource:false);
                Check("other_observer_uses_only_public_projection", ReadDictionaryList(lastRequest,"eligible").All(r=>ReadString(r,"view","")=="public")
                    && found.Count==1 && !PrecisionExchangeEvidence(connection,found[0],listener,false,1,true).Text.Contains("stolen kiss"));
                LateInteractionReplay.Value = request => {
                    var response=Answer(request,"private");
                    ReadDictionaryList(response,"results").First()["view"]="self";
                    return response;
                };
                Check("worker_cannot_upgrade_private_view", FindLateInteractionTurns(connection,listenerPlan,listener,4,false).Count==0);
                LateInteractionReplay.Value = request => {
                    var response=Answer(request,"question");
                    ReadDictionaryList(response,"results").First()["sourceId"]="unheard";
                    return response;
                };
                Check("worker_cannot_introduce_unlisted_source", FindLateInteractionTurns(connection,plan,knowledge,4,false).Count==0);
                LateInteractionReplay.Value = request => {
                    var response=Answer(request,"question");
                    ReadDictionaryList(response,"results").First()["score"]=double.NaN;
                    return response;
                };
                Check("nonfinite_scores_rejected", FindLateInteractionTurns(connection,plan,knowledge,4,false).Count==0);
                LateInteractionReplay.Value = request => throw new TimeoutException("fixture only");
                Check("worker_failure_returns_fallback_not_exception", FindLateInteractionTurns(connection,plan,knowledge,4,false).Count==0
                    && ReadString(LateInteractionReplayAudit.Value.Last(),"status","")=="local_search_fallback");
                LateInteractionReplay.Value = request => {
                    var response=Answer(request,"question");
                    ExecuteSql(connection,"UPDATE conversation_turns SET text='A changed source.' WHERE turn_id='question';");
                    return response;
                };
                Check("canonical_content_rechecked_after_worker", FindLateInteractionTurns(connection,plan,knowledge,4,false).Count==0);
                LateInteractionReplay.Value = request => Answer(request,"question");
                FindLateInteractionTurns(connection,plan,knowledge,4,false);
                Check("stale_database_revision_excluded_before_scoring", !ReadDictionaryList(lastRequest,"eligible").Any(r=>ReadString(r,"sourceId","")=="question"));
                var rowA=TestDict("turn_id","a"); var rowB=TestDict("turn_id","b"); var rowC=TestDict("turn_id","c");
                Check("rank_fusion_is_three_way_not_raw_score_comparison", ReadString(FuseMemorySearchRanks(3,new(){rowA,rowB},new(){rowB},new(){rowC,rowB}).First(),"turn_id","")=="b");
                var sole = FuseMemorySearchRanks(2,new(),new(),new(){rowC,rowA});
                Check("sole_model_rank_survives_downstream_sort",ReadDouble(sole[0],"relevance",0)>ReadDouble(sole[1],"relevance",0)
                    && ReadString(sole[0],"turn_id","")=="c");
                rowA["relevance"]=.7;
                Check("lexical_only_score_preserved",ReadDouble(FuseMemorySearchRanks(1,new(){rowA},new())[0],"relevance",0)==.7);
                LateInteractionReplay.Value = null;
                Check("isolated_verification_never_contacts_installed_worker", FindLateInteractionTurns(connection,plan,knowledge,4,false).Count==0);
                RunLateInteractionIndexRecoveryContracts(Check);
            }
            catch(Exception ex) { results.Add(TestDict("caseId","late_interaction_fixture_completed","passed",false,"summary",LimitText(ex.ToString(),2500))); }
            finally { LateInteractionReplay.Value=prior; LateInteractionReplayAudit.Value=priorAudit; LateInteractionReplayRecovery.Value=priorRecovery; ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }

        private static void RunLateInteractionIndexRecoveryContracts(Action<string, bool> check)
        {
            string campaign = "late_interaction_recovery_" + Guid.NewGuid().ToString("N");
            try
            {
                using var connection = OpenCampaignConnection(campaign);
                var row = SpecialistFixtureTurn(connection, campaign, "original", "npc", "We will meet tomorrow at the gate.", 10, 10);
                string originalHash = MemorySpecialistSourceHash(row);
                var settings = TestDict("memoryLateInteractionMode", "shadow", "vectorWorkerUrl", "http://isolated.invalid");
                var recovery = new LateInteractionIndexRecovery(); LateInteractionReplayRecovery.Value = recovery;
                int calls = 0;
                LateInteractionReplay.Value = request => { calls++; throw new InvalidOperationException("private", new TimeoutException("private")); };
                ReconcileLateInteractionMemory(campaign, settings);
                ReconcileLateInteractionMemory(campaign, settings);
                check("startup_failure_is_deferred_without_immediate_http_retry", calls == 1
                    && ReadString(LateInteractionReplayAudit.Value.Last(), "phase", "") == "worker_status");
                check("startup_failure_does_not_spend_source_attempts", QuerySql(connection, "SELECT * FROM memory_late_interaction_attempts;").Count == 0);
                recovery.Complete("http://isolated.invalid");
                LateInteractionReplay.Value = request => {
                    calls++;
                    return ReadString(request, "operation", "") == "/memory/late/status"
                        ? TestDict("modelInstalled", true, "schema", LateInteractionSchema, "modelHash", LateInteractionModelHash, "indexGeneration", "epoch")
                        : TestDict("ok", false, "status", "index_busy", "schema", LateInteractionSchema, "modelHash", LateInteractionModelHash, "retryable", true);
                };
                ReconcileLateInteractionMemory(campaign, settings);
                check("busy_worker_preserves_content_attempt_budget", QuerySql(connection, "SELECT * FROM memory_late_interaction_attempts;").Count == 0
                    && ReadString(LateInteractionReplayAudit.Value.Last(), "phase", "") == "worker_index");
                recovery.Complete("http://isolated.invalid");
                bool complete = false;
                int indexCalls = 0;
                LateInteractionReplay.Value = request => {
                    if (ReadString(request, "operation", "") == "/memory/late/status")
                        return TestDict("modelInstalled", true, "schema", LateInteractionSchema, "modelHash", LateInteractionModelHash, "indexGeneration", "epoch");
                    indexCalls++;
                    var views = ReadDictionaryList(request, "sources").Select(CloneDictionary).ToList();
                    if (!complete) views.RemoveAt(views.Count - 1);
                    return TestDict("ok", true, "schema", LateInteractionSchema, "modelHash", LateInteractionModelHash, "sources", views, "indexGeneration", "epoch");
                };
                ReconcileLateInteractionMemory(campaign, settings);
                check("incomplete_worker_receipts_never_mark_source_indexed", QuerySql(connection, "SELECT * FROM memory_late_interaction_sources;").Count == 0
                    && ReadString(QuerySql(connection, "SELECT * FROM memory_late_interaction_attempts;").Single(), "outcome", "") == "invalid_receipts");
                ExecuteSql(connection, "UPDATE memory_late_interaction_attempts SET next_attempt_ts=0;");
                complete = true;
                ReconcileLateInteractionMemory(campaign, settings);
                check("later_maintenance_recovers_complete_views_and_clears_attempts", QuerySql(connection, "SELECT * FROM memory_late_interaction_sources;").Count == 2
                    && QuerySql(connection, "SELECT * FROM memory_late_interaction_attempts;").Count == 0);
                ExecuteSql(connection, "UPDATE memory_late_interaction_sources SET schema_version=@schema;",
                    TestDict("schema", LateInteractionSchema));
                int beforePolicyUpgrade = indexCalls;
                ReconcileLateInteractionMemory(campaign, settings);
                var upgraded = QuerySql(connection, "SELECT * FROM memory_late_interaction_sources;");
                check("normal_maintenance_upgrades_old_observer_projection_policy", indexCalls == beforePolicyUpgrade + 1
                    && upgraded.Count == 2 && upgraded.All(source => ReadString(source, "schema_version", "") == LateInteractionIndexSchema));
                int afterPolicyUpgrade = indexCalls;
                ReconcileLateInteractionMemory(campaign, settings);
                check("current_observer_projection_policy_is_not_reindexed", indexCalls == afterPolicyUpgrade);
                check("index_recovery_keeps_original_conversation_unchanged", MemorySpecialistSourceHash(
                    QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='original';").Single()) == originalHash);
                int before = calls; LateInteractionReplay.Value = null;
                ReconcileLateInteractionMemory(campaign, settings);
                check("isolated_index_without_adapter_has_no_worker_dispatch", calls == before);
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
        }
    }
}
