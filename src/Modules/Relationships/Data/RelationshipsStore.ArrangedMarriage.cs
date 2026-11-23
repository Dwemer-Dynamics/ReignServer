using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static List<Dictionary<string, object>> SelectMarriageLeaderRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT roll_id FROM marriage_leader_rolls WHERE timeline_id=$t AND period_index=$p AND leader_id=$l;", parameters, transaction);
        }

        internal static void InsertMarriageLeaderRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO marriage_leader_rolls(roll_id,timeline_id,period_index,leader_id,clan_id,world_day,chance,roll,urgency,status,payload_json,created_ts)
VALUES($id,$t,$p,$l,$clan,$day,$chance,$roll,$u,$status,'{}',$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectArrangedMarriageRefusals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT refusal_key FROM arranged_marriage_refusals WHERE refusal_key=$k AND timeline_id=$t;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMarriageEvaluations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM marriage_evaluations WHERE timeline_id=$t AND status IN ('approved','player_court_pending','awaiting_diplomacy','court_resolving','recovery');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMarriageEvaluations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT hero_a_id,hero_b_id,payload_json FROM marriage_evaluations WHERE timeline_id=$t AND status IN ('approved','player_court_pending','awaiting_diplomacy','court_resolving','recovery','executed');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectArrangedMarriageEffects(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT effect_id FROM arranged_marriage_effects WHERE effect_id=$k;", parameters, transaction);
        }

        internal static void InsertArrangedMarriageEffects(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO arranged_marriage_effects(effect_id,timeline_id,proposal_id,delta) VALUES($k,$t,$p,$d);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMarriageEvaluations3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT evaluation_id,payload_json,world_day FROM marriage_evaluations WHERE timeline_id=$t AND status IN ('approved','awaiting_diplomacy','player_court_pending','court_resolving');", parameters, transaction);
        }

        internal static void UpdateMarriageEvaluations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE marriage_evaluations SET status='legacy_quote_required' WHERE evaluation_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT director_action_id,payload_json FROM relationship_director_actions WHERE action_type='marriage' AND status IN ('pending','claimed');", parameters, transaction);
        }

        internal static void UpdateRelationshipDirectorActions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_director_actions SET status='obsolete' WHERE director_action_id=$id;", parameters, transaction);
        }

        internal static void DeleteRelationshipMarriageReservations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM relationship_marriage_reservations WHERE director_action_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSqliteMaster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT name FROM sqlite_master WHERE type='table' AND name='court_matters';", parameters, transaction);
        }

        internal static void UpdateCourtMatters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE court_matters SET state='invalidated' WHERE timeline_id=$t AND source_key=$s AND state NOT IN ('resolved','refused','expired','invalidated');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPgAttribute(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT a.attname AS name FROM pg_attribute a
JOIN pg_class t ON t.oid=a.attrelid JOIN pg_namespace n ON n.oid=t.relnamespace
WHERE n.nspname=current_schema() AND t.relname=$table AND a.attnum>0 AND NOT a.attisdropped;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPgIndex(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT index_row.relname AS name FROM pg_index i
JOIN pg_class t ON t.oid=i.indrelid JOIN pg_namespace n ON n.oid=t.relnamespace
JOIN pg_class index_row ON index_row.oid=i.indexrelid
WHERE n.nspname=current_schema() AND t.relname=$table AND NOT i.indisprimary;", parameters, transaction);
        }

        internal static void Lock(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "LOCK TABLE world_test_native_heartbeats IN ACCESS EXCLUSIVE MODE;", parameters, transaction);
        }

        internal static void Set(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "SET lock_timeout='50ms';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldTestNativeHeartbeats(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM world_test_native_heartbeats;", parameters, transaction);
        }

        internal static void Set2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "SET lock_timeout=0;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPgIndexes(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT indexname FROM pg_indexes
WHERE schemaname=current_schema() AND indexname='idx_world_test_heartbeat_day';", parameters, transaction);
        }

        internal static void InsertRelationshipPersonalities(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_personalities(hero_id,mbti_type,title,description,source,assignment_day,template_version,traits_json,created_ts,updated_ts)
VALUES($id,'INTJ','Fixture','','fixture',1,1,'{}',1,1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectArrangedMarriageEffects2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT effect_id FROM arranged_marriage_effects LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry WHERE pair_key='proposer|respondent';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectArrangedMarriageEffects3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT effect_id FROM arranged_marriage_effects;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectArrangedMarriagePeriods(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM arranged_marriage_periods WHERE timeline_id='main';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMarriageEvaluations4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT evaluation_id FROM marriage_evaluations WHERE timeline_id='fixture';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMarriageLeaderRolls2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT roll_id FROM marriage_leader_rolls WHERE timeline_id='fixture';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectArrangedMarriageEffects4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT effect_id FROM arranged_marriage_effects WHERE timeline_id='fixture';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT director_action_id FROM relationship_director_actions WHERE actor_id='son' AND target_id='daughter';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMarriageEvaluations5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT route,status,hero_a_id,hero_b_id,clan_a_id,clan_b_id,leader_a_id,leader_b_id,world_day,score,leader_a_approved,leader_b_approved,resentment_a,resentment_b,payload_json FROM marriage_evaluations WHERE timeline_id='fixture' ORDER BY hero_a_id,hero_b_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMarriageLeaderRolls3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT leader_id,clan_id,chance,roll,urgency,status FROM marriage_leader_rolls WHERE timeline_id='fixture' ORDER BY leader_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectArrangedMarriageRefusals2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT refusal_key FROM arranged_marriage_refusals WHERE timeline_id='fixture' ORDER BY refusal_key;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT pair_key,affinity_a_to_b,affinity_b_to_a FROM relationship_pair_chemistry ORDER BY pair_key;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMarriageEvaluations6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM marriage_evaluations WHERE timeline_id='fixture';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_director_actions WHERE actor_id='son' OR target_id='daughter';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$p;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT director_action_id FROM relationship_director_actions WHERE actor_id='paid_son';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectArrangedMarriageEffects5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM arranged_marriage_effects WHERE timeline_id='paid_fixture';", parameters, transaction);
        }

        internal static void UpdateDynamic(ReignDbConnection connection, object table, object column, object column2, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE "+table+" SET "+column+"="+column2+" WHERE 1=0;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPgLocks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT l.pid,l.locktype,l.mode,l.granted,a.state,a.wait_event_type,a.wait_event
FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid
WHERE l.pid<>pg_backend_pid() AND (l.locktype='advisory' OR l.relation IN (
SELECT c.oid FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=current_schema()
AND c.relname IN ('arranged_marriage_periods','marriage_leader_rolls','arranged_marriage_effects','relationship_pair_chemistry','relationship_pair_provenance','relationship_native_targets')))
ORDER BY l.pid,l.locktype,l.mode LIMIT 24;", parameters, transaction);
        }

        internal const string SelectSql = "SELECT pg_try_advisory_xact_lock(hashtextextended(current_schema(), 9137));";
    }
}
