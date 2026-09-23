using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly string[] MemoryLaneIds =
        {
            "personal_state",
            "interpersonal_history",
            "world_affairs",
            "local_awareness",
            "commitments_and_plots",
            "beliefs_and_rumors",
            "exact_history"
        };

        private static Dictionary<string, object> BuildMemoryRetrievalRoute(Dictionary<string, object> settings, string topic, Dictionary<string, object> context)
        {
            settings = settings ?? LoadSettings();
            context = context ?? new Dictionary<string, object>();
            string normalized = (topic ?? "").ToLowerInvariant();
            bool explicitExactRecall = LooksLikeMostRecentConversationRecall(topic) || LooksLikeExactFactRestatement(topic) || ContainsAny(normalized,
                "remember", "exactly", "exact words", "what you said", "what i said", "you told me", "i told you",
                "when we met", "previous meeting", "earlier conversation", "first conversation", "first scene",
                "repeat", "quote", "trail-name", "trail name", "wrote to", "letter said");
            Dictionary<string, double> scores = MemoryLaneIds.ToDictionary(id => id, id => 0d, StringComparer.OrdinalIgnoreCase);
            AddLaneScore(scores, "personal_state", normalized, 5d,
                "feel", "feeling", "mood", "today", "lately", "dream", "fear", "desire", "hope", "regret", "how are you", "your day", "yourself");
            AddLaneScore(scores, "interpersonal_history", normalized, 5d,
                "between us", "think of me", "trust", "friend", "friendship", "relationship", "love", "hate", "you and i",
                "last time", "when we met", "our conversation", "earlier conversation", "earlier conversations", "first scene");
            AddLaneScore(scores, "world_affairs", normalized, 6d,
                "tournament", "war", "peace", "kingdom", "siege", "alliance", "marriage", "battle", "rebellion", "ruler", "captured", "fief", "army campaign");
            AddLaneScore(scores, "local_awareness", normalized, 6d,
                "nearby", "around here", "outside", "in this town", "this settlement", "enemy army", "danger here", "what do you see", "local");
            AddLaneScore(scores, "commitments_and_plots", normalized, 7d,
                "promise", "promised", "owe", "debt", "favor", "oath", "bargain", "deal", "threat", "blackmail", "scheme", "plan", "secret agreement",
                "spymaster", "spy mission", "intelligence mission", "covert operation", "operation you performed", "mission i gave you");
            AddLaneScore(scores, "beliefs_and_rumors", normalized, 7d,
                "rumor", "rumour", "gossip", "heard", "believe", "suspect", "people say", "word is", "claim");
            AddLaneScore(scores, "exact_history", normalized, 9d,
                "remember", "exactly", "exact words", "what you said", "what i said", "you told me", "i told you",
                "days ago", "weeks ago", "when we met", "previous meeting", "earlier conversation", "first conversation",
                "first scene", "repeat", "quote", "trail-name", "trail name", "wrote to", "letter said");
            if (LooksLikeExactFactRestatement(topic))
            {
                scores["exact_history"] += 9d;
            }

            // Interactive routing must remain local and deterministic. Semantic evidence is
            // applied by the single indexed vector search later in the pipeline; calling the
            // embedding worker here used to add another complete inference pass per turn.
            Dictionary<string, double> minimeScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            double semanticWorldScore = minimeScores.ContainsKey("world_affairs") ? minimeScores["world_affairs"] : -1d;
            double bestSemanticLaneScore = minimeScores.Count == 0 ? -1d : minimeScores.Values.Max();
            bool semanticWorldConnection = semanticWorldScore >= 0.45d && semanticWorldScore >= bestSemanticLaneScore - 0.02d;
            bool worldConnected = scores["world_affairs"] > 0d
                || ReadBool(context, "worldEventConnected", false)
                || ReadBool(context, "currentDanger", false)
                || ReadStringList(context, "aboutEntityIds").Count > 0
                || LooksLikeNamedWorldRecall(topic)
                || semanticWorldConnection;
            if (!worldConnected)
            {
                scores["world_affairs"] = -100d;
            }
            else if (scores["world_affairs"] <= 0d)
            {
                scores["world_affairs"] = 2.5d;
            }

            foreach (KeyValuePair<string, double> pair in minimeScores)
            {
                if (scores.ContainsKey(pair.Key) && (pair.Key != "world_affairs" || worldConnected))
                {
                    scores[pair.Key] += Math.Max(0d, pair.Value) * 5d;
                }
            }

            if (scores.Values.Max() <= 0d)
            {
                scores["personal_state"] = 1d;
                scores["interpersonal_history"] = 0.75d;
            }
            else if (scores["personal_state"] > 0d && scores["interpersonal_history"] <= 0d)
            {
                scores["interpersonal_history"] = 0.75d;
            }

            List<string> ordered = scores.Where(pair => pair.Value > 0d)
                .OrderByDescending(pair => pair.Value).ThenBy(pair => Array.IndexOf(MemoryLaneIds, pair.Key))
                .Select(pair => pair.Key).Take(3).ToList();
            if (explicitExactRecall)
            {
                ordered.RemoveAll(lane => string.Equals(lane, "exact_history", StringComparison.OrdinalIgnoreCase));
                ordered.Insert(0, "exact_history");
                ordered = ordered.Take(3).ToList();
            }
            if (ordered.Count == 0)
            {
                ordered.Add("personal_state");
            }
            string primary = ordered[0];
            List<string> supporting = ordered.Skip(1).Take(2).ToList();
            Dictionary<string, object> allocation = new Dictionary<string, object> { [primary] = 55 };
            if (supporting.Count > 0) allocation[supporting[0]] = supporting.Count == 1 ? 45 : 25;
            if (supporting.Count > 1) allocation[supporting[1]] = 20;

            double currentDay = ReadDouble(context, "worldDay", ReadDouble(context, "currentWorldDay", 0d));
            if (currentDay <= 0d)
            {
                Match currentDayMatch = Regex.Match(topic ?? "", @"campaign\s+day\s*:?\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                if (currentDayMatch.Success)
                {
                    double.TryParse(currentDayMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out currentDay);
                }
            }
            double targetDay = ExtractTargetWorldDay(normalized, currentDay);
            bool exact = explicitExactRecall || ordered.Contains("exact_history", StringComparer.OrdinalIgnoreCase)
                || scores["world_affairs"] > 0d || LooksLikeNamedWorldRecall(topic);
            return new Dictionary<string, object>
            {
                ["primaryLane"] = primary,
                ["supportingLanes"] = supporting,
                ["selectedLanes"] = ordered,
                ["reason"] = "deterministic intent scores" + (minimeScores.Count > 0 ? " with Minime lane reranking" : " with deterministic fallback"),
                ["scores"] = scores.ToDictionary(pair => pair.Key, pair => (object)Math.Round(pair.Value, 4), StringComparer.OrdinalIgnoreCase),
                ["tokenAllocationPercent"] = allocation,
                ["worldConnected"] = worldConnected,
                ["needsExactTranscript"] = exact,
                ["targetWorldDay"] = targetDay,
                ["exactTranscriptTokenBudget"] = 600,
                ["maxSupportingLanes"] = 2,
                ["minimeApplied"] = minimeScores.Count > 0
            };
        }

        private static bool LooksLikeNamedWorldRecall(string topic)
        {
            string text = topic ?? "";
            string normalized = text.ToLowerInvariant();
            if (!ContainsAny(normalized, "what happened", "happened to", "tell me about", "news of", "news about",
                "do you know about", "what became of", "where is", "where are"))
            {
                return false;
            }

            MatchCollection names = Regex.Matches(text, @"\b[\p{Lu}][\p{L}'-]{2,}\b");
            HashSet<string> sentenceWords = new HashSet<string>(new[]
            {
                "What", "Where", "When", "Who", "Why", "How", "Tell", "Do", "Did", "Does", "Is", "Are", "The", "A", "An"
            }, StringComparer.OrdinalIgnoreCase);
            return names.Cast<Match>().Any(match => !sentenceWords.Contains(match.Value));
        }

        private static void AddLaneScore(Dictionary<string, double> scores, string lane, string text, double amount, params string[] cues)
        {
            foreach (string cue in cues ?? new string[0])
            {
                if (!string.IsNullOrWhiteSpace(cue) && ContainsMemoryLaneCue(text, cue))
                {
                    scores[lane] += amount;
                }
            }
        }

        private static bool ContainsMemoryLaneCue(string text, string cue)
        {
            string phrase = Regex.Escape((cue ?? "").Trim()).Replace("\\ ", @"\s+");
            return phrase.Length > 0 && Regex.IsMatch(text ?? "",
                @"(?<![\p{L}\p{N}_])" + phrase + @"(?![\p{L}\p{N}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static Dictionary<string, double> TryMinimeMemoryLaneScores(Dictionary<string, object> settings, string topic, bool worldConnected)
        {
            Dictionary<string, double> scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (!ReadBool(settings, "enableMinimeMemoryWorker", true) || !ReadBool(settings, "enableMinimeMemoryReranking", true)
                || string.IsNullOrWhiteSpace(topic))
            {
                return scores;
            }
            string endpoint = ReadString(settings, "minimeRerankUrl", "http://127.0.0.1:8082/rerank");
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return scores;
            }
            List<Dictionary<string, object>> candidates = new List<Dictionary<string, object>>
            {
                LaneCandidate("personal_state", "The NPC's recent feelings, mood, fears, desires, private experiences, and current personal condition."),
                LaneCandidate("interpersonal_history", "Past interactions and relationship continuity between this NPC and the player or another named person."),
                LaneCandidate("world_affairs", "Wars, alliances, marriages, tournaments, sieges, rulers, settlements, and major public events."),
                LaneCandidate("local_awareness", "Nearby armies, local danger, the present settlement, witnessed activity, and immediate surroundings."),
                LaneCandidate("commitments_and_plots", "Promises, debts, favors, threats, schemes, bargains, oaths, secrets, and unresolved plans."),
                LaneCandidate("beliefs_and_rumors", "What the NPC believes, suspects, has heard as gossip, or considers uncertain court knowledge."),
                LaneCandidate("exact_history", "A request to recall exact wording, a specific earlier conversation, letter, meeting, or date.")
            };
            try
            {
                string raw = PostJsonToUrl(endpoint, Json.Serialize(new Dictionary<string, object>
                {
                    ["query"] = LimitText(topic, 1600), ["candidates"] = candidates, ["top_k"] = candidates.Count
                }), Math.Max(250, Math.Min(15000, ReadInt(settings, "minimeRerankTimeoutMs", 15000))));
                Dictionary<string, object> decoded = TryParseJsonObject(raw);
                List<Dictionary<string, object>> results = ReadDictionaryList(decoded, "results")
                    .Concat(ReadDictionaryList(decoded, "ranked")).Concat(ReadDictionaryList(decoded, "items")).ToList();
                foreach (Dictionary<string, object> result in results)
                {
                    string id = ReadFirstString(result, "id", "candidate_id", "candidateId");
                    if (!MemoryLaneIds.Contains(id, StringComparer.OrdinalIgnoreCase) || id == "world_affairs" && !worldConnected) continue;
                    if (!result.ContainsKey("score") && !result.ContainsKey("similarity")) continue;
                    double semantic = ClampDouble(ReadDouble(result, "score", ReadDouble(result, "similarity", 0d)), -1d, 1d);
                    if (semantic >= 0.25d)
                    {
                        scores[id] = semantic;
                    }
                }
            }
            catch (Exception ex)
            {
                LogOperational("minime.memory_lane_route_failed", new Dictionary<string, object> { ["error"] = LimitText(ex.Message, 500) });
                scores.Clear();
            }
            return scores;
        }

        private static Dictionary<string, object> LaneCandidate(string id, string text)
        {
            return new Dictionary<string, object> { ["id"] = id, ["text"] = text };
        }

        private static bool RouteHasLane(Dictionary<string, object> route, string lane)
        {
            return ReadStringList(route, "selectedLanes").Contains(lane, StringComparer.OrdinalIgnoreCase)
                || string.Equals(ReadString(route, "primaryLane", ""), lane, StringComparison.OrdinalIgnoreCase)
                || ReadStringList(route, "supportingLanes").Contains(lane, StringComparer.OrdinalIgnoreCase);
        }

        private static bool MemoryRowAllowedByRoute(Dictionary<string, object> row, Dictionary<string, object> route)
        {
            string domain = ClassifyMemoryDomain(row);
            return RouteHasLane(route, domain)
                || domain == "interpersonal_history" && RouteHasLane(route, "exact_history")
                || domain == "personal_state" && RouteHasLane(route, "interpersonal_history");
        }

        private static bool SummaryRowAllowedByRoute(Dictionary<string, object> row, Dictionary<string, object> route)
        {
            string lane = NormalizeMemoryCategory(ReadString(row, "memory_lane", ""));
            if (string.IsNullOrWhiteSpace(lane))
            {
                lane = ClassifyMemoryDomain(row);
            }
            return RouteHasLane(route, lane)
                || ReadString(row, "summary_type", "") == "scene" && RouteHasLane(route, "exact_history")
                || lane == "interpersonal_history" && RouteHasLane(route, "personal_state");
        }

        private static bool RouteRecentFallback(Dictionary<string, object> row, Dictionary<string, object> route, string timestampColumn)
        {
            string domain = ClassifyMemoryDomain(row);
            string primary = ReadString(route, "primaryLane", "personal_state");
            if (!string.Equals(domain, primary, StringComparison.OrdinalIgnoreCase)
                && !(primary == "exact_history" && domain == "interpersonal_history"))
            {
                return false;
            }
            if (ReadDouble(row, "importance", 0d) >= 0.75d)
            {
                return true;
            }
            long timestamp = ReadLong(row, timestampColumn, 0);
            long age = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp);
            return timestamp > 0 && age <= 7L * 86400L;
        }

        private static Dictionary<string, object> SearchExactConversationHistory(ReignDbConnection connection, string npcId, string topic,
            Dictionary<string, object> route, int charBudget, Dictionary<string, object> semanticSearch = null, string excludedSessionId = "",
            int recentRawTurnLimit = 30, KnowledgeAccessContext knowledge = null)
        {
            if (LooksLikeMostRecentConversationRecall(topic))
            {
                Dictionary<string, object> recentClosed = LoadMostRecentClosedConversation(connection, npcId, excludedSessionId,
                    charBudget, recentRawTurnLimit, knowledge);
                if (!string.IsNullOrWhiteSpace(ReadString(recentClosed, "text", "")))
                {
                    return recentClosed;
                }
            }

            List<Dictionary<string, object>> matched = new List<Dictionary<string, object>>();
            List<string> terms = MemoryQueryTerms(topic).Where(term => !ExactRecallStopWords.Contains(term, StringComparer.OrdinalIgnoreCase)).Take(64).ToList();
            string fts = BuildFtsQuery(terms);
            string excludeCurrent = string.IsNullOrWhiteSpace(excludedSessionId) ? "" : " AND s.session_id<>$excluded_session";
            Dictionary<string, object> searchParameters = new Dictionary<string, object> { ["query"] = fts, ["npc"] = npcId, ["excluded_session"] = excludedSessionId ?? "" };
            if (!string.IsNullOrWhiteSpace(fts))
            {
                try
                {
                    string lexicalSql = ReignPostgreSqlDialect.IsPostgreSql(connection)
                        ? @"SELECT t.*,ts_rank(
to_tsvector('simple',COALESCE(f.text,'')),
websearch_to_tsquery('simple',$query)) AS lexical_rank
FROM conversation_turn_fts f
JOIN conversation_turns t ON t.turn_id=f.turn_id JOIN conversation_sessions s ON s.session_id=t.session_id
WHERE to_tsvector('simple',COALESCE(f.text,'')) @@ websearch_to_tsquery('simple',$query)
AND s.npc_id=$npc AND t.status='active'" + excludeCurrent + @"
ORDER BY lexical_rank DESC,t.ts DESC LIMIT 8;"
                        : @"SELECT t.*,bm25(conversation_turn_fts) AS lexical_rank FROM conversation_turn_fts f
JOIN conversation_turns t ON t.turn_id=f.turn_id JOIN conversation_sessions s ON s.session_id=t.session_id
WHERE conversation_turn_fts MATCH $query AND s.npc_id=$npc AND t.status='active'" + excludeCurrent + @"
ORDER BY bm25(conversation_turn_fts),t.ts DESC LIMIT 8;";
                    matched = QuerySql(connection, lexicalSql,
                        searchParameters);
                    for (int index = 0; index < matched.Count; index++)
                    {
                        matched[index]["lexical_ordinal"] = index;
                    }
                }
                catch (Exception ex)
                {
                    LogOperational("memory.conversation_fts_failed", new Dictionary<string, object> { ["error"] = ex.Message, ["query"] = fts });
                }
            }

            HashSet<string> matchedIds = new HashSet<string>(matched.Select(row => ReadString(row, "turn_id", "")), StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> hit in SemanticHits(semanticSearch, "conversation_turn"))
            {
                Dictionary<string, object> payload = ReadDictionary(hit, "payload") ?? new Dictionary<string, object>();
                string turnId = ReadString(payload, "sourceId", "");
                if (string.IsNullOrWhiteSpace(turnId) || matchedIds.Contains(turnId)) continue;
                Dictionary<string, object> row = QuerySql(connection, @"SELECT t.* FROM conversation_turns t
JOIN conversation_sessions s ON s.session_id=t.session_id WHERE t.turn_id=$id AND s.npc_id=$npc AND t.status='active'" + excludeCurrent + " LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = turnId, ["npc"] = npcId, ["excluded_session"] = excludedSessionId ?? "" }).FirstOrDefault();
                if (row == null) continue;
                row["vectorSemanticScore"] = ReadDouble(hit, "score", 0d);
                row["lexical_ordinal"] = int.MaxValue;
                matched.Add(row);
                matchedIds.Add(turnId);
            }

            // A reconstruction request and the NPC's reconstruction answer are audit
            // evidence that recall was attempted, not new factual source material. Exclude
            // recall-only sessions before choosing an anchor so repeated probes cannot
            // outrank and hide the older player-authored fact they were asking about.
            Dictionary<string, bool> sourceBearingSessions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            matched = matched.Where(row => ConversationSessionContainsSourceBearingPlayerTurn(connection,
                ReadString(row, "session_id", ""), sourceBearingSessions)).ToList();

            double targetDay = ReadDouble(route, "targetWorldDay", -1d);
            if (matched.Count == 0 && targetDay >= 0d)
            {
                matched = QuerySql(connection, @"SELECT t.* FROM conversation_turns t
JOIN conversation_sessions s ON s.session_id=t.session_id WHERE s.npc_id=$npc AND t.status='active'" + excludeCurrent + @"
ORDER BY ABS(t.world_day-$day),t.ts DESC LIMIT 12;", new Dictionary<string, object> { ["npc"] = npcId, ["day"] = targetDay, ["excluded_session"] = excludedSessionId ?? "" })
                    .Where(row => ConversationSessionContainsSourceBearingPlayerTurn(connection,
                        ReadString(row, "session_id", ""), sourceBearingSessions)).Take(4).ToList();
            }
            if (matched.Count == 0)
            {
                matched = QuerySql(connection, @"SELECT t.* FROM conversation_turns t
JOIN conversation_sessions s ON s.session_id=t.session_id WHERE s.npc_id=$npc AND t.status='active'" + excludeCurrent + @"
ORDER BY t.ts DESC LIMIT 16;", new Dictionary<string, object> { ["npc"] = npcId, ["excluded_session"] = excludedSessionId ?? "" })
                    .Where(row => ConversationSessionContainsSourceBearingPlayerTurn(connection,
                        ReadString(row, "session_id", ""), sourceBearingSessions)).Take(2).ToList();
            }
            if (matched.Count == 0)
            {
                return new Dictionary<string, object> { ["text"] = "", ["matchedTurnIds"] = new List<string>(), ["expandedTurnIds"] = new List<string>() };
            }

            Dictionary<string, object> anchor = matched.OrderBy(row => targetDay >= 0d ? Math.Abs(ReadDouble(row, "world_day", 0d) - targetDay) : 0d)
                .ThenBy(row => ReadInt(row, "lexical_ordinal", int.MaxValue))
                .ThenByDescending(row => ReadDouble(row, "vectorSemanticScore", 0d))
                .ThenByDescending(row => ReadLong(row, "ts", 0)).First();
            string sessionId = ReadString(anchor, "session_id", "");
            int ordinal = ReadInt(anchor, "turn_order", 0);
            Dictionary<string, object> sourceSession = QuerySql(connection,
                "SELECT * FROM conversation_sessions WHERE session_id=$session LIMIT 1;",
                new Dictionary<string, object> { ["session"] = sessionId }).FirstOrDefault()
                ?? new Dictionary<string, object>();
            Dictionary<string, object> sourceSessionPayload =
                TryParseJsonObject(ReadString(sourceSession, "payload_json", "")) ?? new Dictionary<string, object>();
            List<string> sourceParticipants = TextListFromJson(ReadString(sourceSession, "participants_json", "[]"));
            string sourceLocation = FirstNonEmpty(
                ReadFirstString(sourceSessionPayload, "locationName", "settlementName", "currentSettlementName"),
                ReadString(sourceSession, "location_id", ReadFirstString(sourceSessionPayload, "locationId", "settlementId")),
                "unknown");
            int effectiveBudget = Math.Max(600, charBudget);
            string sourceMetadata = "Source session metadata: session=" + sessionId
                + "; participants=" + (sourceParticipants.Count == 0 ? "unknown" : string.Join(", ",
                    sourceParticipants.Take(8).Select(value => SanitizeUnknownIdentityEvidenceText(value, knowledge)).ToArray()))
                + "; location=" + sourceLocation
                + "; channel=" + ReadString(sourceSession, "channel", "unknown") + ".\n";
            sourceMetadata = SanitizeUnknownIdentityEvidenceText(sourceMetadata, knowledge);
            string sourceSemantics = effectiveBudget < 1200
                ? "Source semantics: we/us/here refer to this recorded session; listed participants heard this conversation but are not automatically witnesses to events described within it.\n"
                : "Historical deictic terms in this source (we/us/here/present/everyone/all three) refer only to the source conversation, never to the current group. Its participant list proves who heard this source conversation, not who witnessed an older event described inside it. Only an explicit source-event witness link or the current NPC's own attributed first-person line establishes firsthand participation; another NPC's collective wording cannot override this NPC's denial or missing witness evidence.\n";
            string sourceHeader = sourceMetadata + sourceSemantics;
            List<Dictionary<string, object>> expanded = QuerySql(connection, @"SELECT * FROM conversation_turns
WHERE session_id=$session AND status='active'
 AND turn_order BETWEEN $start AND $end ORDER BY turn_order;",
                new Dictionary<string, object> { ["session"] = sessionId, ["start"] = Math.Max(0, ordinal - 4), ["end"] = ordinal + 4 });
            StringBuilder builder = new StringBuilder();
            List<string> included = new List<string>();
            string anchorTurnId = ReadString(anchor, "turn_id", "");
            string anchorExchangeId = ReadString(anchor, "exchange_id", "");
            List<Dictionary<string, object>> prioritized = expanded
                .OrderBy(turn => ReadString(turn, "turn_id", "").Equals(anchorTurnId, StringComparison.OrdinalIgnoreCase) ? 0
                    : !string.IsNullOrWhiteSpace(anchorExchangeId)
                        && ReadString(turn, "exchange_id", "").Equals(anchorExchangeId, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(turn => Math.Abs(ReadInt(turn, "turn_order", 0) - ordinal))
                .ThenBy(turn => ReadInt(turn, "turn_order", 0))
                .ToList();
            bool hasExchangePartner = prioritized.Skip(1).Any(turn =>
                !string.IsNullOrWhiteSpace(anchorExchangeId)
                && ReadString(turn, "exchange_id", "").Equals(anchorExchangeId, StringComparison.OrdinalIgnoreCase));
            List<Dictionary<string, object>> selected = new List<Dictionary<string, object>>();
            int remaining = Math.Max(120, effectiveBudget - sourceHeader.Length);
            for (int index = 0; index < prioritized.Count && (remaining >= 120 || charBudget >= 24000); index++)
            {
                Dictionary<string, object> turn = prioritized[index];
                string line = RenderExactHistoryTurn(turn, knowledge);
                bool requiredWholeTurn = charBudget >= 24000 && (ReadString(turn, "turn_id", "") == anchorTurnId
                    || (anchorExchangeId.Length > 0 && ReadString(turn, "exchange_id", "") == anchorExchangeId));
                int partnerReserve = index == 0 && hasExchangePartner
                    ? (effectiveBudget < 1200 ? 120 : Math.Min(800, Math.Max(180, effectiveBudget / 3)))
                    : 0;
                int allowed = Math.Max(0, remaining - partnerReserve - 1);
                if (allowed < 120 && !requiredWholeTurn)
                {
                    continue;
                }
                string rendered = charBudget >= 24000 ? (requiredWholeTurn || line.Length <= allowed ? line : "") : ExactHistoryLineExcerpt(line, terms, allowed);
                if (string.IsNullOrWhiteSpace(rendered))
                {
                    continue;
                }
                selected.Add(new Dictionary<string, object>
                {
                    ["turnOrder"] = ReadInt(turn, "turn_order", 0),
                    ["turnId"] = ReadString(turn, "turn_id", ""),
                    ["text"] = rendered
                });
                remaining -= rendered.Length + 1;
            }
            builder.Append(sourceHeader);
            foreach (Dictionary<string, object> row in selected.OrderBy(row => ReadInt(row, "turnOrder", 0)))
            {
                builder.AppendLine(ReadString(row, "text", ""));
                included.Add(ReadString(row, "turnId", ""));
            }
            return new Dictionary<string, object>
            {
                ["text"] = charBudget >= 24000
                    ? RenderContinuityRecord(string.Join(",", included), "targeted_historical_exchange", npcId, sessionId,
                        ReadDouble(anchor, "world_day", 0d), 95, true, SanitizeUnknownIdentityEvidenceText(builder.ToString().TrimEnd(), knowledge))
                    : SanitizeUnknownIdentityEvidenceText(builder.ToString().TrimEnd(), knowledge),
                ["matchedTurnIds"] = matched.Select(row => ReadString(row, "turn_id", "")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ["expandedTurnIds"] = included,
                ["sessionId"] = sessionId,
                ["anchorTurnId"] = anchorTurnId
            };
        }

        private static string RenderExactHistoryTurn(Dictionary<string, object> turn,
            KnowledgeAccessContext knowledge)
        {
            turn = turn ?? new Dictionary<string, object>();
            string role = NormalizeMemoryCategory(ReadString(turn, "role", ""));
            string speaker = ReadString(turn, "speaker_name", ReadString(turn, "role", "Unknown"));
            string body = ReadString(turn, "text", "");
            if (knowledge != null && knowledge.PlayerIdentityUnknown)
            {
                bool playerAuthored = role.Equals("player", StringComparison.OrdinalIgnoreCase);
                if (playerAuthored)
                {
                    speaker = "Unidentified interlocutor";
                }
                else
                {
                    bool aimedAtCurrentStranger = Regex.IsMatch(body ?? "",
                        @"\b(you|your|yours|stranger|interlocutor|player)\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (aimedAtCurrentStranger && UnknownIdentityContainsPrivateStatus(body))
                    {
                        body = "[Earlier NPC wording withheld because it relied on canonical identity or private status evidence unavailable to this observer.]";
                    }
                }
                speaker = SanitizeUnknownIdentityEvidenceText(speaker, knowledge);
                body = SanitizeUnknownIdentityEvidenceText(body, knowledge);
            }

            return "- [Day " + ReadDouble(turn, "world_day", 0d)
                .ToString("0.##", CultureInfo.InvariantCulture) + "] "
                + speaker + ": " + body;
        }

        private static Dictionary<string, object> SanitizeUnknownIdentityExactHistory(
            Dictionary<string, object> exactHistory, KnowledgeAccessContext knowledge)
        {
            if (exactHistory == null || knowledge == null || !knowledge.PlayerIdentityUnknown)
            {
                return exactHistory ?? new Dictionary<string, object>
                {
                    ["text"] = "", ["matchedTurnIds"] = new List<string>(), ["expandedTurnIds"] = new List<string>()
                };
            }

            Dictionary<string, object> copy =
                new Dictionary<string, object>(exactHistory, StringComparer.OrdinalIgnoreCase);
            copy["text"] = SanitizeUnknownIdentityEvidenceText(ReadString(copy, "text", ""), knowledge);
            return copy;
        }

        private static bool LooksLikeMostRecentConversationRecall(string topic)
        {
            string text = (topic ?? "").ToLowerInvariant();
            bool reconstructionRequest = ContainsAnyTerm(text, "reconstruct", "reconstruction", "recap")
                && ContainsAnyTerm(text, "conversation", "conversations", "discussion", "discussions", "meeting", "meetings", "exchange", "exchanges", "speaker", "speakers", "scene", "scenes");
            bool explicitStoredRecall = ContainsAny(text,
                "from your own records", "from your records", "using your own records", "using your records",
                "according to your own records", "according to your records", "based on your own records", "based on your records",
                "from your own memory", "from your memory", "according to your own memory", "according to your memory");
            return reconstructionRequest || explicitStoredRecall || ContainsAny(text, "last conversation", "our last conversation", "previous conversation", "previous meeting",
                "last time we spoke", "last time we talked", "most recent conversation", "most recent meeting",
                "prior conversation", "prior discussion", "earlier conversation", "earlier discussion", "previous discussion",
                "earlier i asked you to remember", "i asked you to remember", "what did i ask you to remember",
                "preceding conversation", "preceding discussion", "preceding meeting", "recent conversation", "recent discussion",
                "recent group conversation", "previous group conversation", "prior group conversation", "preceding group conversation",
                "our prior", "what we discussed", "what did we discuss", "what did we talk about", "what we just discussed",
                "what we just talked about", "reconstruct our prior", "recall the preceding", "recalling the preceding",
                "recall our preceding", "recalling our preceding", "recover an earlier detail", "recover one older detail",
                "recover one much older detail", "immediately preceding answer", "immediately preceding reply",
                "your preceding answer", "your preceding reply", "your previous answer", "your previous reply",
                "your last answer", "your last reply", "what did you just say");
        }

        private static bool LooksLikeExactFactRestatement(string topic)
        {
            string text = (topic ?? "").ToLowerInvariant();
            bool restatement = ContainsAnyTerm(text, "paraphrase", "restate", "recite", "repeat", "summarize");
            bool historicalQuestion = ContainsAny(text,
                "what did i tell you", "what did i say", "what was its", "what were its",
                "you once heard me", "you previously heard me", "i told you earlier", "i shared earlier");
            bool preservation = ContainsAny(text,
                "without losing", "without omitting", "do not omit", "don't omit", "preserve the exact",
                "keep the exact", "exact name", "exact number", "exact color", "exact colour",
                "exact place", "exact location", "exact storage", "all exact details");
            return (restatement || historicalQuestion) && preservation;
        }

        private static bool IsPureConversationRecallRequest(string topic)
        {
            if (!LooksLikeMostRecentConversationRecall(topic) && !LooksLikeExactFactRestatement(topic)) return false;

            string text = Regex.Replace(topic ?? string.Empty, @"\s+", " ").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(text)) return false;

            // Exact-history routing and recall-write quarantine are different decisions. A turn
            // may ask for an old scene while also supplying new first-person information. Those
            // mixed turns still need exact history, but the new player-authored source must not be
            // discarded merely because the same message contains a recall request.
            if (ContainsAny(text,
                "my real name is", "my name is", "i am introducing myself", "i'm introducing myself",
                "a new detail for later", "a new low-salience detail for later", "a new fact for later",
                "new information for later", "for later is", "for later:",
                "remember this new", "hold this new", "note this new"))
            {
                return false;
            }

            string[] clauses = Regex.Split(text, @"(?<=[.!?;])\s+");
            foreach (string clause in clauses)
            {
                if (string.IsNullOrWhiteSpace(clause)) continue;
                bool recallClause = LooksLikeMostRecentConversationRecall(clause) || LooksLikeExactFactRestatement(clause)
                    || ContainsAny(clause, "recall", "reconstruct", "recap", "remember our", "remember the", "recover an earlier", "recover one older");
                if (recallClause) continue;

                if (Regex.IsMatch(clause,
                    @"\b(?:i|we)\s+(?:am|are|was|were|have|had|did|set|put|placed|gave|brought|bought|sold|found|saw|heard|learned|met|saved|captured|lost|won|made|built|hid|left|took|owe|promise|intend|will|carry|own|believe|claim)\b",
                    RegexOptions.CultureInvariant))
                {
                    return false;
                }
            }

            return true;
        }

        private static string RepairVisibleExactRecallIdentifiers(
            string playerText,
            string visibleReply,
            Dictionary<string, object> parsed,
            List<Dictionary<string, object>> messages,
            out List<Dictionary<string, object>> repairs)
        {
            repairs = new List<Dictionary<string, object>>();
            if (string.IsNullOrWhiteSpace(visibleReply)
                || parsed == null
                || !IsPureConversationRecallRequest(playerText)
                || !Regex.IsMatch(playerText ?? string.Empty, @"\bexact(?:ly)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return visibleReply ?? string.Empty;
            }

            string authoritativePrompt = string.Join("\n", (messages ?? new List<Dictionary<string, object>>())
                .Select(message => ReadString(message, "content", ""))
                .Where(content => !string.IsNullOrWhiteSpace(content)));
            if (string.IsNullOrWhiteSpace(authoritativePrompt)) return visibleReply;

            // Prefer compact exact identifiers that the model itself selected in its private
            // decision evidence or memory proposal. If the model also lost the identifier there,
            // a uniquely relevant identifier from authoritative prompt evidence may still repair
            // the visible answer. That fallback is deliberately narrow: the identifier's nearby
            // source text must overlap the recall question, and a competing identifier with the
            // same score makes the result ambiguous and therefore ineligible for repair.
            Dictionary<string, object> decisionBrief = ReadDictionary(parsed, "decisionBrief")
                ?? ReadDictionary(parsed, "decision_brief")
                ?? new Dictionary<string, object>();
            object memoryWriteValue = parsed.ContainsKey("memoryWrites")
                ? parsed["memoryWrites"]
                : (parsed.ContainsKey("memory_writes") ? parsed["memory_writes"] : null);
            string selectedEvidence = Json.Serialize(new Dictionary<string, object>
            {
                ["decisionBrief"] = decisionBrief,
                ["memoryWrites"] = memoryWriteValue ?? new List<object>()
            });
            Regex identifierPattern = new Regex(
                @"\b(?<base>[\p{L}][\p{L}]{3,})(?<suffix>(?:[-_][\p{L}\p{N}]*\d[\p{L}\p{N}]*)+)\b",
                RegexOptions.CultureInvariant);
            foreach (Match match in identifierPattern.Matches(selectedEvidence)
                .Cast<Match>()
                .Where(candidate => candidate.Success)
                .GroupBy(candidate => candidate.Value, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()))
            {
                string candidate = match.Value;
                string candidateBase = match.Groups["base"].Value;
                if (string.IsNullOrWhiteSpace(candidateBase)
                    || (playerText ?? string.Empty).IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0
                    || visibleReply.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0
                    || authoritativePrompt.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                Regex visibleBase = new Regex(
                    @"\b" + Regex.Escape(candidateBase) + @"\b(?![-_])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                Match visibleMatch = visibleBase.Match(visibleReply);
                if (!visibleMatch.Success) continue;

                string repairedReply = visibleBase.Replace(visibleReply, _ => candidate, 1);
                repairs.Add(new Dictionary<string, object>
                {
                    ["kind"] = "exact_recall_identifier_completion",
                    ["visibleToken"] = visibleMatch.Value,
                    ["authoritativeIdentifier"] = candidate,
                    ["evidence"] = "model_selected_and_prompt_grounded"
                });
                return repairedReply;
            }

            HashSet<string> questionTerms = new HashSet<string>(
                Regex.Matches(playerText ?? string.Empty, @"\b[\p{L}]{4,}\b",
                        RegexOptions.CultureInvariant)
                    .Cast<Match>()
                    .Select(match => match.Value.ToLowerInvariant())
                    .Where(term => !new[]
                    {
                        "what", "which", "where", "when", "whose", "earlier",
                        "asked", "remember", "exact", "exactly", "name", "called",
                        "said", "tell", "please"
                    }.Contains(term, StringComparer.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> groundedCandidates =
                identifierPattern.Matches(authoritativePrompt)
                    .Cast<Match>()
                    .Where(match => match.Success
                        && visibleReply.IndexOf(
                            match.Value, StringComparison.OrdinalIgnoreCase) < 0)
                    .GroupBy(match => match.Value, StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                    {
                        Match match = group.First();
                        int start = Math.Max(0, match.Index - 240);
                        int length = Math.Min(
                            authoritativePrompt.Length - start,
                            match.Length + 480);
                        string window = authoritativePrompt.Substring(start, length);
                        int score = questionTerms.Count(term =>
                            Regex.IsMatch(
                                window,
                                @"\b" + Regex.Escape(term) + @"\b",
                                RegexOptions.IgnoreCase
                                    | RegexOptions.CultureInvariant));
                        return new Dictionary<string, object>
                        {
                            ["identifier"] = match.Value,
                            ["base"] = match.Groups["base"].Value,
                            ["score"] = score
                        };
                    })
                    .OrderByDescending(candidate => ReadInt(candidate, "score", 0))
                    .ThenBy(candidate => ReadString(candidate, "identifier", ""),
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();
            Dictionary<string, object> best = groundedCandidates.FirstOrDefault();
            int bestScore = ReadInt(best, "score", 0);
            bool uniquelyGrounded = best != null
                && bestScore >= 2
                && !groundedCandidates.Skip(1).Any(candidate =>
                    ReadInt(candidate, "score", 0) == bestScore);
            if (uniquelyGrounded)
            {
                string candidate = ReadString(best, "identifier", "");
                string candidateBase = ReadString(best, "base", "");
                Regex visibleBase = new Regex(
                    @"\b" + Regex.Escape(candidateBase) + @"\b(?![-_])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                Match visibleMatch = visibleBase.Match(visibleReply);
                string repairedReply = visibleMatch.Success
                    ? visibleBase.Replace(visibleReply, _ => candidate, 1)
                    : visibleReply.TrimEnd() + " Its exact name was " + candidate + ".";
                repairs.Add(new Dictionary<string, object>
                {
                    ["kind"] = "exact_recall_identifier_completion",
                    ["visibleToken"] = visibleMatch.Success ? visibleMatch.Value : "",
                    ["authoritativeIdentifier"] = candidate,
                    ["evidence"] = "unique_prompt_grounded_recall_match",
                    ["questionOverlapScore"] = bestScore
                });
                return repairedReply;
            }

            return visibleReply;
        }

        private static bool ConversationSessionContainsSourceBearingPlayerTurn(ReignDbConnection connection, string sessionId,
            Dictionary<string, bool> cache)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return false;
            cache = cache ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (cache.TryGetValue(sessionId, out bool cached)) return cached;
            List<Dictionary<string, object>> playerTurns = QuerySql(connection, @"SELECT text FROM conversation_turns
WHERE session_id=$session AND status='active' AND role='player' ORDER BY turn_order;",
                new Dictionary<string, object> { ["session"] = sessionId });
            bool sourceBearing = playerTurns.Any(turn => !IsPureConversationRecallRequest(ReadString(turn, "text", "")));
            cache[sessionId] = sourceBearing;
            return sourceBearing;
        }

        private static Dictionary<string, object> LoadMostRecentClosedConversation(ReignDbConnection connection, string npcId,
            string excludedSessionId, int charBudget, int turnLimit, KnowledgeAccessContext knowledge = null)
        {
            string exclusion = string.IsNullOrWhiteSpace(excludedSessionId) ? "" : " AND session_id<>$excluded_session";
            List<Dictionary<string, object>> sessions = QuerySql(connection, @"SELECT * FROM conversation_sessions
WHERE status='closed' AND (npc_id=$npc OR participants_json LIKE $participant)" + exclusion + @"
ORDER BY end_ts DESC,start_ts DESC LIMIT 30;",
                new Dictionary<string, object>
                {
                    ["npc"] = npcId, ["participant"] = "%\"" + (npcId ?? "") + "\"%", ["excluded_session"] = excludedSessionId ?? ""
                });
            List<Dictionary<string, object>> turns = new List<Dictionary<string, object>>();
            Dictionary<string, Dictionary<string, object>> includedSessions =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            int targetTurns = Math.Max(2, Math.Min(100, turnLimit));
            foreach (Dictionary<string, object> session in sessions)
            {
                string candidateSessionId = ReadString(session, "session_id", "");
                List<Dictionary<string, object>> sessionTurns = QuerySql(connection, @"SELECT * FROM conversation_turns
WHERE session_id=$session AND status='active'
ORDER BY turn_order DESC;", new Dictionary<string, object> { ["session"] = candidateSessionId });
                // Recall probes are evidence that the probe occurred, but must not displace the
                // source conversations they were trying to reconstruct. Keep every complete
                // source-bearing session involving this NPC until the configured raw window is met.
                bool sourceBearing = sessionTurns.Any(turn =>
                    ReadString(turn, "role", "").Equals("player", StringComparison.OrdinalIgnoreCase)
                    && !IsPureConversationRecallRequest(ReadString(turn, "text", "")));
                if (!sourceBearing) continue;
                includedSessions[candidateSessionId] = session;
                foreach (Dictionary<string, object> turn in sessionTurns)
                {
                    turn["session_end_ts"] = ReadLong(session, "end_ts", 0);
                    turn["session_start_ts"] = ReadLong(session, "start_ts", 0);
                    turns.Add(turn);
                }
                if (turns.Count >= targetTurns) break;
            }
            if (turns.Count == 0)
            {
                return new Dictionary<string, object> { ["text"] = "", ["matchedTurnIds"] = new List<string>(), ["expandedTurnIds"] = new List<string>() };
            }

            string sessionId = ReadString(turns[0], "session_id", "");
            int budget = Math.Max(1200, charBudget);
            List<Dictionary<string, object>> selected = new List<Dictionary<string, object>>();
            int used = 0;
            for (int index = 0; index < turns.Count; index++)
            {
                Dictionary<string, object> turn = turns[index];
                string line = RenderExactHistoryTurn(turn, knowledge);
                int remaining = budget - used;
                if (remaining < 180) break;
                string rendered = line.Length <= remaining ? line : "";
                if (string.IsNullOrWhiteSpace(rendered)) continue;
                selected.Add(new Dictionary<string, object>
                {
                    ["turnOrder"] = ReadInt(turn, "turn_order", 0), ["turnId"] = ReadString(turn, "turn_id", ""), ["text"] = rendered,
                    ["sessionId"] = ReadString(turn, "session_id", ""), ["sessionEndTs"] = ReadLong(turn, "session_end_ts", 0)
                });
                used += rendered.Length + 1;
            }
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Recent source-bearing closed conversations - authoritative verbatim continuity. For reconstructing facts, prioritize player-authored lines and later player corrections. NPC lines are authoritative evidence of what that NPC said, not proof that an NPC reconstruction was factually correct. Later player corrections supersede earlier player statements:");
            builder.AppendLine("Historical deictic terms in each source (we/us/here/present/everyone/all three) refer only to that source conversation, never to the current group. A source session's participant list proves only who heard or spoke in that conversation, not who witnessed an older event described inside it. Only an explicit source-event witness link or the current NPC's own attributed first-person line establishes firsthand participation; another NPC's collective wording cannot override this NPC's denial or missing witness evidence.");
            string renderedSession = "";
            foreach (Dictionary<string, object> row in selected
                .OrderBy(item => ReadLong(item, "sessionEndTs", 0)).ThenBy(item => ReadInt(item, "turnOrder", 0)))
            {
                string rowSession = ReadString(row, "sessionId", "");
                if (!rowSession.Equals(renderedSession, StringComparison.OrdinalIgnoreCase))
                {
                    renderedSession = rowSession;
                    includedSessions.TryGetValue(rowSession, out Dictionary<string, object> sourceSession);
                    sourceSession = sourceSession ?? new Dictionary<string, object>();
                    Dictionary<string, object> sourceSessionPayload =
                        TryParseJsonObject(ReadString(sourceSession, "payload_json", "")) ?? new Dictionary<string, object>();
                    string locationName = ReadFirstString(sourceSessionPayload, "locationName", "settlementName", "currentSettlementName");
                    string locationId = ReadString(sourceSession, "location_id", ReadFirstString(sourceSessionPayload, "locationId", "settlementId"));
                    List<string> sourceParticipants = TextListFromJson(ReadString(sourceSession, "participants_json", "[]"));
                    string sourceScene = ReadString(sourceSessionPayload, "sceneContext", "");
                    builder.AppendLine("[HISTORICAL SOURCE SESSION " + rowSession + "]");
                    builder.AppendLine("Session setting metadata: location=" + FirstNonEmpty(locationName, locationId, "unknown")
                        + "; channel=" + ReadString(sourceSession, "channel", "")
                        + "; participants=" + (sourceParticipants.Count == 0 ? "unknown" : string.Join(", ",
                            sourceParticipants.Take(8).Select(value => SanitizeUnknownIdentityEvidenceText(value, knowledge)).ToArray()))
                        + "; world day=" + ReadDouble(sourceSession, "start_world_day", 0d).ToString("0.##", CultureInfo.InvariantCulture) + ".");
                    if (!string.IsNullOrWhiteSpace(sourceScene)) builder.AppendLine("Recorded scene context: "
                        + LimitText(SanitizeUnknownIdentityEvidenceText(sourceScene, knowledge), 600));
                    builder.AppendLine("Distinguish the conversation setting above from places mentioned inside anecdotes.");
                }
                builder.AppendLine(ReadString(row, "text", ""));
            }
            List<string> ids = selected.Select(row => ReadString(row, "turnId", "")).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
            return new Dictionary<string, object>
            {
                ["text"] = SanitizeUnknownIdentityEvidenceText(builder.ToString().TrimEnd(), knowledge),
                ["matchedTurnIds"] = ids, ["expandedTurnIds"] = ids,
                ["sessionId"] = sessionId, ["selectionMode"] = "recent_closed_turn_window",
                ["rawTurnLimit"] = Math.Max(2, Math.Min(100, turnLimit)), ["rawTurnCount"] = ids.Count
            };
        }

        private static string ExactHistoryLineExcerpt(string line, List<string> queryTerms, int charBudget)
        {
            string value = line ?? "";
            int budget = Math.Max(0, charBudget);
            if (budget == 0 || value.Length == 0)
            {
                return "";
            }
            if (value.Length <= budget)
            {
                return value;
            }
            if (budget < 80)
            {
                return LimitText(value, budget);
            }

            int matchIndex = -1;
            int matchLength = 0;
            foreach (string term in (queryTerms ?? new List<string>())
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .OrderByDescending(term => term.Length))
            {
                int index = value.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    continue;
                }
                matchIndex = index;
                matchLength = term.Length;
                break;
            }
            if (matchIndex < 0)
            {
                return LimitText(value, budget);
            }

            int prefixEnd = value.IndexOf(": ", StringComparison.Ordinal);
            string speakerPrefix = prefixEnd >= 0 && prefixEnd + 2 < Math.Min(value.Length, 120)
                ? value.Substring(0, prefixEnd + 2)
                : "";
            int bodyStart = speakerPrefix.Length;
            int bodyBudget = Math.Max(40, budget - speakerPrefix.Length - 6);
            int relativeMatch = Math.Max(0, matchIndex - bodyStart);
            int start = Math.Max(0, relativeMatch - bodyBudget / 3);
            int bodyLength = Math.Min(bodyBudget, value.Length - bodyStart - start);
            if (relativeMatch + matchLength > start + bodyLength)
            {
                start = Math.Max(0, relativeMatch + matchLength - bodyLength);
            }
            string body = value.Substring(bodyStart + start, bodyLength);
            string rendered = speakerPrefix
                + (start > 0 ? "... " : "")
                + body
                + (bodyStart + start + bodyLength < value.Length ? " ..." : "");
            return rendered.Length <= budget ? rendered : rendered.Substring(0, budget);
        }

        private static readonly string[] ExactRecallStopWords =
        {
            "remember", "exactly", "exact", "words", "word", "said", "say", "told", "tell", "me", "you", "what", "when",
            "days", "day", "ago", "weeks", "week", "previous", "meeting", "conversation", "last", "time", "quote"
        };

        private static double ExtractTargetWorldDay(string text, double currentDay)
        {
            Match match = Regex.Match(text ?? "", @"\b(\d+(?:\.\d+)?|one|two|three|four|five|six|seven|eight|nine|ten)\s+(day|days|week|weeks)\s+ago\b", RegexOptions.IgnoreCase);
            if (!match.Success || currentDay <= 0d) return -1d;
            double amount;
            if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out amount))
            {
                string[] words = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" };
                int index = Array.FindIndex(words, word => word.Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
                if (index < 0) return -1d;
                amount = index;
            }
            if (match.Groups[2].Value.StartsWith("week", StringComparison.OrdinalIgnoreCase)) amount *= 7d;
            return Math.Max(0d, currentDay - amount);
        }
    }
}
