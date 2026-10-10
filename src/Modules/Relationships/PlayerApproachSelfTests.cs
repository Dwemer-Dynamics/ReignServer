using System;
using System.Collections.Generic;
using System.Linq;
using ReignBeta.Shared;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunPlayerApproachSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed, string detail) => results.Add(TestDict("ok", true, "passed", passed,
                "suite", "relationship_system", "caseId", "player_approach_" + id, "name", id, "summary", detail, "durationMs", 0));
            var defaults = ReadPlayerApproachOptions(TestDict());
            Check("options_legacy_defaults", defaults.TrackAttention && new[] { "shared", "keep", "patrol", "battle_rescue", "prison_rescue" }.All(defaults.Allows),
                "Old settings without any approach fields retain every enabled feature.");
            bool strictBooleans = true;
            foreach (string key in PlayerApproachOptions.SettingKeys)
                foreach (object invalid in new object[] { "false", 0, 1, null, TestDict() })
                    strictBooleans &= ValidatePlayerApproachOptions(TestDict(key, invalid)).Length > 0;
            Check("options_strict_boolean_validation", strictBooleans && ValidatePlayerApproachOptions(TestDict(PlayerApproachOptions.MasterKey, false)).Length == 0,
                "Every supplied switch requires a real JSON boolean; partial updates remain valid.");
            var savedOptions = DefaultSettings();
            MergeProviderSettings(savedOptions, TestDict(PlayerApproachOptions.JealousyKey, false, PlayerApproachOptions.PatrolKey, false));
            MergeProviderSettings(savedOptions, TestDict(PlayerApproachOptions.MasterKey, false));
            var publicOptions = PlayerApproachOptionsForClient(TryParseJsonObject(Json.Serialize(savedOptions)));
            MergeProviderSettings(savedOptions, TestDict(PlayerApproachOptions.MasterKey, true));
            var resumed = ReadPlayerApproachOptions(savedOptions);
            Check("options_partial_save_roundtrip", publicOptions.Count == 7 && publicOptions.Values.All(value => value is bool)
                && !ReadBool(publicOptions, PlayerApproachOptions.MasterKey, true) && !ReadBool(publicOptions, PlayerApproachOptions.JealousyKey, true)
                && !resumed.Allows("shared") && !resumed.Allows("patrol") && resumed.Allows("keep") && resumed.TrackAttention,
                "Partial merges and JSON persistence preserve independent child choices; the public projection contains only seven booleans.");
            Check("independent_rivals", PlayerApproachPolicy.NeglectScore(new[] { 4, 4 }, 50, 50) == 5
                && PlayerApproachPolicy.NeglectScore(new[] { 8 }, 50, 50) == 8,
                "Four witnessed days for each of two people cause moderate neglect; eight for one cause strong neglect.");
            Check("traits_and_bounds", PlayerApproachPolicy.SharedDayInterval(100, 0) == 2
                && PlayerApproachPolicy.SharedDayInterval(0, 100) == 10
                && PlayerApproachPolicy.NeglectRefusalPenalty(4, 100, 0) == 8
                && PlayerApproachPolicy.NeglectRefusalPenalty(1, 0, 100) == 1
                && PlayerApproachPolicy.NeglectApproachChance(100, 0, 100, 4, 100) == 80
                && PlayerApproachPolicy.NeglectApproachChance(100, 0, 100, 0, 100) == 0, "Trait extremes, zero neglect and mechanical caps are enforced.");
            Check("keep_and_patrol_percentages", PlayerApproachPolicy.KeepApproachChance(19, 0) == 0
                && PlayerApproachPolicy.KeepApproachChance(19, 250) == 4
                && PlayerApproachPolicy.KeepApproachChance(95, 9999) == 100
                && new[] { 19, 20, 50, 100, 200 }.Select(PlayerApproachPolicy.PatrolApproachChance).SequenceEqual(new[] { 5, 10, 15, 20, 25 })
                && PlayerApproachPolicy.PassingPenalty(100, 100, 0) == 5, "Stranger renown, threshold boundaries, patrol party size and passing penalties use the approved policy.");
            var brief = TestDict("severityLevel", 2, "primaryRival", TestDict("heroId", "rival", "name", "Caladog"),
                "rivals", new[] { TestDict("heroId", "rival", "name", "Caladog", "days", 4) });
            var opening = TestDict("authoritativeApproachOpening", true, "authoritativeApproachBrief", brief);
            var valid = TestDict("reply", "You have been giving Caladog your time while overlooking me. I miss your company.",
                "approachOpeningEvidence", TestDict("primaryRivalId", "rival", "severityLevel", 2, "rivalDays", TestDict("rival", 4)));
            Check("opening_contract", ApproachOpeningViolations(valid, opening).Count == 0
                && ApproachOpeningViolations(TestDict("reply", "Greetings. What do you need?"), opening).Count > 0,
                "A generic greeting cannot satisfy an NPC-first jealousy invitation; the primary rival and correct severity are required.");
            var wrong = CloneDictionary(valid); wrong["approachOpeningEvidence"] = TestDict("primaryRivalId", "rival", "severityLevel", 4, "rivalDays", TestDict("rival", 8));
            Check("wrong_counts_and_severity", ApproachOpeningViolations(wrong, opening).Count >= 2, "Wrong severity and pooled/unsupported rival counts fail independently.");
            var wrongSpokenDays = CloneDictionary(valid); wrongSpokenDays["reply"] = "You gave Caladog eight days of attention and overlooked me.";
            Check("spoken_days_not_hidden_by_proof", ApproachOpeningViolations(wrongSpokenDays, opening).Count > 0,
                "Correct hidden evidence cannot authorize a different day count in spoken dialogue.");
            var politicalOpening = CloneDictionary(opening); politicalOpening["authoritativeApproachKind"] = "keep";
            Check("political_approach_retains_its_reason", ApproachOpeningViolations(TestDict("reply", "Your recent standing has caught my interest. May we discuss supporting my cause?"), politicalOpening).Count == 0,
                "A keep invitation keeps its political purpose even if that noble has also witnessed neglected attention.");
            int repairCalls = 0;
            var repaired = EnforceApproachOpening(TestDict("ok", true, "content", Json.Serialize(TestDict("reply", "Hello."))), TestDict(), opening,
                _ => { repairCalls++; return TestDict("ok", true, "content", Json.Serialize(valid)); });
            var failed = EnforceApproachOpening(TestDict("ok", true, "content", Json.Serialize(TestDict("reply", "Hello."))), TestDict(), opening,
                _ => TestDict("ok", true, "content", "{\"reply\":\"Hello again.\"}"));
            Check("bounded_repair", repairCalls == 1 && ReadBool(repaired, "ok", false) && !ReadBool(failed, "ok", true),
                "One isolated repair can recover an invalid opening; exhausted failure publishes no speech and applies no contact reset.");
            var rescue = TestDict("rescueOpening", true, "rescueContext", TestDict("eventId", "rescued", "kind", "prison_rescue"));
            Check("rescue_specific_opening", ApproachOpeningViolations(TestDict("reply", "Thank you for helping me escape that prison.",
                "rescueOpeningEvidence", TestDict("eventId", "rescued", "kind", "prison_rescue")), rescue).Count == 0
                && ApproachOpeningViolations(TestDict("reply", "Thank you for winning the battle.", "rescueOpeningEvidence", TestDict("eventId", "rescued", "kind", "battle_rescue")), rescue).Count > 0,
                "Prison rescue cannot be described as battle help or bind another event.");
            string campaign = "__player_approach_test_" + Guid.NewGuid().ToString("N");
            try
            {
                var people = new[] { TestDict("heroId", "npc", "alive", true, "scopes", new[] { "settlement:town" }),
                    TestDict("heroId", "observer", "alive", true, "scopes", new[] { "settlement:town" }),
                    TestDict("heroId", "absent", "alive", true, "scopes", new[] { "settlement:other" }) };
                AcceptedConversationUnit Unit(string hero, string exchange, int day) => new AcceptedConversationUnit
                {
                    Campaign = campaign, Hero = hero, HeroName = hero, Player = "player", Reply = "A successful spoken response.", Location = "town",
                    Payload = TestDict("campaignId", campaign, "timelineId", "attention_test", "playerHeroStringId", "player", "channel", "in_person",
                        "sceneTurnId", exchange, "worldDay", (double)day, "playerAttentionPresence", people)
                };
                List<Dictionary<string, object>> Sources(string hero, string id) => new List<Dictionary<string, object>> { TestDict("speaker_id", hero, "turn_id", id) };
                using (var c = OpenCampaignConnection(campaign))
                {
                    EnsurePlayerApproachSchema(c);
                    var individualBrief = CloneDictionary(brief); individualBrief["heroId"] = "npc"; individualBrief["name"] = "Mira";
                    var invitation = TestDict("requestId", "individual_invitation", "playerId", "player", "kind", "shared", "status", "accepted",
                        "heroes", new[] { individualBrief }, "heard", new List<string>());
                    PutApproachState(c, "individual_opening_test", "request:individual_invitation", invitation);
                    var individualOpening = TestDict("campaignId", campaign, "timelineId", "individual_opening_test", "playerHeroStringId", "player",
                        "channel", "in_person", "worldDay", 8d, "playerText", "", "approachOpening", true,
                        "approachRequestId", "individual_invitation", "playerAttentionPresence", people);
                    Check("individual_npc_first_preparation", PrepareIndividualNpcOpening(campaign, "npc", individualOpening)
                        && ReadString(individualOpening, "playerText", "missing") == "" && ReadBool(individualOpening, "suppressPlayerTranscript", false)
                        && ReadString(individualOpening, "turnType", "") == "npc_approach_opening"
                        && ReadString(ReadDictionary(ReadDictionary(individualOpening, "authoritativeApproachBrief"), "primaryRival"), "heroId", "") == "rival",
                        "An accepted individual invitation permits an empty player contribution and binds the saved opening brief without manufacturing player speech.");
                    foreach (string invalid in new[] { "missing_request", "wrong_player", "wrong_hero", "wrong_timeline", "unaccepted", "forged_flags" })
                    {
                        var attempt = CloneDictionary(individualOpening);
                        if (invalid == "missing_request") attempt["approachRequestId"] = "missing";
                        if (invalid == "wrong_player") attempt["playerHeroStringId"] = "other";
                        if (invalid == "wrong_timeline") attempt["timelineId"] = "sibling";
                        if (invalid == "unaccepted")
                        { var pending = CloneDictionary(invitation); pending["status"] = "pending"; PutApproachState(c, "individual_opening_test", "request:individual_invitation", pending); }
                        if (invalid == "forged_flags")
                        { attempt.Remove("approachRequestId"); attempt["authoritativeApproachOpening"] = true; }
                        bool refused = false;
                        try { refused = !PrepareIndividualNpcOpening(campaign, invalid == "wrong_hero" ? "other" : "npc", attempt); }
                        catch (InvalidOperationException) { refused = true; }
                        Check("individual_opening_rejects_" + invalid, refused, "Only the exact saved accepted speaker/player/timeline can initiate an NPC-first individual opening.");
                        PutApproachState(c, "individual_opening_test", "request:individual_invitation", invitation);
                    }
                    var ordinary = CloneDictionary(individualOpening); ordinary["playerText"] = "Hello.";
                    Check("individual_player_reply_stays_player_reply", !PrepareIndividualNpcOpening(campaign, "npc", ordinary)
                        && ReadString(ordinary, "playerText", "") == "Hello.", "A real player contribution cannot be promoted into NPC-first speech.");
                    var openingUnit = new AcceptedConversationUnit { Campaign = campaign, Hero = "npc", Player = "player", HeroName = "Mira", PlayerName = "Player",
                        EventId = "individual_opening_accepted", EventType = "dialogue_turn", PlayerText = "", Reply = ReadString(valid, "reply", ""), Timestamp = 8,
                        Generation = ReadString(ReadMemoryPrecisionState(c), "restore_generation", ""), Payload = individualOpening, Parsed = valid,
                        Memories = new(), Beliefs = new(), Relationships = new(), Obligations = new(), Comprehensions = new(), Continuity = new() };
                    var acceptedOpening = PublishAcceptedConversationUnit(openingUnit, accept => StoreConversationExchange(campaign, individualOpening,
                        "npc", "player", "Player", "Mira", "", openingUnit.Reply, openingUnit.EventId, 8, acceptance: accept));
                    string openingSession = ReadString(acceptedOpening, "sessionId", "");
                    Check("individual_opening_only_npc_source_and_heard_receipt", ReadBool(acceptedOpening, "ok", false)
                        && QuerySql(c, "SELECT turn_id FROM conversation_turns WHERE session_id=$session AND role='player';", TestDict("session", openingSession)).Count == 0
                        && QuerySql(c, "SELECT turn_id FROM conversation_turns WHERE session_id=$session AND role='npc';", TestDict("session", openingSession)).Count == 1
                        && ReadStringList(ApproachState(c, "individual_opening_test", "request:individual_invitation"), "heard").Contains("npc"),
                        "Accepted opening speech and the heard/contact receipt commit together with no empty player source.");
                    Check("individual_heard_opening_cannot_restart", !PrepareIndividualNpcOpening(campaign, "npc", CloneDictionary(individualOpening)),
                        "A heard invitation cannot reauthorize an empty NPC-first opening.");
                    ExecuteSql(c, "DELETE FROM player_attention_events WHERE timeline_id='individual_opening_test';");
                    foreach (string key in new[] { PlayerApproachOptions.AttentionKey, PlayerApproachOptions.MasterKey })
                    {
                        var disabled = new PlayerApproachOptions(field => field != key);
                        var tracking = RecordAcceptedPlayerAttention(c, Unit("npc", "tracking_off_" + key, 9), Sources("npc", "off_source"), disabled);
                        Check("tracking_off_" + key, ReadBool(tracking, "trackingDisabled", false) && QuerySql(c, "SELECT * FROM player_attention_events;").Count == 0
                            && ReadInt(ApproachState(c, "attention_test", "contact:player:npc"), "day", 0) == 9,
                            "Disabling tracking inserts no observation while successful player contact still clears stale neglect.");
                    }
                    RecordAcceptedPlayerAttention(c, Unit("npc", "first", 10), Sources("npc", "source1"));
                    RecordAcceptedPlayerAttention(c, Unit("npc", "again", 10), Sources("npc", "source2"));
                    Check("daily_observer_deduplication", QuerySql(c, "SELECT * FROM player_attention_events;").Count == 1,
                        "Two successful conversations create one generic event for the present observer and none for the absent observer.");
                    var newcomer = people.Concat(new[] { TestDict("heroId", "newcomer", "alive", true, "scopes", new[] { "settlement:town" }) }).ToList();
                    var afterArrival = Unit("npc", "new-arrival", 10); afterArrival.Payload["playerAttentionPresence"] = newcomer;
                    RecordAcceptedPlayerAttention(c, afterArrival, Sources("npc", "source3"));
                    Check("newcomer_observes_same_day", QuerySql(c, "SELECT * FROM player_attention_events WHERE observer_id='newcomer';").Count == 1,
                        "Per-observer deduplication preserves same-day observation by a newly arrived character.");
                    string baselineBeforeFailure = CanonicalJson(ApproachState(c, "attention_test", "contact:player:npc"));
                    bool noticesExcluded = true;
                    foreach (string failure in new[] { "action_verification", "continuity_evidence", "memory_rewrite", "dialogue_validation" })
                    {
                        var notice = Unit("npc", "failure_" + failure, 11);
                        notice.Reply = ReadString(VisibleFailedRepairWithoutEffects(TestDict("reply", "An unverified attempted response."), failure), "reply", "");
                        noticesExcluded &= ReadString(RecordAcceptedPlayerAttention(c, notice, Sources("npc", "bad_" + failure)), "status", "") == "excluded";
                    }
                    var letter = Unit("npc", "letter", 11); letter.Payload["channel"] = "correspondence";
                    RecordAcceptedPlayerAttention(c, letter, Sources("npc", "mail"));
                    Check("failed_and_remote_excluded", noticesExcluded
                        && QuerySql(c, "SELECT * FROM player_attention_events WHERE calendar_day=11;").Count == 0
                        && CanonicalJson(ApproachState(c, "attention_test", "contact:player:npc")) == baselineBeforeFailure,
                        "Technical notices and letters neither observe attention nor reset a successful-contact baseline.");
                    ExecuteSql(c, "BEGIN;");
                    RecordAcceptedPlayerAttention(c, Unit("npc", "rollback", 12), Sources("npc", "rollback_source"));
                    ExecuteSql(c, "ROLLBACK;");
                    Check("accepted_transaction_rollback", QuerySql(c, "SELECT * FROM player_attention_events WHERE calendar_day=12;").Count == 0,
                        "An accepted-source transaction rollback also removes attention and its contact effects.");
                    RecordAcceptedPlayerAttention(c, Unit("observer", "group", 13), Sources("observer", "group_a"));
                    RecordAcceptedPlayerAttention(c, Unit("npc", "group", 13), Sources("npc", "group_b"));
                    var contact = ApproachState(c, "attention_test", "contact:player:observer");
                    var live = QuerySql(c, "SELECT * FROM player_attention_events WHERE observer_id='observer' AND sequence_id>$sequence AND exchange_id<>$exchange;",
                        TestDict("sequence", ReadLong(contact, "sequence", 0), "exchange", ReadString(contact, "exchange", "")));
                    Check("group_participants_not_neglected", live.Count == 0, "Later speakers in the same successful group exchange do not artificially neglect an earlier speaker.");
                    var request = TestDict("requestId", "accepted_request", "playerId", "player", "kind", "shared", "status", "accepted", "heroes", new[] { TestDict("heroId", "observer", "name", "Observer", "severityLevel", 2) }, "heard", new List<string>());
                    PutApproachState(c, "attention_test", "request:accepted_request", request);
                    var alreadyHeard = Unit("observer", "approach", 14); alreadyHeard.Payload["approachRequestId"] = "accepted_request";
                    RecordAcceptedPlayerAttention(c, alreadyHeard, Sources("observer", "approach_source"));
                    Check("saved_brief_survives_contact_reset", ReadDictionaryList(ApproachState(c, "attention_test", "request:accepted_request"), "heroes")[0].ContainsKey("severityLevel")
                        && ReadStringList(ApproachState(c, "attention_test", "request:accepted_request"), "heard").Contains("observer"), "The frozen approach reason survives contact reset and remembers its accepted opening.");
                    EnsureMbtiRelationshipSchema(c);
                    var nobles = Enumerable.Range(0, 5).Select(i => TestDict("heroId", "noble_" + i, "name", "Noble " + i,
                        "alive", true, "adult", true, "free", true, "coPresent", true, "isLord", true, "known", false, "nativeRelation", 100)).ToList();
                    foreach (var noble in nobles)
                        ExecuteSql(c, @"INSERT INTO relationship_pair_chemistry(pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,
affinity_a_to_b,affinity_b_to_a,first_day,last_day,updated_ts) VALUES($pair,$hero,'player','ENFP','INTJ',100,0,1,20,123);",
                            TestDict("pair", AmbientPairKey(ReadString(noble, "heroId", ""), "player"), "hero", ReadString(noble, "heroId", "")));
                    foreach (var scope in new[] { (kind: "shared", key: PlayerApproachOptions.JealousyKey), (kind: "keep", key: PlayerApproachOptions.KeepKey), (kind: "patrol", key: PlayerApproachOptions.PatrolKey) })
                    {
                        foreach (string offKey in new[] { scope.key, PlayerApproachOptions.MasterKey })
                        {
                            string isolated = "options_" + scope.kind + "_" + offKey;
                            var disabled = new PlayerApproachOptions(field => field != offKey);
                            var evaluate = TestDict("campaignId", campaign, "timelineId", isolated, "playerId", "player", "worldDay", 18d,
                                "kind", scope.kind, "candidates", nobles);
                            var offResult = PlayerApproachEvaluateApi(evaluate, disabled);
                            Check("disabled_evaluation_" + isolated, ReadString(offResult, "status", "") == "disabled"
                                && QuerySql(c, "SELECT * FROM player_approach_state WHERE timeline_id=$timeline;", TestDict("timeline", isolated)).Count == 0,
                                "Disabled evaluation consumes no roll, cadence, daily quota or cooldown and returns no request.");
                            string requestKey = "off_request";
                            PutApproachState(c, isolated, "request:" + requestKey, TestDict("requestId", requestKey, "playerId", "player", "kind", scope.kind,
                                "status", "pending", "heroes", new[] { TestDict("heroId", "noble_0", "lastContact", TestDict(), "penalty", 8) }));
                            var refuse = TestDict("campaignId", campaign, "timelineId", isolated, "playerId", "player", "requestId", requestKey, "choice", "refuse");
                            var cancelled = PlayerApproachDecisionApi(refuse, disabled);
                            var replayed = PlayerApproachDecisionApi(refuse, new PlayerApproachOptions());
                            Check("disabled_decision_" + isolated, ReadString(ReadDictionary(cancelled, "request"), "status", "") == "cancelled"
                                && ReadDictionaryList(ReadDictionary(cancelled, "request"), "receipts").Count == 0
                                && ReadBool(replayed, "idempotent", false)
                                && ReadInt(QuerySql(c, "SELECT affinity_a_to_b FROM relationship_pair_chemistry WHERE pair_key=$pair;",
                                    TestDict("pair", AmbientPairKey("noble_0", "player"))).Single(), "affinity_a_to_b", 0) == 100,
                                "A pending disabled invitation cancels without affinity loss or native receipts; re-enabling cannot replay it.");
                            PutApproachState(c, isolated, "request:off_accept", TestDict("requestId", "off_accept", "playerId", "player", "kind", scope.kind,
                                "status", "pending", "heroes", new[] { TestDict("heroId", "noble_0", "lastContact", TestDict(), "penalty", 8) }));
                            refuse["requestId"] = "off_accept"; refuse["choice"] = "accept";
                            Check("disabled_accept_" + isolated, ReadString(ReadDictionary(PlayerApproachDecisionApi(refuse, disabled), "request"), "status", "") == "cancelled",
                                "A click racing a settings change cannot accept a disabled pending invitation.");
                        }
                    }
                    var keepInput = TestDict("campaignId", campaign, "timelineId", "attention_test", "playerId", "player", "worldDay", 20d,
                        "kind", "keep", "playerRenown", 0d, "candidates", nobles);
                    var keep = PlayerApproachEvaluateApi(keepInput);
                    var invited = ReadDictionaryList(keep, "requests").Single();
                    var invitedHeroes = ReadDictionaryList(invited, "heroes");
                    Check("four_noble_group_and_daily_replay", invitedHeroes.Count == 4
                        && invitedHeroes.Select(h => ReadString(h, "heroId", "")).Distinct().Count() == 4
                        && ReadDictionaryList(PlayerApproachEvaluateApi(keepInput), "requests").Count == 0,
                        "Five guaranteed passing nobles select exactly four distinct invitees; re-entering a keep cannot reroll or create another daily group.");
                    string invitationId = ReadString(invited, "requestId", "");
                    var decline = TestDict("campaignId", campaign, "timelineId", "attention_test", "playerId", "player", "worldDay", 20d,
                        "requestId", invitationId, "choice", "refuse");
                    var firstDecline = PlayerApproachDecisionApi(decline);
                    var secondDecline = PlayerApproachDecisionApi(decline);
                    Check("refusal_atomic_replay", ReadBool(firstDecline, "ok", false) && ReadBool(secondDecline, "idempotent", false)
                        && invitedHeroes.All(h => ReadInt(QuerySql(c, "SELECT affinity_a_to_b FROM relationship_pair_chemistry WHERE pair_key=$pair;",
                            TestDict("pair", AmbientPairKey(ReadString(h, "heroId", ""), "player"))).Single(), "affinity_a_to_b", 0) == 100 - ReadInt(h, "penalty", 0))
                        && ReadDictionaryList(ReadDictionary(firstDecline, "request"), "receipts").Count == 4,
                        "A repeated decline applies each saved 1d5 penalty once and returns the same four native receipt identities.");
                    var effect = ReadDictionaryList(ReadDictionary(firstDecline, "request"), "receipts")[0];
                    int requested = ReadInt(effect, "nativeRelationDelta", 0);
                    var observation = TestDict("receiptId", ReadString(effect, "receiptId", ""), "subjectId", ReadString(effect, "heroId", ""),
                        "targetId", "player", "requestedDelta", requested, "priorNativeRelation", -100, "nativeRelation", -100,
                        "observedDelta", 0, "measurementStatus", "observed_before_and_after");
                    var acknowledgment = CloneDictionary(decline); acknowledgment["choice"] = "acknowledge"; acknowledgment["nativeObservations"] = new[] { observation };
                    var firstAck = PlayerApproachDecisionApi(acknowledgment);
                    var secondAck = PlayerApproachDecisionApi(acknowledgment, new PlayerApproachOptions(key => key != PlayerApproachOptions.MasterKey));
                    Check("native_cap_observation_replay", ReadBool(firstAck, "ok", false) && ReadBool(secondAck, "ok", false)
                        && ReadDictionaryList(ReadDictionary(secondAck, "request"), "nativeObservations").Count == 1,
                        "Observed native clamping is retained separately from requested loss, and retry acknowledgment cannot duplicate the effect.");
                    observation["nativeRelation"] = -99; observation["observedDelta"] = 1;
                    Check("native_observation_immutable", !ReadBool(PlayerApproachDecisionApi(acknowledgment), "ok", true), "A retained native observation cannot be replaced by different before/after values.");
                    var cadenceInput = TestDict("campaignId", campaign, "timelineId", "cadence_test", "playerId", "player", "worldDay", 1d,
                        "kind", "shared", "candidates", new[] { TestDict("heroId", "observer", "alive", true, "adult", true, "free", true, "coPresent", true, "known", true) });
                    PlayerApproachEvaluateApi(cadenceInput); PlayerApproachEvaluateApi(cadenceInput);
                    int sameDayCount = ReadInt(ApproachState(c, "cadence_test", "cadence:player:observer"), "sharedDays", 0);
                    cadenceInput["worldDay"] = 8d; PlayerApproachEvaluateApi(cadenceInput);
                    Check("shared_days_pause_and_resume", sameDayCount == 1 && ReadInt(ApproachState(c, "cadence_test", "cadence:player:observer"), "sharedDays", 0) == 2,
                        "Multiple hourly evaluations count one shared day; six absent days add no cadence credit on return.");
                    var stale = TestDict("requestId", "stale", "playerId", "player", "kind", "shared", "status", "pending",
                        "heroes", new[] { TestDict("heroId", "observer", "lastContact", TestDict(), "penalty", 8) });
                    PutApproachState(c, "attention_test", "request:stale", stale);
                    var staleDecision = CloneDictionary(decline); staleDecision["requestId"] = "stale";
                    Check("contact_resolves_queued_neglect", ReadString(ReadDictionary(PlayerApproachDecisionApi(staleDecision), "request"), "status", "") == "cancelled",
                        "A successful conversation after an invitation was queued cancels its stale neglect reason without a refusal consequence.");
                    Check("timeline_history_isolation", PlayerAttentionHistory(campaign, "observer", TestDict("timelineId", "another_timeline", "playerId", "player")).Count == 0,
                        "Public attention history cannot cross campaign timeline boundaries.");
                }
                long savedEvents, savedSequence; string savedRequest;
                using (var c = OpenCampaignConnection(campaign))
                {
                    savedEvents = ReadLong(QuerySql(c, "SELECT COUNT(*) AS count FROM player_attention_events;").Single(), "count", 0);
                    savedSequence = ReadLong(QuerySql(c, "SELECT MAX(sequence_id) AS sequence FROM player_attention_events;").Single(), "sequence", 0);
                    savedRequest = CanonicalJson(ApproachState(c, "attention_test", "request:accepted_request"));
                }
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "approach_checkpoint");
                using (var c = OpenCampaignConnection(campaign))
                {
                    ExecuteSql(c, "DELETE FROM player_attention_events;");
                    PutApproachState(c, "attention_test", "request:accepted_request", TestDict("status", "changed_after_save"));
                }
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "approach_checkpoint");
                using (var c = OpenCampaignConnection(campaign))
                {
                    bool restored = ReadLong(QuerySql(c, "SELECT COUNT(*) AS count FROM player_attention_events;").Single(), "count", 0) == savedEvents
                        && CanonicalJson(ApproachState(c, "attention_test", "request:accepted_request")) == savedRequest;
                    RecordAcceptedPlayerAttention(c, Unit("npc", "after_restore", 30), Sources("npc", "restored_source"));
                    Check("snapshot_restore_and_sequence", restored
                        && ReadLong(QuerySql(c, "SELECT MAX(sequence_id) AS sequence FROM player_attention_events;").Single(), "sequence", 0) > savedSequence,
                        "A real isolated PostgreSQL Save Sync schema snapshot restores attention and frozen requests, then preserves monotonic accepted-source ordering.");
                    var individualContact = Unit("observer", "", 31); individualContact.Payload.Remove("sceneTurnId"); individualContact.EventId = "individual_contact";
                    RecordAcceptedPlayerAttention(c, individualContact, Sources("observer", "individual_contact_source"));
                    var individualRival = Unit("npc", "", 32); individualRival.Payload.Remove("sceneTurnId"); individualRival.EventId = "individual_rival";
                    RecordAcceptedPlayerAttention(c, individualRival, Sources("npc", "individual_rival_source"));
                    var individualBaseline = ApproachState(c, "attention_test", "contact:player:observer");
                    Check("individual_exchange_identity", ReadString(individualBaseline, "exchange", "") == "individual_contact"
                        && QuerySql(c, "SELECT * FROM player_attention_events WHERE observer_id='observer' AND sequence_id>$sequence AND exchange_id<>$exchange;",
                            TestDict("sequence", ReadLong(individualBaseline, "sequence", 0), "exchange", ReadString(individualBaseline, "exchange", ""))).Count == 1,
                        "Individual dialogue uses its accepted event identity when no group turn key exists; later individual rival attention remains visible.");
                }
            }
            catch (Exception ex) { Check("persistence_fixture", false, ex.ToString()); }
            finally
            {
                try { ReignPostgreSqlStorage.DropCampaign(campaign); }
                catch (Exception ex) { Check("fixture_cleanup", false, ex.Message); }
                TryDeleteDirectory(CampaignDirectory(campaign));
            }
            return results;
        }
    }
}
