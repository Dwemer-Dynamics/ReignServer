using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Dialogue module's schema statements (tables, indexes and columns) used only by its self-tests (verification build only); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class DialogueSelfTestSchema
    {
        internal static void AlterDynamic(ReignDbConnection connection, object table, object table2, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "ALTER TABLE " + table + " RENAME TO continuity_absent_" + table2 + ";", parameters, transaction);
        }

        internal static void CreateGauntletRollbackProbe(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS gauntlet_rollback_probe(
id TEXT PRIMARY KEY,value TEXT NOT NULL);
INSERT INTO gauntlet_rollback_probe(id,value)
VALUES('state','before')
ON CONFLICT(id) DO UPDATE SET value=excluded.value;", parameters, transaction);
        }

        internal static void CreateRouteAtomicFixture(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE TABLE route_atomic_fixture(id TEXT PRIMARY KEY);", parameters, transaction);
        }

        internal static void DropMemoryRestoredSources(ReignDbConnection connection, object oldSchema, object oldSchema2, object oldSchema3, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP TABLE " + oldSchema + ".memory_restored_sources; DROP TABLE " + oldSchema2 + ".memory_restored_source_context; DROP TABLE " + oldSchema3 + ".character_performance_events;", parameters, transaction);
        }

        internal static void DropMemoryRestoredSources2(ReignDbConnection connection, object branchSchema, object branchSchema2, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP TABLE " + branchSchema + ".memory_restored_sources; DROP TABLE " + branchSchema2 + ".memory_restored_source_context;", parameters, transaction);
        }

        internal static void CreateStatement(ReignDbConnection connection, object quotedSchema, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE SCHEMA " + quotedSchema + ";", parameters, transaction);
        }

        internal static void CreateOwnershipSentinel(ReignDbConnection connection, object quotedSchema, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE TABLE " + quotedSchema + ".ownership_sentinel(value TEXT NOT NULL);", parameters, transaction);
        }

        internal static void DropStatement(ReignDbConnection connection, object quotedSchema, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP SCHEMA IF EXISTS " + quotedSchema + " CASCADE;", parameters, transaction);
        }

        internal static void DropStatement2(ReignDbConnection connection, object quotedSchema, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP SCHEMA " + quotedSchema + " CASCADE;", parameters, transaction);
        }
    }
}
