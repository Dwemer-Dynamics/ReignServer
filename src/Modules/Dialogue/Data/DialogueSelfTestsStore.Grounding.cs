using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueSelfTestStore
    {
        internal static void UpdateConversationSessions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_sessions SET status='closed',close_reason='scene_transition' WHERE session_id='session-a';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns33(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='question';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns34(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='answer';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns35(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE exchange_id='exchange-topic' ORDER BY turn_order;", parameters, transaction);
        }

        internal static void UpdateConversationTurns28(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text='Will you stay here?' WHERE turn_id='topic-npc';", parameters, transaction);
        }

        internal static void UpdateConversationTurns29(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET participants_json='[\"player\",\"other-npc\"]' WHERE turn_id='other-topic';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns36(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='chain-0';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns37(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE exchange_id='chain-exchange-1' ORDER BY turn_order;", parameters, transaction);
        }

        internal static void UpdateConversationTurns30(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET participants_json='[\"player\",\"other-npc\"]' WHERE turn_id='chain-3';", parameters, transaction);
        }

        internal static void UpdateConversationTurns31(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET participants_json='[\"npc\",\"player\"]' WHERE turn_id='chain-3';", parameters, transaction);
        }

        internal static void UpdateConversationTurns32(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET participants_json='[\"player\",\"other-npc\"]',speaker_id='other-npc' WHERE turn_id='chain-4';", parameters, transaction);
        }

        internal static void UpdateConversationTurns33(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET participants_json='[\"npc\",\"player\"]',speaker_id='npc' WHERE turn_id='chain-4';", parameters, transaction);
        }

        internal static void UpdateConversationTurns34(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text=$text WHERE turn_id='chain-3';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns38(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE exchange_id='statement-exchange-0' ORDER BY turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns39(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE exchange_id='statement-exchange-1' ORDER BY turn_order;", parameters, transaction);
        }

        internal static void UpdateConversationTurns35(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET participants_json='[\"player\",\"other-npc\"]' WHERE turn_id='statement-2';", parameters, transaction);
        }

        internal static void UpdateConversationTurns36(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET participants_json='[\"npc\",\"player\"]' WHERE turn_id='statement-2';", parameters, transaction);
        }

        internal static void UpdateConversationTurns37(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET session_id='session-other' WHERE turn_id IN ('statement-2','statement-3');", parameters, transaction);
        }

        internal static void UpdateConversationTurns38(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET session_id='session-a' WHERE turn_id IN ('statement-2','statement-3');", parameters, transaction);
        }

        internal static void UpdateConversationTurns39(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text='Yes, I hear you.' WHERE turn_id='statement-4';", parameters, transaction);
        }

        internal static void UpdateConversationTurns40(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text=$text WHERE turn_id='statement-4';", parameters, transaction);
        }

        internal static void UpdateConversationTurns41(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text=$text WHERE turn_id='statement-2';", parameters, transaction);
        }

        internal static void InsertConversationSessions10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,
participants_json,payload_json) VALUES('rarity-scene',$campaign,'npc','player','in_person','closed',10,'[""npc"",""player""]','{""timelineId"":""fixture""}');", parameters, transaction);
        }

        internal static void InsertConversationContinuity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_continuity(record_id,timeline_id,owner_id,peer_id,kind,status,importance,revision,source_event_id,source_turn_id,updated_ts,payload_json)
VALUES($id,'fixture','npc','player','episode','resolved',0.5,1,'event-a','answer',$ts,$payload);", parameters, transaction);
        }

        internal static void UpdateConversationSessions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_sessions SET status='closed',close_reason='closed_by_player' WHERE session_id='session-b';", parameters, transaction);
        }

        internal static void InsertConversationSessions11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,start_world_day,participants_json,payload_json)
VALUES($session,$campaign,'npc','player','closed',$ts,$day,$participants,$payload);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memories WHERE memory_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationTurns42(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text='Changed source' WHERE turn_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns40(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text,turn_order FROM conversation_turns WHERE session_id='sequential_originals' AND status='source_only';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns41(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text,turn_order FROM conversation_turns WHERE session_id='concurrent_originals' AND status='source_only';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns42(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id='grounding-group' AND status='source_only';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns43(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE exchange_id='grounding-floor' AND role='player';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns44(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id='quiet-group' AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns45(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id='quiet-group' AND status='source_only';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns46(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='legacy_missing';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns47(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM conversation_turns WHERE turn_id='legacy_missing';", parameters, transaction);
        }

        internal static void InsertConversationSessions12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('past',$campaign,'npc','player','closed',1,2,'[""npc"",""player""]','{""timelineId"":""parent""}');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns48(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='past_player';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns49(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE status='source_only';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns50(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE status='source_only';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurnFts4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turn_fts WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationTurns43(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text='A changed accepted turn.' WHERE turn_id='past_player';", parameters, transaction);
        }
    }
}
