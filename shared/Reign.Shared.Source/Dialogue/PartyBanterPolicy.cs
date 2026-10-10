using System;
using System.Globalization;

namespace ReignBeta.Shared.Dialogue
{
    // Pure rules shared by the native real-time scheduler and the isolated harness.
    public static class PartyBanterPolicy
    {
        public const int DefaultResponses = 6, DefaultCharacters = 360;
        public const double CooldownDays = 8d / 24d;
        public static bool CanStart(bool enabled, bool traveling, bool active, bool chatOpen,
            int eligibleCount, double worldDay, double endedDay, double realSeconds, double retryAfter) =>
            enabled && traveling && !active && !chatOpen && eligibleCount >= 2
            && realSeconds >= retryAfter && (endedDay < 0 || worldDay >= endedDay + CooldownDays);
        public static double ReadingSeconds(string text) => Math.Max(2, Math.Min(6,
            2 + (text ?? "").Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length / 5d));
        // Polling the same failed episode must not keep moving the retry deadline.
        public static double FailureRetryAfter(string previousSession, string previousState,
            string session, string state, double realSeconds, double retryAfter) =>
            state == "failed" && (previousSession != session || previousState != "failed")
                ? Math.Max(retryAfter, realSeconds + 60) : retryAfter;
        public static int CharacterCount(string text) => StringInfo.ParseCombiningCharacters(text ?? "").Length;
        public static bool Fits(string text, int maximum) => !string.IsNullOrWhiteSpace(text)
            && CharacterCount(text) <= maximum && (text ?? "").Length <= maximum * 8;
        public static int OutputTokens(int responses, int characters) =>
            (int)Math.Ceiling(responses * (characters / 2.5d + 170)) + 100;
    }
}
