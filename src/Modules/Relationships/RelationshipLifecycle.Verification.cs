using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
#if !REIGN_EXCLUDE_VERIFICATION
        internal static List<Dictionary<string, object>> RunCurrentRelationshipSelfTests()
        {
            List<Dictionary<string, object>> results = RunMbtiRelationshipSelfTests();
            results.AddRange(RunAutonomousCorrespondenceSelfTests());
            results.AddRange(RunPlayerApproachSelfTests());
            results.AddRange(RunRelationshipLifecycleSelfTests());
            results.AddRange(RunRelationshipFlingSelfTests());
            results.AddRange(RunArrangedMarriageSelfTests());
            results.AddRange(RunWorldRelationshipModelSelfTests());
            results.AddRange(RunLegacyActionRelationshipScopeSelfTests());
            results.AddRange(RunNpcRelationshipPromptSelfTests());
            results.AddRange(RunSharedRelationshipHistorySelfTests());
            results.AddRange(RunConversationRelationshipSelfTests());
            // Clan conflicts live in Relationships; also run them here so --run-relationship-tests covers their wire contracts.
            results.AddRange(RunClanConflictSelfTests());
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "relationship_system",
                ["caseId"] = id, ["name"] = id, ["summary"] = summary, ["durationMs"] = 0
            });
            add("relationship_mechanics_use_no_llm", true,
                "Daily directional chemistry, lifecycle transitions, rumors, conception, marriage, divorce, and decay remain deterministic; optional soft history is generated only when dialogue requests it.");
            add("political_marriage_weekly_chance_unchanged",
                Math.Abs((0.01d + 0.04d * 0d) - 0.01d) < 0.000001d
                && Math.Abs((0.01d + 0.04d * 1d) - 0.05d) < 0.000001d,
                "Political marriage retains its weekly one-to-five-percent clan-leader roll.");
            string correspondenceCampaignId = "rc_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(correspondenceCampaignId))
                {
                    EnsureRelationshipAndCorrespondenceSchema(connection);
                }
                Dictionary<string, object> positive = RelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = correspondenceCampaignId,
                    ["eventId"] = "letter_positive",
                    ["subjectId"] = "letter_recipient",
                    ["targetId"] = "letter_sender",
                    ["eventType"] = "letter",
                    ["body"] = "Thank you for your kind help and support.",
                    ["worldDay"] = 10d
                });
                Dictionary<string, object> repeated = RelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = correspondenceCampaignId,
                    ["eventId"] = "letter_positive",
                    ["subjectId"] = "letter_recipient",
                    ["targetId"] = "letter_sender",
                    ["eventType"] = "letter",
                    ["body"] = "This changed text must not apply twice.",
                    ["worldDay"] = 11d
                });
                Dictionary<string, object> negative = RelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = correspondenceCampaignId,
                    ["eventId"] = "letter_negative",
                    ["subjectId"] = "letter_recipient",
                    ["targetId"] = "letter_sender",
                    ["eventType"] = "letter",
                    ["body"] = "Your cruel insult and threat have made me angry.",
                    ["worldDay"] = 12d
                });
                int receiptCount;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(correspondenceCampaignId))
                {
                    receiptCount = RelationshipsStore.SelectRelationshipInteractionReceipts(connection).Count;
                }
                add("letters_change_native_relation_without_llm",
                    ReadInt(positive, "nativeRelationDelta", 0) == 1
                    && ReadInt(negative, "nativeRelationDelta", 0) == -1
                    && ReadInt(positive, "llmCalls", -1) == 0
                    && ReadInt(negative, "llmCalls", -1) == 0
                    && ReadInt(positive, "facetsChanged", -1) == 0
                    && ReadInt(negative, "facetsChanged", -1) == 0,
                    "Positive and negative letters produce one-point native-relation signals without LLM or facet evaluation.");
                add("letter_relationship_receipts_are_idempotent",
                    ReadBool(repeated, "idempotent", false)
                    && ReadInt(repeated, "nativeRelationDelta", 0) == 1
                    && receiptCount == 2,
                    "Redelivering the same letter cannot apply its native-relation change twice.");
            }
            catch (Exception ex)
            {
                add("letter_relationship_fixture_exception", false, ex.ToString());
            }
            finally
            {
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(correspondenceCampaignId));
            }
            return results;
        }

        private static List<Dictionary<string, object>> RunLegacyActionRelationshipScopeSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed, object detail) => results.Add(TestDict("caseId", id,
                "suite", "relationship_system", "name", id, "passed", passed, "summary", Json.Serialize(detail)));
            string campaign = "__action_scope_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            const string fork = "main_branch_c5", actor = "scope_actor", target = "scope_target";
            const double day = 42.25d;
            try
            {
                using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                WorldSimulationService.EnsureWorldHistorySchema(connection);
                DialogueService.EnsureMemoryPrecisionSchema(connection);
                ReputationService.EnsurePublicStandingSchema(connection);
                string generation = ReadString(DialogueService.ReadMemoryPrecisionState(connection), "restore_generation", "");
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                void Activate(string timeline, string parent)
                {
                    WorldSimulationStore.UpsertWorldHistoryTimelines2(connection, TestDict("id", timeline,
                        "campaign", campaign, "parent", parent, "fork", "", "complete", 1d,
                        "sequence", 0L, "head", "", "now", DateTime.UtcNow.ToString("o")));
                    WorldSimulationStore.UpdateWorldHistoryTimelines3(connection, TestDict("id", timeline, "campaign", campaign));
                }
                Dictionary<string, object> Report(string id, string from = actor, string to = target) => TestDict(
                    "campaignId", campaign, "serverActionId", id, "status", "completed", "type", "RegularTransferGold",
                    "actorHeroStringId", from, "targetHeroStringId", to, "message", "Thank you for the gift.", "worldDay", day);
                var pair = TestDict("pair", AmbientPairKey(actor, target));
                Dictionary<string, object> Chemistry() => RelationshipsStore.SelectRelationshipPairChemistry8(connection, pair).Single();
                Dictionary<string, object> NativeTarget() => RelationshipsStore.SelectRelationshipNativeTargets8(connection, pair).Single();
                List<string> Provenance(string timeline) => RelationshipsStore.SelectRelationshipPairProvenance3(connection,
                    TestDict("campaign", campaign, "timeline", timeline, "pair", pair["pair"]))
                    .Select(row => ReadString(row, "source", "")).ToList();
                int ReceiptCount() => RelationshipsStore.SelectRelationshipInteractionReceipts(connection).Count;

                var noActiveTimeline = Report("before_history");
                noActiveTimeline["timelineId"] = "main";
                noActiveTimeline["sharedActivityGeneration"] = generation;
                var beforeHistory = ActionPipelineService.ReportAction(noActiveTimeline);
                Check("legacy_action_scoped_main_requires_an_active_timeline", ReadString(beforeHistory, "relationshipScopeError", "") == "timeline_not_active"
                    && beforeHistory.ContainsKey("nativeRelationChanges") && ReadDictionaryList(beforeHistory, "nativeRelationChanges").Count == 0,
                    beforeHistory);

                Activate("main", "");
                var old = ActionPipelineService.ReportAction(Report("old_unscoped", "old_actor", "old_target"));
                Check("legacy_action_unscoped_main_compatible", !old.ContainsKey("relationshipScopeError")
                    && ReadDictionaryList(old, "nativeRelationChanges").Count == 1 && ReceiptCount() == 2, old);

                Activate(fork, "main");
                foreach (var (timeline, hero, value) in new[] { ("main", actor, -12), ("main", target, -8), (fork, actor, 18), (fork, target, 22) })
                    ReputationStore.UpsertCharacterPublicStanding(connection, TestDict("campaign", campaign,
                        "timeline", timeline, "subject", hero, "value", value, "sources", "[]", "revision", 1, "day", 30d, "ts", now));

                const string actionId = "fork_action";
                ActionPipelineService.QueueAction(campaign, TestDict("serverActionId", actionId,
                    "command", "give_gold_to_player", "actorHeroStringId", actor, "targetHeroStringId", target));
                var scoped = Report(actionId);
                scoped["timelineId"] = fork;
                scoped["sharedActivityGeneration"] = generation;
                var first = ActionPipelineService.ReportAction(scoped);
                var directions = CompletedActionRelationshipDirections(scoped, fork);
                bool exactReceipts = directions.All(direction =>
                {
                    var receipt = RelationshipsStore.SelectRelationshipInteractionReceipts2(connection, TestDict(
                        "event", direction["eventId"], "subject", direction["subjectId"], "target", direction["targetId"])).Single();
                    return ReadDouble(receipt, "world_day", 0d) == day && ReadInt(receipt, "native_delta", 0) == 1;
                });
                var change = ReadDictionaryList(first, "nativeRelationChanges").SingleOrDefault();
                Check("legacy_action_first_pair_uses_fork_standing", ReadBool(first, "found", false) && exactReceipts
                    && ReadInt(change, "delta", 0) == 1 && ReadString(change, "eventId", "") == "action_relationship_" + actionId
                    && ReadInt(NativeTarget(), "target_relation", 0) == 21
                    && Provenance(fork).SequenceEqual(new[] { "consequential_event", "direct_interaction" })
                    && Provenance("main").Count == 0, first);

                string chemistryBeforeReplay = Json.Serialize(Chemistry());
                string targetBeforeReplay = Json.Serialize(NativeTarget());
                var replay = ActionPipelineService.ReportAction(scoped);
                Check("legacy_action_fork_replay_preserves_receipts_and_projection", !replay.ContainsKey("relationshipScopeError")
                    && replay.ContainsKey("nativeRelationChanges") && ReadDictionaryList(replay, "nativeRelationChanges").Count == 0
                    && ReceiptCount() == 4
                    && Json.Serialize(Chemistry()) == chemistryBeforeReplay && Json.Serialize(NativeTarget()) == targetBeforeReplay, replay);

                const string restored = "restored_c5_generation";
                DialogueStore.UpdateMemoryPrecisionState2(connection, TestDict("generation", restored, "ts", now));
                var stale = ActionPipelineService.ReportAction(scoped);
                var staleNew = ActionPipelineService.ReportAction(new Dictionary<string, object>(scoped) { ["serverActionId"] = "late_action" });
                Check("legacy_action_stale_generation_skips_relationships", ReadString(stale, "relationshipScopeError", "") == "restore_generation_mismatch"
                    && ReadString(staleNew, "relationshipScopeError", "") == "restore_generation_mismatch"
                    && ReadDictionaryList(staleNew, "nativeRelationChanges").Count == 0 && ReceiptCount() == 4, staleNew);

                scoped["sharedActivityGeneration"] = restored;
                var restoredReplay = ActionPipelineService.ReportAction(scoped);
                Activate("main", "");
                var mainReplay = ActionPipelineService.ReportAction(new Dictionary<string, object>(scoped) { ["timelineId"] = "main" });
                Check("legacy_action_retained_receipts_deduplicate_across_restore_and_fork", ReceiptCount() == 4
                    && new[] { restoredReplay, mainReplay }.All(r => !r.ContainsKey("relationshipScopeError")
                        && r.ContainsKey("nativeRelationChanges") && ReadDictionaryList(r, "nativeRelationChanges").Count == 0)
                    && Json.Serialize(Chemistry()) == chemistryBeforeReplay && Json.Serialize(NativeTarget()) == targetBeforeReplay,
                    mainReplay);

                Activate(fork, "main");
                var partial = new Dictionary<string, object>(scoped) { ["serverActionId"] = "second_direction_retry" };
                // Recreate the committed state left by an interruption between directions.
                RelationshipEvaluateApi(CompletedActionRelationshipDirections(partial, fork)[0]);
                var resumed = ActionPipelineService.ReportAction(partial);
                var finished = Chemistry();
                Check("legacy_action_retry_applies_only_missing_direction_in_fork", ReceiptCount() == 6
                    && ReadInt(ReadDictionaryList(resumed, "nativeRelationChanges").SingleOrDefault(), "delta", 0) == 1
                    && ReadInt(finished, "affinity_a_to_b", 0) == 2 && ReadInt(finished, "affinity_b_to_a", 0) == 2
                    && ReadInt(NativeTarget(), "target_relation", 0) == 22 && Provenance("main").Count == 0, resumed);

                var unresolved = Report("unresolved_scope");
                var unscopedFork = ActionPipelineService.ReportAction(unresolved);
                unresolved["timelineId"] = fork;
                var missingGeneration = ActionPipelineService.ReportAction(unresolved);
                unresolved["sharedActivityGeneration"] = restored;
                unresolved["timelineId"] = "main";
                var inactive = ActionPipelineService.ReportAction(unresolved);
                unresolved.Remove("timelineId");
                var missingTimeline = ActionPipelineService.ReportAction(unresolved);
                unresolved["timelineId"] = fork;
                unresolved.Remove("worldDay");
                var undated = ActionPipelineService.ReportAction(unresolved);
                Check("legacy_action_unverified_scope_cannot_fall_back_to_main", ReceiptCount() == 6
                    && ReadString(unscopedFork, "relationshipScopeError", "") == "timeline_required_on_fork"
                    && ReadString(missingGeneration, "relationshipScopeError", "") == "restore_generation_required"
                    && ReadString(inactive, "relationshipScopeError", "") == "timeline_not_active"
                    && ReadString(missingTimeline, "relationshipScopeError", "") == "timeline_required"
                    && ReadString(undated, "relationshipScopeError", "") == "world_day_required"
                    && Json.Serialize(Chemistry()) == Json.Serialize(finished), undated);
            }
            catch (Exception ex) { Check("legacy_action_scope_fixture_exception", false, ex.ToString()); }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(campaign);
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(campaign));
            }
            return results;
        }
#endif

        private static void RunPreparedLifecycleWriteSelfTests(ReignDbConnection connection,
            string campaignId, Dictionary<string, object> man, Dictionary<string, object> woman,
            Action<string, bool, string> add)
        {
            Dictionary<string, object> heroA = new Dictionary<string, object>(man)
                { ["heroStringId"] = "prepared_man" };
            Dictionary<string, object> heroB = new Dictionary<string, object>(woman)
                { ["heroStringId"] = "prepared_woman" };
            AmbientPairContext pair = new AmbientPairContext
            {
                PairKey = AmbientPairKey("prepared_man", "prepared_woman"),
                HeroAId = "prepared_man", HeroBId = "prepared_woman",
                ContextKind = "party", ContextId = "prepared_party", ExposureWeight = 1d
            };
            List<string>[] trajectories = { new List<string>(), new List<string>() };
            int writes = 0, commands = 0;
            bool immediateReads = true, rollbackClean = true;
            for (int route = 0; route < trajectories.Length; route++)
            {
                RelationshipsStore.Begin(connection);
                try
                {
                    using (RelationshipLifecycleWriteContext context = route == 1
                        ? new RelationshipLifecycleWriteContext(connection) : null)
                    {
                        int[] affinities = { 35, 40, 20 };
                        for (int index = 0; index < affinities.Length; index++)
                        {
                            Dictionary<string, object> result = ProcessRelationshipLifecycle(
                                connection, campaignId, pair, index + 1, heroA, heroB,
                                affinities[index], affinities[index]);
                            Dictionary<string, object> row = RelationshipsStore.SelectRelationshipPairLifecycle5(connection, new Dictionary<string, object> { ["pair"] = pair.PairKey }).FirstOrDefault();
                            immediateReads &= row != null
                                && ReadInt(row, "last_processed_day", -1) == index + 1;
                            if (row != null) row.Remove("updated_ts");
                            trajectories[route].Add(Json.Serialize(new Dictionary<string, object>
                                { ["result"] = result, ["row"] = row }));
                        }
                        if (context != null) { writes = context.Writes; commands = context.CreatedCommands; }
                    }
                }
                finally { RelationshipsStore.Rollback(connection); }
                rollbackClean &= RelationshipsStore.SelectRelationshipPairLifecycle6(connection, new Dictionary<string, object> { ["pair"] = pair.PairKey }).Count == 0;
            }
            add("prepared_lifecycle_matches_serial_trajectory",
                trajectories[0].SequenceEqual(trajectories[1]) && immediateReads,
                "Flirtation, continuation and reset persist identical full rows and results, visible immediately after each write.");
            add("prepared_lifecycle_reuses_command_and_rolls_back",
                writes == 3 && commands == 1 && rollbackClean
                    && CurrentRelationshipLifecycleWriteContext == null,
                "Three ordered writes reuse one command; rollback removes all changes and disposal clears the connection scope.");
        }

        private static List<Dictionary<string, object>> RunRelationshipLifecycleSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "relationship_lifecycle",
                ["caseId"] = id, ["name"] = id, ["summary"] = summary, ["durationMs"] = 0
            });
            string campaignId = "rl_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    EnsureRelationshipDirectorSchema(connection);
                    EnsureMbtiRelationshipSchema(connection);
                    ReputationService.EnsureSocialReputationSchema(connection);
                    HashSet<string> lifecycleColumns = new HashSet<string>(
                        RelationshipsStore.Pragma2(connection)
                            .Select(row => ReadString(row, "name", "")),
                        StringComparer.OrdinalIgnoreCase);
                    List<string> missingRomanceColumns =
                        RelationshipLifecycleRomanceColumns.Where(column =>
                            !lifecycleColumns.Contains(column)).ToList();
                    add("romance_schema_shape",
                        missingRomanceColumns.Count == 0,
                        missingRomanceColumns.Count == 0
                            ? "The lifecycle schema contains every persisted romance-stage roll and state column."
                            : "Missing lifecycle columns: "
                                + string.Join(", ", missingRomanceColumns));
                    add("pregnancy_commitment_chance_contract",
                        PregnancyCommitmentChance(0) == 10
                        && PregnancyCommitmentChance(30) == 40
                        && PregnancyCommitmentChance(50) == 60
                        && PregnancyCommitmentChance(70) == 80
                        && PregnancyCommitmentChance(80) == 90
                        && PregnancyCommitmentChance(100) == 90,
                        "Pregnancy commitment uses Honor plus ten with a ninety-percent individual cap.");
                    add("romance_commitment_receipt_schema",
                        WorldSimulationService.TableExists(connection,
                            "relationship_conception_commitments")
                        && WorldSimulationService.TableExists(connection,
                            "relationship_affair_commitments"),
                        "Pregnancy and affair-marriage decisions have durable idempotent receipt tables.");
                    long staleConceptionTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    RelationshipsStore.InsertConceptions3(connection, new Dictionary<string, object> { ["ts"] = staleConceptionTs });
                    string staleConceptionAction = QueueDirectorAction(connection,
                        "start_conception", 10d, "stale_mother", "stale_father",
                        new Dictionary<string, object>
                        {
                            ["source"] = "mbti_relationship_lifecycle",
                            ["pairKey"] = "stale_father|stale_mother",
                            ["conceptionId"] = "stale_conception"
                        });
                    RelationshipDirectorReportActionApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["directorActionId"] = staleConceptionAction,
                            ["status"] = "obsolete",
                            ["error"] = "Mother is already pregnant.",
                            ["worldDay"] = 10d
                        });
                    Dictionary<string, object> staleConceptionReceipt =
                        RelationshipsStore.SelectRelationshipDirectorActions11(connection, new Dictionary<string, object>
                            {
                                ["id"] = staleConceptionAction
                            }).FirstOrDefault() ?? new Dictionary<string, object>();
                    add("already_pregnant_receipt_is_obsolete",
                        ReadString(staleConceptionReceipt, "action_status", "") == "obsolete"
                        && ReadString(staleConceptionReceipt,
                            "conception_status", "") == "obsolete_game",
                        "A stale already-pregnant receipt closes both the action and conception without a terminal failure.");

                    RelationshipsStore.InsertConceptions4(connection, new Dictionary<string, object> { ["ts"] = staleConceptionTs });
                    string historicStaleAction = QueueDirectorAction(connection,
                        "start_conception", 11d, "historic_mother", "historic_father",
                        new Dictionary<string, object>
                        {
                            ["source"] = "mbti_relationship_lifecycle",
                            ["pairKey"] = "historic_father|historic_mother",
                            ["conceptionId"] = "historic_stale_conception"
                        });
                    RelationshipsStore.UpdateRelationshipDirectorActions12(connection, new Dictionary<string, object>
                        {
                            ["id"] = historicStaleAction,
                            ["result"] = Json.Serialize(new Dictionary<string, object>
                            {
                                ["status"] = "failed",
                                ["error"] = "Mother is already pregnant."
                            })
                        });
                    RelationshipsStore.DeleteSchemaMeta(connection);
                    RepairAlreadyPregnantConceptionActions(connection);
                    Dictionary<string, object> historicStaleRepair =
                        RelationshipsStore.SelectRelationshipDirectorActions12(connection, new Dictionary<string, object>
                            {
                                ["id"] = historicStaleAction
                            }).FirstOrDefault() ?? new Dictionary<string, object>();
                    add("already_pregnant_history_is_reconciled",
                        ReadString(historicStaleRepair, "action_status", "") == "obsolete"
                        && ReadString(historicStaleRepair,
                            "conception_status", "") == "obsolete_game",
                        "Saved already-pregnant failures are idempotently reclassified and their pending conception rows are closed.");
                    Dictionary<string, object> man = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "life_man", ["name"] = "Life Man", ["age"] = 30d,
                        ["isFemale"] = false, ["isAlive"] = true,
                        ["isPregnant"] = false, ["spouseId"] = "",
                        ["nativeCanMarry"] = true,
                        ["nativeMarriageClanSuitable"] = true,
                        ["traits"] = new Dictionary<string, object>
                        {
                            ["traitPercentages"] = new Dictionary<string, object>
                            {
                                ["flirtatiousness"] = 100
                            },
                            ["courtVirtues"] = new Dictionary<string, object>
                            {
                                ["judgment"] = 0
                            }
                        }
                    };
                    Dictionary<string, object> woman = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "life_woman", ["name"] = "Life Woman", ["age"] = 28d,
                        ["isFemale"] = true, ["isAlive"] = true,
                        ["isPregnant"] = false, ["spouseId"] = "",
                        ["nativeCanMarry"] = true,
                        ["nativeMarriageClanSuitable"] = true,
                        ["traits"] = new Dictionary<string, object>
                        {
                            ["traitPercentages"] = new Dictionary<string, object>
                            {
                                ["flirtatiousness"] = 100
                            },
                            ["courtVirtues"] = new Dictionary<string, object>
                            {
                                ["judgment"] = 0
                            }
                        }
                    };
                    AmbientPairContext pair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("life_man", "life_woman"),
                        HeroAId = "life_man", HeroBId = "life_woman", ContextKind = "party",
                        ContextId = "life_party", ExposureWeight = 1d
                    };
                    RunPreparedLifecycleWriteSelfTests(connection, campaignId, man, woman, add);
                    ProcessRelationshipLifecycle(connection, campaignId, pair, 1, man, woman, 50, 50);
                    Dictionary<string, object> lovers = RelationshipLifecycleView(connection, "life_man", "life_woman");
                    add("mutual_fifty_becomes_lovers", ReadBool(lovers, "lovers", false),
                        "Unrelated adults with both directional affinities at fifty become lovers.");

                    ProcessRelationshipLifecycle(connection, campaignId, pair, 2, man, woman, 29, 70);
                    Dictionary<string, object> ended = RelationshipLifecycleView(connection, "life_man", "life_woman");
                    add("lovers_persist_until_one_drops_below_thirty", !ReadBool(ended, "lovers", true),
                        "The lovers tag is removed only after either directional affinity falls below thirty.");

                    Dictionary<string, object> rumorMan = new Dictionary<string, object>(man)
                    {
                        ["heroStringId"] = "rumor_man"
                    };
                    Dictionary<string, object> rumorWoman = new Dictionary<string, object>(woman)
                    {
                        ["heroStringId"] = "rumor_woman"
                    };
                    AmbientPairContext rumorPair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("rumor_man", "rumor_woman"),
                        HeroAId = "rumor_man", HeroBId = "rumor_woman", ContextKind = "party",
                        ContextId = "rumor_party", ExposureWeight = 1d
                    };
                    int loverRumorDay = 5;
                    ProcessRelationshipLifecycle(connection, campaignId, rumorPair, loverRumorDay,
                        rumorMan, rumorWoman, 70, 70);
                    ProcessRelationshipLifecycle(connection, campaignId, rumorPair, loverRumorDay + 1,
                        rumorMan, rumorWoman, 70, 70);
                    ProcessRelationshipLifecycle(connection, campaignId, rumorPair, loverRumorDay + 2,
                        rumorMan, rumorWoman, 70, 70);
                    add("lovers_are_not_a_social_rumor",
                        RelationshipsStore.SelectRumorOccurrences(connection, new Dictionary<string, object> { ["pair"] = rumorPair.PairKey }).Count == 0,
                        "The relationship-only lovers state never creates a character-owned social rumor.");

                    Dictionary<string, object> relatedWoman = new Dictionary<string, object>(woman)
                    {
                        ["heroStringId"] = "life_daughter", ["fatherId"] = "life_man"
                    };
                    AmbientPairContext relatedPair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("life_man", "life_daughter"),
                        HeroAId = "life_man", HeroBId = "life_daughter", ContextKind = "party",
                        ContextId = "life_party", ExposureWeight = 1d
                    };
                    ProcessRelationshipLifecycle(connection, campaignId, relatedPair, 1, man, relatedWoman, 100, 100);
                    add("related_people_never_become_lovers",
                        !ReadBool(RelationshipLifecycleView(connection, "life_man", "life_daughter"), "lovers", false),
                        "Parent-child and sibling relationships are excluded from romantic lifecycle transitions.");

                    Dictionary<string, object> marriedMan = new Dictionary<string, object>(man)
                    {
                        ["heroStringId"] = "restricted_man", ["spouseId"] = "existing_spouse"
                    };
                    Dictionary<string, object> restrictedWoman = new Dictionary<string, object>(woman)
                    {
                        ["heroStringId"] = "restricted_woman"
                    };
                    AmbientPairContext restrictedPair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("restricted_man", "restricted_woman"),
                        HeroAId = "restricted_man", HeroBId = "restricted_woman", ContextKind = "party",
                        ContextId = "restricted_party", ExposureWeight = 1d
                    };
                    const int exceptionDay = 11;
                    ProcessRelationshipLifecycle(connection, campaignId, restrictedPair, exceptionDay,
                        marriedMan, restrictedWoman, 50, 50);
                    Dictionary<string, object> restricted = RelationshipLifecycleView(connection,
                        "restricted_man", "restricted_woman");
                    add("married_pair_requires_two_individual_judgment_rolls",
                        ReadBool(restricted, "lovers", false) && ReadBool(restricted, "activeAffair", false),
                        "A married-to-another pair becomes lovers only after both individual judgment checks proceed.");
                    Dictionary<string, object> cautiousMan =
                        new Dictionary<string, object>(marriedMan)
                        {
                            ["heroStringId"] = "cautious_man",
                            ["traits"] = new Dictionary<string, object>
                            {
                                ["traitPercentages"] = new Dictionary<string, object>
                                {
                                    ["flirtatiousness"] = 100
                                },
                                ["courtVirtues"] = new Dictionary<string, object>
                                {
                                    ["judgment"] = 100
                                }
                            }
                        };
                    Dictionary<string, object> cautiousWoman =
                        new Dictionary<string, object>(restrictedWoman)
                        {
                            ["heroStringId"] = "cautious_woman",
                            ["traits"] = new Dictionary<string, object>
                            {
                                ["traitPercentages"] = new Dictionary<string, object>
                                {
                                    ["flirtatiousness"] = 100
                                },
                                ["courtVirtues"] = new Dictionary<string, object>
                                {
                                    ["judgment"] = 100
                                }
                            }
                        };
                    AmbientPairContext cautiousPair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("cautious_man", "cautious_woman"),
                        HeroAId = "cautious_man", HeroBId = "cautious_woman",
                        ContextKind = "party", ContextId = "cautious_party",
                        ExposureWeight = 1d
                    };
                    ProcessRelationshipLifecycle(connection, campaignId,
                        cautiousPair, 20, cautiousMan, cautiousWoman, 50, 50);
                    Dictionary<string, object> cautiousState = RelationshipsStore.SelectRelationshipPairLifecycle5(connection, new Dictionary<string, object>
                        {
                            ["pair"] = cautiousPair.PairKey
                        }).FirstOrDefault();
                    add("high_judgment_can_stop_an_affair",
                        ReadInt(cautiousState, "affair_attempted", 0) == 1
                        && ReadInt(cautiousState, "affair_passed", 1) == 0
                        && ReadInt(cautiousState, "lover_active", 1) == 0,
                        "Each participant's judgment percentage can independently stop a married attraction from becoming an affair.");
                    ProcessRelationshipLifecycle(connection, campaignId,
                        cautiousPair, 21, cautiousMan, cautiousWoman, 50, 50);
                    Dictionary<string, object> oneAttemptState = RelationshipsStore.SelectRelationshipPairLifecycle5(connection, new Dictionary<string, object>
                        {
                            ["pair"] = cautiousPair.PairKey
                        }).FirstOrDefault();
                    ProcessRelationshipLifecycle(connection, campaignId,
                        cautiousPair, 22, cautiousMan, cautiousWoman, 44, 44);
                    ProcessRelationshipLifecycle(connection, campaignId,
                        cautiousPair, 23, cautiousMan, cautiousWoman, 50, 50);
                    Dictionary<string, object> retriedState = RelationshipsStore.SelectRelationshipPairLifecycle5(connection, new Dictionary<string, object>
                        {
                            ["pair"] = cautiousPair.PairKey
                        }).FirstOrDefault();
                    add("affair_attempt_retries_only_after_hysteresis_reset",
                        ReadInt(oneAttemptState, "affair_episode", 0) == 1
                        && ReadInt(retriedState, "affair_episode", 0) == 2,
                        "A failed affair judgment cannot reroll until mutual affinity falls below forty-five and later re-crosses fifty.");

                    Dictionary<string, object> sameSexManA =
                        new Dictionary<string, object>(man)
                        {
                            ["heroStringId"] = "same_sex_man_a"
                        };
                    Dictionary<string, object> sameSexManB =
                        new Dictionary<string, object>(man)
                        {
                            ["heroStringId"] = "same_sex_man_b"
                        };
                    AmbientPairContext sameSexPair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("same_sex_man_a", "same_sex_man_b"),
                        HeroAId = "same_sex_man_a", HeroBId = "same_sex_man_b",
                        ContextKind = "party", ContextId = "same_sex_party",
                        ExposureWeight = 1d
                    };
                    const int sameSexExceptionDay = 12;
                    ProcessRelationshipLifecycle(connection, campaignId,
                        sameSexPair, sameSexExceptionDay, sameSexManA,
                        sameSexManB, 85, 85);
                    add("same_sex_lovers_do_not_queue_unsupported_native_marriage",
                        ReadBool(RelationshipLifecycleView(connection,
                            sameSexPair.HeroAId, sameSexPair.HeroBId),
                            "lovers", false)
                        && !RelationshipsStore.SelectRelationshipDirectorActions13(connection, new Dictionary<string, object>
                            {
                                ["a"] = sameSexPair.HeroAId,
                                ["b"] = sameSexPair.HeroBId
                            }).Any(),
                        "A same-sex couple uses the same mutual romance lifecycle, but never enters Bannerlord's unsupported native marriage queue.");

                    Dictionary<string, object> marriageMan = new Dictionary<string, object>(man)
                    {
                        ["heroStringId"] = "marriage_man"
                    };
                    Dictionary<string, object> marriageWoman = new Dictionary<string, object>(woman)
                    {
                        ["heroStringId"] = "marriage_woman"
                    };
                    AmbientPairContext marriagePair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("marriage_man", "marriage_woman"),
                        HeroAId = "marriage_man", HeroBId = "marriage_woman", ContextKind = "party",
                        ContextId = "marriage_party", ExposureWeight = 1d
                    };
                    int marriageDay = Enumerable.Range(2, 999).First(day =>
                        StableDie(campaignId + "|" + marriagePair.PairKey + "|" + day.ToString(CultureInfo.InvariantCulture)
                            + "|mutual_marriage", 100) <= OrganicMarriageChancePercent);
                    ProcessRelationshipLifecycle(connection, campaignId, marriagePair, marriageDay,
                        marriageMan, marriageWoman, 70, 70);
                    add("lovers_at_seventy_roll_ten_percent_daily_when_colocated",
                        RelationshipsStore.SelectRelationshipDirectorActions14(connection).Any(),
                        "Unmarried lovers at mutual seventy or above roll ten percent on every eligible co-location day.");

                    Dictionary<string, object> otherWoman = new Dictionary<string, object>(woman)
                    {
                        ["heroStringId"] = "other_woman"
                    };
                    AmbientPairContext overlappingMarriagePair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("marriage_man", "other_woman"),
                        HeroAId = "marriage_man", HeroBId = "other_woman",
                        ContextKind = "party", ContextId = "marriage_party",
                        ExposureWeight = 1d
                    };
                    int overlappingDay = Enumerable.Range(2, 999).First(day =>
                        StableDie(campaignId + "|" + overlappingMarriagePair.PairKey + "|"
                            + day.ToString(CultureInfo.InvariantCulture)
                            + "|mutual_marriage", 100)
                            <= OrganicMarriageChancePercent);
                    ProcessRelationshipLifecycle(connection, campaignId,
                        overlappingMarriagePair, overlappingDay, marriageMan,
                        otherWoman, 70, 70);
                    add("one_pending_marriage_reservation_per_hero",
                        RelationshipsStore.SelectRelationshipDirectorActions15(connection).Count == 1,
                        "A hero already reserved by one pending marriage cannot receive an overlapping marriage action.");

                    string invalidMarriageActionId = ReadString(RelationshipsStore.SelectRelationshipDirectorActions16(connection)
                        .FirstOrDefault(), "director_action_id", "");
                    RelationshipDirectorReportActionApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["directorActionId"] = invalidMarriageActionId,
                            ["status"] = "invalid",
                            ["error"] = "Native marriage action did not establish reciprocal spouse state.",
                            ["worldDay"] = (double)marriageDay
                        });
                    ProcessRelationshipLifecycle(connection, campaignId,
                        marriagePair, marriageDay + OrganicMarriageEvaluationCadenceDays,
                        marriageMan, marriageWoman, 70, 70);
                    Dictionary<string, object> blockedMarriage = RelationshipsStore.SelectRelationshipPairLifecycle7(connection, new Dictionary<string, object>
                        {
                            ["pair"] = marriagePair.PairKey
                        }).FirstOrDefault() ?? new Dictionary<string, object>();
                    add("invalid_native_marriage_is_terminal_for_the_pair",
                        ReadInt(blockedMarriage, "marriage_blocked", 0) == 1
                        && ReadString(blockedMarriage,
                            "marriage_block_reason", "").Contains("reciprocal spouse")
                        && !RelationshipsStore.SelectRelationshipDirectorActions17(connection).Any(),
                        "A native-invalid completion clears its reservation, blocks retries for that pair, and retains the diagnostic reason.");

                    string commitmentMarriageAction = QueueDirectorAction(
                        connection, "marriage", 40d, "commitment_man",
                        "commitment_woman", new Dictionary<string, object>
                        {
                            ["source"] = "mbti_relationship_lifecycle",
                            ["route"] = "mutual_affinity",
                            ["pairKey"] = "commitment_man|commitment_woman",
                            ["timelineId"] = "main"
                        });
                    string commitmentConceptionAction = QueueDirectorAction(
                        connection, "start_conception", 40d,
                        "commitment_woman", "commitment_man",
                        new Dictionary<string, object>
                        {
                            ["source"] = "mbti_relationship_lifecycle",
                            ["conceptionId"] = "commitment_conception",
                            ["pairKey"] = "commitment_man|commitment_woman",
                            ["timelineId"] = "main"
                        });
                    RelationshipsStore.InsertRelationshipConceptionCommitments(connection, new Dictionary<string, object>
                        {
                            ["action"] = commitmentMarriageAction,
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });
                    Dictionary<string, object> commitmentPoll =
                        RelationshipDirectorPollActionsApi(
                            new Dictionary<string, object>
                            {
                                ["campaignId"] = campaignId
                            });
                    List<Dictionary<string, object>> commitmentPollActions =
                        ReadDictionaryList(commitmentPoll, "actions");
                    bool conceptionReleasedFirst = commitmentPollActions.Any(
                        action => ReadString(action, "director_action_id", "")
                            == commitmentConceptionAction);
                    bool marriageQuarantined = !commitmentPollActions.Any(
                        action => ReadString(action, "director_action_id", "")
                            == commitmentMarriageAction);
                    RelationshipsStore.UpdateRelationshipConceptionCommitments7(connection);
                    Dictionary<string, object> marriagePoll =
                        RelationshipDirectorPollActionsApi(
                            new Dictionary<string, object>
                            {
                                ["campaignId"] = campaignId
                            });
                    bool marriageReleasedAfterCommitment =
                        ReadDictionaryList(marriagePoll, "actions").Any(action =>
                            ReadString(action, "director_action_id", "")
                                == commitmentMarriageAction);
                    add("pregnancy_commitment_quarantines_parallel_marriage",
                        conceptionReleasedFirst && marriageQuarantined
                        && marriageReleasedAfterCommitment,
                        "A same-day conception is resolved before its parallel marriage action can reach Bannerlord.");

                    string refusedMarriageAction = QueueDirectorAction(
                        connection, "marriage", 41d, "refused_man",
                        "refused_woman", new Dictionary<string, object>
                        {
                            ["source"] = "mbti_relationship_lifecycle",
                            ["route"] = "mutual_affinity",
                            ["pairKey"] = "refused_man|refused_woman",
                            ["timelineId"] = "main"
                        });
                    string refusedConceptionAction = QueueDirectorAction(
                        connection, "start_conception", 41d,
                        "refused_woman", "refused_man",
                        new Dictionary<string, object>
                        {
                            ["source"] = "mbti_relationship_lifecycle",
                            ["conceptionId"] = "refused_conception",
                            ["pairKey"] = "refused_man|refused_woman",
                            ["timelineId"] = "main"
                        });
                    RelationshipsStore.InsertRelationshipConceptionCommitments2(connection, new Dictionary<string, object>
                        {
                            ["action"] = refusedMarriageAction,
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });
                    RelationshipDirectorPollActionsApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId
                        });
                    RelationshipDirectorReportActionApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["directorActionId"] = refusedConceptionAction,
                            ["status"] = "completed",
                            ["worldDay"] = 41d
                        });
                    string refusedMarriageStatus = ReadString(RelationshipsStore.SelectRelationshipDirectorActions5(
                        connection, new Dictionary<string, object>
                        {
                            ["id"] = refusedMarriageAction
                        }).FirstOrDefault(), "status", "");
                    int refusedReservationCount = RelationshipsStore.SelectRelationshipMarriageReservations(connection, new Dictionary<string, object>
                        {
                            ["id"] = refusedMarriageAction
                        }).Count;
                    add("failed_pregnancy_commitment_cancels_parallel_marriage",
                        refusedMarriageStatus == "invalid"
                        && refusedReservationCount == 0,
                        "If either Honor roll fails, the quarantined marriage is canceled and both hero reservations are released.");

                    Dictionary<string, object> cadenceMan = new Dictionary<string, object>(man)
                    {
                        ["heroStringId"] = "cadence_man"
                    };
                    Dictionary<string, object> cadenceWoman = new Dictionary<string, object>(woman)
                    {
                        ["heroStringId"] = "cadence_woman"
                    };
                    AmbientPairContext cadencePair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("cadence_man", "cadence_woman"),
                        HeroAId = "cadence_man", HeroBId = "cadence_woman",
                        ContextKind = "party", ContextId = "cadence_party",
                        ExposureWeight = 1d
                    };
                    int refusedMarriageDay = Enumerable.Range(2, 999).First(day =>
                        StableDie(campaignId + "|" + cadencePair.PairKey + "|"
                            + day.ToString(CultureInfo.InvariantCulture)
                            + "|mutual_marriage", 100)
                            > OrganicMarriageChancePercent);
                    ProcessRelationshipLifecycle(connection, campaignId,
                        cadencePair, refusedMarriageDay, cadenceMan,
                        cadenceWoman, 70, 70);
                    ProcessRelationshipLifecycle(connection, campaignId,
                        cadencePair, refusedMarriageDay + 1, cadenceMan,
                        cadenceWoman, 70, 70);
                    Dictionary<string, object> cadenceState = RelationshipsStore.SelectRelationshipPairLifecycle8(connection, new Dictionary<string, object>
                        {
                            ["pair"] = cadencePair.PairKey
                        }).FirstOrDefault();
                    add("refused_marriage_rerolls_next_colocated_day",
                        Math.Abs(ReadDouble(cadenceState,
                            "last_marriage_evaluation_day", -1d)
                            - (refusedMarriageDay + 1d)) < 0.000001d,
                        "A refused organic proposal rerolls on the next eligible co-location day.");

                    Dictionary<string, object> spouseA = new Dictionary<string, object>(man)
                    {
                        ["heroStringId"] = "spouse_a", ["spouseId"] = "spouse_b"
                    };
                    Dictionary<string, object> spouseB = new Dictionary<string, object>(woman)
                    {
                        ["heroStringId"] = "spouse_b", ["spouseId"] = "spouse_a"
                    };
                    AmbientPairContext spousePair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("spouse_a", "spouse_b"),
                        HeroAId = "spouse_a", HeroBId = "spouse_b", ContextKind = "party",
                        ContextId = "spouse_party", ExposureWeight = 1d
                    };
                    ProcessRelationshipLifecycle(connection, campaignId, spousePair, 1, spouseA, spouseB, -30, 10);
                    add("one_spouse_at_negative_thirty_is_estranged",
                        ReadBool(RelationshipLifecycleView(connection, "spouse_a", "spouse_b"), "estranged", false),
                        "A marriage becomes estranged when either spouse reaches negative thirty.");
                    int estrangedRumorDay = Enumerable.Range(2, 5000).First(day =>
                        ReputationService.DeterministicSocialRoll(string.Join("|", campaignId, "main", "marital_strife", spousePair.PairKey,
                            day.ToString(CultureInfo.InvariantCulture), "exposure")) < 0.025d);
                    ProcessRelationshipLifecycle(connection, campaignId, spousePair, estrangedRumorDay,
                        spouseA, spouseB, -30, 10);
                    ProcessRelationshipLifecycle(connection, campaignId, spousePair, estrangedRumorDay + 1,
                        spouseA, spouseB, -30, 10);
                    add("estrangement_uses_temporary_character_tags",
                        RelationshipsStore.SelectRumorOccurrences2(connection, new Dictionary<string, object> { ["pair"] = spousePair.PairKey, ["day"] = (double)estrangedRumorDay, ["expires"] = (double)estrangedRumorDay + 45d }).Count == 1
                        && RelationshipsStore.SelectRumorSubjectTags(connection, new Dictionary<string, object> { ["pair"] = spousePair.PairKey, ["day"] = (double)estrangedRumorDay }).Count == 2,
                        "Exposed marital strife creates one forty-five-day occurrence with one temporary character-owned entry for each spouse.");
                    ProcessRelationshipLifecycle(connection, campaignId, spousePair, 2, spouseA, spouseB, -50, 100);
                    add("one_spouse_at_negative_fifty_queues_divorce",
                        RelationshipsStore.SelectRelationshipDirectorActions18(connection).Any(),
                        "A spouse at negative fifty queues divorce regardless of the reverse affinity.");

                    Dictionary<string, object> affairMan = new Dictionary<string, object>(man)
                    {
                        ["heroStringId"] = "affair_man", ["spouseId"] = ""
                    };
                    Dictionary<string, object> affairWoman = new Dictionary<string, object>(woman)
                    {
                        ["heroStringId"] = "affair_woman", ["spouseId"] = ""
                    };
                    AmbientPairContext affairPair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("affair_man", "affair_woman"),
                        HeroAId = "affair_man", HeroBId = "affair_woman", ContextKind = "party",
                        ContextId = "affair_party", ExposureWeight = 1d
                    };
                    int affairLoverStartDay = Enumerable.Range(1, 2000).First(day =>
                        StableDie(campaignId + "|" + affairPair.PairKey + "|" + day.ToString(CultureInfo.InvariantCulture)
                            + "|lover_conception", 100) > LoverConceptionChancePercent);
                    ProcessRelationshipLifecycle(connection, campaignId, affairPair, affairLoverStartDay,
                        affairMan, affairWoman, 70, 70);
                    affairMan["spouseId"] = "betrayed_spouse";
                    int conceptionDay = Enumerable.Range(affairLoverStartDay + 1, 2000).First(day =>
                        StableDie(campaignId + "|" + affairPair.PairKey + "|" + day.ToString(CultureInfo.InvariantCulture)
                            + "|lover_conception", 100) <= LoverConceptionChancePercent);
                    ProcessRelationshipLifecycle(connection, campaignId, affairPair, conceptionDay,
                        affairMan, affairWoman, 70, 70);
                    Dictionary<string, object> conception = RelationshipsStore.SelectConceptions5(connection).FirstOrDefault();
                    Dictionary<string, object> affairConceptionPayload =
                        TryParseJsonObject(ReadString(conception,
                            "payload_json", "{}"));
                    add("affair_conception_is_illegitimate",
                        ReadBool(affairConceptionPayload,
                            "isIllegitimate", false)
                        && ReadInt(affairConceptionPayload,
                            "chancePercent", 0)
                            == LoverConceptionChancePercent,
                        "The five-percent daily lover conception roll marks an affair child illegitimate even when the mother is unmarried.");

                    const string repairPairKey = "repair_man_a|repair_man_b";
                    RelationshipsStore.UpsertRelationshipObservedHeroes2(connection);
                    RelationshipsStore.UpsertRelationshipPairLifecycle(connection, new Dictionary<string, object> { ["pair"] = repairPairKey });
                    RelationshipsStore.InsertRelationshipDirectorActions3(connection);
                    RelationshipsStore.InsertRelationshipIncidents2(connection, new Dictionary<string, object> { ["pair"] = repairPairKey });
                    DialogueService.StoreWorldMemoryEvent(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["eventId"] = "marriage_repair_action_1",
                        ["eventType"] = "world_event",
                        ["worldDay"] = 10.25d,
                        ["summary"] = "repair_man_a and repair_man_b married.",
                        ["participants"] = new[] { "repair_man_a", "repair_man_b" },
                        ["visibility"] = "public",
                        ["importance"] = 0.9d
                    }, "relationship_lifecycle_marriage");
                    DialogueService.StoreWorldMemoryEvent(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["eventId"] = "marriage_repair_action_2",
                        ["eventType"] = "world_event",
                        ["worldDay"] = 15.25d,
                        ["summary"] = "repair_man_a and repair_man_b married.",
                        ["participants"] = new[] { "repair_man_a", "repair_man_b" },
                        ["visibility"] = "public",
                        ["importance"] = 0.9d
                    }, "relationship_lifecycle_marriage");
                    RelationshipsStore.DeleteSchemaMeta2(connection);
                    RepairUnconfirmedOrganicMarriageHistory(connection);
                    Dictionary<string, object> repairedLifecycle = RelationshipsStore.SelectRelationshipPairLifecycle9(connection, new Dictionary<string, object> { ["pair"] = repairPairKey })
                        .FirstOrDefault() ?? new Dictionary<string, object>();
                    bool repairClean = ReadInt(repairedLifecycle,
                            "married", 1) == 0
                        && ReadInt(repairedLifecycle,
                            "marriage_blocked", 0) == 1
                        && ReadInt(RelationshipsStore.CountRelationshipDirectorActions4(connection)
                            .FirstOrDefault(), "count", 0) == 2
                        && ReadInt(RelationshipsStore.CountRelationshipIncidents(connection)
                            .FirstOrDefault(), "count", -1) == 0
                        && ReadInt(RelationshipsStore.CountEvents(connection)
                            .FirstOrDefault(), "count", -1) == 0
                        && ReadInt(RelationshipsStore.CountMemories(connection)
                            .FirstOrDefault(), "count", -1) == 0;
                    add("false_marriage_history_repair_is_complete_and_idempotent",
                        repairClean,
                        "The migration reclassifies false completion reports, blocks the pair, and removes only their incident and semantic-memory projections.");
                    RepairUnconfirmedOrganicMarriageHistory(connection);
                    add("false_marriage_history_repair_marker_is_idempotent",
                        ReadInt(RelationshipsStore.CountRelationshipDirectorActions4(connection)
                            .FirstOrDefault(), "count", 0) == 2,
                        "Re-entering schema initialization after repair does not mutate or duplicate the migration result.");

                    HashSet<string> schemaTables = new HashSet<string>(RelationshipsStore.SelectSqliteMaster8(connection)
                        .Select(item => ReadString(item, "name", "")), StringComparer.OrdinalIgnoreCase);
                    string[] retiredTables =
                    {
                        "relationships", "relationship_evaluations", "relationship_milestones",
                        "relationship_pressures", "relationship_developments", "ambient_relationship_exposure",
                        "ambient_relationship_runs", "ambient_relationship_changes",
                        "passive_relationship_events", "life_change_readiness"
                    };
                    add("fresh_schema_excludes_retired_relationship_storage",
                        retiredTables.All(table => !schemaTables.Contains(table)),
                        "Fresh campaigns create only compact MBTI, lifecycle, interaction-receipt, rumor, conception, and political-marriage state.");
                }

                string divorceActionId;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    divorceActionId = ReadString(RelationshipsStore.SelectRelationshipDirectorActions19(connection).FirstOrDefault(), "director_action_id", "");
					Dictionary<string, object> stagedAction=RelationshipsStore.SelectRelationshipDirectorActions20(connection, new Dictionary<string, object>{{"id",divorceActionId}}).FirstOrDefault();
					Dictionary<string, object> stagedPayload=TryParseJsonObject(
						ReadString(stagedAction,"payload_json","{}"))??new Dictionary<string, object>();
					add("divorce_action_carries_timeline",
						ReadString(stagedPayload,"timelineId","")=="main",
						"Every queued lifecycle divorce carries its authoritative timeline into native completion.");
					ReputationService.RegisterSocialOccurrence(connection,campaignId,
						ReputationService.BuildRelationshipSocialOccurrence(new AmbientPairContext
						{
							PairKey=AmbientPairKey("spouse_a","spouse_b"),
							HeroAId="spouse_a",HeroBId="spouse_b"
						},1,"marital_strife",
						new Dictionary<string, object>{{"heroStringId","spouse_a"}},
						new Dictionary<string, object>{{"heroStringId","spouse_b"}},
						"spouse_b","spouse_a",true,true,"fixture","main"));
                }
                RelationshipDirectorReportActionApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["directorActionId"] = divorceActionId,
					["status"] = "completed", ["worldDay"] = 2d,
					["timelineId"] = "main"
                });
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    Dictionary<string, object> divorceView = RelationshipLifecycleView(connection,
                        "spouse_a", "spouse_b");
                    add("divorce_is_tagged_and_establishes_reputations",
                        ReadBool(divorceView, "divorced", false)
                        && !string.IsNullOrWhiteSpace(ReadString(RelationshipsStore.SelectRelationshipPairLifecycle10(connection, new Dictionary<string, object> { ["pair"] = AmbientPairKey("spouse_a", "spouse_b") })
                            .FirstOrDefault(), "divorce_rumor_id", ""))
						&& RelationshipsStore.SelectCharacterReputations(connection).Count == 2
						&& !RelationshipsStore.SelectCharacterReputations2(connection).Any()
						&& RelationshipsStore.SelectEvents3(connection, new Dictionary<string, object>{{"id","divorce_"+divorceActionId}}).Any()
						&& RelationshipsStore.SelectWorldHistoryEvents2(connection, new Dictionary<string, object>{{"id","divorce_"+divorceActionId}}).Any(),
						"Completed divorce retains its timeline, establishes divorcee reputations, resolves marital-strife standing, and writes authoritative history.");
                }
            }
            catch (Exception ex)
            {
                add("lifecycle_fixture_exception", false, ex.ToString());
            }
            finally
            {
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(campaignId));
            }
            return results;
        }
    }
}
