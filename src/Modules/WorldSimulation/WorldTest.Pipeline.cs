using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationService
    {
        private static Dictionary<string, object> BuildWorldTestPipeline(ReignDbConnection connection, string campaignId, string timelineId, double latestDay, Dictionary<string, object> nativePayload)
        {
            EnsureWorldHistorySchema(connection);
            List<Dictionary<string, object>> actions = ReadWorldTestActions(campaignId);
            int failed = actions.Count(x => IsTerminalFailureStatus(ReadString(x, "status", "")));
            int pending = actions.Count(x => IsPendingStatus(ReadString(x, "status", "")));
            int overdue = actions.Count(x => WorldTestActionOverdue(x, latestDay));
            int unknownWeddingSchedules = actions.Count(x => ReadString(x, "status", "") == "wedding_pending"
                && WorldTestWeddingEndDay(x) < 0);
            Dictionary<string, object> saveSync = ReadDictionary(nativePayload, "saveSync") ?? new Dictionary<string, object>();
            bool alignmentPending = ReadBool(saveSync, "alignmentPending", false);
            double relationshipDay = RelationshipsService.RelationshipLastProcessedDay(connection, timelineId);
            double relationshipLag = relationshipDay < 0d ? -1d : Math.Max(0d, latestDay - relationshipDay);
            int relationshipInputBacklog = ReadInt(WorldSimulationStore.CountRelationshipDailyInputs(connection, new Dictionary<string, object> { ["timeline"] = timelineId }).FirstOrDefault(), "count", 0);
            double latestRelationshipIngestedDay = ReadDouble(WorldSimulationStore.SelectRelationshipDailyInputs(connection, new Dictionary<string, object> { ["timeline"] = timelineId })
                .FirstOrDefault(), "day", -1d);
            Dictionary<string, object> partialRelationshipDay = WorldSimulationStore.SelectRelationshipDailyInputs2(connection, new Dictionary<string, object> { ["timeline"] = timelineId })
                .FirstOrDefault() ?? new Dictionary<string, object>();
            string relationshipMbtiError = ReadString(WorldSimulationStore.SelectSchemaMeta5(connection)
                .FirstOrDefault(), "value", "");
            Dictionary<string, object> relationshipShardCounts = WorldSimulationStore.SelectRelationshipPairChemistry2(connection, new Dictionary<string, object>
                {
                    ["maxShard"] = RelationshipsService.RelationshipCadenceShardCount - 1
                }).ToDictionary(
                    row => ReadInt(row, "processing_shard", -1)
                        .ToString(CultureInfo.InvariantCulture),
                    row => (object)ReadInt(row, "count", 0),
                    StringComparer.OrdinalIgnoreCase);
            double oldestNativeTarget = ReadDouble(WorldSimulationStore.SelectRelationshipNativeTargets(connection).FirstOrDefault(), "day", -1d);
            double nativeProjectionLag = oldestNativeTarget < 0d ? 0d : Math.Max(0d, latestDay - oldestNativeTarget);
            Dictionary<string, object> storage =
                WorldHistoryStorageDiagnostics(connection, campaignId, timelineId, latestDay);
            Dictionary<string, object> matching = WorldSimulationStore.SelectWorldTestChunkCounters2(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> matchingCounters =
                TryParseJsonObject(ReadString(matching, "counters_json", "{}"))
                ?? new Dictionary<string, object>();
            Dictionary<string, object> initializationSocial =
                Program.InitializationReadinessStatusApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                    ["resumeWorkers"] = false
                });
            int compactionBacklog = ReadInt(storage, "compactionBacklog", 0);
            string status = failed > 0 || !string.IsNullOrWhiteSpace(relationshipMbtiError)
                || ReadBool(nativePayload, "timelineRegression", false) ? "error"
                : overdue > 0 || unknownWeddingSchedules > 0 || alignmentPending || relationshipLag > 1d
                    || nativeProjectionLag > 1d || relationshipInputBacklog > 1
                    || compactionBacklog > WorldHistoryCompactionBatchSize ? "warning"
                : nativePayload.Count == 0 ? "insufficient_data" : "healthy";
            List<string> anomalies = new List<string>();
            if (failed > 0) anomalies.Add(failed + " world actions have terminal failures.");
            if (overdue > 0) anomalies.Add(overdue + " world actions are past their processing or scheduled completion window.");
            if (unknownWeddingSchedules > 0) anomalies.Add(unknownWeddingSchedules + " pending weddings have no reported ceremony window; timing cannot be evaluated.");
            if (alignmentPending) anomalies.Add("Save Sync alignment is pending.");
            if (relationshipLag > 1d) anomalies.Add("Relationship processing is "
                + relationshipLag.ToString("0.##", CultureInfo.InvariantCulture) + " campaign days behind.");
            if (relationshipInputBacklog > 1) anomalies.Add(relationshipInputBacklog
                + " durable relationship-day inputs are waiting for processing.");
            if (nativeProjectionLag > 1d) anomalies.Add("The oldest native relationship projection is "
                + nativeProjectionLag.ToString("0.##", CultureInfo.InvariantCulture) + " campaign days behind.");
            if (compactionBacklog > WorldHistoryCompactionBatchSize) anomalies.Add(compactionBacklog
                + " expired World History records remain in the incremental compaction backlog.");
            if (!string.IsNullOrWhiteSpace(relationshipMbtiError))
                anomalies.Add(relationshipMbtiError);
            return new Dictionary<string, object>
            {
                ["name"] = "Supporting Pipeline Health", ["status"] = status, ["latestSuccessfulDay"] = latestDay,
                ["actionStatuses"] = CountBy(actions, "status"), ["pendingActions"] = pending, ["failedActions"] = failed,
                ["overdueActions"] = overdue, ["weddingsWithoutSchedule"] = unknownWeddingSchedules, ["saveSync"] = saveSync,
                ["relationshipProcessingLagDays"] = relationshipLag,
                ["latestRelationshipIngestedDay"] = latestRelationshipIngestedDay,
                ["latestRelationshipCompletedDay"] = relationshipDay,
                ["relationshipInputBacklog"] = relationshipInputBacklog,
                ["partialRelationshipDay"] = partialRelationshipDay,
                ["relationshipWorker"] = RelationshipsService.ContinuousRelationshipWorkerStatus(),
                ["politicalRosterReconciliation"] =
                    WorldTestPoliticalRosterTimingStatus(),
                ["relationshipCadence"] = new Dictionary<string, object>
                {
                    ["daysPerBatch"] = RelationshipsService.RelationshipCadenceDays,
                    ["shardCount"] = RelationshipsService.RelationshipCadenceShardCount,
                    ["pairCountsByShard"] = relationshipShardCounts,
                    ["mode"] = "daily_random_matching",
                    ["magnitudeDie"] = "1d15",
                    ["baseMagnitudeDie"] = "1d15",
                    ["loverMagnitudeDie"] = "1d20",
                    ["latestMatchingDay"] = ReadInt(matching, "day_key", -1),
                    ["latestMatching"] = matchingCounters
                },
                ["initialSocialWorld"] = initializationSocial,
                ["relationshipClient"] = ReadDictionary(nativePayload, "relationshipClient") ?? new Dictionary<string, object>(),
                ["nativeProjectionLagDays"] = nativeProjectionLag,
                ["worldHistoryStorage"] = storage,
                ["featureFlags"] = ReadDictionary(nativePayload, "featureFlags") ?? new Dictionary<string, object>(),
                ["population"] = ReadDictionary(nativePayload, "population") ?? new Dictionary<string, object>(),
                ["nativeOutcomes"] = ReadDictionary(nativePayload, "nativeOutcomes") ?? new Dictionary<string, object>(),
                ["heartbeats"] = new Dictionary<string, object>
                {
                    ["native"] = latestDay,
                    ["relationships"] = relationshipDay,
                    ["rumors"] = Math.Max(
                        ReadDouble(WorldSimulationStore.SelectRumorOccurrences2(connection).FirstOrDefault(), "day", -1d),
                        ReadDouble(WorldSimulationStore.SelectWorldHistoryEvents22(connection, new Dictionary<string, object> { ["timeline"] = timelineId }).FirstOrDefault(), "day", -1d)),
                    ["rebellions"] = LatestNativeArray(connection, campaignId, timelineId, "rebellions", "weeklyRolls").Select(x => ReadDouble(x, "rolledDay", -1d)).DefaultIfEmpty(-1d).Max()
                },
                ["anomalies"] = anomalies
            };
        }

        private static Dictionary<string, object> CompactWorldTestRollup(Dictionary<string, object> overview)
        {
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["campaignId"] = ReadString(overview, "campaignId", ""),
                ["timelineId"] = ReadString(overview, "timelineId", ""),
                ["worldDay"] = ReadDouble(overview, "latestObservedDay", 0d),
                ["overallStatus"] = ReadString(overview, "overallStatus", "insufficient_data")
            };
            foreach (string key in new[] { "relationships", "kingdomLeaders", "politicalPressures", "clanConflicts", "diplomacy", "rumors", "rebellions", "warEconomy", "pipeline" })
            {
                Dictionary<string, object> source = ReadDictionary(overview, key) ?? new Dictionary<string, object>();
                Dictionary<string, object> compact = new Dictionary<string, object>
                {
                    ["status"] = ReadString(source, "status", "insufficient_data"),
                    ["latestSuccessfulDay"] = ReadDouble(source, "latestSuccessfulDay", -1d),
                    ["anomalyCount"] = CharactersService.ReadObjectList(source, "anomalies").Count
                };
                if (key == "relationships")
                {
                    compact["uniqueNpcCount"] = ReadInt(source, "uniqueNpcCount", 0);
                    compact["pairCount"] = ReadInt(source, "pairCount", 0);
                    compact["directionalEdgeCount"] = ReadInt(source, "directionalEdgeCount", 0);
                    compact["bands"] = ReadDictionary(source, "bands") ?? new Dictionary<string, object>();
                    compact["tags"] = CharactersService.ReadObjectList(source, "tags");
                    compact["romanticMarriages"] = ReadInt(
                        ReadDictionary(source, "lifecycle") ?? new Dictionary<string, object>(),
                        "romanticMarriages", 0);
                    compact["arrangedMarriages"] = ReadInt(
                        ReadDictionary(source, "lifecycle") ?? new Dictionary<string, object>(),
                        "arrangedMarriages", 0);
                    compact["nativePending"] = ReadInt(
                        ReadDictionary(source, "nativeSync") ?? new Dictionary<string, object>(),
                        "pending", 0);
                }
                else if (key == "diplomacy")
                {
                    compact["eventCount"] = ReadInt(source, "eventCount", 0);
                    compact["accepted"] = ReadInt(source, "accepted", 0);
                    compact["refused"] = ReadInt(source, "refused", 0);
                    compact["pendingActions"] = ReadInt(source, "pendingActions", 0);
                    compact["failedActions"] = ReadInt(source, "failedActions", 0);
                }
                else if (key == "politicalPressures")
                {
                    compact["dailyRolls"] = ReadInt(source, "dailyRolls", 0);
                    compact["incidentCount"] = ReadInt(source, "incidentCount", 0);
                    compact["hostileIncidents"] = ReadInt(source, "hostileIncidents", 0);
                    compact["peacefulIncidents"] = ReadInt(source, "peacefulIncidents", 0);
                    compact["actionRolls"] = ReadInt(source, "actionRolls", 0);
                    compact["actionsSelected"] = ReadInt(source, "actionsSelected", 0);
                    compact["nativeEffectsPending"] = ReadInt(source, "nativeEffectsPending", 0);
                }
                else if (key == "clanConflicts")
                {
                    compact["expectedKingdomRolls"] = ReadInt(source, "expectedKingdomRolls", 0);
                    compact["kingdomRolls"] = ReadInt(source, "kingdomRolls", 0);
                    compact["kingdomRollsMissing"] = ReadInt(source, "kingdomRollsMissing", 0);
                    compact["incidentCount"] = ReadInt(source, "incidentCount", 0);
                    compact["mediationSuccesses"] = ReadInt(source, "mediationSuccesses", 0);
                    compact["failedMediations"] = ReadInt(source, "failedMediations", 0);
                    compact["relationshipEffectsPending"] = ReadInt(source, "relationshipEffectsPending", 0);
                    compact["relationshipEffectsFailed"] = ReadInt(source, "relationshipEffectsFailed", 0);
                }
                else if (key == "rumors")
                {
                    compact["rumorCount"] = ReadInt(source, "rumorCount", 0);
                    compact["sourceOutcomeCount"] = ReadInt(source, "sourceOutcomeCount", 0);
                    compact["processedSourceOutcomes"] = ReadInt(source, "processedSourceOutcomes", 0);
                    compact["pendingSourceOutcomes"] = ReadInt(source, "pendingSourceOutcomes", 0);
                    compact["failedSourceOutcomes"] = ReadInt(source, "failedSourceOutcomes", 0);
                    compact["eligibleHooks"] = ReadInt(source, "eligibleHooks", 0);
                    compact["exposureAttempts"] = ReadInt(source, "exposureAttempts", 0);
                    compact["exposurePassed"] = ReadInt(source, "exposurePassed", 0);
                    compact["exposureFailed"] = ReadInt(source, "exposureFailed", 0);
                    compact["occurrencesCreated"] = ReadInt(source, "occurrencesCreated", 0);
                    compact["promotionPassed"] = ReadInt(source, "promotionPassed", 0);
                }
                else if (key == "rebellions")
                {
                    compact["weeklyRollCount"] = ReadInt(source, "weeklyRollCount", 0);
                    compact["eligibleRollCount"] = ReadInt(source, "eligibleRollCount", 0);
                    compact["triggeredRollCount"] = ReadInt(source, "triggeredRollCount", 0);
                    compact["activeCivilWars"] = ReadInt(source, "activeCivilWars", 0);
                    compact["resolvedCivilWars"] = ReadInt(source, "resolvedCivilWars", 0);
                }
                else if (key == "pipeline")
                {
                    compact["pendingActions"] = ReadInt(source, "pendingActions", 0);
                    compact["overdueActions"] = ReadInt(source, "overdueActions", 0);
                    compact["failedActions"] = ReadInt(source, "failedActions", 0);
                }
                result[key] = compact;
            }
            return result;
        }

        private static Dictionary<string, object> BuildWorldTestRelationshipDailyChange(
            Dictionary<string, object> current, Dictionary<string, object> priorRollup)
        {
            Dictionary<string, object> prior = ReadDictionary(priorRollup, "relationships");
            if (prior == null || !prior.ContainsKey("bands"))
                return new Dictionary<string, object> { ["available"] = false };

            Dictionary<string, object> currentBands = ReadDictionary(current, "bands")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> priorBands = ReadDictionary(prior, "bands")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> bandChanges = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (string band in WorldTestRelationshipBands)
            {
                Dictionary<string, object> now = WorldTestCountDictionary(
                    currentBands.TryGetValue(band, out object currentBand) ? currentBand : null);
                Dictionary<string, object> before = WorldTestCountDictionary(
                    priorBands.TryGetValue(band, out object priorBand) ? priorBand : null);
                bandChanges[band] = new Dictionary<string, object>
                {
                    ["uniqueNpcCount"] = ReadInt(now, "uniqueNpcCount", 0) - ReadInt(before, "uniqueNpcCount", 0),
                    ["directionalEdgeCount"] = ReadInt(now, "directionalEdgeCount", 0) - ReadInt(before, "directionalEdgeCount", 0),
                    ["pairCount"] = ReadInt(now, "pairCount", 0) - ReadInt(before, "pairCount", 0)
                };
            }

            Dictionary<string, Dictionary<string, object>> currentTags = CharactersService.ReadObjectList(current, "tags")
                .OfType<Dictionary<string, object>>()
                .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "tag", "")))
                .ToDictionary(x => ReadString(x, "tag", ""), x => x, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> priorTags = CharactersService.ReadObjectList(prior, "tags")
                .OfType<Dictionary<string, object>>()
                .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "tag", "")))
                .ToDictionary(x => ReadString(x, "tag", ""), x => x, StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> tagChanges = currentTags.Keys.Union(priorTags.Keys, StringComparer.OrdinalIgnoreCase)
                .Select(tag =>
                {
                    Dictionary<string, object> now = currentTags.TryGetValue(tag, out Dictionary<string, object> n)
                        ? n : new Dictionary<string, object>();
                    Dictionary<string, object> before = priorTags.TryGetValue(tag, out Dictionary<string, object> p)
                        ? p : new Dictionary<string, object>();
                    return new Dictionary<string, object>
                    {
                        ["tag"] = tag,
                        ["uniqueNpcCount"] = ReadInt(now, "uniqueNpcCount", 0) - ReadInt(before, "uniqueNpcCount", 0),
                        ["directionalEdgeCount"] = ReadInt(now, "directionalEdgeCount", 0) - ReadInt(before, "directionalEdgeCount", 0),
                        ["pairCount"] = ReadInt(now, "pairCount", 0) - ReadInt(before, "pairCount", 0)
                    };
                })
                .Where(x => ReadInt(x, "uniqueNpcCount", 0) != 0
                    || ReadInt(x, "directionalEdgeCount", 0) != 0
                    || ReadInt(x, "pairCount", 0) != 0)
                .OrderByDescending(x => Math.Abs(ReadInt(x, "directionalEdgeCount", 0)))
                .ToList();
            return new Dictionary<string, object>
            {
                ["available"] = true,
                ["priorDay"] = ReadDouble(priorRollup, "worldDay", -1d),
                ["uniqueNpcCount"] = ReadInt(current, "uniqueNpcCount", 0) - ReadInt(prior, "uniqueNpcCount", 0),
                ["pairCount"] = ReadInt(current, "pairCount", 0) - ReadInt(prior, "pairCount", 0),
                ["directionalEdgeCount"] = ReadInt(current, "directionalEdgeCount", 0)
                    - ReadInt(prior, "directionalEdgeCount", 0),
                ["bands"] = bandChanges,
                ["tags"] = tagChanges
            };
        }

        private static Dictionary<string, object> WorldTestCountDictionary(object value)
        {
            if (value is Dictionary<string, object> objectMap)
                return objectMap;
            if (value is Dictionary<string, int> integerMap)
                return integerMap.ToDictionary(
                    pair => pair.Key, pair => (object)pair.Value, StringComparer.OrdinalIgnoreCase);
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        private static List<Dictionary<string, object>> ReadWorldTestDiplomacyEvents(string campaignId)
        {
            Dictionary<string, Dictionary<string, object>> byId = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> row in Program.ReadJsonLinesFromPath(CampaignFileService.CampaignFile(campaignId, "diplomacy", "events.jsonl")))
            {
                string id = ReadString(row, "eventId", "");
                if (!string.IsNullOrWhiteSpace(id)) byId[id] = row;
            }
            foreach (Dictionary<string, object> row in DiplomacyService.ReadDiplomaticEventQueue(campaignId))
            {
                string id = ReadString(row, "eventId", "");
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (byId.TryGetValue(id, out Dictionary<string, object> existing))
                    foreach (KeyValuePair<string, object> pair in row) existing[pair.Key] = pair.Value;
                else byId[id] = row;
            }
            return byId.Values.ToList();
        }

        private static List<Dictionary<string, object>> ReadWorldTestActions(string campaignId)
        {
            Dictionary<string, Dictionary<string, object>> byId = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> row in Program.ReadJsonLinesFromPath(CampaignFileService.CampaignFile(campaignId, "actions", "actions.jsonl")))
            {
                string id = ReadString(row, "id", ReadFirstString(ReadDictionary(row, "report"), "serverActionId", "actionId"));
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!byId.TryGetValue(id, out Dictionary<string, object> existing))
                {
                    existing = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    byId[id] = existing;
                }
                foreach (KeyValuePair<string, object> pair in row) existing[pair.Key] = pair.Value;
                Dictionary<string, object> report = ReadDictionary(row, "report");
                if (report != null)
                {
                    existing["report"] = report;
                    existing["status"] = ReadString(report, "status", ReadString(row, "status", ""));
                }
            }
            List<Dictionary<string, object>> queue;
            lock (Program.FileLock) queue = ActionPipelineService.ReadActionQueueUnlocked(campaignId);
            foreach (Dictionary<string, object> row in queue)
            {
                string id = ReadString(row, "id", "");
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!byId.TryGetValue(id, out Dictionary<string, object> existing)) byId[id] = row;
                else foreach (KeyValuePair<string, object> pair in row) existing[pair.Key] = pair.Value;
            }
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                if (WorldSimulationStore.SelectSqliteMaster3(connection).Any())
                {
                    foreach (Dictionary<string, object> row in WorldSimulationStore.SelectRelationshipDirectorActions2(connection))
                    {
                        string id = ReadString(row, "director_action_id", "");
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        Dictionary<string, object> payload = TryParseJsonObject(
                            ReadString(row, "payload_json", "{}")) ?? new Dictionary<string, object>();
                        byId["relationship_director:" + id] = new Dictionary<string, object>
                        {
                            ["id"] = id,
                            ["source"] = "relationship_director",
                            ["type"] = ReadString(row, "action_type", ""),
                            ["status"] = ReadString(row, "status", ""),
                            ["createdDay"] = ReadDouble(row, "world_day", 0d),
                            ["result"] = TryParseJsonObject(ReadString(row, "result_json", "{}")) ?? new Dictionary<string, object>(),
                            ["attemptCount"] = ReadString(row, "status", "") == "claimed" ? 1 : 0,
                            ["record"] = new Dictionary<string, object>
                            {
                                ["createdDay"] = ReadDouble(row, "world_day", 0d),
                                ["actorId"] = ReadString(row, "actor_id", ""),
                                ["targetId"] = ReadString(row, "target_id", ""),
                                ["payload"] = payload
                            }
                        };
                    }
                }
            }
            return byId.Values.ToList();
        }

        private static List<Dictionary<string, object>> LatestNativeArray(ReignDbConnection connection, string campaignId, string timelineId, string section, string key)
        {
            if (section == "rebellions" && key == "weeklyRolls")
                return WorldSimulationStore.SelectWorldTestRebellionRolls(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId })
                    .Select(x => TryParseJsonObject(ReadString(x, "payload_json", "{}")) ?? new Dictionary<string, object>()).ToList();
            if (section == "rebellions" && key == "movements")
                return WorldSimulationStore.SelectWorldTestRebellionMovements(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId })
                    .Select(x => TryParseJsonObject(ReadString(x, "payload_json", "{}")) ?? new Dictionary<string, object>()).ToList();
            if (section == "rebellions" && key == "memberships")
                return WorldSimulationStore.SelectWorldTestRebellionMemberships(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId })
                    .Select(x => TryParseJsonObject(ReadString(x, "payload_json", "{}")) ?? new Dictionary<string, object>()).ToList();
            Dictionary<string, object> heartbeat = WorldSimulationStore.SelectWorldTestNativeHeartbeats(connection, new Dictionary<string, object> { ["timeline"] = timelineId }).FirstOrDefault();
            Dictionary<string, object> payload = TryParseJsonObject(ReadString(heartbeat, "payload_json", "{}")) ?? new Dictionary<string, object>();
            Dictionary<string, object> group = ReadDictionary(payload, section) ?? new Dictionary<string, object>();
            return ReadDictionaryList(group, key);
        }

        private static int LatestPopulationValue(ReignDbConnection connection, string key)
        {
            Dictionary<string, object> heartbeat = WorldSimulationStore.SelectWorldTestNativeHeartbeats2(connection).FirstOrDefault();
            Dictionary<string, object> payload = TryParseJsonObject(ReadString(heartbeat, "payload_json", "{}")) ?? new Dictionary<string, object>();
            return ReadInt(ReadDictionary(payload, "population"), key, 0);
        }

        private static bool LatestFeatureFlag(ReignDbConnection connection, string key, bool fallback)
        {
            Dictionary<string, object> heartbeat = WorldSimulationStore.SelectWorldTestNativeHeartbeats2(connection).FirstOrDefault();
            Dictionary<string, object> payload = TryParseJsonObject(ReadString(heartbeat, "payload_json", "{}")) ?? new Dictionary<string, object>();
            return ReadBool(ReadDictionary(payload, "featureFlags"), key, fallback);
        }

        private static bool LatestFeatureFlagFromPayload(string campaignId, string key, bool fallback)
        {
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId)) return LatestFeatureFlag(connection, key, fallback);
        }

        internal static double LatestKnownWorldDay(ReignDbConnection connection,
            string campaignId)
        {
            double relationship = ReadDouble(WorldSimulationStore.SelectSchemaMeta6(connection)
                .FirstOrDefault(), "value", 0d);
            double rumor = ReadDouble(WorldSimulationStore.SelectRumorOccurrences2(connection)
                .FirstOrDefault(), "day", 0d);
            Dictionary<string, object> state = DiplomacyService.ReadDirectorState(campaignId);
            return Math.Max(relationship, Math.Max(rumor,
                ReadDouble(state, "lastEvaluatedDay", 0d)));
        }

        internal static double LatestKnownWorldDay(ReignDbConnection connection,
            string campaignId, string timelineId)
        {
            string relationshipKey = "mbti_relationship_last_processed_day:"
                + timelineId;
            double relationship = ReadDouble(WorldSimulationStore.SelectSchemaMeta7(connection, new Dictionary<string, object> { ["key"] = relationshipKey })
                .FirstOrDefault(), "value", -1d);
            if (relationship < 0d && string.Equals(timelineId, "main",
                StringComparison.OrdinalIgnoreCase))
                relationship = ReadDouble(WorldSimulationStore.SelectSchemaMeta6(connection)
                    .FirstOrDefault(), "value", -1d);
            double ingested = ReadDouble(WorldSimulationStore.SelectRelationshipDailyInputs3(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault(), "day", -1d);
            double rumor = ReadDouble(WorldSimulationStore.SelectRumorOccurrences3(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault(), "day", -1d);
            double rollup = ReadDouble(WorldSimulationStore.SelectWorldTestDailyRollups(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault(), "day", -1d);
            return Math.Max(Math.Max(relationship, ingested), Math.Max(rumor, rollup));
        }

        private static Dictionary<string, object> AffinitySummaryFromHistogram(
            Dictionary<int, int> histogram)
        {
            List<KeyValuePair<int, int>> ordered = (histogram
                ?? new Dictionary<int, int>())
                .Where(x => x.Value > 0)
                .OrderBy(x => x.Key)
                .ToList();
            int count = ordered.Sum(x => x.Value);
            if (count == 0)
                return new Dictionary<string, object>
                {
                    ["minimum"] = 0, ["maximum"] = 0,
                    ["average"] = 0d, ["median"] = 0d
                };
            long weightedTotal = ordered.Sum(x => (long)x.Key * x.Value);
            int lowerIndex = (count - 1) / 2;
            int upperIndex = count / 2;
            int cursor = 0, lower = ordered[0].Key, upper = ordered[0].Key;
            foreach (KeyValuePair<int, int> bucket in ordered)
            {
                int next = cursor + bucket.Value;
                if (cursor <= lowerIndex && lowerIndex < next) lower = bucket.Key;
                if (cursor <= upperIndex && upperIndex < next)
                {
                    upper = bucket.Key;
                    break;
                }
                cursor = next;
            }
            return new Dictionary<string, object>
            {
                ["minimum"] = ordered.First().Key,
                ["maximum"] = ordered.Last().Key,
                ["average"] = Math.Round(weightedTotal / (double)count, 2),
                ["median"] = Math.Round((lower + upper) / 2d, 2)
            };
        }

        private static Dictionary<string, int> CountBy(List<Dictionary<string, object>> rows, string key)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> row in rows ?? new List<Dictionary<string, object>>())
                Increment(counts, FirstNonEmpty(ReadString(row, key, ""), "unknown"));
            return counts;
        }

        private static Dictionary<string, int> CountDiplomacyIntentFamilies(List<Dictionary<string, object>> events)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> item in events)
            {
                Dictionary<string, object> diagnostic = ReadDictionary(item, "initiativeDiagnostics") ?? new Dictionary<string, object>();
                Increment(counts, FirstNonEmpty(ReadString(diagnostic, "family", ""), "unknown"));
            }
            return counts;
        }

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) key = "unknown";
            counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
        }

        internal static int TableCount(ReignDbConnection connection, string table)
        {
            if (!WorldSimulationStore.SelectSqliteMaster(connection, new Dictionary<string, object> { ["name"] = table }).Any()) return 0;
            return ReadInt(WorldSimulationStore.CountDynamic(connection, table).FirstOrDefault(), "count", 0);
        }

        private static Dictionary<string, int> StatusCounts(ReignDbConnection connection, string table, string column)
        {
            Dictionary<string, int> result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!WorldSimulationStore.SelectSqliteMaster(connection, new Dictionary<string, object> { ["name"] = table }).Any()) return result;
            foreach (Dictionary<string, object> row in WorldSimulationStore.SelectDynamic(connection, column, table, column))
                result[ReadString(row, column, "unknown")] = ReadInt(row, "count", 0);
            return result;
        }

        internal static bool IsTerminalFailureStatus(string status)
        {
            string value = ActionPipelineService.NormalizeLookup(status);
            return DialogueService.ContainsAny(value, "failed", "blocked", "expired", "validation_failed");
        }

        private static double WorldTestWeddingEndDay(Dictionary<string, object> action)
        {
            var result = ReadDictionary(action, "result") ?? ReadDictionary(ReadDictionary(action, "record"), "result");
            double end = ReadDouble(result, "scheduledEndDay", -1);
            return ReignBeta.Shared.Rulers.ReignRulerPolicy.Finite(end) && end > 0 ? end : -1;
        }

        private static bool WorldTestActionOverdue(Dictionary<string, object> action, double day)
        {
            string status = ReadString(action, "status", "");
            if (!IsPendingStatus(status)) return false;
            if (status == "wedding_pending")
            {
                double end = WorldTestWeddingEndDay(action);
                // Allow one day for the terminal native receipt. An absent schedule
                // is a coverage gap, not proof a legitimately booked wedding stalled.
                return end >= 0 && day > end + 1d;
            }
            return day - ReadDouble(ReadDictionary(action, "record") ?? action, "createdDay", day) > 1d;
        }

        private static bool IsPendingStatus(string status)
        {
            string value = ActionPipelineService.NormalizeLookup(status);
            return DialogueService.ContainsAny(value, "queued", "pending", "delivered", "executing", "retry", "proposed");
        }

        private static string WorstWorldTestStatus(IEnumerable<string> statuses)
        {
            Dictionary<string, int> rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["healthy"] = 0, ["disabled"] = 1, ["insufficient_data"] = 2, ["warning"] = 3, ["error"] = 4
            };
            return (statuses ?? Enumerable.Empty<string>()).OrderByDescending(x => rank.TryGetValue(x ?? "", out int value) ? value : 2).FirstOrDefault() ?? "insufficient_data";
        }

        private static List<Dictionary<string, object>> BuildWorldTestCheckpoints(double latestDay)
        {
            return WorldTestCheckpointGates.Select(day => new Dictionary<string, object>
            {
                ["day"] = day, ["reached"] = latestDay >= day, ["remainingDays"] = Math.Max(0d, Math.Round(day - latestDay, 2))
            }).ToList();
        }

        private static string UnixToIso(long unix)
        {
            return unix <= 0 ? "" : DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("o");
        }

        private static void AddPoliticalPressureObservationContracts(ReignDbConnection connection,
            string campaign, Action<string, bool, string> add)
        {
            var petitionerClans = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["clanId"] = "crown", ["kingdomId"] = "realm", ["leaderHeroId"] = "RULER", ["fiefCount"] = 1000 },
                new Dictionary<string, object> { ["clanId"] = "vassal", ["kingdomId"] = "realm", ["leaderHeroId"] = "lord", ["leaderName"] = "The Vassal" },
                new Dictionary<string, object> { ["clanId"] = "legacy", ["kingdomId"] = "realm", ["leaderId"] = "second_lord", ["leaderName"] = "Second Vassal" },
                new Dictionary<string, object> { ["clanId"] = "unknown", ["kingdomId"] = "realm", ["leaderName"] = "Unknown", ["fiefCount"] = 1000 },
                new Dictionary<string, object> { ["clanId"] = "foreign", ["kingdomId"] = "other", ["leaderHeroId"] = "foreign_lord", ["fiefCount"] = 1000 }
            };
            var petitioners = DiplomacyService.SelectPoliticalPressureClans(campaign, "petitioners", 12, petitionerClans, "realm", "ruler", 4, "origin");
            add("political_pressure_petitioners_exclude_sovereign",
                petitioners.Count == 2 && DiplomacyService.PoliticalPressureLeadLords(petitioners).OrderBy(x => x).SequenceEqual(new[] { "Second Vassal", "The Vassal" }),
                "A dominant ruling clan, unknown leader and foreign clan cannot become petitioners; named vassals and legacy leader IDs remain eligible.");
            add("political_pressure_petitioner_severity_limit",
                DiplomacyService.SelectPoliticalPressureClans(campaign, "petitioners", 12, petitionerClans, "realm", "ruler", 1, "target").Count == 1,
                "Minor incidents retain the one-clan limit after filtering on either side.");
            add("political_pressure_no_self_petition_fallback",
                DiplomacyService.SelectPoliticalPressureClans(campaign, "petitioners", 12, petitionerClans.Take(1).ToList(), "realm", "ruler", 4, "origin").Count == 0
                && DiplomacyService.SelectPoliticalPressureClans(campaign, "petitioners", 12, petitionerClans, "realm", "", 4, "origin").Count == 0,
                "A realm without a vassal or a known sovereign yields no petitioners rather than substituting the ruler.");
            DiplomacyService.EnsurePoliticalPressureSchema(connection);
            WorldSimulationStore.Savepoint2(connection);
            try
            {
                var values = new Dictionary<string, object>();
                foreach (string key in new[] { "incident_id", "campaign_id", "timeline_id", "archetype_id", "polarity", "channel", "severity",
                    "origin_kingdom_id", "target_kingdom_id", "origin_kingdom_name", "target_kingdom_name", "origin_ruler_id", "target_ruler_id",
                    "origin_ruler_name", "target_ruler_name", "origin_clans_json", "origin_lords_json", "target_clans_json", "target_lords_json",
                    "trait_routes_json", "trait_rolls_json", "origin_stance", "target_stance", "headline", "narrative",
                    "llm_selector_status", "llm_narration_status", "correlation_id" }) values[key] = "";
                foreach (string key in new[] { "day_key", "world_day", "severity_rank", "pressure_amount", "relationship_penalty",
                    "origin_war_count", "target_war_count", "hostile_chance", "polarity_roll", "origin_pressure_before", "origin_pressure_after",
                    "target_pressure_before", "target_pressure_after", "created_ts", "updated_ts" }) values[key] = 0;
                values["incident_id"] = "pressure_observation_incident"; values["campaign_id"] = campaign; values["timeline_id"] = "pressure_main";
                values["origin_kingdom_id"] = "player"; values["target_kingdom_id"] = "foreign";
                values["origin_ruler_id"] = "player_hero"; values["target_ruler_id"] = "foreign_ruler";
                values["origin_stance"] = "awaiting_player"; values["target_stance"] = "preserve_relations";
                values["severity_rank"] = 2; values["pressure_amount"] = 10;
                values["target_pressure_before"] = 17; values["target_pressure_after"] = 7;
                WorldSimulationStore.InsertPoliticalPressureIncidents(connection, string.Join(",", values.Keys), string.Join(",", values.Keys.Select(key => "$" + key)),values);
                WorldSimulationStore.InsertPoliticalPressureActivity(connection, new Dictionary<string, object> { ["campaign"] = campaign });
                Func<string> snapshot = () => Json.Serialize(new Dictionary<string, object>
                {
                    ["incidents"] = WorldSimulationStore.SelectPoliticalPressureIncidents(connection),
                    ["activity"] = WorldSimulationStore.SelectPoliticalPressureActivity(connection),
                    ["pressure"] = WorldSimulationStore.SelectPoliticalPressureState(connection),
                    ["receipts"] = WorldSimulationStore.SelectPoliticalPressureDeltaReceipts(connection)
                });
                string before = snapshot();
                var filter = new Dictionary<string, string> { ["pair"] = "player|foreign" };
                var observed = DiplomacyService.QueryPoliticalPressureActivity(connection, campaign, "pressure_main", filter, 10).Single();
                var incident = ReadDictionary(observed, "incident");
                add("political_pressure_incident_sides_observed",
                    ReadInt(observed, "before_value", -1) == 0 && ReadInt(observed, "after_value", -1) == 0
                    && ReadInt(incident, "origin_pressure_before", -1) == 0 && ReadInt(incident, "origin_pressure_after", -1) == 0
                    && ReadInt(incident, "target_pressure_before", -1) == 17 && ReadInt(incident, "target_pressure_after", -1) == 7
                    && ReadString(incident, "origin_stance", "") == "awaiting_player"
                    && ReadString(incident, "target_stance", "") == "preserve_relations",
                    "Activity retains its origin values while incident detail exposes the foreign target's actual before/after pressure and both sovereign choices.");
                var otherTimeline = DiplomacyService.QueryPoliticalPressureActivity(connection, campaign, "pressure_other", filter, 10).Single();
                var missingIncident = DiplomacyService.QueryPoliticalPressureActivity(connection, campaign, "pressure_main", null, 10)
                    .Single(row => ReadString(row, "incident_id", "") == "missing_incident");
                add("political_pressure_observation_scope_and_missing_incident",
                    ReadDictionary(otherTimeline, "incident") == null && ReadDictionary(missingIncident, "incident") == null
                    && DiplomacyService.QueryPoliticalPressureActivity(connection, campaign, "pressure_main",
                        new Dictionary<string, string> { ["pair"] = "foreign|player" }, 10).Count == 0,
                    "Incident joins respect timeline ownership; missing records remain observable with null details and activity orientation is not reversed.");
                add("political_pressure_observation_is_non_mutating", before == snapshot(),
                    "Pressure observation does not alter incidents, activity, pressure state or application receipts.");
            }
            finally
            {
                WorldSimulationStore.Rollback3(connection);
                WorldSimulationStore.Release2(connection);
            }
        }

#if !REIGN_EXCLUDE_VERIFICATION
        internal static List<Dictionary<string, object>> RunWorldTestSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            results.AddRange(RunArrestSystemSelfTests());
            results.AddRange(RelationshipsService.RunNativeRelationProjectionSelfTests());
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "world_test", ["caseId"] = id,
                ["name"] = id, ["summary"] = summary, ["durationMs"] = 0
            });
            var scheduledWedding = TestDict("status", "wedding_pending", "createdDay", 1d,
                "result", TestDict("scheduledStartDay", 20d, "scheduledEndDay", 21d));
            add("scheduled_wedding_overdue_uses_ceremony_window",
                !WorldTestActionOverdue(scheduledWedding, 19d) && !WorldTestActionOverdue(scheduledWedding, 22d)
                && WorldTestActionOverdue(scheduledWedding, 22.01d),
                "Weddings wait through their booked window and one day of receipt grace, independently of creation age.");
            add("missing_wedding_schedule_is_unknown_not_overdue",
                WorldTestWeddingEndDay(TestDict("status", "wedding_pending")) < 0
                && !WorldTestActionOverdue(TestDict("status", "wedding_pending", "createdDay", 1d), 100d)
                && WorldTestActionOverdue(TestDict("status", "pending", "createdDay", 1d), 3d)
                && !WorldTestActionOverdue(TestDict("status", "completed", "createdDay", 1d), 100d),
                "Missing ceremony dates remain a visible coverage gap while ordinary actions retain the one-day rule.");
            add("relationship_band_boundaries",
                RelationshipsService.RelationshipBand(-70) == "nemesis" && RelationshipsService.RelationshipBand(-69) == "enemy"
                && RelationshipsService.RelationshipBand(9) == "neutral" && RelationshipsService.RelationshipBand(10) == "acquaintance"
                && RelationshipsService.RelationshipBand(84) == "devoted" && RelationshipsService.RelationshipBand(85) == "bonded",
                "World Test uses the authoritative relationship-band boundaries.");
            add("health_precedence",
                WorstWorldTestStatus(new[] { "healthy", "warning", "disabled" }) == "warning"
                && WorstWorldTestStatus(new[] { "healthy", "error", "insufficient_data" }) == "error",
                "Errors and warnings dominate informational health states.");
            add("checkpoint_boundaries",
                BuildWorldTestCheckpoints(31.5d).Count(x => ReadBool(x, "reached", false)) == 3,
                "Campaign checkpoints are reached at days 1, 7, 31.5, 63, and 126.");
            add("configured_rebellion_chance", RebellionService.RebellionWeeklyOutbreakPercent == 5,
                "World Test reports the configured 5% weekly outbreak chance used by the native rebellion contract.");
            List<Dictionary<string, object>> treatyFixture =
                new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["kind"] = "defensive_pact",
                        ["actorKingdomId"] = "aserai",
                        ["targetKingdomId"] = "empire",
                        ["isActive"] = true
                    }
                };
            add("native_treaty_receipt_is_pair_symmetric",
                WorldTestNativeAgreementExists(treatyFixture,
                    "defensive_pact", "empire", "aserai")
                && !WorldTestNativeAgreementExists(treatyFixture,
                    "trade_agreement", "empire", "aserai"),
                "Completed treaty validation accepts the native pair in either direction but still requires the exact agreement kind.");
            add("ruler_bonus_polarity_lock",
                DiplomacyService.ClassifyRulerDiplomacyPolarity(new Dictionary<string, object>
                    { ["command"] = "sign_alliance" }) == "positive"
                && DiplomacyService.ClassifyRulerDiplomacyPolarity(new Dictionary<string, object>
                    { ["command"] = "declare_war" }) == "negative",
                "Relationship bonus opportunities classify cooperative and hostile commands with opposite locked polarities.");
            add("ruler_mixed_package_is_ambiguous",
                DiplomacyService.ClassifyRulerDiplomacyPolarity(new Dictionary<string, object>
                {
                    ["command"] = "diplomatic_package",
                    ["terms"] = new Dictionary<string, object>
                    {
                        ["includeAlliance"] = true,
                        ["reparationsGold"] = 5000
                    }
                }) == "ambiguous",
                "A mixed positive and punitive package cannot satisfy a relationship bonus opportunity.");
            add("ruler_roll_is_directionally_stable",
                DiplomacyService.StableRulerD100("campaign", "main", 8, "a|b")
                    == DiplomacyService.StableRulerD100("campaign", "main", 8, "a|b")
                && DiplomacyService.StableRulerD100("campaign", "main", 8, "a|b") >= 1
                && DiplomacyService.StableRulerD100("campaign", "main", 8, "a|b") <= 100,
                "Ruler compatibility and bonus dice are stable d100 values keyed to timeline, day, and direction.");
            add("daily_ruler_relationship_rolls_disabled",
                !DiplomacyService.DailyRulerRelationshipRollsEnabled,
                "Ruler relationships change through recorded political and diplomatic incidents without an independent daily dice pass.");
            add("kingdom_leaders_control_center_script_contract",
                Program.ControlCenterHtml().Contains("cell.contextLabel||''"),
                "The rendered Kingdom Leaders matrix uses a valid JavaScript empty-string fallback so Control Center initialization can parse and run.");
            results.AddRange(RunWorldTestTelemetrySelfTests());
            results.AddRange(RunWorldTestRollupSelfTests());
            results.AddRange(DiplomacyService.RunPoliticalPressureSelfTests());
            results.AddRange(RelationshipsService.RunClanConflictSelfTests());
            string previousCampaignsRoot = Program.CampaignsRootOverride.Value;
            string isolatedCampaignsRoot = Path.Combine(
                string.IsNullOrWhiteSpace(previousCampaignsRoot) ? Path.Combine(Program.TestsDir, "isolated-campaigns") : previousCampaignsRoot,
                "wt_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Program.CampaignsRootOverride.Value = isolatedCampaignsRoot;
            string campaign = "wt_" + Guid.NewGuid().ToString("N").Substring(0, 10);
            try
            {
                results.AddRange(RelationshipsService.RunWorldRelationshipModelSelfTests());
                Dictionary<string, object> missingOverview = WorldTestOverviewApi(new Dictionary<string, string>
                {
                    ["campaignId"] = "default", ["timelineId"] = "main"
                });
                add("empty_selection_is_read_only", !ReadBool(missingOverview, "ok", true) && !Directory.Exists(CampaignFileService.CampaignDirectory("default")),
                    "An empty or stale World Test selection never creates a synthetic default campaign database.");

                Dictionary<string, object> heartbeat = new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["timelineId"] = "main", ["worldDay"] = 7.25d,
                    ["mainHeroStringId"] = "player_hero", ["mainHeroName"] = "Player Hero",
                    ["population"] = new Dictionary<string, object> { ["eligibleNpcCount"] = 12, ["kingdomCount"] = 4 },
                    ["featureFlags"] = new Dictionary<string, object> { ["passiveRelationshipsEnabled"] = true },
                    ["rebellions"] = new Dictionary<string, object>
                    {
                        ["weeklyRolls"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["kingdomId"] = "kingdom_a", ["clanId"] = "clan_a", ["weekIndex"] = 1,
                                ["roll"] = 7, ["rulerRelation"] = -30, ["wasEligible"] = true,
                                ["triggered"] = true, ["rolledDay"] = 7d, ["isPlayerClan"] = false
                            }
                        },
                        ["movements"] = new List<object>(), ["memberships"] = new List<object>()
                    }
                };
                Dictionary<string, object> first = WorldTestHeartbeatApi(heartbeat);
                Dictionary<string, object> second = WorldTestHeartbeatApi(heartbeat);
                ProcessNextWorldTestRollup();
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    EnsureWorldTestSchema(connection);
                    List<Dictionary<string, object>> announcementFixture = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["eventId"] = "awaiting_government", ["announcementReady"] = false, ["executionStatus"] = "executing" },
                        new Dictionary<string, object> { ["eventId"] = "ready_unseen", ["announcementReady"] = true },
                        new Dictionary<string, object> { ["eventId"] = "ready_delivered", ["announcementReady"] = true, ["delivered"] = true },
                        new Dictionary<string, object> { ["eventId"] = "already_acknowledged", ["announcementReady"] = true, ["acknowledged"] = true },
                        new Dictionary<string, object> { ["eventId"] = "readiness_absent" }
                    };
                    List<Dictionary<string, object>> previousAnnouncements = DiplomacyService.ReadDiplomaticEventQueue(campaign);
                    try
                    {
                        DiplomacyService.WriteDiplomaticEventQueue(campaign, announcementFixture);
                        Dictionary<string, object> announcementOverview = BuildWorldTestDiplomacy(
                            connection, campaign, "main", 7.25d, 1d, new Dictionary<string, object>());
                        Dictionary<string, object> nextAnnouncements = DiplomacyService.NextDiplomaticEvents(
                            new Dictionary<string, object> { ["campaignId"] = campaign },
                            new Dictionary<string, string>());
                        add("announcement_quiescence_matches_native_delivery",
                            ReadInt(announcementOverview, "undeliveredAnnouncements", -1) == 1
                            && ReadInt(announcementOverview, "unacknowledgedAnnouncements", -1) == 2
                            && ReadInt(nextAnnouncements, "count", -1) == 2
                            && ReadDictionaryList(nextAnnouncements, "events").All(x =>
                                ReadString(x, "eventId", "").StartsWith("ready_", StringComparison.Ordinal)),
                            "Save quiescence counts only announcements the native delivery API can display; pending government decisions and absent readiness do not block a paused checkpoint.");
                        announcementFixture[0]["announcementReady"] = true;
                        announcementFixture[0]["executionStatus"] = "completed";
                        DiplomacyService.WriteDiplomaticEventQueue(campaign, announcementFixture);
                        announcementOverview = BuildWorldTestDiplomacy(
                            connection, campaign, "main", 7.25d, 1d, new Dictionary<string, object>());
                        nextAnnouncements = DiplomacyService.NextDiplomaticEvents(
                            new Dictionary<string, object> { ["campaignId"] = campaign },
                            new Dictionary<string, string>());
                        add("completed_government_announcement_blocks_until_acknowledged",
                            ReadInt(announcementOverview, "undeliveredAnnouncements", -1) == 2
                            && ReadInt(announcementOverview, "unacknowledgedAnnouncements", -1) == 3
                            && ReadInt(nextAnnouncements, "count", -1) == 3,
                            "Once government execution completes and marks the announcement ready, the unchanged delivery and acknowledgement gates require it to drain.");
                        var publicFixture = Enumerable.Range(0, 35).Select(index => TestDict("eventId", "published_" + index,
                            "announcementReady", true, "acknowledged", true, "summary", "A published public decision.",
                            "privateDecisionFactors", "PRIVATE_DECISION_SENTINEL", "initiativeDiagnostics", "PRIVATE_INITIATIVE_SENTINEL")).ToList();
                        publicFixture.Add(TestDict("eventId", "pending_private_proposal", "announcementReady", false));
                        publicFixture.Add(TestDict("eventId", "private_ready", "announcementReady", true, "visibility", "private"));
                        DiplomacyService.WriteDiplomaticEventQueue(campaign, publicFixture);
                        string publicationQueuePath = CampaignFileService.CampaignFile(campaign, "diplomacy", "event-queue.json");
                        string beforeRead = File.ReadAllText(publicationQueuePath);
                        var publicAnnouncements = DiplomacyService.NextDiplomaticEvents(TestDict("campaignId", campaign),
                            new Dictionary<string, string> { ["publicHistory"] = "true", ["limit"] = "500" });
                        var publicRows = ReadDictionaryList(publicAnnouncements, "events");
                        add("public_diplomacy_ignores_popup_and_ack_limits", ReadInt(publicAnnouncements, "count", 0) == 35
                            && publicRows.All(row => ReadBool(row, "publicAnnouncement", false) && ReadBool(row, "acknowledged", false)),
                            "All retained ready public decisions are readable beyond the ten-popup window and acknowledgment.");
                        add("public_diplomacy_projection_is_read_only_and_allowlisted", beforeRead == File.ReadAllText(publicationQueuePath)
                            && !Json.Serialize(publicRows).Contains("PRIVATE_DECISION_SENTINEL") && !Json.Serialize(publicRows).Contains("PRIVATE_INITIATIVE_SENTINEL")
                            && !publicRows.Any(row => ReadString(row, "eventId", "").Contains("private")),
                            "Public projection excludes pending/private rows and unselected fields without changing fetch/ack metadata.");
                    }
                    finally
                    {
                        DiplomacyService.WriteDiplomaticEventQueue(campaign, previousAnnouncements);
                    }
                    int count = ReadInt(WorldSimulationStore.CountWorldTestNativeHeartbeats(connection).FirstOrDefault(), "count", 0);
                    int rollups = ReadInt(WorldSimulationStore.CountWorldTestDailyRollups(connection).FirstOrDefault(), "count", 0);
                    int rebellionRolls = ReadInt(WorldSimulationStore.CountWorldTestRebellionRolls(connection).FirstOrDefault(), "count", 0);
                    bool heartbeatIdempotent = ReadBool(first, "ok", false)
                        && ReadBool(second, "ok", false) && count == 1
                        && rollups == 1 && rebellionRolls == 1;
                    add("heartbeat_idempotency", heartbeatIdempotent,
                        heartbeatIdempotent
                            ? "Repeated telemetry for one campaign, timeline, and day replaces rather than duplicates."
                            : "Heartbeat idempotency counts disagreed: "
                                + Json.Serialize(new Dictionary<string, object>
                                {
                                    ["heartbeats"] = count,
                                    ["rollups"] = rollups,
                                    ["rebellionRolls"] = rebellionRolls,
                                    ["worker"] = WorldTestRollupWorkerStatus()
                                }));
                    RelationshipsService.EnsureMbtiRelationshipSchema(connection);
                    WorldSimulationStore.InsertRelationshipPairChemistry(connection);
                    WorldSimulationStore.InsertRelationshipObservedHeroes(connection);
                    ApplyWorldTestRelationshipPairDelta(connection, campaign, "main", 7,
                        new Dictionary<string, object>(),
                        new Dictionary<string, object>
                        {
                            ["pairKey"] = "a|b", ["heroAId"] = "a", ["heroBId"] = "b",
                            ["affinityAToB"] = -70, ["affinityBToA"] = 85,
                            ["tagAToB"] = "nemesis", ["tagBToA"] = "bonded", ["firstDay"] = 1
                        }, new Dictionary<string, object>());
                    ApplyWorldTestRelationshipPairDelta(connection, campaign, "main", 7,
                        new Dictionary<string, object>(),
                        new Dictionary<string, object>
                        {
                            ["pairKey"] = "a|c", ["heroAId"] = "a", ["heroBId"] = "c",
                            ["affinityAToB"] = 10, ["affinityBToA"] = 10,
                            ["tagAToB"] = "acquaintance", ["tagBToA"] = "acquaintance",
                            ["firstDay"] = 2
                        }, new Dictionary<string, object>());
                    WorldSimulationStore.InsertMarriageEvaluations(connection);
                    WorldSimulationStore.InsertRelationshipDirectorActions(connection);
                }
                Dictionary<string, object> concurrentTraits = CharactersService.CoreTraitKeys.ToDictionary(
                    x => x, x => (object)50, StringComparer.OrdinalIgnoreCase);
                List<Dictionary<string, object>> concurrentHeroes = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["heroStringId"] = "concurrent_a", ["name"] = "Concurrent A", ["age"] = 30, ["traitPercentages"] = concurrentTraits },
                    new Dictionary<string, object> { ["heroStringId"] = "concurrent_b", ["name"] = "Concurrent B", ["age"] = 30, ["traitPercentages"] = concurrentTraits }
                };
                Dictionary<string, object> concurrentGroup = new Dictionary<string, object>
                {
                    ["kind"] = "settlement", ["id"] = "concurrent_settlement",
                    ["heroIds"] = new List<string> { "concurrent_a", "concurrent_b" },
                    ["nativeRelations"] = new List<Dictionary<string, object>>()
                };
                string concurrentCampaign = "__wt_concurrent_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                List<Task> concurrentWriters = new List<Task>();
                for (int index = 0; index < 4; index++)
                {
                    int day = 8 + index;
                    concurrentWriters.Add(Task.Run(() => WorldTestHeartbeatApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = concurrentCampaign, ["timelineId"] = "main", ["worldDay"] = (double)day,
                        ["population"] = new Dictionary<string, object> { ["eligibleNpcCount"] = 12, ["kingdomCount"] = 4 },
                        ["featureFlags"] = new Dictionary<string, object> { ["passiveRelationshipsEnabled"] = true }
                    })));
                    concurrentWriters.Add(Task.Run(() => RelationshipsService.MbtiRelationshipSnapshotApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = concurrentCampaign, ["worldDay"] = (double)day,
                        ["heroes"] = concurrentHeroes,
                        ["presenceGroups"] = new List<Dictionary<string, object>> { concurrentGroup },
                        ["correlationId"] = "world_test_concurrency_" + day.ToString(CultureInfo.InvariantCulture)
                    })));
                }
                bool concurrentWritesPassed = true;
                string concurrentWriteError = "";
                try { Task.WaitAll(concurrentWriters.ToArray()); }
                catch (AggregateException ex)
                {
                    concurrentWritesPassed = false;
                    concurrentWriteError = string.Join(" | ", ex.Flatten().InnerExceptions.Select(x => x.Message).Distinct());
                }
                add("concurrent_campaign_writers", concurrentWritesPassed,
                    concurrentWritesPassed
                        ? "Native heartbeat and relationship producers can write the same campaign concurrently without SQLite or metadata-file contention."
                        : "Concurrent campaign producers encountered a write conflict: " + concurrentWriteError);
                Dictionary<string, object> aggregate = BuildWorldTestOverview(campaign, "main", double.MinValue, double.MaxValue, 100);
                Dictionary<string, object> relationships = ReadDictionary(aggregate, "relationships") ?? new Dictionary<string, object>();
                Dictionary<string, object> bandMap = ReadDictionary(relationships, "bands") ?? new Dictionary<string, object>();
                Dictionary<string, int> nemesis = bandMap.TryGetValue("nemesis", out object nemesisValue)
                    ? nemesisValue as Dictionary<string, int> ?? new Dictionary<string, int>() : new Dictionary<string, int>();
                Dictionary<string, int> acquaintance = bandMap.TryGetValue("acquaintance", out object acquaintanceValue)
                    ? acquaintanceValue as Dictionary<string, int> ?? new Dictionary<string, int>() : new Dictionary<string, int>();
                bool aggregationExact =
                    ReadInt(relationships, "uniqueNpcCount", 0) == 3 && ReadInt(relationships, "pairCount", 0) == 2
                    && ReadInt(relationships, "directionalEdgeCount", 0) == 4
                    && nemesis.TryGetValue("uniqueNpcCount", out int nemesisNpc) && nemesisNpc == 1
                    && nemesis.TryGetValue("directionalEdgeCount", out int nemesisEdges) && nemesisEdges == 1
                    && acquaintance.TryGetValue("uniqueNpcCount", out int acquaintanceNpcs) && acquaintanceNpcs == 2
                    && acquaintance.TryGetValue("pairCount", out int acquaintancePairs) && acquaintancePairs == 1;
                add("exact_relationship_aggregation", aggregationExact,
                    aggregationExact ? "Unique NPC, directional edge, pair, band, and tag totals remain distinct and exact."
                    : "Unexpected relationship aggregation: " + Json.Serialize(new Dictionary<string, object>
                    {
                        ["uniqueNpcCount"] = ReadInt(relationships, "uniqueNpcCount", 0),
                        ["pairCount"] = ReadInt(relationships, "pairCount", 0),
                        ["directionalEdgeCount"] = ReadInt(relationships, "directionalEdgeCount", 0),
                        ["nemesis"] = nemesis, ["acquaintance"] = acquaintance
                    }));
                Dictionary<string, object> playerFilteredDetails = WorldTestDetailsApi(new Dictionary<string, string>
                {
                    ["campaignId"] = campaign, ["timelineId"] = "main", ["subsystem"] = "relationships",
                    ["search"] = "player_hero"
                });
                add("player_relationships_excluded",
                    ReadInt(playerFilteredDetails, "total", -1) == 0,
                    "World Test passive relationship totals, marriages, and relationship drill-downs exclude the player even when player rows exist in the source ledgers.");
                Dictionary<string, object> exactPairDetails = WorldTestDetailsApi(new Dictionary<string, string>
                {
                    ["campaignId"] = campaign, ["timelineId"] = "main", ["subsystem"] = "relationship pair",
                    ["pair"] = "player_hero|a"
                });
                var exactPairRow = ReadDictionaryList(exactPairDetails, "rows").Single();
                add("exact_relationship_pair_observation",
                    ReadBool(exactPairRow, "hasPair", false)
                    && ReadString(exactPairRow, "pairKey", "") == "a|player_hero"
                    && ReadInt(ReadDictionary(exactPairRow, "pair"), "affinity_a_to_b", 0) == 100
                    && ReadString(ReadDictionary(exactPairRow, "firstToSecond"), "observerId", "") == "player_hero",
                    "Explicit exact-pair diagnostics include player consequences and preserve requested observer direction; passive reports still exclude the player.");
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    int beforePairs = ReadInt(WorldSimulationStore.CountRelationshipPairChemistry3(connection).First(), "n", 0);
                    var missingPair = ReadWorldTestRelationshipPair(connection, campaign, "main", "missing_observer|missing_target");
                    int afterPairs = ReadInt(WorldSimulationStore.CountRelationshipPairChemistry3(connection).First(), "n", 0);
                    bool rejected = false;
                    try { ReadWorldTestRelationshipPair(connection, campaign, "main", "a|a"); }
                    catch (ArgumentException) { rejected = true; }
                    add("missing_relationship_pair_observation", beforePairs == afterPairs
                        && !ReadBool(missingPair, "hasPair", true)
                        && !ReadBool(ReadDictionary(missingPair, "firstToSecond"), "hasPair", true)
                        && rejected,
                        "Missing pair observation creates no relationship; malformed self-pairs fail before access.");
                    ReputationService.EnsureRulerFavorContactSchema(connection);
                    ReputationService.EnsureSocialReputationSchema(connection);
                    WorldSimulationStore.Savepoint3(connection);
                    try
                    {
                        var p = new Dictionary<string, object> { ["campaign"] = campaign };
                        WorldSimulationStore.InsertCourtRulerFavorContact(connection, p);
                        WorldSimulationStore.InsertCharacterReputations(connection, p);
                        string beforeContact = Json.Serialize(WorldSimulationStore.SelectCourtRulerFavorContact(connection));
                        string beforeReputations = Json.Serialize(WorldSimulationStore.SelectCharacterReputations2(connection));
                        var observed = ReadWorldTestRelationshipPair(connection, campaign, "main", "a|player_hero");
                        add("relationship_pair_favor_observation_is_non_mutating",
                            ReadDictionaryList(observed, "favorContact").Count == 1
                            && ReadDictionaryList(observed, "favorReputations").Count == 1
                            && beforeContact == Json.Serialize(WorldSimulationStore.SelectCourtRulerFavorContact(connection))
                            && beforeReputations == Json.Serialize(WorldSimulationStore.SelectCharacterReputations2(connection)),
                            "Exact-pair favor observation returns the clock and established tag without refreshing contact or expiring reputation.");
                    }
                    finally
                    {
                        WorldSimulationStore.Rollback4(connection);
                        WorldSimulationStore.Release3(connection);
                    }
                }
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaign))
                    AddPoliticalPressureObservationContracts(connection, campaign, add);
                Dictionary<string, object> dayEightHeartbeat =
                    new Dictionary<string, object>(heartbeat, StringComparer.OrdinalIgnoreCase)
                    {
                        ["timelineId"] = "main", ["worldDay"] = 8.25d
                };
                WorldTestHeartbeatApi(dayEightHeartbeat);
                ProcessNextWorldTestRollup();
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    WorldSimulationStore.UpdateRelationshipPairChemistry(connection);
                    ApplyWorldTestRelationshipPairDelta(connection, campaign, "main", 9,
                        new Dictionary<string, object>
                        {
                            ["pairKey"] = "a|c", ["heroAId"] = "a", ["heroBId"] = "c",
                            ["affinityAToB"] = 10, ["affinityBToA"] = 10,
                            ["tagAToB"] = "acquaintance", ["tagBToA"] = "acquaintance",
                            ["firstDay"] = 2
                        },
                        new Dictionary<string, object>
                        {
                            ["pairKey"] = "a|c", ["heroAId"] = "a", ["heroBId"] = "c",
                            ["affinityAToB"] = 35, ["affinityBToA"] = 35,
                            ["tagAToB"] = "friend", ["tagBToA"] = "friend",
                            ["firstDay"] = 2
                        }, new Dictionary<string, object>());
                }
                Dictionary<string, object> dayNineHeartbeat =
                    new Dictionary<string, object>(heartbeat, StringComparer.OrdinalIgnoreCase)
                    {
                        ["timelineId"] = "main", ["worldDay"] = 9.25d
                };
                WorldTestHeartbeatApi(dayNineHeartbeat);
                ProcessNextWorldTestRollup();
                Dictionary<string, object> changedOverview =
                    BuildWorldTestOverview(campaign, "main", double.MinValue, double.MaxValue, 100);
                Dictionary<string, object> dailyChange = ReadDictionary(
                    ReadDictionary(changedOverview, "relationships") ?? new Dictionary<string, object>(),
                    "dailyChange") ?? new Dictionary<string, object>();
                Dictionary<string, object> dailyBands = ReadDictionary(dailyChange, "bands")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> acquaintanceChange = ReadDictionary(dailyBands, "acquaintance")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> friendChange = ReadDictionary(dailyBands, "friend")
                    ?? new Dictionary<string, object>();
                bool compactChangesCorrect =
                    ReadBool(dailyChange, "available", false)
                    && ReadInt(acquaintanceChange, "directionalEdgeCount", 0) == -2
                    && ReadInt(friendChange, "directionalEdgeCount", 0) == 2;
                add("compact_daily_distribution_changes",
                    compactChangesCorrect,
                    compactChangesCorrect
                        ? "One compact rollup per day reports affinity-band and tag population changes without storing per-change telemetry."
                        : "Compact daily distribution changes disagreed: "
                            + Json.Serialize(dailyChange));
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    WorldSimulationStore.UpsertSchemaMeta7(connection, new Dictionary<string, object>
                        {
                            ["key"] = "mbti_relationship_last_processed_day:main",
                            ["day"] = 10.25d
                        });
                }
                Dictionary<string, object> advancedClockOverview =
                    BuildWorldTestOverview(campaign, "main", double.MinValue,
                        double.MaxValue, 100);
                Dictionary<string, object> advancedClocks = ReadDictionary(
                    advancedClockOverview, "clocks") ?? new Dictionary<string, object>();
                add("overview_uses_latest_authoritative_subsystem_day",
                    Math.Abs(ReadDouble(advancedClockOverview,
                        "latestObservedDay", 0d) - 10.25d) < 0.001d
                    && Math.Abs(ReadDouble(advancedClocks,
                        "nativeHeartbeatDay", 0d) - 9.25d) < 0.001d
                    && Math.Abs(ReadDouble(advancedClocks,
                        "nativeHeartbeatLagDays", 0d) - 1d) < 0.001d,
                    "The World Test header follows the newest timeline-isolated authoritative subsystem clock while exposing native-heartbeat lag separately.");
                Dictionary<string, object> lifecycleMetrics =
                    ReadDictionary(relationships, "lifecycle") ?? new Dictionary<string, object>();
                add("reign_marriage_counts",
                    ReadInt(lifecycleMetrics, "romanticMarriages", 0) == 1
                    && ReadInt(lifecycleMetrics,
                        "romanticMarriageCompletedReports", 0) == 3
                    && ReadInt(lifecycleMetrics,
                        "romanticMarriageUnconfirmedReports", -1) == 0
                    && ReadInt(lifecycleMetrics,
                        "romanticMarriageDuplicateReports", 0) == 1
                    && ReadInt(lifecycleMetrics, "arrangedMarriages", 0) == 1,
                    "The tracker counts one unique reciprocal native marriage, exposes duplicate reports, and does not call a fresh same-day completion overdue before its one-day confirmation grace expires.");
                heartbeat["timelineId"] = "branch";
                heartbeat["worldDay"] = 3d;
                WorldTestHeartbeatApi(heartbeat);
                Dictionary<string, object> main = BuildWorldTestOverview(campaign, "main", double.MinValue, double.MaxValue, 100);
                Dictionary<string, object> branch = BuildWorldTestOverview(campaign, "branch", double.MinValue, double.MaxValue, 100);
                add("timeline_isolation", Math.Abs(ReadDouble(main, "latestObservedDay", 0d) - 10.25d) < 0.001d
                    && Math.Abs(ReadDouble(branch, "latestObservedDay", 0d) - 3d) < 0.001d,
                    "Save branches retain independent observation ranges.");

                string realCampaign = "wr_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string observedCampaign = "wo_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string legacyObservedCampaign = "wl_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string internalCampaign = "__wv_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string incompleteCampaign = "wi_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                Directory.CreateDirectory(CampaignFileService.CampaignDirectory(realCampaign));
                Directory.CreateDirectory(CampaignFileService.CampaignDirectory(internalCampaign));
                Directory.CreateDirectory(CampaignFileService.CampaignDirectory(incompleteCampaign));
                CampaignFileService.WriteJsonObject(Path.Combine(CampaignFileService.CampaignDirectory(realCampaign), "campaign.json"),
                    new Dictionary<string, object> { ["campaignId"] = realCampaign, ["campaignLabel"] = "Real fixture" });
                using (ReignDbConnection realConnection =
                    ReignDatabase.OpenCampaignConnection(realCampaign))
                {
                    EnsureWorldTestSchema(realConnection);
                }
                CampaignFileService.WriteJsonObject(Path.Combine(CampaignFileService.CampaignDirectory(internalCampaign), "campaign.json"),
                    new Dictionary<string, object> { ["campaignId"] = internalCampaign, ["campaignLabel"] = "Verification fixture" });
                Dictionary<string, object> observedHeartbeat = new Dictionary<string, object>(heartbeat, StringComparer.OrdinalIgnoreCase)
                {
                    ["campaignId"] = observedCampaign,
                    ["campaignLabel"] = "Observed fixture",
                    ["mainHeroName"] = "Observed Hero"
                };
                WorldTestHeartbeatApi(observedHeartbeat);
                bool heartbeatCreatedMetadata = File.Exists(Path.Combine(CampaignFileService.CampaignDirectory(observedCampaign), "campaign.json"))
                    && ReadString(CampaignFileService.ReadJsonObject(Path.Combine(CampaignFileService.CampaignDirectory(observedCampaign), "campaign.json")), "mainHeroName", "") == "Observed Hero";
                add("heartbeat_establishes_real_campaign", heartbeatCreatedMetadata,
                    "A native World Test heartbeat makes a fresh campaign selectable before its first Save Sync registration.");
                using (ReignDbConnection observedConnection = ReignDatabase.OpenCampaignConnection(observedCampaign))
                {
                    ReputationService.EnsureRumorSchema(observedConnection);
                    WorldSimulationStore.UpsertRumorOccurrences(observedConnection, new Dictionary<string, object> { ["campaign"] = observedCampaign });
                    WorldSimulationStore.UpsertRumorSubjectTags(observedConnection);
                }
                Dictionary<string, object> receiptDetails = WorldTestDetailsApi(new Dictionary<string, string>
                {
                    ["campaignId"] = observedCampaign, ["timelineId"] = "main",
                    ["subsystem"] = "rumor subjects", ["search"] = "rumor_inspector_fixture"
                });
                List<object> receiptRows = CharactersService.ReadObjectList(receiptDetails, "rows");
                add("rumor_subject_drilldown", receiptRows.Count == 1
                    && ReadString(receiptRows[0] as Dictionary<string, object>, "subject_id", "") == "npc_observer",
                    "The read-only social inspector can load character-owned subject tags for one selected occurrence.");
                observedHeartbeat["campaignId"] = legacyObservedCampaign;
                observedHeartbeat["campaignLabel"] = "Legacy observed fixture";
                WorldTestHeartbeatApi(observedHeartbeat);
                File.Delete(Path.Combine(CampaignFileService.CampaignDirectory(legacyObservedCampaign), "campaign.json"));
                using (ReignDbConnection observedConnection = ReignDatabase.OpenCampaignConnection(observedCampaign))
                    WorldSimulationStore.UpdateWorldTestNativeHeartbeats(observedConnection);
                Dictionary<string, object> worldTestCampaigns = WorldTestCampaignsApi(new Dictionary<string, string>());
                Dictionary<string, object> backupCampaigns = PersistenceService.CampaignListApi();
                List<object> worldTestRows = CharactersService.ReadObjectList(worldTestCampaigns, "campaigns");
                List<object> backupRows = CharactersService.ReadObjectList(backupCampaigns, "campaigns");
                bool legacyPromoted = File.Exists(Path.Combine(CampaignFileService.CampaignDirectory(legacyObservedCampaign), "campaign.json"));
                add("registry_campaign_without_local_metadata_is_isolated", !legacyPromoted,
                    "Campaign discovery never imports a PostgreSQL registry entry from another local Reign data root.");
                HashSet<string> expectedRealCampaigns = new HashSet<string>(
                    new[] { campaign, realCampaign, observedCampaign }, StringComparer.OrdinalIgnoreCase);
                bool alignedFiltering = worldTestRows.Count == 3 && backupRows.Count == 3
                    && expectedRealCampaigns.SetEquals(worldTestRows.Select(x => ReadString(x as Dictionary<string, object>, "campaignId", "")))
                    && expectedRealCampaigns.SetEquals(backupRows.Select(x => ReadString(x as Dictionary<string, object>, "campaignId", "")))
                    && ReadString(worldTestCampaigns, "latestCampaignId", "") == observedCampaign;
                add("real_campaign_filtering", alignedFiltering,
                    alignedFiltering
                        ? "World Test and Campaign Backups expose only authoritative real campaigns, and latest selection follows the newest native heartbeat rather than directory writes."
                        : "Campaign filtering disagreed: " + Json.Serialize(new Dictionary<string, object>
                        {
                            ["worldTest"] = worldTestRows, ["backups"] = backupRows, ["latest"] = SettingsService.LatestCampaignId()
                        }));
            }
            finally
            {
                try
                {
                    foreach (Dictionary<string, object> registered
                        in ReignPostgreSqlStorage.ListCampaignMetadata())
                    {
                        string registeredId = ReadString(
                            registered, "campaignId", "");
                        string registeredDirectory =
                            CampaignFileService.CampaignDirectory(registeredId);
                        if (Directory.Exists(registeredDirectory)
                            && Path.GetFullPath(registeredDirectory)
                                .StartsWith(
                                    Path.GetFullPath(isolatedCampaignsRoot)
                                        + Path.DirectorySeparatorChar,
                                    StringComparison.OrdinalIgnoreCase))
                        {
                            ReignPostgreSqlStorage.DropCampaign(
                                registeredId);
                        }
                    }
                }
                catch { }
                ReignPostgreSqlStorage.ClearAllPools();
                Program.CampaignsRootOverride.Value = previousCampaignsRoot;
                PersistenceService.TryDeleteDirectory(isolatedCampaignsRoot);
            }
            return results;
        }
#endif
    }
}
