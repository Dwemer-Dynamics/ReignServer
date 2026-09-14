using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text.RegularExpressions;
using Reign.Core.Contracts.Government;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // This scope is created by the server from the actual response before the action
        // planner runs. JSON markers cannot create it, and retries retain the same scope.
        private static readonly AsyncLocal<GovernmentPaymentScope> GovernmentPaymentTurn = new AsyncLocal<GovernmentPaymentScope>();

        private sealed class GovernmentPaymentScope : IDisposable
        {
            public GovernmentPaymentScope Previous;
            public string RulerId = "", MemberId = "", PlayerText = "", Reply = "";
            public Dictionary<string, object> Commitment;
            public List<Dictionary<string, object>> Business = new List<Dictionary<string, object>>();
            public void Dispose() { GovernmentPaymentTurn.Value = Previous; }
        }

        private static IDisposable ReserveGovernmentPaymentOwnership(Dictionary<string, object> payload,
            Dictionary<string, object> response, string playerText, string reply)
        {
            var context = ReadDictionary(payload, "governmentPrivateContext");
            var scope = new GovernmentPaymentScope { Previous = GovernmentPaymentTurn.Value };
            // Always replace an outer scope, even for an unrelated nested request.
            if (ReadBool(context, "enabled", false))
            {
                scope.RulerId = ReadString(context, "rulerHeroId", "");
                scope.MemberId = ReadString(context, "memberHeroId", "");
                scope.PlayerText = playerText ?? "";
                scope.Reply = reply ?? "";
                scope.Commitment = ReadDictionary(response, "governmentCommitment");
                scope.Business = ReadDictionaryList(context, "business").Take(12).ToList();
            }
            GovernmentPaymentTurn.Value = scope;
            return scope;
        }

        // Called after canonicalization and reference/direction resolution, before QueueAction.
        // Rejection leaves the native typed receipt as the only possible payment owner;
        // invalid/stale receipts never fall back to a generic payment.
        private static void ValidateGovernmentPaymentOwnership(Dictionary<string, object> raw,
            Dictionary<string, object> terms, string command, List<string> errors)
        {
            var scope = GovernmentPaymentTurn.Value;
            if (scope == null || scope.RulerId.Length == 0 || scope.MemberId.Length == 0) return;
            // give_gold_to_player always pays the player ruler, so it cannot duplicate
            // this ruler-to-member bribe. Do not infer its direction from provider fields.
            bool paymentCommand = command == "transfer_gold"
                || command == "trade_package" || command == "ransom_package" || command == "diplomatic_package";
            if (!paymentCommand) return;
            int gold = ReadInt(terms, "gold", ReadInt(terms, "GoldAmount", ReadInt(terms, "goldAmount",
                ReadInt(raw, "GoldAmount", ReadInt(raw, "gold", 0)))));
            if (gold <= 0) return;
            bool package = command != "transfer_gold";
            string from = FirstNonEmpty(package ? ReadFirstString(terms, "goldFromHeroStringId", "fromHeroStringId") : ReadString(terms, "fromHeroStringId", ""),
                ReadFirstString(raw, "actorHeroStringId", "actorHeroId", "FromHero"));
            string to = FirstNonEmpty(package ? ReadFirstString(terms, "goldToHeroStringId", "toHeroStringId") : ReadString(terms, "toHeroStringId", ""),
                ReadFirstString(raw, "targetHeroStringId", "targetHeroId", "TargetHero", "ToHero"));
            if (!string.Equals(from, scope.RulerId, StringComparison.Ordinal)
                || !string.Equals(to, scope.MemberId, StringComparison.Ordinal)) return;

            string reason = ReadFirstString(raw, "reason", "publicReason") + " " + ReadFirstString(terms, "purpose", "paymentPurpose");
            string marker = ReadFirstString(terms, "governmentReceiptId", "governmentBusinessId");
            bool namedMatter = scope.Business.Any(b =>
            {
                string id = ReadString(b, "businessId", "");
                return id.Length > 0 && (reason.IndexOf(id, StringComparison.Ordinal) >= 0
                    || ReadString(terms, "businessId", "") == id);
            });
            bool politicalPayment = marker.Length > 0 || namedMatter || GovernmentPaymentLanguage(reason);
            bool sameTypedAmount = scope.Commitment != null && ReadInt(scope.Commitment, "gold", 0) == gold;
            bool politicalExchange = GovernmentPaymentLanguage(scope.PlayerText + " " + scope.Reply)
                && GovernmentPrivateConversationEligibility.ContainsExactGold(scope.PlayerText, gold);
            if (!politicalPayment && !sameTypedAmount && !politicalExchange) return;

            // A separate, actually accepted purchase/ransom/gift may coexist with lobbying.
            // A purpose label alone cannot launder the same bribe into another action.
            if (marker.Length == 0 && !namedMatter && HasIndependentGovernmentTurnPayment(scope, terms, command, gold)) return;
            errors.Add(politicalPayment ? "government_payment_owned_by_typed_receipt"
                : "government_payment_ambiguous: the same parties and amount need separate accepted payment evidence; no generic payment was queued");
        }

        private static bool GovernmentPaymentLanguage(string text) => Regex.IsMatch(text ?? "",
            @"(?i)\b(brib(?:e|ery|ing)|lobby(?:ing)?|government|senat(?:e|or)|council|hearing|vot(?:e|es|ing)|support (?:the|my|your|this) (?:policy|proposal|petition|motion))\b");

        private static bool HasIndependentGovernmentTurnPayment(GovernmentPaymentScope scope,
            Dictionary<string, object> terms, string command, int gold)
        {
            string playerQuote = ReadString(terms, "playerConsentQuote", "");
            string memberQuote = ReadString(terms, "memberConsentQuote", "");
            if (playerQuote.Length < 5 || memberQuote.Length < 5
                || scope.PlayerText.IndexOf(playerQuote, StringComparison.Ordinal) < 0
                || scope.Reply.IndexOf(memberQuote, StringComparison.Ordinal) < 0
                || !GovernmentPrivateConversationEligibility.ContainsExactGold(playerQuote, gold)
                || !GovernmentPrivateConversationEligibility.ContainsExactGold(memberQuote, gold)
                || GovernmentPaymentLanguage(playerQuote + " " + memberQuote)) return false;
            // A model-selected prefix must not omit a refusal or condition immediately
            // before/after the quoted words. Evaluate each whole actual sentence.
            string playerSentence = GovernmentPaymentEnclosingSentence(scope.PlayerText, playerQuote);
            string memberSentence = GovernmentPaymentEnclosingSentence(scope.Reply, memberQuote);
            if (!GovernmentIndependentPaymentAcceptance(playerSentence)
                || !GovernmentIndependentPaymentAcceptance(memberSentence)
                || GovernmentPaymentLanguage(playerSentence + " " + memberSentence)) return false;
            string purpose = ReadFirstString(terms, "paymentPurpose", "purpose");
            string pattern = command == "ransom_package" || purpose == "ransom" ? @"\b(ransom|prisoner|captive)\b"
                : command == "trade_package" || purpose == "purchase" ? @"\b(buy|sell|purchase|price|pay.*for)\b"
                : purpose == "gift" ? @"\bgift\b" : "(?!)";
            return Regex.IsMatch(playerQuote, pattern, RegexOptions.IgnoreCase)
                && Regex.IsMatch(memberQuote, pattern, RegexOptions.IgnoreCase);
        }

        // Conservative disambiguation for a second payment in a mixed conversation,
        // not a substitute for the ordinary action gate or native asset validation.
        private static bool GovernmentIndependentPaymentAcceptance(string quote)
        {
            if (Regex.IsMatch(quote, @"(?i)[?]|\b(no|not|never|refus\w*|declin\w*|reject\w*|if|unless|provided|once|when|until|after|only|would|could|might|may|cannot|can't|don't|won't|isn't|isnt|dont|wont)\b")) return false;
            return Regex.IsMatch(quote, @"(?i)\b(I (?:will )?(?:accept|agree|offer|pay|give|buy|sell)|agreed|accepted|yes)\b");
        }

        private static string GovernmentPaymentEnclosingSentence(string actualText, string quote)
        {
            int start = actualText.IndexOf(quote, StringComparison.Ordinal);
            if (start < 0 || actualText.IndexOf(quote, start + quote.Length, StringComparison.Ordinal) >= 0) return "";
            int end = start + quote.Length;
            // A line wrap does not end a condition or refusal in the actual utterance.
            char[] boundaries = { '.', '!', '?' };
            int previous = start == 0 ? -1 : actualText.LastIndexOfAny(boundaries, start - 1);
            // Include a terminal mark already inside the quote, otherwise all trailing
            // words through the next mark (not just the provider-selected substring).
            int next = end > 0 && Array.IndexOf(boundaries, actualText[end - 1]) >= 0 ? end - 1 : actualText.IndexOfAny(boundaries, end);
            if (next < 0) next = actualText.Length - 1;
            return actualText.Substring(previous + 1, next - previous);
        }
    }
}
