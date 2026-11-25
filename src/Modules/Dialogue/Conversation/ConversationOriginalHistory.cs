using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        // Player-facing history only. Never call this from NPC recall or prompt construction.
        internal static List<Dictionary<string, object>> ReadOriginalConversationHistory(string campaign,
            Dictionary<string, object> payload, string npc, string session, int limit,
            List<Dictionary<string, object>> legacy)
        {
            string player = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            if (player.Length == 0) return legacy;
            return MergeOriginalConversationHistory(campaign, payload, npc, session, limit, player, legacy);
        }

        internal static List<Dictionary<string, object>> ReadOriginalConversationHistoryFromFile(string campaign,
            Dictionary<string, object> payload, string npc, string session, int limit, string path,
            Func<Dictionary<string, object>, bool> legacyFilter = null)
        {
            var legacy = Program.EnumerateJsonLinesFromPath(path);
            if (legacyFilter != null) legacy = legacy.Where(legacyFilter);
            string player = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            if (player.Length == 0) return legacy.ToList();

            bool schemaMayNeedInitialization;
            lock (Program.CampaignSchemaLock)
                schemaMayNeedInitialization = !Program.CampaignSchemaReady.Contains(ReignPostgreSqlStorage.CampaignSchemaName(campaign));
            // Before a cold SQL open can initialize campaign state, validate the
            // saved legacy stream as the original path did. Keep no row payloads
            // from this pass, and release the writer lock before opening SQL.
            if (schemaMayNeedInitialization) _ = legacy.LongCount();
            string strictPath = legacyFilter == null && !CampaignFileService.IsDialogueHistoryPath(path) ? path : null;
            return MergeOriginalConversationHistory(campaign, payload, npc, session, limit, player, legacy, strictPath);
        }

        private static List<Dictionary<string, object>> ReadAuthoritativeConversationHistory(string campaign,
            Dictionary<string, object> payload, string npc, string session, int limit, string player,
            KnowledgeAccessContext historyAccess)
        {
            using var connection = ReignDatabase.OpenCampaignConnection(campaign);
            var rows = DialogueStore.SelectConversationTurns26(connection, TestDict("player", player, "session", session, "npc", npc, "limit", Math.Max(1000, Math.Min(10000, limit * 8))));
            bool official = ReadString(payload, "conversationMode", "") == "ambassador_official";
            string posting = ReadString(payload, "postingId", "");
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
                foreach (var source in DialogueStore.SelectConversationTurns27(connection, string.Join(",", parameters),args).Where(r => MemoryTimeVisible(r, historyAccess, connection)))
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
                foreach (var source in DialogueStore.SelectConversationTurns28(connection, string.Join(",", parameters),args)
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
            return lines;
        }

        private static List<Dictionary<string, object>> MergeOriginalConversationHistory(string campaign,
            Dictionary<string, object> payload, string npc, string session, int limit, string player,
            IEnumerable<Dictionary<string, object>> legacy, string strictPath = null)
        {
            string timeline = ContinuityTimeline(payload);
            var access = new KnowledgeAccessContext { NpcId = player, TimelineId = timeline,
                WorldDay = ReadDouble(payload, "worldDay", double.MaxValue), AsOfAcceptedSequence = long.MaxValue, AsOfTimestamp = long.MaxValue };
            var authoritative = ReadAuthoritativeConversationHistory(campaign, payload, npc, session, limit, player, access);
            if (strictPath != null && StrictJsonlHistoryIndex.TryReadAuthenticatedWindow(
                strictPath, timeline, access.WorldDay, limit, authoritative, out var indexed))
                return indexed;

            var selection = new OriginalHistorySelection<Dictionary<string, object>>(timeline, access.WorldDay, limit);
            for (int index = 0; index < authoritative.Count; index++)
                selection.AddAuthoritative(OriginalHistorySeed.FromRow(authoritative[index]), index);
            foreach (var old in legacy ?? Enumerable.Empty<Dictionary<string, object>>())
                selection.AddLegacy(old, old);
            return selection.Complete().Select(slot => slot.IsLegacy
                ? MarkLegacyOriginal(slot.Legacy) : authoritative[slot.AuthoritativeIndex]).ToList();
        }

        internal readonly record struct OriginalHistorySeed(string Role, string Text, string Exchange, long Timestamp)
        {
            internal static OriginalHistorySeed FromRow(Dictionary<string, object> row) => new(
                ReadString(row, "role", ""), ReadString(row, "text", ""), ReadString(row, "exchangeId", ""), ReadLong(row, "ts", 0));
        }

        internal readonly record struct OriginalHistorySlot<T>(int AuthoritativeIndex, T Legacy)
        {
            internal bool IsLegacy => AuthoritativeIndex < 0;
        }

        internal sealed class OriginalHistorySelection<T>
        {
            private readonly string timeline;
            private readonly double worldDay;
            private readonly int limit;
            private readonly PriorityQueue<OriginalHistorySlot<T>, (long Timestamp, long Ordinal)> selected = new();
            private readonly HashSet<(string Role, string Text)> textKeys = new();
            private readonly HashSet<(string Role, string Exchange)> exchangeKeys = new();
            private long ordinal;

            internal OriginalHistorySelection(string timeline, double worldDay, int limit)
            {
                this.timeline = timeline;
                this.worldDay = worldDay;
                this.limit = limit;
            }

            internal void AddAuthoritative(OriginalHistorySeed seed, int slot)
                => Select(seed, new OriginalHistorySlot<T>(slot, default));

            internal void AddLegacy(Dictionary<string, object> old, T position)
            {
                if (ReadString(old, "timelineId", timeline) != timeline) return;
                string exchange = ReadFirstString(old, "exchangeId", "turnId", "eventId"), role = ReadString(old, "role", "");
                if (exchange.Length > 0 && exchangeKeys.Contains((role, exchange))) return;
                string text = ReadString(old, "text", "");
                if (textKeys.Contains((role, text)) || ReadDouble(old, "worldDay", 0d) > worldDay) return;
                // Remember only the stored exchangeId. A fallback alias is an
                // incoming lookup and does not own a key for later rows.
                Select(new OriginalHistorySeed(role, text, ReadString(old, "exchangeId", ""), ReadLong(old, "ts", 0)),
                    new OriginalHistorySlot<T>(-1, position));
            }

            private void Select(OriginalHistorySeed seed, OriginalHistorySlot<T> slot)
            {
                // All accepted rows own exact keys, including rows outside the window.
                textKeys.Add((seed.Role, seed.Text));
                if (seed.Exchange.Length > 0) exchangeKeys.Add((seed.Role, seed.Exchange));
                // Later entries win a tied cutoff, matching stable OrderBy(...).TakeLast(limit).
                var priority = (seed.Timestamp, ordinal++);
                if (selected.Count < limit) selected.Enqueue(slot, priority);
                else if (limit > 0) selected.EnqueueDequeue(slot, priority);
            }

            internal IEnumerable<OriginalHistorySlot<T>> Complete()
            {
                while (selected.TryDequeue(out var slot, out _)) yield return slot;
            }
        }

        internal static Dictionary<string, object> MarkLegacyOriginal(Dictionary<string, object> row)
        {
            var retained = CloneDictionary(row);
            retained["sourceIntegrity"] = "legacy_original_unverified";
            return retained;
        }

        internal static string OriginalConversationSourceIntegrity(Dictionary<string, object> source)
        {
            string hash = ReadString(TryParseJsonObject(ReadString(source, "payload_json", "{}")), "exactTextHash", "");
            if (hash.Length == 0) return "original_hash_unavailable";
            if (hash != ConversationExactTextHash(ReadString(source, "text", "")))
                throw new InvalidOperationException("An original conversation source failed its integrity check.");
            return "original_verified";
        }
    }
}
