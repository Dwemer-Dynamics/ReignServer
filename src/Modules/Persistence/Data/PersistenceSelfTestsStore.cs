using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Persistence module's SQL statements used only by its self-tests (verification build only); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class PersistenceSelfTestStore
    {
        internal static void InsertProviderWaitContract(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO provider_wait_contract VALUES('stale');", parameters, transaction);
        }

        internal static void UpdateSchemaMeta(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE schema_meta SET value='4' WHERE key='version';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='semantic_memory_provider_generation';", parameters, transaction);
        }
    }
}
