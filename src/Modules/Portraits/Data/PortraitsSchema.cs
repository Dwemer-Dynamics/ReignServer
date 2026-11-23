using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Portraits module's schema statements (tables, indexes and columns); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class PortraitsSchema
    {
        internal static void CreateChatViewWardrobeEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
CREATE TABLE IF NOT EXISTS chat_view_wardrobe_evidence (
entry_id TEXT PRIMARY KEY,session_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
source_hash TEXT NOT NULL,payload_json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_view_wardrobe_session ON chat_view_wardrobe_evidence(session_id);", parameters, transaction);
        }
    }
}
