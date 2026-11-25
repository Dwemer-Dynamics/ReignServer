namespace ReignBeta.Shared.Characters
{
    // Shared by the native SaveAs gate and the external checkpoint controller.
    public static class NpcLivesCheckpointPolicy
    {
        public const string Schema = "reign-npc-lives-checkpoint-v4";
        public const string MeetingObservationSchema = "reign-npc-lives-checkpoint-v3";
        public const string TelemetryUnavailable = "npc_lives_telemetry_unavailable";

        public static string Blocker(string schema, bool scopeCurrent, bool? snapshotReady, bool? mutationInFlight,
            long pendingReceipts, long dueNativeRewards, long dueNativePersonalTravel, long dueNativeMeetingRelease,
            long dueNativeWeddingAttendanceRelease)
        {
            if (schema != Schema || !scopeCurrent || !snapshotReady.HasValue || !mutationInFlight.HasValue
                || pendingReceipts < 0 || dueNativeRewards < 0 || dueNativePersonalTravel < 0
                || dueNativeMeetingRelease < 0 || dueNativeWeddingAttendanceRelease < 0)
                return TelemetryUnavailable;
            if (snapshotReady != true) return "npc_lives_snapshot_pending";
            if (mutationInFlight == true) return "npc_lives_mutation_in_flight";
            if (pendingReceipts > 0) return "npc_lives_receipts_pending";
            if (dueNativeRewards > 0) return "npc_lives_native_rewards_due";
            if (dueNativePersonalTravel > 0) return "npc_lives_native_personal_travel_due";
            if (dueNativeMeetingRelease > 0) return "npc_lives_native_meeting_release_due";
            return dueNativeWeddingAttendanceRelease > 0 ? "npc_lives_native_wedding_attendance_release_due" : string.Empty;
        }

        // An earlier definitive interruption records a receipt even when a
        // later travel or camp gate would defer ordinary native completion.
        public static bool MeetingReleaseCanDrain(bool interrupted, bool canAdvance, bool campInBattle)
            => interrupted || canAdvance && !campInBattle;
    }
}
