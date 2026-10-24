using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Reign.Core.Contracts.Actions
{
    public sealed class AgreementPaymentFact
    {
        public string AgreementId { get; set; } = "";
        public string ClauseId { get; set; } = "";
        public string TermsHash { get; set; } = "";
        public string FromHeroId { get; set; } = "";
        public string ToHeroId { get; set; } = "";
        public long Amount { get; set; }
        public int QuantityPerInstallment { get; set; }
        public int FulfilledInstallments { get; set; }
        public int ScheduledInstallments { get; set; }
        public double StepStartedDay { get; set; }
        public bool UnusedReservationReturned { get; set; }
        public string[] AssociatedActionIds { get; set; } = Array.Empty<string>();
    }

    public sealed class AgreementPaymentHistory
    {
        public const string Schema = "reign-agreement-payment-history-v1";
        public int Total { get; set; }
        public bool Truncated { get; set; }
        public IReadOnlyList<AgreementPaymentFact> Rows { get; set; } = Array.Empty<AgreementPaymentFact>();
    }

    /// <summary>Read-only evidence of delivered gold; never a new transfer, refund credit or consent.</summary>
    public static class ReignAgreementPaymentFacts
    {
        public static AgreementPaymentHistory Capture(IEnumerable<AgreementJournal>? journals,
            string playerId, string npcId, int limit = 12)
        {
            if (limit < 1 || limit > 64) throw new ArgumentOutOfRangeException(nameof(limit));
            var rows = new List<AgreementPaymentFact>();
            if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(npcId) || playerId == npcId)
                return new AgreementPaymentHistory();
            foreach (var group in (journals ?? Enumerable.Empty<AgreementJournal>())
                .Where(j => j?.Agreement != null).GroupBy(j => j.Agreement.Id, StringComparer.Ordinal))
            {
                if (group.Count() != 1) continue;
                var journal = group.First();
                var agreement = journal.Agreement;
                if (string.IsNullOrWhiteSpace(agreement.Id) || agreement.PlayerId != playerId || agreement.NpcId != npcId
                    || string.IsNullOrEmpty(journal.CommittedHash) || journal.CommittedHash != agreement.TermsHash()
                    || !(agreement.HasRequiredConsents || journal.OperatorAuthorized && agreement.Origin == ActionOrigin.Console)
                    || !CommittedState(agreement.State)) continue;
                foreach (var clause in agreement.Clauses.Where(c => c.ActionId == "give.gold"))
                {
                    if (string.IsNullOrWhiteSpace(clause.Id) || agreement.Clauses.Count(c => c.Id == clause.Id) != 1
                        || !((clause.ActorId == playerId && clause.RecipientId == npcId)
                            || (clause.ActorId == npcId && clause.RecipientId == playerId))) continue;
                    var steps = journal.Steps.Where(s => s.ClauseId == clause.Id).ToList();
                    if (steps.Count != 1) continue;
                    var step = steps[0];
                    if (!step.Reserved || step.FulfilledInstallments <= 0 || clause.Installments < 1
                        || step.FulfilledInstallments > clause.Installments || step.Installment < 0
                        || step.Installment >= clause.Installments
                        || !(step.FulfilledInstallments == step.Installment
                            || step.FulfilledInstallments == step.Installment + 1 && step.Started && step.Completed)
                        || !clause.Arguments.TryGetValue("quantity", out string? quantityText)
                        || !int.TryParse(quantityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int quantity)
                        || quantity <= 0) continue;
                    // Return releases only the unfulfilled reservation. Previously delivered
                    // installments remain payments even after the unused escrow is returned.
                    rows.Add(new AgreementPaymentFact {
                        AgreementId = agreement.Id, ClauseId = clause.Id, TermsHash = journal.CommittedHash,
                        FromHeroId = clause.ActorId, ToHeroId = clause.RecipientId,
                        Amount = (long)quantity * step.FulfilledInstallments, QuantityPerInstallment = quantity,
                        FulfilledInstallments = step.FulfilledInstallments, ScheduledInstallments = clause.Installments,
                        StepStartedDay = step.StartedDay, UnusedReservationReturned = step.Returned,
                        AssociatedActionIds = agreement.Clauses.Where(c => !c.ActionId.StartsWith("give.", StringComparison.Ordinal))
                            .Select(c => c.ActionId).Distinct(StringComparer.Ordinal).ToArray()
                    });
                }
            }
            var ordered = rows.OrderByDescending(r => r.StepStartedDay)
                .ThenBy(r => r.AgreementId, StringComparer.Ordinal).ThenBy(r => r.ClauseId, StringComparer.Ordinal).ToList();
            return new AgreementPaymentHistory { Total = ordered.Count, Truncated = ordered.Count > limit,
                Rows = ordered.Take(limit).ToArray() };
        }

        private static bool CommittedState(AgreementState state) => state == AgreementState.InTransit
            || state == AgreementState.Waiting || state == AgreementState.Completed || state == AgreementState.Cancelled
            || state == AgreementState.Impossible || state == AgreementState.RecoveryRequired;
    }
}
