using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly object NpcDialogueAuditLock = new object();
        private static readonly HashSet<string> NpcDialogueAuditMemoryDomains = new HashSet<string>(
            new[] { "personal_state", "interpersonal_history", "world_affairs", "local_awareness", "commitments_and_plots", "beliefs_and_rumors" },
            StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> NpcDialogueAuditServerContextPulls = new HashSet<string>(
            new[] { "relevant_memory", "relationship_history" }, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> NpcDialogueAuditRelationshipSignals = new HashSet<string>(
            new[] { "warmer", "colder", "unchanged", "afraid", "respectful", "suspicious", "hostile", "indebted", "dominant", "submissive" },
            StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, object> NpcDialogueAuditRequestApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            if (string.IsNullOrWhiteSpace(campaignId))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "campaignId is required." };

            string requestId = FirstNonEmpty(ReadString(payload, "requestId", ""), "npc-dialogue-request-" + Guid.NewGuid().ToString("N"));
            string heroSearch = ReadString(payload, "heroSearch", "Gavalon");
            lock (NpcDialogueAuditLock)
            {
                Dictionary<string, object> active = FindActiveNpcDialogueAudit(campaignId);
                if (active != null)
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["error"] = "An NPC dialogue audit is already active.",
                        ["activeRunId"] = ReadString(active, "runId", "")
                    };
                }

                Dictionary<string, object> existing = LoadNpcDialogueAuditRequest(campaignId);
                if (existing != null && string.Equals(ReadString(existing, "status", ""), "queued", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(ReadString(existing, "requestId", ""), requestId, StringComparison.OrdinalIgnoreCase))
                        return NpcDialogueAuditRequestSummary(existing);
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["error"] = "Another NPC dialogue audit request is already queued.",
                        ["requestId"] = ReadString(existing, "requestId", "")
                    };
                }

                Dictionary<string, object> request = new Dictionary<string, object>
                {
                    ["requestId"] = requestId,
                    ["campaignId"] = campaignId,
                    ["heroSearch"] = heroSearch,
                    ["seed"] = ReadLong(payload, "seed", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                    ["status"] = "queued",
                    ["queuedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o")
                };
                SaveNpcDialogueAuditRequest(request);
                return NpcDialogueAuditRequestSummary(request);
            }
        }

        private static Dictionary<string, object> NpcDialogueAuditRequestPollApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            if (string.IsNullOrWhiteSpace(campaignId))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "campaignId is required." };

            lock (NpcDialogueAuditLock)
            {
                Dictionary<string, object> request = LoadNpcDialogueAuditRequest(campaignId);
                if (request == null || !string.Equals(ReadString(request, "status", ""), "queued", StringComparison.OrdinalIgnoreCase))
                    return new Dictionary<string, object> { ["ok"] = true, ["found"] = false, ["campaignId"] = campaignId };
                return NpcDialogueAuditRequestSummary(request);
            }
        }

        private static Dictionary<string, object> NpcDialogueAuditRequestAckApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            string requestId = ReadString(payload, "requestId", "");
            string status = ReadString(payload, "status", "");
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(requestId))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "campaignId and requestId are required." };
            if (!string.Equals(status, "accepted", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status, "rejected", StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "status must be accepted or rejected." };

            lock (NpcDialogueAuditLock)
            {
                Dictionary<string, object> request = LoadNpcDialogueAuditRequest(campaignId);
                if (request == null || !string.Equals(ReadString(request, "requestId", ""), requestId, StringComparison.OrdinalIgnoreCase))
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "NPC dialogue audit request was not found." };

                request["status"] = status.ToLowerInvariant();
                request["heroId"] = ReadString(payload, "heroId", "");
                request["heroName"] = ReadString(payload, "heroName", "");
                request["message"] = ReadString(payload, "message", "");
                request["acknowledgedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                request["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                SaveNpcDialogueAuditRequest(request);
                return NpcDialogueAuditRequestSummary(request);
            }
        }

        private static Dictionary<string, object> NpcDialogueAuditStartApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string runId = FirstNonEmpty(ReadString(payload, "runId", ""), "npc-dialogue-" + Guid.NewGuid().ToString("N"));
            string npcId = ReadFirstString(payload, "npcId", "heroStringId");
            string playerId = ReadFirstString(payload, "playerId", "playerHeroStringId");
            if (string.IsNullOrWhiteSpace(npcId))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "npcId is required." };

            lock (NpcDialogueAuditLock)
            {
                Dictionary<string, object> active = FindActiveNpcDialogueAudit(campaignId);
                if (active != null && !string.Equals(ReadString(active, "runId", ""), runId, StringComparison.OrdinalIgnoreCase))
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Another NPC dialogue audit is already active.", ["activeRunId"] = ReadString(active, "runId", "") };

                Dictionary<string, object> report = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["runId"] = runId,
                    ["campaignId"] = campaignId,
                    ["npcId"] = npcId,
                    ["npcName"] = ReadString(payload, "npcName", npcId),
                    ["playerId"] = playerId,
                    ["seed"] = ReadLong(payload, "seed", 0),
                    ["expectedExchanges"] = ReadInt(payload, "expectedExchanges", 30),
                    ["expectedScenes"] = ReadInt(payload, "expectedScenes", 5),
                    ["status"] = "running",
                    ["startedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["turns"] = new List<Dictionary<string, object>>(),
                    ["scenes"] = new List<Dictionary<string, object>>(),
                    ["assertions"] = new List<Dictionary<string, object>>(),
                    ["baseline"] = NpcDialogueAuditCounts(campaignId, npcId)
                };
                SaveNpcDialogueAudit(report);
                return NpcDialogueAuditSummary(report, true);
            }
        }

        private static Dictionary<string, object> NpcDialogueAuditTurnApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string runId = ReadString(payload, "runId", "");
            lock (NpcDialogueAuditLock)
            {
                Dictionary<string, object> report = LoadNpcDialogueAudit(campaignId, runId);
                if (report == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "NPC dialogue audit was not found." };

                string correlationId = ReadString(payload, "correlationId", "");
                string sessionId = ReadString(payload, "sessionId", "");
                string exchangeId = ReadString(payload, "exchangeId", "");
                string playerText = ReadString(payload, "playerText", "");
                string npcReply = ReadString(payload, "npcReply", "");
                List<string> expectedPulls = ReadStringList(payload, "expectedContextPulls");
                List<Dictionary<string, object>> assertions = new List<Dictionary<string, object>>();

                List<Dictionary<string, object>> auditRows = ReadJsonLinesFromPath(CampaignFile(campaignId, "audit", "audit.jsonl"))
                    .Where(row => string.Equals(ReadString(row, "correlationId", ""), correlationId, StringComparison.OrdinalIgnoreCase)).ToList();
                AddNpcDialogueAssertion(assertions, "audit_context_select", auditRows.Any(row => ReadString(row, "phase", "") == "context.select"), "Context selector audit exists.", correlationId);
                AddNpcDialogueAssertion(assertions, "audit_context_loaded", auditRows.Any(row => ReadString(row, "phase", "") == "context.loaded"), "Context-loaded audit exists.", correlationId);
                AddNpcDialogueAssertion(assertions, "audit_prompt_built", auditRows.Any(row => ReadString(row, "phase", "") == "prompt.built"), "Prompt audit exists.", correlationId);
                AddNpcDialogueAssertion(assertions, "audit_model_parsed", auditRows.Any(row => ReadString(row, "phase", "") == "model.parsed" && ReadString(row, "status", "") != "warning"), "Model returned structured JSON.", correlationId);
                AddNpcDialogueAssertion(assertions, "audit_response", auditRows.Any(row => ReadString(row, "phase", "") == "dialogue.response" && ReadString(row, "status", "") == "completed"), "Dialogue response audit completed.", correlationId);

                Dictionary<string, object> contextRow = auditRows.LastOrDefault(row => ReadString(row, "phase", "") == "context.loaded");
                Dictionary<string, object> contextData = ReadDictionary(contextRow, "data") ?? new Dictionary<string, object>();
                List<Dictionary<string, object>> selected = ReadDictionaryList(contextData, "selectedContextPulls");
                List<Dictionary<string, object>> bundles = ReadDictionaryList(contextData, "contextBundles");
                List<string> selectedIds = selected.Select(row => ReadString(row, "id", "")).Where(value => value.Length > 0).ToList();
                foreach (string expected in expectedPulls)
                    AddNpcDialogueAssertion(assertions, "context_expected_" + expected, selectedIds.Contains(expected, StringComparer.OrdinalIgnoreCase), "Expected context pull was selected.", expected);
                List<string> bundlePullIds = selectedIds.Where(id => !NpcDialogueAuditServerContextPulls.Contains(id)).ToList();
                AddNpcDialogueAssertion(assertions, "context_bundle_alignment",
                    bundlePullIds.All(id => bundles.Any(bundle => string.Equals(ReadFirstString(bundle, "id", "pullId"), id, StringComparison.OrdinalIgnoreCase))),
                    "Every selected game-data context pull has a matching bundle; server-native memory and relationship pulls are loaded directly.", string.Join(",", bundlePullIds));
                AddNpcDialogueAssertion(assertions, "context_bundle_payloads",
                    bundles.All(bundle => !string.IsNullOrWhiteSpace(ReadString(bundle, "id", ""))
                        && ReadDictionary(bundle, "data") != null),
                    "Every context bundle identifies its helper and contains a structured native-data payload.", Json.Serialize(bundles));
                AddNpcDialogueAssertion(assertions, "context_bundle_success",
                    bundles.All(bundle => ReadBool(bundle, "ok", false)),
                    "Every selected game-data helper completed without falling back to an unavailable/error payload.",
                    string.Join(",", bundles.Where(bundle => !ReadBool(bundle, "ok", false)).Select(bundle => ReadString(bundle, "id", "") + ":" + ReadString(bundle, "error", ""))));

                Dictionary<string, object> promptRow = auditRows.LastOrDefault(row => ReadString(row, "phase", "") == "prompt.built");
                Dictionary<string, object> promptData = ReadDictionary(promptRow, "data") ?? new Dictionary<string, object>();
                Dictionary<string, object> promptEvidence = ReadDictionary(promptData, "promptEvidence") ?? new Dictionary<string, object>();
                Dictionary<string, object> contextPullEvidence = ReadDictionary(promptEvidence, "contextPulls") ?? new Dictionary<string, object>();
                string promptJson = Json.Serialize(promptData);
                foreach (string selectedId in selectedIds)
                    AddNpcDialogueAssertion(assertions, "prompt_contains_" + selectedId,
                        contextPullEvidence.ContainsKey(selectedId)
                            ? ReadBool(contextPullEvidence, selectedId, false)
                            : NpcDialoguePromptContainsContext(promptJson, selectedId),
                        "Selected context evidence is represented in the prompt audit.", selectedId);

                Dictionary<string, object> modelRow = auditRows.LastOrDefault(row => ReadString(row, "phase", "") == "model.parsed");
                Dictionary<string, object> modelData = ReadDictionary(modelRow, "data") ?? new Dictionary<string, object>();
                Dictionary<string, object> parsed = ReadDictionary(modelData, "parsed") ?? new Dictionary<string, object>();
                string relationshipSignal = ReadFirstString(parsed, "relationshipSignal", "relationship_signal");
                AddNpcDialogueAssertion(assertions, "model_required_fields",
                    parsed.Count > 0
                    && !string.IsNullOrWhiteSpace(ReadString(parsed, "reply", ""))
                    && ReadDictionary(parsed, "decisionBrief") != null
                    && !string.IsNullOrWhiteSpace(ReadString(parsed, "emotion", ""))
                    && !string.IsNullOrWhiteSpace(ReadString(parsed, "intent", ""))
                    && !string.IsNullOrWhiteSpace(relationshipSignal)
                    && ReadDictionary(parsed, "actionGate") != null,
                    "Parsed model JSON contains the required dialogue fields.", Json.Serialize(parsed.Keys.ToList()));
                AddNpcDialogueAssertion(assertions, "model_relationship_signal",
                    NpcDialogueAuditRelationshipSignals.Contains(relationshipSignal),
                    "The relationship signal is a permitted enum value.", relationshipSignal);
                List<Dictionary<string, object>> modelMemoryWrites = ReadDictionaryList(parsed, "memoryWrites");
                AddNpcDialogueAssertion(assertions, "model_memory_write_shape",
                    modelMemoryWrites.Count <= 2 && modelMemoryWrites.All(write =>
                        !string.IsNullOrWhiteSpace(ReadFirstString(write, "text", "summary"))
                        && ReadDouble(write, "importance", -1d) >= 0d),
                    "Model episodic memory writes respect the count limit and required shape.", Json.Serialize(modelMemoryWrites));

                List<Dictionary<string, object>> turns;
                Dictionary<string, object> evt;
                List<Dictionary<string, object>> memories;
                int eventFts = 0;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    turns = QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$session AND exchange_id=$exchange ORDER BY turn_order;",
                        new Dictionary<string, object> { ["session"] = sessionId, ["exchange"] = exchangeId });
                    evt = QuerySql(connection, "SELECT * FROM events WHERE event_id=$id LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = exchangeId }).FirstOrDefault();
                    memories = QuerySql(connection, "SELECT * FROM memories WHERE event_id=$id;",
                        new Dictionary<string, object> { ["id"] = exchangeId });
                    eventFts = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM event_fts WHERE event_id=$id;",
                        new Dictionary<string, object> { ["id"] = exchangeId }).FirstOrDefault(), "count", 0);
                    foreach (Dictionary<string, object> turn in turns)
                    {
                        int ftsCount = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM conversation_turn_fts WHERE turn_id=$id;",
                            new Dictionary<string, object> { ["id"] = ReadString(turn, "turn_id", "") }).FirstOrDefault(), "count", 0);
                        AddNpcDialogueAssertion(assertions, "turn_fts_" + ReadString(turn, "role", ""), ftsCount == 1, "Conversation turn has one FTS row.", ReadString(turn, "turn_id", ""));
                    }
                }

                AddNpcDialogueAssertion(assertions, "turn_pair", turns.Count == 2, "Exchange contains exactly two turns.", turns.Count.ToString());
                Dictionary<string, object> playerTurn = turns.FirstOrDefault(row => ReadString(row, "role", "") == "player");
                Dictionary<string, object> npcTurn = turns.FirstOrDefault(row => ReadString(row, "role", "") == "npc");
                string npcId = ReadString(report, "npcId", "");
                string playerId = ReadString(report, "playerId", "");
                AddNpcDialogueAssertion(assertions, "turn_roles",
                    turns.Count(row => ReadString(row, "role", "") == "player") == 1
                    && turns.Count(row => ReadString(row, "role", "") == "npc") == 1,
                    "Exchange contains exactly one player role and one NPC role.", string.Join(",", turns.Select(row => ReadString(row, "role", ""))));
                AddNpcDialogueAssertion(assertions, "turn_speakers",
                    string.Equals(ReadString(playerTurn, "speaker_id", ""), playerId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ReadString(npcTurn, "speaker_id", ""), npcId, StringComparison.OrdinalIgnoreCase),
                    "Stored turn speakers match the player and selected NPC.", ReadString(playerTurn, "speaker_id", "") + "," + ReadString(npcTurn, "speaker_id", ""));
                AddNpcDialogueAssertion(assertions, "turn_session_exchange",
                    turns.All(row => ReadString(row, "session_id", "") == sessionId && ReadString(row, "exchange_id", "") == exchangeId),
                    "Both turns retain the requested session and exchange IDs.", sessionId + "," + exchangeId);
                AddNpcDialogueAssertion(assertions, "player_text", ReadString(playerTurn, "text", "") == playerText, "Stored player text is exact.", playerText);
                AddNpcDialogueAssertion(assertions, "npc_text", ReadString(npcTurn, "text", "") == npcReply, "Stored NPC reply is exact.", npcReply);
                AddNpcDialogueAssertion(assertions, "turn_order", playerTurn != null && npcTurn != null && ReadInt(npcTurn, "turn_order", -1) == ReadInt(playerTurn, "turn_order", -2) + 1, "Turn order is consecutive.", exchangeId);
                AddNpcDialogueAssertion(assertions, "turn_channel", turns.All(row => ReadString(row, "channel", "") == "in_person"), "Both turns use the in-person channel.", exchangeId);
                AddNpcDialogueAssertion(assertions, "turn_participants", turns.All(row =>
                    TextListFromJson(ReadString(row, "participants_json", "[]")).Contains(npcId, StringComparer.OrdinalIgnoreCase)
                    && TextListFromJson(ReadString(row, "participants_json", "[]")).Contains(playerId, StringComparer.OrdinalIgnoreCase)),
                    "Both turns include the player and selected NPC as participants.", exchangeId);
                AddNpcDialogueAssertion(assertions, "turn_location", turns.All(row => !string.IsNullOrWhiteSpace(ReadString(row, "location_id", ""))),
                    "Both in-person turns retain a native location ID.", string.Join(",", turns.Select(row => ReadString(row, "location_id", ""))));
                AddNpcDialogueAssertion(assertions, "event_category", evt != null && ReadString(evt, "event_category", "") == "conversation", "Dialogue event is categorized as conversation.", exchangeId);
                AddNpcDialogueAssertion(assertions, "event_fts", eventFts == 1, "Dialogue event has one FTS row.", eventFts.ToString());
                AddNpcDialogueAssertion(assertions, "event_participants", evt != null
                    && TextListFromJson(ReadString(evt, "participants_json", "[]")).Contains(npcId, StringComparer.OrdinalIgnoreCase)
                    && TextListFromJson(ReadString(evt, "participants_json", "[]")).Contains(playerId, StringComparer.OrdinalIgnoreCase),
                    "Dialogue event includes both conversation participants.", exchangeId);
                AddNpcDialogueAssertion(assertions, "memory_domains", memories.All(row => NpcDialogueAuditMemoryDomains.Contains(ReadString(row, "memory_domain", ""))), "All episodic memories use permitted domains.", string.Join(",", memories.Select(row => ReadString(row, "memory_domain", ""))));
                AddNpcDialogueAssertion(assertions, "memory_ownership",
                    memories.Any(row => string.Equals(ReadString(row, "owner_id", ""), npcId, StringComparison.OrdinalIgnoreCase))
                    && memories.All(row =>
                        new[] { npcId, playerId }.Contains(ReadString(row, "owner_id", ""), StringComparer.OrdinalIgnoreCase)
                        && ReadString(row, "event_id", "") == exchangeId),
                    "Every episodic memory is linked to the originating event and owned by a participant, including at least one NPC-owned record.", string.Join(",", memories.Select(row => ReadString(row, "owner_id", "") + ":" + ReadString(row, "memory_id", ""))));
                AddNpcDialogueAssertion(assertions, "memory_participants", memories.All(row =>
                    TextListFromJson(ReadString(row, "participants_json", "[]")).Contains(npcId, StringComparer.OrdinalIgnoreCase)
                    && TextListFromJson(ReadString(row, "participants_json", "[]")).Contains(playerId, StringComparer.OrdinalIgnoreCase)),
                    "Every episodic memory includes both conversation participants.", exchangeId);
                AddNpcDialogueAssertion(assertions, "memory_visibility", memories.All(row =>
                    new[] { "private", "public", "rumor" }.Contains(ReadString(row, "visibility", ""), StringComparer.OrdinalIgnoreCase)
                    && TextListFromJson(ReadString(row, "known_by_json", "[]")).Contains(ReadString(row, "owner_id", ""), StringComparer.OrdinalIgnoreCase)),
                    "Every episodic memory has valid visibility and remains known to its owner.", exchangeId);

                int queuedActions = ReadInt(payload, "queuedActionCount", 0);
                AddNpcDialogueAssertion(assertions, "no_false_positive_action", queuedActions == 0, "Non-action test prompt queued no world action.", queuedActions.ToString());
                string recallToken = ReadString(payload, "recallToken", "");
                if (!string.IsNullOrWhiteSpace(recallToken))
                {
                    AddNpcDialogueAssertion(assertions, "recall_context_available",
                        promptEvidence.ContainsKey("auditRecallTokenIncluded")
                            ? ReadBool(promptEvidence, "auditRecallTokenIncluded", false)
                            : promptJson.IndexOf(recallToken, StringComparison.OrdinalIgnoreCase) >= 0,
                        "The seeded anchor token was available in the actual model prompt.", recallToken);
                    AddNpcDialogueAssertion(assertions, "recall_exact_history_expansion",
                        promptEvidence.ContainsKey("containsExactConversationSource")
                            ? ReadBool(promptEvidence, "containsExactConversationSource", false)
                            : promptJson.IndexOf("Exact conversation source", StringComparison.OrdinalIgnoreCase) >= 0,
                        "The recall probe activated exact-history transcript expansion.", "Exact conversation source");
                    AddNpcDialogueAssertion(assertions, "recall_reply", npcReply.IndexOf(recallToken, StringComparison.OrdinalIgnoreCase) >= 0,
                        "NPC reply recalled the seeded anchor token.", recallToken);
                }

                Dictionary<string, object> turnResult = new Dictionary<string, object>
                {
                    ["turnIndex"] = ReadInt(payload, "turnIndex", 0),
                    ["sceneIndex"] = ReadInt(payload, "sceneIndex", 0),
                    ["correlationId"] = correlationId,
                    ["sessionId"] = sessionId,
                    ["exchangeId"] = exchangeId,
                    ["playerText"] = LimitText(playerText, 1200),
                    ["npcReply"] = LimitText(npcReply, 1800),
                    ["selectedContextPulls"] = selectedIds,
                    ["memoryIds"] = memories.Select(row => ReadString(row, "memory_id", "")).ToList(),
                    ["assertions"] = assertions,
                    ["passed"] = assertions.All(AssertionPassed)
                };
                List<Dictionary<string, object>> reportTurns = ReadDictionaryList(report, "turns");
                UpsertAuditRecord(reportTurns, turnResult, "turnIndex");
                report["turns"] = reportTurns;
                AppendAssertions(report, assertions);
                report["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                SaveNpcDialogueAudit(report);
                return new Dictionary<string, object> { ["ok"] = true, ["passed"] = ReadBool(turnResult, "passed", false), ["turn"] = turnResult, ["summary"] = NpcDialogueAuditSummary(report, false) };
            }
        }

        private static Dictionary<string, object> NpcDialogueAuditSceneApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string runId = ReadString(payload, "runId", "");
            string sessionId = ReadString(payload, "sessionId", "");
            lock (NpcDialogueAuditLock)
            {
                Dictionary<string, object> report = LoadNpcDialogueAudit(campaignId, runId);
                if (report == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "NPC dialogue audit was not found." };
                List<Dictionary<string, object>> assertions = new List<Dictionary<string, object>>();
                Dictionary<string, object> session;
                Dictionary<string, object> summary = null;
                Dictionary<string, object> middleTerm = null;
                int turns = 0, links = 0, fts = 0, embeddingJobs = 0, middleTermFts = 0, middleTermEmbeddingJobs = 0, unconsolidatedNpcMemories = 0;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    session = QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$id LIMIT 1;", new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault();
                    string summaryId = ReadString(session, "scene_summary_id", "");
                    summary = QuerySql(connection, "SELECT * FROM summaries WHERE summary_id=$id LIMIT 1;", new Dictionary<string, object> { ["id"] = summaryId }).FirstOrDefault();
                    turns = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM conversation_turns WHERE session_id=$id;", new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault(), "count", 0);
                    links = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM memory_sources WHERE document_type='summary' AND document_id=$id;", new Dictionary<string, object> { ["id"] = summaryId }).FirstOrDefault(), "count", 0);
                    fts = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM summary_fts WHERE summary_id=$id;", new Dictionary<string, object> { ["id"] = summaryId }).FirstOrDefault(), "count", 0);
                    embeddingJobs = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM embedding_jobs WHERE source_type='summary' AND source_id=$id;", new Dictionary<string, object> { ["id"] = summaryId }).FirstOrDefault(), "count", 0);
                    middleTerm = QuerySql(connection, "SELECT * FROM summaries WHERE scope=$scope AND summary_type='middle_term' ORDER BY updated_ts DESC LIMIT 1;",
                        new Dictionary<string, object> { ["scope"] = "conversation:" + sessionId }).FirstOrDefault();
                    string middleTermId = ReadString(middleTerm, "summary_id", "");
                    middleTermFts = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM summary_fts WHERE summary_id=$id;",
                        new Dictionary<string, object> { ["id"] = middleTermId }).FirstOrDefault(), "count", 0);
                    middleTermEmbeddingJobs = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM embedding_jobs WHERE source_type='summary' AND source_id=$id;",
                        new Dictionary<string, object> { ["id"] = middleTermId }).FirstOrDefault(), "count", 0);
                    unconsolidatedNpcMemories = ReadInt(QuerySql(connection, @"SELECT COUNT(*) count FROM memories m
JOIN conversation_turns t ON t.event_id=m.event_id
WHERE t.session_id=$session AND m.owner_id=$npc AND m.status<>'consolidated';",
                        new Dictionary<string, object> { ["session"] = sessionId, ["npc"] = ReadString(report, "npcId", "") }).FirstOrDefault(), "count", 0);
                }
                AddNpcDialogueAssertion(assertions, "scene_closed", session != null && ReadString(session, "status", "") != "open", "Conversation session is closed.", sessionId);
                AddNpcDialogueAssertion(assertions, "scene_turn_count", turns == ReadInt(payload, "expectedTurns", 12), "Scene contains the expected number of raw turns.", turns.ToString());
                AddNpcDialogueAssertion(assertions, "scene_summary", summary != null && ReadString(summary, "summary_type", "") == "scene", "Scene summary exists.", ReadString(session, "scene_summary_id", ""));
                AddNpcDialogueAssertion(assertions, "scene_lane", summary != null && MemoryLaneIds.Contains(ReadString(summary, "memory_lane", ""), StringComparer.OrdinalIgnoreCase), "Scene summary has a valid memory lane.", ReadString(summary, "memory_lane", ""));
                AddNpcDialogueAssertion(assertions, "scene_sources", links >= turns + 1, "Scene summary links every turn and its session.", links.ToString());
                AddNpcDialogueAssertion(assertions, "scene_fts", fts == 1, "Scene summary has one FTS row.", fts.ToString());
                string npcId = ReadString(report, "npcId", "");
                string playerId = ReadString(report, "playerId", "");
                AddNpcDialogueAssertion(assertions, "scene_ownership_visibility", summary != null
                    && string.Equals(ReadString(summary, "owner_id", ""), npcId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ReadString(summary, "visibility", ""), "private", StringComparison.OrdinalIgnoreCase)
                    && TextListFromJson(ReadString(summary, "participants_json", "[]")).Contains(npcId, StringComparer.OrdinalIgnoreCase)
                    && TextListFromJson(ReadString(summary, "participants_json", "[]")).Contains(playerId, StringComparer.OrdinalIgnoreCase)
                    && TextListFromJson(ReadString(summary, "known_by_json", "[]")).Contains(npcId, StringComparer.OrdinalIgnoreCase),
                    "Scene summary ownership, participants, visibility, and knowledge are correct.", ReadString(summary, "summary_id", ""));
                Dictionary<string, object> settings = LoadSettings();
                if (ReadBool(settings, "enableSemanticMemory", true))
                    AddNpcDialogueAssertion(assertions, "scene_embedding_job", embeddingJobs >= 1 || ReadString(summary, "embedding_status", "") == "indexed", "Scene summary is queued or indexed for semantic memory.", embeddingJobs.ToString());
                int minimumItems = Math.Max(1, ReadInt(settings, "memoryConsolidationMinItems", 4));
                bool middleTermExpected = turns / 2 >= minimumItems;
                AddNpcDialogueAssertion(assertions, "middle_term_threshold",
                    !middleTermExpected || middleTerm != null,
                    middleTermExpected ? "Configured consolidation threshold produced a middle-term summary." : "Scene remained below the configured consolidation threshold.",
                    "minimum=" + minimumItems + " exchanges=" + (turns / 2) + " summary=" + ReadString(middleTerm, "summary_id", ""));
                if (middleTermExpected && middleTerm != null)
                {
                    AddNpcDialogueAssertion(assertions, "middle_term_sources",
                        ReadInt(middleTerm, "event_count", 0) >= minimumItems
                        && TextListFromJson(ReadString(middleTerm, "source_events_json", "[]")).Count >= minimumItems,
                        "Middle-term summary retains complete source-event lineage.", ReadString(middleTerm, "source_events_json", "[]"));
                    AddNpcDialogueAssertion(assertions, "middle_term_consolidated", unconsolidatedNpcMemories == 0,
                        "NPC source memories consolidated by the middle-term pass are marked consolidated.", unconsolidatedNpcMemories.ToString());
                    AddNpcDialogueAssertion(assertions, "middle_term_fts", middleTermFts == 1,
                        "Middle-term summary has one FTS row.", middleTermFts.ToString());
                    if (ReadBool(settings, "enableSemanticMemory", true))
                        AddNpcDialogueAssertion(assertions, "middle_term_embedding_job",
                            middleTermEmbeddingJobs >= 1 || ReadString(middleTerm, "embedding_status", "") == "indexed",
                            "Middle-term summary is queued or indexed for semantic memory.", middleTermEmbeddingJobs.ToString());
                }

                Dictionary<string, object> sceneResult = new Dictionary<string, object>
                {
                    ["sceneIndex"] = ReadInt(payload, "sceneIndex", 0), ["sessionId"] = sessionId,
                    ["summaryId"] = ReadString(session, "scene_summary_id", ""), ["memoryLane"] = ReadString(summary, "memory_lane", ""),
                    ["middleTermSummaryId"] = ReadString(middleTerm, "summary_id", ""),
                    ["assertions"] = assertions, ["passed"] = assertions.All(AssertionPassed)
                };
                List<Dictionary<string, object>> reportScenes = ReadDictionaryList(report, "scenes");
                UpsertAuditRecord(reportScenes, sceneResult, "sceneIndex");
                report["scenes"] = reportScenes;
                AppendAssertions(report, assertions);
                report["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                SaveNpcDialogueAudit(report);
                return new Dictionary<string, object> { ["ok"] = true, ["passed"] = ReadBool(sceneResult, "passed", false), ["scene"] = sceneResult, ["summary"] = NpcDialogueAuditSummary(report, false) };
            }
        }

        private static Dictionary<string, object> NpcDialogueAuditFinishApi(Dictionary<string, object> payload)
        {
            return CompleteNpcDialogueAudit(payload, false);
        }

        private static Dictionary<string, object> NpcDialogueAuditCancelApi(Dictionary<string, object> payload)
        {
            return CompleteNpcDialogueAudit(payload, true);
        }

        private static Dictionary<string, object> CompleteNpcDialogueAudit(Dictionary<string, object> payload, bool cancelled)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string runId = ReadString(payload, "runId", "");
            lock (NpcDialogueAuditLock)
            {
                Dictionary<string, object> report = LoadNpcDialogueAudit(campaignId, runId);
                if (report == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "NPC dialogue audit was not found." };
                List<Dictionary<string, object>> assertions = new List<Dictionary<string, object>>();
                List<Dictionary<string, object>> turns = ReadDictionaryList(report, "turns");
                List<Dictionary<string, object>> scenes = ReadDictionaryList(report, "scenes");
                if (!cancelled)
                {
                    AddNpcDialogueAssertion(assertions, "run_turn_count", turns.Count == ReadInt(report, "expectedExchanges", 30), "Run completed every expected exchange.", turns.Count.ToString());
                    AddNpcDialogueAssertion(assertions, "run_scene_count", scenes.Count == ReadInt(report, "expectedScenes", 5), "Run completed every expected scene.", scenes.Count.ToString());
                    Dictionary<string, object> counts = NpcDialogueAuditCounts(campaignId, ReadString(report, "npcId", ""));
                    AddNpcDialogueAssertion(assertions, "run_scene_summaries", ReadInt(counts, "sceneSummaries", 0) >= ReadInt(ReadDictionary(report, "baseline"), "sceneSummaries", 0) + scenes.Count, "All test scenes produced summaries.", Json.Serialize(counts));
                    AddNpcDialogueAssertion(assertions, "run_middle_term_summaries",
                        ReadInt(counts, "middleTermSummaries", 0) >= ReadInt(ReadDictionary(report, "baseline"), "middleTermSummaries", 0) + scenes.Count,
                        "Every qualifying test scene produced a middle-term summary.", Json.Serialize(counts));
                    Dictionary<string, int> laneCounts = scenes.GroupBy(scene => ReadString(scene, "memoryLane", ""), StringComparer.OrdinalIgnoreCase)
                        .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
                    int relatedMax = laneCounts.Count == 0 ? 0 : laneCounts.Values.Max();
                    int baselineArcs = ReadInt(ReadDictionary(report, "baseline"), "arcSummaries", 0);
                    bool arcExists = ReadInt(counts, "arcSummaries", 0) > baselineArcs;
                    AddNpcDialogueAssertion(assertions, "rolling_arc_threshold", relatedMax < 4 || arcExists,
                        relatedMax >= 4 ? "A rolling arc exists after four related scenes." : "No lane reached four related scenes; the arc threshold correctly remained ineligible.",
                        "laneCounts=" + Json.Serialize(laneCounts) + " newArc=" + arcExists);

                    Dictionary<string, object> settings = LoadSettings();
                    if (ReadBool(settings, "enableSemanticMemory", true))
                    {
                        List<string> summaryIds = scenes.SelectMany(scene => new[]
                            {
                                ReadString(scene, "summaryId", ""),
                                ReadString(scene, "middleTermSummaryId", "")
                            })
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        Dictionary<string, object> semantic = WaitForNpcDialogueAuditEmbeddings(campaignId, summaryIds, 12000);
                        AddNpcDialogueAssertion(assertions, "semantic_worker_available", ReadBool(semantic, "workerAvailable", false),
                            "Configured semantic-memory worker is available.", ReadString(semantic, "workerError", ""));
                        AddNpcDialogueAssertion(assertions, "semantic_audit_summaries_indexed",
                            ReadInt(semantic, "indexed", 0) == summaryIds.Count,
                            "All audit scene and middle-term summaries were indexed within the bounded wait.",
                            Json.Serialize(semantic));
                        AddNpcDialogueAssertion(assertions, "semantic_audit_summary_retrieval",
                            ReadBool(semantic, "retrievalApplied", false)
                            && ReadBool(semantic, "retrievedExpectedSummary", false),
                            "An indexed audit summary is retrievable through the configured semantic-memory path.",
                            Json.Serialize(semantic));
                    }
                }
                AppendAssertions(report, assertions);
                List<Dictionary<string, object>> all = ReadDictionaryList(report, "assertions");
                int failed = all.Count(row => !AssertionPassed(row));
                report["status"] = cancelled ? "cancelled" : failed == 0 ? "completed" : "failed";
                report["completedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                report["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                report["finalCounts"] = NpcDialogueAuditCounts(campaignId, ReadString(report, "npcId", ""));
                SaveNpcDialogueAudit(report);
                return NpcDialogueAuditSummary(report, true);
            }
        }

        private static Dictionary<string, object> NpcDialogueAuditStatusApi(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>();
            string campaignId = query.TryGetValue("campaignId", out string campaign) ? campaign : ResolveLogCampaignId("");
            string runId = query.TryGetValue("runId", out string run) ? run : "";
            Dictionary<string, object> report = string.IsNullOrWhiteSpace(runId) ? FindLatestNpcDialogueAudit(campaignId) : LoadNpcDialogueAudit(campaignId, runId);
            return report == null
                ? new Dictionary<string, object> { ["ok"] = true, ["found"] = false, ["campaignId"] = campaignId }
                : NpcDialogueAuditSummary(report, true);
        }

        private static Dictionary<string, object> NpcDialogueAuditCounts(string campaignId, string npcId)
        {
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                return new Dictionary<string, object>
                {
                    ["sessions"] = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM conversation_sessions WHERE npc_id=$npc;", new Dictionary<string, object> { ["npc"] = npcId }).FirstOrDefault(), "count", 0),
                    ["turns"] = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM conversation_turns WHERE speaker_id=$npc OR participants_json LIKE $like;", new Dictionary<string, object> { ["npc"] = npcId, ["like"] = "%" + npcId + "%" }).FirstOrDefault(), "count", 0),
                    ["memories"] = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM memories WHERE owner_id=$npc;", new Dictionary<string, object> { ["npc"] = npcId }).FirstOrDefault(), "count", 0),
                    ["sceneSummaries"] = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM summaries WHERE owner_id=$npc AND summary_type='scene';", new Dictionary<string, object> { ["npc"] = npcId }).FirstOrDefault(), "count", 0),
                    ["middleTermSummaries"] = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM summaries WHERE owner_id=$npc AND summary_type='middle_term';", new Dictionary<string, object> { ["npc"] = npcId }).FirstOrDefault(), "count", 0),
                    ["arcSummaries"] = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM summaries WHERE owner_id=$npc AND summary_type='arc';", new Dictionary<string, object> { ["npc"] = npcId }).FirstOrDefault(), "count", 0)
                };
            }
        }

        private static int NpcDialogueAuditMaxSceneLaneCount(string campaignId, string npcId, string playerId)
        {
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                return ReadInt(QuerySql(connection, @"SELECT COUNT(*) count FROM summaries WHERE owner_id=$npc AND summary_type='scene'
AND participants_json LIKE $player GROUP BY memory_lane ORDER BY count DESC LIMIT 1;",
                    new Dictionary<string, object> { ["npc"] = npcId, ["player"] = "%" + playerId + "%" }).FirstOrDefault(), "count", 0);
            }
        }

        private static bool NpcDialogueAuditArcExists(string campaignId, string npcId, string playerId)
        {
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                return ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM summaries WHERE owner_id=$npc AND summary_type='arc' AND participants_json LIKE $player;",
                    new Dictionary<string, object> { ["npc"] = npcId, ["player"] = "%" + playerId + "%" }).FirstOrDefault(), "count", 0) > 0;
        }

        private static bool NpcDialoguePromptContainsContext(string promptJson, string contextPullId)
        {
            string prompt = promptJson ?? "";
            string id = contextPullId ?? "";
            if (string.IsNullOrWhiteSpace(id)) return false;
            if (string.Equals(id, "relevant_memory", StringComparison.OrdinalIgnoreCase))
                return prompt.IndexOf("REIGN KNOWLEDGE PACKET", StringComparison.OrdinalIgnoreCase) >= 0;
            if (string.Equals(id, "relationship_history", StringComparison.OrdinalIgnoreCase))
                return prompt.IndexOf("Relationship state", StringComparison.OrdinalIgnoreCase) >= 0
                    || prompt.IndexOf("relationshipNpcToTarget", StringComparison.OrdinalIgnoreCase) >= 0;
            string readable = id.Replace('_', ' ');
            return prompt.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0
                || prompt.IndexOf(readable, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Dictionary<string, object> BuildNpcDialoguePromptAuditEvidence(
            string campaignId,
            Dictionary<string, object> requestPayload,
            List<Dictionary<string, object>> messages,
            List<Dictionary<string, object>> selectedContextPulls)
        {
            string fullPrompt = string.Join("\n", (messages ?? new List<Dictionary<string, object>>())
                .Select(message => ReadString(message, "content", "")));
            Dictionary<string, object> contextEvidence = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in (selectedContextPulls ?? new List<Dictionary<string, object>>())
                .Select(row => ReadString(row, "id", ""))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                contextEvidence[id] = NpcDialoguePromptContainsContext(fullPrompt, id);
            }

            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["evaluatedBeforeAuditTruncation"] = true,
                ["fullPromptChars"] = fullPrompt.Length,
                ["playerTextIncluded"] = !string.IsNullOrWhiteSpace(ReadString(requestPayload, "playerText", ""))
                    && fullPrompt.IndexOf(ReadString(requestPayload, "playerText", ""), StringComparison.OrdinalIgnoreCase) >= 0,
                ["contextPulls"] = contextEvidence,
                ["containsKnowledgePacket"] = fullPrompt.IndexOf("REIGN KNOWLEDGE PACKET", StringComparison.OrdinalIgnoreCase) >= 0,
                ["containsExactConversationSource"] = fullPrompt.IndexOf("Exact conversation source", StringComparison.OrdinalIgnoreCase) >= 0
            };
            List<Dictionary<string, object>> groupTranscript = ReadDictionaryList(requestPayload, "groupTranscript");
            string currentExchangeId = ReadFirstString(requestPayload, "sceneTurnId", "turnId", "exchangeId");
            string currentSpeakerId = ReadFirstString(requestPayload, "speakerHeroStringId", "heroStringId");
            evidence["groupTranscriptCount"] = groupTranscript.Count;
            evidence["groupTranscriptAttributed"] = groupTranscript.All(line =>
                !string.IsNullOrWhiteSpace(ReadString(line, "speaker", ""))
                && !string.IsNullOrWhiteSpace(ReadString(line, "role", ""))
                && !string.IsNullOrWhiteSpace(ReadString(line, "text", ""))
                && (string.Equals(ReadString(line, "role", ""), "system", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(ReadFirstString(line, "speakerHeroStringId", "speaker_id", "heroStringId"))));
            evidence["currentExchangePriorNpcCount"] = groupTranscript.Count(line =>
                string.Equals(ReadString(line, "role", ""), "npc", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ReadFirstString(line, "exchangeId", "exchange_id"), currentExchangeId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(ReadFirstString(line, "speakerHeroStringId", "speaker_id", "heroStringId"), currentSpeakerId, StringComparison.OrdinalIgnoreCase));
            string runId = ReadString(requestPayload, "dialogueAuditRunId", "");
            if (!string.IsNullOrWhiteSpace(runId))
            {
                Dictionary<string, object> report = LoadNpcDialogueAudit(campaignId, runId);
                int seed = ReadInt(report, "seed", 0);
                string token = "Juniper" + Math.Abs((long)seed % 10000L).ToString("0000", CultureInfo.InvariantCulture);
                evidence["dialogueAuditRunId"] = runId;
                evidence["auditRecallTokenIncluded"] = fullPrompt.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return evidence;
        }

        private static Dictionary<string, object> WaitForNpcDialogueAuditEmbeddings(string campaignId, List<string> summaryIds, int timeoutMs)
        {
            summaryIds = (summaryIds ?? new List<string>()).Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["expected"] = summaryIds.Count,
                ["indexed"] = 0,
                ["pendingIds"] = summaryIds,
                ["waitedMs"] = 0,
                ["workerAvailable"] = false,
                ["workerError"] = ""
            };
            Dictionary<string, object> settings = LoadSettings();
            try
            {
                Dictionary<string, object> health = TryParseJsonObject(GetTextFromUrl(
                    SemanticWorkerUrl(settings, "/health"), 1500)) ?? new Dictionary<string, object>();
                result["workerAvailable"] = ReadBool(health, "ok", false)
                    && ReadBool(health, "modelLoaded", false);
                result["workerError"] = ReadString(health, "lastError", "");
                if (ReadBool(result, "workerAvailable", false))
                {
                    SemanticWorkerLastHealthyUtc = DateTime.UtcNow;
                    SemanticWorkerUnavailableUntilUtc = DateTime.MinValue;
                }
            }
            catch (Exception ex)
            {
                result["workerError"] = ex.Message;
                return result;
            }

            Stopwatch timer = Stopwatch.StartNew();
            int pumpAttempts = 0;
            while (timer.ElapsedMilliseconds <= Math.Max(0, timeoutMs))
            {
                List<string> indexed;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    indexed = summaryIds.Where(id =>
                    {
                        Dictionary<string, object> row = QuerySql(connection,
                            "SELECT embedding_status FROM summaries WHERE summary_id=$id LIMIT 1;",
                            new Dictionary<string, object> { ["id"] = id }).FirstOrDefault();
                        return string.Equals(ReadString(row, "embedding_status", ""), "indexed", StringComparison.OrdinalIgnoreCase);
                    }).ToList();
                }
                result["indexed"] = indexed.Count;
                result["pendingIds"] = summaryIds.Except(indexed, StringComparer.OrdinalIgnoreCase).ToList();
                result["waitedMs"] = timer.ElapsedMilliseconds;
                if (indexed.Count == summaryIds.Count || timer.ElapsedMilliseconds >= timeoutMs) break;
                bool entered = false;
                try
                {
                    entered = Monitor.TryEnter(SemanticJobProcessingLock, 500);
                    if (entered)
                    {
                        pumpAttempts++;
                        ProcessSemanticJobs(campaignId, settings);
                    }
                }
                catch (Exception ex)
                {
                    result["pumpError"] = ex.Message;
                }
                finally
                {
                    if (entered) Monitor.Exit(SemanticJobProcessingLock);
                }
                Thread.Sleep(250);
            }
            result["pumpAttempts"] = pumpAttempts;
            if (ReadInt(result, "indexed", 0) == summaryIds.Count && summaryIds.Count > 0)
            {
                string probeId = summaryIds[summaryIds.Count - 1];
                string probeText = "";
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    Dictionary<string, object> probe = QuerySql(connection,
                        "SELECT summary FROM summaries WHERE summary_id=$id LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = probeId }).FirstOrDefault();
                    probeText = ReadString(probe, "summary", "");
                }
                Dictionary<string, object> route = new Dictionary<string, object>
                {
                    ["primaryLane"] = "interpersonal_history",
                    ["selectedLanes"] = MemoryLaneIds.Where(id => !string.Equals(id, "exact_history", StringComparison.OrdinalIgnoreCase)).ToList(),
                    ["supportingLanes"] = new List<string>()
                };
                Dictionary<string, object> retrieval = TrySemanticMemorySearch(campaignId, probeText, route, settings, new[] { "summary" });
                List<string> retrievedIds = ReadDictionaryList(retrieval, "results")
                    .Select(hit => ReadString(ReadDictionary(hit, "payload"), "sourceId", ""))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                result["retrievalApplied"] = ReadBool(retrieval, "applied", false);
                result["retrievalReason"] = ReadString(retrieval, "reason", "");
                result["retrievalError"] = ReadString(retrieval, "error", "");
                result["retrievedExpectedSummary"] = retrievedIds.Contains(probeId, StringComparer.OrdinalIgnoreCase);
                result["retrievedSourceIds"] = retrievedIds.Take(20).ToList();
            }
            return result;
        }

        private static void AddNpcDialogueAssertion(List<Dictionary<string, object>> target, string id, bool passed, string message, string evidence)
        {
            target.Add(new Dictionary<string, object> { ["id"] = id, ["passed"] = passed, ["message"] = message, ["evidence"] = LimitText(evidence ?? "", 2000) });
        }

        private static bool AssertionPassed(Dictionary<string, object> assertion)
        {
            return ReadBool(assertion, "passed", false);
        }

        private static void AppendAssertions(Dictionary<string, object> report, List<Dictionary<string, object>> assertions)
        {
            List<Dictionary<string, object>> all = ReadDictionaryList(report, "assertions");
            all.AddRange(assertions ?? new List<Dictionary<string, object>>());
            report["assertions"] = all;
        }

        private static void UpsertAuditRecord(List<Dictionary<string, object>> rows, Dictionary<string, object> value, string key)
        {
            int id = ReadInt(value, key, -1);
            rows.RemoveAll(row => ReadInt(row, key, -2) == id);
            rows.Add(value);
        }

        private static Dictionary<string, object> NpcDialogueAuditSummary(Dictionary<string, object> report, bool includeReport)
        {
            List<Dictionary<string, object>> assertions = ReadDictionaryList(report, "assertions");
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["ok"] = true, ["found"] = true, ["runId"] = ReadString(report, "runId", ""),
                ["campaignId"] = ReadString(report, "campaignId", ""), ["npcId"] = ReadString(report, "npcId", ""),
                ["npcName"] = ReadString(report, "npcName", ""), ["seed"] = ReadLong(report, "seed", 0),
                ["status"] = ReadString(report, "status", "running"), ["turnCount"] = ReadDictionaryList(report, "turns").Count,
                ["sceneCount"] = ReadDictionaryList(report, "scenes").Count, ["assertionCount"] = assertions.Count,
                ["passed"] = assertions.Count(AssertionPassed), ["failed"] = assertions.Count(row => !AssertionPassed(row)),
                ["reportPath"] = NpcDialogueAuditPath(ReadString(report, "campaignId", ""), ReadString(report, "runId", "")),
                ["updatedUtc"] = ReadString(report, "updatedUtc", "")
            };
            if (includeReport) result["report"] = report;
            return result;
        }

        private static string NpcDialogueAuditDirectory(string campaignId)
        {
            return CampaignFile(campaignId, "tests", "npc-dialogue-audits");
        }

        private static string NpcDialogueAuditRequestPath(string campaignId)
        {
            return CampaignFile(campaignId, "tests", "npc-dialogue-audits", "requests", "current.json");
        }

        private static Dictionary<string, object> LoadNpcDialogueAuditRequest(string campaignId)
        {
            string path = NpcDialogueAuditRequestPath(campaignId);
            if (!File.Exists(path)) return null;
            Dictionary<string, object> request = ReadJsonObject(path);
            return request.Count == 0 ? null : request;
        }

        private static void SaveNpcDialogueAuditRequest(Dictionary<string, object> request)
        {
            string path = NpcDialogueAuditRequestPath(ReadString(request, "campaignId", "default"));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            WriteJsonObject(path, request);
        }

        private static Dictionary<string, object> NpcDialogueAuditRequestSummary(Dictionary<string, object> request)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["found"] = true,
                ["requestId"] = ReadString(request, "requestId", ""),
                ["campaignId"] = ReadString(request, "campaignId", ""),
                ["heroSearch"] = ReadString(request, "heroSearch", ""),
                ["seed"] = ReadLong(request, "seed", 0),
                ["status"] = ReadString(request, "status", "queued"),
                ["heroId"] = ReadString(request, "heroId", ""),
                ["heroName"] = ReadString(request, "heroName", ""),
                ["message"] = ReadString(request, "message", ""),
                ["updatedUtc"] = ReadString(request, "updatedUtc", "")
            };
        }

        private static string NpcDialogueAuditPath(string campaignId, string runId)
        {
            return Path.Combine(NpcDialogueAuditDirectory(campaignId), SafePathSegment(runId, "latest") + ".json");
        }

        private static string NpcDialogueAuditResponsePath(string campaignId, string correlationId)
        {
            string key = NpcDialogueAuditResponseKey(correlationId);
            return CampaignFile(campaignId, "tests", "npc-dialogue-audits", "responses", key + ".json");
        }

        private static string NpcDialogueAuditResponseKey(string correlationId)
        {
            const int maximumKeyLength = 96;
            const int digestLength = 24;
            string original = string.IsNullOrWhiteSpace(correlationId)
                ? "response"
                : correlationId.Trim();
            string safe = SafePathSegment(original, "response");
            if (safe.Length <= maximumKeyLength) return safe;

            string digest;
            using (SHA256 sha = SHA256.Create())
            {
                digest = BitConverter.ToString(
                        sha.ComputeHash(Encoding.UTF8.GetBytes(original)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant()
                    .Substring(0, digestLength);
            }

            return safe.Substring(0, maximumKeyLength - digestLength - 1)
                + "_" + digest;
        }

        private static Dictionary<string, object> ReadNpcDialogueAuditResponse(string campaignId, string correlationId)
        {
            string path = NpcDialogueAuditResponsePath(campaignId, correlationId);
            if (!File.Exists(path)) return null;
            Dictionary<string, object> response = ReadJsonObject(path);
            return response.Count == 0 ? null : response;
        }

        private static void SaveNpcDialogueAuditResponse(string campaignId, string correlationId, Dictionary<string, object> response)
        {
            WriteJsonObject(NpcDialogueAuditResponsePath(campaignId, correlationId), response ?? new Dictionary<string, object>());
        }

        private static void SaveNpcDialogueAudit(Dictionary<string, object> report)
        {
            string path = NpcDialogueAuditPath(ReadString(report, "campaignId", "default"), ReadString(report, "runId", "latest"));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            WriteJsonObject(path, report);
        }

        private static Dictionary<string, object> LoadNpcDialogueAudit(string campaignId, string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) return FindLatestNpcDialogueAudit(campaignId);
            string path = NpcDialogueAuditPath(campaignId, runId);
            return File.Exists(path) ? ReadJsonObject(path) : null;
        }

        private static Dictionary<string, object> FindActiveNpcDialogueAudit(string campaignId)
        {
            return EnumerateNpcDialogueAudits(campaignId).FirstOrDefault(report => ReadString(report, "status", "") == "running");
        }

        private static Dictionary<string, object> FindLatestNpcDialogueAudit(string campaignId)
        {
            return EnumerateNpcDialogueAudits(campaignId).OrderByDescending(report => ReadString(report, "updatedUtc", "")).FirstOrDefault();
        }

        private static IEnumerable<Dictionary<string, object>> EnumerateNpcDialogueAudits(string campaignId)
        {
            string directory = NpcDialogueAuditDirectory(campaignId);
            if (!Directory.Exists(directory)) return Enumerable.Empty<Dictionary<string, object>>();
            return Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .Select(ReadJsonObject).Where(report => report != null && report.Count > 0).ToList();
        }

        private static List<Dictionary<string, object>> RunNpcDialogueAuditSelfTests()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => rows.Add(new Dictionary<string, object>
            {
                ["id"] = "npc_dialogue_audit_" + id, ["suite"] = "npc_dialogue_audit", ["passed"] = passed, ["summary"] = summary
            });
            string campaignId = "nda_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string runId = "r_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                string requestId = "q_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                Dictionary<string, object> queued = NpcDialogueAuditRequestApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["requestId"] = requestId, ["heroSearch"] = "npc_a", ["seed"] = 1337
                });
                Dictionary<string, object> idempotent = NpcDialogueAuditRequestApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["requestId"] = requestId, ["heroSearch"] = "npc_a", ["seed"] = 1337
                });
                Dictionary<string, object> competing = NpcDialogueAuditRequestApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["requestId"] = "other_request", ["heroSearch"] = "npc_b"
                });
                Dictionary<string, object> polled = NpcDialogueAuditRequestPollApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId
                });
                add("request_queue", ReadBool(queued, "ok", false)
                    && ReadBool(idempotent, "ok", false)
                    && !ReadBool(competing, "ok", true)
                    && ReadBool(polled, "found", false),
                    "Remote audit requests are durable, idempotent, and single-queued per campaign.");

                Dictionary<string, object> acknowledged = NpcDialogueAuditRequestAckApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["requestId"] = requestId, ["status"] = "accepted",
                    ["heroId"] = "npc_a", ["heroName"] = "NPC A"
                });
                Dictionary<string, object> afterAck = NpcDialogueAuditRequestPollApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId
                });
                add("request_ack", ReadString(acknowledged, "status", "") == "accepted"
                    && !ReadBool(afterAck, "found", true),
                    "An accepted request is not returned to the game again.");

                Dictionary<string, object> start = NpcDialogueAuditStartApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["runId"] = runId, ["npcId"] = "npc_a", ["playerId"] = "player",
                    ["seed"] = 1337, ["expectedExchanges"] = 30, ["expectedScenes"] = 5
                });
                Dictionary<string, object> duplicate = NpcDialogueAuditStartApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["runId"] = "other_run", ["npcId"] = "npc_b", ["playerId"] = "player"
                });
                add("single_run_lock", ReadBool(start, "ok", false) && !ReadBool(duplicate, "ok", true),
                    "Only one active NPC dialogue audit is allowed per campaign.");

                Dictionary<string, object> cancelled = NpcDialogueAuditCancelApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["runId"] = runId
                });
                Dictionary<string, object> status = NpcDialogueAuditStatusApi(new Dictionary<string, string>
                {
                    ["campaignId"] = campaignId, ["runId"] = runId
                });
                add("partial_report", ReadString(cancelled, "status", "") == "cancelled"
                    && ReadBool(status, "found", false) && File.Exists(ReadString(status, "reportPath", "")),
                    "Cancelled runs remain available as durable partial reports.");

                List<Dictionary<string, object>> assertions = new List<Dictionary<string, object>>();
                AddNpcDialogueAssertion(assertions, "pass", true, "pass", "evidence");
                AddNpcDialogueAssertion(assertions, "fail", false, "fail", "evidence");
                add("assertion_totals", assertions.Count(AssertionPassed) == 1 && assertions.Count(row => !AssertionPassed(row)) == 1,
                    "Assertion aggregation preserves both passing and failing evidence.");

                string longCorrelationPrefix = new string('x', 110);
                string firstLongKey = NpcDialogueAuditResponseKey(
                    longCorrelationPrefix + "_first-turn");
                string secondLongKey = NpcDialogueAuditResponseKey(
                    longCorrelationPrefix + "_follow-up-turn");
                add("long_correlation_idempotency_keys",
                    firstLongKey.Length <= 96
                    && secondLongKey.Length <= 96
                    && !string.Equals(firstLongKey, secondLongKey,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(NpcDialogueAuditResponseKey("short-id"),
                        "short-id", StringComparison.Ordinal),
                    "Long audit correlations retain a bounded readable prefix plus a stable hash so adjacent natural dialogue turns cannot replay each other's response.");

                Dictionary<string, object> untruncatedEvidence = BuildNpcDialoguePromptAuditEvidence(campaignId,
                    new Dictionary<string, object>(),
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["role"] = "user",
                            ["content"] = new string('x', AuditMaxStringChars + 2000)
                                + "\nREIGN KNOWLEDGE PACKET\nExact conversation source\nJuniper9999"
                        }
                    },
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["id"] = "relevant_memory" }
                    });
                add("untruncated_prompt_evidence",
                    ReadBool(untruncatedEvidence, "evaluatedBeforeAuditTruncation", false)
                    && ReadBool(untruncatedEvidence, "containsKnowledgePacket", false)
                    && ReadBool(untruncatedEvidence, "containsExactConversationSource", false)
                    && ReadBool(ReadDictionary(untruncatedEvidence, "contextPulls"), "relevant_memory", false),
                    "Prompt evidence is evaluated against the complete model prompt before bounded audit serialization.");

                string continuityPrompt = "Has anything in our earlier conversations changed your trust or opinion of me?";
                List<string> continuityPulls = PruneContextPullSelection(continuityPrompt,
                        DeterministicContextPullSelection("dialogue", continuityPrompt, "", ContextPullIds.ToList()))
                    .Select(row => ReadString(row, "id", "")).ToList();
                string roadPrompt = "What should a traveler notice about this place and the roads around it?";
                List<string> roadPulls = PruneContextPullSelection(roadPrompt,
                        DeterministicContextPullSelection("dialogue", roadPrompt, "", ContextPullIds.ToList()))
                    .Select(row => ReadString(row, "id", "")).ToList();
                string nearbyLordPrompt = "Which lords, armies, or patrols are nearby right now?";
                List<string> nearbyLordPulls = PruneContextPullSelection(nearbyLordPrompt,
                        DeterministicContextPullSelection("dialogue", nearbyLordPrompt, "", ContextPullIds.ToList()))
                    .Select(row => ReadString(row, "id", "")).ToList();
                string worldHistoryPrompt = "I claim that I won a famous battle yesterday. Does reliable world history support that claim?";
                List<string> worldHistoryPulls = PruneContextPullSelection(worldHistoryPrompt,
                        DeterministicContextPullSelection("dialogue", worldHistoryPrompt, "", ContextPullIds.ToList()))
                    .Select(row => ReadString(row, "id", "")).ToList();
                add("context_pruning",
                    continuityPulls.Contains("relationship_history", StringComparer.OrdinalIgnoreCase)
                    && continuityPulls.Contains("relevant_memory", StringComparer.OrdinalIgnoreCase)
                    && roadPulls.Contains("nearby_settlements", StringComparer.OrdinalIgnoreCase)
                    && nearbyLordPulls.Contains("nearby_lord_parties", StringComparer.OrdinalIgnoreCase)
                    && !nearbyLordPulls.Contains("check_player_appearance_status", StringComparer.OrdinalIgnoreCase)
                    && worldHistoryPulls.Contains("verify_world_history", StringComparer.OrdinalIgnoreCase)
                    && !worldHistoryPulls.Contains("clan_wealth_and_influence", StringComparer.OrdinalIgnoreCase),
                    "Deterministic selector pruning preserves cross-scene memory and roads-around settlement helpers.");
            }
            catch (Exception ex)
            {
                add("fixture", false, "NPC dialogue audit fixture failed: " + LimitText(ex.Message, 400));
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
