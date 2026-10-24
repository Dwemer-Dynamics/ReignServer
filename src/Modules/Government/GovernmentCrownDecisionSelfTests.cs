using System;
using System.Linq;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void AddGovernmentCrownDecisionSelfTests(Action<string, bool, string, object> add)
        {
            var allowed = new[] { TestDict("id", "accept", "label", "Enact the levy"), TestDict("id", "reject", "label", "Keep the existing law") };
            var valid = NormalizeGovernmentCrownDecision(TestDict("optionId", "reject", "publicReason", "The towns cannot bear another levy."), allowed);
            var forged = NormalizeGovernmentCrownDecision(TestDict("optionId", "seize_everything", "publicReason", "I decree it."), allowed);
            var leaked = NormalizeGovernmentCrownDecision(TestDict("optionId", "accept", "publicReason", "My relationship is 76."), allowed);
            var privateReason = NormalizeGovernmentCrownDecision(TestDict("optionId", "accept", "publicReason", "The ruler privately promised me payment for my support."), allowed);
            var blank = NormalizeGovernmentCrownDecision(TestDict("optionId", ""), allowed);
            add("government.chancellor_choice_is_scoped_and_public",
                ReadBool(valid, "ok", false) && !ReadBool(forged, "ok", true) && !ReadBool(blank, "ok", true)
                && ReadString(leaked, "publicReason", "") == "The Crown supports: Enact the levy."
                && ReadString(privateReason, "publicReason", "") == "The Crown supports: Enact the levy."
                && !valid.ContainsKey("nativeActions") && !valid.ContainsKey("governmentCommitment"),
                "Only a supplied option and its public label survive; private prose and numeric relations never cross the publication boundary.", new { valid, forged, leaked, privateReason, blank });
            var attempts = new[] { "Vacant", "Inactive", "EmergencyActive", "CaptiveContinuity", "Handoff" }.Select(state => GovernmentCrownDecision(TestDict(
                "campaignId", "unused", "heroStringId", "chancellor", "playerHeroStringId", "ruler", "businessId", "matter",
                "authorityLevel", 3, "officeState", state, "options", new[] { TestDict("id", "accept"), TestDict("id", "reject") }))).ToList();
            add("government.chancellor_inactive_office_never_calls_provider",
                attempts.All(result => !ReadBool(result, "ok", true) && ReadInt(result, "providerCallCount", -1) == 0),
                "Every non-Active office is rejected before character or provider access.", attempts);
        }
    }
}
