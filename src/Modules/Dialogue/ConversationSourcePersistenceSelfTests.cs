using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunConversationSourcePersistenceSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed) => results.Add(TestDict("caseId", "conversation_source_" + id,
                "passed", passed, "summary", id));
            string campaign = "conversation_source_" + Guid.NewGuid().ToString("N");
            string previousGeneration = _playerActionGeneration;
            try
            {
                _playerActionGeneration = null;
                var payload = TestDict("campaignId", campaign, "conversationSessionId", "source_session",
                    "timelineId", "source_branch", "worldDay", 4d, "participants", new[] { "player", "npc" });
                using (var connection = OpenCampaignConnection(campaign))
                {
                    ReadMemoryPrecisionState(connection);
                    ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json)
VALUES('source_session',$campaign,'npc','player','open',1,1,'[""player"",""npc""]');", TestDict("campaign", campaign));
                }
                int Count(string table, string turn = "")
                {
                    using var connection = OpenCampaignConnection(campaign);
                    return ReadInt(QuerySql(connection, "SELECT COUNT(*) AS n FROM " + table + (turn.Length == 0 ? "" : " WHERE turn_id=$turn") + ";", TestDict("turn", turn)).Single(), "n", 0);
                }
                string Stored(string table, string id, string field)
                {
                    using var connection = OpenCampaignConnection(campaign);
                    return ReadString(QuerySql(connection, "SELECT * FROM " + table + " WHERE turn_id=$id;", TestDict("id", id)).Single(), field, "");
                }
                Dictionary<string, object> Store(string id, Action<string> boundary = null) => StoreConversationExchange(campaign,
                    payload, "npc", "player", "Player", "Mira", "The old mill is my answer.", "I understand your answer.", id, 100, boundary);
                foreach (string phase in new[] { "after_player_source", "before_source_commit" })
                {
                    bool failed = false, invisible = false;
                    try
                    {
                        Store("fault_" + phase, actual => {
                            if (actual != phase) return;
                            invisible = Count("conversation_turns") == 0 && Count("conversation_turn_fts") == 0;
                            throw new InvalidOperationException("Injected source publication fault.");
                        });
                    }
                    catch (InvalidOperationException) { failed = true; }
                    Check("rollback_and_reader_isolation_" + phase, failed && invisible
                        && Count("conversation_turns") == 0 && Count("conversation_turn_fts") == 0);
                }
                Store("accepted");
                Store("accepted");
                Check("exact_replay_retains_one_source_pair", Count("conversation_turns") == 2 && Count("conversation_turn_fts") == 2);
                foreach (string conflict in new[] { "text", "speaker", "audience", "timeline" })
                {
                    bool rejected = false;
                    var changed = CloneDictionary(payload);
                    if (conflict == "audience") changed["participants"] = new[] { "npc", "player", "unheard" };
                    if (conflict == "timeline") changed["timelineId"] = "wrong_branch";
                    try
                    {
                        StoreConversationExchange(campaign, changed, conflict == "speaker" ? "different_npc" : "npc", "player", "Player", "Mira",
                            "The old mill is my answer.", conflict == "text" ? "Conflicting forged words." : "I understand your answer.", "accepted", 101);
                    }
                    catch (InvalidOperationException) { rejected = true; }
                    Check("rejects_changed_" + conflict, rejected && Count("conversation_turns") == 2 && Count("conversation_turn_fts") == 2
                        && Stored("conversation_turns", "accepted_npc", "text") == "I understand your answer."
                        && Stored("conversation_turn_fts", "accepted_npc", "text") == "I understand your answer.");
                }
                using (var connection = OpenCampaignConnection(campaign))
                    ExecuteSql(connection, "DELETE FROM conversation_turn_fts WHERE turn_id='accepted_npc';");
                Store("accepted");
                Check("repairs_derived_index_from_immutable_source", Count("conversation_turn_fts") == 2
                    && Stored("conversation_turn_fts", "accepted_npc", "text") == Stored("conversation_turns", "accepted_npc", "text"));

                Task.WaitAll(Enumerable.Range(0, 6).Select(index => Task.Run(() => Store("parallel_" + index))).ToArray());
                using (var connection = OpenCampaignConnection(campaign))
                {
                    var turns = QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id='source_session' ORDER BY turn_order;");
                    Check("concurrent_complete_pairs_have_distinct_order", turns.Count == 14
                        && turns.Select(t => ReadInt(t, "turn_order", -1)).Distinct().Count() == 14
                        && turns.GroupBy(t => ReadString(t, "exchange_id", "")).All(group => group.Count() == 2
                            && ReadString(group.First(), "role", "") == "player"
                            && ReadString(group.Last(), "role", "") == "npc"
                            && ReadInt(group.Last(), "turn_order", -1) == ReadInt(group.First(), "turn_order", -1) + 1));
                }

                var groupPayload = CloneDictionary(payload);
                groupPayload["participants"] = new[] { "player", "npc", "visitor" };
                groupPayload["sceneTurnId"] = "group_with_visitor";
                StoreGroupedConversationExchange(campaign, groupPayload, "npc", "player", "Player", "Mira", "A public question.",
                    "A public answer.", "public_event", 102, "party_chat", "source_session");
                groupPayload["participants"] = new[] { "player", "npc" };
                groupPayload["sceneTurnId"] = "group_after_departure";
                StoreGroupedConversationExchange(campaign, groupPayload, "npc", "player", "Player", "Mira", "A later private question.",
                    "An answer after the visitor left.", "later_event", 103, "party_chat", "source_session");
                Check("former_participant_does_not_inherit_later_speech",
                    !TextListFromJson(Stored("conversation_turns", "group_after_departure_npc_npc", "participants_json")).Contains("visitor")
                    && !TextListFromJson(Stored("conversation_turns", "group_after_departure_player", "participants_json")).Contains("visitor")
                    && TextListFromJson(Stored("conversation_turns", "group_with_visitor_player", "participants_json")).Contains("visitor"));
                var secondSpeaker = CloneDictionary(groupPayload);
                secondSpeaker["sceneTurnId"] = "group_with_visitor";
                secondSpeaker["participants"] = new[] { "player", "npc", "visitor" };
                bool changedPlayerRejected = false;
                try { StoreGroupedConversationExchange(campaign, secondSpeaker, "visitor", "player", "Player", "Visitor",
                    "A question the player never asked.", "My answer.", "second_event", 103, "party_chat", "source_session"); }
                catch (InvalidOperationException) { changedPlayerRejected = true; }
                Check("later_group_speaker_cannot_replace_player_words", changedPlayerRejected
                    && Stored("conversation_turns", "group_with_visitor_player", "text") == "A public question."
                    && Count("conversation_turns", "group_with_visitor_npc_visitor") == 0);
                StoreGroupedConversationExchange(campaign, secondSpeaker, "visitor", "player", "Player", "Visitor",
                    "A public question.", "My distinct answer.", "second_event", 103, "party_chat", "source_session");
                Check("shared_player_source_supports_distinct_group_speaker", Stored("conversation_turns", "group_with_visitor_npc_visitor", "text") == "My distinct answer."
                    && Stored("conversation_turns", "group_with_visitor_player", "event_id") == "public_event");
                int beforeGroupFault = Count("conversation_turns");
                groupPayload["sceneTurnId"] = "group_fault";
                bool groupFailed = false;
                try
                {
                    StoreGroupedConversationExchange(campaign, groupPayload, "npc", "player", "Player", "Mira", "Not accepted.",
                        "Not accepted either.", "group_fault_event", 104, "party_chat", "source_session",
                        phase => { if (phase == "before_source_commit") throw new InvalidOperationException("Injected grouped source fault."); });
                }
                catch (InvalidOperationException) { groupFailed = true; }
                Check("group_pair_and_index_roll_back_together", groupFailed && Count("conversation_turns") == beforeGroupFault
                    && Count("conversation_turn_fts") == beforeGroupFault);

                var oversized = CloneDictionary(payload);
                foreach (string key in new[] { "attendees", "socialSignals" })
                    oversized[key] = Enumerable.Range(0, 16).Select(i => TestDict("heroStringId", new string('h', 160) + i,
                        "name", new string('n', 160), "supportingQuote", new string('q', 512), "signalId", new string('s', 512),
                        "type", new string('t', 512), "speakerHeroId", new string('x', 512), "targetHeroId", new string('y', 512))).ToList();
                var compact = TryParseJsonObject(SerializeConversationTurnStoragePayload(oversized));
                Check("oversized_metadata_keeps_timeline_identity", ReadBool(compact, "payloadGuardTriggered", false)
                    && ReadString(compact, "timelineId", "") == "source_branch" && ReadDouble(compact, "worldDay", 0d) == 4d);
                using (var connection = OpenCampaignConnection(campaign))
                    _playerActionGeneration = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                AdvanceMemoryRestoreGeneration(campaign);
                bool replaced = false;
                try { Store("stale_generation"); }
                catch (InvalidOperationException) { replaced = true; }
                Check("pre_restore_writer_cannot_publish", replaced && Count("conversation_turns") == beforeGroupFault);
                using (var connection = OpenCampaignConnection(campaign))
                {
                    const string notice = "[Dialogue memory correction could not be verified. No new memory or world change is confirmed.]";
                    InsertConversationTurn(connection, "technical_new", "source_session", "technical_event", 99,
                        "technical_exchange", "npc", "npc", "Mira", notice, "in_person", 4d, 105L, "",
                        new List<string> { "npc", "player" }, payload);
                    var technical = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='technical_new';").Single();
                    Check("technical_notice_is_inactive_and_unindexed", ReadString(technical, "status", "") == "technical_notice"
                        && !QuerySql(connection, "SELECT * FROM conversation_turn_fts WHERE turn_id='technical_new';").Any()
                        && !ReadLogicalConversationTurns(connection, "source_session", 100).Any(t => ReadString(t, "turn_id", "") == "technical_new"));
                    ExecuteSql(connection, "UPDATE conversation_turns SET status='active' WHERE turn_id='technical_new';");
                    Check("legacy_active_notice_excluded_without_rewriting_row", !PrecisionTurnVisible(
                        QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='technical_new';").Single(), "npc", null)
                        && !ReadLogicalConversationTurns(connection, "source_session", 100).Any(t => ReadString(t, "turn_id", "") == "technical_new"));
                    const string safeSpeech = "The old mill is settled. Which gate should we use?";
                    string mixed = safeSpeech + "\n\n" + notice + ".,";
                    InsertConversationTurn(connection, "recovered_speech", "source_session", "recovered_event", 100,
                        "recovered_exchange", "npc", "npc", "Mira", mixed, "in_person", 4d, 106L, "",
                        new List<string> { "npc", "player" }, payload);
                    InsertConversationTurn(connection, "next_answer", "source_session", "next_event", 101,
                        "next_exchange", "player", "player", "Player", "Use the east gate.", "in_person", 4d, 107L, "",
                        new List<string> { "npc", "player" }, payload);
                    InsertConversationTurn(connection, "next_reply", "source_session", "next_event", 102,
                        "next_exchange", "npc", "npc", "Mira", "The east gate, then.", "in_person", 4d, 108L, "",
                        new List<string> { "npc", "player" }, payload);
                    var continued = ReadLogicalConversationTurns(connection, "source_session", 30);
                    Check("same_session_continues_with_safe_question_and_answer", continued
                        .Any(t => ReadString(t, "turn_id", "") == "recovered_speech" && ReadString(t, "text", "") == safeSpeech)
                        && continued.Any(t => ReadString(t, "turn_id", "") == "next_answer" && ReadString(t, "text", "") == "Use the east gate.")
                        && continued.Any(t => ReadString(t, "turn_id", "") == "next_reply")
                        && QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id='recovered_speech';")
                            .Any(t => ReadString(t, "text", "") == mixed)
                        && QuerySql(connection, "SELECT text FROM conversation_turn_fts WHERE turn_id='recovered_speech';")
                            .Any(t => ReadString(t, "text", "") == safeSpeech));
                    RebuildCategorizedSearchIndexes(connection);
                    Check("diagnostic_cannot_support_future_continuity", ConversationContinuityLinkError(connection,
                        TestDict("relatedTurnId", "recovered_speech", "relationKind", "correction",
                            "relatedQuote", "No new memory or world change is confirmed."), "npc", "source_branch", 4d)
                            == "related_source_quote_mismatch"
                        && ConversationContinuityLinkError(connection, TestDict("relatedTurnId", "recovered_speech",
                            "relationKind", "answer", "relatedQuote", "Which gate should we use?"), "npc", "source_branch", 4d) == "");
                    Check("index_rebuild_keeps_safe_speech_without_diagnostic", QuerySql(connection,
                        "SELECT text FROM conversation_turn_fts WHERE turn_id='recovered_speech';")
                        .Any(t => ReadString(t, "text", "") == safeSpeech)
                        && !QuerySql(connection, "SELECT * FROM conversation_turn_fts WHERE turn_id='technical_new';").Any());
                }
            }
            finally
            {
                _playerActionGeneration = previousGeneration;
                ReignPostgreSqlStorage.DropCampaign(campaign);
            }
            return results;
        }
    }
}
