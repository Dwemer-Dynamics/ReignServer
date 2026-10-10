using System;
using System.Collections.Generic;
using System.Linq;

namespace Reign.Core.Contracts.Actions
{
    public sealed class AgreementJournal
    {
        public ActionAgreement Agreement { get; set; } = new ActionAgreement();
        public string CommittedHash { get; set; } = "";
        public double ArrivalDay { get; set; }
        public List<AgreementStep> Steps { get; set; } = new List<AgreementStep>();
        public List<ActionReceipt> Receipts { get; set; } = new List<ActionReceipt>();
        public string RecoveryDetails { get; set; } = "";
        public bool OperatorAuthorized { get; set; }
    }

    public sealed class AgreementStep
    {
        public string ClauseId { get; set; } = "";
        public int Installment { get; set; }
        public int FulfilledInstallments { get; set; }
        public string Snapshot { get; set; } = "";
        public bool Reserved { get; set; }
        public bool Started { get; set; }
        public double StartedDay { get; set; }
        public bool Completed { get; set; }
        public bool Returned { get; set; }
    }

    /// <summary>The adapter could not prove rollback. Freeze custody until explicit recovery; never refund twice.</summary>
    public sealed class AgreementRecoveryException : Exception
    {
        public string Details { get; }
        public AgreementRecoveryException(string message, string details, Exception inner) : base(message, inner) { Details = details; }
    }

    /// <summary>All callbacks run on the owning game thread. Snapshots and receipts are saved with the campaign.</summary>
    public interface IAgreementBackend
    {
        IReadOnlyList<string> Preflight(ActionAgreement agreement);
        string Capture(ActionAgreement agreement, ActionClause clause);
        void Reserve(ActionAgreement agreement, ActionClause clause, string snapshot);
        void Return(ActionAgreement agreement, ActionClause clause, string snapshot, int fulfilledInstallments);
        void Apply(ActionAgreement agreement, ActionClause clause, string snapshot, int installment);
        void ApplyAssetsAtomically(ActionAgreement agreement, IReadOnlyList<AgreementStep> steps);
        bool Observe(ActionAgreement agreement, ActionClause clause, string snapshot, int installment);
        bool ConditionSatisfied(ActionClause clause, double now);
        double DeliveryDays(ActionAgreement agreement);
        void SaveJournal(AgreementJournal journal);
    }

    public static class ReignAgreementEngine
    {
        public static ActionReceipt Commit(AgreementJournal journal, IAgreementBackend backend, double now, bool operatorAuthorized = false)
        {
            var a = journal.Agreement;
            string hash = a.TermsHash();
            if (journal.CommittedHash.Length > 0)
            {
                if (journal.CommittedHash != hash) throw new InvalidOperationException("The operation ID already belongs to different terms.");
                return Receipt(journal, "Existing operation; no resources changed.", now);
            }
            if (a.State != AgreementState.Accepted || (!a.HasRequiredConsents && !(operatorAuthorized && a.Origin == ActionOrigin.Console)))
                throw new InvalidOperationException("The required participants must authorize the exact current terms.");
            var errors = backend.Preflight(a);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
            // Capture every term before the first mutation, including repeated claims on one resource.
            journal.Steps = a.Clauses.Select(c => new AgreementStep { ClauseId = c.Id, Snapshot = backend.Capture(a, c) }).ToList();
            journal.CommittedHash = hash;
            journal.OperatorAuthorized = operatorAuthorized && a.Origin == ActionOrigin.Console;
            journal.ArrivalDay = now + Math.Max(0, backend.DeliveryDays(a));
            a.State = AgreementState.Reserved;
            backend.SaveJournal(journal);
            try
            {
                foreach (var step in journal.Steps)
                {
                    backend.Reserve(a, Clause(journal, step), step.Snapshot);
                    step.Reserved = true;
                    backend.SaveJournal(journal);
                }
                a.State = journal.ArrivalDay > now ? AgreementState.InTransit : AgreementState.Waiting;
                backend.SaveJournal(journal);
            }
            catch (Exception ex)
            {
                Fail(journal, backend, ex);
                throw;
            }
            return Advance(journal, backend, now);
        }

        public static ActionReceipt Advance(AgreementJournal journal, IAgreementBackend backend, double now)
        {
            var a = journal.Agreement;
            if (a.State != AgreementState.InTransit && a.State != AgreementState.Waiting) return Receipt(journal, a.Failure, now);
            if (journal.CommittedHash != a.TermsHash()) throw new InvalidOperationException("Saved agreement terms have changed after commitment.");
            if (now < journal.ArrivalDay && !a.AdvancePayment) return Receipt(journal, "Delivery is in transit.", now);
            try
            {
                // Observe all started effects before considering dependent payment. Re-entry only observes;
                // it never repeats a native action whose adapter already began execution.
                foreach (var step in journal.Steps.Where(s => s.Started && !s.Completed))
                    if (backend.Observe(a, Clause(journal, step), step.Snapshot, step.Installment)) Complete(step);
                // Services are started/observed before considering any delivery payment.
                var ordered = journal.Steps.OrderBy(s => IsAsset(Clause(journal, s)) ? 1 : 0).ToList();
                var readyAssets = new List<AgreementStep>();
                foreach (var step in ordered.Where(s => !s.Completed))
                {
                    var c = Clause(journal, step);
                    if (step.Started) continue;
                    if (now < journal.ArrivalDay && !(a.AdvancePayment && c.ActionId == "give.gold")) continue;
                    if (!Ready(c, step, backend, now)) continue;
                    if (IsAsset(c))
                    {
                        readyAssets.Add(step); continue;
                    }
                    // Backend.Apply must provide idempotency for its operation+clause+installment identity.
                    backend.Apply(a, c, step.Snapshot, step.Installment);
                    step.Started = true;
                    step.StartedDay = now;
                    if (backend.Observe(a, c, step.Snapshot, step.Installment)) Complete(step);
                    backend.SaveJournal(journal);
                }
                if (!a.AdvancePayment)
                {
                    // One reciprocal exchange settles as one batch. An unmet condition on either
                    // side must not allow the other side's assets or payment to leave custody.
                    var outstanding = journal.Steps.Where(s => !s.Completed).ToList();
                    if (outstanding.Any(s => !IsAsset(Clause(journal, s))) || outstanding.Any(s => IsAsset(Clause(journal, s)) && !readyAssets.Contains(s))) readyAssets.Clear();
                }
                if (readyAssets.Count > 0)
                {
                    backend.ApplyAssetsAtomically(a, readyAssets);
                    foreach (var step in readyAssets) { step.Started = true; step.StartedDay = now; Complete(step); }
                    backend.SaveJournal(journal);
                }
                foreach (var step in journal.Steps.Where(s => s.Completed && s.Installment + 1 < Clause(journal, s).Installments))
                {
                    step.Installment++; step.Started = false; step.Completed = false;
                }
                a.State = journal.Steps.All(s => s.Completed) ? AgreementState.Completed : now < journal.ArrivalDay ? AgreementState.InTransit : AgreementState.Waiting;
                backend.SaveJournal(journal);
                return Receipt(journal, a.State == AgreementState.Completed ? "All agreed effects were verified." : "Awaiting agreed delivery or fulfillment.", now);
            }
            catch (Exception ex)
            {
                Fail(journal, backend, ex);
                return Receipt(journal, a.Failure, now);
            }
        }

        public static ActionReceipt Cancel(AgreementJournal journal, IAgreementBackend backend, double now)
        {
            if (journal.Agreement.State != AgreementState.Waiting && journal.Agreement.State != AgreementState.InTransit) throw new InvalidOperationException("Only an unfulfilled active agreement can be cancelled.");
            if (journal.Steps.Any(s => s.Started || s.FulfilledInstallments > 0)) throw new InvalidOperationException("Fulfillment has begun; this agreement cannot be cancelled unilaterally.");
            ReturnUnfulfilled(journal, backend, now);
            if (journal.Agreement.State != AgreementState.RecoveryRequired) journal.Agreement.State = AgreementState.Cancelled;
            backend.SaveJournal(journal);
            return Receipt(journal, "Agreement cancelled; reserved assets returned.", now);
        }

        public static ActionReceipt CancelObservedOrder(AgreementJournal journal, AgreementJournal cancellation,
            string clauseId, string orderId, IAgreementBackend backend, double now)
        {
            var a = journal.Agreement;
            var accepted = cancellation?.Agreement;
            var step = journal.Steps.SingleOrDefault(s => s.ClauseId == clauseId);
            var clause = a.Clauses.SingleOrDefault(c => c.Id == clauseId);
            bool targetsOrder = accepted != null && accepted.Clauses.Any(c =>
                (c.ActionId == "cancel_campaign_order" || c.ActionId == "respond_to_order_report"
                    && c.Arguments.TryGetValue("terms.response", out var response) && response == "cancel")
                && c.Arguments.TryGetValue("terms.orderId", out var target) && target == orderId
                && clause != null && c.ActorId == clause.ActorId && c.RecipientId == clause.RecipientId);
            if (cancellation == null || accepted == null || string.IsNullOrWhiteSpace(orderId) || !targetsOrder
                || cancellation.CommittedHash != accepted.TermsHash() || !accepted.HasBothConsents
                || accepted.State != AgreementState.Completed || cancellation.Steps.Count != accepted.Clauses.Count
                || cancellation.Steps.Count == 0
                || accepted.Clauses.Any(c => cancellation.Steps.Count(s => s.ClauseId == c.Id) != 1)
                || cancellation.Steps.Any(s => !s.Reserved || !s.Started || !s.Completed || s.Returned || s.FulfilledInstallments <= 0)
                || accepted.PlayerId != a.PlayerId || accepted.NpcId != a.NpcId
                || journal.CommittedHash != a.TermsHash()
                || !(a.HasRequiredConsents || journal.OperatorAuthorized && a.Origin == ActionOrigin.Console)
                || step == null || clause == null || IsAsset(clause) || !step.Reserved || !step.Started || step.Completed || step.Returned)
                throw new InvalidOperationException("Cancellation requires the exact observed order and completed mutual consent.");
            if (a.State == AgreementState.Cancelled) return Receipt(journal, "Existing mutual cancellation; no resources changed.", now);
            if (a.State != AgreementState.Waiting && a.State != AgreementState.InTransit)
                throw new InvalidOperationException("Only an active agreement can be ended by an observed order cancellation.");
            // A stopped order is not fulfillment. Preserve delivered payments and start
            // history; other irreversible work remains explicitly in recovery.
            bool recovery = journal.Steps.Any(s => s != step && s.Started && !s.Completed
                && !IsAsset(Clause(journal, s)));
            foreach (var pending in journal.Steps.Where(s => s.Reserved && !s.Returned
                && IsAsset(Clause(journal, s))).Reverse())
            {
                try { backend.Return(a, Clause(journal, pending), pending.Snapshot, pending.FulfilledInstallments); pending.Returned = true; }
                catch (Exception ex)
                {
                    recovery = true; a.Failure += " Return pending: " + ex.Message;
                    if (ex is AgreementRecoveryException uncertain) journal.RecoveryDetails += uncertain.Details + "\n";
                }
            }
            a.State = recovery ? AgreementState.RecoveryRequired : AgreementState.Cancelled;
            if (recovery && a.Failure.Length == 0) a.Failure = "Other started services require explicit recovery after this order's cancellation.";
            backend.SaveJournal(journal);
            return Receipt(journal, "Order cancelled by agreement " + accepted.Id
                + "; delivered effects retained and unused reservations returned.", now);
        }

        private static void ReturnUnfulfilled(AgreementJournal journal, IAgreementBackend backend, double now)
        {
            bool recovery = journal.Steps.Any(s => s.Started || s.FulfilledInstallments > 0);
            foreach (var step in journal.Steps.Where(s => s.Reserved && !s.Returned && (!s.Started || IsAsset(Clause(journal, s)))).Reverse())
            {
                try { backend.Return(journal.Agreement, Clause(journal, step), step.Snapshot, step.FulfilledInstallments); step.Returned = true; }
                catch (Exception ex)
                {
                    recovery = true; journal.Agreement.Failure += " Return pending: " + ex.Message;
                    if (ex is AgreementRecoveryException uncertain) journal.RecoveryDetails += uncertain.Details + "\n";
                }
            }
            journal.Agreement.State = recovery ? AgreementState.RecoveryRequired : AgreementState.Impossible;
            backend.SaveJournal(journal);
        }

        private static void Fail(AgreementJournal journal, IAgreementBackend backend, Exception error)
        {
            journal.Agreement.Failure = error.Message;
            if (error is AgreementRecoveryException uncertain)
            {
                journal.RecoveryDetails = uncertain.Details;
                journal.Agreement.State = AgreementState.RecoveryRequired;
                backend.SaveJournal(journal);
            }
            else ReturnUnfulfilled(journal, backend, 0);
        }
        private static bool IsAsset(ActionClause c) => c.ActionId.StartsWith("give.", StringComparison.Ordinal);
        private static bool Ready(ActionClause c, AgreementStep s, IAgreementBackend b, double now) => now >= c.DueDay + s.Installment * c.IntervalDays && b.ConditionSatisfied(c, now);
        private static void Complete(AgreementStep s) { s.Completed = true; s.FulfilledInstallments = Math.Max(s.FulfilledInstallments, s.Installment + 1); }

        private static ActionClause Clause(AgreementJournal journal, AgreementStep step) => journal.Agreement.Clauses.Single(x => x.Id == step.ClauseId);
        private static ActionReceipt Receipt(AgreementJournal journal, string message, double now)
        {
            var receipt = new ActionReceipt { OperationId = journal.Agreement.Id, TermsHash = journal.CommittedHash,
                State = journal.Agreement.State, Day = now, Message = message,
                ObservedEffects = journal.Steps.Where(x => x.Completed).Select(x => x.ClauseId).ToList() };
            if (journal.Receipts.Count == 0 || journal.Receipts.Last().State != receipt.State || journal.Receipts.Last().Message != message)
                journal.Receipts.Add(receipt);
            return receipt;
        }
    }
}
