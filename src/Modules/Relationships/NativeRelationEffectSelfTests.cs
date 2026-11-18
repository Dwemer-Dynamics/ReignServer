using System;
using System.Collections.Generic;
using System.Linq;
using ReignBeta.Shared;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        private static List<Dictionary<string, object>> RunNativeRelationEffectAssertions()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed) => results.Add(TestDict("caseId", "native_relation_effect_" + id, "passed", passed, "summary", id));
            var journal = new List<NativeRelationEffectReceipt>();
            var saved = new List<NativeRelationEffectReceipt>();
            int native = 10, calls = 0;
            NativeRelationEffectReceipt Apply(string id, int delta, Action<int> action = null) => NativeRelationEffectLedger.Apply(journal,
                new[] { id }, "npc", "player", delta, () => native, action ?? (value => { calls++; native = Math.Clamp(native + value * (value > 0 ? 2 : 1), -100, 100); }), saved.Add, out _);
            var first = Apply("first", 3);
            Check("modifier_is_observed_not_silently_removed", first.RequestedDelta == 3 && first.Before == 10 && first.After == 16 && first.ObservationComplete);
            var retry = Apply("first", 3);
            Check("failed_ack_retry_uses_original_receipt", calls == 1 && ReferenceEquals(first, retry) && saved.Count == 1 && native == 16);
            var reversed = NativeRelationEffectLedger.Apply(journal, new[] { "first" }, "player", "npc", 3, () => native, _ => calls++, saved.Add, out bool reverseReplay);
            Check("reversed_pair_retry_preserves_original_observation", reverseReplay && ReferenceEquals(first, reversed) && calls == 1);
            native = 99;
            var capped = Apply("cap", 3);
            Check("native_cap_is_distinct_from_requested_delta", capped.Before == 99 && capped.After == 100 && capped.RequestedDelta == 3);
            var negative = Apply("negative", -1);
            Check("legitimate_negative_not_amplified_or_flattened", negative.Before == 100 && negative.After == 99);
            int beforeNoop = calls;
            Apply("zero", 0);
            Check("zero_delta_preserves_receipt_without_native_call", calls == beforeNoop);
            bool changedRejected = false, wrongPairRejected = false, overlapRejected = false;
            try { Apply("first", 4); } catch (InvalidOperationException) { changedRejected = true; }
            try { NativeRelationEffectLedger.Apply(journal, new[] { "first" }, "other", "player", 3, () => native, _ => calls++, saved.Add, out _); }
            catch (InvalidOperationException) { wrongPairRejected = true; }
            try { NativeRelationEffectLedger.Apply(journal, new[] { "first", "new" }, "npc", "player", 3, () => native, _ => calls++, saved.Add, out _); }
            catch (InvalidOperationException) { overlapRejected = true; }
            Check("changed_delta_pair_and_partial_overlap_rejected", changedRejected && wrongPairRejected && overlapRejected && calls == beforeNoop);
            var failed = Apply("callback_error", 2, value => { calls++; native = Math.Clamp(native + value, -100, 100); throw new InvalidOperationException("Native callback failed after effect."); });
            int afterError = native, afterErrorCalls = calls;
            Apply("callback_error", 2);
            Check("callback_failure_cannot_repeat_native_effect", failed.ObservationComplete && failed.ApplicationError.Length > 0 && native == afterError && calls == afterErrorCalls);
            var restored = saved.Select(r => new NativeRelationEffectReceipt { ReceiptIds = r.ReceiptIds.ToArray(), SubjectId = r.SubjectId, TargetId = r.TargetId,
                RequestedDelta = r.RequestedDelta, Before = r.Before, After = r.After, ObservationComplete = r.ObservationComplete,
                ApplicationError = r.ApplicationError, AppliedUtc = r.AppliedUtc }).ToList();
            var restoredReceipt = NativeRelationEffectLedger.Apply(restored, new[] { "first" }, "npc", "player", 3, () => native, _ => calls++, _ => { }, out bool replayed);
            Check("restored_journal_preserves_old_observation", replayed && restoredReceipt.After == 16 && native == afterError && calls == afterErrorCalls);
            // Pure classification of what a persisted journal proves (Apply's identity rules).
            var unobserved = new NativeRelationEffectReceipt { ReceiptIds = new[] { "unobserved" }, SubjectId = "npc", TargetId = "player", RequestedDelta = 2 };
            var classified = restored.Concat(new[] { unobserved }).ToList();
            Check("classify_distinguishes_absent_complete_incomplete_mismatch",
                NativeRelationEffectLedger.Classify(classified, new[] { "missing" }, "npc", "player", 3) == NativeRelationEffectClass.Absent
                && NativeRelationEffectLedger.Classify(classified, new[] { "first" }, "player", "npc", 3) == NativeRelationEffectClass.Complete
                && NativeRelationEffectLedger.Classify(classified, new[] { "callback_error" }, "npc", "player", 2) == NativeRelationEffectClass.Complete
                && NativeRelationEffectLedger.Classify(classified, new[] { "unobserved" }, "npc", "player", 2) == NativeRelationEffectClass.Incomplete
                && NativeRelationEffectLedger.Classify(classified, new[] { "first" }, "npc", "player", 4) == NativeRelationEffectClass.Mismatch
                && NativeRelationEffectLedger.Classify(classified, new[] { "first" }, "other", "player", null) == NativeRelationEffectClass.Mismatch
                && NativeRelationEffectLedger.Classify(classified, new[] { "first", "new" }, "npc", "player", 3) == NativeRelationEffectClass.Mismatch);
            // Before throws: no entry, no native call. persist throws after the native call: the
            // cache has the entry but the persisted list does not (the unpersisted gap).
            var cache = new List<NativeRelationEffectReceipt>();
            var persisted = new List<NativeRelationEffectReceipt>();
            int throwCalls = 0;
            bool beforeThrew = false, persistThrew = false;
            try { NativeRelationEffectLedger.Apply(cache, new[] { "before" }, "npc", "player", 2, () => throw new InvalidOperationException("read"), _ => throwCalls++, persisted.Add, out _); }
            catch (InvalidOperationException) { beforeThrew = true; }
            Check("before_failure_leaves_no_entry_and_no_native_call", beforeThrew && cache.Count == 0 && throwCalls == 0
                && NativeRelationEffectLedger.Classify(persisted, new[] { "before" }, "npc", "player", 2) == NativeRelationEffectClass.Absent);
            try { NativeRelationEffectLedger.Apply(cache, new[] { "persist" }, "npc", "player", 2, () => 0, _ => throwCalls++, _ => throw new InvalidOperationException("persist"), out _); }
            catch (InvalidOperationException) { persistThrew = true; }
            Check("persist_failure_is_cached_but_unpersisted", persistThrew && throwCalls == 1
                && NativeRelationEffectLedger.Classify(cache, new[] { "persist" }, "npc", "player", 2) == NativeRelationEffectClass.Complete
                && NativeRelationEffectLedger.Classify(persisted, new[] { "persist" }, "npc", "player", 2) == NativeRelationEffectClass.Absent);
            string campaign = "native_relation_receipt_test_" + Guid.NewGuid().ToString("N");
            try
            {
                using (var connection = Program.OpenCampaignConnection(campaign))
                {
                    EnsureConversationRelationshipSchema(connection);
                    foreach (string id in new[] { "a", "b", "legacy", "replay" })
                        Program.ExecuteSql(connection, @"INSERT INTO conversation_relationship_receipts(receipt_id,campaign_id,exchange_id,observer_id,target_id,
final_delta,prior_native_relation,resulting_native_relation,native_pair_delta,created_ts)
VALUES($id,$campaign,$id,$observer,'player',3,10,13,3,1);", TestDict("id", id, "campaign", campaign, "observer", id == "b" ? "other" : "npc"));
                }
                var measured = TestDict("schema", "reign-native-relationship-receipt-v2", "subjectId", "npc", "targetId", "player",
                    "delta", 3, "priorNativeRelation", 10, "nativeRelation", 16);
                Dictionary<string, object> Ack(string[] ids, Dictionary<string, object> value) => ConversationRelationshipNativeReceiptApi(
                    TestDict("campaignId", campaign, "receiptIds", ids, "nativeReceipt", value));
                Check("wrong_pair_is_rejected_before_any_batch_write", !ReadBool(Ack(new[] { "a", "b" }, measured), "ok", true));
                using (var connection = Program.OpenCampaignConnection(campaign))
                    Check("mixed_pair_rejection_is_atomic", Program.QuerySql(connection, "SELECT * FROM conversation_relationship_receipts WHERE native_application_status='applied';").Count == 0);
                Check("wrong_requested_delta_is_rejected", !ReadBool(Ack(new[] { "a" }, new Dictionary<string, object>(measured) { ["delta"] = 4 }), "ok", true));
                Check("matching_measured_receipt_is_accepted", ReadBool(Ack(new[] { "a" }, measured), "ok", false));
                using (var connection = Program.OpenCampaignConnection(campaign))
                {
                    var row = Program.QuerySql(connection, "SELECT * FROM conversation_relationship_receipts WHERE receipt_id='a';").Single();
                    var observation = TryParseJsonObject(ReadString(row, "native_receipt_json", "{}"));
                    Check("proposed_directional_and_observed_deltas_are_distinct", ReadInt(row, "final_delta", 0) == 3
                        && ReadInt(row, "native_pair_delta", 0) == 6 && ReadInt(observation, "plannedNativeDelta", 0) == 3
                        && ReadInt(observation, "observedDelta", 0) == 6);
                }
                Check("acknowledgment_replay_is_noop", ReadInt(Ack(new[] { "a" }, measured), "updatedCount", -1) == 0);
                Check("acknowledged_observation_cannot_change", !ReadBool(Ack(new[] { "a" }, new Dictionary<string, object>(measured) { ["nativeRelation"] = 17 }), "ok", true));
                Ack(new[] { "legacy" }, TestDict("nativeRelation", 16, "delta", 3));
                using (var connection = Program.OpenCampaignConnection(campaign))
                {
                    var row = Program.QuerySql(connection, "SELECT * FROM conversation_relationship_receipts WHERE receipt_id='legacy';").Single();
                    Check("missing_legacy_before_value_is_not_invented", ReadInt(row, "prior_native_relation", 0) == 10
                        && ReadString(TryParseJsonObject(ReadString(row, "native_receipt_json", "{}")), "measurementStatus", "") == "legacy_unmeasured_transition");
                    var pending = Program.QuerySql(connection, "SELECT * FROM conversation_relationship_receipts WHERE receipt_id='replay';").Single();
                    var changes = new Dictionary<string, Dictionary<string, object>>();
                    ReconcilePendingConversationNativeApplication(connection, pending,
                        new List<Dictionary<string, object>> { TestDict("subjectId", "npc", "targetId", "player", "nativeRelation", 13) },
                        "player", changes, 2L, true);
                    Check("matching_current_number_is_not_proof_of_application", changes.Count == 1
                        && ReadString(Program.QuerySql(connection, "SELECT * FROM conversation_relationship_receipts WHERE receipt_id='replay';").Single(), "native_application_status", "") == "pending");
                }
            }
            catch (Exception ex) { results.Add(TestDict("caseId", "native_relation_effect_suite_exception", "passed", false, "summary", Program.LimitText(ex.ToString(), 2500))); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
