using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueStore
    {
        internal static List<Dictionary<string, object>> SelectConversationTurns81(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE status='active' AND (speaker_id=$observer OR participants_json::jsonb ? $observer) ORDER BY ts,turn_order,turn_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memories WHERE status='active' ORDER BY ts,memory_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM letters WHERE sender_id=$observer OR recipient_id=$observer ORDER BY created_ts,letter_id;", parameters, transaction);
        }
    }
}
