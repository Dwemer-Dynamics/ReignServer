namespace ReignLiveTest
{
    public static class PassiveWorldCheckpointPolicy
    {
        public static string ActionFailureError(long failedActions)
        {
            if (failedActions < 0)
                return "Action failure telemetry is unavailable; checkpoint safety cannot be established.";
            return failedActions == 0 ? string.Empty
                : failedActions + " terminal action failure(s) remain; repair is required before checkpointing.";
        }
    }
}
