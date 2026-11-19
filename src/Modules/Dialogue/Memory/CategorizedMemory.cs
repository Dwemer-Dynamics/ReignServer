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
            DialogueSchema.EnsureColumnEventsEventCategory(connection);
            DialogueSchema.EnsureColumnEventsEventSubtype(connection);
            DialogueSchema.EnsureColumnMemoriesMemoryDomain(connection);
            DialogueSchema.EnsureColumnSummariesMemoryLane(connection);

            DialogueSchema.CreateKnowledgeReceipts(connection);
            DialogueSchema.CreateIndexKnowledgeReceiptsNpc(connection);
            DialogueSchema.CreateIndexKnowledgeReceiptsEvent(connection);

            DialogueSchema.CreateConversationSessions(connection);
            DialogueSchema.CreateIndexConversationSessionsNpc(connection);

            DialogueSchema.CreateConversationTurns(connection);
            DialogueSchema.CreateIndexConversationTurnsSession(connection);
            DialogueSchema.CreateIndexConversationTurnsTs(connection);
            DialogueSchema.CreateIndexConversationTurnsSpeakerTs(connection);
            EnsureChatViewSchema(connection);
            DialogueSchema.CreateConversationTurnFts(connection);

            DialogueSchema.CreateMemorySources(connection);
            DialogueSchema.CreateIndexMemorySourcesSource(connection);
        }

        internal static void RunCategorizedMemoryMigration(ReignDbConnection connection, string campaignId)
        {
            Dictionary<string, object> applied = DialogueStore.SelectSchemaMeta2(connection).FirstOrDefault();
            if (string.Equals(ReadString(applied, "value", ""), "complete", StringComparison.OrdinalIgnoreCase))
            {
                RunMemoryDomainV2Migration(connection);
                RunConversationTurnPayloadV2Migration(connection);
                RunPlayerLieDetectionRemovalMigration(connection);
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (Dictionary<string, object> row in DialogueStore.SelectEvents(connection))
            {
                Dictionary<string, object> payload = TryParseJsonObject(ReadString(row, "payload_json", "")) ?? new Dictionary<string, object>();
                string eventType = ReadString(row, "event_type", "unknown");
                string summary = ReadString(row, "summary", "");
                DialogueStore.UpdateEvents(connection, new Dictionary<string, object>
                    {
                        ["category"] = ClassifyEventCategory(eventType, payload, summary),
                        ["subtype"] = ClassifyEventSubtype(eventType, summary),
                        ["id"] = ReadString(row, "event_id", "")
                    });
            }

            foreach (Dictionary<string, object> row in DialogueStore.SelectMemories4(connection))
            {
                string domain = ClassifyMemoryDomain(row);
                DialogueStore.UpdateMemories(connection, new Dictionary<string, object> { ["domain"] = domain, ["id"] = ReadString(row, "memory_id", "") });
            }

            MigrateLegacyPublicProjections(connection, now);
            ImportLegacyConversationHistory(connection, campaignId);
            RebuildCategorizedSearchIndexes(connection);
            DialogueStore.UpsertSchemaMeta2(connection);
            RunMemoryDomainV2Migration(connection);
            RunConversationTurnPayloadV2Migration(connection);
            RunPlayerLieDetectionRemovalMigration(connection);
        }

        private static void RunMemoryDomainV2Migration(ReignDbConnection connection)
        {
            Dictionary<string, object> applied = DialogueStore.SelectSchemaMeta3(connection).FirstOrDefault();
            if (string.Equals(ReadString(applied, "value", ""), "complete", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (Dictionary<string, object> row in DialogueStore.SelectMemories4(connection))
            {
                Dictionary<string, object> payload = TryParseJsonObject(ReadString(row, "payload_json", "")) ?? new Dictionary<string, object>();
                string eventType = ReadFirstString(payload, "eventType", "event_type", "type");
                string eventCategory = FirstNonEmpty(ReadFirstString(payload, "eventCategory", "event_category"),
                    ClassifyEventCategory(eventType, payload, ReadString(row, "summary", "")));
                string domain = Program.MemoryDomainForWorldEventProjection(eventCategory, eventType,
                    ReadString(row, "memory_type", ""), ReadString(row, "summary", ""),
                    Program.TextListFromJson(ReadString(row, "tags_json", "[]")), ReadString(row, "source", ""));
                DialogueStore.UpdateMemories(connection, new Dictionary<string, object> { ["domain"] = domain, ["id"] = ReadString(row, "memory_id", "") });
            }
            DialogueStore.UpsertSchemaMeta3(connection);
        }

        private static void RunConversationTurnPayloadV2Migration(ReignDbConnection connection)
        {
            Dictionary<string, object> applied = DialogueStore.SelectSchemaMeta4(connection).FirstOrDefault();
            if (string.Equals(ReadString(applied, "value", ""), "complete", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Dictionary<string, object> result = CompactConversationTurnPayloadRows(connection);
            DialogueStore.UpsertSchemaMeta4(connection);
            if (ReadLong(result, "bytesRemoved", 0L) > 0)
            {
                DialogueStore.UpsertSchemaMeta5(connection);
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
                command.CommandText = DialogueStore.SelectConversationTurnsSql2;
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
                DialogueStore.Begin2(connection);
                try
                {
                    using (ReignDbCommand update = connection.CreateCommand())
                    {
                        update.CommandText = DialogueStore.UpdateConversationTurnsSql;
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
                    DialogueStore.Commit(connection);
                }
                catch
                {
                    try { DialogueStore.Rollback(connection); } catch { }
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
            List<Dictionary<string, object>> groups = DialogueStore.SelectMemories5(connection);
            foreach (Dictionary<string, object> group in groups)
            {
                string eventId = ReadString(group, "event_id", "");
                List<Dictionary<string, object>> rows = DialogueStore.SelectMemories6(connection, new Dictionary<string, object> { ["event"] = eventId });
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
                DialogueStore.UpdateMemories3(connection, new Dictionary<string, object> { ["id"] = canonicalId });

                HashSet<string> direct = new HashSet<string>(
                    rows.SelectMany(row => Program.TextListFromJson(ReadString(row, "participants_json", "[]"))
                        .Concat(Program.TextListFromJson(ReadString(row, "witnesses_json", "[]")))), StringComparer.OrdinalIgnoreCase);
                foreach (Dictionary<string, object> row in rows)
                {
                    string memoryId = ReadString(row, "memory_id", "");
                    string ownerId = ReadString(row, "owner_id", "");
                    if (!string.IsNullOrWhiteSpace(ownerId) && direct.Contains(ownerId))
                    {
                        DialogueStore.UpdateMemories4(connection, new Dictionary<string, object> { ["known"] = Json.Serialize(new List<string> { ownerId }), ["id"] = memoryId });
                        UpsertKnowledgeReceipt(connection, eventId, ownerId,
                            Program.TextListFromJson(ReadString(row, "witnesses_json", "[]")).Contains(ownerId, StringComparer.OrdinalIgnoreCase) ? "witness" : "participant",
                            ReadDouble(row, "confidence", 1d), 1d, ReadDouble(row, "world_day", 0d), memoryId, now);
                    }
                    else
                    {
                        DialogueStore.UpdateMemories5(connection, new Dictionary<string, object> { ["id"] = memoryId });
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
                    DialogueStore.InsertOrIgnoreConversationSessions5(connection, new Dictionary<string, object>
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
            DialogueStore.DeleteMemoryFts(connection);
            foreach (Dictionary<string, object> row in DialogueStore.SelectMemories7(connection))
            {
                Program.InsertMemoryFts(connection, ReadString(row, "memory_id", ""), ReadString(row, "summary", ""),
                    Program.TextListFromJson(ReadString(row, "tags_json", "[]")),
                    Program.TextListFromJson(ReadString(row, "about_entities_json", "[]"))
                        .Concat(Program.TextListFromJson(ReadString(row, "participants_json", "[]"))).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            }
            DialogueStore.DeleteConversationTurnFts(connection);
            foreach (Dictionary<string, object> row in DialogueStore.SelectConversationTurns49(connection))
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
            using (var connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                var logical = PrepareLogicalConversation(connection, payload, campaignId, npcId, playerId, id);
                if (!ReadBool(logical, "ok", false) || ReadBool(logical, "idempotent", false)) return logical;
            }
            List<string> interrupted;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                interrupted = DialogueStore.SelectConversationSessions9(connection, new Dictionary<string, object> { ["npc"] = npcId })
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
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                DialogueStore.InsertOrIgnoreConversationSessions6(connection, new Dictionary<string, object>
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
            using (var connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                var session = DialogueStore.SelectConversationSessions10(connection, TestDict("id", sessionId)).FirstOrDefault();
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
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                openSessions = DialogueStore.SelectConversationSessions11(connection)
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
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            using (var closure = connection.BeginTransaction())
            {
                session = DialogueStore.SelectConversationSessions8(connection, new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault();
                if (session == null)
                {
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Conversation session was not found.", ["sessionId"] = sessionId };
                }
                // A late teardown/transition receipt cannot replace an explicit
                // ending and make a finished conversation eligible for resumption.
                string existingReason = ReadString(session, "close_reason", "");
                if (new[] { "closed_by_player", "conversation_ended", "goodbye" }.Contains(existingReason))
                    reason = existingReason;
                turns = DialogueStore.SelectConversationTurns50(connection, new Dictionary<string, object> { ["id"] = sessionId });
                var existingScene=DialogueStore.SelectSummaries4(connection, new Dictionary<string,object>{["id"]=ReadString(session,"scene_summary_id","")}).FirstOrDefault();
                if (existingScene != null && ReadString(TryParseJsonObject(ReadString(existingScene,"payload_json","{}")),"sourceHash","")==MemorySourceHash(turns))
                {
                    if (ReadString(session,"status","")!="closed" && ReadString(session,"status","")!="interrupted")
                        DialogueStore.UpdateConversationSessions4(connection, new Dictionary<string,object>{["status"]=interrupted?"interrupted":"closed",["ts"]=ts,["day"]=worldDay,["reason"]=reason??"",["id"]=sessionId});
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
                DialogueStore.UpdateConversationSessions5(connection, new Dictionary<string, object>
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
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                var job = DialogueStore.SelectMemoryBackgroundJobs(connection, new Dictionary<string, object> { ["id"] = sceneJobId }).Single();
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
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                using var transaction = connection.BeginTransaction();
                LockConversationSourcePublication(connection, sessionId);
                Dictionary<string, object> max = DialogueStore.SelectConversationTurns51(connection, new Dictionary<string, object> { ["session"] = sessionId }).FirstOrDefault();
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
                DialogueStore.UpdateConversationSessions6(connection, new Dictionary<string, object> { ["ts"] = ts, ["day"] = worldDay, ["location"] = locationId, ["id"] = sessionId });
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
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                sessionExists = !string.IsNullOrWhiteSpace(sessionId)
                    && DialogueStore.SelectConversationSessions12(connection, new Dictionary<string, object> { ["id"] = sessionId }).Count > 0;
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
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                using var transaction = connection.BeginTransaction();
                LockConversationSourcePublication(connection, sessionId);
                // Planning uses a conservative local projection and makes no model
                // call. The interpreted writer projection must not collide with it.
                // Keep its bytes, but retire that provisional view atomically when
                // the same original receives its accepted observer projection.
                if (floorProjection && DialogueStore.SelectConversationTurns20(connection, TestDict("id", exchangeId + "_player")).Count > 0)
                {
                    transaction.Commit();
                    return result;
                }
                Dictionary<string, object> session = DialogueStore.SelectConversationSessions13(connection, new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault();
                var sessionParticipants = Program.MergeStringLists(
                    Program.TextListFromJson(ReadString(session, "participants_json", "[]")),
                    participants);

                var existingPlayer = DialogueStore.SelectConversationTurns52(connection, new Dictionary<string, object> { ["turn"] = playerTurnId }).FirstOrDefault();
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
                int nextOrder = ReadInt(DialogueStore.SelectConversationTurns51(connection, new Dictionary<string, object> { ["session"] = sessionId }).FirstOrDefault(), "next_order", 0);
                if (!playerExists)
                {
                    InsertConversationTurn(connection, playerTurnId, sessionId, eventId, nextOrder++, exchangeId,
                        "player", playerId, playerName, playerText, channel, worldDay, ts, locationId, participants, request);
                }
                if (!floorProjection && ReadString(request, "inputSourceId", "").Length > 0)
                {
                    var provisional = DialogueStore.SelectConversationTurns53(connection, TestDict("id", exchangeId + "_floor_player", "session", sessionId, "player", playerId)).SingleOrDefault();
                    if (ReadString(TryParseJsonObject(ReadString(provisional, "payload_json", "{}")), "inputSourceId", "") == ReadString(request, "inputSourceId", ""))
                    {
                        DialogueStore.UpdateConversationTurns3(connection, TestDict("id", exchangeId + "_floor_player"));
                        DialogueStore.DeleteConversationTurnFts2(connection, TestDict("id", exchangeId + "_floor_player"));
                    }
                }
                acceptanceBoundary?.Invoke("after_player_source");
                InsertConversationTurn(connection, npcTurnId, sessionId, eventId, nextOrder, exchangeId,
                    "npc", npcId, npcName, reply, channel, worldDay, ts, locationId, participants, request);
                DialogueStore.UpdateConversationSessions7(connection, new Dictionary<string, object>
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
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
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
            DialogueStore.SelectStatement6(connection, TestDict("key", "conversation_source:" + sessionId));

        private static void LockConversationSourcePublication(ReignDbConnection connection, string sessionId)
        {
            LockConversationSourceSession(connection, sessionId);
            if (!string.IsNullOrWhiteSpace(_playerActionGeneration))
            {
                var current = DialogueStore.SelectMemoryPrecisionState(connection).Single();
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
            DialogueStore.InsertOrIgnoreConversationTurns(connection, new Dictionary<string, object>
                {
                    ["turn"] = turnId, ["session"] = sessionId, ["event"] = eventId ?? "", ["ordinal"] = turnOrder,
                    ["exchange"] = exchangeId ?? "", ["role"] = role ?? "", ["speaker_id"] = speakerId ?? "",
                    ["speaker_name"] = speakerName ?? "", ["text"] = text ?? "", ["channel"] = channel ?? "in_person",
                    ["day"] = worldDay, ["ts"] = ts, ["location"] = locationId ?? "",
                    ["participants"] = Json.Serialize(participants ?? new List<string>()),
                    ["status"] = technicalNotice ? "technical_notice" : "active",
                    ["payload"] = SerializeConversationTurnStoragePayload(payload)
                });
            var stored = DialogueStore.SelectConversationTurns6(connection, TestDict("id", turnId)).Single();
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
            DialogueStore.DeleteConversationTurnFts2(connection, new Dictionary<string, object> { ["id"] = turnId });
            DialogueStore.InsertConversationTurnFts(connection, new Dictionary<string, object> { ["id"] = turnId, ["text"] = text ?? "", ["speaker"] = speaker ?? "", ["participants"] = participants ?? "", ["channel"] = channel ?? "" });
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
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                using var transaction = connection.BeginTransaction();
                LockConversationSourcePublication(connection, sessionId);
                DialogueStore.InsertOrIgnoreConversationSessions7(connection, new Dictionary<string, object>
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
