using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueStore
    {
        internal static List<Dictionary<string, object>> SelectConversationTurns40(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE event_id=$id AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns41(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_turns WHERE session_id=$session AND status='active'
AND turn_order BETWEEN $first AND $last ORDER BY turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns42(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE event_id=$event AND status='active' ORDER BY turn_order;", parameters, transaction);
        }

        internal static string SelectConversationContinuitySql2(object part, object part2, object part3)
        {
            return @"SELECT * FROM conversation_continuity WHERE owner_id=$owner AND " + part + @"
AND (peer_id='' OR peer_id=$peer) AND payload_json::jsonb->>'relatedTurnId'<>'' AND status<>'invalidated'
AND updated_ts<=$asOf AND CASE WHEN jsonb_typeof(payload_json::jsonb->'worldDay')='number'
THEN (payload_json::jsonb->>'worldDay')::double precision ELSE 0 END<=$day"
                + (part2)
                + " ORDER BY " + (part3)
                + "updated_ts DESC,record_id LIMIT 64 OFFSET $offset;";
        }

        /// <summary>Runs a statement this module composes at run time from text kept in this class.</summary>
        internal static List<Dictionary<string, object>> QuerySql(ReignDbConnection connection, string sql, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, sql, parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns43(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.*,s.player_id,s.npc_id,s.payload_json AS session_payload
FROM conversation_turns t JOIN conversation_sessions s ON s.session_id=t.session_id
WHERE t.status IN ('active','source_only') AND t.role IN ('player','player_source') ORDER BY t.ts,t.turn_order,t.turn_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns44(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id AND status='active' AND role='player';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns45(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id AND status='source_only';", parameters, transaction);
        }

        internal static void UpdateConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET payload_json=$payload WHERE turn_id=$id AND status='source_only';", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationSessions4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,location_id,participants_json,payload_json)
VALUES($session,$campaign,$npc,$player,$channel,'open',$now,$now,$day,$day,$location,$audience,$payload) ON CONFLICT(session_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT player_id,payload_json FROM conversation_sessions WHERE session_id=$session;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationInputReceipts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_input_receipts WHERE input_id=$id;", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationInputReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_input_receipts
(input_id,timeline_id,session_id,player_id,source_turn_id,source_hash,restore_generation,status,created_ts,updated_ts)
VALUES($id,$timeline,$session,$player,$source,$hash,$generation,'received',$now,$now) ON CONFLICT(input_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns46(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id=$id AND status='source_only';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationInputReceipts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT response_json,restore_generation FROM conversation_input_receipts WHERE input_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationInputReceipts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_input_receipts SET response_json=$responses,status=$status,updated_ts=$now
WHERE input_id=$id AND restore_generation=$generation;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns47(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM conversation_turns WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET payload_json=$json WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationInputReceipts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_input_receipts SET
response_json=((response_json::jsonb)||jsonb_build_object(CAST($observer AS text),CAST($response AS jsonb)))::text,
status='accepted',updated_ts=$now WHERE input_id=$id AND restore_generation=$generation;", parameters, transaction);
        }
    }
}
