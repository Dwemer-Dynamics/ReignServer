using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Spymaster module's schema statements (tables, indexes and columns); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class SpymasterSchema
    {
        internal static void CreateMitigationReceipts(ReignDbConnection connection)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS spymaster_mitigation_receipts (
campaign_id TEXT NOT NULL, mission_id TEXT NOT NULL, terms_hash TEXT NOT NULL, response_json TEXT NOT NULL,
PRIMARY KEY(campaign_id,mission_id));");
        }

        internal static void CreateSpymasterMissionMemory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS spymaster_mission_memory (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,mission_id TEXT NOT NULL,spymaster_hero_id TEXT NOT NULL,
phase TEXT NOT NULL,mission_type TEXT NOT NULL,target_type TEXT NOT NULL,target_id TEXT NOT NULL,target_name TEXT NOT NULL,
started_day REAL NOT NULL,due_day REAL NOT NULL,resolved_day REAL NOT NULL DEFAULT -1,request_summary TEXT NOT NULL DEFAULT '',
result_summary TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,mission_id));", parameters, transaction);
        }

        internal static void CreateIndexSpymasterMemoryOwner(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_spymaster_memory_owner ON spymaster_mission_memory(campaign_id,timeline_id,spymaster_hero_id,started_day DESC);", parameters, transaction);
        }

    }
}
