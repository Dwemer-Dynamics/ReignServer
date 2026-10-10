using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> SpecialistFixtureTurn(ReignDbConnection connection, string campaign,
            string id, string speaker, string text, double day = 10, long ts = 10, string timeline = "main",
            string[] audience = null, string session = "", string role = "npc", Dictionary<string, object> extra = null)
        {
            audience ??= new[] { "npc", "player", "listener" };
            session = FirstNonEmpty(session, "session_" + id);
            var payload = new Dictionary<string, object>(extra ?? TestDict()) { ["timelineId"] = timeline, ["playerHeroStringId"] = "player", ["playerName"] = "Michael" };
            ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES($session,$campaign,'npc','player','closed',1,$ts,$audience,$payload);",
                TestDict("session", session, "campaign", campaign, "ts", ts, "audience", Json.Serialize(audience), "payload", Json.Serialize(payload)));
            InsertConversationTurn(connection, id, session, id, (int)ts, id, role, speaker, speaker == "npc" ? "Ira" : speaker,
                text, "in_person", day, ts, "", audience.ToList(), payload);
            return QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id;", TestDict("id", id)).Single();
        }

        private static List<Dictionary<string, object>> RunMemorySpecialistIndexSelfTests()
        {
            if (string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)) throw new InvalidOperationException("Specialist contracts require isolated Verification Lab.");
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed, object data = null) => results.Add(TestDict("caseId", "memory_specialist_" + id, "passed", passed, "summary", id, "data", data));
            string campaign = "memory_specialist_test_" + Guid.NewGuid().ToString("N");
            try
            {
                using var connection = OpenCampaignConnection(campaign);
                var source = SpecialistFixtureTurn(connection, campaign, "source", "npc", "Ira asked Michael about the little plant. Michael had already answered her.");
                var doc = BuildMemorySpecialistDocument(source);
                Check("speaker_is_source_identity_not_mentioned_actor", ReadString(doc, "speakerId", "") == "npc"
                    && ReadStringList(doc, "actorIds").Count == 0 && ReadStringList(doc, "addresseeIds").Count == 0
                    && ReadDictionaryList(doc, "entities").Any(e => ReadString(e, "entityId", "") == "player"));
                Check("topic_does_not_assert_completed_event", ReadStringList(doc, "topics").Contains("botany")
                    && ReadString(doc, "authority", "") == "retrieval_candidates_only");
                string original = CanonicalJson(source);
                var extraction = TestDict("ok", true, "schema", MemorySpecialistSchema, "sourceId", "source", "offsetEncoding", "utf16",
                    "textHash", SemanticSha256Hex(ReadString(source, "text", "")), "topics", new[] { "botany", "execute_action" },
                    "entities", new[] { TestDict("kind", "person", "text", "Ira", "start", 0, "end", 3, "confidence", .99, "entityId", "player", "role", "actor"),
                        TestDict("kind", "person", "text", "Maxax", "start", 0, "end", 5, "confidence", 1d) });
                var accepted = BuildMemorySpecialistDocument(source, extraction);
                Check("untrusted_ids_roles_and_unsupported_spans_rejected", ReadDictionaryList(accepted, "entities")
                    .Where(e => ReadString(e, "text", "") == "Ira").All(e => ReadString(e, "entityId", "") == "npc" && ReadString(e, "role", "") == "mentioned")
                    && ReadInt(accepted, "rejectedSpans", 0) == 1 && !ReadStringList(accepted, "topics").Contains("execute_action"));
                extraction["textHash"] = "wrong";
                Check("stale_model_result_rejected", ReadString(BuildMemorySpecialistDocument(source, extraction), "extractorStatus", "") == "rejected_source_identity");
                Check("original_immutable", original == CanonicalJson(source));
                var changed = new Dictionary<string, object>(source) { ["participants_json"] = "[\"npc\"]" };
                Check("source_hash_binds_audience", MemorySpecialistSourceHash(changed) != MemorySpecialistSourceHash(source));
                var named = PlanMemoryQuery("npc", "player", "Pirates spoke to Mirana.", TestDict("participantProfiles", new[] { TestDict("name", "Ira", "heroStringId", "npc") }));
                Check("name_substrings_do_not_resolve_identity", !named.ExplicitEntities.Contains("npc"));
                Check("whole_name_resolves_known_identity", PlanMemoryQuery("npc", "player", "Ira and I discussed this.",
                    TestDict("participantProfiles", new[] { TestDict("name", "Ira", "heroStringId", "npc") })).ExplicitEntities.Contains("npc"));
                string modelHash = new string('a', 64);
                extraction["textHash"] = SemanticSha256Hex(ReadString(source, "text", ""));
                extraction["modelHash"] = modelHash;
                var capture = TestDict("sourceId", "source", "textHash", extraction["textHash"], "result", extraction);
                var replay = TestDict("sources", new[] { TestDict("sourceId", "source", "text", ReadString(source, "text", "")) },
                    "capturedMemoryExtractions", new[] { capture });
                Check("extractor_replay_accepts_complete_bound_results", MemoryExtractionReplayInputError(replay, modelHash) == "");
                string capturedBefore = CanonicalJson(capture);
                var rebound = RebindMemoryExtractionReplay(capture, "fixture:source", modelHash);
                Check("extractor_replay_rebinds_only_disposable_identity", ReadString(rebound, "sourceId", "") == "fixture:source"
                    && CanonicalJson(capture) == capturedBefore && ReadString(rebound, "modelHash", "") == modelHash);
                Check("extractor_replay_rejects_unqualified_model", MemoryExtractionReplayInputError(replay, new string('b', 64)) == "invalid_memory_extraction_identity");
                extraction["offsetEncoding"] = "utf8";
                Check("extractor_replay_rejects_wrong_offsets", MemoryExtractionReplayInputError(replay, modelHash) == "invalid_memory_extraction_identity");
                extraction["offsetEncoding"] = "utf16";
                capture["textHash"] = new string('0', 64);
                Check("extractor_replay_rejects_changed_source", MemoryExtractionReplayInputError(replay, modelHash) == "invalid_memory_extraction_source");
                capture["textHash"] = extraction["textHash"];
                replay["capturedMemoryExtractions"] = Array.Empty<Dictionary<string, object>>();
                Check("extractor_replay_reports_missing_coverage", MemoryExtractionReplayInputError(replay, modelHash) == "incomplete_memory_extraction_captures");
                replay["capturedMemoryExtractions"] = new object[] { "malformed" };
                Check("extractor_replay_rejects_malformed_list", MemoryExtractionReplayInputError(replay, modelHash) == "invalid_memory_extraction_captures");
                replay["capturedMemoryExtractions"] = new[] { capture };
                capture["result"] = TestDict("ok", false, "status", "busy");
                Check("extractor_replay_retains_real_failure", MemoryExtractionReplayInputError(replay, modelHash) == ""
                    && ReadString(BuildMemorySpecialistDocument(source, RebindMemoryExtractionReplay(capture, "source", modelHash)), "extractorStatus", "") == "local_metadata");
                capture["result"] = extraction;
                StoreMemorySpecialistDocument(connection, source, extraction);
                var storedExtraction = TryParseJsonObject(ReadString(QuerySql(connection,
                    "SELECT payload_json FROM memory_specialist_sources WHERE turn_id='source';").Single(), "payload_json", "{}"));
                Check("captured_extraction_uses_production_acceptance_and_storage", ReadString(storedExtraction, "extractorStatus", "") == "source_bound_candidates"
                    && ReadInt(storedExtraction, "rejectedSpans", 0) == 1 && ReadStringList(storedExtraction, "actorIds").Count == 0
                    && ReadString(storedExtraction, "speakerId", "") == "npc"
                    && ReadString(storedExtraction, "extractorModelHash", "") == modelHash);
                Check("disabled_extractor_has_no_due_work", !MemorySpecialistExtractionDue(doc, "", 100));
                Check("local_metadata_upgrades_when_model_qualified", MemorySpecialistExtractionDue(doc, modelHash, 100));
                var retry = TestDict("extractorExpectedModelHash", modelHash, "extractorAttempts", 1, "extractorNextAttemptTs", 160L);
                Check("timeout_uses_backoff_then_bounded_retry", !MemorySpecialistExtractionDue(retry, modelHash, 100)
                    && MemorySpecialistExtractionDue(retry, modelHash, 160));
                retry["extractorAttempts"] = 4;
                Check("retry_exhaustion_does_not_loop", !MemorySpecialistExtractionDue(retry, modelHash, 1000));
                Check("new_model_revision_can_retry", MemorySpecialistExtractionDue(retry, new string('b', 64), 1000));
                retry["extractorModelHash"] = modelHash; retry["extractorStatus"] = "source_bound_candidates";
                Check("successful_extraction_is_not_repeated", !MemorySpecialistExtractionDue(retry, modelHash, 1000));
                // More recent unrelated records cannot erase an older paraphrased
                // topic; annotations widen discovery but never filter lexical recall.
                StoreMemorySpecialistDocument(connection, source);
                SpecialistFixtureTurn(connection, campaign, "source_player", "player", "The herb grows beside a stream.", ts:11, session: "session_source", role: "player");
                var knowledge = new KnowledgeAccessContext { NpcId = "npc", PlayerId = "player", TimelineId = "main", WorldDay = 20 };
                var plan = PlanMemoryQuery("npc", "player", "What did we say about botany?", TestDict("timelineId", "main", "worldDay", 20d));
                Check("paraphrased_topic_finds_original", FindMemorySpecialistTurns(connection, plan, knowledge, 4, 32, true).Any(r => ReadString(r, "turn_id", "") == "source"));
                var legacy=SearchExactConversationHistory(connection,"npc","botany",TestDict(),12000,null,"",30,knowledge);
                Check("legacy_route_uses_same_source_index",ReadString(legacy,"text","").Contains("little plant")
                    && ReadString(legacy,"text","").Contains("herb grows beside a stream"));
                var question = SpecialistFixtureTurn(connection, campaign, "question", "npc", "Where did you find the plant?", ts:30, session:"qa");
                SpecialistFixtureTurn(connection, campaign, "answer", "player", "Beside the northern stream.", ts:31, session:"qa", role:"player");
                SpecialistFixtureTurn(connection, campaign, "acknowledgment", "npc", "That is where we should look, then.", ts:32, session:"qa");
                StoreMemorySpecialistDocument(connection, question);
                Check("expanded_bundle_keeps_answer_without_recent_floor", FindPrecisionExchanges(connection, plan, knowledge, 8, includeRecentFloor:false)
                    .Any(e => e.Text.Contains("Where did you find the plant?") && e.Text.Contains("Beside the northern stream.") && e.Text.Contains("where we should look")));
                var claim = SpecialistFixtureTurn(connection, campaign, "claim", "player", "I could send a rider with a rare flower.", ts:40, session:"clarify", role:"player");
                var assumption = SpecialistFixtureTurn(connection, campaign, "assumption", "npc", "You need not send a rider; bring the plant yourself.", ts:41, session:"clarify");
                var clarification = SpecialistFixtureTurn(connection, campaign, "clarification", "player", "I wasnt talking about sending a rider. I meant enjoying the discovery together.", ts:42, session:"clarify", role:"player");
                var response = SpecialistFixtureTurn(connection, campaign, "clarified_response", "npc", "The shared discovery is what matters.", ts:43, session:"clarify");
                SpecialistFixtureTurn(connection, campaign, "unrelated", "player", "How is the weather?", ts:44, session:"clarify", role:"player");
                StoreMemorySpecialistDocument(connection, assumption);
                var bundles = FindPrecisionExchanges(connection, plan, knowledge, 8, includeRecentFloor:false);
                Check("clarification_follows_statement_without_question", bundles.Any(e => e.Text.Contains("bring the plant yourself")
                    && e.Text.Contains("enjoying the discovery together") && e.Text.Contains("shared discovery is what matters")));
                Check("clarification_expansion_stops_before_unrelated_topic", !ExpandPrecisionQuestionContext(connection,new(){assumption},knowledge)
                    .Any(r=>ReadString(r,"turn_id","")=="unrelated"));
                Check("clarification_anchor_retains_preceding_claim", ExpandPrecisionQuestionContext(connection,new(){clarification,response},knowledge)
                    .Any(r=>ReadString(r,"turn_id","")=="claim"));
                var beforeClarification = new KnowledgeAccessContext { NpcId="npc",PlayerId="player",TimelineId="main",WorldDay=20,AsOfTimestamp=41 };
                Check("future_clarification_is_not_leaked", !ExpandPrecisionQuestionContext(connection,new(){assumption},beforeClarification)
                    .Any(r=>ReadString(r,"turn_id","")=="clarification"));
                ExecuteSql(connection,"UPDATE conversation_turns SET participants_json='[\"player\",\"outsider\"]' WHERE turn_id='clarification';");
                Check("hidden_correction_cannot_be_skipped_to_retrieve_ack", !ExpandPrecisionQuestionContext(connection,new(){assumption},knowledge)
                    .Any(r=>ReadString(r,"turn_id","")=="clarified_response"));
                var early = new KnowledgeAccessContext { NpcId="npc", PlayerId="player", TimelineId="main", WorldDay=9 };
                var earlyPlan = PlanMemoryQuery("npc","player","botany",TestDict("timelineId","main","worldDay",9d));
                Check("future_original_excluded", FindMemorySpecialistTurns(connection, earlyPlan, early, 4, 32, false).Count == 0);
                var foreign = new KnowledgeAccessContext { NpcId="outsider", TimelineId="main", WorldDay=20 };
                Check("unheard_source_excluded", FindMemorySpecialistTurns(connection, new MemoryQueryPlan { Observer="outsider", Query="botany", Timeline="main", WorldDay=20 }, foreign, 4, 32, false).Count == 0);
                var privateSource = SpecialistFixtureTurn(connection,campaign,"private","npc","*I secretly remember our garden and the rare flower.* Let us discuss the weather.", ts:20);
                StoreMemorySpecialistDocument(connection,privateSource);
                var listener = new KnowledgeAccessContext { NpcId="listener", TimelineId="main", WorldDay=20 };
                var publicPlan = new MemoryQueryPlan { Observer="listener", Query="botany", Timeline="main", WorldDay=20 };
                Check("private_thought_tags_do_not_influence_listener_search", !FindMemorySpecialistTurns(connection,publicPlan,listener,8,32,false).Any(r=>ReadString(r,"turn_id","")=="private"));
                Check("own_private_memory_remains_searchable", FindMemorySpecialistTurns(connection,plan,knowledge,8,32,false).Any(r=>ReadString(r,"turn_id","")=="private"));
                ExecuteSql(connection,"UPDATE conversation_turns SET text='The weather is cold.' WHERE turn_id='private';");
                Check("changed_original_invalidates_derived_tags", !FindMemorySpecialistTurns(connection,plan,knowledge,8,32,false).Any(r=>ReadString(r,"turn_id","")=="private"));
                Check("changed_source_is_detected_for_background_reindex", QuerySql(connection,@"SELECT t.turn_id FROM conversation_turns t
JOIN memory_specialist_sources i ON i.turn_id=t.turn_id WHERE t.turn_id='private' AND i.source_revision<>t.xmin::text;").Count==1);
                Check("missing_index_does_not_filter_lexical_candidates", FuseMemorySpecialistRanks(new(){source},new(),1).Count==1);
                Check("rank_fusion_deduplicates_source",FuseMemorySpecialistRanks(new(){source},new(){source},4).Count==1);
            }
            catch(Exception ex) { results.Add(TestDict("caseId","memory_specialist_fixture_completed","passed",false,"summary",LimitText(ex.ToString(),2500))); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
