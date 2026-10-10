using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string ConversationInputSchema = "reign-conversation-input-v1";

        private static AcceptedPlayerInputSource CapturePlayerInput(Dictionary<string, object> payload)
        {
            if (payload == null) return null;
            if (!PlayerInputSources.TryGetValue(payload, out var source))
            {
                source = new AcceptedPlayerInputSource { Original = ReadFirstString(payload, "playerText", "text", "message") };
                PlayerInputSources.Add(payload, source);
            }
            return source;
        }

        private static string OriginalPlayerInput(Dictionary<string, object> payload) =>
            CapturePlayerInput(payload)?.Original ?? "";

        private static void EnsureConversationInputSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_input_receipts (
input_id TEXT PRIMARY KEY, timeline_id TEXT NOT NULL, session_id TEXT NOT NULL,
player_id TEXT NOT NULL, source_turn_id TEXT NOT NULL, source_hash TEXT NOT NULL,
restore_generation TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'received',
projection_json TEXT NOT NULL DEFAULT '{}', response_json TEXT NOT NULL DEFAULT '{}',
created_ts BIGINT NOT NULL, updated_ts BIGINT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_conversation_input_session ON conversation_input_receipts(session_id,created_ts);");
        }

        // Admission is a separate, durable transaction BEFORE interpretation or generation.
        // Acceptance may roll back without erasing what the player actually submitted.
        private static Dictionary<string, object> AdmitConversationInput(string campaign,
            Dictionary<string, object> payload, string generation, string observer = "")
        {
            var source = CapturePlayerInput(payload);
            if (source == null || string.IsNullOrWhiteSpace(source.Original))
                return TestDict("status", "no_player_contribution", "persisted", false);
            string player = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            if (player.Length == 0) throw new InvalidOperationException("The original message requires a player identity before generation.");
            string contribution = FirstNonEmpty(ReadFirstString(payload, "contributionId", "sceneTurnId", "turnId", "requestId"), EnsureCorrelationId(payload));
            string session = ReadFirstString(payload, "conversationSessionId", "sessionId", "conversationId");
            if (session.Length == 0)
            {
                // Admission never closes an unrelated conversation or runs its summary.
                session = "input_session_" + PromptHash(campaign + "|" + ContinuityTimeline(payload) + "|" + player + "|" + observer + "|" + contribution);
                payload["conversationSessionId"] = session;
            }
            // A group submission can be accepted into several observer sessions.
            // Its original and interpretation belong to the contribution, not a speaker.
            string inputId = "input_" + PromptHash(campaign + "|" + ContinuityTimeline(payload) + "|" + player + "|" + contribution);
            string sourceId = "input_source_" + inputId.Substring(6);
            string hash = ConversationExactTextHash(source.Original);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var connection = OpenCampaignConnection(campaign);
            EnsureConversationInputSchema(connection);
            using var transaction = connection.BeginTransaction();
            LockConversationSourcePublication(connection, "input:" + inputId);
            string currentGeneration = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
            if (generation.Length > 0 && currentGeneration != generation)
                throw new InvalidOperationException("The campaign changed before the original message could be saved.");
            ExecuteSql(connection, @"INSERT INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,location_id,participants_json,payload_json)
VALUES($session,$campaign,$npc,$player,$channel,'open',$now,$now,$day,$day,$location,$audience,$payload) ON CONFLICT(session_id) DO NOTHING;",
                TestDict("session", session, "campaign", campaign, "npc", observer, "player", player,
                    "channel", ReadString(payload, "channel", "in_person"), "now", now, "day", ReadDouble(payload, "worldDay", 0d),
                    "location", ReadFirstString(payload, "locationId", "currentSettlementId"),
                    "audience", Json.Serialize(MergeStringLists(ReadStringList(payload, "participants"), new[] { player, observer })),
                    "payload", SerializeConversationTurnStoragePayload(payload)));
            var sessionRow = QuerySql(connection, "SELECT player_id,payload_json FROM conversation_sessions WHERE session_id=$session;", TestDict("session", session)).Single();
            string sessionTimeline = ReadString(TryParseJsonObject(ReadString(sessionRow, "payload_json", "{}")), "timelineId", "");
            if (ReadString(sessionRow, "player_id", "") != player || sessionTimeline.Length > 0 && sessionTimeline != ContinuityTimeline(payload))
                throw new InvalidOperationException("The contribution belongs to a different player or timeline.");
            var existing = QuerySql(connection, "SELECT * FROM conversation_input_receipts WHERE input_id=$id;", TestDict("id", inputId)).SingleOrDefault();
            if (existing != null && (ReadString(existing, "source_hash", "") != hash
                || ReadString(existing, "restore_generation", "") != currentGeneration))
                throw new InvalidOperationException("This contribution ID belongs to different text or a previous save. The original was preserved.");
            source.SourceId = sourceId;
            source.InputId = inputId;
            source.Generation = currentGeneration;
            PreservePlayerInputSource(connection, payload, session, contribution, contribution, player,
                ReadString(payload, "playerName", "Player"), ReadDouble(payload, "worldDay", 0d), now,
                ReadFirstString(payload, "locationId", "currentSettlementId", "settlementId"));
            ExecuteSql(connection, @"INSERT INTO conversation_input_receipts
(input_id,timeline_id,session_id,player_id,source_turn_id,source_hash,restore_generation,status,created_ts,updated_ts)
VALUES($id,$timeline,$session,$player,$source,$hash,$generation,'received',$now,$now) ON CONFLICT(input_id) DO NOTHING;",
                TestDict("id", inputId, "timeline", ContinuityTimeline(payload), "session", session, "player", player,
                    "source", sourceId, "hash", hash, "generation", currentGeneration, "now", now));
            var original = QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id=$id AND status='source_only';",
                TestDict("id", sourceId)).SingleOrDefault();
            if (original == null || ReadString(original, "text", "") != source.Original)
                throw new InvalidOperationException("The saved original message failed its exact-content check.");
            transaction.Commit();
            source.Receipt = TestDict("schema", ConversationInputSchema, "contributionId", contribution,
                "inputId", inputId, "sourceId", sourceId, "sourceHash", hash, "persisted", true,
                "status", ReadString(existing, "status", "received"));
            payload["inputSourceId"] = sourceId;
            payload["contributionId"] = contribution;
            payload["sceneTurnId"] = contribution;
            // Only references, never the unprojected original, enter writer-bound dictionaries.
            payload["inputReceipt"] = source.Receipt;
            return source.Receipt;
        }

        private static string ConversationExactTextHash(string text)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))).Replace("-", "").ToLowerInvariant();
        }

        private static Dictionary<string, object> ReadConversationInputReplay(string campaign,
            Dictionary<string, object> payload, string observer)
        {
            var source = CapturePlayerInput(payload);
            if (source == null || source.InputId.Length == 0 || observer.Length == 0) return null;
            using var connection = OpenCampaignConnection(campaign);
            EnsureConversationInputSchema(connection);
            if (ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "") != source.Generation)
                throw new InvalidOperationException("The save changed before this reply could be replayed.");
            var row = QuerySql(connection, "SELECT response_json,restore_generation FROM conversation_input_receipts WHERE input_id=$id;",
                TestDict("id", source.InputId)).SingleOrDefault();
            if (ReadString(row, "restore_generation", "") != source.Generation) return null;
            var responses = TryParseJsonObject(ReadString(row, "response_json", "{}"));
            var response = ReadDictionary(responses, observer);
            if (response == null || !ReadBool(response, "ok", false)) return null;
            response = CloneDictionary(response);
            response["idempotentReplay"] = true;
            response["inputReceipt"] = source.Receipt;
            if (payload.TryGetValue("conversationGrounding", out var grounding)) response["conversationGrounding"] = grounding;
            return response;
        }

        private static void CompleteConversationInputDelivery(string campaign, Dictionary<string, object> payload,
            string observer, Dictionary<string, object> response)
        {
            var source = CapturePlayerInput(payload);
            if (source == null || source.InputId.Length == 0 || response == null) return;
            response["inputReceipt"] = source.Receipt;
            if (payload.TryGetValue("conversationGrounding", out var grounding)) response["conversationGrounding"] = grounding;
            using var connection = OpenCampaignConnection(campaign);
            using var transaction = connection.BeginTransaction();
            LockConversationSourcePublication(connection, "input:" + source.InputId);
            string currentGeneration = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
            var row = QuerySql(connection, "SELECT response_json,restore_generation FROM conversation_input_receipts WHERE input_id=$id;",
                TestDict("id", source.InputId)).SingleOrDefault();
            if (ReadString(row, "restore_generation", "") != source.Generation || currentGeneration != source.Generation)
                throw new InvalidOperationException("The save changed before the message delivery receipt could be committed.");
            var responses = TryParseJsonObject(ReadString(row, "response_json", "{}"));
            if (ReadBool(response, "ok", false) && observer.Length > 0)
            {
                var replay = CloneDictionary(response);
                // Full provider drafts/prompts have their own private diagnostics store.
                foreach (string key in new[] { "llm", "prompt", "messages", "promptEnvelope" }) replay.Remove(key);
                responses[observer] = replay;
            }
            ExecuteSql(connection, @"UPDATE conversation_input_receipts SET response_json=$responses,status=$status,updated_ts=$now
WHERE input_id=$id AND restore_generation=$generation;",
                TestDict("responses", Json.Serialize(responses), "status", responses.Count > 0 ? "answered" : "unanswered",
                    "now", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "id", source.InputId, "generation", source.Generation));
            transaction.Commit();
        }

        private static void BindAcceptedInputSource(ReignDbConnection connection, Dictionary<string, object> payload,
            string session, string exchange, IEnumerable<string> turnIds)
        {
            var captured = CapturePlayerInput(payload);
            if (captured == null || captured.SourceId.Length == 0) return;
            foreach (string id in turnIds ?? Enumerable.Empty<string>())
            {
                var row = QuerySql(connection, "SELECT payload_json FROM conversation_turns WHERE turn_id=$id;", TestDict("id", id)).SingleOrDefault();
                if (row == null) continue;
                var metadata = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                metadata["inputSourceId"] = captured.SourceId;
                metadata["contributionId"] = ReadString(payload, "contributionId", "");
                if (ReadDictionary(payload, "conversationGrounding") is { } grounding)
                    // Public/whisper projections may share this source. Full audit
                    // quotations remain in the author receipt, never public metadata.
                    metadata["conversationGrounding"] = TestDict("schema", ReadString(grounding, "schema", ""),
                        "status", ReadString(grounding, "status", ""), "replyHash", ReadString(grounding, "replyHash", ""),
                        "originalSourceId", captured.SourceId,
                        "issueTypes", ReadDictionaryList(grounding, "issues").Select(i => ReadString(i, "type", "")).Distinct().ToList());
                ExecuteSql(connection, "UPDATE conversation_turns SET payload_json=$json WHERE turn_id=$id;",
                    TestDict("json", Json.Serialize(metadata), "id", id));
            }
        }

        private static void SaveConversationInputAcceptance(ReignDbConnection connection, AcceptedConversationUnit unit,
            Dictionary<string, object> exchange, Dictionary<string, object> accepted)
        {
            if (!PlayerInputSources.TryGetValue(unit.Payload, out var source) || source.InputId.Length == 0) return;
            // This minimal replay commits with the accepted source. If the process dies
            // before native-action publication or HTTP delivery, never regenerate the
            // already accepted NPC turn or claim that the later action batch executed.
            var replay = TestDict("ok", true, "reply", unit.Reply, "heroStringId", unit.Hero,
                "emotion", ReadString(unit.Parsed, "emotion", ""), "intent", ReadString(unit.Parsed, "intent", ""),
                "repairStatus", ReadDictionary(unit.Parsed, "repairStatus"), "inputReceipt", source.Receipt,
                "conversationExchange", TestDict("sessionId", ReadString(exchange, "sessionId", ""),
                    "exchangeId", ReadString(exchange, "exchangeId", ""), "turnIds", ReadStringList(exchange, "turnIds"), "acceptedUnit", accepted),
                "deliveryRecovery", TestDict("status", "accepted_response_recovered", "nativeActionPublication", "not_confirmed"));
            ExecuteSql(connection, @"UPDATE conversation_input_receipts SET
response_json=((response_json::jsonb)||jsonb_build_object(CAST($observer AS text),CAST($response AS jsonb)))::text,
status='accepted',updated_ts=$now WHERE input_id=$id AND restore_generation=$generation;",
                TestDict("observer", unit.Hero, "response", Json.Serialize(replay), "now", unit.Timestamp, "id", source.InputId, "generation", source.Generation));
        }
    }
}
