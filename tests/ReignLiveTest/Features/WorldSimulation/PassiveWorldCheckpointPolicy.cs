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
        public static string NpcLivesBlocker(string schema, bool scopeCurrent, bool? snapshotReady,
            bool? mutationInFlight, long pendingReceipts, long dueNativeRewards)
        {
            if (schema != "reign-npc-lives-checkpoint-v1" || !scopeCurrent
                || !snapshotReady.HasValue || !mutationInFlight.HasValue || pendingReceipts < 0 || dueNativeRewards < 0)
                return "npc_lives_telemetry_unavailable";
            if (snapshotReady != true) return "npc_lives_snapshot_pending";
            if (mutationInFlight == true) return "npc_lives_mutation_in_flight";
            if (pendingReceipts > 0) return "npc_lives_receipts_pending";
            return dueNativeRewards > 0 ? "npc_lives_native_rewards_due" : string.Empty;
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
