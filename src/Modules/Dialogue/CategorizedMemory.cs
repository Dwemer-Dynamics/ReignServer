using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NpgsqlTypes;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void EnsureCategorizedMemorySchema(ReignDbConnection connection)
        {
            EnsureDatabaseColumn(connection, "events", "event_category", "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "events", "event_subtype", "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "memories", "memory_domain", "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "summaries", "memory_lane", "TEXT NOT NULL DEFAULT ''");

            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS knowledge_receipts (
receipt_id TEXT PRIMARY KEY,event_id TEXT NOT NULL,npc_id TEXT NOT NULL,acquisition_type TEXT NOT NULL DEFAULT 'known',
confidence REAL NOT NULL DEFAULT 1.0,reliability REAL NOT NULL DEFAULT 1.0,acquired_day REAL NOT NULL DEFAULT 0,
source_id TEXT NOT NULL DEFAULT '',status TEXT NOT NULL DEFAULT 'active',payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,
UNIQUE(event_id,npc_id,acquisition_type,source_id));");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_knowledge_receipts_npc ON knowledge_receipts(npc_id,status,acquired_day DESC);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_knowledge_receipts_event ON knowledge_receipts(event_id,status);");

            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_sessions (
session_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,npc_id TEXT NOT NULL,player_id TEXT NOT NULL DEFAULT '',
channel TEXT NOT NULL DEFAULT 'in_person',status TEXT NOT NULL DEFAULT 'open',start_ts INTEGER NOT NULL,end_ts INTEGER NOT NULL DEFAULT 0,
start_world_day REAL NOT NULL DEFAULT 0,end_world_day REAL NOT NULL DEFAULT 0,location_id TEXT NOT NULL DEFAULT '',
participants_json TEXT NOT NULL DEFAULT '[]',close_reason TEXT NOT NULL DEFAULT '',scene_summary_id TEXT NOT NULL DEFAULT '',
legacy_inferred INTEGER NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}');");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_sessions_npc ON conversation_sessions(npc_id,status,start_ts DESC);");

            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_turns (
turn_id TEXT PRIMARY KEY,session_id TEXT NOT NULL,event_id TEXT NOT NULL DEFAULT '',turn_order INTEGER NOT NULL,
exchange_id TEXT NOT NULL DEFAULT '',role TEXT NOT NULL,speaker_id TEXT NOT NULL DEFAULT '',speaker_name TEXT NOT NULL DEFAULT '',
text TEXT NOT NULL,channel TEXT NOT NULL DEFAULT 'in_person',world_day REAL NOT NULL DEFAULT 0,ts INTEGER NOT NULL,
location_id TEXT NOT NULL DEFAULT '',participants_json TEXT NOT NULL DEFAULT '[]',status TEXT NOT NULL DEFAULT 'active',
payload_json TEXT NOT NULL DEFAULT '{}',UNIQUE(session_id,turn_order));");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_turns_session ON conversation_turns(session_id,turn_order);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_turns_ts ON conversation_turns(ts DESC);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_turns_speaker_ts ON conversation_turns(speaker_id,ts DESC);");
            ExecuteSql(connection, @"CREATE VIRTUAL TABLE IF NOT EXISTS conversation_turn_fts USING fts5(
turn_id UNINDEXED,text,speaker,participants,channel UNINDEXED);");

            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS memory_sources (
document_type TEXT NOT NULL,document_id TEXT NOT NULL,source_type TEXT NOT NULL,source_id TEXT NOT NULL,
ordinal INTEGER NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}',
PRIMARY KEY(document_type,document_id,source_type,source_id));");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_memory_sources_source ON memory_sources(source_type,source_id);");
        }

        private static void RunCategorizedMemoryMigration(ReignDbConnection connection, string campaignId)
        {
            Dictionary<string, object> applied = QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='categorized_memory_v1' LIMIT 1;").FirstOrDefault();
            if (string.Equals(ReadString(applied, "value", ""), "complete", StringComparison.OrdinalIgnoreCase))
            {
                RunMemoryDomainV2Migration(connection);
                RunConversationTurnPayloadV2Migration(connection);
                RunPlayerLieDetectionRemovalMigration(connection);
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (Dictionary<string, object> row in QuerySql(connection, "SELECT event_id,event_type,summary,payload_json FROM events;"))
            {
                Dictionary<string, object> payload = TryParseJsonObject(ReadString(row, "payload_json", "")) ?? new Dictionary<string, object>();
                string eventType = ReadString(row, "event_type", "unknown");
                string summary = ReadString(row, "summary", "");
                ExecuteSql(connection, "UPDATE events SET event_category=$category,event_subtype=$subtype WHERE event_id=$id;",
                    new Dictionary<string, object>
                    {
                        ["category"] = ClassifyEventCategory(eventType, payload, summary),
                        ["subtype"] = ClassifyEventSubtype(eventType, summary),
                        ["id"] = ReadString(row, "event_id", "")
                    });
            }

            foreach (Dictionary<string, object> row in QuerySql(connection, "SELECT memory_id,memory_type,summary,source,tags_json,payload_json FROM memories;"))
            {
                string domain = ClassifyMemoryDomain(row);
                ExecuteSql(connection, "UPDATE memories SET memory_domain=$domain WHERE memory_id=$id;",
                    new Dictionary<string, object> { ["domain"] = domain, ["id"] = ReadString(row, "memory_id", "") });
            }

            MigrateLegacyPublicProjections(connection, now);
            ImportLegacyConversationHistory(connection, campaignId);
            RebuildCategorizedSearchIndexes(connection);
            ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('categorized_memory_v1','complete');");
            RunMemoryDomainV2Migration(connection);
            RunConversationTurnPayloadV2Migration(connection);
            RunPlayerLieDetectionRemovalMigration(connection);
        }

        private static void RunMemoryDomainV2Migration(ReignDbConnection connection)
        {
            Dictionary<string, object> applied = QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='categorized_memory_domain_v2' LIMIT 1;").FirstOrDefault();
            if (string.Equals(ReadString(applied, "value", ""), "complete", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (Dictionary<string, object> row in QuerySql(connection,
                "SELECT memory_id,memory_type,summary,source,tags_json,payload_json FROM memories;"))
            {
                Dictionary<string, object> payload = TryParseJsonObject(ReadString(row, "payload_json", "")) ?? new Dictionary<string, object>();
                string eventType = ReadFirstString(payload, "eventType", "event_type", "type");
                string eventCategory = FirstNonEmpty(ReadFirstString(payload, "eventCategory", "event_category"),
                    ClassifyEventCategory(eventType, payload, ReadString(row, "summary", "")));
                string domain = MemoryDomainForWorldEventProjection(eventCategory, eventType,
                    ReadString(row, "memory_type", ""), ReadString(row, "summary", ""),
                    TextListFromJson(ReadString(row, "tags_json", "[]")), ReadString(row, "source", ""));
                ExecuteSql(connection, "UPDATE memories SET memory_domain=$domain WHERE memory_id=$id;",
                    new Dictionary<string, object> { ["domain"] = domain, ["id"] = ReadString(row, "memory_id", "") });
            }
            ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('categorized_memory_domain_v2','complete');");
        }

        private static void RunConversationTurnPayloadV2Migration(ReignDbConnection connection)
        {
            Dictionary<string, object> applied = QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='conversation_turn_payload_v2' LIMIT 1;").FirstOrDefault();
            if (string.Equals(ReadString(applied, "value", ""), "complete", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Dictionary<string, object> result = CompactConversationTurnPayloadRows(connection);
            ExecuteSql(connection,
                "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('conversation_turn_payload_v2','complete');");
            if (ReadLong(result, "bytesRemoved", 0L) > 0)
            {
                ExecuteSql(connection,
                    "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('campaign_compaction_required','1');");
            }
        }

        private static Dictionary<string, object> CompactConversationTurnPayloadRows(ReignDbConnection connection)
        {
            long rowsScanned = 0;
            long rowsChanged = 0;
            long bytesBefore = 0;
            long bytesAfter = 0;
            List<Tuple<string, string>> updates = new List<Tuple<string, string>>();
            using (ReignDbCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT turn_id,payload_json FROM conversation_turns WHERE length(payload_json)>2048;";
                command.CommandTimeout = 300;
                using (ReignDbDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string turnId = reader.IsDBNull(0) ? "" : reader.GetString(0);
                        string raw = reader.IsDBNull(1) ? "{}" : reader.GetString(1);
                        rowsScanned++;
                        Dictionary<string, object> source = TryParseJsonObject(raw);
                        if (source == null)
                        {
                            continue;
                        }

                        string compactJson = Json.Serialize(BuildConversationTurnStoragePayload(source, raw.Length));
                        if (compactJson.Length >= raw.Length)
                        {
                            continue;
                        }

                        updates.Add(Tuple.Create(turnId, compactJson));
                        rowsChanged++;
                        bytesBefore += Encoding.UTF8.GetByteCount(raw);
                        bytesAfter += Encoding.UTF8.GetByteCount(compactJson);
                    }
                }
            }

            if (updates.Count > 0)
            {
                ExecuteSql(connection, "BEGIN IMMEDIATE;");
                try
                {
                    using (ReignDbCommand update = connection.CreateCommand())
                    {
                        update.CommandText = "UPDATE conversation_turns SET payload_json=$payload WHERE turn_id=$id;";
                        update.CommandTimeout = 300;
                        System.Data.Common.DbParameter id = update.Parameters.Add("$id", NpgsqlDbType.Text);
                        System.Data.Common.DbParameter payload = update.Parameters.Add("$payload", NpgsqlDbType.Text);
                        foreach (Tuple<string, string> item in updates)
                        {
                            id.Value = item.Item1;
                            payload.Value = item.Item2;
                            update.ExecuteNonQuery();
                        }
                    }
                    ExecuteSql(connection, "COMMIT;");
                }
                catch
                {
                    try { ExecuteSql(connection, "ROLLBACK;"); } catch { }
                    throw;
                }
            }

            return new Dictionary<string, object>
            {
                ["rowsScanned"] = rowsScanned,
                ["rowsChanged"] = rowsChanged,
                ["bytesBefore"] = bytesBefore,
                ["bytesAfter"] = bytesAfter,
                ["bytesRemoved"] = Math.Max(0L, bytesBefore - bytesAfter)
            };
        }

        private static Dictionary<string, object> BuildConversationTurnStoragePayload(
            Dictionary<string, object> source, int originalCharacters = 0)
        {
            source = source ?? new Dictionary<string, object>();
            Dictionary<string, object> compact = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["storageSchema"] = "conversation_turn_payload_v2"
            };
            string[] scalarKeys =
            {
                "campaignId", "timelineId", "correlationId", "dialogueAuditRunId", "auditRunId",
                "liveTestRunId", "qualificationId", "dialogueAuditSceneIndex", "dialogueAuditTurnIndex",
                "conversationSessionId", "sessionId", "conversationId", "sceneTurnId", "turnId",
                "exchangeId", "eventId", "socialEventId", "sourceEventId", "templateId", "mode",
                "channel", "locationId", "currentSettlementId", "settlementId", "worldDay",
                "playerHeroStringId", "mainHeroStringId", "playerId", "playerName", "mainHeroName",
                "playerClanId", "playerClanName", "playerKingdomId", "playerKingdomName",
                "speakerHeroStringId", "heroStringId", "heroId", "speakerName", "speakerClanId",
                "speakerKingdomId", "npcClanId", "npcKingdomId", "npcSettlementId", "npcPartyId",
                "phaseId", "phaseIndex", "memoryGroupKey", "playerPromptLabel", "approachOpening",
                "actualPlayerTurn", "courtLifePhase", "conversationMode"
            };
            foreach (string key in scalarKeys)
            {
                object value;
                if (source.TryGetValue(key, out value) && IsCompactConversationMetadataScalar(value))
                {
                    compact[key] = value;
                }
            }

            string[] listKeys =
            {
                "participants", "activeHeroIds", "attendeeIds", "knownBy", "witnesses",
                "sourceTurnIds", "sourceEventIds"
            };
            foreach (string key in listKeys)
            {
                List<string> values = ReadStringList(source, key)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(16)
                    .ToList();
                if (values.Count > 0)
                {
                    compact[key] = values;
                }
            }

            List<Dictionary<string, object>> socialSignals = ReadDictionaryList(source, "socialSignals")
                .Take(8)
                .Select(signal => new Dictionary<string, object>
                {
                    ["signalId"] = ReadString(signal, "signalId", ""),
                    ["type"] = ReadString(signal, "type", ""),
                    ["speakerHeroId"] = ReadString(signal, "speakerHeroId", ""),
                    ["targetHeroId"] = ReadString(signal, "targetHeroId", ""),
                    ["supportingQuote"] = LimitText(ReadString(signal, "supportingQuote", ""), 512),
                    ["validated"] = ReadBool(signal, "validated", false),
                    ["validationSource"] = ReadString(signal, "validationSource", ""),
                    ["confidence"] = ReadString(signal, "confidence", ""),
                    ["exchangeId"] = ReadString(signal, "exchangeId", ""),
                    ["worldDay"] = ReadDouble(signal, "worldDay", 0d),
                    ["speakerClanTier"] = Math.Max(0, Math.Min(6, ReadInt(signal, "speakerClanTier", 0))),
                    ["targetClanTier"] = Math.Max(0, Math.Min(6, ReadInt(signal, "targetClanTier", 0)))
                })
                .ToList();
            if (socialSignals.Count > 0)
                compact["socialSignals"] = socialSignals;

            Dictionary<string, object> identityView = ReadDictionary(source, "identityView");
            if (identityView != null)
            {
                Dictionary<string, object> compactIdentity = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (string key in new[] { "knowsIdentity", "identityState", "canonicalName", "claimedName", "confidence", "verificationSource" })
                {
                    object value;
                    if (identityView.TryGetValue(key, out value) && IsCompactConversationMetadataScalar(value))
                    {
                        compactIdentity[key] = value;
                    }
                }
                if (compactIdentity.Count > 0)
                {
                    compact["identityView"] = compactIdentity;
                }
            }

            Dictionary<string, object> hero = ReadDictionary(source, "hero")
                ?? ReadDictionary(source, "speaker")
                ?? new Dictionary<string, object>();
            foreach (KeyValuePair<string, string> map in new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["heroStringId"] = "heroStringId",
                ["name"] = "speakerName",
                ["clanId"] = "speakerClanId",
                ["kingdomId"] = "speakerKingdomId",
                ["currentSettlementId"] = "npcSettlementId",
                ["partyId"] = "npcPartyId"
            })
            {
                if (compact.ContainsKey(map.Value))
                {
                    continue;
                }
                object value;
                if (hero.TryGetValue(map.Key, out value) && IsCompactConversationMetadataScalar(value))
                {
                    compact[map.Value] = value;
                }
            }

            if (originalCharacters > 0)
            {
                compact["originalPayloadCharacters"] = originalCharacters;
                compact["actionResolutionIndexOmitted"] = source.ContainsKey("actionResolutionIndex");
            }
            return compact;
        }

        private static string SerializeConversationTurnStoragePayload(Dictionary<string, object> source)
        {
            const int hardLimitCharacters = 16384;
            Dictionary<string, object> compact = BuildConversationTurnStoragePayload(source);
            string json = Json.Serialize(compact);
            if (json.Length <= hardLimitCharacters) return json;
            Dictionary<string, object> fallback = new Dictionary<string, object>
            {
                ["storageSchema"] = "conversation_turn_payload_v2",
                ["payloadGuardTriggered"] = true,
                ["correlationId"] = ReadString(compact, "correlationId", ""),
                ["conversationSessionId"] = ReadFirstString(compact, "conversationSessionId", "sessionId", "conversationId"),
                ["exchangeId"] = ReadString(compact, "exchangeId", ""),
                ["turnId"] = ReadString(compact, "turnId", "")
            };
            LogOperational("storage.conversation_turn_payload_guard", new Dictionary<string, object>
            {
                ["characters"] = json.Length, ["hardLimitCharacters"] = hardLimitCharacters,
                ["correlationId"] = ReadString(compact, "correlationId", "")
            });
            return Json.Serialize(fallback);
        }

        private static bool IsCompactConversationMetadataScalar(object value)
        {
            if (value == null || value is bool || value is byte || value is short || value is int
                || value is long || value is float || value is double || value is decimal)
            {
                return true;
            }
            string text = value as string;
            return text != null && text.Length <= 512;
        }

        private static void MigrateLegacyPublicProjections(ReignDbConnection connection, long now)
        {
            List<Dictionary<string, object>> groups = QuerySql(connection, @"SELECT event_id,COUNT(*) AS row_count
FROM memories WHERE event_id<>'' AND visibility LIKE 'public%' AND status='active'
GROUP BY event_id HAVING COUNT(*)>1;");
            foreach (Dictionary<string, object> group in groups)
            {
                string eventId = ReadString(group, "event_id", "");
                List<Dictionary<string, object>> rows = QuerySql(connection,
                    "SELECT * FROM memories WHERE event_id=$event AND status='active' ORDER BY ts,memory_id;",
                    new Dictionary<string, object> { ["event"] = eventId });
                if (rows.Count == 0)
                {
                    continue;
                }

                Dictionary<string, object> first = rows[0];
                string canonicalId = "mem_world_" + Guid.NewGuid().ToString("N");
                InsertMemoryRow(connection, canonicalId, eventId, "", "world_knowledge",
                    ReadLong(first, "ts", now), ReadDouble(first, "world_day", 0d), ReadString(first, "location_id", ""),
                    ReadString(first, "summary", ""), TextListFromJson(ReadString(first, "participants_json", "[]")),
                    TextListFromJson(ReadString(first, "witnesses_json", "[]")), ReadString(first, "about_entities_json", "[]"),
                    new List<string>(), TextListFromJson(ReadString(first, "heard_as_rumor_by_json", "[]")),
                    TextListFromJson(ReadString(first, "hidden_from_json", "[]")), ReadString(first, "visibility", "public"),
                    ReadDouble(first, "importance", 0.5d), ReadDouble(first, "emotional_weight", 0d), ReadDouble(first, "confidence", 1d),
                    TextListFromJson(ReadString(first, "tags_json", "[]")), "active", "legacy_canonical_migration",
                    ReadString(first, "payload_json", "{}"), "", "not_indexed");
                ExecuteSql(connection, "UPDATE memories SET memory_domain='world_affairs' WHERE memory_id=$id;",
                    new Dictionary<string, object> { ["id"] = canonicalId });

                HashSet<string> direct = new HashSet<string>(
                    rows.SelectMany(row => TextListFromJson(ReadString(row, "participants_json", "[]"))
                        .Concat(TextListFromJson(ReadString(row, "witnesses_json", "[]")))), StringComparer.OrdinalIgnoreCase);
                foreach (Dictionary<string, object> row in rows)
                {
                    string memoryId = ReadString(row, "memory_id", "");
                    string ownerId = ReadString(row, "owner_id", "");
                    if (!string.IsNullOrWhiteSpace(ownerId) && direct.Contains(ownerId))
                    {
                        ExecuteSql(connection, @"UPDATE memories SET memory_type='personal_experience',memory_domain='world_affairs',
visibility='private',known_by_json=$known WHERE memory_id=$id;",
                            new Dictionary<string, object> { ["known"] = Json.Serialize(new List<string> { ownerId }), ["id"] = memoryId });
                        UpsertKnowledgeReceipt(connection, eventId, ownerId,
                            TextListFromJson(ReadString(row, "witnesses_json", "[]")).Contains(ownerId, StringComparer.OrdinalIgnoreCase) ? "witness" : "participant",
                            ReadDouble(row, "confidence", 1d), 1d, ReadDouble(row, "world_day", 0d), memoryId, now);
                    }
                    else
                    {
                        ExecuteSql(connection, "UPDATE memories SET status='superseded',memory_domain='world_affairs' WHERE memory_id=$id;",
                            new Dictionary<string, object> { ["id"] = memoryId });
                    }
                }
            }
        }

        private static void ImportLegacyConversationHistory(ReignDbConnection connection, string campaignId)
        {
            string charactersDir = Path.Combine(CampaignDirectory(campaignId), "characters");
            if (!Directory.Exists(charactersDir))
            {
                return;
            }

            foreach (string characterDir in Directory.GetDirectories(charactersDir))
            {
                string heroId = Path.GetFileName(characterDir);
                string path = Path.Combine(characterDir, "history", "dialogue.jsonl");
                List<Dictionary<string, object>> lines = ReadJsonLinesFromPath(path)
                    .OrderBy(line => ReadLong(line, "ts", 0)).ToList();
                if (lines.Count == 0)
                {
                    continue;
                }

                List<List<Dictionary<string, object>>> sessions = new List<List<Dictionary<string, object>>>();
                foreach (Dictionary<string, object> line in lines)
                {
                    long ts = ReadLong(line, "ts", 0);
                    string channel = ReadString(line, "channel", "in_person");
                    List<Dictionary<string, object>> current = sessions.LastOrDefault();
                    if (current == null || current.Count == 0
                        || ts - ReadLong(current.Last(), "ts", 0) > 1800
                        || !string.Equals(channel, ReadString(current.Last(), "channel", "in_person"), StringComparison.OrdinalIgnoreCase))
                    {
                        current = new List<Dictionary<string, object>>();
                        sessions.Add(current);
                    }
                    current.Add(line);
                }

                int sessionOrdinal = 0;
                foreach (List<Dictionary<string, object>> sessionLines in sessions)
                {
                    long startTs = ReadLong(sessionLines.First(), "ts", 0);
                    long endTs = ReadLong(sessionLines.Last(), "ts", startTs);
                    string sessionId = "legacy_" + SafeMemoryKey(heroId) + "_" + startTs.ToString(CultureInfo.InvariantCulture) + "_" + sessionOrdinal.ToString(CultureInfo.InvariantCulture);
                    ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_sessions(
session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,location_id,
participants_json,close_reason,scene_summary_id,legacy_inferred,payload_json)
VALUES($id,$campaign,$npc,'',$channel,'closed',$start,$end,$start_day,$end_day,'','[]','legacy_import','',1,'{}');",
                        new Dictionary<string, object>
                        {
                            ["id"] = sessionId, ["campaign"] = campaignId, ["npc"] = heroId,
                            ["channel"] = ReadString(sessionLines.First(), "channel", "in_person"),
                            ["start"] = startTs, ["end"] = endTs,
                            ["start_day"] = ReadDouble(sessionLines.First(), "worldDay", 0d),
                            ["end_day"] = ReadDouble(sessionLines.Last(), "worldDay", 0d)
                        });
                    int turnOrder = 0;
                    foreach (Dictionary<string, object> line in sessionLines)
                    {
                        string turnId = FirstNonEmpty(ReadString(line, "id", ""), sessionId + "_" + turnOrder.ToString(CultureInfo.InvariantCulture));
                        InsertConversationTurn(connection, turnId, sessionId, "", turnOrder, sessionId + "_exchange_" + (turnOrder / 2).ToString(CultureInfo.InvariantCulture),
                            ReadString(line, "role", ""), "", ReadString(line, "speaker", ""), ReadString(line, "text", ""),
                            ReadString(line, "channel", "in_person"), ReadDouble(line, "worldDay", 0d), ReadLong(line, "ts", startTs), "",
                            new List<string>(), line);
                        turnOrder++;
                    }
                    sessionOrdinal++;
                }
            }
        }

        private static void RebuildCategorizedSearchIndexes(ReignDbConnection connection)
        {
            ExecuteSql(connection, "DELETE FROM memory_fts;");
            foreach (Dictionary<string, object> row in QuerySql(connection, "SELECT * FROM memories WHERE status='active';"))
            {
                InsertMemoryFts(connection, ReadString(row, "memory_id", ""), ReadString(row, "summary", ""),
                    TextListFromJson(ReadString(row, "tags_json", "[]")),
                    TextListFromJson(ReadString(row, "about_entities_json", "[]"))
                        .Concat(TextListFromJson(ReadString(row, "participants_json", "[]"))).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            }
            ExecuteSql(connection, "DELETE FROM conversation_turn_fts;");
            foreach (Dictionary<string, object> row in QuerySql(connection, "SELECT * FROM conversation_turns WHERE status='active';"))
            {
                InsertConversationTurnFts(connection, ReadString(row, "turn_id", ""), ReadString(row, "text", ""),
                    ReadString(row, "speaker_name", ""), ReadString(row, "participants_json", "[]"), ReadString(row, "channel", "in_person"));
            }
        }

        private static Dictionary<string, object> ConversationStartApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string npcId = ReadFirstString(payload, "npcId", "heroStringId", "heroId");
            string playerId = ReadFirstString(payload, "playerId", "playerHeroStringId", "mainHeroStringId");
            if (string.IsNullOrWhiteSpace(npcId))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "npcId or heroStringId is required." };
            }

            List<string> interrupted;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                interrupted = QuerySql(connection,
                    "SELECT session_id FROM conversation_sessions WHERE npc_id=$npc AND status='open' ORDER BY start_ts;",
                    new Dictionary<string, object> { ["npc"] = npcId })
                    .Select(row => ReadString(row, "session_id", "")).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
            }
            foreach (string sessionId in interrupted)
            {
                FinishConversationSession(campaignId, sessionId, "interrupted_by_new_session", true,
                    ReadDouble(payload, "worldDay", 0d), ReadLong(payload, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            }

            string id = FirstNonEmpty(ReadFirstString(payload, "sessionId", "conversationSessionId"), "conversation_" + Guid.NewGuid().ToString("N"));
            long ts = ReadLong(payload, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            string channel = ReadString(payload, "channel", "in_person");
            string locationId = ReadFirstString(payload, "locationId", "settlementId", "currentSettlementId");
            List<string> participants = MergeStringLists(ReadStringList(payload, "participants"), new[] { npcId, playerId });
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                ExecuteSql(connection, @"INSERT OR REPLACE INTO conversation_sessions(
session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,location_id,
participants_json,close_reason,scene_summary_id,legacy_inferred,payload_json)
VALUES($id,$campaign,$npc,$player,$channel,'open',$ts,0,$day,0,$location,$participants,'','',0,$payload);",
                    new Dictionary<string, object>
                    {
                        ["id"] = id, ["campaign"] = campaignId, ["npc"] = npcId, ["player"] = playerId,
                        ["channel"] = channel, ["ts"] = ts, ["day"] = worldDay, ["location"] = locationId,
                        ["participants"] = Json.Serialize(participants), ["payload"] = Json.Serialize(payload)
                    });
            }
            return new Dictionary<string, object> { ["ok"] = true, ["sessionId"] = id, ["interruptedSessionsClosed"] = interrupted };
        }

        private static Dictionary<string, object> ConversationFinishApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string sessionId = ReadFirstString(payload, "sessionId", "conversationSessionId", "conversationId");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return MemoryConversationFinishedApi(payload);
            }
            return FinishConversationSession(campaignId, sessionId, ReadString(payload, "reason", "closed_by_game"),
                ReadBool(payload, "interrupted", false), ReadDouble(payload, "worldDay", 0d),
                ReadLong(payload, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        }

        private static Dictionary<string, object> ConversationRecoverApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            long ts = ReadLong(payload, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            List<string> openSessions;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                openSessions = QuerySql(connection, "SELECT session_id FROM conversation_sessions WHERE status='open' ORDER BY start_ts;")
                    .Select(row => ReadString(row, "session_id", "")).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
            }
            List<Dictionary<string, object>> recovered = new List<Dictionary<string, object>>();
            foreach (string sessionId in openSessions)
            {
                recovered.Add(FinishConversationSession(campaignId, sessionId, "interrupted_by_campaign_reload", true, worldDay, ts));
            }
            return new Dictionary<string, object> { ["ok"] = true, ["recoveredCount"] = recovered.Count, ["sessions"] = recovered };
        }

        private static Dictionary<string, object> FinishConversationSession(string campaignId, string sessionId, string reason, bool interrupted, double worldDay, long ts)
        {
            Dictionary<string, object> session;
            List<Dictionary<string, object>> turns;
            string sceneJobId;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            using (var closure = connection.BeginTransaction())
            {
                session = QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$id LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault();
                if (session == null)
                {
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Conversation session was not found.", ["sessionId"] = sessionId };
                }
                turns = QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$id AND status='active' ORDER BY turn_order;",
                    new Dictionary<string, object> { ["id"] = sessionId });
                var existingScene=QuerySql(connection,"SELECT payload_json FROM summaries WHERE summary_id=$id;",
                    new Dictionary<string,object>{["id"]=ReadString(session,"scene_summary_id","")}).FirstOrDefault();
                if (existingScene != null && ReadString(TryParseJsonObject(ReadString(existingScene,"payload_json","{}")),"sourceHash","")==MemorySourceHash(turns))
                {
                    if (ReadString(session,"status","")!="closed" && ReadString(session,"status","")!="interrupted")
                        ExecuteSql(connection,"UPDATE conversation_sessions SET status=$status,end_ts=$ts,end_world_day=$day,close_reason=$reason WHERE session_id=$id;",
                            new Dictionary<string,object>{["status"]=interrupted?"interrupted":"closed",["ts"]=ts,["day"]=worldDay,["reason"]=reason??"",["id"]=sessionId});
                    closure.Commit();
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true, ["idempotent"] = true, ["sessionId"] = sessionId,
                        ["sceneSummaryId"] = ReadString(session, "scene_summary_id", "")
                    };
                }
                sceneJobId = EnqueueMemoryJob(connection, campaignId, "scene_summary", ReadString(session, "npc_id", ""), "scene:" + sessionId, sessionId);
                ExecuteSql(connection, @"UPDATE conversation_sessions SET status=$status,end_ts=$ts,end_world_day=$day,
close_reason=$reason WHERE session_id=$id;", new Dictionary<string, object>
                {
                    ["status"] = interrupted ? "interrupted" : "closed", ["ts"] = ts, ["day"] = worldDay,
                    ["reason"] = reason ?? "", ["id"] = sessionId
                });
                closure.Commit();
            }

            List<Dictionary<string, object>> playerTurns = turns
                .Where(turn => ReadString(turn, "role", "").Equals("player", StringComparison.OrdinalIgnoreCase))
                .ToList();
            bool recallOnlySession = playerTurns.Count > 0
                && playerTurns.All(turn => IsPureConversationRecallRequest(ReadString(turn, "text", "")));
            ProcessMemoryBackgroundJob(campaignId, sceneJobId);
            Dictionary<string, object> scene;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                var job = QuerySql(connection, "SELECT result_json FROM memory_background_jobs WHERE job_id=$id;",
                    new Dictionary<string, object> { ["id"] = sceneJobId }).Single();
                scene = TryParseJsonObject(ReadString(job, "result_json", "{}")) ?? new Dictionary<string, object>();
            }
            string sceneId = ReadString(scene, "summaryId", "");
            Dictionary<string,object> arc = ReadDictionary(scene,"arc") ?? new Dictionary<string,object>();
            Dictionary<string,object> continuityArcRepairs = ReadDictionary(scene,"continuityArcRepairs") ?? new Dictionary<string,object>();
            string playerId = ReadString(session, "player_id", "");
            List<string> memoryOwners = TextListFromJson(ReadString(session, "participants_json", "[]"))
                .Where(id => !string.IsNullOrWhiteSpace(id)
                    && !string.Equals(id, playerId, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string primaryNpcId = ReadString(session, "npc_id", "");
            if (!string.IsNullOrWhiteSpace(primaryNpcId)
                && !memoryOwners.Contains(primaryNpcId, StringComparer.OrdinalIgnoreCase))
            {
                memoryOwners.Insert(0, primaryNpcId);
            }
            List<Dictionary<string, object>> participantConsolidations = string.IsNullOrWhiteSpace(sceneId) || recallOnlySession
                ? new List<Dictionary<string, object>>()
                : memoryOwners.Select(ownerId =>
                {
                    Dictionary<string, object> item = ScheduleMemoryConsolidation(
                        campaignId, ownerId, "conversation:" + sessionId, sessionId);
                    item["ownerId"] = ownerId;
                    return item;
                }).ToList();
            Dictionary<string, object> consolidation = participantConsolidations
                .FirstOrDefault(item => string.Equals(ReadString(item, "ownerId", ""), primaryNpcId, StringComparison.OrdinalIgnoreCase))
                ?? new Dictionary<string, object>();
            Dictionary<string, object> courtSocial;
            try
            {
                courtSocial = ProcessCompletedConversationCourtStanding(
                    campaignId, session, turns, worldDay, interrupted);
            }
            catch (Exception ex)
            {
                courtSocial = new Dictionary<string, object>
                {
                    ["processed"] = false,
                    ["error"] = LimitText(ex.Message, 1000)
                };
                LogOperational("court_social.conversation_finish_failed",
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId, ["sessionId"] = sessionId,
                        ["error"] = LimitText(ex.ToString(), 4000)
                    });
            }
            Dictionary<string, object> ambassadorReport;
            try
            {
                ambassadorReport = FinalizeOfficialAmbassadorScene(campaignId, session, turns, scene, worldDay, ts);
            }
            catch (Exception ex)
            {
                ambassadorReport = new Dictionary<string, object> { ["processed"] = false, ["error"] = LimitText(ex.Message, 1000) };
                LogOperational("ambassador.scene_finalize_failed", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["sessionId"] = sessionId, ["error"] = LimitText(ex.ToString(), 4000)
                });
            }
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["sessionId"] = sessionId, ["status"] = interrupted ? "interrupted" : "closed",
                ["sceneSummaryId"] = sceneId, ["scene"] = scene, ["arc"] = arc, ["consolidation"] = consolidation,
                ["continuityArcRepairs"] = continuityArcRepairs,
                ["participantConsolidations"] = participantConsolidations, ["recallOnlySession"] = recallOnlySession,
                ["courtSocial"] = courtSocial, ["ambassadorReport"] = ambassadorReport
            };
        }

        private static Dictionary<string, object> StoreConversationExchange(string campaignId, Dictionary<string, object> request,
            string npcId, string playerId, string playerName, string npcName, string playerText, string reply, string eventId, long ts)
        {
            request = request ?? new Dictionary<string, object>();
            string sessionId = ReadFirstString(request, "conversationSessionId", "sessionId", "conversationId");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                Dictionary<string, object> started = ConversationStartApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["npcId"] = npcId, ["playerId"] = playerId,
                    ["worldDay"] = ReadDouble(request, "worldDay", 0d), ["locationId"] = ReadFirstString(request, "locationId", "currentSettlementId", "settlementId"),
                    ["channel"] = ReadString(request, "channel", "in_person"), ["ts"] = ts
                });
                sessionId = ReadString(started, "sessionId", "");
            }
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Could not establish a conversation session." };
            }

            double worldDay = ReadDouble(request, "worldDay", 0d);
            string locationId = ReadFirstString(request, "locationId", "currentSettlementId", "settlementId");
            string channel = ReadString(request, "channel", "in_person");
            List<string> participants = MergeStringLists(ReadStringList(request, "participants"), new[] { npcId, playerId });
            int nextOrder;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                Dictionary<string, object> max = QuerySql(connection,
                    "SELECT COALESCE(MAX(turn_order),-1)+1 AS next_order FROM conversation_turns WHERE session_id=$session;",
                    new Dictionary<string, object> { ["session"] = sessionId }).FirstOrDefault();
                nextOrder = ReadInt(max, "next_order", 0);
                string exchangeId = eventId;
                string playerTurnId = eventId + "_player";
                string npcTurnId = eventId + "_npc";
                InsertConversationTurn(connection, playerTurnId, sessionId, eventId, nextOrder, exchangeId, "player", playerId,
                    playerName, playerText, channel, worldDay, ts, locationId, participants, request);
                InsertConversationTurn(connection, npcTurnId, sessionId, eventId, nextOrder + 1, exchangeId, "npc", npcId,
                    npcName, reply, channel, worldDay, ts, locationId, participants, request);
                ExecuteSql(connection, "UPDATE conversation_sessions SET end_ts=$ts,end_world_day=$day,location_id=CASE WHEN $location<>'' THEN $location ELSE location_id END WHERE session_id=$id;",
                    new Dictionary<string, object> { ["ts"] = ts, ["day"] = worldDay, ["location"] = locationId, ["id"] = sessionId });
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["sessionId"] = sessionId, ["exchangeId"] = exchangeId,
                    ["turnIds"] = new List<string> { playerTurnId, npcTurnId }, ["turnOrder"] = nextOrder
                };
            }
        }

        private static Dictionary<string, object> StorePartyConversationExchange(
            string campaignId,
            Dictionary<string, object> request,
            string npcId,
            string playerId,
            string playerName,
            string npcName,
            string playerText,
            string reply,
            string eventId,
            long ts)
        {
            return StoreGroupedConversationExchange(campaignId, request, npcId, playerId, playerName, npcName,
                playerText, reply, eventId, ts, "party_chat", "");
        }

        private static Dictionary<string, object> StoreSocialEventConversationExchange(
            string campaignId,
            Dictionary<string, object> request,
            string npcId,
            string playerId,
            string playerName,
            string npcName,
            string playerText,
            string reply,
            string sourceEventId,
            string socialEventId,
            long ts)
        {
            string channel = string.Equals(ReadString(request, "templateId", ""), "generated_wilderness", StringComparison.OrdinalIgnoreCase)
                ? "wilderness_event" : "social_event";
            string sessionId = string.IsNullOrWhiteSpace(socialEventId) ? "" : "social_event_" + SafeMemoryKey(socialEventId);
            return StoreGroupedConversationExchange(campaignId, request, npcId, playerId, playerName, npcName,
                playerText, reply, sourceEventId, ts, channel, sessionId);
        }

        private static Dictionary<string, object> StoreGroupedConversationExchange(
            string campaignId,
            Dictionary<string, object> request,
            string npcId,
            string playerId,
            string playerName,
            string npcName,
            string playerText,
            string reply,
            string eventId,
            long ts,
            string channel,
            string fallbackSessionId)
        {
            request = request ?? new Dictionary<string, object>();
            channel = string.IsNullOrWhiteSpace(channel) ? "party_chat" : channel;
            string sessionId = FirstNonEmpty(ReadFirstString(request, "conversationSessionId", "sessionId", "conversationId"), fallbackSessionId);
            List<string> participants = MergeStringLists(
                MergeStringLists(ReadStringList(request, "participants"), ReadStringList(request, "activeHeroIds")),
                new[] { npcId, playerId });
            double worldDay = ReadDouble(request, "worldDay", 0d);
            string locationId = ReadFirstString(request, "locationId", "currentSettlementId", "settlementId");

            bool sessionExists;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                sessionExists = !string.IsNullOrWhiteSpace(sessionId)
                    && QuerySql(connection, "SELECT session_id FROM conversation_sessions WHERE session_id=$id LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = sessionId }).Count > 0;
            }
            if (!sessionExists)
            {
                Dictionary<string, object> started = ConversationStartApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["sessionId"] = sessionId,
                    ["npcId"] = npcId,
                    ["playerId"] = playerId,
                    ["worldDay"] = worldDay,
                    ["locationId"] = locationId,
                    ["channel"] = channel,
                    ["participants"] = participants,
                    ["ts"] = ts
                });
                sessionId = ReadString(started, "sessionId", "");
            }
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Could not establish a party conversation session." };
            }

            string exchangeId = FirstNonEmpty(
                ReadFirstString(request, "sceneTurnId", "turnId", "exchangeId"),
                eventId);
            string playerTurnId = exchangeId + "_player";
            string npcTurnId = exchangeId + "_npc_" + SafeMemoryKey(npcId);
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                Dictionary<string, object> session = QuerySql(connection,
                    "SELECT participants_json FROM conversation_sessions WHERE session_id=$id LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault();
                participants = MergeStringLists(
                    TextListFromJson(ReadString(session, "participants_json", "[]")),
                    participants);

                bool playerExists = QuerySql(connection,
                    "SELECT turn_id FROM conversation_turns WHERE turn_id=$turn LIMIT 1;",
                    new Dictionary<string, object> { ["turn"] = playerTurnId }).Count > 0;
                int nextOrder = ReadInt(QuerySql(connection,
                    "SELECT COALESCE(MAX(turn_order),-1)+1 AS next_order FROM conversation_turns WHERE session_id=$session;",
                    new Dictionary<string, object> { ["session"] = sessionId }).FirstOrDefault(), "next_order", 0);
                if (!playerExists)
                {
                    InsertConversationTurn(connection, playerTurnId, sessionId, eventId, nextOrder++, exchangeId,
                        "player", playerId, playerName, playerText, channel, worldDay, ts, locationId, participants, request);
                }
                InsertConversationTurn(connection, npcTurnId, sessionId, eventId, nextOrder, exchangeId,
                    "npc", npcId, npcName, reply, channel, worldDay, ts, locationId, participants, request);
                ExecuteSql(connection, @"UPDATE conversation_sessions SET end_ts=$ts,end_world_day=$day,
location_id=CASE WHEN $location<>'' THEN $location ELSE location_id END,participants_json=$participants
WHERE session_id=$id;", new Dictionary<string, object>
                {
                    ["ts"] = ts,
                    ["day"] = worldDay,
                    ["location"] = locationId,
                    ["participants"] = Json.Serialize(participants),
                    ["id"] = sessionId
                });
            }
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["sessionId"] = sessionId,
                ["exchangeId"] = exchangeId,
                ["turnIds"] = new List<string> { playerTurnId, npcTurnId }
            };
        }

        private static List<Dictionary<string, object>> ReadCurrentSessionDialogueLines(string campaignId, string sessionId, int limit)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return new List<Dictionary<string, object>>();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                return QuerySql(connection, @"SELECT turn_id AS id,turn_order,ts,role,speaker_name AS speaker,channel,text,world_day AS worldDay,
session_id AS sessionId,exchange_id AS exchangeId,location_id AS locationId FROM conversation_turns
WHERE session_id=$session AND status='active' ORDER BY turn_order DESC LIMIT $limit;",
                    new Dictionary<string, object> { ["session"] = sessionId, ["limit"] = Math.Max(1, limit) })
                    .OrderBy(row => ReadInt(row, "turn_order", 0)).ToList();
            }
        }

        private static void InsertConversationTurn(ReignDbConnection connection, string turnId, string sessionId, string eventId,
            int turnOrder, string exchangeId, string role, string speakerId, string speakerName, string text, string channel,
            double worldDay, long ts, string locationId, List<string> participants, Dictionary<string, object> payload)
        {
            if (string.IsNullOrWhiteSpace(turnId) || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_turns(
turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,channel,world_day,ts,location_id,
participants_json,status,payload_json)
VALUES($turn,$session,$event,$ordinal,$exchange,$role,$speaker_id,$speaker_name,$text,$channel,$day,$ts,$location,$participants,'active',$payload);",
                new Dictionary<string, object>
                {
                    ["turn"] = turnId, ["session"] = sessionId, ["event"] = eventId ?? "", ["ordinal"] = turnOrder,
                    ["exchange"] = exchangeId ?? "", ["role"] = role ?? "", ["speaker_id"] = speakerId ?? "",
                    ["speaker_name"] = speakerName ?? "", ["text"] = text ?? "", ["channel"] = channel ?? "in_person",
                    ["day"] = worldDay, ["ts"] = ts, ["location"] = locationId ?? "",
                    ["participants"] = Json.Serialize(participants ?? new List<string>()),
                    ["payload"] = SerializeConversationTurnStoragePayload(payload)
                });
            InsertConversationTurnFts(connection, turnId, text, speakerName, Json.Serialize(participants ?? new List<string>()), channel);
        }

        private static void InsertConversationTurnFts(ReignDbConnection connection, string turnId, string text, string speaker, string participants, string channel)
        {
            ExecuteSql(connection, "DELETE FROM conversation_turn_fts WHERE turn_id=$id;", new Dictionary<string, object> { ["id"] = turnId });
            ExecuteSql(connection, "INSERT INTO conversation_turn_fts(turn_id,text,speaker,participants,channel) VALUES($id,$text,$speaker,$participants,$channel);",
                new Dictionary<string, object> { ["id"] = turnId, ["text"] = text ?? "", ["speaker"] = speaker ?? "", ["participants"] = participants ?? "", ["channel"] = channel ?? "" });
        }

        private static void StoreCorrespondenceTurn(string campaignId, string npcId, string playerId, Dictionary<string, object> letter, double worldDay)
        {
            string letterId = ReadFirstString(letter, "letter_id", "letterId");
            string text = ReadFirstString(letter, "body", "text");
            if (string.IsNullOrWhiteSpace(letterId) || string.IsNullOrWhiteSpace(npcId) || string.IsNullOrWhiteSpace(text)) return;
            string sessionId = "mail_" + SafeMemoryKey(letterId);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string senderId = ReadString(letter, "sender_id", "");
            string recipientId = ReadString(letter, "recipient_id", "");
            List<string> participants = MergeStringLists(new[] { senderId, recipientId, npcId, playerId }, new List<string>());
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_sessions(
session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,location_id,
participants_json,close_reason,scene_summary_id,legacy_inferred,payload_json)
VALUES($id,$campaign,$npc,$player,'correspondence','closed',$ts,$ts,$day,$day,'',$participants,'letter_delivered','',0,$payload);",
                    new Dictionary<string, object>
                    {
                        ["id"] = sessionId, ["campaign"] = campaignId, ["npc"] = npcId, ["player"] = playerId ?? "",
                        ["ts"] = ts, ["day"] = worldDay, ["participants"] = Json.Serialize(participants), ["payload"] = Json.Serialize(letter)
                    });
                InsertConversationTurn(connection, "turn_" + SafeMemoryKey(letterId), sessionId, "mail_delivery_" + letterId, 0, letterId,
                    senderId.Equals(playerId, StringComparison.OrdinalIgnoreCase) ? "player" : "npc", senderId,
                    ReadString(letter, "sender_name", senderId), text, "correspondence", worldDay, ts, "", participants, letter);
            }
        }

        private static Dictionary<string, object> CreateConversationSceneSummary(string campaignId, Dictionary<string, object> session,
            List<Dictionary<string, object>> turns, long now, Dictionary<string, object> settingsOverride = null,
            bool retrievalEligible = true)
        {
            turns = turns ?? new List<Dictionary<string, object>>();
            if (turns.Count == 0)
            {
                return new Dictionary<string, object> { ["ok"] = true, ["summaryId"] = "", ["reason"] = "empty_session" };
            }
            string npcId = ReadString(session, "npc_id", "");
            string sessionId = ReadString(session, "session_id", "");
            string playerId = ReadString(session, "player_id", "");
            Dictionary<string, object> identitySource =
                TryParseJsonObject(ReadString(session, "payload_json", "")) ?? new Dictionary<string, object>();
            foreach (Dictionary<string, object> turn in turns)
            {
                Dictionary<string, object> turnPayload =
                    TryParseJsonObject(ReadString(turn, "payload_json", "")) ?? new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> pair in turnPayload)
                {
                    identitySource[pair.Key] = pair.Value;
                }
                if (ReadString(turn, "role", "").Equals("player", StringComparison.OrdinalIgnoreCase))
                {
                    if (!identitySource.ContainsKey("mainHeroName"))
                        identitySource["mainHeroName"] = ReadString(turn, "speaker_name", "");
                    if (!identitySource.ContainsKey("mainHeroStringId"))
                        identitySource["mainHeroStringId"] = ReadString(turn, "speaker_id", playerId);
                }
            }
            KnowledgeAccessContext identityKnowledge = BuildKnowledgeAccessContext(
                npcId, playerId, ReadString(session, "location_id", ""), identitySource);
            List<Dictionary<string, object>> pseudoRows = turns.Select(turn =>
            {
                string role = ReadString(turn, "role", "");
                string provenance = role.Equals("player", StringComparison.OrdinalIgnoreCase)
                    ? "PLAYER LINE (authoritative only that the player said or requested this; factual claims remain claims)"
                    : "NPC LINE (authoritative only that this NPC said it; autobiography, alibis, eyewitness claims, actions, and world facts remain unverified reported speech)";
                string renderedTurn = identityKnowledge.PlayerIdentityUnknown
                    ? RenderExactHistoryTurn(turn, identityKnowledge)
                    : ReadString(turn, "speaker_name", role) + ": " + ReadString(turn, "text", "");
                return new Dictionary<string, object>
                {
                    ["summary"] = provenance + " — " + renderedTurn,
                    ["memory_type"] = "interpersonal_experience",
                    ["importance"] = 0.6d, ["ts"] = ReadLong(turn, "ts", 0)
                };
            }).ToList();
            List<Dictionary<string, object>> verifiedLieEvidence = LoadConversationVerifiedLieEvidence(campaignId, turns);
            pseudoRows.AddRange(verifiedLieEvidence.Select(item => new Dictionary<string, object>
            {
                ["summary"] = "INDEPENDENT SYSTEM EVIDENCE (authoritative for the adjudicated component) — "
                    + SanitizeUnknownIdentityEvidenceText(
                        ReadString(item, "evidenceSummary", "A conversation claim was independently adjudicated."),
                        identityKnowledge),
                ["memory_type"] = "verified_system_evidence",
                ["importance"] = 1d,
                ["ts"] = ReadLong(item, "created_ts", now)
            }));
            Dictionary<string, object> summarySettings = settingsOverride ?? LoadSettings();
            string summaryCorrelationId = ReadFirstString(
                identitySource,
                "correlationId",
                "correlation_id",
                "liveTestCorrelationId");
            Dictionary<string, object> compact = BuildMiddleTermSummary(summarySettings, npcId, "scene:" + sessionId, pseudoRows,
                120000,
                "This is the canonical transcript of one closed conversation session. Consider all turns through the final listed turn. "
                + "Never claim a participant had not answered when a later listed turn contains that participant's response. "
                + "Player-authored lines are authoritative for what the player originally disclosed; NPC recollections are evidence of what those NPCs said, not proof that added reconstruction details are true. "
                + "A session participant list proves only who heard or spoke in this conversation, not who witnessed an older event described inside it. If one NPC denies witnessing an older event while another speaker says 'we', 'everyone', or 'all three', preserve that contradiction explicitly and never silently promote the collective wording into firsthand evidence. "
                + "An INDEPENDENT SYSTEM EVIDENCE record is authoritative for the component it adjudicates. A first-hand caught-lie receipt makes that contradicted component false rather than unresolved; preserve its lie-check lineage and do not flatten it back into two equally unsupported claims. "
                + "Phrase unverified world claims as reports or beliefs, not objective campaign facts, and do not call a recollection wholly correct when it adds unsupported detail. "
                + "Treat any claim that the current speaker is absent, permanently withdrawn, or will never re-engage as performative refusal limited to this historical scene; do not turn it into continuing physical state. "
                + (identityKnowledge.PlayerIdentityUnknown
                    ? "The player remains unidentified to this memory owner. Use only 'the unidentified interlocutor' or 'the stranger'; do not restore a canonical player name, clan, realm, title, tier, fiefs, wealth, inventory, or political status from speaker metadata or an NPC reconstruction. "
                    : "")
                + "Narrated gifts, payments, item handoffs, orders, or other gameplay actions prove only what was proposed or said unless a supplied production action receipt confirms completion; never convert narrated acceptance into a completed native transfer.",
                summaryCorrelationId);
            string summaryText = ReadString(compact, "summary", "");
            if (string.IsNullOrWhiteSpace(summaryText))
                summaryText = string.Join("\n---\n", pseudoRows.Select(row => ReadString(row, "summary", "")));
            summaryText = EnsureConversationSummaryProvenance(summaryText,
                Math.Max(500, Math.Min(5000, ReadInt(summarySettings, "memoryConsolidationMaxSummaryChars", 1800))));
            summaryText = SanitizeUnknownIdentityEvidenceText(summaryText, identityKnowledge);
            string originalSummaryText = summaryText;
            summaryText = NormalizeHistoricalRoleplayContinuityText(summaryText,
                Math.Max(summaryText.Length + 4096, 5000),
                false);
            bool continuityNormalized = !string.Equals(
                originalSummaryText, summaryText, StringComparison.Ordinal);
            string routingText = RemoveNegatedSceneCommitmentTerms(summaryText);
            Dictionary<string, object> sceneRoute = BuildMemoryRetrievalRoute(summarySettings, routingText,
                new Dictionary<string, object> { ["worldDay"] = ReadDouble(session, "end_world_day", ReadDouble(session, "start_world_day", 0d)) });
            string memoryLane = SelectConversationSceneMemoryLane(sceneRoute);
            string summaryId = "scene_" + PromptHash(sessionId + "|" + MemorySourceHash(turns) + "|" + MemoryPrecisionVersion).Substring(0, 32);
            List<string> participants = TextListFromJson(ReadString(session, "participants_json", "[]"));
            List<string> sourceEventIds = turns
                .Select(turn => ReadString(turn, "event_id", ""))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> knownBy = participants.Where(id => turns.All(turn => ReadString(turn,"speaker_id","") == id
                || TextListFromJson(ReadString(turn,"participants_json","[]")).Contains(id,StringComparer.OrdinalIgnoreCase))).ToList();
            participants = knownBy.ToList();
            string summaryOwner = knownBy.Contains(npcId,StringComparer.OrdinalIgnoreCase) ? npcId : "";
            List<string> tags = MergeStringLists(ReadStringList(compact, "tags"),
                new[] { "scene", "conversation", "reported_speech", "claim_attribution", memoryLane });
            if (verifiedLieEvidence.Count > 0)
            {
                tags = MergeStringLists(tags, new[] { "verified_lie", "independent_evidence" });
            }
            if (!retrievalEligible)
            {
                tags = MergeStringLists(tags, new[] { "recall_reconstruction", "audit_only", "non_source" });
            }
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            using (var publication = connection.BeginTransaction())
            {
                VerifyMemoryPublicationFence(connection);
                VerifyMemorySourceRows(connection, "conversation_turns", "turn_id", turns);
                ExecuteSql(connection, @"INSERT INTO summaries(
summary_id,scope,owner_id,summary_type,summary,source_events_json,start_ts,end_ts,event_count,location_id,
participants_json,about_entities_json,known_by_json,hidden_from_json,visibility,importance,confidence,tags_json,
status,source,vector_id,embedding_status,updated_ts,payload_json,memory_lane)
VALUES($id,$scope,$owner,'scene',$summary,$events,$start,$end,$count,$location,$participants,'[]',$known,'[]','private',
$importance,$confidence,$tags,$status,$source,'',$embedding,$updated,$payload,$lane)
ON CONFLICT(summary_id) DO UPDATE SET summary=$summary,payload_json=$payload,updated_ts=$updated;",
                    new Dictionary<string, object>
                    {
                        ["id"] = summaryId, ["scope"] = "scene:" + sessionId, ["owner"] = summaryOwner, ["summary"] = summaryText,
                        ["events"] = Json.Serialize(sourceEventIds),
                        ["start"] = turns.Min(turn => ReadLong(turn, "ts", 0)), ["end"] = turns.Max(turn => ReadLong(turn, "ts", 0)),
                        ["count"] = turns.Select(turn => ReadString(turn, "exchange_id", "")).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                        ["location"] = ReadString(session, "location_id", ""), ["participants"] = Json.Serialize(participants),
                        ["known"] = Json.Serialize(knownBy), ["importance"] = 0.65d,
                        ["confidence"] = ReadDouble(compact, "confidence", 0.8d), ["tags"] = Json.Serialize(tags), ["updated"] = now,
                        ["payload"] = Json.Serialize(new Dictionary<string, object>
                        {
                            ["timelineId"] = ReadString(TryParseJsonObject(ReadString(session,"payload_json","{}")),"timelineId",""),
                            ["worldDay"] = turns.Max(t => ReadDouble(t,"world_day",0d)),
                            ["sessionId"] = sessionId, ["method"] = ReadString(compact, "method", "deterministic"),
                            ["coverageComplete"] = ReadBool(compact, "coverageComplete", false),
                            ["compactionStatus"] = ReadString(compact, "compactionStatus", "incomplete"),
                            ["sourceHash"] = MemorySourceHash(turns), ["processingVersion"] = MemoryPrecisionVersion,
                            ["retrievalRoute"] = sceneRoute, ["retrievalEligible"] = retrievalEligible,
                            ["recallReconstruction"] = !retrievalEligible,
                            ["evidenceClass"] = "dialogue_reported_speech",
                            ["speakerClaimsRequireIndependentCorroboration"] = true,
                            ["correlationId"] = summaryCorrelationId,
                            ["roleplayContinuityNormalized"] = continuityNormalized,
                            ["playerIdentityKnown"] = !identityKnowledge.PlayerIdentityUnknown,
                            ["playerIdentityState"] = identityKnowledge.PlayerIdentityUnknown ? "unknown" : "known",
                            ["verifiedLieCheckIds"] = verifiedLieEvidence.Select(item => ReadString(item, "lie_check_id", "")).Where(id => !string.IsNullOrWhiteSpace(id)).ToList(),
                            ["verifiedRelationshipReceiptIds"] = verifiedLieEvidence.Select(item => ReadString(item, "receipt_id", "")).Where(id => !string.IsNullOrWhiteSpace(id)).ToList()
                        }),
                        ["source"] = !retrievalEligible ? "conversation_recall_audit" : "conversation_scene",
                        ["status"] = retrievalEligible ? "active" : "audit_only",
                        ["embedding"] = retrievalEligible ? "not_indexed" : "skipped",
                        ["lane"] = memoryLane
                    });
                if (retrievalEligible)
                {
                    InsertSummaryFts(connection, summaryId, summaryText, tags, participants);
                }
                int ordinal = 0;
                foreach (Dictionary<string, object> turn in turns)
                {
                    LinkMemorySource(connection, "summary", summaryId, "turn", ReadString(turn, "turn_id", ""), ordinal++);
                }
                int eventOrdinal = 0;
                foreach (string sourceEventId in sourceEventIds)
                {
                    LinkMemorySource(connection, "summary", summaryId, "event", sourceEventId, eventOrdinal++);
                }
                int evidenceOrdinal = 0;
                foreach (Dictionary<string, object> item in verifiedLieEvidence)
                {
                    LinkMemorySource(connection, "summary", summaryId, "lie_check", ReadString(item, "lie_check_id", ""), evidenceOrdinal);
                    LinkMemorySource(connection, "summary", summaryId, "relationship_receipt", ReadString(item, "receipt_id", ""), evidenceOrdinal++);
                }
                LinkMemorySource(connection, "summary", summaryId, "session", sessionId, 0);
                ExecuteSql(connection, "UPDATE conversation_sessions SET scene_summary_id=$summary WHERE session_id=$id;",
                    new Dictionary<string, object> { ["summary"] = summaryId, ["id"] = sessionId });
                publication.Commit();
            }
            return new Dictionary<string, object> { ["ok"] = true, ["summaryId"] = summaryId, ["summary"] = summaryText, ["memoryLane"] = memoryLane,
                ["compactionStatus"] = ReadString(compact, "compactionStatus", "incomplete"),
                ["sourceTurnIds"] = turns.Select(turn => ReadString(turn, "turn_id", "")).ToList(), ["retrievalEligible"] = retrievalEligible,
                ["sourceEventIds"] = sourceEventIds,
                ["knowledgeBoundary"] = new Dictionary<string, object>
                {
                    ["playerIdentityKnown"] = !identityKnowledge.PlayerIdentityUnknown,
                    ["playerIdentityState"] = identityKnowledge.PlayerIdentityUnknown ? "unknown" : "known",
                    ["playerId"] = playerId,
                    ["playerName"] = identityKnowledge.PlayerName,
                    ["playerClanId"] = identityKnowledge.PlayerClanId,
                    ["playerClanName"] = identityKnowledge.PlayerClanName,
                    ["playerKingdomId"] = identityKnowledge.PlayerKingdomId,
                    ["playerKingdomName"] = identityKnowledge.PlayerKingdomName
                },
                ["status"] = retrievalEligible ? "active" : "audit_only" };
        }

        private static List<Dictionary<string, object>> LoadConversationVerifiedLieEvidence(
            string campaignId, List<Dictionary<string, object>> turns)
        {
            List<string> exchangeIds = (turns ?? new List<Dictionary<string, object>>())
                .Select(turn => ReadString(turn, "exchange_id", ""))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<Dictionary<string, object>> evidence = new List<Dictionary<string, object>>();
            if (exchangeIds.Count == 0) return evidence;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureConversationRelationshipSchema(connection);
                foreach (string exchangeId in exchangeIds)
                {
                    List<Dictionary<string, object>> rows = QuerySql(connection, @"SELECT r.receipt_id,r.exchange_id,r.observer_id,r.target_id,r.lie_check_id,r.created_ts,
l.raw_claim,l.objective_verdict,l.knowledge_basis,l.outcome,l.evidence_event_ids_json,l.prompt_packet_json
FROM conversation_relationship_receipts r
JOIN world_history_lie_checks l ON l.lie_check_id=r.lie_check_id
WHERE r.exchange_id=$exchange AND r.status='applied' AND r.lie_check_id<>''
ORDER BY r.created_ts ASC;", new Dictionary<string, object> { ["exchange"] = exchangeId });
                    foreach (Dictionary<string, object> row in rows)
                    {
                        Dictionary<string, object> packet = TryParseJsonObject(ReadString(row, "prompt_packet_json", "{}"))
                            ?? new Dictionary<string, object>();
                        string explanation = ReadString(packet, "factualExplanation", "");
                        row["evidenceSummary"] = "Lie check " + ReadString(row, "lie_check_id", "")
                            + " found the claim by " + ReadString(row, "target_id", "the claimant")
                            + " — “" + LimitText(ReadString(row, "raw_claim", ""), 500) + "” — "
                            + ReadString(row, "objective_verdict", "contradicted") + " and "
                            + ReadString(row, "outcome", "detected") + " for observer " + ReadString(row, "observer_id", "")
                            + (string.IsNullOrWhiteSpace(explanation) ? "." : ". " + explanation)
                            + " Treat the contradicted component as false, not as an unresolved disagreement.";
                        evidence.Add(row);
                    }
                }
            }
            return evidence;
        }

        private static string RemoveNegatedSceneCommitmentTerms(string text)
        {
            string value = text ?? "";
            value = Regex.Replace(value,
                @"\bno\s+(?:(?:plans?|orders?|commitments?|promises?|agreements?)(?:\s*(?:,|or|and)\s*)?)+(?:\s+(?:were\s+)?made)?\b",
                "", RegexOptions.IgnoreCase);
            value = Regex.Replace(value,
                @"\b(?:do|did)\s+not\s+(?:make\s+plans?|issue\s+orders?|make\s+commitments?|promise|agree)(?:\s+(?:or|and)\s+(?:make\s+plans?|issue\s+orders?))*\b",
                "", RegexOptions.IgnoreCase);
            value = Regex.Replace(value,
                @"\b(?:no[-\s]?strings?(?:\s+attached)?|no\s+(?:favor|favour|obligation|debt)s?\s+(?:was\s+)?expected|without\s+(?:any\s+)?(?:favor|favour|obligation|debt)s?|free\s+of\s+(?:any\s+)?(?:favor|favour|obligation|debt)s?)\b",
                "", RegexOptions.IgnoreCase);
            value = Regex.Replace(value,
                @"\b(?:no\s+one|nobody)\s+(?:named|made|stated|requested|demanded|accepted|created)\s+(?:a\s+|any\s+)?(?:specific\s+)?(?:favor|favour|obligation|debt|promise|commitment)s?(?:\s+(?:or|and)\s+(?:a\s+|any\s+)?(?:specific\s+)?(?:favor|favour|obligation|debt|promise|commitment)s?)*\b",
                "", RegexOptions.IgnoreCase);
            return value;
        }

        private static string SelectConversationSceneMemoryLane(Dictionary<string, object> sceneRoute)
        {
            string lane = ReadString(sceneRoute, "primaryLane", "interpersonal_history");
            if (lane == "exact_history") return "interpersonal_history";
            if (lane == "personal_state")
            {
                Dictionary<string, object> scores = ReadDictionary(sceneRoute, "scores") ?? new Dictionary<string, object>();
                double personal = ReadDouble(scores, "personal_state", 0d);
                double interpersonal = ReadDouble(scores, "interpersonal_history", 0d);
                // The retrieval router's no-cue fallback is personal=1/interpersonal=.75.
                // A closed conversation with no explicit personal-state cue belongs to
                // interpersonal history; otherwise ordinary discussion scenes fragment
                // into the private mood lane merely because no stronger keyword fired.
                if (personal <= 1d && interpersonal <= 0.75d) return "interpersonal_history";
            }
            return MemoryLaneIds.Contains(lane, StringComparer.OrdinalIgnoreCase) ? lane : "interpersonal_history";
        }

        private static Dictionary<string, object> UpdateRollingConversationArc(string campaignId, string npcId, string playerId,
            string memoryLane, long now, Dictionary<string, object> knowledgeBoundary = null)
        {
            memoryLane = MemoryLaneIds.Contains(memoryLane, StringComparer.OrdinalIgnoreCase) && memoryLane != "exact_history"
                ? memoryLane : "interpersonal_history";
            List<Dictionary<string, object>> scenes;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                scenes = QuerySql(connection, @"SELECT * FROM summaries WHERE owner_id=$npc AND summary_type='scene' AND status='active'
AND memory_lane=$lane AND ($player='' OR participants_json LIKE $player_like) ORDER BY updated_ts DESC LIMIT 12;",
                    new Dictionary<string, object> { ["npc"] = npcId, ["lane"] = memoryLane, ["player"] = playerId ?? "", ["player_like"] = "%" + (playerId ?? "") + "%" });
            }
            bool identityUnknown = knowledgeBoundary != null
                && !ReadBool(knowledgeBoundary, "playerIdentityKnown", true);
            KnowledgeAccessContext identityKnowledge = null;
            int withheldScenes = 0;
            if (identityUnknown)
            {
                Dictionary<string, object> source =
                    new Dictionary<string, object>(knowledgeBoundary, StringComparer.OrdinalIgnoreCase)
                    {
                        ["mainHeroStringId"] = FirstNonEmpty(
                            ReadString(knowledgeBoundary, "playerId", ""), playerId),
                        ["mainHeroName"] = ReadString(knowledgeBoundary, "playerName", ""),
                        ["identityView"] = new Dictionary<string, object> { ["knowsIdentity"] = false }
                    };
                identityKnowledge = BuildKnowledgeAccessContext(npcId, playerId, "", source);
                int before = scenes.Count;
                scenes = scenes
                    .Where(row => UnknownIdentityMemoryEvidenceAllowed(row, identityKnowledge,
                        ReadString(row, "summary", "")))
                    .Select(row => SanitizeUnknownIdentityMemoryRow(row, identityKnowledge))
                    .ToList();
                withheldScenes = before - scenes.Count;
            }
            if (scenes.Count < 4)
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["updated"] = false,
                    ["reason"] = "fewer_than_four_related_scenes",
                    ["identityBoundaryWithheldScenes"] = withheldScenes
                };
            }
            scenes = scenes.Take(8).OrderBy(row => ReadLong(row, "updated_ts", 0)).ToList();
            Dictionary<string, object> arcSettings = LoadSettings();
            string arcCorrelationId = scenes
                .Select(StoredConversationArtifactCorrelationId)
                .LastOrDefault(value =>
                    !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
            Dictionary<string, object> compact = BuildMiddleTermSummary(arcSettings, npcId,
                "arc:" + npcId + ":" + playerId + ":" + memoryLane, scenes, 60000,
                "These records are chronological closed conversation scenes. A departure, silence, refusal, or closure in an older scene applies only to that scene. If a later scene exists, it proves the participants subsequently re-engaged; never preserve an older scene ending as current physical absence or permanent non-engagement.",
                arcCorrelationId);
            string text = ReadString(compact, "summary", "");
            if (identityUnknown)
            {
                text = SanitizeUnknownIdentityEvidenceText(text, identityKnowledge);
            }
            string originalArcText = text;
            text = NormalizeHistoricalRoleplayContinuityText(text,
                Math.Max(text.Length + 4096, 5000),
                true);
            bool continuityNormalized = !string.Equals(
                originalArcText, text, StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new Dictionary<string, object> { ["ok"] = true, ["updated"] = false, ["reason"] = "empty_summary" };
            }
            string arcId = "arc_" + SafeMemoryKey(npcId) + "_" + SafeMemoryKey(playerId) + "_" + SafeMemoryKey(memoryLane);
            List<string> tags = MergeStringLists(ReadStringList(compact, "tags"), new[] { "rolling_arc", memoryLane });
            List<string> participants = MergeStringLists(new[] { npcId, playerId }, new List<string>());
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            using (var publication = connection.BeginTransaction())
            {
                VerifyMemoryPublicationFence(connection);
                VerifyMemorySourceRows(connection, "summaries", "summary_id", scenes);
                ExecuteSql(connection, @"INSERT OR REPLACE INTO summaries(
summary_id,scope,owner_id,summary_type,summary,source_events_json,start_ts,end_ts,event_count,location_id,
participants_json,about_entities_json,known_by_json,hidden_from_json,visibility,importance,confidence,tags_json,
status,source,vector_id,embedding_status,updated_ts,payload_json,memory_lane)
VALUES($id,$scope,$owner,'arc',$summary,'[]',$start,$end,$count,'',$participants,'[]',$known,'[]','private',
0.75,$confidence,$tags,'active','conversation_arc','','not_indexed',$updated,$payload,'interpersonal_history');",
                    new Dictionary<string, object>
                    {
                        ["id"] = arcId, ["scope"] = "arc:" + npcId + ":" + playerId + ":" + memoryLane, ["owner"] = npcId, ["summary"] = text,
                        ["start"] = scenes.Min(row => ReadLong(row, "start_ts", 0)), ["end"] = scenes.Max(row => ReadLong(row, "end_ts", 0)),
                        ["count"] = scenes.Count, ["participants"] = Json.Serialize(participants), ["known"] = Json.Serialize(new List<string> { npcId }),
                        ["confidence"] = ReadDouble(compact, "confidence", 0.8d), ["tags"] = Json.Serialize(tags), ["updated"] = now,
                        ["payload"] = Json.Serialize(new Dictionary<string, object>
                        {
                            ["sourceSummaryIds"] = scenes.Select(row => ReadString(row, "summary_id", "")).ToList(),
                            ["coverageComplete"] = ReadBool(compact, "coverageComplete", false),
                            ["compactionStatus"] = ReadString(compact, "compactionStatus", "incomplete"),
                            ["sourceHash"] = MemorySourceHash(scenes), ["processingVersion"] = MemoryPrecisionVersion,
                            ["correlationId"] = arcCorrelationId,
                            ["roleplayContinuityNormalized"] = continuityNormalized,
                            ["latestSceneProvesReengagement"] = true
                        })
                    });
                InsertSummaryFts(connection, arcId, text, tags, participants);
                ExecuteSql(connection, "UPDATE summaries SET memory_lane=$lane WHERE summary_id=$id;",
                    new Dictionary<string, object> { ["lane"] = memoryLane, ["id"] = arcId });
                ExecuteSql(connection, "DELETE FROM memory_sources WHERE document_type='summary' AND document_id=$id;", new Dictionary<string, object> { ["id"] = arcId });
                int ordinal = 0;
                foreach (Dictionary<string, object> scene in scenes)
                {
                    LinkMemorySource(connection, "summary", arcId, "summary", ReadString(scene, "summary_id", ""), ordinal++);
                }
                publication.Commit();
            }
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["updated"] = true,
                ["summaryId"] = arcId,
                ["summary"] = text,
                ["roleplayContinuityNormalized"] = continuityNormalized,
                ["identityBoundaryWithheldScenes"] = withheldScenes
            };
        }

        private static Dictionary<string, object> RepairExistingRollingConversationArcsAfterReengagement(
            string campaignId,
            string npcId,
            string playerId,
            long now)
        {
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(npcId))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["repairedCount"] = 0
                };
            }
            int maxChars = Math.Max(500, Math.Min(5000,
                ReadInt(LoadSettings(), "memoryConsolidationMaxSummaryChars", 1800)));
            List<string> repairedIds = new List<string>();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                List<Dictionary<string, object>> arcs = QuerySql(connection, @"SELECT * FROM summaries
WHERE owner_id=$npc AND summary_type='arc' AND status='active'
AND ($player='' OR participants_json LIKE $player_like);",
                    new Dictionary<string, object>
                    {
                        ["npc"] = npcId,
                        ["player"] = playerId ?? "",
                        ["player_like"] = "%\"" + (playerId ?? "") + "\"%"
                    });
                foreach (Dictionary<string, object> row in arcs)
                {
                    string original = ReadString(row, "summary", "");
                    string normalized = NormalizeHistoricalRoleplayContinuityText(
                        original, maxChars, true);
                    if (string.Equals(original, normalized, StringComparison.Ordinal)) continue;
                    string summaryId = ReadString(row, "summary_id", "");
                    if (string.IsNullOrWhiteSpace(summaryId)) continue;
                    Dictionary<string, object> payload =
                        TryParseJsonObject(ReadString(row, "payload_json", ""))
                        ?? new Dictionary<string, object>();
                    payload["roleplayContinuityNormalized"] = true;
                    payload["latestSceneProvesReengagement"] = true;
                    payload["continuityNormalizedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    ExecuteSql(connection, @"UPDATE summaries
SET summary=$summary,payload_json=$payload,updated_ts=$updated,embedding_status='not_indexed',vector_id=''
WHERE summary_id=$id;", new Dictionary<string, object>
                    {
                        ["summary"] = normalized,
                        ["payload"] = Json.Serialize(payload),
                        ["updated"] = now,
                        ["id"] = summaryId
                    });
                    InsertSummaryFts(connection, summaryId, normalized,
                        TextListFromJson(ReadString(row, "tags_json", "[]")),
                        TextListFromJson(ReadString(row, "participants_json", "[]")));
                    repairedIds.Add(summaryId);
                }
            }
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["repairedCount"] = repairedIds.Count,
                ["summaryIds"] = repairedIds
            };
        }

        private static void LinkMemorySource(ReignDbConnection connection, string documentType, string documentId, string sourceType, string sourceId, int ordinal)
        {
            if (string.IsNullOrWhiteSpace(documentId) || string.IsNullOrWhiteSpace(sourceId))
            {
                return;
            }
            ExecuteSql(connection, @"INSERT OR REPLACE INTO memory_sources(document_type,document_id,source_type,source_id,ordinal,payload_json)
VALUES($document_type,$document_id,$source_type,$source_id,$ordinal,'{}');", new Dictionary<string, object>
            {
                ["document_type"] = documentType ?? "", ["document_id"] = documentId,
                ["source_type"] = sourceType ?? "", ["source_id"] = sourceId, ["ordinal"] = ordinal
            });
        }

        private static void UpsertKnowledgeReceipt(ReignDbConnection connection, string eventId, string npcId, string acquisitionType,
            double confidence, double reliability, double worldDay, string sourceId, long ts)
        {
            if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(npcId))
            {
                return;
            }
            string receiptId = "knowledge_" + Guid.NewGuid().ToString("N");
            ExecuteSql(connection, @"INSERT OR IGNORE INTO knowledge_receipts(
receipt_id,event_id,npc_id,acquisition_type,confidence,reliability,acquired_day,source_id,status,payload_json,created_ts)
VALUES($id,$event,$npc,$type,$confidence,$reliability,$day,$source,'active','{}',$ts);", new Dictionary<string, object>
            {
                ["id"] = receiptId, ["event"] = eventId, ["npc"] = npcId, ["type"] = acquisitionType ?? "known",
                ["confidence"] = ClampDouble(confidence, 0d, 1d), ["reliability"] = ClampDouble(reliability, 0d, 1d),
                ["day"] = worldDay, ["source"] = sourceId ?? "", ["ts"] = ts
            });
        }

        private static string ClassifyEventCategory(string eventType, Dictionary<string, object> payload, string summary)
        {
            string value = NormalizeMemoryCategory(eventType + " " + summary);
            if (ContainsAny(value, "conversation", "dialogue_turn", "private_conversation", "party_chat")) return "conversation";
            if (ContainsAny(value, "letter", "mail", "correspondence")) return "correspondence";
            if (ContainsAny(value, "action_result", "action_completed", "action_failed")) return "action_result";
            if (ContainsAny(value, "nearby", "visible_army", "scout", "local_observation")) return "local_observation";
            if (ContainsAny(value, "social_event", "tournament", "feast", "celebration", "court_event")) return "social_event";
            return "world_event";
        }

        private static string ClassifyEventSubtype(string eventType, string summary)
        {
            string value = NormalizeMemoryCategory(eventType + " " + summary);
            if (value.Contains("tournament") && ContainsAny(value, "winner", "won", "result", "victory")) return "tournament_result";
            if (value.Contains("tournament")) return "tournament_announcement";
            if (ContainsAny(value, "war_declared", "declared_war", "war declaration")) return "war_declared";
            if (ContainsAny(value, "siege_result", "siege ended", "siege won")) return "siege_result";
            if (value.Contains("marriage")) return "marriage";
            if (ContainsAny(value, "nearby_army", "army nearby", "enemy army")) return "nearby_army";
            return NormalizeMemoryCategory(eventType);
        }

        private static string ClassifyMemoryDomain(Dictionary<string, object> row)
        {
            string explicitDomain = ReadFirstString(row, "memory_domain", "memoryDomain", "lane");
            if (!string.IsNullOrWhiteSpace(explicitDomain)) return NormalizeMemoryCategory(explicitDomain);
            string type = NormalizeMemoryCategory(ReadFirstString(row, "memory_type", "memoryType", "type"));
            string blob = NormalizeMemoryCategory(type + " " + ReadString(row, "source", "") + " " + ReadString(row, "tags_json", "") + " " + ReadString(row, "summary", ""));
            if (ContainsAny(blob, "world_knowledge", "war", "tournament", "siege", "alliance", "marriage", "kingdom")) return "world_affairs";
            if (ContainsAny(blob, "local_observation", "nearby", "scout", "visible_army")) return "local_awareness";
            if (ContainsAny(blob, "private_reflection", "comprehension", "feeling", "mood", "fear", "dream", "desire")) return "personal_state";
            if (ContainsAny(blob, "interpersonal", "conversation", "dialogue", "letter", "correspondence")) return "interpersonal_history";
            if (ContainsAny(blob, "obligation", "promise", "debt", "oath", "threat", "blackmail", "spymaster", "intelligence_mission", "covert_operation")) return "commitments_and_plots";
            if (ContainsAny(blob, "belief", "rumor", "gossip")) return "beliefs_and_rumors";
            return "personal_state";
        }

        private static string NormalizeMemoryCategory(string value)
        {
            return Regex.Replace((value ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
        }

        private static string SafeMemoryKey(string value)
        {
            string key = Regex.Replace((value ?? "unknown").Trim(), @"[^A-Za-z0-9_-]+", "_");
            return string.IsNullOrWhiteSpace(key) ? "unknown" : key;
        }

        private static void InsertCategorizedMemoryTestRow(ReignDbConnection connection, string memoryId, string eventId,
            string ownerId, string summary, long ts, double importance, double confidence, string domain,
            List<string> participants, List<string> witnesses, List<string> heardAsRumorBy)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["eventType"] = "dialogue_turn",
                ["memoryGroupKey"] = "test:" + eventId
            };
            InsertMemoryRow(connection, memoryId, eventId, ownerId, "episodic", ts, 10d, "", summary,
                participants, witnesses, Json.Serialize(participants), new List<string> { ownerId }, heardAsRumorBy,
                new List<string>(), "private", importance, 0.5d, confidence,
                MergeStringLists(SimpleTags(summary), new[] { "conversation" }), "active", "categorized_memory_test",
                Json.Serialize(payload), "", "not_indexed");
            ExecuteSql(connection, "UPDATE memories SET memory_domain=$domain WHERE memory_id=$id;",
                new Dictionary<string, object> { ["domain"] = domain, ["id"] = memoryId });
        }

        private static List<Dictionary<string, object>> RunCategorizedMemorySubsystemSelfTests()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, pass, summary) => rows.Add(new Dictionary<string, object>
            {
                ["id"] = "categorized_memory_" + id, ["suite"] = "categorized_memory", ["passed"] = pass, ["summary"] = summary
            });
            add("event_categories",
                ClassifyEventSubtype("social_event_announced", "Tournament Celebration") == "tournament_announcement"
                && ClassifyEventSubtype("siege_result", "The siege ended") == "siege_result"
                && ClassifyEventCategory("dialogue_turn", new Dictionary<string, object>(), "") == "conversation"
                && ClassifyEventCategory("party_chat_turn", new Dictionary<string, object>(), "") == "conversation",
                "Events receive stable categories and subtypes.");
            string negatedCommitmentScene = RemoveNegatedSceneCommitmentTerms(
                "They discussed a cracked jug. No plans or orders were made. They did not make commitments.");
            Dictionary<string, object> negatedCommitmentRoute = BuildMemoryRetrievalRoute(
                new Dictionary<string, object> { ["enableMinimeMemoryWorker"] = false }, negatedCommitmentScene,
                new Dictionary<string, object>());
            add("negated_commitment_scene_lane",
                !RouteHasLane(negatedCommitmentRoute, "commitments_and_plots"),
                "Explicit statements that no plan, order, promise, or commitment occurred cannot misclassify a casual scene as a commitment.");
            string unconditionalGiftScene = RemoveNegatedSceneCommitmentTerms(
                "Rhovarion gave Menor a silver cup as a sincere, no-strings gift with no favor expected. No one named a specific favor or obligation.");
            Dictionary<string, object> unconditionalGiftRoute = BuildMemoryRetrievalRoute(
                new Dictionary<string, object> { ["enableMinimeMemoryWorker"] = false }, unconditionalGiftScene,
                new Dictionary<string, object>());
            add("unconditional_gift_not_commitment_lane",
                !string.Equals(ReadString(unconditionalGiftRoute, "primaryLane", ""), "commitments_and_plots", StringComparison.OrdinalIgnoreCase),
                "An explicitly no-strings gift cannot create a phantom commitment merely because its summary states that no obligation exists.");

            Dictionary<string, object> disabledSettings = new Dictionary<string, object>
            {
                ["enableMinimeMemoryWorker"] = false, ["enableMinimeMemoryReranking"] = false
            };
            Dictionary<string, object> personalRoute = BuildMemoryRetrievalRoute(disabledSettings, "How have you been feeling today?", new Dictionary<string, object>());
            Dictionary<string, object> worldRoute = BuildMemoryRetrievalRoute(disabledSettings, "Who won the tournament?", new Dictionary<string, object>());
            Dictionary<string, object> namedWorldRoute = BuildMemoryRetrievalRoute(disabledSettings, "What happened to Rhotae?", new Dictionary<string, object>());
            Dictionary<string, object> resolvedEntityRoute = BuildMemoryRetrievalRoute(disabledSettings, "What became of it?",
                new Dictionary<string, object> { ["aboutEntityIds"] = new List<string> { "settlement_rhotae" } });
            Dictionary<string, object> firstSceneRecallRoute = BuildMemoryRetrievalRoute(disabledSettings,
                "Please repeat the unusual trail-name from our first scene.", new Dictionary<string, object>());
            Dictionary<string, object> firstConversationRecallRoute = BuildMemoryRetrievalRoute(disabledSettings,
                "For a simple memory check, what trail-name did I share in our first conversation?", new Dictionary<string, object>());
            add("lane_routing",
                ReadString(personalRoute, "primaryLane", "") == "personal_state" && !RouteHasLane(personalRoute, "world_affairs")
                && RouteHasLane(worldRoute, "world_affairs") && RouteHasLane(namedWorldRoute, "world_affairs")
                && RouteHasLane(resolvedEntityRoute, "world_affairs")
                && RouteHasLane(firstSceneRecallRoute, "exact_history")
                && ReadBool(firstSceneRecallRoute, "needsExactTranscript", false)
                && string.Equals(ReadString(firstConversationRecallRoute, "primaryLane", ""), "exact_history", StringComparison.OrdinalIgnoreCase)
                && ReadBool(firstConversationRecallRoute, "needsExactTranscript", false),
                "Personal questions suppress world affairs, world questions open that lane, and first-scene repeat requests activate exact transcript retrieval.");
            add("relative_day", Math.Abs(ExtractTargetWorldDay("what did you say three days ago", 100d) - 97d) < 0.001d,
                "Relative-day exact recall resolves against the current campaign day.");
            Dictionary<string, object> oversizedTurnPayload = new Dictionary<string, object>
            {
                ["campaignId"] = "campaign_a",
                ["correlationId"] = "correlation_a",
                ["playerHeroStringId"] = "player",
                ["playerName"] = "Caribos",
                ["hero"] = new Dictionary<string, object>
                {
                    ["heroStringId"] = "npc_a", ["name"] = "Mira", ["clanId"] = "clan_a",
                    ["kingdomId"] = "kingdom_a"
                },
                ["identityView"] = new Dictionary<string, object>
                {
                    ["knowsIdentity"] = false, ["identityState"] = "claimed",
                    ["unboundedEvidence"] = new string('x', 5000)
                },
                ["actionResolutionIndex"] = new Dictionary<string, object>
                {
                    ["heroes"] = Enumerable.Range(0, 100).Select(index =>
                        (object)new Dictionary<string, object>
                        {
                            ["heroStringId"] = "hero_" + index.ToString(CultureInfo.InvariantCulture),
                            ["description"] = new string('h', 2000)
                        }).ToList()
                },
                ["contextBundles"] = new List<object> { new string('c', 10000) }
            };
            Dictionary<string, object> compactTurnPayload = BuildConversationTurnStoragePayload(
                oversizedTurnPayload, Json.Serialize(oversizedTurnPayload).Length);
            string compactTurnJson = Json.Serialize(compactTurnPayload);
            add("compact_turn_payload",
                compactTurnJson.Length < 4096
                && !compactTurnPayload.ContainsKey("actionResolutionIndex")
                && !compactTurnPayload.ContainsKey("contextBundles")
                && ReadString(compactTurnPayload, "correlationId", "") == "correlation_a"
                && ReadString(compactTurnPayload, "speakerName", "") == "Mira"
                && ReadString(ReadDictionary(compactTurnPayload, "identityView"), "identityState", "") == "claimed",
                "Raw dialogue and structured columns remain authoritative while turn metadata preserves compact identity and lineage without duplicating the native action index.");
            add("stored_summary_work_inherits_turn_correlation",
                StoredConversationArtifactCorrelationId(
                    new Dictionary<string, object>
                    {
                        ["payload_json"] = compactTurnJson
                    }) == "correlation_a",
                "Scene, middle-term, and rolling-arc summary provider calls can inherit the originating live-test correlation and consume the same physical-call ledger.");

            string campaignId = "categorized_memory_test_" + Guid.NewGuid().ToString("N");
            try
            {
                Dictionary<string, object> stored = StoreWorldMemoryEvent(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["eventId"] = "tournament_test", ["eventType"] = "tournament_result",
                    ["summary"] = "Lady Mira won the tournament.", ["participants"] = new List<string> { "npc_a", "npc_b" },
                    ["known_by"] = new List<string> { "npc_a", "npc_b", "npc_c" }, ["visibility"] = "public",
                    ["worldDay"] = 12d, ["ts"] = 1200L
                }, "categorized_memory_test");
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    int canonical = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM memories WHERE event_id='tournament_test' AND memory_type='world_knowledge' AND status='active';").FirstOrDefault(), "count", 0);
                    int experiences = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM memories WHERE event_id='tournament_test' AND memory_type='personal_experience' AND status='active';").FirstOrDefault(), "count", 0);
                    int receipts = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM knowledge_receipts WHERE event_id='tournament_test' AND status='active';").FirstOrDefault(), "count", 0);
                    add("canonical_world_event", canonical == 1 && experiences == 2 && receipts == 3,
                        "One canonical public fact is stored with participant experiences and knowledge receipts.");
                }

                const string privatePromiseMarker = "SABLE_MEMORY_SELFTEST_4817";
                StoreWorldMemoryEvent(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["eventId"] = "private_promise_test", ["eventType"] = "private_promise",
                    ["summary"] = "The player promised npc_a to guard the phrase " + privatePromiseMarker + ".",
                    ["participants"] = new List<string> { "npc_a", "player" }, ["known_by"] = new List<string> { "npc_a", "player" },
                    ["about_entities"] = new List<string> { "npc_a" }, ["visibility"] = "private",
                    ["worldDay"] = 13d, ["ts"] = 1300L, ["importance"] = 0.9d
                }, "categorized_memory_test");
                Dictionary<string, object> authorizedPromisePacket = BuildNpcMemoryPacket(campaignId, "npc_a", "player", "", "SABLE memory promise", 2500);
                Dictionary<string, object> unauthorizedPromisePacket = BuildNpcMemoryPacket(campaignId, "npc_c", "player", "", "SABLE memory promise", 2500);
                string authorizedPromiseText = ReadString(authorizedPromisePacket, "memoryPacket", "");
                string unauthorizedPromiseText = ReadString(unauthorizedPromisePacket, "memoryPacket", "");
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    int commitmentRows = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM memories WHERE event_id='private_promise_test' AND owner_id='npc_a' AND memory_domain='commitments_and_plots' AND status='active';").FirstOrDefault(), "count", 0);
                    add("private_promise_retrieval",
                        commitmentRows == 1 && authorizedPromiseText.Contains(privatePromiseMarker) && !unauthorizedPromiseText.Contains(privatePromiseMarker),
                        "Private promises route through commitments, remain retrievable by their owner, and do not leak to uninvolved NPCs.");
                }

                const string oldArchiveMarker = "OBSIDIAN_ARCHIVE_OATH_9173";
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    InsertCategorizedMemoryTestRow(connection, "old_archive_memory", "old_archive_event", "npc_a",
                        "The NPC witnessed the " + oldArchiveMarker + " and swore to preserve it.", 1400L, 0.65d, 0.82d,
                        "interpersonal_history", new List<string> { "npc_a", "player" }, new List<string> { "npc_a" }, new List<string>());
                    for (int index = 0; index < 90; index++)
                    {
                        InsertCategorizedMemoryTestRow(connection, "archive_decoy_" + index.ToString(CultureInfo.InvariantCulture),
                            "archive_decoy_event_" + index.ToString(CultureInfo.InvariantCulture), "npc_a",
                            "An ordinary unrelated recollection number " + index.ToString(CultureInfo.InvariantCulture) + ".",
                            3000L + index, 0.2d, 0.7d, "interpersonal_history",
                            new List<string> { "npc_a", "player" }, new List<string> { "npc_a" }, new List<string>());
                    }
                    HashSet<string> ftsIds = SearchMemoryFts(connection, MemoryQueryTerms(oldArchiveMarker), 80);
                    List<Dictionary<string, object>> materialized = LoadFtsRows(connection, "memories", "memory_id", ftsIds,
                        new KnowledgeAccessContext { NpcId = "npc_a", PlayerId = "player" });
                    add("fts_full_archive_materialization",
                        ftsIds.Contains("old_archive_memory") && materialized.Any(row => ReadString(row, "memory_id", "") == "old_archive_memory"),
                        "Full-archive FTS hits are materialized by id instead of being discarded outside the newest-memory window.");
                }
                Dictionary<string, object> oldArchivePacket = BuildNpcMemoryPacket(campaignId, "npc_a", "player", "",
                    "What exactly do you remember about " + oldArchiveMarker + "?", 2500);
                Dictionary<string, object> oldArchiveStrangerPacket = BuildNpcMemoryPacket(campaignId, "npc_c", "player", "",
                    "What exactly do you remember about " + oldArchiveMarker + "?", 2500);
                string oldArchiveText = ReadString(oldArchivePacket, "memoryPacket", "");
                bool oldArchiveRecall = oldArchiveText.Contains(oldArchiveMarker)
                    && oldArchiveText.Contains("firsthand witness")
                    && oldArchiveText.Contains("confidence 0.82")
                    && !ReadString(oldArchiveStrangerPacket, "memoryPacket", "").Contains(oldArchiveMarker);
                int selectedMemoryCount = ReadInt(ReadDictionary(oldArchivePacket, "counts"), "memories", 0);
                add("long_archive_recall", oldArchiveRecall,
                    "An exact lexical memory older than the newest 80 rows is recalled without leaking to another NPC.");
                rows.Last()["data"] = new Dictionary<string, object>
                {
                    ["expectedRelevant"] = 1,
                    ["recalledRelevant"] = oldArchiveText.Contains(oldArchiveMarker) ? 1 : 0,
                    ["recallAtPacket"] = oldArchiveText.Contains(oldArchiveMarker) ? 1d : 0d,
                    ["selectedMemoryCount"] = selectedMemoryCount,
                    ["precisionAtPacket"] = selectedMemoryCount <= 0 ? 0d : (oldArchiveText.Contains(oldArchiveMarker) ? 1d : 0d) / selectedMemoryCount
                };

                const string contradictionMarker = "SUNDERED_CROWN_REPORT_4421";
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    InsertCategorizedMemoryTestRow(connection, "contradiction_firsthand", "contradiction_event_a", "npc_a",
                        contradictionMarker + ": Duke Orman survived the ambush.", 1460L, 0.7d, 0.85d,
                        "beliefs_and_rumors", new List<string> { "npc_a", "player" }, new List<string> { "npc_a" }, new List<string>());
                    InsertCategorizedMemoryTestRow(connection, "contradiction_rumor", "contradiction_event_b", "npc_a",
                        contradictionMarker + ": court gossip claims Duke Orman died in the ambush.", 1470L, 0.6d, 0.40d,
                        "beliefs_and_rumors", new List<string> { "player" }, new List<string>(), new List<string> { "npc_a" });
                }
                Dictionary<string, object> contradictionPacket = BuildNpcMemoryPacket(campaignId, "npc_a", "player", "",
                    "What rumor or claim have you heard about " + contradictionMarker + "?", 2500);
                string contradictionText = ReadString(contradictionPacket, "memoryPacket", "");
                add("contradictory_recollections",
                    contradictionText.Contains("survived the ambush") && contradictionText.Contains("died in the ambush")
                    && contradictionText.Contains("firsthand witness") && contradictionText.Contains("secondhand rumor")
                    && contradictionText.Contains("confidence 0.85") && contradictionText.Contains("confidence 0.40"),
                    "Conflicting recollections remain distinct and carry enough provenance for the dialogue model to preserve uncertainty.");

                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    InsertCategorizedMemoryTestRow(connection, "consolidated_semantic_memory", "consolidated_semantic_event", "npc_a",
                        "This source memory must disappear after consolidation.", 1450L, 0.7d, 0.8d,
                        "interpersonal_history", new List<string> { "npc_a", "player" }, new List<string> { "npc_a" }, new List<string>());
                    ExecuteSql(connection, "UPDATE memories SET status='consolidated' WHERE memory_id='consolidated_semantic_memory';");
                    Dictionary<string, object> consolidated = QuerySql(connection,
                        "SELECT * FROM memories WHERE memory_id='consolidated_semantic_memory' LIMIT 1;").FirstOrDefault();
                    Dictionary<string, object> fakeSemanticSearch = new Dictionary<string, object>
                    {
                        ["results"] = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["score"] = 0.99d,
                                ["payload"] = new Dictionary<string, object>
                                {
                                    ["sourceType"] = "memory", ["sourceId"] = "consolidated_semantic_memory"
                                }
                            }
                        }
                    };
                    List<Dictionary<string, object>> semanticRows = LoadSemanticRows(connection, "memories", "memory_id", "memory",
                        fakeSemanticSearch, new KnowledgeAccessContext { NpcId = "npc_a", PlayerId = "player" });
                    add("consolidated_semantic_filter",
                        !EmbeddingRowActive("memory", consolidated) && semanticRows.Count == 0,
                        "Consolidated source memories are removed from the semantic index contract and rejected during row materialization.");
                }

                Dictionary<string, object> allocationRoute = new Dictionary<string, object>
                {
                    ["selectedLanes"] = new List<string> { "personal_state", "interpersonal_history", "beliefs_and_rumors" },
                    ["tokenAllocationPercent"] = new Dictionary<string, object>
                    {
                        ["personal_state"] = 55, ["interpersonal_history"] = 25, ["beliefs_and_rumors"] = 20
                    }
                };
                Dictionary<string, int> allocation = AllocateMemoryLaneCharacters(
                    new List<string> { "personal_state", "interpersonal_history", "beliefs_and_rumors" },
                    new Dictionary<string, string>
                    {
                        ["personal_state"] = new string('a', 1000),
                        ["interpersonal_history"] = new string('b', 1000),
                        ["beliefs_and_rumors"] = new string('c', 1000)
                    }, allocationRoute, 1000);
                add("lane_budget_enforcement",
                    allocation.Values.Sum() == 1000
                    && allocation["personal_state"] > allocation["interpersonal_history"]
                    && allocation["interpersonal_history"] > allocation["beliefs_and_rumors"],
                    "Rendered packet character budgets follow the router's primary and supporting lane allocation.");

                string partySessionId = "party_chat_fixture_" + Guid.NewGuid().ToString("N");
                ConversationStartApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["sessionId"] = partySessionId,
                    ["npcId"] = "npc_a",
                    ["playerId"] = "player",
                    ["channel"] = "party_chat",
                    ["locationId"] = "town_fixture",
                    ["participants"] = new List<string> { "npc_a", "npc_b", "player" },
                    ["worldDay"] = 19d,
                    ["ts"] = 1900L
                });
                Dictionary<string, object> partyRequest = new Dictionary<string, object>
                {
                    ["conversationSessionId"] = partySessionId,
                    ["sceneTurnId"] = "party_fixture_turn_1",
                    ["turnId"] = "party_fixture_turn_1",
                    ["worldDay"] = 19d,
                    ["locationId"] = "town_fixture",
                    ["channel"] = "party_chat",
                    ["participants"] = new List<string> { "npc_a", "npc_b", "player" },
                    ["activeHeroIds"] = new List<string> { "npc_a", "npc_b" }
                };
                StorePartyConversationExchange(campaignId, partyRequest, "npc_a", "player", "Player", "Aldric",
                    "Remember the blue ribbon and the bent pin.", "I will remember both.", "party_event_a", 1901L);
                StorePartyConversationExchange(campaignId, partyRequest, "npc_b", "player", "Player", "Beatrice",
                    "Remember the blue ribbon and the bent pin.", "The ribbon and pin are noted.", "party_event_b", 1902L);
                StorePartyConversationExchange(campaignId, partyRequest, "npc_a", "player", "Player", "Aldric",
                    "Remember the blue ribbon and the bent pin.", "I will remember both.", "party_event_a", 1901L);
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    Dictionary<string, object> partySession = QuerySql(connection,
                        "SELECT * FROM conversation_sessions WHERE session_id=$id LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = partySessionId }).FirstOrDefault();
                    List<Dictionary<string, object>> partyTurns = QuerySql(connection,
                        "SELECT * FROM conversation_turns WHERE session_id=$id ORDER BY turn_order;",
                        new Dictionary<string, object> { ["id"] = partySessionId });
                    Dictionary<string, object> partyScene = CreateConversationSceneSummary(
                        campaignId,
                        partySession,
                        partyTurns,
                        1903L,
                        new Dictionary<string, object>
                        {
                            ["enableMemoryConsolidation"] = true,
                            ["useMemoryLlmForConsolidation"] = false,
                            ["enableMinimeMemoryWorker"] = false,
                            ["enableMinimeMemoryReranking"] = false
                        });
                    string partySummaryId = ReadString(partyScene, "summaryId", "");
                    Dictionary<string, object> partySummary = QuerySql(connection,
                        "SELECT * FROM summaries WHERE summary_id=$id LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = partySummaryId }).FirstOrDefault();
                    int partySourceLinks = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) count FROM memory_sources WHERE document_type='summary' AND document_id=$id;",
                        new Dictionary<string, object> { ["id"] = partySummaryId }).FirstOrDefault(), "count", 0);
                    bool partyParticipantsComplete = partyTurns.All(turn =>
                    {
                        List<string> ids = TextListFromJson(ReadString(turn, "participants_json", "[]"));
                        return ids.Contains("npc_a", StringComparer.OrdinalIgnoreCase)
                            && ids.Contains("npc_b", StringComparer.OrdinalIgnoreCase)
                            && ids.Contains("player", StringComparer.OrdinalIgnoreCase);
                    });
                    List<string> summaryKnownBy = TextListFromJson(ReadString(partySummary, "known_by_json", "[]"));
                    List<string> summarySourceEvents = TextListFromJson(ReadString(partySummary, "source_events_json", "[]"));
                    add("party_chat_group_session",
                        ReadString(partySession, "channel", "") == "party_chat"
                        && partyTurns.Count == 3
                        && partyTurns.Count(turn => ReadString(turn, "role", "") == "player") == 1
                        && partyTurns.Count(turn => ReadString(turn, "role", "") == "npc") == 2
                        && partyTurns.Select(turn => ReadString(turn, "exchange_id", "")).Distinct().Count() == 1
                        && partyParticipantsComplete
                        && summaryKnownBy.Contains("npc_a", StringComparer.OrdinalIgnoreCase)
                        && summaryKnownBy.Contains("npc_b", StringComparer.OrdinalIgnoreCase)
                        && summarySourceEvents.Count == 2
                        && summarySourceEvents.Contains("party_event_a", StringComparer.OrdinalIgnoreCase)
                        && summarySourceEvents.Contains("party_event_b", StringComparer.OrdinalIgnoreCase)
                        && partySourceLinks == partyTurns.Count + summarySourceEvents.Count + 1,
                        "Party chat stores one player turn plus one turn per NPC, remains retry-idempotent, preserves all witnesses, and links its turns, production events, and shared session to the scene summary.");
                }
                ConversationFinishApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["sessionId"] = partySessionId, ["worldDay"] = 19d, ["ts"] = 1904L
                });

                Dictionary<string, object> started = ConversationStartApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["npcId"] = "npc_a", ["playerId"] = "player", ["worldDay"] = 20d
                });
                string sessionId = ReadString(started, "sessionId", "");
                StoreConversationExchange(campaignId, new Dictionary<string, object>
                {
                    ["conversationSessionId"] = sessionId, ["worldDay"] = 20d, ["channel"] = "in_person"
                }, "npc_a", "player", "Player", "Aldric", "Do you remember the silver falcon?",
                    "I said the silver falcon would return at dawn.", "exchange_test", 2000L);
                Dictionary<string, object> finished = FinishConversationSession(campaignId, sessionId, "test", false, 20d, 2001L);
                Dictionary<string, object> newerStarted = ConversationStartApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["npcId"] = "npc_a", ["playerId"] = "player", ["worldDay"] = 21d
                });
                string newerSessionId = ReadString(newerStarted, "sessionId", "");
                StoreConversationExchange(campaignId, new Dictionary<string, object>
                {
                    ["conversationSessionId"] = newerSessionId, ["worldDay"] = 21d, ["channel"] = "in_person"
                }, "npc_a", "player", "Player", "Aldric", "Did you see a falcon?",
                    "A falcon crossed the western road.", "exchange_newer_test", 2100L);
                FinishConversationSession(campaignId, newerSessionId, "test", false, 21d, 2101L);
                Dictionary<string, object> longStarted = ConversationStartApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["npcId"] = "npc_a", ["playerId"] = "player", ["worldDay"] = 22d
                });
                string longSessionId = ReadString(longStarted, "sessionId", "");
                string longPlayerTurn = new string('x', 3000) + " obsidian compass";
                StoreConversationExchange(campaignId, new Dictionary<string, object>
                {
                    ["conversationSessionId"] = longSessionId, ["worldDay"] = 22d, ["channel"] = "in_person"
                }, "npc_a", "player", "Player", "Aldric", longPlayerTurn,
                    "I will remember that.", "exchange_long_exact_test", 2200L);
                FinishConversationSession(campaignId, longSessionId, "test", false, 22d, 2201L);
                Dictionary<string, object> failedRecallStarted = ConversationStartApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["npcId"] = "npc_a", ["playerId"] = "player", ["worldDay"] = 22.5d
                });
                string failedRecallSessionId = ReadString(failedRecallStarted, "sessionId", "");
                StoreConversationExchange(campaignId, new Dictionary<string, object>
                {
                    ["conversationSessionId"] = failedRecallSessionId, ["worldDay"] = 22.5d, ["channel"] = "in_person"
                }, "npc_a", "player", "Player", "Aldric",
                    "Paraphrase the obsidian-compass fact without losing its exact name or description.",
                    "No.", "exchange_failed_recall_probe", 2250L);
                FinishConversationSession(campaignId, failedRecallSessionId, "test", false, 22.5d, 2251L);
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    Dictionary<string, object> exactRoute = BuildMemoryRetrievalRoute(disabledSettings,
                        "What exactly did you say about the silver falcon returning at dawn?", new Dictionary<string, object> { ["worldDay"] = 23d });
                    Dictionary<string, object> exact = SearchExactConversationHistory(connection, "npc_a", "silver falcon return dawn", exactRoute, 2400);
                    Dictionary<string, object> longExactRoute = BuildMemoryRetrievalRoute(disabledSettings,
                        "What exactly did I say about the obsidian compass?", new Dictionary<string, object> { ["worldDay"] = 23d });
                    Dictionary<string, object> longExact = SearchExactConversationHistory(connection, "npc_a", "obsidian compass", longExactRoute, 600);
                    Dictionary<string, object> recentRoute = BuildMemoryRetrievalRoute(disabledSettings,
                        "What do you remember about our last conversation?", new Dictionary<string, object> { ["worldDay"] = 23d });
                    Dictionary<string, object> recentRaw = SearchExactConversationHistory(connection, "npc_a",
                        "What do you remember about our last conversation?", recentRoute, 50000, new Dictionary<string, object>(), "", 30);
                    int turns = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM conversation_turns WHERE session_id=$id AND status='active';", new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault(), "count", 0);
                    int links = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM memory_sources WHERE document_type='summary' AND document_id=$id;", new Dictionary<string, object> { ["id"] = ReadString(finished, "sceneSummaryId", "") }).FirstOrDefault(), "count", 0);
                    add("session_sources", turns == 2 && !string.IsNullOrWhiteSpace(ReadString(finished, "sceneSummaryId", "")) && links >= 3,
                        "Completed sessions retain exact turns and link scene summaries back to their sources.");
                    add("exact_expansion", ReadString(exact, "text", "").Contains("silver falcon")
                        && ReadStringList(exact, "expandedTurnIds").Count == 2
                        && ReadString(exact, "sessionId", "") == sessionId,
                        "BM25-ranked exact-history retrieval selects the strongest lexical exchange instead of the newest partial mention.");
                    add("exact_long_first_turn_excerpt", ReadString(longExact, "text", "").Contains("obsidian compass")
                        && ReadStringList(longExact, "expandedTurnIds").Count >= 1
                        && ReadString(longExact, "sessionId", "") == longSessionId,
                        "Exact-history retrieval keeps a bounded, source-linked excerpt containing the matched evidence when a turn exceeds the prompt budget, and a newer failed recall probe cannot shadow the source.");
                    add("recent_raw_npc_window",
                        ReadString(recentRaw, "selectionMode", "") == "recent_closed_turn_window"
                        && ReadInt(recentRaw, "rawTurnLimit", 0) == 30
                        && ReadString(recentRaw, "text", "").Contains("obsidian compass")
                        && ReadString(recentRaw, "text", "").Contains("Beatrice")
                        && ReadString(recentRaw, "text", "").Contains("ribbon and pin"),
                        "The recent 30-turn raw window is NPC-specific and preserves every attributed line from included group sessions.");
                    string livePromptContext = BuildPromptContext(campaignId, "npc_a",
                        new Dictionary<string, object> { ["mainHeroStringId"] = "player", ["worldDay"] = 23d },
                        "For a simple memory check, what did I say about the obsidian compass in our first conversation?",
                        "", new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>(),
                        new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(),
                        false, new Dictionary<string, object> { ["mainHeroStringId"] = "player", ["worldDay"] = 23d }, "dialogue");
                    add("exact_live_prompt_context",
                        livePromptContext.Contains("Exact conversation source") && livePromptContext.Contains("obsidian compass"),
                        "The production dialogue prompt context includes the exact-history section and its matched evidence.");
                    string ordinaryContinuityContext = BuildPromptContext(campaignId, "npc_a",
                        new Dictionary<string, object> { ["mainHeroStringId"] = "player", ["worldDay"] = 23d },
                        "Paraphrase the obsidian-compass fact without losing its exact description.",
                        "", new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>(),
                        new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(),
                        false, new Dictionary<string, object> { ["mainHeroStringId"] = "player", ["worldDay"] = 23d }, "dialogue");
                    add("recent_raw_window_without_recall_keywords",
                        ordinaryContinuityContext.Contains("Recent source-bearing closed conversations")
                        && ordinaryContinuityContext.Contains("obsidian compass")
                        && ordinaryContinuityContext.Contains("Beatrice")
                        && ordinaryContinuityContext.Contains("ribbon and pin"),
                        "The configured recent raw NPC window is present even when ordinary player wording does not trigger the exact-recall router, and complete attributed group context is retained.");
                }
                Dictionary<string, object> secondFinish = ConversationFinishApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["sessionId"] = sessionId, ["worldDay"] = 20d
                });
                add("finish_idempotency", ReadBool(secondFinish, "idempotent", false), "Conversation finish is idempotent.");
            }
            catch (Exception ex)
            {
                add("integration_fixture", false, "Categorized memory fixture failed: " + LimitText(ex.Message, 400));
            }
            finally
            {
                try
                {
                    ReignPostgreSqlStorage.ClearAllPools();
                    string path = CampaignDirectory(campaignId);
                    if (Directory.Exists(path)) Directory.Delete(path, true);
                }
                catch
                {
                }
            }
            return rows;
        }
    }
}
