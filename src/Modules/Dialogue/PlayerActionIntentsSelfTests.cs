using System;
using System.Collections.Generic;
using System.Linq;
using ReignBeta.Shared.Dialogue;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunPlayerActionIntentsSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            Action<string, bool, object> add = (id, pass, detail) => results.Add(TestDict("caseId", "player_actions_" + id, "passed", pass, "summary", id, "data", detail));
            string campaign = "player_actions_test_" + Guid.NewGuid().ToString("N");
            Dictionary<string, object> NewContribution(Dictionary<string, object> prior, string id)
            {
                var next = CloneDictionary(prior);
                PlayerInputSources.Remove(next); // A new authored contribution is not an observer clone of the prior one.
                next["sceneTurnId"] = id; next["contributionId"] = id; next["correlationId"] = id;
                next.Remove("inputReceipt"); next.Remove("inputSourceId");
                return next;
            }
            var payload = CloneDictionary(ReadDictionary(DefaultActionTestSnapshot(), "payload"));
            payload["campaignId"] = campaign; payload["timelineId"] = "player_actions_timeline";
            payload["sceneTurnId"] = "captured"; payload["correlationId"] = "captured";
            payload["playerText"] = "*I give her the 2000 denars to join as a companion* You can leave tomorrow.";
            var hero = ReadDictionary(payload, "hero");
            hero["encounteredResident"] = TestDict("recruitmentAgreementId", "resident:fixture");
            var failure = RunPlayerActionTurn(payload, () => TestDict("ok", false, "error", "Injected provider failure"));
            var rows = ReadDictionaryList(failure, "queuedDialogueActions");
            add("captured_payment_survives_reply_failure", ReadBool(failure, "ok", false) && rows.Count == 1
                && ReadString(ReadDictionary(rows.FirstOrDefault(), "record"), "authorizationMode", "") == PlayerActionRules.Policy, failure);
            var repeat = RunPlayerActionTurn(payload, () => TestDict("ok", false));
            add("retry_keeps_one_outbox_record", ReadDictionaryList(repeat, "queuedDialogueActions").Count == 1 && ReadDurableConversationActions(campaign).Count == 1, repeat);
            var first = rows.FirstOrDefault();
            if (first != null)
            {
                var poll = NextActions(TestDict("campaignId", campaign), new Dictionary<string, string>());
                var delivered = ReadDurableConversationAction(campaign, ReadString(first, "id", ""));
                delivered["deliveredTs"] = 1L; SaveDurableConversationAction(campaign, delivered);
                var retry = NextActions(TestDict("campaignId", campaign), new Dictionary<string, string>());
                add("lost_delivery_ack_reuses_id", ReadDictionaryList(poll, "actions").Count == 1
                    && ReadDictionaryList(retry, "actions").Count == 1
                    && ReadString(ReadDictionaryList(retry, "actions").FirstOrDefault(), "serverActionId", "") == ReadString(first, "id", ""), retry);
                ReportAction(TestDict("campaignId", campaign, "actionId", ReadString(first, "id", ""), "serverActionId", ReadString(first, "id", ""), "status", "completed", "message", "Verified fixture transfer."));
                add("terminal_report_stops_delivery", ReadDictionaryList(NextActions(TestDict("campaignId", campaign), new Dictionary<string, string>()), "actions").Count == 0, null);
                var stale = CloneDictionary(first); stale["status"] = "rejected";
                SaveDurableConversationAction(campaign, stale);
                add("late_reply_cannot_overwrite_applied_receipt", ReadString(stale, "status", "") == "completed"
                    && ReadString(ReadDurableConversationAction(campaign, ReadString(first, "id", "")), "status", "") == "completed", stale);
            }
            var alteredRetry = CloneDictionary(payload); alteredRetry["playerText"] = "I give you 2500 gold to join as a companion.";
            PlayerInputSources.Remove(alteredRetry);
            bool changedInputRejected = false, changedInputGenerated = false;
            try { RunPlayerActionTurn(alteredRetry, () => { changedInputGenerated = true; return TestDict("ok", true, "reply", "I will consider joining."); }); }
            catch (InvalidOperationException) { changedInputRejected = true; }
            add("same_turn_changed_terms_conflict", changedInputRejected && !changedInputGenerated
                && ReadDurableConversationActions(campaign).Count == 1, null);

            payload = NewContribution(payload, "two_gifts"); payload["playerText"] = "I give you 100 gold and I give you 50 coins.";
            var two = RunPlayerActionTurn(payload, () => throw new InvalidOperationException("Injected reply exception"));
            add("two_independent_intents_survive_exception", ReadDictionaryList(two, "queuedDialogueActions").Count == 2, two);
            // Opt-in terminal report: interruption, exact replay, identity conflict and the staged action delta.
            string protocolId = ReadString(ReadDictionaryList(two, "queuedDialogueActions").FirstOrDefault(), "id", "");
            var protocolRecord = ReadDictionary(ReadDurableConversationAction(campaign, protocolId), "record");
            string protocolActor = ReadString(protocolRecord, "actorHeroStringId", ""), protocolGeneration;
            using (var connection = OpenCampaignConnection(campaign))
                protocolGeneration = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
            Func<string, string, string> protocolReport = (player, message) => Json.Serialize(TestDict("campaignId", campaign,
                "serverActionId", protocolId, "actionId", protocolId, "timelineId", "player_actions_timeline", "playerHeroStringId", player,
                "sharedActivityGeneration", protocolGeneration, "status", "completed", "type", "RegularTransferGold", "message", message,
                "actorHeroStringId", protocolActor, "targetHeroStringId", ReadString(protocolRecord, "targetHeroStringId", ""),
                "termsHash", ReadString(protocolRecord, "termsHash", ""), "worldDay", 20d));
            Func<string, string, Dictionary<string, object>> protocolEnvelope = (player, json) =>
            {
                Reign.Core.Contracts.Actions.TerminalActionReportProtocol.TryReportSha256(json, out string sha, out _);
                return TestDict("schema", Reign.Core.Contracts.Actions.TerminalActionReportProtocol.RequestSchema, "campaignId", campaign,
                    "timelineId", "player_actions_timeline", "playerHeroStringId", player, "generation", protocolGeneration, "actionId", protocolId,
                    "termsHash", ReadString(protocolRecord, "termsHash", ""), "status", "completed", "reportJson", json, "reportSha256", sha);
            };
            string protocolJson = protocolReport(protocolActor, "A kind gift was handed over with thanks.");
            Func<string, int> protocolCount = sql => { using (var c = OpenCampaignConnection(campaign)) return QuerySql(c, sql, TestDict("id", protocolId)).Count; };
            const string directionalSql = "SELECT event_id FROM relationship_interaction_receipts WHERE event_id LIKE 'action_relationship_' || $id || '_%';";
            const string actionReceiptSql = "SELECT receipt_id FROM conversation_relationship_receipts WHERE source_turn_id=$id AND mode='completed_action' AND status='action_effect' AND severity_tier='routine';";
            // Derived court-popularity metrics and dependent standing/reputation rows for the action pair.
            var derivedArgs = TestDict("campaign", campaign, "actor", protocolActor, "target", ReadString(protocolRecord, "targetHeroStringId", ""));
            const string derivedMetricSql = "SELECT subject_id,positive_count,negative_count,calculated_day FROM court_dynamic_reputation_metrics WHERE campaign_id=$campaign AND timeline_id='player_actions_timeline' AND domain='popularity' AND subject_id IN ($actor,$target) ORDER BY subject_id;";
            Func<ReignDbConnection, string> derivedSnapshot = c => Json.Serialize(new object[] { QuerySql(c, derivedMetricSql, derivedArgs),
                QuerySql(c, "SELECT subject_id,standing_value,revision FROM character_public_standing WHERE campaign_id=$campaign AND timeline_id='player_actions_timeline' AND subject_id IN ($actor,$target) ORDER BY subject_id;", derivedArgs),
                QuerySql(c, "SELECT subject_id,tag_id,status,reputation_value FROM character_reputations WHERE campaign_id=$campaign AND timeline_id='player_actions_timeline' AND subject_id IN ($actor,$target) ORDER BY subject_id,tag_id;", derivedArgs) });
            Func<string> derivedCommitted = () => { using (var c = OpenCampaignConnection(campaign)) { EnsureSocialReputationSchema(c); EnsureCourtSocialReputationSchema(c); return derivedSnapshot(c); } };
            string derivedBefore = derivedCommitted();
            int derivedMetricsBefore, derivedMetricsInTransaction = -1;
            using (var c = OpenCampaignConnection(campaign)) derivedMetricsBefore = QuerySql(c, derivedMetricSql, derivedArgs).Count;
            bool interrupted = false;
            try
            {
                TerminalActionReportApi(protocolEnvelope(protocolActor, protocolJson), (step, c) =>
                {
                    if (step != 2) return;
                    derivedMetricsInTransaction = QuerySql(c, derivedMetricSql, derivedArgs).Count(r => ReadDouble(r, "calculated_day", 0d) == 20d);
                    throw new InvalidOperationException("Injected pre-commit failure.");
                });
            }
            catch (InvalidOperationException) { interrupted = true; }
            var afterInterrupt = ReadDurableConversationAction(campaign, protocolId);
            add("terminal_report_interruption_rolls_back_everything", interrupted && !IsProtocolOwnedTerminalAction(afterInterrupt)
                && !IsTerminalActionReportStatus(ReadString(afterInterrupt, "status", "")) && protocolCount(directionalSql) == 0 && protocolCount(actionReceiptSql) == 0, afterInterrupt);
            add("terminal_report_interruption_rolls_back_derived_standing", derivedMetricsBefore == 0 && derivedMetricsInTransaction > 0
                && derivedCommitted() == derivedBefore, TestDict("inTransaction", derivedMetricsInTransaction, "before", derivedBefore));
            var changedType = TerminalActionReportApi(protocolEnvelope(protocolActor, protocolJson.Replace("\"RegularTransferGold\"", "\"RegularDuelPlayer\"")));
            add("terminal_report_changed_action_type_rejected", ReadString(changedType, "reason", "") == "action_type_mismatch"
                && !IsProtocolOwnedTerminalAction(ReadDurableConversationAction(campaign, protocolId)) && protocolCount(directionalSql) == 0, changedType);
            // Read-only preflight: the same predicates, distinct dispositions, no writes and no campaign creation.
            Func<string, string, string, string, Dictionary<string, object>> preflight = (camp, id, gen, terms) => TerminalActionReportPreflightApi(
                new Dictionary<string, string> { ["campaignId"] = camp, ["timelineId"] = "player_actions_timeline", ["playerHeroStringId"] = protocolActor,
                    ["generation"] = gen, ["actionId"] = id, ["termsHash"] = terms });
            string protocolTerms = ReadString(protocolRecord, "termsHash", ""), rowBeforePreflight = Json.Serialize(ReadDurableConversationAction(campaign, protocolId));
            var eligible = preflight(campaign, protocolId, protocolGeneration, protocolTerms);
            string absentCampaign = "player_actions_absent_" + Guid.NewGuid().ToString("N");
            add("terminal_report_preflight_distinguishes_dispositions", ReadString(eligible, "disposition", "") == "eligible"
                && ReadString(eligible, "schema", "") == Reign.Core.Contracts.Actions.TerminalActionReportProtocol.PreflightSchema
                && ReadString(eligible, "reportType", "") == "RegularTransferGold" && ReadString(eligible, "actionId", "") == protocolId
                && ReadString(preflight(campaign, protocolId, "other_generation", protocolTerms), "disposition", "") == "stale"
                && ReadString(preflight(campaign, protocolId, protocolGeneration, new string('0', 64)), "disposition", "") == "conflict"
                && ReadString(preflight(campaign, "pa_absent_action", protocolGeneration, protocolTerms), "disposition", "") == "unknown"
                && ReadString(preflight(campaign, "da_other_class", protocolGeneration, protocolTerms), "disposition", "") == "unknown"
                && ReadString(preflight(absentCampaign, protocolId, protocolGeneration, protocolTerms), "disposition", "") == "unknown", eligible);
            // A known row made ineligible by a foreign projection owner answers ineligible only for
            // its exact current scope; stale or mismatched scope never permits legacy fallback.
            Action<string, bool> foreignOwner = (id, present) => { using (var c = OpenCampaignConnection(campaign)) { EnsureRebellionSchema(c);
                ExecuteSql(c, present ? @"INSERT INTO negotiated_action_drafts(negotiation_id,campaign_id,timeline_id,broker_hero_id,first_kingdom_id,
second_kingdom_id,first_ruler_id,second_ruler_id,command,terms_json,terms_hash,status,created_day,updated_day,expires_day,queued_action_id)
VALUES($n,$campaign,'player_actions_timeline','b','k1','k2','r1','r2','make_peace','{}','h','queued',20,20,30,$id);"
                    : "DELETE FROM negotiated_action_drafts WHERE negotiation_id=$n AND campaign_id=$campaign AND queued_action_id=$id;",
                    TestDict("n", "terminal_report_foreign_" + id, "campaign", campaign, "id", id)); } };
            string foreignId = ReadString(ReadDictionaryList(two, "queuedDialogueActions").ElementAtOrDefault(1), "id", "");
            string foreignTerms = ReadString(ReadDictionary(ReadDurableConversationAction(campaign, foreignId), "record"), "termsHash", "");
            foreignOwner(foreignId, true);
            var foreignIneligible = preflight(campaign, foreignId, protocolGeneration, foreignTerms);
            add("terminal_report_preflight_ineligible_requires_exact_current_scope", foreignId.Length > 0
                && ReadString(foreignIneligible, "disposition", "") == "ineligible" && ReadString(foreignIneligible, "reason", "") == "foreign_projection_owner"
                && ReadString(preflight(campaign, foreignId, "other_generation", foreignTerms), "disposition", "") == "stale"
                && ReadString(preflight(campaign, foreignId, protocolGeneration, new string('0', 64)), "disposition", "") == "conflict", foreignIneligible);
            foreignOwner(foreignId, false);
            add("terminal_report_preflight_is_write_free", Json.Serialize(ReadDurableConversationAction(campaign, protocolId)) == rowBeforePreflight
                && !ReignPostgreSqlStorage.CampaignExists(absentCampaign), null);
            var protocolFirst = TerminalActionReportApi(protocolEnvelope(protocolActor, protocolJson));
            var protocolEffect = ReadDictionary(protocolFirst, "nativeEffect");
            List<int> directionalDeltas;
            using (var connection = OpenCampaignConnection(campaign))
                directionalDeltas = QuerySql(connection, "SELECT native_delta FROM relationship_interaction_receipts WHERE event_id LIKE 'action_relationship_' || $id || '_%';",
                    TestDict("id", protocolId)).Select(r => ReadInt(r, "native_delta", 0)).Where(d => d != 0).ToList();
            add("terminal_report_stages_one_isolated_action_delta", ReadString(protocolFirst, "disposition", "") == "accepted"
                && ReadInt(protocolEffect, "delta", 0) != 0 && ReadInt(protocolEffect, "delta", 0) == CompletedActionSharedDelta(directionalDeltas)
                && protocolCount(directionalSql) == 2 && protocolCount(actionReceiptSql) == 1
                && IsProtocolOwnedTerminalAction(ReadDurableConversationAction(campaign, protocolId)), protocolFirst);
            // The retried first acceptance commits exactly one derived metric per changed target, at the report day.
            List<string> derivedTargets;
            List<Dictionary<string, object>> derivedMetrics;
            using (var connection = OpenCampaignConnection(campaign))
            {
                derivedTargets = QuerySql(connection, "SELECT target_id FROM relationship_interaction_receipts WHERE event_id LIKE 'action_relationship_' || $id || '_%' AND native_delta<>0;",
                    TestDict("id", protocolId)).Select(r => ReadString(r, "target_id", "")).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                derivedMetrics = QuerySql(connection, derivedMetricSql, derivedArgs);
            }
            string derivedAfterFirst = derivedCommitted();
            add("terminal_report_commits_one_set_of_derived_writes", derivedTargets.Count > 0
                && derivedMetrics.Select(r => ReadString(r, "subject_id", "")).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(derivedTargets)
                && derivedMetrics.All(r => ReadDouble(r, "calculated_day", 0d) == 20d)
                && ReadString(protocolFirst, "memoryProjectionStatus", "").Length > 0 && !protocolFirst.ContainsKey("projectionStatus"),
                TestDict("targets", derivedTargets, "metrics", derivedMetrics));
            // Action memory commits with projectedTs under the exact outbox row lock: one row per owner, stable IDs.
            const string memorySql = "SELECT owner_id,memory_id FROM memories WHERE event_id='action_' || $id ORDER BY owner_id,memory_id;";
            Func<ReignDbConnection, string> memoryIn = c => Json.Serialize(QuerySql(c, memorySql, TestDict("id", protocolId)));
            Func<string> memoryRows = () => { using (var c = OpenCampaignConnection(campaign)) return memoryIn(c); };
            Func<bool> onePerOwner = () => { using (var c = OpenCampaignConnection(campaign)) { var rows = QuerySql(c, memorySql, TestDict("id", protocolId));
                return rows.Count > 0 && rows.Select(r => ReadString(r, "owner_id", "")).Distinct().Count() == rows.Count; } };
            Func<bool> markerProjected = () => ReadDictionary(ReadDurableConversationAction(campaign, protocolId), "terminalReport")?.ContainsKey("projectedTs") == true;
            Action clearProjected = () => { using (var c = OpenCampaignConnection(campaign)) ExecuteSql(c,
                "UPDATE conversation_action_outbox SET payload_json=(payload_json::jsonb #- '{terminalReport,projectedTs}')::text WHERE action_id=$id;", TestDict("id", protocolId)); };
            Func<Action<ReignDbConnection>, bool> project = seam => ProjectAcceptedTerminalReport(campaign, protocolId, ReadString(protocolFirst, "reportSha256", ""),
                protocolGeneration, TryParseJsonObject(protocolJson), protocolRecord, ReadString(protocolFirst, "authoritativeStatus", ""), seam);
            string firstMemory = memoryRows();
            bool repeatsCompleted = new[] { 0, 1 }.All(_ => ReadString(TerminalActionReportApi(protocolEnvelope(protocolActor, protocolJson)), "memoryProjectionStatus", "") == "completed");
            add("terminal_report_exact_repeat_keeps_one_stable_memory_per_owner", ReadString(protocolFirst, "memoryProjectionStatus", "") == "completed"
                && markerProjected() && onePerOwner() && repeatsCompleted && memoryRows() == firstMemory, firstMemory);
            // A failure after the memory write but before projectedTs rolls both back and stays pending.
            clearProjected();
            bool rewrittenInTransaction = false;
            bool failedProjection = project(c => { rewrittenInTransaction = memoryIn(c) != firstMemory; throw new InvalidOperationException("Injected pre-marker failure."); });
            add("terminal_report_pre_marker_failure_rolls_back_memory", !failedProjection && rewrittenInTransaction
                && memoryRows() == firstMemory && !markerProjected(), memoryRows());
            bool resumed = project(null);
            string resumedMemory = memoryRows();
            add("terminal_report_resumed_projection_is_stable", resumed && markerProjected() && onePerOwner() && project(null)
                && ReadString(TerminalActionReportApi(protocolEnvelope(protocolActor, protocolJson)), "memoryProjectionStatus", "") == "completed"
                && memoryRows() == resumedMemory, resumedMemory);
            // Concurrent exact projection: the second caller waits on the row lock, then sees projectedTs.
            clearProjected();
            string lockedMemory = "";
            System.Threading.Tasks.Task<bool> concurrent = null;
            bool lockHolder = project(c => { lockedMemory = memoryIn(c); concurrent = System.Threading.Tasks.Task.Run(() => project(null)); concurrent.Wait(250); });
            bool waiter = concurrent != null && concurrent.Wait(30000) && concurrent.Result;
            add("terminal_report_concurrent_projection_writes_once", lockHolder && waiter && markerProjected() && onePerOwner()
                && lockedMemory.Length > 0 && memoryRows() == lockedMemory, TestDict("locked", lockedMemory, "committed", memoryRows()));
            var protocolReplay =TerminalActionReportApi(protocolEnvelope(protocolActor, protocolJson));
            add("terminal_report_exact_replay_leaves_derived_writes", derivedCommitted() == derivedAfterFirst, null);
            add("terminal_report_exact_replay_reuses_receipt", ReadString(protocolReplay, "reason", "") == "exact_replay"
                && ReadString(ReadDictionary(protocolReplay, "nativeEffect"), "receiptId", "") == ReadString(protocolEffect, "receiptId", "")
                && ReadInt(ReadDictionary(protocolReplay, "nativeEffect"), "delta", 0) == ReadInt(protocolEffect, "delta", 0)
                && protocolCount(directionalSql) == 2 && protocolCount(actionReceiptSql) == 1, protocolReplay);
            // A foreign projection owner appearing after acceptance cannot downgrade the protocol owner.
            foreignOwner(protocolId, true);
            var foreignReplay = TerminalActionReportApi(protocolEnvelope(protocolActor, protocolJson));
            var foreignOwned = preflight(campaign, protocolId, protocolGeneration, protocolTerms);
            foreignOwner(protocolId, false);
            add("terminal_report_accepted_owner_survives_later_foreign_owner", ReadString(foreignReplay, "reason", "") == "exact_replay"
                && ReadString(foreignOwned, "disposition", "") == "accepted" && ReadString(foreignOwned, "reason", "") == "protocol_owned"
                && protocolCount(directionalSql) == 2 && protocolCount(actionReceiptSql) == 1, foreignReplay);
            add("terminal_report_changed_payload_or_owner_conflicts",
                ReadString(TerminalActionReportApi(protocolEnvelope(protocolActor, protocolReport(protocolActor, "A different result."))), "disposition", "") == "conflict"
                && ReadString(TerminalActionReportApi(protocolEnvelope("intruder_hero", protocolReport("intruder_hero", "A kind gift was handed over with thanks."))), "disposition", "") == "conflict"
                && ReadString(ReadDictionary(ReadDurableConversationAction(campaign, protocolId), "terminalReport"), "sha256", "") == ReadString(protocolFirst, "reportSha256", ""), null);
            var ownedPreflight = preflight(campaign, protocolId, protocolGeneration, protocolTerms);
            add("terminal_report_preflight_reports_protocol_owner", ReadString(ownedPreflight, "disposition", "") == "accepted"
                && ReadString(ownedPreflight, "reason", "") == "protocol_owned", ownedPreflight);
            // A late legacy report of the same status is answered from the protocol row without projections.
            var legacyLate = ReportAction(TestDict("campaignId", campaign, "serverActionId", protocolId, "status", "completed", "result", TestDict("message", "Late legacy report.")));
            add("terminal_report_legacy_report_defers_to_protocol_owner", ReadString(legacyLate, "terminalReportOwner", "") == "protocol"
                && ReadString(legacyLate, "status", "") == "completed" && !legacyLate.ContainsKey("memoryStore")
                && ReadString(ReadDictionary(ReadDurableConversationAction(campaign, protocolId), "report"), "message", "") == "A kind gift was handed over with thanks."
                && protocolCount(directionalSql) == 2 && protocolCount(actionReceiptSql) == 1, legacyLate);
            // A legacy terminal winner cannot be upgraded into the protocol.
            string legacyId = ReadString(ReadDictionaryList(two, "queuedDialogueActions").ElementAtOrDefault(1), "id", "");
            ReportAction(TestDict("campaignId", campaign, "serverActionId", legacyId, "status", "completed", "result", TestDict("message", "Legacy handover.")));
            var legacyWinner = preflight(campaign, legacyId, protocolGeneration, ReadString(ReadDictionary(ReadDurableConversationAction(campaign, legacyId), "record"), "termsHash", ""));
            add("terminal_report_preflight_keeps_legacy_winner", legacyId.Length > 0 && ReadString(legacyWinner, "disposition", "") == "conflict"
                && ReadString(legacyWinner, "reason", "") == "legacy_terminal_status"
                && !IsProtocolOwnedTerminalAction(ReadDurableConversationAction(campaign, legacyId)), legacyWinner);
            var legacyOverwrite = ReadDurableConversationAction(campaign, protocolId); legacyOverwrite["report"] = TestDict("message", "Saved action result.");
            SaveDurableConversationAction(campaign, legacyOverwrite);
            add("terminal_report_evidence_survives_same_status_legacy_save", ReadString(ReadDictionary(ReadDurableConversationAction(campaign, protocolId), "report"), "message", "")
                == "A kind gift was handed over with thanks.", null);
            Func<string, Dictionary<string, object>> protocolAck = ackGeneration => ConversationRelationshipNativeReceiptApi(TestDict("campaignId", campaign,
                "receiptIds", new List<string> { ReadString(protocolEffect, "receiptId", "") }, "nativeReceipt", TestDict(
                    "schema", "reign-native-relationship-receipt-v2", "subjectId", ReadString(protocolEffect, "subjectId", ""), "targetId", ReadString(protocolEffect, "targetId", ""),
                    "priorNativeRelation", 0, "nativeRelation", ReadInt(protocolEffect, "delta", 0), "delta", ReadInt(protocolEffect, "delta", 0),
                    "timelineId", "player_actions_timeline", "reportGeneration", ackGeneration, "reportSha256", ReadString(protocolFirst, "reportSha256", ""))));
            add("terminal_report_ack_requires_generation_binding", !ReadBool(protocolAck("other_generation"), "ok", true)
                && ReadBool(protocolAck(protocolGeneration), "ok", false), null);
            payload = NewContribution(payload, "refused_gift"); payload["playerText"] = "I give you 100 gold.";
            var refused = RunPlayerActionTurn(payload, () => TestDict("ok", true, "reply", "I refuse to accept your gold."));
            add("specific_gift_refusal_has_no_transfer", ReadDictionaryList(refused, "queuedDialogueActions").Count == 0, refused);
            payload = NewContribution(payload, "refused_job");
            var job = RunPlayerActionTurn(payload, () => TestDict("ok", true, "reply", "I will not join your clan."));
            add("employment_refusal_does_not_cancel_gift", ReadDictionaryList(job, "queuedDialogueActions").Count == 1, job);
            payload = NewContribution(payload, "gesture_only");
            payload["playerText"] = "*I give her a confident glance back so she knows that I am with her as I wait for the responses.*";
            var gesture = RunPlayerActionTurn(payload, () => TestDict("ok", true, "reply", "I am ready to hear them."));
            add("described_glance_is_not_an_item_handover", CurrentPlayerTransfers(payload).Count == 0
                && ReadDictionaryList(gesture, "playerActionOutcomes").Count == 0
                && ReadString(gesture, "reply", "") == "I am ready to hear them.", gesture);
            payload["playerText"] = "I give her an iron sword and offer a smile.";
            add("tangible_gift_with_gesture_is_still_detected", CurrentPlayerTransfers(payload).Count == 1, null);
            payload["channel"] = "correspondence";
            add("letter_cannot_deliver_physical_coins", CurrentPlayerTransfers(payload).Count == 0, null);
            payload["channel"] = "in_person"; payload["guardedPromptOverride"] = true;
            add("test_directive_keeps_existing_gate", CurrentPlayerTransfers(payload).Count == 0, null);
            payload.Remove("guardedPromptOverride");
            var group = CloneDictionary(payload);
            group["attendees"] = new List<Dictionary<string, object>> { TestDict("heroStringId", "npc_a"), TestDict("heroStringId", "npc_b") };
            add("two_npc_attendees_make_you_ambiguous", !SinglePlayerTransferRecipient(group)
                && CurrentPlayerTransfers(group).All(i => i.Error.Length > 0), null);
            var hypothetical = CloneDictionary(payload);
            hypothetical["playerText"] = "For example:\nI give you 2000 gold.";
            var hypotheticalRecord = TestDict("source", "dialogue_auto_commit", "actorHeroStringId", ReadString(payload, "playerHeroStringId", ""));
            var hypotheticalErrors = new List<string>();
            BindDialogueActionAuthority(hypotheticalRecord, TestDict(), hypothetical, "transfer_gold", "RegularTransferGold", "hypothetical", hypotheticalErrors);
            add("npc_planner_cannot_turn_example_into_player_payment", hypotheticalErrors.Any(e => e.Contains("hypothetical")), hypotheticalErrors);
            var together = NewContribution(payload, "pay_and_join");
            together["playerText"] = "I give you 2000 gold to join as a companion.";
            ReadDictionary(ReadDictionary(together, "hero"), "encounteredResident")["schema"] = "reign-encountered-resident-v1";
            together["conversationAgencyReceipt"] = TestDict("ok", true, "receiptId", "fixture_accepted", "updates",
                new List<Dictionary<string, object>> { TestDict("topic", "allegiance", "disposition", "accepted", "id", "agreement_fixture", "revision", 1,
                    "terms", TestDict("gold", 2000)) });
            var joined = RunPlayerActionTurn(together, () => TestDict("ok", true, "reply", "I join you as your companion. My husband could visit later."));
            var joinedRows = ReadDictionaryList(joined, "queuedDialogueActions");
            add("payment_and_companion_are_separate_actions", joinedRows.Count == 2
                && joinedRows.Any(r => ReadString(r, "command", "") == "transfer_gold")
                && joinedRows.Any(r => ReadString(r, "command", "") == "recruit_encountered_resident"), joined);
            var recruitment = joinedRows.FirstOrDefault(r => ReadString(r, "command", "") == "recruit_encountered_resident");
            var recruitmentTerms = TryParseJsonObject(ReadString(ReadDictionary(recruitment, "record"), "termsJson", "{}"));
            add("recruitment_depends_on_verified_advance", ReadStringList(recruitmentTerms, "paymentDependencies").Count == 1
                && ReadInt(recruitmentTerms, "agreedGold", -1) == 2000, recruitmentTerms);
            foreach (string decision in new[] { "I join you as your companion. My husband could visit later.", "I accept. If my family visits, they will need a room." })
                add("unrelated_condition_" + results.Count, ResidentHasUnconditionalConsent(decision), decision);
            foreach (string decision in new[] { "I could join if you pay.", "I will join tomorrow.", "I agree, provided you release me first.", "I join you. But only if my husband agrees.", "I join you. If you pay me." })
                add("real_condition_" + results.Count, !ResidentHasUnconditionalConsent(decision), decision);
            add("purse_claim_requires_receipt", VisibleReplyClaimsCompletedWorldAction("Two thousand denars in my purse.", out _), null);
            var visible = VisibleFailedRepairWithoutEffects(TestDict("reply", "Two thousand denars in my purse. I am your companion."));
            add("invalid_reply_cannot_preserve_success_claim", !ReadString(visible, "reply", "").Contains("in my purse")
                && !ReadString(visible, "reply", "").Contains("I am your companion")
                && ReadString(visible, "reply", "").StartsWith("I hear you.", StringComparison.Ordinal)
                && ReadString(visible, "reply", "").Contains("could not be verified"), visible);
            var facts = TestDict("trait.judgment", 83);
            add("only_matching_evidence_value_normalizes", AgencyEvidenceKey("trait.judgment=83", facts) == "trait.judgment"
                && AgencyEvidenceKey("trait.judgment=82", facts) != "trait.judgment" && AgencyEvidenceKey("trait.pragmatism=83", facts) == "trait.pragmatism=83", null);
            add("typography_does_not_remove_words", AgencyContainsExactQuote("I'm willing to join.", "I’m willing to join.")
                && !AgencyContainsExactQuote("I am not willing to join.", "I am willing to join."), null);
            Func<string, Dictionary<string, object>> stagedNpc = id => TestDict("serverActionId", id, "actionId", id,
                "campaignId", campaign, "source", "dialogue_auto_commit", "command", "accept_temporary_party_guest",
                "actorHeroStringId", "npc_batch", "targetHeroStringId", "player_batch", "termsHash", "fixture_terms",
                "termsJson", Json.Serialize(TestDict("timelineId", "player_actions_timeline")));
            var failedNpcPayload = NewContribution(payload, "failed_npc_without_handover");
            failedNpcPayload["playerText"] = "The weather is pleasant.";
            string failedNpcId = "";
            var failedNpcResult = RunPlayerActionTurn(failedNpcPayload, () => {
                var row = QueueAction(campaign, stagedNpc("unbound")); failedNpcId = ReadString(row, "id", "");
                return TestDict("ok", false, "queuedDialogueActions", new List<Dictionary<string, object>> { row });
            });
            add("failed_npc_response_discards_staged_native_action", !ReadBool(failedNpcResult, "ok", true)
                && ReadDictionaryList(failedNpcResult, "queuedDialogueActions").Count == 0
                && ReadDurableConversationAction(campaign, failedNpcId) == null, failedNpcResult);
            var mixedFailurePayload = NewContribution(payload, "failed_npc_with_handover");
            mixedFailurePayload["playerText"] = "I give you 17 gold.";
            string discardedNpcId = "";
            var mixedFailure = RunPlayerActionTurn(mixedFailurePayload, () => {
                discardedNpcId = ReadString(QueueAction(campaign, stagedNpc("unbound")), "id", "");
                throw new InvalidOperationException("Injected storage failure after action staging.");
            });
            add("failed_npc_keeps_only_independent_player_handover", ReadBool(mixedFailure, "ok", false)
                && ReadDictionaryList(mixedFailure, "queuedDialogueActions").Count == 1
                && ReadString(mixedFailure, "reply", "").StartsWith("I hear you. Let us keep talking", StringComparison.Ordinal)
                && ReadString(ReadDictionaryList(mixedFailure, "queuedDialogueActions").Single(), "id", "").StartsWith("pa_", StringComparison.Ordinal)
                && ReadDurableConversationAction(campaign, discardedNpcId) == null, mixedFailure);

            string generation;
            using (var connection = OpenCampaignConnection(campaign))
                generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
            var batchPayload = TestDict("campaignId", campaign, "timelineId", "player_actions_timeline", "sceneTurnId", "atomic_batch");
            Func<string, Dictionary<string, object>> batchRow = id => TestDict("id", id, "campaignId", campaign,
                "status", "queued", "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "record", stagedNpc(id));
            Func<List<Dictionary<string, object>>> batchRows = () => new List<Dictionary<string, object>> { batchRow("da_batch_a"), batchRow("da_batch_b") };
            var acceptedResponse = TestDict("ok", true, "reply", "I agree to the described terms.");
            for (int boundary = 1; boundary <= 3; boundary++)
            {
                bool failed = false, invisible = true;
                try { PublishConversationActionBatch(campaign, batchPayload, acceptedResponse, batchRows(), generation, index => {
                    invisible &= !ReadDurableConversationActions(campaign).Any(r => ReadString(r, "id", "").StartsWith("da_batch_", StringComparison.Ordinal));
                    if (index == boundary) throw new InvalidOperationException("Injected batch boundary failure.");
                }); } catch (InvalidOperationException) { failed = true; }
                add("publication_fault_rolls_back_all_actions_" + boundary, failed && invisible
                    && ReadDurableConversationAction(campaign, "da_batch_a") == null && ReadDurableConversationAction(campaign, "da_batch_b") == null, null);
            }
            var batch = PublishConversationActionBatch(campaign, batchPayload, acceptedResponse, batchRows(), generation);
            var duplicateBatch = PublishConversationActionBatch(campaign, batchPayload, acceptedResponse, batchRows(), generation);
            add("atomic_batch_commits_once_without_json_cache", ReadBool(batch, "atomic", false) && ReadInt(batch, "createdCount", 0) == 2
                && ReadInt(duplicateBatch, "createdCount", -1) == 0
                && ReadActionQueueUnlocked(campaign).Count(r => ReadString(r, "id", "").StartsWith("da_batch_", StringComparison.Ordinal)) == 2, batch);
            var changedBatch = new List<Dictionary<string, object>> { batchRow("da_batch_must_rollback"), batchRow("da_batch_b") };
            bool changedReplyRejected = false;
            try { PublishConversationActionBatch(campaign, batchPayload, TestDict("ok", true, "reply", "I refuse those terms."), changedBatch, generation); }
            catch (InvalidOperationException) { changedReplyRejected = true; }
            add("accepted_prose_binding_rejects_changed_reply_and_partial_batch", changedReplyRejected
                && ReadDurableConversationAction(campaign, "da_batch_must_rollback") == null
                && ReadString(ReadDurableConversationAction(campaign, "da_batch_b"), "acceptedResponseHash", "") == PromptHash(ReadString(acceptedResponse, "reply", "")), null);
            bool mixedBatchRejected = false;
            try { PublishConversationActionBatch(campaign, batchPayload, acceptedResponse,
                new List<Dictionary<string, object>> { batchRow("da_mixed_must_rollback"), batchRow("legacy_directive_fixture") }, generation); }
            catch (InvalidOperationException) { mixedBatchRejected = true; }
            add("mixed_legacy_and_durable_batch_rejected_before_write", mixedBatchRejected
                && ReadDurableConversationAction(campaign, "da_mixed_must_rollback") == null, null);
            var auditRows = new List<Dictionary<string, object>> { batchRow("da_audit_a"), batchRow("da_audit_b") };
            var auditFailure = PublishConversationActionBatch(campaign, batchPayload, acceptedResponse, auditRows, generation,
                auditWriter: _ => throw new System.IO.IOException("Injected postcommit audit projection failure."));
            var auditRetry = PublishConversationActionBatch(campaign, batchPayload, acceptedResponse, auditRows, generation);
            add("postcommit_audit_failure_keeps_truthful_committed_receipt", ReadBool(auditFailure, "committed", false)
                && ReadInt(auditFailure, "auditProjectionPending", 0) == 2 && ReadInt(auditRetry, "createdCount", -1) == 0
                && ReadDurableConversationAction(campaign, "da_audit_a") != null && ReadDurableConversationAction(campaign, "da_audit_b") != null, auditFailure);
            AdvanceMemoryRestoreGeneration(campaign);
            bool staleBatchRejected = false;
            try { PublishConversationActionBatch(campaign, batchPayload, acceptedResponse, new List<Dictionary<string, object>> { batchRow("da_stale_batch") }, generation); }
            catch (InvalidOperationException) { staleBatchRejected = true; }
            add("restored_campaign_rejects_pre_restore_action_publication", staleBatchRejected
                && ReadDurableConversationAction(campaign, "da_stale_batch") == null, null);
            var staleReplay = TerminalActionReportApi(protocolEnvelope(protocolActor, protocolJson));
            add("terminal_report_stale_generation_offers_no_effect", ReadString(staleReplay, "disposition", "") == "stale"
                && ReadDictionary(staleReplay, "nativeEffect") == null && !ReadBool(protocolAck(protocolGeneration), "ok", true), staleReplay);
            var point = SaveSyncTestPointPayload(campaign, "player_action_snapshot", 20d, "Player action isolated snapshot");
            point["timelineId"] = "player_actions_timeline";
            var registered = SaveSyncRegisterApi(point);
            int before = ReadDurableConversationActions(campaign).Count;
            payload = NewContribution(payload, "future_gift");
            RunPlayerActionTurn(payload, () => TestDict("ok", true, "reply", "Thank you for the offer."));
            MarkSaveSyncActiveStateDirty(campaign);
            var restored = SaveSyncLoadApi(point, false);
            add("save_sync_rewinds_outbox_and_ignores_newer_json", ReadBool(registered, "ok", false) && ReadBool(restored, "ok", false)
                && ReadDurableConversationActions(campaign).Count == before
                && !ReadActionQueueUnlocked(campaign).Any(r => ReadString(ReadDictionary(r, "record"), "negotiationId", "") == "future_gift"), restored);
            return results;
        }
    }
}
