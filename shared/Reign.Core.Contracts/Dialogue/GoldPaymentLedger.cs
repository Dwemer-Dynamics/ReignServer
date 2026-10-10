#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBeta.Shared.Dialogue
{
    public sealed class GoldPaymentReceipt
    {
        public string Id { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public int Amount { get; set; }
        public string Purpose { get; set; } = "gift";
        public string Agreement { get; set; } = "";
        public string ConsumedBy { get; set; } = "";
        public int Credited { get; set; }
        public int Refunded { get; set; }
        public string RefundOf { get; set; } = "";
        public int FromBefore { get; set; }
        public int ToBefore { get; set; }
        public string Status { get; set; } = "pending";
        public string Message { get; set; } = "";
    }

    /// <summary>Saved alongside native balances. History trimming must never remove these receipts.</summary>
    public sealed class GoldPaymentLedger
    {
        public int Version { get; set; } = 1;
        public List<GoldPaymentReceipt> Receipts { get; set; } = new List<GoldPaymentReceipt>();

        public GoldPaymentReceipt Transfer(string id, string from, string to, int amount, string purpose, string agreement,
            Func<string, int> balance, Action<string, string, int> move, Action<string, int> restore, string refundOf = "")
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || from == to || amount < 0)
                throw new ArgumentException("A payment requires a durable ID, distinct heroes, and an exact nonnegative amount.");
            string fingerprint = PlayerActionRules.Hash(from + "|" + to + "|" + amount + "|" + purpose + "|" + agreement + "|" + refundOf);
            var prior = Receipts.SingleOrDefault(r => r.Id == id);
            if (prior != null)
            {
                if (prior.Fingerprint != fingerprint) throw new InvalidOperationException("A payment ID was reused with different terms.");
                // Pending saved state is indeterminate, never permission for another debit.
                if (prior.Status == "pending") { prior.Status = "unknown"; prior.Message = "Payment interrupted; reconciliation is required."; }
                return prior;
            }
            GoldPaymentReceipt original = null;
            if (purpose == "refund" || !string.IsNullOrEmpty(refundOf))
            {
                original = Receipts.SingleOrDefault(r => r.Id == refundOf);
                if (original == null || original.Status != "applied" || original.From != to || original.To != from
                    || amount <= 0 || amount > original.Amount - original.Refunded)
                    throw new InvalidOperationException("A refund requires the exact original payment and an unrefunded amount.");
            }
            var receipt = new GoldPaymentReceipt { Id = id, Fingerprint = fingerprint, From = from, To = to, RefundOf = refundOf,
                Amount = amount, Purpose = purpose ?? "gift", Agreement = agreement ?? "",
                FromBefore = balance(from), ToBefore = balance(to) };
            Receipts.Add(receipt);
            if (receipt.FromBefore < amount || (long)receipt.ToBefore + amount > int.MaxValue)
            { receipt.Status = "failed"; receipt.Message = "The exact payment cannot be afforded or received."; return receipt; }
            try
            {
                if (amount > 0) move(from, to, amount);
                if (balance(from) != receipt.FromBefore - amount || balance(to) != receipt.ToBefore + amount)
                    throw new InvalidOperationException("Native gold balances did not match the payment.");
                if (original != null) original.Refunded += amount;
                receipt.Status = "applied"; receipt.Message = "Verified transfer of " + amount + " denars.";
            }
            catch (Exception)
            {
                try { restore(from, receipt.FromBefore); restore(to, receipt.ToBefore); }
                catch (Exception) { /* Verify below; an incomplete restoration is indeterminate. */ }
                bool restored = false;
                try { restored = balance(from) == receipt.FromBefore && balance(to) == receipt.ToBefore; }
                catch (Exception) { /* An unavailable native balance is indeterminate. */ }
                receipt.Status = restored ? "failed" : "unknown";
                receipt.Message = restored ? "Payment failed and both balances were restored." : "Payment result is uncertain; do not repeat it.";
            }
            return receipt;
        }

        public int AvailableCredit(string from, string to, string agreement)
        {
            if (string.IsNullOrEmpty(agreement)) return 0;
            long sum = Receipts.Where(r => r.Status == "applied" && r.Purpose == "recruitment_advance"
                && r.From == from && r.To == to && r.Agreement == agreement && r.ConsumedBy.Length == 0).Sum(r => (long)r.Amount - r.Refunded);
            return (int)Math.Min(int.MaxValue, sum);
        }

        public int CreditUsed(string transaction) => (int)Receipts.Where(r => r.ConsumedBy == transaction).Sum(r => (long)r.Credited);

        public int ConsumeCredit(string from, string to, string agreement, string transaction, int agreedTotal)
        {
            int existing = CreditUsed(transaction);
            if (existing > 0) return existing;
            int remaining = agreedTotal;
            foreach (var receipt in Receipts.Where(r => r.Status == "applied" && r.Purpose == "recruitment_advance"
                && r.From == from && r.To == to && r.Agreement == agreement && r.ConsumedBy.Length == 0))
            {
                if (remaining <= 0) break;
                receipt.Credited = Math.Min(remaining, receipt.Amount - receipt.Refunded); receipt.ConsumedBy = transaction;
                remaining -= receipt.Credited;
            }
            return agreedTotal - remaining;
        }

        public void ReleaseCredit(string transaction)
        {
            foreach (var receipt in Receipts.Where(r => r.ConsumedBy == transaction))
            { receipt.Credited = 0; receipt.ConsumedBy = ""; }
        }
    }
}
