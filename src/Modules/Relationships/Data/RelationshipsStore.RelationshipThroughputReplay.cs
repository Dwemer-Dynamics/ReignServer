using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static List<Dictionary<string, object>> SelectConceptions6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conceptions;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions21(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_director_actions;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamic(ReignDbConnection connection, object row, object table, object filter, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT md5(COALESCE(string_agg(value,'' ORDER BY value),'')) AS hash FROM (
 SELECT md5((" + row + ")::text) AS value FROM " + table + " t" + filter + ") rows;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamic2(ReignDbConnection connection, object table, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM " + table + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipConceptionCommitments5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_conception_commitments;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipMarriageReservations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_marriage_reservations;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipAffairCommitments3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_affair_commitments;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairLifecycle11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_lifecycle WHERE marriage_action_id<>'' OR divorce_action_id<>'' OR conception_action_id<>'';", parameters, transaction);
        }

        internal static void InsertRelationshipPersonalities3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_personalities(hero_id,mbti_type,title,description,source,assignment_day,template_version,traits_json,created_ts,updated_ts)
SELECT hero_id,CASE mod(CAST(substring(hero_id FROM 8) AS integer),4) WHEN 0 THEN 'ENFP' WHEN 1 THEN 'ISTJ' WHEN 2 THEN 'INTJ' ELSE 'ESFJ' END,
'Replay personality','Replay fixture','persisted',1,1,'{}',1,1 FROM identity_roster;", parameters, transaction);
        }

        internal static void InsertNotableMbtiProfiles(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO notable_mbti_profiles(hero_id,mbti_type,title,description,foundation_json,percentages_json,native_traits_json,template_version,first_observed_day,native_sync_status,updated_ts)
SELECT hero_id,CASE mod(CAST(substring(hero_id FROM 8) AS integer),4) WHEN 0 THEN 'ENFP' WHEN 1 THEN 'ISTJ' WHEN 2 THEN 'INTJ' ELSE 'ESFJ' END,
'Replay personality','Replay fixture','{}',$percentages,$native,1,1,'synchronized',1 FROM identity_roster WHERE is_notable=1;", parameters, transaction);
        }

        internal static void SelectNamed(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"WITH numbers AS (
 SELECT a,mod(a+101*d,4305) AS b FROM generate_series(0,4304) a CROSS JOIN generate_series(1,40) d
 UNION SELECT a,a+d FROM generate_series(0,4304) a CROSS JOIN generate_series(1,3) d WHERE a+d<4305
), pairs AS (SELECT a,b FROM numbers WHERE a<b ORDER BY a,b LIMIT $count), named AS (
 SELECT 'replay_'||lpad(a::text,4,'0') AS a,'replay_'||lpad(b::text,4,'0') AS b,
 CASE WHEN b-a<=3 AND mod(a+b,7)=0 THEN 40 ELSE 0 END AS affinity FROM pairs)
INSERT INTO relationship_pair_chemistry(pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,affinity_a_to_b,affinity_b_to_a,first_day,last_day,last_decay_period,updated_ts)
SELECT named.a||'|'||named.b,named.a,named.b,pa.mbti_type,pb.mbti_type,affinity,affinity,1,999,199,1 FROM named
JOIN relationship_personalities pa ON pa.hero_id=named.a JOIN relationship_personalities pb ON pb.hero_id=named.b;", parameters, transaction);
        }

        internal static void InsertRelationshipPairLifecycle2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_lifecycle(pair_key,hero_a_id,hero_b_id,last_processed_day,version,updated_ts)
SELECT pair_key,hero_a_id,hero_b_id,999,$version,1 FROM relationship_pair_chemistry;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairLifecycle10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_lifecycle SET flirt_attempted=1,flirt_passed=0
WHERE mod(CAST(substring(hero_a_id FROM 8) AS integer)+CAST(substring(hero_b_id FROM 8) AS integer),29)=0;", parameters, transaction);
        }

        internal const string SelectPgStatSslSql = "SELECT ssl FROM pg_stat_ssl WHERE pid=pg_backend_pid();";

        internal const string SelectSql4 = "SELECT length(@payload);";
    }
}
