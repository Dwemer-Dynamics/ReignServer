using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        // These legacy producer phases synchronously publish a crier article.
        // major_world alone never establishes publicity for another producer.
        private const string PublicWorldHistorySql = @"((e.event_type='public_announcement' AND e.source='reign_public_news'
 AND e.dissemination_class='major_world' AND e.payload_json LIKE '%reign-public-news-v1%')
 OR (e.event_type='ruler_judgment' AND e.source='reign_system' AND e.dissemination_class='major_world'
 AND e.phase IN ('sentenced','completed','escaped','cancelled') AND e.payload_json LIKE '%""judgmentId""%')
 OR (e.event_type IN ('tournament_started','tournament_finished') AND e.dissemination_class IN ('ordinary','major_world'))
 OR (e.event_type IN ('war_declared','peace_made') AND e.source='bannerlord_native' AND e.dissemination_class='major_world'))";

        private static void EnsurePublicWorldNewsKnowledge(ReignDbConnection connection)
        {
            const string marker = "public_world_news_legacy_rules_v2";
            if (ReadString(Program.QuerySql(connection, "SELECT value FROM schema_meta WHERE key=$key;", TestDict("key", marker)).FirstOrDefault(), "value", "") == "1") return;
            // Derived access only, never rewriting a source, deadline or article.
            foreach (var row in Program.QuerySql(connection, "SELECT e.* FROM world_history_events e WHERE e.is_complete=1 AND " + PublicWorldHistorySql)
                .Where(ValidPublicWorldHistory))
                Program.ExecuteSql(connection, @"INSERT INTO world_history_knowledge_rules
(rule_id,event_id,audience_type,audience_id,available_day,acquisition_mode,confidence)
VALUES($rule,$event,'global','all',$day,'public_world_news',1) ON CONFLICT(rule_id) DO NOTHING;",
                    TestDict("rule", ReadString(row, "event_id", "") + ":public_world_news", "event", ReadString(row, "event_id", ""), "day", ReadDouble(row, "world_day", 0)));
            Program.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES($key,'1');", TestDict("key", marker));
        }

        internal static bool ValidPublicWorldHistory(Dictionary<string, object> row)
        {
            string type = ReadString(row, "event_type", "");
            if (Reign.Core.Contracts.World.ReignPublicNews.IsNativePublicDiplomacy(type, ReadString(row, "source", ""), ReadString(row, "dissemination_class", ""))) return true;
            if (IsPublicTournamentHistory(type, ReadString(row, "dissemination_class", ""))) return true;
            var data = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
            if (type == "ruler_judgment") return ReadString(data, "judgmentId", "").Length > 0
                && (ReadString(data, "verdict", "").Length > 0 || (ReadString(row, "phase", "") == "escaped" || ReadString(row, "phase", "") == "cancelled")
                    && ReadString(data, "CancellationReason", "").Length > 0);
            double published = ReadDouble(data, "publicationDay", -1);
            return ReadString(data, "schema", "") == Reign.Core.Contracts.World.ReignPublicNews.Schema
                && Reign.Core.Contracts.World.ReignNewsCategories.IsCategory(ReadString(data, "category", ""))
                && ReadString(data, "articleId", "").Length > 0 && ReadString(data, "title", "").Length > 0
                && ReadString(data, "body", "").Length > 0 && double.IsFinite(published) && published >= 0
                && Math.Abs(published - ReadDouble(row, "world_day", -2)) < 1e-8;
        }

        private static IEnumerable<Dictionary<string, object>> DistinctPublicWorldHistory(IEnumerable<Dictionary<string, object>> records)
        {
            var valid = records.Where(ValidPublicWorldHistory).ToList();
            var articles = valid.Where(r => ReadString(r, "source", "") == "reign_public_news")
                .Select(r => ReadString(r, "correlation_id", "")).ToHashSet(StringComparer.Ordinal);
            return valid.Where(r => ReadString(r, "event_type", "") != "ruler_judgment"
                || !articles.Contains("crier:judgment:" + ReadString(r, "correlation_id", "") + ":" + ReadString(r, "phase", "")));
        }

        private static List<Dictionary<string, object>> LoadPublicWorldNews(ReignDbConnection connection,
            string timeline, double day, Program.KnowledgeAccessContext observer, string topic)
        {
            var args = TestDict("timeline", timeline, "day", day, "topic", topic ?? "");
            string visible = PublicWorldHistoryTimelineScope(connection, timeline, observer.AsOfWorldHistorySequence, args)
                + " AND e.world_day<=$day AND e.is_complete=1 AND " + PublicWorldHistorySql;
            var rows = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            void Add(IEnumerable<Dictionary<string, object>> values, bool direct)
            {
                foreach (var value in values)
                {
                    if (!ValidPublicWorldHistory(value)) continue;
                    string id = ReadString(value, "event_id", "");
                    if (rows.TryGetValue(id, out var prior)) { if (direct) prior["publicDirectRecall"] = true; continue; }
                    value["publicWorldNews"] = true; value["publicDirectRecall"] = direct;
                    rows[id] = value;
                }
            }
            // Public announcements have their own ambient budget; tournament
            // traffic cannot displace the latest sentence or other public news.
            Add(DistinctPublicWorldHistory(Program.QuerySql(connection, "SELECT e.* FROM world_history_events e WHERE " + visible
                + " ORDER BY CASE WHEN e.event_type LIKE 'tournament_%' THEN 1 ELSE 0 END,e.world_day DESC,e.sequence DESC LIMIT 24;", args)).Take(6), false);
            // Resolve named subjects against the full eligible public archive,
            // before any ambient limit. Word boundaries prevent Ira/Tirana aliasing.
            var subjects = Program.QuerySql(connection, @"SELECT DISTINCT n.entity_id,n.name_snapshot FROM world_history_entities n
JOIN world_history_events e ON e.event_id=n.event_id WHERE " + visible + @"
AND LENGTH(n.name_snapshot)>=3 AND LOWER($topic) LIKE '%' || LOWER(n.name_snapshot) || '%';", args)
                .Where(n => Regex.IsMatch(topic ?? "", @"(?<![\p{L}\p{Nd}])" + Regex.Escape(ReadString(n, "name_snapshot", ""))
                    + @"(?![\p{L}\p{Nd}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .Select(n => ReadString(n, "entity_id", "")).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (subjects.Count > 0)
            {
                args["subjects"] = Json.Serialize(subjects);
                Add(Program.QuerySql(connection, "SELECT e.* FROM world_history_events e WHERE " + visible + @"
AND EXISTS(SELECT 1 FROM world_history_entities n JOIN jsonb_array_elements_text(CAST($subjects AS jsonb)) s
ON s.value=n.entity_id WHERE n.event_id=e.event_id) ORDER BY e.world_day DESC,e.sequence DESC;", args), true);
            }
            // Topic recall also searches every public category without depending
            // on a world-affairs routing decision or vector availability.
            string fts = Program.BuildFtsQuery(Program.MemoryQueryTerms(topic));
            if (fts.Length > 0)
            {
                args["query"] = fts;
                Add(Program.QuerySql(connection, @"SELECT e.* FROM world_history_fts JOIN world_history_events e ON e.event_id=world_history_fts.event_id
WHERE world_history_fts MATCH $query AND " + visible + " ORDER BY e.world_day DESC,e.sequence DESC LIMIT 24;", args), true);
            }
            return DistinctPublicWorldHistory(rows.Values).Where(row => !observer.PlayerIdentityUnknown || !WorldSimulationService.QueryWorldHistoryEntities(connection,
                    ReadString(row, "event_id", "")).Any(n => ReadString(n, "entity_id", "") == observer.PlayerId
                        || ReadString(n, "entity_id", "") == "main_hero" || ReadString(n, "entity_id", "") == "player_hero"))
                .Select(row => {
                    row["knowledgeBasis"] = "secondhand";
                    row["acquisitionModes"] = new List<string> { "public_world_news" };
                    if (ReadString(row, "event_type", "").StartsWith("tournament_", StringComparison.Ordinal))
                    {
                        var participants = WorldSimulationService.QueryWorldHistoryEntities(connection, ReadString(row, "event_id", ""));
                        row["tournamentFirsthand"] = participants.Any(n => ReadString(n, "entity_id", "") == observer.NpcId
                            && WorldSimulationService.RoleContains(ReadString(n, "role", ""), "participant", "winner"));
                        row["tournamentWinningTeam"] = participants.Where(n => WorldSimulationService.RoleContains(ReadString(n, "role", ""), "winner"))
                            .Select(n => ReadString(n, "name_snapshot", "")).Where(n => n.Length > 0).Distinct().ToList();
                    }
                    if (ReadString(row, "event_type", "") == "ruler_judgment")
                    {
                        var people = WorldSimulationService.QueryWorldHistoryEntities(connection, ReadString(row, "event_id", ""));
                        var data = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                        string actor = ReadString(people.FirstOrDefault(n => WorldSimulationService.RoleContains(ReadString(n, "role", ""), "actor")), "name_snapshot", "The ruling authority");
                        string target = ReadString(people.FirstOrDefault(n => WorldSimulationService.RoleContains(ReadString(n, "role", ""), "participant")), "name_snapshot", "the captive leader");
                        row["summary"] = actor + " publicly announced the ruling concerning " + target + ": "
                            + ReadString(row, "summary", "").TrimEnd('.') + ". Verdict: " + ReadString(data, "verdict", "unknown")
                            + (ReadString(row, "phase", "") == "sentenced" ? "; scheduled execution day "
                                + (ReadDouble(data, "Deadline", 0) > 0 ? ReadDouble(data, "Deadline", 0).ToString("0.#####", CultureInfo.InvariantCulture) : "unrecorded")
                                + "; execution has not been verified by this sentence."
                                : ReadString(row, "phase", "") == "completed" ? "; ruling completed."
                                    : "; pending judgment cancelled; this record does not establish an execution.");
                    }
                    if (ReadString(row, "event_type", "") == "public_announcement")
                    {
                        // Render the frozen public projection, never arbitrary producer fields or an unselected summary.
                        var data = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                        row["summary"] = ReadString(data, "title", "") + ": " + ReadString(data, "body", "")
                            + (ReadString(data, "consequences", "").Length > 0 ? "\n" + ReadString(data, "consequences", "") : "");
                        if (ReadString(data, "sourceEventType", "") == "ruler_judgment")
                        {
                            row["summary"] += " Public ruling: " + ReadString(data, "publicVerdict", "unrecorded")
                                + "; phase " + ReadString(data, "sourcePhase", "unrecorded") + ".";
                            if (ReadString(data, "sourcePhase", "") == "sentenced")
                            {
                                double due = ReadDouble(data, "executionDueDay", -1);
                                row["summary"] += " Scheduled execution day " + (double.IsFinite(due) && due >= 0
                                    ? due.ToString("0.#####", CultureInfo.InvariantCulture) : "unrecorded")
                                    + "; execution has not been verified by this sentence.";
                            }
                        }
                    }
                    return row;
                }).OrderByDescending(row => ReadBool(row, "publicDirectRecall", false))
                .ThenByDescending(row => ReadDouble(row, "world_day", 0)).ToList();
        }

        private static string PublicWorldHistoryTimelineScope(ReignDbConnection connection, string timeline, long sequence,
            Dictionary<string, object> args)
        {
            args["publicTimeline0"] = timeline; args["publicSequence0"] = sequence;
            const string current = "(e.timeline_id=$publicTimeline0 AND e.sequence<=$publicSequence0)";
            var clauses = new List<string> { current };
            var seen = new HashSet<string>(StringComparer.Ordinal) { timeline };
            long ceiling = sequence;
            for (int depth = 0; depth < 32; depth++)
            {
                var branch = Program.QuerySql(connection, "SELECT * FROM world_history_timelines WHERE timeline_id=$branch;", TestDict("branch", timeline)).SingleOrDefault();
                string parent = ReadString(branch, "parent_timeline_id", "");
                if (parent.Length == 0) break;
                if (!seen.Add(parent)) return "(" + current + ")";
                // Reuse the existing retained-fork/canonical-immediate-child
                // witness. A parent name alone never grants inherited news.
                long fork = VerifiedLegacyMemoryForkSequence(connection, branch);
                if (fork < 0) break;
                ceiling = Math.Min(ceiling, fork);
                int index = clauses.Count;
                args["publicTimeline" + index] = parent; args["publicSequence" + index] = ceiling;
                clauses.Add("(e.timeline_id=$publicTimeline" + index + " AND e.sequence<=$publicSequence" + index + ")");
                timeline = parent;
                if (depth == 31) return "(" + current + ")";
            }
            return "(" + string.Join(" OR ", clauses) + ")";
        }
        internal static bool IsPublicTournamentHistory(string eventType, string dissemination)
        {
            return (string.Equals(eventType, "tournament_started", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(eventType, "tournament_finished", StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(dissemination)
                    || string.Equals(dissemination, "ordinary", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(dissemination, "major_world", StringComparison.OrdinalIgnoreCase));
        }

        internal static Dictionary<string, object> PublicTournamentKnowledgeRule(double day)
        {
            return new Dictionary<string, object>
            {
                ["audienceType"] = "global", ["audienceId"] = "all",
                ["availableDay"] = day, ["acquisitionMode"] = "public_tournament_news",
                ["confidence"] = 1d
            };
        }

        internal static void EnsureImmediateTournamentKnowledge(ReignDbConnection connection)
        {
            const string marker = "world_history_immediate_tournament_news_v1";
            if (ReadString(Program.QuerySql(connection, "SELECT value FROM schema_meta WHERE key=$key;",
                new Dictionary<string, object> { ["key"] = marker }).FirstOrDefault(), "value", "") == "1") return;

            // Add derived access rules to the existing events. Never rewrite an
            // outcome, duplicate an event, or publish a participant-only event.
            Program.ExecuteSql(connection, @"INSERT INTO world_history_knowledge_rules
(rule_id,event_id,audience_type,audience_id,available_day,acquisition_mode,confidence)
SELECT event_id || ':public_tournament_news',event_id,'global','all',world_day,'public_tournament_news',1
FROM world_history_events WHERE event_type IN ('tournament_started','tournament_finished')
AND dissemination_class IN ('ordinary','major_world')
ON CONFLICT(rule_id) DO UPDATE SET available_day=excluded.available_day;");
            Program.ExecuteSql(connection, @"UPDATE world_history_knowledge_rules SET available_day=
(SELECT e.world_day FROM world_history_events e WHERE e.event_id=world_history_knowledge_rules.event_id)
WHERE acquisition_mode IN ('realm_news','major_world_news') AND event_id IN
(SELECT event_id FROM world_history_events WHERE event_type IN ('tournament_started','tournament_finished')
AND dissemination_class IN ('ordinary','major_world'));");
            Program.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES($key,'1');",
                new Dictionary<string, object> { ["key"] = marker });
        }

        internal static List<Dictionary<string, object>> LoadKnownWorldHistoryForDialogue(
            ReignDbConnection connection, string timelineId, double worldDay,
            Program.KnowledgeAccessContext knowledge, string topic, Dictionary<string, object> route,
            Dictionary<string, object> semanticSearch, bool verifiedInterlocutor)
        {
            var result = new List<Dictionary<string, object>>();
            if (knowledge == null || !knowledge.HasNpc || worldDay <= 0d
                || double.IsNaN(worldDay) || double.IsInfinity(worldDay)) return result;
            bool worldQuestion = RouteHasLane(route, "world_affairs") || RouteHasLane(route, "local_awareness");
            bool recognize = verifiedInterlocutor && !knowledge.PlayerIdentityUnknown
                && !string.IsNullOrWhiteSpace(knowledge.PlayerId);
            WorldSimulationService.EnsureWorldHistorySchema(connection);
            timelineId = FirstNonEmpty(timelineId, WorldSimulationService.ActiveWorldHistoryTimeline(connection));
            EnsurePublicWorldNewsKnowledge(connection);
            var publicNews = LoadPublicWorldNews(connection, timelineId, worldDay, knowledge, topic);
            if (!worldQuestion && !recognize) return publicNews;

            var parameters = new Dictionary<string, object>
            {
                ["timeline"] = timelineId, ["day"] = worldDay,
                ["legacyTournamentDay"] = worldDay + 0.005d,
                ["npc"] = knowledge.NpcId, ["kingdom"] = knowledge.KingdomId,
                ["player"] = knowledge.PlayerId, ["location"] = knowledge.LocationId
            };
            // Filter knowledge, time and timeline BEFORE applying a candidate
            // limit. Routine traffic must not bury an older matching victory.
            string visible = @"e.timeline_id=$timeline AND (e.world_day<=$day OR
 (e.event_type='tournament_finished' AND e.source='bannerlord_native' AND e.world_day<=$legacyTournamentDay)) AND e.is_complete=1
AND EXISTS(SELECT 1 FROM world_history_knowledge_rules k WHERE k.event_id=e.event_id
AND (k.available_day<=$day OR (e.event_type='tournament_finished' AND e.source='bannerlord_native'
 AND k.available_day<=$legacyTournamentDay)) AND ((k.audience_type='entity' AND k.audience_id=$npc)
OR (k.audience_type='kingdom' AND k.audience_id=$kingdom) OR k.audience_type='global'))";
            string eligible = "(" + WorldHistoryEmbeddingEligibilitySql("e.")
                + " OR e.event_type='tournament_started')";
            var candidates = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            var recognitionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int documentedPlayerTournamentWins = 0;
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
                documentedPlayerTournamentWins = ReadInt(Program.QuerySql(connection,
                    "SELECT COUNT(DISTINCT e.event_id) AS count FROM world_history_events e WHERE " + visible + @"
AND e.event_type='tournament_finished'
AND EXISTS(SELECT 1 FROM world_history_entities n WHERE n.event_id=e.event_id
AND n.entity_id=$player AND n.role LIKE '%winner%');", parameters).FirstOrDefault(), "count", 0);
                var achievements = Program.QuerySql(connection, @"SELECT e.* FROM world_history_events e WHERE " + visible + @"
AND e.event_type='tournament_finished'
AND EXISTS(SELECT 1 FROM world_history_entities n WHERE n.event_id=e.event_id
AND n.entity_id=$player AND n.role LIKE '%winner%')
ORDER BY CASE WHEN e.location_id=$location THEN 0 ELSE 1 END,e.sequence DESC LIMIT 3;", parameters);
                add(achievements);
                foreach (var row in achievements) recognitionIds.Add(ReadString(row, "event_id", ""));
            }

            List<string> terms = Program.MemoryQueryTerms(topic);
            bool tournamentQuestion = Program.ContainsAny((topic ?? "").ToLowerInvariant(), "tournament", "champion", "arena");
            if (worldQuestion)
            {
                if (tournamentQuestion)
                    add(Program.QuerySql(connection, "SELECT e.* FROM world_history_events e WHERE " + visible
                        + @" AND e.event_type IN ('tournament_started','tournament_finished')
ORDER BY CASE WHEN e.location_id=$location THEN 0 ELSE 1 END,e.sequence DESC LIMIT 24;", parameters));

                string ftsQuery = Program.BuildFtsQuery(terms);
                if (!string.IsNullOrWhiteSpace(ftsQuery))
                {
                    parameters["query"] = ftsQuery;
                    add(Program.QuerySql(connection, @"SELECT e.* FROM world_history_fts
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
                    add(Program.QuerySql(connection, "SELECT e.* FROM world_history_events e WHERE " + visible
                        + " AND " + eligible + " AND e.event_id IN (" + string.Join(",", ids) + ");", parameters));
                    foreach (var pair in scores)
                        if (candidates.TryGetValue(pair.Key, out var row)) row["vectorSemanticScore"] = pair.Value;
                }
            }

            foreach (var row in candidates.Values)
            {
                double lexical = WorldSimulationService.HistoryTermScore(row, terms);
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
                var entities = WorldSimulationService.QueryWorldHistoryEntities(connection, eventId);
                bool aboutPlayer = entities.Any(e => ReadString(e, "entity_id", "").Equals(knowledge.PlayerId, StringComparison.OrdinalIgnoreCase));
                if (knowledge.PlayerIdentityUnknown && (aboutPlayer || entities.Any(e =>
                    ReadString(e, "entity_id", "") == "main_hero" || ReadString(e, "entity_id", "") == "player_hero"))) continue;
                if (!Program.UnknownIdentityMemoryEvidenceAllowed(row, knowledge, ReadString(row, "summary", ""))) continue;
                double accessDay = ReadString(row, "event_type", "") == "tournament_finished"
                    && ReadString(row, "source", "") == "bannerlord_native"
                    ? Math.Max(worldDay, Math.Min(worldDay + 0.005d, ReadDouble(row, "world_day", worldDay))) : worldDay;
                var access = DetermineWorldHistoryKnowledge(connection, new List<string> { eventId }, knowledge.NpcId, knowledge.KingdomId, accessDay);
                if (ReadString(access, "basis", "none") == "none") continue;
                row["knowledgeBasis"] = ReadString(access, "basis", "none");
                row["acquisitionModes"] = ReadStringList(access, "acquisitionModes");
                if (ReadString(row, "event_type", "") == "tournament_finished")
                {
                    row["tournamentFirsthand"] = entities.Any(e => string.Equals(ReadString(e, "entity_id", ""), knowledge.NpcId, StringComparison.OrdinalIgnoreCase)
                        && WorldSimulationService.RoleContains(ReadString(e, "role", ""), "participant", "winner"));
                    row["tournamentWinningTeam"] = entities.Where(e => WorldSimulationService.RoleContains(ReadString(e, "role", ""), "winner"))
                        .Select(e => ReadString(e, "name_snapshot", "")).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToList();
                    row["documentedPlayerTournamentWins"] = documentedPlayerTournamentWins;
                }
                row["verifiedInterlocutorAchievement"] = recognize && aboutPlayer
                    && ReadString(row, "event_type", "") == "tournament_finished"
                    && entities.Any(e => ReadString(e, "entity_id", "") == knowledge.PlayerId
                        && WorldSimulationService.RoleContains(ReadString(e, "role", ""), "winner"));
                if (ReadBool(row, "verifiedInterlocutorAchievement", false) && !recognitionIds.Contains(eventId))
                    row["dialogueHistoryScore"] = ReadDouble(row, "dialogueHistoryScore", 0d) + 32d;
                result.Add(row);
            }
            foreach (var publicRow in publicNews)
            {
                var enriched = result.FirstOrDefault(r => ReadString(r, "event_id", "") == ReadString(publicRow, "event_id", ""));
                if (enriched != null)
                    foreach (string key in new[] { "knowledgeBasis", "tournamentFirsthand", "tournamentWinningTeam", "documentedPlayerTournamentWins", "verifiedInterlocutorAchievement" })
                        if (enriched.TryGetValue(key, out var value)) publicRow[key] = value;
            }
            return publicNews.Concat(result.OrderByDescending(r => ReadDouble(r, "dialogueHistoryScore", 0d))
                .ThenByDescending(r => ReadLong(r, "sequence", 0)).Take(6))
                .GroupBy(r => ReadString(r, "event_id", ""), StringComparer.Ordinal).Select(g => g.First()).ToList();
        }

        private const string KnownWorldHistoryRules = "These recorded game events are known to this NPC. Public news establishes the reported result, not personal attendance; allegations remain allegations. Later dated outcomes supersede earlier schedules without erasing history. The current briefing is a selection, never proof that no other public news exists. A firsthand participant competed in that event and must not describe it as merely heard news. A named winning teammate won with the champion; do not say that teammate lost, was eliminated, or failed to finish. When relevant to a verified introduction, connect the supplied achievement to that person; do not treat it as an unsupported boast. Do not invent additional feats or witnesses.";

        internal static string FormatKnownWorldHistoryForDialogue(List<Dictionary<string, object>> events, int maxChars, bool includeRules = true)
        {
            if (events == null || events.Count == 0) return "";
            // A direct archive answer is not constrained by the ambient briefing
            // allowance. Preserve its complete source and the ordinary history
            // budget; the final request capacity preflight still applies.
            maxChars = (int)Math.Min(int.MaxValue, (long)maxChars + events.Where(row => ReadBool(row, "publicDirectRecall", false))
                .Sum(row => (long)ReadString(row, "summary", "").Length + 512));
            var text = new StringBuilder("KNOWN NATIVE WORLD HISTORY — AUTHORITATIVE\n");
            if (includeRules) text.AppendLine(KnownWorldHistoryRules);
            int documentedWins = events.Max(row => ReadInt(row, "documentedPlayerTournamentWins", 0));
            if (documentedWins > 0)
                text.AppendLine("The visible history documents " + RoughPerformanceCount(documentedWins)
                    + " tournament victories for this person in this timeline. Use rough language; the examples below are a selection, not a lifetime total. Do not say there were only as many victories as listed.");
            foreach (var row in events)
            {
                string basis = ReadBool(row, "tournamentFirsthand", false) || ReadString(row, "knowledgeBasis", "secondhand") == "firsthand"
                    ? "firsthand participant" : ReadStringList(row, "acquisitionModes").Contains("public_tournament_news")
                        ? "public tournament news; secondhand" : "known report; secondhand";
                List<string> winningTeam = ReadStringList(row, "tournamentWinningTeam");
                string teamFact = winningTeam.Count > 1 ? " Winning team: " + string.Join(", ", winningTeam) + "." : "";
                string line = "- [" + basis + "; day " + ReadDouble(row, "world_day", 0d).ToString("0.###", CultureInfo.InvariantCulture)
                    + "; event " + ReadString(row, "event_id", "") + "] "
                    + (ReadBool(row, "publicWorldNews", false) ? "Public announcement: "
                        + (ReadBool(row, "publicDirectRecall", false) ? ReadString(row, "summary", "") : Program.LimitText(ReadString(row, "summary", ""), 800))
                        : Program.LimitText(ReadString(row, "summary", ""), 450)) + teamFact;
                if (text.Length + line.Length + 2 > maxChars) continue;
                text.AppendLine(line);
            }
            return text.ToString().TrimEnd();
        }
    }
}
