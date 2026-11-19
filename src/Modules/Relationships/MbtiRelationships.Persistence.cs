using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Reign.Relationships;
using ReignBeta.Shared;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        private static ReignDbCommand CreatePreparedRelationshipCommand(
            ReignDbConnection connection,
            string sql,
            params string[] parameterNames)
        {
            ReignDbCommand command = connection.CreateCommand();
            command.CommandText = ReignPostgreSqlDialect.Normalize(connection, sql);
            command.CommandTimeout = 60;
            foreach (string name in parameterNames.Distinct(StringComparer.Ordinal))
            {
                System.Data.Common.DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = ReignPostgreSqlDialect.ParameterName(connection, name);
                parameter.Value = DBNull.Value;
                command.Parameters.Add(parameter);
            }
            return command;
        }

        private static void ExecutePreparedRelationshipCommand(
            ReignDbCommand command,
            Dictionary<string, object> values)
        {
            // Iterate the prepared collection once and use the caller's hash
            // lookup for the high-volume relationship pair upsert.
            for (int index = 0; index < command.Parameters.Count; index++)
            {
                System.Data.Common.DbParameter parameter = command.Parameters[index];
                string key = parameter.ParameterName.StartsWith("$", StringComparison.Ordinal)
                    ? parameter.ParameterName.Substring(1)
                    : parameter.ParameterName;
                if (values.TryGetValue(key, out object value)
                    || values.TryGetValue(parameter.ParameterName, out value))
                    parameter.Value = value ?? DBNull.Value;
            }
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    command.ExecuteNonQuery();
                    return;
                }
                catch (Exception ex) when (
                    IsTransientDatabaseContention(ex) && attempt < 240)
                {
                    Thread.Sleep(Math.Min(250, 10 + attempt * 5));
                }
            }
        }

        private static void FlushPostgreSqlRelationshipWriteBatches(
            ReignDbConnection connection,
            ReignDbCommand pairUpsert,
            ReignDbCommand nativeTargetUpsert,
            ReignDbCommand nativeTargetDelete,
            List<Dictionary<string, object>> pairRows,
            List<Dictionary<string, object>> nativeTargetRows,
            List<Dictionary<string, object>> nativeTargetDeletes)
        {
            if (!ReignPostgreSqlDialect.IsPostgreSql(connection))
                return;
            ExecutePostgreSqlRelationshipWriteSet(connection, pairRows,
                nativeTargetRows, nativeTargetDeletes);
            pairRows.Clear();
            nativeTargetRows.Clear();
            nativeTargetDeletes.Clear();
        }

        internal static string PostgreSqlRelationshipRowsJson(
            List<Dictionary<string, object>> rows)
        {
            return Json.Serialize((rows
                    ?? new List<Dictionary<string, object>>())
                .Select(row => row.ToDictionary(
                    item => item.Key.ToLowerInvariant(),
                    item => item.Value,
                    StringComparer.OrdinalIgnoreCase))
                .ToList());
        }

        internal static void ExecutePostgreSqlJsonCommand(
            ReignDbConnection connection, string sql, string json)
        {
            NpgsqlConnection postgreSql = connection as NpgsqlConnection;
            if (postgreSql == null)
                throw new InvalidOperationException(
                    "A PostgreSQL relationship set write requires Npgsql.");
            using (NpgsqlCommand command = postgreSql.CreateCommand())
            {
                command.CommandText = sql;
                command.CommandTimeout = 60;
                command.Parameters.Add(new NpgsqlParameter(
                    "rows", NpgsqlTypes.NpgsqlDbType.Jsonb)
                {
                    Value = json ?? "[]"
                });
                command.ExecuteNonQuery();
            }
        }

        private static void ExecutePostgreSqlRelationshipWriteSet(
            ReignDbConnection connection,
            List<Dictionary<string, object>> pairRows,
            List<Dictionary<string, object>> nativeTargetRows,
            List<Dictionary<string, object>> nativeTargetDeletes)
        {
            if (pairRows != null && pairRows.Count > 0)
            {
                ExecutePostgreSqlRelationshipPairCopy(connection, pairRows);
            }
            if (nativeTargetRows != null && nativeTargetRows.Count > 0)
            {
                ExecutePostgreSqlJsonCommand(connection, @"
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
   OR relationship_native_targets.status NOT IN ('pending','claimed');",
                    PostgreSqlRelationshipRowsJson(nativeTargetRows));
            }
            if (nativeTargetDeletes != null
                && nativeTargetDeletes.Count > 0)
            {
                ExecutePostgreSqlJsonCommand(connection, @"
DELETE FROM relationship_native_targets AS target
USING jsonb_to_recordset(@rows) AS x(pair text)
WHERE target.pair_key=x.pair;",
                    PostgreSqlRelationshipRowsJson(nativeTargetDeletes));
            }
        }

        private static void ExecutePostgreSqlRelationshipPairCopy(
            ReignDbConnection connection,
            List<Dictionary<string, object>> pairRows)
        {
            NpgsqlConnection postgreSql = connection as NpgsqlConnection;
            if (postgreSql == null)
                throw new InvalidOperationException(
                    "A PostgreSQL relationship COPY requires Npgsql.");

            using (NpgsqlCommand setup = postgreSql.CreateCommand())
            {
                setup.CommandTimeout = 60;
                setup.CommandText = @"
CREATE TEMP TABLE IF NOT EXISTS relationship_pair_chemistry_stage(
pair text NOT NULL,a text NOT NULL,b text NOT NULL,
typea text NOT NULL,typeb text NOT NULL,axesa text NOT NULL,
axesb text NOT NULL,baseab bigint NOT NULL,baseba bigint NOT NULL,
chanceab bigint NOT NULL,chanceba bigint NOT NULL,
affinityab bigint NOT NULL,affinityba bigint NOT NULL,
signalab bigint NOT NULL,signalba bigint NOT NULL,
tagab text NOT NULL,tagba text NOT NULL,shared text NOT NULL,
firstday bigint NOT NULL,lastday bigint NOT NULL,
consecutive bigint NOT NULL,weighted double precision NOT NULL,
kind text NOT NULL,contextid text NOT NULL,rollab bigint NOT NULL,
rollba bigint NOT NULL,deltaab bigint NOT NULL,deltaba bigint NOT NULL,
projected bigint NOT NULL,nativeday double precision NOT NULL,
pending bigint NOT NULL,actionid text NOT NULL,shard bigint NOT NULL,
presenceday bigint NOT NULL,batchsize bigint NOT NULL,
presencemask bigint NOT NULL,decayperiod bigint NOT NULL,
version bigint NOT NULL,updatedts bigint NOT NULL)
ON COMMIT DELETE ROWS;
TRUNCATE relationship_pair_chemistry_stage;";
                setup.ExecuteNonQuery();
            }

            Stopwatch copyTimer = Stopwatch.StartNew();
            using (NpgsqlBinaryImporter writer =
                postgreSql.BeginBinaryImport(@"
COPY relationship_pair_chemistry_stage(
pair,a,b,typea,typeb,axesa,axesb,baseab,baseba,chanceab,chanceba,
affinityab,affinityba,signalab,signalba,tagab,tagba,shared,
firstday,lastday,consecutive,weighted,kind,contextid,rollab,rollba,
deltaab,deltaba,projected,nativeday,pending,actionid,shard,
presenceday,batchsize,presencemask,decayperiod,version,updatedts)
FROM STDIN (FORMAT BINARY)"))
            {
                foreach (Dictionary<string, object> row in pairRows)
                {
                    writer.StartRow();
                    writer.Write(ReadString(row, "pair", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(ReadString(row, "a", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(ReadString(row, "b", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(ReadString(row, "typeA", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(ReadString(row, "typeB", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(ReadString(row, "axesA", "{}"),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(ReadString(row, "axesB", "{}"),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "baseAB");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "baseBA");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "chanceAB");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "chanceBA");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "affinityAB");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "affinityBA");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "signalAB");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "signalBA");
                    writer.Write(ReadString(row, "tagAB", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(ReadString(row, "tagBA", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(ReadString(row, "shared", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "first");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "last");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "consecutive");
                    writer.Write(ReadDouble(row, "weighted", 0d),
                        NpgsqlTypes.NpgsqlDbType.Double);
                    writer.Write(ReadString(row, "kind", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(ReadString(row, "context", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "rollAB");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "rollBA");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "deltaAB");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "deltaBA");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "projected");
                    writer.Write(ReadDouble(row, "nativeDay", -1000d),
                        NpgsqlTypes.NpgsqlDbType.Double);
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "pending");
                    writer.Write(ReadString(row, "action", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "shard");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "presenceDay");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "batchSize");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "presenceMask");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "decayPeriod");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "version");
                    writer.Write(ReadLong(row, "ts", 0),
                        NpgsqlTypes.NpgsqlDbType.Bigint);
                }
                writer.Complete();
            }
            copyTimer.Stop();
            Interlocked.Add(ref PostgreSqlRelationshipStagedRows,
                pairRows.Count);
            Interlocked.Add(ref PostgreSqlRelationshipCopyMs,
                copyTimer.ElapsedMilliseconds);

            Stopwatch mergeTimer = Stopwatch.StartNew();
            int written;
            using (NpgsqlCommand merge = postgreSql.CreateCommand())
            {
                merge.CommandTimeout = 60;
                merge.CommandText = @"
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
                written = merge.ExecuteNonQuery();
            }
            mergeTimer.Stop();
            Interlocked.Add(ref PostgreSqlRelationshipWrittenRows, written);
            Interlocked.Add(ref PostgreSqlRelationshipMergeMs,
                mergeTimer.ElapsedMilliseconds);
        }

        private static void WritePostgreSqlRelationshipCopyInteger(
            NpgsqlBinaryImporter writer,
            Dictionary<string, object> row, string key)
        {
            writer.Write(ReadLong(row, key, 0),
                NpgsqlTypes.NpgsqlDbType.Bigint);
        }

        private static void FlushPostgreSqlRelationshipProvenanceBatch(
            ReignDbConnection connection,
            List<Dictionary<string, object>> rows)
        {
            if (!ReignPostgreSqlDialect.IsPostgreSql(connection)
                || rows == null || rows.Count == 0)
                return;
            string json = Json.Serialize(rows.Select(row =>
                row.ToDictionary(
                    item => item.Key.ToLowerInvariant(),
                    item => item.Value,
                    StringComparer.OrdinalIgnoreCase)).ToList());
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
OR relationship_pair_provenance.details_json<>excluded.details_json;",
                new Dictionary<string, object> { ["rows"] = json });
            rows.Clear();
        }

        private static ReignDbCommand CreateRelationshipPairUpsertCommand(ReignDbConnection connection)
        {
            return CreatePreparedRelationshipCommand(connection, @"INSERT INTO relationship_pair_chemistry(
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
 state_revision=relationship_pair_chemistry.state_revision+1,updated_ts=$ts;",
                "$pair", "$a", "$b", "$typeA", "$typeB", "$axesA", "$axesB",
                "$baseAB", "$baseBA", "$chanceAB", "$chanceBA", "$affinityAB", "$affinityBA",
                "$signalAB", "$signalBA", "$tagAB", "$tagBA", "$shared", "$first", "$last",
                "$consecutive", "$weighted", "$kind", "$context", "$rollAB", "$rollBA",
                "$deltaAB", "$deltaBA", "$projected", "$nativeDay", "$pending", "$action",
                "$shard", "$presenceDay", "$batchSize", "$presenceMask", "$decayPeriod",
                "$version", "$ts");
        }

        private static ReignDbCommand CreateOpeningRelationshipPairUpsertCommand(
            ReignDbConnection connection)
        {
            return CreatePreparedRelationshipCommand(connection, @"INSERT INTO relationship_pair_chemistry(
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
WHERE relationship_pair_chemistry.opening_seed_version<$seedVersion;",
                "$pair", "$a", "$b", "$typeA", "$typeB", "$baseAB",
                "$baseBA", "$chanceAB", "$chanceBA", "$affinityAB",
                "$affinityBA", "$standingA", "$standingB", "$effectiveAB",
                "$effectiveBA", "$tagAB", "$tagBA", "$day", "$last",
                "$shard", "$projected", "$pending", "$action", "$version",
                "$seedVersion", "$baseline", "$signAB", "$signBA",
                "$magnitudeAB", "$magnitudeBA", "$deltaAB", "$deltaBA",
                "$eligible", "$provenance", "$decayPeriod", "$ts");
        }

        private static ReignDbCommand
            CreateOpeningRelationshipProvenanceUpsertCommand(
                ReignDbConnection connection)
        {
            return CreatePreparedRelationshipCommand(connection, @"
INSERT INTO relationship_pair_provenance(
campaign_id,timeline_id,pair_key,source,active,required,first_day,last_day,
details_json,updated_ts)
VALUES($campaign,$timeline,$pair,$source,1,$required,$day,$day,$details,$ts)
ON CONFLICT(campaign_id,timeline_id,pair_key,source) DO UPDATE SET
active=1,required=$required,last_day=$day,details_json=$details,updated_ts=$ts
WHERE relationship_pair_provenance.active<>1
OR relationship_pair_provenance.required<>$required
OR relationship_pair_provenance.last_day<>$day
OR relationship_pair_provenance.details_json<>$details;",
                "$campaign", "$timeline", "$pair", "$source", "$required",
                "$day", "$details", "$ts");
        }

        private static Dictionary<string, object>
            OpeningRelationshipPairParameters(
                string campaignId, OpeningSeedCandidate candidate,
                string typeA, string typeB, int chanceAB, int chanceBA,
                int affinityAB, int affinityBA, int standingA, int standingB,
                int effectiveAB, int effectiveBA, int projected,
                bool pending, int day,
                bool eligible, int baseline, int signAB, int signBA,
                int magnitudeAB, int magnitudeBA, int deltaAB, int deltaBA,
                string provenance, long ts)
        {
            return new Dictionary<string, object>
            {
                ["pair"] = candidate.PairKey,
                ["a"] = candidate.HeroAId,
                ["b"] = candidate.HeroBId,
                ["typeA"] = typeA,
                ["typeB"] = typeB,
                ["baseAB"] = MbtiCompatibility(typeA, typeB),
                ["baseBA"] = MbtiCompatibility(typeB, typeA),
                ["chanceAB"] = chanceAB,
                ["chanceBA"] = chanceBA,
                ["affinityAB"] = affinityAB,
                ["affinityBA"] = affinityBA,
                ["standingA"] = standingA,
                ["standingB"] = standingB,
                ["effectiveAB"] = effectiveAB,
                ["effectiveBA"] = effectiveBA,
                ["tagAB"] = DirectionalRelationshipTag(campaignId,
                    candidate.PairKey, "a_to_b", affinityAB, affinityBA),
                ["tagBA"] = DirectionalRelationshipTag(campaignId,
                    candidate.PairKey, "b_to_a", affinityBA, affinityAB),
                ["day"] = day,
                ["last"] = day - 1,
                ["shard"] = RelationshipCadenceShard(campaignId,
                    candidate.PairKey),
                ["projected"] = projected,
                ["pending"] = pending ? 1 : 0,
                ["action"] = pending
                    ? "native_target:" + candidate.PairKey : "",
                ["version"] = MbtiChemistryVersion,
                ["seedVersion"] = OpeningRelationshipSeedVersion,
                ["baseline"] = baseline,
                ["signAB"] = signAB,
                ["signBA"] = signBA,
                ["magnitudeAB"] = magnitudeAB,
                ["magnitudeBA"] = magnitudeBA,
                ["deltaAB"] = deltaAB,
                ["deltaBA"] = deltaBA,
                ["eligible"] = eligible ? 1 : 0,
                ["provenance"] = provenance,
                ["decayPeriod"] = Math.Max(0, day / 5),
                ["ts"] = ts
            };
        }

        private static void ExecuteOpeningRelationshipPostgreSqlBulk(
            ReignDbConnection connection,
            List<Dictionary<string, object>> pairRows,
            List<Dictionary<string, object>> provenanceRows,
            List<Dictionary<string, object>> nativeTargetRows,
            List<string> nativeTargetDeletes)
        {
            Func<List<Dictionary<string, object>>, string> jsonRows = rows =>
                Json.Serialize((rows ?? new List<Dictionary<string, object>>())
                    .Select(row => row.ToDictionary(
                        item => item.Key.ToLowerInvariant(),
                        item => item.Value,
                        StringComparer.OrdinalIgnoreCase)).ToList());
            if (pairRows != null && pairRows.Count > 0)
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
    < excluded.opening_seed_version;",
                    new Dictionary<string, object>
                    {
                        ["rows"] = jsonRows(pairRows)
                    });
            }
            if (provenanceRows != null && provenanceRows.Count > 0)
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
OR relationship_pair_provenance.details_json<>excluded.details_json;",
                    new Dictionary<string, object>
                    {
                        ["rows"] = jsonRows(provenanceRows)
                    });
            }
            if (nativeTargetRows != null && nativeTargetRows.Count > 0)
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
updated_ts=excluded.updated_ts;",
                    new Dictionary<string, object>
                    {
                        ["rows"] = jsonRows(nativeTargetRows)
                    });
            }
            if (nativeTargetDeletes != null && nativeTargetDeletes.Count > 0)
            {
                ReignDatabase.ExecuteSql(connection, @"
DELETE FROM relationship_native_targets
WHERE pair_key IN (
    SELECT value FROM jsonb_array_elements_text(CAST($pairs AS jsonb)));",
                    new Dictionary<string, object>
                    {
                        ["pairs"] = Json.Serialize(nativeTargetDeletes)
                    });
            }
        }

        private static Dictionary<string, Dictionary<string, object>>
            RelationshipPairStateSnapshot(ReignDbConnection connection,
                string campaignId)
        {
            string key = string.IsNullOrWhiteSpace(campaignId)
                ? "default" : campaignId;
            if (!RelationshipPairStateCache.TryGetValue(key,
                out ConcurrentDictionary<string, Dictionary<string, object>> cached))
            {
                lock (CampaignRelationshipWriteLock(key))
                {
                    if (!RelationshipPairStateCache.TryGetValue(key, out cached))
                    {
                        cached = new ConcurrentDictionary<string,
                            Dictionary<string, object>>(
                                ReignDatabase.QuerySql(connection,
                                    "SELECT * FROM relationship_pair_chemistry;")
                                .Where(row => !string.IsNullOrWhiteSpace(
                                    ReadString(row, "pair_key", "")))
                                .ToDictionary(row => ReadString(row,
                                        "pair_key", ""),
                                    CloneRelationshipPairCacheRow,
                                    StringComparer.OrdinalIgnoreCase),
                                StringComparer.OrdinalIgnoreCase);
                        RelationshipPairStateCache[key] = cached;
                    }
                }
            }
            return cached.ToDictionary(item => item.Key,
                item => CloneRelationshipPairCacheRow(item.Value),
                StringComparer.OrdinalIgnoreCase);
        }

        private static void InstallOpeningRelationshipPairStateCache(
            string campaignId,
            Dictionary<string, Dictionary<string, object>> existingRows,
            List<Dictionary<string, object>> openingRows)
        {
            string key = string.IsNullOrWhiteSpace(campaignId)
                ? "default" : campaignId;
            Dictionary<string, Dictionary<string, object>> rows =
                new Dictionary<string, Dictionary<string, object>>(
                    StringComparer.OrdinalIgnoreCase);
            if (existingRows != null)
            {
                foreach (KeyValuePair<string, Dictionary<string, object>>
                    item in existingRows)
                    rows[item.Key] = CloneRelationshipPairCacheRow(item.Value);
            }
            foreach (Dictionary<string, object> values in openingRows
                ?? new List<Dictionary<string, object>>())
            {
                string pairKey = ReadString(values, "pair", "");
                if (string.IsNullOrWhiteSpace(pairKey)) continue;
                rows.TryGetValue(pairKey,
                    out Dictionary<string, object> existing);
                Dictionary<string, object> row = existing == null
                    ? new Dictionary<string, object>(
                        StringComparer.OrdinalIgnoreCase)
                    : CloneRelationshipPairCacheRow(existing);
                row["pair_key"] = pairKey;
                row["hero_a_id"] = ReadString(values, "a", "");
                row["hero_b_id"] = ReadString(values, "b", "");
                row["mbti_a"] = ReadString(values, "typeA", "");
                row["mbti_b"] = ReadString(values, "typeB", "");
                row["axes_a_json"] = "{}";
                row["axes_b_json"] = "{}";
                row["base_chance_a_to_b"] = ReadInt(values, "baseAB", 0);
                row["base_chance_b_to_a"] = ReadInt(values, "baseBA", 0);
                row["chance_a_to_b"] = ReadInt(values, "chanceAB", 0);
                row["chance_b_to_a"] = ReadInt(values, "chanceBA", 0);
                row["affinity_a_to_b"] = ReadInt(values, "affinityAB", 0);
                row["affinity_b_to_a"] = ReadInt(values, "affinityBA", 0);
                row["social_modifier_a_to_b"] =
                    ReadInt(values, "standingB", 0);
                row["social_modifier_b_to_a"] =
                    ReadInt(values, "standingA", 0);
                row["effective_affinity_a_to_b"] =
                    ReadInt(values, "effectiveAB", 0);
                row["effective_affinity_b_to_a"] =
                    ReadInt(values, "effectiveBA", 0);
                row["tag_a_to_b"] = ReadString(values, "tagAB", "");
                row["tag_b_to_a"] = ReadString(values, "tagBA", "");
                row["first_day"] = ReadInt(values, "day", 0);
                row["last_day"] = ReadInt(values, "last", -1);
                row["processing_shard"] = ReadInt(values, "shard", 0);
                row["last_presence_day"] = -1;
                row["projected_native_relation"] =
                    ReadInt(values, "projected", 0);
                row["native_action_pending"] =
                    ReadInt(values, "pending", 0);
                row["native_action_id"] = ReadString(values, "action", "");
                row["compatibility_version"] =
                    ReadInt(values, "version", MbtiChemistryVersion);
                row["opening_seed_version"] =
                    ReadInt(values, "seedVersion",
                        OpeningRelationshipSeedVersion);
                row["opening_seed_baseline"] =
                    ReadInt(values, "baseline", 0);
                row["opening_seed_chance_a_to_b"] =
                    ReadInt(values, "chanceAB", 0);
                row["opening_seed_chance_b_to_a"] =
                    ReadInt(values, "chanceBA", 0);
                row["opening_seed_sign_roll_a_to_b"] =
                    ReadInt(values, "signAB", 0);
                row["opening_seed_sign_roll_b_to_a"] =
                    ReadInt(values, "signBA", 0);
                row["opening_seed_magnitude_roll_a_to_b"] =
                    ReadInt(values, "magnitudeAB", 0);
                row["opening_seed_magnitude_roll_b_to_a"] =
                    ReadInt(values, "magnitudeBA", 0);
                row["opening_seed_delta_a_to_b"] =
                    ReadInt(values, "deltaAB", 0);
                row["opening_seed_delta_b_to_a"] =
                    ReadInt(values, "deltaBA", 0);
                row["opening_seed_affinity_a_to_b"] =
                    ReadInt(values, "affinityAB", 0);
                row["opening_seed_affinity_b_to_a"] =
                    ReadInt(values, "affinityBA", 0);
                row["opening_seed_eligible"] =
                    ReadInt(values, "eligible", 0);
                row["opening_seed_provenance"] =
                    ReadString(values, "provenance", "");
                row["last_decay_period"] =
                    ReadInt(values, "decayPeriod", 0);
                row["state_revision"] = existing == null
                    ? 1 : ReadLong(existing, "state_revision", 1) + 1;
                row["updated_ts"] = ReadLong(values, "ts", 0);
                rows[pairKey] = row;
            }
            RelationshipPairStateCache[key] =
                new ConcurrentDictionary<string,
                    Dictionary<string, object>>(rows,
                    StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object>
            CloneRelationshipPairCacheRow(Dictionary<string, object> row)
        {
            return row == null
                ? new Dictionary<string, object>(
                    StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object>(row,
                    StringComparer.OrdinalIgnoreCase);
        }

        private static void StoreCommittedRelationshipPairCacheRows(
            string campaignId,
            IDictionary<string, Dictionary<string, object>> rows)
        {
            string key = string.IsNullOrWhiteSpace(campaignId)
                ? "default" : campaignId;
            if (!RelationshipPairStateCache.TryGetValue(key,
                    out ConcurrentDictionary<string,
                        Dictionary<string, object>> cached)
                || rows == null)
                return;
            foreach (KeyValuePair<string, Dictionary<string, object>> row
                in rows)
            {
                cached[row.Key] =
                    CloneRelationshipPairCacheRow(row.Value);
            }
        }

        internal static void InvalidateRelationshipPairStateCache(
            string campaignId, string pairKey = "")
        {
            string key = string.IsNullOrWhiteSpace(campaignId)
                ? "default" : campaignId;
            if (string.IsNullOrWhiteSpace(pairKey))
            {
                RelationshipPairStateCache.TryRemove(key, out _);
                return;
            }
            if (RelationshipPairStateCache.TryGetValue(key,
                out ConcurrentDictionary<string,
                    Dictionary<string, object>> cached))
                cached.TryRemove(pairKey, out _);
        }

        internal static void InvalidateRelationshipPairCacheForMutation(
            ReignDbConnection connection, string sql,
            Dictionary<string, object> parameters)
        {
            string value = (sql ?? string.Empty).TrimStart();
            RelationshipStandingContext.For(connection)?.Invalidate(value);
            if (value.IndexOf("relationship_pair_chemistry",
                    StringComparison.OrdinalIgnoreCase) < 0
                || !(value.StartsWith("UPDATE ",
                        StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("INSERT ",
                        StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("DELETE ",
                        StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("TRUNCATE ",
                        StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("DROP ",
                        StringComparison.OrdinalIgnoreCase)))
                return;
            string campaignId =
                ReignPostgreSqlStorage.CampaignIdForConnection(connection);
            if (string.IsNullOrWhiteSpace(campaignId)) return;
            // This cache represents a complete campaign snapshot. Pair-only
            // eviction would leave a valid-looking but incomplete snapshot.
            // Direct mutations are relatively rare, so force a full lazy reload.
            InvalidateRelationshipPairStateCache(campaignId);
        }

        private static ReignDbCommand CreateRelationshipNativeTargetUpsertCommand(
            ReignDbConnection connection)
        {
            return CreatePreparedRelationshipCommand(connection, @"INSERT INTO relationship_native_targets(
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
target_relation=excluded.target_relation,last_error='',updated_ts=excluded.updated_ts;",
                "$pair", "$actor", "$targetHero", "$targetRelation", "$observed", "$day", "$ts", "$requiresObservation");
        }

        private static string QueueMbtiNativeRelationAction(ReignDbConnection connection, AmbientPairContext pair,
            int day, int target, int observed,
            ReignDbCommand preparedCommand = null,
            List<Dictionary<string, object>> deferredRows = null,
            bool requiresObservation = false)
        {
            string id = "native_target:" + pair.PairKey;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, object> parameters = new Dictionary<string, object>
            {
                ["pair"] = pair.PairKey, ["day"] = day, ["actor"] = pair.HeroAId,
                ["targetHero"] = pair.HeroBId, ["targetRelation"] = target,
                ["observed"] = observed, ["ts"] = ts, ["requiresObservation"] = requiresObservation ? 1 : 0
            };
            if (deferredRows != null)
                deferredRows.Add(parameters);
            else if (preparedCommand != null)
                ExecutePreparedRelationshipCommand(preparedCommand, parameters);
            else
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
target_relation=excluded.target_relation,last_error='',updated_ts=excluded.updated_ts;",
                    parameters);
            return id;
        }

        internal static Dictionary<string, object>
            ApplyAtomicDirectionalRelationshipDelta(
                ReignDbConnection connection,
                string campaignId,
                string observerId,
                string targetId,
                int delta,
                double worldDay,
                string reason,
                string timelineId)
        {
            if (connection == null || string.IsNullOrWhiteSpace(observerId)
                || string.IsNullOrWhiteSpace(targetId)
                || observerId.Equals(targetId,
                    StringComparison.OrdinalIgnoreCase)
                || delta == 0)
                return null;
            string pairKey = AmbientPairKey(observerId, targetId);
            bool observerIsA = pairKey.StartsWith(observerId + "|",
                StringComparison.OrdinalIgnoreCase);
            string affinityColumn = observerIsA
                ? "affinity_a_to_b" : "affinity_b_to_a";
            int day = (int)Math.Floor(worldDay + 0.000001d);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<Dictionary<string, object>> updated = ReignDatabase.QuerySql(connection,
                @"UPDATE relationship_pair_chemistry SET "
                + affinityColumn + @"=MAX(-100,MIN(100,"
                + affinityColumn + @"+$delta)),
last_day=MAX(last_day,$day),last_context_kind=$kind,
last_context_id=$context,state_revision=state_revision+1,
updated_ts=$ts
WHERE pair_key=$pair
RETURNING *;",
                new Dictionary<string, object>
                {
                    ["delta"] = delta, ["day"] = day,
                    ["kind"] = "consequence",
                    ["context"] = Program.LimitText(reason ?? string.Empty, 240),
                    ["ts"] = ts, ["pair"] = pairKey
                });
            Dictionary<string, object> pair = updated.FirstOrDefault();
            if (pair == null) return null;

            int affinityAB = ReadInt(pair, "affinity_a_to_b", 0);
            int affinityBA = ReadInt(pair, "affinity_b_to_a", 0);
            string pairHeroA = ReadString(pair, "hero_a_id", "");
            string pairHeroB = ReadString(pair, "hero_b_id", "");
            int effectiveAB = Clamp(affinityAB + ReadInt(ResolveObserverPublicStanding(
                connection, campaignId, timelineId, pairHeroA, pairHeroB), "value", 0), -100, 100);
            int effectiveBA = Clamp(affinityBA + ReadInt(ResolveObserverPublicStanding(
                connection, campaignId, timelineId, pairHeroB, pairHeroA), "value", 0), -100, 100);
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
effective_affinity_a_to_b=$effectiveAB,
effective_affinity_b_to_a=$effectiveBA,
tag_a_to_b=$tagAB,tag_b_to_a=$tagBA
WHERE pair_key=$pair AND state_revision=$revision;",
                new Dictionary<string, object>
                {
                    ["effectiveAB"] = effectiveAB,
                    ["effectiveBA"] = effectiveBA,
                    ["tagAB"] = DirectionalRelationshipTag(campaignId,
                        pairKey, "a_to_b", affinityAB, affinityBA),
                    ["tagBA"] = DirectionalRelationshipTag(campaignId,
                        pairKey, "b_to_a", affinityBA, affinityAB),
                    ["pair"] = pairKey,
                    ["revision"] = ReadLong(pair, "state_revision", 1)
                });
            pair["effective_affinity_a_to_b"] = effectiveAB;
            pair["effective_affinity_b_to_a"] = effectiveBA;
            pair["tag_a_to_b"] = DirectionalRelationshipTag(campaignId,
                pairKey, "a_to_b", affinityAB, affinityBA);
            pair["tag_b_to_a"] = DirectionalRelationshipTag(campaignId,
                pairKey, "b_to_a", affinityBA, affinityAB);
            RefreshEffectivePairProjection(connection, campaignId,
                timelineId, pair, worldDay, reason);
            Dictionary<string, object> committed = ReignDatabase.QuerySql(connection,
                "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = pairKey })
                .FirstOrDefault() ?? pair;
            // This helper may run inside a caller-owned transaction. Never expose
            // uncommitted rows through RAM; the next read reloads committed state.
            InvalidateRelationshipPairStateCache(campaignId);
            return committed;
        }

        internal static void ApplyAuthoritativeRelationshipDelta(
            ReignDbConnection connection,
            string campaignId,
            string observerId,
            string targetId,
            int delta,
            double worldDay,
            string reason,
            string timelineId = "main",
            bool ensurePair = false)
        {
            if (string.IsNullOrWhiteSpace(observerId) || string.IsNullOrWhiteSpace(targetId)
                || observerId.Equals(targetId, StringComparison.OrdinalIgnoreCase) || (delta == 0 && !ensurePair))
                return;
            lock (CampaignRelationshipWriteLock(campaignId))
            {
            string pairKey = AmbientPairKey(observerId, targetId);
            bool observerIsA = pairKey.StartsWith(observerId + "|", StringComparison.OrdinalIgnoreCase);
            string heroA = observerIsA ? observerId : targetId;
            string heroB = observerIsA ? targetId : observerId;
            int day = (int)Math.Floor(worldDay + 0.000001d);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, object> existing = ReignDatabase.QuerySql(connection,
                "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
            if (existing != null)
            {
                ApplyAtomicDirectionalRelationshipDelta(connection,
                    campaignId, observerId, targetId, delta, worldDay,
                    reason, timelineId);
                return;
            }
            string typeA = existing == null ? ResolvePermanentMbtiTypeById(connection, campaignId, heroA, day)
                : ReadString(existing, "mbti_a", "XXXX");
            string typeB = existing == null ? ResolvePermanentMbtiTypeById(connection, campaignId, heroB, day)
                : ReadString(existing, "mbti_b", "XXXX");
            if (!MbtiDefinitions.ContainsKey(typeA) || !MbtiDefinitions.ContainsKey(typeB))
                throw new InvalidOperationException("An authoritative relationship consequence could not resolve immutable MBTI values for " + pairKey + ".");
            int affinityAB = existing == null ? 0 : ReadInt(existing, "affinity_a_to_b", 0);
            int affinityBA = existing == null ? 0 : ReadInt(existing, "affinity_b_to_a", 0);
            if (observerIsA) affinityAB = Clamp(affinityAB + delta, -100, 100);
            else affinityBA = Clamp(affinityBA + delta, -100, 100);
            int socialAB = ReadInt(ResolveObserverPublicStanding(connection, campaignId,
                timelineId, heroA, heroB), "value", 0);
            int socialBA = ReadInt(ResolveObserverPublicStanding(connection, campaignId,
                timelineId, heroB, heroA), "value", 0);
            int effectiveAB = Clamp(affinityAB + socialAB, -100, 100);
            int effectiveBA = Clamp(affinityBA + socialBA, -100, 100);
            int baseAB = existing == null ? MbtiCompatibility(typeA, typeB) : ReadInt(existing, "base_chance_a_to_b", 50);
            int baseBA = existing == null ? MbtiCompatibility(typeB, typeA) : ReadInt(existing, "base_chance_b_to_a", 50);
            int projected = ProjectNativeRelation(connection, heroA, heroB,
                affinityAB, affinityBA, effectiveAB, effectiveBA);
            Dictionary<string, object> existingNativeTarget = ReignDatabase.QuerySql(connection,
                "SELECT observed_relation,requires_observation FROM relationship_native_targets WHERE pair_key=$pair LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
            int observedNative = existingNativeTarget != null
                ? ReadInt(existingNativeTarget, "observed_relation", 0)
                : existing != null && ReadInt(existing, "native_action_pending", 0) == 0
                    ? ReadInt(existing, "projected_native_relation", 0)
                    : 0;
            bool requiresNativeTarget = projected != observedNative || RelationshipNativeObservationRequired(existing, existingNativeTarget);
            string nativeActionId = requiresNativeTarget ? "native_target:" + pairKey : "";
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
native_action_id=$action,updated_ts=$ts;",
                new Dictionary<string, object>
                {
                    ["pair"] = pairKey, ["a"] = heroA, ["b"] = heroB,
                    ["typeA"] = typeA, ["typeB"] = typeB,
                    ["baseAB"] = baseAB, ["baseBA"] = baseBA,
                    ["chanceAB"] = AdjustedMbtiCompatibility(baseAB),
                    ["chanceBA"] = AdjustedMbtiCompatibility(baseBA),
                    ["affinityAB"] = affinityAB, ["affinityBA"] = affinityBA,
                    ["tagAB"] = DirectionalRelationshipTag(campaignId, pairKey, "a_to_b", affinityAB, affinityBA),
                    ["tagBA"] = DirectionalRelationshipTag(campaignId, pairKey, "b_to_a", affinityBA, affinityAB),
                    ["day"] = day, ["projected"] = projected,
                    ["pending"] = requiresNativeTarget ? 1 : 0,
                    ["action"] = nativeActionId,
                    ["version"] = MbtiChemistryVersion, ["ts"] = ts
                });
            if (existing == null)
                RecordRelationshipPairProvenance(connection, campaignId, timelineId,
                    pairKey, "consequential_event", false, worldDay,
                    new Dictionary<string, object> { ["reason"] = reason ?? string.Empty });
            Dictionary<string, object> current = ReignDatabase.QuerySql(connection,
                "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = pairKey })
                .FirstOrDefault();
            RefreshEffectivePairProjection(connection, campaignId, timelineId,
                current, worldDay, reason);
            }
        }

        private static string ResolvePermanentMbtiTypeById(
            ReignDbConnection connection, string campaignId, string heroId, int day)
        {
            Dictionary<string, object> stored = ReignDatabase.QuerySql(connection,
                "SELECT mbti_type FROM relationship_personalities WHERE hero_id=$hero LIMIT 1;",
                new Dictionary<string, object> { ["hero"] = heroId }).FirstOrDefault();
            string storedType = ReadString(stored, "mbti_type", "");
            if (MbtiDefinitions.ContainsKey(storedType)) return storedType;
            // A cold library load logs under FileLock; a file-input scope supplies the
            // catalog reference it captured before its database locks.
            Dictionary<string, Dictionary<string, object>> catalog =
                RelationshipStandingContext.FilesFor(connection, campaignId)?.Catalog ?? CharactersService.LoadCharacterProfileLibrary();
            if (catalog.TryGetValue(heroId, out Dictionary<string, object> pregenerated))
            {
                Dictionary<string, object> mbti = ReadDictionary(pregenerated, "mbtiProfile")
                    ?? ReadDictionary(ReadDictionary(pregenerated, "traits"), "mbtiProfile");
                string catalogType = ReadString(mbti, "type", "");
                if (!MbtiDefinitions.ContainsKey(catalogType)) return "XXXX";
                ResolvePermanentRelationshipMbti(connection, campaignId, day,
                    new Dictionary<string, object> { ["heroStringId"] = heroId, ["name"] = heroId },
                    new Dictionary<string, Dictionary<string, object>>());
                return catalogType;
            }
            Dictionary<string, object> assigned = ResolvePermanentRelationshipMbti(connection, campaignId, day,
                new Dictionary<string, object> { ["heroStringId"] = heroId, ["name"] = heroId },
                new Dictionary<string, Dictionary<string, object>>());
            return ReadString(assigned, "type", "XXXX");
        }

        private static void CompactNoisyNativeRelationBacklog(ReignDbConnection connection)
        {
            if (ReadString(ReignDatabase.QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='mbti_native_relation_bulk_projection_v1' LIMIT 1;")
                .FirstOrDefault(), "value", "") == "1") return;

            ReignDatabase.ExecuteSql(connection, "SAVEPOINT compact_native_relation_backlog;");
            try
            {
                ReignDatabase.ExecuteSql(connection, "UPDATE relationship_native_targets SET status='pending',claimed_ts=0,last_error='';");
                if (ReadInt(ReignDatabase.QuerySql(connection,
                    "SELECT COUNT(*) AS count FROM sqlite_master WHERE type='table' AND name='relationship_director_actions';")
                    .FirstOrDefault(), "count", 0) > 0)
                {
                    ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_director_actions
SET status='superseded',resolved_ts=$ts,
result_json='{""status"":""superseded"",""reason"":""native_relation_bulk_projection""}'
WHERE action_type='native_relation' AND status IN ('pending','claimed');",
                        new Dictionary<string, object> { ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                }
                ReignDatabase.ExecuteSql(connection,
                    "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('mbti_native_relation_bulk_projection_v1','1');");
                ReignDatabase.ExecuteSql(connection, "RELEASE compact_native_relation_backlog;");
            }
            catch
            {
                try { ReignDatabase.ExecuteSql(connection, "ROLLBACK TO compact_native_relation_backlog;"); } catch { }
                try { ReignDatabase.ExecuteSql(connection, "RELEASE compact_native_relation_backlog;"); } catch { }
                throw;
            }
        }
    }
}
