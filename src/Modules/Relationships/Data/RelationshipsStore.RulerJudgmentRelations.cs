using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static List<Dictionary<string, object>> SelectRelationshipRulerReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT terms_hash FROM relationship_ruler_receipts WHERE timeline_id=$timeline AND operation_id=$id;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry26(ReignDbConnection connection, object column, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_pair_chemistry SET " + column + "=$value,state_revision=state_revision+1 WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void InsertRelationshipRulerReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO relationship_ruler_receipts(timeline_id,operation_id,terms_hash) VALUES($timeline,$id,$hash);", parameters, transaction);
        }
    }
}
