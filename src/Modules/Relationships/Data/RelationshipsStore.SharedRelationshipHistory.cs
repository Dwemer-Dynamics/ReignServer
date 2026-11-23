using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static void UpsertSchemaMeta16(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
INSERT INTO schema_meta(key,value)
VALUES('postgresql_shared_relationship_history_schema_revision',$revision)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSharedRelationshipHistoryChapters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM shared_relationship_history_chapters
WHERE pair_key=$pair ORDER BY chapter_ordinal DESC LIMIT 100;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSharedRelationshipHistoryChapters2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM shared_relationship_history_chapters
ORDER BY created_ts DESC LIMIT 250;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipIncidents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT incident_id,kind,world_day,summary
FROM relationship_incidents WHERE pair_key=$pair ORDER BY world_day DESC,created_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSharedRelationshipHistoryChapters3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COALESCE(MAX(chapter_ordinal),0) value
FROM shared_relationship_history_chapters WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void InsertSharedRelationshipHistoryChapters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO shared_relationship_history_chapters(
history_id,pair_key,hero_a_id,hero_b_id,chapter_ordinal,state_fingerprint,
objective_summary,a_interpretation,b_interpretation,canon_status,source_kind,source_basis_json,
generation_status,provider,model,created_world_day,created_ts,updated_ts)
VALUES($id,$pair,$a,$b,$ordinal,$fingerprint,$objective,$aView,$bView,
'soft_relational_canon','lazy_dialogue_generation',$basis,$status,$provider,$model,$day,$ts,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSharedRelationshipHistoryChapters4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM shared_relationship_history_chapters WHERE history_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSharedRelationshipHistoryChapters5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT *
FROM shared_relationship_history_chapters WHERE hero_a_id=$id OR hero_b_id=$id
ORDER BY created_world_day DESC,chapter_ordinal DESC LIMIT 500;", parameters, transaction);
        }

        internal static void InsertRelationshipPairChemistry5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,
affinity_a_to_b,affinity_b_to_a,tag_a_to_b,tag_b_to_a,
first_day,last_day,projected_native_relation,updated_ts)
VALUES('fixture_a|fixture_b','fixture_a','fixture_b','INTJ','ESFP',
5,-3,'neutral','neutral',1,1,1,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry20(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry WHERE pair_key='fixture_a|fixture_b' LIMIT 1;", parameters, transaction);
        }
    }
}
