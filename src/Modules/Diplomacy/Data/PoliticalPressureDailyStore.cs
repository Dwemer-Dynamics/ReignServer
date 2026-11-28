using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DiplomacyStore
    {
        internal static void CreatePoliticalPressureDailyProducerSchema(ReignDbConnection connection)
        {
            ReignDatabase.ExecuteSql(connection, @"
CREATE TABLE IF NOT EXISTS political_pressure_daily_producers (
  campaign_id TEXT NOT NULL,
  timeline_id TEXT NOT NULL,
  day_key INTEGER NOT NULL,
  world_day DOUBLE PRECISION NOT NULL,
  daily_roll INTEGER NOT NULL CHECK (daily_roll BETWEEN 1 AND 100),
  storage_witness TEXT NOT NULL CHECK (length(storage_witness) > 0),
  restore_generation TEXT NOT NULL,
  claim_id TEXT NOT NULL CHECK (length(claim_id) = 32),
  claim_expires_utc TIMESTAMPTZ NOT NULL,
  input_json TEXT NOT NULL,
  input_sha256 TEXT NOT NULL CHECK (length(input_sha256) = 64),
  prepared_json TEXT,
  prepared_sha256 TEXT CHECK (length(prepared_sha256) = 64),
  result_json TEXT,
  result_sha256 TEXT CHECK (length(result_sha256) = 64),
  PRIMARY KEY (campaign_id,timeline_id,day_key),
  CHECK (day_key >= 0 AND world_day >= day_key AND world_day < day_key + 1.0),
  CHECK ((prepared_json IS NULL) = (prepared_sha256 IS NULL)),
  CHECK ((result_json IS NULL) = (result_sha256 IS NULL)),
  CHECK (result_json IS NULL OR prepared_json IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS idx_pressure_daily_producers_unfinished
  ON political_pressure_daily_producers(campaign_id,timeline_id,day_key)
  WHERE result_json IS NULL;
CREATE TABLE IF NOT EXISTS political_pressure_daily_holds (
  campaign_id TEXT NOT NULL,
  timeline_id TEXT NOT NULL,
  day_key INTEGER NOT NULL,
  storage_witness TEXT NOT NULL,
  restore_generation TEXT NOT NULL,
  reason TEXT NOT NULL,
  recorded_utc TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
  PRIMARY KEY(campaign_id,timeline_id,day_key,storage_witness,restore_generation)
);");
        }

        internal static Dictionary<string, object> ReadPoliticalPressureStorageWitness(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> scope)
        {
            // The registry and physical schema are local authorities. A
            // backup can carry its old restore generation, but cannot
            // carry the registry timestamp or replace the current schema OID.
            return ReignDatabase.QuerySql(connection, @"
SELECT n.oid::text || ':' || extract(epoch from r.updated_utc)::text AS storage_witness,
       p.restore_generation
FROM reign_meta.campaign_registry r
JOIN pg_namespace n ON n.nspname=r.schema_name AND n.nspname=current_schema()
JOIN world_history_timelines w ON w.campaign_id=r.campaign_id AND w.timeline_id=$timeline AND w.is_active=1
JOIN memory_precision_state p ON p.state_id='current'
WHERE r.campaign_id=$campaign;", scope, transaction).SingleOrDefault();
        }

        internal static Dictionary<string, object> FindPoliticalPressureDailyProducer(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> scope)
            => ReignDatabase.QuerySql(connection, @"
SELECT * FROM political_pressure_daily_producers
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day;", scope, transaction).SingleOrDefault();

        internal static int ReadLastTerminalPoliticalPressureDay(ReignDbConnection connection,
            Dictionary<string, object> scope)
        {
            var row = ReignDatabase.QuerySql(connection, @"
SELECT MAX(day_key) AS latest_day
FROM political_pressure_daily_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key<=$day
AND status IN ('incident_created','roll_failed','no_eligible_kingdoms','no_eligible_target','catalog_cooldown','no_eligible_petitioners');",
                scope).Single();
            return row["latest_day"] == null || row["latest_day"] is System.DBNull ? int.MinValue
                : System.Convert.ToInt32(row["latest_day"], System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static int FindFirstUnheldPoliticalPressureDay(ReignDbConnection connection,
            Dictionary<string, object> scope)
        {
            var row = ReignDatabase.QuerySql(connection, @"
WITH held AS (
  SELECT day_key FROM political_pressure_daily_holds
  WHERE campaign_id=$campaign AND timeline_id=$timeline AND storage_witness=$storage
    AND restore_generation=$generation AND day_key >= $start AND day_key <= $day
)
SELECT MIN(candidate) AS first_day
FROM (SELECT CAST($start AS BIGINT) AS candidate UNION ALL SELECT day_key+1 FROM held) AS candidates
WHERE candidate <= $day AND NOT EXISTS(SELECT 1 FROM held WHERE day_key=candidate);", scope).Single();
            return row["first_day"] == null || row["first_day"] is System.DBNull ? (int)scope["day"]
                : System.Convert.ToInt32(row["first_day"], System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static IReadOnlyList<int> FindUnfinishedPoliticalPressureDays(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> scope)
            => ReignDatabase.QuerySql(connection, @"
SELECT pending.day_key
FROM (
  SELECT day_key FROM political_pressure_daily_producers
  WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key<=$day AND result_json IS NULL
  UNION
  SELECT day_key FROM political_pressure_daily_rolls
  WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key<=$day AND status='processing'
) AS pending
WHERE NOT EXISTS (
  SELECT 1 FROM political_pressure_daily_holds h
  WHERE h.campaign_id=$campaign AND h.timeline_id=$timeline AND h.day_key=pending.day_key
    AND h.storage_witness=$storage AND h.restore_generation=$generation
)
ORDER BY pending.day_key LIMIT 32;", scope, transaction)
                .Select(row => System.Convert.ToInt32(row["day_key"], System.Globalization.CultureInfo.InvariantCulture))
                .ToList();

        internal static Dictionary<string, object> ReadPoliticalPressureDailyHold(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> scope)
            => ReignDatabase.QuerySql(connection, @"
SELECT reason FROM political_pressure_daily_holds
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day
  AND storage_witness=$storage AND restore_generation=$generation;", scope, transaction).SingleOrDefault();

        internal static void InsertPoliticalPressureDailyHold(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> scope)
            => ReignDatabase.ExecuteSql(connection, @"
INSERT INTO political_pressure_daily_holds(campaign_id,timeline_id,day_key,storage_witness,restore_generation,reason)
VALUES($campaign,$timeline,$day,$storage,$generation,$reason)
ON CONFLICT(campaign_id,timeline_id,day_key,storage_witness,restore_generation) DO NOTHING;", scope, transaction);

        // One keyset window. The caller owns the active campaign, timeline and restore
        // witness and holds the pressure advisory transaction lock before this statement.
        internal static (int? LastDay, int Inserted) RepairLegacyPoliticalPressureMetadataWindow(
            ReignDbConnection connection, ReignDbTransaction transaction, Dictionary<string, object> scope)
        {
            var row = ReignDatabase.QuerySql(connection, @"
WITH day_window AS MATERIALIZED (
 SELECT d.* FROM political_pressure_daily_rolls d
 WHERE d.campaign_id=$campaign AND d.timeline_id=$timeline
   AND d.day_key>$after AND d.day_key<=$through
 ORDER BY d.day_key LIMIT 32
), proved AS (
 SELECT i.*, i.correlation_id || ':incident_completed::' || i.incident_id AS completion_id,
        'daily:' || CAST(d.day_key AS text) AS daily_id
 FROM day_window d
 JOIN political_pressure_incidents i ON i.campaign_id=d.campaign_id AND i.timeline_id=d.timeline_id
   AND i.day_key=d.day_key AND i.incident_id=d.incident_id
 WHERE d.status='incident_created' AND d.passed=1 AND d.chance=$chance AND d.roll BETWEEN 1 AND d.chance
   AND i.incident_id='political-pressure:' || d.timeline_id || ':' || CAST(d.day_key AS text)
   AND i.correlation_id=i.incident_id
   AND i.origin_kingdom_id=d.origin_kingdom_id AND i.origin_kingdom_id<>''
   AND i.target_kingdom_id<>'' AND i.origin_kingdom_id<>i.target_kingdom_id
   AND i.origin_ruler_id<>'' AND i.target_ruler_id<>''
   AND i.world_day=d.world_day AND d.world_day>=d.day_key AND d.world_day<d.day_key+1
   AND i.status='completed' AND i.notice_status IN ('ready','fetched','acknowledged') AND btrim(i.narrative)<>''
   AND i.llm_selector_status IN ('llm','invalid_fallback','unavailable_fallback')
   AND i.llm_narration_status IN ('llm','invalid_fallback','unavailable_fallback')
 FOR SHARE OF i
), eligible AS (
 SELECT p.* FROM proved p
 LEFT JOIN political_pressure_activity a ON a.activity_id=p.completion_id
 WHERE a.activity_id IS NULL OR (a.campaign_id=p.campaign_id AND a.timeline_id=p.timeline_id
       AND a.incident_id=p.incident_id AND a.event_type='incident_completed' AND a.status='completed'
       AND a.correlation_id=p.correlation_id AND a.action_id='' AND a.world_day=p.world_day
       AND a.actor_kingdom_id=p.origin_kingdom_id AND a.target_kingdom_id=p.target_kingdom_id
       AND a.actor_ruler_id=p.origin_ruler_id AND a.target_ruler_id=p.target_ruler_id)
), activity AS (
 INSERT INTO political_pressure_activity(activity_id,campaign_id,timeline_id,world_day,incident_id,
    actor_kingdom_id,target_kingdom_id,actor_ruler_id,target_ruler_id,event_type,polarity,channel,severity,
    status,before_value,after_value,roll,chance,action_id,correlation_id,reason,payload_json,created_ts)
 SELECT completion_id,campaign_id,timeline_id,world_day,incident_id,
    origin_kingdom_id,target_kingdom_id,origin_ruler_id,target_ruler_id,'incident_completed',polarity,channel,severity,
    'completed',origin_pressure_before,origin_pressure_after,polarity_roll,hostile_chance,'',correlation_id,
    origin_stance || ' / ' || target_stance,
    CAST(json_build_object('originStance',origin_stance,'targetStance',target_stance,'pressureAmount',pressure_amount,
                           'selectorStatus',llm_selector_status,'narrationStatus',llm_narration_status) AS text),$ts
 FROM eligible ON CONFLICT(activity_id) DO NOTHING RETURNING activity_id
), counters AS (
 INSERT INTO world_test_chunk_counters(campaign_id,timeline_id,day_key,subsystem,chunk_key,counters_json,
     created_ts,updated_ts)
 SELECT campaign_id,timeline_id,day_key,'political_pressures',
     daily_id, '{""dailyRolls"":1,""dailyRollsPassed"":1,""status:incident_created"":1}',$ts,$ts
 FROM eligible
 UNION ALL
 SELECT campaign_id,timeline_id,day_key,'political_pressures',
     completion_id, '{""activity:incident_completed"":1,""status:completed"":1}',$ts,$ts
 FROM eligible
 ON CONFLICT(campaign_id,timeline_id,day_key,subsystem,chunk_key) DO NOTHING RETURNING chunk_key
)
SELECT (SELECT MAX(day_key) FROM day_window)::integer AS last_day,
       ((SELECT COUNT(*) FROM activity) + (SELECT COUNT(*) FROM counters))::integer AS inserted;",
                scope, transaction, commandTimeoutSeconds: 2).Single();
            // Legacy day_key uses bigint on PostgreSQL. The int caller bounds make this projection exact.
            return (row["last_day"] is null ? (int?)null : (int)row["last_day"], (int)row["inserted"]);
        }

        internal static Dictionary<string, object> ReadPressureRepairOwner(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> scope)
        {
            var ready = ReignDatabase.QuerySql(connection, @"
SELECT to_regclass(current_schema() || '.world_history_timelines') IS NOT NULL AND
       to_regclass(current_schema() || '.memory_precision_state') IS NOT NULL AND
       to_regclass(current_schema() || '.political_pressure_daily_rolls') IS NOT NULL AND
       to_regclass(current_schema() || '.political_pressure_incidents') IS NOT NULL AND
       to_regclass(current_schema() || '.political_pressure_activity') IS NOT NULL AND
       to_regclass(current_schema() || '.world_test_chunk_counters') IS NOT NULL AS ready;",
                scope, transaction, commandTimeoutSeconds: 2).Single();
            if (!ReadBool(ready, "ready", false)) return null;

            var owners = ReignDatabase.QuerySql(connection, @"
SELECT w.timeline_id,n.oid::text || ':' || extract(epoch from r.updated_utc)::text AS storage_witness,
       p.restore_generation
FROM reign_meta.campaign_registry r
JOIN pg_namespace n ON n.nspname=r.schema_name AND n.nspname=current_schema()
JOIN world_history_timelines w ON w.campaign_id=r.campaign_id AND w.is_active=1
JOIN memory_precision_state p ON p.state_id='current'
WHERE r.campaign_id=$campaign
LIMIT 2 FOR SHARE OF r,w,p;", scope, transaction, commandTimeoutSeconds: 2);
            return owners.Count == 1 ? owners[0] : null;
        }

        internal static Dictionary<string, object> ReadPressureRepairCursor(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> scope)
            => ReignDatabase.QuerySql(connection, @"
SELECT last_day,through_day,retry_after IS NOT NULL AS completed,
       COALESCE(retry_after>clock_timestamp(),false) AS cooling_down
FROM reign_meta.political_pressure_repair_cursor
WHERE campaign_id=$campaign AND timeline_id=$timeline
  AND storage_witness=$witness AND restore_generation=$restore;",
                scope, transaction, commandTimeoutSeconds: 2).SingleOrDefault();

        internal static int ReadPressureRepairHighWater(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> scope)
            => ReadInt(ReignDatabase.QuerySql(connection, @"
SELECT day_key FROM political_pressure_daily_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key>=0
ORDER BY day_key DESC LIMIT 1;", scope, transaction, commandTimeoutSeconds: 2).SingleOrDefault(), "day_key", -1);

        internal static void PublishPressureRepairCursor(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> scope)
        {
            if (ReignDatabase.QuerySql(connection, @"
INSERT INTO reign_meta.political_pressure_repair_cursor(campaign_id,timeline_id,storage_witness,
 restore_generation,last_day,through_day,retry_after)
VALUES($campaign,$timeline,$witness,$restore,$last,$through,
 CASE WHEN $completed THEN clock_timestamp()+interval '1 day' ELSE NULL END)
ON CONFLICT(campaign_id) DO UPDATE SET timeline_id=excluded.timeline_id,storage_witness=excluded.storage_witness,
 restore_generation=excluded.restore_generation,last_day=excluded.last_day,through_day=excluded.through_day,
 retry_after=excluded.retry_after
RETURNING campaign_id;", scope, transaction, commandTimeoutSeconds: 2).Count != 1)
                throw new System.InvalidOperationException("Could not publish the exact pressure repair cursor.");
        }

        internal static Dictionary<string, object> ReadPublishedLegacyPressureIncident(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> scope)
            => ReignDatabase.QuerySql(connection, @"
SELECT incident_id,world_day,origin_kingdom_id,polarity,severity
FROM political_pressure_incidents
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day AND incident_id=$incident
  AND correlation_id=$incident AND status='completed' AND length(trim(narrative))>0
  AND notice_status IN ('ready','fetched','acknowledged')
  AND llm_selector_status IN ('llm','invalid_fallback','unavailable_fallback')
  AND llm_narration_status IN ('llm','invalid_fallback','unavailable_fallback');", scope, transaction).SingleOrDefault();

        internal static bool TryFinalizePublishedLegacyPressureDay(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> scope)
            => ReignDatabase.QuerySql(connection, @"
INSERT INTO political_pressure_daily_rolls(campaign_id,timeline_id,day_key,world_day,chance,roll,passed,
  status,origin_kingdom_id,incident_id,created_ts)
VALUES($campaign,$timeline,$day,$worldDay,$chance,$roll,1,'incident_created',$origin,$incident,$ts)
ON CONFLICT(campaign_id,timeline_id,day_key) DO UPDATE
SET passed=1,status='incident_created',origin_kingdom_id=$origin,incident_id=$incident
WHERE political_pressure_daily_rolls.status='processing'
  AND political_pressure_daily_rolls.world_day=$worldDay
  AND political_pressure_daily_rolls.chance=$chance AND political_pressure_daily_rolls.roll=$roll
  AND political_pressure_daily_rolls.incident_id IN ('',$incident)
RETURNING day_key;", scope, transaction).Count == 1;

        internal static bool InsertMissingLegacyPressureRollCounter(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> scope)
            => ReignDatabase.QuerySql(connection, @"
INSERT INTO world_test_chunk_counters(campaign_id,timeline_id,day_key,subsystem,chunk_key,counters_json,created_ts,updated_ts)
VALUES($campaign,$timeline,$day,'political_pressures',$chunk,$counter,$ts,$ts)
ON CONFLICT(campaign_id,timeline_id,day_key,subsystem,chunk_key) DO NOTHING RETURNING chunk_key;", scope, transaction).Count == 1;

        internal static bool HasLegacyPoliticalPressureFootprint(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> scope)
        {
            var row = ReignDatabase.QuerySql(connection, @"
SELECT (
    EXISTS(SELECT 1 FROM political_pressure_incidents
      WHERE campaign_id=$campaign AND timeline_id=$timeline AND (incident_id=$incident OR day_key=$day))
 OR EXISTS(SELECT 1 FROM political_pressure_state
      WHERE campaign_id=$campaign AND timeline_id=$timeline AND last_incident_id=$incident)
 OR EXISTS(SELECT 1 FROM political_pressure_delta_receipts
      WHERE campaign_id=$campaign AND timeline_id=$timeline AND incident_id=$incident)
 OR EXISTS(SELECT 1 FROM ruler_diplomatic_incidents
      WHERE campaign_id=$campaign AND timeline_id=$timeline AND source_id=$incident)
 OR EXISTS(SELECT 1 FROM political_pressure_native_effects
      WHERE campaign_id=$campaign AND timeline_id=$timeline AND incident_id=$incident)
 OR EXISTS(SELECT 1 FROM world_test_chunk_counters
      WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day AND subsystem='political_pressures'
      AND (chunk_key=$incident OR strpos(chunk_key,$incident || ':')=1 OR chunk_key='daily:' || CAST($day AS TEXT)))
 OR EXISTS(SELECT 1 FROM political_pressure_activity
      WHERE campaign_id=$campaign AND timeline_id=$timeline AND incident_id=$incident)
" +
#if !REIGN_EXCLUDE_COURT
@" OR EXISTS(SELECT 1 FROM international_court_queue
      WHERE campaign_id=$campaign AND timeline_id=$timeline AND matter_id=$incident)" +
#endif
") AS has_footprint;", scope, transaction).Single();
            return row["has_footprint"] is true;
        }

        internal static bool HasExactPoliticalPressureLoyaltyEffect(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> effect)
            => ReignDatabase.QuerySql(connection, @"
SELECT effect_id FROM political_pressure_native_effects
WHERE effect_id=$id AND incident_id=$incident AND campaign_id=$campaign AND timeline_id=$timeline
AND world_day=$day AND effect_type='settlement_loyalty' AND target_id=$target AND amount=-10;",
                effect, transaction).Count == 1;

        internal static bool TryClaimPoliticalPressureDailyProducer(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> claim)
            => ReignDatabase.QuerySql(connection, @"
INSERT INTO political_pressure_daily_producers AS current_producer(
  campaign_id,timeline_id,day_key,world_day,daily_roll,storage_witness,restore_generation,
  claim_id,claim_expires_utc,input_json,input_sha256)
VALUES($campaign,$timeline,$day,$worldDay,$roll,$storage,$generation,
       $claim,clock_timestamp()+interval '5 minutes',$input,$inputSha)
ON CONFLICT (campaign_id,timeline_id,day_key) DO UPDATE
SET claim_id=excluded.claim_id,claim_expires_utc=excluded.claim_expires_utc
WHERE current_producer.storage_witness=excluded.storage_witness
  AND current_producer.restore_generation=excluded.restore_generation
  AND current_producer.result_json IS NULL
  AND current_producer.claim_expires_utc<=clock_timestamp()
RETURNING day_key;", claim, transaction).Count == 1;

        internal static bool SealPoliticalPressureDailyProducer(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> prepared)
            => ReignDatabase.QuerySql(connection, @"
UPDATE political_pressure_daily_producers
SET prepared_json=$prepared,prepared_sha256=$preparedSha
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day
  AND storage_witness=$storage AND restore_generation=$generation
  AND claim_id=$claim AND claim_expires_utc>clock_timestamp()
  AND input_sha256=$inputSha AND prepared_json IS NULL AND result_json IS NULL
RETURNING day_key;", prepared, transaction).Count == 1;

        internal static Dictionary<string, object> LockPoliticalPressureDailyProducer(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> prepared)
            => ReignDatabase.QuerySql(connection, @"
SELECT * FROM political_pressure_daily_producers
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day
  AND storage_witness=$storage AND restore_generation=$generation
  AND claim_id=$claim AND claim_expires_utc>clock_timestamp()
  AND input_sha256=$inputSha AND prepared_sha256=$preparedSha AND result_json IS NULL
FOR UPDATE;", prepared, transaction).SingleOrDefault();

        internal static bool CompletePoliticalPressureDailyProducer(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> completed)
            => ReignDatabase.QuerySql(connection, @"
UPDATE political_pressure_daily_producers
SET result_json=$result,result_sha256=$resultSha
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day
  AND storage_witness=$storage AND restore_generation=$generation
  AND claim_id=$claim AND input_sha256=$inputSha AND prepared_sha256=$preparedSha AND result_json IS NULL
RETURNING day_key;", completed, transaction).Count == 1;
    }
}