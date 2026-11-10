using System;
using System.Security.Cryptography;
using System.Text;

namespace Reign.Relationships
{
    /// <summary>Provider-free policy shared by the director, simulation and native adapter.</summary>
    public static class ArrangedMarriagePolicy
    {
        public const int Version = 1;
        public const int DenarsPerTier = 20000;
        public const int SuccessRelation = 20;
        public const int RefusalRelation = -10;
        public const int PlayerRefusalRelation = -5;
        public const double PlayerReplyDays = 30d;
        public const double WeddingNoticeDays = 7d;
        public const double WeddingWindowDays = 1d;

        public static bool PlayerReplyWindow(double delivered, double reply) =>
            reply >= delivered && reply < delivered + PlayerReplyDays;

        public static int PlayerQuote(int playerTier, int leaderTier, long appraisalAdjustment)
        {
            int gap = checked(leaderTier - playerTier);
            int net = checked(gap * DenarsPerTier + (int)Math.Max(-DenarsPerTier, Math.Min(DenarsPerTier, appraisalAdjustment)));
            return gap > 0 ? Math.Max(1, net) : gap < 0 ? Math.Min(-1, net) : net;
        }

        public static bool CanConcludeUnattended(double now, double ends, bool playerSpouse, bool appealPending)
            => now >= ends && !playerSpouse && !appealPending;
        public const double RoyalAnnualChance = .05d;
        public static long Period(double day) => (long)Math.Floor(day / 7d);
        public static bool CanAcknowledge(long expected,long observed,bool ok,bool skipped)
            => ok && !skipped && expected==observed;
        public static int Fee(int a, int b) => checked(Math.Abs(a - b) * DenarsPerTier);
        public static double Rank(double a, double b, int tierA, int tierB, int fiefsA, int fiefsB)
            => Math.Min(a, b) + (Math.Abs(tierA - tierB) <= 1 && (fiefsA > 0) == (fiefsB > 0) ? 20d : 0d);
        // Save branches and repeated proposers deliberately do not enter this seed.
        public static double RoyalRoll(string campaign, string hero, int year)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(campaign + "|" + hero + "|" + year + "|royal-landless-v1"));
                ulong value = 0;
                for (int i = 0; i < 8; i++) value = (value << 8) | bytes[i];
                return value / ((double)ulong.MaxValue + 1d);
            }
        }
        public static bool RoyalAllowed(string campaign, string hero, bool royal, int otherFiefs, int year)
            => !royal || otherFiefs > 0 || RoyalRoll(campaign, hero, year) < RoyalAnnualChance;
    }

    public sealed class MarriagePaymentJournal
    {
        public string Id { get; set; } = "";
        public string State { get; set; } = "quoted";
        public int Fee { get; set; }
        public int PayerBefore { get; set; }
        public int PayeeBefore { get; set; }
        public string Error { get; set; } = "";
    }

    public static class MarriagePaymentTransaction
    {
        // All delegates execute synchronously on the game thread. A native save cannot
        // interleave the transfer, marriage or observation. Unknown partial state is fenced.
        public static bool Execute(MarriagePaymentJournal journal, Func<bool> married,
            Func<bool> eligible, Func<int> payerGold, Func<int> payeeGold,
            Action<int> transfer, Action marry, Action saveJournal)
        {
            if (journal.State == "completed") return married();
            if (journal.State != "quoted") throw new InvalidOperationException("Marriage transaction requires recovery: " + journal.Id);
            if (married()) throw new InvalidOperationException("Unjournaled marriage cannot be charged retroactively.");
            if (!eligible() || payerGold() < journal.Fee) return false;
            journal.PayerBefore = payerGold(); journal.PayeeBefore = payeeGold();
            journal.State = "applying"; saveJournal();
            bool transferred = false;
            try
            {
                transfer(journal.Fee); transferred = true;
                if (payerGold() != journal.PayerBefore - journal.Fee || payeeGold() != journal.PayeeBefore + journal.Fee)
                    throw new InvalidOperationException("Marriage payment did not conserve native wallets.");
                marry();
                if (!married()) throw new InvalidOperationException("Native marriage did not establish reciprocal spouses.");
                journal.State = "completed"; saveJournal(); return true;
            }
            catch (Exception ex)
            {
                journal.Error = ex.Message;
                // Never undo or repeat an established union. A thrown native callback
                // may have established spouses but left other clan effects incomplete.
                journal.State = "recovery"; saveJournal();
                if (!married() && transferred && payerGold() == journal.PayerBefore - journal.Fee
                    && payeeGold() == journal.PayeeBefore + journal.Fee)
                {
                    transfer(-journal.Fee);
                    if (payerGold() == journal.PayerBefore && payeeGold() == journal.PayeeBefore)
                        journal.State = "refunded";
                    saveJournal();
                }
                throw;
            }
        }
    }
}
