using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Platform module's schema statements (tables, indexes and columns); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class PlatformSchema
    {
        internal static void CreateInitializationReadinessSeals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS initialization_readiness_seals (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,seal_version INTEGER NOT NULL,
generation_id TEXT NOT NULL,history_sequence INTEGER NOT NULL DEFAULT 0,
relationship_plan_id TEXT NOT NULL DEFAULT '',acknowledged_utc TEXT NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,seal_version));", parameters, transaction);
        }
    }
}
