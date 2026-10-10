using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunCharacterCommitmentAssertions()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed, object diagnostic = null)
            {
                results.Add(TestDict("caseId", "commitment_performance_" + id, "passed", passed, "summary", id));
                if (!passed && diagnostic != null)
                {
                    string directory = Path.Combine(TestsDir, "commitment-performance-diagnostics");
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, id + ".json"), Json.Serialize(diagnostic));
                }
            }
            const string corein = "I'll give you my word on two of the three. I'll go with you. I'll speak to my father — my mouth, my timing, my framing, the way I already told you. Those I can promise because they're mine to give. The clan-name promise is not mine to give yet.";
            var clauses = ExplicitPromiseClauses(corein);
            Check("corein_two_promises_not_conditional_clan_acceptance", clauses.Count == 2 && clauses[0] == "I'll go with you." && clauses[1].Contains("I'll speak to my father")
                && !clauses.Any(c => c.Contains("clan-name")));
            foreach (string noPromise in new[] { "I will go to town.", "I cannot promise to go.", "I promise nothing.", "If I promise to go, will you come?", "He said I promise to go.", "\"I promise to go.\"", "*I promise to go.*", "I would promise to go.", "I promised to go yesterday." })
                Check("ordinary_hypothetical_report_or_refusal_" + PromptHash(noPromise).Substring(0, 8), ExplicitPromiseClauses(noPromise).Count == 0);
            Check("qualified_promise_stays_qualified", ExplicitPromiseClauses("I promise to go if my father agrees.").Single() == "I promise to go if my father agrees.");
            Check("spoken_word_after_gesture_survives", ExplicitPromiseClauses("*She looks at you.* " + corein).Count == 2);
            Check("next_sentence_condition_is_not_dropped", ExplicitPromiseClauses("I promise to go. But only if my father agrees.").Single() == "I promise to go. But only if my father agrees.");
            Check("conditional_word_is_not_unconditional_acceptance", ExplicitPromiseClauses("I'll give you my word if my father agrees. I'll go with you.").Count == 0);
            foreach (string qualification in new[] { "when my father agrees", "as long as my father agrees", "on condition my father agrees" })
                Check("qualified_word_intro_" + PromptHash(qualification).Substring(0, 8), ExplicitPromiseClauses("I'll give you my word " + qualification + ". I'll go with you.").Count == 0);
            Check("compound_metadata_condition_is_not_split_off", AtomicCommitmentWrites(TestDict("description", "Corein promised to go. Only if her father agrees.")).Count() == 1);
            string cliEvidence = Path.Combine(TestsDir, "cli-report-fixture-" + Guid.NewGuid().ToString("N"));
            try
            {
                string largeDetail = new string('x', 300000);
                var fullCli = TestDict("ok", true, "results", new[] { TestDict("caseId", "retained_large_pass", "passed", true, "actual", largeDetail),
                    TestDict("caseId", "retained_large_failure", "passed", false, "summary", largeDetail) });
                var compactCli = PersistBoundedMotiveCliReport(fullCli, cliEvidence);
                string retainedCli = File.ReadAllText(ReadString(compactCli, "resultsPath", ""));
                Check("large_cli_evidence_retained_without_false_success", !ReadBool(compactCli, "ok", true) && ReadInt(compactCli, "failedCount", 0) == 1
                    && Json.Serialize(compactCli).Length < 8192 && ReadDictionaryList(TryParseJsonObject(retainedCli), "results").Count == 2
                    && ReadString(ReadDictionaryList(TryParseJsonObject(retainedCli), "results")[0], "actual", "") == largeDetail
                    && ReadString(compactCli, "resultsSha256", "") == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ReadString(compactCli, "resultsPath", "")))).ToLowerInvariant());
                var manyFailures = Enumerable.Range(0, 80).Select(index => TestDict("caseId", "failed_" + index, "passed", false, "summary", largeDetail.Substring(0, 10000))).ToList();
                var boundedFailures = PersistBoundedMotiveCliReport(TestDict("ok", false, "results", manyFailures), cliEvidence);
                Check("cli_failure_detail_cap_keeps_exact_failure_count", ReadInt(boundedFailures, "failedCount", 0) == 80
                    && ReadDictionaryList(boundedFailures, "failures").Count == 32 && ReadBool(boundedFailures, "failureDetailsLimited", false)
                    && Json.Serialize(boundedFailures).Length < 32768 && ReadDictionaryList(TryParseJsonObject(File.ReadAllText(ReadString(boundedFailures, "resultsPath", ""))), "results").Count == 80);
            }
            finally { TryDeleteDirectory(cliEvidence); }
            var agencyFacts = TestDict("player.knownTournamentWins", 107);
            string agencyPrompt = ConversationAgencyPrompt(TestDict("facts", agencyFacts));
            Check("negotiation_prompt_rough_but_engine_fact_exact", agencyPrompt.Contains("over a hundred") && !agencyPrompt.Contains("107") && ReadInt(agencyFacts, "player.knownTournamentWins", 0) == 107);
            var context = TestDict("timelineId", "promise_branch", "worldDay", 20d, "playerHeroStringId", "player", "mainHeroStringId", "player");
            var parsed = TestDict("reply", corein, "obligationWrites", new[] { TestDict("description", "Corein gave her word to go with Michael and speak to her father. The clan-name commitment remains conditional on hearing the inner circle answers first.",
                "owed_by", "npc", "owed_to", "player", "status", "unresolved", "sourceEvidence", TestDict("turnId", "current_npc", "speakerId", "npc", "quote", clauses[0] + " " + clauses[1], "evidenceType", "spoken_statement")) });
            var held = QuarantineUncoveredDialogueFacts(parsed, context, new List<Dictionary<string, object>>(), "npc");
            var writes = ReadDictionaryList(parsed, "obligationWrites");
            Check("unsupported_condition_does_not_discard_supported_promises", held.Any(h => ReadString(h, "field", "") == "obligationWrites")
                && writes.Count == 2 && writes.Any(w => ReadString(w, "description", "").Contains("speak to my father"))
                && !writes.Any(w => ReadString(w, "description", "").Contains("answers first")));
            Check("grounded_compound_summary_keeps_independent_promise_scopes", writes.Count == 2
                && writes.Select(w => ReadString(ReadDictionary(w, "sourceEvidence"), "quote", "")).OrderBy(q => q).SequenceEqual(clauses.OrderBy(q => q)));
            var empty = TestDict("reply", corein);
            QuarantineUncoveredDialogueFacts(empty, context, new List<Dictionary<string, object>>(), "npc");
            var fallback = ReadDictionaryList(empty, "obligationWrites");
            Check("explicit_promises_captured_without_model_metadata", fallback.Count == 2);
            var playerPromise = TestDict("reply", "I understand.");
            AddExplicitCommitmentFallback(playerPromise, new Dictionary<string, object>(context) { ["playerText"] = "I promise to wait for you." }, "npc");
            var playerWrite = ReadDictionaryList(playerPromise, "obligationWrites").Single();
            Check("player_owns_their_own_promise", ReadString(playerWrite, "owed_by", "") == "player" && ReadString(ReadDictionary(playerWrite, "sourceEvidence"), "turnId", "") == "current_player");
            AttachConversationWriteEvidence(fallback, CurrentConversationSources("player", "npc", "What can you promise?", corein, "promise_event"), "npc", "promise_event");
            Check("fallback_bound_to_exact_accepted_speaker", fallback.All(w => ReadString(w, "sourceValidation", "") == "supported_span"
                && ReadString(ReadDictionary(w, "sourceEvidence"), "speakerId", "") == "npc"));
            string campaign = "commitment_performance_" + Guid.NewGuid().ToString("N");
            try
            {
                using var connection = OpenCampaignConnection(campaign);
                foreach (var write in PrepareStructuredWrites(fallback, "npc", "player", "", "dialogue", "obligation"))
                {
                    write["timelineId"] = "promise_branch"; write["worldDay"] = 20d; write["created_day"] = 20d;
                    InsertObligation(connection, "promise_event", write, 100, "dialogue");
                    InsertObligation(connection, "promise_event", write, 100, "dialogue");
                }
                Check("accepted_retry_does_not_duplicate_debt", QuerySql(connection, "SELECT * FROM obligations;").Count == 2);
                Check("accepted_promise_persists_canonical_timeline", QuerySql(connection, "SELECT * FROM obligations;").All(row => ReadString(row, "timeline_id", "") == "promise_branch"));
                ExecuteSql(connection, "UPDATE obligations SET timeline_id='main'; DELETE FROM schema_meta WHERE key='character_commitment_timeline_v1';");
                EnsureCharacterCommitmentTimeline(connection);
                EnsureCharacterCommitmentTimeline(connection);
                Check("legacy_supported_default_timeline_repaired_once", QuerySql(connection, "SELECT * FROM obligations;").All(row => ReadString(row, "timeline_id", "") == "promise_branch"));
                foreach (string guard in new[] { "unsupported", "edited", "foreign", "missing" })
                {
                    var guarded = CloneDictionary(fallback[0]);
                    guarded["obligation_id"] = "migration_" + guard; guarded["status"] = "fulfilled"; guarded["timelineId"] = "promise_branch";
                    if (guard == "unsupported") guarded["sourceValidation"] = "incomplete";
                    if (guard == "missing") guarded.Remove("timelineId");
                    InsertObligation(connection, "promise_event", guarded, 100, "dialogue");
                    ExecuteSql(connection, "UPDATE obligations SET timeline_id=$timeline,revision=$revision WHERE obligation_id=$id;",
                        TestDict("id", "migration_" + guard, "timeline", guard == "foreign" ? "other" : "main", "revision", guard == "edited" ? 1 : 0));
                }
                ExecuteSql(connection, "DELETE FROM schema_meta WHERE key='character_commitment_timeline_v1';");
                EnsureCharacterCommitmentTimeline(connection);
                Check("timeline_migration_preserves_unverified_revised_foreign_and_missing_rows", QuerySql(connection, "SELECT * FROM obligations WHERE obligation_id LIKE 'migration_%';")
                    .All(row => ReadString(row, "timeline_id", "") == (ReadString(row, "obligation_id", "") == "migration_foreign" ? "other" : "main")));
                var ids = new List<string>();
                string prompt = BuildCharacterCommitmentPrompt(connection, "npc", "player", context, ids);
                Check("unrelated_topic_keeps_both_protected_promises", prompt.Contains(clauses[0]) && prompt.Contains(clauses[1]) && ids.Count == 2,
                    TestDict("prompt", prompt, "evidence", ReadDictionary(context, "characterCommitmentEvidence"), "rows", QuerySql(connection, "SELECT * FROM obligations;").Select(row =>
                        TestDict("id", ReadString(row, "obligation_id", ""), "payload", TryParseJsonObject(ReadString(row, "payload_json", "{}")),
                            "visible", KnowledgeRowVisibleToNpc("obligations", row, BuildKnowledgeAccessContext("npc", "player", "", context), connection))).ToList(),
                        "eligible", QuerySql(connection, "SELECT obligation_id FROM obligations WHERE " + MemoryEligibilitySql("obligations") + " AND " + MemorySourceTimelineSql("obligations") + ";", TestDict("observer", "npc", "timeline", "promise_branch")).Count));
                Check("joint_prompt_does_not_share_owner_only_commitment", BuildCharacterCommitmentPrompt(connection, "npc", "player", context, new List<string>(), new[] { "stranger" }).Length == 0
                    && BuildCharacterCommitmentPrompt(connection, "npc", "player", context, new List<string>(), new[] { "player" }).Contains(clauses[0]));
                var banterHero = TestDict("heroStringId", "npc", "name", "Corein", "age", 30);
                string banter = BuildBanterPrompt(connection, campaign, new List<Dictionary<string, object>> { banterHero, TestDict("heroStringId", "player", "name", "Michael", "age", 30) },
                    "duty|keeping one's word", TestDict("maxResponses", 2, "maxCharacters", 360), new(), context, false);
                Check("banter_keeps_eligible_commitment_in_required_floor", banter.Contains(clauses[0]) && banter.Contains(clauses[1]));
                string privateBanter = BuildBanterPrompt(connection, campaign, new List<Dictionary<string, object>> { banterHero, TestDict("heroStringId", "stranger", "name", "Another companion", "age", 30) },
                    "duty|keeping one's word", TestDict("maxResponses", 2, "maxCharacters", 360), new(), context, false);
                Check("banter_hides_private_commitment_from_other_npc", !privateBanter.Contains(clauses[0]) && !privateBanter.Contains(clauses[1]));
                Check("unheard_observer_cannot_read_private_promise", BuildCharacterCommitmentPrompt(connection, "stranger", "player", context, new List<string>()).Length == 0);
                Check("future_promise_cannot_leak", BuildCharacterCommitmentPrompt(connection, "npc", "player", new Dictionary<string, object>(context) { ["worldDay"] = 19d }, new List<string>()).Length == 0);
                Check("wrong_timeline_cannot_leak", BuildCharacterCommitmentPrompt(connection, "npc", "player", new Dictionary<string, object>(context) { ["timelineId"] = "other" }, new List<string>()).Length == 0);
                Check("official_channel_preserves_private_firewall", BuildCharacterCommitmentPrompt(connection, "npc", "player", new Dictionary<string, object>(context) { ["officialMemoryFirewall"] = true }, new List<string>()).Length == 0);
                ExecuteSql(connection, "UPDATE obligations SET status='fulfilled' WHERE description=$description;", TestDict("description", clauses[0]));
                string partlyFulfilled = BuildCharacterCommitmentPrompt(connection, "npc", "player", context, new List<string>());
                Check("closing_one_promise_keeps_the_other_active", !partlyFulfilled.Contains(clauses[0]) && partlyFulfilled.Contains(clauses[1]));
                ExecuteSql(connection, "UPDATE obligations SET status='fulfilled';");
                Check("fulfilled_promise_does_not_reappear_active", BuildCharacterCommitmentPrompt(connection, "npc", "player", context, new List<string>()).Length == 0);

                ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('promise_bootstrap',$campaign,'bootstrap_npc','player','open',1,1,'[""player"",""bootstrap_npc""]','{""timelineId"":""promise_branch""}');", TestDict("campaign", campaign));
                var bootstrapContext = new Dictionary<string, object>(context) { ["conversationSessionId"] = "promise_bootstrap", ["participants"] = new[] { "player", "bootstrap_npc" } };
                StoreConversationExchange(campaign, bootstrapContext, "bootstrap_npc", "player", "Michael", "Corein", "What can you promise?", corein, "bootstrap_accepted", 200);
                ExecuteSql(connection, "DELETE FROM schema_meta WHERE key='explicit_accepted_commitment_bootstrap_v1';");
                EnsureLatestAcceptedCommitments(connection);
                EnsureLatestAcceptedCommitments(connection);
                string bootstrapped = BuildCharacterCommitmentPrompt(connection, "bootstrap_npc", "player", bootstrapContext, new List<string>());
                Check("latest_accepted_private_source_bootstrapped_once", bootstrapped.Contains(clauses[0]) && bootstrapped.Contains(clauses[1])
                    && QuerySql(connection, "SELECT * FROM obligations WHERE owed_by='bootstrap_npc';").Count == 2,
                    TestDict("prompt", bootstrapped, "rows", QuerySql(connection, "SELECT * FROM obligations WHERE owed_by='bootstrap_npc';")));

                EnsureWorldHistorySchema(connection);
                EnsurePlayerPerformanceSchema(connection);
                var publicRule = new[] { TestDict("audienceType", "global", "audienceId", "all", "availableDay", 20d) };
                var events = new List<Dictionary<string, object>>();
                void Receipt(string id, string kind, string outcome, string type, string competition, string timeline = "promise_branch", double day = 20d, object rules = null)
                    => events.Add(TestDict("eventId", id, "timeline", timeline, "worldDay", day, "sequence", events.Count + 1,
                        "eventType", kind, "phase", "completed", "source", "bannerlord_native", "correlationId", competition,
                        "disseminationClass", rules == null ? "major_world" : "participant_only", "isComplete", true,
                        "summary", "A documented native fixture result.", "payload", TestDict("battleType", type, "winningSide", outcome == "win" ? "Attacker" : "Defender"),
                        "entities", new[] { TestDict("entityId", "player", "entityType", "hero", "role", "participant attacker " + (outcome == "win" ? "winner" : "loser"), "side", "Attacker", "isPlayer", true) },
                        "knowledgeRules", rules ?? publicRule));
                for (int i = 0; i < 107; i++) Receipt("win_" + i, "battle_completed", "win", i % 2 == 0 ? "FieldBattle" : "NavalBattle", "battle_" + i);
                Receipt("duplicate_win", "battle_completed", "win", "FieldBattle", "battle_0");
                Receipt("loss", "battle_completed", "loss", "Siege", "loss");
                Receipt("team", "tournament_finished", "win", "", "tournament");
                Receipt("team_retry", "tournament_finished", "win", "", "tournament");
                Receipt("wrong_branch", "battle_completed", "win", "", "wrong", "other");
                Receipt("future", "battle_completed", "win", "", "future", day: 21d);
                Receipt("private", "battle_completed", "win", "", "private", rules: new[] { TestDict("audienceType", "entity", "audienceId", "other", "availableDay", 20d) });
                foreach (var group in events.GroupBy(e => ReadString(e, "timeline", "")))
                {
                    var receipt = WorldHistoryIngestBatchApi(TestDict("campaignId", campaign, "timelineId", group.Key, "events", group.ToList()));
                    Check("native_ingest_" + group.Key, ReadBool(receipt, "ok", false) && ReadInt(receipt, "accepted", 0) == group.Count());
                    var replay = WorldHistoryIngestBatchApi(TestDict("campaignId", campaign, "timelineId", group.Key, "events", group.ToList()));
                    Check("native_replay_" + group.Key, ReadInt(replay, "accepted", 0) == 0);
                }
                var access = BuildKnowledgeAccessContext("npc", "player", "", context);
                var counts = ReadPlayerPerformanceCounts(connection, "promise_branch", "player", 20d, access);
                Check("all_types_counted_independently_of_examples", ReadInt(counts, "battleWins", 0) == 107 && ReadInt(counts, "battleLosses", 0) == 1
                    && ReadDictionary(counts, "battleTypes").Count == 3);
                Check("team_tournament_count_once", ReadInt(counts, "tournamentWins", 0) == 1);
                Check("private_counts_stay_engine_only", ReadInt(ReadPlayerPerformanceCounts(connection, "promise_branch", "player", 20d), "battleWins", 0) == 108);
                var privateAccess = BuildKnowledgeAccessContext("other", "player", "", context);
                Check("official_performance_does_not_include_private_outcome", ReadInt(ReadPlayerPerformanceCounts(connection, "promise_branch", "player", 20d, privateAccess, publicOnly: true), "battleWins", 0) == 107);
                Check("joint_performance_counts_use_audience_intersection", ReadInt(ReadPlayerPerformanceCounts(connection, "promise_branch", "player", 20d, privateAccess), "battleWins", 0) == 108
                    && ReadInt(ReadPlayerPerformanceCounts(connection, "promise_branch", "player", 20d, privateAccess, new[] { access }), "battleWins", 0) == 107);
                Check("unknown_identity_hides_record", BuildPlayerPerformancePrompt(connection, "npc", context, new List<string>()).Length == 0);
                var known = new Dictionary<string, object>(context) { ["identityView"] = TestDict("canonicalNameAllowed", true) };
                string record = BuildPlayerPerformancePrompt(connection, "npc", known, new List<string>());
                Check("joint_performance_requires_every_observer_to_know_player", BuildPlayerPerformancePrompt(connection, "npc", known, new List<string>(), new[] { "stranger" }).Length == 0);
                Check("known_player_rough_record_not_exact_tally", record.Contains("over a hundred") && !record.Contains("107") && record.Contains("battle defeats") && record.Contains("winning teams"));
                known["identityView"] = TestDict("canonicalNameAllowed", false, "knowsIdentity", true);
                Check("unverified_claim_does_not_reveal_canonical_achievements", BuildPlayerPerformancePrompt(connection, "npc", known, new List<string>()).Length == 0);
                Check("draw_unknown_side_not_a_defeat", NativePerformanceOutcome("battle_completed", TestDict("winningSide", "None"), TestDict("role", "participant attacker loser", "side", "Attacker")) == "");
                Check("witness_or_prisoner_is_not_a_combatant", NativePerformanceOutcome("battle_completed", TestDict("winningSide", "Attacker"), TestDict("role", "spectator", "side", "Attacker")) == ""
                    && NativePerformanceOutcome("battle_completed", TestDict("winningSide", "Attacker"), TestDict("role", "prisoner", "side", "Attacker")) == "");
                Check("retreat_native_result_counts_by_side", NativePerformanceOutcome("battle_completed", TestDict("winningSide", "Defender", "endedByRetreat", true), TestDict("side", "Attacker")) == "loss");
                Check("team_winner_not_merely_champion", NativePerformanceOutcome("tournament_finished", TestDict(), TestDict("role", "tournament winner teammate actor")) == "win");
                Check("duel_native_ids_not_prose", NativePerformanceOutcome("social_outcome", TestDict("sourceEventId", "duel|action|winner", "winnerHeroId", "player", "loserHeroId", "npc"), TestDict("entity_id", "player")) == "win"
                    && NativePerformanceOutcome("social_outcome", TestDict("sourceEventId", "rumor|duel", "winnerHeroId", "player", "loserHeroId", "npc"), TestDict("entity_id", "player")) == "");
                EnsureSocialReputationSchema(connection);
                int rumorsBeforePractice = QuerySql(connection, "SELECT * FROM rumor_occurrences;").Count;
                int reputationsBeforePractice = QuerySql(connection, "SELECT * FROM character_reputations;").Count;
                foreach (bool playerWon in new[] { true, false })
                {
                    string action = "private_practice_" + playerWon;
                    string summary = playerWon ? "Michael bested Corein in a private practice duel." : "Corein bested Michael in a private practice duel.";
                    var practice = TestDict("campaignId", campaign, "timelineId", "promise_branch", "worldDay", 20d,
                        "actionId", action, "type", "RegularDuelPlayer", "status", "completed", "message", summary,
                        "playerHeroStringId", "player", "actorHeroStringId", "npc", "targetHeroStringId", "player",
                        "result", TestDict("success", true, "completed", true, "resultCode", "practice_duel_completed", "message", summary));
                    WriteActionQueueUnlocked(campaign, new List<Dictionary<string, object>> {
                        TestDict("id", action, "status", "delivered", "record", CloneDictionary(practice)) });
                    var pending = CloneDictionary(practice); pending["status"] = "executing";
                    pending["message"] = "Practice duel is in progress."; pending["result"] = TestDict("resultCode", "conversation_duel_armed");
                    StoreActionResultMemory(pending, "fixture");
                    var report = ReportAction(practice);
                    var replay = ReportAction(practice);
                    var memories = QuerySql(connection, "SELECT * FROM memories WHERE event_id=$event;", TestDict("event", "action_" + action));
                    var evt = QuerySql(connection, "SELECT * FROM events WHERE event_id=$event;", TestDict("event", "action_" + action)).Single();
                    Check("practice_remembers_result_for_opponent_" + playerWon, ReadBool(report, "ok", false) && ReadBool(report, "found", false)
                        && ReadBool(ReadDictionary(report, "memoryStore"), "ok", false) && ReadBool(replay, "ok", false)
                        && memories.Count == 1 && ReadString(memories[0], "owner_id", "") == "npc"
                        && ReadString(memories[0], "summary", "") == summary && ReadString(evt, "summary", "") == summary,
                        TestDict("report", report, "memories", memories, "event", evt));
                    Check("practice_memory_is_private_and_replay_safe_" + playerWon, ReadString(evt, "visibility", "") == "private"
                        && TextListFromJson(ReadString(evt, "known_by_json", "[]")).SequenceEqual(new[] { "npc" })
                        && KnowledgeRowVisibleToNpc("events", evt, access, connection)
                        && !KnowledgeRowVisibleToNpc("events", evt, privateAccess, connection)
                        && ReadDouble(evt, "world_day", 0d) == 20d, evt);
                    string receipt = "action_receipt_" + PromptHash("promise_branch|" + action + "|completed").Substring(0, 32);
                    var observers = QuerySql(connection, "SELECT * FROM shared_activity_observers WHERE activity_id=$id;", TestDict("id", receipt));
                    Check("practice_action_receipt_has_no_other_npc_" + playerWon,
                        QuerySql(connection, "SELECT * FROM shared_activity_events WHERE activity_id=$id;", TestDict("id", receipt)).Count == 1
                        && observers.Any(row => ReadString(row, "owner_id", "") == "npc")
                        && observers.All(row => new[] { "npc", "player" }.Contains(ReadString(row, "owner_id", ""))), observers);
                    Check("practice_has_no_automatic_relationship_effect_" + playerWon,
                        ReadDictionaryList(report, "nativeRelationChanges").Count == 0
                        && EvaluateCompletedActionRelationships(practice).Count == 0);
                    Check("practice_creates_no_public_or_social_history_" + playerWon,
                        QuerySql(connection, "SELECT * FROM world_history_events WHERE correlation_id=$action;", TestDict("action", action)).Count == 0
                        && QuerySql(connection, "SELECT * FROM character_performance_events WHERE event_id=$event;", TestDict("event", "action_" + action)).Count == 0);
                    var malformed = CloneDictionary(practice); malformed["actionId"] = action + "_missing"; malformed.Remove("actorHeroStringId");
                    Check("practice_missing_identity_never_becomes_public_" + playerWon,
                        !ReadBool(StoreActionResultMemory(malformed, "fixture"), "ok", true)
                        && QuerySql(connection, "SELECT * FROM events WHERE event_id=$event;", TestDict("event", "action_" + action + "_missing")).Count == 0);
                }
                Check("practice_leaves_rumors_and_reputations_unchanged",
                    QuerySql(connection, "SELECT * FROM rumor_occurrences;").Count == rumorsBeforePractice
                    && QuerySql(connection, "SELECT * FROM character_reputations;").Count == reputationsBeforePractice);
                Check("practice_report_detection_preserves_other_duels", !IsPracticeDuelActionReport(TestDict("type", "RegularDuelPlayer",
                    "result", TestDict("resultCode", "training_duel_completed")))
                    && !IsPracticeDuelActionReport(TestDict("type", "RegularDuelPlayer", "result", TestDict("resultCode", "lethal_duel_completed")))
                    && !IsPracticeDuelActionReport(TestDict("type", "give_gold_to_player", "result", TestDict("resultCode", "practice_duel_completed"))));
                foreach (string entity in new[] { "player", "npc" })
                    Check("practice_excluded_from_public_performance_" + entity, NativePerformanceOutcome("social_outcome",
                        TestDict("sourceEventId", "duel|practice|winner", "duelMode", "practice", "winnerHeroId", "player", "loserHeroId", "npc"), TestDict("entity_id", entity)) == "");
                Check("practice_inference_and_violence_gate", InferDuelModeFromDirective("Let us have a practice duel.") == "practice"
                    && InferDuelModeFromDirective("Would you like some sparring?") == "practice"
                    && InferDuelModeFromDirective("Let's spar.") == "practice"
                    && InferDuelModeFromDirective("A sparse crowd watched the duel.") != "practice" && !IsLethalDuelMode("practice"));
                Check("one_and_zero_are_not_false_lifetime_claims", RoughPerformanceCount(0) == "no documented results" && RoughPerformanceCount(1) == "one recorded result");
                Check("hundred_boundary_conservative", RoughPerformanceCount(100) == "around a hundred" && RoughPerformanceCount(101) == "over a hundred" && RoughPerformanceCount(200) == "hundreds");
                ExecuteSql(connection, "DELETE FROM world_history_events;");
                Check("retained_receipts_survive_lossy_history", ReadInt(ReadPlayerPerformanceCounts(connection, "promise_branch", "player", 20d, access), "battleWins", 0) == 107);
                string generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                CaptureRestoredMemorySources(connection, TestDict("savePointId", "fixture_point", "timelineId", "promise_branch", "campaignTimeDays", 20d, "worldHistorySequence", 1L, "worldHistoryHeadEventId", "fixture_head"), generation);
                BindRecoveredMemorySources(connection, "promise_branch", "recovered", 1L, "fixture_head");
                Check("verified_restored_receipts_follow_recovered_branch", ReadInt(ReadPlayerPerformanceCounts(connection, "recovered", "player", 20d, access), "battleWins", 0) == 107);
                ExecuteSql(connection, "UPDATE character_performance_events SET source_hash='changed' WHERE competition_id='battle_0';");
                Check("changed_or_foreign_receipt_cannot_borrow_snapshot_proof", ReadInt(ReadPlayerPerformanceCounts(connection, "recovered", "player", 20d, access), "battleWins", 0) == 106
                    && ReadInt(ReadPlayerPerformanceCounts(connection, "unrelated_branch", "player", 20d, access), "battleWins", 0) == 0);
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            results.AddRange(RunCommitmentSourceContextAssertions());
            return results;
        }
        private static List<Dictionary<string, object>> RunCommitmentSourceContextAssertions()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed) => results.Add(TestDict("caseId", "commitment_source_context_" + id, "passed", passed, "summary", id));
            string campaign = "commitment_source_context_" + Guid.NewGuid().ToString("N");
            try
            {
                using var c = OpenCampaignConnection(campaign);
                var context = TestDict("campaignId", campaign, "timelineId", "source_branch", "worldDay", 30d,
                    "playerHeroStringId", "player", "playerText", "What did you promise about your father?", "participants", new[] { "npc", "player" });
                const string originalQuestion = "Will you speak to your father? You do not need to promise to join my clan.";
                const string promise = "I promise to speak to my father. But only after you have explained the proposal.";
                Dictionary<string, object> Save(string id, string question, string reply, double day = 20d, string obligation = "", object participants = null, string timeline = "source_branch")
                {
                    var p = CloneDictionary(context); p["worldDay"] = day;
                    p["timelineId"] = timeline;
                    if (participants != null) p["participants"] = participants;
                    StoreConversationExchange(campaign, p, "npc", "player", "Michael", "Corein", question, reply, id, (long)(day * 10));
                    var write = TestDict("description", ExplicitPromiseClauses(reply).First(), "owed_by", "npc", "owed_to", "player",
                        "status", "active", "sourceEvidence", TestDict("turnId", "current_npc", "speakerId", "npc", "quote", ExplicitPromiseClauses(reply).First()));
                    AttachConversationWriteEvidence(new[] { write }, CurrentConversationSources("player", "npc", question, reply, id), "npc", id);
                    write = PrepareStructuredWrites(new[] { write }.ToList(), "npc", "player", "", "dialogue", "obligation").Single();
                    write["timelineId"] = timeline; write["worldDay"] = day; write["created_day"] = day;
                    if (obligation.Length > 0) write["obligation_id"] = obligation;
                    InsertObligation(c, id, write, (long)(day * 10), "dialogue");
                    return QuerySql(c, "SELECT * FROM obligations WHERE event_id=$event;", TestDict("event", id)).Single();
                }
                var first = Save("original", originalQuestion, promise);
                string obligationId = ReadString(first, "obligation_id", "");
                string Build(Dictionary<string, object> p = null, params string[] audience)
                    => BuildCharacterCommitmentPrompt(c, "npc", "player", p ?? context, new List<string>(), audience);
                string expanded = Build();
                Check("relevant_source_keeps_question_and_qualified_reply", expanded.Contains(originalQuestion) && expanded.Contains(promise)
                    && expanded.Contains("session=") && expanded.Contains("turn_id=original_player") && expanded.Contains("turn_id=original_npc"));
                Check("current_source_is_deduplicated", Regex.Matches(expanded, "kind=commitment_source_context ").Count == 1);
                Check("source_context_is_protected", expanded.Contains("priority=120 required=true"));
                var storedPayload = TryParseJsonObject(ReadString(first, "payload_json", "{}"));
                foreach (string legacyShape in new[] { "missing", "null", "non_dictionary" })
                {
                    var legacyPayload = CloneDictionary(storedPayload);
                    if (legacyShape == "missing") legacyPayload.Remove("commitmentOriginalSource");
                    else legacyPayload["commitmentOriginalSource"] = legacyShape == "null" ? null : "legacy value";
                    ExecuteSql(c, "UPDATE obligations SET payload_json=$payload WHERE obligation_id=$id;",
                        TestDict("payload", Json.Serialize(legacyPayload), "id", obligationId));
                    string legacyPrompt = Build();
                    Check("legacy_original_" + legacyShape + "_uses_verified_current_source",
                        legacyPrompt.Contains(originalQuestion) && legacyPrompt.Contains(promise)
                        && Regex.Matches(legacyPrompt, "kind=commitment_source_context ").Count == 1);
                }
                var legacyUpdate = CloneDictionary(storedPayload);
                legacyUpdate["timelineId"] = "source_branch"; legacyUpdate["owed_by"] = "npc"; legacyUpdate["owed_to"] = "player";
                PreserveCommitmentOriginalSource(c, obligationId, "legacy_update", legacyUpdate);
                Check("legacy_update_preserves_verified_original_source", Json.Serialize(ReadDictionary(legacyUpdate, "commitmentOriginalSource"))
                    == Json.Serialize(ReadDictionary(storedPayload, "sourceEvidence")));
                var unreferenced = TestDict("obligation_id", "legacy_without_references", "description", "I promise to speak to my father.",
                    "payload_json", "{\"commitmentOriginalSource\":null,\"sourceEvidence\":null}");
                var unreferencedSources = new List<string>();
                string unreferencedContext = BuildRelevantCommitmentContext(c, new List<Dictionary<string, object>> { unreferenced },
                    context, new List<KnowledgeAccessContext> { BuildKnowledgeAccessContext("npc", "player", "", context) },
                    unreferencedSources, new List<Dictionary<string, object>>());
                Check("missing_both_references_does_not_invent_source_context", unreferencedContext.Length == 0 && unreferencedSources.Count == 0);
                ExecuteSql(c, "UPDATE obligations SET payload_json=$payload WHERE obligation_id=$id;",
                    TestDict("payload", Json.Serialize(storedPayload), "id", obligationId));
                string unrelated = Build(new Dictionary<string, object>(context) { ["playerText"] = "The weather is pleasant today." });
                Check("unrelated_topic_keeps_ledger_without_expansion", unrelated.Contains(promise) && !unrelated.Contains(originalQuestion));
                var topic = new Dictionary<string, object>(context) { ["playerText"] = "", ["memoryActiveTopic"] = "father" };
                Check("active_topic_can_follow_exact_source", Build(topic).Contains(originalQuestion));
                Check("keeping_your_word_can_follow_exact_source", Build(new Dictionary<string, object>(context) { ["playerText"] = "Are you keeping your word?" }).Contains(originalQuestion));
                var forged = TryParseJsonObject(ReadString(first, "payload_json", "{}"));
                forged["commitmentOriginalSource"] = TestDict("sourceEventId", "forged", "quote", "an invented unrelated conversation");
                InsertObligation(c, "original", forged, 200, "dialogue");
                Check("model_cannot_replace_original_source_reference", ReadString(ReadDictionary(TryParseJsonObject(ReadString(QuerySql(c,
                    "SELECT * FROM obligations WHERE obligation_id=$id;", TestDict("id", obligationId)).Single(), "payload_json", "{}")), "commitmentOriginalSource"), "sourceEventId", "") == "original");
                const string revised = "I promise to speak to my father after the harvest instead.";
                Save("revision", "Please wait until the harvest before speaking to your father.", revised, 21d, obligationId);
                string revision = Build();
                Check("revision_keeps_original_and_latest_exchange", revision.Contains(originalQuestion) && revision.Contains(promise)
                    && revision.Contains("turn_id=revision_npc") && revision.Contains(revised) && revision.Contains("current ledger controls status"));
                Check("revision_retains_first_source_reference", ReadString(ReadDictionary(TryParseJsonObject(ReadString(QuerySql(c,
                    "SELECT * FROM obligations WHERE obligation_id=$id;", TestDict("id", obligationId)).Single(), "payload_json", "{}")), "commitmentOriginalSource"), "sourceEventId", "") == "original");
                Check("future_source_cannot_leak", !Build(new Dictionary<string, object>(context) { ["worldDay"] = 20d }).Contains(revised));
                Check("sequence_cutoff_cannot_leak", !Build(new Dictionary<string, object>(context) { ["memoryAsOfAcceptedSequence"] = 0L }).Contains(originalQuestion));
                Check("timestamp_cutoff_cannot_leak", !Build(new Dictionary<string, object>(context) { ["memoryAsOfTimestamp"] = 199L }).Contains(originalQuestion));
                Check("official_channel_does_not_expand_private_source", Build(new Dictionary<string, object>(context) { ["officialMemoryFirewall"] = true }).Length == 0);
                Check("new_observer_has_no_private_context", Build(null, "stranger").Length == 0);
                ExecuteSql(c, "UPDATE conversation_turns SET text='Changed accepted wording.' WHERE turn_id='original_npc';");
                string changed = Build();
                Check("changed_hash_rejects_original_keeps_current", !changed.Contains(originalQuestion) && changed.Contains(revised));
                ExecuteSql(c, "UPDATE conversation_turns SET text=$text WHERE turn_id='original_npc';", TestDict("text", promise));
                var beforeRead = QuerySql(c, "SELECT turn_id,text,payload_json FROM conversation_turns ORDER BY turn_id;");
                Build();
                Check("reading_context_preserves_transcript_bytes", Json.Serialize(beforeRead) == Json.Serialize(QuerySql(c, "SELECT turn_id,text,payload_json FROM conversation_turns ORDER BY turn_id;")));
                var source = QuerySql(c, "SELECT * FROM conversation_turns WHERE turn_id='revision_npc';").Single();
                InsertConversationTurn(c, "ambiguous_revision", ReadString(source, "session_id", ""), "revision", 99,
                    ReadString(source, "exchange_id", ""), "npc", "npc", "Corein", revised, "in_person", 21d, 210,
                    "", new List<string> { "npc", "player" }, new Dictionary<string, object>(context) { ["worldDay"] = 21d });
                string ambiguous = Build();
                Check("ambiguous_exact_sources_do_not_become_invented_context", !ambiguous.Contains("turn_id=revision_npc") && ambiguous.Contains(revised));
                ExecuteSql(c, "DELETE FROM conversation_turns WHERE event_id IN ('original','revision');");
                string missing = Build();
                Check("missing_originals_keep_protected_ledger_without_reconstruction", missing.Contains(revised) && !missing.Contains("EXACT ACCEPTED COMMITMENT CONTEXT"));
                ExecuteSql(c, "UPDATE obligations SET status='fulfilled';");
                Check("closed_commitment_does_not_renew_from_old_context", Build().Length == 0);

                const string shared = "I promise to repair the wagon tomorrow. *I privately remember SECRET-NARRATION-991.*";
                Save("shared", "Please repair the wagon.", shared, participants: new[] { "npc", "player", "friend" });
                var sharedRow = QuerySql(c, "SELECT * FROM obligations WHERE event_id='shared';").Single();
                var sharedValue = TryParseJsonObject(ReadString(sharedRow, "payload_json", "{}"));
                sharedValue["known_by"] = new[] { "npc", "player", "friend" };
                ExecuteSql(c, "UPDATE obligations SET known_by_json=$known,payload_json=$payload WHERE event_id='shared';",
                    TestDict("known", Json.Serialize(new[] { "npc", "player", "friend" }), "payload", Json.Serialize(sharedValue)));
                var sharedContext = new Dictionary<string, object>(context) { ["playerText"] = "How about your promise to repair the wagon?" };
                string joint = Build(sharedContext, "friend");
                Check("joint_source_keeps_common_spoken_context", joint.Contains("Please repair the wagon.") && joint.Contains("turn_id=shared_npc"));
                Check("joint_source_excludes_speakers_private_narration", !joint.Contains("SECRET-NARRATION-991"));
                string banter = BuildBanterPrompt(c, campaign, new List<Dictionary<string, object>> {
                    TestDict("heroStringId", "npc", "name", "Corein", "age", 30), TestDict("heroStringId", "friend", "name", "Companion", "age", 30) },
                    "duty|keeping one's word", TestDict("maxResponses", 2, "maxCharacters", 360), new(),
                    new Dictionary<string, object>(sharedContext) { ["playerText"] = "" }, false);
                Check("banter_active_topic_follows_common_source_without_private_thoughts", banter.Contains("Please repair the wagon.") && !banter.Contains("SECRET-NARRATION-991"));
                ExecuteSql(c, "UPDATE conversation_turns SET participants_json='[\"npc\",\"player\"]' WHERE turn_id='shared_player';");
                Check("known_promise_does_not_grant_private_question_access", !Build(sharedContext, "friend").Contains("Please repair the wagon."));
                ExecuteSql(c, "UPDATE conversation_turns SET channel='letter' WHERE event_id='shared';");
                Check("letter_without_delivery_receipt_does_not_expand", !Build(sharedContext).Contains("EXACT ACCEPTED COMMITMENT CONTEXT"));
                ExecuteSql(c, "UPDATE obligations SET status='fulfilled';");

                Save("oversized", "Explain the archive promise.", "I promise to maintain the archive. " + new string('x', 7000) + " END-OF-LONG-EXCHANGE");
                string oversized = Build(new Dictionary<string, object>(context) { ["playerText"] = "What did you promise about the archive?" });
                Check("oversized_exchange_is_not_cut_into_misleading_excerpt", oversized.Contains("I promise to maintain the archive.")
                    && !oversized.Contains("EXACT ACCEPTED COMMITMENT CONTEXT") && !oversized.Contains("END-OF-LONG-EXCHANGE"));
                ExecuteSql(c, "UPDATE obligations SET status='fulfilled';");
                for (int i = 0; i < 5; i++) Save("bounded_" + i, "Will you pay the ransom for prisoner " + i + "?", "I promise to pay the ransom for prisoner " + i + ".");
                var bounded = new Dictionary<string, object>(context) { ["playerText"] = "What are your promises about ransom?" };
                string boundedText = Build(bounded);
                Check("context_expansion_bounded_but_all_ledgers_retained", Regex.Matches(boundedText, "kind=commitment_source_context ").Count == 3
                    && ReadInt(ReadDictionary(bounded, "characterCommitmentEvidence"), "activeCount", 0) == 5);
                string restoredTurn = Regex.Match(boundedText, @"turn_id=(bounded_\d+_npc)").Groups[1].Value;
                string generation = ReadString(ReadMemoryPrecisionState(c), "restore_generation", "");
                CaptureRestoredMemorySources(c, TestDict("savePointId", "context_point", "timelineId", "source_branch", "campaignTimeDays", 30d,
                    "worldHistorySequence", 1L, "worldHistoryHeadEventId", "context_head"), generation);
                BindRecoveredMemorySources(c, "source_branch", "recovered", 1L, "context_head");
                var recovered = new Dictionary<string, object>(bounded) { ["timelineId"] = "recovered" };
                Check("verified_restore_carries_exact_source_context", restoredTurn.Length > 0 && Build(recovered).Contains("turn_id=" + restoredTurn));
                string originalEvent = restoredTurn.Replace("_npc", "");
                var restoredPromise = QuerySql(c, "SELECT * FROM obligations WHERE event_id=$event;", TestDict("event", originalEvent)).Single();
                Save("recovered_revision", "Please pay this ransom after the harvest.", "I promise to pay this ransom after the harvest.",
                    25d, ReadString(restoredPromise, "obligation_id", ""), timeline: "recovered");
                Check("recovered_revision_retains_verified_original", Build(recovered).Contains("turn_id=" + restoredTurn)
                    && Build(recovered).Contains("turn_id=recovered_revision_npc"));
                ExecuteSql(c, "UPDATE conversation_turns SET text='tampered source' WHERE turn_id=$turn;", TestDict("turn", restoredTurn));
                Check("tampered_restored_source_cannot_expand", restoredTurn.Length > 0 && !Build(recovered).Contains("turn_id=" + restoredTurn));
                Check("abandoned_branch_cannot_expand", Build(new Dictionary<string, object>(bounded) { ["timelineId"] = "unrelated" }).Length == 0);
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
