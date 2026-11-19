using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueSelfTestStore
    {
        internal static List<Dictionary<string, object>> SelectConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_messages;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_messages WHERE message_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationHistory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_history;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationHistory2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_history WHERE owner_id='target';", parameters, transaction);
        }

        internal static void UpdateCommunicationMessages(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE communication_messages SET status='reply_pending';", parameters, transaction);
        }

        internal static void UpdateMemoryPrecisionState2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memory_precision_state SET restore_generation=$generation WHERE state_id='current';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_messages WHERE parent_id<>'';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationHistory3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_history WHERE owner_id='source';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_messages WHERE parent_id='';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_messages WHERE topic_key='library';", parameters, transaction);
        }

        internal static void UpdateConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text='changed' WHERE turn_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_messages WHERE speaker_id='lord_1_14' AND recipient_id='lord_1_37' AND status='draft';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT accepted_sequence FROM conversation_turns WHERE turn_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_messages WHERE speaker_id='lord_1_14' AND recipient_id='lord_1_37';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_messages WHERE speaker_id='lord_1_14';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_messages WHERE speaker_id='lord_1_14' AND recipient_id='lord_1_37' AND status='queued';", parameters, transaction);
        }

        internal static void InsertCommunicationHistory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO communication_history(receipt_id,message_id,timeline_id,owner_id,peer_id,acquired_day,acquired_ts,accepted_sequence,payload_json)
VALUES($id,$id,$timeline,$owner,$peer,$day,$ts,$sequence,$json);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_messages WHERE message_id=$id AND status='queued';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationHistory4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_history WHERE message_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_messages WHERE topic_key IN ('north_note','south_note');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM letters WHERE letter_id=$id;", parameters, transaction);
        }

        internal static void UpdateLetters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE letters SET status='delivered',delivered_day=11 WHERE letter_id=$id;", parameters, transaction);
        }

        internal static void UpdateLetters2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE letters SET status='read',read_day=12,delivery_timeline_id='main' WHERE letter_id=$id;", parameters, transaction);
        }

        internal static void UpdateLetters3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE letters SET body='Altered later account.' WHERE letter_id=$id;", parameters, transaction);
        }

        internal static void UpdateLetters4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE letters SET body=$body WHERE letter_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE session_id=$id AND role='player';", parameters, transaction);
        }

        internal static void UpdateConversationTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET status='invalidated' WHERE turn_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT receipt_id FROM conversation_relationship_receipts WHERE exchange_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_sessions SET status='closed',close_reason='closed_by_player' WHERE session_id=$id;", parameters, transaction);
        }
    }
}
