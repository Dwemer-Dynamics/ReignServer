using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Reign.Core.Contracts.Government
{
    /// <summary>Production boundary for private political conversations; never grants acceptance itself.</summary>
    public static class GovernmentPrivateConversationEligibility
    {
        public static bool CanDiscuss(bool matterOpen, bool votesRecorded, bool currentRuler,
            bool seatedMember, bool sameSettlement, bool blockedDuty)
            => matterOpen && !votesRecorded && currentRuler && seatedMember && sameSettlement && !blockedDuty;

        /// <summary>
        /// Verifies an already-classified accepted response against the reserved real conversation.
        /// Quote containment is provenance, not a linguistic consent classifier. The ordinary dialogue
        /// decision layer must supply explicit mutual acceptance, never a request or conditional promise.
        /// </summary>
        public static bool ValidateReceipt(GovernmentPrivateReceiptProof proof, out string reason)
        {
            reason = "The agreement lacks a matching reserved private conversation.";
            if (proof == null || !proof.Accepted || !Exact(proof.ReceiptId, proof.ExpectedReceiptId)
                || !Exact(proof.SessionId, proof.ExpectedSessionId)
                || !Exact(proof.MemberHeroId, proof.ExpectedMemberHeroId)
                || !Exact(proof.RulerHeroId, proof.ExpectedRulerHeroId)
                || !Exact(proof.BusinessId, proof.ExpectedBusinessId)
                || proof.MemberHeroId == proof.RulerHeroId) return false;
            reason = "Both parties' agreement must be quoted from this exact exchange.";
            if (!ContainsQuote(proof.PlayerText, proof.PlayerConsentQuote)
                || !ContainsQuote(proof.MemberReply, proof.MemberConsentQuote)) return false;
            reason = "The agreement does not name an option offered in this matter.";
            if (string.IsNullOrWhiteSpace(proof.OptionId)
                || !(proof.AllowedOptionIds ?? Array.Empty<string>()).Contains(proof.OptionId, StringComparer.Ordinal)) return false;
            reason = "The agreement method or payment terms are invalid.";
            if (proof.Method != "persuasion" && proof.Method != "bribe" && proof.Method != "deal") return false;
            if (proof.Method == "bribe" ? proof.Gold <= 0 : proof.Gold != 0) return false;
            if (proof.Method == "bribe" && (!ContainsExactGold(proof.PlayerConsentQuote, proof.Gold)
                || !ContainsExactGold(proof.MemberConsentQuote, proof.Gold)))
            { reason = "Both agreement quotes must state the exact agreed gold amount in digits."; return false; }
            reason = "A conditional deal must identify an offered verifiable obligation.";
            if (proof.Method == "deal" && (string.IsNullOrWhiteSpace(proof.ObligationId)
                || !(proof.VerifiableObligationIds ?? Array.Empty<string>()).Contains(proof.ObligationId, StringComparer.Ordinal))) return false;
            if (proof.Method != "deal" && !string.IsNullOrWhiteSpace(proof.ObligationId)) return false;
            reason = string.Empty;
            return true;
        }

        private static bool Exact(string value, string expected) => !string.IsNullOrWhiteSpace(value)
            && !string.IsNullOrWhiteSpace(expected) && string.Equals(value, expected, StringComparison.Ordinal);
        private static bool ContainsQuote(string text, string quote) => !string.IsNullOrWhiteSpace(quote)
            && quote.Trim().Length >= 5 && !string.IsNullOrWhiteSpace(text)
            && text.IndexOf(quote, StringComparison.Ordinal) >= 0;
        public static bool ContainsExactGold(string text, int gold)
        {
            if (gold <= 0 || string.IsNullOrWhiteSpace(text)) return false;
            foreach (Match match in Regex.Matches(text,
                @"(?<![\d.,-])(?:[0-9]{1,3}(?:,[0-9]{3})+|[0-9]+)(?![\d,]|\.[0-9])"))
                if (int.TryParse(match.Value.Replace(",", ""), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int amount) && amount == gold) return true;
            return false;
        }
    }

    public sealed class GovernmentPrivateReceiptProof
    {
        public bool Accepted { get; set; }
        public string ReceiptId { get; set; } = string.Empty;
        public string ExpectedReceiptId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string ExpectedSessionId { get; set; } = string.Empty;
        public string MemberHeroId { get; set; } = string.Empty;
        public string ExpectedMemberHeroId { get; set; } = string.Empty;
        public string RulerHeroId { get; set; } = string.Empty;
        public string ExpectedRulerHeroId { get; set; } = string.Empty;
        public string BusinessId { get; set; } = string.Empty;
        public string ExpectedBusinessId { get; set; } = string.Empty;
        public string PlayerText { get; set; } = string.Empty;
        public string MemberReply { get; set; } = string.Empty;
        public string PlayerConsentQuote { get; set; } = string.Empty;
        public string MemberConsentQuote { get; set; } = string.Empty;
        public string OptionId { get; set; } = string.Empty;
        public IEnumerable<string> AllowedOptionIds { get; set; } = Array.Empty<string>();
        public string Method { get; set; } = string.Empty;
        public int Gold { get; set; }
        public string ObligationId { get; set; } = string.Empty;
        public IEnumerable<string> VerifiableObligationIds { get; set; } = Array.Empty<string>();
    }
}
