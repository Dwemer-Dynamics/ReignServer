namespace Reign.Core.Contracts.World
{
    /// <summary>Opt-in observation must never acknowledge a government bulletin or save it unattended.</summary>
    public static class PassiveWorldAnnouncementPolicy
    {
        public static bool ShouldAcknowledge(bool observeGovernmentNews, bool isGovernmentNews)
            => !observeGovernmentNews || !isGovernmentNews;

        public static bool ShouldHoldClock(bool observeGovernmentNews, bool isGovernmentNewsOpen)
            => observeGovernmentNews && isGovernmentNewsOpen;

    }
}
