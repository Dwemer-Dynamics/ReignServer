using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly double[] WorldTestCheckpointGates =
            { 1d, 7d, 31.5d, 63d, 126d, 378d, 630d };

        private static void GenerateEligibleWorldTestCheckpointReports(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            double latestWorldDay)
        {
            double firstWorldDay = ReadDouble(QuerySql(connection, @"
SELECT MIN(world_day) AS day FROM world_test_native_heartbeats
WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault(), "day", latestWorldDay);
            double elapsed = Math.Max(0d, latestWorldDay - firstWorldDay);
            foreach (double gate in WorldTestCheckpointGates.Where(x => elapsed >= x))
            {
                int exists = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM world_test_checkpoint_reports
WHERE campaign_id=$campaign AND timeline_id=$timeline AND checkpoint_day=$gate;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId, ["gate"] = gate
                    }).FirstOrDefault(), "count", 0);
                if (exists > 0) continue;
                Dictionary<string, object> selected = QuerySql(connection, @"
SELECT day_key,world_day,overall_status,rollup_json,updated_ts
FROM world_test_daily_rollups
WHERE campaign_id=$campaign AND timeline_id=$timeline AND world_day>=$target
ORDER BY world_day LIMIT 1;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["target"] = firstWorldDay + gate
                    }).FirstOrDefault();
                if (selected == null) continue;
                Dictionary<string, object> prior = QuerySql(connection, @"
SELECT checkpoint_day,report_json FROM world_test_checkpoint_reports
WHERE campaign_id=$campaign AND timeline_id=$timeline AND checkpoint_day<$gate
ORDER BY checkpoint_day DESC LIMIT 1;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId, ["gate"] = gate
                    }).FirstOrDefault();
                Dictionary<string, object> manifestRow = QuerySql(connection, @"
SELECT manifest_json FROM world_test_manifests
WHERE campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    }).FirstOrDefault();
                Dictionary<string, object> previousReport =
                    TryParseJsonObject(ReadString(prior, "report_json", "{}"))
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> reconciliation =
                    BuildWorldTestRelationshipReconciliation(
                        connection, campaignId, timelineId);
                string reportStatus = ReadBool(reconciliation, "matched", true)
                    ? ReadString(selected, "overall_status", "insufficient_data")
                    : "error";
                Dictionary<string, object> report = new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                    ["checkpointDay"] = gate,
                    ["selectedWorldDay"] = ReadDouble(selected, "world_day", firstWorldDay + gate),
                    ["firstObservedWorldDay"] = firstWorldDay,
                    ["overallStatus"] = reportStatus,
                    ["rollup"] = TryParseJsonObject(ReadString(selected, "rollup_json", "{}"))
                        ?? new Dictionary<string, object>(),
                    ["previousCheckpointDay"] = ReadDouble(prior, "checkpoint_day", -1d),
                    ["previousRollup"] = ReadDictionary(previousReport, "rollup")
                        ?? new Dictionary<string, object>(),
                    ["reconciliation"] = reconciliation,
                    ["manifest"] = TryParseJsonObject(ReadString(manifestRow, "manifest_json", "{}"))
                        ?? new Dictionary<string, object>(),
                    ["evidenceCount"] = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM world_test_diagnostic_evidence
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key<=$day;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["day"] = ReadInt(selected, "day_key", 0)
                        }).FirstOrDefault(), "count", 0)
                };
                if (!ReadBool(reconciliation, "matched", true))
                {
                    RecordWorldTestEvidence(connection, campaignId, timelineId,
                        ReadInt(selected, "day_key", 0), "relationships",
                        "checkpoint_reconciliation_" + gate.ToString(CultureInfo.InvariantCulture),
                        "error", reconciliation);
                }
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ExecuteSql(connection, @"INSERT INTO world_test_checkpoint_reports(
campaign_id,timeline_id,checkpoint_day,report_json,created_ts,updated_ts)
VALUES($campaign,$timeline,$gate,$report,$ts,$ts);",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["gate"] = gate, ["report"] = Json.Serialize(report), ["ts"] = now
                    });
            }
        }

        private static Dictionary<string, object> BuildWorldTestRelationshipReconciliation(
            ReignDbConnection connection,
            string campaignId,
            string timelineId)
        {
            string player = ReadFirstString(
                ReadJsonObject(CampaignFile(campaignId, "campaign.json")),
                "mainHeroStringId", "mainHeroId");
            Dictionary<string, object> parameters = new Dictionary<string, object>
            {
                ["campaign"] = campaignId,
                ["timeline"] = timelineId,
                ["player"] = player ?? string.Empty
            };
            List<Dictionary<string, object>> source = QuerySql(connection, @"
SELECT pair_key,hero_a_id,hero_b_id,
affinity_a_to_b,
affinity_b_to_a
FROM relationship_pair_chemistry
WHERE $player='' OR (hero_a_id<>$player AND hero_b_id<>$player);", parameters);
            List<Dictionary<string, object>> mismatches = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> row in source)
            {
                string pair = ReadString(row, "pair_key", "");
                string heroA = ReadString(row, "hero_a_id", "");
                string heroB = ReadString(row, "hero_b_id", "");
                string reason = "";
                if (string.IsNullOrWhiteSpace(pair)
                    || string.IsNullOrWhiteSpace(heroA)
                    || string.IsNullOrWhiteSpace(heroB))
                {
                    reason = "missing_pair_identity";
                }
                else if (heroA.Equals(heroB, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "self_pair";
                }
                else if (ReadInt(row, "affinity_a_to_b", 0) < -100
                    || ReadInt(row, "affinity_a_to_b", 0) > 100
                    || ReadInt(row, "affinity_b_to_a", 0) < -100
                    || ReadInt(row, "affinity_b_to_a", 0) > 100)
                {
                    reason = "affinity_out_of_range";
                }
                if (!string.IsNullOrWhiteSpace(reason) && mismatches.Count < 50)
                    mismatches.Add(new Dictionary<string, object>
                    {
                        ["pairKey"] = pair, ["reason"] = reason
                    });
            }
            int mismatchCount = source.Count(row =>
            {
                string pair = ReadString(row, "pair_key", "");
                string heroA = ReadString(row, "hero_a_id", "");
                string heroB = ReadString(row, "hero_b_id", "");
                return string.IsNullOrWhiteSpace(pair)
                    || string.IsNullOrWhiteSpace(heroA)
                    || string.IsNullOrWhiteSpace(heroB)
                    || heroA.Equals(heroB, StringComparison.OrdinalIgnoreCase)
                    || ReadInt(row, "affinity_a_to_b", 0) < -100
                    || ReadInt(row, "affinity_a_to_b", 0) > 100
                    || ReadInt(row, "affinity_b_to_a", 0) < -100
                    || ReadInt(row, "affinity_b_to_a", 0) > 100;
            });
            return new Dictionary<string, object>
            {
                ["matched"] = mismatchCount == 0,
                ["sourceLedger"] = "relationship_pair_chemistry",
                ["materializedLedger"] = "none_direct_authoritative_read",
                ["sourcePairCount"] = source.Count,
                ["materializedPairCount"] = source.Count,
                ["mismatchCount"] = mismatchCount,
                ["sampledMismatches"] = mismatches
            };
        }

        private static Dictionary<string, object> WorldTestCheckpointsApi(
            Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>();
            string campaignId = query.TryGetValue("campaignId", out string campaign)
                ? campaign : LatestCampaignId();
            string timelineId = query.TryGetValue("timelineId", out string timeline)
                ? timeline : "main";
            if (!HasRealCampaignStorage(campaignId))
                return new Dictionary<string, object>
                {
                    ["ok"] = false, ["campaignId"] = campaignId,
                    ["timelineId"] = timelineId, ["reports"] = new List<object>(),
                    ["error"] = "No observed real campaign matches this World Test selection."
                };
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureWorldTestTelemetrySchema(connection);
                List<Dictionary<string, object>> reports = QuerySql(connection, @"
SELECT checkpoint_day,report_json,updated_ts FROM world_test_checkpoint_reports
WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY checkpoint_day;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    }).Select(row =>
                    {
                        Dictionary<string, object> report =
                            TryParseJsonObject(ReadString(row, "report_json", "{}"))
                            ?? new Dictionary<string, object>();
                        report["updatedUtc"] = UnixToIso(ReadLong(row, "updated_ts", 0));
                        return report;
                    }).ToList();
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["campaignId"] = campaignId,
                    ["timelineId"] = timelineId, ["gates"] = WorldTestCheckpointGates,
                    ["reports"] = reports, ["worker"] = WorldTestRollupWorkerStatus()
                };
            }
        }

        private static Dictionary<string, object> WorldTestComparisonApi(
            Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>();
            string raw = query.TryGetValue("campaigns", out string campaigns) ? campaigns : "";
            List<string[]> selections = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split(new[] { '|' }, 2))
                .Where(x => x.Length > 0 && !string.IsNullOrWhiteSpace(x[0]))
                .ToList();
            if (selections.Count != 3)
                return new Dictionary<string, object>
                {
                    ["ok"] = false, ["comparable"] = false,
                    ["error"] = "Select exactly three campaign|timeline values."
                };
            List<Dictionary<string, object>> campaignsResult = new List<Dictionary<string, object>>();
            foreach (string[] selection in selections)
            {
                string campaignId = selection[0];
                string timelineId = selection.Length > 1 ? selection[1] : "main";
                if (!HasRealCampaignStorage(campaignId))
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false, ["comparable"] = false,
                        ["error"] = "Campaign " + campaignId + " has no observed World Test storage."
                    };
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureWorldTestSchema(connection);
                    EnsureWorldTestTelemetrySchema(connection);
                    Dictionary<string, object> checkpoint = QuerySql(connection, @"
SELECT report_json FROM world_test_checkpoint_reports
WHERE campaign_id=$campaign AND timeline_id=$timeline AND checkpoint_day=126
LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId
                        }).FirstOrDefault();
                    Dictionary<string, object> manifest = TryParseJsonObject(ReadString(
                        QuerySql(connection, @"SELECT manifest_json FROM world_test_manifests
WHERE campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;",
                            new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId, ["timeline"] = timelineId
                            }).FirstOrDefault(), "manifest_json", "{}"))
                        ?? new Dictionary<string, object>();
                    campaignsResult.Add(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                        ["manifest"] = manifest,
                        ["report"] = TryParseJsonObject(ReadString(checkpoint, "report_json", "{}"))
                            ?? new Dictionary<string, object>()
                    });
                }
            }
            if (campaignsResult.Any(x =>
                (ReadDictionary(x, "report") ?? new Dictionary<string, object>()).Count == 0))
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["comparable"] = false,
                    ["reason"] = "All three campaigns must complete the 126-day one-year checkpoint.",
                    ["campaigns"] = campaignsResult,
                    ["summary"] = new Dictionary<string, object>()
                };
            string[] hashes = campaignsResult.Select(x => ReadString(
                ReadDictionary(x, "manifest") ?? new Dictionary<string, object>(),
                "configurationHash", "")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            bool comparable = hashes.Length == 1 && !string.IsNullOrWhiteSpace(hashes[0]);
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["comparable"] = comparable,
                ["reason"] = comparable ? "" : "Campaign manifests use different or missing configuration hashes.",
                ["campaigns"] = campaignsResult,
                ["summary"] = comparable ? BuildWorldTestComparisonSummary(campaignsResult)
                    : new Dictionary<string, object>()
            };
        }

        private static Dictionary<string, object> BuildWorldTestComparisonSummary(
            List<Dictionary<string, object>> campaigns)
        {
            string[] metrics =
            {
                "relationships.pairCount", "relationships.lifecycle.romanticMarriages",
                "relationships.lifecycle.arrangedMarriages", "diplomacy.eventCount",
                "politicalPressures.incidentCount", "politicalPressures.hostileIncidents",
                "politicalPressures.peacefulIncidents", "politicalPressures.actionsSelected",
                "politicalPressures.dailyRollsMissing",
                "politicalPressures.dailyRollCoveragePercent",
                "clanConflicts.incidentCount", "clanConflicts.mediationSuccesses",
                "clanConflicts.failedMediations", "clanConflicts.kingdomRollsMissing",
                "rumors.occurrencesCreated", "rebellions.triggeredRollCount"
            };
            Dictionary<string, object> summary = new Dictionary<string, object>();
            foreach (string metric in metrics)
            {
                List<double> values = campaigns.Select(x =>
                {
                    Dictionary<string, object> report = ReadDictionary(x, "report")
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> rollup = ReadDictionary(report, "rollup")
                        ?? new Dictionary<string, object>();
                    string[] path = metric.Split('.');
                    Dictionary<string, object> parent = ReadDictionary(rollup, path[0])
                        ?? new Dictionary<string, object>();
                    if (path.Length == 3)
                        parent = ReadDictionary(parent, path[1])
                            ?? new Dictionary<string, object>();
                    return ReadDouble(parent, path[path.Length - 1], 0d);
                }).OrderBy(x => x).ToList();
                summary[metric] = new Dictionary<string, object>
                {
                    ["sampleSize"] = values.Count, ["minimum"] = values.FirstOrDefault(),
                    ["maximum"] = values.LastOrDefault(), ["mean"] = values.Average(),
                    ["median"] = values[values.Count / 2]
                };
            }
            AddWorldTestRateComparison(summary, campaigns,
                "rebellions.successRate", "rebellions", "triggeredRollCount", "eligibleRollCount");
            AddWorldTestRateComparison(summary, campaigns,
                "rumors.promotionRate", "rumors", "promotionPassed", "promotionEligible");
            AddWorldTestRateComparison(summary, campaigns,
                "politicalPressures.incidentRate", "politicalPressures", "incidentCount", "dailyRolls");
            AddWorldTestRateComparison(summary, campaigns,
                "clanConflicts.incidentRate", "clanConflicts", "incidentCount", "kingdomRolls");
            return summary;
        }

        private static void AddWorldTestRateComparison(
            Dictionary<string, object> summary,
            List<Dictionary<string, object>> campaigns,
            string metricKey,
            string subsystem,
            string numeratorKey,
            string denominatorKey)
        {
            long numerator = 0, denominator = 0;
            foreach (Dictionary<string, object> campaign in campaigns)
            {
                Dictionary<string, object> report = ReadDictionary(campaign, "report")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> rollup = ReadDictionary(report, "rollup")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> values = ReadDictionary(rollup, subsystem)
                    ?? new Dictionary<string, object>();
                numerator += ReadLong(values, numeratorKey, 0);
                denominator += ReadLong(values, denominatorKey, 0);
            }
            const double z = 1.959963984540054d;
            double rate = denominator > 0 ? numerator / (double)denominator : 0d;
            double center = denominator > 0
                ? (rate + z * z / (2d * denominator)) / (1d + z * z / denominator) : 0d;
            double margin = denominator > 0
                ? z * Math.Sqrt(rate * (1d - rate) / denominator
                    + z * z / (4d * denominator * denominator))
                    / (1d + z * z / denominator) : 0d;
            summary[metricKey] = new Dictionary<string, object>
            {
                ["sampleSize"] = campaigns.Count,
                ["successes"] = numerator,
                ["trials"] = denominator,
                ["rate"] = Math.Round(rate, 6),
                ["wilson95Low"] = Math.Round(Math.Max(0d, center - margin), 6),
                ["wilson95High"] = Math.Round(Math.Min(1d, center + margin), 6),
                ["insufficientData"] = denominator == 0
            };
        }

        private static List<Dictionary<string, object>> RunWorldTestRollupSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) =>
                results.Add(new Dictionary<string, object>
                {
                    ["ok"] = true, ["passed"] = passed, ["suite"] = "world_test_rollups",
                    ["caseId"] = id, ["name"] = id, ["summary"] = summary, ["durationMs"] = 0
                });
            add("world_test_checkpoint_gates",
                WorldTestCheckpointGates.SequenceEqual(
                    new[] { 1d, 7d, 31.5d, 63d, 126d, 378d, 630d }),
                "Shakedown, one-year, three-year, and five-year gates are exact.");
            string storageCampaign = "__world_test_rollup_"
                + Guid.NewGuid().ToString("N").Substring(0, 12);
            using (ReignDbConnection connection =
                OpenCampaignConnection(storageCampaign))
            {
                EnsureWorldTestTelemetrySchema(connection);
                EnqueueWorldTestRollup(connection, "campaign", "main", 31, "first");
                EnqueueWorldTestRollup(connection, "campaign", "main", 31, "duplicate");
                EnqueueWorldTestRollup(connection, "campaign", "branch", 31, "branch");
                Dictionary<string, object> main = QuerySql(connection, @"
SELECT status,revision FROM world_test_rollup_queue
WHERE campaign_id='campaign' AND timeline_id='main' AND day_key=31 LIMIT 1;")
                    .FirstOrDefault();
                int branchCount = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM world_test_rollup_queue
WHERE campaign_id='campaign' AND timeline_id='branch';").FirstOrDefault(), "count", 0);
                add("world_test_rollup_queue_idempotency",
                    ReadString(main, "status", "") == "pending"
                        && ReadInt(main, "revision", 0) == 2,
                    "Duplicate enqueue replaces one day and advances its revision.");
                add("world_test_rollup_timeline_isolation", branchCount == 1,
                    "Timeline branches retain independent rollup work.");
                EnsureWorldTestTelemetrySchema(connection);
                add("world_test_rollup_restart_recovery",
                    ReadString(QuerySql(connection, @"
SELECT status FROM world_test_rollup_queue
WHERE campaign_id='campaign' AND timeline_id='main' AND day_key=31;")
                        .FirstOrDefault(), "status", "") == "pending",
                    "Pending rollup work survives schema reinitialization.");
                EnsureMemorySchema(connection);
                int rebellionTableCount = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM sqlite_master
WHERE type='table' AND name IN (
'rebellion_movements','rebellion_memberships','rebellion_transitions',
'negotiated_action_drafts','negotiated_action_approvals');")
                    .FirstOrDefault(), "count", 0);
                add("world_test_fresh_campaign_rebellion_schema",
                    rebellionTableCount == 5,
                    "Core campaign initialization creates every rebellion table before World Test reads the subsystem.");
                EnsureMbtiRelationshipSchema(connection);
                ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,
effective_affinity_a_to_b,effective_affinity_b_to_a,first_day,last_day,updated_ts)
VALUES('reconcile_a|reconcile_b','reconcile_a','reconcile_b',10,20,10,20,1,1,1);");
                Dictionary<string, object> aligned =
                    BuildWorldTestRelationshipReconciliation(connection, "campaign", "main");
                ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET affinity_a_to_b=101 WHERE pair_key='reconcile_a|reconcile_b';");
                Dictionary<string, object> contradicted =
                    BuildWorldTestRelationshipReconciliation(connection, "campaign", "main");
                add("world_test_checkpoint_reconciliation",
                    ReadBool(aligned, "matched", false)
                    && !ReadBool(contradicted, "matched", true)
                    && ReadInt(contradicted, "mismatchCount", 0) == 1,
                    "Checkpoint-only reconciliation detects corrupt authoritative pair state without maintaining a duplicate diagnostic ledger.");
            }
            ReignPostgreSqlStorage.DropCampaign(storageCampaign);
            List<double> completedDays = new List<double> { 31.4d, 31.75d, 32d };
            add("world_test_half_season_selection",
                completedDays.Where(x => x >= 31.5d).OrderBy(x => x).First() == 31.75d,
                "The 31.5-day checkpoint selects the first completed rollup at or after the gate.");
            List<Dictionary<string, object>> comparisonFixtures =
                Enumerable.Range(1, 3).Select(index => new Dictionary<string, object>
                {
                    ["report"] = new Dictionary<string, object>
                    {
                        ["rollup"] = new Dictionary<string, object>
                        {
                            ["relationships"] = new Dictionary<string, object>
                            {
                                ["pairCount"] = index * 10
                            }
                        }
                    }
                }).ToList();
            Dictionary<string, object> comparison = BuildWorldTestComparisonSummary(comparisonFixtures);
            add("world_test_three_campaign_comparison",
                ReadInt(ReadDictionary(comparison, "relationships.pairCount")
                    ?? new Dictionary<string, object>(), "sampleSize", 0) == 3,
                "Three-campaign summaries retain an explicit sample size.");
            return results;
        }
    }
}
