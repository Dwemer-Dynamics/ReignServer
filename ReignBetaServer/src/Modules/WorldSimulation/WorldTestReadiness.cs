using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // This view intentionally reads the authoritative gate counters on every
        // observation. Historical charts and the 30-second overview cache are not
        // readiness evidence. Missing schema/data fails rather than becoming zero.
        private static Dictionary<string, object> BuildWorldTestReadiness(string campaignId, string timelineId)
        {
            var timer = Stopwatch.StartNew();
            var metadata = ReadJsonObject(CampaignFile(campaignId, "campaign.json"));
            string player = ReadFirstString(metadata, "mainHeroStringId", "mainHeroId");
            using (var connection = OpenCampaignConnection(campaignId))
            {
                EnsureWorldTestSchema(connection);
                EnsureMbtiRelationshipSchema(connection);
                EnsurePoliticalPressureSchema(connection);
                EnsureClanConflictSchema(connection);
                var parameters = new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["player"] = player ?? "" };
                Func<string, double> day = sql => ReadDouble(QuerySql(connection, sql, parameters).FirstOrDefault(), "day", -1d);
                Func<string, long> count = sql => ReadLong(QuerySql(connection, sql, parameters).FirstOrDefault(), "count", -1);
                double latest = Math.Max(day("SELECT MAX(world_day) AS day FROM world_test_native_heartbeats WHERE timeline_id=$timeline;"),
                    LatestKnownWorldDay(connection, campaignId, timelineId));
                parameters["latest"] = Math.Max(0, latest);
                const string nativeFilter = " AND ($player='' OR (hero_a_id<>$player AND hero_b_id<>$player))";
                long pending = count("SELECT COUNT(*) AS count FROM relationship_native_targets WHERE status IN ('pending','claimed')" + nativeFilter + ";");
                pending += CountUnrepresentedNativeFlags(connection, player);
                long failed = count("SELECT COUNT(*) AS count FROM relationship_native_targets WHERE status='failed'" + nativeFilter + ";");
                var batch = QuerySql(connection, "SELECT client_failed FROM relationship_native_sync_batches WHERE timeline_id=$timeline ORDER BY world_day DESC,issued_ts DESC LIMIT 1;", parameters).FirstOrDefault();
                failed = Math.Max(failed, ReadInt(batch, "client_failed", 0));
                var events = ReadWorldTestDiplomacyEvents(campaignId);
                var actions = ReadWorldTestActions(campaignId);
                var result = new Dictionary<string, object>
                {
                    ["ok"] = true, ["schema"] = "reign_world_readiness_v1",
                    ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                    ["campaignGeneration"] = RelationshipCampaignGeneration(campaignId),
                    ["observationId"] = Guid.NewGuid().ToString("N"),
                    ["observedUtc"] = DateTime.UtcNow.ToString("O"),
                    ["latestObservedDay"] = Math.Max(0, latest),
                    ["clocks"] = new Dictionary<string, object>
                    {
                        ["gameDay"] = latest,
                        ["ingestedDay"] = day("SELECT MAX(world_day) AS day FROM relationship_daily_inputs WHERE timeline_id=$timeline;"),
                        ["completedRelationshipDay"] = RelationshipLastProcessedDay(connection, timelineId),
                        ["rollupDay"] = day("SELECT MAX(world_day) AS day FROM world_test_daily_rollups WHERE campaign_id=$campaign AND timeline_id=$timeline;"),
                        ["nativeConfirmedDay"] = day("SELECT MAX(last_native_sync_day) AS day FROM relationship_pair_chemistry WHERE last_native_sync_day>=0;")
                    },
                    ["relationships"] = new Dictionary<string, object>
                    {
                        ["nativeSync"] = new Dictionary<string, object>
                        {
                            ["pending"] = pending, ["failed"] = failed, ["chemistryPending"] = pending,
                            ["overdue"] = count("SELECT COUNT(*) AS count FROM relationship_native_targets WHERE status IN ('pending','claimed','failed') AND $latest-world_day>1" + nativeFilter + ";")
                        }
                    },
                    ["politicalPressures"] = new Dictionary<string, object>
                    {
                        ["nativeEffectsPending"] = count("SELECT COUNT(*) AS count FROM political_pressure_native_effects WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='pending';"),
                        ["nativeEffectsFailed"] = count("SELECT COUNT(*) AS count FROM political_pressure_native_effects WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='failed';")
                    },
                    ["clanConflicts"] = new Dictionary<string, object>
                    {
                        ["relationshipEffectsPending"] = count("SELECT COUNT(*) AS count FROM clan_conflict_relationship_effects WHERE campaign_id=$campaign AND timeline_id=$timeline AND status IN ('pending','applying');"),
                        ["relationshipEffectsFailed"] = count("SELECT COUNT(*) AS count FROM clan_conflict_relationship_effects WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='failed';"),
                        ["incidentFailures"] = count("SELECT COUNT(*) AS count FROM clan_conflict_incidents WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='failed';"),
                        ["pendingNotices"] = count("SELECT COUNT(*) AS count FROM clan_conflict_incidents WHERE campaign_id=$campaign AND timeline_id=$timeline AND notice_status IN ('ready','fetched');")
                    },
                    ["diplomacy"] = new Dictionary<string, object>
                    {
                        ["undeliveredAnnouncements"] = events.Count(x => ReadBool(x, "announcementReady", false) && !ReadBool(x, "delivered", false) && !ReadBool(x, "acknowledged", false)),
                        ["unacknowledgedAnnouncements"] = events.Count(x => ReadBool(x, "announcementReady", false) && !ReadBool(x, "acknowledged", false))
                    },
                    ["pipeline"] = new Dictionary<string, object>
                    {
                        ["failedActions"] = actions.Count(x => IsTerminalFailureStatus(ReadString(x, "status", "")))
                    }
                };
                result["readinessQueryMs"] = timer.ElapsedMilliseconds;
                return result;
            }
        }
    }
}
