using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Relationships module's SQL statements; every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class RelationshipsStore
    {
        internal static List<Dictionary<string, object>> SelectAmbientRelationshipRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM ambient_relationship_runs WHERE campaign_id=$campaign AND world_day=$day LIMIT 1;", parameters, transaction);
        }

        internal static void Begin(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "BEGIN IMMEDIATE;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbientRelationshipExposure(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM ambient_relationship_exposure WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertAmbientRelationshipExposure(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO ambient_relationship_exposure(pair_key,hero_a_id,hero_b_id,first_day,last_day,consecutive_days,weighted_exposure,last_context_kind,last_context_id,compatibility_version,compatibility_json,pending_native_signal,last_native_sync_day,native_action_pending,native_action_id,updated_ts)
VALUES($pair,$a,$b,$first,$last,$consecutive,$weighted,$kind,$context,$version,$compatibility,$signal,$nativeDay,$pending,$action,$ts)
ON CONFLICT(pair_key) DO UPDATE SET last_day=$last,consecutive_days=$consecutive,weighted_exposure=$weighted,last_context_kind=$kind,last_context_id=$context,compatibility_version=$version,compatibility_json=$compatibility,pending_native_signal=$signal,native_action_pending=$pending,native_action_id=$action,updated_ts=$ts;", parameters, transaction);
        }

        internal static void InsertAmbientRelationshipRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO ambient_relationship_runs(run_id,campaign_id,world_day,status,group_count,pair_count,direction_count,positive_drift,negative_drift,native_changes,duration_ms,summary_json,created_ts)
VALUES($id,$campaign,$day,'completed',$groups,$pairs,$directions,$positive,$negative,$native,$duration,$summary,$ts);", parameters, transaction);
        }

        internal static void Commit(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "COMMIT;", parameters, transaction);
        }

        internal static void Rollback(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "ROLLBACK;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbientRelationshipRuns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM ambient_relationship_runs ORDER BY world_day DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbientRelationshipExposure2(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM ambient_relationship_exposure ORDER BY last_day DESC,weighted_exposure DESC LIMIT " + part + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbientRelationshipChanges(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM ambient_relationship_changes ORDER BY world_day DESC,created_ts DESC LIMIT " + part + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbientRelationshipExposure3(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT pair_key,hero_a_id,hero_b_id,pending_native_signal,native_action_pending,native_action_id,last_native_sync_day FROM ambient_relationship_exposure WHERE ABS(pending_native_signal)>=0.01 OR native_action_pending=1 ORDER BY ABS(pending_native_signal) DESC LIMIT " + part + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT pairs.pair_key,
pairs.affinity_a_to_b,pairs.affinity_b_to_a,
pairs.chance_a_to_b,pairs.chance_b_to_a
FROM relationship_pair_chemistry pairs
JOIN jsonb_array_elements_text(CAST($pairs AS jsonb)) requested
  ON requested.value=pairs.pair_key;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT pair_key,
affinity_a_to_b,affinity_b_to_a,chance_a_to_b,chance_b_to_a
FROM relationship_pair_chemistry;", parameters, transaction);
        }

        internal static void UpdateRelationships(ReignDbConnection connection, object facet, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationships SET " + facet + "=$value,updated_ts=$ts,last_updated_from_json=$source WHERE subject_id=$subject AND target_id=$target;", parameters, transaction);
        }

        internal static void UpsertAmbientRelationshipChanges(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO ambient_relationship_changes(change_id,run_id,pair_key,subject_id,target_id,facet,old_value,delta,new_value,equilibrium,world_day,context_kind,context_id,compatibility_json,created_ts)
VALUES($id,$run,$pair,$subject,$target,$facet,$old,$delta,$new,$equilibrium,$day,$kind,$context,'{}',$ts)
ON CONFLICT(pair_key,subject_id,target_id,facet) DO UPDATE SET
run_id=excluded.run_id,old_value=excluded.old_value,delta=excluded.delta,new_value=excluded.new_value,
equilibrium=excluded.equilibrium,world_day=excluded.world_day,context_kind=excluded.context_kind,
context_id=excluded.context_id,compatibility_json='{}',created_ts=excluded.created_ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipMilestones(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_milestones WHERE subject_id=$subject AND target_id=$target AND status='active' AND kind IN ('betrayed','nemesis','estranged','heartbroken');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipPressures(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_pressures WHERE subject_id=$subject AND target_id=$target AND status='active' AND intensity>=50;", parameters, transaction);
        }

        internal static void InsertRelationshipDirectorActions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_director_actions(director_action_id,action_type,status,world_day,actor_id,target_id,payload_json,created_ts)
VALUES($id,'native_relation','pending',$day,$actor,$target,$payload,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountAmbientRelationshipChanges(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM ambient_relationship_changes;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbientRelationshipChanges2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COALESCE(AVG(ABS(delta)),0) AS value FROM ambient_relationship_changes WHERE world_day=1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationships(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT loyalty,fear,envy,attraction,jealousy,dependence,debt FROM relationships WHERE subject_id=$a AND target_id=$b LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbientRelationshipChanges3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COALESCE(AVG(ABS(delta)),0) AS value FROM ambient_relationship_changes WHERE world_day=3;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAmbientRelationshipExposure4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT weighted_exposure,consecutive_days FROM ambient_relationship_exposure WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipDirectorActions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_director_actions WHERE action_type='native_relation' AND actor_id=$a AND target_id=$b;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationships2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT trust FROM relationships WHERE subject_id=$a AND target_id=$b LIMIT 1;", parameters, transaction);
        }

        internal static void InsertRelationshipMilestones(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_milestones(milestone_id,subject_id,target_id,kind,status,stability,strength,reason,created_event_id,last_event_id,evidence_json,created_day,last_meaningful_day,updated_ts,payload_json)
VALUES($id,$a,$b,'betrayed','active',80,80,'ambient self-test','','','[]',18,18,$ts,'{}');", parameters, transaction);
        }
    }
}
