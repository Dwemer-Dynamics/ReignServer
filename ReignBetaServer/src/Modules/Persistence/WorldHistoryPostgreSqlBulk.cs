using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Npgsql;
using NpgsqlTypes;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private sealed class PreparedWorldHistoryEvent
        {
            public string EventId = string.Empty;
            public long Sequence;
            public double WorldDay;
            public string EventType = string.Empty;
            public string Category = string.Empty;
            public string Phase = string.Empty;
            public string CorrelationId = string.Empty;
            public string LocationId = string.Empty;
            public string LocationName = string.Empty;
            public string Dissemination = string.Empty;
            public string Retention = string.Empty;
            public bool PlayerInvolved;
            public string Summary = string.Empty;
            public string SemanticText = string.Empty;
            public string Source = string.Empty;
            public bool IsComplete;
            public string Checksum = string.Empty;
            public string PayloadJson = string.Empty;
            public string CreatedUtc = string.Empty;
            public string SubjectId = string.Empty;
            public List<Dictionary<string, object>> Entities =
                new List<Dictionary<string, object>>();
            public List<Dictionary<string, object>> KnowledgeRules =
                new List<Dictionary<string, object>>();

            public Dictionary<string, object> EventParameters(
                string campaignId, string timelineId)
            {
                return new Dictionary<string, object>
                {
                    ["id"] = EventId,
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId,
                    ["sequence"] = Sequence,
                    ["day"] = WorldDay,
                    ["type"] = EventType,
                    ["category"] = Category,
                    ["phase"] = Phase,
                    ["correlation"] = CorrelationId,
                    ["location"] = LocationId,
                    ["locationName"] = LocationName,
                    ["dissemination"] = Dissemination,
                    ["retention"] = Retention,
                    ["player"] = PlayerInvolved ? 1 : 0,
                    ["summary"] = Summary,
                    ["semantic"] = SemanticText,
                    ["source"] = Source,
                    ["complete"] = IsComplete ? 1 : 0,
                    ["checksum"] = Checksum,
                    ["payload"] = PayloadJson,
                    ["created"] = CreatedUtc,
                    ["subject"] = SubjectId
                };
            }
        }

        private static PreparedWorldHistoryEvent PrepareWorldHistoryEvent(
            Dictionary<string, object> item, string createdUtc)
        {
            string eventType = FirstNonEmpty(ReadFirstString(item,
                "eventType", "event_type", "type"), "unknown");
            string phase = FirstNonEmpty(ReadString(item, "phase", ""),
                "completed");
            string locationName = ReadFirstString(item, "locationName",
                "location_name", "settlementName");
            string dissemination = FirstNonEmpty(ReadFirstString(item,
                "disseminationClass", "dissemination_class"),
                IsMajorWorldHistoryEvent(eventType)
                    ? "major_world"
                    : "ordinary");
            string summary = FirstNonEmpty(ReadFirstString(item, "summary",
                "text"), eventType);
            List<Dictionary<string, object>> entities =
                CompactWorldHistoryEntities(eventType,
                    ReadDictionaryList(item, "entities"));
            object payloadValue = item.ContainsKey("payload")
                ? item["payload"]
                : item;
            string payloadJson = payloadValue is string rawPayload
                ? rawPayload
                : Json.Serialize(payloadValue);
            Dictionary<string, object> payloadObject = payloadValue
                as Dictionary<string, object>
                ?? TryParseJsonObject(payloadJson)
                ?? new Dictionary<string, object>();
            double worldDay = ReadDouble(item, "worldDay",
                ReadDouble(item, "world_day", 0d));
            string source = FirstNonEmpty(ReadString(item, "source", ""),
                "native");

            return new PreparedWorldHistoryEvent
            {
                EventId = ReadFirstString(item, "eventId", "event_id", "id"),
                Sequence = ReadLong(item, "sequence", 0),
                WorldDay = worldDay,
                EventType = eventType,
                Category = FirstNonEmpty(ReadFirstString(item, "category",
                    "eventCategory"), ClassifyWorldHistoryCategory(eventType)),
                Phase = phase,
                CorrelationId = ReadFirstString(item, "correlationId",
                    "correlation_id"),
                LocationId = ReadFirstString(item, "locationId",
                    "location_id", "settlementId"),
                LocationName = locationName,
                Dissemination = dissemination,
                Retention = ClassifyWorldHistoryRetention(eventType, phase,
                    summary, source, entities, payloadValue),
                PlayerInvolved = WorldHistoryPlayerInvolved(entities),
                Summary = summary,
                SemanticText = FirstNonEmpty(ReadFirstString(item,
                    "semanticText", "semantic_text"),
                    BuildWorldHistorySemanticText(eventType, phase, summary,
                        entities, locationName)),
                Source = source,
                IsComplete = ReadBool(item, "isComplete", true),
                Checksum = ReadFirstString(item, "checksum", "hash"),
                PayloadJson = payloadJson,
                CreatedUtc = createdUtc,
                SubjectId = eventType.Equals("social_outcome",
                    StringComparison.OrdinalIgnoreCase)
                    ? ReadFirstString(payloadObject, "subjectId", "heroId")
                    : string.Empty,
                Entities = entities,
                KnowledgeRules = ReadDictionaryList(item, "knowledgeRules")
            };
        }

        private static List<Dictionary<string, object>>
            BuildWorldHistoryKnowledgeRules(string eventType, double day,
                string dissemination,
                List<Dictionary<string, object>> entities,
                List<Dictionary<string, object>> supplied)
        {
            List<Dictionary<string, object>> rules = supplied == null
                ? new List<Dictionary<string, object>>()
                : supplied.Select(rule =>
                    new Dictionary<string, object>(rule,
                        StringComparer.OrdinalIgnoreCase)).ToList();
            if (rules.Count > 0) return rules;

            foreach (string participant in (entities
                    ?? new List<Dictionary<string, object>>())
                .Where(entity => IsImmediateHistoryParticipant(eventType,
                    entity))
                .Select(entity => ReadFirstString(entity, "entityId",
                    "entity_id", "id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                rules.Add(new Dictionary<string, object>
                {
                    ["audienceType"] = "entity",
                    ["audienceId"] = participant,
                    ["availableDay"] = day,
                    ["acquisitionMode"] = "participant"
                });
            }

            bool participantOnly = string.Equals(dissemination,
                    "participants", StringComparison.OrdinalIgnoreCase)
                || string.Equals(dissemination, "participant_only",
                    StringComparison.OrdinalIgnoreCase);
            double kingdomDelay = string.Equals(dissemination,
                "major_world", StringComparison.OrdinalIgnoreCase) ? 1d : 3d;
            foreach (string kingdom in participantOnly
                ? Enumerable.Empty<string>()
                : (entities ?? new List<Dictionary<string, object>>())
                    .Select(entity => ReadFirstString(entity, "kingdomId",
                        "kingdom_id"))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                rules.Add(new Dictionary<string, object>
                {
                    ["audienceType"] = "kingdom",
                    ["audienceId"] = kingdom,
                    ["availableDay"] = day + kingdomDelay,
                    ["acquisitionMode"] = "realm_news"
                });
            }
            if (string.Equals(dissemination, "major_world",
                StringComparison.OrdinalIgnoreCase))
            {
                rules.Add(new Dictionary<string, object>
                {
                    ["audienceType"] = "global",
                    ["audienceId"] = "all",
                    ["availableDay"] = day + 3d,
                    ["acquisitionMode"] = "major_world_news"
                });
            }
            return rules;
        }

        private static int InsertWorldHistoryPostgreSqlBulk(
            ReignDbConnection connection, ReignDbTransaction transaction,
            string campaignId, string timelineId,
            List<PreparedWorldHistoryEvent> events)
        {
            if (events == null || events.Count == 0) return 0;
            NpgsqlConnection postgres = connection as NpgsqlConnection;
            NpgsqlTransaction postgresTransaction = transaction
                as NpgsqlTransaction;
            if (postgres == null || postgresTransaction == null)
                throw new InvalidOperationException(
                    "World History PostgreSQL bulk ingest requires Npgsql.");

            using (NpgsqlCommand setup = new NpgsqlCommand(@"
CREATE TEMP TABLE IF NOT EXISTS wh_event_stage
    (LIKE world_history_events INCLUDING DEFAULTS) ON COMMIT PRESERVE ROWS;
CREATE TEMP TABLE IF NOT EXISTS wh_entity_stage
    (LIKE world_history_entities INCLUDING DEFAULTS) ON COMMIT PRESERVE ROWS;
CREATE TEMP TABLE IF NOT EXISTS wh_rule_stage
    (LIKE world_history_knowledge_rules INCLUDING DEFAULTS) ON COMMIT PRESERVE ROWS;
CREATE TEMP TABLE IF NOT EXISTS wh_fts_stage
    (LIKE world_history_fts INCLUDING DEFAULTS) ON COMMIT PRESERVE ROWS;
CREATE TEMP TABLE IF NOT EXISTS wh_inserted_ids(event_id text PRIMARY KEY)
    ON COMMIT PRESERVE ROWS;
TRUNCATE TABLE wh_event_stage,wh_entity_stage,wh_rule_stage,wh_fts_stage,
    wh_inserted_ids;", postgres, postgresTransaction))
            {
                setup.ExecuteNonQuery();
            }

            CopyWorldHistoryEvents(postgres, campaignId, timelineId, events);
            CopyWorldHistoryEntities(postgres, events);
            CopyWorldHistoryRules(postgres, events);
            CopyWorldHistoryFts(postgres, events);

            using (NpgsqlCommand merge = new NpgsqlCommand(@"
WITH inserted AS (
    INSERT INTO world_history_events(
        event_id,campaign_id,timeline_id,sequence,world_day,event_type,
        category,phase,correlation_id,location_id,location_name,
        dissemination_class,retention_class,player_involved,summary,
        semantic_text,source,is_complete,checksum,payload_json,created_utc,
        subject_id)
    SELECT event_id,campaign_id,timeline_id,sequence,world_day,event_type,
        category,phase,correlation_id,location_id,location_name,
        dissemination_class,retention_class,player_involved,summary,
        semantic_text,source,is_complete,checksum,payload_json,created_utc,
        subject_id
    FROM wh_event_stage
    ON CONFLICT(event_id) DO NOTHING
    RETURNING event_id
)
INSERT INTO wh_inserted_ids(event_id)
SELECT event_id FROM inserted;

INSERT INTO world_history_entities(
    event_id,ordinal,entity_id,entity_type,name_snapshot,role,side,party_id,
    clan_id,kingdom_id,quantity,before_json,after_json,payload_json)
SELECT s.event_id,s.ordinal,s.entity_id,s.entity_type,s.name_snapshot,s.role,
    s.side,s.party_id,s.clan_id,s.kingdom_id,s.quantity,s.before_json,
    s.after_json,s.payload_json
FROM wh_entity_stage s
JOIN wh_inserted_ids i ON i.event_id=s.event_id;

INSERT INTO world_history_knowledge_rules(
    rule_id,event_id,audience_type,audience_id,available_day,
    acquisition_mode,confidence)
SELECT s.rule_id,s.event_id,s.audience_type,s.audience_id,s.available_day,
    s.acquisition_mode,s.confidence
FROM wh_rule_stage s
JOIN wh_inserted_ids i ON i.event_id=s.event_id;

INSERT INTO world_history_fts(event_id,text,entities,roles,location,event_type)
SELECT s.event_id,s.text,s.entities,s.roles,s.location,s.event_type
FROM wh_fts_stage s
JOIN wh_inserted_ids i ON i.event_id=s.event_id;",
                postgres, postgresTransaction))
            {
                merge.ExecuteNonQuery();
            }

            using (NpgsqlCommand count = new NpgsqlCommand(
                "SELECT COUNT(*) FROM wh_inserted_ids;", postgres,
                postgresTransaction))
            {
                return Convert.ToInt32(count.ExecuteScalar());
            }
        }

        private static void CopyWorldHistoryEvents(NpgsqlConnection connection,
            string campaignId, string timelineId,
            IEnumerable<PreparedWorldHistoryEvent> events)
        {
            using (NpgsqlBinaryImporter writer = connection.BeginBinaryImport(@"
COPY wh_event_stage(event_id,campaign_id,timeline_id,sequence,world_day,
event_type,category,phase,correlation_id,location_id,location_name,
dissemination_class,retention_class,player_involved,summary,semantic_text,
source,is_complete,checksum,payload_json,created_utc,subject_id)
FROM STDIN (FORMAT BINARY)"))
            {
                foreach (PreparedWorldHistoryEvent item in events)
                {
                    writer.StartRow();
                    WriteText(writer, item.EventId);
                    WriteText(writer, campaignId);
                    WriteText(writer, timelineId);
                    writer.Write(item.Sequence, NpgsqlDbType.Bigint);
                    writer.Write(item.WorldDay, NpgsqlDbType.Double);
                    WriteText(writer, item.EventType);
                    WriteText(writer, item.Category);
                    WriteText(writer, item.Phase);
                    WriteText(writer, item.CorrelationId);
                    WriteText(writer, item.LocationId);
                    WriteText(writer, item.LocationName);
                    WriteText(writer, item.Dissemination);
                    WriteText(writer, item.Retention);
                    writer.Write(item.PlayerInvolved ? 1L : 0L,
                        NpgsqlDbType.Bigint);
                    WriteText(writer, item.Summary);
                    WriteText(writer, item.SemanticText);
                    WriteText(writer, item.Source);
                    writer.Write(item.IsComplete ? 1L : 0L,
                        NpgsqlDbType.Bigint);
                    WriteText(writer, item.Checksum);
                    WriteText(writer, item.PayloadJson);
                    WriteText(writer, item.CreatedUtc);
                    WriteText(writer, item.SubjectId);
                }
                writer.Complete();
            }
        }

        private static void CopyWorldHistoryEntities(NpgsqlConnection connection,
            IEnumerable<PreparedWorldHistoryEvent> events)
        {
            using (NpgsqlBinaryImporter writer = connection.BeginBinaryImport(@"
COPY wh_entity_stage(event_id,ordinal,entity_id,entity_type,name_snapshot,
role,side,party_id,clan_id,kingdom_id,quantity,before_json,after_json,
payload_json) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (PreparedWorldHistoryEvent item in events)
                {
                    int ordinal = 0;
                    foreach (Dictionary<string, object> entity in item.Entities)
                    {
                        writer.StartRow();
                        WriteText(writer, item.EventId);
                        writer.Write((long)ordinal++, NpgsqlDbType.Bigint);
                        WriteText(writer, ReadFirstString(entity, "entityId",
                            "entity_id", "id"));
                        WriteText(writer, ReadFirstString(entity, "entityType",
                            "entity_type", "type"));
                        WriteText(writer, ReadFirstString(entity, "name",
                            "nameSnapshot", "name_snapshot"));
                        WriteText(writer, ReadString(entity, "role",
                            "participant"));
                        WriteText(writer, ReadString(entity, "side", ""));
                        WriteText(writer, ReadFirstString(entity, "partyId",
                            "party_id"));
                        WriteText(writer, ReadFirstString(entity, "clanId",
                            "clan_id"));
                        WriteText(writer, ReadFirstString(entity, "kingdomId",
                            "kingdom_id"));
                        writer.Write(ReadDouble(entity, "quantity", 0d),
                            NpgsqlDbType.Double);
                        WriteText(writer, Json.Serialize(entity.ContainsKey(
                            "before") ? entity["before"]
                            : new Dictionary<string, object>()));
                        WriteText(writer, Json.Serialize(entity.ContainsKey(
                            "after") ? entity["after"]
                            : new Dictionary<string, object>()));
                        WriteText(writer, Json.Serialize(entity));
                    }
                }
                writer.Complete();
            }
        }

        private static void CopyWorldHistoryRules(NpgsqlConnection connection,
            IEnumerable<PreparedWorldHistoryEvent> events)
        {
            using (NpgsqlBinaryImporter writer = connection.BeginBinaryImport(@"
COPY wh_rule_stage(rule_id,event_id,audience_type,audience_id,available_day,
acquisition_mode,confidence) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (PreparedWorldHistoryEvent item in events)
                {
                    List<Dictionary<string, object>> rules =
                        BuildWorldHistoryKnowledgeRules(item.EventType,
                            item.WorldDay, item.Dissemination, item.Entities,
                            item.KnowledgeRules);
                    int ordinal = 0;
                    foreach (Dictionary<string, object> rule in rules)
                    {
                        string audienceType = FirstNonEmpty(ReadFirstString(
                            rule, "audienceType", "audience_type"), "entity");
                        string audienceId = ReadFirstString(rule, "audienceId",
                            "audience_id");
                        writer.StartRow();
                        WriteText(writer, item.EventId + ":" + ordinal++ + ":"
                            + audienceType + ":" + audienceId);
                        WriteText(writer, item.EventId);
                        WriteText(writer, audienceType);
                        WriteText(writer, audienceId);
                        writer.Write(ReadDouble(rule, "availableDay",
                            ReadDouble(rule, "available_day", item.WorldDay)),
                            NpgsqlDbType.Double);
                        WriteText(writer, ReadFirstString(rule,
                            "acquisitionMode", "acquisition_mode"));
                        writer.Write(ReadDouble(rule, "confidence", 1d),
                            NpgsqlDbType.Double);
                    }
                }
                writer.Complete();
            }
        }

        private static void CopyWorldHistoryFts(NpgsqlConnection connection,
            IEnumerable<PreparedWorldHistoryEvent> events)
        {
            using (NpgsqlBinaryImporter writer = connection.BeginBinaryImport(@"
COPY wh_fts_stage(event_id,text,entities,roles,location,event_type)
FROM STDIN (FORMAT BINARY)"))
            {
                foreach (PreparedWorldHistoryEvent item in events)
                {
                    writer.StartRow();
                    WriteText(writer, item.EventId);
                    WriteText(writer, item.SemanticText + " " + item.Summary);
                    WriteText(writer, string.Join(" ", item.Entities.Select(
                        entity => ReadFirstString(entity, "entityId", "name",
                            "nameSnapshot"))));
                    WriteText(writer, string.Join(" ", item.Entities.Select(
                        entity => ReadString(entity, "role", ""))));
                    WriteText(writer, item.LocationName);
                    WriteText(writer, item.EventType);
                }
                writer.Complete();
            }
        }

        private static void WriteText(NpgsqlBinaryImporter writer, string value)
        {
            writer.Write(value ?? string.Empty, NpgsqlDbType.Text);
        }

        private static void UpsertWorldHistoryCompactionAggregatesPostgreSql(
            ReignDbConnection connection, string campaignId,
            string timelineId, long timestamp,
            List<WorldHistoryCompactionAggregate> aggregates)
        {
            if (aggregates == null || aggregates.Count == 0) return;
            NpgsqlConnection postgres = connection as NpgsqlConnection;
            if (postgres == null)
                throw new InvalidOperationException(
                    "World History aggregate batching requires Npgsql.");
            const string sql = @"INSERT INTO world_history_daily_aggregates(
campaign_id,timeline_id,day_key,event_type,category,retention_class,kingdom_id,
player_involved,event_count,quantity_total,first_world_day,last_world_day,
updated_ts)
VALUES(@campaign,@timeline,@dayKey,@type,@category,@retention,@kingdom,@player,
@count,@quantity,@first,@last,@ts)
ON CONFLICT(campaign_id,timeline_id,day_key,event_type,category,
retention_class,kingdom_id,player_involved)
DO UPDATE SET
event_count=world_history_daily_aggregates.event_count+EXCLUDED.event_count,
quantity_total=world_history_daily_aggregates.quantity_total+EXCLUDED.quantity_total,
first_world_day=LEAST(world_history_daily_aggregates.first_world_day,
    EXCLUDED.first_world_day),
last_world_day=GREATEST(world_history_daily_aggregates.last_world_day,
    EXCLUDED.last_world_day),updated_ts=EXCLUDED.updated_ts;";
            using (NpgsqlBatch batch = new NpgsqlBatch(postgres))
            {
                batch.Timeout = 120;
                foreach (WorldHistoryCompactionAggregate aggregate in aggregates)
                {
                    NpgsqlBatchCommand command = new NpgsqlBatchCommand(sql);
                    command.Parameters.AddWithValue("campaign", campaignId);
                    command.Parameters.AddWithValue("timeline", timelineId);
                    command.Parameters.AddWithValue("dayKey",
                        (long)aggregate.DayKey);
                    command.Parameters.AddWithValue("type", aggregate.EventType);
                    command.Parameters.AddWithValue("category",
                        aggregate.Category);
                    command.Parameters.AddWithValue("retention",
                        aggregate.Retention);
                    command.Parameters.AddWithValue("kingdom",
                        aggregate.KingdomId);
                    command.Parameters.AddWithValue("player",
                        (long)aggregate.PlayerInvolved);
                    command.Parameters.AddWithValue("count",
                        (long)aggregate.EventCount);
                    command.Parameters.AddWithValue("quantity",
                        aggregate.QuantityTotal);
                    command.Parameters.AddWithValue("first",
                        aggregate.FirstWorldDay);
                    command.Parameters.AddWithValue("last",
                        aggregate.LastWorldDay);
                    command.Parameters.AddWithValue("ts", timestamp);
                    batch.BatchCommands.Add(command);
                }
                batch.ExecuteNonQuery();
            }
        }
    }
}
