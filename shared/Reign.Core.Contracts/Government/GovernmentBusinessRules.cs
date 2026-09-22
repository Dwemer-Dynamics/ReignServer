using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Reign.Core.Contracts.Government
{
    /// <summary>Deterministic hearing rules. Scores are private simulation inputs, never intelligence.</summary>
    public static class GovernmentBusinessRules
    {
        public static bool IsRecordedAuthorizationRefusal(string status, string executedOptionId) =>
            string.Equals(status, "decided", StringComparison.OrdinalIgnoreCase)
            && string.Equals(executedOptionId, "reject", StringComparison.OrdinalIgnoreCase);

        public static ReignGovernmentActionKind ClassifyWorldAction(string type)
        {
            switch (type)
            {
                case "DiplomacyDeclareWar":
                case "DiplomacyPayToJoinWar":
                    return ReignGovernmentActionKind.War;
                case "DiplomacyMakePeace":
                case "DiplomacyOfferTributePeace":
                case "DiplomacyDemandReparationsPeace":
                case "DiplomacyDemandSettlementPeace":
                case "DiplomacyDemandSurrenderPeace":
                    return ReignGovernmentActionKind.Peace;
                case "DiplomacySignTradeAgreement":
                case "DiplomacySignNonAggressionPact":
                case "DiplomacySignAlliance":
                case "DiplomacySignDefensivePact":
                case "DiplomacySignTemporaryTruce":
                case "DiplomacyBreakTreaty":
                case "DiplomacyRecordPromise":
                case "DiplomacyExchangePrisoners":
                case "DiplomacyHostageGuarantee":
                case "DiplomacyRecognizeConquest":
                case "DiplomacyDemilitarizedBorder":
                case "DiplomacyTradeEmbargo":
                case "DiplomacyCaravanProtectionAgreement":
                case "DiplomacySupplyAgreement":
                case "DiplomacyGuaranteeIndependence":
                case "DiplomacyProtectorateOrVassalage":
                case "DiplomacyPackage":
                    return ReignGovernmentActionKind.Treaty;
                case "DiplomacyLoanOrSubsidy":
                case "DiplomacyPayToStayNeutral":
                case "DiplomacyWarIndemnity":
                case "DiplomacyRansomPackage":
                    return ReignGovernmentActionKind.MajorSpending;
                case "DiplomacyReturnOccupiedSettlement":
                    return ReignGovernmentActionKind.FiefTransfer;
                case "PoliticsExileClan":
                case "PoliticsRestoreExiledClan":
                case "RegularConfirmArrest":
                    return ReignGovernmentActionKind.MajorJustice;
                case "DiplomacyBackRebellion":
                case "RegularDismissPlayerVassal":
                    return ReignGovernmentActionKind.Policy;
                default:
                    return ReignGovernmentActionKind.Advice;
            }
        }

        public static bool IsConstitutionalWorldAction(string type) => ClassifyWorldAction(type) != ReignGovernmentActionKind.Advice;

        public static bool RequiresCounterpartWorldApproval(string type) => IsConstitutionalWorldAction(type)
            && type != null && type.StartsWith("Diplomacy", StringComparison.Ordinal)
            && type != "DiplomacyDeclareWar" && type != "DiplomacyBreakTreaty"
            && type != "DiplomacyTradeEmbargo" && type != "DiplomacyBackRebellion"
            && type != "DiplomacyRecordPromise" && type != "DiplomacyGuaranteeIndependence";

        public static string PublicText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var clauses = Regex.Split(text, @"((?<=[.!?;])\s+|[\r\n]+)");
            return string.Concat(clauses.Select(clause =>
                Regex.IsMatch(clause, @"\b(relations?|relationships?|trust|affinity|government\s+loyalty|party\s+loyalty|support\s+score)\b", RegexOptions.IgnoreCase)
                && Regex.IsMatch(clause, @"\d") ? "Private political measurements are withheld." : clause));
        }

        public const int RecessDays = 7;
        public const int PetitionExpiryDays = 21;
        public static bool IsClosed(string status) => status == "decided" || status == "invalidated" || status == "expired";
        public static bool CanPostpone(string status, bool urgent, bool postponed) => status == "hearing" && !urgent && !postponed;
        public static bool CanRecommend(string status) => status == "hearing" || status == "reconsideration" || status == "awaiting_ruler";
        public static bool CanVote(string status) => status == "hearing";

        public static int OptionScore(int interest, int partyAlignment, int rulerRelation, bool recommended,
            int commitmentShift, int personality, int stableVariance)
        {
            return Clamp(interest, -60, 60) + Clamp(partyAlignment, -40, 40)
                + (recommended ? Clamp(rulerRelation, -100, 100) / 4 : 0)
                + Clamp(commitmentShift, -30, 30) + Clamp(personality, -20, 20)
                + Clamp(stableVariance, -5, 5);
        }

        public static string SelectSponsor(IEnumerable<GovernmentSponsorCandidate> candidates)
        {
            // Relationship ranks genuinely willing members; it cannot create policy willingness.
            return (candidates ?? Array.Empty<GovernmentSponsorCandidate>())
                .Where(x => x != null && x.Eligible && x.IssueSupport > 0 && !string.IsNullOrWhiteSpace(x.HeroId))
                .OrderByDescending(x => x.IssueSupport + Clamp(x.PetitionerRelation, -100, 100) / 5)
                .ThenBy(x => x.HeroId, StringComparer.Ordinal).Select(x => x.HeroId).FirstOrDefault() ?? string.Empty;
        }

        public static string SelectWinner(IEnumerable<string> ballots, IEnumerable<string> options, string statusQuoOptionId)
        {
            string[] valid = (options ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (valid.Length == 0) return string.Empty;
            var votes = (ballots ?? Array.Empty<string>()).Where(valid.Contains).ToList();
            if (votes.Count == 0) return string.Empty; // An empty institution may not invent approval.
            var tally = valid.Select(x => new { Id = x, Count = votes.Count(v => v == x) }).ToList();
            int highest = tally.Max(x => x.Count);
            var tied = tally.Where(x => x.Count == highest).Select(x => x.Id).ToList();
            if (tied.Contains(statusQuoOptionId)) return statusQuoOptionId;
            // Essential multi-candidate allocations have no status quo. Stable lot prevents stalled ownership.
            return tied.OrderBy(x => ReignGovernmentRules.StableHash("government-tie|" + x)).ThenBy(x => x, StringComparer.Ordinal).First();
        }

        public static int ParticipantRelationDelta(bool granted, bool rulerRecommendedRequest, bool bindingVote,
            bool petitioner, bool sponsor, bool beneficiary, bool harmed)
        {
            int magnitude = petitioner || sponsor ? 4 : beneficiary || harmed ? 2 : 0;
            if (magnitude == 0) return 0;
            int delta = harmed ? (granted ? -magnitude : magnitude) : (granted ? magnitude : -magnitude);
            if (bindingVote && rulerRecommendedRequest != granted)
                delta = delta / 2; // Constituents distinguish the recommendation from the binding institution.
            return delta;
        }
        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    }

    public sealed class GovernmentSponsorCandidate
    {
        public string HeroId { get; set; } = string.Empty;
        public bool Eligible { get; set; }
        public int IssueSupport { get; set; }
        public int PetitionerRelation { get; set; }
    }
}
