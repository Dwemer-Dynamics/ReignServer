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
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_native_heartbeats (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,world_day REAL NOT NULL,
eligible_npc_count INTEGER NOT NULL DEFAULT 0,kingdom_count INTEGER NOT NULL DEFAULT 0,
payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,day_key));");
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_world_test_heartbeat_day ON world_test_native_heartbeats(timeline_id,world_day DESC);");
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_daily_rollups (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,world_day REAL NOT NULL,
overall_status TEXT NOT NULL DEFAULT 'insufficient_data',rollup_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,day_key));");
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_world_test_rollup_day ON world_test_daily_rollups(timeline_id,world_day DESC);");
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_rebellion_rolls (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,kingdom_id TEXT NOT NULL,clan_id TEXT NOT NULL,
week_index INTEGER NOT NULL,rolled_day REAL NOT NULL,was_eligible INTEGER NOT NULL,triggered INTEGER NOT NULL,
roll INTEGER NOT NULL,ruler_relation INTEGER NOT NULL,is_player_clan INTEGER NOT NULL DEFAULT 0,
exclusion_reason TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',
updated_ts INTEGER NOT NULL,PRIMARY KEY(campaign_id,timeline_id,kingdom_id,clan_id,week_index));");
            ReignDatabase.EnsureDatabaseColumn(connection, "world_test_rebellion_rolls", "exclusion_reason",
                "TEXT NOT NULL DEFAULT ''");
            ReignDatabase.EnsureDatabaseColumn(connection, "world_test_rebellion_rolls",
                "personal_affinity_to_ruler", "INTEGER NOT NULL DEFAULT 0");
            ReignDatabase.EnsureDatabaseColumn(connection, "world_test_rebellion_rolls",
                "ruler_public_standing", "INTEGER NOT NULL DEFAULT 0");
            ReignDatabase.EnsureDatabaseColumn(connection, "world_test_rebellion_rolls",
                "ruler_standing_revision", "INTEGER NOT NULL DEFAULT 0");
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_rebellion_movements (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,movement_id TEXT NOT NULL,updated_day REAL NOT NULL,
stage TEXT NOT NULL DEFAULT '',resolution_applied INTEGER NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}',
updated_ts INTEGER NOT NULL,PRIMARY KEY(campaign_id,timeline_id,movement_id));");
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_rebellion_memberships (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,movement_id TEXT NOT NULL,clan_id TEXT NOT NULL,
side TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,movement_id,clan_id));");
            DiplomacyService.EnsureKingdomLeaderDiplomacySchema(connection);
            DiplomacyService.EnsurePoliticalPressureSchema(connection);
        }

        internal static Dictionary<string, object> WorldTestHeartbeatApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", Program.LatestCampaignId());
            string timelineId = ReadString(payload, "timelineId", "main");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            int dayKey = (int)Math.Floor(worldDay);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, object> population = ReadDictionary(payload, "population") ?? new Dictionary<string, object>();
            if (!Program.IsInternalCampaignId(campaignId))
            {
                Program.WriteCampaignMetadata(campaignId, payload);
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
                ReignDatabase.ExecuteSql(connection, "BEGIN IMMEDIATE;");
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
                    ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_test_rebellion_rolls(
campaign_id,timeline_id,kingdom_id,clan_id,week_index,rolled_day,was_eligible,triggered,roll,ruler_relation,is_player_clan,exclusion_reason,
personal_affinity_to_ruler,ruler_public_standing,ruler_standing_revision,payload_json,updated_ts)
VALUES($campaign,$timeline,$kingdom,$clan,$week,$day,$eligible,$triggered,$roll,$relation,$player,$exclusion,
$personal,$standing,$standingRevision,$payload,$ts)
ON CONFLICT(campaign_id,timeline_id,kingdom_id,clan_id,week_index) DO UPDATE SET
rolled_day=$day,was_eligible=$eligible,triggered=$triggered,roll=$roll,ruler_relation=$relation,is_player_clan=$player,
exclusion_reason=$exclusion,personal_affinity_to_ruler=$personal,
ruler_public_standing=$standing,ruler_standing_revision=$standingRevision,
payload_json=$payload,updated_ts=$ts
WHERE world_test_rebellion_rolls.rolled_day<>$day
OR world_test_rebellion_rolls.was_eligible<>$eligible
OR world_test_rebellion_rolls.triggered<>$triggered
OR world_test_rebellion_rolls.roll<>$roll
OR world_test_rebellion_rolls.ruler_relation<>$relation
OR world_test_rebellion_rolls.is_player_clan<>$player
OR world_test_rebellion_rolls.exclusion_reason<>$exclusion
OR world_test_rebellion_rolls.personal_affinity_to_ruler<>$personal
OR world_test_rebellion_rolls.ruler_public_standing<>$standing
OR world_test_rebellion_rolls.ruler_standing_revision<>$standingRevision
OR world_test_rebellion_rolls.payload_json<>$payload;",
                        new Dictionary<string, object>
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
                    Dictionary<string, object> priorMovement = ReignDatabase.QuerySql(connection, @"SELECT stage,resolution_applied
FROM world_test_rebellion_movements
WHERE campaign_id=$campaign AND timeline_id=$timeline AND movement_id=$id LIMIT 1;",
                        new Dictionary<string, object>
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
                    ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_test_rebellion_movements(
campaign_id,timeline_id,movement_id,updated_day,stage,resolution_applied,payload_json,updated_ts)
VALUES($campaign,$timeline,$id,$day,$stage,$resolved,$payload,$ts)
ON CONFLICT(campaign_id,timeline_id,movement_id) DO UPDATE SET
updated_day=$day,stage=$stage,resolution_applied=$resolved,payload_json=$payload,updated_ts=$ts
WHERE world_test_rebellion_movements.updated_day<>$day
OR world_test_rebellion_movements.stage<>$stage
OR world_test_rebellion_movements.resolution_applied<>$resolved
OR world_test_rebellion_movements.payload_json<>$payload;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["id"] = ReadString(row, "movementId", ""),
                            ["day"] = ReadDouble(row, "updatedDay", worldDay), ["stage"] = ReadString(row, "stage", ""),
                            ["resolved"] = ReadBool(row, "resolutionApplied", false) ? 1 : 0, ["payload"] = Json.Serialize(row), ["ts"] = now
                        });
                }
                foreach (Dictionary<string, object> row in ReadDictionaryList(rebellion, "memberships"))
                {
                    ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_test_rebellion_memberships(
campaign_id,timeline_id,movement_id,clan_id,side,payload_json,updated_ts)
VALUES($campaign,$timeline,$movement,$clan,$side,$payload,$ts)
ON CONFLICT(campaign_id,timeline_id,movement_id,clan_id) DO UPDATE SET
side=$side,payload_json=$payload,updated_ts=$ts
WHERE world_test_rebellion_memberships.side<>$side
OR world_test_rebellion_memberships.payload_json<>$payload;",
                        new Dictionary<string, object>
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
                    string actionStatus = Program.NormalizeLookup(ReadString(row, "status", "unknown"));
                    Dictionary<string, object> actionCounters = new Dictionary<string, object>
                    {
                        ["actionStatus:" + FirstNonEmpty(actionStatus, "unknown")] = 1,
                        ["actionType:" + FirstNonEmpty(Program.NormalizeLookup(ReadString(row, "type", "")), "unknown")] = 1,
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
                ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_test_manifests(
campaign_id,timeline_id,manifest_json,first_observed_day,latest_observed_day,created_ts,updated_ts)
VALUES($campaign,$timeline,$manifest,$day,$day,$ts,$ts)
ON CONFLICT(campaign_id,timeline_id) DO UPDATE SET
manifest_json=$manifest,latest_observed_day=MAX(world_test_manifests.latest_observed_day,$day),updated_ts=$ts;",
                    new Dictionary<string, object>
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
                ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_test_native_heartbeats(
campaign_id,timeline_id,day_key,world_day,eligible_npc_count,kingdom_count,payload_json,created_ts,updated_ts)
VALUES($campaign,$timeline,$key,$day,$npcs,$kingdoms,$payload,$ts,$ts)
ON CONFLICT(campaign_id,timeline_id,day_key) DO UPDATE SET
world_day=$day,eligible_npc_count=$npcs,kingdom_count=$kingdoms,payload_json=$payload,updated_ts=$ts;",
                    new Dictionary<string, object>
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
                ReignDatabase.ExecuteSql(connection, "COMMIT;");
                return response;
                }
                catch
                {
                    try { ReignDatabase.ExecuteSql(connection, "ROLLBACK;"); } catch { }
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
            string previous = ReadString(ReignDatabase.QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key=$key LIMIT 1;",
                new Dictionary<string, object> { ["key"] = fingerprintKey })
                .FirstOrDefault(), "value", "");
            Dictionary<string, List<Dictionary<string, object>>>
                expectedPolitical = RelationshipsService.BuildRequiredPoliticalPairDefinitions(leaders);
            int expectedKingdomPairs = expectedPolitical.Values.Count(items =>
                items.Any(item => ReadString(item, "source", "").Equals(
                    "kingdom_leadership", StringComparison.OrdinalIgnoreCase)));
            int expectedRulerPairs = expectedPolitical.Values.Count(items =>
                items.Any(item => ReadString(item, "source", "").Equals(
                    "ruler_network", StringComparison.OrdinalIgnoreCase)));
            Dictionary<string, object> storedPolitical = ReignDatabase.QuerySql(connection, @"
SELECT
SUM(CASE WHEN source='kingdom_leadership' AND active=1 AND required=1
    THEN 1 ELSE 0 END) AS kingdom_pairs,
SUM(CASE WHEN source='ruler_network' AND active=1 AND required=1
    THEN 1 ELSE 0 END) AS ruler_pairs
FROM relationship_pair_provenance
WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            int expectedClanLeaders = leaders.Count(hero =>
                ReadBool(hero, "isClanLeader", false));
            int storedClanLeaders = ReadInt(ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM identity_roster
WHERE is_alive=1 AND is_adult=1 AND is_player=0 AND is_clan_leader=1; ")
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
                ReignDatabase.ExecuteSql(connection, "BEGIN IMMEDIATE;");
                try
                {
                    ReignDatabase.ExecuteSql(connection, @"UPDATE identity_roster SET
is_ruler=0,is_clan_leader=0
                    WHERE is_ruler<>0 OR is_clan_leader<>0;");
                    long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    Stopwatch identityTimer = Stopwatch.StartNew();
                    CharactersService.UpsertIdentityRosterHeroesBatch(connection, leaders, ts);
                    identityTimer.Stop();
                    ReignDatabase.ExecuteSql(connection,
                        "INSERT OR REPLACE INTO schema_meta(key,value) VALUES($key,$value);",
                        new Dictionary<string, object>
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
                    ReignDatabase.ExecuteSql(connection, "COMMIT;");
                    RecordWorldTestPoliticalRosterTiming(totalTimer,
                        lockWaitTimer.ElapsedMilliseconds,
                        identityTimer.ElapsedMilliseconds,
                        standingTimer.ElapsedMilliseconds,
                        networkTimer.ElapsedMilliseconds, true);
                }
                catch
                {
                    try { ReignDatabase.ExecuteSql(connection, "ROLLBACK;"); } catch { }
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
                string directoryPath = Program.CampaignDirectory(campaignId);
                if (!Directory.Exists(directoryPath)) continue;
                DirectoryInfo directory = new DirectoryInfo(directoryPath);
                if (!Program.IsRealCampaignDirectory(directory)) continue;
                Dictionary<string, object> metadata = Program.ReadJsonObject(
                    Path.Combine(directoryPath, "campaign.json"));
                List<Dictionary<string, object>> timelines =
                    new List<Dictionary<string, object>>();
                try
                {
                    using (ReignDbConnection connection =
                        ReignDatabase.OpenCampaignConnection(campaignId))
                    {
                        EnsureWorldTestSchema(connection);
                        timelines = ReignDatabase.QuerySql(connection, @"SELECT timeline_id,MIN(world_day) AS first_day,MAX(world_day) AS latest_day,
COUNT(*) AS observed_days,MAX(updated_ts) AS updated_ts
FROM world_test_native_heartbeats GROUP BY timeline_id ORDER BY latest_day DESC;");
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
            string campaignId = query.TryGetValue("campaignId", out string campaign) ? campaign : Program.LatestCampaignId();
            if (!Program.HasRealCampaignStorage(campaignId))
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
            string campaignId = query.TryGetValue("campaignId", out string campaign) ? campaign : Program.LatestCampaignId();
            if (!Program.HasRealCampaignStorage(campaignId))
                return new Dictionary<string, object>
                {
                    ["ok"] = false, ["campaignId"] = campaignId, ["rows"] = new List<Dictionary<string, object>>(), ["total"] = 0,
                    ["error"] = "No observed real campaign matches this World Test selection."
                };
            string timelineId = query.TryGetValue("timelineId", out string timeline) ? timeline : "main";
            string subsystem = query.TryGetValue("subsystem", out string system) ? Program.NormalizeLookup(system) : "relationships";
            string search = query.TryGetValue("search", out string searchValue) ? searchValue : "";
            string status = query.TryGetValue("status", out string statusValue) ? statusValue : "";
            string type = query.TryGetValue("type", out string typeValue) ? typeValue : "";
            string mainHeroId = ReadFirstString(
                Program.ReadJsonObject(Program.CampaignFile(campaignId, "campaign.json")),
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
                    rows = ReignDatabase.QuerySql(connection, @"SELECT p.*,
COALESCE(sa.standing_value,0) AS public_standing_a,
COALESCE(sb.standing_value,0) AS public_standing_b,
COALESCE(sa.revision,1) AS standing_revision_a,
COALESCE(sb.revision,1) AS standing_revision_b,
MAX(-100,MIN(100,p.affinity_a_to_b+COALESCE(sb.standing_value,0)))
AS effective_attitude_a_to_b,
MAX(-100,MIN(100,p.affinity_b_to_a+COALESCE(sa.standing_value,0)))
AS effective_attitude_b_to_a
FROM relationship_pair_chemistry p
LEFT JOIN character_public_standing sa ON sa.campaign_id=$campaign
AND sa.timeline_id=$timeline AND sa.subject_id=p.hero_a_id
LEFT JOIN character_public_standing sb ON sb.campaign_id=$campaign
AND sb.timeline_id=$timeline AND sb.subject_id=p.hero_b_id
ORDER BY p.last_day DESC,p.pair_key LIMIT 1000;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId,
                            ["timeline"] = timelineId
                        });
                else if (subsystem == "relationship pair")
                    rows.Add(ReadWorldTestRelationshipPair(connection, campaignId, timelineId,
                        query.TryGetValue("pair", out string exactPair) ? exactPair : ""));
                else if (subsystem == "rumors")
                    rows = ReignDatabase.QuerySql(connection, "SELECT * FROM rumor_occurrences ORDER BY world_day DESC,updated_ts DESC LIMIT 1000;");
                else if (subsystem == "rumor subjects")
                {
                    rows = string.IsNullOrWhiteSpace(search)
                        ? new List<Dictionary<string, object>>()
                        : ReignDatabase.QuerySql(connection, "SELECT * FROM rumor_subject_tags WHERE occurrence_id=$id ORDER BY status,subject_id LIMIT 1000;",
                            new Dictionary<string, object> { ["id"] = search });
                    search = string.Empty;
                }
                else if (subsystem == "rumor funnel")
                {
                    rows = ReignDatabase.QuerySql(connection, @"SELECT
'counter' AS record_kind,day_key,subsystem,chunk_key AS evidence_key,
'info' AS severity,counters_json AS payload_json,updated_ts
FROM world_test_chunk_counters
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subsystem='rumors'
UNION ALL
SELECT 'evidence' AS record_kind,day_key,subsystem,evidence_key,severity,payload_json,updated_ts
FROM world_test_diagnostic_evidence
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subsystem='rumors'
ORDER BY day_key DESC,updated_ts DESC LIMIT 1000;",
                        new Dictionary<string, object>
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
                    rows = ReignDatabase.QuerySql(connection, @"SELECT * FROM clan_conflict_incidents
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY world_day DESC,incident_id LIMIT 1000;",
                        new Dictionary<string, object>
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
                        row["effects"] = ReignDatabase.QuerySql(connection, @"SELECT *
FROM clan_conflict_relationship_effects WHERE incident_id=$incident ORDER BY effect_id;",
                            new Dictionary<string, object>
                            {
                                ["incident"] = ReadString(row, "incident_id", "")
                            });
                    }
                }
                else if (subsystem == "actions")
                {
                    rows = ReadWorldTestActions(campaignId);
                    rows.AddRange(ReignDatabase.QuerySql(connection, @"SELECT
'native_target:'||pair_key AS actionId,
'RelationshipNativeProjection' AS type,status,world_day AS createdDay,
world_day AS lastAttemptDay,attempt_count AS attemptCount,last_error AS failureReason,
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation
FROM relationship_native_targets
ORDER BY world_day,pair_key LIMIT 1000;"));
                }
                else
                    rows = ReignDatabase.QuerySql(connection, "SELECT * FROM world_test_daily_rollups WHERE timeline_id=$timeline ORDER BY world_day DESC LIMIT 1000;",
                        new Dictionary<string, object> { ["timeline"] = timelineId });
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
            var pair = ReignDatabase.QuerySql(connection,
                "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;", parameters).FirstOrDefault();
            var result = new Dictionary<string, object>
            {
                ["schema"] = "reign-relationship-pair-observation-v1",
                ["pairKey"] = pairKey, ["hasPair"] = pair != null, ["pair"] = pair,
                ["firstToSecond"] = RelationshipsService.ResolveEffectiveAttitude(connection, campaignId, timelineId, ids[0], ids[1], "world_test_pair_observation"),
                ["secondToFirst"] = RelationshipsService.ResolveEffectiveAttitude(connection, campaignId, timelineId, ids[1], ids[0], "world_test_pair_observation")
            };
            result["familyAttention"] = TableExists(connection, "court_family_attention")
                ? ReignDatabase.QuerySql(connection, @"SELECT player_id,hero_id,dismissals,neglected,directional_relation,last_decay_day,decay_finished
FROM court_family_attention WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ((player_id=$a AND hero_id=$b) OR (player_id=$b AND hero_id=$a));", parameters)
                : new List<Dictionary<string, object>>();
            result["favorContact"] = TableExists(connection, "court_ruler_favor_contact")
                ? ReignDatabase.QuerySql(connection, @"SELECT * FROM court_ruler_favor_contact WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ((ruler_id=$a AND favorite_id=$b) OR (ruler_id=$b AND favorite_id=$a));", parameters)
                : new List<Dictionary<string, object>>();
            result["favorReputations"] = TableExists(connection, "character_reputations")
                ? ReignDatabase.QuerySql(connection, @"SELECT * FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id IN ($a,$b) AND (tag_id='ruler_favoring' OR tag_id LIKE 'ruler_favoring:%');", parameters)
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
            Dictionary<string, object> metadata = Program.ReadJsonObject(Program.CampaignFile(campaignId, "campaign.json"));
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureWorldTestSchema(connection);
                RelationshipsService.EnsureMbtiRelationshipSchema(connection);
                RelationshipsService.EnsureRelationshipDirectorSchema(connection);
                ReputationService.EnsureRumorSchema(connection);
                // Remain safe when reading an old Save Sync snapshot whose
                // campaign schema predates eager rebellion initialization.
                RebellionService.EnsureRebellionSchema(connection);
                Dictionary<string, object> latestHeartbeat = ReignDatabase.QuerySql(connection,
                    "SELECT * FROM world_test_native_heartbeats WHERE timeline_id=$timeline ORDER BY world_day DESC LIMIT 1;",
                    new Dictionary<string, object> { ["timeline"] = timelineId }).FirstOrDefault() ?? new Dictionary<string, object>();
                Dictionary<string, object> firstHeartbeat = ReignDatabase.QuerySql(connection,
                    "SELECT * FROM world_test_native_heartbeats WHERE timeline_id=$timeline ORDER BY world_day ASC LIMIT 1;",
                    new Dictionary<string, object> { ["timeline"] = timelineId }).FirstOrDefault() ?? new Dictionary<string, object>();
                Dictionary<string, object> nativePayload = TryParseJsonObject(ReadString(latestHeartbeat, "payload_json", "{}")) ?? new Dictionary<string, object>();
                Dictionary<string, object> manifestRow = ReignDatabase.QuerySql(connection, @"SELECT manifest_json
FROM world_test_manifests
WHERE campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;",
                    new Dictionary<string, object>
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
                List<Dictionary<string, object>> trends = ReignDatabase.QuerySql(connection, @"SELECT day_key,world_day,overall_status,rollup_json,updated_ts
FROM world_test_daily_rollups WHERE timeline_id=$timeline AND world_day>=$from AND world_day<=$to
ORDER BY world_day DESC LIMIT " + Math.Max(10, Math.Min(500, limit)).ToString(CultureInfo.InvariantCulture) + ";",
                    new Dictionary<string, object> { ["timeline"] = timelineId, ["from"] = fromDay, ["to"] = toDay });
                Dictionary<string, object> priorRollup = TryParseJsonObject(ReadString(ReignDatabase.QuerySql(connection, @"
SELECT rollup_json FROM world_test_daily_rollups
WHERE timeline_id=$timeline AND day_key<$day
ORDER BY day_key DESC LIMIT 1;",
                    new Dictionary<string, object>
                    {
                        ["timeline"] = timelineId,
                        ["day"] = (int)Math.Floor(latestDay + 0.000001d)
                    }).FirstOrDefault(), "rollup_json", "{}")) ?? new Dictionary<string, object>();
                relationships["dailyChange"] = BuildWorldTestRelationshipDailyChange(relationships, priorRollup);
                double latestRollupDay = ReadDouble(ReignDatabase.QuerySql(connection, @"
SELECT MAX(world_day) AS day FROM world_test_daily_rollups
WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    }).FirstOrDefault(), "day", -1d);
                Dictionary<string, object> pipelineClocks =
                    ReadDictionary(pipeline, "heartbeats") ?? new Dictionary<string, object>();
                double relationshipIngested = ReadDouble(
                    pipeline, "latestRelationshipIngestedDay", -1d);
                double relationshipCompleted = ReadDouble(
                    pipeline, "latestRelationshipCompletedDay", -1d);
                double nativeConfirmed = ReadDouble(ReignDatabase.QuerySql(connection, @"
SELECT MAX(last_native_sync_day) AS day FROM relationship_pair_chemistry
WHERE last_native_sync_day>=0;").FirstOrDefault(), "day", -1d);
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
