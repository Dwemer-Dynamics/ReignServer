namespace ReignLiveTest
{
    public static class PassiveWorldCheckpointPolicy
    {
        public static bool PersonalGossipQuiescent(bool readinessValid, bool enabled, bool schemaReady,
            long pending, long awaitingClock, long due, bool? nativeInFlight, bool nativeScopeCurrent, string nativeError)
        {
            if (!readinessValid || nativeInFlight != false) return false;
            if (!enabled) return true;
            return pending == 0 && awaitingClock == 0 && due == 0
                && (!schemaReady || nativeScopeCurrent && nativeError == "");
        }
        public static bool GovernmentQuiescent(string schema, bool available,
            bool? calendarReady, bool? syncInFlight, long missingProfiles, long pendingEvidence, string syncError)
        {
            return schema == "reign-government-runtime-v1" && available
                && calendarReady == true && syncInFlight == false
                && missingProfiles == 0 && pendingEvidence == 0 && syncError == "";
        }
        public static string ActionFailureError(long failedActions)
        {
            if (failedActions < 0)
                return "Action failure telemetry is unavailable; checkpoint safety cannot be established.";
            return failedActions == 0 ? string.Empty
                : failedActions + " terminal action failure(s) remain; repair is required before checkpointing.";
        }
    }
}
