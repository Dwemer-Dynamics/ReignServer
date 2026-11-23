using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Persistence module's schema statements (tables, indexes and columns) used only by its self-tests (verification build only); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class PersistenceSelfTestSchema
    {
        internal static void CreateProviderWaitContract(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE TABLE provider_wait_contract(value TEXT); INSERT INTO provider_wait_contract VALUES('snapshot');", parameters, transaction);
        }
    }
}
