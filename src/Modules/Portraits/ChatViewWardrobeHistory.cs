using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class PortraitsService
    {
        internal const string ViewWardrobeEvidenceOutputContract = "viewWardrobeEvidence: always [] or <=8 {source:player|reply,characterIds:[supplied IDs],quote:exact source words <=400 chars}. Include clothing/dressing and plans, agreements, refusals, corrections or resets from current player text/final reply; retain negation/conditions. No invented garments, equipment/age inference or game effects.";

        internal static void EnsureChatViewWardrobeSchema(ReignDbConnection connection) => PortraitsSchema.CreateChatViewWardrobeEvidence(connection);

        // Use the native conversation boundary, not mutable per-actor prose or
        // hourly caches. Roleplay time jumps/resets remain explicit selector data.
        internal static string ChatViewWardrobeSceneKey(Dictionary<string, object> request) => DialogueService.PromptHash(CanonicalJson(TestDict(
            "location", ReadFirstString(request, "locationId", "settlementId"),
            "room", ReadString(request, "castleRoomName", ""), "interface", ReadString(request, "sceneInterface", ""))));

        internal static string ChatViewWardrobeFrameScope(Dictionary<string, object> frame)
        {
            string frozen = ReadString(frame, "wardrobeScene", "");
            return frozen.Length > 0 ? frozen : ChatViewWardrobeSceneKey(TestDict(
                "locationId", ReadString(frame, "locationId", ""), "castleRoomName", ReadString(frame, "room", ""),
                "sceneInterface", ReadString(frame, "sceneInterface", "")));
        }

        // Read the same original player bytes used by View, never the NPC's observer projection.
        private static string ChatViewWardrobeOriginalText(ReignDbConnection connection, Dictionary<string, object> row,
            Program.KnowledgeAccessContext access = null)
        {
            string text = ReadString(row, "text", "");
            if (ReadString(row, "role", "") != "player") return text;
            var meta = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
            string originalId = ReadString(meta, "inputSourceId", "");
            if (originalId.Length == 0) return text;
            var original = PortraitsStore.SelectConversationTurns(connection, TestDict("id", originalId, "speaker", ReadString(row, "speaker_id", ""))).SingleOrDefault();
            if (original == null || access != null && !DialogueService.MemoryTimeVisible(original, access, connection))
                throw new InvalidDataException("The original wardrobe evidence is unavailable in this timeline.");
            DialogueService.OriginalConversationSourceIntegrity(original);
            return ReadString(original, "text", "");
        }

        internal static Dictionary<string, object> StoreChatViewWardrobeEvidence(ReignDbConnection connection,
            Dictionary<string, object> request, Dictionary<string, object> parsed,
            List<Dictionary<string, object>> rows, string npc)
        {
            var reply = rows.LastOrDefault(r => ReadString(r, "role", "") == "npc" && ReadString(r, "speaker_id", "") == npc);
            if (reply == null) return TestDict("status", "no_accepted_reply");
            string turn = ReadString(reply, "turn_id", ""), session = ReadString(reply, "session_id", "");
            var storedFrame = PortraitsStore.SelectChatViewFrames(connection, TestDict("turn", turn)).SingleOrDefault();
            if (storedFrame == null) return TestDict("status", "no_scene_frame");
            var frame = TryParseJsonObject(ReadString(storedFrame, "frame_json", "{}"));
            var allowed = new HashSet<string>(ReadDictionaryList(frame, "actors").Select(a => ReadString(a, "id", "")), StringComparer.Ordinal);
            var player = rows.FirstOrDefault(r => ReadString(r, "role", "") == "player");
            var evidence = new List<Dictionary<string, object>>();
            var rejected = new List<string>();
            bool arrayValid = parsed != null && parsed.TryGetValue("viewWardrobeEvidence", out var raw)
                && raw is System.Collections.IList list && list.Cast<object>().All(x => x is Dictionary<string, object>);
            if (!arrayValid) rejected.Add("missing_or_invalid_evidence_field");
            var nominations = arrayValid ? ReadDictionaryList(parsed, "viewWardrobeEvidence") : new List<Dictionary<string, object>>();
            if (nominations.Count > 32) rejected.Add("too_many_nominations");
            else foreach (var nomination in nominations)
            {
                string part = ReadString(nomination, "source", "");
                var source = part == "reply" ? reply : part == "player" ? player : null;
                var ids = ReadStringList(nomination, "characterIds");
                string quote = ReadString(nomination, "quote", "");
                if (source == null || ids.Count == 0 || ids.Count > 16 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count
                    || ids.Any(id => !allowed.Contains(id)) || quote.Length < 3 || quote.Length > 2000)
                { rejected.Add("invalid_source_actor_or_bound"); continue; }
                string text = ChatViewWardrobeOriginalText(connection, source);
                int start = text.IndexOf(quote, StringComparison.Ordinal);
                if (start < 0 || text.IndexOf(quote, start + 1, StringComparison.Ordinal) >= 0)
                { rejected.Add("invented_or_ambiguous_quote"); continue; }
                evidence.Add(TestDict("turnId", ReadString(source, "turn_id", ""), "sourceHash", DialogueService.PromptHash(text),
                    "start", start, "length", quote.Length, "quote", quote, "characterIds", ids));
            }
            string payload = Json.Serialize(TestDict("contract", ChatViewWardrobeContract, "evidence", evidence, "rejected", rejected));
            if (payload.Length > 64000)
            {
                evidence.Clear(); rejected.Clear(); rejected.Add("evidence_budget_exceeded");
                payload = Json.Serialize(TestDict("contract", ChatViewWardrobeContract, "evidence", evidence, "rejected", rejected));
            }
            PortraitsStore.InsertOrIgnoreChatViewWardrobeEvidence(connection, TestDict("id", turn, "session", session,
                "timeline", DialogueService.ContinuityTimeline(request), "hash", DialogueService.PromptHash(ReadString(reply, "text", "")), "payload", payload));
            return TestDict("status", "recorded", "evidenceCount", evidence.Count, "rejected", rejected);
        }

        private static Dictionary<string, object> ReadChatViewWardrobeHistory(ReignDbConnection connection,
            Dictionary<string, object> request, List<Dictionary<string, object>> window)
        {
            string session = ReadString(request, "sessionId", ""), player = ReadString(request, "playerId", "");
            int first = ReadInt(window.First(), "turnOrder", 0);
            string scope = ChatViewWardrobeFrameScope(ReadDictionary(window.First(), "frame"));
            var access = new Program.KnowledgeAccessContext { NpcId = player, TimelineId = ReadString(request, "timelineId", ""),
                WorldDay = ReadDouble(request, "worldDay", double.MaxValue), AsOfAcceptedSequence = long.MaxValue, AsOfTimestamp = long.MaxValue };
            var prior = PortraitsStore.SelectConversationTurns2(connection, TestDict("session", session, "first", first));
            var eligible = new List<Dictionary<string, object>>();
            bool incompleteBoundary = false;
            foreach (var row in prior)
            {
                if (!DialogueService.MemoryTimeVisible(row, access, connection)
                    || !Program.TextListFromJson(ReadString(row, "participants_json", "[]")).Contains(player, StringComparer.OrdinalIgnoreCase)) continue;
                if (ReadString(row, "frame_json", "").Length == 0) { incompleteBoundary = true; break; }
                if (ChatViewWardrobeFrameScope(TryParseJsonObject(ReadString(row, "frame_json", "{}"))) != scope) break;
                eligible.Add(row);
            }
            if (eligible.Count > 4096) throw new InvalidOperationException("This scene exceeds View's retained clothing-context limit; no history was truncated or image requested.");
            var byId = eligible.ToDictionary(r => ReadString(r, "turn_id", ""), StringComparer.Ordinal);
            int minimum = eligible.Count == 0 ? first : eligible.Min(r => ReadInt(r, "turn_order", 0));
            var entries = PortraitsStore.SelectChatViewWardrobeEvidence2(connection, TestDict("session", session, "minimum", minimum, "first", first));
            var sources = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            void FreezeSource(Dictionary<string, object> source, List<string> nominated)
            {
                var ids = nominated.Where(id => eligible.Where(r => ReadInt(r, "turn_order", 0) >= ReadInt(source, "turn_order", 0))
                    .All(r => ReadDictionaryList(TryParseJsonObject(ReadString(r, "frame_json", "{}")), "actors").Any(a => ReadString(a, "id", "") == id))).ToList();
                if (ids.Count == 0) return;
                string sourceId = ReadString(source, "turn_id", ""), text = ChatViewWardrobeOriginalText(connection, source, access);
                if (!sources.TryGetValue(sourceId, out var frozen))
                {
                    frozen = TestDict("turnId", sourceId, "exchangeId", ReadString(source, "exchange_id", ""),
                        "turnOrder", ReadInt(source, "turn_order", 0), "role", ReadString(source, "role", ""),
                        "speakerId", ReadString(source, "speaker_id", ""), "text", text, "sourceHash", DialogueService.PromptHash(text),
                        "frame", TryParseJsonObject(ReadString(source, "frame_json", "{}")), "wardrobeActorIds", ids);
                    sources[sourceId] = frozen;
                }
                else frozen["wardrobeActorIds"] = ReadStringList(frozen, "wardrobeActorIds").Concat(ids).Distinct(StringComparer.Ordinal).ToList();
            }
            int covered = 0, rejected = 0;
            foreach (var entry in entries)
            {
                if (!byId.TryGetValue(ReadString(entry, "entry_id", ""), out var reply)) continue;
                if (DialogueService.PromptHash(ReadString(reply, "text", "")) != ReadString(entry, "source_hash", ""))
                    throw new InvalidDataException("Retained clothing evidence no longer matches its accepted response.");
                covered++;
                var data = TryParseJsonObject(ReadString(entry, "payload_json", "{}"));
                rejected += ReadStringList(data, "rejected").Count;
                var relatedActors = new List<string>();
                foreach (var item in ReadDictionaryList(data, "evidence"))
                {
                    if (!byId.TryGetValue(ReadString(item, "turnId", ""), out var source)) continue;
                    string text = ChatViewWardrobeOriginalText(connection, source, access);
                    string quote = ReadString(item, "quote", "");
                    int start = ReadInt(item, "start", -1);
                    if (DialogueService.PromptHash(text) != ReadString(item, "sourceHash", "") || start < 0 || start > text.Length - quote.Length
                        || text.Substring(start, quote.Length) != quote) throw new InvalidDataException("Retained clothing quotation failed its source check.");
                    var ids = ReadStringList(item, "characterIds");
                    FreezeSource(source, ids);
                    relatedActors.AddRange(ids);
                }
                // Preserve the actual answer to a clothing proposal, including refusal,
                // even if its words did not independently nominate another garment.
                if (relatedActors.Count > 0)
                    foreach (var companion in eligible.Where(r => ReadString(r, "exchange_id", "") == ReadString(reply, "exchange_id", "")
                        && (ReadString(r, "role", "") == "player" || ReadString(r, "turn_id", "") == ReadString(reply, "turn_id", ""))))
                        FreezeSource(companion, relatedActors.Distinct(StringComparer.Ordinal).ToList());
            }
            if (sources.Count > 64) throw new InvalidOperationException("This scene has too many clothing-evidence sources for View; no source was silently truncated.");
            return TestDict("contract", ChatViewWardrobeContract,
                "sources", sources.Values.OrderBy(r => ReadInt(r, "turnOrder", 0)).ToList(),
                "coverage", eligible.Count == 0 ? "no_earlier_nominations"
                    : !incompleteBoundary && covered == eligible.Count(r => ReadString(r, "role", "") == "npc") && rejected == 0
                        ? "post_contract_nominations" : "legacy_or_incomplete_nominations",
                "rejectedNominationCount", rejected);
        }
    }
}
