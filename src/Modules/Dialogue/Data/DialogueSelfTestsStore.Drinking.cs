using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueSelfTestStore
    {
        internal static List<Dictionary<string, object>> SelectConversationIntoxication2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT state_json FROM conversation_intoxication WHERE hero_id=$hero;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationDrinkScene(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT state_json FROM conversation_drink_scene WHERE scene_id='active';", parameters, transaction);
        }

        internal static void DeleteConversationDrinkScene(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM conversation_drink_scene WHERE scene_id='active';", parameters, transaction);
        }

        internal static void InsertConversationSessions9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES($session,$campaign,'mira','player','open',1,1,'[""mira"",""player""]','{""timelineId"":""main""}');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns30(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE session_id=$session;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationAcceptedUnits2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT unit_id FROM conversation_accepted_units WHERE unit_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationDrinkingTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_key FROM conversation_drinking_turns WHERE hero_id=$hero AND turn_key=$turn;", parameters, transaction);
        }
    }
}
