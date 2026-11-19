using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Government module's SQL statements; every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class GovernmentStore
    {
        internal static List<Dictionary<string, object>> SelectConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_turns WHERE status='active'
AND (speaker_id=$hero OR participants_json::jsonb ? $hero)
AND (speaker_id=$ruler OR participants_json::jsonb ? $ruler)
ORDER BY ts DESC,turn_order DESC,turn_id DESC LIMIT 1200;", parameters, transaction);
        }
    }
}
