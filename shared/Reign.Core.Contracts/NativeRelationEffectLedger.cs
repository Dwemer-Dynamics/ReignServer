using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBeta.Shared
{
    public sealed class NativeRelationEffectReceipt
    {
        public string[] ReceiptIds { get; set; } = Array.Empty<string>();
        public string SubjectId { get; set; } = "";
        public string TargetId { get; set; } = "";
        public int RequestedDelta { get; set; }
        public int Before { get; set; }
        public int After { get; set; }
        public bool ObservationComplete { get; set; }
        public string ApplicationError { get; set; } = "";
        public string AppliedUtc { get; set; } = "";
    }

    // What a persisted journal proves about one native effect identity.
    public enum NativeRelationEffectClass { Absent, Complete, Incomplete, Mismatch }

    // The caller runs this on the native thread and persists the returned receipt
    // with the same save as the relationship. The native API owns modifiers/events.
    public static class NativeRelationEffectLedger
    {
        // Pure and read-only, using Apply's own identity rules. Absent: no entry shares an id.
        // Complete: the one exact entry observed Before and After (ApplicationError allowed,
        // it still means attempted). Incomplete: that entry lacks an After observation.
        // Mismatch: reused ids with a different pair, delta or id set, or invalid identity.
        // A null requestedDelta compares ids and pair only (the server did not restate it).
        public static NativeRelationEffectClass Classify(IEnumerable<NativeRelationEffectReceipt> journal,
            IEnumerable<string> receiptIds, string subjectId, string targetId, int? requestedDelta)
        {
            var ids = (receiptIds ?? Enumerable.Empty<string>()).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (journal == null || ids.Length == 0 || ids.Length > 64 || ids.Any(id => id.Length > 200)
                || string.IsNullOrWhiteSpace(subjectId) || string.IsNullOrWhiteSpace(targetId) || subjectId == targetId)
                return NativeRelationEffectClass.Mismatch;
            var previous = journal.Where(r => r != null && (r.ReceiptIds ?? Array.Empty<string>()).Any(ids.Contains)).ToList();
            if (previous.Count == 0) return NativeRelationEffectClass.Absent;
            var receipt = previous[0];
            bool samePair = (receipt.SubjectId == subjectId && receipt.TargetId == targetId)
                || (receipt.SubjectId == targetId && receipt.TargetId == subjectId);
            if (previous.Count != 1 || !receipt.ReceiptIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(ids)
                || !samePair || (requestedDelta.HasValue && receipt.RequestedDelta != requestedDelta.Value))
                return NativeRelationEffectClass.Mismatch;
            return receipt.ObservationComplete ? NativeRelationEffectClass.Complete : NativeRelationEffectClass.Incomplete;
        }

        public static NativeRelationEffectReceipt Apply(IList<NativeRelationEffectReceipt> journal,
            IEnumerable<string> receiptIds, string subjectId, string targetId, int requestedDelta,
            Func<int> readRelation, Action<int> applyNative, Action<NativeRelationEffectReceipt> persist,
            out bool replayed)
        {
            var ids = receiptIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (ids.Length == 0 || ids.Length > 64 || ids.Any(id => id.Length > 200)
                || string.IsNullOrWhiteSpace(subjectId) || string.IsNullOrWhiteSpace(targetId)
                || subjectId == targetId || requestedDelta < -200 || requestedDelta > 200)
                throw new InvalidOperationException("Invalid native relationship effect identity.");
            var previous = journal.Where(r => r.ReceiptIds.Any(ids.Contains)).ToList();
            if (previous.Count > 0)
            {
                var receipt = previous[0];
                bool samePair = (receipt.SubjectId == subjectId && receipt.TargetId == targetId)
                    || (receipt.SubjectId == targetId && receipt.TargetId == subjectId);
                if (previous.Count != 1 || !receipt.ReceiptIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(ids)
                    || !samePair || receipt.RequestedDelta != requestedDelta)
                    throw new InvalidOperationException("A native relationship receipt was reused for a different effect.");
                replayed = true;
                return receipt;
            }
            replayed = false;
            var result = new NativeRelationEffectReceipt
            {
                ReceiptIds = ids, SubjectId = subjectId, TargetId = targetId, RequestedDelta = requestedDelta,
                Before = readRelation(), AppliedUtc = DateTimeOffset.UtcNow.ToString("o")
            };
            if (result.Before < -100 || result.Before > 100) throw new InvalidOperationException("Invalid native relationship observation.");
            try { if (requestedDelta != 0) applyNative(requestedDelta); }
            catch (Exception ex) { result.ApplicationError = ex.Message; }
            try
            {
                result.After = readRelation();
                result.ObservationComplete = result.After >= -100 && result.After <= 100;
            }
            catch (Exception ex) { result.ApplicationError += " Native observation failed: " + ex.Message; }
            // Even an exception after the game changed state must prevent another
            // application. An unavailable observation stays explicitly unmeasured.
            journal.Add(result);
            persist(result);
            return result;
        }
    }
}
