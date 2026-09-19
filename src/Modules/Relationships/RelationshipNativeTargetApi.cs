using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static bool RelationshipNativeObservationRequired(
            Dictionary<string, object> pair, Dictionary<string, object> target)
        {
            return ReadInt(target, "requires_observation", 0) == 1
                || target == null && ReadInt(pair, "native_action_pending", 0) == 1;
        }

        private static Dictionary<string, object> RelationshipNativeTargetPullApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            const int maxBatchSize = 1024;
            int limit = Clamp(ReadInt(payload, "limit", maxBatchSize), 1, maxBatchSize);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                List<Dictionary<string, object>> rows;
                int prunedAlignedCount;
                ExecuteSql(connection, "BEGIN IMMEDIATE;");
                try
                {
                    ExecuteSql(connection, @"UPDATE relationship_native_targets target SET
target_relation=pair.projected_native_relation,requires_observation=1,status='pending',
revision=target.revision+1,claimed_ts=0,last_error='projection_changed_requires_observation',updated_ts=$ts
FROM relationship_pair_chemistry pair WHERE pair.pair_key=target.pair_key
AND pair.native_action_pending=1 AND pair.projected_native_relation<>target.target_relation;",
                        new Dictionary<string, object> { ["ts"] = ts });
                    // An old successful receipt can remove its target after the pair
                    // projection has changed. Orphan flags require a real native read;
                    // equality here is deliberately NOT evidence of alignment.
                    ExecuteSql(connection, @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,world_day,last_sync_day,
attempt_count,claimed_ts,last_error,revision,updated_ts,requires_observation)
SELECT pair.pair_key,pair.hero_a_id,pair.hero_b_id,pair.projected_native_relation,pair.projected_native_relation,
'pending',pair.last_day,-1000,0,0,'native_observation_required',1,$ts,1
FROM relationship_pair_chemistry pair
WHERE pair.native_action_pending=1 AND NOT EXISTS (
 SELECT 1 FROM relationship_native_targets target WHERE target.pair_key=pair.pair_key)
ON CONFLICT(pair_key) DO NOTHING;", new Dictionary<string, object> { ["ts"] = ts });
                    prunedAlignedCount = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_native_targets
WHERE target_relation=observed_relation AND requires_observation=0;").FirstOrDefault(), "count", 0);
                    ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
native_action_pending=0,native_action_id='',updated_ts=$ts
WHERE pair_key IN (
    SELECT pair_key FROM relationship_native_targets
    WHERE target_relation=observed_relation AND requires_observation=0
);", new Dictionary<string, object> { ["ts"] = ts });
                    ExecuteSql(connection, @"
DELETE FROM relationship_native_targets
WHERE target_relation=observed_relation AND requires_observation=0;");
                    rows = QuerySql(connection, @"
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
world_day,pair_key LIMIT " + limit + ";",
                        new Dictionary<string, object> { ["expired"] = ts - 15 });
                    if (ReignPostgreSqlDialect.IsPostgreSql(connection)
                        && rows.Count > 0)
                    {
                        string claimRows = Json.Serialize(rows.Select(row =>
                            new Dictionary<string, object>
                            {
                                ["pair"] = ReadString(row, "pair_key", ""),
                                ["revision"] = ReadInt(row, "revision", 1)
                            }).ToList());
                        ExecutePostgreSqlJsonCommand(connection, @"
UPDATE relationship_native_targets AS target SET
status='claimed',claimed_ts=" + ts + @",updated_ts=" + ts + @"
FROM jsonb_to_recordset(@rows) AS x(pair text,revision integer)
WHERE target.pair_key=x.pair
AND target.revision=x.revision
AND (target.target_relation<>target.observed_relation OR target.requires_observation=1)
AND (target.status='pending'
 OR (target.status='claimed' AND target.claimed_ts<"
                            + (ts - 15) + "));", claimRows);
                    }
                    else
                    {
                        foreach (Dictionary<string, object> row in rows)
                        {
                            ExecuteSql(connection, @"UPDATE relationship_native_targets
SET status='claimed',claimed_ts=$ts,updated_ts=$ts
WHERE pair_key=$pair AND revision=$revision
AND (target_relation<>observed_relation OR requires_observation=1)
AND (status='pending' OR (status='claimed' AND claimed_ts<$expired));",
                                new Dictionary<string, object>
                                {
                                    ["ts"] = ts,
                                    ["expired"] = ts - 15,
                                    ["pair"] = ReadString(row, "pair_key", ""),
                                    ["revision"] = ReadInt(row, "revision", 1)
                                });
                        }
                    }
                    ExecuteSql(connection, "COMMIT;");
                }
                catch
                {
                    try { ExecuteSql(connection, "ROLLBACK;"); } catch { }
                    throw;
                }
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["campaignId"] = campaignId,
                    ["timelineId"] = timelineId,
                    ["targets"] = rows.Select(row => new Dictionary<string, object>
                    {
                        ["pairKey"] = ReadString(row, "pair_key", ""),
                        ["heroAId"] = ReadString(row, "hero_a_id", ""),
                        ["heroBId"] = ReadString(row, "hero_b_id", ""),
                        ["targetRelation"] = ReadInt(row, "target_relation", 0),
                        ["observedRelation"] = ReadInt(row, "observed_relation", 0),
                        ["sourceDay"] = ReadDouble(row, "world_day", 0d),
                        ["revision"] = ReadInt(row, "revision", 1)
                    }).ToList(),
                    ["targetCount"] = rows.Count,
                    ["prunedAlignedCount"] = prunedAlignedCount,
                    ["batchLimit"] = maxBatchSize
                };
            }
        }

        private static Dictionary<string, object> RelationshipNativeTargetReceiptsApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            List<Dictionary<string, object>> receipts = ReadDictionaryList(payload, "receipts");
            int accepted = 0, stale = 0, failed = 0;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                ExecuteSql(connection, "BEGIN IMMEDIATE;");
                try
                {
                    if (ReignPostgreSqlDialect.IsPostgreSql(connection))
                    {
                        List<Dictionary<string, object>> normalized =
                            receipts.Select(receipt =>
                                new Dictionary<string, object>
                                {
                                    ["pair"] = ReadString(receipt, "pairKey", ""),
                                    ["revision"] = ReadInt(receipt, "revision", 0),
                                    ["status"] = NormalizeLookup(ReadString(
                                            receipt, "status", "failed"))
                                        .Replace(' ', '_'),
                                    ["observed"] = ReadInt(receipt,
                                        "observedRelation", 0),
                                    ["error"] = LimitText(ReadString(receipt,
                                        "error", "Native application failed."), 800)
                                })
                            .Where(row => !string.IsNullOrWhiteSpace(
                                ReadString(row, "pair", "")))
                            .ToList();
                        string normalizedJson = Json.Serialize(normalized);
                        HashSet<string> currentKeys = new HashSet<string>(
                            QuerySql(connection, @"
SELECT target.pair_key,target.revision
FROM relationship_native_targets AS target
JOIN jsonb_to_recordset(CAST($rows AS jsonb))
 AS x(pair text,revision integer)
ON target.pair_key=x.pair AND target.revision=x.revision;",
                                new Dictionary<string, object>
                                {
                                    ["rows"] = normalizedJson
                                }).Select(row => ReadString(row, "pair_key", "")
                                    + "\n" + ReadInt(row, "revision", 0)),
                            StringComparer.Ordinal);
                        List<Dictionary<string, object>> acceptedRows =
                            new List<Dictionary<string, object>>();
                        List<Dictionary<string, object>> obsoleteRows =
                            new List<Dictionary<string, object>>();
                        List<Dictionary<string, object>> failedRows =
                            new List<Dictionary<string, object>>();
                        foreach (Dictionary<string, object> row in normalized)
                        {
                            string key = ReadString(row, "pair", "") + "\n"
                                + ReadInt(row, "revision", 0);
                            if (!currentKeys.Contains(key))
                            {
                                stale++;
                                continue;
                            }
                            string status = ReadString(row, "status", "failed");
                            if (status == "applied"
                                || status == "already_aligned")
                                acceptedRows.Add(row);
                            else if (status == "obsolete")
                                obsoleteRows.Add(row);
                            else
                                failedRows.Add(row);
                        }
                        if (acceptedRows.Count > 0)
                        {
                            string rowsJson = Json.Serialize(acceptedRows);
                            ExecutePostgreSqlJsonCommand(connection, @"
UPDATE relationship_pair_chemistry AS pair SET
native_action_pending=0,native_action_id='',
last_native_sync_day=" + worldDay.ToString(
                                System.Globalization.CultureInfo.InvariantCulture)
                                + ",updated_ts=" + ts + @"
FROM jsonb_to_recordset(@rows)
 AS x(pair text,revision integer,status text,observed integer,error text)
WHERE pair.pair_key=x.pair
AND pair.projected_native_relation=x.observed;", rowsJson);
                            ExecutePostgreSqlJsonCommand(connection, @"
DELETE FROM relationship_native_targets AS target
USING jsonb_to_recordset(@rows)
 AS x(pair text,revision integer,status text,observed integer,error text)
WHERE target.pair_key=x.pair
AND target.revision=x.revision;", rowsJson);
                            accepted += acceptedRows.Count;
                        }
                        if (obsoleteRows.Count > 0)
                        {
                            string rowsJson = Json.Serialize(obsoleteRows);
                            ExecutePostgreSqlJsonCommand(connection, @"
UPDATE relationship_pair_chemistry AS pair SET
native_action_pending=0,native_action_id='',updated_ts=" + ts + @"
FROM jsonb_to_recordset(@rows)
 AS x(pair text,revision integer,status text,observed integer,error text)
WHERE pair.pair_key=x.pair;", rowsJson);
                            ExecutePostgreSqlJsonCommand(connection, @"
DELETE FROM relationship_native_targets AS target
USING jsonb_to_recordset(@rows)
 AS x(pair text,revision integer,status text,observed integer,error text)
WHERE target.pair_key=x.pair
AND target.revision=x.revision;", rowsJson);
                            accepted += obsoleteRows.Count;
                        }
                        if (failedRows.Count > 0)
                        {
                            ExecutePostgreSqlJsonCommand(connection, @"
UPDATE relationship_native_targets AS target SET
status='pending',observed_relation=x.observed,
attempt_count=target.attempt_count+1,claimed_ts=0,
last_error=x.error,updated_ts=" + ts + @"
FROM jsonb_to_recordset(@rows)
 AS x(pair text,revision integer,status text,observed integer,error text)
WHERE target.pair_key=x.pair
AND target.revision=x.revision;",
                                Json.Serialize(failedRows));
                            failed += failedRows.Count;
                        }
                    }
                    else
                    {
                        ApplySqliteRelationshipNativeReceipts(connection,
                            receipts, worldDay, ts,
                            ref accepted, ref stale, ref failed);
                    }
                    ExecuteSql(connection, "COMMIT;");
                }
                catch
                {
                    try { ExecuteSql(connection, "ROLLBACK;"); } catch { }
                    throw;
                }
            }
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["acceptedCount"] = accepted,
                ["staleCount"] = stale, ["failedCount"] = failed
            };
        }

        private static void ApplySqliteRelationshipNativeReceipts(
            ReignDbConnection connection,
            List<Dictionary<string, object>> receipts,
            double worldDay,
            long ts,
            ref int accepted,
            ref int stale,
            ref int failed)
        {
            foreach (Dictionary<string, object> receipt in receipts)
            {
                string pairKey = ReadString(receipt, "pairKey", "");
                int revision = ReadInt(receipt, "revision", 0);
                // NormalizeLookup turns separators into spaces. Convert the
                // result back to the protocol's underscore form before
                // comparing it, otherwise "already_aligned" is treated as
                // a failure and recycled forever.
                string status = NormalizeLookup(ReadString(
                        receipt, "status", "failed"))
                    .Replace(' ', '_');
                Dictionary<string, object> current = QuerySql(connection,
                    "SELECT * FROM relationship_native_targets WHERE pair_key=$pair LIMIT 1;",
                    new Dictionary<string, object>
                    {
                        ["pair"] = pairKey
                    }).FirstOrDefault();
                if (current == null
                    || revision != ReadInt(current, "revision", 1))
                {
                    stale++;
                    continue;
                }
                int observed = ReadInt(receipt, "observedRelation",
                    ReadInt(current, "observed_relation", 0));
                if (status == "applied" || status == "already_aligned")
                {
                    ExecuteSql(connection,
                        "DELETE FROM relationship_native_targets WHERE pair_key=$pair AND revision=$revision;",
                        new Dictionary<string, object>
                        {
                            ["pair"] = pairKey,
                            ["revision"] = revision
                        });
                    ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
native_action_pending=0,native_action_id='',last_native_sync_day=$day,updated_ts=$ts
WHERE pair_key=$pair AND projected_native_relation=$observed;",
                        new Dictionary<string, object>
                        {
                            ["day"] = worldDay,
                            ["ts"] = ts,
                            ["pair"] = pairKey,
                            ["observed"] = observed
                        });
                    accepted++;
                }
                else if (status == "obsolete")
                {
                    ExecuteSql(connection,
                        "DELETE FROM relationship_native_targets WHERE pair_key=$pair AND revision=$revision;",
                        new Dictionary<string, object>
                        {
                            ["pair"] = pairKey,
                            ["revision"] = revision
                        });
                    ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
native_action_pending=0,native_action_id='',updated_ts=$ts
WHERE pair_key=$pair;",
                        new Dictionary<string, object>
                        {
                            ["ts"] = ts,
                            ["pair"] = pairKey
                        });
                    accepted++;
                }
                else
                {
                    ExecuteSql(connection, @"UPDATE relationship_native_targets SET
status='pending',observed_relation=$observed,attempt_count=attempt_count+1,
claimed_ts=0,last_error=$error,updated_ts=$ts
WHERE pair_key=$pair AND revision=$revision;",
                        new Dictionary<string, object>
                        {
                            ["observed"] = observed,
                            ["error"] = LimitText(ReadString(receipt,
                                "error", "Native application failed."), 800),
                            ["ts"] = ts,
                            ["pair"] = pairKey,
                            ["revision"] = revision
                        });
                    failed++;
                }
            }
        }
    }
}
