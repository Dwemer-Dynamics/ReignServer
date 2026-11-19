using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static void SelectX(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(CAST($rows AS jsonb)) AS r(
        campaign text,timeline text,pair text,source text,required integer,
        day double precision,details text,ts bigint)
)
INSERT INTO relationship_pair_provenance(
campaign_id,timeline_id,pair_key,source,active,required,first_day,last_day,
details_json,updated_ts)
SELECT campaign,timeline,pair,source,1,required,day,day,details,ts FROM x
ON CONFLICT(campaign_id,timeline_id,pair_key,source) DO UPDATE SET
active=1,required=excluded.required,last_day=excluded.last_day,
details_json=excluded.details_json,updated_ts=excluded.updated_ts
WHERE relationship_pair_provenance.active<>1
OR relationship_pair_provenance.required<>excluded.required
OR relationship_pair_provenance.last_day<>excluded.last_day
OR relationship_pair_provenance.details_json<>excluded.details_json;", parameters, transaction);
        }

        internal static void SelectX2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(CAST($rows AS jsonb)) AS r(
        pair text,a text,b text,typea text,typeb text,baseab integer,
        baseba integer,chanceab integer,chanceba integer,affinityab integer,
        affinityba integer,standinga integer,standingb integer,
        effectiveab integer,effectiveba integer,tagab text,tagba text,
        day integer,last integer,shard integer,projected integer,
        pending integer,action text,version integer,seedversion integer,
        baseline integer,signab integer,signba integer,magnitudeab integer,
        magnitudeba integer,deltaab integer,deltaba integer,eligible integer,
        provenance text,decayperiod integer,ts bigint)
)
INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,base_chance_a_to_b,
base_chance_b_to_a,chance_a_to_b,chance_b_to_a,affinity_a_to_b,
affinity_b_to_a,social_modifier_a_to_b,social_modifier_b_to_a,
effective_affinity_a_to_b,effective_affinity_b_to_a,tag_a_to_b,tag_b_to_a,
first_day,last_day,processing_shard,last_presence_day,
projected_native_relation,native_action_pending,native_action_id,
compatibility_version,opening_seed_version,opening_seed_baseline,
opening_seed_chance_a_to_b,opening_seed_chance_b_to_a,
opening_seed_sign_roll_a_to_b,opening_seed_sign_roll_b_to_a,
opening_seed_magnitude_roll_a_to_b,opening_seed_magnitude_roll_b_to_a,
opening_seed_delta_a_to_b,opening_seed_delta_b_to_a,
opening_seed_affinity_a_to_b,opening_seed_affinity_b_to_a,
 opening_seed_eligible,opening_seed_provenance,last_decay_period,
 state_revision,updated_ts)
SELECT pair,a,b,typea,typeb,baseab,baseba,chanceab,chanceba,affinityab,
affinityba,standingb,standinga,effectiveab,effectiveba,tagab,tagba,
day,last,shard,-1,projected,pending,action,version,seedversion,baseline,
chanceab,chanceba,signab,signba,magnitudeab,magnitudeba,deltaab,deltaba,
 affinityab,affinityba,eligible,provenance,decayperiod,1,ts
FROM x
ON CONFLICT(pair_key) DO UPDATE SET
mbti_a=excluded.mbti_a,mbti_b=excluded.mbti_b,
base_chance_a_to_b=excluded.base_chance_a_to_b,
base_chance_b_to_a=excluded.base_chance_b_to_a,
chance_a_to_b=excluded.chance_a_to_b,chance_b_to_a=excluded.chance_b_to_a,
affinity_a_to_b=excluded.affinity_a_to_b,
affinity_b_to_a=excluded.affinity_b_to_a,
social_modifier_a_to_b=excluded.social_modifier_a_to_b,
social_modifier_b_to_a=excluded.social_modifier_b_to_a,
effective_affinity_a_to_b=excluded.effective_affinity_a_to_b,
effective_affinity_b_to_a=excluded.effective_affinity_b_to_a,
tag_a_to_b=excluded.tag_a_to_b,tag_b_to_a=excluded.tag_b_to_a,
last_day=excluded.last_day,processing_shard=excluded.processing_shard,
projected_native_relation=excluded.projected_native_relation,
native_action_pending=excluded.native_action_pending,
native_action_id=excluded.native_action_id,
compatibility_version=excluded.compatibility_version,
opening_seed_version=excluded.opening_seed_version,
opening_seed_baseline=excluded.opening_seed_baseline,
opening_seed_chance_a_to_b=excluded.opening_seed_chance_a_to_b,
opening_seed_chance_b_to_a=excluded.opening_seed_chance_b_to_a,
opening_seed_sign_roll_a_to_b=excluded.opening_seed_sign_roll_a_to_b,
opening_seed_sign_roll_b_to_a=excluded.opening_seed_sign_roll_b_to_a,
opening_seed_magnitude_roll_a_to_b=excluded.opening_seed_magnitude_roll_a_to_b,
opening_seed_magnitude_roll_b_to_a=excluded.opening_seed_magnitude_roll_b_to_a,
opening_seed_delta_a_to_b=excluded.opening_seed_delta_a_to_b,
opening_seed_delta_b_to_a=excluded.opening_seed_delta_b_to_a,
opening_seed_affinity_a_to_b=excluded.opening_seed_affinity_a_to_b,
opening_seed_affinity_b_to_a=excluded.opening_seed_affinity_b_to_a,
 opening_seed_eligible=excluded.opening_seed_eligible,
 opening_seed_provenance=excluded.opening_seed_provenance,
 last_decay_period=excluded.last_decay_period,
state_revision=relationship_pair_chemistry.state_revision+1,
updated_ts=excluded.updated_ts
WHERE relationship_pair_chemistry.opening_seed_version
    < excluded.opening_seed_version;", parameters, transaction);
        }

        internal static void SelectX3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(CAST($rows AS jsonb)) AS r(
        pair text,actor text,targethero text,targetrelation integer,
        observed integer,day double precision,ts bigint)
)
INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,
world_day,last_sync_day,attempt_count,claimed_ts,last_error,updated_ts)
SELECT pair,actor,targethero,targetrelation,observed,'pending',day,
-1000,0,0,'',ts FROM x
ON CONFLICT(pair_key) DO UPDATE SET
hero_a_id=excluded.hero_a_id,hero_b_id=excluded.hero_b_id,
observed_relation=excluded.observed_relation,world_day=excluded.world_day,
status='pending',
revision=CASE
    WHEN relationship_native_targets.target_relation=excluded.target_relation
    THEN relationship_native_targets.revision
    ELSE relationship_native_targets.revision+1 END,
attempt_count=CASE
    WHEN relationship_native_targets.target_relation=excluded.target_relation
    THEN relationship_native_targets.attempt_count ELSE 0 END,
claimed_ts=0,target_relation=excluded.target_relation,last_error='',
updated_ts=excluded.updated_ts;", parameters, transaction);
        }

        internal static void DeleteRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
DELETE FROM relationship_native_targets
WHERE pair_key IN (
    SELECT value FROM jsonb_array_elements_text(CAST($pairs AS jsonb)));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry;", parameters, transaction);
        }

        internal static void UpsertRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,world_day,last_sync_day,
attempt_count,claimed_ts,last_error,updated_ts,requires_observation)
VALUES($pair,$actor,$targetHero,$targetRelation,$observed,'pending',$day,-1000,0,0,'',$ts,$requiresObservation)
ON CONFLICT(pair_key) DO UPDATE SET
hero_a_id=excluded.hero_a_id,hero_b_id=excluded.hero_b_id,
observed_relation=excluded.observed_relation,world_day=excluded.world_day,
requires_observation=CASE WHEN relationship_native_targets.requires_observation=1 OR excluded.requires_observation=1 THEN 1 ELSE 0 END,
status='pending',
revision=CASE WHEN relationship_native_targets.target_relation=excluded.target_relation
    THEN relationship_native_targets.revision ELSE relationship_native_targets.revision+1 END,
attempt_count=CASE
    WHEN relationship_native_targets.target_relation=excluded.target_relation
    THEN relationship_native_targets.attempt_count ELSE 0
END,
claimed_ts=0,
target_relation=excluded.target_relation,last_error='',updated_ts=excluded.updated_ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> UpdateRelationshipPairChemistry2(ReignDbConnection connection, object affinityColumn, object affinityColumn2, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"UPDATE relationship_pair_chemistry SET "
                + affinityColumn + @"=MAX(-100,MIN(100,"
                + affinityColumn2 + @"+$delta)),
last_day=MAX(last_day,$day),last_context_kind=$kind,
last_context_id=$context,state_revision=state_revision+1,
updated_ts=$ts
WHERE pair_key=$pair
RETURNING *;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
effective_affinity_a_to_b=$effectiveAB,
effective_affinity_b_to_a=$effectiveBA,
tag_a_to_b=$tagAB,tag_b_to_a=$tagBA
WHERE pair_key=$pair AND state_revision=$revision;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT observed_relation,requires_observation FROM relationship_native_targets WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,base_chance_a_to_b,base_chance_b_to_a,
chance_a_to_b,chance_b_to_a,affinity_a_to_b,affinity_b_to_a,social_modifier_a_to_b,social_modifier_b_to_a,
effective_affinity_a_to_b,effective_affinity_b_to_a,tag_a_to_b,tag_b_to_a,
first_day,last_day,projected_native_relation,native_action_pending,native_action_id,
compatibility_version,updated_ts)
VALUES($pair,$a,$b,$typeA,$typeB,$baseAB,$baseBA,$chanceAB,$chanceBA,$affinityAB,$affinityBA,0,0,$affinityAB,$affinityBA,
$tagAB,$tagBA,$day,$day,$projected,$pending,$action,$version,$ts)
ON CONFLICT(pair_key) DO UPDATE SET
affinity_a_to_b=$affinityAB,affinity_b_to_a=$affinityBA,
social_modifier_a_to_b=0,social_modifier_b_to_a=0,
effective_affinity_a_to_b=$affinityAB,effective_affinity_b_to_a=$affinityBA,
tag_a_to_b=$tagAB,tag_b_to_a=$tagBA,
last_day=MAX(relationship_pair_chemistry.last_day,$day),
projected_native_relation=$projected,native_action_pending=$pending,
native_action_id=$action,updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPersonalities(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT mbti_type FROM relationship_personalities WHERE hero_id=$hero LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='mbti_native_relation_bulk_projection_v1' LIMIT 1;", parameters, transaction);
        }

        internal static void Savepoint(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "SAVEPOINT compact_native_relation_backlog;", parameters, transaction);
        }

        internal static void UpdateRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_native_targets SET status='pending',claimed_ts=0,last_error='';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountSqliteMaster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM sqlite_master WHERE type='table' AND name='relationship_director_actions';", parameters, transaction);
        }

        internal static void UpdateRelationshipDirectorActions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_director_actions
SET status='superseded',resolved_ts=$ts,
result_json='{""status"":""superseded"",""reason"":""native_relation_bulk_projection""}'
WHERE action_type='native_relation' AND status IN ('pending','claimed');", parameters, transaction);
        }

        internal static void UpsertSchemaMeta(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('mbti_native_relation_bulk_projection_v1','1');", parameters, transaction);
        }

        internal static void Release(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "RELEASE compact_native_relation_backlog;", parameters, transaction);
        }

        internal static void Rollback2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "ROLLBACK TO compact_native_relation_backlog;", parameters, transaction);
        }

        internal const string SelectXSql = @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(@rows) AS r(
        pair text,actor text,targethero text,targetrelation integer,
        observed integer,day double precision,ts bigint,requiresobservation integer)
)
INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,
world_day,last_sync_day,attempt_count,claimed_ts,last_error,updated_ts,requires_observation)
SELECT pair,actor,targethero,targetrelation,observed,'pending',day,
-1000,0,0,'',ts,COALESCE(requiresobservation,0) FROM x
ON CONFLICT(pair_key) DO UPDATE SET
hero_a_id=excluded.hero_a_id,hero_b_id=excluded.hero_b_id,
observed_relation=excluded.observed_relation,world_day=excluded.world_day,
requires_observation=GREATEST(relationship_native_targets.requires_observation,excluded.requires_observation),
status='pending',
revision=CASE
    WHEN relationship_native_targets.target_relation
        = excluded.target_relation
    THEN relationship_native_targets.revision
    ELSE relationship_native_targets.revision+1 END,
attempt_count=CASE
    WHEN relationship_native_targets.target_relation
        = excluded.target_relation
    THEN relationship_native_targets.attempt_count ELSE 0 END,
claimed_ts=0,target_relation=excluded.target_relation,last_error='',
updated_ts=excluded.updated_ts
WHERE relationship_native_targets.target_relation
        <> excluded.target_relation
   OR relationship_native_targets.observed_relation
         <> excluded.observed_relation
   OR relationship_native_targets.requires_observation < excluded.requires_observation
   OR relationship_native_targets.status NOT IN ('pending','claimed');";

        internal const string DeleteRelationshipNativeTargetsSql = @"
DELETE FROM relationship_native_targets AS target
USING jsonb_to_recordset(@rows) AS x(pair text)
WHERE target.pair_key=x.pair;";

        internal const string UpsertRelationshipPairChemistrySql = @"
INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,axes_a_json,axes_b_json,
base_chance_a_to_b,base_chance_b_to_a,chance_a_to_b,chance_b_to_a,
affinity_a_to_b,affinity_b_to_a,signal_a_to_b,signal_b_to_a,
tag_a_to_b,tag_b_to_a,shared_tag,first_day,last_day,consecutive_days,
weighted_exposure,last_context_kind,last_context_id,last_roll_a_to_b,
last_roll_b_to_a,last_delta_a_to_b,last_delta_b_to_a,
projected_native_relation,last_native_sync_day,native_action_pending,
native_action_id,processing_shard,last_presence_day,last_batch_size,
last_presence_mask,last_decay_period,compatibility_version,state_revision,
updated_ts)
SELECT pair,a,b,typea,typeb,axesa,axesb,baseab,baseba,chanceab,chanceba,
affinityab,affinityba,signalab,signalba,tagab,tagba,shared,
firstday,lastday,consecutive,weighted,kind,contextid,rollab,rollba,
deltaab,deltaba,projected,nativeday,pending,actionid,shard,presenceday,
batchsize,presencemask,decayperiod,version,1,updatedts
FROM relationship_pair_chemistry_stage
ON CONFLICT(pair_key) DO UPDATE SET
mbti_a=excluded.mbti_a,mbti_b=excluded.mbti_b,
axes_a_json=excluded.axes_a_json,axes_b_json=excluded.axes_b_json,
base_chance_a_to_b=excluded.base_chance_a_to_b,
base_chance_b_to_a=excluded.base_chance_b_to_a,
chance_a_to_b=excluded.chance_a_to_b,
chance_b_to_a=excluded.chance_b_to_a,
affinity_a_to_b=excluded.affinity_a_to_b,
affinity_b_to_a=excluded.affinity_b_to_a,
signal_a_to_b=excluded.signal_a_to_b,
signal_b_to_a=excluded.signal_b_to_a,
tag_a_to_b=excluded.tag_a_to_b,tag_b_to_a=excluded.tag_b_to_a,
shared_tag=excluded.shared_tag,last_day=excluded.last_day,
consecutive_days=excluded.consecutive_days,
weighted_exposure=excluded.weighted_exposure,
last_context_kind=excluded.last_context_kind,
last_context_id=excluded.last_context_id,
last_roll_a_to_b=excluded.last_roll_a_to_b,
last_roll_b_to_a=excluded.last_roll_b_to_a,
last_delta_a_to_b=excluded.last_delta_a_to_b,
last_delta_b_to_a=excluded.last_delta_b_to_a,
projected_native_relation=excluded.projected_native_relation,
last_native_sync_day=excluded.last_native_sync_day,
native_action_pending=excluded.native_action_pending,
native_action_id=excluded.native_action_id,
processing_shard=excluded.processing_shard,
last_presence_day=excluded.last_presence_day,
last_batch_size=excluded.last_batch_size,
last_presence_mask=excluded.last_presence_mask,
last_decay_period=CASE
    WHEN relationship_pair_chemistry.affinity_a_to_b
            <> excluded.affinity_a_to_b
      OR relationship_pair_chemistry.affinity_b_to_a
            <> excluded.affinity_b_to_a
    THEN excluded.last_decay_period
    ELSE relationship_pair_chemistry.last_decay_period END,
compatibility_version=excluded.compatibility_version,
state_revision=relationship_pair_chemistry.state_revision+1,
updated_ts=excluded.updated_ts
WHERE (relationship_pair_chemistry.mbti_a,
       relationship_pair_chemistry.mbti_b,
       relationship_pair_chemistry.axes_a_json,
       relationship_pair_chemistry.axes_b_json,
       relationship_pair_chemistry.base_chance_a_to_b,
       relationship_pair_chemistry.base_chance_b_to_a,
       relationship_pair_chemistry.chance_a_to_b,
       relationship_pair_chemistry.chance_b_to_a,
       relationship_pair_chemistry.affinity_a_to_b,
       relationship_pair_chemistry.affinity_b_to_a,
       relationship_pair_chemistry.signal_a_to_b,
       relationship_pair_chemistry.signal_b_to_a,
       relationship_pair_chemistry.tag_a_to_b,
       relationship_pair_chemistry.tag_b_to_a,
       relationship_pair_chemistry.shared_tag,
       relationship_pair_chemistry.last_day,
       relationship_pair_chemistry.consecutive_days,
       relationship_pair_chemistry.weighted_exposure,
       relationship_pair_chemistry.last_context_kind,
       relationship_pair_chemistry.last_context_id,
       relationship_pair_chemistry.last_roll_a_to_b,
       relationship_pair_chemistry.last_roll_b_to_a,
       relationship_pair_chemistry.last_delta_a_to_b,
       relationship_pair_chemistry.last_delta_b_to_a,
       relationship_pair_chemistry.projected_native_relation,
       relationship_pair_chemistry.last_native_sync_day,
       relationship_pair_chemistry.native_action_pending,
       relationship_pair_chemistry.native_action_id,
       relationship_pair_chemistry.processing_shard,
       relationship_pair_chemistry.last_presence_day,
       relationship_pair_chemistry.last_batch_size,
       relationship_pair_chemistry.last_presence_mask,
       relationship_pair_chemistry.compatibility_version)
IS DISTINCT FROM
      (excluded.mbti_a,excluded.mbti_b,excluded.axes_a_json,
       excluded.axes_b_json,excluded.base_chance_a_to_b,
       excluded.base_chance_b_to_a,excluded.chance_a_to_b,
       excluded.chance_b_to_a,excluded.affinity_a_to_b,
       excluded.affinity_b_to_a,excluded.signal_a_to_b,
       excluded.signal_b_to_a,excluded.tag_a_to_b,
       excluded.tag_b_to_a,excluded.shared_tag,excluded.last_day,
       excluded.consecutive_days,excluded.weighted_exposure,
       excluded.last_context_kind,excluded.last_context_id,
       excluded.last_roll_a_to_b,excluded.last_roll_b_to_a,
       excluded.last_delta_a_to_b,excluded.last_delta_b_to_a,
       excluded.projected_native_relation,
       excluded.last_native_sync_day,excluded.native_action_pending,
       excluded.native_action_id,excluded.processing_shard,
       excluded.last_presence_day,excluded.last_batch_size,
       excluded.last_presence_mask,excluded.compatibility_version)
OR ((relationship_pair_chemistry.affinity_a_to_b
        <> excluded.affinity_a_to_b
     OR relationship_pair_chemistry.affinity_b_to_a
        <> excluded.affinity_b_to_a)
    AND relationship_pair_chemistry.last_decay_period
        IS DISTINCT FROM excluded.last_decay_period);";

        internal const string UpsertRelationshipPairChemistrySql2 = @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,axes_a_json,axes_b_json,base_chance_a_to_b,base_chance_b_to_a,
chance_a_to_b,chance_b_to_a,affinity_a_to_b,affinity_b_to_a,signal_a_to_b,signal_b_to_a,tag_a_to_b,tag_b_to_a,
 shared_tag,first_day,last_day,consecutive_days,weighted_exposure,last_context_kind,last_context_id,last_roll_a_to_b,
 last_roll_b_to_a,last_delta_a_to_b,last_delta_b_to_a,projected_native_relation,last_native_sync_day,native_action_pending,
 native_action_id,processing_shard,last_presence_day,last_batch_size,last_presence_mask,last_decay_period,compatibility_version,
 state_revision,updated_ts)
 VALUES($pair,$a,$b,$typeA,$typeB,$axesA,$axesB,$baseAB,$baseBA,$chanceAB,$chanceBA,$affinityAB,$affinityBA,$signalAB,
 $signalBA,$tagAB,$tagBA,$shared,$first,$last,$consecutive,$weighted,$kind,$context,$rollAB,$rollBA,$deltaAB,$deltaBA,
 $projected,$nativeDay,$pending,$action,$shard,$presenceDay,$batchSize,$presenceMask,$decayPeriod,$version,1,$ts)
ON CONFLICT(pair_key) DO UPDATE SET mbti_a=$typeA,mbti_b=$typeB,axes_a_json=$axesA,axes_b_json=$axesB,
base_chance_a_to_b=$baseAB,base_chance_b_to_a=$baseBA,chance_a_to_b=$chanceAB,chance_b_to_a=$chanceBA,
affinity_a_to_b=$affinityAB,affinity_b_to_a=$affinityBA,signal_a_to_b=$signalAB,signal_b_to_a=$signalBA,
tag_a_to_b=$tagAB,tag_b_to_a=$tagBA,shared_tag=$shared,last_day=$last,consecutive_days=$consecutive,
weighted_exposure=$weighted,last_context_kind=$kind,last_context_id=$context,last_roll_a_to_b=$rollAB,
 last_roll_b_to_a=$rollBA,last_delta_a_to_b=$deltaAB,last_delta_b_to_a=$deltaBA,
 projected_native_relation=$projected,last_native_sync_day=$nativeDay,native_action_pending=$pending,native_action_id=$action,
 processing_shard=$shard,last_presence_day=$presenceDay,last_batch_size=$batchSize,last_presence_mask=$presenceMask,
 last_decay_period=CASE WHEN relationship_pair_chemistry.affinity_a_to_b<>$affinityAB
 OR relationship_pair_chemistry.affinity_b_to_a<>$affinityBA
 THEN $decayPeriod ELSE relationship_pair_chemistry.last_decay_period END,
 compatibility_version=$version,
 state_revision=relationship_pair_chemistry.state_revision+1,updated_ts=$ts;";

        internal const string UpsertRelationshipPairChemistrySql3 = @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,base_chance_a_to_b,
base_chance_b_to_a,chance_a_to_b,chance_b_to_a,affinity_a_to_b,
affinity_b_to_a,social_modifier_a_to_b,social_modifier_b_to_a,
effective_affinity_a_to_b,effective_affinity_b_to_a,tag_a_to_b,tag_b_to_a,
first_day,last_day,processing_shard,last_presence_day,
projected_native_relation,native_action_pending,native_action_id,
compatibility_version,opening_seed_version,opening_seed_baseline,
opening_seed_chance_a_to_b,opening_seed_chance_b_to_a,
opening_seed_sign_roll_a_to_b,opening_seed_sign_roll_b_to_a,
opening_seed_magnitude_roll_a_to_b,opening_seed_magnitude_roll_b_to_a,
opening_seed_delta_a_to_b,opening_seed_delta_b_to_a,
opening_seed_affinity_a_to_b,opening_seed_affinity_b_to_a,
  opening_seed_eligible,opening_seed_provenance,last_decay_period,
  state_revision,updated_ts)
VALUES($pair,$a,$b,$typeA,$typeB,$baseAB,$baseBA,$chanceAB,$chanceBA,
$affinityAB,$affinityBA,$standingB,$standingA,$effectiveAB,$effectiveBA,
$tagAB,$tagBA,$day,$last,$shard,-1,$projected,$pending,$action,$version,
$seedVersion,$baseline,$chanceAB,$chanceBA,$signAB,$signBA,$magnitudeAB,
$magnitudeBA,$deltaAB,$deltaBA,$affinityAB,$affinityBA,$eligible,
 $provenance,$decayPeriod,1,$ts)
ON CONFLICT(pair_key) DO UPDATE SET
mbti_a=$typeA,mbti_b=$typeB,base_chance_a_to_b=$baseAB,
base_chance_b_to_a=$baseBA,chance_a_to_b=$chanceAB,chance_b_to_a=$chanceBA,
affinity_a_to_b=$affinityAB,affinity_b_to_a=$affinityBA,
social_modifier_a_to_b=$standingB,social_modifier_b_to_a=$standingA,
effective_affinity_a_to_b=$effectiveAB,effective_affinity_b_to_a=$effectiveBA,
tag_a_to_b=$tagAB,tag_b_to_a=$tagBA,last_day=$last,
processing_shard=$shard,projected_native_relation=$projected,
native_action_pending=$pending,native_action_id=$action,
compatibility_version=$version,opening_seed_version=$seedVersion,
opening_seed_baseline=$baseline,opening_seed_chance_a_to_b=$chanceAB,
opening_seed_chance_b_to_a=$chanceBA,opening_seed_sign_roll_a_to_b=$signAB,
opening_seed_sign_roll_b_to_a=$signBA,
opening_seed_magnitude_roll_a_to_b=$magnitudeAB,
opening_seed_magnitude_roll_b_to_a=$magnitudeBA,
opening_seed_delta_a_to_b=$deltaAB,opening_seed_delta_b_to_a=$deltaBA,
opening_seed_affinity_a_to_b=$affinityAB,
opening_seed_affinity_b_to_a=$affinityBA,
 opening_seed_eligible=$eligible,opening_seed_provenance=$provenance,
 last_decay_period=$decayPeriod,
state_revision=relationship_pair_chemistry.state_revision+1,updated_ts=$ts
WHERE relationship_pair_chemistry.opening_seed_version<$seedVersion;";

        internal const string UpsertRelationshipPairProvenanceSql = @"
INSERT INTO relationship_pair_provenance(
campaign_id,timeline_id,pair_key,source,active,required,first_day,last_day,
details_json,updated_ts)
VALUES($campaign,$timeline,$pair,$source,1,$required,$day,$day,$details,$ts)
ON CONFLICT(campaign_id,timeline_id,pair_key,source) DO UPDATE SET
active=1,required=$required,last_day=$day,details_json=$details,updated_ts=$ts
WHERE relationship_pair_provenance.active<>1
OR relationship_pair_provenance.required<>$required
OR relationship_pair_provenance.last_day<>$day
OR relationship_pair_provenance.details_json<>$details;";

        internal const string UpsertRelationshipNativeTargetsSql = @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,world_day,last_sync_day,
attempt_count,claimed_ts,last_error,updated_ts,requires_observation)
VALUES($pair,$actor,$targetHero,$targetRelation,$observed,'pending',$day,-1000,0,0,'',$ts,COALESCE($requiresObservation,0))
ON CONFLICT(pair_key) DO UPDATE SET
hero_a_id=excluded.hero_a_id,hero_b_id=excluded.hero_b_id,
observed_relation=excluded.observed_relation,world_day=excluded.world_day,
requires_observation=CASE WHEN relationship_native_targets.requires_observation=1 OR excluded.requires_observation=1 THEN 1 ELSE 0 END,
status='pending',
revision=CASE WHEN relationship_native_targets.target_relation=excluded.target_relation
    THEN relationship_native_targets.revision ELSE relationship_native_targets.revision+1 END,
attempt_count=CASE
    WHEN relationship_native_targets.target_relation=excluded.target_relation
    THEN relationship_native_targets.attempt_count ELSE 0
END,
claimed_ts=0,
target_relation=excluded.target_relation,last_error='',updated_ts=excluded.updated_ts;";

        internal static List<Dictionary<string, object>> SelectRelationshipNativeTargets2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,world_day,
attempt_count,revision
FROM relationship_native_targets
WHERE status IN ('pending','claimed','failed')
ORDER BY ABS(target_relation-observed_relation) DESC,pair_key;", parameters, transaction);
        }

        internal static void UpsertRelationshipNativeSyncBatches(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_native_sync_batches(
plan_id,campaign_id,timeline_id,world_day,target_count,issued_ts,client_remaining)
VALUES($plan,$campaign,$timeline,$day,$count,$ts,$count)
ON CONFLICT(plan_id) DO UPDATE SET target_count=$count,issued_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeSyncBatches(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_native_sync_batches WHERE plan_id=$plan AND timeline_id=$timeline LIMIT 1;", parameters, transaction);
        }

        internal static void DeleteRelationshipNativeTargets2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM relationship_native_targets WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET native_action_pending=0,native_action_id='',updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateRelationshipNativeTargets2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_native_targets
SET status='failed',attempt_count=attempt_count+1,claimed_ts=0,last_error=$error,updated_ts=$ts
WHERE pair_key=$pair AND status<>'failed';", parameters, transaction);
        }

        internal static void UpdateRelationshipNativeTargets3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_native_targets
SET status='pending',claimed_ts=0,last_error='',updated_ts=$ts
WHERE world_day<=$day;", parameters, transaction);
        }

        internal static void UpdateRelationshipNativeTargets4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_native_targets
SET status='failed',claimed_ts=0,last_error=$error,updated_ts=$ts
WHERE pair_key=$pair AND world_day<=$day;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET native_action_pending=0,native_action_id='',last_native_sync_day=$day,updated_ts=$ts
WHERE pair_key IN (
    SELECT pair_key FROM relationship_native_targets
    WHERE world_day<=$day AND status<>'failed'
);", parameters, transaction);
        }

        internal static void DeleteRelationshipNativeTargets3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM relationship_native_targets
WHERE world_day<=$day AND status<>'failed';", parameters, transaction);
        }

        internal static void UpdateRelationshipNativeSyncBatches(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_native_sync_batches SET
client_applied=$applied,client_already_aligned=$aligned,client_obsolete=$obsolete,
client_failed=$failed,client_remaining=$remaining,client_duration_ms=$duration,
last_report_day=$day,last_report_ts=$ts,last_error=$error
WHERE plan_id=$plan AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDailyInputs3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT MAX(world_day) AS day FROM relationship_daily_inputs;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipDailyInputs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_daily_inputs WHERE status IN ('pending','processing');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDailyInputs4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT MIN(world_day) AS day FROM relationship_daily_inputs WHERE status='pending';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDailyInputs5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT day_key,pair_cursor,candidate_pairs,processed_pairs,
started_ts,last_error FROM relationship_daily_inputs WHERE status='processing'
ORDER BY day_key LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='relationship_mbti_error' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry10(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry ORDER BY last_day DESC,weighted_exposure DESC LIMIT " + part + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry11(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,projected_native_relation,native_action_pending,native_action_id FROM relationship_pair_chemistry WHERE native_action_pending=1 ORDER BY last_day DESC LIMIT " + part + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key=$key LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='mbti_relationship_last_processed_day' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT hero_a_id,hero_b_id,tag_a_to_b,tag_b_to_a FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertRelationshipPersonalities(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_personalities(
hero_id,mbti_type,title,description,source,assignment_day,template_version,traits_json,created_ts,updated_ts)
VALUES($hero,$type,$title,$description,'notable_mbti_template',$day,$version,$traits,$ts,$ts)
ON CONFLICT(hero_id) DO UPDATE SET
mbti_type=excluded.mbti_type,title=excluded.title,description=excluded.description,
source=excluded.source,assignment_day=excluded.assignment_day,
template_version=excluded.template_version,traits_json=excluded.traits_json,updated_ts=excluded.updated_ts
WHERE relationship_personalities.mbti_type<>excluded.mbti_type
OR relationship_personalities.title<>excluded.title
OR relationship_personalities.description<>excluded.description
OR relationship_personalities.source<>excluded.source
OR relationship_personalities.assignment_day<>excluded.assignment_day
OR relationship_personalities.template_version<>excluded.template_version
OR relationship_personalities.traits_json<>excluded.traits_json;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPersonalities2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_personalities WHERE hero_id=$hero LIMIT 1;", parameters, transaction);
        }

        internal static void InsertOrIgnoreRelationshipPersonalities(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_personalities(
hero_id,mbti_type,title,description,source,assignment_day,template_version,traits_json,created_ts,updated_ts)
VALUES($hero,$type,$title,$description,$source,$day,$version,$traits,$ts,$ts)
ON CONFLICT(hero_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPersonalities3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT personalities.hero_id,
personalities.mbti_type,personalities.title,personalities.description,
personalities.source
FROM relationship_personalities personalities
JOIN jsonb_array_elements_text(CAST($heroes AS jsonb)) requested
  ON requested.value=personalities.hero_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPersonalities4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT hero_id,mbti_type,title,
description,source FROM relationship_personalities;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDailyInputs6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT pair_cursor,processed_pairs FROM relationship_daily_inputs
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day
LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_native_targets WHERE status IN ('pending','claimed','failed');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectNotableMbtiProfiles(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT profiles.*
FROM notable_mbti_profiles profiles
JOIN jsonb_array_elements_text(CAST($heroes AS jsonb)) requested
  ON requested.value=profiles.hero_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectNotableMbtiProfiles2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM notable_mbti_profiles;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT pairs.*
FROM relationship_pair_chemistry pairs
JOIN jsonb_array_elements_text(CAST($pairs AS jsonb)) requested
  ON requested.value=pairs.pair_key;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeTargets3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT targets.*
FROM relationship_native_targets targets
JOIN jsonb_array_elements_text(CAST($pairs AS jsonb)) requested
  ON requested.value=targets.pair_key;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeTargets4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_native_targets;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('relationship_mbti_error',$error);", parameters, transaction);
        }

        internal static void UpdateRelationshipDailyInputs5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_daily_inputs SET
pair_cursor=$cursor,candidate_pairs=$candidates,processed_pairs=$processed
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET shared_tag=$shared,state_revision=state_revision+1,updated_ts=$ts
WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES($key,$day);", parameters, transaction);
        }

        internal static void UpsertSchemaMeta4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('mbti_relationship_last_processed_day',$day);", parameters, transaction);
        }

        internal const string DeleteRelationshipNativeTargetsSql2 = "DELETE FROM relationship_native_targets WHERE pair_key=$pair;";

        internal static List<Dictionary<string, object>> CountRelationshipObservedHeroes(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_observed_heroes;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipDailyInputs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_daily_inputs WHERE status='pending';", parameters, transaction);
        }

        internal static void UpdateRelationshipObservedHeroes(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_observed_heroes
SET updated_ts=17 WHERE hero_id='campaign_hero_0';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipObservedHeroes(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT updated_ts FROM relationship_observed_heroes
WHERE hero_id='campaign_hero_0' LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertRelationshipPersonalities2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_personalities(
hero_id,mbti_type,title,description,source,assignment_day,template_version,
traits_json,created_ts,updated_ts)
VALUES('seed_b',$type,$title,$description,'fixture_conflict',0,1,'{}',$ts,$ts)
ON CONFLICT(hero_id) DO UPDATE SET mbti_type=excluded.mbti_type,
title=excluded.title,description=excluded.description,
source=excluded.source,updated_ts=excluded.updated_ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM relationship_pair_chemistry
WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipOpeningSeedRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_opening_seed_runs LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDailyInputs7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_daily_inputs LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectNotableMbtiProfiles3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT mbti_type FROM notable_mbti_profiles
WHERE hero_id='seed_b' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairProvenance(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT active,required FROM relationship_pair_provenance
WHERE pair_key=$pair AND source='kingdom_leadership' LIMIT 1;", parameters, transaction);
        }

        internal static void SelectX4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(CAST($rows AS jsonb))
    AS r(hero_id text)
)
INSERT INTO relationship_personalities(
hero_id,mbti_type,title,description,source,assignment_day,
template_version,traits_json,created_ts,updated_ts)
SELECT hero_id,'INTJ','Architect','Scale fixture personality',
'pregenerated_profile',0,1,'{}',1,1 FROM x
ON CONFLICT(hero_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_pair_chemistry;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry15(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeTargets5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_native_targets ORDER BY world_day,pair_key LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSqliteMaster2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT name FROM sqlite_master WHERE type='table' AND name='ambient_relationship_changes' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeSyncBatches2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_native_sync_batches WHERE plan_id=$plan LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipNativeTargets2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_native_targets;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry16(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT native_action_pending FROM relationship_pair_chemistry LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeTargets6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT attempt_count FROM relationship_native_targets WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static void DeleteRelationshipNativeTargets4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM relationship_native_targets;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
affinity_a_to_b=92,affinity_b_to_a=92,projected_native_relation=92,
native_action_pending=0,native_action_id='',compatibility_version=3;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipDirectorActions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_director_actions WHERE action_type='native_relation';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSqliteMaster3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT name FROM sqlite_master WHERE type='table' AND name='ambient_relationship_runs' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipDailyInputs3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_daily_inputs
WHERE status='processed' AND presence_groups_json='[]' AND hero_changes_json='[]';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry17(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT last_day FROM relationship_pair_chemistry LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeTargets7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_native_targets LIMIT 1;", parameters, transaction);
        }

        internal static void InsertRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,world_day,
last_sync_day,attempt_count,claimed_ts,last_error,updated_ts,revision)
VALUES($pair,$a,$b,$target,$observed,'pending',636,-1000,0,0,'',$ts,1);", parameters, transaction);
        }

        internal static void InsertRelationshipPairChemistry3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,
affinity_a_to_b,affinity_b_to_a,effective_affinity_a_to_b,
effective_affinity_b_to_a,first_day,last_day,state_revision,updated_ts)
VALUES($pair,'atomic_a','atomic_b','INTJ','ESFP',
20,0,20,0,1,1,1,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountNotableMbtiProfiles(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM notable_mbti_profiles;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
INSERT INTO schema_meta(key,value)
VALUES('postgresql_relationship_schema_revision',$revision)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='mbti_relationship_chemistry_version' LIMIT 1;", parameters, transaction);
        }

        internal static void Savepoint2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "SAVEPOINT mbti_relationship_upgrade;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSqliteMaster4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT name FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;", parameters, transaction);
        }

        internal static void DeleteDynamic(ReignDbConnection connection, object retiredTable, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM " + retiredTable + ";", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
affinity_a_to_b=MAX(-100,MIN(100,affinity_a_to_b+signal_a_to_b)),
affinity_b_to_a=MAX(-100,MIN(100,affinity_b_to_a+signal_b_to_a)),
signal_a_to_b=0,signal_b_to_a=0,compatibility_version=2;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
native_incident_offset=0,
projected_native_relation=MAX(-100,MIN(100,ROUND((affinity_a_to_b+affinity_b_to_a)/2.0))),
native_action_pending=0,native_action_id='',compatibility_version=3;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSqliteMaster5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT name FROM sqlite_master WHERE type='table' AND name='relationship_director_actions' LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateRelationshipDirectorActions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_director_actions
SET status='superseded',resolved_ts=$ts,
result_json='{""status"":""superseded"",""reason"":""reign_directional_affinity_became_authoritative""}'
WHERE action_type='native_relation' AND status IN ('pending','claimed')
  AND COALESCE(json_extract(payload_json,'$.source'),'')='mbti_relationship_chemistry';", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET axes_a_json='{}',axes_b_json='{}'
WHERE axes_a_json<>'{}' OR axes_b_json<>'{}';", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
social_modifier_a_to_b=0,social_modifier_b_to_a=0,
effective_affinity_a_to_b=affinity_a_to_b,
effective_affinity_b_to_a=affinity_b_to_a;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('mbti_relationship_chemistry_version',$version);", parameters, transaction);
        }

        internal static void UpsertSchemaMeta7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('campaign_compaction_required','1');", parameters, transaction);
        }

        internal static void Release2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "RELEASE mbti_relationship_upgrade;", parameters, transaction);
        }

        internal static void Rollback3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "ROLLBACK TO mbti_relationship_upgrade;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> UpsertRelationshipDailyInputs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"INSERT INTO relationship_daily_inputs(
campaign_id,timeline_id,day_key,world_day,status,presence_groups_json,hero_changes_json,group_count,received_ts)
VALUES($campaign,$timeline,$dayKey,$day,'pending',$groups,$heroes,$count,$ts)
ON CONFLICT(campaign_id,timeline_id,day_key) DO UPDATE SET
world_day=$day,presence_groups_json=$groups,hero_changes_json=$heroes,
group_count=$count,
status='pending'
WHERE relationship_daily_inputs.status='pending' AND relationship_daily_inputs.started_ts=0
RETURNING day_key;", parameters, transaction);
        }

        internal static void UpsertRelationshipObservedHeroes(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_observed_heroes(
hero_id,profile_json,profile_hash,first_observed_day,last_observed_day,updated_ts)
VALUES($hero,$profile,$hash,$day,$day,$ts)
ON CONFLICT(hero_id) DO UPDATE SET profile_json=$profile,profile_hash=$hash,
last_observed_day=$day,updated_ts=$ts
WHERE relationship_observed_heroes.profile_hash<>$hash;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipOpeningSeedRuns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM relationship_opening_seed_runs
WHERE campaign_id=$campaign AND timeline_id=$timeline AND seed_version=$version LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertRelationshipOpeningSeedRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_opening_seed_runs(
campaign_id,timeline_id,seed_version,generation_id,world_day,status,
candidate_pairs,created_ts,updated_ts)
VALUES($campaign,$timeline,$version,$generation,$day,'processing',$candidates,$ts,$ts)
ON CONFLICT(campaign_id,timeline_id,seed_version) DO UPDATE SET
generation_id=$generation,world_day=$day,
status=CASE
    WHEN relationship_opening_seed_runs.status='completed'
    THEN relationship_opening_seed_runs.status
    ELSE 'processing'
END,
candidate_pairs=$candidates,updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPersonalities5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
 SELECT hero_id,mbti_type,2 AS priority FROM relationship_personalities
 UNION ALL
 SELECT hero_id,mbti_type,1 AS priority FROM notable_mbti_profiles;", parameters, transaction);
        }

        internal static void UpdateRelationshipOpeningSeedRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_opening_seed_runs SET
status='seeded',completed_pairs=$completed,skipped_pairs=$skipped,
failed_pairs=$failed,co_location_pairs=$coLocation,spouse_pairs=$spouses,
family_pairs=$family,kingdom_leadership_pairs=$leadership,
ruler_network_pairs=$rulers,positive_directions=$positive,
negative_directions=$negative,missing_mbti=$missing,duration_ms=$duration,
average_delta=$average,median_delta=$median,minimum_delta=$minimum,
maximum_delta=$maximum,starting_bands_json=$startingBands,
resulting_bands_json=$resultingBands,parallel_workers=$parallelWorkers,
parallel_compute_ms=$parallelComputeMs,last_error='',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND seed_version=$version;", parameters, transaction);
        }

        internal static void UpdateRelationshipDailyInputs6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_daily_inputs
SET status='initialization_pending',last_error=''
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day
AND status='pending';", parameters, transaction);
        }

        internal static void UpdateRelationshipOpeningSeedRuns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_opening_seed_runs SET
status='completed',duration_ms=$duration,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND seed_version=$version;", parameters, transaction);
        }

        internal static void UpdateRelationshipOpeningSeedRuns3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_opening_seed_runs SET
status='failed',failed_pairs=$failed,missing_mbti=$missing,
duration_ms=$duration,last_error=$error,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND seed_version=$version;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPersonalities6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT mbti_type FROM (
 SELECT mbti_type,2 AS priority FROM relationship_personalities WHERE hero_id=$hero
 UNION ALL
 SELECT mbti_type,1 AS priority FROM notable_mbti_profiles WHERE hero_id=$hero
) resolved ORDER BY priority LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateRelationshipDailyInputs7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_daily_inputs
SET status='processed',presence_groups_json='[]',hero_changes_json='[]',
processed_ts=$ts WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDailyInputs8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT MIN(world_day) AS day
FROM relationship_daily_inputs WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status='pending';", parameters, transaction);
        }

        /// <summary>Runs a statement this module composes at run time from text kept in this class.</summary>
        internal static void ExecuteSql(ReignDbConnection connection, string sql, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, sql, parameters, transaction);
        }
    }
}
