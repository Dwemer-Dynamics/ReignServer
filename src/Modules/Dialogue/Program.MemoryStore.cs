using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Npgsql;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> IngestEvent(Dictionary<string, object> payload)
        {
            string campaignId = ReadString(payload, "campaignId", "default");
            string eventType = ReadString(payload, "eventType", "unknown");
            List<string> actorIds = ReadStringList(payload, "actorIds");
            string locationId = ReadString(payload, "locationId", "");
            string summary = ReadString(payload, "summary", ReadString(payload, "text", ""));
            List<string> tags = SimpleTags(summary);
            long id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            Dictionary<string, object> row = new Dictionary<string, object>
            {
                ["id"] = id,
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["campaignId"] = campaignId,
                ["eventType"] = eventType,
                ["actorIds"] = actorIds,
                ["locationId"] = locationId,
                ["payload"] = payload
            };

            AppendJsonLineToPath(CampaignFile(campaignId, "world", "events.jsonl"), row);
            bool volatileCurrentState =
                ReadBool(payload, "volatileCurrentState", false)
                || eventType.Equals(
                    "daily_world_snapshot",
                    StringComparison.OrdinalIgnoreCase);
            Dictionary<string, object> memoryPayload = new Dictionary<string, object>(payload ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase)
            {
                ["campaignId"] = campaignId,
                ["eventId"] = Convert.ToString(id, CultureInfo.InvariantCulture),
                ["ts"] = ReadLong(row, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                ["eventType"] = eventType,
                ["participants"] = actorIds,
                ["known_by"] = actorIds,
                ["locationId"] = locationId,
                ["summary"] = summary,
                ["tags"] = tags
            };
            Dictionary<string, object> memoryStore;
            if (volatileCurrentState)
            {
                WriteJsonObject(
                    CampaignFile(
                        campaignId, "world",
                        "current_native_world_state.json"),
                    memoryPayload);
                memoryStore = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["stored"] = false,
                    ["reason"] =
                        "volatile_current_state_not_durable_memory"
                };
            }
            else
            {
                memoryStore = StoreWorldMemoryEvent(
                    memoryPayload, "events_ingest");
            }

            foreach (string ownerId in volatileCurrentState
                ? new List<string>()
                : actorIds)
            {
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    Dictionary<string, object> memoryRow = new Dictionary<string, object>
                    {
                        ["id"] = Guid.NewGuid().ToString("N"),
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ["campaignId"] = campaignId,
                        ["ownerId"] = ownerId,
                        ["tags"] = tags,
                        ["text"] = summary,
                        ["sourceEventId"] = id
                    };
                    AppendJsonLineToPath(CampaignFile(campaignId, "world", "memories.jsonl"), memoryRow);
                    if (!string.IsNullOrWhiteSpace(ownerId))
                    {
                        AppendJsonLineToPath(CharacterFile(campaignId, ownerId, "memory", "memories.jsonl"), memoryRow);
                    }
                }
            }

            Console.WriteLine("Ingested event " + eventType + " campaign=" + campaignId + " tags=" + string.Join(",", tags));
            LogOperational("event.ingest", new Dictionary<string, object>
            {
                ["eventId"] = id,
                ["campaignId"] = campaignId,
                ["eventType"] = eventType,
                ["actorIds"] = actorIds,
                ["locationId"] = locationId,
                ["tags"] = tags
            });
            return new Dictionary<string, object> { ["ok"] = true, ["eventId"] = id, ["tags"] = tags, ["memoryStore"] = memoryStore };
        }

        private static Dictionary<string, object> BuildContext(Dictionary<string, object> payload)
        {
            string campaignId = ReadString(payload, "campaignId", "default");
            List<string> actorIds = ReadStringList(payload, "actorIds");
            string query = ReadString(payload, "query", "");
            int limit = ReadInt(payload, "limit", 12);
            List<string> queryTags = SimpleTags(query);

            List<Dictionary<string, object>> memories = ReadCampaignMemoryLines(campaignId, actorIds)
                .Where(x => actorIds.Count == 0 || actorIds.Contains(ReadString(x, "ownerId", "")))
                .Where(x => queryTags.Count == 0 || ReadStringList(x, "tags").Any(queryTags.Contains))
                .OrderByDescending(x => ReadLong(x, "ts", 0))
                .Take(limit)
                .ToList();

            LogOperational("context.build", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["actorIds"] = actorIds,
                ["queryTags"] = queryTags,
                ["resultCount"] = memories.Count
            });

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["queryTags"] = queryTags,
                ["memories"] = memories
            };
        }

        private static Dictionary<string, object> MemoryEventApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            Dictionary<string, object> stored = StoreWorldMemoryEvent(payload, "memory_event_api");
            string campaignId = ReadString(payload, "campaignId", "default");
            LogOperational("memory.event", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["eventId"] = ReadString(stored, "eventId", ""),
                ["eventType"] = ReadFirstString(payload, "eventType", "event_type", "type"),
                ["memoryCount"] = ReadInt(stored, "memoryCount", 0)
            });
            return stored;
        }

        internal static Dictionary<string, object> MemoryConversationFinishedApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string npcId = ReadFirstString(payload, "npcId", "heroStringId", "heroId", "speakerHeroStringId");
            string playerId = ReadFirstString(payload, "playerId", "mainHeroStringId");
            string text = ReadFirstString(payload, "conversationText", "conversation_text", "summary", "text");
            Dictionary<string, object> evt = new Dictionary<string, object>(payload, StringComparer.OrdinalIgnoreCase)
            {
                ["campaignId"] = campaignId,
                ["eventType"] = ReadFirstString(payload, "eventType", "event_type", "type")
            };
            if (string.IsNullOrWhiteSpace(ReadString(evt, "eventType", "")))
            {
                evt["eventType"] = "conversation_finished";
            }

            evt["summary"] = string.IsNullOrWhiteSpace(text) ? "Conversation finished." : LimitText(text, 1200);
            evt["participants"] = MergeStringLists(ReadStringList(payload, "participants"), new[] { npcId, playerId });
            evt["known_by"] = MergeStringLists(ReadStringList(payload, "known_by"), new[] { npcId, playerId });
            evt["visibility"] = ReadString(payload, "visibility", "private");
            evt["importance"] = ReadDouble(payload, "importance", 0.5d);
            long ts = ReadLong(payload, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            string groupKey = BuildMemoryGroupKey(payload, npcId, playerId, ReadFirstString(payload, "locationId", "currentSettlementId", "settlementId"), "conversation", ts);
            evt["memoryGroupKey"] = groupKey;
            Dictionary<string, object> stored = StoreWorldMemoryEvent(evt, "memory_conversation_finished");
            stored["consolidation"] = ConsolidateNpcMemory(campaignId, npcId, true, groupKey);
            return stored;
        }

        private static Dictionary<string, object> MemoryActionResultApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            return StoreActionResultMemory(payload, "memory_action_result_api");
        }

        private static Dictionary<string, object> MemoryBuildContextApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string npcId = ReadFirstString(payload, "npcId", "heroStringId", "heroId", "speakerHeroStringId", "characterId");
            string playerId = ReadFirstString(payload, "playerId", "mainHeroStringId");
            string locationId = ReadFirstString(payload, "locationId", "currentSettlementId", "settlementId");
            string topic = ReadFirstString(payload, "currentTopic", "current_topic", "query", "playerText", "text", "message");
            int tokenBudget = Math.Max(400, Math.Min(8000, ReadInt(payload, "tokenBudget", ReadInt(payload, "token_budget", 2500))));

            Dictionary<string, object> packet = BuildNpcMemoryPacket(campaignId, npcId, playerId, locationId, topic, tokenBudget, payload);
            packet["ok"] = true;
            packet["campaignId"] = campaignId;
            packet["npcId"] = npcId;
            packet["playerId"] = playerId;
            return packet;
        }

        private static Dictionary<string, object> MemoryConsolidateApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string npcId = ReadFirstString(payload, "npcId", "heroStringId", "heroId", "speakerHeroStringId", "characterId");
            if (string.IsNullOrWhiteSpace(npcId))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "npcId or heroStringId is required." };
            }

            string groupKey = ReadFirstString(payload, "memoryGroupKey", "groupKey", "conversationId", "sceneId", "eventId");
            return ConsolidateNpcMemory(campaignId, npcId, ReadBool(payload, "force", true), groupKey);
        }

        private static string CampaignMemoryDbPath(string campaignId)
        {
            return "postgresql://"
                + ReignPostgreSqlOptions.RequiredDatabaseName + "/"
                + ReignPostgreSqlStorage.CampaignSchemaName(campaignId);
        }

        internal static bool HasCampaignPostgreSqlStorage(string campaignId)
        {
            try
            {
                return ReignPostgreSqlStorage.CampaignExists(campaignId);
            }
            catch
            {
                return false;
            }
        }

        internal static Dictionary<string, object> StoreWorldMemoryEvent(Dictionary<string, object> payload, string source, ReignDbConnection existingConnection = null)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            long ts = ReadLong(payload, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            string eventId = FirstNonEmpty(ReadFirstString(payload, "event_id", "eventId", "id"), "evt_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string eventType = FirstNonEmpty(ReadFirstString(payload, "eventType", "event_type", "type"), "unknown");
            string summary = FirstNonEmpty(ReadFirstString(payload, "summary", "text", "conversation_text", "conversationText", "message"), eventType);
            string locationId = ReadFirstString(payload, "location_id", "locationId", "settlementId", "currentSettlementId");
            double worldDay = ReadDouble(payload, "world_day", ReadDouble(payload, "worldDay", 0d));
            double importance = ClampDouble(ReadDouble(payload, "importance", 0.5d), 0d, 1d);
            double reliability = ClampDouble(ReadDouble(payload, "source_reliability", ReadDouble(payload, "sourceReliability", 1d)), 0d, 1d);
            double confidence = ClampDouble(ReadDouble(payload, "confidence", 1d), 0d, 1d);
            string visibility = FirstNonEmpty(ReadString(payload, "visibility", ""), IsPrivateEventType(eventType) ? "private" : "public");
            List<string> participants = MergeStringLists(ReadStringList(payload, "participants"), ReadStringList(payload, "actorIds"));
            List<string> witnesses = ReadStringList(payload, "witnesses");
            List<string> knownBy = MergeStringLists(ReadStringList(payload, "known_by"), ReadStringList(payload, "knownBy"));
            if (knownBy.Count == 0 && string.Equals(visibility, "private", StringComparison.OrdinalIgnoreCase))
            {
                knownBy = MergeStringLists(participants, witnesses);
            }

            List<string> heardAsRumorBy = MergeStringLists(ReadStringList(payload, "heard_as_rumor_by"), ReadStringList(payload, "heardAsRumorBy"));
            List<string> hiddenFrom = MergeStringLists(ReadStringList(payload, "hidden_from"), ReadStringList(payload, "hiddenFrom"));
            object aboutEntitiesValue = payload.ContainsKey("about_entities") ? payload["about_entities"] : payload.ContainsKey("aboutEntities") ? payload["aboutEntities"] : null;
            List<string> aboutEntityIds = ExtractEntityIds(aboutEntitiesValue);
            if (aboutEntityIds.Count == 0)
            {
                aboutEntityIds = MergeStringLists(ReadStringList(payload, "aboutEntityIds"), ReadStringList(payload, "about_entities"));
            }

            List<string> tags = MergeStringLists(ReadStringList(payload, "tags"), SimpleTags(summary + " " + eventType));
            string aboutEntitiesJson = aboutEntitiesValue == null || aboutEntityIds.Count > 0 && !(aboutEntitiesValue is ArrayList) ? Json.Serialize(aboutEntityIds) : Json.Serialize(aboutEntitiesValue);
            string eventCategory = DialogueService.ClassifyEventCategory(eventType, payload, summary);
            string eventSubtype = DialogueService.ClassifyEventSubtype(eventType, summary);
            bool indexEventForRetrieval = ReadBool(payload, "indexEventForRetrieval", true);
            bool projectParticipantMemories = ReadBool(payload, "projectParticipantMemories", true);
            payload["eventCategory"] = eventCategory;
            payload["eventSubtype"] = eventSubtype;
            payload["indexEventForRetrieval"] = indexEventForRetrieval;
            payload["projectParticipantMemories"] = projectParticipantMemories;
            string payloadJson = Json.Serialize(payload);
            List<string> memoryIds = new List<string>();

            using (ReignDbConnection ownedConnection = existingConnection == null ? OpenCampaignConnection(campaignId) : null)
            {
                var connection = existingConnection ?? ownedConnection;
                using var publication = existingConnection == null ? connection.BeginTransaction() : null;
                DeleteWorldMemoryEventProjection(connection, eventId);
                ExecuteSql(connection, @"INSERT OR REPLACE INTO events(
    event_id,campaign_id,ts,world_day,event_type,event_category,event_subtype,location_id,summary,participants_json,witnesses_json,about_entities_json,
    known_by_json,heard_as_rumor_by_json,hidden_from_json,visibility,importance,source_reliability,confidence,payload_json,vector_id,embedding_status,created_utc)
VALUES(
    $event_id,$campaign_id,$ts,$world_day,$event_type,$event_category,$event_subtype,$location_id,$summary,$participants_json,$witnesses_json,$about_entities_json,
    $known_by_json,$heard_as_rumor_by_json,$hidden_from_json,$visibility,$importance,$source_reliability,$confidence,$payload_json,$vector_id,$embedding_status,$created_utc);",
                    new Dictionary<string, object>
                    {
                        ["event_id"] = eventId,
                        ["campaign_id"] = campaignId,
                        ["ts"] = ts,
                        ["world_day"] = worldDay,
                        ["event_type"] = eventType,
                        ["event_category"] = eventCategory,
                        ["event_subtype"] = eventSubtype,
                        ["location_id"] = locationId,
                        ["summary"] = summary,
                        ["participants_json"] = Json.Serialize(participants),
                        ["witnesses_json"] = Json.Serialize(witnesses),
                        ["about_entities_json"] = aboutEntitiesJson,
                        ["known_by_json"] = Json.Serialize(knownBy),
                        ["heard_as_rumor_by_json"] = Json.Serialize(heardAsRumorBy),
                        ["hidden_from_json"] = Json.Serialize(hiddenFrom),
                        ["visibility"] = visibility,
                        ["importance"] = importance,
                        ["source_reliability"] = reliability,
                        ["confidence"] = confidence,
                        ["payload_json"] = payloadJson,
                        ["vector_id"] = ReadString(payload, "vector_id", ReadString(payload, "vectorId", "")),
                        ["embedding_status"] = indexEventForRetrieval
                            ? ReadString(payload, "embedding_status", ReadString(payload, "embeddingStatus", "not_indexed"))
                            : "skipped",
                        ["created_utc"] = DateTime.UtcNow.ToString("o")
                    });
                if (indexEventForRetrieval)
                {
                    InsertEventFts(connection, eventId, summary, tags, aboutEntityIds.Concat(participants).Concat(witnesses).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                }

                if (projectParticipantMemories && !string.IsNullOrWhiteSpace(summary))
                {
                    bool canonicalPublic = visibility.StartsWith("public", StringComparison.OrdinalIgnoreCase)
                        || visibility.Equals("world", StringComparison.OrdinalIgnoreCase)
                        || visibility.Equals("global", StringComparison.OrdinalIgnoreCase);
                    if (canonicalPublic)
                    {
                        string memoryId = "mem_world_" + Guid.NewGuid().ToString("N");
                        string memoryType = eventCategory == "local_observation" ? "local_observation" : "world_knowledge";
                        InsertMemoryRow(connection, memoryId, eventId, "", memoryType, ts, worldDay, locationId, summary,
                            participants, witnesses, aboutEntitiesJson, new List<string>(), heardAsRumorBy, hiddenFrom, visibility,
                            importance, 0d, confidence, tags, "active", source, payloadJson, "", "not_indexed");
                        ExecuteSql(connection, "UPDATE memories SET memory_domain=$domain WHERE memory_id=$id;",
                            new Dictionary<string, object>
                            {
                                ["domain"] = MemoryDomainForWorldEventProjection(eventCategory, eventType, memoryType, summary, tags, source),
                                ["id"] = memoryId
                            });
                        memoryIds.Add(memoryId);
                    }

                    List<string> directOwners = MergeStringLists(participants, witnesses);
                    if (!canonicalPublic && directOwners.Count == 0)
                    {
                        directOwners = knownBy;
                    }
                    foreach (string ownerId in directOwners.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        string acquisition = witnesses.Contains(ownerId, StringComparer.OrdinalIgnoreCase) ? "witness" : "participant";
                        DialogueService.UpsertKnowledgeReceipt(connection, eventId, ownerId, acquisition, confidence, reliability, worldDay, source, ts);
                        string memoryId = "mem_" + Guid.NewGuid().ToString("N");
                        string memoryType = eventCategory == "conversation" || eventCategory == "correspondence"
                            ? "interpersonal_experience"
                            : eventCategory == "local_observation" ? "local_observation" : "personal_experience";
                        InsertMemoryRow(connection, memoryId, eventId, ownerId, memoryType, ts, worldDay, locationId, summary,
                            participants, witnesses, aboutEntitiesJson, new List<string> { ownerId }, new List<string>(), hiddenFrom,
                            "private", importance, 0d, confidence, tags, "active", source, payloadJson, "", "not_indexed");
                        ExecuteSql(connection, "UPDATE memories SET memory_domain=$domain WHERE memory_id=$id;",
                            new Dictionary<string, object>
                            {
                                ["domain"] = MemoryDomainForWorldEventProjection(eventCategory, eventType, memoryType, summary, tags, source),
                                ["id"] = memoryId
                            });
                        memoryIds.Add(memoryId);
                    }

                    foreach (string ownerId in knownBy.Where(id => !string.IsNullOrWhiteSpace(id)).Except(directOwners, StringComparer.OrdinalIgnoreCase))
                    {
                        DialogueService.UpsertKnowledgeReceipt(connection, eventId, ownerId, "known", confidence, reliability, worldDay, source, ts);
                    }
                }

                StoreStructuredWrites(connection, campaignId, eventId, payload, ts, source);
                publication?.Commit();
            }

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaignId"] = campaignId,
                ["eventId"] = eventId,
                ["memoryIds"] = memoryIds,
                ["memoryCount"] = memoryIds.Count,
                ["indexEventForRetrieval"] = indexEventForRetrieval,
                ["projectParticipantMemories"] = projectParticipantMemories,
                ["dbPath"] = CampaignMemoryDbPath(campaignId)
            };
        }

        internal static string MemoryDomainForWorldEventProjection(string eventCategory, string eventType, string memoryType, string summary, List<string> tags, string source)
        {
            if (string.Equals(eventCategory, "conversation", StringComparison.OrdinalIgnoreCase)
                || string.Equals(eventCategory, "correspondence", StringComparison.OrdinalIgnoreCase))
            {
                return "interpersonal_history";
            }
            if (string.Equals(eventCategory, "local_observation", StringComparison.OrdinalIgnoreCase))
            {
                return "local_awareness";
            }

            string classified = DialogueService.ClassifyMemoryDomain(new Dictionary<string, object>
            {
                ["memory_type"] = memoryType ?? "",
                ["summary"] = summary ?? "",
                ["source"] = (source ?? "") + " " + (eventType ?? ""),
                ["tags_json"] = Json.Serialize(tags ?? new List<string>())
            });
            // Generic native events remain world knowledge, while promises,
            // beliefs, conversations, and personal state keep their specific lane.
            if (string.Equals(classified, "personal_state", StringComparison.OrdinalIgnoreCase)
                && string.Equals(eventCategory, "world_event", StringComparison.OrdinalIgnoreCase))
            {
                return "world_affairs";
            }
            return classified;
        }

        internal static void DeleteWorldMemoryEventProjection(ReignDbConnection connection, string eventId)
        {
            if (connection == null || string.IsNullOrWhiteSpace(eventId))
            {
                return;
            }

            Dictionary<string, object> parameters = new Dictionary<string, object> { ["event_id"] = eventId };
            ExecuteSql(connection, "DELETE FROM memory_fts WHERE memory_id IN (SELECT memory_id FROM memories WHERE event_id=$event_id);", parameters);
            ExecuteSql(connection, "DELETE FROM event_fts WHERE event_id=$event_id;", parameters);
            ExecuteSql(connection, "DELETE FROM memories WHERE event_id=$event_id;", parameters);
            ExecuteSql(connection, "DELETE FROM knowledge_receipts WHERE event_id=$event_id;", parameters);
            ExecuteSql(connection, "DELETE FROM beliefs WHERE event_id=$event_id;", parameters);
            ExecuteSql(connection, "DELETE FROM obligations WHERE event_id=$event_id;", parameters);
            ExecuteSql(connection, "DELETE FROM comprehension WHERE event_id=$event_id;", parameters);
        }

        internal static void InsertMemoryRow(
            ReignDbConnection connection,
            string memoryId,
            string eventId,
            string ownerId,
            string memoryType,
            long ts,
            double worldDay,
            string locationId,
            string summary,
            List<string> participants,
            List<string> witnesses,
            string aboutEntitiesJson,
            List<string> knownBy,
            List<string> heardAsRumorBy,
            List<string> hiddenFrom,
            string visibility,
            double importance,
            double emotionalWeight,
            double confidence,
            List<string> tags,
            string status,
            string source,
            string payloadJson,
            string vectorId,
            string embeddingStatus)
        {
            ExecuteSql(connection, @"INSERT OR REPLACE INTO memories(
    memory_id,event_id,owner_id,memory_type,ts,world_day,location_id,summary,participants_json,witnesses_json,about_entities_json,
    known_by_json,heard_as_rumor_by_json,hidden_from_json,visibility,importance,emotional_weight,confidence,tags_json,status,source,vector_id,embedding_status,payload_json)
VALUES(
    $memory_id,$event_id,$owner_id,$memory_type,$ts,$world_day,$location_id,$summary,$participants_json,$witnesses_json,$about_entities_json,
    $known_by_json,$heard_as_rumor_by_json,$hidden_from_json,$visibility,$importance,$emotional_weight,$confidence,$tags_json,$status,$source,$vector_id,$embedding_status,$payload_json);",
                new Dictionary<string, object>
                {
                    ["memory_id"] = memoryId,
                    ["event_id"] = eventId,
                    ["owner_id"] = ownerId ?? "",
                    ["memory_type"] = memoryType ?? "event",
                    ["ts"] = ts,
                    ["world_day"] = worldDay,
                    ["location_id"] = locationId ?? "",
                    ["summary"] = summary ?? "",
                    ["participants_json"] = Json.Serialize(participants ?? new List<string>()),
                    ["witnesses_json"] = Json.Serialize(witnesses ?? new List<string>()),
                    ["about_entities_json"] = string.IsNullOrWhiteSpace(aboutEntitiesJson) ? "[]" : aboutEntitiesJson,
                    ["known_by_json"] = Json.Serialize(knownBy ?? new List<string>()),
                    ["heard_as_rumor_by_json"] = Json.Serialize(heardAsRumorBy ?? new List<string>()),
                    ["hidden_from_json"] = Json.Serialize(hiddenFrom ?? new List<string>()),
                    ["visibility"] = visibility ?? "private",
                    ["importance"] = importance,
                    ["emotional_weight"] = emotionalWeight,
                    ["confidence"] = confidence,
                    ["tags_json"] = Json.Serialize(tags ?? new List<string>()),
                    ["status"] = status ?? "active",
                    ["source"] = source ?? "",
                    ["vector_id"] = vectorId ?? "",
                    ["embedding_status"] = string.IsNullOrWhiteSpace(embeddingStatus) ? "not_indexed" : embeddingStatus,
                    ["payload_json"] = payloadJson ?? "{}"
                });
            InsertMemoryFts(connection, memoryId, summary, tags, ExtractEntityIdsFromJson(aboutEntitiesJson).Concat(participants ?? new List<string>()).Concat(witnesses ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }

        internal static void InsertMemoryFts(ReignDbConnection connection, string memoryId, string text, List<string> tags, List<string> entities)
        {
            ExecuteSql(connection, "INSERT INTO memory_fts(memory_id, text, tags, entities) VALUES($memory_id, $text, $tags, $entities);",
                new Dictionary<string, object>
                {
                    ["memory_id"] = memoryId,
                    ["text"] = text ?? "",
                    ["tags"] = string.Join(" ", tags ?? new List<string>()),
                    ["entities"] = string.Join(" ", entities ?? new List<string>())
                });
        }

        private static void InsertEventFts(ReignDbConnection connection, string eventId, string text, List<string> tags, List<string> entities)
        {
            ExecuteSql(connection, "INSERT INTO event_fts(event_id, text, tags, entities) VALUES($event_id, $text, $tags, $entities);",
                new Dictionary<string, object>
                {
                    ["event_id"] = eventId,
                    ["text"] = text ?? "",
                    ["tags"] = string.Join(" ", tags ?? new List<string>()),
                    ["entities"] = string.Join(" ", entities ?? new List<string>())
                });
        }

        private static void StoreStructuredWrites(ReignDbConnection connection, string campaignId, string eventId, Dictionary<string, object> payload, long ts, string source)
        {
            foreach (Dictionary<string, object> item in ReadDictionaryList(payload, "beliefWrites").Concat(ReadDictionaryList(payload, "belief_writes")))
            {
                item["campaignId"] = campaignId;
                InsertBelief(connection, eventId, item, ts, source);
            }

            // Native Bannerlord relationship values remain the authority for character-to-character relation.
            // Reign does not maintain a second relationship score layer yet.

            foreach (Dictionary<string, object> item in ReadDictionaryList(payload, "obligationWrites").Concat(ReadDictionaryList(payload, "obligation_writes")))
            {
                item["campaignId"] = campaignId;
                InsertObligation(connection, eventId, item, ts, source);
            }

            foreach (Dictionary<string, object> item in ReadDictionaryList(payload, "comprehensionWrites").Concat(ReadDictionaryList(payload, "comprehension_writes")))
            {
                item["campaignId"] = campaignId;
                InsertComprehension(connection, eventId, item, ts, source);
            }
        }

        internal static void InsertBelief(ReignDbConnection connection, string eventId, Dictionary<string, object> item, long ts, string source)
        {
            string claim = ReadFirstString(item, "claim", "text", "belief", "summary");
            if (string.IsNullOrWhiteSpace(claim))
            {
                return;
            }

            string believer = ReadFirstString(item, "believer", "believer_id", "believerId", "ownerId", "heroStringId");
            List<string> knownBy = MergeStringLists(ReadStringList(item, "known_by"), ReadStringList(item, "knownBy"));
            if (knownBy.Count == 0 && !string.IsNullOrWhiteSpace(believer))
            {
                knownBy.Add(believer);
            }

            string beliefId = FirstNonEmpty(ReadFirstString(item, "belief_id", "beliefId", "id"), "belief_" + Guid.NewGuid().ToString("N"));
            ExecuteSql(connection, @"INSERT OR REPLACE INTO beliefs(belief_id,event_id,believer_id,claim,confidence,source,known_by_json,about_entities_json,hidden_from_json,visibility,ts,payload_json)
VALUES($belief_id,$event_id,$believer_id,$claim,$confidence,$source,$known_by_json,$about_entities_json,$hidden_from_json,$visibility,$ts,$payload_json);",
                new Dictionary<string, object>
                {
                    ["belief_id"] = beliefId,
                    ["event_id"] = eventId ?? "",
                    ["believer_id"] = believer,
                    ["claim"] = claim.Trim(),
                    ["confidence"] = ClampDouble(ReadDouble(item, "confidence", 0.5d), 0d, 1d),
                    ["source"] = FirstNonEmpty(ReadFirstString(item, "source"), source),
                    ["known_by_json"] = Json.Serialize(knownBy),
                    ["about_entities_json"] = Json.Serialize(ExtractEntityIds(item.ContainsKey("about_entities") ? item["about_entities"] : item.ContainsKey("aboutEntities") ? item["aboutEntities"] : null)),
                    ["hidden_from_json"] = Json.Serialize(MergeStringLists(ReadStringList(item, "hidden_from"), ReadStringList(item, "hiddenFrom"))),
                    ["visibility"] = ReadString(item, "visibility", "private"),
                    ["ts"] = ts,
                    ["payload_json"] = Json.Serialize(item)
                });
            DialogueService.UpsertTemporalAssertion(connection, ReadString(item, "campaignId", ""), eventId, beliefId, "belief", item, ts);
        }

        internal static void InsertObligation(ReignDbConnection connection, string eventId, Dictionary<string, object> item, long ts, string source)
        {
            string description = ReadFirstString(item, "description", "text", "promise", "threat", "summary");
            if (string.IsNullOrWhiteSpace(description))
            {
                return;
            }

            string obligationId = DialogueService.DialogueCommitmentTarget(connection, DialogueService.StableCommitmentId(item), item);
            if (obligationId == null) return; // Exact interpretation/proposal replay.
            DialogueService.PreserveCommitmentOriginalSource(connection, obligationId, eventId, item);
            ExecuteSql(connection, @"INSERT OR REPLACE INTO obligations(obligation_id,event_id,owed_by,owed_to,description,status,created_day,importance,participants_json,about_entities_json,known_by_json,hidden_from_json,visibility,ts,payload_json,timeline_id)
VALUES($obligation_id,$event_id,$owed_by,$owed_to,$description,$status,$created_day,$importance,$participants_json,$about_entities_json,$known_by_json,$hidden_from_json,$visibility,$ts,$payload_json,$timeline);",
                new Dictionary<string, object>
                {
                    ["obligation_id"] = obligationId,
                    ["timeline"] = DialogueService.ContinuityTimeline(item),
                    ["event_id"] = eventId ?? "",
                    ["owed_by"] = ReadFirstString(item, "owed_by", "owedBy", "actorId", "subject"),
                    ["owed_to"] = ReadFirstString(item, "owed_to", "owedTo", "targetId", "target"),
                    ["description"] = description.Trim(),
                    ["status"] = ReadString(item, "status", "unresolved"),
                    ["created_day"] = ReadDouble(item, "created_day", ReadDouble(item, "createdDay", 0d)),
                    ["importance"] = ClampDouble(ReadDouble(item, "importance", 0.7d), 0d, 1d),
                    ["participants_json"] = Json.Serialize(ReadStringList(item, "participants")),
                    ["about_entities_json"] = Json.Serialize(ExtractEntityIds(item.ContainsKey("about_entities") ? item["about_entities"] : item.ContainsKey("aboutEntities") ? item["aboutEntities"] : null)),
                    ["known_by_json"] = Json.Serialize(MergeStringLists(ReadStringList(item, "known_by"), ReadStringList(item, "knownBy"))),
                    ["hidden_from_json"] = Json.Serialize(MergeStringLists(ReadStringList(item, "hidden_from"), ReadStringList(item, "hiddenFrom"))),
                    ["visibility"] = ReadString(item, "visibility", "private"),
                    ["ts"] = ts,
                    ["payload_json"] = Json.Serialize(item)
                });
            if (ReadString(item, "interpretationStatus", "") != "model_inferred")
                DialogueService.UpsertTemporalAssertion(connection, ReadString(item, "campaignId", ""), eventId, obligationId, "obligation", item, ts);
        }

        private static void InsertComprehension(ReignDbConnection connection, string eventId, Dictionary<string, object> item, long ts, string source)
        {
            string text = ReadFirstString(item, "text", "comprehension", "interpretation", "summary");
            string owner = ReadFirstString(item, "ownerId", "owner_id", "heroStringId", "npcId");
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(owner))
            {
                return;
            }

            List<string> knownBy = MergeStringLists(ReadStringList(item, "known_by"), ReadStringList(item, "knownBy"));
            if (knownBy.Count == 0)
            {
                knownBy.Add(owner);
            }

            string comprehensionId = FirstNonEmpty(ReadFirstString(item, "comprehension_id", "comprehensionId", "id"), "comp_" + Guid.NewGuid().ToString("N"));
            ExecuteSql(connection, @"INSERT OR REPLACE INTO comprehension(comprehension_id,event_id,owner_id,text,stance,confidence,known_by_json,about_entities_json,hidden_from_json,ts,payload_json)
VALUES($comprehension_id,$event_id,$owner_id,$text,$stance,$confidence,$known_by_json,$about_entities_json,$hidden_from_json,$ts,$payload_json);",
                new Dictionary<string, object>
                {
                    ["comprehension_id"] = comprehensionId,
                    ["event_id"] = eventId ?? "",
                    ["owner_id"] = owner,
                    ["text"] = text.Trim(),
                    ["stance"] = ReadFirstString(item, "stance", "tone"),
                    ["confidence"] = ClampDouble(ReadDouble(item, "confidence", 0.6d), 0d, 1d),
                    ["known_by_json"] = Json.Serialize(knownBy),
                    ["about_entities_json"] = Json.Serialize(ExtractEntityIds(item.ContainsKey("about_entities") ? item["about_entities"] : item.ContainsKey("aboutEntities") ? item["aboutEntities"] : null)),
                    ["hidden_from_json"] = Json.Serialize(MergeStringLists(ReadStringList(item, "hidden_from"), ReadStringList(item, "hiddenFrom"))),
                    ["ts"] = ts,
                    ["payload_json"] = Json.Serialize(item)
                });
            DialogueService.UpsertTemporalAssertion(connection, ReadString(item, "campaignId", ""), eventId, comprehensionId, "interpretation", item, ts);
        }

        internal static bool IsPracticeDuelActionReport(Dictionary<string, object> payload)
        {
            return CanonicalCommand(ReadFirstString(payload, "type", "command")) == "duel_player"
                && ReadString(ReadDictionary(payload, "result"), "resultCode", "") == "practice_duel_completed";
        }

        // existingConnection: the caller owns the open transaction (see StoreWorldMemoryEvent).
        internal static Dictionary<string, object> StoreActionResultMemory(Dictionary<string, object> payload, string source,
            ReignDbConnection existingConnection = null)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            Dictionary<string, object> result = ReadDictionary(payload, "result") ?? new Dictionary<string, object>();
            string actionId = ReadFirstString(payload, "serverActionId", "actionId", "id");
            string type = ReadFirstString(payload, "type", "command");
            string status = ReadString(payload, "status", ReadString(result, "status", "reported"));
            string message = FirstNonEmpty(ReadFirstString(payload, "message"), ReadFirstString(result, "message", "debugMessage"));
            List<string> participants = MergeStringLists(new List<string>
            {
                ReadFirstString(payload, "actorHeroStringId", "actorHeroId"),
                ReadFirstString(payload, "targetHeroStringId", "targetHeroId")
            }, new List<string>());
            bool practiceDuel = IsPracticeDuelActionReport(payload);
            if (practiceDuel)
            {
                string playerId = ReadString(payload, "playerHeroStringId", "");
                // The native report must identify exactly the player and one NPC.
                // Missing identity must never fall through to a public memory.
                if (string.IsNullOrWhiteSpace(playerId) || participants.Count != 2 || !participants.Contains(playerId))
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Practice duel memory requires the player and exact opponent." };
                participants = participants.Where(id => id != playerId).ToList();
            }
            Dictionary<string, object> evt = new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["eventId"] = string.IsNullOrWhiteSpace(actionId) ? "" : "action_" + actionId,
                ["eventType"] = "action_result",
                ["summary"] = FirstNonEmpty(message, type + " " + status),
                ["participants"] = participants,
                ["known_by"] = participants,
                ["visibility"] = participants.Count > 0 ? "private" : "public",
                ["importance"] = status.Equals("completed", StringComparison.OrdinalIgnoreCase) ? 0.75d : 0.55d,
                ["payload"] = payload
            };
            if (practiceDuel)
            {
                evt["worldDay"] = ReadDouble(payload, "worldDay", 0d);
                evt["timelineId"] = ReadString(payload, "timelineId", "");
                evt["eventType"] = "practice_duel_completed";
            }
            return StoreWorldMemoryEvent(evt, source, existingConnection);
        }

        internal static Dictionary<string, object> StoreTurnMemoryLayers(
            string campaignId,
            string eventId,
            string eventType,
            string heroId,
            string playerId,
            string locationId,
            string playerText,
            string reply,
            List<Dictionary<string, object>> memoryWrites,
            List<Dictionary<string, object>> beliefWrites,
            List<Dictionary<string, object>> relationshipUpdates,
            List<Dictionary<string, object>> obligationWrites,
            List<Dictionary<string, object>> comprehensionWrites,
            string memoryGroupKey,
            long ts,
            List<string> sharedParticipants = null,
            bool retrievalEligible = true,
            Dictionary<string, object> continuityPayload = null,
            List<Dictionary<string, object>> continuityWrites = null,
            ReignDbConnection existingConnection = null)
        {
            List<string> participants = MergeStringLists(sharedParticipants, new[] { heroId, playerId });
            string summary = LimitText("Player: " + (playerText ?? "") + "\nNPC: " + (reply ?? ""), 1200);
            if (string.IsNullOrWhiteSpace(summary))
            {
                summary = eventType ?? "conversation turn";
            }

            // A recall answer is evidence about an existing conversation, not a new
            // fact source. The canonical conversation rows and decision audit still
            // retain the exchange, but projecting the answer back into events,
            // memories, FTS, or vectors creates a self-reinforcing recall loop.
            if (!retrievalEligible)
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["campaignId"] = campaignId,
                    ["eventId"] = eventId,
                    ["memoryIds"] = new List<string>(),
                    ["memoryCount"] = 0,
                    ["episodicMemoryWriteIds"] = new List<string>(),
                    ["retrievalEligible"] = false,
                    ["auditOnly"] = true,
                    ["reason"] = "recall_response_audit_only",
                    ["dbPath"] = CampaignMemoryDbPath(campaignId),
                    ["structuredWriteCounts"] = new Dictionary<string, object>
                    {
                        ["memoryWrites"] = 0,
                        ["beliefWrites"] = 0,
                        ["relationshipUpdatesSkipped"] = relationshipUpdates == null ? 0 : relationshipUpdates.Count,
                        ["obligationWrites"] = 0,
                        ["comprehensionWrites"] = 0
                    },
                    ["suppressedWriteCounts"] = new Dictionary<string, object>
                    {
                        ["memoryWrites"] = memoryWrites == null ? 0 : memoryWrites.Count,
                        ["beliefWrites"] = beliefWrites == null ? 0 : beliefWrites.Count,
                        ["obligationWrites"] = obligationWrites == null ? 0 : obligationWrites.Count,
                        ["comprehensionWrites"] = comprehensionWrites == null ? 0 : comprehensionWrites.Count
                    }
                };
            }

            List<Dictionary<string, object>> preparedBeliefs = PrepareStructuredWrites(beliefWrites, heroId, playerId, locationId, eventType, "belief", participants);
            List<Dictionary<string, object>> preparedObligations = PrepareStructuredWrites(obligationWrites, heroId, playerId, locationId, eventType, "obligation", participants);
            List<Dictionary<string, object>> preparedComprehension = PrepareStructuredWrites(comprehensionWrites, heroId, playerId, locationId, eventType, "comprehension", participants);
            var acceptedSources = DialogueService.CurrentConversationSources(playerId, heroId, playerText, reply, eventId,
                ReadFirstString(continuityPayload, "sceneTurnId", "exchangeId", "turnId"));
            foreach (var acceptedSource in acceptedSources) acceptedSource["timestamp"] = ts;
            DialogueService.AttachConversationWriteEvidence(memoryWrites, acceptedSources, heroId, eventId, requireSemanticAdmission: true);
            DialogueService.AttachConversationWriteEvidence(preparedBeliefs.Concat(preparedComprehension),
                acceptedSources, heroId, eventId);
            var commitmentSources = new List<Dictionary<string, object>>(acceptedSources);
            if (continuityPayload != null && DialogueService.PromptStatementSources.TryGetValue(continuityPayload, out var historicStatements))
                commitmentSources.AddRange(historicStatements);
            DialogueService.AttachConversationWriteEvidence(preparedObligations, commitmentSources, heroId, eventId);
            foreach (var commitment in preparedObligations) DialogueService.NormalizeDialogueCommitment(commitment, heroId);
            int incompleteSourceWrites = (memoryWrites ?? new List<Dictionary<string, object>>()).Concat(preparedBeliefs)
                .Concat(preparedObligations).Concat(preparedComprehension)
                .Count(write => ReadString(write, "sourceValidation", "incomplete") != "supported_span");
            foreach (var write in preparedBeliefs.Concat(preparedObligations).Concat(preparedComprehension))
            {
                write["timelineId"] = DialogueService.ContinuityTimeline(continuityPayload);
                write["worldDay"] = ReadDouble(continuityPayload, "worldDay", 0d);
                write["sourceSpeakerId"] = heroId;
                write["sourceAcceptedText"] = reply ?? "";
            }

            memoryWrites = DialogueService.ScopeConversationMemoryWrites(memoryWrites, heroId, playerId, playerText, reply, eventId, participants);

            Dictionary<string, object> eventPayload = new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["eventId"] = eventId,
                ["ts"] = ts,
                ["eventType"] = eventType,
                ["summary"] = summary,
                ["participants"] = participants,
                ["known_by"] = participants,
                ["locationId"] = locationId ?? "",
                ["memoryGroupKey"] = memoryGroupKey ?? "",
                ["visibility"] = "private",
                ["importance"] = CalculateTurnImportance(memoryWrites, preparedBeliefs, preparedObligations, preparedComprehension),
                ["tags"] = MergeStringLists(SimpleTags(summary + " " + eventType), new[] { "conversation" }),
                // Canonical conversation_turns plus their FTS window preserve the
                // recent raw exchange. Do not duplicate every low-salience line as
                // an event-FTS hit and two durable participant memories; explicit
                // model/structured writes below are the long-term layer.
                ["indexEventForRetrieval"] = false,
                ["projectParticipantMemories"] = false,
                ["beliefWrites"] = preparedBeliefs,
                ["obligationWrites"] = preparedObligations,
                ["comprehensionWrites"] = preparedComprehension,
                ["payload"] = new Dictionary<string, object>
                {
                    ["playerText"] = playerText ?? "",
                    ["reply"] = reply ?? "",
                    ["memoryGroupKey"] = memoryGroupKey ?? "",
                    ["relationshipUpdatesSkipped"] = relationshipUpdates == null ? 0 : relationshipUpdates.Count
                }
            };

            Dictionary<string, object> stored;
            List<string> episodicMemoryIds = new List<string>();
            using (ReignDbConnection ownedConnection = existingConnection == null ? OpenCampaignConnection(campaignId) : null)
            {
                var connection = existingConnection ?? ownedConnection;
                using var publication = existingConnection == null ? connection.BeginTransaction() : null;
                stored = StoreWorldMemoryEvent(eventPayload, eventType, connection);
                foreach (Dictionary<string, object> memory in memoryWrites ?? new List<Dictionary<string, object>>())
                {
                    string text = ReadFirstString(memory, "text", "memory", "summary", "content");
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    Dictionary<string, object> copy = new Dictionary<string, object>(memory, StringComparer.OrdinalIgnoreCase);
                    copy["timelineId"] = DialogueService.ContinuityTimeline(continuityPayload);
                    copy["worldDay"] = ReadDouble(continuityPayload, "worldDay", ReadDouble(copy, "worldDay", 0d));
                    copy["sourceEventId"] = eventId;
                    copy["sourceHash"] = DialogueService.PromptHash(reply ?? "");
                    copy["evidenceClass"] = "reported";
                    if (!copy.ContainsKey("memoryGroupKey"))
                    {
                        copy["memoryGroupKey"] = memoryGroupKey ?? "";
                    }
                    bool authorInterpretation = ReadString(copy, "observerScope", "") == "author_interpretation";
                    List<string> memoryAudience = authorInterpretation ? new List<string> { heroId } : participants;
                    List<string> knownBy = authorInterpretation ? memoryAudience : MergeStringLists(MergeStringLists(ReadStringList(copy, "known_by"), ReadStringList(copy, "knownBy")), memoryAudience);
                    List<string> witnesses = MergeStringLists(ReadStringList(copy, "witnesses"), new List<string>());
                    List<string> tags = MergeStringLists(ReadStringList(copy, "tags"), new[] { "conversation" });
                    if (tags.Count == 0)
                    {
                        tags = SimpleTags(text);
                    }

                    object aboutRaw = copy.ContainsKey("about_entities") ? copy["about_entities"] : copy.ContainsKey("aboutEntities") ? copy["aboutEntities"] : null;
                    string memoryId = FirstNonEmpty(ReadFirstString(copy, "memory_id", "memoryId", "id"), "mem_" + Guid.NewGuid().ToString("N"));
                    string ownerId = FirstNonEmpty(ReadFirstString(copy, "ownerId", "owner_id", "heroStringId"), heroId);
                    InsertMemoryRow(
                        connection,
                        memoryId,
                        eventId,
                        ownerId,
                        FirstNonEmpty(ReadFirstString(copy, "memoryType", "memory_type", "type"), "episodic"),
                        ts,
                        ReadDouble(copy, "world_day", ReadDouble(copy, "worldDay", 0d)),
                        FirstNonEmpty(ReadFirstString(copy, "location_id", "locationId", "settlementId"), locationId),
                        text.Trim(),
                        memoryAudience,
                        witnesses,
                        Json.Serialize(ExtractEntityIds(aboutRaw)),
                        knownBy,
                        MergeStringLists(ReadStringList(copy, "heard_as_rumor_by"), ReadStringList(copy, "heardAsRumorBy")),
                        MergeStringLists(ReadStringList(copy, "hidden_from"), ReadStringList(copy, "hiddenFrom")),
                        ReadString(copy, "visibility", "private"),
                        MemoryImportance01(copy, 0.55d),
                        ClampDouble(ReadDouble(copy, "emotionalWeight", ReadDouble(copy, "emotional_weight", 0d)), 0d, 1d),
                        ClampDouble(ReadDouble(copy, "confidence", 0.75d), 0d, 1d),
                        tags,
                        ReadString(copy, "status", "active"),
                        FirstNonEmpty(ReadFirstString(copy, "source"), eventType),
                        Json.Serialize(copy),
                        ReadString(copy, "vector_id", ReadString(copy, "vectorId", "")),
                        ReadString(copy, "embedding_status", ReadString(copy, "embeddingStatus", "not_indexed")));
                    ExecuteSql(connection, "UPDATE memories SET memory_domain=$domain WHERE memory_id=$id;",
                        new Dictionary<string, object>
                        {
                            ["domain"] = DialogueService.ClassifyMemoryDomain(new Dictionary<string, object>(copy, StringComparer.OrdinalIgnoreCase)
                            {
                                ["memory_type"] = FirstNonEmpty(ReadFirstString(copy, "memoryType", "memory_type", "type"), "episodic"),
                                ["summary"] = text,
                                ["source"] = FirstNonEmpty(ReadFirstString(copy, "source"), eventType)
                            }),
                            ["id"] = memoryId
                        });
                    episodicMemoryIds.Add(memoryId);
                }
                if (continuityPayload != null && continuityWrites != null && continuityWrites.Count > 0)
                {
                    stored["continuity"] = DialogueService.StoreAcceptedConversationContinuity(connection, continuityPayload,
                        heroId, playerId, eventId, reply, continuityWrites, ts);
                }
                ExecuteSql(connection, "DELETE FROM memory_projections WHERE owner_id=$owner;", new Dictionary<string, object> { ["owner"] = heroId });
                publication?.Commit();
            }
            List<string> projectedMemoryIds = ReadStringList(stored, "memoryIds");
            stored["projectedMemoryIds"] = projectedMemoryIds;
            stored["projectedMemoryCount"] = projectedMemoryIds.Count;
            stored["episodicMemoryWriteIds"] = episodicMemoryIds;
            stored["memoryIds"] = MergeStringLists(projectedMemoryIds, episodicMemoryIds);
            stored["memoryCount"] = projectedMemoryIds.Count + episodicMemoryIds.Count;
            stored["sourceEvidenceStatus"] = incompleteSourceWrites == 0 ? "complete" : "incomplete";
            stored["incompleteSourceWriteCount"] = incompleteSourceWrites;
            stored["structuredWriteCounts"] = new Dictionary<string, object>
            {
                ["memoryWrites"] = memoryWrites == null ? 0 : memoryWrites.Count,
                ["beliefWrites"] = preparedBeliefs.Count,
                ["relationshipUpdatesSkipped"] = relationshipUpdates == null ? 0 : relationshipUpdates.Count,
                ["obligationWrites"] = preparedObligations.Count,
                ["comprehensionWrites"] = preparedComprehension.Count
            };
            return stored;
        }

        internal static List<Dictionary<string, object>> PrepareStructuredWrites(
            List<Dictionary<string, object>> writes,
            string heroId,
            string playerId,
            string locationId,
            string source,
            string layer,
            List<string> sharedParticipants = null)
        {
            List<Dictionary<string, object>> prepared = new List<Dictionary<string, object>>();
            List<string> participants = MergeStringLists(sharedParticipants, new[] { heroId, playerId });
            foreach (Dictionary<string, object> write in writes ?? new List<Dictionary<string, object>>())
            {
                Dictionary<string, object> copy = new Dictionary<string, object>(write ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
                if (!copy.ContainsKey("source") || string.IsNullOrWhiteSpace(Convert.ToString(copy["source"])))
                {
                    copy["source"] = source ?? "dialogue";
                }

                if (!copy.ContainsKey("locationId") && !copy.ContainsKey("location_id") && !string.IsNullOrWhiteSpace(locationId))
                {
                    copy["locationId"] = locationId;
                }

                // A response may author only its speaker's internal state. Other
                // people hearing the reply does not disclose its hidden metadata.
                if (layer.Equals("belief", StringComparison.OrdinalIgnoreCase))
                {
                    copy["believer"] = heroId;
                    copy["believer_id"] = heroId;
                    copy["believerId"] = heroId;
                }

                if (layer.Equals("comprehension", StringComparison.OrdinalIgnoreCase))
                {
                    copy["ownerId"] = heroId;
                    copy["owner_id"] = heroId;
                }

                if (DialogueService.IsPrivateMentalLayer(layer))
                {
                    copy["believer"] = heroId;
                    copy["believer_id"] = heroId;
                    copy["believerId"] = heroId;
                    copy["ownerId"] = heroId;
                    copy["owner_id"] = heroId;
                    copy["heroStringId"] = heroId;
                    copy["npcId"] = heroId;
                    copy["known_by"] = new List<string> { heroId };
                    copy["knownBy"] = new List<string> { heroId };
                    copy["visibility"] = "private";
                    copy["truthStatus"] = layer.Equals("belief", StringComparison.OrdinalIgnoreCase)
                        ? "believed" : "interpretation";
                }

                if (ReadStringList(copy, "known_by").Count == 0 && ReadStringList(copy, "knownBy").Count == 0)
                {
                    copy["known_by"] = layer.Equals("court", StringComparison.OrdinalIgnoreCase) && ReadString(copy, "visibility", "rumor").Equals("public", StringComparison.OrdinalIgnoreCase)
                        ? new List<string>()
                        : participants;
                }

                if (layer.Equals("obligation", StringComparison.OrdinalIgnoreCase) && ReadStringList(copy, "participants").Count == 0)
                {
                    copy["participants"] = participants;
                }

                if (!copy.ContainsKey("visibility") || string.IsNullOrWhiteSpace(Convert.ToString(copy["visibility"])))
                {
                    copy["visibility"] = layer.Equals("court", StringComparison.OrdinalIgnoreCase) ? "rumor" : "private";
                }

                prepared.Add(copy);
            }

            return prepared;
        }

        private static double CalculateTurnImportance(
            List<Dictionary<string, object>> memoryWrites,
            List<Dictionary<string, object>> beliefWrites,
            List<Dictionary<string, object>> obligationWrites,
            List<Dictionary<string, object>> comprehensionWrites)
        {
            if ((obligationWrites != null && obligationWrites.Count > 0) || (beliefWrites != null && beliefWrites.Count > 0))
            {
                return 0.78d;
            }

            if ((memoryWrites != null && memoryWrites.Count > 0) || (comprehensionWrites != null && comprehensionWrites.Count > 0))
            {
                return 0.62d;
            }

            return 0.35d;
        }

        internal static double MemoryImportance01(Dictionary<string, object> memory, double fallback)
        {
            double value = ReadDouble(memory, "importance", fallback);
            if (value > 1d)
            {
                value = value / 5d;
            }

            return ClampDouble(value, 0d, 1d);
        }

        internal static string BuildMemoryGroupKey(Dictionary<string, object> payload, string npcId, string playerId, string locationId, string prefix, long ts)
        {
            payload = payload ?? new Dictionary<string, object>();
            string existing = ReadFirstString(payload, "memoryGroupKey", "memory_group_key");
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing.Trim();
            }

            string explicitId = ReadFirstString(payload, "conversationId", "conversation_id", "sceneId", "scene_id", "socialEventId", "eventId", "templateId");
            if (!string.IsNullOrWhiteSpace(explicitId))
            {
                return FirstNonEmpty(prefix, "memory") + ":" + explicitId.Trim();
            }

            Dictionary<string, object> settings = LoadSettings();
            int windowMinutes = Math.Max(5, Math.Min(240, ReadInt(settings, "memoryConsolidationWindowMinutes", 30)));
            long bucket = Math.Max(0, ts) / (windowMinutes * 60L);
            List<string> actors = MergeStringLists(new[] { npcId, playerId }, new List<string>())
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return FirstNonEmpty(prefix, "memory") + ":" + string.Join("+", actors.ToArray()) + ":" + (locationId ?? "") + ":" + bucket.ToString(CultureInfo.InvariantCulture);
        }

        private static string MemoryGroupKeyFromRow(Dictionary<string, object> row, string ownerId)
        {
            Dictionary<string, object> payload = TryParseJsonObject(ReadString(row, "payload_json", "")) ?? new Dictionary<string, object>();
            string existing = ReadFirstString(payload, "memoryGroupKey", "memory_group_key");
            if (string.IsNullOrWhiteSpace(existing))
            {
                existing = ReadFirstString(ReadDictionary(payload, "payload"), "memoryGroupKey", "memory_group_key");
            }

            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            List<string> participants = TextListFromJson(ReadString(row, "participants_json", "[]"));
            string other = participants.FirstOrDefault(x => !string.Equals(x, ownerId, StringComparison.OrdinalIgnoreCase)) ?? "";
            string source = ReadString(row, "source", "");
            string prefix = source.IndexOf("social", StringComparison.OrdinalIgnoreCase) >= 0 || source.IndexOf("event", StringComparison.OrdinalIgnoreCase) >= 0
                ? "social_event"
                : "conversation";
            return BuildMemoryGroupKey(new Dictionary<string, object>(), ownerId, other, ReadString(row, "location_id", ""), prefix, ReadLong(row, "ts", 0));
        }

        internal static string StoredConversationArtifactCorrelationId(
            Dictionary<string, object> row)
        {
            row = row ?? new Dictionary<string, object>();
            string direct = ReadFirstString(
                row,
                "correlationId",
                "correlation_id",
                "liveTestCorrelationId");
            if (!string.IsNullOrWhiteSpace(direct)) return direct;
            Dictionary<string, object> payload = TryParseJsonObject(
                    ReadString(row, "payload_json", ""))
                ?? new Dictionary<string, object>();
            return FirstNonEmpty(
                ReadFirstString(
                    payload,
                    "correlationId",
                    "correlation_id",
                    "liveTestCorrelationId"),
                ReadFirstString(
                    ReadDictionary(payload, "payload"),
                    "correlationId",
                    "correlation_id",
                    "liveTestCorrelationId"),
                ReadFirstString(
                    ReadDictionary(payload, "source"),
                    "correlationId",
                    "correlation_id",
                    "liveTestCorrelationId"));
        }

        internal static Dictionary<string, object> ConsolidateNpcMemory(string campaignId, string npcId, bool force, string requestedGroupKey)
        {
            Dictionary<string, object> settings = LoadSettings();
            if (!force && !ReadBool(settings, "enableMemoryConsolidation", true))
            {
                return new Dictionary<string, object> { ["ok"] = true, ["enabled"] = false, ["summaryCount"] = 0 };
            }

            int minEvents = Math.Max(2, Math.Min(20, ReadInt(settings, "memoryConsolidationMinItems", 4)));
            int maxGroups = Math.Max(1, Math.Min(10, ReadInt(settings, "memoryConsolidationMaxGroups", 3)));
            int maxSourceItems = Math.Max(4, Math.Min(80, ReadInt(settings, "memoryConsolidationMaxSourceItems", 24)));
            List<Dictionary<string, object>> candidates;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                candidates = QuerySql(connection, @"SELECT * FROM memories
WHERE owner_id=$owner_id AND status='active'
ORDER BY ts DESC LIMIT 400;",
                    new Dictionary<string, object> { ["owner_id"] = npcId });
            }

            candidates = candidates
                .Where(row =>
                {
                    List<string> tags = TextListFromJson(ReadString(row, "tags_json", "[]"));
                    string source = ReadString(row, "source", "");
                    return tags.Any(tag => tag.Equals("conversation", StringComparison.OrdinalIgnoreCase) || tag.Equals("event", StringComparison.OrdinalIgnoreCase))
                        || source.IndexOf("dialogue", StringComparison.OrdinalIgnoreCase) >= 0
                        || source.IndexOf("conversation", StringComparison.OrdinalIgnoreCase) >= 0
                        || source.IndexOf("social_event", StringComparison.OrdinalIgnoreCase) >= 0;
                })
                .ToList();

            List<IGrouping<string, Dictionary<string, object>>> groups = candidates
                .GroupBy(row => MemoryGroupKeyFromRow(row, npcId), StringComparer.OrdinalIgnoreCase)
                .Where(group => string.IsNullOrWhiteSpace(requestedGroupKey) || string.Equals(group.Key, requestedGroupKey, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(group => group.Max(row => ReadLong(row, "ts", 0)))
                .ToList();

            List<Dictionary<string, object>> created = new List<Dictionary<string, object>>();
            foreach (IGrouping<string, Dictionary<string, object>> group in groups)
            {
                List<Dictionary<string, object>> rows = group
                    .OrderBy(row => ReadLong(row, "ts", 0))
                    .GroupBy(MemoryPacketDedupeKey, StringComparer.OrdinalIgnoreCase)
                    .Select(items => items.First())
                    .Take(maxSourceItems)
                    .ToList();
                int distinctEvents = rows.Select(row => ReadString(row, "event_id", "")).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                int requiredEvents = force ? 2 : minEvents;
                if (distinctEvents < requiredEvents)
                {
                    continue;
                }

                string summaryCorrelationId = rows
                    .Select(StoredConversationArtifactCorrelationId)
                    .LastOrDefault(value =>
                        !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
                Dictionary<string, object> compact = BuildMiddleTermSummary(
                    settings,
                    npcId,
                    group.Key,
                    rows,
                    60000,
                    string.Empty,
                    summaryCorrelationId, campaignId: campaignId);
                string summaryText = ReadString(compact, "summary", "");
                if (string.IsNullOrWhiteSpace(summaryText))
                {
                    continue;
                }

                string summaryId = "sum_" + DialogueService.PromptHash(npcId + "|" + group.Key + "|" + DialogueService.MemorySourceHash(rows) + "|" + DialogueService.MemoryPrecisionVersion).Substring(0, 32);
                long startTs = rows.Min(row => ReadLong(row, "ts", 0));
                long endTs = rows.Max(row => ReadLong(row, "ts", 0));
                List<string> sourceEvents = rows.Select(row => ReadString(row, "event_id", "")).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                List<string> sourceMemoryIds = rows.Select(row => ReadString(row, "memory_id", "")).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                List<string> participants = rows.SelectMany(row => TextListFromJson(ReadString(row, "participants_json", "[]"))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                List<string> entities = rows.SelectMany(row => TextListFromJson(ReadString(row, "about_entities_json", "[]"))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                List<string> tags = MergeStringLists(ReadStringList(compact, "tags"), new[] { "middle_term", "conversation" });
                string locationId = rows.Select(row => ReadString(row, "location_id", "")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
                double importance = rows.Count == 0 ? 0.5d : rows.Max(row => ReadDouble(row, "importance", 0.5d));
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Dictionary<string, object> summaryPayload = new Dictionary<string, object>
                {
                    ["memoryGroupKey"] = group.Key,
                    ["sourceMemoryIds"] = sourceMemoryIds,
                    ["sourceEventIds"] = sourceEvents,
                    ["method"] = ReadString(compact, "method", "deterministic"),
                    ["interpretationStatus"] = ReadString(compact, "interpretationStatus", "source_extract"),
                    ["originalSourceRecords"] = ReadStringList(compact, "originalSourceRecords"),
                    ["coverageComplete"] = ReadBool(compact, "coverageComplete", false),
                    ["compactionStatus"] = ReadString(compact, "compactionStatus", "incomplete"),
                    ["sourceCoverage"] = ReadString(compact, "sourceCoverage", "unverified"),
                    ["selectedSourceIndices"] = compact.TryGetValue("selectedSourceIndices", out object selectedIndices) ? selectedIndices : Array.Empty<int>(),
                    ["retryable"] = ReadBool(compact, "retryable", false),
                    ["compactionDiagnostic"] = ReadDictionary(compact, "compactionDiagnostic") ?? TestDict(),
                    ["sourceHash"] = DialogueService.MemorySourceHash(rows), ["processingVersion"] = DialogueService.MemoryPrecisionVersion,
                    ["sourceItemCount"] = rows.Count,
                    ["correlationId"] = summaryCorrelationId
                };

                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                using (var publication = connection.BeginTransaction())
                {
                    DialogueService.VerifyMemoryPublicationFence(connection);
                    DialogueService.VerifyMemorySourceRows(connection, "memories", "memory_id", rows);
                    ExecuteSql(connection, @"INSERT INTO summaries(
summary_id,scope,owner_id,summary_type,summary,source_events_json,start_ts,end_ts,event_count,location_id,
participants_json,about_entities_json,known_by_json,hidden_from_json,visibility,importance,confidence,tags_json,
status,source,vector_id,embedding_status,updated_ts,payload_json)
VALUES($summary_id,$scope,$owner_id,'middle_term',$summary,$source_events_json,$start_ts,$end_ts,$event_count,$location_id,
$participants_json,$about_entities_json,$known_by_json,'[]','private',$importance,$confidence,$tags_json,
'active',$source,'','not_indexed',$updated_ts,$payload_json)
ON CONFLICT(summary_id) DO UPDATE SET summary=$summary,payload_json=$payload_json,updated_ts=$updated_ts;",
                        new Dictionary<string, object>
                        {
                            ["summary_id"] = summaryId,
                            ["scope"] = group.Key,
                            ["owner_id"] = npcId,
                            ["summary"] = summaryText,
                            ["source_events_json"] = Json.Serialize(sourceEvents),
                            ["start_ts"] = startTs,
                            ["end_ts"] = endTs,
                            ["event_count"] = distinctEvents,
                            ["location_id"] = locationId,
                            ["participants_json"] = Json.Serialize(participants),
                            ["about_entities_json"] = Json.Serialize(entities),
                            ["known_by_json"] = Json.Serialize(new List<string> { npcId }),
                            ["importance"] = importance,
                            ["confidence"] = ReadDouble(compact, "confidence", 0.75d),
                            ["tags_json"] = Json.Serialize(tags),
                            ["source"] = "memory_consolidation",
                            ["updated_ts"] = now,
                            ["payload_json"] = Json.Serialize(summaryPayload)
                        });
                    InsertSummaryFts(connection, summaryId, summaryText, tags, entities.Concat(participants).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                    int sourceOrdinal = 0;
                    foreach (string memoryId in sourceMemoryIds)
                    {
                        DialogueService.LinkMemorySource(connection, "summary", summaryId, "memory", memoryId, sourceOrdinal++);
                    }
                    int eventOrdinal = 0;
                    foreach (string eventId in sourceEvents)
                    {
                        DialogueService.LinkMemorySource(connection, "summary", summaryId, "event", eventId, eventOrdinal++);
                    }
                    foreach (string memoryId in DialogueService.MemorySummaryCoversSources(summaryText, rows)
                        && ReadString(compact, "compactionStatus", "") == "complete" ? sourceMemoryIds : new List<string>())
                    {
                        ExecuteSql(connection, "UPDATE memories SET status='consolidated' WHERE memory_id=$memory_id;",
                            new Dictionary<string, object> { ["memory_id"] = memoryId });
                    }
                    publication.Commit();
                }

                WriteJsonObject(CharacterFile(campaignId, npcId, "memory", "summary.json"), new Dictionary<string, object>
                {
                    ["version"] = 2,
                    ["summaryId"] = summaryId,
                    ["summary"] = summaryText,
                    ["sourceEventIds"] = sourceEvents,
                    ["memoryGroupKey"] = group.Key,
                    ["updatedUtc"] = DateTime.UtcNow.ToString("o")
                });
                created.Add(new Dictionary<string, object>
                {
                    ["summaryId"] = summaryId,
                    ["memoryGroupKey"] = group.Key,
                    ["summary"] = summaryText,
                    ["sourceEventIds"] = sourceEvents,
                    ["sourceMemoryCount"] = sourceMemoryIds.Count,
                    ["method"] = ReadString(compact, "method", "deterministic"),
                    ["compactionStatus"] = ReadString(compact, "compactionStatus", "incomplete"),
                    ["compactionDiagnostic"] = ReadDictionary(compact, "compactionDiagnostic") ?? TestDict(),
                    ["retryable"] = ReadBool(compact, "retryable", true)
                });
                if (created.Count >= maxGroups)
                {
                    break;
                }
            }

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["enabled"] = ReadBool(settings, "enableMemoryConsolidation", true),
                ["campaignId"] = campaignId,
                ["npcId"] = npcId,
                ["summaryCount"] = created.Count,
                ["compactionStatus"] = created.Any(r => ReadString(r, "compactionStatus", "") == "incomplete") ? "incomplete" : "complete",
                ["retryable"] = created.Any(r => ReadString(r, "compactionStatus", "") == "incomplete" && ReadBool(r, "retryable", true)),
                ["summaries"] = created
            };
        }

        internal static Dictionary<string, object> BuildMiddleTermSummary(Dictionary<string, object> settings, string npcId, string groupKey,
            List<Dictionary<string, object>> rows, int sourceCharacterLimit = 60000, string sourceInstructions = "",
            string correlationId = "", Func<Dictionary<string, object>, Dictionary<string, object>> responder = null,
            Action<IReadOnlyList<string>> captureSourceRecords = null, string campaignId = "")
        {
            if (responder != null && string.IsNullOrWhiteSpace(CampaignsRootOverride.Value))
                throw new InvalidOperationException("Compaction response fixtures require isolated Verification Lab state.");
            bool useLlm = ReadBool(settings, "useMemoryLlmForConsolidation", true);
            if (captureSourceRecords != null && (useLlm || !ReignPostgreSqlStorage.Options.IsValidation
                || string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)))
                throw new InvalidOperationException("Compaction source capture requires isolated validation with generation disabled.");
            int maxChars = Math.Max(500, Math.Min(5000, ReadInt(settings, "memoryConsolidationMaxSummaryChars", 1800)));
            bool dialogueDerived = (groupKey ?? "").StartsWith("conversation:", StringComparison.OrdinalIgnoreCase)
                || (groupKey ?? "").StartsWith("scene:", StringComparison.OrdinalIgnoreCase);
            List<string> sourceText = rows
                .OrderBy(row => ReadLong(row, "ts", 0))
                .SelectMany(row => DialogueService.OriginalCompactionRecords(row, dialogueDerived))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            captureSourceRecords?.Invoke(sourceText.AsReadOnly());
            sourceCharacterLimit = Math.Max(10000, Math.Min(120000, sourceCharacterLimit));
            string joined = BuildBalancedSummarySource(sourceText, sourceCharacterLimit);
            List<string> topics = DialogueService.BuildMemorySummaryTopics(rows.OrderBy(row => ReadLong(row, "ts", 0)), ReadInt(settings, "minimeMaxTopics", 5));
            var retained = DialogueService.ExtractiveMemoryFallback(sourceText, topics);
            if (!sourceText.Any()) return DialogueService.MemoryCompactionOutcome(retained, "no_original_source_records");
            if (rows.Any(row => {
                var originalPayload = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                return ReadString(originalPayload, "method", "") == "model_attributed_digest"
                    && ReadStringList(originalPayload, "originalSourceRecords").Count == 0;
            })) return DialogueService.MemoryCompactionOutcome(retained, "digest_original_sources_unavailable");
            var complete = DialogueService.AcceptMemorySummarySelection(TestDict("sourceIndices", Enumerable.Range(1, sourceText.Count).ToArray()),
                rows, sourceText, topics, maxChars);
            if (complete != null) return DialogueService.MemoryCompactionOutcome(complete, "complete_sources_fit_locally");
            if (!useLlm) return DialogueService.MemoryCompactionOutcome(retained, "generation_disabled");
            if (joined != string.Join("\n---\n", sourceText))
                return DialogueService.MemoryCompactionOutcome(retained, "source_input_exceeds_budget");
            if (responder == null && !LlmProviderConfigured(settings))
                return DialogueService.MemoryCompactionOutcome(retained, "provider_not_configured");
            {
                var request = new Dictionary<string, object>
                {
                    ["requestType"] = "memory",
                    ["campaignId"] = campaignId,
                    ["singleProviderAttempt"] = true,
                    ["correlationId"] = correlationId ?? string.Empty,
                    ["heroStringId"] = npcId,
                    ["temperature"] = 0.1d,
                    // The provider may spend completion tokens on hidden reasoning.
                    // Keep a bounded reserve in this one call; no automatic judge/retry.
                    ["maxTokens"] = Math.Clamp(maxChars * 2 + 2048, 4096, 12288),
                    ["messages"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["role"] = "system",
                            ["content"] = "You are Bannerlord Reign's private memory consolidation worker. Return strict JSON only. Preserve uncertainty, promises, threats, names, outcomes, chronology, and the NPC's point of view. Do not invent facts. Dialogue proves what a speaker said or believed; it does not by itself prove that the speaker's factual claim was true. NPC autobiography, alibis, claimed work, claimed sales, claimed location, claimed eyewitness activity, and claimed world facts must remain explicitly attributed to that speaker unless a separate non-dialogue evidence record verifies them. Never convert vivid first-person dialogue into direct-witness evidence. Conversation participation proves only that a person heard or spoke in that conversation, not that they witnessed an older event described inside it. If one NPC denies witnessing an older event while another speaker says 'we', 'everyone', or 'all three', preserve the contradiction and never silently promote the collective wording into firsthand evidence. Consider every supplied record, including the final record, before describing who did or did not respond. Use neutral terms such as participant or speaker unless the supplied evidence explicitly establishes a person's title or social role; never generalize one participant's title to the entire group."
                        },
                        new Dictionary<string, object>
                        {
                            ["role"] = "user",
                            ["content"] = "Compress these original records into a concise attributed recollection. Return {\"digest\":[{\"text\":\"Speaker A requested X; speaker B refused because Y.\",\"sourceIndices\":[1,2]}]}. Total text including citation labels must fit " + (maxChars - 200) + " characters; at most 24 entries. Cite existing one-based record numbers. Consider every record and the final answers. Preserve consequential names, who actually said or proposed what, promises, requests, standing preferences, conditions, scope, refusals, counteroffers, corrections, withdrawal and unresolved questions. Do not equate a quotation's reporter with its underlying actor/proposer or infer agreement from an offer. Keep uncertainty and contradictory accounts explicit. Omit repeated stage directions before consequential terms. This is model interpretation, not certified fact; the engine cannot prove semantic completeness. Originals remain available. If a responsible compact account cannot be made, return {\"digest\":[]}.\nGroup: " + groupKey
                                + "\nRecord count: " + sourceText.Count.ToString(CultureInfo.InvariantCulture)
                                + (string.IsNullOrWhiteSpace(sourceInstructions) ? "" : "\nSource rules: " + sourceInstructions.Trim())
                                + "\nRecords (chronological):\n" + string.Join("\n---\n", sourceText.Select((text, index) =>
                                    "Record " + (index + 1).ToString(CultureInfo.InvariantCulture) + " (" + text.Length + " characters):\n" + text))
                        }
                    },
                    ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
                };
                var llm = responder == null ? ChatWithLlm(request) : responder(request);
                return DialogueService.EvaluateMemoryCompactionResponse(llm, rows, sourceText, topics, maxChars, dialogueDerived);
            }
        }

        internal static string EnsureConversationSummaryProvenance(string summary, int maxChars)
        {
            const string prefix = "Conversation record (reported speech; not independent proof of world facts): ";
            string value = (summary ?? "").Trim();
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) value = prefix + value;
            return value;
        }

        private static string BuildBalancedSummarySource(List<string> sourceText, int maxChars)
        {
            sourceText = (sourceText ?? new List<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToList();
            if (sourceText.Count == 0) return "";
            maxChars = Math.Max(1000, maxChars);
            const string separator = "\n---\n";
            string complete = string.Join(separator, sourceText.ToArray());
            if (complete.Length <= maxChars) return complete;

            // A worker that cannot fit all source records must retain an
            // extractive fallback, rather than summarize clipped evidence.
            return string.Join(separator, DialogueService.SelectCompleteMemoryRecords(sourceText, maxChars));
        }

        internal static void InsertSummaryFts(ReignDbConnection connection, string summaryId, string text, List<string> tags, List<string> entities)
        {
            ExecuteSql(connection, "DELETE FROM summary_fts WHERE summary_id=$summary_id;",
                new Dictionary<string, object> { ["summary_id"] = summaryId });
            ExecuteSql(connection, "INSERT INTO summary_fts(summary_id,text,tags,entities) VALUES($summary_id,$text,$tags,$entities);",
                new Dictionary<string, object>
                {
                    ["summary_id"] = summaryId,
                    ["text"] = text ?? "",
                    ["tags"] = string.Join(" ", tags ?? new List<string>()),
                    ["entities"] = string.Join(" ", entities ?? new List<string>())
                });
        }
    }
}
