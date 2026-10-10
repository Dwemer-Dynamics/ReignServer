using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // Player-facing history only. Never call this from NPC recall or prompt construction.
        private static List<Dictionary<string, object>> ReadOriginalConversationHistory(string campaign,
            Dictionary<string, object> payload, string npc, string session, int limit,
            List<Dictionary<string, object>> legacy)
        {
            string player = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            string timeline = ContinuityTimeline(payload);
            if (player.Length == 0) return legacy;
            using var connection = OpenCampaignConnection(campaign);
            var rows = QuerySql(connection, @"SELECT t.*,s.npc_id,s.player_id,s.payload_json AS session_payload
FROM conversation_turns t JOIN conversation_sessions s ON s.session_id=t.session_id
WHERE s.player_id=$player AND ($session='' OR t.session_id=$session)
AND ($npc='' OR s.npc_id=$npc OR EXISTS(SELECT 1 FROM conversation_turns n
 WHERE n.session_id=t.session_id AND n.speaker_id=$npc AND n.role='npc'))
AND t.status IN ('active','source_only') ORDER BY t.ts DESC,t.session_id DESC,t.turn_order DESC,t.turn_id DESC LIMIT $limit;",
                TestDict("player", player, "session", session, "npc", npc, "limit", Math.Max(1000, Math.Min(10000, limit * 8))));
            bool official = ReadString(payload, "conversationMode", "") == "ambassador_official";
            string posting = ReadString(payload, "postingId", "");
            var historyAccess = new KnowledgeAccessContext { NpcId = player, TimelineId = timeline,
                WorldDay = ReadDouble(payload, "worldDay", double.MaxValue), AsOfAcceptedSequence = long.MaxValue, AsOfTimestamp = long.MaxValue };
            rows = rows.Where(row => {
                var meta = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                var sessionMeta = TryParseJsonObject(ReadString(row, "session_payload", "{}"));
                string rowTimeline = FirstNonEmpty(ReadString(meta, "timelineId", ""), ReadString(sessionMeta, "timelineId", ""));
                var scoped = CloneDictionary(row); scoped["timeline_id"] = rowTimeline;
                return MemoryTimeVisible(scoped, historyAccess, connection) && (!official || ReadString(row, "channel", "") == "ambassador_official"
                    || ReadString(sessionMeta, "channel", "") == "ambassador_official")
                    && (!official || posting.Length == 0 || FirstNonEmpty(ReadString(meta, "postingId", ""), ReadString(sessionMeta, "postingId", "")) == posting);
            }).ToList();
            var sources = rows.Where(row => ReadString(row, "status", "") == "source_only"
                && ReadString(row, "speaker_id", "") == player).ToDictionary(row => ReadString(row, "turn_id", ""));
            // A group's source may live in another observer session, or precede the
            // paginated window. Resolve exact references independently of that window.
            var sourceIds = rows.Where(r => ReadString(r, "role", "") == "player" && ReadString(r, "speaker_id", "") == player)
                .Select(r => ReadString(TryParseJsonObject(ReadString(r, "payload_json", "{}")), "inputSourceId", ""))
                .Where(id => id.Length > 0 && !sources.ContainsKey(id)).Distinct().ToList();
            foreach (var batch in sourceIds.Chunk(256))
            {
                var args = TestDict("player", player);
                var parameters = batch.Select((id, index) => { args["source" + index] = id; return "$source" + index; }).ToList();
                foreach (var source in QuerySql(connection, "SELECT * FROM conversation_turns WHERE status='source_only' AND speaker_id=$player AND turn_id IN ("
                    + string.Join(",", parameters) + ");", args).Where(r => MemoryTimeVisible(r, historyAccess, connection)))
                    sources[ReadString(source, "turn_id", "")] = source;
            }
            // Recovery links are stored on private originals so inherited accepted
            // rows stay byte-identical. Resolve these links outside the page as well.
            var recoveredIds = rows.Where(r => ReadString(r, "role", "") == "player" && ReadString(r, "status", "") == "active")
                .Select(r => ReadString(r, "turn_id", "")).Where(id => id.Length > 0).Distinct();
            foreach (var batch in recoveredIds.Chunk(256))
            {
                var args = TestDict("player", player);
                var parameters = batch.Select((id, index) => { args["turn" + index] = id; return "$turn" + index; }).ToList();
                foreach (var source in QuerySql(connection, "SELECT * FROM conversation_turns WHERE status='source_only' AND speaker_id=$player"
                    + " AND payload_json::jsonb->'originalRecovery'->>'acceptedTurnId' IN (" + string.Join(",", parameters) + ");", args)
                    .Where(r => MemoryTimeVisible(r, historyAccess, connection)))
                    sources[ReadString(source, "turn_id", "")] = source;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var lines = new List<Dictionary<string, object>>();
            foreach (var row in rows.Where(r => ReadString(r, "status", "") == "active")
                .OrderBy(r => ReadLong(r, "ts", 0)).ThenBy(r => ReadString(r, "session_id", ""))
                .ThenBy(r => ReadInt(r, "turn_order", 0)).ThenBy(r => ReadString(r, "turn_id", "")))
            {
                string role = ReadString(row, "role", ""), text = ReadString(row, "text", "");
                if (role != "player" && role != "npc") continue;
                var meta = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                string sourceId = ReadString(meta, "inputSourceId", "");
                Dictionary<string, object> original = null;
                if (role == "player" && ReadString(row, "speaker_id", "") == player)
                {
                    if (sourceId.Length > 0) sources.TryGetValue(sourceId, out original);
                    original ??= FindRecoveredOriginal(row, sources.Values);
                    if (original != null) sourceId = ReadString(original, "turn_id", "");
                    if (original == null && sourceId.Length == 0)
                    {
                        var candidates = sources.Values.Where(s => ReadString(s, "session_id", "") == ReadString(row, "session_id", "")
                            && ReadString(s, "exchange_id", "") == ReadString(row, "exchange_id", "") && RecoveredOriginalLink(s) == null).ToList();
                        if (candidates.Count == 1) { original = candidates[0]; sourceId = ReadString(original, "turn_id", ""); }
                    }
                    if (original != null)
                    {
                        if (!seen.Add(sourceId)) continue; // public/private projections share one original
                        string raw = ReadString(original, "text", "");
                        OriginalConversationSourceIntegrity(original);
                        text = raw;
                    }
                }
                var line = TestDict("speaker", ReadString(row, "speaker_name", ""), "speakerHeroStringId", ReadString(row, "speaker_id", ""),
                    "role", role, "text", text, "channel", ReadString(row, "channel", ""), "worldDay", ReadDouble(row, "world_day", 0d),
                    "ts", ReadLong(row, "ts", 0), "turnId", ReadString(row, "turn_id", ""), "sessionId", ReadString(row, "session_id", ""),
                    "exchangeId", ReadString(row, "exchange_id", ""), "contributionId", ReadString(meta, "contributionId", ""),
                    "sourceIntegrity", original != null ? OriginalConversationSourceIntegrity(original) : role == "player" ? "original_unavailable" : "accepted_reply",
                    "inputSourceId", original != null ? sourceId : "");
                lines.Add(line);
            }
            // Admission survives writer failures. A pending source is displayed as submitted,
            // never as proof that an NPC heard it or that any game effect occurred.
            foreach (var source in sources.Values.Where(s => !seen.Contains(ReadString(s, "turn_id", "")) && RecoveredOriginalLink(s) == null))
                lines.Add(TestDict("speaker", ReadString(source, "speaker_name", "Player"), "role", "player",
                    "speakerHeroStringId", player, "text", ReadString(source, "text", ""), "ts", ReadLong(source, "ts", 0),
                    "worldDay", ReadDouble(source, "world_day", 0d), "sessionId", ReadString(source, "session_id", ""),
                    "exchangeId", ReadString(source, "exchange_id", ""), "contributionId", ReadString(source, "event_id", ""),
                    "inputSourceId", ReadString(source, "turn_id", ""), "sourceIntegrity", OriginalConversationSourceIntegrity(source), "deliveryStatus", "submitted"));
            // Preserve historical display records that predate canonical persistence.
            // They remain explicitly unverified and are never an NPC retrieval source.
            foreach (var old in legacy ?? new List<Dictionary<string, object>>())
            {
                if (ReadString(old, "timelineId", timeline) != timeline) continue;
                string exchange = ReadFirstString(old, "exchangeId", "turnId", "eventId"), role = ReadString(old, "role", "");
                if (lines.Any(line => ReadString(line, "role", "") == role &&
                    (exchange.Length > 0 && ReadString(line, "exchangeId", "") == exchange
                    || ReadString(line, "text", "") == ReadString(old, "text", "")))) continue;
                if (ReadDouble(old, "worldDay", 0d) > historyAccess.WorldDay) continue;
                var retained = CloneDictionary(old); retained["sourceIntegrity"] = "legacy_original_unverified"; lines.Add(retained);
            }
            return lines.OrderBy(line => ReadLong(line, "ts", 0)).TakeLast(limit).ToList();
        }

        private static string OriginalConversationSourceIntegrity(Dictionary<string, object> source)
        {
            string hash = ReadString(TryParseJsonObject(ReadString(source, "payload_json", "{}")), "exactTextHash", "");
            if (hash.Length == 0) return "original_hash_unavailable";
            if (hash != ConversationExactTextHash(ReadString(source, "text", "")))
                throw new InvalidOperationException("An original conversation source failed its integrity check.");
            return "original_verified";
        }
    }
}
