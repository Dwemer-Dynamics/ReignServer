using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void EnsurePlayerPerformanceSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS character_performance_events (
receipt_id TEXT PRIMARY KEY,event_id TEXT NOT NULL,timeline_id TEXT NOT NULL,subject_id TEXT NOT NULL,world_day REAL NOT NULL,sequence INTEGER NOT NULL,
kind TEXT NOT NULL,outcome TEXT NOT NULL,battle_type TEXT NOT NULL DEFAULT '',competition_id TEXT NOT NULL,
source TEXT NOT NULL,source_hash TEXT NOT NULL,knowledge_rules_json TEXT NOT NULL DEFAULT '[]',
UNIQUE(event_id,subject_id));");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_performance_subject_day ON character_performance_events(timeline_id,subject_id,world_day);");
            // A compact native receipt survives lossy world-history retention.
            // Save Sync snapshots the whole campaign schema, including this ledger.
            const string marker = "character_performance_backfill_v1";
            if (ReadString(QuerySql(connection, "SELECT value FROM schema_meta WHERE key=$key;", TestDict("key", marker)).FirstOrDefault(), "value", "") == "1") return;
            foreach (var row in QuerySql(connection, "SELECT * FROM world_history_events WHERE is_complete=1 AND (event_type IN ('battle_completed','tournament_finished') OR (event_type='social_outcome' AND source='reign_social' AND payload_json LIKE '%\"duelMode\"%'));"))
                ProjectPlayerPerformanceEvent(connection, row);
            ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES($key,'1');", TestDict("key", marker));
        }

        private static string NativePerformanceOutcome(string kind, Dictionary<string, object> facts, Dictionary<string, object> entity)
        {
            string role = ReadString(entity, "role", "");
            if (kind == "social_outcome")
            {
                if (ReadString(facts, "duelMode", "").Equals("practice", StringComparison.OrdinalIgnoreCase)) return "";
                if (!ReadString(facts, "sourceEventId", "").StartsWith("duel|", StringComparison.Ordinal)) return "";
                string subject = ReadFirstString(entity, "entity_id", "entityId");
                string duelWinner = ReadString(facts, "winnerHeroId", ""), loser = ReadString(facts, "loserHeroId", "");
                if (duelWinner.Length == 0 || loser.Length == 0 || duelWinner == loser) return "";
                return subject == duelWinner ? "win" : subject == loser ? "loss" : "";
            }
            if (kind == "tournament_finished")
                return Regex.IsMatch(role, @"\bwinner\b", RegexOptions.IgnoreCase) ? "win" : "";
            if (kind != "battle_completed") return "";
            if (Regex.IsMatch(role, @"\b(?:prisoner|captive|spectator|witness|observer)\b", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(role, @"\bparticipant\b", RegexOptions.IgnoreCase)) return "";
            string winner = ReadString(facts, "winningSide", "");
            if (!new[] { "attacker", "defender" }.Contains(winner.ToLowerInvariant())) return "";
            string side = ReadString(entity, "side", "").ToLowerInvariant();
            if (side.Length == 0)
                side = Regex.IsMatch(role, @"\battacker\b", RegexOptions.IgnoreCase) ? "attacker"
                    : Regex.IsMatch(role, @"\bdefender\b", RegexOptions.IgnoreCase) ? "defender" : "";
            if (side != "attacker" && side != "defender") return "";
            return side.Equals(winner, StringComparison.OrdinalIgnoreCase) ? "win" : "loss";
        }

        private static void ProjectPlayerPerformanceEvent(ReignDbConnection connection, Dictionary<string, object> row)
        {
            string id = ReadString(row, "event_id", "");
            var rules = QuerySql(connection, "SELECT audience_type,audience_id,available_day,acquisition_mode FROM world_history_knowledge_rules WHERE event_id=$id;", TestDict("id", id));
            foreach (var receipt in BuildPlayerPerformanceReceipts(row, QueryWorldHistoryEntities(connection, id), rules))
                ExecuteSql(connection, @"INSERT OR IGNORE INTO character_performance_events
(receipt_id,event_id,timeline_id,subject_id,world_day,sequence,kind,outcome,battle_type,competition_id,source,source_hash,knowledge_rules_json)
VALUES($receipt_id,$event_id,$timeline_id,$subject_id,$world_day,$sequence,$kind,$outcome,$battle_type,$competition_id,$source,$source_hash,$knowledge_rules_json);", receipt);
        }

        // Shared by ingestion and read-only legacy proof. Recovery compares
        // these receipts with existing rows; it never replays native events.
        private static List<Dictionary<string, object>> BuildPlayerPerformanceReceipts(Dictionary<string, object> row,
            List<Dictionary<string, object>> nativeEntities, List<Dictionary<string, object>> nativeRules)
        {
            var receipts = new List<Dictionary<string, object>>();
            string kind = ReadString(row, "event_type", "");
            string source = ReadString(row, "source", "");
            bool duel = kind == "social_outcome" && source == "reign_social";
            if ((!new[] { "battle_completed", "tournament_finished" }.Contains(kind) && !duel)
                || (!new[] { "bannerlord_native", "arena_overhaul", "verified_native_receipt" }.Contains(source) && !duel)
                || ReadInt(row, "is_complete", 0) != 1 || !ValidActivityDay(ReadDouble(row, "world_day", 0d))) return receipts;
            string id = ReadString(row, "event_id", ""), timeline = ReadString(row, "timeline_id", "");
            if (id.Length == 0 || timeline.Length == 0) return receipts;
            var facts = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
            if (duel && (!ReadString(facts, "sourceEventId", "").StartsWith("duel|", StringComparison.Ordinal) || !facts.ContainsKey("duelMode"))) return receipts;
            var rules = new List<Dictionary<string, object>>(nativeRules);
            // The requested coarse public record is available to a character
            // who knows the player. It grants no exact event, witness or location
            // knowledge. Participant-only outcomes keep their original scope.
            if (new[] { "ordinary", "major_world" }.Contains(ReadString(row, "dissemination_class", "")))
                rules.Add(TestDict("audience_type", "global", "audience_id", "all", "available_day", ReadDouble(row, "world_day", 0d),
                    "acquisition_mode", "coarse_public_performance"));
            var entities = duel ? new[] { ReadString(facts, "winnerHeroId", ""), ReadString(facts, "loserHeroId", "") }.Distinct()
                .Select(subject => TestDict("entity_id", subject, "entity_type", "hero")).ToList()
                : nativeEntities;
            foreach (var entity in entities.Where(e => ReadString(e, "entity_type", "") == "hero"))
            {
                string outcome = NativePerformanceOutcome(kind, facts, entity);
                string subject = ReadString(entity, "entity_id", "");
                if (outcome.Length == 0 || subject.Length == 0) continue;
                receipts.Add(TestDict("receipt_id", "performance_" + PromptHash(timeline + "|" + id + "|" + subject), "event_id", id, "timeline_id", timeline, "subject_id", subject, "world_day", ReadDouble(row, "world_day", 0d),
                        "sequence", ReadLong(row, "sequence", 0), "kind", duel ? "battle_completed" : kind, "outcome", outcome,
                        "battle_type", duel ? "Duel:" + ReadString(facts, "duelMode", "unknown") : (ReadBool(facts, "isNaval", false) ? "Naval:" : "") + ReadString(facts, "battleType", ""),
                        "competition_id", duel ? Regex.Replace(ReadString(facts, "sourceEventId", ""), @"\|(?:winner|loser)$", "") : FirstNonEmpty(ReadString(row, "correlation_id", ""), id), "source", source,
                        "source_hash", ReadString(row, "checksum", ""), "knowledge_rules_json", Json.Serialize(rules)));
            }
            return receipts;
        }

        private static bool PerformanceKnownToObserver(Dictionary<string, object> row, KnowledgeAccessContext access, bool publicOnly = false)
        {
            if (access == null) return true; // Engine metadata, never an NPC prompt.
            var rules = ReadDictionaryList(TestDict("rules", Json.DeserializeObject(ReadString(row, "knowledge_rules_json", "[]"))), "rules");
            return rules.Any(rule => ReadDouble(rule, "available_day", double.MaxValue) <= access.WorldDay
                && (ReadString(rule, "audience_type", "") == "global"
                    || !publicOnly && ReadString(rule, "audience_type", "") == "entity" && ReadString(rule, "audience_id", "") == access.NpcId
                    || !publicOnly && ReadString(rule, "audience_type", "") == "kingdom" && access.KingdomId.Length > 0 && ReadString(rule, "audience_id", "") == access.KingdomId));
        }

        private static Dictionary<string, object> ReadPlayerPerformanceCounts(ReignDbConnection connection, string timeline, string player,
            double day, KnowledgeAccessContext access = null, IEnumerable<KnowledgeAccessContext> additionalObservers = null, bool publicOnly = false)
        {
            var rows = TableExists(connection, "character_performance_events") ? QuerySql(connection,
                "SELECT * FROM character_performance_events WHERE " + MemorySourceTimelineSql("character_performance_events")
                    + " AND subject_id=$player AND world_day<=$day ORDER BY world_day,sequence;",
                TestDict("timeline", timeline, "player", player, "day", day)) : new List<Dictionary<string, object>>();
            var unique = rows.Where(r => PerformanceKnownToObserver(r, access, publicOnly)
                    && (additionalObservers ?? Enumerable.Empty<KnowledgeAccessContext>()).All(other => PerformanceKnownToObserver(r, other, publicOnly)))
                .GroupBy(r => ReadString(r, "kind", "") + ":" + ReadString(r, "competition_id", ""))
                .Select(g => g.First()).ToList();
            return TestDict("battleWins", unique.Count(r => ReadString(r, "kind", "") == "battle_completed" && ReadString(r, "outcome", "") == "win"),
                "battleLosses", unique.Count(r => ReadString(r, "kind", "") == "battle_completed" && ReadString(r, "outcome", "") == "loss"),
                "tournamentWins", unique.Count(r => ReadString(r, "kind", "") == "tournament_finished" && ReadString(r, "outcome", "") == "win"),
                "battleTypes", unique.Where(r => ReadString(r, "kind", "") == "battle_completed").GroupBy(r => ReadString(r, "battle_type", "unknown"))
                    .ToDictionary(g => g.Key, g => (object)TestDict("wins", g.Count(r => ReadString(r, "outcome", "") == "win"), "losses", g.Count(r => ReadString(r, "outcome", "") == "loss"))),
                "coverage", "Recorded native outcomes only; missing earlier history is unknown, and zero does not prove never.");
        }

        private static string RoughPerformanceCount(int count)
        {
            if (count <= 0) return "no documented results";
            if (count == 1) return "one recorded result";
            if (count < 5) return "a few";
            if (count < 10) return "several";
            if (count < 20) return "over a handful";
            if (count < 50) return "dozens";
            if (count < 100) return "many dozens";
            if (count == 100) return "around a hundred";
            if (count < 200) return "over a hundred";
            if (count < 1000) return "hundreds";
            return "thousands";
        }

        private static string BuildPlayerPerformancePrompt(ReignDbConnection connection, string observer, Dictionary<string, object> payload, List<string> sources,
            IEnumerable<string> additionalObservers = null)
        {
            string player = TableExists(connection, "identity_roster") ? ReadString(QuerySql(connection,
                "SELECT hero_id FROM identity_roster WHERE is_player=1 ORDER BY hero_id LIMIT 1;").FirstOrDefault(), "hero_id", "") : "";
            player = FirstNonEmpty(player, ReadFirstString(payload, "mainHeroStringId", "playerHeroStringId", "playerId"));
            if (player.Length == 0 || observer == player) return "";
            string peer = ReadFirstString(payload, "playerHeroStringId", "playerId", "mainHeroStringId");
            var identity = peer == player ? ReadDictionary(payload, "identityView") : null;
            bool known = identity != null ? ReadBool(identity, "canonicalNameAllowed", false)
                : TableExists(connection, "acquaintances") && TableExists(connection, "identity_roster") && IdentityStateVerified(ReadAcquaintance(connection, observer, player));
            if (!known) return "";
            var others = (additionalObservers ?? Enumerable.Empty<string>()).ToList();
            if (others.Any(other => !TableExists(connection, "acquaintances") || !TableExists(connection, "identity_roster")
                || !IdentityStateVerified(ReadAcquaintance(connection, other, player)))) return "";
            var access = BuildKnowledgeAccessContext(observer, player, "", payload);
            var counts = ReadPlayerPerformanceCounts(connection, ContinuityTimeline(payload), player, ReadDouble(payload, "worldDay", 0d), access,
                others.Select(other => BuildKnowledgeAccessContext(other, player, "", payload)), ReadBool(payload, "officialMemoryFirewall", false));
            payload["playerPerformanceMetadata"] = TestDict("subjectId", player, "knownOutcomes", counts,
                "nativeOutcomes", ReadPlayerPerformanceCounts(connection, ContinuityTimeline(payload), player, ReadDouble(payload, "worldDay", 0d)));
            var phrases = new List<string>();
            foreach (var item in new[] { ("battleWins", "battle/combat victories, including duels"), ("battleLosses", "battle defeats and combat losses"), ("tournamentWins", "tournament victories, including winning teams") })
            {
                int count = ReadInt(counts, item.Item1, 0);
                if (count > 0) phrases.Add(item.Item2 + ": " + RoughPerformanceCount(count));
            }
            if (phrases.Count == 0) return "";
            string id = "performance_" + observer + "_" + player;
            sources.Add(id);
            return RenderContinuityRecord(id, "known_player_performance", observer, "", ReadDouble(payload, "worldDay", 0d), 120, true,
                "KNOWN PLAYER PERFORMANCE — " + string.Join("; ", phrases)
                + ". Use rough language naturally only when relevant. This is a known record, not proof you witnessed it, that this person personally led every battle, or that unrecorded defeats never occurred. Include all native battle types; a tournament team victory counts once. Never invent an exact lifetime tally from these bands or the selected history examples.");
        }
    }
}
