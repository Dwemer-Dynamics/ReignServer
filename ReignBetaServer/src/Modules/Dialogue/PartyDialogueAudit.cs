using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly object PartyDialogueAuditLock = new object();

        private static Dictionary<string, object> PartyDialogueAuditRequestApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            List<string> searches = ReadStringList(payload, "heroSearches")
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
            if (string.IsNullOrWhiteSpace(campaignId))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "campaignId is required." };
            if (searches.Count < 2)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "At least two heroSearches are required." };

            string requestId = FirstNonEmpty(ReadString(payload, "requestId", ""), "party-dialogue-request-" + Guid.NewGuid().ToString("N"));
            lock (PartyDialogueAuditLock)
            {
                Dictionary<string, object> active = FindActivePartyDialogueAudit(campaignId);
                if (active != null)
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "A party dialogue audit is already active.", ["activeRunId"] = ReadString(active, "runId", "") };
                Dictionary<string, object> existing = LoadPartyDialogueAuditRequest(campaignId);
                if (existing != null && string.Equals(ReadString(existing, "status", ""), "queued", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(ReadString(existing, "requestId", ""), requestId, StringComparison.OrdinalIgnoreCase))
                        return PartyDialogueAuditRequestSummary(existing);
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Another party dialogue audit request is already queued.", ["requestId"] = ReadString(existing, "requestId", "") };
                }

                Dictionary<string, object> request = new Dictionary<string, object>
                {
                    ["requestId"] = requestId,
                    ["campaignId"] = campaignId,
                    ["heroSearches"] = searches,
                    ["seed"] = ReadLong(payload, "seed", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                    ["status"] = "queued",
                    ["queuedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o")
                };
                SavePartyDialogueAuditRequest(request);
                return PartyDialogueAuditRequestSummary(request);
            }
        }

        private static Dictionary<string, object> PartyDialogueAuditRequestPollApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            if (string.IsNullOrWhiteSpace(campaignId))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "campaignId is required." };
            lock (PartyDialogueAuditLock)
            {
                Dictionary<string, object> request = LoadPartyDialogueAuditRequest(campaignId);
                if (request == null || !string.Equals(ReadString(request, "status", ""), "queued", StringComparison.OrdinalIgnoreCase))
                    return new Dictionary<string, object> { ["ok"] = true, ["found"] = false, ["campaignId"] = campaignId };
                return PartyDialogueAuditRequestSummary(request);
            }
        }

        private static Dictionary<string, object> PartyDialogueAuditRequestAckApi(Dictionary<string, object> payload)
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
            lock (PartyDialogueAuditLock)
            {
                Dictionary<string, object> request = LoadPartyDialogueAuditRequest(campaignId);
                if (request == null || !string.Equals(ReadString(request, "requestId", ""), requestId, StringComparison.OrdinalIgnoreCase))
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Party dialogue request was not found." };
                request["status"] = status.ToLowerInvariant();
                request["heroes"] = ReadDictionaryList(payload, "heroes");
                request["message"] = ReadString(payload, "message", "");
                request["acknowledgedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                request["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                SavePartyDialogueAuditRequest(request);
                return PartyDialogueAuditRequestSummary(request);
            }
        }

        private static Dictionary<string, object> PartyDialogueAuditStartApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string runId = FirstNonEmpty(ReadString(payload, "runId", ""), "party-dialogue-" + Guid.NewGuid().ToString("N"));
            List<Dictionary<string, object>> heroes = ReadDictionaryList(payload, "heroes")
                .Where(hero => !string.IsNullOrWhiteSpace(ReadFirstString(hero, "heroId", "heroStringId")))
                .GroupBy(hero => ReadFirstString(hero, "heroId", "heroStringId"), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).Take(5).ToList();
            if (heroes.Count < 2)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "At least two unique heroes are required." };
            lock (PartyDialogueAuditLock)
            {
                Dictionary<string, object> active = FindActivePartyDialogueAudit(campaignId);
                if (active != null && !string.Equals(ReadString(active, "runId", ""), runId, StringComparison.OrdinalIgnoreCase))
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Another party dialogue audit is active.", ["activeRunId"] = ReadString(active, "runId", "") };

                List<string> heroIds = heroes.Select(hero => ReadFirstString(hero, "heroId", "heroStringId")).ToList();
                Dictionary<string, object> report = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["runId"] = runId,
                    ["campaignId"] = campaignId,
                    ["heroes"] = heroes,
                    ["heroIds"] = heroIds,
                    ["playerId"] = ReadFirstString(payload, "playerId", "playerHeroStringId"),
                    ["seed"] = ReadLong(payload, "seed", 0),
                    ["expectedExchanges"] = ReadInt(payload, "expectedExchanges", 15),
                    ["expectedScenes"] = ReadInt(payload, "expectedScenes", 5),
                    ["expectedNpcReplies"] = ReadInt(payload, "expectedNpcReplies", heroes.Count * 15),
                    ["status"] = "running",
                    ["startedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["turns"] = new List<Dictionary<string, object>>(),
                    ["scenes"] = new List<Dictionary<string, object>>(),
                    ["assertions"] = new List<Dictionary<string, object>>(),
                    ["baseline"] = PartyDialogueAuditCounts(campaignId, heroIds)
                };
                SavePartyDialogueAudit(report);
                return PartyDialogueAuditSummary(report, true);
            }
        }

        private static Dictionary<string, object> PartyDialogueAuditTurnApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string runId = ReadString(payload, "runId", "");
            lock (PartyDialogueAuditLock)
            {
                Dictionary<string, object> report = LoadPartyDialogueAudit(campaignId, runId);
                if (report == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Party dialogue audit was not found." };

                string sessionId = ReadString(payload, "sessionId", "");
                string exchangeId = ReadString(payload, "exchangeId", "");
                string playerText = ReadString(payload, "playerText", "");
                List<string> heroIds = ReadStringList(report, "heroIds");
                string playerId = ReadString(report, "playerId", "");
                List<Dictionary<string, object>> replies = ReadDictionaryList(payload, "replies");
                List<string> recallTokens = ReadStringList(payload, "recallTokens");
                bool requireEverySpeaker = ReadBool(payload, "requireEverySpeakerRecall", false);
                List<Dictionary<string, object>> assertions = new List<Dictionary<string, object>>();
                List<Dictionary<string, object>> turns;
                Dictionary<string, int> turnFts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                List<Dictionary<string, object>> memories = new List<Dictionary<string, object>>();
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    turns = QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$session AND exchange_id=$exchange ORDER BY turn_order;",
                        new Dictionary<string, object> { ["session"] = sessionId, ["exchange"] = exchangeId });
                    foreach (Dictionary<string, object> turn in turns)
                    {
                        string turnId = ReadString(turn, "turn_id", "");
                        turnFts[turnId] = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM conversation_turn_fts WHERE turn_id=$id;",
                            new Dictionary<string, object> { ["id"] = turnId }).FirstOrDefault(), "count", 0);
                    }
                    List<string> eventIds = turns.Select(turn => ReadString(turn, "event_id", "")).Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (string eventId in eventIds)
                    {
                        memories.AddRange(QuerySql(connection, "SELECT * FROM memories WHERE event_id=$id;", new Dictionary<string, object> { ["id"] = eventId }));
                    }
                }

                Dictionary<string, object> playerTurn = turns.FirstOrDefault(turn => ReadString(turn, "role", "") == "player");
                List<Dictionary<string, object>> npcTurns = turns.Where(turn => ReadString(turn, "role", "") == "npc").ToList();
                AddNpcDialogueAssertion(assertions, "party_turn_count", turns.Count == heroIds.Count + 1, "A grouped exchange stores one player turn plus one turn per selected NPC.", turns.Count.ToString());
                AddNpcDialogueAssertion(assertions, "party_single_player_turn", turns.Count(turn => ReadString(turn, "role", "") == "player") == 1, "Grouped storage does not duplicate the player line.", turns.Count(turn => ReadString(turn, "role", "") == "player").ToString());
                AddNpcDialogueAssertion(assertions, "party_npc_speakers", heroIds.All(id => npcTurns.Count(turn => string.Equals(ReadString(turn, "speaker_id", ""), id, StringComparison.OrdinalIgnoreCase)) == 1), "Every selected NPC has exactly one stored reply.", string.Join(",", npcTurns.Select(turn => ReadString(turn, "speaker_id", ""))));
                AddNpcDialogueAssertion(assertions, "party_player_text", playerTurn != null && ReadString(playerTurn, "text", "") == playerText, "The grouped player line is exact.", playerText);
                AddNpcDialogueAssertion(assertions, "party_reply_text", replies.All(reply => npcTurns.Any(turn =>
                    string.Equals(ReadString(turn, "speaker_id", ""), ReadFirstString(reply, "heroId", "heroStringId"), StringComparison.OrdinalIgnoreCase)
                    && ReadString(turn, "text", "") == ReadString(reply, "text", ""))), "Stored NPC reply text matches every returned reply.", string.Join(",", replies.Select(reply => ReadFirstString(reply, "heroId", "heroStringId"))));
                AddNpcDialogueAssertion(assertions, "party_channel", turns.All(turn => ReadString(turn, "channel", "") == "party_chat"), "Every grouped turn uses the party_chat channel.", string.Join(",", turns.Select(turn => ReadString(turn, "channel", ""))));
                AddNpcDialogueAssertion(assertions, "party_session_exchange", turns.All(turn => ReadString(turn, "session_id", "") == sessionId && ReadString(turn, "exchange_id", "") == exchangeId), "Every grouped turn shares the requested session and exchange IDs.", sessionId + "," + exchangeId);
                AddNpcDialogueAssertion(assertions, "party_participants", turns.All(turn =>
                {
                    List<string> participants = TextListFromJson(ReadString(turn, "participants_json", "[]"));
                    return heroIds.Concat(new[] { playerId }).All(id => participants.Contains(id, StringComparer.OrdinalIgnoreCase));
                }), "Every turn carries the full group participant set.", string.Join(",", heroIds.Concat(new[] { playerId })));
                AddNpcDialogueAssertion(assertions, "party_location", turns.All(turn => !string.IsNullOrWhiteSpace(ReadString(turn, "location_id", ""))), "Every grouped turn retains the native location ID.", string.Join(",", turns.Select(turn => ReadString(turn, "location_id", ""))));
                AddNpcDialogueAssertion(assertions, "party_turn_fts", turns.All(turn => turnFts.ContainsKey(ReadString(turn, "turn_id", "")) && turnFts[ReadString(turn, "turn_id", "")] == 1), "Every grouped turn has exactly one FTS row.", Json.Serialize(turnFts));
                AddNpcDialogueAssertion(assertions, "party_no_false_actions", replies.All(reply => ReadInt(reply, "queuedActionCount", 0) == 0), "Harmless party prompts queued no world actions.", string.Join(",", replies.Select(reply => ReadInt(reply, "queuedActionCount", 0))));

                Dictionary<string, List<string>> pullsByHero = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                for (int replyIndex = 0; replyIndex < replies.Count; replyIndex++)
                {
                    Dictionary<string, object> reply = replies[replyIndex];
                    string heroId = ReadFirstString(reply, "heroId", "heroStringId");
                    string correlationId = ReadString(reply, "correlationId", "");
                    List<Dictionary<string, object>> auditRows = ReadJsonLinesFromPath(CampaignFile(campaignId, "audit", "audit.jsonl"))
                        .Where(row => string.Equals(ReadString(row, "correlationId", ""), correlationId, StringComparison.OrdinalIgnoreCase)).ToList();
                    AddNpcDialogueAssertion(assertions, "party_audit_context_" + SafeMemoryKey(heroId), auditRows.Any(row => ReadString(row, "phase", "") == "context.loaded"), "Speaker context was loaded.", correlationId);
                    AddNpcDialogueAssertion(assertions, "party_audit_prompt_" + SafeMemoryKey(heroId), auditRows.Any(row => ReadString(row, "phase", "") == "prompt.built"), "Speaker prompt was audited.", correlationId);
                    AddNpcDialogueAssertion(assertions, "party_audit_parse_" + SafeMemoryKey(heroId), auditRows.Any(row => ReadString(row, "phase", "") == "model.parsed" && ReadString(row, "status", "") != "warning"), "Speaker returned structured model JSON.", correlationId);
                    AddNpcDialogueAssertion(assertions, "party_audit_response_" + SafeMemoryKey(heroId), auditRows.Any(row => ReadString(row, "phase", "") == "party_chat.response" && ReadString(row, "status", "") == "completed"), "Speaker response completed through party_chat mode.", correlationId);
                    Dictionary<string, object> contextRow = auditRows.LastOrDefault(row => ReadString(row, "phase", "") == "context.loaded");
                    Dictionary<string, object> contextData = ReadDictionary(contextRow, "data") ?? new Dictionary<string, object>();
                    List<string> pulls = ReadDictionaryList(contextData, "selectedContextPulls")
                        .Select(row => ReadString(row, "id", "")).Where(id => id.Length > 0).ToList();
                    pullsByHero[heroId] = pulls;
                    List<Dictionary<string, object>> bundles = ReadDictionaryList(contextData, "contextBundles");
                    AddNpcDialogueAssertion(assertions, "party_context_bundles_" + SafeMemoryKey(heroId), bundles.All(bundle => ReadBool(bundle, "ok", false) && ReadDictionary(bundle, "data") != null), "Selected native helper bundles completed with structured data.", Json.Serialize(bundles));
                    Dictionary<string, object> promptRow = auditRows.LastOrDefault(row => ReadString(row, "phase", "") == "prompt.built");
                    Dictionary<string, object> promptData = ReadDictionary(promptRow, "data") ?? new Dictionary<string, object>();
                    Dictionary<string, object> promptEvidence = ReadDictionary(promptData, "promptEvidence") ?? new Dictionary<string, object>();
                    string promptJson = Json.Serialize(promptData);
                    AddNpcDialogueAssertion(assertions, "party_prompt_player_text_" + SafeMemoryKey(heroId),
                        promptEvidence.ContainsKey("playerTextIncluded")
                            ? ReadBool(promptEvidence, "playerTextIncluded", false)
                            : promptJson.IndexOf(playerText, StringComparison.OrdinalIgnoreCase) >= 0,
                        "The actual speaker prompt contains the exact player line.", correlationId);
                    AddNpcDialogueAssertion(assertions, "party_prompt_group_attribution_" + SafeMemoryKey(heroId),
                        ReadBool(promptEvidence, "groupTranscriptAttributed", false),
                        "The live party transcript carries explicit speaker, role, id, and text attribution before prompt truncation.", correlationId);
                    AddNpcDialogueAssertion(assertions, "party_prompt_prior_npcs_" + SafeMemoryKey(heroId),
                        ReadInt(promptEvidence, "currentExchangePriorNpcCount", 0) >= replyIndex,
                        "Each later speaker receives every earlier NPC contribution from the current group beat.",
                        ReadInt(promptEvidence, "currentExchangePriorNpcCount", 0) + "/" + replyIndex);
                }

                string reactionEvidence;
                AddNpcDialogueAssertion(assertions, "party_group_reaction_targets",
                    PartyDialogueHasSequentialNpcReactions(replies, heroIds, out reactionEvidence),
                    "Every later NPC explicitly targets an earlier NPC contribution in the same group beat.", reactionEvidence);

                AddNpcDialogueAssertion(assertions, "party_memory_domains", memories.All(memory => NpcDialogueAuditMemoryDomains.Contains(ReadString(memory, "memory_domain", ""))), "Party memory writes use permitted domains.", string.Join(",", memories.Select(memory => ReadString(memory, "memory_domain", ""))));
                AddNpcDialogueAssertion(assertions, "party_memory_lineage", memories.All(memory =>
                    !string.IsNullOrWhiteSpace(ReadString(memory, "event_id", ""))
                    && new[] { "private", "public", "rumor" }.Contains(ReadString(memory, "visibility", ""), StringComparer.OrdinalIgnoreCase)
                    && TextListFromJson(ReadString(memory, "known_by_json", "[]")).Contains(ReadString(memory, "owner_id", ""), StringComparer.OrdinalIgnoreCase)), "Party memory writes retain source lineage, visibility, and owner knowledge.", string.Join(",", memories.Select(memory => ReadString(memory, "memory_id", ""))));
                if (recallTokens.Count > 0)
                {
                    foreach (string token in recallTokens)
                    {
                        int replyMatches = replies.Count(reply => ReadString(reply, "text", "").IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
                        AddNpcDialogueAssertion(assertions, "party_recall_" + SafeMemoryKey(token), requireEverySpeaker ? replyMatches == heroIds.Count : replyMatches >= 1,
                            requireEverySpeaker ? "Every selected NPC recalled the requested detail." : "The group recovered the requested detail without requiring identical phrasing from every NPC.", token + ":" + replyMatches + "/" + heroIds.Count);
                    }
                    AddNpcDialogueAssertion(assertions, "party_recall_context", pullsByHero.Values.All(pulls => pulls.Contains("relevant_memory", StringComparer.OrdinalIgnoreCase) || pulls.Contains("relationship_history", StringComparer.OrdinalIgnoreCase)), "Recall probes selected a memory/history retrieval lane for every speaker.", Json.Serialize(pullsByHero));
                }

                Dictionary<string, object> turnResult = new Dictionary<string, object>
                {
                    ["turnIndex"] = ReadInt(payload, "turnIndex", 0),
                    ["sceneIndex"] = ReadInt(payload, "sceneIndex", 0),
                    ["sessionId"] = sessionId,
                    ["exchangeId"] = exchangeId,
                    ["playerText"] = LimitText(playerText, 1600),
                    ["replies"] = replies,
                    ["selectedContextPulls"] = pullsByHero,
                    ["memoryIds"] = memories.Select(memory => ReadString(memory, "memory_id", "")).ToList(),
                    ["assertions"] = assertions,
                    ["passed"] = assertions.All(AssertionPassed)
                };
                List<Dictionary<string, object>> reportTurns = ReadDictionaryList(report, "turns");
                UpsertAuditRecord(reportTurns, turnResult, "turnIndex");
                report["turns"] = reportTurns;
                AppendAssertions(report, assertions);
                report["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                SavePartyDialogueAudit(report);
                return new Dictionary<string, object> { ["ok"] = true, ["passed"] = ReadBool(turnResult, "passed", false), ["turn"] = turnResult, ["summary"] = PartyDialogueAuditSummary(report, false) };
            }
        }

        private static Dictionary<string, object> PartyDialogueAuditSceneApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string runId = ReadString(payload, "runId", "");
            string sessionId = ReadString(payload, "sessionId", "");
            lock (PartyDialogueAuditLock)
            {
                Dictionary<string, object> report = LoadPartyDialogueAudit(campaignId, runId);
                if (report == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Party dialogue audit was not found." };
                List<string> heroIds = ReadStringList(report, "heroIds");
                string playerId = ReadString(report, "playerId", "");
                List<Dictionary<string, object>> assertions = new List<Dictionary<string, object>>();
                Dictionary<string, object> session;
                Dictionary<string, object> summary;
                int turnCount, links, fts, embeddingJobs;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    session = QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$id LIMIT 1;", new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault();
                    string summaryId = FirstNonEmpty(ReadString(session, "scene_summary_id", ""), ReadString(payload, "sceneSummaryId", ""));
                    summary = QuerySql(connection, "SELECT * FROM summaries WHERE summary_id=$id LIMIT 1;", new Dictionary<string, object> { ["id"] = summaryId }).FirstOrDefault();
                    turnCount = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM conversation_turns WHERE session_id=$id;", new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault(), "count", 0);
                    links = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM memory_sources WHERE document_type='summary' AND document_id=$id;", new Dictionary<string, object> { ["id"] = summaryId }).FirstOrDefault(), "count", 0);
                    fts = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM summary_fts WHERE summary_id=$id;", new Dictionary<string, object> { ["id"] = summaryId }).FirstOrDefault(), "count", 0);
                    embeddingJobs = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM embedding_jobs WHERE source_type='summary' AND source_id=$id;", new Dictionary<string, object> { ["id"] = summaryId }).FirstOrDefault(), "count", 0);
                }
                List<string> participants = TextListFromJson(ReadString(summary, "participants_json", "[]"));
                AddNpcDialogueAssertion(assertions, "party_scene_closed", session != null && ReadString(session, "status", "") != "open", "Party conversation session is closed.", sessionId);
                AddNpcDialogueAssertion(assertions, "party_scene_turn_count", turnCount == ReadInt(payload, "expectedTurns", 12), "Party scene contains the expected grouped raw turns.", turnCount.ToString());
                AddNpcDialogueAssertion(assertions, "party_scene_summary", summary != null && ReadString(summary, "summary_type", "") == "scene", "Party scene summary exists.", ReadString(summary, "summary_id", ""));
                AddNpcDialogueAssertion(assertions, "party_scene_lane", summary != null && MemoryLaneIds.Contains(ReadString(summary, "memory_lane", ""), StringComparer.OrdinalIgnoreCase), "Party scene summary has a valid memory lane.", ReadString(summary, "memory_lane", ""));
                AddNpcDialogueAssertion(assertions, "party_scene_participants", heroIds.Concat(new[] { playerId }).All(id => participants.Contains(id, StringComparer.OrdinalIgnoreCase)), "Party scene summary retains the full participant set.", Json.Serialize(participants));
                AddNpcDialogueAssertion(assertions, "party_scene_sources", links >= turnCount + 1, "Party scene summary links every raw turn and its session.", links.ToString());
                AddNpcDialogueAssertion(assertions, "party_scene_fts", fts == 1, "Party scene summary has one FTS row.", fts.ToString());
                Dictionary<string, object> settings = LoadSettings();
                if (ReadBool(settings, "enableSemanticMemory", true))
                    AddNpcDialogueAssertion(assertions, "party_scene_embedding", embeddingJobs >= 1 || ReadString(summary, "embedding_status", "") == "indexed", "Party scene summary is queued or indexed for semantic memory.", embeddingJobs.ToString());

                Dictionary<string, object> sceneResult = new Dictionary<string, object>
                {
                    ["sceneIndex"] = ReadInt(payload, "sceneIndex", 0),
                    ["sessionId"] = sessionId,
                    ["summaryId"] = ReadString(summary, "summary_id", ""),
                    ["memoryLane"] = ReadString(summary, "memory_lane", ""),
                    ["assertions"] = assertions,
                    ["passed"] = assertions.All(AssertionPassed)
                };
                List<Dictionary<string, object>> scenes = ReadDictionaryList(report, "scenes");
                UpsertAuditRecord(scenes, sceneResult, "sceneIndex");
                report["scenes"] = scenes;
                AppendAssertions(report, assertions);
                report["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                SavePartyDialogueAudit(report);
                return new Dictionary<string, object> { ["ok"] = true, ["passed"] = ReadBool(sceneResult, "passed", false), ["scene"] = sceneResult, ["summary"] = PartyDialogueAuditSummary(report, false) };
            }
        }

        private static Dictionary<string, object> PartyDialogueAuditFinishApi(Dictionary<string, object> payload)
        {
            return CompletePartyDialogueAudit(payload, false);
        }

        private static Dictionary<string, object> PartyDialogueAuditCancelApi(Dictionary<string, object> payload)
        {
            return CompletePartyDialogueAudit(payload, true);
        }

        private static Dictionary<string, object> CompletePartyDialogueAudit(Dictionary<string, object> payload, bool cancelled)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string runId = ReadString(payload, "runId", "");
            lock (PartyDialogueAuditLock)
            {
                Dictionary<string, object> report = LoadPartyDialogueAudit(campaignId, runId);
                if (report == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Party dialogue audit was not found." };
                if (!cancelled)
                {
                    List<Dictionary<string, object>> assertions = new List<Dictionary<string, object>>();
                    List<Dictionary<string, object>> turns = ReadDictionaryList(report, "turns");
                    List<Dictionary<string, object>> scenes = ReadDictionaryList(report, "scenes");
                    AddNpcDialogueAssertion(assertions, "party_run_exchange_count", turns.Count == ReadInt(report, "expectedExchanges", 15), "Party audit completed every expected grouped exchange.", turns.Count.ToString());
                    AddNpcDialogueAssertion(assertions, "party_run_scene_count", scenes.Count == ReadInt(report, "expectedScenes", 5), "Party audit completed every expected closed scene.", scenes.Count.ToString());
                    int replies = turns.Sum(turn => ReadDictionaryList(turn, "replies").Count);
                    AddNpcDialogueAssertion(assertions, "party_run_reply_count", replies == ReadInt(report, "expectedNpcReplies", 45), "Party audit received one reply from every selected NPC in every exchange.", replies.ToString());
                    AppendAssertions(report, assertions);
                }
                List<Dictionary<string, object>> all = ReadDictionaryList(report, "assertions");
                int failed = all.Count(assertion => !AssertionPassed(assertion));
                report["status"] = cancelled ? "cancelled" : failed == 0 ? "completed" : "failed";
                report["completedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                report["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                report["finalCounts"] = PartyDialogueAuditCounts(campaignId, ReadStringList(report, "heroIds"));
                SavePartyDialogueAudit(report);
                return PartyDialogueAuditSummary(report, true);
            }
        }

        private static Dictionary<string, object> PartyDialogueAuditStatusApi(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>();
            string campaignId = query.ContainsKey("campaignId") ? query["campaignId"] : "default";
            string runId = query.ContainsKey("runId") ? query["runId"] : "";
            lock (PartyDialogueAuditLock)
            {
                Dictionary<string, object> report = LoadPartyDialogueAudit(campaignId, runId);
                return report == null
                    ? new Dictionary<string, object> { ["ok"] = true, ["found"] = false, ["campaignId"] = campaignId }
                    : PartyDialogueAuditSummary(report, true);
            }
        }

        private static Dictionary<string, object> PartyDialogueAuditCounts(string campaignId, List<string> heroIds)
        {
            Dictionary<string, object> counts = new Dictionary<string, object>();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                foreach (string heroId in heroIds ?? new List<string>())
                {
                    counts[heroId] = new Dictionary<string, object>
                    {
                        ["turns"] = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM conversation_turns WHERE speaker_id=$id OR participants_json LIKE $like;", new Dictionary<string, object> { ["id"] = heroId, ["like"] = "%" + heroId + "%" }).FirstOrDefault(), "count", 0),
                        ["memories"] = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM memories WHERE owner_id=$id;", new Dictionary<string, object> { ["id"] = heroId }).FirstOrDefault(), "count", 0),
                        ["sceneSummaries"] = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM summaries WHERE summary_type='scene' AND (owner_id=$id OR participants_json LIKE $like);", new Dictionary<string, object> { ["id"] = heroId, ["like"] = "%" + heroId + "%" }).FirstOrDefault(), "count", 0),
                        ["middleTermSummaries"] = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM summaries WHERE summary_type='middle_term' AND (owner_id=$id OR participants_json LIKE $like);", new Dictionary<string, object> { ["id"] = heroId, ["like"] = "%" + heroId + "%" }).FirstOrDefault(), "count", 0),
                        ["arcSummaries"] = ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM summaries WHERE summary_type='arc' AND (owner_id=$id OR participants_json LIKE $like);", new Dictionary<string, object> { ["id"] = heroId, ["like"] = "%" + heroId + "%" }).FirstOrDefault(), "count", 0)
                    };
                }
            }
            return counts;
        }

        private static Dictionary<string, object> PartyDialogueAuditSummary(Dictionary<string, object> report, bool includeReport)
        {
            List<Dictionary<string, object>> assertions = ReadDictionaryList(report, "assertions");
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["found"] = true,
                ["runId"] = ReadString(report, "runId", ""),
                ["campaignId"] = ReadString(report, "campaignId", ""),
                ["heroIds"] = ReadStringList(report, "heroIds"),
                ["status"] = ReadString(report, "status", ""),
                ["turnCount"] = ReadDictionaryList(report, "turns").Count,
                ["sceneCount"] = ReadDictionaryList(report, "scenes").Count,
                ["passed"] = assertions.Count(AssertionPassed),
                ["failed"] = assertions.Count(assertion => !AssertionPassed(assertion)),
                ["reportPath"] = PartyDialogueAuditPath(ReadString(report, "campaignId", "default"), ReadString(report, "runId", "latest")),
                ["updatedUtc"] = ReadString(report, "updatedUtc", "")
            };
            if (includeReport) result["report"] = report;
            return result;
        }

        private static string PartyDialogueAuditDirectory(string campaignId)
        {
            return CampaignFile(campaignId, "tests", "party-dialogue-audits");
        }

        private static string PartyDialogueAuditPath(string campaignId, string runId)
        {
            return Path.Combine(PartyDialogueAuditDirectory(campaignId), SafePathSegment(runId, "latest") + ".json");
        }

        private static string PartyDialogueAuditRequestPath(string campaignId)
        {
            return CampaignFile(campaignId, "tests", "party-dialogue-audits", "requests", "current.json");
        }

        private static void SavePartyDialogueAudit(Dictionary<string, object> report)
        {
            string path = PartyDialogueAuditPath(ReadString(report, "campaignId", "default"), ReadString(report, "runId", "latest"));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            WriteJsonObject(path, report);
        }

        private static Dictionary<string, object> LoadPartyDialogueAudit(string campaignId, string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) return FindLatestPartyDialogueAudit(campaignId);
            string path = PartyDialogueAuditPath(campaignId, runId);
            return File.Exists(path) ? ReadJsonObject(path) : null;
        }

        private static Dictionary<string, object> FindActivePartyDialogueAudit(string campaignId)
        {
            return EnumeratePartyDialogueAudits(campaignId).FirstOrDefault(report => ReadString(report, "status", "") == "running");
        }

        private static Dictionary<string, object> FindLatestPartyDialogueAudit(string campaignId)
        {
            return EnumeratePartyDialogueAudits(campaignId).OrderByDescending(report => ReadString(report, "updatedUtc", "")).FirstOrDefault();
        }

        private static IEnumerable<Dictionary<string, object>> EnumeratePartyDialogueAudits(string campaignId)
        {
            string directory = PartyDialogueAuditDirectory(campaignId);
            if (!Directory.Exists(directory)) return Enumerable.Empty<Dictionary<string, object>>();
            return Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Select(ReadJsonObject).Where(report => report != null && report.Count > 0).ToList();
        }

        private static Dictionary<string, object> LoadPartyDialogueAuditRequest(string campaignId)
        {
            string path = PartyDialogueAuditRequestPath(campaignId);
            if (!File.Exists(path)) return null;
            Dictionary<string, object> request = ReadJsonObject(path);
            return request.Count == 0 ? null : request;
        }

        private static void SavePartyDialogueAuditRequest(Dictionary<string, object> request)
        {
            string path = PartyDialogueAuditRequestPath(ReadString(request, "campaignId", "default"));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            WriteJsonObject(path, request);
        }

        private static Dictionary<string, object> PartyDialogueAuditRequestSummary(Dictionary<string, object> request)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["found"] = true,
                ["requestId"] = ReadString(request, "requestId", ""),
                ["campaignId"] = ReadString(request, "campaignId", ""),
                ["heroSearches"] = ReadStringList(request, "heroSearches"),
                ["seed"] = ReadLong(request, "seed", 0),
                ["status"] = ReadString(request, "status", "queued"),
                ["heroes"] = ReadDictionaryList(request, "heroes"),
                ["message"] = ReadString(request, "message", ""),
                ["updatedUtc"] = ReadString(request, "updatedUtc", "")
            };
        }

        private static List<Dictionary<string, object>> RunPartyDialogueAuditSelfTests()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => rows.Add(new Dictionary<string, object>
            {
                ["id"] = "party_dialogue_audit_" + id,
                ["suite"] = "party_dialogue_audit",
                ["passed"] = passed,
                ["summary"] = summary
            });
            string campaignId = "pda_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string runId = "r_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                string requestId = "q_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                List<string> searches = new List<string> { "npc_a", "npc_b", "npc_c" };
                Dictionary<string, object> queued = PartyDialogueAuditRequestApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["requestId"] = requestId, ["heroSearches"] = searches, ["seed"] = 1337
                });
                Dictionary<string, object> duplicate = PartyDialogueAuditRequestApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["requestId"] = requestId, ["heroSearches"] = searches, ["seed"] = 1337
                });
                Dictionary<string, object> competing = PartyDialogueAuditRequestApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["requestId"] = "other", ["heroSearches"] = searches
                });
                Dictionary<string, object> polled = PartyDialogueAuditRequestPollApi(new Dictionary<string, object> { ["campaignId"] = campaignId });
                add("request_queue", ReadBool(queued, "ok", false) && ReadBool(duplicate, "ok", false)
                    && !ReadBool(competing, "ok", true) && ReadBool(polled, "found", false)
                    && ReadStringList(polled, "heroSearches").SequenceEqual(searches),
                    "Three-person party requests are durable, idempotent, ordered, and single-queued.");

                Dictionary<string, object> acknowledged = PartyDialogueAuditRequestAckApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["requestId"] = requestId, ["status"] = "accepted",
                    ["heroes"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["heroId"] = "npc_a", ["heroName"] = "NPC A" },
                        new Dictionary<string, object> { ["heroId"] = "npc_b", ["heroName"] = "NPC B" },
                        new Dictionary<string, object> { ["heroId"] = "npc_c", ["heroName"] = "NPC C" }
                    }
                });
                Dictionary<string, object> afterAck = PartyDialogueAuditRequestPollApi(new Dictionary<string, object> { ["campaignId"] = campaignId });
                add("request_ack", ReadString(acknowledged, "status", "") == "accepted" && !ReadBool(afterAck, "found", true),
                    "Accepted party requests are not returned to the game twice.");

                List<Dictionary<string, object>> heroes = ReadDictionaryList(acknowledged, "heroes");
                Dictionary<string, object> started = PartyDialogueAuditStartApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["runId"] = runId, ["playerId"] = "player", ["heroes"] = heroes,
                    ["expectedExchanges"] = 15, ["expectedScenes"] = 5, ["expectedNpcReplies"] = 45
                });
                Dictionary<string, object> locked = PartyDialogueAuditStartApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["runId"] = "other_run", ["playerId"] = "player", ["heroes"] = heroes
                });
                add("single_run_lock", ReadBool(started, "ok", false) && !ReadBool(locked, "ok", true),
                    "Only one party dialogue audit can run in a campaign.");

                Dictionary<string, object> cancelled = PartyDialogueAuditCancelApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["runId"] = runId
                });
                Dictionary<string, object> status = PartyDialogueAuditStatusApi(new Dictionary<string, string>
                {
                    ["campaignId"] = campaignId, ["runId"] = runId
                });
                add("partial_report", ReadString(cancelled, "status", "") == "cancelled"
                    && ReadBool(status, "found", false) && File.Exists(ReadString(status, "reportPath", "")),
                    "Cancelled party runs remain available as durable partial reports.");

                List<Dictionary<string, object>> incidentalSellerPulls = DeterministicContextPullSelection(
                    "party_chat", "A spice seller sneezed twice beside my chipped cup.", "", ContextPullIds.ToList());
                List<Dictionary<string, object>> explicitTradePulls = DeterministicContextPullSelection(
                    "party_chat", "Will you sell me that chipped cup, and how much is it worth?", "", ContextPullIds.ToList());
                add("commerce_term_boundaries",
                    !incidentalSellerPulls.Any(pull => string.Equals(ReadString(pull, "id", ""), "appraise_trade_offer", StringComparison.OrdinalIgnoreCase))
                    && explicitTradePulls.Any(pull => string.Equals(ReadString(pull, "id", ""), "appraise_trade_offer", StringComparison.OrdinalIgnoreCase)),
                    "Incidental words such as seller do not trigger trade appraisal, while explicit trade language still does.");

                const string auditPlayerText = "The blue cup is wrapped in green cloth.";
                Dictionary<string, object> promptEvidence = BuildNpcDialoguePromptAuditEvidence(
                    campaignId,
                    new Dictionary<string, object> { ["playerText"] = auditPlayerText },
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["role"] = "user", ["content"] = "PLAYER LINE\n" + auditPlayerText }
                    },
                    new List<Dictionary<string, object>>());
                add("pretruncation_prompt_evidence", ReadBool(promptEvidence, "playerTextIncluded", false),
                    "Party prompt assertions use evidence computed before large audit payloads are truncated.");

                string reactionEvidence;
                add("sequential_group_reactions", PartyDialogueHasSequentialNpcReactions(
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["heroId"] = "npc_a", ["reactionTargetHeroStringId"] = "player" },
                        new Dictionary<string, object> { ["heroId"] = "npc_b", ["reactionTargetHeroStringId"] = "npc_a" },
                        new Dictionary<string, object> { ["heroId"] = "npc_c", ["reactionTargetHeroStringId"] = "npc_b" }
                    },
                    new List<string> { "npc_a", "npc_b", "npc_c" }, out reactionEvidence),
                    "Later group speakers must target an NPC who already spoke in the current beat.");
            }
            catch (Exception ex)
            {
                add("fixture", false, "Party dialogue audit fixture failed: " + LimitText(ex.Message, 400));
            }
            finally
            {
                try
                {
                    ReignPostgreSqlStorage.ClearAllPools();
                    string path = CampaignDirectory(campaignId);
                    if (Directory.Exists(path)) Directory.Delete(path, true);
                }
                catch { }
            }
            return rows;
        }

        private static bool PartyDialogueHasSequentialNpcReactions(
            List<Dictionary<string, object>> replies,
            List<string> heroIds,
            out string evidence)
        {
            replies = replies ?? new List<Dictionary<string, object>>();
            heroIds = heroIds ?? new List<string>();
            List<string> rows = new List<string>();
            bool valid = replies.Count >= 2 && heroIds.Count >= replies.Count;
            for (int index = 1; index < replies.Count; index++)
            {
                string speakerId = ReadFirstString(replies[index], "heroId", "heroStringId");
                string targetId = ReadFirstString(replies[index], "reactionTargetHeroStringId", "reactionTargetId");
                bool rowValid = !string.IsNullOrWhiteSpace(targetId)
                    && !string.Equals(speakerId, targetId, StringComparison.OrdinalIgnoreCase)
                    && heroIds.Take(index).Contains(targetId, StringComparer.OrdinalIgnoreCase);
                rows.Add(speakerId + "->" + (string.IsNullOrWhiteSpace(targetId) ? "none" : targetId));
                valid &= rowValid;
            }
            evidence = string.Join(",", rows);
            return valid;
        }
    }
}
