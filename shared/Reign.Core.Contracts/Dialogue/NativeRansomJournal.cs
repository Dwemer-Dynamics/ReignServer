using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBeta.Shared.Dialogue
{
    public enum NativeRansomState { Prepared, Releasing, Paying, Held, Verified }

    public sealed class NativeRansomCustody
    {
        public bool SameLivingHero { get; set; }
        public bool IsPrisoner { get; set; }
        public bool HasCaptor { get; set; }
        public bool OriginalCaptor { get; set; }
        public int? OriginalRosterCount { get; set; }

        public bool MatchesCustody(int count) => SameLivingHero && IsPrisoner && HasCaptor && OriginalCaptor && OriginalRosterCount == count;
        public bool ProvesRelease() => SameLivingHero && !IsPrisoner && !HasCaptor && !OriginalCaptor && OriginalRosterCount == 0;
    }

    public sealed class NativeRansomRelease
    {
        public string HeroId { get; set; } = "";
        public string CaptorId { get; set; } = "";
        public int RosterCount { get; set; }
        public string Method { get; set; } = "";
        public bool Invoked { get; set; }
        public bool Returned { get; set; }
        public NativeRansomCustody? Observed { get; set; }

        internal bool IsValid() => !string.IsNullOrWhiteSpace(HeroId) && !string.IsNullOrWhiteSpace(CaptorId)
            && RosterCount > 0 && (Method == "ransom" || Method == "peace" || Method == "choice");
    }

    public sealed class NativeRansomBalances
    {
        public int Payer { get; set; }
        public int Recipient { get; set; }

        public bool CanPay(int amount) => amount >= 0 && Payer >= amount && Recipient >= 0 && (long)Recipient + amount <= int.MaxValue;
    }

    public sealed class NativeRansomPayment
    {
        public string PayerId { get; set; } = "";
        public string RecipientId { get; set; } = "";
        public int Amount { get; set; }
        public bool Invoked { get; set; }
        public bool Returned { get; set; }
        public NativeRansomBalances? Before { get; set; }
        public NativeRansomBalances? After { get; set; }

        internal bool IsValid() => !string.IsNullOrWhiteSpace(PayerId) && !string.IsNullOrWhiteSpace(RecipientId)
            && PayerId != RecipientId && Amount >= 0;

        public bool ProvesPayment() => Returned && Invoked == (Amount > 0) && IsValid()
            && Before is NativeRansomBalances before && before.CanPay(Amount)
            && After is NativeRansomBalances after
            && (long)before.Payer - after.Payer == Amount && (long)after.Recipient - before.Recipient == Amount;
    }

    public sealed class NativeRansomAttempt
    {
        public string Binding { get; set; } = "";
        public string MaterialJson { get; set; } = "";
        public NativeRansomState State { get; set; }
        public string HoldReason { get; set; } = "";
        public string PublicationId { get; set; } = "";
        public NativeRansomWorldAction? WorldAction { get; set; }
        public List<NativeRansomRelease> Releases { get; set; } = new List<NativeRansomRelease>();
        public NativeRansomPayment? Payment { get; set; }

        internal bool HasCompletePlan() => !string.IsNullOrWhiteSpace(Binding) && !string.IsNullOrWhiteSpace(MaterialJson)
            && Releases != null && Releases.Count > 0 && Releases.All(leg => leg?.IsValid() == true)
            && Releases.Select(leg => leg.HeroId).Distinct(StringComparer.Ordinal).Count() == Releases.Count
            && (Payment == null || Payment.IsValid());

        public bool IsVerified() => State == NativeRansomState.Verified && HasCompletePlan()
            && Releases.All(leg => leg.Invoked && leg.Returned && leg.Observed?.ProvesRelease() == true)
            && (Payment == null || Payment.ProvesPayment());
    }

    public sealed class NativeRansomWorldAction
    {
        public string CampaignId { get; set; } = "";
        public string TimelineId { get; set; } = "";
        public string ActionId { get; set; } = "";
        public string IdentityJson { get; set; } = "";

        public static string Key(string campaignId, string timelineId, string actionId)
            => PlayerActionRules.Hash("world_ransom:" + Field(campaignId) + Field(timelineId) + Field(actionId));

        public string Binding() => PlayerActionRules.Hash(IdentityJson);

        internal bool MatchesScope(string campaignId, string timelineId, string actionId)
            => CampaignId == campaignId && TimelineId == timelineId && ActionId == actionId;

        internal bool IsValid() => !string.IsNullOrWhiteSpace(CampaignId) && !string.IsNullOrWhiteSpace(TimelineId)
            && !string.IsNullOrWhiteSpace(ActionId) && !string.IsNullOrWhiteSpace(IdentityJson);

        private static string Field(string value) => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;
    }

    // Native captivity callbacks are irreversible. Persist attempts alongside the world
    // independently of action history. Loading an interrupted attempt never resumes a leg.
    public sealed class NativeRansomJournal
    {
        private string? _runningKey;
        public int Version { get; set; } = 1;
        public Dictionary<string, NativeRansomAttempt> Attempts { get; set; } =
            new Dictionary<string, NativeRansomAttempt>(StringComparer.Ordinal);

        public NativeRansomAttempt? Find(string key) => Attempts.TryGetValue(key, out var prior) ? prior : null;
        public bool HasAttempt(string key) => Attempts.ContainsKey(key);
        public bool IsRunning(string key) => _runningKey == key;

        // A saved ordinary ransom remains queryable even after the action list drops
        // its terminal row. A corrupt attempt under the exact key is a hold.
        public bool TryFindWorldAction(string campaignId, string timelineId, string actionId, out NativeRansomAttempt? attempt)
        {
            attempt = null;
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(timelineId) || string.IsNullOrWhiteSpace(actionId)
                || !Attempts.TryGetValue(NativeRansomWorldAction.Key(campaignId, timelineId, actionId), out var record))
                return false;
            if (record?.WorldAction is NativeRansomWorldAction owner && owner.IsValid()
                && owner.MatchesScope(campaignId, timelineId, actionId) && record.Binding == owner.Binding())
                attempt = record;
            return true;
        }

        public NativeRansomAttempt? Execute(string key, NativeRansomAttempt attempt, Func<bool> owns,
            Func<NativeRansomRelease, bool> canRelease, Func<NativeRansomRelease, NativeRansomCustody?> readCustody,
            Action<NativeRansomRelease> release, Func<NativeRansomBalances?> readBalances, Action<int> pay)
        {
            if (_runningKey != null || string.IsNullOrWhiteSpace(key) || HasAttempt(key) || !attempt.HasCompletePlan()
                || attempt.State != NativeRansomState.Prepared || !string.IsNullOrEmpty(attempt.PublicationId)
                || attempt.Releases.Any(leg => leg.Invoked || leg.Returned || leg.Observed != null)
                || (attempt.Payment is NativeRansomPayment initial && (initial.Invoked || initial.Returned || initial.Before != null || initial.After != null)))
                return null;
            if (attempt.WorldAction is NativeRansomWorldAction owner && (!owner.IsValid() || attempt.Binding != owner.Binding()
                || key != NativeRansomWorldAction.Key(owner.CampaignId, owner.TimelineId, owner.ActionId)))
                return null;

            if (!owns() || attempt.Releases.Any(leg => !canRelease(leg) || readCustody(leg)?.MatchesCustody(leg.RosterCount) != true)
                || (attempt.Payment is NativeRansomPayment planned && readBalances()?.CanPay(planned.Amount) != true))
                return null;

            Attempts.Add(key, attempt);
            _runningKey = key;
            try
            {
                foreach (NativeRansomRelease leg in attempt.Releases)
                {
                    if (!owns() || !canRelease(leg) || readCustody(leg)?.MatchesCustody(leg.RosterCount) != true)
                        return Hold(attempt, "custody_or_authority_changed");
                    attempt.State = NativeRansomState.Releasing;
                    leg.Invoked = true;
                    try { release(leg); leg.Returned = true; }
                    finally { leg.Observed = Observe(() => readCustody(leg)); }
                    if (leg.Observed?.ProvesRelease() != true) return Hold(attempt, "release_unverified");
                }

                if (!owns() || !ObserveReleasedBatch(attempt, readCustody))
                    return Hold(attempt, "release_or_authority_changed");
                if (attempt.Payment is NativeRansomPayment payment)
                {
                    payment.Before = readBalances();
                    if (payment.Before?.CanPay(payment.Amount) != true) return Hold(attempt, "payment_unavailable");
                    attempt.State = NativeRansomState.Paying;
                    try
                    {
                        if (payment.Amount > 0) { payment.Invoked = true; pay(payment.Amount); }
                        payment.Returned = true;
                    }
                    finally { payment.After = Observe(readBalances); }
                    if (!payment.ProvesPayment()) return Hold(attempt, "payment_unverified");
                    if (!owns() || !ObserveReleasedBatch(attempt, readCustody))
                        return Hold(attempt, "post_payment_authority_or_custody_changed");
                }
                attempt.State = NativeRansomState.Verified;
                return attempt;
            }
            catch (Exception)
            {
                // The finally blocks retain any postcondition we could observe. A throwing
                // native callback may be incomplete even when its visible balances match.
                return Hold(attempt, "native_callback_interrupted");
            }
            finally { _runningKey = null; }
        }

        private static bool ObserveReleasedBatch(NativeRansomAttempt attempt, Func<NativeRansomRelease, NativeRansomCustody?> read)
        {
            bool verified = true;
            foreach (NativeRansomRelease leg in attempt.Releases)
            {
                leg.Observed = Observe(() => read(leg));
                verified &= leg.Observed?.ProvesRelease() == true;
            }
            return verified;
        }

        private static T? Observe<T>(Func<T?> read) where T : class
        {
            try { return read(); }
            catch (Exception) { return null; }
        }

        private static NativeRansomAttempt Hold(NativeRansomAttempt attempt, string reason)
        {
            attempt.State = NativeRansomState.Held;
            attempt.HoldReason = reason;
            return attempt;
        }
    }
}
