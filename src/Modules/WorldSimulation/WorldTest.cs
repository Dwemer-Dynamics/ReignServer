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
    internal static partial class Program
    {
        private static readonly string[] WorldTestRelationshipBands =
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

        private static void EnsureWorldTestSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_native_heartbeats (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,world_day REAL NOT NULL,
eligible_npc_count INTEGER NOT NULL DEFAULT 0,kingdom_count INTEGER NOT NULL DEFAULT 0,
payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,day_key));");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_world_test_heartbeat_day ON world_test_native_heartbeats(timeline_id,world_day DESC);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_daily_rollups (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,world_day REAL NOT NULL,
overall_status TEXT NOT NULL DEFAULT 'insufficient_data',rollup_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,day_key));");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_world_test_rollup_day ON world_test_daily_rollups(timeline_id,world_day DESC);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_rebellion_rolls (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,kingdom_id TEXT NOT NULL,clan_id TEXT NOT NULL,
week_index INTEGER NOT NULL,rolled_day REAL NOT NULL,was_eligible INTEGER NOT NULL,triggered INTEGER NOT NULL,
roll INTEGER NOT NULL,ruler_relation INTEGER NOT NULL,is_player_clan INTEGER NOT NULL DEFAULT 0,
exclusion_reason TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',
updated_ts INTEGER NOT NULL,PRIMARY KEY(campaign_id,timeline_id,kingdom_id,clan_id,week_index));");
            EnsureDatabaseColumn(connection, "world_test_rebellion_rolls", "exclusion_reason",
                "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "world_test_rebellion_rolls",
                "personal_affinity_to_ruler", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "world_test_rebellion_rolls",
                "ruler_public_standing", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "world_test_rebellion_rolls",
                "ruler_standing_revision", "INTEGER NOT NULL DEFAULT 0");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_rebellion_movements (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,movement_id TEXT NOT NULL,updated_day REAL NOT NULL,
stage TEXT NOT NULL DEFAULT '',resolution_applied INTEGER NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}',
updated_ts INTEGER NOT NULL,PRIMARY KEY(campaign_id,timeline_id,movement_id));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_rebellion_memberships (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,movement_id TEXT NOT NULL,clan_id TEXT NOT NULL,
side TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,movement_id,clan_id));");
            EnsureKingdomLeaderDiplomacySchema(connection);
            EnsurePoliticalPressureSchema(connection);
        }

        private static Dictionary<string, object> WorldTestHeartbeatApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", LatestCampaignId());
            string timelineId = ReadString(payload, "timelineId", "main");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            int dayKey = (int)Math.Floor(worldDay);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, object> population = ReadDictionary(payload, "population") ?? new Dictionary<string, object>();
            if (!IsInternalCampaignId(campaignId))
            {
                WriteCampaignMetadata(campaignId, payload);
            }
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureWorldTestSchema(connection);
                EnsureMbtiRelationshipSchema(connection);
                EnsureRelationshipDirectorSchema(connection);
                EnsureRumorSchema(connection);
                EnsureClanConflictSchema(connection);
                EnsureWorldTestTelemetrySchema(connection);
                ReconcileHeartbeatPoliticalRoster(connection, campaignId, timelineId,
                    worldDay, payload);
                ProcessDailyRulerRelationships(connection, campaignId, timelineId,
                    worldDay, payload);
                ExecuteSql(connection, "BEGIN IMMEDIATE;");
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
                    ExecuteSql(connection, @"INSERT INTO world_test_rebellion_rolls(
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
                    Dictionary<string, object> priorMovement = QuerySql(connection, @"SELECT stage,resolution_applied
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
                    ExecuteSql(connection, @"INSERT INTO world_test_rebellion_movements(
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
                    ExecuteSql(connection, @"INSERT INTO world_test_rebellion_memberships(
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
                    string actionStatus = NormalizeLookup(ReadString(row, "status", "unknown"));
                    Dictionary<string, object> actionCounters = new Dictionary<string, object>
                    {
                        ["actionStatus:" + FirstNonEmpty(actionStatus, "unknown")] = 1,
                        ["actionType:" + FirstNonEmpty(NormalizeLookup(ReadString(row, "type", "")), "unknown")] = 1,
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
                    ReadInt(ReadActiveSocialCatalog(connection), "revision", 0);
                ExecuteSql(connection, @"INSERT INTO world_test_manifests(
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
                ExecuteSql(connection, @"INSERT INTO world_test_native_heartbeats(
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
                ExecuteSql(connection, "COMMIT;");
                return response;
                }
                catch
                {
                    try { ExecuteSql(connection, "ROLLBACK;"); } catch { }
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
            EnsureIdentitySchema(connection);
            EnsureMbtiRelationshipSchema(connection);
            EnsureWorldRelationshipSchema(connection);
            string fingerprintKey = "political_roster_fingerprint:"
                + timelineId;
            string previous = ReadString(QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key=$key LIMIT 1;",
                new Dictionary<string, object> { ["key"] = fingerprintKey })
                .FirstOrDefault(), "value", "");
            Dictionary<string, List<Dictionary<string, object>>>
                expectedPolitical = BuildRequiredPoliticalPairDefinitions(leaders);
            int expectedKingdomPairs = expectedPolitical.Values.Count(items =>
                items.Any(item => ReadString(item, "source", "").Equals(
                    "kingdom_leadership", StringComparison.OrdinalIgnoreCase)));
            int expectedRulerPairs = expectedPolitical.Values.Count(items =>
                items.Any(item => ReadString(item, "source", "").Equals(
                    "ruler_network", StringComparison.OrdinalIgnoreCase)));
            Dictionary<string, object> storedPolitical = QuerySql(connection, @"
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
            int storedClanLeaders = ReadInt(QuerySql(connection, @"
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
            lock (CampaignRelationshipWriteLock(campaignId))
            {
                lockWaitTimer.Stop();
                ExecuteSql(connection, "BEGIN IMMEDIATE;");
                try
                {
                    ExecuteSql(connection, @"UPDATE identity_roster SET
is_ruler=0,is_clan_leader=0
                    WHERE is_ruler<>0 OR is_clan_leader<>0;");
                    long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    Stopwatch identityTimer = Stopwatch.StartNew();
                    UpsertIdentityRosterHeroesBatch(connection, leaders, ts);
                    identityTimer.Stop();
                    ExecuteSql(connection,
                        "INSERT OR REPLACE INTO schema_meta(key,value) VALUES($key,$value);",
                        new Dictionary<string, object>
                        {
                            ["key"] = fingerprintKey, ["value"] = fingerprint
                        });
                    Stopwatch standingTimer = Stopwatch.StartNew();
                    // A leadership change does not change rumor, reputation, or
                    // Charm inputs. Ensure rows for newly observed leaders without
                    // recalculating every already-current Public Standing record.
                    EnsurePublicStandingForRoster(connection, campaignId,
                        timelineId, worldDay, false);
                    standingTimer.Stop();
                    Stopwatch networkTimer = Stopwatch.StartNew();
                    ReconcilePoliticalRelationshipNetwork(connection, campaignId,
                        timelineId, worldDay, "political_heartbeat_changed");
                    networkTimer.Stop();
                    ExecuteSql(connection, "COMMIT;");
                    RecordWorldTestPoliticalRosterTiming(totalTimer,
                        lockWaitTimer.ElapsedMilliseconds,
                        identityTimer.ElapsedMilliseconds,
                        standingTimer.ElapsedMilliseconds,
                        networkTimer.ElapsedMilliseconds, true);
                }
                catch
                {
                    try { ExecuteSql(connection, "ROLLBACK;"); } catch { }
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

        private static Dictionary<string, object> WorldTestCampaignsApi(Dictionary<string, string> query)
        {
            List<Dictionary<string, object>> campaigns = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> registered
                in ReignPostgreSqlStorage.ListCampaignMetadata())
            {
                string campaignId = ReadString(
                    registered, "campaignId", "");
                string directoryPath = CampaignDirectory(campaignId);
                if (!Directory.Exists(directoryPath)) continue;
                DirectoryInfo directory = new DirectoryInfo(directoryPath);
                if (!IsRealCampaignDirectory(directory)) continue;
                Dictionary<string, object> metadata = ReadJsonObject(
                    Path.Combine(directoryPath, "campaign.json"));
                List<Dictionary<string, object>> timelines =
                    new List<Dictionary<string, object>>();
                try
                {
                    using (ReignDbConnection connection =
                        OpenCampaignConnection(campaignId))
                    {
                        EnsureWorldTestSchema(connection);
                        timelines = QuerySql(connection, @"SELECT timeline_id,MIN(world_day) AS first_day,MAX(world_day) AS latest_day,
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

        private static bool TryPromoteObservedWorldTestCampaign(DirectoryInfo directory)
        {
            if (directory == null || IsInternalCampaignId(directory.Name)) return false;
            if (!ReignPostgreSqlStorage.CampaignExists(directory.Name))
                return false;
            try
            {
                Dictionary<string, object> heartbeat;
                using (ReignDbConnection connection =
                    OpenCampaignConnection(directory.Name))
                {
                    if (!TableExists(
                        connection, "world_test_native_heartbeats"))
                        return false;
                    heartbeat = QuerySql(connection,
                        "SELECT payload_json FROM world_test_native_heartbeats ORDER BY updated_ts DESC LIMIT 1;")
                        .FirstOrDefault();
                }
                Dictionary<string, object> payload = TryParseJsonObject(ReadString(heartbeat, "payload_json", "{}"))
                    ?? new Dictionary<string, object>();
                payload["campaignId"] = directory.Name;
                if (string.IsNullOrWhiteSpace(ReadString(payload, "campaignLabel", "")))
                    payload["campaignLabel"] = ReadString(payload, "mainHeroName", directory.Name);
                WriteCampaignMetadata(directory.Name, payload);
                return File.Exists(Path.Combine(directory.FullName, "campaign.json"));
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<string, object> WorldTestOverviewApi(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>();
            string campaignId = query.TryGetValue("campaignId", out string campaign) ? campaign : LatestCampaignId();
            if (!HasRealCampaignStorage(campaignId))
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

        private static Dictionary<string, object> WorldTestDetailsApi(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>();
            string campaignId = query.TryGetValue("campaignId", out string campaign) ? campaign : LatestCampaignId();
            if (!HasRealCampaignStorage(campaignId))
                return new Dictionary<string, object>
                {
                    ["ok"] = false, ["campaignId"] = campaignId, ["rows"] = new List<Dictionary<string, object>>(), ["total"] = 0,
                    ["error"] = "No observed real campaign matches this World Test selection."
                };
            string timelineId = query.TryGetValue("timelineId", out string timeline) ? timeline : "main";
            string subsystem = query.TryGetValue("subsystem", out string system) ? NormalizeLookup(system) : "relationships";
            string search = query.TryGetValue("search", out string searchValue) ? searchValue : "";
            string status = query.TryGetValue("status", out string statusValue) ? statusValue : "";
            string type = query.TryGetValue("type", out string typeValue) ? typeValue : "";
            string mainHeroId = ReadFirstString(
                ReadJsonObject(CampaignFile(campaignId, "campaign.json")),
                "mainHeroStringId", "mainHeroId");
            int page = query.TryGetValue("page", out string pageText) && int.TryParse(pageText, out int parsedPage) ? Math.Max(1, parsedPage) : 1;
            int pageSize = query.TryGetValue("pageSize", out string sizeText) && int.TryParse(sizeText, out int parsedSize) ? Math.Max(10, Math.Min(200, parsedSize)) : 50;
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureWorldTestSchema(connection);
                EnsureMbtiRelationshipSchema(connection);
                EnsureRelationshipDirectorSchema(connection);
                EnsureRumorSchema(connection);
                if (subsystem == "relationships")
                    rows = QuerySql(connection, @"SELECT p.*,
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
                    rows = QuerySql(connection, "SELECT * FROM rumor_occurrences ORDER BY world_day DESC,updated_ts DESC LIMIT 1000;");
                else if (subsystem == "rumor subjects")
                {
                    rows = string.IsNullOrWhiteSpace(search)
                        ? new List<Dictionary<string, object>>()
                        : QuerySql(connection, "SELECT * FROM rumor_subject_tags WHERE occurrence_id=$id ORDER BY status,subject_id LIMIT 1000;",
                            new Dictionary<string, object> { ["id"] = search });
                    search = string.Empty;
                }
                else if (subsystem == "rumor funnel")
                {
                    rows = QuerySql(connection, @"SELECT
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
                    rows = QueryKingdomLeaderActivity(connection, campaignId,
                        timelineId, query, 1000);
                else if (subsystem == "political pressures")
                    rows = QueryPoliticalPressureActivity(connection, campaignId,
                        timelineId, query, 1000);
                else if (subsystem == "clan conflicts")
                {
                    rows = QuerySql(connection, @"SELECT * FROM clan_conflict_incidents
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY world_day DESC,incident_id LIMIT 1000;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId
                        });
                    foreach (Dictionary<string, object> row in rows)
                    {
                        row["participantsA"] = ReadJsonStringList(ReadString(row,
                            "involved_a_names_json", "[]")).Cast<object>().ToList();
                        row["participantsB"] = ReadJsonStringList(ReadString(row,
                            "involved_b_names_json", "[]")).Cast<object>().ToList();
                        row["votes"] = ReadDictionaryList(TryParseJsonObject("{\"votes\":"
                            + ReadString(row, "trait_votes_json", "[]") + "}"), "votes")
                            .Cast<object>().ToList();
                        row["effects"] = QuerySql(connection, @"SELECT *
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
                    rows.AddRange(QuerySql(connection, @"SELECT
'native_target:'||pair_key AS actionId,
'RelationshipNativeProjection' AS type,status,world_day AS createdDay,
world_day AS lastAttemptDay,attempt_count AS attemptCount,last_error AS failureReason,
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation
FROM relationship_native_targets
ORDER BY world_day,pair_key LIMIT 1000;"));
                }
                else
                    rows = QuerySql(connection, "SELECT * FROM world_test_daily_rollups WHERE timeline_id=$timeline ORDER BY world_day DESC LIMIT 1000;",
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
            string pairKey = AmbientPairKey(ids[0], ids[1]);
            var parameters = new Dictionary<string, object>
            {
                ["campaign"] = campaignId, ["timeline"] = timelineId,
                ["a"] = ids[0], ["b"] = ids[1], ["pair"] = pairKey
            };
            var pair = QuerySql(connection,
                "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;", parameters).FirstOrDefault();
            var result = new Dictionary<string, object>
            {
                ["schema"] = "reign-relationship-pair-observation-v1",
                ["pairKey"] = pairKey, ["hasPair"] = pair != null, ["pair"] = pair,
                ["firstToSecond"] = ResolveEffectiveAttitude(connection, campaignId, timelineId, ids[0], ids[1], "world_test_pair_observation"),
                ["secondToFirst"] = ResolveEffectiveAttitude(connection, campaignId, timelineId, ids[1], ids[0], "world_test_pair_observation")
            };
            result["familyAttention"] = TableExists(connection, "court_family_attention")
                ? QuerySql(connection, @"SELECT player_id,hero_id,dismissals,neglected,directional_relation,last_decay_day,decay_finished
FROM court_family_attention WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ((player_id=$a AND hero_id=$b) OR (player_id=$b AND hero_id=$a));", parameters)
                : new List<Dictionary<string, object>>();
            result["favorContact"] = TableExists(connection, "court_ruler_favor_contact")
                ? QuerySql(connection, @"SELECT * FROM court_ruler_favor_contact WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ((ruler_id=$a AND favorite_id=$b) OR (ruler_id=$b AND favorite_id=$a));", parameters)
                : new List<Dictionary<string, object>>();
            result["favorReputations"] = TableExists(connection, "character_reputations")
                ? QuerySql(connection, @"SELECT * FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
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

        private static void InvalidateWorldTestOverviewCache(string campaignId,
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
            if (!string.IsNullOrWhiteSpace(CampaignsRootOverride.Value))
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
            Dictionary<string, object> metadata = ReadJsonObject(CampaignFile(campaignId, "campaign.json"));
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureWorldTestSchema(connection);
                EnsureMbtiRelationshipSchema(connection);
                EnsureRelationshipDirectorSchema(connection);
                EnsureRumorSchema(connection);
                // Remain safe when reading an old Save Sync snapshot whose
                // campaign schema predates eager rebellion initialization.
                EnsureRebellionSchema(connection);
                Dictionary<string, object> latestHeartbeat = QuerySql(connection,
                    "SELECT * FROM world_test_native_heartbeats WHERE timeline_id=$timeline ORDER BY world_day DESC LIMIT 1;",
                    new Dictionary<string, object> { ["timeline"] = timelineId }).FirstOrDefault() ?? new Dictionary<string, object>();
                Dictionary<string, object> firstHeartbeat = QuerySql(connection,
                    "SELECT * FROM world_test_native_heartbeats WHERE timeline_id=$timeline ORDER BY world_day ASC LIMIT 1;",
                    new Dictionary<string, object> { ["timeline"] = timelineId }).FirstOrDefault() ?? new Dictionary<string, object>();
                Dictionary<string, object> nativePayload = TryParseJsonObject(ReadString(latestHeartbeat, "payload_json", "{}")) ?? new Dictionary<string, object>();
                Dictionary<string, object> manifestRow = QuerySql(connection, @"SELECT manifest_json
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
                    BuildWorldTestKingdomLeaders(connection, campaignId,
                        timelineId, latestDay, nativePayload);
                Dictionary<string, object> politicalPressures =
                    BuildWorldTestPoliticalPressures(connection, campaignId,
                        timelineId, latestDay);
                Dictionary<string, object> clanConflicts =
                    BuildWorldTestClanConflicts(connection, campaignId,
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
                List<Dictionary<string, object>> trends = QuerySql(connection, @"SELECT day_key,world_day,overall_status,rollup_json,updated_ts
FROM world_test_daily_rollups WHERE timeline_id=$timeline AND world_day>=$from AND world_day<=$to
ORDER BY world_day DESC LIMIT " + Math.Max(10, Math.Min(500, limit)).ToString(CultureInfo.InvariantCulture) + ";",
                    new Dictionary<string, object> { ["timeline"] = timelineId, ["from"] = fromDay, ["to"] = toDay });
                Dictionary<string, object> priorRollup = TryParseJsonObject(ReadString(QuerySql(connection, @"
SELECT rollup_json FROM world_test_daily_rollups
WHERE timeline_id=$timeline AND day_key<$day
ORDER BY day_key DESC LIMIT 1;",
                    new Dictionary<string, object>
                    {
                        ["timeline"] = timelineId,
                        ["day"] = (int)Math.Floor(latestDay + 0.000001d)
                    }).FirstOrDefault(), "rollup_json", "{}")) ?? new Dictionary<string, object>();
                relationships["dailyChange"] = BuildWorldTestRelationshipDailyChange(relationships, priorRollup);
                double latestRollupDay = ReadDouble(QuerySql(connection, @"
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
                double nativeConfirmed = ReadDouble(QuerySql(connection, @"
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
                    ["clanAccords"] = ClanAccordsSnapshot(campaignId, timelineId),
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

        private static Dictionary<string, object> BuildWorldTestRelationships(
            ReignDbConnection connection, string campaignId, string timelineId, double latestDay, string mainHeroId)
        {
            EnsureNotableMbtiSchema(connection);
            EnsureRelationshipFlingSchemaCore(connection);
            EnsureWorldTestTelemetrySchema(connection);
            Dictionary<string, object> standingAndPolitical =
                BuildWorldTestStandingAndPoliticalMetrics(connection, campaignId,
                    timelineId, latestDay);
            Dictionary<string, object> pairParameters = new Dictionary<string, object>
            {
                ["campaign"] = campaignId,
                ["timeline"] = timelineId,
                ["player"] = mainHeroId ?? string.Empty
            };
            Dictionary<string, object> pairSummary = QuerySql(connection, @"
SELECT COUNT(*) AS pair_count,
SUM(CASE WHEN p.last_delta_a_to_b>0 THEN 1 ELSE 0 END)
 +SUM(CASE WHEN p.last_delta_b_to_a>0 THEN 1 ELSE 0 END) AS positive_rolls,
SUM(CASE WHEN p.last_delta_a_to_b<0 THEN 1 ELSE 0 END)
 +SUM(CASE WHEN p.last_delta_b_to_a<0 THEN 1 ELSE 0 END) AS negative_rolls,
SUM(CASE WHEN p.last_context_kind='party' THEN 1 ELSE 0 END) AS party_pairs,
SUM(CASE WHEN p.last_context_kind='settlement' THEN 1 ELSE 0 END) AS settlement_pairs,
SUM(CASE WHEN
  CASE
    WHEN p.affinity_a_to_b<=-70 THEN 'nemesis' WHEN p.affinity_a_to_b<=-50 THEN 'enemy'
    WHEN p.affinity_a_to_b<=-30 THEN 'rival' WHEN p.affinity_a_to_b<=-10 THEN 'irritant'
    WHEN p.affinity_a_to_b<=9 THEN 'neutral' WHEN p.affinity_a_to_b<=29 THEN 'acquaintance'
    WHEN p.affinity_a_to_b<=49 THEN 'friend' WHEN p.affinity_a_to_b<=69 THEN 'close_friend'
    WHEN p.affinity_a_to_b<=84 THEN 'devoted' ELSE 'bonded' END
  =
  CASE
    WHEN p.affinity_b_to_a<=-70 THEN 'nemesis' WHEN p.affinity_b_to_a<=-50 THEN 'enemy'
    WHEN p.affinity_b_to_a<=-30 THEN 'rival' WHEN p.affinity_b_to_a<=-10 THEN 'irritant'
    WHEN p.affinity_b_to_a<=9 THEN 'neutral' WHEN p.affinity_b_to_a<=29 THEN 'acquaintance'
    WHEN p.affinity_b_to_a<=49 THEN 'friend' WHEN p.affinity_b_to_a<=69 THEN 'close_friend'
    WHEN p.affinity_b_to_a<=84 THEN 'devoted' ELSE 'bonded' END
  THEN 1 ELSE 0 END) AS symmetric_pairs,
SUM(CASE WHEN p.first_day=$latestDay THEN 1 ELSE 0 END) AS new_pairs,
SUM(CASE WHEN (na.hero_id IS NULL)<>(nb.hero_id IS NULL) THEN 1 ELSE 0 END)
 AS noble_notable_pairs,
SUM(CASE WHEN p.mbti_a='' OR p.mbti_a='XXXX' OR p.mbti_b='' OR p.mbti_b='XXXX'
  THEN 1 ELSE 0 END) AS missing_mbti
FROM relationship_pair_chemistry p
LEFT JOIN notable_mbti_profiles na ON na.hero_id=p.hero_a_id
LEFT JOIN notable_mbti_profiles nb ON nb.hero_id=p.hero_b_id
WHERE $player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player);",
                new Dictionary<string, object>(pairParameters)
                {
                    ["latestDay"] = (int)Math.Floor(latestDay)
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            int pairCount = ReadInt(pairSummary, "pair_count", 0);
            List<Dictionary<string, object>> notableProfiles = QuerySql(connection, "SELECT * FROM notable_mbti_profiles;");
            Dictionary<string, int> notableTypes = notableProfiles
                .GroupBy(x => ReadString(x, "mbti_type", "XXXX"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> notableSync = notableProfiles
                .GroupBy(x => ReadString(x, "native_sync_status", "pending"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
            int nobleNotablePairs = ReadInt(pairSummary, "noble_notable_pairs", 0);
            int missingMbti = ReadInt(pairSummary, "missing_mbti", 0);
            int typeMismatches = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_pair_chemistry p
LEFT JOIN notable_mbti_profiles a ON p.hero_a_id=a.hero_id
LEFT JOIN notable_mbti_profiles b ON p.hero_b_id=b.hero_id
WHERE ($player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player))
AND ((a.hero_id IS NOT NULL AND p.mbti_a<>a.mbti_type)
  OR (b.hero_id IS NOT NULL AND p.mbti_b<>b.mbti_type));",
                pairParameters).FirstOrDefault(), "count", 0);
            int notableSyncErrors = notableProfiles.Count(x => ContainsAny(ReadString(x, "native_sync_status", ""), "failed", "contradicted"));
            int notableSyncOverdue = notableProfiles.Count(x =>
                ContainsAny(ReadString(x, "native_sync_status", ""), "pending", "claimed")
                && latestDay - ReadDouble(x, "native_action_created_day", latestDay) > 1d);
            Dictionary<string, Dictionary<string, int>> bands = WorldTestRelationshipBands.ToDictionary(
                x => x, x => new Dictionary<string, int> { ["uniqueNpcCount"] = 0, ["directionalEdgeCount"] = 0, ["pairCount"] = 0 },
                StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> materializedCategories = QuerySql(connection, @"
WITH directions AS (
 SELECT p.pair_key,p.hero_a_id AS hero_id,p.affinity_a_to_b AS affinity,
        p.tag_a_to_b AS directional_tag
 FROM relationship_pair_chemistry p
 WHERE $player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player)
 UNION ALL
 SELECT p.pair_key,p.hero_b_id,p.affinity_b_to_a,p.tag_b_to_a
 FROM relationship_pair_chemistry p
 WHERE $player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player)
), category_edges AS (
 SELECT 'band' AS category_type,
   CASE WHEN affinity<=-70 THEN 'nemesis' WHEN affinity<=-50 THEN 'enemy'
        WHEN affinity<=-30 THEN 'rival' WHEN affinity<=-10 THEN 'irritant'
        WHEN affinity<=9 THEN 'neutral' WHEN affinity<=29 THEN 'acquaintance'
        WHEN affinity<=49 THEN 'friend' WHEN affinity<=69 THEN 'close_friend'
        WHEN affinity<=84 THEN 'devoted' ELSE 'bonded' END AS category_key,
   hero_id,pair_key,1 AS directional
 FROM directions
 UNION ALL
 SELECT 'tag',directional_tag,hero_id,pair_key,1 FROM directions
 WHERE directional_tag<>''
 UNION ALL
 SELECT 'tag',p.shared_tag,p.hero_a_id,p.pair_key,0
 FROM relationship_pair_chemistry p
 WHERE p.shared_tag<>'' AND ($player='' OR
   (p.hero_a_id<>$player AND p.hero_b_id<>$player))
 UNION ALL
 SELECT 'tag',p.shared_tag,p.hero_b_id,p.pair_key,0
 FROM relationship_pair_chemistry p
 WHERE p.shared_tag<>'' AND ($player='' OR
   (p.hero_a_id<>$player AND p.hero_b_id<>$player))
), hero_counts AS (
 SELECT category_type,category_key,hero_id,SUM(directional) AS directional_count
 FROM category_edges GROUP BY category_type,category_key,hero_id
), pair_counts AS (
 SELECT category_type,category_key,COUNT(DISTINCT pair_key) AS pair_count
 FROM category_edges GROUP BY category_type,category_key
)
SELECT h.category_type,h.category_key,COUNT(*) AS unique_npc_count,
SUM(h.directional_count) AS directional_edge_count,p.pair_count
FROM hero_counts h JOIN pair_counts p
 ON p.category_type=h.category_type AND p.category_key=h.category_key
GROUP BY h.category_type,h.category_key,p.pair_count;",
                pairParameters);
            Dictionary<string, Dictionary<string, object>> tags =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<int, int> affinityHistogram = new Dictionary<int, int>();
            foreach (Dictionary<string, object> row in materializedCategories)
            {
                string type = ReadString(row, "category_type", "");
                string key = ReadString(row, "category_key", "");
                Dictionary<string, int> counts = new Dictionary<string, int>
                {
                    ["uniqueNpcCount"] = ReadInt(row, "unique_npc_count", 0),
                    ["directionalEdgeCount"] = ReadInt(row, "directional_edge_count", 0),
                    ["pairCount"] = ReadInt(row, "pair_count", 0)
                };
                if (type.Equals("band", StringComparison.OrdinalIgnoreCase) && bands.ContainsKey(key))
                    bands[key] = counts;
                else if (type.Equals("tag", StringComparison.OrdinalIgnoreCase))
                    tags[key] = new Dictionary<string, object>
                    {
                        ["tag"] = key,
                        ["uniqueNpcCount"] = counts["uniqueNpcCount"],
                        ["directionalEdgeCount"] = counts["directionalEdgeCount"],
                        ["pairCount"] = counts["pairCount"]
                    };
            }
            foreach (Dictionary<string, object> row in QuerySql(connection, @"
WITH directions AS (
 SELECT p.affinity_a_to_b AS affinity FROM relationship_pair_chemistry p
 WHERE $player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player)
 UNION ALL
 SELECT p.affinity_b_to_a FROM relationship_pair_chemistry p
 WHERE $player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player)
)
SELECT affinity,COUNT(*) AS directional_edge_count
FROM directions GROUP BY affinity;", pairParameters))
            {
                affinityHistogram[ReadInt(row, "affinity", 0)] =
                    ReadInt(row, "directional_edge_count", 0);
            }
            int uniqueNpcCount = ReadInt(QuerySql(connection, @"
WITH heroes AS (
 SELECT p.hero_a_id AS hero_id FROM relationship_pair_chemistry p
 WHERE $player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player)
 UNION
 SELECT p.hero_b_id FROM relationship_pair_chemistry p
 WHERE $player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player)
)
SELECT COUNT(*) AS count FROM heroes;",
                pairParameters).FirstOrDefault(), "count", 0);
            List<Dictionary<string, object>> lifecycle = QuerySql(connection, "SELECT * FROM relationship_pair_lifecycle;");
            List<Dictionary<string, object>> lifecycleMarriageActions = QuerySql(connection,
                    @"SELECT director_action_id,status,world_day,actor_id,target_id,payload_json,result_json
FROM relationship_director_actions WHERE action_type='marriage';")
                .Where(row =>
                {
                    if (!string.IsNullOrWhiteSpace(mainHeroId)
                        && (ReadString(row, "actor_id", "").Equals(mainHeroId, StringComparison.OrdinalIgnoreCase)
                            || ReadString(row, "target_id", "").Equals(mainHeroId, StringComparison.OrdinalIgnoreCase)))
                        return false;
                    Dictionary<string, object> actionPayload =
                        TryParseJsonObject(ReadString(row, "payload_json", "{}")) ?? new Dictionary<string, object>();
                    return ReadString(actionPayload, "source", "").Equals(
                            "mbti_relationship_lifecycle", StringComparison.OrdinalIgnoreCase)
                        && ReadString(actionPayload, "timelineId", timelineId)
                            .Equals(timelineId, StringComparison.OrdinalIgnoreCase);
                }).ToList();
            Func<Dictionary<string, object>, string> lifecycleMarriageRoute = row =>
                ReadString(TryParseJsonObject(ReadString(row, "payload_json", "{}"))
                    ?? new Dictionary<string, object>(), "route", "");
            List<Dictionary<string, object>> organicMarriageActions =
                lifecycleMarriageActions.Where(row => lifecycleMarriageRoute(row)
                    .Equals("mutual_affinity",
                        StringComparison.OrdinalIgnoreCase)).ToList();
            List<Dictionary<string, object>> pregnancyCommitmentMarriageActions =
                lifecycleMarriageActions.Where(row => lifecycleMarriageRoute(row)
                    .Equals("pregnancy_commitment",
                        StringComparison.OrdinalIgnoreCase)).ToList();
            Dictionary<string, Dictionary<string, object>> observedMarriageRows =
                QuerySql(connection, @"SELECT hero_id,profile_json,last_observed_day
FROM relationship_observed_heroes;")
                    .ToDictionary(row => ReadString(row, "hero_id", ""), row => row,
                        StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> observedMarriageProfiles =
                observedMarriageRows.ToDictionary(row => row.Key, row =>
                    TryParseJsonObject(ReadString(row.Value, "profile_json", "{}"))
                        ?? new Dictionary<string, object>(),
                    StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> completedOrganicMarriageReports =
                organicMarriageActions.Where(row => ReadString(row, "status", "")
                    .Equals("completed", StringComparison.OrdinalIgnoreCase)).ToList();
            List<Dictionary<string, object>> completedPregnancyCommitmentMarriageReports =
                pregnancyCommitmentMarriageActions.Where(row => ReadString(row,
                    "status", "").Equals("completed",
                        StringComparison.OrdinalIgnoreCase)).ToList();
            Func<Dictionary<string, object>, bool> reciprocalNativeMarriage = row =>
            {
                string actorId = ReadString(row, "actor_id", "");
                string targetId = ReadString(row, "target_id", "");
                return observedMarriageProfiles.TryGetValue(actorId,
                        out Dictionary<string, object> actorProfile)
                    && observedMarriageProfiles.TryGetValue(targetId,
                        out Dictionary<string, object> targetProfile)
                    && ReadString(actorProfile, "spouseId", "")
                        .Equals(targetId, StringComparison.OrdinalIgnoreCase)
                    && ReadString(targetProfile, "spouseId", "")
                        .Equals(actorId, StringComparison.OrdinalIgnoreCase);
            };
            Func<Dictionary<string, object>, bool> nativeMarriageConfirmationOverdue = row =>
            {
                string actorId = ReadString(row, "actor_id", "");
                string targetId = ReadString(row, "target_id", "");
                if (!observedMarriageRows.TryGetValue(actorId,
                        out Dictionary<string, object> actorObservation)
                    || !observedMarriageRows.TryGetValue(targetId,
                        out Dictionary<string, object> targetObservation))
                    return false;
                Dictionary<string, object> result = TryParseJsonObject(
                    ReadString(row, "result_json", "{}"))
                    ?? new Dictionary<string, object>();
                double reportDay = ReadDouble(result, "worldDay",
                    ReadDouble(row, "world_day", 0d));
                double observedThrough = Math.Min(
                    ReadDouble(actorObservation, "last_observed_day", -1d),
                    ReadDouble(targetObservation, "last_observed_day", -1d));
                return observedThrough >= Math.Floor(reportDay) + 1d;
            };
            Func<Dictionary<string, object>, string> organicPairKey = row =>
            {
                Dictionary<string, object> actionPayload = TryParseJsonObject(
                    ReadString(row, "payload_json", "{}"))
                    ?? new Dictionary<string, object>();
                return FirstNonEmpty(ReadString(actionPayload, "pairKey", ""),
                    AmbientPairKey(ReadString(row, "actor_id", ""),
                        ReadString(row, "target_id", "")));
            };
            int romanticMarriages = completedOrganicMarriageReports
                .Where(reciprocalNativeMarriage).Select(organicPairKey)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int unconfirmedOrganicMarriageReports =
                completedOrganicMarriageReports.Count(row =>
                    !reciprocalNativeMarriage(row)
                    && nativeMarriageConfirmationOverdue(row));
            int duplicateOrganicMarriageReports = Math.Max(0,
                completedOrganicMarriageReports.Count
                - completedOrganicMarriageReports.Select(organicPairKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count());
            int pregnancyCommitmentConfirmedMarriages =
                completedPregnancyCommitmentMarriageReports
                    .Where(reciprocalNativeMarriage).Select(organicPairKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int unconfirmedPregnancyCommitmentMarriageReports =
                completedPregnancyCommitmentMarriageReports.Count(row =>
                    !reciprocalNativeMarriage(row)
                    && nativeMarriageConfirmationOverdue(row));
            int duplicatePregnancyCommitmentMarriageReports = Math.Max(0,
                completedPregnancyCommitmentMarriageReports.Count
                - completedPregnancyCommitmentMarriageReports
                    .Select(organicPairKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count());
            int arrangedMarriages = QuerySql(connection,
                    "SELECT evaluation_id,hero_a_id,hero_b_id FROM marriage_evaluations WHERE route='arranged' AND status='executed';")
                .Count(row => string.IsNullOrWhiteSpace(mainHeroId)
                    || (!ReadString(row, "hero_a_id", "").Equals(mainHeroId, StringComparison.OrdinalIgnoreCase)
                        && !ReadString(row, "hero_b_id", "").Equals(mainHeroId, StringComparison.OrdinalIgnoreCase)));
            int affairEligibleLovers = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count
FROM relationship_pair_lifecycle lifecycle
JOIN relationship_observed_heroes observed_a
  ON observed_a.hero_id=lifecycle.hero_a_id
JOIN relationship_observed_heroes observed_b
  ON observed_b.hero_id=lifecycle.hero_b_id
WHERE lifecycle.lover_active=1 AND (
    (COALESCE(json_extract(observed_a.profile_json,'$.spouseId'),'')<>''
     AND COALESCE(json_extract(observed_a.profile_json,'$.spouseId'),'')<>lifecycle.hero_b_id)
 OR (COALESCE(json_extract(observed_b.profile_json,'$.spouseId'),'')<>''
     AND COALESCE(json_extract(observed_b.profile_json,'$.spouseId'),'')<>lifecycle.hero_a_id)
);", new Dictionary<string, object>()).FirstOrDefault(), "count", 0);
            Dictionary<string, object> flingParameters =
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                };
            Dictionary<string, object> flingRollTotals = QuerySql(connection, @"
SELECT COUNT(*) AS rolls,SUM(passed) AS passes,
SUM(CASE WHEN status='passed_unmatched' THEN 1 ELSE 0 END) AS unmatched,
COUNT(DISTINCT world_day) AS observed_days
FROM relationship_fling_rolls WHERE campaign_id=$campaign
AND timeline_id=$timeline;", flingParameters).FirstOrDefault()
                ?? new Dictionary<string, object>();
            Dictionary<string, object> flingSparkTotals = QuerySql(connection, @"
SELECT COUNT(*) AS attempts,SUM(passed) AS passes
FROM relationship_fling_sparks WHERE campaign_id=$campaign
AND timeline_id=$timeline;", flingParameters).FirstOrDefault()
                ?? new Dictionary<string, object>();
            Dictionary<string, object> flingEncounterTotals = QuerySql(connection, @"
SELECT COUNT(*) AS pairings,SUM(same_sex) AS same_sex,
SUM(CASE WHEN same_sex=0 THEN 1 ELSE 0 END) AS opposite_sex,
SUM(short_affair) AS short_affairs,SUM(fixed_discovered) AS fixed_discoveries,
SUM(pregnancy_discovered) AS pregnancy_discoveries,
SUM(CASE WHEN conception_attempt_id<>'' THEN 1 ELSE 0 END) AS conception_attempts,
SUM(conceived) AS conceptions,SUM(discovery_applied) AS discovered_encounters
FROM relationship_fling_encounters WHERE campaign_id=$campaign
AND timeline_id=$timeline AND status='completed';", flingParameters)
                .FirstOrDefault() ?? new Dictionary<string, object>();
            int flingChanceViolations = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND (judgment<0 OR judgment>40 OR chance_percent<>50-judgment
 OR roll<1 OR roll>100 OR passed<>CASE WHEN roll<=chance_percent THEN 1 ELSE 0 END);",
                flingParameters).FirstOrDefault(), "count", 0);
            int flingSparkChanceViolations = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_sparks
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND (chance_percent<>25 OR roll<1 OR roll>100
 OR passed<>CASE WHEN roll<=chance_percent THEN 1 ELSE 0 END);",
                flingParameters).FirstOrDefault(), "count", 0);
            int flingReceiptViolations = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_encounters encounter
WHERE encounter.campaign_id=$campaign AND encounter.timeline_id=$timeline
AND (NOT EXISTS (SELECT 1 FROM relationship_fling_rolls roll
  WHERE roll.campaign_id=encounter.campaign_id
    AND roll.timeline_id=encounter.timeline_id
    AND roll.world_day=encounter.world_day AND roll.hero_id=encounter.hero_a_id
    AND roll.passed=1 AND roll.location_kind=encounter.location_kind
    AND roll.location_id=encounter.location_id)
 OR NOT EXISTS (SELECT 1 FROM relationship_fling_rolls roll
  WHERE roll.campaign_id=encounter.campaign_id
    AND roll.timeline_id=encounter.timeline_id
    AND roll.world_day=encounter.world_day AND roll.hero_id=encounter.hero_b_id
    AND roll.passed=1 AND roll.location_kind=encounter.location_kind
    AND roll.location_id=encounter.location_id)
 OR (EXISTS (SELECT 1 FROM relationship_fling_sparks spark_day
      WHERE spark_day.campaign_id=encounter.campaign_id
        AND spark_day.timeline_id=encounter.timeline_id
        AND spark_day.world_day=encounter.world_day)
     AND NOT EXISTS (SELECT 1 FROM relationship_fling_sparks spark
      WHERE spark.campaign_id=encounter.campaign_id
        AND spark.timeline_id=encounter.timeline_id
        AND spark.world_day=encounter.world_day
        AND spark.pair_key=encounter.pair_key AND spark.passed=1
        AND spark.location_kind=encounter.location_kind
        AND spark.location_id=encounter.location_id)));", flingParameters)
                .FirstOrDefault(), "count", 0);
            int flingCooldownViolations = ReadInt(QuerySql(connection, @"
WITH participation AS (
 SELECT hero_a_id AS hero_id,world_day FROM relationship_fling_encounters
 WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='completed'
 UNION ALL
 SELECT hero_b_id AS hero_id,world_day FROM relationship_fling_encounters
 WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='completed'
), ordered AS (
 SELECT hero_id,world_day,LAG(world_day) OVER(PARTITION BY hero_id ORDER BY world_day) AS prior_day
 FROM participation
)
SELECT COUNT(*) AS count FROM ordered
WHERE prior_day IS NOT NULL AND world_day-prior_day<7;",
                flingParameters).FirstOrDefault(), "count", 0);
            int flingSameSexConceptionViolations = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_encounters
WHERE campaign_id=$campaign AND timeline_id=$timeline AND same_sex=1
AND (conception_attempt_id<>'' OR conception_id<>'' OR conceived=1);",
                flingParameters).FirstOrDefault(), "count", 0);
            int flingDuplicatePersonDayViolations = ReadInt(QuerySql(connection, @"
WITH participation AS (
 SELECT hero_a_id AS hero_id,world_day FROM relationship_fling_encounters
 WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='completed'
 UNION ALL
 SELECT hero_b_id AS hero_id,world_day FROM relationship_fling_encounters
 WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='completed'
)
SELECT COUNT(*) AS count FROM (
 SELECT hero_id,world_day,COUNT(*) AS uses FROM participation
 GROUP BY hero_id,world_day HAVING COUNT(*)>1
) duplicates;", flingParameters).FirstOrDefault(), "count", 0);
            Dictionary<string, object> flingTelemetry =
                new Dictionary<string, object>
                {
                    ["judgmentMaximum"] = FlingMaximumJudgment,
                    ["chanceAtZeroJudgmentPercent"] = 50,
                    ["chanceAtFortyJudgmentPercent"] = 10,
                    ["sparkChancePercent"] = FlingSparkChancePercent,
                    ["cooldownDays"] = FlingCooldownDays,
                    ["shortAffairDiscoveryPercent"] =
                        FlingDiscoveryChancePercent,
                    ["observedDays"] = ReadInt(flingRollTotals,
                        "observed_days", 0),
                    ["eligibleRolls"] = ReadInt(flingRollTotals, "rolls", 0),
                    ["passes"] = ReadInt(flingRollTotals, "passes", 0),
                    ["unmatched"] = ReadInt(flingRollTotals, "unmatched", 0),
                    ["sparkAttempts"] = ReadInt(flingSparkTotals,
                        "attempts", 0),
                    ["sparkPasses"] = ReadInt(flingSparkTotals,
                        "passes", 0),
                    ["pairs"] = ReadInt(flingEncounterTotals, "pairings", 0),
                    ["sameSexPairs"] = ReadInt(flingEncounterTotals,
                        "same_sex", 0),
                    ["oppositeSexPairs"] = ReadInt(flingEncounterTotals,
                        "opposite_sex", 0),
                    ["shortAffairs"] = ReadInt(flingEncounterTotals,
                        "short_affairs", 0),
                    ["fixedDiscoveries"] = ReadInt(flingEncounterTotals,
                        "fixed_discoveries", 0),
                    ["pregnancyDiscoveries"] = ReadInt(flingEncounterTotals,
                        "pregnancy_discoveries", 0),
                    ["discoveredEncounters"] = ReadInt(flingEncounterTotals,
                        "discovered_encounters", 0),
                    ["conceptionAttempts"] = ReadInt(flingEncounterTotals,
                        "conception_attempts", 0),
                    ["conceptions"] = ReadInt(flingEncounterTotals,
                        "conceptions", 0),
                    ["chanceViolations"] = flingChanceViolations,
                    ["sparkChanceViolations"] =
                        flingSparkChanceViolations,
                    ["receiptViolations"] = flingReceiptViolations,
                    ["cooldownViolations"] = flingCooldownViolations,
                    ["sameSexConceptionViolations"] =
                        flingSameSexConceptionViolations,
                    ["duplicatePersonDayViolations"] =
                        flingDuplicatePersonDayViolations,
                    ["recentEncounters"] = QuerySql(connection, @"
SELECT * FROM relationship_fling_encounters
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY world_day DESC,encounter_id DESC LIMIT 20;", flingParameters),
                    ["recentSparks"] = QuerySql(connection, @"
SELECT * FROM relationship_fling_sparks
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY world_day DESC,pair_key LIMIT 40;", flingParameters),
                    ["recentRolls"] = QuerySql(connection, @"
SELECT * FROM relationship_fling_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY world_day DESC,hero_id LIMIT 40;", flingParameters)
                };
            Dictionary<string, object> lifecycleCounts = new Dictionary<string, object>
            {
                ["lovers"] = lifecycle.Count(x => ReadInt(x, "lover_active", 0) == 1),
                ["activeAffairs"] = lifecycle.Count(x => ReadInt(x, "affair_active", 0) == 1),
                ["affairEligibleLovers"] = affairEligibleLovers,
                ["flirtationStagePairs"] = lifecycle.Count(x =>
                    ReadString(x, "romance_stage", "").Equals("flirtation",
                        StringComparison.OrdinalIgnoreCase)),
                ["growingAttractionPairs"] = lifecycle.Count(x =>
                    ReadString(x, "romance_stage", "").Equals(
                        "growing_attraction", StringComparison.OrdinalIgnoreCase)),
                ["seriousAttractionPairs"] = lifecycle.Count(x =>
                    ReadString(x, "romance_stage", "").Equals(
                        "serious_attraction", StringComparison.OrdinalIgnoreCase)),
                ["flirtationAttempts"] = lifecycle.Count(x =>
                    ReadInt(x, "flirt_attempted", 0) == 1),
                ["flirtationPasses"] = lifecycle.Count(x =>
                    ReadInt(x, "flirt_passed", 0) == 1),
                ["affairJudgmentAttempts"] = lifecycle.Count(x =>
                    ReadInt(x, "affair_attempted", 0) == 1),
                ["affairJudgmentPasses"] = lifecycle.Count(x =>
                    ReadInt(x, "affair_passed", 0) == 1),
                ["romanceBonusTenPairs"] = lifecycle.Count(x =>
                    ReadInt(x, "romance_positive_bonus", 0)
                        == RomanceFlirtationPositiveChanceBonus),
                ["romanceBonusTwentyPairs"] = lifecycle.Count(x =>
                    ReadInt(x, "romance_positive_bonus", 0)
                        == RomanceGrowingPositiveChanceBonus),
                ["restrictedLoverAttempts"] = lifecycle.Count(x =>
                    ReadInt(x, "affair_attempted", 0) == 1),
                ["restrictedLoverPasses"] = lifecycle.Count(x =>
                    ReadInt(x, "affair_passed", 0) == 1),
                ["married"] = lifecycle.Count(x => ReadInt(x, "married", 0) == 1),
                ["estrangedDirections"] = lifecycle.Sum(x => ReadInt(x, "estranged_a_to_b", 0) + ReadInt(x, "estranged_b_to_a", 0)),
                ["divorced"] = lifecycle.Count(x => ReadInt(x, "divorced", 0) == 1),
                ["romanticMarriages"] = romanticMarriages,
                ["romanticMarriageAttempts"] = organicMarriageActions.Count,
                ["romanticMarriageCompletedReports"] = completedOrganicMarriageReports.Count,
                ["romanticMarriageUnconfirmedReports"] = unconfirmedOrganicMarriageReports,
                ["romanticMarriageDuplicateReports"] = duplicateOrganicMarriageReports,
                ["pregnancyCommitmentMarriageAttempts"] =
                    pregnancyCommitmentMarriageActions.Count,
                ["pregnancyCommitmentMarriageCompletedReports"] =
                    completedPregnancyCommitmentMarriageReports.Count,
                ["pregnancyCommitmentConfirmedMarriages"] =
                    pregnancyCommitmentConfirmedMarriages,
                ["pregnancyCommitmentMarriageUnconfirmedReports"] =
                    unconfirmedPregnancyCommitmentMarriageReports,
                ["pregnancyCommitmentMarriageDuplicateReports"] =
                    duplicatePregnancyCommitmentMarriageReports,
                ["romanticMarriageBlockedPairs"] = lifecycle.Count(x =>
                    ReadInt(x, "marriage_blocked", 0) == 1),
                ["arrangedMarriages"] = arrangedMarriages,
                ["affairDiscoveries"] = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='affair';", flingParameters).FirstOrDefault(), "count", 0),
                ["flings"] = flingTelemetry,
                ["pregnancyCommitments"] = TableCount(connection,
                    "relationship_conception_commitments"),
                ["pregnancyCommitmentMarriages"] = ReadInt(QuerySql(connection,
                    "SELECT COUNT(*) AS count FROM relationship_conception_commitments WHERE status='married';")
                    .FirstOrDefault(), "count", 0),
                ["pregnancyCommitmentBreakups"] = ReadInt(QuerySql(connection,
                    "SELECT COUNT(*) AS count FROM relationship_conception_commitments WHERE status='broke_up';")
                    .FirstOrDefault(), "count", 0),
                ["pregnancyCommitmentChanceViolations"] = ReadInt(QuerySql(connection,
                    "SELECT COUNT(*) AS count FROM relationship_conception_commitments WHERE chance_a>90 OR chance_b>90 OR chance_a<>MIN(90,honor_a+10) OR chance_b<>MIN(90,honor_b+10);")
                    .FirstOrDefault(), "count", 0),
                ["affairMarriageCommitments"] = TableCount(connection,
                    "relationship_affair_commitments"),
                ["affairMarriageCommitmentMarriages"] = ReadInt(QuerySql(connection,
                    "SELECT COUNT(*) AS count FROM relationship_affair_commitments WHERE status='married';")
                    .FirstOrDefault(), "count", 0),
                ["affairMarriageChanceCapViolations"] = ReadInt(QuerySql(connection,
                    "SELECT COUNT(*) AS count FROM relationship_affair_commitments WHERE chance_a>30 AND spouse_a_id<>'' OR chance_b>30 AND spouse_b_id<>'';")
                    .FirstOrDefault(), "count", 0),
                ["incidents"] = TableCount(connection, "relationship_incidents"),
                ["conceptions"] = StatusCounts(connection, "conceptions", "status"),
                ["concealedParentage"] = ReadInt(QuerySql(connection, "SELECT COUNT(*) AS count FROM parentage WHERE revealed=0 AND is_illegitimate=1;").FirstOrDefault(), "count", 0)
            };
            Dictionary<string, object> playerParameter = new Dictionary<string, object>
            {
                ["player"] = mainHeroId ?? string.Empty,
                ["latest"] = latestDay
            };
            int pending = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_native_targets
WHERE status IN ('pending','claimed')
AND ($player='' OR (hero_a_id<>$player AND hero_b_id<>$player));", playerParameter).FirstOrDefault(), "count", 0);
            int overdue = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_native_targets
WHERE status IN ('pending','claimed','failed') AND $latest-world_day>1
AND ($player='' OR (hero_a_id<>$player AND hero_b_id<>$player));",
                playerParameter).FirstOrDefault(), "count", 0);
            int unrepresentedNativeFlags = CountUnrepresentedNativeFlags(connection, mainHeroId);
            pending += unrepresentedNativeFlags;
            Dictionary<string, object> latestNativeBatch = QuerySql(connection, @"
SELECT * FROM relationship_native_sync_batches
WHERE timeline_id=$timeline ORDER BY world_day DESC,issued_ts DESC LIMIT 1;",
                new Dictionary<string, object> { ["timeline"] = timelineId })
                .FirstOrDefault() ?? new Dictionary<string, object>();
            int clientRemaining = ReadInt(latestNativeBatch, "client_remaining", 0);
            int persistedTargetFailures = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_native_targets WHERE status='failed'
AND ($player='' OR (hero_a_id<>$player AND hero_b_id<>$player));", playerParameter).FirstOrDefault(), "count", 0);
            int latestClientFailures = ReadInt(latestNativeBatch, "client_failed", 0);
            int nativeFailures = Math.Max(persistedTargetFailures, latestClientFailures);
            int directorFailures = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_director_actions
WHERE action_type<>'native_relation' AND status='failed'
AND ($player='' OR (actor_id<>$player AND target_id<>$player));", playerParameter).FirstOrDefault(), "count", 0);
            int failed = nativeFailures + directorFailures;
            Dictionary<string, object> openingSeed = QuerySql(connection, @"
SELECT * FROM relationship_opening_seed_runs
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY seed_version DESC LIMIT 1;", pairParameters).FirstOrDefault()
                ?? new Dictionary<string, object>();
            openingSeed["directionalCount"] =
                ReadInt(openingSeed, "completed_pairs", 0) * 2;
            openingSeed["averageDelta"] = ReadDouble(openingSeed,
                "average_delta", 0d);
            openingSeed["medianDelta"] = ReadDouble(openingSeed,
                "median_delta", 0d);
            openingSeed["minimumDelta"] = ReadInt(openingSeed,
                "minimum_delta", 0);
            openingSeed["maximumDelta"] = ReadInt(openingSeed,
                "maximum_delta", 0);
            openingSeed["startingBands"] = TryParseJsonObject(ReadString(
                openingSeed, "starting_bands_json", "{}"))
                ?? new Dictionary<string, object>();
            openingSeed["resultingBands"] = TryParseJsonObject(ReadString(
                openingSeed, "resulting_bands_json", "{}"))
                ?? new Dictionary<string, object>();
            string openingSeedStatus = ReadString(openingSeed, "status", "");
            double lastProcessed = RelationshipLastProcessedDay(connection, timelineId);
            bool enabled = LatestFeatureFlag(connection, "passiveRelationshipsEnabled", true);
            int eligible = LatestPopulationValue(connection, "eligibleNpcCount");
            string status = !enabled ? "disabled" : pairCount == 0 ? "insufficient_data"
                : failed > 0 || notableSyncErrors > 0 || missingMbti > 0 || typeMismatches > 0
                    || openingSeedStatus.Equals("failed", StringComparison.OrdinalIgnoreCase)
                    || unconfirmedOrganicMarriageReports > 0
                    || unconfirmedPregnancyCommitmentMarriageReports > 0
                    || flingChanceViolations > 0
                    || flingSparkChanceViolations > 0
                    || flingReceiptViolations > 0
                    || flingCooldownViolations > 0
                    || flingSameSexConceptionViolations > 0
                    || flingDuplicatePersonDayViolations > 0
                    ? "error"
                : lastProcessed < 0d ? "insufficient_data"
                : eligible > 1 && latestDay - lastProcessed > 1d || notableSyncOverdue > 0 || overdue > 0 ? "warning" : "healthy";
            List<string> anomalies = new List<string>();
            if (nativeFailures > 0) anomalies.Add(nativeFailures + " native relationship projections failed in the latest or still-persisted plan.");
            if (directorFailures > 0) anomalies.Add(directorFailures + " relationship lifecycle actions failed.");
            if (unconfirmedOrganicMarriageReports > 0)
                anomalies.Add(unconfirmedOrganicMarriageReports
                    + " organic marriage completion reports lack reciprocal native spouse confirmation.");
            if (duplicateOrganicMarriageReports > 0)
                anomalies.Add(duplicateOrganicMarriageReports
                    + " duplicate organic marriage completion reports were recorded for existing pairs.");
            if (unconfirmedPregnancyCommitmentMarriageReports > 0)
                anomalies.Add(unconfirmedPregnancyCommitmentMarriageReports
                    + " pregnancy-commitment marriage completion reports lack reciprocal native spouse confirmation.");
            if (duplicatePregnancyCommitmentMarriageReports > 0)
                anomalies.Add(duplicatePregnancyCommitmentMarriageReports
                    + " duplicate pregnancy-commitment marriage completion reports were recorded for existing pairs.");
            if (flingChanceViolations > 0)
                anomalies.Add(flingChanceViolations
                    + " fling roll receipts violate the judgment/chance contract.");
            if (flingSparkChanceViolations > 0)
                anomalies.Add(flingSparkChanceViolations
                    + " fling spark receipts violate the fixed twenty-five-percent contract.");
            if (flingReceiptViolations > 0)
                anomalies.Add(flingReceiptViolations
                    + " fling encounters lack two passed participant receipts or a passed same-location spark receipt.");
            if (flingCooldownViolations > 0)
                anomalies.Add(flingCooldownViolations
                    + " fling participants were reused inside the seven-day cooldown.");
            if (flingSameSexConceptionViolations > 0)
                anomalies.Add(flingSameSexConceptionViolations
                    + " same-sex fling encounters incorrectly created conception work.");
            if (flingDuplicatePersonDayViolations > 0)
                anomalies.Add(flingDuplicatePersonDayViolations
                    + " characters participated in more than one fling on the same day.");
            if (notableSyncErrors > 0) anomalies.Add(notableSyncErrors + " notable MBTI native-trait synchronizations failed or were contradicted.");
            if (missingMbti > 0) anomalies.Add(missingMbti + " relationship pairs contain a missing MBTI type.");
            if (typeMismatches > 0) anomalies.Add(typeMismatches + " relationship pairs disagree with a notable's persisted MBTI assignment.");
            if (notableSyncOverdue > 0) anomalies.Add(notableSyncOverdue + " notable native-trait synchronizations are overdue.");
            if (enabled && eligible > 1 && lastProcessed >= 0 && latestDay - lastProcessed > 1d) anomalies.Add("The daily relationship producer is overdue.");
            if (overdue > 0) anomalies.Add(overdue + " native relationship projections remain unconfirmed after one campaign day.");
            if (openingSeedStatus.Equals("failed", StringComparison.OrdinalIgnoreCase))
                anomalies.Add("Campaign-opening relationship seeding failed: "
                    + ReadString(openingSeed, "last_error", "unknown error"));
            return new Dictionary<string, object>
            {
                ["name"] = "NPC Relationships, Romance, and Family", ["status"] = status,
                ["latestSuccessfulDay"] = lastProcessed, ["pairCount"] = pairCount,
                ["uniqueNpcCount"] = uniqueNpcCount,
                ["directionalEdgeCount"] = pairCount * 2,
                ["newPairsLatestDay"] = ReadInt(pairSummary, "new_pairs", 0),
                ["partyPairs"] = ReadInt(pairSummary, "party_pairs", 0),
                ["settlementPairs"] = ReadInt(pairSummary, "settlement_pairs", 0),
                ["positiveLatestRolls"] = ReadInt(pairSummary, "positive_rolls", 0),
                ["negativeLatestRolls"] = ReadInt(pairSummary, "negative_rolls", 0),
                ["symmetricPairCount"] = ReadInt(pairSummary, "symmetric_pairs", 0),
                ["asymmetricPairCount"] = pairCount - ReadInt(pairSummary, "symmetric_pairs", 0),
                ["affinity"] = AffinitySummaryFromHistogram(affinityHistogram),
                ["bands"] = bands.ToDictionary(x => x.Key, x => (object)x.Value, StringComparer.OrdinalIgnoreCase),
                ["tags"] = tags.Values.OrderByDescending(x => ReadInt(x, "directionalEdgeCount", 0)).ToList(),
                ["nativeSync"] = new Dictionary<string, object>
                {
                    ["pending"] = pending, ["failed"] = nativeFailures,
                    ["aligned"] = Math.Max(0, pairCount - pending - persistedTargetFailures),
                    ["chemistryPending"] = pending,
                    ["unrepresentedNativeFlags"] = unrepresentedNativeFlags,
                    ["overdue"] = overdue,
                    ["planId"] = ReadString(latestNativeBatch, "plan_id", ""),
                    ["planTargetCount"] = ReadInt(latestNativeBatch, "target_count", 0),
                    ["clientApplied"] = ReadInt(latestNativeBatch, "client_applied", 0),
                    ["clientAlreadyAligned"] = ReadInt(latestNativeBatch, "client_already_aligned", 0),
                    ["clientObsolete"] = ReadInt(latestNativeBatch, "client_obsolete", 0),
                    ["clientFailed"] = ReadInt(latestNativeBatch, "client_failed", 0),
                    ["clientRemaining"] = clientRemaining,
                    ["clientDurationMs"] = ReadInt(latestNativeBatch, "client_duration_ms", 0),
                    ["lastClientReportDay"] = ReadDouble(latestNativeBatch, "last_report_day", -1d)
                },
                ["notableMbti"] = new Dictionary<string, object>
                {
                    ["assignedCount"] = notableProfiles.Count,
                    ["nobleNotablePairCount"] = nobleNotablePairs,
                    ["missingPairTypes"] = missingMbti,
                    ["typeMismatches"] = typeMismatches,
                    ["types"] = notableTypes,
                    ["nativeSyncStatuses"] = notableSync
                },
                ["openingSeed"] = openingSeed,
                ["lifecycle"] = lifecycleCounts,
                ["marriageFunnel"] = WorldTestCounterTotals(
                    connection, campaignId, timelineId, "marriages"),
                ["politicalMarriage"] = new Dictionary<string, object>
                {
                    ["rolls"] = TableCount(connection, "marriage_leader_rolls"),
                    ["evaluations"] = TableCount(connection, "marriage_evaluations"),
                    ["romanticMarriages"] = romanticMarriages,
                    ["arrangedMarriages"] = arrangedMarriages,
                    ["statuses"] = StatusCounts(connection, "marriage_evaluations", "status")
                },
                ["publicStanding"] = ReadDictionary(standingAndPolitical,
                    "publicStanding") ?? new Dictionary<string, object>(),
                ["politicalNetwork"] = ReadDictionary(standingAndPolitical,
                    "politicalNetwork") ?? new Dictionary<string, object>(),
                ["effectiveBands"] = standingAndPolitical.TryGetValue(
                    "effectiveBands", out object effectiveBands)
                    ? effectiveBands : new Dictionary<string, int>(),
                ["anomalies"] = anomalies
            };
        }

        private static Dictionary<string, object> BuildWorldTestDiplomacy(
            ReignDbConnection connection, string campaignId, string timelineId,
            double latestDay, double elapsedObservedDays,
            Dictionary<string, object> nativePayload)
        {
            List<Dictionary<string, object>> events = ReadWorldTestDiplomacyEvents(campaignId);
            Dictionary<string, object> state = ReadDirectorState(campaignId);
            List<Dictionary<string, object>> actions = ReadWorldTestActions(campaignId);
            List<Dictionary<string, object>> diplomaticActions = actions.Where(x =>
            {
                Dictionary<string, object> record = ReadDictionary(x, "record") ?? x;
                return ReadString(record, "source", "").Equals("world_diplomacy_director", StringComparison.OrdinalIgnoreCase);
            }).ToList();
            int failed = diplomaticActions.Count(x => IsTerminalFailureStatus(ReadString(x, "status", "")));
            int pending = diplomaticActions.Count(x => IsPendingStatus(ReadString(x, "status", "")));
            List<Dictionary<string, object>> nativeAgreements =
                ReadDictionaryList(ReadDictionary(nativePayload,
                    "nativeOutcomes") ?? new Dictionary<string, object>(),
                    "agreements");
            List<Dictionary<string, object>> missingAgreementReceipts = events
                .Where(x => ReadBool(x, "accepted", false)
                    && ReadString(x, "executionStatus", "")
                        .Equals("completed", StringComparison.OrdinalIgnoreCase)
                    && latestDay - ReadDouble(x, "worldDay", latestDay) > 0.01d)
                .Where(x => ExpectedAgreementKindForDiplomacyCommand(
                    ReadString(x, "command", "")) != "")
                .Where(x => !WorldTestNativeAgreementExists(nativeAgreements,
                    ExpectedAgreementKindForDiplomacyCommand(
                        ReadString(x, "command", "")),
                    ReadString(x, "actorKingdomId", ""),
                    ReadString(x, "targetKingdomId", "")))
                .ToList();
            double lastDirectorEvent = ReadDouble(state, "lastEvaluatedDay", -1d);
            double lastEvaluation = Math.Max(lastDirectorEvent,
                LatestSuccessfulDiplomacyEvaluationDay(connection, campaignId,
                    timelineId));
            bool enabled = LatestFeatureFlagFromPayload(campaignId, "worldDiplomacyEnabled", true);
            string status = !enabled ? "disabled" : lastEvaluation < 0 ? "insufficient_data"
                : failed > 0 || missingAgreementReceipts.Count > 0 ? "error"
                : latestDay - lastEvaluation > 3d ? "warning" : "healthy";
            Dictionary<string, int> commands = CountBy(events, "command");
            Dictionary<string, int> outcomes = CountBy(events, "outcome");
            Dictionary<string, int> actors = CountBy(events, "actorKingdomName");
            List<string> anomalies = new List<string>();
            if (failed > 0) anomalies.Add(failed + " diplomatic actions failed.");
            if (lastEvaluation >= 0 && latestDay - lastEvaluation > 3d) anomalies.Add("Diplomacy evaluation is past its three-day cadence.");
            if (pending > 0) anomalies.Add(pending + " diplomatic actions remain pending.");
            if (missingAgreementReceipts.Count > 0)
                anomalies.Add(missingAgreementReceipts.Count
                    + " completed treaty actions are absent from the next native agreement heartbeat.");
            return new Dictionary<string, object>
            {
                ["name"] = "NPC Diplomacy", ["status"] = status, ["latestSuccessfulDay"] = lastEvaluation,
                ["eventCount"] = events.Count, ["accepted"] = events.Count(x => ReadBool(x, "accepted", false)),
                ["refused"] = events.Count(x => !ReadBool(x, "accepted", false)), ["pendingActions"] = pending, ["failedActions"] = failed,
                ["undeliveredAnnouncements"] = events.Count(x =>
                    ReadBool(x, "announcementReady", false)
                    && !ReadBool(x, "delivered", false)
                    && !ReadBool(x, "acknowledged", false)),
                ["unacknowledgedAnnouncements"] = events.Count(x =>
                    ReadBool(x, "announcementReady", false)
                    && !ReadBool(x, "acknowledged", false)),
                ["nativeAgreementMismatches"] = missingAgreementReceipts.Count,
                ["missingAgreementReceipts"] = missingAgreementReceipts,
                ["eventsPerSeason"] = elapsedObservedDays > 0
                    ? Math.Round(events.Count / Math.Max(1d, elapsedObservedDays / 31.5d), 2) : 0d,
                ["eventsPerYear"] = elapsedObservedDays > 0
                    ? Math.Round(events.Count / Math.Max(1d, elapsedObservedDays / 126d), 2) : 0d,
                ["commands"] = commands, ["outcomes"] = outcomes, ["actorKingdoms"] = actors,
                ["targetKingdoms"] = CountBy(events, "targetKingdomName"),
                ["intentFamilies"] = CountDiplomacyIntentFamilies(events), ["anomalies"] = anomalies,
                ["recentEvents"] = events.OrderByDescending(x => ReadDouble(x, "worldDay", 0d)).Take(20).ToList()
            };
        }

        private static double LatestSuccessfulDiplomacyEvaluationDay(
            ReignDbConnection connection, string campaignId, string timelineId)
        {
            if (connection == null) return -1d;
            foreach (Dictionary<string, object> row in QuerySql(connection, @"SELECT day_key,counters_json
FROM world_test_chunk_counters
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subsystem='diplomacy' AND chunk_key LIKE 'evaluation_%'
ORDER BY day_key DESC;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId ?? string.Empty,
                    ["timeline"] = string.IsNullOrWhiteSpace(timelineId)
                        ? "main" : timelineId
                }))
            {
                Dictionary<string, object> counters = TryParseJsonObject(
                    ReadString(row, "counters_json", "{}"))
                    ?? new Dictionary<string, object>();
                if (ReadInt(counters, "completed", 0) > 0
                    && ReadInt(counters, "failures", 0) == 0)
                    return ReadDouble(row, "day_key", -1d);
            }
            return -1d;
        }

        private static string ExpectedAgreementKindForDiplomacyCommand(
            string command)
        {
            switch (NormalizeLookup(command))
            {
                case "sign_trade_agreement": return "trade_agreement";
                case "sign_non_aggression_pact": return "non_aggression_pact";
                case "sign_alliance": return "alliance";
                case "sign_defensive_pact": return "defensive_pact";
                default: return "";
            }
        }

        private static bool WorldTestNativeAgreementExists(
            IEnumerable<Dictionary<string, object>> agreements, string kind,
            string actorKingdomId, string targetKingdomId)
        {
            return (agreements ?? Enumerable.Empty<Dictionary<string, object>>())
                .Any(row => ReadBool(row, "isActive", true)
                    && ReadString(row, "kind", "").Equals(kind,
                        StringComparison.OrdinalIgnoreCase)
                    && ((ReadString(row, "actorKingdomId", "").Equals(
                            actorKingdomId, StringComparison.OrdinalIgnoreCase)
                         && ReadString(row, "targetKingdomId", "").Equals(
                            targetKingdomId, StringComparison.OrdinalIgnoreCase))
                        || (ReadString(row, "actorKingdomId", "").Equals(
                            targetKingdomId, StringComparison.OrdinalIgnoreCase)
                         && ReadString(row, "targetKingdomId", "").Equals(
                            actorKingdomId, StringComparison.OrdinalIgnoreCase))));
        }

        private static Dictionary<string, object> BuildWorldTestRumors(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            double latestDay)
        {
            Dictionary<string, object> rumorSummary = QuerySql(connection, @"
SELECT COUNT(*) AS rumor_count,
COUNT(*) FILTER (WHERE status='active' AND expires_day<=$day) AS expired_active,
COALESCE(MAX(world_day),-1) AS last_occurrence_day
FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId,
                    ["day"] = latestDay
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> outcomeSummary = QuerySql(connection, @"
SELECT COUNT(*) AS source_count,
COUNT(*) FILTER (WHERE r.completed=1) AS processed_count,
COUNT(*) FILTER (WHERE r.completed<0 OR
 (COALESCE(r.completed,0)=0 AND COALESCE(r.attempt_count,0)>=3)) AS failed_count,
COUNT(*) FILTER (WHERE r.event_id IS NULL OR
 (r.completed=0 AND r.attempt_count<3)) AS pending_count,
COALESCE(MIN(e.world_day) FILTER (WHERE r.event_id IS NULL OR
 (r.completed=0 AND r.attempt_count<3)),-1) AS oldest_pending_day,
COALESCE(MAX(e.world_day) FILTER (WHERE r.completed=1),-1) AS last_processed_day
FROM world_history_events e
LEFT JOIN social_outcome_receipts r ON r.event_id=e.event_id
WHERE e.timeline_id=$timeline AND e.event_type='social_outcome';",
                new Dictionary<string, object>
                {
                    ["timeline"] = timelineId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            int rumorCount = ReadInt(rumorSummary, "rumor_count", 0);
            int expiredActive = ReadInt(rumorSummary, "expired_active", 0);
            double lastOccurrenceDay = ReadDouble(rumorSummary,
                "last_occurrence_day", -1d);
            int sourceOutcomeCount = ReadInt(outcomeSummary,
                "source_count", 0);
            int processedSourceOutcomes = ReadInt(outcomeSummary,
                "processed_count", 0);
            int failedSourceOutcomes = ReadInt(outcomeSummary,
                "failed_count", 0);
            int pendingSourceOutcomeCount = ReadInt(outcomeSummary,
                "pending_count", 0);
            double oldestPendingDay = ReadDouble(outcomeSummary,
                "oldest_pending_day", -1d);
            double lastProcessedSourceDay = ReadDouble(outcomeSummary,
                "last_processed_day", -1d);
            Dictionary<string, int> funnel =
                WorldTestCounterTotals(connection, campaignId, timelineId, "rumors");
            bool enabled = LatestFeatureFlag(connection, "rumorsEnabled", true);
            string status = !enabled ? "disabled" : failedSourceOutcomes > 0 ? "error"
                : pendingSourceOutcomeCount > SocialWorldHistoryBatchSize * 2 ? "warning"
                : sourceOutcomeCount > 0 && processedSourceOutcomes > 0 ? "healthy"
                : rumorCount == 0 ? "insufficient_data"
                : expiredActive > 0 ? "warning" : "healthy";
            List<string> anomalies = new List<string>();
            if (expiredActive > 0) anomalies.Add(expiredActive + " expired rumor occurrences are still marked active.");
            if (pendingSourceOutcomeCount > SocialWorldHistoryBatchSize * 2)
                anomalies.Add(pendingSourceOutcomeCount + " social outcomes are waiting for rumor/reputation processing.");
            if (failedSourceOutcomes > 0)
                anomalies.Add(failedSourceOutcomes + " social outcomes exhausted their processing retries.");
            Dictionary<string, int> statuses = WorldTestGroupedCounts(connection,
                "SELECT status AS key,COUNT(*) AS count FROM rumor_occurrences "
                + "WHERE campaign_id=$campaign AND timeline_id=$timeline GROUP BY status;",
                campaignId, timelineId);
            Dictionary<string, int> sources = WorldTestGroupedCounts(connection,
                "SELECT COALESCE(NULLIF(archetype_id,''),'other') AS key,COUNT(*) AS count "
                + "FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline "
                + "GROUP BY COALESCE(NULLIF(archetype_id,''),'other');",
                campaignId, timelineId);
            Dictionary<string, int> tagStatuses = WorldTestGroupedCounts(connection,
                "SELECT rst.status AS key,COUNT(*) AS count FROM rumor_subject_tags rst "
                + "JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id "
                + "WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline GROUP BY rst.status;",
                campaignId, timelineId);
            Dictionary<string, object> supportingCounts = QuerySql(connection, @"
SELECT
(SELECT COUNT(*) FROM rumor_subject_tags rst JOIN rumor_occurrences ro
 ON ro.occurrence_id=rst.occurrence_id WHERE ro.campaign_id=$campaign
 AND ro.timeline_id=$timeline) AS tag_count,
(SELECT COUNT(*) FROM rumor_promotion_results rpr
 JOIN rumor_occurrences ro ON ro.occurrence_id=rpr.occurrence_id
 WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline) AS promotion_count,
(SELECT COUNT(*) FROM rumor_promotion_results rpr
 JOIN rumor_occurrences ro ON ro.occurrence_id=rpr.occurrence_id
 WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline
 AND rpr.promoted=1) AS promoted_count,
(SELECT COUNT(*) FROM character_reputations
 WHERE campaign_id=$campaign AND timeline_id=$timeline) AS reputation_count;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> reach = QuerySql(connection, @"
SELECT ro.occurrence_id AS rumor_id,ro.provenance_summary AS claim,
ro.status,ro.archetype_id AS archetype,
COUNT(DISTINCT rst.subject_id) AS subject_count,
COUNT(rst.occurrence_id) AS tag_count,
COALESCE(MAX(rpr.promoted),0) AS promoted,
COALESCE(MAX(rpr.streak_count),0) AS streak_count
FROM rumor_occurrences ro
LEFT JOIN rumor_subject_tags rst ON rst.occurrence_id=ro.occurrence_id
LEFT JOIN rumor_promotion_results rpr ON rpr.occurrence_id=ro.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline
GROUP BY ro.occurrence_id,ro.provenance_summary,ro.status,ro.archetype_id
ORDER BY COUNT(DISTINCT rst.subject_id) DESC,ro.world_day DESC
LIMIT 50;", new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId
                }).Select(row => new Dictionary<string, object>
                {
                    ["rumorId"] = ReadString(row, "rumor_id", ""),
                    ["claim"] = ReadString(row, "claim", ""),
                    ["status"] = ReadString(row, "status", ""),
                    ["archetype"] = ReadString(row, "archetype", ""),
                    ["subjectCount"] = ReadInt(row, "subject_count", 0),
                    ["tagCount"] = ReadInt(row, "tag_count", 0),
                    ["promoted"] = ReadInt(row, "promoted", 0) == 1,
                    ["streakCount"] = ReadInt(row, "streak_count", 0)
                }).ToList();
            return new Dictionary<string, object>
            {
                ["name"] = "Character-Owned Social Reputation", ["status"] = status,
                ["latestSuccessfulDay"] = Math.Max(lastOccurrenceDay, lastProcessedSourceDay),
                ["rumorCount"] = rumorCount,
                ["subjectTagCount"] = ReadInt(supportingCounts, "tag_count", 0),
                ["statuses"] = statuses, ["sources"] = sources,
                ["tagStatuses"] = tagStatuses,
                ["promotionResults"] = ReadInt(supportingCounts,
                    "promotion_count", 0),
                ["promotedOccurrences"] = ReadInt(supportingCounts,
                    "promoted_count", 0),
                ["durableReputations"] = ReadInt(supportingCounts,
                    "reputation_count", 0), ["expiredActive"] = expiredActive,
                ["sourceOutcomeCount"] = sourceOutcomeCount,
                ["processedSourceOutcomes"] = processedSourceOutcomes,
                ["pendingSourceOutcomes"] = pendingSourceOutcomeCount,
                ["failedSourceOutcomes"] = failedSourceOutcomes,
                ["oldestPendingSourceDay"] = oldestPendingDay,
                ["funnel"] = funnel,
                ["eligibleHooks"] = funnel.TryGetValue("eligibleHooks", out int eligibleHooks) ? eligibleHooks : 0,
                ["exposureAttempts"] = funnel.TryGetValue("exposureAttempts", out int exposureAttempts) ? exposureAttempts : 0,
                ["exposurePassed"] = funnel.TryGetValue("exposurePassed", out int exposurePassed) ? exposurePassed : 0,
                ["exposureFailed"] = funnel.TryGetValue("exposureFailed", out int exposureFailed) ? exposureFailed : 0,
                ["duplicateSourcesSuppressed"] = funnel.TryGetValue("duplicateSourcesSuppressed", out int duplicates) ? duplicates : 0,
                ["occurrencesCreated"] = funnel.TryGetValue("occurrencesCreated", out int occurrences) ? occurrences : 0,
                ["promotionPassed"] = funnel.TryGetValue("promotionPassed", out int promotionPassed) ? promotionPassed : 0,
                ["promotionFailed"] = funnel.TryGetValue("promotionFailed", out int promotionFailed) ? promotionFailed : 0,
                ["promotionEligible"] = (funnel.TryGetValue("promotionPassed", out int passedPromotions) ? passedPromotions : 0)
                    + (funnel.TryGetValue("promotionFailed", out int failedPromotions) ? failedPromotions : 0),
                ["reach"] = reach, ["anomalies"] = anomalies
            };
        }

        private static Dictionary<string, int> WorldTestGroupedCounts(
            ReignDbConnection connection, string sql, string campaignId,
            string timelineId)
        {
            return QuerySql(connection, sql, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId
                }).Where(row => !string.IsNullOrWhiteSpace(
                    ReadString(row, "key", "")))
                .ToDictionary(row => ReadString(row, "key", ""),
                    row => ReadInt(row, "count", 0),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> BuildWorldTestRebellions(ReignDbConnection connection, string campaignId, string timelineId, double latestDay)
        {
            List<Dictionary<string, object>> rolls = LatestNativeArray(connection, campaignId, timelineId, "rebellions", "weeklyRolls");
            List<Dictionary<string, object>> movements = LatestNativeArray(connection, campaignId, timelineId, "rebellions", "movements");
            List<Dictionary<string, object>> memberships = LatestNativeArray(connection, campaignId, timelineId, "rebellions", "memberships");
            List<Dictionary<string, object>> recognitionAttempts = QuerySql(connection,
                @"SELECT * FROM rebellion_recognition_attempts
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY world_day DESC, attempt_index DESC LIMIT 100;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                });
            int eligible = rolls.Count(x => ReadBool(x, "wasEligible", false));
            int triggered = rolls.Count(x => ReadBool(x, "triggered", false));
            bool playerIncluded = rolls.Any(x => ReadBool(x, "isPlayerClan", false)) || memberships.Any(x => ReadBool(x, "automaticNpcSelection", false) && ReadBool(x, "isPlayerClan", false));
            double lastRollDay = rolls.Count == 0 ? -1d : rolls.Max(x => ReadDouble(x, "rolledDay", -1d));
            List<int> missingWeekIndexes = MissingRebellionWeekIndexes(rolls);
            bool enabled = LatestFeatureFlag(connection, "rebellionsEnabled", true);
            string status = !enabled ? "disabled" : rolls.Count == 0 ? "insufficient_data"
                : playerIncluded ? "error"
                : latestDay - lastRollDay > 8d || missingWeekIndexes.Count > 0
                    ? "warning" : "healthy";
            List<string> anomalies = new List<string>();
            if (playerIncluded) anomalies.Add("The player appeared in an automatic NPC rebellion roll or side selection.");
            if (lastRollDay >= 0 && latestDay - lastRollDay > 8d) anomalies.Add("Weekly rebellion rolls are overdue by more than one campaign day.");
            if (missingWeekIndexes.Count > 0) anomalies.Add(
                "Weekly rebellion roll sets are missing for week indexes: "
                + string.Join(", ", missingWeekIndexes.Select(value =>
                    value.ToString(CultureInfo.InvariantCulture))) + ".");
            int recognitionQueueFailures = recognitionAttempts.Count(x =>
                ReadString(x, "status", "") == "accepted_queue_failed");
            int recognitionChanceViolations = recognitionAttempts.Count(x =>
                ReadInt(x, "chance_percent", 0) < 1
                || ReadInt(x, "chance_percent", 0)
                    > RebellionRecognitionMaximumChancePercent);
            if (recognitionQueueFailures > 0)
                anomalies.Add("One or more accepted rebel-independence recognition attempts failed to queue their exact negotiated resolution.");
            if (recognitionChanceViolations > 0)
                anomalies.Add("A rebel-independence recognition attempt escaped the hard 1-30 percent acceptance range.");
            if (recognitionQueueFailures > 0 || recognitionChanceViolations > 0)
                status = "error";
            return new Dictionary<string, object>
            {
                ["name"] = "Relationship-Triggered Rebellions", ["status"] = status, ["latestSuccessfulDay"] = lastRollDay,
                ["configuredWeeklyChancePercent"] = RebellionWeeklyOutbreakPercent,
                ["configuredRelationshipThreshold"] = RebellionRelationshipThreshold,
                ["weeklyRollCount"] = rolls.Count, ["eligibleRollCount"] = eligible, ["triggeredRollCount"] = triggered,
                ["observedSuccessPercent"] = eligible == 0 ? 0d : Math.Round(100d * triggered / eligible, 2),
                ["sampleStatus"] = eligible < 30 ? "insufficient_data" : "informational",
                ["missingWeeklyRollSetCount"] = missingWeekIndexes.Count,
                ["missingWeeklyRollWeekIndexes"] = missingWeekIndexes,
                ["playerAutomaticInclusion"] = playerIncluded,
                ["movementCount"] = movements.Count, ["activeCivilWars"] = movements.Count(x => ReadString(x, "stage", "") == "civil_war"),
                ["resolvedCivilWars"] = movements.Count(x => ReadBool(x, "resolutionApplied", false) || ReadString(x, "stage", "") == "resolved"),
                ["resolutionCauses"] = CountBy(movements, "resolutionCause"),
                ["membershipSides"] = CountBy(memberships, "side"),
                ["loneRebellions"] = movements.Count(m => memberships.Count(x => ReadString(x, "movementId", "") == ReadString(m, "movementId", "") && ReadString(x, "side", "") == "rebel") <= 1),
                ["fateOutcomes"] = CountBy(memberships, "fateOutcome"), ["anomalies"] = anomalies,
                ["recognitionAttemptCount"] = recognitionAttempts.Count,
                ["recognitionAcceptedCount"] = recognitionAttempts.Count(x =>
                    ReadString(x, "status", "").StartsWith("accepted",
                        StringComparison.OrdinalIgnoreCase)),
                ["recognitionQueueFailureCount"] = recognitionQueueFailures,
                ["recognitionChanceViolationCount"] = recognitionChanceViolations,
                ["recognitionMaximumChancePercent"] = RebellionRecognitionMaximumChancePercent,
                ["recognitionStatuses"] = CountBy(recognitionAttempts, "status"),
                ["recentRecognitionAttempts"] = recognitionAttempts.Take(20).ToList(),
                ["recentMovements"] = movements.OrderByDescending(x => ReadDouble(x, "updatedDay", 0d)).Take(20).ToList()
            };
        }

        private static Dictionary<string, object> BuildWorldTestWarEconomy(
            Dictionary<string, object> nativePayload, double latestDay)
        {
            Dictionary<string, object> economy = ReadDictionary(nativePayload,
                "warEconomy") ?? new Dictionary<string, object>();
            bool active = ReadBool(economy, "active", false);
            bool hooksHealthy = ReadBool(economy, "allHooksHealthy", false);
            int mismatches = ReadInt(economy, "patrolConservationMismatches", 0);
            int duplicateCreations = ReadInt(economy,
                "retinueDuplicateCreations", 0);
            double reliefShipped = ReadDouble(economy, "reliefFoodShipped", 0d);
            double reliefDelivered = ReadDouble(economy, "reliefFoodDelivered", 0d);
            double reliefLost = ReadDouble(economy, "reliefFoodLost", 0d);
            bool reliefConserved = ReliefFoodIsConserved(
                reliefShipped, reliefDelivered, reliefLost);
            string patchError = ReadString(economy, "lastPatchError", "");
            string status = economy.Count == 0 ? "insufficient_data"
                : !active || !hooksHealthy || !string.IsNullOrWhiteSpace(patchError)
                    || mismatches > 0 || !reliefConserved ? "error"
                : duplicateCreations > 0 ? "warning" : "healthy";
            List<string> anomalies = new List<string>();
            if (!active) anomalies.Add("The Reign economy Harmony patch group is inactive.");
            if (active && !hooksHealthy) anomalies.Add("One or more required economy Harmony hooks are missing.");
            if (!string.IsNullOrWhiteSpace(patchError)) anomalies.Add("Economy patch error: " + patchError);
            if (mismatches > 0) anomalies.Add(mismatches + " patrol troop conservation mismatches were observed.");
            if (!reliefConserved) anomalies.Add("Realm relief food was not conserved between shipped, delivered, and lost totals.");
            if (duplicateCreations > 0) anomalies.Add(duplicateCreations
                + " mobile-party identifiers received household retinues more than once in this process lifetime.");
            Dictionary<string, object> result = new Dictionary<string, object>(economy,
                StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = "War Economy and Manpower",
                ["status"] = status,
                ["latestSuccessfulDay"] = economy.Count == 0 ? -1d : latestDay,
                ["anomalies"] = anomalies
            };
            return result;
        }

        private static bool ReliefFoodIsConserved(
            double shipped, double delivered, double lost)
        {
            if (shipped < -0.01d || delivered < -0.01d || lost < -0.01d)
                return false;
            // Native campaign counters are persisted as cumulative single-precision
            // floats. Thousands of individually conserved shipments can therefore
            // acquire a few parts-per-million of independent summation drift.
            // Keep a strict absolute floor for small samples and a 10 ppm relative
            // allowance for long campaigns; genuine missing food remains an error.
            double tolerance = Math.Max(0.1d, Math.Abs(shipped) * 0.00001d);
            return Math.Abs(shipped - delivered - lost) <= tolerance;
        }

        private static List<int> MissingRebellionWeekIndexes(
            IEnumerable<Dictionary<string, object>> rolls)
        {
            List<int> observed = (rolls
                    ?? Enumerable.Empty<Dictionary<string, object>>())
                .Select(row => ReadInt(row, "weekIndex", -1))
                .Where(value => value >= 0).Distinct().OrderBy(value => value)
                .ToList();
            if (observed.Count < 2) return new List<int>();
            HashSet<int> present = new HashSet<int>(observed);
            List<int> missing = new List<int>();
            for (int week = observed[0]; week <= observed[observed.Count - 1]; week++)
                if (!present.Contains(week)) missing.Add(week);
            return missing;
        }

        private static Dictionary<string, object> BuildWorldTestPipeline(ReignDbConnection connection, string campaignId, string timelineId, double latestDay, Dictionary<string, object> nativePayload)
        {
            EnsureWorldHistorySchema(connection);
            List<Dictionary<string, object>> actions = ReadWorldTestActions(campaignId);
            int failed = actions.Count(x => IsTerminalFailureStatus(ReadString(x, "status", "")));
            int pending = actions.Count(x => IsPendingStatus(ReadString(x, "status", "")));
            int overdue = actions.Count(x =>
            {
                Dictionary<string, object> record = ReadDictionary(x, "record") ?? x;
                return IsPendingStatus(ReadString(x, "status", "")) && latestDay - ReadDouble(record, "createdDay", latestDay) > 1d;
            });
            Dictionary<string, object> saveSync = ReadDictionary(nativePayload, "saveSync") ?? new Dictionary<string, object>();
            bool alignmentPending = ReadBool(saveSync, "alignmentPending", false);
            double relationshipDay = RelationshipLastProcessedDay(connection, timelineId);
            double relationshipLag = relationshipDay < 0d ? -1d : Math.Max(0d, latestDay - relationshipDay);
            int relationshipInputBacklog = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_daily_inputs
WHERE timeline_id=$timeline AND status IN ('pending','processing');",
                new Dictionary<string, object> { ["timeline"] = timelineId }).FirstOrDefault(), "count", 0);
            double latestRelationshipIngestedDay = ReadDouble(QuerySql(connection, @"
SELECT MAX(world_day) AS day FROM relationship_daily_inputs
WHERE timeline_id=$timeline;", new Dictionary<string, object> { ["timeline"] = timelineId })
                .FirstOrDefault(), "day", -1d);
            Dictionary<string, object> partialRelationshipDay = QuerySql(connection, @"
SELECT day_key,pair_cursor,candidate_pairs,processed_pairs,pair_day_evaluations,
cadence_shard,window_start_day,window_day_count,started_ts,last_error
FROM relationship_daily_inputs WHERE timeline_id=$timeline AND status='processing'
ORDER BY day_key LIMIT 1;", new Dictionary<string, object> { ["timeline"] = timelineId })
                .FirstOrDefault() ?? new Dictionary<string, object>();
            string relationshipMbtiError = ReadString(QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='relationship_mbti_error' LIMIT 1;")
                .FirstOrDefault(), "value", "");
            Dictionary<string, object> relationshipShardCounts = QuerySql(connection, @"
SELECT processing_shard,COUNT(*) AS count
FROM relationship_pair_chemistry
WHERE processing_shard BETWEEN 0 AND $maxShard
GROUP BY processing_shard
ORDER BY processing_shard;",
                new Dictionary<string, object>
                {
                    ["maxShard"] = RelationshipCadenceShardCount - 1
                }).ToDictionary(
                    row => ReadInt(row, "processing_shard", -1)
                        .ToString(CultureInfo.InvariantCulture),
                    row => (object)ReadInt(row, "count", 0),
                    StringComparer.OrdinalIgnoreCase);
            double oldestNativeTarget = ReadDouble(QuerySql(connection, @"
SELECT MIN(world_day) AS day FROM relationship_native_targets
WHERE status IN ('pending','claimed','failed');").FirstOrDefault(), "day", -1d);
            double nativeProjectionLag = oldestNativeTarget < 0d ? 0d : Math.Max(0d, latestDay - oldestNativeTarget);
            Dictionary<string, object> storage =
                WorldHistoryStorageDiagnostics(connection, campaignId, timelineId, latestDay);
            Dictionary<string, object> matching = QuerySql(connection, @"
SELECT day_key,counters_json FROM world_test_chunk_counters
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subsystem='relationships'
AND chunk_key='daily_random_matching_summary'
ORDER BY day_key DESC LIMIT 1;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> matchingCounters =
                TryParseJsonObject(ReadString(matching, "counters_json", "{}"))
                ?? new Dictionary<string, object>();
            Dictionary<string, object> initializationSocial =
                InitializationReadinessStatusApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                    ["resumeWorkers"] = false
                });
            int compactionBacklog = ReadInt(storage, "compactionBacklog", 0);
            string status = failed > 0 || !string.IsNullOrWhiteSpace(relationshipMbtiError)
                || ReadBool(nativePayload, "timelineRegression", false) ? "error"
                : overdue > 0 || alignmentPending || relationshipLag > 1d
                    || nativeProjectionLag > 1d || relationshipInputBacklog > 1
                    || compactionBacklog > WorldHistoryCompactionBatchSize ? "warning"
                : nativePayload.Count == 0 ? "insufficient_data" : "healthy";
            List<string> anomalies = new List<string>();
            if (failed > 0) anomalies.Add(failed + " world actions have terminal failures.");
            if (overdue > 0) anomalies.Add(overdue + " world actions have been pending for more than one campaign day.");
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
                ["overdueActions"] = overdue, ["saveSync"] = saveSync,
                ["relationshipProcessingLagDays"] = relationshipLag,
                ["latestRelationshipIngestedDay"] = latestRelationshipIngestedDay,
                ["latestRelationshipCompletedDay"] = relationshipDay,
                ["relationshipInputBacklog"] = relationshipInputBacklog,
                ["partialRelationshipDay"] = partialRelationshipDay,
                ["relationshipWorker"] = ContinuousRelationshipWorkerStatus(),
                ["politicalRosterReconciliation"] =
                    WorldTestPoliticalRosterTimingStatus(),
                ["relationshipCadence"] = new Dictionary<string, object>
                {
                    ["daysPerBatch"] = RelationshipCadenceDays,
                    ["shardCount"] = RelationshipCadenceShardCount,
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
                        ReadDouble(QuerySql(connection, "SELECT MAX(world_day) AS day FROM rumor_occurrences;").FirstOrDefault(), "day", -1d),
                        ReadDouble(QuerySql(connection, @"SELECT MAX(e.world_day) AS day
FROM world_history_events e JOIN social_outcome_receipts r ON r.event_id=e.event_id
WHERE e.timeline_id=$timeline AND e.event_type='social_outcome' AND r.completed=1;",
                            new Dictionary<string, object> { ["timeline"] = timelineId }).FirstOrDefault(), "day", -1d)),
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
                    ["anomalyCount"] = WorldTestObjectList(source, "anomalies").Count
                };
                if (key == "relationships")
                {
                    compact["uniqueNpcCount"] = ReadInt(source, "uniqueNpcCount", 0);
                    compact["pairCount"] = ReadInt(source, "pairCount", 0);
                    compact["directionalEdgeCount"] = ReadInt(source, "directionalEdgeCount", 0);
                    compact["bands"] = ReadDictionary(source, "bands") ?? new Dictionary<string, object>();
                    compact["tags"] = WorldTestObjectList(source, "tags");
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

            Dictionary<string, Dictionary<string, object>> currentTags = WorldTestObjectList(current, "tags")
                .OfType<Dictionary<string, object>>()
                .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "tag", "")))
                .ToDictionary(x => ReadString(x, "tag", ""), x => x, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> priorTags = WorldTestObjectList(prior, "tags")
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
            foreach (Dictionary<string, object> row in ReadJsonLinesFromPath(CampaignFile(campaignId, "diplomacy", "events.jsonl")))
            {
                string id = ReadString(row, "eventId", "");
                if (!string.IsNullOrWhiteSpace(id)) byId[id] = row;
            }
            foreach (Dictionary<string, object> row in ReadDiplomaticEventQueue(campaignId))
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
            foreach (Dictionary<string, object> row in ReadJsonLinesFromPath(CampaignFile(campaignId, "actions", "actions.jsonl")))
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
            lock (FileLock) queue = ReadActionQueueUnlocked(campaignId);
            foreach (Dictionary<string, object> row in queue)
            {
                string id = ReadString(row, "id", "");
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!byId.TryGetValue(id, out Dictionary<string, object> existing)) byId[id] = row;
                else foreach (KeyValuePair<string, object> pair in row) existing[pair.Key] = pair.Value;
            }
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                if (QuerySql(connection,
                    "SELECT name FROM sqlite_master WHERE type='table' AND name='relationship_director_actions' LIMIT 1;").Any())
                {
                    foreach (Dictionary<string, object> row in QuerySql(connection,
                        "SELECT * FROM relationship_director_actions;"))
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
                return QuerySql(connection, "SELECT payload_json FROM world_test_rebellion_rolls WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY rolled_day DESC;",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId })
                    .Select(x => TryParseJsonObject(ReadString(x, "payload_json", "{}")) ?? new Dictionary<string, object>()).ToList();
            if (section == "rebellions" && key == "movements")
                return QuerySql(connection, "SELECT payload_json FROM world_test_rebellion_movements WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY updated_day DESC;",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId })
                    .Select(x => TryParseJsonObject(ReadString(x, "payload_json", "{}")) ?? new Dictionary<string, object>()).ToList();
            if (section == "rebellions" && key == "memberships")
                return QuerySql(connection, "SELECT payload_json FROM world_test_rebellion_memberships WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId })
                    .Select(x => TryParseJsonObject(ReadString(x, "payload_json", "{}")) ?? new Dictionary<string, object>()).ToList();
            Dictionary<string, object> heartbeat = QuerySql(connection,
                "SELECT payload_json FROM world_test_native_heartbeats WHERE timeline_id=$timeline ORDER BY world_day DESC LIMIT 1;",
                new Dictionary<string, object> { ["timeline"] = timelineId }).FirstOrDefault();
            Dictionary<string, object> payload = TryParseJsonObject(ReadString(heartbeat, "payload_json", "{}")) ?? new Dictionary<string, object>();
            Dictionary<string, object> group = ReadDictionary(payload, section) ?? new Dictionary<string, object>();
            return ReadDictionaryList(group, key);
        }

        private static int LatestPopulationValue(ReignDbConnection connection, string key)
        {
            Dictionary<string, object> heartbeat = QuerySql(connection, "SELECT payload_json FROM world_test_native_heartbeats ORDER BY world_day DESC LIMIT 1;").FirstOrDefault();
            Dictionary<string, object> payload = TryParseJsonObject(ReadString(heartbeat, "payload_json", "{}")) ?? new Dictionary<string, object>();
            return ReadInt(ReadDictionary(payload, "population"), key, 0);
        }

        private static bool LatestFeatureFlag(ReignDbConnection connection, string key, bool fallback)
        {
            Dictionary<string, object> heartbeat = QuerySql(connection, "SELECT payload_json FROM world_test_native_heartbeats ORDER BY world_day DESC LIMIT 1;").FirstOrDefault();
            Dictionary<string, object> payload = TryParseJsonObject(ReadString(heartbeat, "payload_json", "{}")) ?? new Dictionary<string, object>();
            return ReadBool(ReadDictionary(payload, "featureFlags"), key, fallback);
        }

        private static bool LatestFeatureFlagFromPayload(string campaignId, string key, bool fallback)
        {
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId)) return LatestFeatureFlag(connection, key, fallback);
        }

        private static double LatestKnownWorldDay(ReignDbConnection connection,
            string campaignId)
        {
            double relationship = ReadDouble(QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='mbti_relationship_last_processed_day' LIMIT 1;")
                .FirstOrDefault(), "value", 0d);
            double rumor = ReadDouble(QuerySql(connection,
                "SELECT MAX(world_day) AS day FROM rumor_occurrences;")
                .FirstOrDefault(), "day", 0d);
            Dictionary<string, object> state = ReadDirectorState(campaignId);
            return Math.Max(relationship, Math.Max(rumor,
                ReadDouble(state, "lastEvaluatedDay", 0d)));
        }

        private static double LatestKnownWorldDay(ReignDbConnection connection,
            string campaignId, string timelineId)
        {
            string relationshipKey = "mbti_relationship_last_processed_day:"
                + timelineId;
            double relationship = ReadDouble(QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key=$key LIMIT 1;",
                new Dictionary<string, object> { ["key"] = relationshipKey })
                .FirstOrDefault(), "value", -1d);
            if (relationship < 0d && string.Equals(timelineId, "main",
                StringComparison.OrdinalIgnoreCase))
                relationship = ReadDouble(QuerySql(connection,
                    "SELECT value FROM schema_meta WHERE key='mbti_relationship_last_processed_day' LIMIT 1;")
                    .FirstOrDefault(), "value", -1d);
            double ingested = ReadDouble(QuerySql(connection, @"SELECT MAX(world_day) AS day
FROM relationship_daily_inputs WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault(), "day", -1d);
            double rumor = ReadDouble(QuerySql(connection, @"SELECT MAX(world_day) AS day
FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault(), "day", -1d);
            double rollup = ReadDouble(QuerySql(connection, @"SELECT MAX(world_day) AS day
FROM world_test_daily_rollups WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault(), "day", -1d);
            return Math.Max(Math.Max(relationship, ingested), Math.Max(rumor, rollup));
        }

        private static void AddWorldTestBand(Dictionary<string, Dictionary<string, int>> bands, Dictionary<string, HashSet<string>> npcs,
            Dictionary<string, HashSet<string>> pairs, string band, string npc, string pair)
        {
            if (!bands.ContainsKey(band)) band = "neutral";
            bands[band]["directionalEdgeCount"]++;
            if (!string.IsNullOrWhiteSpace(npc)) npcs[band].Add(npc);
            if (!string.IsNullOrWhiteSpace(pair)) pairs[band].Add(pair);
        }

        private static void AddWorldTestTag(Dictionary<string, Dictionary<string, object>> tags, string tag, string npc, string pair, bool directional)
        {
            if (string.IsNullOrWhiteSpace(tag)) return;
            if (!tags.TryGetValue(tag, out Dictionary<string, object> item))
            {
                item = new Dictionary<string, object>
                {
                    ["tag"] = tag, ["directionalEdgeCount"] = 0, ["uniqueNpcIds"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    ["pairIds"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                };
                tags[tag] = item;
            }
            if (directional) item["directionalEdgeCount"] = ReadInt(item, "directionalEdgeCount", 0) + 1;
            ((HashSet<string>)item["uniqueNpcIds"]).Add(npc);
            ((HashSet<string>)item["pairIds"]).Add(pair);
        }

        private static void FinalizeWorldTestTags(Dictionary<string, Dictionary<string, object>> tags)
        {
            foreach (Dictionary<string, object> item in tags.Values)
            {
                item["uniqueNpcCount"] = ((HashSet<string>)item["uniqueNpcIds"]).Count;
                item["pairCount"] = ((HashSet<string>)item["pairIds"]).Count;
                item.Remove("uniqueNpcIds"); item.Remove("pairIds");
            }
        }

        private static Dictionary<string, object> AffinitySummary(List<int> values)
        {
            if (values == null || values.Count == 0)
                return new Dictionary<string, object> { ["minimum"] = 0, ["maximum"] = 0, ["average"] = 0d, ["median"] = 0d };
            List<int> ordered = values.OrderBy(x => x).ToList();
            double median = ordered.Count % 2 == 1 ? ordered[ordered.Count / 2] : (ordered[ordered.Count / 2 - 1] + ordered[ordered.Count / 2]) / 2d;
            return new Dictionary<string, object>
            {
                ["minimum"] = ordered.First(), ["maximum"] = ordered.Last(),
                ["average"] = Math.Round(ordered.Average(), 2), ["median"] = Math.Round(median, 2)
            };
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

        private static int TableCount(ReignDbConnection connection, string table)
        {
            if (!QuerySql(connection, "SELECT name FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;",
                new Dictionary<string, object> { ["name"] = table }).Any()) return 0;
            return ReadInt(QuerySql(connection, "SELECT COUNT(*) AS count FROM " + table + ";").FirstOrDefault(), "count", 0);
        }

        private static Dictionary<string, int> StatusCounts(ReignDbConnection connection, string table, string column)
        {
            Dictionary<string, int> result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!QuerySql(connection, "SELECT name FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;",
                new Dictionary<string, object> { ["name"] = table }).Any()) return result;
            foreach (Dictionary<string, object> row in QuerySql(connection,
                "SELECT " + column + ",COUNT(*) AS count FROM " + table + " GROUP BY " + column + ";"))
                result[ReadString(row, column, "unknown")] = ReadInt(row, "count", 0);
            return result;
        }

        private static bool IsTerminalFailureStatus(string status)
        {
            string value = NormalizeLookup(status);
            return ContainsAny(value, "failed", "blocked", "expired", "validation_failed");
        }

        private static bool IsPendingStatus(string status)
        {
            string value = NormalizeLookup(status);
            return ContainsAny(value, "queued", "pending", "delivered", "executing", "retry", "proposed");
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

        private static List<object> WorldTestObjectList(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null) return new List<object>();
            if (value is ArrayList array) return array.Cast<object>().ToList();
            if (value is IEnumerable<object> objects) return objects.ToList();
            return new List<object>();
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
            var petitioners = SelectPoliticalPressureClans(campaign, "petitioners", 12, petitionerClans, "realm", "ruler", 4, "origin");
            add("political_pressure_petitioners_exclude_sovereign",
                petitioners.Count == 2 && PoliticalPressureLeadLords(petitioners).OrderBy(x => x).SequenceEqual(new[] { "Second Vassal", "The Vassal" }),
                "A dominant ruling clan, unknown leader and foreign clan cannot become petitioners; named vassals and legacy leader IDs remain eligible.");
            add("political_pressure_petitioner_severity_limit",
                SelectPoliticalPressureClans(campaign, "petitioners", 12, petitionerClans, "realm", "ruler", 1, "target").Count == 1,
                "Minor incidents retain the one-clan limit after filtering on either side.");
            add("political_pressure_no_self_petition_fallback",
                SelectPoliticalPressureClans(campaign, "petitioners", 12, petitionerClans.Take(1).ToList(), "realm", "ruler", 4, "origin").Count == 0
                && SelectPoliticalPressureClans(campaign, "petitioners", 12, petitionerClans, "realm", "", 4, "origin").Count == 0,
                "A realm without a vassal or a known sovereign yields no petitioners rather than substituting the ruler.");
            EnsurePoliticalPressureSchema(connection);
            ExecuteSql(connection, "SAVEPOINT pressure_observation_contract;");
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
                ExecuteSql(connection, "INSERT INTO political_pressure_incidents (" + string.Join(",", values.Keys)
                    + ") VALUES (" + string.Join(",", values.Keys.Select(key => "$" + key)) + ");", values);
                ExecuteSql(connection, @"INSERT INTO political_pressure_activity
(activity_id,campaign_id,timeline_id,world_day,incident_id,actor_kingdom_id,target_kingdom_id,event_type,created_ts) VALUES
('pressure_observation_main',$campaign,'pressure_main',1,'pressure_observation_incident','player','foreign','incident_completed',1),
('pressure_observation_orphan',$campaign,'pressure_main',2,'missing_incident','','','message_receipt',2),
('pressure_observation_other',$campaign,'pressure_other',1,'pressure_observation_incident','player','foreign','incident_completed',1);",
                    new Dictionary<string, object> { ["campaign"] = campaign });
                Func<string> snapshot = () => Json.Serialize(new Dictionary<string, object>
                {
                    ["incidents"] = QuerySql(connection, "SELECT * FROM political_pressure_incidents ORDER BY incident_id;"),
                    ["activity"] = QuerySql(connection, "SELECT * FROM political_pressure_activity ORDER BY activity_id;"),
                    ["pressure"] = QuerySql(connection, "SELECT * FROM political_pressure_state ORDER BY actor_kingdom_id,target_kingdom_id;"),
                    ["receipts"] = QuerySql(connection, "SELECT * FROM political_pressure_delta_receipts ORDER BY incident_id,actor_kingdom_id,target_kingdom_id;")
                });
                string before = snapshot();
                var filter = new Dictionary<string, string> { ["pair"] = "player|foreign" };
                var observed = QueryPoliticalPressureActivity(connection, campaign, "pressure_main", filter, 10).Single();
                var incident = ReadDictionary(observed, "incident");
                add("political_pressure_incident_sides_observed",
                    ReadInt(observed, "before_value", -1) == 0 && ReadInt(observed, "after_value", -1) == 0
                    && ReadInt(incident, "origin_pressure_before", -1) == 0 && ReadInt(incident, "origin_pressure_after", -1) == 0
                    && ReadInt(incident, "target_pressure_before", -1) == 17 && ReadInt(incident, "target_pressure_after", -1) == 7
                    && ReadString(incident, "origin_stance", "") == "awaiting_player"
                    && ReadString(incident, "target_stance", "") == "preserve_relations",
                    "Activity retains its origin values while incident detail exposes the foreign target's actual before/after pressure and both sovereign choices.");
                var otherTimeline = QueryPoliticalPressureActivity(connection, campaign, "pressure_other", filter, 10).Single();
                var missingIncident = QueryPoliticalPressureActivity(connection, campaign, "pressure_main", null, 10)
                    .Single(row => ReadString(row, "incident_id", "") == "missing_incident");
                add("political_pressure_observation_scope_and_missing_incident",
                    ReadDictionary(otherTimeline, "incident") == null && ReadDictionary(missingIncident, "incident") == null
                    && QueryPoliticalPressureActivity(connection, campaign, "pressure_main",
                        new Dictionary<string, string> { ["pair"] = "foreign|player" }, 10).Count == 0,
                    "Incident joins respect timeline ownership; missing records remain observable with null details and activity orientation is not reversed.");
                add("political_pressure_observation_is_non_mutating", before == snapshot(),
                    "Pressure observation does not alter incidents, activity, pressure state or application receipts.");
            }
            finally
            {
                ExecuteSql(connection, "ROLLBACK TO SAVEPOINT pressure_observation_contract;");
                ExecuteSql(connection, "RELEASE SAVEPOINT pressure_observation_contract;");
            }
        }

        private static List<Dictionary<string, object>> RunWorldTestSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            results.AddRange(RunArrestSystemSelfTests());
            results.AddRange(RunNativeRelationProjectionSelfTests());
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "world_test", ["caseId"] = id,
                ["name"] = id, ["summary"] = summary, ["durationMs"] = 0
            });
            add("relationship_band_boundaries",
                RelationshipBand(-70) == "nemesis" && RelationshipBand(-69) == "enemy"
                && RelationshipBand(9) == "neutral" && RelationshipBand(10) == "acquaintance"
                && RelationshipBand(84) == "devoted" && RelationshipBand(85) == "bonded",
                "World Test uses the authoritative relationship-band boundaries.");
            add("health_precedence",
                WorstWorldTestStatus(new[] { "healthy", "warning", "disabled" }) == "warning"
                && WorstWorldTestStatus(new[] { "healthy", "error", "insufficient_data" }) == "error",
                "Errors and warnings dominate informational health states.");
            add("checkpoint_boundaries",
                BuildWorldTestCheckpoints(31.5d).Count(x => ReadBool(x, "reached", false)) == 3,
                "Campaign checkpoints are reached at days 1, 7, 31.5, 63, and 126.");
            add("configured_rebellion_chance", RebellionWeeklyOutbreakPercent == 5,
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
                ClassifyRulerDiplomacyPolarity(new Dictionary<string, object>
                    { ["command"] = "sign_alliance" }) == "positive"
                && ClassifyRulerDiplomacyPolarity(new Dictionary<string, object>
                    { ["command"] = "declare_war" }) == "negative",
                "Relationship bonus opportunities classify cooperative and hostile commands with opposite locked polarities.");
            add("ruler_mixed_package_is_ambiguous",
                ClassifyRulerDiplomacyPolarity(new Dictionary<string, object>
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
                StableRulerD100("campaign", "main", 8, "a|b")
                    == StableRulerD100("campaign", "main", 8, "a|b")
                && StableRulerD100("campaign", "main", 8, "a|b") >= 1
                && StableRulerD100("campaign", "main", 8, "a|b") <= 100,
                "Ruler compatibility and bonus dice are stable d100 values keyed to timeline, day, and direction.");
            add("daily_ruler_relationship_rolls_disabled",
                !DailyRulerRelationshipRollsEnabled,
                "Ruler relationships change through recorded political and diplomatic incidents without an independent daily dice pass.");
            add("kingdom_leaders_control_center_script_contract",
                ControlCenterHtml().Contains("cell.contextLabel||''"),
                "The rendered Kingdom Leaders matrix uses a valid JavaScript empty-string fallback so Control Center initialization can parse and run.");
            results.AddRange(RunWorldTestTelemetrySelfTests());
            results.AddRange(RunWorldTestRollupSelfTests());
            results.AddRange(RunPoliticalPressureSelfTests());
            results.AddRange(RunClanConflictSelfTests());
            string previousCampaignsRoot = CampaignsRootOverride.Value;
            string isolatedCampaignsRoot = Path.Combine(
                string.IsNullOrWhiteSpace(previousCampaignsRoot) ? Path.Combine(TestsDir, "isolated-campaigns") : previousCampaignsRoot,
                "wt_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            CampaignsRootOverride.Value = isolatedCampaignsRoot;
            string campaign = "wt_" + Guid.NewGuid().ToString("N").Substring(0, 10);
            try
            {
                results.AddRange(RunWorldRelationshipModelSelfTests());
                Dictionary<string, object> missingOverview = WorldTestOverviewApi(new Dictionary<string, string>
                {
                    ["campaignId"] = "default", ["timelineId"] = "main"
                });
                add("empty_selection_is_read_only", !ReadBool(missingOverview, "ok", true) && !Directory.Exists(CampaignDirectory("default")),
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
                using (ReignDbConnection connection = OpenCampaignConnection(campaign))
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
                    List<Dictionary<string, object>> previousAnnouncements = ReadDiplomaticEventQueue(campaign);
                    try
                    {
                        WriteDiplomaticEventQueue(campaign, announcementFixture);
                        Dictionary<string, object> announcementOverview = BuildWorldTestDiplomacy(
                            connection, campaign, "main", 7.25d, 1d, new Dictionary<string, object>());
                        Dictionary<string, object> nextAnnouncements = NextDiplomaticEvents(
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
                        WriteDiplomaticEventQueue(campaign, announcementFixture);
                        announcementOverview = BuildWorldTestDiplomacy(
                            connection, campaign, "main", 7.25d, 1d, new Dictionary<string, object>());
                        nextAnnouncements = NextDiplomaticEvents(
                            new Dictionary<string, object> { ["campaignId"] = campaign },
                            new Dictionary<string, string>());
                        add("completed_government_announcement_blocks_until_acknowledged",
                            ReadInt(announcementOverview, "undeliveredAnnouncements", -1) == 2
                            && ReadInt(announcementOverview, "unacknowledgedAnnouncements", -1) == 3
                            && ReadInt(nextAnnouncements, "count", -1) == 3,
                            "Once government execution completes and marks the announcement ready, the unchanged delivery and acknowledgement gates require it to drain.");
                    }
                    finally
                    {
                        WriteDiplomaticEventQueue(campaign, previousAnnouncements);
                    }
                    int count = ReadInt(QuerySql(connection, "SELECT COUNT(*) AS count FROM world_test_native_heartbeats;").FirstOrDefault(), "count", 0);
                    int rollups = ReadInt(QuerySql(connection, "SELECT COUNT(*) AS count FROM world_test_daily_rollups;").FirstOrDefault(), "count", 0);
                    int rebellionRolls = ReadInt(QuerySql(connection, "SELECT COUNT(*) AS count FROM world_test_rebellion_rolls;").FirstOrDefault(), "count", 0);
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
                    EnsureMbtiRelationshipSchema(connection);
                    ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,tag_a_to_b,tag_b_to_a,shared_tag,
first_day,last_day,updated_ts) VALUES
('a|b','a','b',-70,85,'nemesis','bonded','',1,7,1),
('a|c','a','c',10,10,'acquaintance','acquaintance','',2,7,1),
('a|player_hero','a','player_hero',100,100,'bonded','bonded','',2,7,1);");
                    ExecuteSql(connection, @"INSERT INTO relationship_observed_heroes(
hero_id,profile_json,profile_hash,first_observed_day,last_observed_day,updated_ts)
VALUES
('a','{""heroStringId"":""a"",""isFemale"":false,""isAlive"":true,""spouseId"":""c""}','',1,7,1),
('c','{""heroStringId"":""c"",""isFemale"":true,""isAlive"":true,""spouseId"":""a""}','',1,7,1),
('unconfirmed_a','{""heroStringId"":""unconfirmed_a"",""isFemale"":false,""isAlive"":true,""spouseId"":""""}','',1,7,1),
('unconfirmed_b','{""heroStringId"":""unconfirmed_b"",""isFemale"":true,""isAlive"":true,""spouseId"":""""}','',1,7,1);");
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
                    ExecuteSql(connection, @"INSERT INTO marriage_evaluations(
evaluation_id,route,status,hero_a_id,hero_b_id,clan_a_id,clan_b_id,leader_a_id,leader_b_id,
world_day,score,leader_a_approved,leader_b_approved,resentment_a,resentment_b,payload_json,created_ts,updated_ts,timeline_id)
VALUES('arranged_executed','arranged','executed','a','b','clan_a','clan_b','leader_a','leader_b',
7,80,1,1,0,0,'{}',1,1,'main'),
('arranged_pending','arranged','approved','a','c','clan_a','clan_c','leader_a','leader_c',
7,70,1,1,0,0,'{}',1,1,'main'),
('arranged_player','arranged','executed','a','player_hero','clan_a','player_clan','leader_a','player_hero',
7,99,1,1,0,0,'{}',1,1,'main');");
                    ExecuteSql(connection, @"INSERT INTO relationship_director_actions(
director_action_id,action_type,status,world_day,actor_id,target_id,payload_json,created_ts,resolved_ts)
VALUES
('romantic_completed','marriage','completed',7,'a','c',
'{""source"":""mbti_relationship_lifecycle"",""route"":""mutual_affinity"",""pairKey"":""a|c""}',1,2),
('romantic_completed_duplicate','marriage','completed',7,'a','c',
'{""source"":""mbti_relationship_lifecycle"",""route"":""mutual_affinity"",""pairKey"":""a|c""}',2,3),
('romantic_unconfirmed','marriage','completed',7,'unconfirmed_a','unconfirmed_b',
'{""source"":""mbti_relationship_lifecycle"",""route"":""mutual_affinity"",""pairKey"":""unconfirmed_a|unconfirmed_b""}',2,3),
('romantic_pending','marriage','pending',7,'b','c',
'{""source"":""mbti_relationship_lifecycle"",""route"":""mutual_affinity"",""pairKey"":""b|c""}',1,0),
('diplomatic_completed','marriage','completed',7,'leader_a','leader_b',
'{""source"":""world_diplomacy_director"",""route"":""alliance_marriage""}',1,2),
('romantic_player','marriage','completed',7,'a','player_hero',
'{""source"":""mbti_relationship_lifecycle"",""route"":""mutual_affinity"",""pairKey"":""a|player_hero""}',1,2);");
                }
                Dictionary<string, object> concurrentTraits = CoreTraitKeys.ToDictionary(
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
                    concurrentWriters.Add(Task.Run(() => MbtiRelationshipSnapshotApi(new Dictionary<string, object>
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
                using (ReignDbConnection connection = OpenCampaignConnection(campaign))
                {
                    int beforePairs = ReadInt(QuerySql(connection, "SELECT COUNT(*) AS n FROM relationship_pair_chemistry;").First(), "n", 0);
                    var missingPair = ReadWorldTestRelationshipPair(connection, campaign, "main", "missing_observer|missing_target");
                    int afterPairs = ReadInt(QuerySql(connection, "SELECT COUNT(*) AS n FROM relationship_pair_chemistry;").First(), "n", 0);
                    bool rejected = false;
                    try { ReadWorldTestRelationshipPair(connection, campaign, "main", "a|a"); }
                    catch (ArgumentException) { rejected = true; }
                    add("missing_relationship_pair_observation", beforePairs == afterPairs
                        && !ReadBool(missingPair, "hasPair", true)
                        && !ReadBool(ReadDictionary(missingPair, "firstToSecond"), "hasPair", true)
                        && rejected,
                        "Missing pair observation creates no relationship; malformed self-pairs fail before access.");
                    EnsureRulerFavorContactSchema(connection);
                    EnsureSocialReputationSchema(connection);
                    ExecuteSql(connection, "SAVEPOINT pair_observation_contract;");
                    try
                    {
                        var p = new Dictionary<string, object> { ["campaign"] = campaign };
                        ExecuteSql(connection, @"INSERT INTO court_ruler_favor_contact
(campaign_id,timeline_id,ruler_id,favorite_id,last_contact_day,grace_day,contact_id)
VALUES($campaign,'main','player_hero','a',-100,-100,'observation_fixture');", p);
                        ExecuteSql(connection, @"INSERT INTO character_reputations
(campaign_id,timeline_id,subject_id,tag_id,acquired_day,catalog_revision,snapshot_json,status,updated_ts)
VALUES($campaign,'main','player_hero','ruler_favoring:observation_fixture',-100,1,'{""linkedHeroId"":""a""}','active',1);", p);
                        string beforeContact = Json.Serialize(QuerySql(connection, "SELECT * FROM court_ruler_favor_contact;"));
                        string beforeReputations = Json.Serialize(QuerySql(connection, "SELECT * FROM character_reputations;"));
                        var observed = ReadWorldTestRelationshipPair(connection, campaign, "main", "a|player_hero");
                        add("relationship_pair_favor_observation_is_non_mutating",
                            ReadDictionaryList(observed, "favorContact").Count == 1
                            && ReadDictionaryList(observed, "favorReputations").Count == 1
                            && beforeContact == Json.Serialize(QuerySql(connection, "SELECT * FROM court_ruler_favor_contact;"))
                            && beforeReputations == Json.Serialize(QuerySql(connection, "SELECT * FROM character_reputations;")),
                            "Exact-pair favor observation returns the clock and established tag without refreshing contact or expiring reputation.");
                    }
                    finally
                    {
                        ExecuteSql(connection, "ROLLBACK TO SAVEPOINT pair_observation_contract;");
                        ExecuteSql(connection, "RELEASE SAVEPOINT pair_observation_contract;");
                    }
                }
                using (ReignDbConnection connection = OpenCampaignConnection(campaign))
                    AddPoliticalPressureObservationContracts(connection, campaign, add);
                Dictionary<string, object> dayEightHeartbeat =
                    new Dictionary<string, object>(heartbeat, StringComparer.OrdinalIgnoreCase)
                    {
                        ["timelineId"] = "main", ["worldDay"] = 8.25d
                };
                WorldTestHeartbeatApi(dayEightHeartbeat);
                ProcessNextWorldTestRollup();
                using (ReignDbConnection connection = OpenCampaignConnection(campaign))
                {
                    ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
affinity_a_to_b=35,affinity_b_to_a=35,tag_a_to_b='friend',tag_b_to_a='friend',
last_day=9,updated_ts=2 WHERE pair_key='a|c';");
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
                using (ReignDbConnection connection = OpenCampaignConnection(campaign))
                {
                    ExecuteSql(connection, @"INSERT INTO schema_meta(key,value)
VALUES($key,$day) ON CONFLICT(key) DO UPDATE SET value=$day;",
                        new Dictionary<string, object>
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
                Directory.CreateDirectory(CampaignDirectory(realCampaign));
                Directory.CreateDirectory(CampaignDirectory(internalCampaign));
                Directory.CreateDirectory(CampaignDirectory(incompleteCampaign));
                WriteJsonObject(Path.Combine(CampaignDirectory(realCampaign), "campaign.json"),
                    new Dictionary<string, object> { ["campaignId"] = realCampaign, ["campaignLabel"] = "Real fixture" });
                using (ReignDbConnection realConnection =
                    OpenCampaignConnection(realCampaign))
                {
                    EnsureWorldTestSchema(realConnection);
                }
                WriteJsonObject(Path.Combine(CampaignDirectory(internalCampaign), "campaign.json"),
                    new Dictionary<string, object> { ["campaignId"] = internalCampaign, ["campaignLabel"] = "Verification fixture" });
                Dictionary<string, object> observedHeartbeat = new Dictionary<string, object>(heartbeat, StringComparer.OrdinalIgnoreCase)
                {
                    ["campaignId"] = observedCampaign,
                    ["campaignLabel"] = "Observed fixture",
                    ["mainHeroName"] = "Observed Hero"
                };
                WorldTestHeartbeatApi(observedHeartbeat);
                bool heartbeatCreatedMetadata = File.Exists(Path.Combine(CampaignDirectory(observedCampaign), "campaign.json"))
                    && ReadString(ReadJsonObject(Path.Combine(CampaignDirectory(observedCampaign), "campaign.json")), "mainHeroName", "") == "Observed Hero";
                add("heartbeat_establishes_real_campaign", heartbeatCreatedMetadata,
                    "A native World Test heartbeat makes a fresh campaign selectable before its first Save Sync registration.");
                using (ReignDbConnection observedConnection = OpenCampaignConnection(observedCampaign))
                {
                    EnsureRumorSchema(observedConnection);
                    ExecuteSql(observedConnection, @"INSERT OR REPLACE INTO rumor_occurrences(
occurrence_id,campaign_id,timeline_id,archetype_id,thread_key,world_day,expires_day,status,
catalog_revision,participants_json,created_ts,updated_ts)
VALUES('rumor_inspector_fixture',$campaign,'main','marital_strife','pair_fixture',7,37,'active',1,'[]',1,1);",
                        new Dictionary<string, object> { ["campaign"] = observedCampaign });
                    ExecuteSql(observedConnection, @"INSERT OR REPLACE INTO rumor_subject_tags(
occurrence_id,subject_id,tag_id,subject_role,description,status,updated_ts)
VALUES('rumor_inspector_fixture','npc_observer','marital_strife','spouse','Fixture social report','active',1);");
                }
                Dictionary<string, object> receiptDetails = WorldTestDetailsApi(new Dictionary<string, string>
                {
                    ["campaignId"] = observedCampaign, ["timelineId"] = "main",
                    ["subsystem"] = "rumor subjects", ["search"] = "rumor_inspector_fixture"
                });
                List<object> receiptRows = WorldTestObjectList(receiptDetails, "rows");
                add("rumor_subject_drilldown", receiptRows.Count == 1
                    && ReadString(receiptRows[0] as Dictionary<string, object>, "subject_id", "") == "npc_observer",
                    "The read-only social inspector can load character-owned subject tags for one selected occurrence.");
                observedHeartbeat["campaignId"] = legacyObservedCampaign;
                observedHeartbeat["campaignLabel"] = "Legacy observed fixture";
                WorldTestHeartbeatApi(observedHeartbeat);
                File.Delete(Path.Combine(CampaignDirectory(legacyObservedCampaign), "campaign.json"));
                using (ReignDbConnection observedConnection = OpenCampaignConnection(observedCampaign))
                    ExecuteSql(observedConnection,
                        "UPDATE world_test_native_heartbeats SET updated_ts=9999999999;");
                Dictionary<string, object> worldTestCampaigns = WorldTestCampaignsApi(new Dictionary<string, string>());
                Dictionary<string, object> backupCampaigns = CampaignListApi();
                List<object> worldTestRows = WorldTestObjectList(worldTestCampaigns, "campaigns");
                List<object> backupRows = WorldTestObjectList(backupCampaigns, "campaigns");
                bool legacyPromoted = File.Exists(Path.Combine(CampaignDirectory(legacyObservedCampaign), "campaign.json"));
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
                            ["worldTest"] = worldTestRows, ["backups"] = backupRows, ["latest"] = LatestCampaignId()
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
                            CampaignDirectory(registeredId);
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
                CampaignsRootOverride.Value = previousCampaignsRoot;
                TryDeleteDirectory(isolatedCampaignsRoot);
            }
            return results;
        }
    }
}
