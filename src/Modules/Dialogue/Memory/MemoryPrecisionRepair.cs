using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static Dictionary<string, object> BuildMemoryStagingManifest(ReignDbConnection connection, Dictionary<string, object> request)
        {
            string campaign = ReadString(request, "campaignId", ""), timeline = ReadString(request, "timelineId", "");
            if (campaign.Length == 0 || timeline.Length == 0) throw new InvalidOperationException("Memory staging requires an exact campaign and timeline.");
            var sessions = DialogueStore.SelectConversationSessions16(connection, new Dictionary<string, object> { ["campaign"] = campaign });
            var requested = ReadStringList(request, "sessionIds");
            if (requested.Count > 0 && requested.Any(id => !sessions.Any(s => ReadString(s, "session_id", "") == id)))
                throw new InvalidOperationException("A requested closed source session is missing.");
            var operations = new List<Dictionary<string, object>>();
            var unresolved = new List<Dictionary<string, object>>();
            foreach (var session in sessions.Where(s => requested.Count == 0 || requested.Contains(ReadString(s, "session_id", ""))))
            {
                string id = ReadString(session, "session_id", "");
                var turns = DialogueStore.SelectConversationTurns50(connection, new Dictionary<string, object> { ["id"] = id });
                if (turns.Count == 0) { unresolved.Add(new Dictionary<string, object> { ["sessionId"] = id, ["reason"] = "empty_source" }); continue; }
                if (turns.Any(t => { var p = TryParseJsonObject(ReadString(t, "payload_json", "{}")); string recorded = ReadString(p, "timelineId", ""); return recorded.Length > 0 && recorded != timeline; }))
                { unresolved.Add(new Dictionary<string, object> { ["sessionId"] = id, ["reason"] = "different_timeline" }); continue; }
                var players = turns.Where(t => ReadString(t, "role", "") == "player").ToList();
                if (players.Count == 0 || players.All(t => IsPureConversationRecallRequest(ReadString(t, "text", ""))))
                { unresolved.Add(new Dictionary<string, object> { ["sessionId"] = id, ["reason"] = "recall_or_non_source_session" }); continue; }
                var prepared = PrepareStagedSceneMemory(session, turns, timeline);
                operations.Add(new Dictionary<string, object> { ["kind"] = "rebuild_scene_memory", ["id"] = id,
                    ["reviewReason"] = "Replace a derived scene with complete attributed source records; retain originals and uncertainty.",
                    ["expectedSourceHash"] = MemorySourceHash(turns), ["expectedSessionHash"] = MemorySourceHash(new[] { session }),
                    ["prepared"] = prepared });
            }
            var state = WorldSimulationService.TableExists(connection, "memory_precision_state") ? ReadMemoryPrecisionState(connection) : new Dictionary<string, object>();
            var derivativeReview = new List<Dictionary<string,object>>();
            foreach (string table in new[] { "summaries", "temporal_knowledge_assertions", "beliefs", "comprehension", "conversation_continuity" })
            {
                if (!WorldSimulationService.TableExists(connection,table)) continue;
                string key = table=="summaries" ? "summary_id" : table=="temporal_knowledge_assertions" ? "assertion_id" : table=="beliefs" ? "belief_id" : table=="comprehension" ? "comprehension_id" : "record_id";
                foreach (var row in DialogueStore.SelectDynamic4(connection, table, key))
                {
                    string id=ReadString(row,key,"");
                    var body=TryParseJsonObject(ReadString(row,"payload_json","{}"));
                    derivativeReview.Add(new Dictionary<string,object> { ["table"]=table,["id"]=id,["sourceHash"]=MemorySourceHash(new[]{row}),
                        ["recordedTimeline"]=FirstNonEmpty(ReadString(row,"timeline_id",""),ReadString(body,"timelineId","")),
                        ["reviewStatus"]=table=="summaries" && ReadString(row,"summary_type","")=="scene" ? "source_reconstruction_staged_when_eligible" : "retained_requires_semantic_review",
                        ["reason"]="Exact source retrieval remains authoritative. Do not upgrade an old belief, interpretation, agreement or relationship meaning to verified truth without reviewing its original evidence." });
                }
            }
            return new Dictionary<string, object> { ["schema"] = "reign-continuity-repair-v1", ["campaignId"] = campaign,
                ["timelineId"] = timeline, ["processingVersion"] = MemoryPrecisionVersion,
                ["expectedRestoreGeneration"] = ReadString(state, "restore_generation", "uninitialized"),
                ["sourceSnapshotHash"] = PromptHash(CanonicalJson(operations)), ["operations"] = operations,
                ["unresolved"] = unresolved, ["providerCalls"] = 0,
                ["derivativeReview"] = derivativeReview,
                ["coverage"] = new Dictionary<string, object> { ["closedSessions"] = sessions.Count, ["stagedSessions"] = operations.Count,
                    ["unresolvedSessions"] = unresolved.Count, ["sourceRecordsComplete"] = true,
                    ["semanticInterpretation"] = "Extractive evidence only; no invented agreement, belief, relationship score or native completion." } };
        }

        private static Dictionary<string, object> PrepareStagedSceneMemory(Dictionary<string, object> session,
            List<Dictionary<string, object>> turns, string timeline)
        {
            // Pure, reproducible staging: the reviewed artifact is recomputed from
            // unchanged originals on apply. No provider participates in maintenance.
            string owner = ReadString(session, "npc_id", "");
            var views = turns.Select(t => ObserverConversationText(ReadString(t, "text", ""),
                ReadString(t, "speaker_id", ""), "", ReadString(t, "role", "") == "player")).ToList();
            var records = turns.Select((t, i) => "[reported speech; source=" + ReadString(t, "turn_id", "")
                + "; speaker=" + ReadString(t, "speaker_id", "")
                + "; role=" + ReadString(t, "role", "") + "; source day=" + ReadDouble(t, "world_day", 0d).ToString("R",CultureInfo.InvariantCulture)
                + "] " + views[i]).ToList();
            string summary = string.Join("\n---\n", records);
            // Topic hints use the same public observation as new scene summaries.
            // IDs and private narration must not become searchable topic words.
            var topics = BuildMemorySummaryTopics(views.Select(view => TestDict("summary", view)), 8);
            var tags = Program.MergeStringLists(topics, new[] { "scene", "conversation", "reported_speech", "claim_attribution", "interpersonal_history" });
            return new Dictionary<string, object> { ["summary"] = summary, ["ownerId"] = owner, ["timelineId"] = timeline,
                ["stagingVersion"] = "source-linked-scene-v2", ["tags"] = tags,
                ["sourceHash"] = MemorySourceHash(turns), ["sourceTurnIds"] = turns.Select(t => ReadString(t, "turn_id", "")).ToList(),
                ["sourceReferences"] = turns.Select((t, i) => TestDict("sourceType", "turn", "sourceId", ReadString(t, "turn_id", ""),
                    "sourceHash", MemorySourceHash(new[] { t }), "viewHash", PromptHash(views[i]),
                    "speakerId", ReadString(t, "speaker_id", ""), "role", ReadString(t, "role", ""),
                    "worldDay", ReadDouble(t, "world_day", 0d))).ToList(),
                ["tokenEstimate"] = EstimateContinuityTokens(summary), ["coverageComplete"] = true,
                ["status"] = "extractive_complete", ["processingVersion"] = MemoryPrecisionVersion };
        }

        private static Dictionary<string, object> ApplyStagedSceneMemory(ReignDbConnection connection,
            Dictionary<string, object> manifest, Dictionary<string, object> operation)
        {
            string id = ReadString(operation, "id", ""), timeline = ReadString(manifest, "timelineId", "");
            var session = DialogueStore.SelectConversationSessions17(connection, new Dictionary<string, object> { ["id"] = id }).SingleOrDefault();
            var turns = DialogueStore.SelectConversationTurns68(connection, new Dictionary<string, object> { ["id"] = id });
            if (session == null || ReadString(session, "campaign_id", "") != ReadString(manifest, "campaignId", "")
                || MemorySourceHash(new[] { session }) != ReadString(operation, "expectedSessionHash", "")
                || MemorySourceHash(turns) != ReadString(operation, "expectedSourceHash", ""))
                throw new InvalidOperationException("Staged memory source changed; rebuild and review the staging preview.");
            var prepared = PrepareStagedSceneMemory(session, turns, timeline);
            if (CanonicalJson(prepared) != CanonicalJson(ReadDictionary(operation, "prepared")))
                throw new InvalidOperationException("Staged memory content differs from complete original evidence.");
            string summaryId = "precision_scene_" + PromptHash(id + "|" + MemorySourceHash(turns) + "|" + ReadString(prepared, "stagingVersion", "")).Substring(0, 32);
            string summary = ReadString(prepared, "summary", "");
            var tags = ReadStringList(prepared, "tags");
            var known = turns.SelectMany(t => Program.TextListFromJson(ReadString(t, "participants_json", "[]"))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // A session can contain whispers. A shared summary can be used only
            // by observers present for every source record. Exact turn retrieval
            // continues to expose each narrower audience separately.
            known = known.Where(observer => turns.All(t => ReadString(t, "speaker_id", "") == observer
                || Program.TextListFromJson(ReadString(t, "participants_json", "[]")).Contains(observer, StringComparer.OrdinalIgnoreCase))).ToList();
            string owner = ReadString(session, "npc_id", "");
            if (!known.Contains(owner, StringComparer.OrdinalIgnoreCase)) owner = "";
            DialogueStore.InsertOrIgnoreSummaries(connection, new Dictionary<string, object> {
                ["id"] = summaryId, ["scope"] = "scene:" + id, ["owner"] = owner, ["summary"] = summary,
                ["events"] = Json.Serialize(turns.Select(t => ReadString(t, "event_id", "")).Where(e => e.Length > 0).Distinct().ToList()),
                ["start"] = turns.Min(t => ReadLong(t, "ts", 0)), ["end"] = turns.Max(t => ReadLong(t, "ts", 0)), ["count"] = turns.Count,
                ["location"] = ReadString(session, "location_id", ""), ["known"] = Json.Serialize(known), ["tags"] = Json.Serialize(tags),
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["payload"] = Json.Serialize(prepared) });
            Program.InsertSummaryFts(connection, summaryId, summary, tags, known);
            int ordinal = 0;
            foreach (var turn in turns) LinkMemorySource(connection, "summary", summaryId, "turn", ReadString(turn, "turn_id", ""), ordinal++);
            LinkMemorySource(connection, "summary", summaryId, "session", id, 0);
            string previous = ReadString(session, "scene_summary_id", "");
            if (previous.Length > 0 && previous != summaryId)
                DialogueStore.UpdateSummaries3(connection, new Dictionary<string, object> { ["id"] = previous });
            DialogueStore.UpdateConversationSessions3(connection, new Dictionary<string, object> { ["summary"] = summaryId, ["id"] = id });
            DialogueStore.DeleteMemoryProjections(connection);
            return new Dictionary<string, object> { ["kind"] = "rebuild_scene_memory", ["sourceId"] = id, ["beforeSummaryId"] = previous,
                ["afterSummaryId"] = summaryId, ["sourceHash"] = ReadString(prepared, "sourceHash", ""), ["sourceRecords"] = turns.Count,
                ["sourceRecordsComplete"] = true, ["nativeActionsReplayed"] = false };
        }
    }
}
