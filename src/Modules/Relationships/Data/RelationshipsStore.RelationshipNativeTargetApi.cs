using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static void UpdateRelationshipNativeTargets6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_native_targets target SET
target_relation=pair.projected_native_relation,requires_observation=1,status='pending',
revision=target.revision+1,claimed_ts=0,last_error='projection_changed_requires_observation',updated_ts=$ts
FROM relationship_pair_chemistry pair WHERE pair.pair_key=target.pair_key
AND pair.native_action_pending=1 AND pair.projected_native_relation<>target.target_relation;", parameters, transaction);
        }

        internal static void InsertOrIgnoreRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,world_day,last_sync_day,
attempt_count,claimed_ts,last_error,revision,updated_ts,requires_observation)
SELECT pair.pair_key,pair.hero_a_id,pair.hero_b_id,pair.projected_native_relation,pair.projected_native_relation,
'pending',pair.last_day,-1000,0,0,'native_observation_required',1,$ts,1
FROM relationship_pair_chemistry pair
WHERE pair.native_action_pending=1 AND NOT EXISTS (
 SELECT 1 FROM relationship_native_targets target WHERE target.pair_key=pair.pair_key)
ON CONFLICT(pair_key) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipNativeTargets3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_native_targets
WHERE target_relation=observed_relation AND requires_observation=0;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry23(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
native_action_pending=0,native_action_id='',updated_ts=$ts
WHERE pair_key IN (
    SELECT pair_key FROM relationship_native_targets
    WHERE target_relation=observed_relation AND requires_observation=0
);", parameters, transaction);
        }

        internal static void DeleteRelationshipNativeTargets5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
DELETE FROM relationship_native_targets
WHERE target_relation=observed_relation AND requires_observation=0;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeTargets9(ReignDbConnection connection, object limit, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,world_day,revision
FROM relationship_native_targets n
WHERE (target_relation<>observed_relation OR requires_observation=1)
AND (status='pending' OR (status='claimed' AND claimed_ts<$expired))
ORDER BY
CASE
  WHEN EXISTS (
    SELECT 1 FROM relationship_pair_provenance p
    WHERE p.pair_key=n.pair_key AND p.active=1 AND p.required=1
    AND p.source IN ('kingdom_leadership','ruler_network')
  ) THEN 0
  WHEN EXISTS (
    SELECT 1 FROM relationship_pair_provenance p
    WHERE p.pair_key=n.pair_key AND p.active=1
    AND p.source IN ('family','marriage','romance')
  ) OR EXISTS (
    SELECT 1 FROM relationship_pair_lifecycle l
    WHERE l.pair_key=n.pair_key
    AND (l.married=1 OR l.lover_active=1 OR l.affair_active=1)
  ) THEN 1
  WHEN EXISTS (
    SELECT 1 FROM relationship_director_actions a
    WHERE a.status IN ('pending','claimed')
    AND ((a.actor_id=n.hero_a_id AND a.target_id=n.hero_b_id)
      OR (a.actor_id=n.hero_b_id AND a.target_id=n.hero_a_id))
  ) THEN 2
  ELSE 3
END,
world_day,pair_key LIMIT " + limit + ";", parameters, transaction);
        }

        internal static void UpdateRelationshipNativeTargets7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_native_targets
SET status='claimed',claimed_ts=$ts,updated_ts=$ts
WHERE pair_key=$pair AND revision=$revision
AND (target_relation<>observed_relation OR requires_observation=1)
AND (status='pending' OR (status='claimed' AND claimed_ts<$expired));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeTargets10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT target.pair_key,target.revision
FROM relationship_native_targets AS target
JOIN jsonb_to_recordset(CAST($rows AS jsonb))
 AS x(pair text,revision integer)
ON target.pair_key=x.pair AND target.revision=x.revision;", parameters, transaction);
        }

        internal static void DeleteRelationshipNativeTargets6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM relationship_native_targets WHERE pair_key=$pair AND revision=$revision;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry24(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
native_action_pending=0,native_action_id='',last_native_sync_day=$day,updated_ts=$ts
WHERE pair_key=$pair AND projected_native_relation=$observed;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry25(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
native_action_pending=0,native_action_id='',updated_ts=$ts
WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateRelationshipNativeTargets8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_native_targets SET
status='pending',observed_relation=$observed,attempt_count=attempt_count+1,
claimed_ts=0,last_error=$error,updated_ts=$ts
WHERE pair_key=$pair AND revision=$revision;", parameters, transaction);
        }

        internal static string UpdateRelationshipNativeTargetsSql(object ts, object ts2, object part)
        {
            return @"
UPDATE relationship_native_targets AS target SET
status='claimed',claimed_ts=" + ts + @",updated_ts=" + ts2 + @"
FROM jsonb_to_recordset(@rows) AS x(pair text,revision integer)
WHERE target.pair_key=x.pair
AND target.revision=x.revision
AND (target.target_relation<>target.observed_relation OR target.requires_observation=1)
AND (target.status='pending'
 OR (target.status='claimed' AND target.claimed_ts<"
                            + (part) + "));";
        }

        internal static string UpdateRelationshipPairChemistrySql(object part, object ts)
        {
            return @"
UPDATE relationship_pair_chemistry AS pair SET
native_action_pending=0,native_action_id='',
last_native_sync_day=" + part
                                + ",updated_ts=" + ts + @"
FROM jsonb_to_recordset(@rows)
 AS x(pair text,revision integer,status text,observed integer,error text)
WHERE pair.pair_key=x.pair
AND pair.projected_native_relation=x.observed;";
        }

        internal const string DeleteRelationshipNativeTargetsSql3 = @"
DELETE FROM relationship_native_targets AS target
USING jsonb_to_recordset(@rows)
 AS x(pair text,revision integer,status text,observed integer,error text)
WHERE target.pair_key=x.pair
AND target.revision=x.revision;";

        internal static string UpdateRelationshipPairChemistrySql2(object ts)
        {
            return @"
UPDATE relationship_pair_chemistry AS pair SET
native_action_pending=0,native_action_id='',updated_ts=" + ts + @"
FROM jsonb_to_recordset(@rows)
 AS x(pair text,revision integer,status text,observed integer,error text)
WHERE pair.pair_key=x.pair;";
        }

        internal static string UpdateRelationshipNativeTargetsSql2(object ts)
        {
            return @"
UPDATE relationship_native_targets AS target SET
status='pending',observed_relation=x.observed,
attempt_count=target.attempt_count+1,claimed_ts=0,
last_error=x.error,updated_ts=" + ts + @"
FROM jsonb_to_recordset(@rows)
 AS x(pair text,revision integer,status text,observed integer,error text)
WHERE target.pair_key=x.pair
AND target.revision=x.revision;";
        }
    }
}
