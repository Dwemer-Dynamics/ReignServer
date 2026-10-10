using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // Historical author links live on the new private original, never on an
        // inherited accepted turn whose full-row hash proves restored visibility.
        private static Dictionary<string, object> RecoveredOriginalLink(Dictionary<string, object> source) =>
            ReadDictionary(TryParseJsonObject(ReadString(source, "payload_json", "{}")), "originalRecovery");

        private static Dictionary<string, object> FindRecoveredOriginal(Dictionary<string, object> turn,
            IEnumerable<Dictionary<string, object>> visibleSources)
        {
            string id = ReadString(turn, "turn_id", ""), hash = MemorySpecialistSourceHash(turn);
            var matches = visibleSources.Where(source => {
                var link = RecoveredOriginalLink(source);
                return ReadString(source, "speaker_id", "") == ReadString(turn, "speaker_id", "")
                    && ReadString(link, "acceptedTurnId", "") == id
                    && ReadString(link, "acceptedSourceHash", "") == hash;
            }).ToList();
            if (matches.Select(source => ReadString(source, "text", "")).Distinct(StringComparer.Ordinal).Count() > 1)
                throw new InvalidOperationException("Conflicting original-input recovery evidence requires review.");
            return matches.OrderBy(source => ReadString(source, "turn_id", ""), StringComparer.Ordinal).FirstOrDefault();
        }

        // Read-only staging across all NPCs in one exact campaign/timeline. A
        // diagnostic capture is evidence for human review, never automatic truth.
        private static Dictionary<string, object> BuildConversationOriginalAudit(ReignDbConnection connection, Dictionary<string, object> request)
        {
            string campaign = ReadString(request, "campaignId", ""), timeline = ReadString(request, "timelineId", "");
            if (campaign.Length == 0 || timeline.Length == 0) throw new InvalidOperationException("Exact campaign and timeline are required.");
            var rows = QuerySql(connection, @"SELECT t.*,s.player_id,s.npc_id,s.payload_json AS session_payload
FROM conversation_turns t JOIN conversation_sessions s ON s.session_id=t.session_id
WHERE t.status IN ('active','source_only') AND t.role IN ('player','player_source') ORDER BY t.ts,t.turn_order,t.turn_id;");
            var access = new KnowledgeAccessContext { TimelineId = timeline, WorldDay = double.MaxValue, AsOfAcceptedSequence = long.MaxValue, AsOfTimestamp = long.MaxValue };
            var sources = rows.Where(r => ReadString(r, "status", "") == "source_only" && MemoryTimeVisible(r, access, connection))
                .ToDictionary(r => ReadString(r, "turn_id", ""));
            var audit = new List<Dictionary<string, object>>(); var operations = new List<Dictionary<string, object>>();
            var captures = ReadDictionaryList(request, "reviewedCaptures");
            foreach (var turn in rows.Where(r => ReadString(r, "role", "") == "player"))
            {
                var meta = TryParseJsonObject(ReadString(turn, "payload_json", "{}"));
                string rowTimeline = FirstNonEmpty(ReadString(meta, "timelineId", ""), ReadString(TryParseJsonObject(ReadString(turn, "session_payload", "{}")), "timelineId", ""));
                var scoped = CloneDictionary(turn); scoped["timeline_id"] = rowTimeline;
                if (rowTimeline.Length > 0 && !MemoryTimeVisible(scoped, access, connection)) continue;
                string id = ReadString(turn, "turn_id", ""), sourceId = ReadString(meta, "inputSourceId", "");
                Dictionary<string, object> original = null;
                if (sourceId.Length > 0) sources.TryGetValue(sourceId, out original);
                original ??= FindRecoveredOriginal(turn, sources.Values);
                if (original == null)
                {
                    var matching = sources.Values.Where(s => ReadString(s, "session_id", "") == ReadString(turn, "session_id", "")
                        && ReadString(s, "exchange_id", "") == ReadString(turn, "exchange_id", "")
                        && ReadString(s, "speaker_id", "") == ReadString(turn, "speaker_id", "") && RecoveredOriginalLink(s) == null).ToList();
                    if (matching.Count == 1) original = matching[0];
                }
                var capture = captures.Where(c => ReadString(c, "turnId", "") == id).ToList();
                string status = rowTimeline.Length == 0 ? "timeline_unproven" : original == null ? "original_missing" : "original_available";
                if (original == null && sourceId.Length > 0) status = "original_reference_unavailable";
                if (original != null && ReadString(original, "speaker_id", "") != ReadString(turn, "speaker_id", "")) status = "source_author_mismatch";
                string integrity = original == null ? "original_unavailable" : OriginalConversationSourceIntegrity(original);
                if (original == null && sourceId.Length == 0 && capture.Count == 1 && rowTimeline.Length > 0)
                {
                    var c = capture[0]; string text = ReadString(c, "originalText", "");
                    bool proven = text.Length > 0 && ReadString(c, "campaignId", "") == campaign && ReadString(c, "timelineId", "") == rowTimeline
                        && ReadString(c, "sessionId", "") == ReadString(turn, "session_id", "") && ReadString(c, "playerId", "") == ReadString(turn, "speaker_id", "")
                        && ReadString(c, "sourceHash", "") == ConversationExactTextHash(text)
                        && ReadString(c, "captureFileSha256", "").Length == 64 && ReadString(c, "traceId", "").Length > 0;
                    if (proven)
                    {
                        status = "reviewed_capture_recoverable";
                        operations.Add(TestDict("kind", "restore_original_input", "id", id, "reviewReason", "Restore the exact reviewed author contribution; preserve the accepted observer text and every NPC reply.",
                            "expected", TestDict("text", ReadString(turn, "text", ""), "payload_json", ReadString(turn, "payload_json", "{}"),
                                "session_id", ReadString(turn, "session_id", ""), "speaker_id", ReadString(turn, "speaker_id", "")), "capture", c));
                    }
                    else status = "capture_provenance_incomplete";
                }
                audit.Add(TestDict("turnId", id, "npcId", ReadString(turn, "npc_id", ""), "sessionId", ReadString(turn, "session_id", ""),
                    "status", status, "sourceIntegrity", integrity, "sourceTimelineId", rowTimeline, "originalSourceId", ReadString(original, "turn_id", ""),
                    "projectionDiffers", original != null && ReadString(original, "text", "") != ReadString(turn, "text", "")));
            }
            return TestDict("schema", "reign-conversation-original-audit-v1", "campaignId", campaign, "timelineId", timeline,
                "coverage", "all_player_turns_all_npcs_in_campaign_with_timeline_or_restore_proof", "turns", audit,
                "missingOriginals", audit.Count(r => ReadString(r, "status", "") == "original_missing"),
                "repairManifest", TestDict("schema", "reign-continuity-repair-v1", "campaignId", campaign, "timelineId", timeline,
                    "expectedRestoreGeneration", ReadString(QuerySql(connection, "SELECT restore_generation FROM memory_precision_state WHERE state_id='current';").Single(), "restore_generation", ""),
                    "operations", operations), "campaignChanged", false, "providerCalls", 0);
        }

        private static Dictionary<string, object> RestoreReviewedOriginalInput(ReignDbConnection connection, Dictionary<string, object> manifest, Dictionary<string, object> operation)
        {
            string id = ReadString(operation, "id", ""), timeline = ReadString(manifest, "timelineId", "");
            var turn = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id AND status='active' AND role='player';", TestDict("id", id)).SingleOrDefault();
            var expected = ReadDictionary(operation, "expected"); var capture = ReadDictionary(operation, "capture");
            if (turn == null || expected == null || !new[] { "text", "payload_json", "session_id", "speaker_id" }.All(expected.ContainsKey)
                || expected.Any(pair => ReadString(turn, pair.Key, "") != Convert.ToString(pair.Value)))
                throw new InvalidOperationException("The historical contribution changed after review.");
            var metadata = TryParseJsonObject(ReadString(turn, "payload_json", "{}"));
            string original = ReadString(capture, "originalText", ""), player = ReadString(turn, "speaker_id", "");
            string sourceTimeline = ReadString(metadata, "timelineId", "");
            var access = new KnowledgeAccessContext { NpcId = player, TimelineId = timeline, WorldDay = double.MaxValue, AsOfAcceptedSequence = long.MaxValue, AsOfTimestamp = long.MaxValue };
            if (original.Length == 0 || ReadString(capture, "sourceHash", "") != ConversationExactTextHash(original)
                || ReadString(capture, "campaignId", "") != ReadString(manifest, "campaignId", "") || ReadString(capture, "timelineId", "") != sourceTimeline
                || ReadString(capture, "sessionId", "") != ReadString(turn, "session_id", "") || ReadString(capture, "playerId", "") != player
                || ReadString(capture, "turnId", "") != id || ReadString(capture, "traceId", "").Length == 0 || ReadString(capture, "captureFileSha256", "").Length != 64
                || sourceTimeline.Length == 0 || !MemoryTimeVisible(turn, access, connection) || ReadString(metadata, "inputSourceId", "").Length > 0)
                throw new InvalidOperationException("Exact reviewed original provenance is missing or already bound.");
            var payload = TestDict("timelineId", timeline, "playerText", original);
            string sourceId = "recovered_input_" + ConversationExactTextHash(timeline + "|" + id + "|" + original);
            CapturePlayerInput(payload).SourceId = sourceId;
            PreservePlayerInputSource(connection, payload, ReadString(turn, "session_id", ""), ReadString(turn, "event_id", ""),
                ReadString(turn, "exchange_id", ""), player, ReadString(turn, "speaker_name", "Player"), ReadDouble(turn, "world_day", 0),
                ReadLong(turn, "ts", 0), ReadString(turn, "location_id", ""));
            var source = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id AND status='source_only';", TestDict("id", sourceId)).Single();
            if (ReadString(source, "text", "") != original) throw new InvalidOperationException("Recovered original failed its exact-content check.");
            var sourceMetadata = TryParseJsonObject(ReadString(source, "payload_json", "{}"));
            sourceMetadata["originalRecovery"] = TestDict("acceptedTurnId", id, "acceptedSourceHash", MemorySpecialistSourceHash(turn),
                "sourceTimelineId", sourceTimeline, "recoveryTimelineId", timeline,
                "traceId", ReadString(capture, "traceId", ""), "captureFileSha256", ReadString(capture, "captureFileSha256", ""),
                "sourceHash", ConversationExactTextHash(original), "npcObserverProjectionChanged", false);
            ExecuteSql(connection, "UPDATE conversation_turns SET payload_json=$payload WHERE turn_id=$id AND status='source_only';",
                TestDict("id", sourceId, "payload", Json.Serialize(sourceMetadata)));
            return TestDict("kind", "restore_original_input", "turnId", id, "originalSourceId", sourceId,
                "sourceHash", ConversationExactTextHash(original), "acceptedTextChanged", false, "acceptedMetadataChanged", false,
                "npcRepliesChanged", false, "npcKnowledgeChanged", false);
        }
    }
}
