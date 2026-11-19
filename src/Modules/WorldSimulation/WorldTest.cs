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
        internal static readonly string[] WorldTestRelationshipBands =
        {
            "nemesis", "enemy", "rival", "irritant", "neutral",
            "acquaintance", "friend", "close_friend", "devoted", "bonded"
        };
        private static long WorldTestPoliticalRosterTotalMs;
        private static long WorldTestPoliticalRosterLockWaitMs;
        private static long WorldTestPoliticalRosterIdentityMs;
        private static long WorldTestPoliticalRosterStandingMs;
        private static long WorldTestPoliticalRosterNetworkMs;
        private static int WorldTestPoliticalRosterLeaderCount;
        private static int WorldTestPoliticalRosterChanged;
        private static long WorldTestPoliticalRosterCompletedTs;
        private static readonly object WorldTestOverviewCacheGate = new object();
        private static readonly Dictionary<string, Tuple<long,
            Dictionary<string, object>>> WorldTestOverviewCache =
            new Dictionary<string, Tuple<long, Dictionary<string, object>>>(
                StringComparer.OrdinalIgnoreCase);
        private const int WorldTestOverviewCacheSeconds = 30;

        internal static void EnsureWorldTestSchema(ReignDbConnection connection)
        {
            WorldSimulationSchema.CreateWorldTestNativeHeartbeats(connection);
            WorldSimulationSchema.CreateIndexWorldTestHeartbeatDay(connection);
            WorldSimulationSchema.CreateWorldTestDailyRollups(connection);
            WorldSimulationSchema.CreateIndexWorldTestRollupDay(connection);
            WorldSimulationSchema.CreateWorldTestRebellionRolls(connection);
            WorldSimulationSchema.EnsureColumnWorldTestRebellionRollsExclusionReason(connection);
            WorldSimulationSchema.EnsureColumnWorldTestRebellionRollsPersonalAffinityToRuler(connection);
            WorldSimulationSchema.EnsureColumnWorldTestRebellionRollsRulerPublicStanding(connection);
            WorldSimulationSchema.EnsureColumnWorldTestRebellionRollsRulerStandingRevision(connection);
            WorldSimulationSchema.CreateWorldTestRebellionMovements(connection);
            WorldSimulationSchema.CreateWorldTestRebellionMemberships(connection);
            DiplomacyService.EnsureKingdomLeaderDiplomacySchema(connection);
            DiplomacyService.EnsurePoliticalPressureSchema(connection);
        }

        internal static Dictionary<string, object> WorldTestHeartbeatApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", SettingsService.LatestCampaignId());
            string timelineId = ReadString(payload, "timelineId", "main");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            int dayKey = (int)Math.Floor(worldDay);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, object> population = ReadDictionary(payload, "population") ?? new Dictionary<string, object>();
            if (!CampaignFileService.IsInternalCampaignId(campaignId))
            {
                CampaignFileService.WriteCampaignMetadata(campaignId, payload);
            }
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureWorldTestSchema(connection);
                RelationshipsService.EnsureMbtiRelationshipSchema(connection);
                RelationshipsService.EnsureRelationshipDirectorSchema(connection);
                ReputationService.EnsureRumorSchema(connection);
                RelationshipsService.EnsureClanConflictSchema(connection);
                EnsureWorldTestTelemetrySchema(connection);
                ReconcileHeartbeatPoliticalRoster(connection, campaignId, timelineId,
                    worldDay, payload);
                DiplomacyService.ProcessDailyRulerRelationships(connection, campaignId, timelineId,
                    worldDay, payload);
                WorldSimulationStore.Begin(connection);
                try
                {
                Dictionary<string, object> rebellion = ReadDictionary(payload, "rebellions") ?? new Dictionary<string, object>();
                foreach (Dictionary<string, object> row in ReadDictionaryList(rebellion, "weeklyRolls"))
                {
                    int rollDay = (int)Math.Floor(ReadDouble(row, "rolledDay", worldDay));
                    Dictionary<string, object> rollCounters =
                        BuildWorldTestRebellionRollCounters(
                            ReadBool(row, "wasEligible", false),
                            ReadBool(row, "triggered", false),
                            ReadInt(row, "rulerRelation", 0),
                            ReadBool(row, "isPlayerClan", false),
                            ReadString(row, "exclusionReason", ""));
                    RecordWorldTestCounter(connection, campaignId, timelineId, rollDay, "rebellions",
                        "weekly_" + ReadString(row, "kingdomId", "") + "_"
                            + ReadString(row, "clanId", "") + "_"
                            + ReadInt(row, "weekIndex", -1).ToString(CultureInfo.InvariantCulture),
                        rollCounters);
                    WorldSimulationStore.UpsertWorldTestRebellionRolls(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["kingdom"] = ReadString(row, "kingdomId", ""),
                            ["clan"] = ReadString(row, "clanId", ""), ["week"] = ReadInt(row, "weekIndex", -1),
                            ["day"] = ReadDouble(row, "rolledDay", worldDay), ["eligible"] = ReadBool(row, "wasEligible", false) ? 1 : 0,
                            ["triggered"] = ReadBool(row, "triggered", false) ? 1 : 0, ["roll"] = ReadInt(row, "roll", 0),
                            ["relation"] = ReadInt(row, "rulerRelation", 0), ["player"] = ReadBool(row, "isPlayerClan", false) ? 1 : 0,
                            ["exclusion"] = ReadString(row, "exclusionReason", ""),
                            ["personal"] = ReadInt(row, "personalAffinityToRuler", 0),
                            ["standing"] = ReadInt(row, "rulerPublicStanding", 0),
                            ["standingRevision"] = ReadInt(row,
                                "rulerStandingRevision", 0),
                            ["payload"] = Json.Serialize(row), ["ts"] = now
                        });
                }
                foreach (Dictionary<string, object> row in ReadDictionaryList(rebellion, "movements"))
                {
                    string movementId = ReadString(row, "movementId", "");
                    Dictionary<string, object> priorMovement = WorldSimulationStore.SelectWorldTestRebellionMovements2(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["id"] = movementId
                        }).FirstOrDefault();
                    string nextStage = ReadString(row, "stage", "");
                    bool nextResolved = ReadBool(row, "resolutionApplied", false);
                    if (priorMovement == null
                        || !ReadString(priorMovement, "stage", "").Equals(nextStage, StringComparison.OrdinalIgnoreCase)
                        || (ReadInt(priorMovement, "resolution_applied", 0) == 0 && nextResolved))
                    {
                        Dictionary<string, object> movementCounters = new Dictionary<string, object>
                        {
                            ["outbreaks"] = priorMovement == null ? 1 : 0,
                            ["resolved"] = nextResolved ? 1 : 0
                        };
                        IncrementWorldTestObjectCounter(movementCounters,
                            "stage:" + FirstNonEmpty(nextStage, "unknown"), 1);
                        IncrementWorldTestObjectCounter(movementCounters,
                            "kingdom:" + FirstNonEmpty(ReadString(row, "parentKingdomId", ""), "unknown"), 1);
                        RecordWorldTestCounter(connection, campaignId, timelineId,
                            (int)Math.Floor(ReadDouble(row, "updatedDay", worldDay)), "rebellions",
                            "movement_" + movementId + "_" + FirstNonEmpty(nextStage, "unknown"),
                            movementCounters);
                    }
                    WorldSimulationStore.UpsertWorldTestRebellionMovements(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["id"] = ReadString(row, "movementId", ""),
                            ["day"] = ReadDouble(row, "updatedDay", worldDay), ["stage"] = ReadString(row, "stage", ""),
                            ["resolved"] = ReadBool(row, "resolutionApplied", false) ? 1 : 0, ["payload"] = Json.Serialize(row), ["ts"] = now
                        });
                }
                foreach (Dictionary<string, object> row in ReadDictionaryList(rebellion, "memberships"))
                {
                    WorldSimulationStore.UpsertWorldTestRebellionMemberships(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["movement"] = ReadString(row, "movementId", ""),
                            ["clan"] = ReadString(row, "clanId", ""), ["side"] = ReadString(row, "side", ""),
                            ["payload"] = Json.Serialize(row), ["ts"] = now
                        });
                }
                Dictionary<string, object> nativeOutcomes =
                    ReadDictionary(payload, "nativeOutcomes") ?? new Dictionary<string, object>();
                foreach (Dictionary<string, object> row in ReadDictionaryList(nativeOutcomes, "actions")
                    .Where(x => ReadString(x, "source", "")
                        .Equals("world_diplomacy_director", StringComparison.OrdinalIgnoreCase)))
                {
                    string actionId = ReadString(row, "actionId", "");
                    if (string.IsNullOrWhiteSpace(actionId)) continue;
                    string actionStatus = ActionPipelineService.NormalizeLookup(ReadString(row, "status", "unknown"));
                    Dictionary<string, object> actionCounters = new Dictionary<string, object>
                    {
                        ["actionStatus:" + FirstNonEmpty(actionStatus, "unknown")] = 1,
                        ["actionType:" + FirstNonEmpty(ActionPipelineService.NormalizeLookup(ReadString(row, "type", "")), "unknown")] = 1,
                        ["actorKingdom:" + FirstNonEmpty(ReadString(row, "actorKingdomId", ""), "unknown")] = 1,
                        ["targetKingdom:" + FirstNonEmpty(ReadString(row, "targetKingdomId", ""), "unknown")] = 1,
                        ["actionQueued"] = actionStatus == "proposed" || actionStatus == "accepted"
                            || actionStatus == "pending" ? 1 : 0,
                        ["actionCompleted"] = actionStatus == "completed" ? 1 : 0,
                        ["actionFailed"] = IsTerminalFailureStatus(actionStatus) ? 1 : 0
                    };
                    RecordWorldTestCounter(connection, campaignId, timelineId,
                        (int)Math.Floor(ReadDouble(row, "createdDay", worldDay)), "diplomacy",
                        "native_action_" + actionId
                            + "_" + FirstNonEmpty(actionStatus, "unknown"),
                        actionCounters);
                }
                Dictionary<string, object> storedPayload = TryParseJsonObject(Json.Serialize(payload)) ?? payload;
                Dictionary<string, object> manifest =
                    ReadDictionary(payload, "manifest") ?? new Dictionary<string, object>();
                manifest["serverAssemblyVersion"] =
                    typeof(Program).Assembly.GetName().Version?.ToString() ?? string.Empty;
                manifest["rumorCatalogRevision"] =
                    ReadInt(ReputationService.ReadActiveSocialCatalog(connection), "revision", 0);
                WorldSimulationStore.UpsertWorldTestManifests(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["manifest"] = Json.Serialize(manifest), ["day"] = worldDay, ["ts"] = now
                    });
                storedPayload["rebellions"] = new Dictionary<string, object>
                {
                    ["weeklyRollDeltaCount"] = ReadDictionaryList(rebellion, "weeklyRolls").Count,
                    ["movementDeltaCount"] = ReadDictionaryList(rebellion, "movements").Count,
                    ["membershipDeltaCount"] = ReadDictionaryList(rebellion, "memberships").Count
                };
                WorldSimulationStore.UpsertWorldTestNativeHeartbeats(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId, ["key"] = dayKey, ["day"] = worldDay,
                        ["npcs"] = ReadInt(population, "eligibleNpcCount", 0), ["kingdoms"] = ReadInt(population, "kingdomCount", 0),
                        ["payload"] = Json.Serialize(storedPayload), ["ts"] = now
                    });
                EnqueueWorldTestRollup(connection, campaignId, timelineId, dayKey, "native_heartbeat");
                Dictionary<string, object> response = new Dictionary<string, object>
                {
                    ["ok"] = true, ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                    ["worldDay"] = worldDay, ["dayKey"] = dayKey, ["rollupStatus"] = "queued",
                    ["idempotentKey"] = campaignId + "|" + timelineId + "|" + dayKey.ToString(CultureInfo.InvariantCulture),
                    ["politicalRosterReconciliation"] =
                        WorldTestPoliticalRosterTimingStatus()
                };
                WorldSimulationStore.Commit(connection);
                return response;
                }
                catch
                {
                    try { WorldSimulationStore.Rollback(connection); } catch { }
                    throw;
                }
            }
        }

        private static void ReconcileHeartbeatPoliticalRoster(
            ReignDbConnection connection, string campaignId, string timelineId,
            double worldDay, Dictionary<string, object> payload)
        {
            Stopwatch totalTimer = Stopwatch.StartNew();
            List<Dictionary<string, object>> leaders =
                ReadDictionaryList(payload, "politicalLeaders");
            string fingerprint = ReadString(payload,
                "politicalRosterFingerprint", "");
            WorldTestPoliticalRosterLeaderCount = leaders.Count;
            if (leaders.Count == 0 || string.IsNullOrWhiteSpace(fingerprint))
            {
                RecordWorldTestPoliticalRosterTiming(totalTimer, 0, 0, 0,
                    0, false);
                return;
            }
            CharactersService.EnsureIdentitySchema(connection);
            RelationshipsService.EnsureMbtiRelationshipSchema(connection);
            RelationshipsService.EnsureWorldRelationshipSchema(connection);
            string fingerprintKey = "political_roster_fingerprint:"
                + timelineId;
            string previous = ReadString(WorldSimulationStore.SelectSchemaMeta7(connection, new Dictionary<string, object> { ["key"] = fingerprintKey })
                .FirstOrDefault(), "value", "");
            Dictionary<string, List<Dictionary<string, object>>>
                expectedPolitical = RelationshipsService.BuildRequiredPoliticalPairDefinitions(leaders);
            int expectedKingdomPairs = expectedPolitical.Values.Count(items =>
                items.Any(item => ReadString(item, "source", "").Equals(
                    "kingdom_leadership", StringComparison.OrdinalIgnoreCase)));
            int expectedRulerPairs = expectedPolitical.Values.Count(items =>
                items.Any(item => ReadString(item, "source", "").Equals(
                    "ruler_network", StringComparison.OrdinalIgnoreCase)));
            Dictionary<string, object> storedPolitical = WorldSimulationStore.SelectRelationshipPairProvenance(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            int expectedClanLeaders = leaders.Count(hero =>
                ReadBool(hero, "isClanLeader", false));
            int storedClanLeaders = ReadInt(WorldSimulationStore.CountIdentityRoster(connection)
                .FirstOrDefault(), "count", 0);
            bool topologyComplete = storedClanLeaders == expectedClanLeaders
                && ReadInt(storedPolitical, "kingdom_pairs", 0)
                    == expectedKingdomPairs
                && ReadInt(storedPolitical, "ruler_pairs", 0)
                    == expectedRulerPairs;
            if (previous.Equals(fingerprint, StringComparison.Ordinal)
                && topologyComplete)
            {
                RecordWorldTestPoliticalRosterTiming(totalTimer, 0, 0, 0,
                    0, false);
                return;
            }
            Stopwatch lockWaitTimer = Stopwatch.StartNew();
            lock (RelationshipsService.CampaignRelationshipWriteLock(campaignId))
            {
                lockWaitTimer.Stop();
                WorldSimulationStore.Begin(connection);
                try
                {
                    WorldSimulationStore.UpdateIdentityRoster(connection);
                    long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    Stopwatch identityTimer = Stopwatch.StartNew();
                    CharactersService.UpsertIdentityRosterHeroesBatch(connection, leaders, ts);
                    identityTimer.Stop();
                    WorldSimulationStore.UpsertSchemaMeta8(connection, new Dictionary<string, object>
                        {
                            ["key"] = fingerprintKey, ["value"] = fingerprint
                        });
                    Stopwatch standingTimer = Stopwatch.StartNew();
                    // A leadership change does not change rumor, reputation, or
                    // Charm inputs. Ensure rows for newly observed leaders without
                    // recalculating every already-current Public Standing record.
                    ReputationService.EnsurePublicStandingForRoster(connection, campaignId,
                        timelineId, worldDay, false);
                    standingTimer.Stop();
                    Stopwatch networkTimer = Stopwatch.StartNew();
                    RelationshipsService.ReconcilePoliticalRelationshipNetwork(connection, campaignId,
                        timelineId, worldDay, "political_heartbeat_changed");
                    networkTimer.Stop();
                    WorldSimulationStore.Commit(connection);
                    RecordWorldTestPoliticalRosterTiming(totalTimer,
                        lockWaitTimer.ElapsedMilliseconds,
                        identityTimer.ElapsedMilliseconds,
                        standingTimer.ElapsedMilliseconds,
                        networkTimer.ElapsedMilliseconds, true);
                }
                catch
                {
                    try { WorldSimulationStore.Rollback(connection); } catch { }
                    throw;
                }
            }
        }

        private static void RecordWorldTestPoliticalRosterTiming(
            Stopwatch totalTimer, long lockWaitMs, long identityMs,
            long standingMs, long networkMs, bool changed)
        {
            totalTimer.Stop();
            WorldTestPoliticalRosterTotalMs = totalTimer.ElapsedMilliseconds;
            WorldTestPoliticalRosterLockWaitMs = Math.Max(0, lockWaitMs);
            WorldTestPoliticalRosterIdentityMs = Math.Max(0, identityMs);
            WorldTestPoliticalRosterStandingMs = Math.Max(0, standingMs);
            WorldTestPoliticalRosterNetworkMs = Math.Max(0, networkMs);
            WorldTestPoliticalRosterChanged = changed ? 1 : 0;
            WorldTestPoliticalRosterCompletedTs =
                DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private static Dictionary<string, object>
            WorldTestPoliticalRosterTimingStatus()
        {
            return new Dictionary<string, object>
            {
                ["changed"] = WorldTestPoliticalRosterChanged != 0,
                ["leaderCount"] = WorldTestPoliticalRosterLeaderCount,
                ["totalMs"] = WorldTestPoliticalRosterTotalMs,
                ["campaignLockWaitMs"] =
                    WorldTestPoliticalRosterLockWaitMs,
                ["identityBatchMs"] =
                    WorldTestPoliticalRosterIdentityMs,
                ["standingEnsureMs"] =
                    WorldTestPoliticalRosterStandingMs,
                ["politicalNetworkMs"] =
                    WorldTestPoliticalRosterNetworkMs,
                ["completedTs"] = WorldTestPoliticalRosterCompletedTs
            };
        }

        internal static Dictionary<string, object> WorldTestCampaignsApi(Dictionary<string, string> query)
        {
            List<Dictionary<string, object>> campaigns = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> registered
                in ReignPostgreSqlStorage.ListCampaignMetadata())
            {
                string campaignId = ReadString(
                    registered, "campaignId", "");
                string directoryPath = CampaignFileService.CampaignDirectory(campaignId);
                if (!Directory.Exists(directoryPath)) continue;
                DirectoryInfo directory = new DirectoryInfo(directoryPath);
                if (!CampaignFileService.IsRealCampaignDirectory(directory)) continue;
                Dictionary<string, object> metadata = CampaignFileService.ReadJsonObject(
                    Path.Combine(directoryPath, "campaign.json"));
                List<Dictionary<string, object>> timelines =
                    new List<Dictionary<string, object>>();
                try
                {
                    using (ReignDbConnection connection =
                        ReignDatabase.OpenCampaignConnection(campaignId))
                    {
                        EnsureWorldTestSchema(connection);
                        timelines = WorldSimulationStore.SelectWorldTestNativeHeartbeats3(connection);
                    }
                }
                catch { }
                long latestTelemetryUpdatedTs = timelines.Count == 0
                    ? 0L
                    : timelines.Max(
                        x => ReadLong(x, "updated_ts", 0L));
                string directoryUpdatedUtc = directory == null
                    ? ReadString(registered, "updatedUtc", "")
                    : directory.LastWriteTimeUtc.ToString("o");
                campaigns.Add(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["label"] = ReadString(
                        metadata, "campaignLabel",
                        ReadString(metadata, "mainHeroName", campaignId)),
                    ["mainHeroName"] =
                        ReadString(metadata, "mainHeroName", ""),
                    ["updatedUtc"] = latestTelemetryUpdatedTs > 0
                        ? UnixToIso(latestTelemetryUpdatedTs)
                        : directoryUpdatedUtc,
                    ["lastTelemetryUpdatedTs"] =
                        latestTelemetryUpdatedTs,
                    ["directoryUpdatedUtc"] = directoryUpdatedUtc,
                    ["databaseProvider"] = "postgresql",
                    ["databaseBytes"] =
                        ReadLong(registered, "databaseBytes", 0),
                    ["hasTelemetry"] = timelines.Count > 0,
                    ["timelines"] = timelines
                });
            }
            campaigns = campaigns
                .OrderByDescending(x => ReadLong(x, "lastTelemetryUpdatedTs", 0L))
                .ThenByDescending(x => ReadString(x, "directoryUpdatedUtc", ""), StringComparer.OrdinalIgnoreCase)
                .ToList();
            string latestCampaignId = ReadString(campaigns.FirstOrDefault(), "campaignId", "default");
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["latestCampaignId"] = latestCampaignId, ["campaigns"] = campaigns
            };
        }

        internal static Dictionary<string, object> WorldTestOverviewApi(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>();
            string campaignId = query.TryGetValue("campaignId", out string campaign) ? campaign : SettingsService.LatestCampaignId();
            if (!CampaignFileService.HasRealCampaignStorage(campaignId))
                return new Dictionary<string, object>
                {
                    ["ok"] = false, ["campaignId"] = campaignId,
                    ["error"] = "No observed real campaign matches this World Test selection."
                };
            string timelineId = query.TryGetValue("timelineId", out string timeline) ? timeline : "main";
            if (query.TryGetValue("view", out string view) && view == "readiness")
                return BuildWorldTestReadiness(campaignId, timelineId);
            double fromDay = query.TryGetValue("fromDay", out string fromText) && double.TryParse(fromText, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedFrom) ? parsedFrom : double.MinValue;
            double toDay = query.TryGetValue("toDay", out string toText) && double.TryParse(toText, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedTo) ? parsedTo : double.MaxValue;
            int limit = query.TryGetValue("limit", out string limitText) && int.TryParse(limitText, out int parsedLimit) ? Math.Max(10, Math.Min(500, parsedLimit)) : 100;
            return BuildWorldTestOverview(campaignId, timelineId, fromDay, toDay, limit);
        }

        internal static Dictionary<string, object> WorldTestDetailsApi(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>();
            string campaignId = query.TryGetValue("campaignId", out string campaign) ? campaign : SettingsService.LatestCampaignId();
            if (!CampaignFileService.HasRealCampaignStorage(campaignId))
                return new Dictionary<string, object>
                {
                    ["ok"] = false, ["campaignId"] = campaignId, ["rows"] = new List<Dictionary<string, object>>(), ["total"] = 0,
                    ["error"] = "No observed real campaign matches this World Test selection."
                };
            string timelineId = query.TryGetValue("timelineId", out string timeline) ? timeline : "main";
            string subsystem = query.TryGetValue("subsystem", out string system) ? ActionPipelineService.NormalizeLookup(system) : "relationships";
            string search = query.TryGetValue("search", out string searchValue) ? searchValue : "";
            string status = query.TryGetValue("status", out string statusValue) ? statusValue : "";
            string type = query.TryGetValue("type", out string typeValue) ? typeValue : "";
            string mainHeroId = ReadFirstString(
                CampaignFileService.ReadJsonObject(CampaignFileService.CampaignFile(campaignId, "campaign.json")),
                "mainHeroStringId", "mainHeroId");
            int page = query.TryGetValue("page", out string pageText) && int.TryParse(pageText, out int parsedPage) ? Math.Max(1, parsedPage) : 1;
            int pageSize = query.TryGetValue("pageSize", out string sizeText) && int.TryParse(sizeText, out int parsedSize) ? Math.Max(10, Math.Min(200, parsedSize)) : 50;
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureWorldTestSchema(connection);
                RelationshipsService.EnsureMbtiRelationshipSchema(connection);
                RelationshipsService.EnsureRelationshipDirectorSchema(connection);
                ReputationService.EnsureRumorSchema(connection);
                if (subsystem == "relationships")
                    rows = WorldSimulationStore.SelectRelationshipPairChemistry3(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId,
                            ["timeline"] = timelineId
                        });
                else if (subsystem == "relationship pair")
                    rows.Add(ReadWorldTestRelationshipPair(connection, campaignId, timelineId,
                        query.TryGetValue("pair", out string exactPair) ? exactPair : ""));
                else if (subsystem == "rumors")
                    rows = WorldSimulationStore.SelectRumorOccurrences4(connection);
                else if (subsystem == "rumor subjects")
                {
                    rows = string.IsNullOrWhiteSpace(search)
                        ? new List<Dictionary<string, object>>()
                        : WorldSimulationStore.SelectRumorSubjectTags2(connection, new Dictionary<string, object> { ["id"] = search });
                    search = string.Empty;
                }
                else if (subsystem == "rumor funnel")
                {
                    rows = WorldSimulationStore.SelectWorldTestChunkCounters3(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId,
                            ["timeline"] = timelineId
                        });
                }
                else if (subsystem == "rebellions")
                    rows = LatestNativeArray(connection, campaignId, timelineId, "rebellions", "movements");
                else if (subsystem == "diplomacy")
                    rows = ReadWorldTestDiplomacyEvents(campaignId).OrderByDescending(x => ReadDouble(x, "worldDay", 0d)).ToList();
                else if (subsystem == "kingdom leaders")
                    rows = DiplomacyService.QueryKingdomLeaderActivity(connection, campaignId,
                        timelineId, query, 1000);
                else if (subsystem == "political pressures")
                    rows = DiplomacyService.QueryPoliticalPressureActivity(connection, campaignId,
                        timelineId, query, 1000);
                else if (subsystem == "clan conflicts")
                {
                    rows = WorldSimulationStore.SelectClanConflictIncidents(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId
                        });
                    foreach (Dictionary<string, object> row in rows)
                    {
                        row["participantsA"] = RelationshipsService.ReadJsonStringList(ReadString(row,
                            "involved_a_names_json", "[]")).Cast<object>().ToList();
                        row["participantsB"] = RelationshipsService.ReadJsonStringList(ReadString(row,
                            "involved_b_names_json", "[]")).Cast<object>().ToList();
                        row["votes"] = ReadDictionaryList(TryParseJsonObject("{\"votes\":"
                            + ReadString(row, "trait_votes_json", "[]") + "}"), "votes")
                            .Cast<object>().ToList();
                        row["effects"] = WorldSimulationStore.SelectClanConflictRelationshipEffects(connection, new Dictionary<string, object>
                            {
                                ["incident"] = ReadString(row, "incident_id", "")
                            });
                    }
                }
                else if (subsystem == "actions")
                {
                    rows = ReadWorldTestActions(campaignId);
                    rows.AddRange(WorldSimulationStore.SelectRelationshipNativeTargets2(connection));
                }
                else
                    rows = WorldSimulationStore.SelectWorldTestDailyRollups2(connection, new Dictionary<string, object> { ["timeline"] = timelineId });
            }
            if (subsystem == "relationships" && !string.IsNullOrWhiteSpace(mainHeroId))
                rows = rows.Where(row =>
                    !ReadString(row, "hero_a_id", "").Equals(mainHeroId, StringComparison.OrdinalIgnoreCase)
                    && !ReadString(row, "hero_b_id", "").Equals(mainHeroId, StringComparison.OrdinalIgnoreCase)).ToList();
            IEnumerable<Dictionary<string, object>> filtered = rows;
            if (!string.IsNullOrWhiteSpace(search))
                filtered = filtered.Where(x => Json.Serialize(x).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrWhiteSpace(status))
                filtered = filtered.Where(x => ReadFirstString(x, "status", "overall_status", "stage").IndexOf(status, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrWhiteSpace(type))
                filtered = filtered.Where(x => ReadFirstString(x, "kind", "command", "action_type", "event_type", "polarity", "channel", "tag_a_to_b", "source").IndexOf(type, StringComparison.OrdinalIgnoreCase) >= 0);
            List<Dictionary<string, object>> all = filtered.ToList();
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["subsystem"] = subsystem,
                ["page"] = page, ["pageSize"] = pageSize, ["total"] = all.Count,
                ["rows"] = all.Skip((page - 1) * pageSize).Take(pageSize).ToList()
            };
        }

        private static Dictionary<string, object> ReadWorldTestRelationshipPair(
            ReignDbConnection connection, string campaignId, string timelineId, string requestedPair)
        {
            string[] ids = (requestedPair ?? "").Split('|');
            if (requestedPair == null || requestedPair.Length > 200 || ids.Length != 2
                || ids.Any(string.IsNullOrWhiteSpace)
                || ids[0].Equals(ids[1], StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Specify two distinct hero identifiers as heroA|heroB.");
            string pairKey = RelationshipsService.AmbientPairKey(ids[0], ids[1]);
            var parameters = new Dictionary<string, object>
            {
                ["campaign"] = campaignId, ["timeline"] = timelineId,
                ["a"] = ids[0], ["b"] = ids[1], ["pair"] = pairKey
            };
            var pair = WorldSimulationStore.SelectRelationshipPairChemistry4(connection, parameters).FirstOrDefault();
            var result = new Dictionary<string, object>
            {
                ["schema"] = "reign-relationship-pair-observation-v1",
                ["pairKey"] = pairKey, ["hasPair"] = pair != null, ["pair"] = pair,
                ["firstToSecond"] = RelationshipsService.ResolveEffectiveAttitude(connection, campaignId, timelineId, ids[0], ids[1], "world_test_pair_observation"),
                ["secondToFirst"] = RelationshipsService.ResolveEffectiveAttitude(connection, campaignId, timelineId, ids[1], ids[0], "world_test_pair_observation")
            };
            result["familyAttention"] = TableExists(connection, "court_family_attention")
                ? WorldSimulationStore.SelectCourtFamilyAttention(connection, parameters)
                : new List<Dictionary<string, object>>();
            result["favorContact"] = TableExists(connection, "court_ruler_favor_contact")
                ? WorldSimulationStore.SelectCourtRulerFavorContact2(connection, parameters)
                : new List<Dictionary<string, object>>();
            result["favorReputations"] = TableExists(connection, "character_reputations")
                ? WorldSimulationStore.SelectCharacterReputations3(connection, parameters)
                    .Where(row =>
                    {
                        string linked = ReadString(TryParseJsonObject(ReadString(row, "snapshot_json", "{}")), "linkedHeroId", "");
                        return ReadString(row, "subject_id", "") == ids[0] ? linked == ids[1] : linked == ids[0];
                    }).ToList()
                : new List<Dictionary<string, object>>();
            // Observing never records contact, expires favor, changes affinity, or creates a relationship pair.
            return result;
        }

        internal static void InvalidateWorldTestOverviewCache(string campaignId,
            string timelineId)
        {
            string prefix = (campaignId ?? "") + "|"
                + (timelineId ?? "main") + "|";
            lock (WorldTestOverviewCacheGate)
            {
                foreach (string key in WorldTestOverviewCache.Keys
                    .Where(value => value.StartsWith(prefix,
                        StringComparison.OrdinalIgnoreCase)).ToList())
                    WorldTestOverviewCache.Remove(key);
            }
        }

        private static Dictionary<string, object> BuildWorldTestOverview(
            string campaignId, string timelineId, double fromDay,
            double toDay, int limit)
        {
            // Verification fixtures mutate their isolated databases directly;
            // production mutations invalidate this cache through the telemetry path.
            if (!string.IsNullOrWhiteSpace(Program.CampaignsRootOverride.Value))
                return BuildWorldTestOverviewUncached(campaignId, timelineId,
                    fromDay, toDay, limit);
            string cacheKey = campaignId + "|" + timelineId + "|"
                + fromDay.ToString("R", CultureInfo.InvariantCulture) + "|"
                + toDay.ToString("R", CultureInfo.InvariantCulture) + "|"
                + limit.ToString(CultureInfo.InvariantCulture);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            lock (WorldTestOverviewCacheGate)
            {
                if (WorldTestOverviewCache.TryGetValue(cacheKey,
                    out Tuple<long, Dictionary<string, object>> cached)
                    && now - cached.Item1 <= WorldTestOverviewCacheSeconds)
                    return cached.Item2;
            }
            Dictionary<string, object> overview = BuildWorldTestOverviewUncached(
                campaignId, timelineId, fromDay, toDay, limit);
            lock (WorldTestOverviewCacheGate)
                WorldTestOverviewCache[cacheKey] = Tuple.Create(now, overview);
            return overview;
        }

        private static Dictionary<string, object> BuildWorldTestOverviewUncached(string campaignId, string timelineId, double fromDay, double toDay, int limit)
        {
            Dictionary<string, object> metadata = CampaignFileService.ReadJsonObject(CampaignFileService.CampaignFile(campaignId, "campaign.json"));
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureWorldTestSchema(connection);
                RelationshipsService.EnsureMbtiRelationshipSchema(connection);
                RelationshipsService.EnsureRelationshipDirectorSchema(connection);
                ReputationService.EnsureRumorSchema(connection);
                // Remain safe when reading an old Save Sync snapshot whose
                // campaign schema predates eager rebellion initialization.
                RebellionService.EnsureRebellionSchema(connection);
                Dictionary<string, object> latestHeartbeat = WorldSimulationStore.SelectWorldTestNativeHeartbeats4(connection, new Dictionary<string, object> { ["timeline"] = timelineId }).FirstOrDefault() ?? new Dictionary<string, object>();
                Dictionary<string, object> firstHeartbeat = WorldSimulationStore.SelectWorldTestNativeHeartbeats5(connection, new Dictionary<string, object> { ["timeline"] = timelineId }).FirstOrDefault() ?? new Dictionary<string, object>();
                Dictionary<string, object> nativePayload = TryParseJsonObject(ReadString(latestHeartbeat, "payload_json", "{}")) ?? new Dictionary<string, object>();
                Dictionary<string, object> manifestRow = WorldSimulationStore.SelectWorldTestManifests(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    }).FirstOrDefault();
                Dictionary<string, object> manifest =
                    TryParseJsonObject(ReadString(manifestRow, "manifest_json", "{}"))
                    ?? new Dictionary<string, object>();
                double nativeHeartbeatDay = ReadDouble(latestHeartbeat, "world_day", -1d);
                double latestDay = Math.Max(nativeHeartbeatDay,
                    LatestKnownWorldDay(connection, campaignId, timelineId));
                if (latestDay < 0d) latestDay = 0d;
                double firstObservedDay = ReadDouble(firstHeartbeat, "world_day", latestDay);
                double elapsedObservedDays = Math.Max(0d, latestDay - firstObservedDay);
                string mainHeroId = ReadFirstString(metadata, "mainHeroStringId", "mainHeroId");
                Dictionary<string, object> relationships = BuildWorldTestRelationships(
                    connection, campaignId, timelineId, latestDay, mainHeroId);
                Dictionary<string, object> kingdomLeaders =
                    DiplomacyService.BuildWorldTestKingdomLeaders(connection, campaignId,
                        timelineId, latestDay, nativePayload);
                Dictionary<string, object> politicalPressures =
                    DiplomacyService.BuildWorldTestPoliticalPressures(connection, campaignId,
                        timelineId, latestDay);
                Dictionary<string, object> clanConflicts =
                    RelationshipsService.BuildWorldTestClanConflicts(connection, campaignId,
                        timelineId, latestDay);
                Dictionary<string, object> diplomacy = BuildWorldTestDiplomacy(
                    connection, campaignId, timelineId, latestDay,
                    elapsedObservedDays, nativePayload);
                Dictionary<string, object> rumors = BuildWorldTestRumors(
                    connection, campaignId, timelineId, latestDay);
                Dictionary<string, object> rebellions = BuildWorldTestRebellions(connection, campaignId, timelineId, latestDay);
                Dictionary<string, object> warEconomy = BuildWorldTestWarEconomy(nativePayload, latestDay);
                Dictionary<string, object> pipeline = BuildWorldTestPipeline(connection, campaignId, timelineId, latestDay, nativePayload);
                List<Dictionary<string, object>> systems = new[] { relationships, kingdomLeaders, politicalPressures, clanConflicts, diplomacy, rumors, rebellions, warEconomy, pipeline }.ToList();
                string overall = WorstWorldTestStatus(systems.Select(x => ReadString(x, "status", "insufficient_data")));
                List<Dictionary<string, object>> trends = WorldSimulationStore.SelectWorldTestDailyRollups3(connection, Math.Max(10, Math.Min(500, limit)).ToString(CultureInfo.InvariantCulture),                    new Dictionary<string, object> { ["timeline"] = timelineId, ["from"] = fromDay, ["to"] = toDay });
                Dictionary<string, object> priorRollup = TryParseJsonObject(ReadString(WorldSimulationStore.SelectWorldTestDailyRollups4(connection, new Dictionary<string, object>
                    {
                        ["timeline"] = timelineId,
                        ["day"] = (int)Math.Floor(latestDay + 0.000001d)
                    }).FirstOrDefault(), "rollup_json", "{}")) ?? new Dictionary<string, object>();
                relationships["dailyChange"] = BuildWorldTestRelationshipDailyChange(relationships, priorRollup);
                double latestRollupDay = ReadDouble(WorldSimulationStore.SelectWorldTestDailyRollups5(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    }).FirstOrDefault(), "day", -1d);
                Dictionary<string, object> pipelineClocks =
                    ReadDictionary(pipeline, "heartbeats") ?? new Dictionary<string, object>();
                double relationshipIngested = ReadDouble(
                    pipeline, "latestRelationshipIngestedDay", -1d);
                double relationshipCompleted = ReadDouble(
                    pipeline, "latestRelationshipCompletedDay", -1d);
                double nativeConfirmed = ReadDouble(WorldSimulationStore.SelectRelationshipPairChemistry5(connection).FirstOrDefault(), "day", -1d);
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                    ["campaignLabel"] = ReadString(metadata, "campaignLabel", ReadString(metadata, "mainHeroName", campaignId)),
                    ["firstObservedDay"] = firstObservedDay,
                    ["latestObservedDay"] = latestDay,
                    ["observedDays"] = Math.Max(1,
                        (int)Math.Floor(latestDay + 0.000001d)
                        - (int)Math.Floor(firstObservedDay + 0.000001d) + 1),
                    ["lastHeartbeatUtc"] = UnixToIso(ReadLong(latestHeartbeat, "updated_ts", 0)),
                    ["overallStatus"] = overall,
                    ["checkpoints"] = BuildWorldTestCheckpoints(elapsedObservedDays),
                    ["relationships"] = relationships, ["kingdomLeaders"] = kingdomLeaders,
                    ["politicalPressures"] = politicalPressures,
                    ["clanAccords"] = DiplomacyService.ClanAccordsSnapshot(campaignId, timelineId),
                    ["clanConflicts"] = clanConflicts,
                    ["diplomacy"] = diplomacy, ["rumors"] = rumors,
                    ["rebellions"] = rebellions, ["warEconomy"] = warEconomy,
                    ["pipeline"] = pipeline,
                    ["startupGrace"] = ReadDictionary(nativePayload,
                        "startupGrace") ?? new Dictionary<string, object>(),
                    ["manifest"] = manifest,
                    ["nativeContext"] = ReadDictionary(nativePayload, "population") ?? new Dictionary<string, object>(),
                    ["dailyTrends"] = trends,
                    ["clocks"] = new Dictionary<string, object>
                    {
                        ["gameDay"] = latestDay,
                        ["nativeHeartbeatDay"] = nativeHeartbeatDay,
                        ["nativeHeartbeatLagDays"] = nativeHeartbeatDay < 0d
                            ? -1d : Math.Max(0d, latestDay - nativeHeartbeatDay),
                        ["ingestedDay"] = relationshipIngested,
                        ["completedRelationshipDay"] = relationshipCompleted,
                        ["rollupDay"] = latestRollupDay,
                        ["nativeConfirmedDay"] = nativeConfirmed,
                        ["rumorDay"] = ReadDouble(pipelineClocks, "rumors", -1d),
                        ["rebellionDay"] = ReadDouble(pipelineClocks, "rebellions", -1d)
                    },
                    ["rollupWorker"] = WorldTestRollupWorkerStatus(),
                    ["sourceLedgers"] = new[] { "PostgreSQL Reign campaign schema", "political pressure incident/action/activity ledgers", "diplomacy/event-queue.json", "actions/action-queue.json", "native daily heartbeat" }
                };
            }
        }
    }
}
