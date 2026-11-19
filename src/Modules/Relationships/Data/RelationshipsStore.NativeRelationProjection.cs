using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static List<Dictionary<string, object>> SelectIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT hero_id FROM identity_roster WHERE is_player=1 AND hero_id<>'';", parameters, transaction);
        }
    }
}
