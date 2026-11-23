using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        internal static List<Dictionary<string, object>> RunWorldHistoryDialogueAssertions(string campaignId, string timelineId)
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
            var categories = Reign.Core.Contracts.World.ReignNewsCategories.Switches.Keys
                .Where(Reign.Core.Contracts.World.ReignNewsCategories.IsCategory).ToArray();
            foreach (var entry in categories.Select((category, index) => new { category, index }))
            {
                var article = new Reign.Core.Contracts.World.ReignNewsArticle { Id = "public_fixture_" + entry.index,
                    Category = entry.category, Title = "Public bulletin", Body = "PublicNoble_" + entry.index + " is the subject of this public report.",
                    WorldDay = 100, PublicationDay = 110, ActorCaption = "PRIVATE_SNAPSHOT_SENTINEL" };
                if (entry.index == 1)
                {
                    article.SourceEventType = "ruler_judgment"; article.SourceCorrelationId = "canonical_judgment";
                    article.SourcePhase = "sentenced"; article.PublicVerdict = "CleanExecution"; article.ExecutionDueDay = 117;
                }
                var publication = evt(article.Id, "public_announcement", article.PublicationDay, article.Text, "major_world");
                publication["source"] = "reign_public_news";
                publication["summary"] = "UNSELECTED_PRODUCER_SUMMARY";
                publication["sequence"] = entry.index == 0 ? 80 : 101;
                publication["payload"] = Reign.Core.Contracts.World.ReignPublicNews.Snapshot(article);
                publication["entities"] = new[] { TestDict("entityId", "public_noble_" + entry.index,
                    "entityType", "hero", "name", "PublicNoble_" + entry.index, "role", "announcement subject") };
                fixtures.Add(publication);
            }
            var sentence = evt("public_legacy_sentence", "ruler_judgment", 100, "Clean execution in fifteen days", "major_world");
            sentence["source"] = "reign_system"; sentence["phase"] = "sentenced"; sentence["correlationId"] = "legacy_judgment";
            sentence["payload"] = TestDict("judgmentId", "legacy_judgment", "verdict", "CleanExecution", "Deadline", 115d);
            sentence["entities"] = new[] { TestDict("entityId", "unqid", "entityType", "hero", "name", "Unqid", "role", "political actor"),
                TestDict("entityId", "rhagaea", "entityType", "hero", "name", "Rhagaea", "role", "political participant") };
            fixtures.Add(sentence);
            var cancelledSentence = evt("public_legacy_cancelled", "ruler_judgment", 109, "The captive left the original captor's custody; the sentence was cancelled.", "major_world");
            cancelledSentence["source"] = "reign_system"; cancelledSentence["phase"] = "escaped"; cancelledSentence["correlationId"] = "legacy_judgment";
            cancelledSentence["payload"] = TestDict("judgmentId", "legacy_judgment", "verdict", "CleanExecution", "Deadline", 115d, "CancellationReason", "escaped_or_rescued");
            cancelledSentence["entities"] = sentence["entities"];
            fixtures.Add(cancelledSentence);
            foreach (string publicType in new[] { "war_declared", "peace_made" })
            {
                var publicDiplomacy = evt("public_native_" + publicType, publicType, 110, "The two realms publicly confirmed " + publicType + ".", "major_world");
                publicDiplomacy["source"] = "bannerlord_native";
                publicDiplomacy["entities"] = new[] { TestDict("entityId", "news_kingdom", "entityType", "kingdom", "name", "NewsRealm", "role", "actor") };
                fixtures.Add(publicDiplomacy);
            }
            for (int index = 0; index < 1; index++)
            {
                var olderWin = evt("dialogue_older_win_" + index, "tournament_finished", 109d - index,
                    "Michael won an earlier tournament at Phycaon.", "ordinary");
                olderWin["sequence"] = 90 - index;
                fixtures.Add(olderWin);
            }
            var ingest = WorldSimulationService.WorldHistoryIngestBatchApi(new Dictionary<string, object>
                { ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["events"] = fixtures });
            check("fixtures_ingested", ReadInt(ingest, "accepted", 0) == fixtures.Count);
            var otherTimeline = WorldSimulationService.WorldHistoryIngestBatchApi(new Dictionary<string, object>
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
            var trafficIngest = WorldSimulationService.WorldHistoryIngestBatchApi(new Dictionary<string, object>
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
            DialogueService.KnowledgeAccessContext observer = DialogueService.BuildKnowledgeAccessContext("itaria_fixture", "main_hero", "town_phycaon", context);
            Func<List<Dictionary<string, object>>, string, bool> has = (rows, id) => rows.Any(r => ReadString(r, "event_id", "") == id);
            var noVectors = new Dictionary<string, object>();
            List<Dictionary<string, object>> sameDay;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                DialogueSelfTestStore.UpsertSchemaMeta(connection);
                sameDay = LoadKnownWorldHistoryForDialogue(connection, timelineId, 110d, observer,
                    "I am Michael of Howarton.", personalRoute, noVectors, true);
                var publicOnly = LoadKnownWorldHistoryForDialogue(connection, timelineId, 110, observer,
                    "Good evening.", personalRoute, noVectors, false);
                check("public_current_news_without_route_identity_or_vectors", publicOnly.Any(r => ReadString(r, "source", "") == "reign_public_news"));
                var publicDiplomacyRows = LoadPublicWorldNews(connection, timelineId, 110, observer, "NewsRealm");
                check("native_war_and_peace_are_immediate_public_news", has(publicDiplomacyRows, "public_native_war_declared")
                    && has(publicDiplomacyRows, "public_native_peace_made"));
                check("public_news_v2_backfills_native_diplomacy_after_v1_marker", ReadString(DetermineWorldHistoryKnowledge(connection,
                    new List<string> { "public_native_war_declared" }, observer.NpcId, "foreign_kingdom", 110), "basis", "") == "secondhand");
                var allCategories = LoadKnownWorldHistoryForDialogue(connection, timelineId, 110, observer,
                    string.Join(" ", Enumerable.Range(0, categories.Length).Select(i => "PublicNoble_" + i)), personalRoute, noVectors, false);
                check("every_public_category_direct_archive_beyond_ambient_cap", categories.All(category => allCategories.Any(r =>
                    ReadString(TryParseJsonObject(ReadString(r, "payload_json", "{}")), "category", "") == category)));
                var beforePublication = LoadKnownWorldHistoryForDialogue(connection, timelineId, 109, observer,
                    "PublicNoble_0", personalRoute, noVectors, false);
                check("event_date_does_not_anticipate_publication", !has(beforePublication, "public_fixture_0"));
                var sentenceNews = LoadKnownWorldHistoryForDialogue(connection, timelineId, 110, observer,
                    "What will happen to Rhagaea?", personalRoute, noVectors, false);
                string sentenceText = FormatKnownWorldHistoryForDialogue(sentenceNews, 24000);
                check("named_legacy_sentence_recalled_beyond_recent_news", has(sentenceNews, "public_legacy_sentence")
                    && sentenceText.Contains("Rhagaea") && sentenceText.Contains("Unqid") && sentenceText.Contains("115")
                    && sentenceText.Contains("execution has not been verified"));
                check("later_escape_is_public_and_does_not_claim_execution_completed", has(sentenceNews, "public_legacy_cancelled")
                    && FormatKnownWorldHistoryForDialogue(sentenceNews.Where(r => ReadString(r, "event_id", "") == "public_legacy_cancelled").ToList(), 6000)
                        .Contains("pending judgment cancelled; this record does not establish an execution"));
                check("public_snapshot_excludes_unselected_producer_fields", !Json.Serialize(allCategories).Contains("PRIVATE_SNAPSHOT_SENTINEL"));
                string publicText = FormatKnownWorldHistoryForDialogue(allCategories, 24000);
                check("canonical_public_sentence_retains_exact_deadline", publicText.Contains("Scheduled execution day 117")
                    && publicText.Contains("execution has not been verified") && !publicText.Contains("UNSELECTED_PRODUCER_SUMMARY"));
                var malformed = TestDict("event_type", "public_announcement", "source", "reign_public_news",
                    "world_day", 110d, "payload_json", "{not-json}");
                check("malformed_public_payload_rejected_without_throw", !ValidPublicWorldHistory(malformed));
                var legacyRow = sentenceNews.First(r => ReadString(r, "event_id", "") == "public_legacy_sentence");
                var schemaLess = TestDict("event_type", "public_announcement", "source", "reign_public_news",
                    "correlation_id", "crier:judgment:legacy_judgment:sentenced", "world_day", 110d,
                    "payload_json", Json.Serialize(TestDict("articleId", "crier:judgment:legacy_judgment:sentenced", "publicationDay", 110d)));
                check("unvalidated_article_cannot_suppress_legacy_sentence", DistinctPublicWorldHistory(new[] { legacyRow, schemaLess })
                    .Any(r => ReadString(r, "event_id", "") == "public_legacy_sentence"));
                var longDirect = TestDict("event_id", "long_direct", "publicWorldNews", true, "publicDirectRecall", true,
                    "summary", new string('x', 30000) + " REQUIRED_ARTICLE_END", "world_day", 110d);
                var ordinary = TestDict("event_id", "ordinary_after_long", "summary", "ORDINARY_EVIDENCE_REMAINS", "world_day", 110d);
                string longText = FormatKnownWorldHistoryForDialogue(new List<Dictionary<string, object>> { longDirect, ordinary }, 6000);
                check("long_direct_article_keeps_full_text_and_later_evidence", longText.Contains("REQUIRED_ARTICLE_END")
                    && longText.Contains("ORDINARY_EVIDENCE_REMAINS"));
                observer.AsOfWorldHistorySequence = 80;
                var sequenceBounded = LoadPublicWorldNews(connection, timelineId, 110, observer, "PublicNoble_0 PublicNoble_1");
                check("public_news_obeys_native_sequence_boundary", has(sequenceBounded, "public_fixture_0") && !has(sequenceBounded, "public_fixture_1"));
                observer.AsOfWorldHistorySequence = long.MaxValue;
                string childTimeline = "public_news_verified_child";
                DialogueSelfTestStore.InsertWorldHistoryTimelines2(connection, TestDict("child", childTimeline, "campaign", campaignId, "parent", timelineId, "now", DateTimeOffset.UtcNow.ToString("O")));
                var inherited = LoadPublicWorldNews(connection, childTimeline, 110, observer, "PublicNoble_0 PublicNoble_1");
                check("verified_fork_inherits_only_public_prefix", has(inherited, "public_fixture_0") && !has(inherited, "public_fixture_1"));
                DialogueSelfTestStore.UpdateWorldHistoryTimelines2(connection, TestDict("child", childTimeline));
                check("unverified_parent_link_grants_no_public_history", !has(LoadPublicWorldNews(connection, childTimeline, 110, observer, "PublicNoble_0"), "public_fixture_0"));
                check("introduction_recognizes_without_keyword_or_vectors", has(sameDay, "dialogue_win"));
                check("legacy_float_precision_does_not_hide_immediate_result", has(sameDay, "dialogue_legacy_precision"));
                check("documented_total_exceeds_sample", sameDay.Any(row => ReadInt(row, "documentedPlayerTournamentWins", 0) == 4)
                    && FormatKnownWorldHistoryForDialogue(sameDay, 6000).Contains("a few tournament victories")
                    && !FormatKnownWorldHistoryForDialogue(sameDay, 6000).Contains("4 player tournament victories"));
                check("noncompetitor_secondhand", ReadString(sameDay.FirstOrDefault(r => ReadString(r, "event_id", "") == "dialogue_win"), "knowledgeBasis", "") == "secondhand");
                DialogueService.KnowledgeAccessContext teammate = DialogueService.BuildKnowledgeAccessContext("ulagara_fixture", "main_hero", "town_phycaon", context);
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
                DialogueSelfTestStore.DeleteWorldHistoryKnowledgeRules(connection);
                DialogueSelfTestStore.UpdateWorldHistoryKnowledgeRules(connection);
                DialogueSelfTestStore.DeleteSchemaMeta3(connection);
                EnsureImmediateTournamentKnowledge(connection);
                EnsureImmediateTournamentKnowledge(connection);
                var migrated = DetermineWorldHistoryKnowledge(connection, new List<string> { "dialogue_win" }, observer.NpcId, "foreign_kingdom", 110d);
                check("legacy_victory_available_immediately", ReadString(migrated, "basis", "") == "secondhand");
                check("migration_idempotent_without_duplicate_event", ReadInt(DialogueSelfTestStore.CountWorldHistoryEvents(connection).FirstOrDefault(), "count", 0) == 1
                    && ReadInt(DialogueSelfTestStore.CountWorldHistoryKnowledgeRules(connection).FirstOrDefault(), "count", 0) == 1);
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
            foreach (bool legacyMode in new[] { true, false })
            {
                var modeSettings = new Dictionary<string, object>(offlineSettings) { ["legacyMemoryMode"] = legacyMode };
                var packet = DialogueService.BuildNpcMemoryPacket(campaignId, "itaria_fixture", "main_hero", "town_phycaon",
                    "Lady Itaria, I was disappointed I did not see you compete in the last tournament here. I am Michael of Howarton.", 2500, context, modeSettings);
                string prompt = ReadString(packet, "memoryPacket", "");
                var eventIds = ReadStringList(packet, "sourceEventIds");
                var evidence = ReadDictionary(packet, "precisionEvidence");
                bool factPresent = prompt.Contains("Michael won the tournament at Phycaon.");
                bool modeCorrect = legacyMode ? evidence == null : ReadString(evidence, "mode", "") == "precision";
                bool prefixCorrect = prompt.StartsWith(ProtectMemoryRecallSourcePolicy("itaria_fixture")
                    + (legacyMode ? "\n\nKNOWN NATIVE WORLD HISTORY" : "\n\nMEMORY EVIDENCE"), StringComparison.Ordinal);
                bool typedFact = legacyMode || ContinuityPromptRecords(prompt, false).Any(record => record.Kind == "native_confirmed"
                    && record.Text.Contains("Michael won the tournament at Phycaon.") && record.Text.Contains("event dialogue_win"));
                bool privateFutureForeignAbsent = !new[] { "dialogue_private", "dialogue_future", "dialogue_other", "dialogue_delayed" }
                    .Any(eventIds.Contains) && !prompt.Contains("Michael won the hidden tournament.")
                    && !prompt.Contains("Michael won the future tournament.") && !prompt.Contains("Michael won a tournament in another timeline.");
                var diagnostic = TestDict("legacyMode", legacyMode, "factPresent", factPresent, "modeCorrect", modeCorrect,
                    "prefixCorrect", prefixCorrect, "typedFact", typedFact, "secondhand", prompt.Contains("public tournament news; secondhand"),
                    "eventIdPresent", eventIds.Contains("dialogue_win"), "privateFutureForeignAbsent", privateFutureForeignAbsent,
                    "characters", prompt.Length, "sourceEventIds", eventIds);
                string prefix = legacyMode ? "production_packet_" : "precision_packet_";
                void PacketCheck(string name, bool passed) => checks.Add(TestDict("name", "dialogue_history_" + prefix + name,
                    "passed", passed, "summary", name + (passed ? "" : ": " + Json.Serialize(diagnostic))));
                PacketCheck("injects_recorded_result", factPresent && modeCorrect && prefixCorrect && typedFact);
                PacketCheck("provenance_and_budget", prompt.Contains("public tournament news; secondhand")
                    && eventIds.Contains("dialogue_win") && privateFutureForeignAbsent && prompt.Length <= 10000);
            }
            return checks;
        }
    }
}
