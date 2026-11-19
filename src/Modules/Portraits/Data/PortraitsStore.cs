using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Portraits module's SQL statements; every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class PortraitsStore
    {
        internal static List<Dictionary<string, object>> SelectConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT session_id FROM conversation_sessions WHERE player_id=$player LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectChatViewJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT payload_json FROM chat_view_jobs WHERE player_id=$player
AND ($session='' OR session_id=$session) ORDER BY created_ts,job_id LIMIT 100 OFFSET $offset;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectChatViewJobs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT payload_json FROM chat_view_jobs WHERE job_id=$id AND player_id=$player
AND ($session='' OR session_id=$session);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectChatViewRequests(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT j.payload_json FROM chat_view_requests r JOIN chat_view_jobs j ON j.job_id=r.job_id
WHERE r.request_id=$request AND r.player_id=$player AND r.session_id=$session AND r.timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectChatViewJobs3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM chat_view_jobs WHERE job_id=$id;", parameters, transaction);
        }

        internal static void InsertOrIgnoreChatViewRequests(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO chat_view_requests(request_id,session_id,timeline_id,player_id,job_id)
VALUES($request,$session,$timeline,$player,$job);", parameters, transaction);
        }

        internal static void UpsertChatViewJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO chat_view_jobs(job_id,session_id,timeline_id,player_id,created_ts,payload_json)
VALUES($id,$session,$timeline,$player,$ts,$payload) ON CONFLICT(job_id) DO UPDATE SET payload_json=excluded.payload_json;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectChatViewJobs4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT job_id FROM chat_view_jobs WHERE job_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectChatViewWardrobeEvidence(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT entry_id,source_hash,payload_json FROM chat_view_wardrobe_evidence WHERE session_id=$session AND entry_id IN ("
                + part + ");", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id AND status='source_only' AND speaker_id=$speaker;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectChatViewFrames(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT frame_json FROM chat_view_frames WHERE turn_id=$turn;", parameters, transaction);
        }

        internal static void InsertOrIgnoreChatViewWardrobeEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO chat_view_wardrobe_evidence(entry_id,session_id,timeline_id,source_hash,payload_json)
VALUES($id,$session,$timeline,$hash,$payload);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.*,f.frame_json,f.timeline_id FROM conversation_turns t
LEFT JOIN chat_view_frames f ON f.turn_id=t.turn_id WHERE t.session_id=$session AND t.status='active' AND t.turn_order<$first
ORDER BY t.turn_order DESC LIMIT 4097;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectChatViewWardrobeEvidence2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT e.* FROM chat_view_wardrobe_evidence e JOIN conversation_turns t ON t.turn_id=e.entry_id
WHERE e.session_id=$session AND t.turn_order>=$minimum AND t.turn_order<$first ORDER BY t.turn_order LIMIT 4097;", parameters, transaction);
        }
    }
}
