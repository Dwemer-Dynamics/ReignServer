using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NpgsqlTypes;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static void InsertCategorizedMemoryTestRow(ReignDbConnection connection, string memoryId, string eventId,
            string ownerId, string summary, long ts, double importance, double confidence, string domain,
            List<string> participants, List<string> witnesses, List<string> heardAsRumorBy)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["eventType"] = "dialogue_turn",
                ["memoryGroupKey"] = "test:" + eventId
            };
            Program.InsertMemoryRow(connection, memoryId, eventId, ownerId, "episodic", ts, 10d, "", summary,
                participants, witnesses, Json.Serialize(participants), new List<string> { ownerId }, heardAsRumorBy,
                new List<string>(), "private", importance, 0.5d, confidence,
                Program.MergeStringLists(Program.SimpleTags(summary), new[] { "conversation" }), "active", "categorized_memory_test",
                Json.Serialize(payload), "", "not_indexed");
            DialogueStore.UpdateMemories(connection, new Dictionary<string, object> { ["domain"] = domain, ["id"] = memoryId });
        }

        internal static List<Dictionary<string, object>> RunCategorizedMemorySubsystemSelfTests()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, pass, summary) => rows.Add(new Dictionary<string, object>
            {
                ["id"] = "categorized_memory_" + id, ["suite"] = "categorized_memory", ["passed"] = pass, ["summary"] = summary
            });
            add("event_categories",
                ClassifyEventSubtype("social_event_announced", "Tournament Celebration") == "tournament_announcement"
                && ClassifyEventSubtype("siege_result", "The siege ended") == "siege_result"
                && ClassifyEventCategory("dialogue_turn", new Dictionary<string, object>(), "") == "conversation"
                && ClassifyEventCategory("party_chat_turn", new Dictionary<string, object>(), "") == "conversation",
                "Events receive stable categories and subtypes.");
            string negatedCommitmentScene = RemoveNegatedSceneCommitmentTerms(
                "They discussed a cracked jug. No plans or orders were made. They did not make commitments.");
            Dictionary<string, object> negatedCommitmentRoute = BuildMemoryRetrievalRoute(
                new Dictionary<string, object> { ["enableMinimeMemoryWorker"] = false }, negatedCommitmentScene,
                new Dictionary<string, object>());
            add("negated_commitment_scene_lane",
                !RouteHasLane(negatedCommitmentRoute, "commitments_and_plots"),
                "Explicit statements that no plan, order, promise, or commitment occurred cannot misclassify a casual scene as a commitment.");
            string unconditionalGiftScene = RemoveNegatedSceneCommitmentTerms(
                "Rhovarion gave Menor a silver cup as a sincere, no-strings gift with no favor expected. No one named a specific favor or obligation.");
            Dictionary<string, object> unconditionalGiftRoute = BuildMemoryRetrievalRoute(
                new Dictionary<string, object> { ["enableMinimeMemoryWorker"] = false }, unconditionalGiftScene,
                new Dictionary<string, object>());
            add("unconditional_gift_not_commitment_lane",
                !string.Equals(ReadString(unconditionalGiftRoute, "primaryLane", ""), "commitments_and_plots", StringComparison.OrdinalIgnoreCase),
                "An explicitly no-strings gift cannot create a phantom commitment merely because its summary states that no obligation exists.");

            Dictionary<string, object> disabledSettings = new Dictionary<string, object>
            {
                ["enableMinimeMemoryWorker"] = false, ["enableMinimeMemoryReranking"] = false
            };
            var legacySettings = new Dictionary<string, object>(disabledSettings)
                { ["enableSemanticMemory"] = false, ["legacyMemoryMode"] = true };
            var precisionSettings = new Dictionary<string, object>(disabledSettings)
                { ["enableSemanticMemory"] = false, ["legacyMemoryMode"] = false };
            var datedContext = TestDict("worldDay", 23d, "mainHeroStringId", "player", "timelineId", "main");
            Dictionary<string, object> personalRoute = BuildMemoryRetrievalRoute(disabledSettings, "How have you been feeling today?", new Dictionary<string, object>());
            Dictionary<string, object> worldRoute = BuildMemoryRetrievalRoute(disabledSettings, "Who won the tournament?", new Dictionary<string, object>());
            Dictionary<string, object> namedWorldRoute = BuildMemoryRetrievalRoute(disabledSettings, "What happened to Rhotae?", new Dictionary<string, object>());
            Dictionary<string, object> resolvedEntityRoute = BuildMemoryRetrievalRoute(disabledSettings, "What became of it?",
                new Dictionary<string, object> { ["aboutEntityIds"] = new List<string> { "settlement_rhotae" } });
            Dictionary<string, object> firstSceneRecallRoute = BuildMemoryRetrievalRoute(disabledSettings,
                "Please repeat the unusual trail-name from our first scene.", new Dictionary<string, object>());
            Dictionary<string, object> firstConversationRecallRoute = BuildMemoryRetrievalRoute(disabledSettings,
                "For a simple memory check, what trail-name did I share in our first conversation?", new Dictionary<string, object>());
            add("lane_routing",
                ReadString(personalRoute, "primaryLane", "") == "personal_state" && !RouteHasLane(personalRoute, "world_affairs")
                && RouteHasLane(worldRoute, "world_affairs") && RouteHasLane(namedWorldRoute, "world_affairs")
                && RouteHasLane(resolvedEntityRoute, "world_affairs")
                && RouteHasLane(firstSceneRecallRoute, "exact_history")
                && ReadBool(firstSceneRecallRoute, "needsExactTranscript", false)
                && string.Equals(ReadString(firstConversationRecallRoute, "primaryLane", ""), "exact_history", StringComparison.OrdinalIgnoreCase)
                && ReadBool(firstConversationRecallRoute, "needsExactTranscript", false),
                "Personal questions suppress world affairs, world questions open that lane, and first-scene repeat requests activate exact transcript retrieval.");
            add("relative_day", Math.Abs(ExtractTargetWorldDay("what did you say three days ago", 100d) - 97d) < 0.001d,
                "Relative-day exact recall resolves against the current campaign day.");
            Dictionary<string, object> oversizedTurnPayload = new Dictionary<string, object>
            {
                ["campaignId"] = "campaign_a",
                ["correlationId"] = "correlation_a",
                ["playerHeroStringId"] = "player",
                ["playerName"] = "Caribos",
                ["hero"] = new Dictionary<string, object>
                {
                    ["heroStringId"] = "npc_a", ["name"] = "Mira", ["clanId"] = "clan_a",
                    ["kingdomId"] = "kingdom_a"
                },
                ["identityView"] = new Dictionary<string, object>
                {
                    ["knowsIdentity"] = false, ["identityState"] = "claimed",
                    ["unboundedEvidence"] = new string('x', 5000)
                },
                ["actionResolutionIndex"] = new Dictionary<string, object>
                {
                    ["heroes"] = Enumerable.Range(0, 100).Select(index =>
                        (object)new Dictionary<string, object>
                        {
                            ["heroStringId"] = "hero_" + index.ToString(CultureInfo.InvariantCulture),
                            ["description"] = new string('h', 2000)
                        }).ToList()
                },
                ["contextBundles"] = new List<object> { new string('c', 10000) }
            };
            Dictionary<string, object> compactTurnPayload = BuildConversationTurnStoragePayload(
                oversizedTurnPayload, Json.Serialize(oversizedTurnPayload).Length);
            string compactTurnJson = Json.Serialize(compactTurnPayload);
            add("compact_turn_payload",
                compactTurnJson.Length < 4096
                && !compactTurnPayload.ContainsKey("actionResolutionIndex")
                && !compactTurnPayload.ContainsKey("contextBundles")
                && ReadString(compactTurnPayload, "correlationId", "") == "correlation_a"
                && ReadString(compactTurnPayload, "speakerName", "") == "Mira"
                && ReadString(ReadDictionary(compactTurnPayload, "identityView"), "identityState", "") == "claimed",
                "Raw dialogue and structured columns remain authoritative while turn metadata preserves compact identity and lineage without duplicating the native action index.");
            add("stored_summary_work_inherits_turn_correlation",
                Program.StoredConversationArtifactCorrelationId(
                    new Dictionary<string, object>
                    {
                        ["payload_json"] = compactTurnJson
                    }) == "correlation_a",
                "Scene, middle-term, and rolling-arc summary provider calls can inherit the originating live-test correlation and consume the same physical-call ledger.");

            string campaignId = "categorized_memory_test_" + Guid.NewGuid().ToString("N");
            try
            {
                Dictionary<string, object> stored = Program.StoreWorldMemoryEvent(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["eventId"] = "tournament_test", ["eventType"] = "tournament_result",
                    ["summary"] = "Lady Mira won the tournament.", ["participants"] = new List<string> { "npc_a", "npc_b" },
                    ["known_by"] = new List<string> { "npc_a", "npc_b", "npc_c" }, ["visibility"] = "public",
                    ["worldDay"] = 12d, ["ts"] = 1200L
                }, "categorized_memory_test");
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    int canonical = ReadInt(DialogueStore.CountMemories(connection).FirstOrDefault(), "count", 0);
                    int experiences = ReadInt(DialogueStore.CountMemories2(connection).FirstOrDefault(), "count", 0);
                    int receipts = ReadInt(DialogueStore.CountKnowledgeReceipts(connection).FirstOrDefault(), "count", 0);
                    add("canonical_world_event", canonical == 1 && experiences == 2 && receipts == 3,
                        "One canonical public fact is stored with participant experiences and knowledge receipts.");
                }

                const string privatePromiseMarker = "SABLE_MEMORY_SELFTEST_4817";
                Program.StoreWorldMemoryEvent(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["eventId"] = "private_promise_test", ["eventType"] = "private_promise",
                    ["summary"] = "The player promised npc_a to guard the phrase " + privatePromiseMarker + ".",
                    ["participants"] = new List<string> { "npc_a", "player" }, ["known_by"] = new List<string> { "npc_a", "player" },
                    ["about_entities"] = new List<string> { "npc_a" }, ["visibility"] = "private",
                    ["worldDay"] = 13d, ["ts"] = 1300L, ["importance"] = 0.9d
                }, "categorized_memory_test");
                Dictionary<string, object> authorizedPromisePacket = Program.BuildNpcMemoryPacket(campaignId, "npc_a", "player", "", "SABLE memory promise", 2500, datedContext, legacySettings);
                Dictionary<string, object> unauthorizedPromisePacket = Program.BuildNpcMemoryPacket(campaignId, "npc_c", "player", "", "SABLE memory promise", 2500, datedContext, legacySettings);
                string authorizedPromiseText = ReadString(authorizedPromisePacket, "memoryPacket", "");
                string unauthorizedPromiseText = ReadString(unauthorizedPromisePacket, "memoryPacket", "");
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    int commitmentRows = ReadInt(DialogueStore.CountMemories3(connection).FirstOrDefault(), "count", 0);
                    add("private_promise_retrieval",
                        commitmentRows == 1 && authorizedPromiseText.Contains(privatePromiseMarker) && !unauthorizedPromiseText.Contains(privatePromiseMarker),
                        "Private promises route through commitments, remain retrievable by their owner, and do not leak to uninvolved NPCs.");
                }
                string precisePromise = ReadString(Program.BuildNpcMemoryPacket(campaignId, "npc_a", "player", "", "SABLE memory promise", 2500, datedContext, precisionSettings), "memoryPacket", "");
                string preciseStranger = ReadString(Program.BuildNpcMemoryPacket(campaignId, "npc_c", "player", "", "SABLE memory promise", 2500, datedContext, precisionSettings), "memoryPacket", "");
                add("precision_private_promise_retrieval", precisePromise.Contains(privatePromiseMarker) && !preciseStranger.Contains(privatePromiseMarker),
                    "Dated precision retrieval preserves an owner's private promise without disclosing it to an uninvolved NPC; owner=" + precisePromise.Contains(privatePromiseMarker) + ", stranger=" + preciseStranger.Contains(privatePromiseMarker) + ".");
                string undatedPromise = ReadString(Program.BuildNpcMemoryPacket(campaignId, "npc_a", "player", "", "SABLE memory promise", 2500, null, precisionSettings), "memoryPacket", "");
                add("precision_missing_date_withholds_later_memory", !undatedPromise.Contains(privatePromiseMarker),
                    "An undated caller cannot disclose a memory later than the conservative day-zero boundary.");

                const string oldArchiveMarker = "OBSIDIAN_ARCHIVE_OATH_9173";
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    InsertCategorizedMemoryTestRow(connection, "old_archive_memory", "old_archive_event", "npc_a",
                        "The NPC witnessed the " + oldArchiveMarker + " and swore to preserve it.", 1400L, 0.65d, 0.82d,
                        "interpersonal_history", new List<string> { "npc_a", "player" }, new List<string> { "npc_a" }, new List<string>());
                    for (int index = 0; index < 90; index++)
                    {
                        InsertCategorizedMemoryTestRow(connection, "archive_decoy_" + index.ToString(CultureInfo.InvariantCulture),
                            "archive_decoy_event_" + index.ToString(CultureInfo.InvariantCulture), "npc_a",
                            "An ordinary unrelated recollection number " + index.ToString(CultureInfo.InvariantCulture) + ".",
                            3000L + index, 0.2d, 0.7d, "interpersonal_history",
                            new List<string> { "npc_a", "player" }, new List<string> { "npc_a" }, new List<string>());
                    }
                    HashSet<string> ftsIds = Program.SearchMemoryFts(connection, Program.MemoryQueryTerms(oldArchiveMarker), 80);
                    List<Dictionary<string, object>> materialized = Program.LoadFtsRows(connection, "memories", "memory_id", ftsIds,
                        new Program.KnowledgeAccessContext { NpcId = "npc_a", PlayerId = "player" });
                    add("fts_full_archive_materialization",
                        ftsIds.Contains("old_archive_memory") && materialized.Any(row => ReadString(row, "memory_id", "") == "old_archive_memory"),
                        "Full-archive FTS hits are materialized by id instead of being discarded outside the newest-memory window.");
                }
                Dictionary<string, object> oldArchivePacket = Program.BuildNpcMemoryPacket(campaignId, "npc_a", "player", "",
                    "What exactly do you remember about " + oldArchiveMarker + "?", 2500, datedContext, legacySettings);
                Dictionary<string, object> oldArchiveStrangerPacket = Program.BuildNpcMemoryPacket(campaignId, "npc_c", "player", "",
                    "What exactly do you remember about " + oldArchiveMarker + "?", 2500, datedContext, legacySettings);
                string oldArchiveText = ReadString(oldArchivePacket, "memoryPacket", "");
                bool oldArchiveRecall = oldArchiveText.Contains(oldArchiveMarker)
                    && oldArchiveText.Contains("firsthand witness")
                    && oldArchiveText.Contains("confidence 0.82")
                    && !ReadString(oldArchiveStrangerPacket, "memoryPacket", "").Contains(oldArchiveMarker);
                int selectedMemoryCount = ReadInt(ReadDictionary(oldArchivePacket, "counts"), "memories", 0);
                add("long_archive_recall", oldArchiveRecall,
                    "An exact lexical memory older than the newest 80 rows is recalled without leaking to another NPC.");
                rows.Last()["data"] = new Dictionary<string, object>
                {
                    ["expectedRelevant"] = 1,
                    ["recalledRelevant"] = oldArchiveText.Contains(oldArchiveMarker) ? 1 : 0,
                    ["recallAtPacket"] = oldArchiveText.Contains(oldArchiveMarker) ? 1d : 0d,
                    ["selectedMemoryCount"] = selectedMemoryCount,
                    ["precisionAtPacket"] = selectedMemoryCount <= 0 ? 0d : (oldArchiveText.Contains(oldArchiveMarker) ? 1d : 0d) / selectedMemoryCount
                };
                string preciseArchive = ReadString(Program.BuildNpcMemoryPacket(campaignId, "npc_a", "player", "",
                    "What exactly do you remember about " + oldArchiveMarker + "?", 2500, datedContext, precisionSettings), "memoryPacket", "");
                string preciseArchiveStranger = ReadString(Program.BuildNpcMemoryPacket(campaignId, "npc_c", "player", "",
                    "What exactly do you remember about " + oldArchiveMarker + "?", 2500, datedContext, precisionSettings), "memoryPacket", "");
                add("precision_long_archive_recall", preciseArchive.Contains(oldArchiveMarker)
                    && preciseArchive.Contains("Legacy recollection; original evidence unavailable")
                    && !preciseArchiveStranger.Contains(oldArchiveMarker),
                    "Precision retrieves the older lexical hit beyond 90 decoys, preserves source uncertainty and observer privacy; recalled=" + preciseArchive.Contains(oldArchiveMarker) + ".");

                const string contradictionMarker = "SUNDERED_CROWN_REPORT_4421";
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    InsertCategorizedMemoryTestRow(connection, "contradiction_firsthand", "contradiction_event_a", "npc_a",
                        contradictionMarker + ": Duke Orman survived the ambush.", 1460L, 0.7d, 0.85d,
                        "beliefs_and_rumors", new List<string> { "npc_a", "player" }, new List<string> { "npc_a" }, new List<string>());
                    InsertCategorizedMemoryTestRow(connection, "contradiction_rumor", "contradiction_event_b", "npc_a",
                        contradictionMarker + ": court gossip claims Duke Orman died in the ambush.", 1470L, 0.6d, 0.40d,
                        "beliefs_and_rumors", new List<string> { "player" }, new List<string>(), new List<string> { "npc_a" });
                }
                Dictionary<string, object> contradictionPacket = Program.BuildNpcMemoryPacket(campaignId, "npc_a", "player", "",
                    "What rumor or claim have you heard about " + contradictionMarker + "?", 2500, datedContext, legacySettings);
                string contradictionText = ReadString(contradictionPacket, "memoryPacket", "");
                add("contradictory_recollections",
                    contradictionText.Contains("survived the ambush") && contradictionText.Contains("died in the ambush")
                    && contradictionText.Contains("firsthand witness") && contradictionText.Contains("secondhand rumor")
                    && contradictionText.Contains("confidence 0.85") && contradictionText.Contains("confidence 0.40"),
                    "Conflicting recollections remain distinct and carry enough provenance for the dialogue model to preserve uncertainty.");
                string preciseContradictions = ReadString(Program.BuildNpcMemoryPacket(campaignId, "npc_a", "player", "",
                    "What rumor or claim have you heard about " + contradictionMarker + "?", 2500, datedContext, precisionSettings), "memoryPacket", "");
                add("precision_contradictory_recollections", preciseContradictions.Contains("survived the ambush")
                    && preciseContradictions.Contains("died in the ambush")
                    && ContinuityPromptRecords(preciseContradictions, false).Count(record => record.Kind == "unverified"
                        && record.Text.Contains(contradictionMarker)) == 2,
                    "Both source-less contradictory recollections remain separate unverified records; survived=" + preciseContradictions.Contains("survived the ambush") + ", died=" + preciseContradictions.Contains("died in the ambush") + ".");

                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    InsertCategorizedMemoryTestRow(connection, "consolidated_semantic_memory", "consolidated_semantic_event", "npc_a",
                        "This source memory must disappear after consolidation.", 1450L, 0.7d, 0.8d,
                        "interpersonal_history", new List<string> { "npc_a", "player" }, new List<string> { "npc_a" }, new List<string>());
                    DialogueStore.UpdateMemories2(connection);
                    Dictionary<string, object> consolidated = DialogueStore.SelectMemories3(connection).FirstOrDefault();
                    Dictionary<string, object> fakeSemanticSearch = new Dictionary<string, object>
                    {
                        ["results"] = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["score"] = 0.99d,
                                ["payload"] = new Dictionary<string, object>
                                {
                                    ["sourceType"] = "memory", ["sourceId"] = "consolidated_semantic_memory"
                                }
                            }
                        }
                    };
                    List<Dictionary<string, object>> semanticRows = LoadSemanticRows(connection, "memories", "memory_id", "memory",
                        fakeSemanticSearch, new Program.KnowledgeAccessContext { NpcId = "npc_a", PlayerId = "player" });
                    add("consolidated_semantic_filter",
                        !EmbeddingRowActive("memory", consolidated) && semanticRows.Count == 0,
                        "Consolidated source memories are removed from the semantic index contract and rejected during row materialization.");
                }

                Dictionary<string, object> allocationRoute = new Dictionary<string, object>
                {
                    ["selectedLanes"] = new List<string> { "personal_state", "interpersonal_history", "beliefs_and_rumors" },
                    ["tokenAllocationPercent"] = new Dictionary<string, object>
                    {
                        ["personal_state"] = 55, ["interpersonal_history"] = 25, ["beliefs_and_rumors"] = 20
                    }
                };
                Dictionary<string, int> allocation = Program.AllocateMemoryLaneCharacters(
                    new List<string> { "personal_state", "interpersonal_history", "beliefs_and_rumors" },
                    new Dictionary<string, string>
                    {
                        ["personal_state"] = new string('a', 1000),
                        ["interpersonal_history"] = new string('b', 1000),
                        ["beliefs_and_rumors"] = new string('c', 1000)
                    }, allocationRoute, 1000);
                add("lane_budget_enforcement",
                    allocation.Values.Sum() == 1000
                    && allocation["personal_state"] > allocation["interpersonal_history"]
                    && allocation["interpersonal_history"] > allocation["beliefs_and_rumors"],
                    "Rendered packet character budgets follow the router's primary and supporting lane allocation.");

                string partySessionId = "party_chat_fixture_" + Guid.NewGuid().ToString("N");
                ConversationStartApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["sessionId"] = partySessionId,
                    ["npcId"] = "npc_a",
                    ["playerId"] = "player",
                    ["channel"] = "party_chat",
                    ["locationId"] = "town_fixture",
                    ["participants"] = new List<string> { "npc_a", "npc_b", "player" },
                    ["worldDay"] = 19d,
                    ["ts"] = 1900L
                });
                Dictionary<string, object> partyRequest = new Dictionary<string, object>
                {
                    ["conversationSessionId"] = partySessionId,
                    ["sceneTurnId"] = "party_fixture_turn_1",
                    ["turnId"] = "party_fixture_turn_1",
                    ["worldDay"] = 19d,
                    ["locationId"] = "town_fixture",
                    ["channel"] = "party_chat",
                    ["participants"] = new List<string> { "npc_a", "npc_b", "player" },
                    ["activeHeroIds"] = new List<string> { "npc_a", "npc_b" }
                };
                StorePartyConversationExchange(campaignId, partyRequest, "npc_a", "player", "Player", "Aldric",
                    "Remember the blue ribbon and the bent pin.", "I will remember both.", "party_event_a", 1901L);
                StorePartyConversationExchange(campaignId, partyRequest, "npc_b", "player", "Player", "Beatrice",
                    "Remember the blue ribbon and the bent pin.", "The ribbon and pin are noted.", "party_event_b", 1902L);
                StorePartyConversationExchange(campaignId, partyRequest, "npc_a", "player", "Player", "Aldric",
                    "Remember the blue ribbon and the bent pin.", "I will remember both.", "party_event_a", 1901L);
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    Dictionary<string, object> partySession = DialogueStore.SelectConversationSessions8(connection, new Dictionary<string, object> { ["id"] = partySessionId }).FirstOrDefault();
                    List<Dictionary<string, object>> partyTurns = DialogueStore.SelectConversationTurns48(connection, new Dictionary<string, object> { ["id"] = partySessionId });
                    Dictionary<string, object> partyScene = CreateConversationSceneSummary(
                        campaignId,
                        partySession,
                        partyTurns,
                        1903L,
                        new Dictionary<string, object>
                        {
                            ["enableMemoryConsolidation"] = true,
                            ["useMemoryLlmForConsolidation"] = false,
                            ["enableMinimeMemoryWorker"] = false,
                            ["enableMinimeMemoryReranking"] = false
                        });
                    string partySummaryId = ReadString(partyScene, "summaryId", "");
                    Dictionary<string, object> partySummary = DialogueStore.SelectSummaries3(connection, new Dictionary<string, object> { ["id"] = partySummaryId }).FirstOrDefault();
                    int partySourceLinks = ReadInt(DialogueStore.CountMemorySources(connection, new Dictionary<string, object> { ["id"] = partySummaryId }).FirstOrDefault(), "count", 0);
                    bool partyParticipantsComplete = partyTurns.All(turn =>
                    {
                        List<string> ids = Program.TextListFromJson(ReadString(turn, "participants_json", "[]"));
                        return ids.Contains("npc_a", StringComparer.OrdinalIgnoreCase)
                            && ids.Contains("npc_b", StringComparer.OrdinalIgnoreCase)
                            && ids.Contains("player", StringComparer.OrdinalIgnoreCase);
                    });
                    List<string> summaryKnownBy = Program.TextListFromJson(ReadString(partySummary, "known_by_json", "[]"));
                    List<string> summarySourceEvents = Program.TextListFromJson(ReadString(partySummary, "source_events_json", "[]"));
                    add("party_chat_group_session",
                        ReadString(partySession, "channel", "") == "party_chat"
                        && partyTurns.Count == 3
                        && partyTurns.Count(turn => ReadString(turn, "role", "") == "player") == 1
                        && partyTurns.Count(turn => ReadString(turn, "role", "") == "npc") == 2
                        && partyTurns.Select(turn => ReadString(turn, "exchange_id", "")).Distinct().Count() == 1
                        && partyParticipantsComplete
                        && summaryKnownBy.Contains("npc_a", StringComparer.OrdinalIgnoreCase)
                        && summaryKnownBy.Contains("npc_b", StringComparer.OrdinalIgnoreCase)
                        && summarySourceEvents.Count == 2
                        && summarySourceEvents.Contains("party_event_a", StringComparer.OrdinalIgnoreCase)
                        && summarySourceEvents.Contains("party_event_b", StringComparer.OrdinalIgnoreCase)
                        && partySourceLinks == partyTurns.Count + summarySourceEvents.Count + 1,
                        "Party chat stores one player turn plus one turn per NPC, remains retry-idempotent, preserves all witnesses, and links its turns, production events, and shared session to the scene summary.");
                }
                ConversationFinishApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["sessionId"] = partySessionId, ["worldDay"] = 19d, ["ts"] = 1904L
                });

                Dictionary<string, object> started = ConversationStartApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["npcId"] = "npc_a", ["playerId"] = "player", ["worldDay"] = 20d
                });
                string sessionId = ReadString(started, "sessionId", "");
                StoreConversationExchange(campaignId, new Dictionary<string, object>
                {
                    ["conversationSessionId"] = sessionId, ["worldDay"] = 20d, ["channel"] = "in_person"
                }, "npc_a", "player", "Player", "Aldric", "Do you remember the silver falcon?",
                    "I said the silver falcon would return at dawn.", "exchange_test", 2000L);
                Dictionary<string, object> finished = FinishConversationSession(campaignId, sessionId, "test", false, 20d, 2001L);
                Dictionary<string, object> newerStarted = ConversationStartApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["npcId"] = "npc_a", ["playerId"] = "player", ["worldDay"] = 21d
                });
                string newerSessionId = ReadString(newerStarted, "sessionId", "");
                StoreConversationExchange(campaignId, new Dictionary<string, object>
                {
                    ["conversationSessionId"] = newerSessionId, ["worldDay"] = 21d, ["channel"] = "in_person"
                }, "npc_a", "player", "Player", "Aldric", "Did you see a falcon?",
                    "A falcon crossed the western road.", "exchange_newer_test", 2100L);
                FinishConversationSession(campaignId, newerSessionId, "test", false, 21d, 2101L);
                Dictionary<string, object> longStarted = ConversationStartApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["npcId"] = "npc_a", ["playerId"] = "player", ["worldDay"] = 22d
                });
                string longSessionId = ReadString(longStarted, "sessionId", "");
                string longPlayerTurn = new string('x', 3000) + " obsidian compass";
                StoreConversationExchange(campaignId, new Dictionary<string, object>
                {
                    ["conversationSessionId"] = longSessionId, ["worldDay"] = 22d, ["channel"] = "in_person"
                }, "npc_a", "player", "Player", "Aldric", longPlayerTurn,
                    "I will remember that.", "exchange_long_exact_test", 2200L);
                FinishConversationSession(campaignId, longSessionId, "test", false, 22d, 2201L);
                Dictionary<string, object> failedRecallStarted = ConversationStartApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["npcId"] = "npc_a", ["playerId"] = "player", ["worldDay"] = 22.5d
                });
                string failedRecallSessionId = ReadString(failedRecallStarted, "sessionId", "");
                StoreConversationExchange(campaignId, new Dictionary<string, object>
                {
                    ["conversationSessionId"] = failedRecallSessionId, ["worldDay"] = 22.5d, ["channel"] = "in_person"
                }, "npc_a", "player", "Player", "Aldric",
                    "Paraphrase the obsidian-compass fact without losing its exact name or description.",
                    "No.", "exchange_failed_recall_probe", 2250L);
                FinishConversationSession(campaignId, failedRecallSessionId, "test", false, 22.5d, 2251L);
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    Dictionary<string, object> exactRoute = BuildMemoryRetrievalRoute(disabledSettings,
                        "What exactly did you say about the silver falcon returning at dawn?", new Dictionary<string, object> { ["worldDay"] = 23d });
                    Dictionary<string, object> exact = SearchExactConversationHistory(connection, "npc_a", "silver falcon return dawn", exactRoute, 2400);
                    Dictionary<string, object> longExactRoute = BuildMemoryRetrievalRoute(disabledSettings,
                        "What exactly did I say about the obsidian compass?", new Dictionary<string, object> { ["worldDay"] = 23d });
                    Dictionary<string, object> longExact = SearchExactConversationHistory(connection, "npc_a", "obsidian compass", longExactRoute, 600);
                    Dictionary<string, object> recentRoute = BuildMemoryRetrievalRoute(disabledSettings,
                        "What do you remember about our last conversation?", new Dictionary<string, object> { ["worldDay"] = 23d });
                    Dictionary<string, object> recentRaw = SearchExactConversationHistory(connection, "npc_a",
                        "What do you remember about our last conversation?", recentRoute, 50000, new Dictionary<string, object>(), "", 30);
                    int turns = ReadInt(DialogueStore.CountConversationTurns(connection, new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault(), "count", 0);
                    int links = ReadInt(DialogueStore.CountMemorySources(connection, new Dictionary<string, object> { ["id"] = ReadString(finished, "sceneSummaryId", "") }).FirstOrDefault(), "count", 0);
                    add("session_sources", turns == 2 && !string.IsNullOrWhiteSpace(ReadString(finished, "sceneSummaryId", "")) && links >= 3,
                        "Completed sessions retain exact turns and link scene summaries back to their sources.");
                    add("exact_expansion", ReadString(exact, "text", "").Contains("silver falcon")
                        && ReadStringList(exact, "expandedTurnIds").Count == 2
                        && ReadString(exact, "sessionId", "") == sessionId,
                        "BM25-ranked exact-history retrieval selects the strongest lexical exchange instead of the newest partial mention.");
                    add("exact_long_first_turn_excerpt", ReadString(longExact, "text", "").Contains("obsidian compass")
                        && ReadStringList(longExact, "expandedTurnIds").Count >= 1
                        && ReadString(longExact, "sessionId", "") == longSessionId,
                        "Exact-history retrieval keeps a bounded, source-linked excerpt containing the matched evidence when a turn exceeds the prompt budget, and a newer failed recall probe cannot shadow the source.");
                    add("recent_raw_npc_window",
                        ReadString(recentRaw, "selectionMode", "") == "recent_closed_turn_window"
                        && ReadInt(recentRaw, "rawTurnLimit", 0) == 30
                        && ReadString(recentRaw, "text", "").Contains("obsidian compass")
                        && ReadString(recentRaw, "text", "").Contains("Beatrice")
                        && ReadString(recentRaw, "text", "").Contains("ribbon and pin"),
                        "The recent 30-turn raw window is NPC-specific and preserves every attributed line from included group sessions.");
                    string FixturePromptContext(string query, string preparedMemory = null) => BuildPromptContext(campaignId, "npc_a",
                        new Dictionary<string, object>(datedContext), query,
                        "", new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>(),
                        new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(),
                        false, new Dictionary<string, object>(datedContext), "dialogue", precomputedMemoryPacket: preparedMemory);
                    string exactQuery = "For a simple memory check, what did I say about the obsidian compass in our first conversation?";
                    string legacyExactMemory = ReadString(Program.BuildNpcMemoryPacket(campaignId, "npc_a", "player", "", exactQuery,
                        2500, datedContext, legacySettings), "memoryPacket", "");
                    string livePromptContext = FixturePromptContext(exactQuery, legacyExactMemory);
                    add("exact_live_prompt_context",
                        livePromptContext.Contains("Exact conversation source") && livePromptContext.Contains("obsidian compass"),
                        "The production dialogue prompt context includes the exact-history section and its matched evidence.");
                    string ordinaryQuery = "Paraphrase the obsidian-compass fact without losing its exact description.";
                    string legacyOrdinaryMemory = ReadString(Program.BuildNpcMemoryPacket(campaignId, "npc_a", "player", "", ordinaryQuery,
                        2500, datedContext, legacySettings), "memoryPacket", "");
                    string ordinaryContinuityContext = FixturePromptContext(ordinaryQuery, legacyOrdinaryMemory);
                    add("recent_raw_window_without_recall_keywords",
                        ordinaryContinuityContext.Contains("Recent source-bearing closed conversations")
                        && ordinaryContinuityContext.Contains("obsidian compass")
                        && ordinaryContinuityContext.Contains("Beatrice")
                        && ordinaryContinuityContext.Contains("ribbon and pin"),
                        "The configured recent raw NPC window is present even when ordinary player wording does not trigger the exact-recall router, and complete attributed group context is retained.");
                    string preciseLiveContext = FixturePromptContext(exactQuery);
                    add("precision_exact_live_prompt_context", preciseLiveContext.Contains("MEMORY EVIDENCE")
                        && preciseLiveContext.Contains("obsidian compass"),
                        "The default production prompt path includes typed precision evidence and the matched original; recalled=" + preciseLiveContext.Contains("obsidian compass") + ".");
                    string preciseOrdinaryContext = FixturePromptContext(ordinaryQuery);
                    add("precision_recent_raw_window_without_recall_keywords", preciseOrdinaryContext.Contains("MEMORY EVIDENCE")
                        && preciseOrdinaryContext.Contains("obsidian compass") && preciseOrdinaryContext.Contains("Beatrice")
                        && preciseOrdinaryContext.Contains("ribbon and pin"),
                        "The default production prompt preserves the original compass fact and complete attributed group context; compass=" + preciseOrdinaryContext.Contains("obsidian compass")
                        + ", groupSpeaker=" + preciseOrdinaryContext.Contains("Beatrice") + ", groupFact=" + preciseOrdinaryContext.Contains("ribbon and pin") + ".");
                }
                Dictionary<string, object> secondFinish = ConversationFinishApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["sessionId"] = sessionId, ["worldDay"] = 20d
                });
                add("finish_idempotency", ReadBool(secondFinish, "idempotent", false), "Conversation finish is idempotent.");
            }
            catch (Exception ex)
            {
                add("integration_fixture", false, "Categorized memory fixture failed: " + Program.LimitText(ex.Message, 400));
            }
            finally
            {
                try
                {
                    ReignPostgreSqlStorage.ClearAllPools();
                    string path = Program.CampaignDirectory(campaignId);
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
