using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationService
    {
        // This view intentionally reads the authoritative gate counters on every
        // observation. Historical charts and the 30-second overview cache are not
        // readiness evidence. Missing schema/data fails rather than becoming zero.
        internal static Dictionary<string, object> BuildWorldTestReadiness(string campaignId, string timelineId)
        {
            var timer = Stopwatch.StartNew();
            var metadata = CampaignFileService.ReadJsonObject(CampaignFileService.CampaignFile(campaignId, "campaign.json"));
            string player = ReadFirstString(metadata, "mainHeroStringId", "mainHeroId");
            // Read other stores before retaining any PostgreSQL table locks.
            var events = ReadWorldTestDiplomacyEvents(campaignId);
            var actions = ReadWorldTestActions(campaignId);
            string generation = RelationshipsService.RelationshipCampaignGeneration(campaignId);
            using (var connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                // PostgreSQL aborts this explicit transaction on a statement
                // error. Retrying that statement would mask it with 25P02.
                // The connection-scoped synchronous lease also covers nested reads.
                using var noStatementRetry = new ReignDatabase.SqlStatementRetrySuppression(connection);
                try
                {
                // A status read must never queue CREATE INDEX behind an active
                // data writer. Normal heartbeat/worker initialization owns schema
                // migration; an old/missing shape fails this observation closed.
                using var transaction = connection.BeginTransaction();
                WorldSimulationStore.Set(connection);
                WorldSimulationStore.Set2(connection);
                WorldSimulationStore.Set3(connection);
                var parameters = new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["player"] = player ?? "" };
                Func<string, double> day = sql => ReadDouble(WorldSimulationStore.QuerySql(connection, sql, parameters).FirstOrDefault(), "day", -1d);
                Func<string, long> count = sql => ReadLong(WorldSimulationStore.QuerySql(connection, sql, parameters).FirstOrDefault(), "count", -1);
                double latest = Math.Max(day(WorldSimulationStore.SelectWorldTestNativeHeartbeatsSql),
                    LatestKnownWorldDay(connection, campaignId, timelineId));
                parameters["latest"] = Math.Max(0, latest);
                const string nativeFilter = " AND ($player='' OR (hero_a_id<>$player AND hero_b_id<>$player))";
                long pending = count(WorldSimulationStore.CountRelationshipNativeTargetsSql(nativeFilter));
                pending += RelationshipsService.CountUnrepresentedNativeFlags(connection, player);
                long failed = count(WorldSimulationStore.CountRelationshipNativeTargetsSql2(nativeFilter));
                var batch = WorldSimulationStore.SelectRelationshipNativeSyncBatches2(connection, parameters).FirstOrDefault();
                failed = Math.Max(failed, ReadInt(batch, "client_failed", 0));
                var result = new Dictionary<string, object>
                {
                    ["ok"] = true, ["schema"] = "reign_world_readiness_v1",
                    ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                    ["campaignGeneration"] = generation,
                    ["observationId"] = Guid.NewGuid().ToString("N"),
                    ["observedUtc"] = DateTime.UtcNow.ToString("O"),
                    ["latestObservedDay"] = Math.Max(0, latest),
                    ["clocks"] = new Dictionary<string, object>
                    {
                        ["gameDay"] = latest,
                        ["ingestedDay"] = day(WorldSimulationStore.SelectRelationshipDailyInputsSql),
                        ["completedRelationshipDay"] = RelationshipsService.RelationshipLastProcessedDay(connection, timelineId),
                        ["rollupDay"] = day(WorldSimulationStore.SelectWorldTestDailyRollupsSql),
                        ["nativeConfirmedDay"] = day(WorldSimulationStore.SelectRelationshipPairChemistrySql)
                    },
                    ["relationships"] = new Dictionary<string, object>
                    {
                        ["nativeSync"] = new Dictionary<string, object>
                        {
                            ["pending"] = pending, ["failed"] = failed, ["chemistryPending"] = pending,
                            ["overdue"] = count(WorldSimulationStore.CountRelationshipNativeTargetsSql3(nativeFilter))
                        }
                    },
                    ["politicalPressures"] = new Dictionary<string, object>
                    {
                        ["nativeEffectsPending"] = count(WorldSimulationStore.CountPoliticalPressureNativeEffectsSql),
                        ["nativeEffectsFailed"] = count(WorldSimulationStore.CountPoliticalPressureNativeEffectsSql2)
                    },
                    ["clanConflicts"] = new Dictionary<string, object>
                    {
                        ["relationshipEffectsPending"] = count(WorldSimulationStore.CountClanConflictRelationshipEffectsSql),
                        ["relationshipEffectsFailed"] = count(WorldSimulationStore.CountClanConflictRelationshipEffectsSql2),
                        ["incidentFailures"] = count(WorldSimulationStore.CountClanConflictIncidentsSql),
                        ["pendingNotices"] = count(WorldSimulationStore.CountClanConflictIncidentsSql2)
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
                catch(Exception ex) when(IsStalePostgreSqlPreparedPlan(ex))
                {
                    // The transaction above has rolled back before this catch.
                    // Fail this observation, but let a later GET prepare fresh plans.
                    try { ResetPostgreSqlPreparedPlans(connection); } catch { }
                    throw;
                }
            }
        }
    }
}
