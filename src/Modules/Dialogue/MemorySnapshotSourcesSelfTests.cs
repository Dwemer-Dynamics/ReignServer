using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunMemorySnapshotSourceAssertions()
        {
            if (string.IsNullOrWhiteSpace(CampaignsRootOverride.Value))
                throw new InvalidOperationException("Snapshot memory contracts require isolated Verification Lab storage.");
            var results = new List<Dictionary<string, object>>();
            void Check(string name, bool ok, object evidence = null) => results.Add(TestDict("caseId", "memory_snapshot_" + name,
                "passed", ok, "summary", name, "data", evidence));
            string campaign = "memory_snapshot_" + Guid.NewGuid().ToString("N");
            const string question = "What price will you pay for the caravan?";
            const string answer = "I will pay 300 denars only after the caravan arrives.";
            const string acknowledgment = "I accept 300 denars after arrival. Nothing has been paid.";
            var point = TestDict("savePointId", "before", "timelineId", "main", "campaignTimeDays", 20d,
                "worldHistorySequence", 8L, "worldHistoryHeadEventId", "saved_head");
            var context = TestDict("timelineId", "branch", "worldDay", 20d, "playerHeroStringId", "player",
                "conversationSessionId", "current", "playerText", answer);
            var access = BuildKnowledgeAccessContext("npc", "player", "", context);
            var settings = TestDict("enableSemanticMemory", false, "minimeEnabled", false, "memoryPrecisionMode", "precision",
                "useMemoryLlmForConsolidation", false);
            try
            {
                using (var connection = OpenCampaignConnection(campaign))
                {
                    ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('past',$campaign,'npc','player','closed',1,4,'[""npc"",""listener"",""player""]','{""timelineId"":""main""}'),
('current',$campaign,'npc','player','open',5,0,'[""npc"",""player""]','{""timelineId"":""branch""}');", TestDict("campaign", campaign));
                    void Turn(string id, string text, string who, int order, string timeline, string audience)
                    {
                        ExecuteSql(connection, @"INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,world_day,ts,participants_json,payload_json)
VALUES($id,'past',$id,$order,'exchange',$role,$who,$who,$text,20,$order,$audience,$payload);",
                            TestDict("id", id, "text", text, "who", who, "order", order, "role", who == "player" ? "player" : "npc",
                                "audience", audience, "payload", Json.Serialize(TestDict("timelineId", timeline))));
                        ExecuteSql(connection, "INSERT INTO conversation_turn_fts(turn_id,text,speaker,participants,channel) VALUES($id,$text,$who,$audience,'party_chat');",
                            TestDict("id", id, "text", text, "who", who, "audience", audience));
                    }
                    Turn("question", question, "npc", 1, "main", "[\"npc\",\"listener\",\"player\"]");
                    Turn("answer", answer, "player", 2, "main", "[\"npc\",\"listener\",\"player\"]");
                    Turn("acknowledgment", acknowledgment, "npc", 3, "main", "[\"npc\",\"listener\",\"player\"]");
                    Turn("secret", "The private orchard password is silverfox.", "npc", 4, "main", "[\"npc\",\"player\"]");
                    Turn("sibling", "Abandoned future: caravan price was 9000 denars.", "player", 5, "sibling", "[\"npc\",\"listener\",\"player\"]");
                    ExecuteSql(connection, @"INSERT INTO beliefs(belief_id,believer_id,claim,ts,payload_json) VALUES
('private_belief','npc','I privately suspect the orchard merchant.',3,'{""timelineId"":""main""}');
INSERT INTO memories(memory_id,owner_id,ts,summary,known_by_json,visibility,status,payload_json) VALUES
('old_memory','npc',3,'300 denars only after caravan arrival. Nothing has been paid.','[""npc""]','private','active','{""timelineId"":""main""}');");
                    var originalContext = new Dictionary<string, object>(context) { ["timelineId"] = "main", ["playerText"] = question };
                    var write = TestDict("id", "old_question", "kind", "question", "peerId", "player", "subject", "caravan price", "evidenceQuote", question,
                        "questionState", TestDict("question", TestDict("turnId", "question", "speakerId", "npc", "quote", question),
                            "addresseeIds", new[] { "player" }, "answerState", "unanswered", "evaluation", "unassessed"));
                    var stored = StoreAcceptedConversationContinuity(connection, originalContext, "npc", "player", "question", question, new() { write }, 1);
                    Check("fixture_original_question_accepted", ReadStringList(stored, "applied").Contains("old_question"), stored);
                }
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "before");
                using (var connection = OpenCampaignConnection(campaign))
                    ExecuteSql(connection, @"INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,text,world_day,ts,participants_json,payload_json)
VALUES('later_same_day','past','later',6,'later','player','player','Actually pay 9000 immediately.',20,6,'[""npc"",""player""]','{""timelineId"":""main""}');");
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "before");
                AdvanceMemoryRestoreGeneration(campaign, point);
                using (var connection = OpenCampaignConnection(campaign))
                {
                    var original = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='question';").Single();
                    Check("foreign_source_denied_until_exact_head_binding", !PrecisionTurnVisible(original, "npc", access, connection));
                    BindRecoveredMemorySources(connection, "main", "branch", 7, "wrong_head");
                    Check("mismatched_head_cannot_bind", !PrecisionTurnVisible(original, "npc", access, connection));
                    BindRecoveredMemorySources(connection, "main", "branch", 8, "saved_head");
                    Check("original_content_and_speaker_retained", PrecisionTurnVisible(original, "npc", access, connection)
                        && ReadString(original, "text", "") == question && ReadString(original, "speaker_id", "") == "npc"
                        && ReadString(TryParseJsonObject(ReadString(original, "payload_json", "{}")), "timelineId", "") == "main");
                    Check("later_same_day_removed_by_actual_restore", QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='later_same_day';").Count == 0);
                    var sibling = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='sibling';").Single();
                    Check("retained_sibling_not_promoted_by_snapshot_presence", !PrecisionTurnVisible(sibling, "npc", access, connection));
                    var otherAccess = BuildKnowledgeAccessContext("listener", "player", "", context);
                    var secret = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='secret';").Single();
                    Check("group_listener_knows_question_but_not_whisper", PrecisionTurnVisible(original, "listener", otherAccess, connection)
                        && !PrecisionTurnVisible(secret, "listener", otherAccess, connection));
                    var belief = QuerySql(connection, "SELECT * FROM beliefs WHERE belief_id='private_belief';").Single();
                    Check("private_belief_ownership_survives_restore", KnowledgeRowVisibleToNpc("beliefs", belief, access, connection)
                        && !KnowledgeRowVisibleToNpc("beliefs", belief, otherAccess, connection));
                    var tooEarly = BuildKnowledgeAccessContext("npc", "player", "", context);
                    tooEarly.AsOfAcceptedSequence = ReadLong(original, "accepted_sequence", 0);
                    var answerRow = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='answer';").Single();
                    Check("same_day_answer_cutoff_preserved", !PrecisionTurnVisible(answerRow, "npc", tooEarly, connection));
                    ExecuteSql(connection, @"INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,text,world_day,ts,participants_json,payload_json)
SELECT 'foreign_'||n,'past','foreign_'||n,100+n,'foreign','player','player','caravan price caravan price unrelated branch',20,100+n,
'[""npc"",""player""]','{""timelineId"":""sibling""}' FROM generate_series(1,2000) n;");
                    var plan = PlanMemoryQuery("npc", "player", "caravan price", context);
                    var found = FindPrecisionExchanges(connection, plan, access, 8);
                    Check("search_expands_question_answer_and_acknowledgment", found.Any(e => e.Text.Contains(question) && e.Text.Contains(answer) && e.Text.Contains(acknowledgment)), found.Select(e => e.Text).ToList());
                    var legacy = SearchExactConversationHistory(connection, "npc", "caravan price", TestDict(), 24000, null, "current", 20, access);
                    Check("legacy_and_precision_routes_both_recall", ReadString(legacy, "text", "").Contains(answer));
                    var recentAudit = TestDict();
                    var recent = LoadRecentConversationWindow(connection, plan, context, access, settings, recentAudit);
                    Check("recent_window_keeps_originals", recent.Any(e => e.Text.Contains(question)) && recent.Any(e => e.Text.Contains(answer))
                        && !recent.Any(e => e.Text.Contains("9000")), recentAudit);
                    foreach (string memoryMode in new[] { "legacy", "precision" })
                    foreach (string mode in new[] { "in_person", "party_chat", "social_event", "correspondence" })
                    {
                        var modeSettings = new Dictionary<string, object>(settings) { ["memoryPrecisionMode"] = memoryMode };
                        var payload = PromptParityPayload(mode, false);
                        foreach (var field in context) payload[field.Key] = field.Value;
                        payload["officialMemoryFirewall"] = false; payload["mode"] = mode;
                        var packet = BuildNpcMemoryPacket(campaign, "npc", "player", "", "caravan price", 4000, payload, modeSettings);
                        string memory = ReadString(packet, "memoryPacket", "");
                        if (memoryMode == "precision") payload["memoryPrecisionEvidence"] = ReadDictionary(packet, "precisionEvidence");
                        var profile = PromptParityProfile(); profile["heroStringId"] = "npc";
                        var empty = TestDict(); var lines = new List<Dictionary<string, object>>();
                        var motive = TestDict("sanitizedState", empty, "prompt", "Discussing caravan terms.", "relationshipNpcToTarget", empty, "relationshipNpcToSpouse", empty);
                        PromptEnvelope envelope = mode == "correspondence"
                            ? BuildCorrespondencePromptEnvelope(campaign, "npc", "Merchant", "player", "Player", 20d, "caravan price", profile, PromptParityCharacteristics(), empty, memory, payload)
                            : mode == "in_person"
                                ? BuildDialoguePromptEnvelope(campaign, "npc", "Merchant", "Player", "Player", "caravan price", "On the road.", profile, PromptParityCharacteristics(), empty, empty, empty, lines, lines, lines, lines, PromptParityIdentity(), payload, memory, "")
                                : BuildEventPromptEnvelope(campaign, "fixture", "npc", "Merchant", "Player", "Player", "caravan price", "On the road.", payload, profile, PromptParityCharacteristics(), empty, empty, empty, lines, lines, lines, lines, PromptParityIdentity(), memory, "", motive);
                        string actual = Json.Serialize(envelope.Messages);
                        Check("actual_prompt_" + memoryMode + "_" + mode, actual.Contains(question) && actual.Contains(answer)
                            && actual.Contains(acknowledgment) && !actual.Contains("9000 denars"), TestDict("characters", actual.Length));
                    }
                    var originalHash = MemorySourceHash(new[] { original });
                    ExecuteSql(connection, "UPDATE conversation_turns SET text='Changed question after restore.' WHERE turn_id='question';");
                    var changed = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='question';").Single();
                    Check("stale_and_changed_rows_both_rejected", !PrecisionTurnVisible(original, "npc", access, connection) && !PrecisionTurnVisible(changed, "npc", access, connection));
                    ExecuteSql(connection, "UPDATE conversation_turns SET text=$text WHERE turn_id='question';", TestDict("text", question));
                    CaptureContinuityReadSnapshot(connection, context, "npc");
                    ExecuteSql(connection, @"INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,world_day,ts,participants_json,payload_json)
VALUES('accepted_ack','current','new_answer',1,'new_answer','npc','npc','npc',$text,20,10,'[""npc"",""player""]','{""timelineId"":""branch""}');", TestDict("text", acknowledgment));
                    var oldContinuity = QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id='old_question';").Single();
                    var update = TestDict("id", "old_question", "kind", "question", "peerId", "player", "subject", "caravan price", "evidenceQuote", acknowledgment,
                        "questionState", TestDict("question", TestDict("turnId", "question", "speakerId", "npc", "quote", question),
                            "addresseeIds", new[] { "player" }, "answerState", "answered", "evaluation", "convincing",
                            "answers", new[] { TestDict("turnId", "answer", "speakerId", "player", "quote", answer) }));
                    var updated = StoreAcceptedConversationContinuity(connection, context, "npc", "player", "new_answer", acknowledgment, new() { update }, 10);
                    string branchId = CurrentContinuityRecordId(connection, "branch", "npc", "old_question");
                    Check("answer_updates_current_branch_without_rewriting_original", ReadStringList(updated, "applied").Contains(branchId) && branchId != "old_question"
                        && MemorySourceHash(new[] { oldContinuity }) == MemorySourceHash(QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id='old_question';")), updated);
                    var promptIds = new List<string>();
                    string next = BuildQuestionContinuityPrompt(connection, context, "npc", "player", promptIds);
                    Check("next_read_preserves_answer_without_reopening_original", next.Contains(answer) && next.Contains("answered")
                        && promptIds.Contains(branchId) && !promptIds.Contains("old_question"), next);
                    var beforeAnswer = new Dictionary<string, object>(context) { ["memoryAsOfTimestamp"] = 9L };
                    var oldAccess = BuildKnowledgeAccessContext("npc", "player", "", beforeAnswer);
                    oldAccess.AsOfTimestamp = 9L;
                    Check("later_revision_does_not_hide_original_in_earlier_replay", MemoryTimeVisible(oldContinuity, oldAccess, connection)
                        && QuerySql(connection, "SELECT * FROM conversation_continuity WHERE " + MemorySourceTimelineSql("conversation_continuity", access: oldAccess) + ";",
                            TestDict("timeline", "branch")).Any(r => ReadString(r, "record_id", "") == "old_question"));
                    var replay = StoreAcceptedConversationContinuity(connection, context, "npc", "player", "new_answer", acknowledgment, new() { update }, 10);
                    Check("branch_update_retry_idempotent", ReadDictionaryList(replay, "writeReceipts").Any(r => ReadString(r, "status", "") == "already_applied"), replay);
                    Check("original_speech_hash_unchanged", MemorySourceHash(QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='question';")) == originalHash);
                }
                var nextPoint = TestDict("savePointId", "next", "timelineId", "branch", "campaignTimeDays", 20d,
                    "worldHistorySequence", 9L, "worldHistoryHeadEventId", "next_head");
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "next");
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "next");
                AdvanceMemoryRestoreGeneration(campaign, nextPoint);
                using (var connection = OpenCampaignConnection(campaign))
                {
                    BindRecoveredMemorySources(connection, "branch", "third", 9, "next_head");
                    var third = new Dictionary<string, object>(context) { ["timelineId"] = "third" };
                    var thirdAccess = BuildKnowledgeAccessContext("npc", "player", "", third);
                    Check("second_restore_preserves_transitive_original", PrecisionTurnVisible(QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='question';").Single(), "npc", thirdAccess, connection));
                    var ids = new List<string>();
                    string text = BuildQuestionContinuityPrompt(connection, third, "npc", "player", ids);
                    Check("second_restore_keeps_resolution_and_suppression", text.Contains(answer) && ids.Count > 0 && !ids.Contains("old_question"), text);
                }
                InitializeVerifiedSnapshotMemorySources(campaign, nextPoint, "next");
                using (var connection = OpenCampaignConnection(campaign))
                {
                    BindRecoveredMemorySources(connection, "branch", "verified_noop", 9, "next_head");
                    var noOpAccess = BuildKnowledgeAccessContext("npc", "player", "", new Dictionary<string, object>(context) { ["timelineId"] = "verified_noop" });
                    Check("verified_noop_retains_existing_chain", PrecisionTurnVisible(QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='question';").Single(), "npc", noOpAccess, connection));
                    ExecuteSql(connection, "UPDATE conversation_turns SET text='Changed canonical question.' WHERE turn_id='question';");
                }
                InitializeVerifiedSnapshotMemorySources(campaign, nextPoint, "next");
                using (var connection = OpenCampaignConnection(campaign))
                    Check("no_op_compares_whole_canonical_row", QuerySql(connection, "SELECT * FROM memory_restored_sources WHERE source_table='conversation_turns' AND source_id='question';").Count == 0);
                AdvanceMemoryRestoreGeneration(campaign);
                using (var connection = OpenCampaignConnection(campaign))
                    Check("missing_snapshot_clears_old_authority", QuerySql(connection, "SELECT * FROM memory_restored_sources;").Count == 0);
            }
            catch (Exception ex)
            {
                Check("suite_exception", false, LimitText(ex.ToString(), 2400));
                results.Last()["summary"] = "Snapshot source contract exception: " + LimitText(ex.ToString(), 2200);
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            results.AddRange(RunLegacyMemoryRecoveryAssertions());
            return results;
        }
    }
}
