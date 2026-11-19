using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        private static bool RelationshipNativeObservationRequired(
            Dictionary<string, object> pair, Dictionary<string, object> target)
        {
            return ReadInt(target, "requires_observation", 0) == 1
                || target == null && ReadInt(pair, "native_action_pending", 0) == 1;
        }

        internal static Dictionary<string, object> RelationshipNativeTargetPullApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            const int maxBatchSize = 1024;
            int limit = Clamp(ReadInt(payload, "limit", maxBatchSize), 1, maxBatchSize);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                List<Dictionary<string, object>> rows;
                int prunedAlignedCount;
                RelationshipsStore.Begin(connection);
                try
                {
                    RelationshipsStore.UpdateRelationshipNativeTargets6(connection, new Dictionary<string, object> { ["ts"] = ts });
                    // An old successful receipt can remove its target after the pair
                    // projection has changed. Orphan flags require a real native read;
                    // equality here is deliberately NOT evidence of alignment.
                    RelationshipsStore.InsertOrIgnoreRelationshipNativeTargets(connection, new Dictionary<string, object> { ["ts"] = ts });
                    prunedAlignedCount = ReadInt(RelationshipsStore.CountRelationshipNativeTargets3(connection).FirstOrDefault(), "count", 0);
                    RelationshipsStore.UpdateRelationshipPairChemistry23(connection, new Dictionary<string, object> { ["ts"] = ts });
                    RelationshipsStore.DeleteRelationshipNativeTargets5(connection);
                    rows = RelationshipsStore.SelectRelationshipNativeTargets9(connection, limit,                        new Dictionary<string, object> { ["expired"] = ts - 15 });
                    if (ReignPostgreSqlDialect.IsPostgreSql(connection)
                        && rows.Count > 0)
                    {
                        string claimRows = Json.Serialize(rows.Select(row =>
                            new Dictionary<string, object>
                            {
                                ["pair"] = ReadString(row, "pair_key", ""),
                                ["revision"] = ReadInt(row, "revision", 1)
                            }).ToList());
                        ExecutePostgreSqlJsonCommand(connection, RelationshipsStore.UpdateRelationshipNativeTargetsSql(ts, ts, ts - 15), claimRows);
                    }
                    else
                    {
                        foreach (Dictionary<string, object> row in rows)
                        {
                            RelationshipsStore.UpdateRelationshipNativeTargets7(connection, new Dictionary<string, object>
                                {
                                    ["ts"] = ts,
                                    ["expired"] = ts - 15,
                                    ["pair"] = ReadString(row, "pair_key", ""),
                                    ["revision"] = ReadInt(row, "revision", 1)
                                });
                        }
                    }
                    RelationshipsStore.Commit(connection);
                }
                catch
                {
                    try { RelationshipsStore.Rollback(connection); } catch { }
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

        internal static Dictionary<string, object> RelationshipNativeTargetReceiptsApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            List<Dictionary<string, object>> receipts = ReadDictionaryList(payload, "receipts");
            int accepted = 0, stale = 0, failed = 0;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                RelationshipsStore.Begin(connection);
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
                                    ["status"] = Program.NormalizeLookup(ReadString(
                                            receipt, "status", "failed"))
                                        .Replace(' ', '_'),
                                    ["observed"] = ReadInt(receipt,
                                        "observedRelation", 0),
                                    ["error"] = Program.LimitText(ReadString(receipt,
                                        "error", "Native application failed."), 800)
                                })
                            .Where(row => !string.IsNullOrWhiteSpace(
                                ReadString(row, "pair", "")))
                            .ToList();
                        string normalizedJson = Json.Serialize(normalized);
                        HashSet<string> currentKeys = new HashSet<string>(
                            RelationshipsStore.SelectRelationshipNativeTargets10(connection, new Dictionary<string, object>
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
                            ExecutePostgreSqlJsonCommand(connection, RelationshipsStore.UpdateRelationshipPairChemistrySql(worldDay.ToString(
                                System.Globalization.CultureInfo.InvariantCulture), ts), rowsJson);
                            ExecutePostgreSqlJsonCommand(connection, RelationshipsStore.DeleteRelationshipNativeTargetsSql3, rowsJson);
                            accepted += acceptedRows.Count;
                        }
                        if (obsoleteRows.Count > 0)
                        {
                            string rowsJson = Json.Serialize(obsoleteRows);
                            ExecutePostgreSqlJsonCommand(connection, RelationshipsStore.UpdateRelationshipPairChemistrySql2(ts), rowsJson);
                            ExecutePostgreSqlJsonCommand(connection, RelationshipsStore.DeleteRelationshipNativeTargetsSql3, rowsJson);
                            accepted += obsoleteRows.Count;
                        }
                        if (failedRows.Count > 0)
                        {
                            ExecutePostgreSqlJsonCommand(connection, RelationshipsStore.UpdateRelationshipNativeTargetsSql2(ts),
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
                    RelationshipsStore.Commit(connection);
                }
                catch
                {
                    try { RelationshipsStore.Rollback(connection); } catch { }
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
                string status = Program.NormalizeLookup(ReadString(
                        receipt, "status", "failed"))
                    .Replace(' ', '_');
                Dictionary<string, object> current = RelationshipsStore.SelectRelationshipNativeTargets8(connection, new Dictionary<string, object>
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
                    RelationshipsStore.DeleteRelationshipNativeTargets6(connection, new Dictionary<string, object>
                        {
                            ["pair"] = pairKey,
                            ["revision"] = revision
                        });
                    RelationshipsStore.UpdateRelationshipPairChemistry24(connection, new Dictionary<string, object>
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
                    RelationshipsStore.DeleteRelationshipNativeTargets6(connection, new Dictionary<string, object>
                        {
                            ["pair"] = pairKey,
                            ["revision"] = revision
                        });
                    RelationshipsStore.UpdateRelationshipPairChemistry25(connection, new Dictionary<string, object>
                        {
                            ["ts"] = ts,
                            ["pair"] = pairKey
                        });
                    accepted++;
                }
                else
                {
                    RelationshipsStore.UpdateRelationshipNativeTargets8(connection, new Dictionary<string, object>
                        {
                            ["observed"] = observed,
                            ["error"] = Program.LimitText(ReadString(receipt,
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
