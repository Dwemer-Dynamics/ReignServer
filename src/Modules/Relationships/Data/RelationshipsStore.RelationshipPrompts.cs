using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static void InsertRelationshipPairChemistry4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,first_day,last_day,updated_ts)
VALUES($pair,'speaker','target',55,-35,1,1,$ts);", parameters, transaction);
        }

        internal static void InsertAcquaintances(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO acquaintances(
observer_id,subject_id,identity_state,canonical_name,verification_source,confidence,updated_ts)
VALUES('speaker','target','verified','Target Noble','same_kingdom',1,$ts);", parameters, transaction);
        }

        internal static void InsertRelationshipPairLifecycle(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_lifecycle(
pair_key,hero_a_id,hero_b_id,lover_active,affair_active,last_processed_day,updated_ts)
VALUES($pair,'speaker','target',1,1,1,$ts);", parameters, transaction);
        }
    }
}
