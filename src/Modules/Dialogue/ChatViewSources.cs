using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string ChatViewVersion = "reign-chat-view-v1";

        private static void EnsureChatViewSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS chat_view_frames (
turn_id TEXT PRIMARY KEY,session_id TEXT NOT NULL,exchange_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
frame_json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS chat_view_requests (
request_id TEXT PRIMARY KEY,session_id TEXT NOT NULL,timeline_id TEXT NOT NULL,player_id TEXT NOT NULL,job_id TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS chat_view_jobs (
job_id TEXT PRIMARY KEY,session_id TEXT NOT NULL,timeline_id TEXT NOT NULL,player_id TEXT NOT NULL,
created_ts INTEGER NOT NULL,payload_json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_chat_view_jobs_session ON chat_view_jobs(session_id,timeline_id,created_ts);");
            EnsureChatViewWardrobeSchema(connection);
        }

        // Called inside the accepted-source transaction. No providers, files, or game effects.
        private static void StoreChatViewFrame(ReignDbConnection connection, string turn, string session,
            string exchange, Dictionary<string, object> request)
        {
            var people = ReadDictionaryList(request, "sceneParticipants");
            if (people.Count == 0) return; // Historical rows without a reliable snapshot cannot invent one.
            var actors = people.Select(p => TestDict("id", CharacterIdFrom(p), "name", ReadString(p, "name", ""),
                "age", p.ContainsKey("age") ? (object)ReadDouble(p, "age", 0) : null,
                "isChild", p.ContainsKey("isChild") ? (object)ReadBool(p, "isChild", false) : null,
                "appearance", ReadDictionary(p, "appearance"),
                "clothing", ReadString(p, "travelClothingDescription", ""),
                "location", ReadString(p, "nativeLocationDescription", ""))).ToList();
            if (actors.Count > 32 || actors.Any(p => ReadString(p, "id", "").Length == 0)) return;
            var expected = ReadStringList(request, "activeHeroIds");
            if (expected.Count == 0)
            {
                string npc = ReadFirstString(request, "speakerHeroStringId", "heroStringId", "npcId");
                if (npc.Length == 0) npc = CharacterIdFrom(ReadDictionary(request, "hero") ?? new Dictionary<string, object>());
                if (npc.Length > 0) expected.Add(npc);
            }
            var frame = TestDict("actors", actors, "expectedNpcIds", expected,
                "scene", ReadString(request, "sceneContext", ""), "locationId", ReadFirstString(request, "locationId", "settlementId"),
                "sceneInterface", ReadString(request, "sceneInterface", ""), "room", ReadString(request, "castleRoomName", ""),
                "wardrobeScene", ChatViewWardrobeSceneKey(request));
            string json = Json.Serialize(frame);
            if (json.Length > 65536) return;
            ExecuteSql(connection, @"INSERT OR IGNORE INTO chat_view_frames(turn_id,session_id,exchange_id,timeline_id,frame_json)
VALUES($turn,$session,$exchange,$timeline,$frame);", TestDict("turn", turn, "session", session,
                "exchange", exchange, "timeline", ContinuityTimeline(request), "frame", json));
        }

        private static Dictionary<string, object> ChatViewScope(ReignDbConnection connection, Dictionary<string, object> payload)
        {
            string session = ReadString(payload, "sessionId", ""), player = ReadString(payload, "playerId", "");
            string timeline = ReadString(payload, "timelineId", "");
            if (session.Length == 0 || player.Length == 0 || timeline.Length == 0)
                throw new InvalidOperationException("A current conversation, player and timeline are required.");
            var scope = QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$session AND player_id=$player;",
                TestDict("session", session, "player", player)).SingleOrDefault();
            if (scope == null) throw new InvalidOperationException("This conversation does not belong to the current player.");
            string channel = ReadString(scope, "channel", "");
            if (channel == "correspondence" || channel == "social_event" || channel == "wilderness_event")
                throw new InvalidOperationException("View is available in individual and party chat.");
            return scope;
        }

        private static List<Dictionary<string, object>> ReadChatViewWindow(ReignDbConnection connection, Dictionary<string, object> payload)
        {
            ChatViewScope(connection, payload);
            string session = ReadString(payload, "sessionId", ""), timeline = ReadString(payload, "timelineId", "");
            string player = ReadString(payload, "playerId", "");
            var access = new KnowledgeAccessContext { NpcId = player, TimelineId = timeline,
                WorldDay = ReadDouble(payload, "worldDay", double.MaxValue), AsOfAcceptedSequence = long.MaxValue, AsOfTimestamp = long.MaxValue };
            // Page by complete exchange identities, never a bounded UI line buffer.
            string through = ReadString(payload, "throughExchangeId", "");
            int boundary = int.MaxValue;
            if (through.Length > 0)
            {
                var edge = QuerySql(connection, "SELECT MAX(turn_order) AS boundary FROM conversation_turns WHERE session_id=$session AND exchange_id=$exchange AND status='active';",
                    TestDict("session", session, "exchange", through)).SingleOrDefault();
                if (edge == null || edge["boundary"] == null || edge["boundary"] == DBNull.Value)
                    throw new InvalidOperationException("The selected conversation boundary is no longer available.");
                boundary = ReadInt(edge, "boundary", -1);
            }
            var groups = QuerySql(connection, @"SELECT exchange_id,MAX(turn_order) AS last_order FROM conversation_turns
WHERE session_id=$session AND status='active' GROUP BY exchange_id HAVING MAX(turn_order)<=$boundary
ORDER BY last_order DESC LIMIT 64;", TestDict("session", session, "boundary", boundary));
            var rows = new List<Dictionary<string, object>>();
            foreach (var group in groups)
            {
                string exchange = ReadString(group, "exchange_id", "");
                var turns = QuerySql(connection, @"SELECT t.*,f.frame_json,f.timeline_id FROM conversation_turns t
LEFT JOIN chat_view_frames f ON f.turn_id=t.turn_id WHERE t.session_id=$session AND t.exchange_id=$exchange
AND t.status='active' ORDER BY t.turn_order;", TestDict("session", session, "exchange", exchange));
                turns = turns.Where(t => ReadString(t, "frame_json", "").Length > 0 && MemoryTimeVisible(t, access, connection)
                    && TextListFromJson(ReadString(t, "participants_json", "[]")).Contains(player, StringComparer.OrdinalIgnoreCase)).ToList();
                var complete = ChatViewCompleteExchange(turns, player);
                if (complete.Count == 0) continue;
                foreach (var line in complete.Where(t => ReadString(t, "role", "") == "player"))
                {
                    var projected = turns.First(t => ReadString(t, "turn_id", "") == ReadString(line, "turnId", ""));
                    var metadata = TryParseJsonObject(ReadString(projected, "payload_json", "{}"));
                    string originalId = ReadString(metadata, "inputSourceId", "");
                    if (originalId.Length == 0) continue;
                    var original = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id AND speaker_id=$player AND status='source_only';",
                        TestDict("id", originalId, "player", player)).SingleOrDefault();
                    if (original == null || !MemoryTimeVisible(original, access, connection)) throw new InvalidDataException("The original player input is unavailable in this timeline.");
                    OriginalConversationSourceIntegrity(original);
                    line["text"] = ReadString(original, "text", "");
                    line["sourceHash"] = PromptHash(ReadString(original, "text", ""));
                }
                rows.InsertRange(0, complete);
                if (rows.Select(t => ReadString(t, "exchangeId", "")).Distinct().Count() == 4) break;
            }
            if (rows.Count == 0) throw new InvalidOperationException("View needs a complete exchange with a retained scene snapshot. Send a message first.");
            if (Json.Serialize(rows).Length > 180000) throw new InvalidOperationException("These four exchanges exceed the View input limit; no text was truncated.");
            return rows;
        }

        private static bool ChatViewJobVisible(ReignDbConnection connection, Dictionary<string, object> job, Dictionary<string, object> request)
        {
            if (job == null || ReadString(job, "playerId", "") != ReadString(request, "playerId", "")) return false;
            var window = ReadDictionaryList(ReadDictionary(job, "identity"), "window");
            string selectedId = ReadString(ReadDictionary(job, "scene"), "selectedTurnId", "");
            if (selectedId.Length == 0) selectedId = ReadString(window.LastOrDefault(), "turnId", "");
            var frozen = window.SingleOrDefault(t => ReadString(t, "turnId", "") == selectedId);
            if (frozen == null || ReadString(frozen, "role", "") != "npc") return false;
            var source = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id AND status='active';", TestDict("id", selectedId)).SingleOrDefault();
            return source != null && PromptHash(ReadString(source, "text", "")) == ReadString(frozen, "sourceHash", "")
                && TextListFromJson(ReadString(source, "participants_json", "[]")).Contains(ReadString(request, "playerId", ""), StringComparer.OrdinalIgnoreCase)
                && MemoryTimeVisible(source, new KnowledgeAccessContext { NpcId = ReadString(request, "playerId", ""),
                    TimelineId = ReadString(request, "timelineId", ""), WorldDay = ReadDouble(request, "worldDay", double.MaxValue),
                    AsOfAcceptedSequence = long.MaxValue, AsOfTimestamp = long.MaxValue }, connection);
        }

        internal static List<Dictionary<string, object>> ChatViewCompleteExchange(List<Dictionary<string, object>> turns, string player)
        {
            var empty = new List<Dictionary<string, object>>();
            var source = turns.FirstOrDefault(t => ReadString(t, "role", "") == "player"
                && ReadString(t, "speaker_id", "") == player && !string.IsNullOrWhiteSpace(ReadString(t, "text", "")));
            var npcs = turns.Where(t => ReadString(t, "role", "") == "npc").ToList();
            if (source == null || npcs.Count == 0) return empty;
            var frame = TryParseJsonObject(ReadString(source, "frame_json", ""));
            if (frame == null || ReadDictionaryList(frame, "actors").Count == 0) return empty;
            var expected = ReadStringList(frame, "expectedNpcIds");
            if (expected.Count == 0 || expected.Any(id => !npcs.Any(t => ReadString(t, "speaker_id", "") == id))) return empty;
            return new[] { source }.Concat(npcs).Select(t => TestDict("turnId", ReadString(t, "turn_id", ""),
                "exchangeId", ReadString(t, "exchange_id", ""), "role", ReadString(t, "role", ""),
                "speakerId", ReadString(t, "speaker_id", ""), "speaker", ReadString(t, "speaker_name", ""),
                "text", ReadString(t, "text", ""), "frame", TryParseJsonObject(ReadString(t, "frame_json", "")) ?? frame,
                "sourceHash", PromptHash(ReadString(t, "text", "")), "turnOrder", ReadInt(t, "turn_order", 0), "worldDay", ReadDouble(t, "world_day", 0))).ToList();
        }
    }
}
