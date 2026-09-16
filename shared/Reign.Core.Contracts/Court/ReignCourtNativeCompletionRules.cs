using System;

namespace Reign.Core.Contracts.Court
{
    public static class ReignCourtNativeCompletionRules
    {
        public static bool CanReconcile(bool playerConfirmed, bool courtSettled, string optionId,
            string matterId, string actionId, string actionStatus, string actionSource,
            bool exactIdentity, bool sameScope)
            => playerConfirmed && !courtSettled && optionId == "accept"
                && !string.IsNullOrWhiteSpace(matterId)
                && string.Equals(actionId, matterId + "_native", StringComparison.Ordinal)
                && actionStatus == "Completed" && actionSource == "court_decision"
                && exactIdentity && sameScope;
    }
}
