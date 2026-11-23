using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static List<Dictionary<string, object>> SelectConversationTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSharedRelationshipHistoryChapters6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM shared_relationship_history_chapters WHERE pair_key=$pair
AND ($scoped=0 OR (created_world_day<=$day AND source_basis_json::jsonb->>'timelineId'=$timeline))
ORDER BY chapter_ordinal DESC LIMIT $limit;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryPrecisionState2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT restore_generation FROM memory_precision_state WHERE state_id='current' FOR SHARE;", parameters, transaction);
        }

        internal const string SelectLettersSql = "SELECT * FROM letters WHERE letter_id=$id;";

        internal const string SelectConversationTurnsSql = "SELECT * FROM conversation_turns WHERE turn_id=$id;";
    }
}
