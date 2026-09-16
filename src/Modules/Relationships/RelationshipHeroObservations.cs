using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void WriteRelationshipHeroObservations(ReignDbConnection connection,
            string timelineId, List<Dictionary<string, object>> rows)
        {
            if (rows.Count == 0) return;
            // Preserve a pre-upgrade predecessor before replacing the latest document.
            // Both history and latest state commit with their durable daily input.
            ExecuteSql(connection, @"
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
  OR excluded.last_observed_day>relationship_observed_heroes.last_observed_day);",
                new Dictionary<string, object> { ["rows"] = Json.Serialize(rows), ["timeline"] = timelineId });
        }

        private static List<Dictionary<string, object>> LoadRelationshipHeroObservations(
            ReignDbConnection connection, string timelineId, int day, IEnumerable<string> heroIds)
        {
            var ids = heroIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (ids.Count == 0) return new List<Dictionary<string, object>>();
            var parameters = new Dictionary<string, object>
            { ["heroes"] = Json.Serialize(ids), ["timeline"] = timelineId, ["day"] = day };
            if (RelationshipThroughputOriginalReadPath)
                return QuerySql(connection, @"SELECT o.hero_id,o.profile_json FROM relationship_observed_heroes o
JOIN jsonb_array_elements_text(CAST($heroes AS jsonb)) requested ON requested.value=o.hero_id;", parameters);

            var rows = QuerySql(connection, @"
SELECT requested.value AS hero_id,o.last_observed_day,
 COALESCE(h.profile_json,CASE WHEN o.last_observed_day<=$day THEN o.profile_json END) AS profile_json
FROM jsonb_array_elements_text(CAST($heroes AS jsonb)) requested
LEFT JOIN relationship_observed_heroes o ON o.hero_id=requested.value
LEFT JOIN LATERAL (
 SELECT profile_json FROM relationship_hero_observations h
 WHERE h.timeline_id=$timeline AND h.hero_id=requested.value AND h.observed_day<=$day
 ORDER BY h.observed_day DESC LIMIT 1
) h ON TRUE;", parameters);
            foreach (var row in rows)
                if (row["profile_json"] == null && ReadInt(row, "last_observed_day", -1) > day)
                    throw new InvalidOperationException("Relationship input history is unavailable for hero "
                        + ReadString(row, "hero_id", "") + " on day " + day
                        + "; refusing to apply a newer profile to an older queued day.");
            return rows.Where(row => row["profile_json"] != null).ToList();
        }

        private static void PruneRelationshipHeroObservations(ReignDbConnection connection,
            string timelineId, int processedDay)
        {
            // Keep the newest predecessor plus all changes in/after the retained
            // window. A backlog's future inputs are never eligible for removal.
            ExecuteSql(connection, @"WITH retention AS (
 SELECT LEAST($cutoff,COALESCE(MIN(day_key)-$window,$cutoff)) AS cutoff
 FROM relationship_daily_inputs WHERE timeline_id=$timeline AND status IN ('pending','processing')
)
DELETE FROM relationship_hero_observations older USING retention
WHERE older.timeline_id=$timeline AND older.observed_day<retention.cutoff AND EXISTS (
 SELECT 1 FROM relationship_hero_observations newer
 WHERE newer.timeline_id=older.timeline_id AND newer.hero_id=older.hero_id
 AND newer.observed_day>older.observed_day AND newer.observed_day<=retention.cutoff);",
                new Dictionary<string, object>
                { ["timeline"] = timelineId, ["cutoff"] = processedDay - Math.Max(7, RelationshipCadenceDays),
                    ["window"] = Math.Max(7, RelationshipCadenceDays) });
        }
    }
}
