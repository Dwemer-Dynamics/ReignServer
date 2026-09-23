using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> AgencyTestContext() => TestDict(
            "timelineId", "agency_timeline", "ownerId", "npc", "peerId", "player", "worldDay", 20d,
            "playerText", "Will you leave your house and travel with me?", "records", new List<Dictionary<string, object>>(),
            "objectIds", new[] { "castle_a", "castle_b" },
            "facts", TestDict("player.clanTier", 1, "npc.clanTier", 4, "opportunity.power", 17d,
                "opportunity.wealth", 50d, "opportunity.prestige", 50d, "opportunity.protection", 40d,
                "relationship.affinity", 10d, "relationship.sharedHistory", 0, "trait.judgment", 75d,
                "trait.patience", 50d, "trait.loyalty", 80d, "trait.honor", 80d, "npc.clanId", "noble_clan"));

        private static Dictionary<string, object> AgencyTestProposal(Dictionary<string, object> context, string disposition = "terms_refusal", string objection = "price")
        {
            var previous = ReadDictionaryList(context, "records").FirstOrDefault();
            return TestDict("topic", "travel", "objectId", "", "recordId", ReadString(previous, "id", ""),
                "expectedRevision", ReadInt(previous, "revision", 0), "subject", "Travel together",
                "disposition", disposition, "engagement", "request", "tactic", "appeal", "stakes", "major",
                "benefit", "personal", "objections", objection.Length == 0 ? new string[0] : new[] { objection },
                "reason", "This request is not worth leaving my household.", "reconsideration", "Offer worthwhile compensation.",
                "evidenceKeys", new[] { "trait.judgment" }, "playerQuote", ReadString(context, "playerText", ""),
                "replyQuote", "My household matters to me.", "terms", TestDict("gold", 0d, "durationDays", 0d,
                    "paymentDeferred", false, "benefit", "", "conditions", ""));
        }

        private static Dictionary<string, object> AgencyTestReply(Dictionary<string, object> proposal) => TestDict(
            "reply", "My household matters to me.", "body", "My household matters to me.", "shouldReply", true,
            "decisionBrief", TestDict("facts", new string[0], "goals", new string[0], "constraints", new string[0], "decision", "Consider the terms.", "confidence", 1d),
            "politicalConduct", TestDict(), "emotion", "calm", "intent", "considering", "participation", "speak",
            "reactionTargetHeroStringId", "player", "relationshipSignal", "neutral", "relationshipAssessments", new List<Dictionary<string, object>>(),
            "actionGate", TestDict("needed", false, "commitment", "refused", "intent", "accept_temporary_party_guest", "confidence", 1d, "reason", "Not agreed."),
            "proposalDecisions", proposal == null ? new List<Dictionary<string, object>>() : new List<Dictionary<string, object>> { proposal });

        private static List<Dictionary<string, object>> RunConversationAgencySelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            Action<string, bool, object> add = (id, pass, details) => results.Add(TestDict("caseId", "agency_" + id, "passed", pass, "summary", id, "data", details));
            var context = AgencyTestContext();
            var first = EvaluateConversationAgency(AgencyTestReply(AgencyTestProposal(context)), context);
            add("first_refusal_has_no_pressure", ReadBool(first, "ok", false) && ReadInt(ReadDictionaryList(first, "updates").Single(), "pressure", -1) == 0, first);
            context["records"] = ReadDictionaryList(first, "updates");
            string[] modes = { "individual", "party_chat", "social_event", "court", "correspondence" };
            bool allHeld = true, baselineHeld = true;
            int penalties = 0;
            for (int i = 0; i < 25; i++)
            {
                context["mode"] = modes[i % modes.Length];
                context["playerText"] = "Think about our glorious future, please. Argument " + i;
                var reversal = AgencyTestProposal(context, "accepted", "");
                allHeld &= !ReadBool(EvaluateConversationAgency(AgencyTestReply(reversal), context), "ok", true);
                var refused = EvaluateConversationAgency(AgencyTestReply(AgencyTestProposal(context)), context);
                allHeld &= ReadBool(refused, "ok", false);
                context["records"] = ReadDictionaryList(refused, "updates");
                var row = ReadDictionaryList(context, "records").Single();
                if (ReadBool(row, "penalize", false)) penalties++;
                baselineHeld &= ReadDouble(ReadDictionary(row, "terms"), "gold", -1) == 0;
            }
            add("twenty_five_paraphrases_across_channels_hold", allHeld, null);
            add("pressure_bounded_and_topic_ends", penalties == 3 && ReadString(ReadDictionaryList(context, "records").Single(), "pressureStage", "") == "end_discussion", penalties);
            add("rejected_baseline_not_eroded", baselineHeld, null);
            var clear = AgencyTestProposal(context); clear["engagement"] = "clarify";
            var clarification = EvaluateConversationAgency(AgencyTestReply(clear), context);
            add("clarification_does_not_escalate", ReadInt(ReadDictionaryList(clarification, "updates").Single(), "pressure", -1) == 6, clarification);
            clear["engagement"] = "apology";
            clear["apologyAccepted"] = true;
            var apology = EvaluateConversationAgency(AgencyTestReply(clear), context);
            add("apology_cools_without_reopening", ReadInt(ReadDictionaryList(apology, "updates").Single(), "pressure", -1) == 5
                && ReadString(ReadDictionaryList(apology, "updates").Single(), "disposition", "") == "terms_refusal", apology);
            context["worldDay"] = 500d;
            add("waiting_alone_never_reopens", !ReadBool(EvaluateConversationAgency(AgencyTestReply(AgencyTestProposal(context, "accepted", "")), context), "ok", true), null);
            ReadDictionaryList(context, "records").Single()["pressure"] = 2;
            add("cooling_does_not_reset_lifetime_penalty_cap", !ReadBool(ReadDictionaryList(
                EvaluateConversationAgency(AgencyTestReply(AgencyTestProposal(context)), context), "updates").Single(), "penalize", true), null);

            context = AgencyTestContext();
            var personal = AgencyTestProposal(context, "accepted", "");
            add("noble_does_not_abandon_household_for_personal_appeal", !ReadBool(EvaluateConversationAgency(AgencyTestReply(personal), context), "ok", true), null);
            personal["benefit"] = "prestige";
            add("claimed_tournament_fame_is_not_verified_wins", !ReadBool(EvaluateConversationAgency(AgencyTestReply(personal), context), "ok", true), null);
            ReadDictionary(context, "facts")["player.knownTournamentWins"] = 8;
            add("known_wins_support_tournament_opportunity", ReadBool(EvaluateConversationAgency(AgencyTestReply(personal), context), "ok", false), null);
            add("gold_number_cannot_invent_trip_duration", !AgencyQuotedDuration("I offer 30 gold.", 30), null);
            add("calendar_units_preserve_fractional_seasons", AgencyQuotedDuration("Travel for half season.", 10.5, TestDict("daysPerSeason", 21d)), null);

            context = AgencyTestContext();
            var refusal = AgencyTestProposal(context); ReadDictionary(refusal, "terms")["gold"] = 800d;
            context["playerText"] = "I offer 800 gold."; refusal["playerQuote"] = context["playerText"];
            context["records"] = ReadDictionaryList(EvaluateConversationAgency(AgencyTestReply(refusal), context), "updates");
            context["playerText"] = "I offer 1000 gold.";
            var paid = AgencyTestProposal(context, "accepted", ""); paid["tactic"] = "compensation"; paid["benefit"] = "wealth";
            ReadDictionary(paid, "terms")["gold"] = 1000d;
            add("meaningful_bribe_can_reopen_price_refusal", ReadBool(EvaluateConversationAgency(AgencyTestReply(paid), context), "ok", false), null);
            context["playerText"] = "I offer 801 gold."; paid["playerQuote"] = context["playerText"]; ReadDictionary(paid, "terms")["gold"] = 801d;
            add("tiny_bid_does_not_reroll", !ReadBool(EvaluateConversationAgency(AgencyTestReply(paid), context), "ok", true), null);
            ReadDictionary(paid, "terms")["gold"] = 1000d;
            add("invented_amount_does_not_reopen", !ReadBool(EvaluateConversationAgency(AgencyTestReply(paid), context), "ok", true), null);
            context["playerText"] = "I offer 1000 gold."; paid["playerQuote"] = context["playerText"]; ReadDictionary(paid, "terms")["paymentDeferred"] = true;
            add("unsecured_future_money_does_not_equal_cash", !ReadBool(EvaluateConversationAgency(AgencyTestReply(paid), context), "ok", true), null);

            context = AgencyTestContext();
            refusal = AgencyTestProposal(context, "personal_refusal", "loyalty");
            context["records"] = ReadDictionaryList(EvaluateConversationAgency(AgencyTestReply(refusal), context), "updates");
            context["playerText"] = "I offer 1000000 gold.";
            paid = AgencyTestProposal(context, "accepted", ""); ReadDictionary(paid, "terms")["gold"] = 1000000d;
            add("loyalty_not_bought_by_unlimited_money", !ReadBool(EvaluateConversationAgency(AgencyTestReply(paid), context), "ok", true), null);
            ReadDictionary(context, "facts")["npc.clanId"] = "new_clan";
            add("changed_allegiance_can_reopen_loyalty_context", ReadBool(EvaluateConversationAgency(AgencyTestReply(paid), context), "ok", false), null);

            context = AgencyTestContext();
            var power = AgencyTestProposal(context, "accepted", ""); power["benefit"] = "power";
            add("ambitious_noble_rejects_unproven_lower_clan", !ReadBool(EvaluateConversationAgency(AgencyTestReply(power), context), "ok", true), null);
            ReadDictionary(context, "facts")["opportunity.prestige"] = 100d;
            add("tournament_fame_does_not_supply_political_capacity", !ReadBool(EvaluateConversationAgency(AgencyTestReply(power), context), "ok", true), null);
            ReadDictionary(context, "facts")["player.clanTier"] = 6;
            ReadDictionary(context, "facts")["opportunity.power"] = 80d;
            add("credible_ruler_can_be_considered", ReadBool(EvaluateConversationAgency(AgencyTestReply(power), context), "ok", false), null);
            context = AgencyTestContext(); ReadDictionary(context, "facts")["trait.judgment"] = 20d;
            add("poor_judgment_allows_risky_choice_not_automatic_yes", ReadBool(EvaluateConversationAgency(AgencyTestReply(power), context), "ok", false)
                && ReadString(ReadDictionaryList(EvaluateConversationAgency(AgencyTestReply(AgencyTestProposal(context)), context), "updates").Single(), "disposition", "") == "terms_refusal", null);
            context = AgencyTestContext(); ReadDictionary(context, "facts")["relationship.affinity"] = 95d;
            add("relationship_number_alone_not_shared_history", !ReadBool(EvaluateConversationAgency(AgencyTestReply(power), context), "ok", true), null);
            ReadDictionary(context, "facts")["relationship.sharedHistory"] = 2;
            add("established_high_trust_can_support_risky_choice", ReadBool(EvaluateConversationAgency(AgencyTestReply(power), context), "ok", false), null);

            context = AgencyTestContext();
            refusal = AgencyTestProposal(context, "terms_refusal", "duty");
            context["records"] = ReadDictionaryList(EvaluateConversationAgency(AgencyTestReply(refusal), context), "updates");
            context["playerText"] = "Come for three days, then return home.";
            var shorter = AgencyTestProposal(context, "accepted", ""); ReadDictionary(shorter, "terms")["durationDays"] = 3d;
            add("bounded_trip_can_replace_indefinite_request", ReadBool(EvaluateConversationAgency(AgencyTestReply(shorter), context), "ok", false), null);
            ReadDictionaryList(context, "records").Single()["objections"] = new[] { "duty", "loyalty" };
            add("all_decisive_objections_must_be_addressed", !ReadBool(EvaluateConversationAgency(AgencyTestReply(shorter), context), "ok", true), null);

            context = AgencyTestContext();
            var threat = AgencyTestProposal(context, "accepted", ""); threat["tactic"] = "threat";
            add("coercion_not_free_consent", !ReadBool(EvaluateConversationAgency(AgencyTestReply(threat), context), "ok", true), null);
            threat["disposition"] = "accepted_under_duress";
            add("empty_threat_not_credible", !ReadBool(EvaluateConversationAgency(AgencyTestReply(threat), context), "ok", true), null);
            ReadDictionary(context, "facts")["authority.danger"] = 85d;
            add("credible_pressure_can_obtain_compliance", ReadBool(EvaluateConversationAgency(AgencyTestReply(threat), context), "ok", false), null);
            threat["tactic"] = "blackmail";
            add("blackmail_needs_evidence", !ReadBool(EvaluateConversationAgency(AgencyTestReply(threat), context), "ok", true), null);
            ReadDictionary(context, "facts")["evidence.known_scandal"] = TestDict("summary", "The NPC knows the witness saw the secret payment.", "confidence", 1d);
            threat["evidenceKeys"] = new[] { "evidence.known_scandal" };
            ReadDictionary(context, "facts")["authority.danger"] = 0d;
            add("known_blackmail_does_not_require_military_superiority", ReadBool(EvaluateConversationAgency(AgencyTestReply(threat), context), "ok", false), null);
            var condition = AgencyTestProposal(context, "accepted", ""); ReadDictionary(condition, "terms")["conditions"] = "After my governor replacement arrives.";
            add("condition_does_not_execute_immediately", !ReadBool(EvaluateConversationAgency(AgencyTestReply(condition), context), "ok", true), null);
            condition["disposition"] = "conditional";
            add("conditional_agreement_can_be_recorded", ReadBool(EvaluateConversationAgency(AgencyTestReply(condition), context), "ok", false), null);
            var conditionalContext = CloneDictionary(context);
            condition["objections"] = new[] { "duty" };
            conditionalContext["records"] = ReadDictionaryList(EvaluateConversationAgency(AgencyTestReply(condition), conditionalContext), "updates");
            var unfulfilled = AgencyTestProposal(conditionalContext, "accepted", "");
            ReadDictionary(unfulfilled, "terms")["durationDays"] = 0;
            unfulfilled["benefit"] = "wealth";
            add("conditional_terms_cannot_be_erased_by_repetition", !ReadBool(EvaluateConversationAgency(AgencyTestReply(unfulfilled), conditionalContext), "ok", true), null);
            var invented = AgencyTestProposal(context); invented["evidenceKeys"] = new[] { "player.saidIamKing" };
            add("player_claim_is_not_native_fact", !ReadBool(EvaluateConversationAgency(AgencyTestReply(invented), context), "ok", true), null);
            invented = AgencyTestProposal(context); invented["objectId"] = "random_new_goal";
            add("invented_object_cannot_reset_record", !ReadBool(EvaluateConversationAgency(AgencyTestReply(invented), context), "ok", true), null);
            invented = AgencyTestProposal(context); invented["playerQuote"] = "I agreed to pay you.";
            add("fabricated_offer_quote_rejected", !ReadBool(EvaluateConversationAgency(AgencyTestReply(invented), context), "ok", true), null);
            var missing = AgencyTestReply(null); ReadDictionary(missing, "actionGate")["needed"] = true; ReadDictionary(missing, "actionGate")["commitment"] = "accepted";
            add("action_cannot_bypass_negotiation_metadata", !ReadBool(EvaluateConversationAgency(missing, context), "ok", true), null);
            ReadDictionary(missing, "actionGate")["intent"] = "declare_war"; ReadDictionary(missing, "actionGate")["commitment"] = "commanded";
            add("unilateral_authority_preserved", ReadBool(EvaluateConversationAgency(missing, context), "ok", false), null);
            add("ordinary_conversation_has_no_negotiation_effect", ReadDictionaryList(EvaluateConversationAgency(AgencyTestReply(null), context), "updates").Count == 0, null);
            add("native_objects_in_resolution_arrays_are_available", AgencySuppliedObjectIds(TestDict("actionResolutionIndex",
                TestDict("settlements", new List<Dictionary<string, object>> { TestDict("settlementId", "castle_a") })), null).Contains("castle_a"), null);

            string campaign = "agency_test_" + Guid.NewGuid().ToString("N");
            context = AgencyTestContext();
            var original = AgencyTestReply(AgencyTestProposal(context));
            using (var connection = OpenCampaignConnection(campaign))
            {
                add("postgresql_backend_only", connection is Npgsql.NpgsqlConnection
                    && ReadString(QuerySql(connection, "SELECT version() AS version;").FirstOrDefault(), "version", "").StartsWith("PostgreSQL", StringComparison.Ordinal), null);
                var committed = CommitConversationAgency(connection, original, context, "turn_1");
                var replay = CommitConversationAgency(connection, original, context, "turn_1");
                add("durable_turn_retry_is_idempotent", ReadBool(committed, "ok", false) && ReadBool(replay, "replayed", false)
                    && QuerySql(connection, "SELECT * FROM conversation_negotiation_turns;").Count == 1, replay);
                add("stale_prepared_reply_rejected", !ReadBool(CommitConversationAgency(connection, original, context, "stale_turn"), "ok", true), null);
            }
            using (var connection = OpenCampaignConnection(campaign))
            {
                var reloaded = ReadConversationNegotiations(connection, "agency_timeline", "npc", "player");
                add("connection_reload_retains_refusal", reloaded.Count == 1 && ReadString(reloaded.Single(), "disposition", "") == "terms_refusal", reloaded);
                add("timeline_npc_and_player_isolation", ReadConversationNegotiations(connection, "other", "npc", "player").Count == 0
                    && ReadConversationNegotiations(connection, "agency_timeline", "other", "player").Count == 0
                    && ReadConversationNegotiations(connection, "agency_timeline", "npc", "other").Count == 0, null);
                context["records"] = reloaded;
            }
            int calls = 0;
            var unsafeReply = AgencyTestReply(AgencyTestProposal(context, "accepted", ""));
            var llm = TestDict("ok", true, "content", Json.Serialize(unsafeReply));
            var enforced = EnforceConversationAgencyResponse(llm, TestDict("requestType", "dialogue"), TestDict("conversationAgency", context), TestDict(), campaign,
                "invalid_repair", "dialogue", "npc", request => { calls++; return TestDict("ok", true, "content", Json.Serialize(unsafeReply)); }, false);
            add("one_failed_repair_never_commits_or_executes", calls == 1 && !ReadBool(enforced, "ok", true), enforced);
            calls = 0;
            enforced = EnforceConversationAgencyResponse(TestDict("ok", true, "content", Json.Serialize(unsafeReply)), TestDict("requestType", "dialogue"),
                TestDict("conversationAgency", context), TestDict(), campaign, "valid_repair", "dialogue", "npc",
                request => { calls++; return TestDict("ok", true, "content", Json.Serialize(AgencyTestReply(AgencyTestProposal(context)))); }, false);
            add("valid_repair_preserves_firm_refusal", calls == 1 && ReadBool(enforced, "ok", false), enforced);
            var pressureReceipt = TestDict("receiptId", "once", "updates", new List<Dictionary<string, object>> { TestDict("penalize", true, "pressure", 2, "playerQuote", "Please again.") });
            var assessmentPayload = TestDict("conversationAgencyReceipt", pressureReceipt, "playerText", "Please again.");
            var assessments = new List<Dictionary<string, object>> { TestDict("targetHeroStringId", "player", "valence", "positive"), TestDict("targetHeroStringId", "bystander", "valence", "positive") };
            ApplyAgencyRelationshipAssessment(assessments, assessmentPayload, "npc", "player");
            add("pressure_uses_bounded_existing_relationship_scale", assessments.Count == 2 && ReadString(assessments.Last(), "severityTier", "") == "routine"
                && ReadString(assessments.Last(), "valence", "") == "negative", assessments);
            pressureReceipt["replayed"] = true; ApplyAgencyRelationshipAssessment(assessments, assessmentPayload, "npc", "player");
            add("pressure_receipt_replay_has_no_second_penalty", assessments.Count == 1 && ReadString(assessments.Single(), "targetHeroStringId", "") == "bystander", null);
            var accepted = TestDict("id", "neg_accepted", "revision", 3, "topic", "travel", "disposition", "accepted", "terms", TestDict("gold", 1000d, "durationDays", 3d));
            var acceptedPayload = TestDict("conversationAgencyReceipt", TestDict("ok", true, "receiptId", "bound", "updates", new List<Dictionary<string, object>> { accepted }));
            var errors = new List<string>(); var authority = TestDict();
            add("native_authority_binds_negotiation_revision", BindConversationAgencyAuthority(acceptedPayload, "accept_temporary_party_guest", TestDict("durationDays", 3d, "agreedGold", 1000d), authority, errors)
                && ReadInt(authority, "agencyRevision", 0) == 3, authority);
            errors.Clear();
            add("planner_cannot_expand_accepted_duration", !BindConversationAgencyAuthority(acceptedPayload, "accept_temporary_party_guest", TestDict("durationDays", 30d), TestDict(), errors), errors);
            errors.Clear();
            add("planner_cannot_drop_fixed_duration", !BindConversationAgencyAuthority(acceptedPayload, "accept_temporary_party_guest", TestDict("termKind", "open_ended"), TestDict(), errors), errors);
            accepted["playerQuote"] = "I offer 1000 gold per season for three days.";
            acceptedPayload["campaignCalendar"] = TestDict("daysPerSeason", 21d);
            errors.Clear();
            add("planner_cannot_convert_seasonal_wages_to_daily", !BindConversationAgencyAuthority(acceptedPayload, "accept_temporary_party_guest",
                TestDict("durationDays", 3d, "wageGold", 1000d, "wagePeriodDays", 1d), TestDict(), errors), errors);
            accepted["topic"] = "property"; accepted["objectId"] = "castle_a"; errors.Clear();
            add("accepted_asset_cannot_authorize_different_asset", !BindConversationAgencyAuthority(acceptedPayload, "transfer_settlement", TestDict(), TestDict(), errors,
                TestDict("targetSettlementStringId", "castle_b")), errors);
            accepted["objectId"] = ""; errors.Clear();
            add("planner_cannot_increase_raw_gold_amount", !BindConversationAgencyAuthority(acceptedPayload, "give_gold_to_player", TestDict(), TestDict(), errors,
                TestDict("GoldAmount", 10000d)), errors);
            accepted["topic"] = "travel";
            accepted["disposition"] = "terms_refusal"; errors = new List<string>();
            add("planner_cannot_bypass_refused_goal", !BindConversationAgencyAuthority(acceptedPayload, "accept_temporary_party_guest", TestDict(), TestDict(), errors), errors);
            calls = 0;
            enforced = EnforceConversationAgencyResponse(TestDict("ok", true, "content", Json.Serialize(unsafeReply)), TestDict("requestType", "dialogue"),
                TestDict("conversationAgency", context), TestDict(), campaign, "conflicting_repair", "dialogue", "npc",
                request => { calls++; return TestDict("ok", true, "content", Json.Serialize(AgencyTestReply(AgencyTestProposal(context)))); }, false,
                candidate => false);
            add("agency_repair_must_pass_other_response_guards", calls == 1 && !ReadBool(enforced, "ok", true), null);

            var warningPayload = TestDict("conversationAgencyReceipt", TestDict("updates", new List<Dictionary<string, object>> { TestDict("pressure", 1) }));
            var warningAssessments = new List<Dictionary<string, object>>();
            ApplyAgencyRelationshipAssessment(warningAssessments, warningPayload, "npc", "player");
            var neutralResult = ConversationRelationshipEvaluateApi(TestDict("campaignId", campaign, "timelineId", "agency_timeline", "exchangeId", "agency_warning",
                "mode", "party_chat", "playerHeroStringId", "player", "assessments", warningAssessments, "participants", new[] { "npc", "player" },
                "speakerResults", new List<Dictionary<string, object>> { TestDict("ok", true, "heroStringId", "npc", "participation", "speak", "reply", "My answer is still no.") }));
            add("warning_cannot_gain_relation_through_party_fallback", ReadDictionaryList(neutralResult, "nativeChanges").Count == 0, neutralResult);
            var replayParty = CompleteSequentialPartyRelationshipAssessments(TestDict("speakerResults", new List<Dictionary<string, object>> {
                TestDict("ok", true, "replayed", true, "heroStringId", "npc", "participation", "speak", "reply", "My answer is still no.") }),
                new List<Dictionary<string, object>>(), "player", "replayed_agency_turn");
            add("replayed_reply_cannot_farm_party_reactions", replayParty.Count == 0, replayParty);
            using (var connection = OpenCampaignConnection(campaign))
            {
                int before = QuerySql(connection, "SELECT letter_id FROM letters;").Count;
                var staleLetter = AgencyTestReply(AgencyTestProposal(AgencyTestContext()));
                staleLetter["motiveDecision"] = TestDict("conversationAgency", context);
                bool rolledBack = false;
                try { InsertLetter(connection, "npc", "Noble", "player", "Traveler", "My household matters to me.", "npc_reply", "test", "stale_parent", "", "", 20, 21, staleLetter); }
                catch { rolledBack = true; }
                add("stale_letter_and_negotiation_roll_back_together", rolledBack && QuerySql(connection, "SELECT letter_id FROM letters;").Count == before, null);
                var letter = AgencyTestReply(AgencyTestProposal(context)); letter["motiveDecision"] = TestDict("conversationAgency", context); letter["agencyTurnId"] = "mail_parent";
                var sent = InsertLetter(connection, "npc", "Noble", "player", "Traveler", "My household matters to me.", "npc_reply", "test", "parent", "", "", 20, 21, letter);
                var resent = InsertLetter(connection, "npc", "Noble", "player", "Traveler", "My household matters to me.", "npc_reply", "test", "parent", "", "", 20, 21, letter);
                add("letter_retry_does_not_duplicate_or_escalate", ReadBool(resent, "replayed", false) && ReadString(sent, "letterId", "") == ReadString(resent, "letterId", "")
                    && ReadInt(ReadConversationNegotiations(connection, "agency_timeline", "npc", "player").Single(), "revision", 0) == 2, resent);
                context["records"] = ReadConversationNegotiations(connection, "agency_timeline", "npc", "player");
            }
            var point = SaveSyncTestPointPayload(campaign, "agency_snapshot", 20d, "Agency isolated snapshot");
            point["timelineId"] = "agency_timeline";
            var registered = SaveSyncRegisterApi(point);
            using (var connection = OpenCampaignConnection(campaign))
                CommitConversationAgency(connection, AgencyTestReply(AgencyTestProposal(context)), context, "future_pressure");
            MarkSaveSyncActiveStateDirty(campaign);
            var restored = SaveSyncLoadApi(point, false);
            using (var connection = OpenCampaignConnection(campaign))
                add("save_sync_restores_refusal_revision_and_receipts", ReadBool(registered, "ok", false) && ReadBool(restored, "ok", false)
                    && ReadInt(ReadConversationNegotiations(connection, "agency_timeline", "npc", "player").Single(), "revision", 0) == 2
                    && QuerySql(connection, "SELECT receipt_id FROM conversation_negotiation_turns WHERE turn_id='future_pressure';").Count == 0, restored);
            return results;
        }
    }
}
