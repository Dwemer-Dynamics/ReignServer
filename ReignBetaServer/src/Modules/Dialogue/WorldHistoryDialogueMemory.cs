using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static bool IsPublicTournamentHistory(string eventType, string dissemination)
        {
            return (string.Equals(eventType, "tournament_started", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(eventType, "tournament_finished", StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(dissemination)
                    || string.Equals(dissemination, "ordinary", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(dissemination, "major_world", StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, object> PublicTournamentKnowledgeRule(double day)
        {
            return new Dictionary<string, object>
            {
                ["audienceType"] = "global", ["audienceId"] = "all",
                ["availableDay"] = day, ["acquisitionMode"] = "public_tournament_news",
                ["confidence"] = 1d
            };
        }

        private static void EnsureImmediateTournamentKnowledge(ReignDbConnection connection)
        {
            const string marker = "world_history_immediate_tournament_news_v1";
            if (ReadString(QuerySql(connection, "SELECT value FROM schema_meta WHERE key=$key;",
                new Dictionary<string, object> { ["key"] = marker }).FirstOrDefault(), "value", "") == "1") return;

            // Add derived access rules to the existing events. Never rewrite an
            // outcome, duplicate an event, or publish a participant-only event.
            ExecuteSql(connection, @"INSERT INTO world_history_knowledge_rules
(rule_id,event_id,audience_type,audience_id,available_day,acquisition_mode,confidence)
SELECT event_id || ':public_tournament_news',event_id,'global','all',world_day,'public_tournament_news',1
FROM world_history_events WHERE event_type IN ('tournament_started','tournament_finished')
AND dissemination_class IN ('ordinary','major_world')
ON CONFLICT(rule_id) DO UPDATE SET available_day=excluded.available_day;");
            ExecuteSql(connection, @"UPDATE world_history_knowledge_rules SET available_day=
(SELECT e.world_day FROM world_history_events e WHERE e.event_id=world_history_knowledge_rules.event_id)
WHERE acquisition_mode IN ('realm_news','major_world_news') AND event_id IN
(SELECT event_id FROM world_history_events WHERE event_type IN ('tournament_started','tournament_finished')
AND dissemination_class IN ('ordinary','major_world'));");
            ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES($key,'1');",
                new Dictionary<string, object> { ["key"] = marker });
        }

        private static List<Dictionary<string, object>> LoadKnownWorldHistoryForDialogue(
            ReignDbConnection connection, string timelineId, double worldDay,
            KnowledgeAccessContext knowledge, string topic, Dictionary<string, object> route,
            Dictionary<string, object> semanticSearch, bool verifiedInterlocutor)
        {
            var result = new List<Dictionary<string, object>>();
            if (knowledge == null || !knowledge.HasNpc || worldDay <= 0d
                || double.IsNaN(worldDay) || double.IsInfinity(worldDay)) return result;
            bool worldQuestion = RouteHasLane(route, "world_affairs") || RouteHasLane(route, "local_awareness");
            bool recognize = verifiedInterlocutor && !knowledge.PlayerIdentityUnknown
                && !string.IsNullOrWhiteSpace(knowledge.PlayerId);
            if (!worldQuestion && !recognize) return result;
            EnsureWorldHistorySchema(connection);
            timelineId = FirstNonEmpty(timelineId, ActiveWorldHistoryTimeline(connection));

            var parameters = new Dictionary<string, object>
            {
                ["timeline"] = timelineId, ["day"] = worldDay,
                ["npc"] = knowledge.NpcId, ["kingdom"] = knowledge.KingdomId,
                ["player"] = knowledge.PlayerId, ["location"] = knowledge.LocationId
            };
            // Filter knowledge, time and timeline BEFORE applying a candidate
            // limit. Routine traffic must not bury an older matching victory.
            string visible = @"e.timeline_id=$timeline AND e.world_day<=$day AND e.is_complete=1
AND EXISTS(SELECT 1 FROM world_history_knowledge_rules k WHERE k.event_id=e.event_id
AND k.available_day<=$day AND ((k.audience_type='entity' AND k.audience_id=$npc)
OR (k.audience_type='kingdom' AND k.audience_id=$kingdom) OR k.audience_type='global'))";
            string eligible = "(" + WorldHistoryEmbeddingEligibilitySql("e.")
                + " OR e.event_type='tournament_started')";
            var candidates = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            var recognitionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<IEnumerable<Dictionary<string, object>>> add = rows =>
            {
                foreach (var row in rows)
                {
                    string id = ReadString(row, "event_id", "");
                    if (!string.IsNullOrWhiteSpace(id)) candidates[id] = row;
                }
            };

            if (recognize)
            {
                var achievements = QuerySql(connection, @"SELECT e.* FROM world_history_events e WHERE " + visible + @"
AND e.event_type='tournament_finished'
AND EXISTS(SELECT 1 FROM world_history_entities n WHERE n.event_id=e.event_id
AND n.entity_id=$player AND n.role LIKE '%winner%')
ORDER BY CASE WHEN e.location_id=$location THEN 0 ELSE 1 END,e.sequence DESC LIMIT 3;", parameters);
                add(achievements);
                foreach (var row in achievements) recognitionIds.Add(ReadString(row, "event_id", ""));
            }

            List<string> terms = MemoryQueryTerms(topic);
            bool tournamentQuestion = ContainsAny((topic ?? "").ToLowerInvariant(), "tournament", "champion", "arena");
            if (worldQuestion)
            {
                if (tournamentQuestion)
                    add(QuerySql(connection, "SELECT e.* FROM world_history_events e WHERE " + visible
                        + @" AND e.event_type IN ('tournament_started','tournament_finished')
ORDER BY CASE WHEN e.location_id=$location THEN 0 ELSE 1 END,e.sequence DESC LIMIT 24;", parameters));

                string ftsQuery = BuildFtsQuery(terms);
                if (!string.IsNullOrWhiteSpace(ftsQuery))
                {
                    parameters["query"] = ftsQuery;
                    add(QuerySql(connection, @"SELECT e.* FROM world_history_fts
JOIN world_history_events e ON e.event_id=world_history_fts.event_id
WHERE world_history_fts MATCH $query AND " + visible + " AND " + eligible
                        + " ORDER BY e.sequence DESC LIMIT 48;", parameters));
                }

                // Reuse the turn's existing vector search; no extra embedding or
                // language-model call. A vector hit is a candidate, never truth.
                var hits = SemanticHits(semanticSearch, "world_history_event")
                    .Where(hit => ReadDouble(hit, "score", 0d) >= 0.3d).Take(48).ToList();
                var ids = new List<string>();
                var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var hit in hits)
                {
                    string id = ReadString(ReadDictionary(hit, "payload"), "sourceId", "");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (scores.ContainsKey(id)) { scores[id] = Math.Max(scores[id], ReadDouble(hit, "score", 0d)); continue; }
                    string key = "hit" + ids.Count.ToString(CultureInfo.InvariantCulture);
                    ids.Add("$" + key);
                    parameters[key] = id;
                    scores[id] = ReadDouble(hit, "score", 0d);
                }
                if (ids.Count > 0)
                {
                    add(QuerySql(connection, "SELECT e.* FROM world_history_events e WHERE " + visible
                        + " AND " + eligible + " AND e.event_id IN (" + string.Join(",", ids) + ");", parameters));
                    foreach (var pair in scores)
                        if (candidates.TryGetValue(pair.Key, out var row)) row["vectorSemanticScore"] = pair.Value;
                }
            }

            foreach (var row in candidates.Values)
            {
                double lexical = HistoryTermScore(row, terms);
                double score = lexical + Math.Max(0d, ReadDouble(row, "vectorSemanticScore", 0d)) * 30d;
                if (ReadString(row, "location_id", "").Equals(knowledge.LocationId, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(knowledge.LocationId)) score += 16d;
                if (tournamentQuestion && ReadString(row, "event_type", "").StartsWith("tournament_", StringComparison.OrdinalIgnoreCase)) score += 20d;
                if (recognitionIds.Contains(ReadString(row, "event_id", ""))) score += 32d;
                row["dialogueHistoryScore"] = score;
            }
            // At most 24 candidates reach the authoritative per-event role and
            // identity checks. They retain their original event IDs/provenance.
            foreach (var row in candidates.Values.OrderByDescending(r => ReadDouble(r, "dialogueHistoryScore", 0d))
                .ThenByDescending(r => ReadLong(r, "sequence", 0)).Take(24))
            {
                string eventId = ReadString(row, "event_id", "");
                var entities = QueryWorldHistoryEntities(connection, eventId);
                bool aboutPlayer = entities.Any(e => ReadString(e, "entity_id", "").Equals(knowledge.PlayerId, StringComparison.OrdinalIgnoreCase));
                if (knowledge.PlayerIdentityUnknown && (aboutPlayer || entities.Any(e =>
                    ReadString(e, "entity_id", "") == "main_hero" || ReadString(e, "entity_id", "") == "player_hero"))) continue;
                if (!UnknownIdentityMemoryEvidenceAllowed(row, knowledge, ReadString(row, "summary", ""))) continue;
                var access = DetermineWorldHistoryKnowledge(connection, new List<string> { eventId }, knowledge.NpcId, knowledge.KingdomId, worldDay);
                if (ReadString(access, "basis", "none") == "none") continue;
                row["knowledgeBasis"] = ReadString(access, "basis", "none");
                row["acquisitionModes"] = ReadStringList(access, "acquisitionModes");
                row["verifiedInterlocutorAchievement"] = recognize && aboutPlayer
                    && ReadString(row, "event_type", "") == "tournament_finished"
                    && entities.Any(e => ReadString(e, "entity_id", "") == knowledge.PlayerId
                        && RoleContains(ReadString(e, "role", ""), "winner"));
                if (ReadBool(row, "verifiedInterlocutorAchievement", false) && !recognitionIds.Contains(eventId))
                    row["dialogueHistoryScore"] = ReadDouble(row, "dialogueHistoryScore", 0d) + 32d;
                result.Add(row);
            }
            return result.OrderByDescending(r => ReadDouble(r, "dialogueHistoryScore", 0d))
                .ThenByDescending(r => ReadLong(r, "sequence", 0)).Take(6).ToList();
        }

        private static string FormatKnownWorldHistoryForDialogue(List<Dictionary<string, object>> events, int maxChars)
        {
            if (events == null || events.Count == 0) return "";
            var text = new StringBuilder("KNOWN NATIVE WORLD HISTORY — AUTHORITATIVE\n");
            text.AppendLine("These recorded game events are known to this NPC. Public news establishes the result, not personal attendance. When relevant to a verified introduction, connect the supplied achievement to that person; do not treat it as an unsupported boast. Do not invent additional feats or witnesses.");
            foreach (var row in events)
            {
                string basis = ReadString(row, "knowledgeBasis", "secondhand") == "firsthand"
                    ? "firsthand participant" : ReadStringList(row, "acquisitionModes").Contains("public_tournament_news")
                        ? "public tournament news; secondhand" : "known report; secondhand";
                string line = "- [" + basis + "; day " + ReadDouble(row, "world_day", 0d).ToString("0.###", CultureInfo.InvariantCulture)
                    + "; event " + ReadString(row, "event_id", "") + "] " + LimitText(ReadString(row, "summary", ""), 450);
                if (text.Length + line.Length + 2 > maxChars) break;
                text.AppendLine(line);
            }
            return text.ToString().TrimEnd();
        }
    }
}
