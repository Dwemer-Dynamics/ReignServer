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
    internal static partial class DialogueService
    {
        internal static void EnsureCategorizedMemorySchema(ReignDbConnection connection)
        {
            Program.EnsureDatabaseColumn(connection, "events", "event_category", "TEXT NOT NULL DEFAULT ''");
            Program.EnsureDatabaseColumn(connection, "events", "event_subtype", "TEXT NOT NULL DEFAULT ''");
            Program.EnsureDatabaseColumn(connection, "memories", "memory_domain", "TEXT NOT NULL DEFAULT ''");
            Program.EnsureDatabaseColumn(connection, "summaries", "memory_lane", "TEXT NOT NULL DEFAULT ''");

            Program.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS knowledge_receipts (
receipt_id TEXT PRIMARY KEY,event_id TEXT NOT NULL,npc_id TEXT NOT NULL,acquisition_type TEXT NOT NULL DEFAULT 'known',
confidence REAL NOT NULL DEFAULT 1.0,reliability REAL NOT NULL DEFAULT 1.0,acquired_day REAL NOT NULL DEFAULT 0,
source_id TEXT NOT NULL DEFAULT '',status TEXT NOT NULL DEFAULT 'active',payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,
UNIQUE(event_id,npc_id,acquisition_type,source_id));");
            Program.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_knowledge_receipts_npc ON knowledge_receipts(npc_id,status,acquired_day DESC);");
            Program.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_knowledge_receipts_event ON knowledge_receipts(event_id,status);");

            Program.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_sessions (
session_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,npc_id TEXT NOT NULL,player_id TEXT NOT NULL DEFAULT '',
channel TEXT NOT NULL DEFAULT 'in_person',status TEXT NOT NULL DEFAULT 'open',start_ts INTEGER NOT NULL,end_ts INTEGER NOT NULL DEFAULT 0,
start_world_day REAL NOT NULL DEFAULT 0,end_world_day REAL NOT NULL DEFAULT 0,location_id TEXT NOT NULL DEFAULT '',
participants_json TEXT NOT NULL DEFAULT '[]',close_reason TEXT NOT NULL DEFAULT '',scene_summary_id TEXT NOT NULL DEFAULT '',
legacy_inferred INTEGER NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}');");
            Program.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_sessions_npc ON conversation_sessions(npc_id,status,start_ts DESC);");

            Program.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_turns (
turn_id TEXT PRIMARY KEY,session_id TEXT NOT NULL,event_id TEXT NOT NULL DEFAULT '',turn_order INTEGER NOT NULL,
exchange_id TEXT NOT NULL DEFAULT '',role TEXT NOT NULL,speaker_id TEXT NOT NULL DEFAULT '',speaker_name TEXT NOT NULL DEFAULT '',
text TEXT NOT NULL,channel TEXT NOT NULL DEFAULT 'in_person',world_day REAL NOT NULL DEFAULT 0,ts INTEGER NOT NULL,
location_id TEXT NOT NULL DEFAULT '',participants_json TEXT NOT NULL DEFAULT '[]',status TEXT NOT NULL DEFAULT 'active',
payload_json TEXT NOT NULL DEFAULT '{}',UNIQUE(session_id,turn_order));");
            Program.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_turns_session ON conversation_turns(session_id,turn_order);");
            Program.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_turns_ts ON conversation_turns(ts DESC);");
            Program.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_turns_speaker_ts ON conversation_turns(speaker_id,ts DESC);");
            EnsureChatViewSchema(connection);
            Program.ExecuteSql(connection, @"CREATE VIRTUAL TABLE IF NOT EXISTS conversation_turn_fts USING fts5(
turn_id UNINDEXED,text,speaker,participants,channel UNINDEXED);");

            Program.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS memory_sources (
document_type TEXT NOT NULL,document_id TEXT NOT NULL,source_type TEXT NOT NULL,source_id TEXT NOT NULL,
ordinal INTEGER NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}',
PRIMARY KEY(document_type,document_id,source_type,source_id));");
            Program.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_memory_sources_source ON memory_sources(source_type,source_id);");
        }

        internal static void RunCategorizedMemoryMigration(ReignDbConnection connection, string campaignId)
        {
            Dictionary<string, object> applied = Program.QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='categorized_memory_v1' LIMIT 1;").FirstOrDefault();
            if (string.Equals(ReadString(applied, "value", ""), "complete", StringComparison.OrdinalIgnoreCase))
            {
                RunMemoryDomainV2Migration(connection);
                RunConversationTurnPayloadV2Migration(connection);
                RunPlayerLieDetectionRemovalMigration(connection);
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (Dictionary<string, object> row in Program.QuerySql(connection, "SELECT event_id,event_type,summary,payload_json FROM events;"))
            {
                Dictionary<string, object> payload = TryParseJsonObject(ReadString(row, "payload_json", "")) ?? new Dictionary<string, object>();
                string eventType = ReadString(row, "event_type", "unknown");
                string summary = ReadString(row, "summary", "");
                Program.ExecuteSql(connection, "UPDATE events SET event_category=$category,event_subtype=$subtype WHERE event_id=$id;",
                    new Dictionary<string, object>
                    {
                        ["category"] = ClassifyEventCategory(eventType, payload, summary),
                        ["subtype"] = ClassifyEventSubtype(eventType, summary),
                        ["id"] = ReadString(row, "event_id", "")
                    });
            }

            foreach (Dictionary<string, object> row in Program.QuerySql(connection, "SELECT memory_id,memory_type,summary,source,tags_json,payload_json FROM memories;"))
            {
                string domain = ClassifyMemoryDomain(row);
                Program.ExecuteSql(connection, "UPDATE memories SET memory_domain=$domain WHERE memory_id=$id;",
                    new Dictionary<string, object> { ["domain"] = domain, ["id"] = ReadString(row, "memory_id", "") });
            }

            MigrateLegacyPublicProjections(connection, now);
            ImportLegacyConversationHistory(connection, campaignId);
            RebuildCategorizedSearchIndexes(connection);
            Program.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('categorized_memory_v1','complete');");
            RunMemoryDomainV2Migration(connection);
            RunConversationTurnPayloadV2Migration(connection);
            RunPlayerLieDetectionRemovalMigration(connection);
        }

        private static void RunMemoryDomainV2Migration(ReignDbConnection connection)
        {
            Dictionary<string, object> applied = Program.QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='categorized_memory_domain_v2' LIMIT 1;").FirstOrDefault();
            if (string.Equals(ReadString(applied, "value", ""), "complete", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (Dictionary<string, object> row in Program.QuerySql(connection,
                "SELECT memory_id,memory_type,summary,source,tags_json,payload_json FROM memories;"))
            {
                Dictionary<string, object> payload = TryParseJsonObject(ReadString(row, "payload_json", "")) ?? new Dictionary<string, object>();
                string eventType = ReadFirstString(payload, "eventType", "event_type", "type");
                string eventCategory = FirstNonEmpty(ReadFirstString(payload, "eventCategory", "event_category"),
                    ClassifyEventCategory(eventType, payload, ReadString(row, "summary", "")));
                string domain = Program.MemoryDomainForWorldEventProjection(eventCategory, eventType,
                    ReadString(row, "memory_type", ""), ReadString(row, "summary", ""),
                    Program.TextListFromJson(ReadString(row, "tags_json", "[]")), ReadString(row, "source", ""));
                Program.ExecuteSql(connection, "UPDATE memories SET memory_domain=$domain WHERE memory_id=$id;",
                    new Dictionary<string, object> { ["domain"] = domain, ["id"] = ReadString(row, "memory_id", "") });
            }
            Program.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('categorized_memory_domain_v2','complete');");
        }

        private static void RunConversationTurnPayloadV2Migration(ReignDbConnection connection)
        {
            Dictionary<string, object> applied = Program.QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='conversation_turn_payload_v2' LIMIT 1;").FirstOrDefault();
            if (string.Equals(ReadString(applied, "value", ""), "complete", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Dictionary<string, object> result = CompactConversationTurnPayloadRows(connection);
            Program.ExecuteSql(connection,
                "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('conversation_turn_payload_v2','complete');");
            if (ReadLong(result, "bytesRemoved", 0L) > 0)
            {
                Program.ExecuteSql(connection,
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
                Program.ExecuteSql(connection, "BEGIN IMMEDIATE;");
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
                    Program.ExecuteSql(connection, "COMMIT;");
                }
                catch
                {
                    try { Program.ExecuteSql(connection, "ROLLBACK;"); } catch { }
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

        internal static Dictionary<string, object> BuildConversationTurnStoragePayload(
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
                "phaseId", "phaseIndex", "memoryGroupKey", "playerPromptLabel", "approachOpening", "npcStoryId", "npcLifeRequestId", "npcLivesRevision", "npcLifeContext",
                "playerAttentionPresence", "approachRequestId", "authoritativeApproachBrief", "authoritativeApproachOpening", "authoritativeApproachKind", "rescueContext", "rescueOpening",
                "actualPlayerTurn", "courtLifePhase", "conversationMode", "routeInputHash", "inputSourceId", "contributionId", "playerNarrationProjectionVersion"
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
                "sourceTurnIds", "sourceEventIds", "npcLifeAudienceIds"
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
                    ["supportingQuote"] = Program.LimitText(ReadString(signal, "supportingQuote", ""), 512),
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

            // Names only bind an explicit whisper recipient on later exact reads;
            // full character profiles and private persona fields are not copied.
            var attendees = ReadDictionaryList(source, "attendees").Take(16)
                .Where(p => !string.IsNullOrWhiteSpace(Program.CharacterIdFrom(p)))
                .Select(p => TestDict("heroStringId", Program.LimitText(Program.CharacterIdFrom(p), 160),
                    "name", Program.LimitText(ReadString(p, "name", ""), 160))).ToList();
            if (attendees.Count > 0) compact["attendees"] = attendees;
            if (ReadDictionary(source, "conversationGrounding") is { } grounding)
                compact["conversationGrounding"] = TestDict("schema", "reign-conversation-grounding-v1",
                    "status", ReadString(grounding, "status", "unresolved"), "replyHash", ReadString(grounding, "replyHash", ""),
                    "originalSourceId", ReadString(grounding, "originalSourceId", ""));

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

        internal static string SerializeConversationTurnStoragePayload(Dictionary<string, object> source)
        {
            const int hardLimitCharacters = 16384;
            Dictionary<string, object> compact = BuildConversationTurnStoragePayload(source);
            string json = Json.Serialize(compact);
            if (json.Length <= hardLimitCharacters) return json;
            Dictionary<string, object> fallback = new Dictionary<string, object>
            {
                ["storageSchema"] = "conversation_turn_payload_v2",
                ["payloadGuardTriggered"] = true,
                ["timelineId"] = ReadString(compact, "timelineId", ""),
                ["worldDay"] = ReadDouble(compact, "worldDay", 0d),
                ["routeInputHash"] = ReadString(compact, "routeInputHash", ""),
                ["correlationId"] = ReadString(compact, "correlationId", ""),
                ["conversationSessionId"] = ReadFirstString(compact, "conversationSessionId", "sessionId", "conversationId"),
                ["exchangeId"] = ReadString(compact, "exchangeId", ""),
                ["turnId"] = ReadString(compact, "turnId", "")
            };
            foreach (string key in new[] { "inputSourceId", "contributionId", "conversationGrounding", "playerNarrationProjectionVersion" })
                if (compact.ContainsKey(key)) fallback[key] = compact[key];
            Program.LogOperational("storage.conversation_turn_payload_guard", new Dictionary<string, object>
            {
                ["characters"] = json.Length, ["hardLimitCharacters"] = hardLimitCharacters,
                ["correlationId"] = ReadString(compact, "correlationId", "")
            });
            return Json.Serialize(fallback);
        }

        internal static bool IsCompactConversationMetadataScalar(object value)
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
            List<Dictionary<string, object>> groups = Program.QuerySql(connection, @"SELECT event_id,COUNT(*) AS row_count
FROM memories WHERE event_id<>'' AND visibility LIKE 'public%' AND status='active'
GROUP BY event_id HAVING COUNT(*)>1;");
            foreach (Dictionary<string, object> group in groups)
            {
                string eventId = ReadString(group, "event_id", "");
                List<Dictionary<string, object>> rows = Program.QuerySql(connection,
                    "SELECT * FROM memories WHERE event_id=$event AND status='active' ORDER BY ts,memory_id;",
                    new Dictionary<string, object> { ["event"] = eventId });
                if (rows.Count == 0)
                {
                    continue;
                }

                Dictionary<string, object> first = rows[0];
                string canonicalId = "mem_world_" + Guid.NewGuid().ToString("N");
                Program.InsertMemoryRow(connection, canonicalId, eventId, "", "world_knowledge",
                    ReadLong(first, "ts", now), ReadDouble(first, "world_day", 0d), ReadString(first, "location_id", ""),
                    ReadString(first, "summary", ""), Program.TextListFromJson(ReadString(first, "participants_json", "[]")),
                    Program.TextListFromJson(ReadString(first, "witnesses_json", "[]")), ReadString(first, "about_entities_json", "[]"),
                    new List<string>(), Program.TextListFromJson(ReadString(first, "heard_as_rumor_by_json", "[]")),
                    Program.TextListFromJson(ReadString(first, "hidden_from_json", "[]")), ReadString(first, "visibility", "public"),
                    ReadDouble(first, "importance", 0.5d), ReadDouble(first, "emotional_weight", 0d), ReadDouble(first, "confidence", 1d),
                    Program.TextListFromJson(ReadString(first, "tags_json", "[]")), "active", "legacy_canonical_migration",
                    ReadString(first, "payload_json", "{}"), "", "not_indexed");
                Program.ExecuteSql(connection, "UPDATE memories SET memory_domain='world_affairs' WHERE memory_id=$id;",
                    new Dictionary<string, object> { ["id"] = canonicalId });

                HashSet<string> direct = new HashSet<string>(
                    rows.SelectMany(row => Program.TextListFromJson(ReadString(row, "participants_json", "[]"))
                        .Concat(Program.TextListFromJson(ReadString(row, "witnesses_json", "[]")))), StringComparer.OrdinalIgnoreCase);
                foreach (Dictionary<string, object> row in rows)
                {
                    string memoryId = ReadString(row, "memory_id", "");
                    string ownerId = ReadString(row, "owner_id", "");
                    if (!string.IsNullOrWhiteSpace(ownerId) && direct.Contains(ownerId))
                    {
                        Program.ExecuteSql(connection, @"UPDATE memories SET memory_type='personal_experience',memory_domain='world_affairs',
visibility='private',known_by_json=$known WHERE memory_id=$id;",
                            new Dictionary<string, object> { ["known"] = Json.Serialize(new List<string> { ownerId }), ["id"] = memoryId });
                        UpsertKnowledgeReceipt(connection, eventId, ownerId,
                            Program.TextListFromJson(ReadString(row, "witnesses_json", "[]")).Contains(ownerId, StringComparer.OrdinalIgnoreCase) ? "witness" : "participant",
                            ReadDouble(row, "confidence", 1d), 1d, ReadDouble(row, "world_day", 0d), memoryId, now);
                    }
                    else
                    {
                        Program.ExecuteSql(connection, "UPDATE memories SET status='superseded',memory_domain='world_affairs' WHERE memory_id=$id;",
                            new Dictionary<string, object> { ["id"] = memoryId });
                    }
                }
            }
        }

        private static void ImportLegacyConversationHistory(ReignDbConnection connection, string campaignId)
        {
            string charactersDir = Path.Combine(Program.CampaignDirectory(campaignId), "characters");
            if (!Directory.Exists(charactersDir))
            {
                return;
            }

            foreach (string characterDir in Directory.GetDirectories(charactersDir))
            {
                string heroId = Path.GetFileName(characterDir);
                string path = Path.Combine(characterDir, "history", "dialogue.jsonl");
                List<Dictionary<string, object>> lines = Program.ReadJsonLinesFromPath(path)
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
                    Program.ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_sessions(
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

        internal static void RebuildCategorizedSearchIndexes(ReignDbConnection connection)
        {
            Program.ExecuteSql(connection, "DELETE FROM memory_fts;");
            foreach (Dictionary<string, object> row in Program.QuerySql(connection, "SELECT * FROM memories WHERE status='active';"))
            {
                Program.InsertMemoryFts(connection, ReadString(row, "memory_id", ""), ReadString(row, "summary", ""),
                    Program.TextListFromJson(ReadString(row, "tags_json", "[]")),
                    Program.TextListFromJson(ReadString(row, "about_entities_json", "[]"))
                        .Concat(Program.TextListFromJson(ReadString(row, "participants_json", "[]"))).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            }
            Program.ExecuteSql(connection, "DELETE FROM conversation_turn_fts;");
            foreach (Dictionary<string, object> row in Program.QuerySql(connection, "SELECT * FROM conversation_turns WHERE status='active';"))
            {
                if (string.IsNullOrWhiteSpace(ConversationTurnTextForRecall(row))) continue;
                InsertConversationTurnFts(connection, ReadString(row, "turn_id", ""), ConversationTurnTextForRecall(row),
                    ReadString(row, "speaker_name", ""), ReadString(row, "participants_json", "[]"), ReadString(row, "channel", "in_person"));
            }
        }

        internal static Dictionary<string, object> ConversationStartApi(Dictionary<string, object> payload)
        {
            // Serialize session-start retries before closing interrupted sessions.
            // A retry must never replace or close the session it just created.
            lock (ConversationStartGate) return ConversationStartCore(payload);
        }

        private static readonly object ConversationStartGate = new object();

        private static Dictionary<string, object> ConversationStartCore(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string npcId = ReadFirstString(payload, "npcId", "heroStringId", "heroId");
            string playerId = ReadFirstString(payload, "playerId", "playerHeroStringId", "mainHeroStringId");
            if (string.IsNullOrWhiteSpace(npcId))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "npcId or heroStringId is required." };
            }

            string id = FirstNonEmpty(ReadFirstString(payload, "sessionId", "conversationSessionId"), "conversation_" + Guid.NewGuid().ToString("N"));
            using (var connection = Program.OpenCampaignConnection(campaignId))
            {
                var logical = PrepareLogicalConversation(connection, payload, campaignId, npcId, playerId, id);
                if (!ReadBool(logical, "ok", false) || ReadBool(logical, "idempotent", false)) return logical;
            }
            List<string> interrupted;
            using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
            {
                interrupted = Program.QuerySql(connection,
                    "SELECT session_id FROM conversation_sessions WHERE npc_id=$npc AND status='open' ORDER BY start_ts;",
                    new Dictionary<string, object> { ["npc"] = npcId })
                    .Select(row => ReadString(row, "session_id", "")).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
            }
            foreach (string sessionId in interrupted)
            {
                FinishConversationSession(campaignId, sessionId, "interrupted_by_new_session", true,
                    ReadDouble(payload, "worldDay", 0d), ReadLong(payload, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            }

            long ts = ReadLong(payload, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            string channel = ReadString(payload, "channel", "in_person");
            string locationId = ReadFirstString(payload, "locationId", "settlementId", "currentSettlementId");
            List<string> participants = Program.MergeStringLists(ReadStringList(payload, "participants"), new[] { npcId, playerId });
            using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
            {
                Program.ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_sessions(
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
            return new Dictionary<string, object> { ["ok"] = true, ["sessionId"] = id, ["logicalConversationId"] = ReadString(payload, "logicalConversationId", ""), ["interruptedSessionsClosed"] = interrupted };
        }

        internal static Dictionary<string, object> ConversationFinishApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string sessionId = ReadFirstString(payload, "sessionId", "conversationSessionId", "conversationId");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Program.MemoryConversationFinishedApi(payload);
            }
            var finished = FinishConversationSession(campaignId, sessionId, ReadString(payload, "reason", "closed_by_game"),
                ReadBool(payload, "interrupted", false), ReadDouble(payload, "worldDay", 0d),
                ReadLong(payload, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            if (ReadBool(finished, "ok", false))
            using (var connection = Program.OpenCampaignConnection(campaignId))
            {
                var session = Program.QuerySql(connection, "SELECT payload_json FROM conversation_sessions WHERE session_id=$id;", TestDict("id", sessionId)).FirstOrDefault();
                string timeline = ContinuityTimeline(GossipJson(session));
                if (timeline == WorldSimulationService.ActiveWorldHistoryTimeline(connection))
                {
                    finished["communicationCampaignId"] = campaignId; finished["communicationTimelineId"] = timeline;
                    finished["communicationGeneration"] = CharactersService.NpcLivesGeneration(connection);
                    finished["communicationHostReservations"] = CommunicationHostWork(connection, timeline);
                }
            }
            return finished;
        }

        internal static Dictionary<string, object> ConversationRecoverApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            long ts = ReadLong(payload, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            List<string> openSessions;
            using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
            {
                openSessions = Program.QuerySql(connection, "SELECT session_id FROM conversation_sessions WHERE status='open' ORDER BY start_ts;")
                    .Select(row => ReadString(row, "session_id", "")).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
            }
            List<Dictionary<string, object>> recovered = new List<Dictionary<string, object>>();
            foreach (string sessionId in openSessions)
            {
                recovered.Add(FinishConversationSession(campaignId, sessionId, "interrupted_by_campaign_reload", true, worldDay, ts));
            }
            return new Dictionary<string, object> { ["ok"] = true, ["recoveredCount"] = recovered.Count, ["sessions"] = recovered };
        }

        internal static Dictionary<string, object> FinishConversationSession(string campaignId, string sessionId, string reason, bool interrupted, double worldDay, long ts)
        {
            Dictionary<string, object> session;
            List<Dictionary<string, object>> turns;
            string sceneJobId;
            lock (ConversationStartGate)
            using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
            using (var closure = connection.BeginTransaction())
            {
                session = Program.QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$id LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault();
                if (session == null)
                {
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Conversation session was not found.", ["sessionId"] = sessionId };
                }
                // A late teardown/transition receipt cannot replace an explicit
                // ending and make a finished conversation eligible for resumption.
                string existingReason = ReadString(session, "close_reason", "");
                if (new[] { "closed_by_player", "conversation_ended", "goodbye" }.Contains(existingReason))
                    reason = existingReason;
                turns = Program.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$id AND status='active' ORDER BY turn_order;",
                    new Dictionary<string, object> { ["id"] = sessionId });
                var existingScene=Program.QuerySql(connection,"SELECT payload_json FROM summaries WHERE summary_id=$id;",
                    new Dictionary<string,object>{["id"]=ReadString(session,"scene_summary_id","")}).FirstOrDefault();
                if (existingScene != null && ReadString(TryParseJsonObject(ReadString(existingScene,"payload_json","{}")),"sourceHash","")==MemorySourceHash(turns))
                {
                    if (ReadString(session,"status","")!="closed" && ReadString(session,"status","")!="interrupted")
                        Program.ExecuteSql(connection,"UPDATE conversation_sessions SET status=$status,end_ts=$ts,end_world_day=$day,close_reason=$reason WHERE session_id=$id;",
                            new Dictionary<string,object>{["status"]=interrupted?"interrupted":"closed",["ts"]=ts,["day"]=worldDay,["reason"]=reason??"",["id"]=sessionId});
                    ReleaseCommunicationSession(connection, session, reason, worldDay);
                    closure.Commit();
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true, ["idempotent"] = true, ["sessionId"] = sessionId,
                        ["sceneSummaryId"] = ReadString(session, "scene_summary_id", "")
                    };
                }
                sceneJobId = EnqueueMemoryJob(connection, campaignId, "scene_summary", ReadString(session, "npc_id", ""), "scene:" + sessionId, sessionId);
                ReleaseCommunicationSession(connection, session, reason, worldDay);
                Program.ExecuteSql(connection, @"UPDATE conversation_sessions SET status=$status,end_ts=$ts,end_world_day=$day,
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
            using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
            {
                var job = Program.QuerySql(connection, "SELECT result_json FROM memory_background_jobs WHERE job_id=$id;",
                    new Dictionary<string, object> { ["id"] = sceneJobId }).Single();
                scene = TryParseJsonObject(ReadString(job, "result_json", "{}")) ?? new Dictionary<string, object>();
            }
            string sceneId = ReadString(scene, "summaryId", "");
            Dictionary<string,object> arc = ReadDictionary(scene,"arc") ?? new Dictionary<string,object>();
            Dictionary<string,object> continuityArcRepairs = ReadDictionary(scene,"continuityArcRepairs") ?? new Dictionary<string,object>();
            string playerId = ReadString(session, "player_id", "");
            List<string> memoryOwners = Program.TextListFromJson(ReadString(session, "participants_json", "[]"))
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
                courtSocial = ReputationService.ProcessCompletedConversationCourtStanding(
                    campaignId, session, turns, worldDay, interrupted);
            }
            catch (Exception ex)
            {
                courtSocial = new Dictionary<string, object>
                {
                    ["processed"] = false,
                    ["error"] = Program.LimitText(ex.Message, 1000)
                };
                Program.LogOperational("court_social.conversation_finish_failed",
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId, ["sessionId"] = sessionId,
                        ["error"] = Program.LimitText(ex.ToString(), 4000)
                    });
            }
            Dictionary<string, object> ambassadorReport;
            try
            {
                ambassadorReport = CourtService.FinalizeOfficialAmbassadorScene(campaignId, session, turns, scene, worldDay, ts);
            }
            catch (Exception ex)
            {
                ambassadorReport = new Dictionary<string, object> { ["processed"] = false, ["error"] = Program.LimitText(ex.Message, 1000) };
                Program.LogOperational("ambassador.scene_finalize_failed", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["sessionId"] = sessionId, ["error"] = Program.LimitText(ex.ToString(), 4000)
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

        internal static Dictionary<string, object> StoreConversationExchange(string campaignId, Dictionary<string, object> request,
            string npcId, string playerId, string playerName, string npcName, string playerText, string reply, string eventId, long ts,
            Action<string> acceptanceBoundary = null, ConversationSourceAcceptance acceptance = null)
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
            List<string> participants = Program.MergeStringLists(ReadStringList(request, "participants"), new[] { npcId, playerId });
            int nextOrder;
            using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
            {
                using var transaction = connection.BeginTransaction();
                LockConversationSourcePublication(connection, sessionId);
                Dictionary<string, object> max = Program.QuerySql(connection,
                    "SELECT COALESCE(MAX(turn_order),-1)+1 AS next_order FROM conversation_turns WHERE session_id=$session;",
                    new Dictionary<string, object> { ["session"] = sessionId }).FirstOrDefault();
                nextOrder = ReadInt(max, "next_order", 0);
                string exchangeId = eventId;
                string playerTurnId = eventId + "_player";
                string npcTurnId = eventId + "_npc";
                PreservePlayerInputSource(connection, request, sessionId, eventId, exchangeId, playerId, playerName, worldDay, ts, locationId);
                InsertConversationTurn(connection, playerTurnId, sessionId, eventId, nextOrder, exchangeId, "player", playerId,
                    playerName, playerText, channel, worldDay, ts, locationId, participants, request);
                acceptanceBoundary?.Invoke("after_player_source");
                InsertConversationTurn(connection, npcTurnId, sessionId, eventId, nextOrder + 1, exchangeId, "npc", npcId,
                    npcName, reply, channel, worldDay, ts, locationId, participants, request);
                Program.ExecuteSql(connection, "UPDATE conversation_sessions SET end_ts=$ts,end_world_day=$day,location_id=CASE WHEN $location<>'' THEN $location ELSE location_id END WHERE session_id=$id;",
                    new Dictionary<string, object> { ["ts"] = ts, ["day"] = worldDay, ["location"] = locationId, ["id"] = sessionId });
                var result = new Dictionary<string, object>
                {
                    ["ok"] = true, ["sessionId"] = sessionId, ["exchangeId"] = exchangeId,
                    ["turnIds"] = new List<string> { playerTurnId, npcTurnId }, ["turnOrder"] = nextOrder
                };
                if (acceptance != null) result["acceptedUnit"] = acceptance(connection, result);
                acceptanceBoundary?.Invoke("before_source_commit");
                transaction.Commit();
                return result;
            }
        }

        internal static Dictionary<string, object> StorePartyConversationExchange(
            string campaignId,
            Dictionary<string, object> request,
            string npcId,
            string playerId,
            string playerName,
            string npcName,
            string playerText,
            string reply,
            string eventId,
            long ts, ConversationSourceAcceptance acceptance = null)
        {
            return StoreGroupedConversationExchange(campaignId, request, npcId, playerId, playerName, npcName,
                playerText, reply, eventId, ts, "party_chat", "", acceptance: acceptance);
        }

        internal static Dictionary<string, object> StoreSocialEventConversationExchange(
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
            long ts, ConversationSourceAcceptance acceptance = null)
        {
            string channel = string.Equals(ReadString(request, "templateId", ""), "generated_wilderness", StringComparison.OrdinalIgnoreCase)
                ? "wilderness_event" : "social_event";
            string sessionId = string.IsNullOrWhiteSpace(socialEventId) ? "" : "social_event_" + SafeMemoryKey(socialEventId);
            return StoreGroupedConversationExchange(campaignId, request, npcId, playerId, playerName, npcName,
                playerText, reply, sourceEventId, ts, channel, sessionId, acceptance: acceptance);
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
            string fallbackSessionId,
            Action<string> acceptanceBoundary = null, ConversationSourceAcceptance acceptance = null, bool floorProjection = false)
        {
            request = request ?? new Dictionary<string, object>();
            channel = string.IsNullOrWhiteSpace(channel) ? "party_chat" : channel;
            string sessionId = FirstNonEmpty(ReadFirstString(request, "conversationSessionId", "sessionId", "conversationId"), fallbackSessionId);
            List<string> participants = Program.MergeStringLists(
                Program.MergeStringLists(ReadStringList(request, "participants"), ReadStringList(request, "activeHeroIds")),
                new[] { npcId, playerId });
            double worldDay = ReadDouble(request, "worldDay", 0d);
            string locationId = ReadFirstString(request, "locationId", "currentSettlementId", "settlementId");

            bool sessionExists;
            using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
            {
                sessionExists = !string.IsNullOrWhiteSpace(sessionId)
                    && Program.QuerySql(connection, "SELECT session_id FROM conversation_sessions WHERE session_id=$id LIMIT 1;",
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
            string playerTurnId = exchangeId + (floorProjection ? "_floor_player" : "_player");
            string npcTurnId = exchangeId + "_npc_" + SafeMemoryKey(npcId);
            var result = TestDict("ok", true, "sessionId", sessionId, "exchangeId", exchangeId,
                "turnIds", new List<string> { playerTurnId, npcTurnId });
            using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
            {
                using var transaction = connection.BeginTransaction();
                LockConversationSourcePublication(connection, sessionId);
                // Planning uses a conservative local projection and makes no model
                // call. The interpreted writer projection must not collide with it.
                // Keep its bytes, but retire that provisional view atomically when
                // the same original receives its accepted observer projection.
                if (floorProjection && Program.QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE turn_id=$id AND status='active';",
                    TestDict("id", exchangeId + "_player")).Count > 0)
                {
                    transaction.Commit();
                    return result;
                }
                Dictionary<string, object> session = Program.QuerySql(connection,
                    "SELECT participants_json FROM conversation_sessions WHERE session_id=$id LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault();
                var sessionParticipants = Program.MergeStringLists(
                    Program.TextListFromJson(ReadString(session, "participants_json", "[]")),
                    participants);

                var existingPlayer = Program.QuerySql(connection,
                    "SELECT * FROM conversation_turns WHERE turn_id=$turn LIMIT 1;",
                    new Dictionary<string, object> { ["turn"] = playerTurnId }).FirstOrDefault();
                bool playerExists = existingPlayer != null;
                // The shared player turn belongs to the first NPC's event ID.
                // Later speakers may reference it, but cannot change its words,
                // speaker or branch, nor retroactively enlarge its audience.
                if (existingPlayer != null && (ReadString(existingPlayer, "text", "") != (playerText ?? "")
                    || ReadString(existingPlayer, "speaker_id", "") != playerId
                    || ReadString(existingPlayer, "session_id", "") != sessionId
                    || ContinuityTimeline(TryParseJsonObject(ReadString(existingPlayer, "payload_json", "{}"))) != ContinuityTimeline(request)))
                    throw new InvalidOperationException("This group beat already has different accepted player words or source scope.");
                PreservePlayerInputSource(connection, request, sessionId, eventId, exchangeId, playerId, playerName, worldDay, ts, locationId);
                int nextOrder = ReadInt(Program.QuerySql(connection,
                    "SELECT COALESCE(MAX(turn_order),-1)+1 AS next_order FROM conversation_turns WHERE session_id=$session;",
                    new Dictionary<string, object> { ["session"] = sessionId }).FirstOrDefault(), "next_order", 0);
                if (!playerExists)
                {
                    InsertConversationTurn(connection, playerTurnId, sessionId, eventId, nextOrder++, exchangeId,
                        "player", playerId, playerName, playerText, channel, worldDay, ts, locationId, participants, request);
                }
                if (!floorProjection && ReadString(request, "inputSourceId", "").Length > 0)
                {
                    var provisional = Program.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id AND session_id=$session AND speaker_id=$player AND status='active';",
                        TestDict("id", exchangeId + "_floor_player", "session", sessionId, "player", playerId)).SingleOrDefault();
                    if (ReadString(TryParseJsonObject(ReadString(provisional, "payload_json", "{}")), "inputSourceId", "") == ReadString(request, "inputSourceId", ""))
                    {
                        Program.ExecuteSql(connection, "UPDATE conversation_turns SET status='superseded_projection' WHERE turn_id=$id;", TestDict("id", exchangeId + "_floor_player"));
                        Program.ExecuteSql(connection, "DELETE FROM conversation_turn_fts WHERE turn_id=$id;", TestDict("id", exchangeId + "_floor_player"));
                    }
                }
                acceptanceBoundary?.Invoke("after_player_source");
                InsertConversationTurn(connection, npcTurnId, sessionId, eventId, nextOrder, exchangeId,
                    "npc", npcId, npcName, reply, channel, worldDay, ts, locationId, participants, request);
                Program.ExecuteSql(connection, @"UPDATE conversation_sessions SET end_ts=$ts,end_world_day=$day,
location_id=CASE WHEN $location<>'' THEN $location ELSE location_id END,participants_json=$participants
WHERE session_id=$id;", new Dictionary<string, object>
                {
                    ["ts"] = ts,
                    ["day"] = worldDay,
                    ["location"] = locationId,
                    ["participants"] = Json.Serialize(sessionParticipants),
                    ["id"] = sessionId
                });
                if (acceptance != null) result["acceptedUnit"] = acceptance(connection, result);
                acceptanceBoundary?.Invoke("before_source_commit");
                transaction.Commit();
            }
            return result;
        }

        internal static List<Dictionary<string, object>> ReadCurrentSessionDialogueLines(string campaignId, string sessionId, int limit)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return new List<Dictionary<string, object>>();
            using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
            {
                return ReadLogicalConversationTurns(connection, sessionId, Math.Min(100, limit + 2))
                    .Select(row => new Dictionary<string, object> {
                        ["id"] = ReadString(row, "turn_id", ""), ["turn_order"] = ReadInt(row, "turn_order", 0),
                        ["ts"] = ReadLong(row, "ts", 0), ["role"] = ReadString(row, "role", ""),
                        ["speaker"] = ReadString(row, "speaker_name", ""), ["speakerHeroStringId"] = ReadString(row, "speaker_id", ""),
                        ["channel"] = ReadString(row, "channel", ""), ["text"] = ReadString(row, "text", ""),
                        ["worldDay"] = ReadDouble(row, "world_day", 0), ["sessionId"] = ReadString(row, "session_id", ""),
                        ["exchangeId"] = ReadString(row, "exchange_id", ""), ["locationId"] = ReadString(row, "location_id", "")
                    }).ToList();
            }
        }

        private static void LockConversationSourceSession(ReignDbConnection connection, string sessionId) =>
            Program.ExecuteSql(connection, "SELECT pg_advisory_xact_lock(hashtextextended($key,0));", TestDict("key", "conversation_source:" + sessionId));

        private static void LockConversationSourcePublication(ReignDbConnection connection, string sessionId)
        {
            LockConversationSourceSession(connection, sessionId);
            if (!string.IsNullOrWhiteSpace(_playerActionGeneration))
            {
                var current = Program.QuerySql(connection, "SELECT restore_generation FROM memory_precision_state WHERE state_id='current' FOR SHARE;").Single();
                if (ReadString(current, "restore_generation", "") != _playerActionGeneration)
                    throw new InvalidOperationException("The campaign changed before conversation source publication.");
            }
        }

        internal static void InsertConversationTurn(ReignDbConnection connection, string turnId, string sessionId, string eventId,
            int turnOrder, string exchangeId, string role, string speakerId, string speakerName, string text, string channel,
            double worldDay, long ts, string locationId, List<string> participants, Dictionary<string, object> payload)
        {
            if (string.IsNullOrWhiteSpace(turnId) || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            bool technicalNotice = role == "npc" && !HasRecallableConversationSpeech(text);
            Program.ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_turns(
turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,channel,world_day,ts,location_id,
participants_json,status,payload_json)
VALUES($turn,$session,$event,$ordinal,$exchange,$role,$speaker_id,$speaker_name,$text,$channel,$day,$ts,$location,$participants,$status,$payload);",
                new Dictionary<string, object>
                {
                    ["turn"] = turnId, ["session"] = sessionId, ["event"] = eventId ?? "", ["ordinal"] = turnOrder,
                    ["exchange"] = exchangeId ?? "", ["role"] = role ?? "", ["speaker_id"] = speakerId ?? "",
                    ["speaker_name"] = speakerName ?? "", ["text"] = text ?? "", ["channel"] = channel ?? "in_person",
                    ["day"] = worldDay, ["ts"] = ts, ["location"] = locationId ?? "",
                    ["participants"] = Json.Serialize(participants ?? new List<string>()),
                    ["status"] = technicalNotice ? "technical_notice" : "active",
                    ["payload"] = SerializeConversationTurnStoragePayload(payload)
                });
            var stored = Program.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id;", TestDict("id", turnId)).Single();
            var storedPayload = TryParseJsonObject(ReadString(stored, "payload_json", "{}"));
            var audience = new HashSet<string>(participants ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            if (ReadString(stored, "session_id", "") != sessionId || ReadString(stored, "exchange_id", "") != (exchangeId ?? "")
                || ReadString(stored, "role", "") != (role ?? "") || ReadString(stored, "speaker_id", "") != (speakerId ?? "")
                || ReadString(stored, "text", "") != (text ?? "") || ReadString(stored, "channel", "") != (channel ?? "in_person")
                || !audience.SetEquals(Program.TextListFromJson(ReadString(stored, "participants_json", "[]")))
                || ReadString(storedPayload, "timelineId", "") != ReadString(payload, "timelineId", ""))
                throw new InvalidOperationException("A conversation source ID already belongs to different words, identity, audience or timeline.");
            if (!technicalNotice) StoreChatViewFrame(connection, turnId, sessionId, exchangeId, payload);
            // Replays may repair a missing derived index, but must only index the
            // immutable winner. Never mix an old source with a new draft's words.
            if (!technicalNotice)
                InsertConversationTurnFts(connection, turnId, ConversationTurnTextForRecall(stored), ReadString(stored, "speaker_name", ""),
                    ReadString(stored, "participants_json", "[]"), ReadString(stored, "channel", "in_person"));
        }

        internal static readonly string[] TechnicalConversationNoticePrefixes = {
            "[The NPC's decision could not be verified.",
            "[Dialogue continuity evidence could not be verified.",
            "[Dialogue memory correction could not be verified.",
            "[Dialogue validation could not be completed."
        };

        internal static bool IsTechnicalConversationNotice(string text)
        {
            string reply = text ?? "";
            foreach (string notice in TechnicalConversationNoticePrefixes)
                if (reply.StartsWith(notice, StringComparison.Ordinal)
                    || reply.Contains("\n\n" + notice, StringComparison.Ordinal)) return true;
            return false;
        }

        private static void InsertConversationTurnFts(ReignDbConnection connection, string turnId, string text, string speaker, string participants, string channel)
        {
            Program.ExecuteSql(connection, "DELETE FROM conversation_turn_fts WHERE turn_id=$id;", new Dictionary<string, object> { ["id"] = turnId });
            Program.ExecuteSql(connection, "INSERT INTO conversation_turn_fts(turn_id,text,speaker,participants,channel) VALUES($id,$text,$speaker,$participants,$channel);",
                new Dictionary<string, object> { ["id"] = turnId, ["text"] = text ?? "", ["speaker"] = speaker ?? "", ["participants"] = participants ?? "", ["channel"] = channel ?? "" });
        }

        internal static void StoreCorrespondenceTurn(string campaignId, string npcId, string playerId, Dictionary<string, object> letter, double worldDay)
        {
            string letterId = ReadFirstString(letter, "letter_id", "letterId");
            string text = ReadFirstString(letter, "body", "text");
            if (string.IsNullOrWhiteSpace(letterId) || string.IsNullOrWhiteSpace(npcId) || string.IsNullOrWhiteSpace(text)) return;
            string sessionId = "mail_" + SafeMemoryKey(letterId);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string senderId = ReadString(letter, "sender_id", "");
            string recipientId = ReadString(letter, "recipient_id", "");
            List<string> participants = Program.MergeStringLists(new[] { senderId, recipientId, npcId, playerId }, new List<string>());
            using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
            {
                using var transaction = connection.BeginTransaction();
                LockConversationSourcePublication(connection, sessionId);
                Program.ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_sessions(
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
                transaction.Commit();
            }
        }
    }
}
