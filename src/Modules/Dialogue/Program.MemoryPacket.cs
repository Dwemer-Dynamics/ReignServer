using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Npgsql;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        internal static Dictionary<string, object> BuildNpcMemoryPacket(string campaignId, string npcId, string playerId, string locationId, string topic, int tokenBudget, Dictionary<string, object> npcContext = null, Dictionary<string, object> settingsOverride = null)
        {
            ReignTraceScope retrievalTrace = BeginReignSpan("memory.retrieve", new Dictionary<string, object>
            { ["campaignId"] = campaignId, ["npcId"] = npcId, ["playerId"] = playerId, ["tokenBudget"] = tokenBudget });
            Stopwatch timer = Stopwatch.StartNew();
            Stopwatch phaseTimer = Stopwatch.StartNew();
            Dictionary<string, object> timingPhases =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> memories = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> relationships = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> obligations = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> beliefs = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> comprehension = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> summaries = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> temporalKnowledge = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> nativeWorldHistory = new List<Dictionary<string, object>>();
            var recentWindow = new List<DialogueService.MemoryEvidence>();
            var recentWindowDiagnostics = TestDict();
            var recallAudit = TestDict();
            List<string> warnings = new List<string>();
            List<string> sourceEventIds = new List<string>();
            Dictionary<string, object> settings = settingsOverride ?? LoadSettings();
            Dictionary<string, object> routeContext = npcContext == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(npcContext, StringComparer.OrdinalIgnoreCase);
            if (!routeContext.ContainsKey("worldDay"))
            {
                routeContext["worldDay"] = ReadDouble(routeContext, "currentWorldDay", 0d);
            }
            string precisionMode;
            using (var connection = ReignDatabase.OpenCampaignConnection(campaignId)) precisionMode = DialogueService.MemoryPrecisionMode(connection, settings);
            string precisionQuery = ReadString(routeContext, "memoryQueryText", topic);
            // Scene state is grounding for the writer, not the player's historical
            // question. Keep lexical routing and semantic retrieval on that same
            // question even for callers that supplied a combined legacy topic.
            topic = precisionQuery;
            if (precisionMode == "precision")
            {
                var precise = DialogueService.BuildPrecisionMemoryPacket(campaignId, npcId, playerId, locationId, precisionQuery, routeContext, settings, false);
                retrievalTrace.Dispose();
                return precise;
            }
            Dictionary<string, object> retrievalRoute = DialogueService.BuildMemoryRetrievalRoute(settings, topic, routeContext);
            Dictionary<string, object> semanticRetrieval = DialogueService.TrySemanticMemorySearch(campaignId, precisionQuery, retrievalRoute, settings,
                null, ReadString(routeContext, "timelineId", ""), npcId);
            timingPhases["routeAndSemanticMs"] = phaseTimer.ElapsedMilliseconds;
            phaseTimer.Restart();
            Dictionary<string, object> exactHistory = new Dictionary<string, object>
            {
                ["text"] = "", ["matchedTurnIds"] = new List<string>(), ["expandedTurnIds"] = new List<string>()
            };
            List<string> minimeTopics = TryExtractMinimeTopics(settings, topic, ReadInt(settings, "minimeMaxTopics", 5));
            string topicWithHints = AppendMinimeTopics(topic, minimeTopics);
            List<string> queryTerms = MemoryQueryTerms(topicWithHints);
            HashSet<string> ftsMemoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> ftsSummaryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> volatileSnapshotEventIds =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
            KnowledgeAccessContext knowledge = BuildKnowledgeAccessContext(npcId, playerId, locationId, npcContext);
            Dictionary<string, object> minimeRerank = MinimeRerankStatus(settings, "not_attempted");
            timingPhases["queryPreparationMs"] = phaseTimer.ElapsedMilliseconds;
            phaseTimer.Restart();

            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                timingPhases["connectionOpenMs"] = phaseTimer.ElapsedMilliseconds;
                phaseTimer.Restart();
                ftsMemoryIds = SearchMemoryFts(connection, queryTerms, 80, knowledge);
                ftsSummaryIds = SearchSummaryFts(connection, queryTerms, 40, knowledge);
                volatileSnapshotEventIds = new HashSet<string>(
                    ReignDatabase.QuerySql(connection,
                            @"SELECT event_id FROM events
WHERE event_type='daily_world_snapshot';")
                        .Select(row => ReadString(
                            row, "event_id", ""))
                        .Where(id =>
                            !string.IsNullOrWhiteSpace(id)),
                    StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(playerId) || DialogueService.RouteHasLane(retrievalRoute, "interpersonal_history") || DialogueService.RouteHasLane(retrievalRoute, "commitments_and_plots"))
                {
                    List<string> relationshipTargets = string.IsNullOrWhiteSpace(playerId)
                        ? ReignDatabase.QuerySql(connection, @"SELECT hero_a_id,hero_b_id FROM relationship_pair_chemistry
WHERE hero_a_id=$npc OR hero_b_id=$npc ORDER BY last_day DESC LIMIT 8;",
                            new Dictionary<string, object> { ["npc"] = npcId })
                            .Select(row => ReadString(row, "hero_a_id", "").Equals(npcId, StringComparison.OrdinalIgnoreCase)
                                ? ReadString(row, "hero_b_id", "") : ReadString(row, "hero_a_id", ""))
                            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                        : new List<string> { playerId };
                    foreach (string target in relationshipTargets)
                    {
                        Dictionary<string, object> relationshipRequest = new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId, ["subjectId"] = npcId, ["targetId"] = target,
                            ["worldDay"] = ReadDouble(routeContext, "worldDay", 0d)
                        };
                        if (!string.IsNullOrWhiteSpace(playerId) && target.Equals(playerId, StringComparison.OrdinalIgnoreCase))
                            relationshipRequest["nativeRelation"] = ReadInt(npcContext, "relationToPlayer", 0);
                        Dictionary<string, object> relationship = RelationshipsService.RelationshipContextApi(relationshipRequest);
                        relationship["target_id"] = target;
                        relationships.Add(relationship);
                    }
                }
                timingPhases["ftsAndRelationshipsMs"] = phaseTimer.ElapsedMilliseconds;
                phaseTimer.Restart();
                obligations = QueryKnownRows(connection, "obligations", knowledge, DialogueService.PromptActiveObligationSql, 12);
                beliefs = DialogueService.RouteHasLane(retrievalRoute, "beliefs_and_rumors")
                    ? FilterStructuredMemoryRows(DialogueService.MergeSemanticRows(
                        QueryKnownRows(connection, "beliefs", knowledge, "", 30),
                        DialogueService.LoadSemanticRows(connection, "beliefs", "belief_id", "belief", semanticRetrieval, knowledge), "belief_id"), queryTerms, 30)
                    : new List<Dictionary<string, object>>();
                if (DialogueService.RouteHasLane(retrievalRoute, "beliefs_and_rumors"))
                    beliefs.AddRange(ReputationService.LoadKnownSocialContextCandidates(connection, campaignId,
                        ReadString(routeContext, "timelineId", "main"), npcId,
                        ReadDouble(routeContext, "worldDay", 0d), topic));
                comprehension = DialogueService.RouteHasLane(retrievalRoute, "personal_state") || DialogueService.RouteHasLane(retrievalRoute, "interpersonal_history")
                    ? FilterStructuredMemoryRows(DialogueService.MergeSemanticRows(
                        QueryKnownRows(connection, "comprehension", knowledge, "", 24),
                        DialogueService.LoadSemanticRows(connection, "comprehension", "comprehension_id", "comprehension", semanticRetrieval, knowledge), "comprehension_id"), queryTerms, 24)
                    : new List<Dictionary<string, object>>();
                temporalKnowledge = DialogueService.LoadTemporalKnowledgeForPrompt(connection, campaignId, knowledge, queryTerms, 10);
                timingPhases["structuredMemoryMs"] = phaseTimer.ElapsedMilliseconds;
                phaseTimer.Restart();
                summaries = DialogueService.MergeSemanticRows(
                        QueryKnownRows(connection, "summaries", knowledge, "status='active'", 40)
                            .Concat(LoadFtsRows(connection, "summaries", "summary_id", ftsSummaryIds, knowledge)),
                        DialogueService.LoadSemanticRows(connection, "summaries", "summary_id", "summary", semanticRetrieval, knowledge), "summary_id")
                    .Where(row => DialogueService.SummaryRowAllowedByRoute(row, retrievalRoute))
                    .Where(row => !TextListFromJson(
                            ReadString(
                                row,
                                "source_events_json", "[]"))
                        .Any(id =>
                            volatileSnapshotEventIds.Contains(id)))
                    .Where(row => SummaryRowRelevant(row, queryTerms, ftsSummaryIds) || ReadDouble(row, "vectorSemanticScore", 0d) > 0d || DialogueService.RouteRecentFallback(row, retrievalRoute, "updated_ts"))
                    .Select(row =>
                    {
                        row["packetScore"] = DialogueService.AddSemanticScore(row, SummaryPacketScore(row, queryTerms, ftsSummaryIds, npcId, playerId, locationId), settings);
                        return row;
                    })
                    .ToList();
                timingPhases["summarySelectionMs"] = phaseTimer.ElapsedMilliseconds;
                phaseTimer.Restart();
                memories = DialogueService.MergeSemanticRows(
                        QueryKnownRows(connection, "memories", knowledge, "status='active'", 80)
                            .Concat(LoadFtsRows(connection, "memories", "memory_id", ftsMemoryIds, knowledge)),
                        DialogueService.LoadSemanticRows(connection, "memories", "memory_id", "memory", semanticRetrieval, knowledge), "memory_id")
                    .Where(row => DialogueService.MemoryRowAllowedByRoute(row, retrievalRoute) || ftsMemoryIds.Contains(ReadString(row, "memory_id", ""))
                        || ReadDouble(row, "vectorSemanticScore", 0d) > 0d)
                    .Where(row => !volatileSnapshotEventIds.Contains(
                        ReadString(row, "event_id", "")))
                    .Where(row => MemoryRowRelevant(row, queryTerms, ftsMemoryIds) || ReadDouble(row, "vectorSemanticScore", 0d) > 0d || DialogueService.RouteRecentFallback(row, retrievalRoute, "ts"))
                    .Select(row =>
                    {
                        row["packetScore"] = DialogueService.AddSemanticScore(row, MemoryPacketScore(row, queryTerms, ftsMemoryIds, npcId, playerId, locationId), settings);
                        return row;
                    })
                    .OrderByDescending(row => ReadDouble(row, "packetScore", 0d))
                    .ThenByDescending(row => ReadLong(row, "ts", 0))
                    .GroupBy(MemoryPacketDedupeKey, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
                timingPhases["memorySelectionMs"] = phaseTimer.ElapsedMilliseconds;
                phaseTimer.Restart();
                string currentConversationSessionId = ReadFirstString(routeContext, "conversationSessionId", "sessionId", "conversationId");
                int rawTurnLimit = Math.Max(2, Math.Min(100, ReadInt(settings, "recentRawConversationTurns", 30)));
                int rawCharBudget = Math.Max(1200, Math.Min(50000, ReadInt(settings, "recentRawConversationCharBudget", 48000)));
                // Recent attributed dialogue is the continuity floor, not an optional
                // exact-recall lane. The player-facing setting promises that the latest
                // NPC-specific raw window remains available before summarized memory takes
                // priority, including complete group sessions in which this NPC participated.
                // Exclude the active session because its canonical transcript is assembled
                // separately by the production conversation controller.
                exactHistory = DialogueService.LoadMostRecentClosedConversation(connection, npcId, currentConversationSessionId,
                    rawCharBudget, rawTurnLimit, knowledge);
                if ((ReadBool(retrievalRoute, "needsExactTranscript", false) || queryTerms.Count > 0)
                    && !DialogueService.LooksLikeMostRecentConversationRecall(topic))
                {
                    Dictionary<string, object> targetedHistory = DialogueService.SearchExactConversationHistory(connection, npcId, topic,
                        retrievalRoute, ReadBool(routeContext, "deferContinuityAllocation", false) ? 24000 : 2400,
                        semanticRetrieval, currentConversationSessionId, rawTurnLimit, knowledge, routeContext);
                    recallAudit = TestDict("schema", "reign-memory-recall-v1", "query", topic,
                        "rankingPolicy", ReadString(targetedHistory, "rankingPolicy", "recent_history"),
                        "matchedTurnIds", ReadStringList(targetedHistory, "matchedTurnIds"),
                        "anchorTurnIds", ReadStringList(targetedHistory, "anchorTurnIds"),
                        "expandedTurnIds", ReadStringList(targetedHistory, "expandedTurnIds"),
                        "candidateLimit", ReadInt(targetedHistory, "candidateLimit", 0),
                        "retainedCandidateCount", ReadInt(targetedHistory, "retainedCandidateCount", 0),
                        "sourceSessionIds", ReadStringList(targetedHistory, "sourceSessionIds"));
                    string targetedText = ReadString(targetedHistory, "text", "");
                    if (!string.IsNullOrWhiteSpace(targetedText))
                    {
                        string recentText = ReadString(exactHistory, "text", "");
                        List<string> recentIds = ReadStringList(exactHistory, "expandedTurnIds");
                        List<string> targetedIds = ReadStringList(targetedHistory, "expandedTurnIds");
                        bool addsOlderEvidence = targetedIds.Any(id => !recentIds.Contains(id, StringComparer.OrdinalIgnoreCase));
                        if (string.IsNullOrWhiteSpace(recentText))
                        {
                            exactHistory = targetedHistory;
                        }
                        // IDs present in the legacy recent block do not prove
                        // final delivery: that unprotected block can be reduced
                        // by the writer's shared allocator. Preserve targeted
                        // whole records independently until final serialization.
                        else if (addsOlderEvidence || ReadBool(routeContext, "deferContinuityAllocation", false))
                        {
                            exactHistory = new Dictionary<string, object>
                            {
                                ["text"] = recentText + "\n\nTargeted older exact-history evidence:\n" + targetedText,
                                ["matchedTurnIds"] = ReadStringList(exactHistory, "matchedTurnIds")
                                    .Concat(ReadStringList(targetedHistory, "matchedTurnIds"))
                                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                                ["expandedTurnIds"] = recentIds.Concat(targetedIds)
                                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                                ["sessionId"] = ReadString(targetedHistory, "sessionId", ReadString(exactHistory, "sessionId", "")),
                                ["selectionMode"] = "recent_plus_targeted_exact_history",
                                ["rawTurnLimit"] = ReadInt(exactHistory, "rawTurnLimit", rawTurnLimit),
                                ["rawTurnCount"] = ReadInt(exactHistory, "rawTurnCount", recentIds.Count)
                            };
                        }
                    }
                }
                timingPhases["exactHistoryMs"] = phaseTimer.ElapsedMilliseconds;
                phaseTimer.Restart();
                nativeWorldHistory = DialogueService.LoadKnownWorldHistoryForDialogue(connection,
                    ReadString(routeContext, "timelineId", ""), ReadDouble(routeContext, "worldDay", 0d),
                    knowledge, topic, retrievalRoute, semanticRetrieval,
                    ReadBool(ReadDictionary(routeContext, "identityView"), "knowsIdentity", false));
                timingPhases["nativeWorldHistoryMs"] = phaseTimer.ElapsedMilliseconds;
                recentWindow = DialogueService.LoadRecentConversationWindow(connection, DialogueService.PlanMemoryQuery(npcId, playerId, precisionQuery, routeContext),
                    routeContext, knowledge, settings, recentWindowDiagnostics);
            }
            phaseTimer.Restart();

            // Candidate vectors are already persisted and scored by TrySemanticMemorySearch.
            // Re-embedding every selected row synchronously duplicated that work and was the
            // largest non-dialogue latency source. FTS, indexed semantic score, recency,
            // importance, lane, participant, and visibility scores remain fully active.
            minimeRerank = MinimeRerankStatus(settings, "indexed_semantic_scores_used");
            minimeRerank["attempted"] = false;
            minimeRerank["applied"] = ReadBool(semanticRetrieval, "applied", false);
            minimeRerank["method"] = "persistent_vector_index_plus_deterministic_rank";
            minimeRerank["candidateCount"] = summaries.Count + memories.Count + beliefs.Count + comprehension.Count;
            using (var observerConnection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                memories = DialogueService.ProjectRetrievedConversationMemories(observerConnection, memories, knowledge);
                summaries = DialogueService.ProjectRetrievedConversationMemories(observerConnection, summaries, knowledge);
            }
            var recentSummaryIds = recentWindow.Where(e => e.Kind == "recent_summary").Select(e => e.SourceId).ToHashSet(StringComparer.Ordinal);
            summaries = OrderRerankedRows(summaries, "updated_ts").Where(row => !recentSummaryIds.Contains(ReadString(row, "summary_id", ""))).Take(8).ToList();
            memories = OrderRerankedRows(memories, "ts").Take(16).ToList();
            beliefs = OrderRerankedRows(beliefs, "ts").Take(8).ToList();
            comprehension = OrderRerankedRows(comprehension, "ts").Take(6).ToList();

            string currentNpcName = ReadFirstString(routeContext, "name", "heroName", "speakerName");
            string currentPlayerName = ReadFirstString(routeContext, "mainHeroName", "playerName");
            string currentPlayerId = ReadFirstString(routeContext, "mainHeroStringId", "playerHeroStringId", "playerId");
            List<string> interactionNpcNames = DialogueService.MergedInteractionParticipantProfiles(routeContext)
                .Where(profile => !CharacterIdFrom(profile).Equals(currentPlayerId, StringComparison.OrdinalIgnoreCase)
                    && !ReadString(profile, "role", "npc").Equals("player", StringComparison.OrdinalIgnoreCase))
                .Select(profile => ReadString(profile, "name", ""))
                .Concat(new[] { currentNpcName })
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            int legacyRoleCollisions = summaries.Count(row => IsLegacyPlayerNpcRoleCollision(row, interactionNpcNames, currentPlayerName))
                + memories.Count(row => IsLegacyPlayerNpcRoleCollision(row, interactionNpcNames, currentPlayerName))
                + beliefs.Count(row => IsLegacyPlayerNpcRoleCollision(row, interactionNpcNames, currentPlayerName))
                + comprehension.Count(row => IsLegacyPlayerNpcRoleCollision(row, interactionNpcNames, currentPlayerName))
                + temporalKnowledge.Count(row => IsLegacyPlayerNpcRoleCollision(row, interactionNpcNames, currentPlayerName));
            if (legacyRoleCollisions > 0)
            {
                summaries = summaries.Where(row => !IsLegacyPlayerNpcRoleCollision(row, interactionNpcNames, currentPlayerName)).ToList();
                memories = memories.Where(row => !IsLegacyPlayerNpcRoleCollision(row, interactionNpcNames, currentPlayerName)).ToList();
                beliefs = beliefs.Where(row => !IsLegacyPlayerNpcRoleCollision(row, interactionNpcNames, currentPlayerName)).ToList();
                comprehension = comprehension.Where(row => !IsLegacyPlayerNpcRoleCollision(row, interactionNpcNames, currentPlayerName)).ToList();
                temporalKnowledge = temporalKnowledge.Where(row => !IsLegacyPlayerNpcRoleCollision(row, interactionNpcNames, currentPlayerName)).ToList();
                warnings.Add("Quarantined " + legacyRoleCollisions.ToString(CultureInfo.InvariantCulture)
                    + " legacy memory record(s) that assigned a current NPC participant's name to the player despite the authoritative role map.");
            }

            int feedbackLoopRecords = 0;
            Func<List<Dictionary<string, object>>, string, List<Dictionary<string, object>>> quarantineRoleplayFeedback =
                (rows, textKey) =>
                {
                    List<Dictionary<string, object>> safe = new List<Dictionary<string, object>>();
                    foreach (Dictionary<string, object> row in rows ?? new List<Dictionary<string, object>>())
                    {
                        if (DialogueService.IsRoleplayFeedbackLoopEvidence(row, ReadString(row, textKey, "")))
                        {
                            feedbackLoopRecords++;
                            continue;
                        }
                        safe.Add(row);
                    }
                    return safe;
                };
            summaries = quarantineRoleplayFeedback(summaries, "summary");
            memories = quarantineRoleplayFeedback(memories, "summary");
            obligations = quarantineRoleplayFeedback(obligations, "description");
            beliefs = quarantineRoleplayFeedback(beliefs, "claim");
            comprehension = quarantineRoleplayFeedback(comprehension, "text");
            temporalKnowledge = quarantineRoleplayFeedback(temporalKnowledge, "claim");
            exactHistory = DialogueService.QuarantineRoleplayFeedbackExactHistory(
                exactHistory, out int quarantinedExactLines);
            feedbackLoopRecords += quarantinedExactLines;
            if (feedbackLoopRecords > 0)
            {
                warnings.Add("Quarantined " + feedbackLoopRecords.ToString(CultureInfo.InvariantCulture)
                    + " conversation-derived record(s) that would recursively turn role-play refusal, fictional absence, or absolute future prediction into current fact. Canonical audit history was preserved.");
            }

            if (knowledge.PlayerIdentityUnknown)
            {
                int identityRejected = 0;
                Func<List<Dictionary<string, object>>, string, List<Dictionary<string, object>>> protect =
                    (rows, textKey) =>
                    {
                        List<Dictionary<string, object>> safe = new List<Dictionary<string, object>>();
                        foreach (Dictionary<string, object> row in rows ?? new List<Dictionary<string, object>>())
                        {
                            string evidence = ReadString(row, textKey, "");
                            if (!UnknownIdentityMemoryEvidenceAllowed(row, knowledge, evidence))
                            {
                                identityRejected++;
                                continue;
                            }
                            safe.Add(SanitizeUnknownIdentityMemoryRow(row, knowledge));
                        }
                        return safe;
                    };
                summaries = protect(summaries, "summary");
                memories = protect(memories, "summary");
                obligations = protect(obligations, "description");
                beliefs = protect(beliefs, "claim");
                comprehension = protect(comprehension, "text");
                temporalKnowledge = protect(temporalKnowledge, "claim");
                relationships = relationships.Select(row => SanitizeUnknownIdentityMemoryRow(row, knowledge)).ToList();
                exactHistory = DialogueService.SanitizeUnknownIdentityExactHistory(exactHistory, knowledge);
                if (identityRejected > 0)
                {
                    warnings.Add("Withheld " + identityRejected.ToString(CultureInfo.InvariantCulture)
                        + " retrieved record(s) whose canonical identity or private player status was unavailable to this observer.");
                }
            }

            foreach (Dictionary<string, object> row in memories.Concat(obligations).Concat(beliefs).Concat(comprehension))
            {
                string eventId = ReadString(row, "event_id", "");
                if (!string.IsNullOrWhiteSpace(eventId))
                {
                    sourceEventIds.Add(eventId);
                }
            }
            foreach (Dictionary<string, object> row in summaries)
            {
                sourceEventIds.AddRange(TextListFromJson(ReadString(row, "source_events_json", "[]")));
            }

            if (string.IsNullOrWhiteSpace(knowledge.NpcId))
            {
                warnings.Add("No npc_id/heroStringId was supplied; private knowledge filtering is limited.");
            }

            int exactReserve = string.IsNullOrWhiteSpace(ReadString(exactHistory, "text", "")) ? 0
                : ReadInt(exactHistory, "rawTurnCount", 0) > 0
                    ? Math.Max(1200, Math.Min(Math.Max(1200, tokenBudget - 400), (int)Math.Ceiling(ReadString(exactHistory, "text", "").Length / 4d))) : 600;
            bool deferAllocation = ReadBool(routeContext, "deferContinuityAllocation", false);
            string nativeHistoryText = DialogueService.FormatKnownWorldHistoryForDialogue(nativeWorldHistory,
                deferAllocation ? 24000 : Math.Min(2400, Math.Max(800, tokenBudget * 4 / 3)));
            int nativeReserve = (int)Math.Ceiling(nativeHistoryText.Length / 4d);
            string packet = BuildMemoryPacketText(npcId, playerId, summaries, memories, relationships, obligations, beliefs, comprehension,
                warnings, retrievalRoute, Math.Max(400, tokenBudget - exactReserve - nativeReserve), deferAllocation);
            if (!string.IsNullOrWhiteSpace(nativeHistoryText)) packet = nativeHistoryText + "\n\n" + packet;
            sourceEventIds.AddRange(nativeWorldHistory.Select(row => ReadString(row, "event_id", "")));
            string temporalText = DialogueService.FormatTemporalKnowledgeForPrompt(temporalKnowledge);
            if (!string.IsNullOrWhiteSpace(temporalText))
                packet = packet.TrimEnd() + "\n\n" + temporalText;
            string exactText = ReadString(exactHistory, "text", "");
            if (!string.IsNullOrWhiteSpace(exactText))
            {
                packet = packet.TrimEnd() + "\n\nExact conversation source (use for precise continuity; do not invent omitted lines):\n" + exactText;
            }
            using (var correspondenceConnection = ReignDatabase.OpenCampaignConnection(campaignId))
                foreach (var letter in DialogueService.LoadCorrespondenceMemory(correspondenceConnection, npcId, playerId, precisionQuery, routeContext, knowledge))
                {
                    packet += "\n\n" + DialogueService.RenderContinuityRecord(letter.SourceId, letter.Kind, letter.Owner, letter.Session, letter.Day, 119, true, letter.Text);
                    sourceEventIds.Add(letter.SourceId);
                }
            var recentPacket = DialogueService.SelectPrecisionEvidence(recentWindow, 2000, DialogueService.PlanMemoryQuery(npcId, playerId, precisionQuery, routeContext).SuppliedSources);
            if (recentPacket.Text.Length > 0) packet += "\n\n" + recentPacket.Text;
            packet = DialogueService.ProtectMemoryRecallSourcePolicy(npcId) + "\n\n" + packet;
            timingPhases["rankingAndFormattingMs"] = phaseTimer.ElapsedMilliseconds;
            timer.Stop();
            Dictionary<string, object> memoryPacketResult = new Dictionary<string, object>
            {
                ["memoryPacket"] = packet,
                ["recallAudit"] = recallAudit,
                ["recentMemoryWindow"] = ReadDictionary(recentWindowDiagnostics, "recentMemoryWindow"),
                ["retrievalRoute"] = retrievalRoute,
                ["semanticRetrieval"] = new Dictionary<string, object>
                {
                    ["enabled"] = ReadBool(semanticRetrieval, "enabled", false), ["attempted"] = ReadBool(semanticRetrieval, "attempted", false),
                    ["applied"] = ReadBool(semanticRetrieval, "applied", false), ["reason"] = ReadString(semanticRetrieval, "reason", ""),
                    ["resultCount"] = ReadInt(semanticRetrieval, "resultCount", 0), ["durationMs"] = ReadLong(semanticRetrieval, "durationMs", 0),
                    ["error"] = ReadString(semanticRetrieval, "error", ""),
                    ["workerTiming"] = ReadDictionary(semanticRetrieval, "workerTiming") ?? new Dictionary<string, object>()
                },
                ["minimeTopics"] = minimeTopics,
                ["minimeRerank"] = minimeRerank,
                ["sourceSummaryIds"] = summaries.Select(row => ReadString(row, "summary_id", "")).Concat(recentSummaryIds).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList(),
                ["matchedTurnIds"] = ReadStringList(exactHistory, "matchedTurnIds"),
                ["expandedTurnIds"] = ReadStringList(exactHistory, "expandedTurnIds"),
                ["exactHistorySessionId"] = ReadString(exactHistory, "sessionId", ""),
                ["exactHistorySelectionMode"] = ReadString(exactHistory, "selectionMode", ""),
                ["recentRawTurnLimit"] = ReadInt(exactHistory, "rawTurnLimit", 0),
                ["recentRawTurnCount"] = ReadInt(exactHistory, "rawTurnCount", 0),
                ["exactHistoryChars"] = ReadString(exactHistory, "text", "").Length,
                ["sourceEventIds"] = sourceEventIds.Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList(),
                ["warnings"] = warnings,
                ["counts"] = new Dictionary<string, object>
                {
                    ["memories"] = memories.Count,
                    ["middleTermSummaries"] = summaries.Count + recentSummaryIds.Count,
                    ["relationships"] = relationships.Count,
                    ["obligations"] = obligations.Count,
                    ["beliefs"] = beliefs.Count,
                    ["comprehension"] = comprehension.Count,
                    ["temporalKnowledge"] = temporalKnowledge.Count,
                    ["nativeWorldHistory"] = nativeWorldHistory.Count,
                    ["exactTranscriptTurns"] = ReadStringList(exactHistory, "expandedTurnIds").Count
                },
                ["timingMs"] = timer.ElapsedMilliseconds
            };
            memoryPacketResult["timingPhases"] = timingPhases;
            if (precisionMode == "shadow")
            {
                var shadow = DialogueService.BuildPrecisionMemoryPacket(campaignId, npcId, playerId, locationId, precisionQuery, routeContext, settings, true);
                memoryPacketResult["precisionEvidence"] = ReadDictionary(shadow, "precisionEvidence");
                memoryPacketResult["precisionShadowCharacters"] = ReadString(shadow, "memoryPacket", "").Length;
                memoryPacketResult["legacyMemoryCharacters"] = packet.Length;
            }
            retrievalTrace.Set("durationMs", timer.ElapsedMilliseconds);
            retrievalTrace.Set("memoryCount", memories.Count);
            retrievalTrace.Set("summaryCount", summaries.Count);
            retrievalTrace.Set("temporalKnowledgeCount", temporalKnowledge.Count);
            retrievalTrace.Set("semanticApplied", ReadBool(semanticRetrieval, "applied", false));
            retrievalTrace.Dispose();
            return memoryPacketResult;
        }

        internal static List<Dictionary<string, object>> QueryKnownRows(ReignDbConnection connection, string table, KnowledgeAccessContext knowledge, string extraWhere, int limit, List<string> queryTerms = null)
        {
            if (!new[] { "events", "memories", "summaries", "beliefs", "comprehension", "obligations" }.Contains(table))
                throw new InvalidOperationException("Unknown memory candidate table.");
            string where = string.IsNullOrWhiteSpace(extraWhere) ? "1=1" : "(" + extraWhere + ")";
            where += " AND COALESCE(payload_json,'{}') NOT LIKE '%\"continuityQuarantined\":true%'";
            where += " AND " + DialogueService.MemoryEligibilitySql(table) + " AND " + DialogueService.MemorySourceTimelineSql(table);
            string order = table == "summaries" ? "updated_ts" : "ts";
            if(queryTerms != null && queryTerms.Count>0)
            {
                if(table != "beliefs" && table != "comprehension") throw new InvalidOperationException("Mental recall query requires an owned mental layer.");
                string field=table=="beliefs" ? "claim" : "text";
                where+=" AND to_tsvector('simple',"+field+") @@ websearch_to_tsquery('simple',$query)";
                order="ts_rank(to_tsvector('simple',"+field+"),websearch_to_tsquery('simple',$query)) DESC,"+order;
            }
            string key = table == "events" ? "event_id" : table == "memories" ? "memory_id" : table == "summaries" ? "summary_id"
                : table == "beliefs" ? "belief_id" : table == "obligations" ? "obligation_id" : "comprehension_id";
            var result = new List<Dictionary<string, object>>();
            int offset = 0, batch = Math.Max(64, Math.Min(256, limit * 4));
            // Regional scope and identity rules are rechecked before counting
            // toward the candidate limit; unrelated rows cannot exhaust it.
            while (result.Count < Math.Max(1, limit))
            {
                var rows = ReignDatabase.QuerySql(connection, "SELECT * FROM " + table + " WHERE " + where
                    + " ORDER BY " + order + " DESC," + key + " LIMIT $batch OFFSET $offset;",
                    new Dictionary<string, object> { ["observer"] = knowledge?.NpcId ?? "", ["batch"] = batch, ["offset"] = offset,
                        ["query"] = BuildFtsQuery(queryTerms ?? new List<string>()), ["timeline"] = knowledge?.TimelineId ?? "" });
                result.AddRange(rows.Where(row => KnowledgeRowVisibleToNpc(table, row, knowledge, connection)).Take(Math.Max(1, limit) - result.Count));
                if (rows.Count < batch) break;
                offset += batch;
            }
            return result;
        }

        internal static List<Dictionary<string, object>> LoadFtsRows(ReignDbConnection connection, string table, string idColumn,
            IEnumerable<string> ids, KnowledgeAccessContext knowledge)
        {
            if (!string.Equals(table, "memories", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(table, "summaries", StringComparison.OrdinalIgnoreCase))
            {
                return new List<Dictionary<string, object>>();
            }
            if (!string.Equals(idColumn, "memory_id", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(idColumn, "summary_id", StringComparison.OrdinalIgnoreCase))
            {
                return new List<Dictionary<string, object>>();
            }

            List<string> selectedIds = (ids ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(120)
                .ToList();
            if (selectedIds.Count == 0)
            {
                return new List<Dictionary<string, object>>();
            }

            Dictionary<string, object> parameters = new Dictionary<string, object>();
            List<string> placeholders = new List<string>();
            for (int index = 0; index < selectedIds.Count; index++)
            {
                string key = "fts_id_" + index.ToString(CultureInfo.InvariantCulture);
                placeholders.Add("$" + key);
                parameters[key] = selectedIds[index];
            }

            return ReignDatabase.QuerySql(connection, "SELECT * FROM " + table + " WHERE status='active' AND " + idColumn
                    + " IN (" + string.Join(",", placeholders.ToArray()) + ");", parameters)
                .Where(row => KnowledgeRowVisibleToNpc(table, row, knowledge, connection))
                .ToList();
        }

        internal static bool KnowledgeRowVisibleToNpc(string table, Dictionary<string, object> row, KnowledgeAccessContext knowledge, ReignDbConnection connection = null)
        {
            if (row == null)
            {
                return false;
            }
            if (ReadBool(TryParseJsonObject(ReadString(row, "payload_json", "{}")), "continuityQuarantined", false)) return false;

            knowledge = knowledge ?? new KnowledgeAccessContext();
            if (!DialogueService.MemoryTimeVisible(row, knowledge, connection)) return false;
            string id = knowledge.NpcId;
            if (!knowledge.HasNpc)
            {
                if (table == "beliefs" || table == "comprehension") return false;
                return KnowledgeVisibilityAllows(row, knowledge);
            }

            if (KnowledgeListContains(row, "hidden_from_json", id))
            {
                return false;
            }

            string tableName = table ?? "";
            if (tableName.Equals("beliefs", StringComparison.OrdinalIgnoreCase))
            {
                return KnowledgeIdEquals(ReadString(row, "believer_id", ""), id);
            }

            if (tableName.Equals("obligations", StringComparison.OrdinalIgnoreCase))
            {
                return KnowledgeVisibilityAllows(row, knowledge)
                    || KnowledgeIdEquals(ReadString(row, "owed_by", ""), id)
                    || KnowledgeIdEquals(ReadString(row, "owed_to", ""), id)
                    || KnowledgeListContains(row, "known_by_json", id)
                    || KnowledgeListContains(row, "participants_json", id);
            }

            if (tableName.Equals("comprehension", StringComparison.OrdinalIgnoreCase))
            {
                return KnowledgeIdEquals(ReadString(row, "owner_id", ""), id);
            }

            if (tableName.Equals("summaries", StringComparison.OrdinalIgnoreCase))
            {
                return KnowledgeIdEquals(ReadString(row, "owner_id", ""), id)
                    || KnowledgeListContains(row, "known_by_json", id)
                    || KnowledgeVisibilityAllows(row, knowledge);
            }

            return KnowledgeVisibilityAllows(row, knowledge)
                || KnowledgeIdEquals(ReadString(row, "owner_id", ""), id)
                || KnowledgeListContains(row, "known_by_json", id)
                || KnowledgeListContains(row, "participants_json", id)
                || KnowledgeListContains(row, "witnesses_json", id)
                || KnowledgeListContains(row, "heard_as_rumor_by_json", id)
                || KnowledgePayloadListContains(row, id, "attendees", "attendeeIds", "nearbyHeroIds", "nearbyHeroes");
        }

        internal sealed class KnowledgeAccessContext
        {
            public string NpcId = "";
            public string TimelineId = "";
            public double WorldDay = double.MaxValue;
            public long AsOfAcceptedSequence = long.MaxValue;
            public long AsOfTimestamp = long.MaxValue;
            public long AsOfWorldHistorySequence = long.MaxValue;
            public string PlayerId = "";
            public string PlayerName = "";
            public string PlayerClanId = "";
            public string PlayerClanName = "";
            public string PlayerKingdomId = "";
            public string PlayerKingdomName = "";
            public string LocationId = "";
            public string KingdomId = "";
            public string ClanId = "";
            public string SettlementId = "";
            public string PartyId = "";
            public bool PlayerIdentityUnknown;
            public readonly HashSet<string> AllScopeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> KingdomIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> ClanIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> SettlementIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> PartyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> ArmyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public bool HasNpc
            {
                get { return !string.IsNullOrWhiteSpace(NpcId); }
            }
        }

        internal static KnowledgeAccessContext BuildKnowledgeAccessContext(string npcId, string playerId, string locationId, Dictionary<string, object> source)
        {
            source = source ?? new Dictionary<string, object>();
            Dictionary<string, object> profile = ReadDictionary(source, "hero")
                ?? ReadDictionary(source, "profile")
                ?? ReadDictionary(source, "speaker")
                ?? ReadDictionary(source, "npc")
                ?? source;

            KnowledgeAccessContext context = new KnowledgeAccessContext
            {
                TimelineId = ReadString(source, "timelineId", ""),
                AsOfAcceptedSequence = ReadLong(source, "memoryAsOfAcceptedSequence", long.MaxValue),
                AsOfTimestamp = ReadLong(source, "memoryAsOfTimestamp", long.MaxValue),
                AsOfWorldHistorySequence = ReadLong(source, "worldHistorySequence", long.MaxValue),
                WorldDay = ReadDouble(source, "worldDay", ReadDouble(source, "currentWorldDay", double.MaxValue)),
                NpcId = NormalizeKnowledgeId(FirstNonEmpty(npcId, ReadFirstString(source, "npcId", "heroStringId", "heroId", "speakerHeroStringId", "characterId"), ReadFirstString(profile, "npcId", "heroStringId", "heroId"))),
                PlayerId = NormalizeKnowledgeId(FirstNonEmpty(playerId, ReadFirstString(source, "playerId", "mainHeroStringId", "playerHeroStringId"), ReadFirstString(profile, "mainHeroStringId", "playerHeroStringId"))),
                PlayerName = FirstNonEmpty(ReadFirstString(source, "mainHeroName", "playerName"), ReadFirstString(profile, "mainHeroName", "playerName")),
                PlayerClanId = NormalizeKnowledgeId(FirstNonEmpty(ReadFirstString(source, "playerClanId", "playerClanStringId", "mainHeroClanId"), ReadFirstString(profile, "playerClanId", "playerClanStringId", "mainHeroClanId"))),
                PlayerClanName = FirstNonEmpty(ReadFirstString(source, "playerClanName", "mainHeroClanName"), ReadFirstString(profile, "playerClanName", "mainHeroClanName")),
                PlayerKingdomId = NormalizeKnowledgeId(FirstNonEmpty(ReadFirstString(source, "playerKingdomId", "playerKingdomStringId", "mainHeroKingdomId"), ReadFirstString(profile, "playerKingdomId", "playerKingdomStringId", "mainHeroKingdomId"))),
                PlayerKingdomName = FirstNonEmpty(ReadFirstString(source, "playerKingdomName", "mainHeroKingdomName"), ReadFirstString(profile, "playerKingdomName", "mainHeroKingdomName")),
                LocationId = NormalizeKnowledgeId(FirstNonEmpty(locationId, ReadFirstString(source, "locationId", "currentSettlementId", "settlementId"), ReadFirstString(profile, "currentSettlementId", "settlementId", "lastKnownSettlementId"))),
                KingdomId = NormalizeKnowledgeId(FirstNonEmpty(ReadFirstString(source, "npcKingdomId", "speakerKingdomId", "kingdomId"), ReadFirstString(profile, "kingdomId", "kingdomStringId", "mapFactionId"))),
                ClanId = NormalizeKnowledgeId(FirstNonEmpty(ReadFirstString(source, "npcClanId", "speakerClanId", "clanId"), ReadFirstString(profile, "clanId", "clanStringId"))),
                SettlementId = NormalizeKnowledgeId(FirstNonEmpty(ReadFirstString(source, "npcSettlementId", "currentSettlementId", "settlementId", "homeSettlementId", "governedSettlementId"), ReadFirstString(profile, "currentSettlementId", "settlementId", "homeSettlementId", "governedSettlementId"), locationId)),
                PartyId = NormalizeKnowledgeId(FirstNonEmpty(ReadFirstString(source, "npcPartyId", "speakerPartyId", "partyId"), ReadFirstString(profile, "partyId", "currentPartyId")))
            };
            Dictionary<string, object> identityView = ReadDictionary(source, "identityView");
            context.PlayerIdentityUnknown = identityView != null && !ReadBool(identityView, "knowsIdentity", false);

            AddKnowledgeContextId(context.AllScopeIds, context.NpcId);
            AddKnowledgeContextId(context.AllScopeIds, context.KingdomId);
            AddKnowledgeContextId(context.AllScopeIds, context.ClanId);
            AddKnowledgeContextId(context.AllScopeIds, context.LocationId);
            AddKnowledgeContextId(context.AllScopeIds, context.SettlementId);
            AddKnowledgeContextId(context.AllScopeIds, context.PartyId);

            AddKnowledgeContextId(context.KingdomIds, context.KingdomId);
            AddKnowledgeContextId(context.ClanIds, context.ClanId);
            AddKnowledgeContextId(context.SettlementIds, context.LocationId);
            AddKnowledgeContextId(context.SettlementIds, context.SettlementId);
            AddKnowledgeContextId(context.PartyIds, context.PartyId);

            AddKnowledgeContextIds(context.KingdomIds, KnowledgeValuesFromDictionary(source, "kingdomIds", "npcKingdomIds", "nearbyKingdomIds"));
            AddKnowledgeContextIds(context.KingdomIds, KnowledgeValuesFromDictionary(profile, "kingdomIds", "npcKingdomIds", "nearbyKingdomIds"));
            AddKnowledgeContextIds(context.ClanIds, KnowledgeValuesFromDictionary(source, "clanIds", "npcClanIds", "nearbyClanIds"));
            AddKnowledgeContextIds(context.ClanIds, KnowledgeValuesFromDictionary(profile, "clanIds", "npcClanIds", "nearbyClanIds"));
            AddKnowledgeContextIds(context.SettlementIds, KnowledgeValuesFromDictionary(source, "settlementIds", "nearbySettlementIds", "visibleSettlementIds"));
            AddKnowledgeContextIds(context.SettlementIds, KnowledgeValuesFromDictionary(profile, "settlementIds", "nearbySettlementIds", "visibleSettlementIds"));
            AddKnowledgeContextIds(context.PartyIds, KnowledgeValuesFromDictionary(source, "partyIds", "nearbyPartyIds", "visiblePartyIds"));
            AddKnowledgeContextIds(context.PartyIds, KnowledgeValuesFromDictionary(profile, "partyIds", "nearbyPartyIds", "visiblePartyIds"));
            AddKnowledgeContextIds(context.ArmyIds, KnowledgeValuesFromDictionary(source, "armyIds", "nearbyArmyIds", "visibleArmyIds"));
            AddKnowledgeContextIds(context.ArmyIds, KnowledgeValuesFromDictionary(profile, "armyIds", "nearbyArmyIds", "visibleArmyIds"));

            AddKnowledgeContextIds(context.AllScopeIds, context.KingdomIds);
            AddKnowledgeContextIds(context.AllScopeIds, context.ClanIds);
            AddKnowledgeContextIds(context.AllScopeIds, context.SettlementIds);
            AddKnowledgeContextIds(context.AllScopeIds, context.PartyIds);
            AddKnowledgeContextIds(context.AllScopeIds, context.ArmyIds);
            return context;
        }

        internal static bool KnowledgeVisibilityAllows(Dictionary<string, object> row, KnowledgeAccessContext context)
        {
            Dictionary<string, object> payload = KnowledgePayload(row);
            string visibility = ReadString(row, "visibility", "").Trim();
            string visibilityKey = NormalizeKnowledgeScopeKey(visibility);
            string scope = KnowledgeScopeText(row, payload);

            if (visibilityKey.Equals("world", StringComparison.OrdinalIgnoreCase)
                || visibilityKey.Equals("global", StringComparison.OrdinalIgnoreCase)
                || visibilityKey.Equals("common", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (visibilityKey.StartsWith("public", StringComparison.OrdinalIgnoreCase))
            {
                if (KnowledgeIsCommonWorldEvent(row, payload))
                {
                    return true;
                }

                string inlineScope = "";
                if (visibility.Length > 6 && visibility[6] == '_')
                {
                    inlineScope = visibility.Substring(7);
                }

                scope = FirstNonEmpty(scope, inlineScope);
                if (string.IsNullOrWhiteSpace(scope) && !KnowledgeHasExplicitScope(row, payload))
                {
                    return true;
                }

                return KnowledgeScopeMatches(scope, row, payload, context);
            }

            if (visibilityKey.Equals("local", StringComparison.OrdinalIgnoreCase)
                || visibilityKey.Equals("nearby", StringComparison.OrdinalIgnoreCase)
                || visibilityKey.Equals("kingdom", StringComparison.OrdinalIgnoreCase)
                || visibilityKey.Equals("clan", StringComparison.OrdinalIgnoreCase)
                || visibilityKey.Equals("settlement", StringComparison.OrdinalIgnoreCase)
                || visibilityKey.Equals("party", StringComparison.OrdinalIgnoreCase)
                || visibilityKey.Equals("army", StringComparison.OrdinalIgnoreCase))
            {
                return KnowledgeScopeMatches(visibility, row, payload, context);
            }

            return false;
        }

        private static bool KnowledgeIsCommonWorldEvent(Dictionary<string, object> row, Dictionary<string, object> payload)
        {
            string eventType = NormalizeKnowledgeScopeKey(FirstNonEmpty(
                ReadFirstString(payload, "eventType", "event_type", "type", "worldEventType", "world_event_type"),
                ReadString(row, "event_type", ""),
                ReadString(row, "memory_type", "")));
            if (string.IsNullOrWhiteSpace(eventType))
            {
                return false;
            }

            if (eventType.Contains("private")
                || eventType.Contains("nearby")
                || eventType.Contains("scout")
                || eventType.Contains("conversation")
                || eventType.Contains("trade")
                || eventType.Contains("bargain")
                || eventType.Contains("secret")
                || eventType.Contains("promise")
                || eventType.Contains("threat")
                || eventType.Contains("blackmail"))
            {
                return false;
            }

            return eventType.Contains("war")
                || eventType.Contains("peace")
                || eventType.Contains("alliance")
                || eventType.Contains("treaty")
                || eventType.Contains("pact")
                || eventType.Contains("marriage")
                || eventType.Contains("kingdom")
                || eventType.Contains("rebellion")
                || eventType.Contains("settlement_captured")
                || eventType.Contains("settlement_owner")
                || eventType.Contains("fief_granted")
                || eventType.Contains("siege_result");
        }

        private static bool KnowledgeScopeMatches(string scope, Dictionary<string, object> row, Dictionary<string, object> payload, KnowledgeAccessContext context)
        {
            context = context ?? new KnowledgeAccessContext();
            string scopeKey = NormalizeKnowledgeScopeKey(scope);
            List<string> scopeIds = KnowledgeScopedIds(row, payload);

            if (string.IsNullOrWhiteSpace(scopeKey))
            {
                return scopeIds.Count > 0 && KnowledgeContainsAny(context.AllScopeIds, scopeIds);
            }

            if (scopeKey.Equals("world", StringComparison.OrdinalIgnoreCase)
                || scopeKey.Equals("global", StringComparison.OrdinalIgnoreCase)
                || scopeKey.Equals("common", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (scopeKey.Contains("kingdom") || scopeKey.Contains("faction"))
            {
                List<string> ids = MergeStringLists(scopeIds, KnowledgeValuesFromDictionary(payload, "scopeKingdomIds", "scope_kingdom_ids", "kingdomIds", "kingdomId", "actorKingdomId", "actorKingdomStringId", "targetKingdomId", "targetKingdomStringId", "ownerKingdomId"));
                return KnowledgeContainsAny(context.KingdomIds, ids);
            }

            if (scopeKey.Contains("clan"))
            {
                List<string> ids = MergeStringLists(scopeIds, KnowledgeValuesFromDictionary(payload, "scopeClanIds", "scope_clan_ids", "clanIds", "clanId", "actorClanId", "actorClanStringId", "targetClanId", "targetClanStringId", "ownerClanId"));
                return KnowledgeContainsAny(context.ClanIds, ids);
            }

            if (scopeKey.Contains("settlement") || scopeKey.Contains("local") || scopeKey.Contains("town") || scopeKey.Contains("castle") || scopeKey.Contains("village"))
            {
                List<string> ids = MergeStringLists(scopeIds, KnowledgeValuesFromDictionary(payload, "scopeSettlementIds", "scope_settlement_ids", "settlementIds", "settlementId", "locationId", "currentSettlementId", "targetSettlementId", "boundSettlementId"));
                ids = MergeStringLists(ids, new[] { ReadString(row, "location_id", "") });
                return KnowledgeContainsAny(context.SettlementIds, ids);
            }

            if (scopeKey.Contains("party") || scopeKey.Contains("nearby"))
            {
                List<string> ids = MergeStringLists(scopeIds, KnowledgeValuesFromDictionary(payload, "scopePartyIds", "scope_party_ids", "partyIds", "partyId", "nearbyPartyIds", "visiblePartyIds", "actorPartyId", "targetPartyId"));
                return KnowledgeContainsAny(context.PartyIds, ids)
                    || KnowledgePayloadListContains(row, context.NpcId, "nearbyHeroIds", "nearbyHeroes", "visibleHeroIds", "visibleHeroes")
                    || KnowledgeContainsAny(context.SettlementIds, MergeStringLists(KnowledgeValuesFromDictionary(payload, "locationId", "settlementId", "currentSettlementId"), new[] { ReadString(row, "location_id", "") }));
            }

            if (scopeKey.Contains("army") || scopeKey.Contains("danger") || scopeKey.Contains("siege") || scopeKey.Contains("raid"))
            {
                List<string> ids = MergeStringLists(scopeIds, KnowledgeValuesFromDictionary(payload, "scopeArmyIds", "scope_army_ids", "armyIds", "armyId", "nearbyArmyIds", "visibleArmyIds", "attackingArmyId", "defendingArmyId"));
                return KnowledgeContainsAny(context.ArmyIds, ids)
                    || KnowledgeContainsAny(context.PartyIds, KnowledgeValuesFromDictionary(payload, "nearbyPartyIds", "visiblePartyIds", "partyIds"))
                    || KnowledgeContainsAny(context.SettlementIds, MergeStringLists(KnowledgeValuesFromDictionary(payload, "locationId", "settlementId", "currentSettlementId", "targetSettlementId"), new[] { ReadString(row, "location_id", "") }));
            }

            if (scopeKey.Contains("attendee") || scopeKey.Contains("witness"))
            {
                return KnowledgeListContains(row, "witnesses_json", context.NpcId)
                    || KnowledgePayloadListContains(row, context.NpcId, "attendees", "attendeeIds", "witnesses", "witnessIds");
            }

            return KnowledgeContainsAny(context.AllScopeIds, scopeIds);
        }

        internal static bool KnowledgeHasExplicitScope(Dictionary<string, object> row, Dictionary<string, object> payload)
        {
            return !string.IsNullOrWhiteSpace(KnowledgeScopeText(row, payload))
                || KnowledgeScopedIds(row, payload).Count > 0;
        }

        private static string KnowledgeScopeText(Dictionary<string, object> row, Dictionary<string, object> payload)
        {
            return FirstNonEmpty(
                ReadString(row, "scope", ""),
                ReadFirstString(payload, "knowledgeScope", "knowledge_scope", "visibilityScope", "visibility_scope", "awarenessScope", "awareness_scope", "scope"));
        }

        private static List<string> KnowledgeScopedIds(Dictionary<string, object> row, Dictionary<string, object> payload)
        {
            return KnowledgeValuesFromDictionary(payload,
                "scopeIds", "scope_ids", "audienceIds", "audience_ids",
                "scopeKingdomIds", "scope_kingdom_ids", "scopeClanIds", "scope_clan_ids",
                "scopeSettlementIds", "scope_settlement_ids", "scopePartyIds", "scope_party_ids",
                "scopeArmyIds", "scope_army_ids");
        }

        private static bool KnowledgePayloadListContains(Dictionary<string, object> row, string normalizedNeedle, params string[] keys)
        {
            if (string.IsNullOrWhiteSpace(normalizedNeedle))
            {
                return false;
            }

            return KnowledgeValuesFromDictionary(KnowledgePayload(row), keys).Any(value => KnowledgeIdEquals(value, normalizedNeedle));
        }

        internal static Dictionary<string, object> KnowledgePayload(Dictionary<string, object> row)
        {
            return TryParseJsonObject(ReadString(row, "payload_json", "")) ?? new Dictionary<string, object>();
        }

        internal static bool UnknownIdentityMemoryEvidenceAllowed(Dictionary<string, object> row,
            KnowledgeAccessContext knowledge, string evidenceText = "")
        {
            if (row == null || knowledge == null || !knowledge.PlayerIdentityUnknown)
            {
                return true;
            }

            string text = FirstNonEmpty(evidenceText,
                ReadFirstString(row, "summary", "claim", "text", "description", "belief"));
            bool referencesPlayer = UnknownIdentityRowReferencesPlayer(row, knowledge);
            bool namesCanonicalIdentity = UnknownIdentityContainsCanonicalIdentity(text, knowledge);
            bool exposesPrivateStatus = referencesPlayer && UnknownIdentityContainsPrivateStatus(text);

            // A stranger's harmless preferences, promises, and directly witnessed conduct
            // may remain useful before formal identification. Canonical identity and native
            // clan/wealth/political state may not cross that boundary simply because a
            // world snapshot or an earlier faulty reconstruction stored them in memory.
            return !namesCanonicalIdentity && !exposesPrivateStatus;
        }

        private static bool UnknownIdentityRowReferencesPlayer(Dictionary<string, object> row,
            KnowledgeAccessContext knowledge)
        {
            if (row == null || knowledge == null)
            {
                return false;
            }

            List<string> protectedIds = new[]
                {
                    knowledge.PlayerId, knowledge.PlayerClanId, knowledge.PlayerKingdomId,
                    "main_hero", "player_hero", "player_faction", "player_clan", "player_kingdom"
                }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(NormalizeKnowledgeId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string[] scalarKeys =
            {
                "subject_id", "object_id", "owner_id", "target_id", "believer_id",
                "owed_by", "owed_to", "player_id", "hero_id", "character_id"
            };
            foreach (string key in scalarKeys)
            {
                string value = NormalizeKnowledgeId(ReadString(row, key, ""));
                if (protectedIds.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            string[] listKeys =
            {
                "about_entities_json", "participants_json", "known_by_json", "witnesses_json"
            };
            foreach (string key in listKeys)
            {
                if (protectedIds.Any(id => KnowledgeListContains(row, key, id)))
                {
                    return true;
                }
            }

            Dictionary<string, object> payload = KnowledgePayload(row);
            string[] payloadKeys =
            {
                "playerId", "mainHeroStringId", "playerHeroStringId", "subjectId", "objectId",
                "targetId", "aboutEntities", "aboutEntityIds", "participants", "participantIds",
                "playerClanId", "playerClanStringId", "playerKingdomId", "playerKingdomStringId"
            };
            return protectedIds.Any(id => KnowledgeValuesFromDictionary(payload, payloadKeys)
                .Contains(id, StringComparer.OrdinalIgnoreCase));
        }

        private static bool UnknownIdentityContainsCanonicalIdentity(string text, KnowledgeAccessContext knowledge)
        {
            string value = text ?? "";
            if (knowledge == null || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return new[]
                {
                    knowledge.PlayerName, knowledge.PlayerClanName, knowledge.PlayerKingdomName,
                    knowledge.PlayerId, knowledge.PlayerClanId, knowledge.PlayerKingdomId
                }
                .Where(item => !string.IsNullOrWhiteSpace(item) && item.Length >= 3)
                .Any(item => value.IndexOf(item, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static bool UnknownIdentityContainsPrivateStatus(string text)
        {
            string normalized = NormalizeLookup(text ?? "");
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            return Regex.IsMatch(normalized,
                @"\b(clan(?:\s+tier|\s+leader|\s+standing|\s+renown|\s+influence|\s+wealth|\s+is\s+independent)?|"
                + @"fiefs?|landless|no\s+lands?|gold|denars?|wealth|destitute|rich|poor|renown|influence|"
                + @"kingdom(?:\s+leader|\s+standing)?|ruler|vassal|mercenary|noble\s+rank|title|rank|"
                + @"workshops?|caravans?|party\s+inventory|inventory\s+value|troop\s+count|party\s+size|"
                + @"owns?\s+(?:a\s+)?(?:town|castle|village|settlement))\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        internal static Dictionary<string, object> SanitizeUnknownIdentityMemoryRow(
            Dictionary<string, object> row, KnowledgeAccessContext knowledge)
        {
            if (row == null || knowledge == null || !knowledge.PlayerIdentityUnknown)
            {
                return row;
            }

            Dictionary<string, object> copy =
                new Dictionary<string, object>(row, StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[] { "summary", "claim", "text", "description", "belief", "payload_json" })
            {
                if (copy.TryGetValue(key, out object raw) && raw != null)
                {
                    copy[key] = SanitizeUnknownIdentityEvidenceText(Convert.ToString(raw, CultureInfo.InvariantCulture), knowledge);
                }
            }
            foreach (string key in new[] { "participants_json", "about_entities_json" })
            {
                if (copy.TryGetValue(key, out object raw) && raw != null)
                {
                    copy[key] = SanitizeUnknownIdentityEvidenceText(Convert.ToString(raw, CultureInfo.InvariantCulture), knowledge);
                }
            }
            if (NormalizeKnowledgeId(ReadString(copy, "target_id", "")).Equals(
                NormalizeKnowledgeId(knowledge.PlayerId), StringComparison.OrdinalIgnoreCase))
            {
                copy["target_id"] = "current unidentified interlocutor";
            }
            List<Dictionary<string, object>> incidents = ReadDictionaryList(copy, "incidents");
            if (incidents.Count > 0)
            {
                copy["incidents"] = incidents
                    .Where(item => UnknownIdentityMemoryEvidenceAllowed(item, knowledge,
                        ReadString(item, "summary", "")))
                    .Select(item => SanitizeUnknownIdentityMemoryRow(item, knowledge))
                    .ToList();
            }
            return copy;
        }

        internal static string SanitizeUnknownIdentityEvidenceText(string text, KnowledgeAccessContext knowledge)
        {
            string result = text ?? "";
            if (knowledge == null || !knowledge.PlayerIdentityUnknown || string.IsNullOrWhiteSpace(result))
            {
                return result;
            }

            foreach (string value in new[]
                {
                    knowledge.PlayerName, knowledge.PlayerId, "main_hero", "player_hero",
                    knowledge.PlayerClanName, knowledge.PlayerClanId, "player_faction", "player_clan",
                    knowledge.PlayerKingdomName, knowledge.PlayerKingdomId, "player_kingdom"
                }.Where(value => !string.IsNullOrWhiteSpace(value))
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .OrderByDescending(value => value.Length))
            {
                string replacement = value.Equals(knowledge.PlayerClanName, StringComparison.OrdinalIgnoreCase)
                    || value.Equals(knowledge.PlayerClanId, StringComparison.OrdinalIgnoreCase)
                    || value.Equals("player_faction", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("player_clan", StringComparison.OrdinalIgnoreCase)
                        ? "unidentified_clan"
                        : value.Equals(knowledge.PlayerKingdomName, StringComparison.OrdinalIgnoreCase)
                          || value.Equals(knowledge.PlayerKingdomId, StringComparison.OrdinalIgnoreCase)
                          || value.Equals("player_kingdom", StringComparison.OrdinalIgnoreCase)
                            ? "unidentified_realm"
                            : "unidentified_interlocutor";
                result = Regex.Replace(result, Regex.Escape(value), replacement,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            result = Regex.Replace(result, @"\bthe\s+armed\s+stranger\b",
                "the player (not yet identified by this NPC)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            result = Regex.Replace(result, @"\barmed\s+stranger\b",
                "the player (not yet identified by this NPC)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return result;
        }

        private static List<string> KnowledgeValuesFromDictionary(Dictionary<string, object> source, params string[] keys)
        {
            List<string> values = new List<string>();
            if (source == null || keys == null)
            {
                return values;
            }

            foreach (string key in keys)
            {
                if (source.TryGetValue(key, out object raw) && raw != null)
                {
                    values.AddRange(ExtractEntityIds(raw));
                }
            }

            return values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(NormalizeKnowledgeId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool KnowledgeContainsAny(HashSet<string> haystack, IEnumerable<string> needles)
        {
            if (haystack == null || needles == null)
            {
                return false;
            }

            foreach (string needle in needles)
            {
                string normalized = NormalizeKnowledgeId(needle);
                if (!string.IsNullOrWhiteSpace(normalized) && haystack.Contains(normalized))
                {
                    return true;
                }
            }

            return false;
        }

        private static void AddKnowledgeContextIds(HashSet<string> target, IEnumerable<string> values)
        {
            foreach (string value in values ?? Enumerable.Empty<string>())
            {
                AddKnowledgeContextId(target, value);
            }
        }

        private static void AddKnowledgeContextId(HashSet<string> target, string value)
        {
            string normalized = NormalizeKnowledgeId(value);
            if (target != null && !string.IsNullOrWhiteSpace(normalized))
            {
                target.Add(normalized);
            }
        }

        private static string NormalizeKnowledgeScopeKey(string value)
        {
            return Regex.Replace((value ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
        }

        internal static bool KnowledgeListContains(Dictionary<string, object> row, string key, string normalizedNeedle)
        {
            if (string.IsNullOrWhiteSpace(normalizedNeedle))
            {
                return false;
            }

            foreach (string value in TextListFromJson(ReadString(row, key, "[]")))
            {
                if (KnowledgeIdEquals(value, normalizedNeedle))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool KnowledgeIdEquals(string value, string normalizedNeedle)
        {
            return !string.IsNullOrWhiteSpace(normalizedNeedle)
                && string.Equals(NormalizeKnowledgeId(value), normalizedNeedle, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeKnowledgeId(string value)
        {
            return (value ?? "").Trim();
        }
    }
}
