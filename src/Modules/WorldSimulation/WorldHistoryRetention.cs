using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationService
    {
        private const double RoutineWorldHistoryRetentionDays = 15.75d;
        private const double ShortRoutineWorldHistoryRetentionDays = 5d;
        private const double SignificantWorldHistoryRetentionDays = 63d;
        private const double DailyWorldHistoryAggregateDays = 126d;
        private const double ReignSeasonDays = 31.5d;
        private const int WorldHistoryCompactionBatchSize = 500;
        private const int WorldHistoryCompactionIdChunkSize = 400;

        internal sealed class WorldHistoryCompactionAggregate
        {
            public int DayKey;
            public double SeasonStartDay;
            public string EventType = string.Empty;
            public string Category = string.Empty;
            public string Retention = string.Empty;
            public string KingdomId = string.Empty;
            public int PlayerInvolved;
            public int EventCount;
            public double QuantityTotal;
            public double FirstWorldDay;
            public double LastWorldDay;
        }

        private static readonly HashSet<string> EphemeralWorldHistoryTypes =
            new HashSet<string>(new[]
            {
                "hero_relation_changed", "relationship_roll", "relationship_projection",
                "native_relation_projection", "native_relation_sync", "trait_sync",
                "heartbeat", "poll", "retry", "receipt",
                "unattributed_state_change"
            }, StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> RoutineWorldHistoryTypes =
            new HashSet<string>(new[]
            {
                "settlement_entered", "settlement_left", "troops_recruited",
                "troops_given_to_settlement", "troops_deserted", "items_looted",
                "item_sold", "loot_distributed", "caravan_transaction_completed", "barter_accepted",
                "prisoners_sold", "mobile_party_created", "mobile_party_destroyed",
                "party_leader_changed", "ship_created", "ship_destroyed",
                "ship_repaired", "ship_owner_changed",
                "issue_updated", "party_contact_interval"
            }, StringComparer.OrdinalIgnoreCase);

        private const string ShortRoutineWorldHistorySqlTypes =
            "'item_sold','caravan_transaction_completed','barter_accepted','troops_recruited'," +
            "'ship_created','ship_repaired','ship_owner_changed'";

        private static readonly HashSet<string> ConsequentialWorldHistoryTypes =
            new HashSet<string>(new[]
            {
                "public_announcement", "tournament_started", "tournament_finished", "birth", "given_birth", "child_conceived", "conception", "hero_killed",
                "hero_executed", "heroes_married", "marriage", "divorce",
                "hero_prisoner_taken", "hero_prisoner_released", "hero_escaped",
                "hero_wounded", "war_declared", "peace_made", "alliance_started",
                "alliance_ended", "treaty_signed", "tribute_agreed", "kingdom_created",
                "kingdom_destroyed", "ruling_clan_changed", "clan_changed_kingdom",
                "hero_changed_clan", "settlement_owner_changed", "rebellion_started",
                "rebellion_finished", "rebellion_ended", "rumor_created",
                "rumor_corrected", "rumor_disproven", "rumor_verified",
                "rumor_consolidated", "parentage_revealed", "affair_discovered",
                "betrayal", "lover_started", "lover_ended", "affair_started", "affair_ended"
            }, StringComparer.OrdinalIgnoreCase);

        private static void EnsureWorldHistoryRetentionSchema(ReignDbConnection connection)
        {
            WorldSimulationSchema.EnsureColumnWorldHistoryEventsRetentionClass(connection);
            WorldSimulationSchema.EnsureColumnWorldHistoryEventsPlayerInvolved(connection);
            WorldSimulationSchema.CreateIndexWhRetentionDay(connection);
            WorldSimulationSchema.CreateWorldHistoryDailyAggregates(connection);
            WorldSimulationSchema.CreateWorldHistorySeasonalAggregates(connection);
            WorldSimulationSchema.CreateIndexWhDailyAggregateDay(connection);
            WorldSimulationSchema.CreateIndexWhSeasonalAggregateDay(connection);
            string retentionRepair = ReadString(WorldSimulationStore.SelectSchemaMeta3(connection)
                .FirstOrDefault(), "value", "");
            if (retentionRepair != "1")
            {
                WorldSimulationStore.UpdateWorldHistoryEvents3(connection);
                WorldSimulationStore.UpsertSchemaMeta5(connection);
            }
            string lootRepair = ReadString(WorldSimulationStore.SelectSchemaMeta4(connection)
                .FirstOrDefault(), "value", "");
            if (lootRepair != "1")
            {
                WorldSimulationSchema.CreateWorldHistoryRemovedNpcLootIds(connection);
                WorldSimulationStore.DeleteWorldHistoryRemovedNpcLootIds(connection);
                WorldSimulationStore.InsertOrIgnoreWorldHistoryRemovedNpcLootIds(connection);
                WorldSimulationStore.DeleteWorldHistoryFts4(connection);
                WorldSimulationStore.DeleteWorldHistoryKnowledgeRules3(connection);
                WorldSimulationStore.DeleteWorldHistoryEntities3(connection);
                if (TableExists(connection, "embedding_jobs"))
                    WorldSimulationStore.DeleteEmbeddingJobs(connection);
                if (TableExists(connection, "embedding_documents"))
                    WorldSimulationStore.DeleteEmbeddingDocuments(connection);
                WorldSimulationStore.DeleteWorldHistoryEvents3(connection);
                WorldSimulationStore.DeleteWorldHistoryDailyAggregates(connection);
                WorldSimulationStore.DeleteWorldHistorySeasonalAggregates(connection);
                WorldSimulationStore.UpsertSchemaMeta6(connection);
            }
        }

        internal static string ClassifyWorldHistoryRetention(
            string eventType,
            string phase,
            string summary,
            string source,
            List<Dictionary<string, object>> entities,
            object payload)
        {
            string normalizedType = (eventType ?? string.Empty).Trim().ToLowerInvariant();
            string context = Program.NormalizeLookup(string.Join(" ", new[]
            {
                normalizedType, phase ?? "", summary ?? "", source ?? "",
                payload == null ? "" : Json.Serialize(payload)
            }));
            if (EphemeralWorldHistoryTypes.Contains(normalizedType)
                || Program.ContainsAny(context, "native relation projection", "relationship sync",
                    "trait synchronization", "telemetry heartbeat", "polling receipt"))
                return "ephemeral";
            if (ConsequentialWorldHistoryTypes.Contains(normalizedType))
                return "consequential";
            if ((normalizedType == "items_looted" || normalizedType == "loot_distributed")
                && !WorldHistoryPlayerInvolved(entities))
                return "ephemeral";
            // Routine callbacks describe non-consequential activity by contract. Their
            // free-form summaries and correlation identifiers must not upgrade them
            // merely because they mention the lasting event that surrounded them.
            if (RoutineWorldHistoryTypes.Contains(normalizedType))
                return "routine";
            if (Program.ContainsAny(context,
                    "hero prisoner taken", "hero prisoner released", "hero escaped",
                    "hero rescued", "noble captured", "noble capture",
                    "noble released", "noble escaped", "noble rescue", "hero wounded",
                    "noble wounded", "noble wound"))
                return "consequential";
            if (Program.ContainsAny(context,
                    "marriage attempt", "romantic attempt", "arranged marriage",
                    "diplomatic proposal", "diplomacy proposal", "diplomatic attempt")
                && Program.ContainsAny(context,
                    "accepted", "completed", "successful", "failed", "refused",
                    "rejected", "abandoned", "invalid", "countered", "overdue"))
                return "consequential";
            if (Program.ContainsAny(context, "romance", "lover", "affair", "betrayal",
                    "parentage", "divorce", "marriage")
                && Program.ContainsAny(context, "started", "ended", "attempt", "proposal",
                    "accepted", "failed", "refused", "abandoned", "discovered"))
                return "consequential";
            if (Program.ContainsAny(context, "war declared", "peace made", "treaty",
                    "alliance", "tribute", "guarantee", "rebellion", "civil war",
                    "kingdom created", "kingdom destroyed", "ruling clan",
                    "settlement owner"))
                return "consequential";
            return "significant";
        }

        internal static bool WorldHistoryPlayerInvolved(List<Dictionary<string, object>> entities)
        {
            return (entities ?? new List<Dictionary<string, object>>()).Any(entity =>
                ReadBool(entity, "isPlayer", false)
                || ReadBool(entity, "isMainHero", false)
                || ReadString(entity, "entity_id", "").Equals("main_hero", StringComparison.OrdinalIgnoreCase)
                || ReadFirstString(entity, "entityId", "id").Equals("main_hero", StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, object> CompactWorldHistory(
            ReignDbConnection connection, string campaignId, string timelineId,
            double currentDay, int batchSize)
        {
            batchSize = Math.Max(1, batchSize);
            Stopwatch compactionTimer = Stopwatch.StartNew();
            // WorldHistoryIngestBatchApi establishes retention schema readiness
            // before opening its data transaction. Re-entering schema setup here
            // would take pg_advisory_xact_lock for the full compaction batch and
            // block unrelated live APIs behind maintenance work.
            List<Dictionary<string, object>> expired = WorldSimulationStore.SelectWorldHistoryEvents11(connection, ShortRoutineWorldHistorySqlTypes, ShortRoutineWorldHistorySqlTypes, batchSize.ToString(CultureInfo.InvariantCulture),                new Dictionary<string, object>
                {
                    ["timeline"] = timelineId,
                    ["shortRoutineCutoff"] = currentDay - ShortRoutineWorldHistoryRetentionDays,
                    ["routineCutoff"] = currentDay - RoutineWorldHistoryRetentionDays,
                    ["significantCutoff"] = currentDay - SignificantWorldHistoryRetentionDays
                });
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<string> ids = expired.Select(row => ReadString(row, "event_id", ""))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Dictionary<string, string> kingdomsByEvent =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double> quantitiesByEvent =
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (ids.Count > 0)
            {
                WorldSimulationSchema.CreateWorldHistoryExpiredIds(connection);
                WorldSimulationStore.DeleteWorldHistoryExpiredIds(connection);
                for (int offset = 0; offset < ids.Count;
                    offset += WorldHistoryCompactionIdChunkSize)
                {
                    List<string> chunk = ids.Skip(offset)
                        .Take(WorldHistoryCompactionIdChunkSize).ToList();
                    Dictionary<string, object> parameters =
                        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    List<string> values = new List<string>();
                    for (int index = 0; index < chunk.Count; index++)
                    {
                        string name = "id" + index.ToString(
                            CultureInfo.InvariantCulture);
                        parameters[name] = chunk[index];
                        values.Add("($" + name + ")");
                    }
                    WorldSimulationStore.InsertOrIgnoreWorldHistoryExpiredIds(connection, string.Join(",", values),parameters);
                }

                foreach (Dictionary<string, object> entity in WorldSimulationStore.SelectWorldHistoryEntities4(connection))
                {
                    string eventId = ReadString(entity, "event_id", "");
                    string kingdomId = ReadString(entity, "kingdom_id", "");
                    if (!string.IsNullOrWhiteSpace(kingdomId)
                        && !kingdomsByEvent.ContainsKey(eventId))
                        kingdomsByEvent[eventId] = kingdomId;
                    quantitiesByEvent[eventId] =
                        (quantitiesByEvent.TryGetValue(eventId, out double prior)
                            ? prior : 0d)
                        + Math.Abs(ReadDouble(entity, "quantity", 0d));
                }
            }

            Dictionary<string, WorldHistoryCompactionAggregate> aggregates =
                new Dictionary<string, WorldHistoryCompactionAggregate>(
                    StringComparer.Ordinal);
            foreach (Dictionary<string, object> row in expired)
            {
                string eventId = ReadString(row, "event_id", "");
                int dayKey = (int)Math.Floor(ReadDouble(row, "world_day", 0d));
                Dictionary<string, object> eventPayload =
                    TryParseJsonObject(ReadString(row, "payload_json", "{}"))
                    ?? new Dictionary<string, object>();
                int coalescedCount = Math.Max(1, ReadInt(eventPayload, "coalescedCount", 1));
                double quantity = eventPayload.ContainsKey("quantityTotal")
                    ? ReadDouble(eventPayload, "quantityTotal", 0d)
                    : (quantitiesByEvent.TryGetValue(eventId,
                        out double entityQuantity) ? entityQuantity : 0d);
                string eventType = ReadString(row, "event_type", "");
                string category = ReadString(row, "category", "");
                string retention = ReadString(row, "retention_class", "");
                string kingdom = kingdomsByEvent.TryGetValue(eventId,
                    out string eventKingdom) ? eventKingdom : string.Empty;
                int player = ReadInt(row, "player_involved", 0);
                double worldDay = ReadDouble(row, "world_day", 0d);
                string key = dayKey.ToString(CultureInfo.InvariantCulture)
                    + "\u001f" + eventType + "\u001f" + category + "\u001f"
                    + retention + "\u001f" + kingdom + "\u001f"
                    + player.ToString(CultureInfo.InvariantCulture);
                if (!aggregates.TryGetValue(key,
                    out WorldHistoryCompactionAggregate aggregate))
                {
                    aggregate = new WorldHistoryCompactionAggregate
                    {
                        DayKey = dayKey,
                        EventType = eventType,
                        Category = category,
                        Retention = retention,
                        KingdomId = kingdom,
                        PlayerInvolved = player,
                        FirstWorldDay = worldDay,
                        LastWorldDay = worldDay
                    };
                    aggregates[key] = aggregate;
                }
                aggregate.EventCount += coalescedCount;
                aggregate.QuantityTotal += quantity;
                aggregate.FirstWorldDay = Math.Min(aggregate.FirstWorldDay,
                    worldDay);
                aggregate.LastWorldDay = Math.Max(aggregate.LastWorldDay,
                    worldDay);
            }

            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                PersistenceService.UpsertWorldHistoryCompactionAggregatesPostgreSql(connection,
                    campaignId, timelineId, ts, aggregates.Values.ToList());
            }
            else
            {
                foreach (WorldHistoryCompactionAggregate aggregate in
                    aggregates.Values)
                {
                    WorldSimulationStore.UpsertWorldHistoryDailyAggregates(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["dayKey"] = aggregate.DayKey,
                        ["type"] = aggregate.EventType,
                        ["category"] = aggregate.Category,
                        ["retention"] = aggregate.Retention,
                        ["kingdom"] = aggregate.KingdomId,
                        ["player"] = aggregate.PlayerInvolved,
                        ["count"] = aggregate.EventCount,
                        ["quantity"] = aggregate.QuantityTotal,
                        ["first"] = aggregate.FirstWorldDay,
                        ["last"] = aggregate.LastWorldDay,
                        ["ts"] = ts
                        });
                }
            }
            if (ids.Count > 0)
            {
                WorldSimulationStore.DeleteWorldHistoryFts5(connection);
                WorldSimulationStore.DeleteWorldHistoryKnowledgeRules4(connection);
                WorldSimulationStore.DeleteWorldHistoryEntities4(connection);
                if (TableExists(connection, "embedding_jobs"))
                    WorldSimulationStore.DeleteEmbeddingJobs2(connection);
                if (TableExists(connection, "embedding_documents"))
                    WorldSimulationStore.DeleteEmbeddingDocuments2(connection);
                WorldSimulationStore.DeleteWorldHistoryEvents4(connection);
            }
            MergeExpiredDailyWorldHistoryAggregates(connection, campaignId, timelineId, currentDay, ts);
            int remaining = ReadInt(WorldSimulationStore.CountWorldHistoryEvents(connection, ShortRoutineWorldHistorySqlTypes, ShortRoutineWorldHistorySqlTypes,                new Dictionary<string, object>
                {
                    ["timeline"] = timelineId,
                    ["shortRoutineCutoff"] = currentDay - ShortRoutineWorldHistoryRetentionDays,
                    ["routineCutoff"] = currentDay - RoutineWorldHistoryRetentionDays,
                    ["significantCutoff"] = currentDay - SignificantWorldHistoryRetentionDays
                }).FirstOrDefault(), "count", 0);
            if (ids.Count >= batchSize && remaining == 0
                && ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                WorldSimulationStore.Analyze(connection);
            }
            compactionTimer.Stop();
            return new Dictionary<string, object>
            {
                ["compacted"] = ids.Count,
                ["remainingBacklog"] = remaining,
                ["aggregateWrites"] = aggregates.Count,
                ["durationMs"] = compactionTimer.ElapsedMilliseconds,
                ["batchSize"] = batchSize,
                ["shortRoutineRetentionDays"] = ShortRoutineWorldHistoryRetentionDays,
                ["routineRetentionDays"] = RoutineWorldHistoryRetentionDays,
                ["significantRetentionDays"] = SignificantWorldHistoryRetentionDays
            };
        }

        private static void MergeExpiredDailyWorldHistoryAggregates(
            ReignDbConnection connection, string campaignId, string timelineId, double currentDay, long ts)
        {
            List<Dictionary<string, object>> rows = WorldSimulationStore.SelectWorldHistoryDailyAggregates(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["cutoff"] = currentDay - DailyWorldHistoryAggregateDays
                });
            Dictionary<string, WorldHistoryCompactionAggregate> seasonal =
                new Dictionary<string, WorldHistoryCompactionAggregate>(
                    StringComparer.Ordinal);
            foreach (Dictionary<string, object> row in rows)
            {
                double seasonStart = Math.Floor(ReadInt(row, "day_key", 0) / ReignSeasonDays) * ReignSeasonDays;
                string eventType = ReadString(row, "event_type", "");
                string category = ReadString(row, "category", "");
                string retention = ReadString(row, "retention_class", "");
                string kingdom = ReadString(row, "kingdom_id", "");
                int player = ReadInt(row, "player_involved", 0);
                string key = seasonStart.ToString("R", CultureInfo.InvariantCulture)
                    + "\u001f" + eventType + "\u001f" + category + "\u001f"
                    + retention + "\u001f" + kingdom + "\u001f"
                    + player.ToString(CultureInfo.InvariantCulture);
                if (!seasonal.TryGetValue(key,
                    out WorldHistoryCompactionAggregate aggregate))
                {
                    aggregate = new WorldHistoryCompactionAggregate
                    {
                        SeasonStartDay = seasonStart,
                        EventType = eventType,
                        Category = category,
                        Retention = retention,
                        KingdomId = kingdom,
                        PlayerInvolved = player,
                        FirstWorldDay = ReadDouble(row, "first_world_day", 0d),
                        LastWorldDay = ReadDouble(row, "last_world_day", 0d)
                    };
                    seasonal[key] = aggregate;
                }
                aggregate.EventCount += ReadInt(row, "event_count", 0);
                aggregate.QuantityTotal += ReadDouble(row,
                    "quantity_total", 0d);
                aggregate.FirstWorldDay = Math.Min(aggregate.FirstWorldDay,
                    ReadDouble(row, "first_world_day", 0d));
                aggregate.LastWorldDay = Math.Max(aggregate.LastWorldDay,
                    ReadDouble(row, "last_world_day", 0d));
            }
            foreach (WorldHistoryCompactionAggregate aggregate in
                seasonal.Values)
            {
                WorldSimulationStore.UpsertWorldHistorySeasonalAggregates(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId,
                        ["timeline"] = timelineId,
                        ["season"] = aggregate.SeasonStartDay,
                        ["type"] = aggregate.EventType,
                        ["category"] = aggregate.Category,
                        ["retention"] = aggregate.Retention,
                        ["kingdom"] = aggregate.KingdomId,
                        ["player"] = aggregate.PlayerInvolved,
                        ["count"] = aggregate.EventCount,
                        ["quantity"] = aggregate.QuantityTotal,
                        ["first"] = aggregate.FirstWorldDay,
                        ["last"] = aggregate.LastWorldDay,
                        ["ts"] = ts
                    });
            }
            if (rows.Count > 0)
                WorldSimulationStore.DeleteWorldHistoryDailyAggregates2(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["cutoff"] = currentDay - DailyWorldHistoryAggregateDays
                    });
        }

        internal static bool TableExists(ReignDbConnection connection, string table)
        {
            return WorldSimulationStore.SelectSqliteMaster(connection, new Dictionary<string, object> { ["name"] = table }).Any();
        }

        private static bool WorldHistoryPeriodWasCompacted(
            ReignDbConnection connection, string timelineId, double fromDay, double toDay)
        {
            double start = fromDay < 0 ? double.MinValue : fromDay;
            double end = toDay <= 0 ? double.MaxValue : toDay;
            int daily = ReadInt(WorldSimulationStore.CountWorldHistoryDailyAggregates(connection, new Dictionary<string, object> { ["timeline"] = timelineId, ["from"] = start, ["to"] = end })
                .FirstOrDefault(), "count", 0);
            int seasonal = ReadInt(WorldSimulationStore.CountWorldHistorySeasonalAggregates(connection, new Dictionary<string, object> { ["timeline"] = timelineId, ["from"] = start, ["to"] = end })
                .FirstOrDefault(), "count", 0);
            return daily + seasonal > 0;
        }

        private static Dictionary<string, object> WorldHistoryStorageDiagnostics(
            ReignDbConnection connection, string campaignId, string timelineId, double currentDay)
        {
            long databaseBytes =
                ReignPostgreSqlStorage.CampaignSizeBytes(campaignId);
            long walBytes = 0L;
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["databaseProvider"] = "postgresql",
                ["databaseName"] = ReignPostgreSqlOptions.RequiredDatabaseName,
                ["databaseBytes"] = databaseBytes,
                ["walBytes"] = walBytes,
                ["rawByRetentionClass"] = WorldSimulationStore.SelectWorldHistoryEvents12(connection, new Dictionary<string, object> { ["timeline"] = timelineId }),
                ["oldestRawDay"] = ReadDouble(WorldSimulationStore.SelectWorldHistoryEvents13(connection, new Dictionary<string, object> { ["timeline"] = timelineId }).FirstOrDefault(), "day", -1d),
                ["dailyAggregateRows"] = ReadInt(WorldSimulationStore.CountWorldHistoryDailyAggregates2(connection, new Dictionary<string, object> { ["timeline"] = timelineId }).FirstOrDefault(), "count", 0),
                ["seasonalAggregateRows"] = ReadInt(WorldSimulationStore.CountWorldHistorySeasonalAggregates2(connection, new Dictionary<string, object> { ["timeline"] = timelineId }).FirstOrDefault(), "count", 0)
            };
            result["compactionBacklog"] = ReadInt(WorldSimulationStore.CountWorldHistoryEvents(connection, ShortRoutineWorldHistorySqlTypes, ShortRoutineWorldHistorySqlTypes,                new Dictionary<string, object>
                {
                    ["timeline"] = timelineId,
                    ["shortRoutineCutoff"] = currentDay - ShortRoutineWorldHistoryRetentionDays,
                    ["routineCutoff"] = currentDay - RoutineWorldHistoryRetentionDays,
                    ["significantCutoff"] = currentDay - SignificantWorldHistoryRetentionDays
                }).FirstOrDefault(), "count", 0);
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                result["postgresTables"] = WorldSimulationStore.SelectPgClass(connection);
            }
            return result;
        }

        private static List<Dictionary<string, object>> RunWorldHistoryRetentionAssertions()
        {
            List<Dictionary<string, object>> assertions = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (name, passed, detail) =>
                assertions.Add(new Dictionary<string, object>
                {
                    ["name"] = name, ["passed"] = passed, ["detail"] = detail
                });
            string campaignId = "whr_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            string timelineId = "retention_main";
            Func<string, long, double, string, string, string, List<Dictionary<string, object>>, Dictionary<string, object>> evt =
                (id, sequence, day, type, phase, summary, entities) =>
                    new Dictionary<string, object>
                    {
                        ["eventId"] = id, ["sequence"] = sequence, ["worldDay"] = day,
                        ["eventType"] = type, ["phase"] = phase, ["summary"] = summary,
                        ["entities"] = entities ?? new List<Dictionary<string, object>>()
                    };
            List<Dictionary<string, object>> heroEntities = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["entityId"] = "noble_a", ["entityType"] = "hero",
                    ["role"] = "noble participant", ["kingdomId"] = "kingdom_a"
                }
            };
            List<Dictionary<string, object>> playerPartyEntities = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["entityId"] = "main_party", ["entityType"] = "party",
                    ["role"] = "loot receiver winner actor", ["isPlayer"] = true
                }
            };
            List<Dictionary<string, object>> initial = new List<Dictionary<string, object>>
            {
                evt("routine_visit", 1, 1d, "settlement_entered", "completed",
                    "A noble entered a town.", heroEntities),
                evt("ordinary_battle", 2, 1d, "battle_completed", "completed",
                    "An ordinary field battle ended.", heroEntities),
                evt("noble_wounded", 3, 1d, "hero_wounded", "completed",
                    "A noble was wounded.", heroEntities),
                evt("failed_marriage", 4, 1d, "marriage_proposal_failed", "failed",
                    "An arranged marriage proposal was refused.", heroEntities),
                evt("failed_diplomacy", 5, 1d, "diplomatic_proposal_failed", "failed",
                    "A diplomatic proposal was abandoned.", heroEntities),
                evt("ephemeral_projection", 6, 1d, "hero_relation_changed", "completed",
                    "A native relation projection was applied.", heroEntities),
                new Dictionary<string, object>
                {
                    ["eventId"] = "consequential_battle", ["sequence"] = 7,
                    ["worldDay"] = 1d, ["eventType"] = "battle_completed",
                    ["phase"] = "completed", ["correlationId"] = "lasting_battle",
                    ["summary"] = "A battle caused a lasting noble wound.", ["entities"] = heroEntities
                },
                new Dictionary<string, object>
                {
                    ["eventId"] = "correlated_wound", ["sequence"] = 8,
                    ["worldDay"] = 1d, ["eventType"] = "hero_wounded",
                    ["phase"] = "completed", ["correlationId"] = "lasting_battle",
                    ["summary"] = "A noble was wounded in battle.", ["entities"] = heroEntities
                },
                new Dictionary<string, object>
                {
                    ["eventId"] = "correlated_loot", ["sequence"] = 9,
                    ["worldDay"] = 1d, ["eventType"] = "loot_distributed",
                    ["phase"] = "completed", ["correlationId"] = "lasting_battle",
                    ["summary"] = "Routine loot was distributed after the battle.",
                    ["entities"] = heroEntities
                },
                new Dictionary<string, object>
                {
                    ["eventId"] = "player_party_loot", ["sequence"] = 10,
                    ["worldDay"] = 1d, ["eventType"] = "loot_distributed",
                    ["phase"] = "completed", ["summary"] = "The player's main party received loot.",
                    ["entities"] = playerPartyEntities
                }
            };
            try
            {
                Dictionary<string, object> first = WorldHistoryIngestBatchApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                        ["waitForRetention"] = true,
                        ["historyCompleteFromWorldDay"] = 1d, ["events"] = initial
                    });
                string routineClass;
                string ordinaryClass;
                string woundClass;
                string failedMarriageClass;
                string failedDiplomacyClass;
                string correlatedBattleClass;
                string correlatedLootClass;
                string playerLootClass;
                int ephemeralRows;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    routineClass = ReadString(WorldSimulationStore.SelectWorldHistoryEvents14(connection)
                        .FirstOrDefault(), "retention_class", "");
                    ordinaryClass = ReadString(WorldSimulationStore.SelectWorldHistoryEvents15(connection)
                        .FirstOrDefault(), "retention_class", "");
                    woundClass = ReadString(WorldSimulationStore.SelectWorldHistoryEvents16(connection)
                        .FirstOrDefault(), "retention_class", "");
                    failedMarriageClass = ReadString(WorldSimulationStore.SelectWorldHistoryEvents17(connection)
                        .FirstOrDefault(), "retention_class", "");
                    failedDiplomacyClass = ReadString(WorldSimulationStore.SelectWorldHistoryEvents18(connection)
                        .FirstOrDefault(), "retention_class", "");
                    correlatedBattleClass = ReadString(WorldSimulationStore.SelectWorldHistoryEvents19(connection)
                        .FirstOrDefault(), "retention_class", "");
                    correlatedLootClass = ReadString(WorldSimulationStore.SelectWorldHistoryEvents20(connection)
                        .FirstOrDefault(), "retention_class", "");
                    playerLootClass = ReadString(WorldSimulationStore.SelectWorldHistoryEvents21(connection)
                        .FirstOrDefault(), "retention_class", "");
                    ephemeralRows = ReadInt(WorldSimulationStore.CountWorldHistoryEvents2(connection)
                        .FirstOrDefault(), "count", -1);
                }
                add("retention_classes_authoritative",
                    ReadInt(first, "accepted", 0) == 8 && ReadInt(first, "ephemeral", 0) == 2
                        && routineClass == "routine" && ordinaryClass == "significant"
                        && woundClass == "consequential" && failedMarriageClass == "consequential"
                        && failedDiplomacyClass == "consequential" && playerLootClass == "routine"
                        && ephemeralRows == 0,
                    "The server classifies routine, significant, consequential, and ephemeral records without trusting client labels.");
                add("npc_loot_is_suppressed",
                    correlatedBattleClass == "consequential"
                        && string.IsNullOrWhiteSpace(correlatedLootClass)
                        && playerLootClass == "routine",
                    "A lasting battle remains permanent, NPC-only loot is discarded, and main-party loot remains available for roleplay. Battle="
                        + correlatedBattleClass + ", loot="
                        + correlatedLootClass + ", player loot=" + playerLootClass + ".");

                int routineEntityEvidence;
                int routineKnowledgeEvidence;
                int routineSearchEvidence;
                using (ReignDbConnection connection =
                    ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    routineEntityEvidence = ReadInt(WorldSimulationStore.CountWorldHistoryEntities(connection)
                        .FirstOrDefault(), "count", 0);
                    routineKnowledgeEvidence = ReadInt(WorldSimulationStore.CountWorldHistoryKnowledgeRules(connection)
                        .FirstOrDefault(), "count", 0);
                    routineSearchEvidence = ReadInt(WorldSimulationStore.CountWorldHistoryFts(connection)
                        .FirstOrDefault(), "count", 0);
                }
                add("routine_awareness_evidence_preserved_until_expiry",
                    routineEntityEvidence > 0
                        && routineKnowledgeEvidence > 0
                        && routineSearchEvidence > 0,
                    "Routine events retain structured entities, speaker-knowledge rules, and searchable evidence for their complete retention window; storage optimization never weakens world awareness or lie detection.");

                WorldHistoryIngestBatchApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                    ["waitForRetention"] = true,
                    ["events"] = new List<Dictionary<string, object>>
                    {
                        evt("day10_sale", 11, 10d, "item_sold", "completed",
                            "A routine item sale occurred.", heroEntities),
                        evt("day10_visit", 12, 10d, "settlement_entered", "completed",
                            "A routine settlement visit occurred.", heroEntities),
                        evt("day16_wound", 13, 16d, "hero_wounded", "completed",
                            "A later noble wound occurred.", heroEntities)
                    }
                });
                int shortRoutineRaw;
                int normalRoutineRaw;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    shortRoutineRaw = ReadInt(WorldSimulationStore.CountWorldHistoryEvents3(connection)
                        .FirstOrDefault(), "count", -1);
                    normalRoutineRaw = ReadInt(WorldSimulationStore.CountWorldHistoryEvents4(connection)
                        .FirstOrDefault(), "count", -1);
                }
                add("selected_routine_types_expire_at_five_days",
                    shortRoutineRaw == 0 && normalRoutineRaw == 1,
                    "Selected low-value routine records expire after five days while other routine world-awareness records keep their normal window.");

                WorldHistoryIngestBatchApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                    ["waitForRetention"] = true,
                    ["events"] = new List<Dictionary<string, object>>
                    {
                        evt("day17_visit", 14, 17d, "settlement_entered", "completed",
                            "A later routine visit.", heroEntities)
                    }
                });
                int routineRaw;
                int routineChildren;
                int routineFts;
                int routineAggregate;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    routineRaw = ReadInt(WorldSimulationStore.CountWorldHistoryEvents5(connection)
                        .FirstOrDefault(), "count", -1);
                    routineChildren = ReadInt(WorldSimulationStore.CountWorldHistoryEntities(connection)
                        .FirstOrDefault(), "count", -1);
                    routineFts = ReadInt(WorldSimulationStore.CountWorldHistoryFts(connection)
                        .FirstOrDefault(), "count", -1);
                    routineAggregate = ReadInt(WorldSimulationStore.SelectWorldHistoryDailyAggregates2(connection, new Dictionary<string, object> { ["timeline"] = timelineId })
                        .FirstOrDefault(), "count", 0);
                }
                add("routine_expires_at_15_75_days",
                    routineRaw == 0 && routineChildren == 0 && routineFts == 0
                        && routineAggregate == 1,
                    "Routine raw events and their derived rows expire after 15.75 days while compact totals remain.");

                WorldHistoryIngestBatchApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                    ["waitForRetention"] = true,
                    ["events"] = new List<Dictionary<string, object>>
                    {
                        evt("day65_battle", 15, 65d, "battle_completed", "completed",
                            "A later field battle ended.", heroEntities)
                    }
                });
                int significantRaw;
                int permanentRaw;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    significantRaw = ReadInt(WorldSimulationStore.CountWorldHistoryEvents6(connection)
                        .FirstOrDefault(), "count", -1);
                    permanentRaw = ReadInt(WorldSimulationStore.CountWorldHistoryEvents7(connection)
                        .FirstOrDefault(), "count", -1);
                }
                add("significant_expires_at_63_days",
                    significantRaw == 0 && permanentRaw == 5,
                    "Significant raw history expires after 63 days while consequential outcomes and failed attempts remain.");

                Dictionary<string, object> expiredClaim = WorldHistoryVerifyApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                        ["claim"] = "I entered the town on day one.",
                        ["claimantId"] = "noble_a", ["speakerId"] = "noble_a",
                        ["fromDay"] = 1d, ["toDay"] = 2d, ["worldDay"] = 65d
                    });
                add("expired_history_never_contradicts",
                    ReadString(expiredClaim, "objectiveVerdict", "") == "history_expired",
                    "A claim covering compacted raw history returns history_expired instead of a false contradiction. Actual: "
                        + ReadString(expiredClaim, "objectiveVerdict", "<missing>") + ".");

                WorldHistoryIngestBatchApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                    ["waitForRetention"] = true,
                    ["events"] = new List<Dictionary<string, object>>
                    {
                        evt("day128_wound", 16, 128d, "hero_wounded", "completed",
                            "A later noble wound occurred.", heroEntities)
                    }
                });
                int oldDaily;
                int seasonal;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    oldDaily = ReadInt(WorldSimulationStore.CountWorldHistoryDailyAggregates3(connection)
                        .FirstOrDefault(), "count", -1);
                    seasonal = ReadInt(WorldSimulationStore.SelectWorldHistorySeasonalAggregates(connection)
                        .FirstOrDefault(), "count", 0);
                }
                add("daily_totals_merge_to_seasons",
                    oldDaily == 0 && seasonal >= 2,
                    "Daily compact totals older than 126 days merge into permanent 31.5-day seasonal totals.");
            }
            catch (Exception ex)
            {
                add("retention_fixture_exception", false, ex.Message);
            }
            finally
            {
                bool cleanupSucceeded = false;
                string cleanupError = string.Empty;
                PersistenceService.TryDeleteDirectory(Program.CampaignDirectory(campaignId));
                int consecutiveAbsentChecks = 0;
                DateTime cleanupDeadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < cleanupDeadline
                    && consecutiveAbsentChecks < 4)
                {
                    try
                    {
                        ReignPostgreSqlStorage.ClearAllPools();
                        // Drop unconditionally. A worker can recreate the schema
                        // between a registry listing and the old conditional drop,
                        // making a test-only campaign reappear throughout the
                        // quiescence window.
                        ReignPostgreSqlStorage.DropCampaign(campaignId);
                        PersistenceService.TryDeleteDirectory(Program.CampaignDirectory(campaignId));
                        System.Threading.Thread.Sleep(250);
                        bool reappeared =
                            ReignPostgreSqlStorage.CampaignExists(campaignId)
                            || Directory.Exists(Program.CampaignDirectory(campaignId));
                        consecutiveAbsentChecks = reappeared
                            ? 0 : consecutiveAbsentChecks + 1;
                    }
                    catch (Exception ex)
                    {
                        cleanupError = ex.Message;
                        consecutiveAbsentChecks = 0;
                        System.Threading.Thread.Sleep(250);
                    }
                }
                cleanupSucceeded = consecutiveAbsentChecks >= 4;
                add("retention_fixture_cleanup", cleanupSucceeded,
                    cleanupSucceeded
                        ? "The isolated PostgreSQL retention fixture remained absent for a full quiescence window after the test."
                        : "The isolated PostgreSQL retention fixture could not be removed: "
                            + cleanupError);
            }
            return assertions;
        }
    }
}
