using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunConversationEvidenceSelfTests()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object actual = null) => rows.Add(TestDict("caseId", "conversation_evidence_" + id,
                "passed", pass, "summary", id, "data", actual));
            var replayCase = TestDict("caseId", "guard", "query", "Who asked?", "asOfSequence", 2L,
                "availableSources", new[] { TestDict("sourceId", "q", "speakerId", "npc", "text", "Where shall we go?", "sequence", 1L) });
            Check("replay_valid_bounded_source", MemoryReplayInputError(replayCase) == "");
            replayCase["semanticExpectation"] = "The NPC asked.";
            Check("replay_rejects_label_leakage", MemoryReplayInputError(replayCase) == "labels_must_not_enter_candidate");
            replayCase.Remove("semanticExpectation");
            replayCase["asOfSequence"] = 0L;
            Check("replay_rejects_future_source", MemoryReplayInputError(replayCase) == "future_or_unsequenced_source");
            replayCase.Remove("asOfSequence");
            Check("replay_rejects_missing_boundary", MemoryReplayInputError(replayCase) == "missing_as_of_boundary");
            replayCase["asOfUtc"] = "2026-09-20T01:00:00Z";
            Check("replay_rejects_undated_source", MemoryReplayInputError(replayCase) == "future_or_undated_source");
            Check("nonphysical_intimate_question_not_foundation", PairHistoryDimension("Can you tell me something not intimate, just less performative?") == "");
            Check("third_party_and_future_affection_not_milestone", PairHistoryDimension("My sister kissed him.") == ""
                && PairHistoryDimension("Perhaps one day we will share a bed.") == "");
            Check("actual_pair_and_boundary_preserved", PairHistoryDimension("We kissed under the tree.") == "physical affection and boundaries"
                && PairHistoryDimension("I will not kiss you.") == "physical affection and boundaries");
            Check("shared_kiss_and_current_refusal_preserved", PairHistoryDimension("I remember the kiss we shared. That does not mean I agree to leave my family today.") == "physical affection and boundaries"
                && PairHistoryDimension("We shared a kiss by the old gate.") == "physical affection and boundaries");
            Check("conditional_kiss_not_completed_history", PairHistoryDimension("If we kissed, it would complicate things.") == ""
                && PairHistoryDimension("Imagine our first kiss.") == ""
                && PairHistoryDimension("Did you kiss me?") == ""
                && PairHistoryDimension("If we shared a kiss, would it change things?") == "");
            Check("hypothetical_sentence_does_not_hide_real_milestone", PairHistoryDimension("We kissed under the tree. If we kissed again, things might change.") == "physical affection and boundaries"
                && PairHistoryDimension("Do not kiss me.") == "physical affection and boundaries");
            Check("all_array_items_rendered", PromptObjectText(new object[] { "first", "second", new string('x', 1000) }).Length > 1000
                && !PromptObjectText(new object[] { "first", "second" }).Contains("System.Object"));
            var context = TestDict("timelineId", "fixture", "worldDay", 5d, "memoryAsOfAcceptedSequence", 10L, "memoryAsOfTimestamp", 100L);
            var knowledge = BuildKnowledgeAccessContext("npc", "player", "", context);
            Check("same_day_later_answers_cannot_leak", !MemoryTimeVisible(TestDict("world_day", 5d, "accepted_sequence", 11L, "ts", 99L), knowledge)
                && !MemoryTimeVisible(TestDict("world_day", 5d, "accepted_sequence", 9L, "ts", 101L), knowledge)
                && MemoryTimeVisible(TestDict("world_day", 5d, "accepted_sequence", 10L, "ts", 100L), knowledge));
            var sources = CurrentConversationSources("player", "npc", "I answered the question you asked.", "Possibly both explanations are right.", "event");
            var write = TestDict("text", "Both explanations are right.", "sourceEvidence", TestDict("turnId", "current_npc", "speakerId", "npc",
                "evidenceType", "confirmed_native_result", "quote", "Possibly both explanations are right."));
            AttachConversationWriteEvidence(new[] { write }, sources, "npc", "event");
            Check("qualification_loss_marked_incomplete", ReadString(write, "sourceValidation", "") == "incomplete"
                && ReadDictionaryList(write, "originalSources").Count == 2, write);
            Check("native_confirmation_cannot_be_invented", ReadString(ReadDictionary(write, "sourceEvidence"), "evidenceType", "") == "hypothesis");
            var mismatch = BindConversationSource(TestDict("sourceEvidence", TestDict("turnId", "current_player", "speakerId", "npc",
                "quote", "I answered the question you asked.")), sources, "npc", "event");
            Check("wrong_speaker_rejected", ReadString(mismatch, "status", "") == "incomplete");
            var beatSources = CurrentConversationSources("player", "npc", "I answered the question you asked.",
                "Possibly both explanations are right.", "accepted_event", "group_beat_7");
            foreach (string alias in new[] { "current", "accepted_event", "accepted_event_npc", "group_beat_7", "group_beat_7_npc_npc" })
            {
                var cited = BindConversationSource(TestDict("sourceEvidence", TestDict("turnId", alias, "speakerId", "npc",
                    "quote", "Possibly both explanations are right.")), beatSources, "npc", "accepted_event");
                Check("current_alias_" + alias, ReadString(cited, "status", "") == "supported_span"
                    && ReadString(cited, "turnId", "") == "current_npc" && ReadString(cited, "sourceEventId", "") == "accepted_event");
            }
            Check("current_alias_never_changes_speaker", ReadString(BindConversationSource(TestDict("sourceEvidence",
                TestDict("turnId", "group_beat_7", "speakerId", "player", "quote", "Possibly both explanations are right.")),
                beatSources, "npc", "accepted_event"), "status", "") == "incomplete");
            Check("foreign_beat_cannot_borrow_current_quote", ReadString(BindConversationSource(TestDict("sourceEvidence",
                TestDict("turnId", "group_beat_6", "speakerId", "npc", "quote", "Possibly both explanations are right.")),
                beatSources, "npc", "accepted_event"), "status", "") == "incomplete");
            foreach (var pair in new[] { (Alias: "current_npc", Speaker: "npc", Quote: "Possibly both explanations are right."),
                (Alias: "current_player", Speaker: "player", Quote: "I answered the question you asked.") })
            {
                var currentSpeaker = BindConversationSource(TestDict("sourceEvidence", TestDict("turnId", pair.Alias,
                    "speakerId", pair.Alias, "quote", pair.Quote)), beatSources, "npc", "accepted_event");
                Check("speaker_alias_binds_exact_current_" + pair.Alias, ReadString(currentSpeaker, "status", "") == "supported_span"
                    && ReadString(currentSpeaker, "speakerId", "") == pair.Speaker
                    && ReadString(currentSpeaker, "resolvedSpeakerAlias", "") == pair.Alias
                    && ReadString(currentSpeaker, "quote", "") == pair.Quote);
                Check("speaker_alias_cannot_borrow_another_event_" + pair.Alias, ReadString(BindConversationSource(
                    TestDict("sourceEvidence", TestDict("turnId", pair.Alias, "speakerId", pair.Alias, "quote", pair.Quote)),
                    beatSources, "npc", "different_event"), "status", "") == "incomplete");
            }
            foreach (string turnId in new[] { "current_player", "group_beat_6", "historical_turn" })
                Check("speaker_alias_never_redirects_" + turnId, ReadString(BindConversationSource(TestDict("sourceEvidence",
                    TestDict("turnId", turnId, "speakerId", "current_npc", "quote", "Possibly both explanations are right.")),
                    beatSources, "npc", "accepted_event"), "status", "") == "incomplete");
            foreach (string speakerId in new[] { "current_player", "other_npc", "Ira" })
                Check("speaker_alias_never_infers_owner_" + speakerId, ReadString(BindConversationSource(TestDict("sourceEvidence",
                    TestDict("turnId", "current_npc", "speakerId", speakerId, "quote", "Possibly both explanations are right.")),
                    beatSources, "npc", "accepted_event"), "status", "") == "incomplete");
            var qualifiedAlias = TestDict("text", "Both explanations are right.", "sourceEvidence",
                TestDict("turnId", "current_npc", "speakerId", "current_npc", "quote", "Possibly both explanations are right."));
            AttachConversationWriteEvidence(new[] { qualifiedAlias }, beatSources, "npc", "accepted_event");
            Check("speaker_alias_does_not_certify_lost_qualification", ReadString(qualifiedAlias, "sourceValidation", "") == "incomplete"
                && ReadString(qualifiedAlias, "sourceValidationReason", "") == "qualification_lost"
                && ReadDictionaryList(qualifiedAlias, "originalSources").Count == 2);
            var sceneLines = new List<Dictionary<string, object>> { TestDict("role", "npc", "speaker", "Mira", "turnId", "scene",
                "text", "I finished this last year. *She would have finished the meal.*") };
            Check("speech_and_hypothesis_not_scene_completion", BuildCurrentSceneProgressPrompt(sceneLines) == "");
            sceneLines[0]["text"] = "*She served the meal, " + new string('x', 500) + ", then departed.*";
            Check("whole_completed_action_not_prefix", BuildCurrentSceneProgressPrompt(sceneLines).Contains(", then departed."));
            Check("temporary_aversion_rejected", DynamicCharacteristicRejectionReason("Mira hates that the thought follows her tonight.", "npc",
                TestDict("name", "Mira"), TestDict(), "Mira hates that the thought follows her tonight.") == "temporary_reaction_is_not_stable_characterization");
            Check("trait_text_never_clipped_mid_fact", FormatDynamicCharacteristicsForPrompt(TestDict("active",
                new[] { TestDict("category", "personal_history", "text", "I remember " + new string('x', 400) + ", but it was only a possibility.") }))
                .Contains(", but it was only a possibility."));
            Dictionary<string, object> Piece(string id, string hash, string text) => TestDict("id", id, "hash", hash, "text", text);
            MemoryEvidence Pieces(string id, params Dictionary<string, object>[] turns) => new MemoryEvidence {
                SourceId = id, SourceHash = id, Kind = "reported_exchange", Text = string.Join("\n", turns.Select(t => ReadString(t, "text", ""))),
                SourceTurns = turns.ToList(), Score = id == "first" ? 100 : 90 };
            var questionPiece = Piece("q", "speaker-question-hash", "Mira: Where would you choose to go?");
            var answerPiece = Piece("a", "speaker-answer-hash", "Player: I would choose the old orchard.");
            var correctionPiece = Piece("c", "speaker-correction-hash", "Mira: Yes, I was the one who asked that.");
            var dedup = SelectPrecisionEvidence(new[] { Pieces("first", questionPiece, answerPiece), Pieces("second", answerPiece, correctionPiece) });
            Check("overlapping_sequences_share_complete_source_content", dedup.Selected.SelectMany(e => e.SourceTurns).Count() == 3
                && dedup.Text.Contains("Where would you choose to go?") && dedup.Text.Contains("Yes, I was the one who asked that."));
            var provided = new Dictionary<string, string> { ["q"] = "speaker-question-hash", ["a"] = "different-speaker-or-text" };
            var commonDedup = SelectPrecisionEvidence(new[] { Pieces("first", questionPiece, answerPiece) }, 4000, provided);
            Check("bundle_dedup_keeps_changed_source_and_uses_exact_transcript", commonDedup.Selected.Single().SourceTurns.Count == 1
                && commonDedup.Text.Contains("Player: I would choose the old orchard."));
            string campaign = "conversation_evidence_" + Guid.NewGuid().ToString("N");
            try
            {
                var start = TestDict("campaignId", campaign, "timelineId", "fixture", "npcId", "npc", "playerId", "player",
                    "sessionId", "session-a", "worldDay", 5d, "ts", 1L);
                var first = ConversationStartApi(start);
                Check("explicit_start_has_logical_id", ReadBool(first, "ok", false) && ReadString(first, "logicalConversationId", "").Length > 0, first);
                var replay = ConversationStartApi(CloneDictionary(start));
                Check("session_start_retry_idempotent", ReadBool(replay, "idempotent", false), replay);
                using (var connection = OpenCampaignConnection(campaign))
                {
                    var next = TestDict("timelineId", "fixture", "continuedFromSessionId", "session-a", "continuationReason", "scene_transition");
                    Check("open_parent_cannot_be_resumed", !ReadBool(PrepareLogicalConversation(connection, next,
                        campaign, "npc", "player", "session-b"), "ok", true));
                    ExecuteSql(connection, "UPDATE conversation_sessions SET status='closed',close_reason='scene_transition' WHERE session_id='session-a';");
                    var continued = PrepareLogicalConversation(connection, next, campaign, "npc", "player", "session-b");
                    Check("scene_transition_keeps_logical_id", ReadBool(continued, "ok", false)
                        && ReadString(continued, "logicalConversationId", "") == ReadString(first, "logicalConversationId", ""), continued);
                    var bad = PrepareLogicalConversation(connection, TestDict("timelineId", "fixture", "logicalConversationId", ReadString(first, "logicalConversationId", "")),
                        campaign, "npc", "player", "session-c");
                    Check("same_participants_not_implicit_continuation", !ReadBool(bad, "ok", true));
                    var wrong = PrepareLogicalConversation(connection, next, campaign, "another-npc", "player", "session-d");
                    Check("continuation_cannot_change_observer", !ReadBool(wrong, "ok", true));
                    InsertConversationTurn(connection, "question", "session-a", "event-q", 0, "exchange-q", "npc", "npc", "Mira",
                        "What quiet place would you choose?", "in_person", 5d, 1L, "", new List<string> { "npc", "player" }, context);
                    InsertConversationTurn(connection, "answer", "session-a", "event-a", 1, "exchange-a", "player", "player", "Player",
                        "I would choose the old orchard.", "in_person", 5d, 2L, "", new List<string> { "npc", "player" }, context);
                    InsertConversationTurn(connection, "reciprocal", "session-a", "event-r", 2, "exchange-r", "player", "player", "Player",
                        "And what quiet place would you choose?", "in_person", 5d, 3L, "", new List<string> { "npc", "player" }, context);
                    var anchor = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='question';").Single();
                    var bundle = ExpandConversationSequence(connection, new List<Dictionary<string, object>> { anchor },
                        new KnowledgeAccessContext { NpcId = "npc", TimelineId = "fixture", WorldDay = 5d });
                    Check("question_answer_reciprocal_whole_sequence", bundle.Select(r => ReadString(r, "turn_id", "")).SequenceEqual(new[] { "question", "answer", "reciprocal" }), bundle);
                    var compactContext = new KnowledgeAccessContext { NpcId = "npc", TimelineId = "fixture", WorldDay = 5d };
                    var compact = ExpandPrecisionQuestionContext(connection, new List<Dictionary<string, object>> { anchor }, compactContext);
                    Check("compact_question_keeps_answer_and_reciprocal", compact.Select(r => ReadString(r, "turn_id", ""))
                        .SequenceEqual(new[] { "question", "answer", "reciprocal" }));
                    var answering = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='answer';").Single();
                    Check("compact_answer_keeps_question_owner", ExpandPrecisionQuestionContext(connection,
                        new List<Dictionary<string, object>> { answering }, compactContext).Any(r => ReadString(r, "turn_id", "") == "question"));
                    var earlyContext = new KnowledgeAccessContext { NpcId = "npc", TimelineId = "fixture", WorldDay = 5d, AsOfTimestamp = 1L };
                    Check("compact_question_does_not_pull_future_answer", ExpandPrecisionQuestionContext(connection,
                        new List<Dictionary<string, object>> { anchor }, earlyContext).Count == 1);
                    using (var narrow = connection.BeginTransaction())
                    {
                        InsertConversationTurn(connection, "topic-player", "session-a", "event-topic", 10, "exchange-topic", "player", "player", "Player",
                            "We agreed on the orchard road.", "in_person", 5d, 10L, "", new List<string> { "npc", "player" }, context);
                        InsertConversationTurn(connection, "topic-npc", "session-a", "event-topic", 11, "exchange-topic", "npc", "npc", "Mira",
                            "I accept the orchard road.", "in_person", 5d, 11L, "", new List<string> { "npc", "player" }, context);
                        InsertConversationTurn(connection, "other-topic", "session-a", "event-other", 12, "exchange-other", "player", "player", "Player",
                            "Now let us discuss the unrelated fishing contest.", "in_person", 5d, 12L, "", new List<string> { "npc", "player" }, context);
                        var core = QuerySql(connection, "SELECT * FROM conversation_turns WHERE exchange_id='exchange-topic' ORDER BY turn_order;");
                        Check("compact_statement_avoids_unrelated_adjacent_bundle", ExpandPrecisionQuestionContext(connection, core, compactContext).Count == 2
                            && ExpandConversationSequence(connection, core, compactContext).Count == 3);
                        ExecuteSql(connection, "UPDATE conversation_turns SET text='Will you stay here?' WHERE turn_id='topic-npc';");
                        ExecuteSql(connection, "UPDATE conversation_turns SET participants_json='[\"player\",\"other-npc\"]' WHERE turn_id='other-topic';");
                        core = QuerySql(connection, "SELECT * FROM conversation_turns WHERE exchange_id='exchange-topic' ORDER BY turn_order;");
                        Check("compact_question_does_not_skip_unheard_answer", ExpandPrecisionQuestionContext(connection, core, compactContext).Count == 2);
                        narrow.Rollback();
                    }
                    using (var paired = connection.BeginTransaction())
                    {
                        var chain = new[] {
                            (Role: "npc", Text: "What personal pastime would you choose to share?"),
                            (Role: "player", Text: "I visit quiet ruins outside the towns."),
                            (Role: "npc", Text: "That answers it. I know a quiet lookout too."),
                            (Role: "player", Text: "That was a difficult question. How about you then?"),
                            (Role: "npc", Text: "I study uncommon plants along the road."),
                            (Role: "player", Text: "What does grain cost in the market?"),
                            (Role: "npc", Text: "The merchant will set the price.") };
                        for (int i = 0; i < chain.Length; i++)
                            InsertConversationTurn(connection, "chain-" + i, "session-a", "chain-event-" + ((i + 1) / 2), 20 + i,
                                "chain-exchange-" + ((i + 1) / 2), chain[i].Role, chain[i].Role == "npc" ? "npc" : "player",
                                chain[i].Role == "npc" ? "Mira" : "Player", chain[i].Text, "in_person", 5d, 20L + i,
                                "", new List<string> { "npc", "player" }, context);
                        var questionAnchor = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='chain-0';");
                        var answerAnchor = QuerySql(connection, "SELECT * FROM conversation_turns WHERE exchange_id='chain-exchange-1' ORDER BY turn_order;");
                        string[] Ids(List<Dictionary<string, object>> rows, KnowledgeAccessContext access = null) =>
                            ExpandPrecisionQuestionContext(connection, rows, access ?? compactContext)
                                .Select(row => ReadString(row, "turn_id", "")).ToArray();
                        var completeIds = Enumerable.Range(0, 5).Select(i => "chain-" + i).ToArray();
                        Check("compact_question_keeps_acknowledgment_return_question_and_its_answer",
                            Ids(questionAnchor).SequenceEqual(completeIds));
                        Check("compact_answer_hit_also_keeps_return_answer_without_unrelated_topic",
                            Ids(answerAnchor).SequenceEqual(completeIds));
                        var untilReturnQuestion = new KnowledgeAccessContext {
                            NpcId = "npc", TimelineId = "fixture", WorldDay = 5d, AsOfTimestamp = 23L };
                        Check("compact_return_answer_cannot_cross_historical_cutoff",
                            Ids(questionAnchor, untilReturnQuestion).SequenceEqual(completeIds.Take(4)));
                        ExecuteSql(connection, "UPDATE conversation_turns SET participants_json='[\"player\",\"other-npc\"]' WHERE turn_id='chain-3';");
                        Check("compact_return_chain_stops_at_unheard_question", Ids(questionAnchor).SequenceEqual(completeIds.Take(3)));
                        ExecuteSql(connection, "UPDATE conversation_turns SET participants_json='[\"npc\",\"player\"]' WHERE turn_id='chain-3';");
                        ExecuteSql(connection, "UPDATE conversation_turns SET participants_json='[\"player\",\"other-npc\"]',speaker_id='other-npc' WHERE turn_id='chain-4';");
                        Check("compact_return_chain_excludes_other_speakers_private_answer", Ids(answerAnchor).SequenceEqual(completeIds.Take(4)));
                        ExecuteSql(connection, "UPDATE conversation_turns SET participants_json='[\"npc\",\"player\"]',speaker_id='npc' WHERE turn_id='chain-4';");
                        foreach (string unrelated in new[] { "What does grain cost?", "*I wonder how about you?* We can leave soon." })
                        {
                            ExecuteSql(connection, "UPDATE conversation_turns SET text=$text WHERE turn_id='chain-3';", TestDict("text", unrelated));
                            Check("compact_return_context_does_not_follow_new_topic_or_private_question_" + unrelated.Length,
                                Ids(answerAnchor).SequenceEqual(completeIds.Take(3)));
                        }
                        paired.Rollback();
                    }
                    using (var statementReply = connection.BeginTransaction())
                    {
                        var chain = new[] {
                            (Role: "player", Text: "There is a place for you on the council."),
                            (Role: "npc", Text: "I cannot leave this season. I intend to finish my current duties before making that choice."),
                            (Role: "player", Text: "*I lower my cup.* Very well, I will wait. That does not settle where we would live."),
                            (Role: "npc", Text: "I hear you. I still need to speak with my family before deciding."),
                            (Role: "player", Text: "Now let us discuss the unrelated fishing contest."),
                            (Role: "npc", Text: "The boats are ready.") };
                        for (int i = 0; i < chain.Length; i++)
                            InsertConversationTurn(connection, "statement-" + i, "session-a", "statement-event-" + i / 2, 40 + i,
                                "statement-exchange-" + i / 2, chain[i].Role, chain[i].Role == "npc" ? "npc" : "player",
                                chain[i].Role == "npc" ? "Mira" : "Player", chain[i].Text, "in_person", 5d, 40L + i,
                                "", new List<string> { "npc", "player" }, context);
                        var proposal = QuerySql(connection, "SELECT * FROM conversation_turns WHERE exchange_id='statement-exchange-0' ORDER BY turn_order;");
                        var reply = QuerySql(connection, "SELECT * FROM conversation_turns WHERE exchange_id='statement-exchange-1' ORDER BY turn_order;");
                        string[] Ids(List<Dictionary<string, object>> rows, KnowledgeAccessContext access = null) =>
                            ExpandPrecisionQuestionContext(connection, rows, access ?? compactContext)
                                .Select(row => ReadString(row, "turn_id", "")).ToArray();
                        var linked = Enumerable.Range(0, 4).Select(i => "statement-" + i).ToArray();
                        Check("compact_statement_keeps_explicit_reply_and_acknowledgment", Ids(proposal).SequenceEqual(linked));
                        Check("compact_explicit_reply_keeps_preceding_statement_owner", Ids(reply).SequenceEqual(linked.Skip(1)));
                        var exact = ExpandPrecisionQuestionContext(connection, proposal, compactContext);
                        Check("compact_reply_preserves_qualification_and_future_intention", exact.Any(r => ReadString(r, "text", "") == chain[2].Text)
                            && exact.Any(r => ReadString(r, "text", "") == chain[1].Text)
                            && ReadString(exact.Single(r => ReadString(r, "turn_id", "") == "statement-2"), "speaker_id", "") == "player");
                        Check("compact_statement_cannot_pull_future_reply", Ids(proposal, new KnowledgeAccessContext {
                            NpcId = "npc", TimelineId = "fixture", WorldDay = 5d, AsOfTimestamp = 41L }).SequenceEqual(linked.Take(2)));
                        ExecuteSql(connection, "UPDATE conversation_turns SET participants_json='[\"player\",\"other-npc\"]' WHERE turn_id='statement-2';");
                        Check("compact_statement_stops_at_unheard_reply_without_skipping_to_ack", Ids(proposal).SequenceEqual(linked.Take(2)));
                        ExecuteSql(connection, "UPDATE conversation_turns SET participants_json='[\"npc\",\"player\"]' WHERE turn_id='statement-2';");
                        ExecuteSql(connection, "UPDATE conversation_turns SET session_id='session-other' WHERE turn_id IN ('statement-2','statement-3');");
                        Check("compact_statement_does_not_link_across_storage_sessions", Ids(proposal).SequenceEqual(linked.Take(2)));
                        ExecuteSql(connection, "UPDATE conversation_turns SET session_id='session-a' WHERE turn_id IN ('statement-2','statement-3');");
                        ExecuteSql(connection, "UPDATE conversation_turns SET text='Yes, I hear you.' WHERE turn_id='statement-4';");
                        Check("compact_statement_reply_hop_is_bounded", Ids(proposal).SequenceEqual(linked));
                        ExecuteSql(connection, "UPDATE conversation_turns SET text=$text WHERE turn_id='statement-4';", TestDict("text", chain[4].Text));
                        foreach (string direct in new[] { "No, I will not leave my post.", "Not yet. I need time to think.",
                            "I decline your offer.", "Agreed, but only if the council approves." })
                        {
                            ExecuteSql(connection, "UPDATE conversation_turns SET text=$text WHERE turn_id='statement-2';", TestDict("text", direct));
                            Check("compact_statement_preserves_reply_without_judging_agreement_" + direct.Length, Ids(proposal).SequenceEqual(linked));
                        }
                        foreach (string unrelated in new[] { "Now let us discuss the fishing contest.",
                            "*I think yes, but do not say it.* The lamps are bright.", "She said yes to someone else.",
                            "\"Yes,\" I might say if the terms changed.", "If I agree, I might have to leave." })
                        {
                            ExecuteSql(connection, "UPDATE conversation_turns SET text=$text WHERE turn_id='statement-2';", TestDict("text", unrelated));
                            Check("compact_statement_excludes_unrelated_private_quoted_hypothetical_cues_" + unrelated.Length,
                                Ids(proposal).SequenceEqual(linked.Take(2)));
                        }
                        statementReply.Rollback();
                    }
                    var sameSessionPlan = PlanMemoryQuery("npc", "player", "quiet orchard", TestDict("timelineId", "fixture", "worldDay", 5d,
                        "conversationSessionId", "session-a"));
                    using (var rarity = connection.BeginTransaction())
                    {
                        ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,
participants_json,payload_json) VALUES('rarity-scene',$campaign,'npc','player','in_person','closed',10,'[""npc"",""player""]','{""timelineId"":""fixture""}');", TestDict("campaign", campaign));
                        var audience = new List<string> { "npc", "player" };
                        for (int i = 0; i < 25; i++)
                            InsertConversationTurn(connection, "common-topic-" + i, "rarity-scene", "common-event-" + i, i,
                                "common-exchange-" + i, "player", "player", "Player", string.Join(" ", Enumerable.Repeat("We discussed the market meeting.", 15)),
                                "in_person", 5d, 10L + i, "", audience, context);
                        InsertConversationTurn(connection, "distinctive-topic", "rarity-scene", "distinctive-event", 25,
                            "distinctive-exchange", "player", "player", "Player", "I carried an amber glyph at our meeting.",
                            "in_person", 5d, 35L, "", audience, context);
                        var topicPlan = PlanMemoryQuery("npc", "player", "*I glance at the market* Tell me about the amber glyph from our market meeting.",
                            TestDict("timelineId", "fixture", "worldDay", 5d, "conversationSessionId", "rarity-scene"));
                        var ranked = FindPrecisionExchanges(connection, topicPlan, compactContext, 4);
                        Check("distinctive_topic_outranks_repeated_scene_words", ranked.FirstOrDefault()?.SourceId.Contains("distinctive-topic") == true);
                        string beforeRarity = string.Join("|", ranked.Select(e => e.SourceId + ":" + e.Score.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
                        for (int i = 0; i < 25; i++)
                            InsertConversationTurn(connection, "future-topic-" + i, "rarity-scene", "future-event-" + i, 26 + i,
                                "future-exchange-" + i, "player", "player", "Player", "I carried an amber glyph from the market meeting.",
                                "in_person", 7d, 50L + i, "", audience, context);
                        var afterFuture = FindPrecisionExchanges(connection, topicPlan, compactContext, 4);
                        Check("future_history_cannot_change_rarity_or_selection", beforeRarity == string.Join("|", afterFuture.Select(e =>
                            e.SourceId + ":" + e.Score.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
                        for (int i = 0; i < 25; i++)
                            InsertConversationTurn(connection, "unheard-topic-" + i, "rarity-scene", "unheard-event-" + i, 60 + i,
                                "unheard-exchange-" + i, "player", "player", "Player", "I carried an amber glyph from the market meeting.",
                                "in_person", 5d, 80L + i, "", new List<string> { "player", "another-npc" }, context);
                        var afterUnheard = FindPrecisionExchanges(connection, topicPlan, compactContext, 4);
                        Check("unheard_history_cannot_change_rarity_or_selection", beforeRarity == string.Join("|", afterUnheard.Select(e =>
                            e.SourceId + ":" + e.Score.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
                        rarity.Rollback();
                    }
                    var recalledCurrent = FindPrecisionExchanges(connection, sameSessionPlan,
                        new KnowledgeAccessContext { NpcId = "npc", PlayerId = "player", TimelineId = "fixture", WorldDay = 5d }, 8);
                    Check("older_turns_in_current_session_remain_searchable", recalledCurrent.Any(e => e.Text.Contains("I would choose the old orchard.")
                        && e.Text.Contains("What quiet place would you choose?")), recalledCurrent.Select(e => e.SourceId).ToArray());
                    var supplied = TestDict();
                    SetMemoryTranscriptContext(supplied, ReadCurrentSessionDialogueLines(campaign, "session-a", 30));
                    var suppliedPlan = PlanMemoryQuery("npc", "player", "quiet orchard", supplied);
                    Check("transcript_dedup_requires_same_source_speaker_and_content", MemorySourceAlreadySupplied(anchor, suppliedPlan.SuppliedSources));
                    var alteredSource = CloneDictionary(anchor); alteredSource["speaker_id"] = "another-npc";
                    Check("changed_speaker_cannot_suppress_memory", !MemorySourceAlreadySupplied(alteredSource, suppliedPlan.SuppliedSources));
                    alteredSource = CloneDictionary(anchor); alteredSource["text"] = "A clipped or rewritten question.";
                    Check("clipped_transcript_cannot_suppress_memory", !MemorySourceAlreadySupplied(alteredSource, suppliedPlan.SuppliedSources));
                    var beforeAnswer = new KnowledgeAccessContext { NpcId = "npc", PlayerId = "player", TimelineId = "fixture", WorldDay = 5d, AsOfTimestamp = 1L };
                    Check("lexical_recall_filters_future_answers_before_limit", FindPrecisionExchanges(connection, sameSessionPlan, beforeAnswer, 1)
                        .All(e => !e.Text.Contains("I would choose the old orchard.")));
                    var link = TestDict("relatedTurnId", "question", "relationKind", "answer", "relatedQuote", "What quiet place would you choose?");
                    Check("link_keeps_original_asker", ConversationContinuityLinkError(connection, link, "npc", "fixture", 5d) == ""
                        && ReadString(link, "relatedSpeakerId", "") == "npc");
                    link["relatedQuote"] = "An invented question about another topic?";
                    Check("invented_related_quote_rejected", ConversationContinuityLinkError(connection, link, "npc", "fixture", 5d) == "related_source_quote_mismatch");
                    var oldLink = TestDict("relatedTurnId", "question", "relatedQuote", "What quiet place would you choose?", "relationKind", "answer",
                        "subject", "Quiet orchard", "worldDay", 5d);
                    ConversationContinuityLinkError(connection, oldLink, "npc", "fixture", 5d);
                    void AddLink(string id, Dictionary<string, object> value, long updated) => ExecuteSql(connection,
                        @"INSERT INTO conversation_continuity(record_id,timeline_id,owner_id,peer_id,kind,status,importance,revision,source_event_id,source_turn_id,updated_ts,payload_json)
VALUES($id,'fixture','npc','player','episode','resolved',0.5,1,'event-a','answer',$ts,$payload);",
                        TestDict("id", id, "ts", updated, "payload", Json.Serialize(value)));
                    AddLink("old_orchard_answer", oldLink, 3L);
                    for (int i = 0; i < 80; i++)
                    {
                        var unrelated = CloneDictionary(oldLink); unrelated["subject"] = "Caravan accounts";
                        unrelated["relatedQuote"] = "How many caravans are expected?";
                        AddLink("newer_unrelated_" + i, unrelated, 10L + i);
                    }
                    var linkedHistory = LinkedConversationEvidence(connection, "npc", "player", "fixture", "quiet orchard",
                        new KnowledgeAccessContext { NpcId = "npc", PlayerId = "player", TimelineId = "fixture", WorldDay = 5d });
                    Check("lifetime_link_survives_newer_unrelated_records", linkedHistory.Any(e => e.SourceId == "old_orchard_answer"
                        && e.Text.Contains("What quiet place would you choose?") && e.Text.Contains("I would choose the old orchard.")));
                    var futureLink = CloneDictionary(oldLink); futureLink["worldDay"] = 6d;
                    for (int i = 0; i < 80; i++) AddLink("future_orchard_" + i, futureLink, 100L + i);
                    Check("future_links_cannot_displace_eligible_answer", LinkedConversationEvidence(connection, "npc", "player", "fixture", "quiet orchard",
                        new KnowledgeAccessContext { NpcId = "npc", PlayerId = "player", TimelineId = "fixture", WorldDay = 5d })
                        .All(e => !e.SourceId.StartsWith("future_", StringComparison.Ordinal)));
                    Check("later_link_not_available_to_earlier_request", LinkedConversationEvidence(connection, "npc", "player", "fixture", "quiet orchard",
                        new KnowledgeAccessContext { NpcId = "npc", PlayerId = "player", TimelineId = "fixture", WorldDay = 5d, AsOfTimestamp = 2L }).Count == 0);
                    var changedLink = CloneDictionary(oldLink); changedLink["relatedSpeakerId"] = "player";
                    AddLink("wrong_question_actor", changedLink, 4L);
                    Check("linked_recall_revalidates_original_actor", LinkedConversationEvidence(connection, "npc", "player", "fixture", "quiet orchard",
                        new KnowledgeAccessContext { NpcId = "npc", PlayerId = "player", TimelineId = "fixture", WorldDay = 5d })
                        .All(e => e.SourceId != "wrong_question_actor"));
                    var closed = TestDict("id", "answered-topic", "kind", "agenda", "peerId", "player", "subject", "Quiet places",
                        "status", "resolved", "expectedRevision", 0, "transitionReason", "The question was answered.",
                        "evidenceQuote", "I understand your answer about the orchard.");
                    StoreAcceptedConversationContinuity(connection, context, "npc", "player", "closed-event",
                        ReadString(closed, "evidenceQuote", ""), new List<Dictionary<string, object>> { closed }, 4L);
                    var reopen = CloneDictionary(closed);
                    reopen["status"] = "open"; reopen["expectedRevision"] = 1; reopen["transitionReason"] = "There was another question.";
                    reopen["evidenceQuote"] = "What shall we discuss next?";
                    var blocked = StoreAcceptedConversationContinuity(connection, context, "npc", "player", "unrelated-question",
                        ReadString(reopen, "evidenceQuote", ""), new List<Dictionary<string, object>> { reopen }, 5L);
                    Check("unrelated_question_cannot_reopen_answer", ReadDictionaryList(blocked, "rejected")
                        .Any(x => ReadString(x, "reason", "") == "reopening_requires_explicit_new_exchange"), blocked);
                    reopen["evidenceQuote"] = "Can we revisit the question about quiet places?";
                    var reopened = StoreAcceptedConversationContinuity(connection, context, "npc", "player", "explicit-question",
                        ReadString(reopen, "evidenceQuote", ""), new List<Dictionary<string, object>> { reopen }, 6L);
                    Check("explicit_cited_reopening_is_allowed", ReadStringList(reopened, "applied").Contains("answered-topic"), reopened);
                    Check("complaint_about_repetition_does_not_reopen", !HasExplicitConversationReopening("I hear you.", "Why are you asking again? I already answered."));
                    Check("player_can_change_their_mind", HasExplicitConversationReopening("We can discuss it.", "I've changed my mind about the plan."));
                }
                var continuation = ConversationStartApi(TestDict("campaignId", campaign, "timelineId", "fixture", "npcId", "npc",
                    "playerId", "player", "sessionId", "session-b", "continuedFromSessionId", "session-a",
                    "continuationReason", "scene_transition", "worldDay", 5d, "ts", 6L));
                Check("acknowledged_native_handoff_starts", ReadBool(continuation, "ok", false), continuation);
                var duplicate = ConversationStartApi(TestDict("campaignId", campaign, "timelineId", "fixture", "npcId", "npc",
                    "playerId", "player", "sessionId", "session-c", "continuedFromSessionId", "session-a",
                    "continuationReason", "scene_transition"));
                Check("parent_handoff_consumed_once", !ReadBool(duplicate, "ok", true));
                using (var connection = OpenCampaignConnection(campaign))
                {
                    InsertConversationTurn(connection, "answer-after-transition", "session-b", "event-after", 0, "exchange-after", "npc", "npc", "Mira",
                        "I would choose the quiet mill. You answered my question before we moved.", "in_person", 5d, 7L, "",
                        new List<string> { "npc", "player" }, context);
                    var transcript = ReadCurrentSessionDialogueLines(campaign, "session-b", 30);
                    string rendered = FormatEventLinesForPrompt(transcript);
                    Check("continued_prompt_keeps_question_answer_and_speakers", rendered.Contains("Mira [id=npc] [role=npc]: What quiet place would you choose?")
                        && rendered.Contains("Player [id=player] [role=player]: I would choose the old orchard.")
                        && rendered.Contains("I would choose the quiet mill."), rendered);
                    Check("historical_parent_does_not_pull_future_child", !ReadCurrentSessionDialogueLines(campaign, "session-a", 30)
                        .Any(t => ReadString(t, "id", "") == "answer-after-transition"));
                    var asOf = BuildKnowledgeAccessContext("npc", "player", "", TestDict("timelineId", "fixture", "worldDay", 5d, "memoryAsOfTimestamp", 3L));
                    Check("continued_history_respects_as_of_before_limit", ReadLogicalConversationTurns(connection, "session-b", 2, asOf)
                        .Select(t => ReadString(t, "turn_id", "")).SequenceEqual(new[] { "answer", "reciprocal" }));
                    ExecuteSql(connection, "UPDATE conversation_sessions SET status='closed',close_reason='closed_by_player' WHERE session_id='session-b';");
                    Check("closed_player_conversation_cannot_resume", !ReadBool(PrepareLogicalConversation(connection,
                        TestDict("timelineId", "fixture", "continuedFromSessionId", "session-b", "continuationReason", "ui_continuation"),
                        campaign, "npc", "player", "session-d"), "ok", true));
                    Check("rollback_timeline_cannot_resume", !ReadBool(PrepareLogicalConversation(connection,
                        TestDict("timelineId", "rollback", "continuedFromSessionId", "session-a", "continuationReason", "scene_transition"),
                        campaign, "npc", "player", "session-e"), "ok", true));
                    InsertConversationTurn(connection, "first-meeting-fact", "session-a", "event-first", 3, "exchange-first", "npc", "npc", "Mira",
                        "The private passphrase we chose at our first meeting was silver willow.", "in_person", 5d, 4L, "",
                        new List<string> { "npc", "player" }, context);
                    for (int i = 0; i < 300; i++)
                    {
                        string later = "later-meeting-" + i;
                        ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,start_world_day,participants_json,payload_json)
VALUES($session,$campaign,'npc','player','closed',$ts,$day,$participants,$payload);", TestDict("session", later, "campaign", campaign,
                            "ts", 1000L + i, "day", 6d + i, "participants", "[\"npc\",\"player\"]", "payload", "{\"timelineId\":\"fixture\"}"));
                        InsertConversationTurn(connection, later + "-p", later, later, 0, later, "player", "player", "Player",
                            "I checked the roof of the stable in village " + i + ".", "in_person", 6d + i, 1000L + i, "",
                            new List<string> { "npc", "player" }, context);
                        InsertConversationTurn(connection, later + "-n", later, later, 1, later, "npc", "npc", "Mira",
                            "The stable roof report for village " + i + " is recorded.", "in_person", 6d + i, 1000L + i, "",
                            new List<string> { "npc", "player" }, context);
                    }
                    var longPlan = PlanMemoryQuery("npc", "player", "What private passphrase did we choose at our first meeting?",
                        TestDict("timelineId", "fixture", "worldDay", 400d, "conversationSessionId", "later-meeting-299",
                            "memoryActiveTopic", "The stable roof report for village 299 is recorded."));
                    var longRecall = FindPrecisionExchanges(connection, longPlan,
                        new KnowledgeAccessContext { NpcId = "npc", PlayerId = "player", TimelineId = "fixture", WorldDay = 400d }, 8);
                    Check("first_meeting_remains_retrievable_after_300_conversations", longRecall.Any(e => e.SourceId.Contains("first-meeting-fact")
                        && e.Text.Contains("Mira: The private passphrase we chose at our first meeting was silver willow.")), longRecall.Select(e => e.SourceId).ToArray());
                    Check("current_question_outranks_secondary_topic", longRecall.FirstOrDefault()?.SourceId.Contains("first-meeting-fact") == true,
                        longRecall.Select(e => e.SourceId).ToArray());
                }
            }
            catch (Exception ex) { Check("suite_exception: " + LimitText(ex.ToString(), 2000), false, ex.ToString()); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            rows.AddRange(RunNormalizedMemoryEvidencePersistenceTests());
            return rows;
        }

        private static List<Dictionary<string, object>> RunNormalizedMemoryEvidencePersistenceTests()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object actual = null) => rows.Add(TestDict("caseId", "normalized_memory_evidence_" + id,
                "passed", pass, "summary", id, "data", actual));
            string campaign = "normalized_memory_evidence_" + Guid.NewGuid().ToString("N");
            const string playerText = "I plan to recruit two escorts; nobody has been chosen.";
            const string reply = "I heard your correction and accept two escorts.";
            try
            {
                var payload = TestDict("campaignId", campaign, "timelineId", "fixture", "worldDay", 5d);
                ConversationStartApi(TestDict("campaignId", campaign, "timelineId", "fixture", "npcId", "npc", "playerId", "player",
                    "sessionId", "citation-session", "worldDay", 5d, "ts", 1L));
                using var connection = OpenCampaignConnection(campaign);
                int order = 0;
                foreach (string route in new[] { "dialogue_turn", "party_chat_turn", "social_event_turn" })
                {
                    string eventId = "citation_" + route;
                    payload["sceneTurnId"] = "beat_" + route;
                    InsertConversationTurn(connection, eventId + "_p", "citation-session", eventId, order++, eventId, "player", "player", "Player",
                        playerText, route, 5d, 2L, "town", new List<string> { "player", "npc" }, payload);
                    InsertConversationTurn(connection, eventId + "_n", "citation-session", eventId, order++, eventId, "npc", "npc", "Mira",
                        reply, route, 5d, 2L, "town", new List<string> { "player", "npc" }, payload);
                    var valid = TestDict("text", "The player plans to recruit two escorts; nobody has been chosen.", "ownerId", "forged-owner",
                        "sourceEvidence", TestDict("turnId", "current_player", "speakerId", "player", "actorIds", new[] { "player" },
                            "evidenceType", "confirmed_native_result", "quote", playerText, "status", "forged_status", "sourceHash", "forged_hash"),
                        "sourceValidation", "supported_span", "originalSources", new[] { TestDict("text", "fabricated original") });
                    var invalid = TestDict("text", "The player recruited a hundred escorts.", "sourceValidation", "supported_span",
                        "originalSources", new[] { TestDict("text", "fabricated original") }, "sourceEvidence",
                        TestDict("turnId", "current_player", "speakerId", "npc", "quote", playerText, "status", "supported_span"));
                    var legacy = TestDict("text", "Mira acknowledged two escorts.", "replyQuote", reply);
                    var aliasWrite = TestDict("text", "Mira acknowledged two escorts.", "sourceEvidence",
                        TestDict("turnId", route == "dialogue_turn" ? "current" : "beat_" + route, "speakerId", "npc", "quote", reply));
                    var normalized = NormalizeMemoryWrites(new object[] { valid, invalid, legacy, aliasWrite }, campaign, "npc", 2L);
                    Check(route + "_normalization_preserves_only_proposed_citation", normalized.Count == 4
                        && ReadString(ReadDictionary(normalized[0], "sourceEvidence"), "quote", "") == playerText
                        && !ReadDictionary(normalized[0], "sourceEvidence").ContainsKey("status")
                        && !normalized[0].ContainsKey("sourceValidation") && !normalized[0].ContainsKey("originalSources")
                        && ReadString(normalized[0], "ownerId", "") == "npc");
                    StoreTurnMemoryLayers(campaign, eventId, route, "npc", "player", "town", playerText, reply,
                        normalized, new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(),
                        new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(), "conversation:citation-session", 2L,
                        new List<string> { "player", "npc" }, true, payload, existingConnection: connection);
                    var stored = normalized.Select(write => QuerySql(connection, "SELECT * FROM memories WHERE memory_id=$id;",
                        TestDict("id", ReadString(write, "id", ""))).Single()).ToList();
                    var validPayload = TryParseJsonObject(ReadString(stored[0], "payload_json", "{}"));
                    var bound = ReadDictionary(validPayload, "sourceEvidence");
                    Check(route + "_stored_citation_bound_to_accepted_player", ReadString(validPayload, "sourceValidation", "") == "supported_span"
                        && ReadString(bound, "speakerId", "") == "player" && ReadString(bound, "sourceEventId", "") == eventId
                        && ReadString(bound, "sourceHash", "") == PromptHash(playerText) && ReadInt(bound, "start", -1) == 0
                        && ReadInt(bound, "length", 0) == playerText.Length && ReadString(bound, "evidenceType", "") == "future_intention"
                        && ReadStringList(bound, "claimedActorIds").SequenceEqual(new[] { "player" }) && !bound.ContainsKey("actorIds"), bound);
                    string validPrompt = FormatMemoryPacketFact(stored[0], ReadString(stored[0], "summary", ""), "npc", "memory");
                    Check(route + "_next_read_preserves_future_qualification", validPrompt.Contains("plans to recruit two escorts")
                        && validPrompt.Contains("evidence=future_intention; speaker=player") && !validPrompt.Contains("fabricated original"), validPrompt);
                    var invalidPayload = TryParseJsonObject(ReadString(stored[1], "payload_json", "{}"));
                    string fallback = FormatMemoryPacketFact(stored[1], ReadString(stored[1], "summary", ""), "npc", "memory");
                    Check(route + "_wrong_speaker_cannot_certify_summary", ReadString(invalidPayload, "sourceValidation", "") == "incomplete"
                        && fallback.Contains("speaker=player: " + playerText) && fallback.Contains("speaker=npc: " + reply)
                        && !fallback.Contains("recruited a hundred escorts") && !fallback.Contains("fabricated original"), fallback);
                    var legacyPayload = TryParseJsonObject(ReadString(stored[2], "payload_json", "{}"));
                    Check(route + "_legacy_quote_rebound_locally", ReadString(legacyPayload, "sourceValidation", "") == "supported_span"
                        && ReadString(ReadDictionary(legacyPayload, "sourceEvidence"), "speakerId", "") == "npc");
                    var aliasPayload = TryParseJsonObject(ReadString(stored[3], "payload_json", "{}"));
                    Check(route + "_actual_current_beat_alias_survives_storage", ReadString(aliasPayload, "sourceValidation", "") == "supported_span"
                        && ReadString(ReadDictionary(aliasPayload, "sourceEvidence"), "turnId", "") == "current_npc"
                        && ReadString(ReadDictionary(aliasPayload, "sourceEvidence"), "sourceHash", "") == PromptHash(reply));
                }
            }
            catch (Exception ex) { Check("suite_exception", false, LimitText(ex.ToString(), 2000)); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return rows;
        }
    }
}
