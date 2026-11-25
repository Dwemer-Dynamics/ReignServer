using System;

namespace Reign.Core.Contracts.World
{
    public enum ReignRecognitionPhase { Prepared, InvokingPeace, PeaceVerified, Publishing, Published, Held }

    public sealed class ReignRecognitionReceipt
    {
        public string CampaignId { get; set; } = "";
        public string TimelineId { get; set; } = "";
        public string MovementId { get; set; } = "";
        public string ActionId { get; set; } = "";
        public string ActionBinding { get; set; } = "";
        public string RebelKingdomId { get; set; } = "";
        public string ParentKingdomId { get; set; } = "";
        public string RebelRulerId { get; set; } = "";
        public string ParentRulerId { get; set; } = "";
        public string AgreementId { get; set; } = "";
        public string Summary { get; set; } = "";
        public string ParentName { get; set; } = "";
        public string RebelName { get; set; } = "";
        public ReignRecognitionPhase Phase { get; set; }
        public string Failure { get; set; } = "";
        public bool WarObservedBefore { get; set; }
        public bool PeaceObserved { get; set; }
        public float? PeaceDay { get; set; }

        public bool Matches(string campaignId, string timelineId, string movementId, string actionId, string binding)
            => this.CampaignId == campaignId && this.TimelineId == timelineId && this.MovementId == movementId
                && this.ActionId == actionId && ActionBinding == binding;

        public static bool AllowsCivilWarMutation(bool activeWarRecord, bool recognitionOwnsMovement, Func<bool?> observeWar)
            => activeWarRecord && !recognitionOwnsMovement && Observe(observeWar) == true;

        public bool HasPublishedOutcome() => Phase == ReignRecognitionPhase.Published && HasObservedPeace();

        public bool Advance(bool allowInitialNativePeace, Func<bool?> observeWar, Action makePeace,
            Func<float> currentDay, Func<bool> publish, Action persist)
        {
            if (Phase == ReignRecognitionPhase.Held) return false;
            if (Phase == ReignRecognitionPhase.InvokingPeace || Phase == ReignRecognitionPhase.Publishing)
                return Hold("interrupted_transition", persist);
            if (Phase == ReignRecognitionPhase.Published)
                return HasPublishedOutcome();

            if (Phase == ReignRecognitionPhase.Prepared)
            {
                if (!allowInitialNativePeace || Observe(observeWar) != true)
                    return Hold("exact_war_not_reestablished", persist);
                WarObservedBefore = true;
                Phase = ReignRecognitionPhase.InvokingPeace;
                persist();
                try { makePeace(); }
                catch (Exception) { return Hold("native_peace_callback_interrupted", persist); }
                if (Observe(observeWar) != false) return Hold("exact_peace_unverified", persist);
                PeaceObserved = true;
                PeaceDay = currentDay();
                Phase = ReignRecognitionPhase.PeaceVerified;
                persist();
            }

            if (Phase != ReignRecognitionPhase.PeaceVerified || !HasObservedPeace() || Observe(observeWar) != false)
                return Hold("saved_peace_proof_unavailable", persist);
            Phase = ReignRecognitionPhase.Publishing;
            persist();
            try { if (!publish()) return Hold("original_publication_unverified", persist); }
            catch (Exception) { return Hold("publication_interrupted", persist); }
            Phase = ReignRecognitionPhase.Published;
            persist();
            return true;
        }

        private bool HasObservedPeace() => WarObservedBefore && PeaceObserved && PeaceDay is float day
            && !float.IsNaN(day) && !float.IsInfinity(day) && day >= 0;

        private static bool? Observe(Func<bool?> observeWar)
        {
            try { return observeWar(); }
            catch (Exception) { return null; }
        }

        private bool Hold(string reason, Action persist)
        {
            Phase = ReignRecognitionPhase.Held;
            Failure = reason;
            persist();
            return false;
        }
    }
}
