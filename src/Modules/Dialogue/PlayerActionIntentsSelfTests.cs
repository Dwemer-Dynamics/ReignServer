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
