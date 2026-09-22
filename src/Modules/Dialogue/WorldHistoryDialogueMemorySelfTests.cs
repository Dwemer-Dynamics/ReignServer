using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunWorldHistoryDialogueAssertions(string campaignId, string timelineId)
        {
            var checks = new List<Dictionary<string, object>>();
            Action<string, bool> check = (name, passed) => checks.Add(new Dictionary<string, object>
                { ["name"] = "dialogue_history_" + name, ["passed"] = passed });
            Func<string, string, double, string, string, Dictionary<string, object>> evt = (id, type, day, summary, dissemination) =>
                new Dictionary<string, object>
                {
                    ["eventId"] = id, ["sequence"] = id == "dialogue_win" ? 100 : 101,
                    ["worldDay"] = day, ["eventType"] = type, ["summary"] = summary,
                    ["locationId"] = "town_phycaon", ["locationName"] = "Phycaon",
                    ["disseminationClass"] = dissemination, ["isComplete"] = true,
                    ["entities"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["entityId"] = "main_hero", ["entityType"] = "hero",
                            ["name"] = "Michael", ["role"] = type == "tournament_finished" ? "tournament winner actor" : "participant", ["kingdomId"] = "empire_s" }
                    }
                };
            var fixtures = new List<Dictionary<string, object>>
            {
                evt("dialogue_win", "tournament_finished", 110d, "Michael won the tournament at Phycaon.", "ordinary"),
                evt("dialogue_announcement", "tournament_started", 110d, "A tournament began at Phycaon.", "ordinary"),
                evt("dialogue_future", "tournament_finished", 120d, "Michael won the future tournament.", "ordinary"),
                evt("dialogue_private", "tournament_finished", 110d, "Michael won the hidden tournament.", "participant_only"),
                evt("dialogue_legacy_precision", "tournament_finished", 110.004d, "Michael won the just-finished tournament.", "ordinary"),
                evt("dialogue_delayed", "hero_killed", 110d, "Zinnober died in a battle.", "ordinary")
            };
            var teamVictory = evt("dialogue_team_win", "tournament_finished", 110d,
                "Michael and his team won the tournament at Phycaon.", "ordinary");
            teamVictory["sequence"] = 150;
            fixtures.First(item => ReadString(item, "eventId", "") == "dialogue_legacy_precision")["source"] = "bannerlord_native";
            fixtures.First(item => ReadString(item, "eventId", "") == "dialogue_legacy_precision")["sequence"] = 140;
            var teamEntities = (List<Dictionary<string, object>>)teamVictory["entities"];
            teamEntities.Add(new Dictionary<string, object> { ["entityId"] = "ulagara_fixture", ["entityType"] = "hero",
                ["name"] = "Ulagara", ["role"] = "tournament winner teammate actor" });
            teamEntities.Add(new Dictionary<string, object> { ["entityId"] = "temurtai_fixture", ["entityType"] = "hero",
                ["name"] = "Temurtai", ["role"] = "tournament winner teammate actor" });
            fixtures.Add(teamVictory);
            for (int index = 0; index < 1; index++)
            {
                var olderWin = evt("dialogue_older_win_" + index, "tournament_finished", 109d - index,
                    "Michael won an earlier tournament at Phycaon.", "ordinary");
                olderWin["sequence"] = 90 - index;
                fixtures.Add(olderWin);
            }
            var ingest = WorldHistoryIngestBatchApi(new Dictionary<string, object>
                { ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["events"] = fixtures });
            check("fixtures_ingested", ReadInt(ingest, "accepted", 0) == fixtures.Count);
            var otherTimeline = WorldHistoryIngestBatchApi(new Dictionary<string, object>
                { ["campaignId"] = campaignId, ["timelineId"] = "dialogue_other_timeline",
                    ["events"] = new List<Dictionary<string, object>> { evt("dialogue_other", "tournament_finished", 110d, "Michael won a tournament in another timeline.", "ordinary") } });
            check("alternate_timeline_fixture", ReadInt(otherTimeline, "accepted", 0) == 1);
            // More than the old history-search window of newer routine events.
            // The tournament must still be found by type/entity and by vectors.
            var traffic = Enumerable.Range(0, 520).Select(index =>
            {
                var item = evt("dialogue_traffic_" + index, "settlement_left", 110d,
                    "A traveler left a settlement after a busy day.", "ordinary");
                item["sequence"] = 200 + index;
                return item;
            }).ToList();
            var trafficIngest = WorldHistoryIngestBatchApi(new Dictionary<string, object>
                { ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["events"] = traffic });
            check("crowded_history_fixture", ReadInt(trafficIngest, "accepted", 0) == traffic.Count);

            var worldRoute = new Dictionary<string, object> { ["selectedLanes"] = new List<string> { "world_affairs" } };
            var personalRoute = new Dictionary<string, object> { ["selectedLanes"] = new List<string> { "personal_state" } };
            var localRoute = new Dictionary<string, object> { ["selectedLanes"] = new List<string> { "local_awareness" } };
            check("semantic_source_wiring", SemanticSourceTypesForRoute(worldRoute).Contains("world_history_event")
                && SemanticSourceTypesForRoute(localRoute).Contains("world_history_event")
                && !SemanticSourceTypesForRoute(personalRoute).Contains("world_history_event"));

            var context = new Dictionary<string, object>
            {
                ["heroStringId"] = "itaria_fixture", ["mainHeroStringId"] = "main_hero", ["mainHeroName"] = "Michael",
                ["kingdomId"] = "empire_s", ["currentSettlementId"] = "town_phycaon",
                ["worldDay"] = 110d, ["timelineId"] = timelineId,
                ["identityView"] = new Dictionary<string, object> { ["knowsIdentity"] = true }
            };
            KnowledgeAccessContext observer = BuildKnowledgeAccessContext("itaria_fixture", "main_hero", "town_phycaon", context);
            Func<List<Dictionary<string, object>>, string, bool> has = (rows, id) => rows.Any(r => ReadString(r, "event_id", "") == id);
            var noVectors = new Dictionary<string, object>();
            List<Dictionary<string, object>> sameDay;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                sameDay = LoadKnownWorldHistoryForDialogue(connection, timelineId, 110d, observer,
                    "I am Michael of Howarton.", personalRoute, noVectors, true);
                check("introduction_recognizes_without_keyword_or_vectors", has(sameDay, "dialogue_win"));
                check("legacy_float_precision_does_not_hide_immediate_result", has(sameDay, "dialogue_legacy_precision"));
                check("documented_total_exceeds_sample", sameDay.Any(row => ReadInt(row, "documentedPlayerTournamentWins", 0) == 4)
                    && FormatKnownWorldHistoryForDialogue(sameDay, 6000).Contains("4 player tournament victories"));
                check("noncompetitor_secondhand", ReadString(sameDay.FirstOrDefault(r => ReadString(r, "event_id", "") == "dialogue_win"), "knowledgeBasis", "") == "secondhand");
                KnowledgeAccessContext teammate = BuildKnowledgeAccessContext("ulagara_fixture", "main_hero", "town_phycaon", context);
                var teammateHistory = LoadKnownWorldHistoryForDialogue(connection, timelineId, 110d, teammate,
                    "We won together.", personalRoute, noVectors, true);
                var sharedWin = teammateHistory.FirstOrDefault(row => ReadString(row, "event_id", "") == "dialogue_team_win");
                check("winning_teammate_firsthand", sharedWin != null && ReadBool(sharedWin, "tournamentFirsthand", false)
                    && ReadStringList(sharedWin, "tournamentWinningTeam").Contains("Ulagara")
                    && FormatKnownWorldHistoryForDialogue(teammateHistory, 6000).Contains("firsthand participant"));
                observer.LocationId = "distant_town";
                observer.KingdomId = "foreign_kingdom";
                var farAway = LoadKnownWorldHistoryForDialogue(connection, timelineId, 110d, observer,
                    "Who won the tournament?", worldRoute, noVectors, true);
                check("same_day_distant_foreign_news", has(farAway, "dialogue_win") && has(farAway, "dialogue_announcement"));
                check("future_other_timeline_and_private_excluded", !has(farAway, "dialogue_future")
                    && !has(farAway, "dialogue_other") && !has(farAway, "dialogue_private"));
                observer.KingdomId = "empire_s";
                var delayed = DetermineWorldHistoryKnowledge(connection, new List<string> { "dialogue_delayed" }, observer.NpcId, "empire_s", 110d);
                var delivered = DetermineWorldHistoryKnowledge(connection, new List<string> { "dialogue_delayed" }, observer.NpcId, "empire_s", 113d);
                check("ordinary_non_tournament_delay_retained", ReadString(delayed, "basis", "") == "none" && ReadString(delivered, "basis", "") == "secondhand");
                var direct = DetermineWorldHistoryKnowledge(connection, new List<string> { "dialogue_win" }, "main_hero", "empire_s", 110d);
                check("competitor_firsthand_retained", ReadString(direct, "basis", "") == "firsthand");
                observer.PlayerIdentityUnknown = true;
                var unknown = LoadKnownWorldHistoryForDialogue(connection, timelineId, 110d, observer,
                    "Who won the tournament?", worldRoute, noVectors, false);
                check("unknown_identity_not_revealed", !has(unknown, "dialogue_win"));
                observer.PlayerIdentityUnknown = false;

                var vectors = new Dictionary<string, object>
                {
                    ["results"] = new[] { "dialogue_win", "dialogue_other", "dialogue_future", "dialogue_private", "missing_history" }
                        .Select(id => new Dictionary<string, object> { ["score"] = 0.9d,
                            ["payload"] = new Dictionary<string, object> { ["sourceType"] = "world_history_event", ["sourceId"] = id } }).ToList()
                };
                var semantic = LoadKnownWorldHistoryForDialogue(connection, timelineId, 110d, observer,
                    "Who earned the laurel?", worldRoute, vectors, false);
                check("semantic_hit_loaded_from_authoritative_history", has(semantic, "dialogue_win"));
                check("semantic_hit_cannot_bypass_boundaries", !has(semantic, "dialogue_other") && !has(semantic, "dialogue_future")
                    && !has(semantic, "dialogue_private") && !has(semantic, "missing_history"));
                check("missing_clock_fails_closed", LoadKnownWorldHistoryForDialogue(connection, timelineId, 0d, observer,
                    "tournament", worldRoute, vectors, true).Count == 0);

                // Recreate a pre-fix access-rule set around the exact existing
                // victory, then exercise the same idempotent schema migration.
                ExecuteSql(connection, "DELETE FROM world_history_knowledge_rules WHERE event_id='dialogue_win' AND audience_type='global';");
                ExecuteSql(connection, "UPDATE world_history_knowledge_rules SET available_day=113 WHERE event_id='dialogue_win' AND audience_type='kingdom';");
                ExecuteSql(connection, "DELETE FROM schema_meta WHERE key='world_history_immediate_tournament_news_v1';");
                EnsureImmediateTournamentKnowledge(connection);
                EnsureImmediateTournamentKnowledge(connection);
                var migrated = DetermineWorldHistoryKnowledge(connection, new List<string> { "dialogue_win" }, observer.NpcId, "foreign_kingdom", 110d);
                check("legacy_victory_available_immediately", ReadString(migrated, "basis", "") == "secondhand");
                check("migration_idempotent_without_duplicate_event", ReadInt(QuerySql(connection,
                    "SELECT COUNT(*) count FROM world_history_events WHERE event_id='dialogue_win';").FirstOrDefault(), "count", 0) == 1
                    && ReadInt(QuerySql(connection, "SELECT COUNT(*) count FROM world_history_knowledge_rules WHERE event_id='dialogue_win' AND audience_type='global';").FirstOrDefault(), "count", 0) == 1);
                check("migration_keeps_private_tournament_private", ReadString(DetermineWorldHistoryKnowledge(connection,
                    new List<string> { "dialogue_private" }, observer.NpcId, "foreign_kingdom", 110d), "basis", "") == "none");
                check("migration_keeps_other_delay", ReadString(DetermineWorldHistoryKnowledge(connection,
                    new List<string> { "dialogue_delayed" }, observer.NpcId, "empire_s", 110d), "basis", "") == "none");
            }

            // Exercise the real packet builder with providers explicitly
            // disabled in this call; do not alter the user's saved settings.
            var offlineSettings = new Dictionary<string, object>
            {
                ["enableSemanticMemory"] = false, ["enableMinimeMemoryWorker"] = false,
                ["enableMinimeMemoryReranking"] = false
            };
            var packet = BuildNpcMemoryPacket(campaignId, "itaria_fixture", "main_hero", "town_phycaon",
                "Lady Itaria, I was disappointed I did not see you compete in the last tournament here. I am Michael of Howarton.", 2500, context, offlineSettings);
            string prompt = ReadString(packet, "memoryPacket", "");
            check("production_packet_injects_recorded_result", prompt.Contains("Michael won the tournament at Phycaon.")
                && prompt.StartsWith("KNOWN NATIVE WORLD HISTORY", StringComparison.Ordinal));
            check("production_packet_provenance_and_budget", prompt.Contains("public tournament news; secondhand")
                && ReadStringList(packet, "sourceEventIds").Contains("dialogue_win") && prompt.Length <= 10000);
            return checks;
        }
    }
}
