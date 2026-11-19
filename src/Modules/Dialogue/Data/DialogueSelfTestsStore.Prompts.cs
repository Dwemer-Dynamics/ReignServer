using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueSelfTestStore
    {
        internal static void InsertConversationSessions26(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,participants_json,payload_json)
VALUES('direct-session',$campaign,'npc','player','in_person','closed',10,'[""npc"",""player""]','{""timelineId"":""main""}');", parameters, transaction);
        }
    }
}
