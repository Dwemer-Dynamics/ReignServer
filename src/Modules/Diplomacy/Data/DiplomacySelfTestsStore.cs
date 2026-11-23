using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Diplomacy module's SQL statements used only by its self-tests (verification build only); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class DiplomacySelfTestStore
    {
        internal static void InsertRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,affinity_a_to_b,affinity_b_to_a,
tag_a_to_b,tag_b_to_a,first_day,last_day,projected_native_relation,updated_ts)
VALUES('npc|player','npc','player','INTJ','ESFP',18,25,'neutral','neutral',1,1,18,1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT memory_id FROM memories ORDER BY memory_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT visibility,known_by_json,summary FROM events WHERE event_id='clan_accord:main:formed:test';", parameters, transaction);
        }

        internal static void UpdateClanAccordEventReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE clan_accord_event_receipts SET memory_done=0 WHERE event_id='formed:test';", parameters, transaction);
        }

        internal static void DeleteKnowledgeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM knowledge_receipts WHERE event_id='clan_accord:main:formed:test' AND npc_id='clan_leader';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectKnowledgeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT receipt_id FROM knowledge_receipts WHERE event_id='clan_accord:main:formed:test' AND npc_id='clan_leader' AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectClanAccordEventReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT memory_done FROM clan_accord_event_receipts WHERE event_id='formed:test';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT affinity_a_to_b,affinity_b_to_a FROM relationship_pair_chemistry WHERE pair_key='npc|player';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT pair_key FROM relationship_native_targets WHERE pair_key='npc|player';", parameters, transaction);
        }
    }
}
