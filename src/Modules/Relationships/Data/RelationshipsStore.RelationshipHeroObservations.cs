using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static void SelectRelationshipObservedHeroes4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
WITH incoming AS MATERIALIZED (
 SELECT * FROM jsonb_to_recordset(CAST($rows AS jsonb)) AS r(
  hero text,profile text,hash text,day integer,ts bigint)
), versions AS (
 INSERT INTO relationship_hero_observations(timeline_id,hero_id,observed_day,profile_json,profile_hash)
 SELECT $timeline,o.hero_id,o.last_observed_day,o.profile_json,o.profile_hash
 FROM relationship_observed_heroes o JOIN incoming x ON x.hero=o.hero_id
 WHERE o.last_observed_day<x.day AND (o.profile_hash<>x.hash OR NOT EXISTS (
  SELECT 1 FROM relationship_hero_observations h WHERE h.timeline_id=$timeline AND h.hero_id=o.hero_id))
 UNION ALL
 SELECT $timeline,hero,day,profile,hash FROM incoming
 ON CONFLICT(timeline_id,hero_id,observed_day) DO UPDATE SET
  profile_json=excluded.profile_json,profile_hash=excluded.profile_hash
 RETURNING hero_id
)
INSERT INTO relationship_observed_heroes(
 hero_id,profile_json,profile_hash,first_observed_day,last_observed_day,updated_ts)
SELECT hero,profile,hash,day,day,ts FROM incoming
ON CONFLICT(hero_id) DO UPDATE SET
 profile_json=excluded.profile_json,profile_hash=excluded.profile_hash,
 last_observed_day=excluded.last_observed_day,
 updated_ts=CASE WHEN relationship_observed_heroes.profile_hash<>excluded.profile_hash
  THEN excluded.updated_ts ELSE relationship_observed_heroes.updated_ts END
WHERE excluded.last_observed_day>=relationship_observed_heroes.last_observed_day
 AND (relationship_observed_heroes.profile_hash<>excluded.profile_hash
  OR excluded.last_observed_day>relationship_observed_heroes.last_observed_day);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipObservedHeroes5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT o.hero_id,o.profile_json FROM relationship_observed_heroes o
JOIN jsonb_array_elements_text(CAST($heroes AS jsonb)) requested ON requested.value=o.hero_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectJsonbArrayElementsText(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT requested.value AS hero_id,o.last_observed_day,
 COALESCE(h.profile_json,CASE WHEN o.last_observed_day<=$day THEN o.profile_json END) AS profile_json
FROM jsonb_array_elements_text(CAST($heroes AS jsonb)) requested
LEFT JOIN relationship_observed_heroes o ON o.hero_id=requested.value
LEFT JOIN LATERAL (
 SELECT profile_json FROM relationship_hero_observations h
 WHERE h.timeline_id=$timeline AND h.hero_id=requested.value AND h.observed_day<=$day
 ORDER BY h.observed_day DESC LIMIT 1
) h ON TRUE;", parameters, transaction);
        }

        internal static void SelectStatement(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"WITH retention AS (
 SELECT LEAST($cutoff,COALESCE(MIN(day_key)-$window,$cutoff)) AS cutoff
 FROM relationship_daily_inputs WHERE timeline_id=$timeline AND status IN ('pending','processing')
)
DELETE FROM relationship_hero_observations older USING retention
WHERE older.timeline_id=$timeline AND older.observed_day<retention.cutoff AND EXISTS (
 SELECT 1 FROM relationship_hero_observations newer
 WHERE newer.timeline_id=older.timeline_id AND newer.hero_id=older.hero_id
 AND newer.observed_day>older.observed_day AND newer.observed_day<=retention.cutoff);", parameters, transaction);
        }
    }
}
