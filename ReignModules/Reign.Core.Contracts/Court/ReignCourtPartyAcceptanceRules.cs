using System;
using System.Collections.Generic;

namespace Reign.Core.Contracts.Court
{
    public static class ReignCourtPartyAcceptanceRules
    {
        public static Dictionary<string, int> Merge(IReadOnlyDictionary<string, int>? previous,
            bool sameAgreement, string speakerId, string agreeingHeroId, int acceptanceTier)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            if (sameAgreement && previous != null)
                foreach (var pair in previous)
                    if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value >= 0 && pair.Value <= 3)
                        result[pair.Key] = pair.Value;
            // A speaker cannot supply another participant's assent.
            if (!string.IsNullOrWhiteSpace(speakerId)
                && string.Equals(speakerId, agreeingHeroId, StringComparison.Ordinal))
                result[speakerId] = Math.Max(0, Math.Min(3, acceptanceTier));
            return result;
        }

        public static bool ForeignSideBenefits(string option, string? recipientId, string? domesticId)
        {
            return option == "favor_foreign" || option == "accept"
                || ((option == "compensate" || option == "compromise")
                    && !string.Equals(recipientId, domesticId, StringComparison.Ordinal));
        }

        public static string AuthorizationActor(string priorActor, string incomingActor,
            bool sameAgreement, IEnumerable<string> ambassadorIds)
        {
            var envoys = new HashSet<string>(ambassadorIds, StringComparer.Ordinal);
            return sameAgreement && envoys.Contains(priorActor) && !envoys.Contains(incomingActor)
                ? priorActor : incomingActor;
        }

        public static int LosingPartyTier(bool foreignBenefits, string? domesticId,
            IEnumerable<string> ambassadorIds, IReadOnlyDictionary<string, int> acceptances)
        {
            if (foreignBenefits)
                return !string.IsNullOrWhiteSpace(domesticId) && acceptances.TryGetValue(domesticId!, out int tier)
                    ? Math.Max(0, Math.Min(3, tier)) : 0;
            int strongest = 0;
            foreach (string id in ambassadorIds)
                if (!string.IsNullOrWhiteSpace(id) && acceptances.TryGetValue(id, out int envoyTier))
                    strongest = Math.Max(strongest, Math.Max(0, Math.Min(3, envoyTier)));
            return strongest;
        }
    }
}
