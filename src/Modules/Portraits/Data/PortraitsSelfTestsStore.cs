using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Portraits module's SQL statements used only by its self-tests (verification build only); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class PortraitsSelfTestStore
    {
        internal static void InsertConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json)
VALUES('view_session',$campaign,'npc0','player','open',1,1,'[""player"",""npc0""]');", parameters, transaction);
        }

        internal static void InsertChatViewRequests(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO chat_view_requests(request_id,session_id,timeline_id,player_id,job_id) VALUES($request,'view_session','main','player',$job);", parameters, transaction);
        }

        internal static void InsertConversationSessions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json)
VALUES('clothes',$campaign,'ira','player','open',1,1,'[""player"",""ira""]');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id='clothes' AND exchange_id=$e ORDER BY turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectChatViewWardrobeEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM chat_view_wardrobe_evidence WHERE entry_id='n6';", parameters, transaction);
        }

        internal static void UpdateChatViewWardrobeEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE chat_view_wardrobe_evidence SET payload_json=$payload WHERE entry_id='n6';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id='clothes' AND exchange_id='e6' ORDER BY turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectChatViewWardrobeEvidence2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM chat_view_wardrobe_evidence WHERE session_id='clothes';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='n7';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectChatViewWardrobeEvidence3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM chat_view_wardrobe_evidence WHERE entry_id='n7';", parameters, transaction);
        }

        internal static void DeleteChatViewWardrobeEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM chat_view_wardrobe_evidence;", parameters, transaction);
        }
    }
}
