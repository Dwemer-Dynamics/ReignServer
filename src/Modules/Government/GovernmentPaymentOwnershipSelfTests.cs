using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void AddGovernmentPaymentOwnershipSelfTests(Action<string, bool, string, object> add)
        {
            var payload = new Dictionary<string, object>
            {
                ["governmentPrivateContext"] = new Dictionary<string, object>
                {
                    ["enabled"] = true, ["rulerHeroId"] = "ruler", ["memberHeroId"] = "member",
                    ["business"] = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["businessId"] = "matter-1" } }
                }
            };
            var receipt = new Dictionary<string, object>
            {
                ["accepted"] = true, ["receiptId"] = "receipt-1", ["sessionId"] = "session-1",
                ["method"] = "bribe", ["gold"] = 125, ["businessId"] = "matter-1"
            };
            var response = new Dictionary<string, object> { ["governmentCommitment"] = receipt };
            string player = "I offer 125 gold for your vote. Separately, I offer a gift of 125 gold. I will buy your horse for 125 gold. I will pay the ransom of 125 gold.";
            string reply = "I accept 125 gold for my vote. I accept your gift of 125 gold. I will sell my horse for 125 gold. I accept the ransom of 125 gold.";
            var cases = new List<object>();
            bool correct = true;
            Action<string, Dictionary<string, object>, Dictionary<string, object>, string, bool> check = (name, raw, terms, command, expectedBlocked) =>
            {
                var errors = new List<string>();
                ValidateGovernmentPaymentOwnership(raw, terms, command, errors);
                bool blocked = errors.Count > 0;
                correct &= blocked == expectedBlocked;
                cases.Add(new { name, command, expectedBlocked, blocked, errors });
            };
            Func<string, string, int, Dictionary<string, object>> payment = (from, to, gold) => new Dictionary<string, object>
                { ["fromHeroStringId"] = from, ["toHeroStringId"] = to, ["gold"] = gold };
            using (ReserveGovernmentPaymentOwnership(payload, response, player, reply))
            {
                check("typed bribe owns matching transfer", new Dictionary<string, object>(), payment("ruler", "member", 125), "transfer_gold", true);
                receipt["receiptId"] = "stale";
                receipt["sessionId"] = "wrong-session";
                check("invalid typed receipt cannot fall back to generic transfer", new Dictionary<string, object>(), payment("ruler", "member", 125), "transfer_gold", true);
                check("reverse direction remains independent", new Dictionary<string, object>(), payment("member", "ruler", 125), "give_gold_to_player", false);
                var misleadingDirection = payment("ruler", "member", 125);
                misleadingDirection["goldFromHeroStringId"] = "member";
                misleadingDirection["goldToHeroStringId"] = "ruler";
                check("plain transfer ignores misleading package direction", new Dictionary<string, object>(), misleadingDirection, "transfer_gold", true);
                check("native pay-player command ignores misleading provider direction", new Dictionary<string, object>(), payment("ruler", "member", 125), "give_gold_to_player", false);
                check("different payee remains independent", new Dictionary<string, object>(), payment("ruler", "other", 125), "transfer_gold", false);
                check("different payer remains independent", new Dictionary<string, object>(), payment("other", "member", 125), "transfer_gold", false);
                check("unrelated amount remains independent", new Dictionary<string, object>(), payment("ruler", "member", 42), "transfer_gold", false);
                check("named matter remains typed even at a changed amount", new Dictionary<string, object> { ["reason"] = "payment for matter-1" }, payment("ruler", "member", 42), "transfer_gold", true);
                var marked = payment("ruler", "member", 42);
                marked["governmentReceiptId"] = "invented";
                check("JSON marker cannot release payment ownership", new Dictionary<string, object>(), marked, "transfer_gold", true);
                foreach (var independent in new[]
                {
                    new { purpose = "gift", command = "transfer_gold", playerQuote = "I offer a gift of 125 gold.", memberQuote = "I accept your gift of 125 gold." },
                    new { purpose = "purchase", command = "trade_package", playerQuote = "I will buy your horse for 125 gold.", memberQuote = "I will sell my horse for 125 gold." },
                    new { purpose = "ransom", command = "ransom_package", playerQuote = "I will pay the ransom of 125 gold.", memberQuote = "I accept the ransom of 125 gold." }
                })
                {
                    var terms = payment("ruler", "member", 125);
                    terms["purpose"] = independent.purpose;
                    terms["playerConsentQuote"] = independent.playerQuote;
                    terms["memberConsentQuote"] = independent.memberQuote;
                    check("separate accepted " + independent.purpose, new Dictionary<string, object>(), terms, independent.command, false);
                    if (independent.purpose == "gift")
                        check("independent accepted gift after council meeting", new Dictionary<string, object> { ["reason"] = "Gift after the council meeting" }, terms, independent.command, false);
                    terms["memberConsentQuote"] = "I accept a fictional gift of 125 gold.";
                    check("invented consent cannot disguise " + independent.purpose, new Dictionary<string, object>(), terms, independent.command, true);
                }
                var labelOnly = payment("ruler", "member", 125);
                labelOnly["purpose"] = "gift";
                check("purpose label alone cannot relabel ambiguous same amount", new Dictionary<string, object>(), labelOnly, "transfer_gold", true);
                foreach (string command in new[] { "trade_package", "ransom_package", "diplomatic_package" })
                    check("package direction owns " + command, new Dictionary<string, object>(), new Dictionary<string, object>
                    { ["goldFromHeroStringId"] = "ruler", ["goldToHeroStringId"] = "member", ["gold"] = 125 }, command, true);
                foreach (string method in new[] { "persuasion", "deal" })
                {
                    receipt["method"] = method;
                    receipt["gold"] = 0;
                    check(method + " cannot add a spurious government payment", new Dictionary<string, object> { ["reason"] = "government vote payment" }, payment("ruler", "member", 42), "transfer_gold", true);
                }
                using (ReserveGovernmentPaymentOwnership(payload, new Dictionary<string, object>(), player, reply))
                    check("political exchange without typed marker still blocks duplicate", new Dictionary<string, object>(), payment("ruler", "member", 125), "transfer_gold", true);
                using (ReserveGovernmentPaymentOwnership(payload, new Dictionary<string, object>(), "Hello, friend.", "Welcome to my home."))
                    check("no government agreement retains unrelated transfer", new Dictionary<string, object>(), payment("ruler", "member", 125), "transfer_gold", false);
                string deniedPlayer = "I offer 125 gold for your vote. Do not pay me a gift of 125 gold.";
                string deniedMember = "I accept 125 gold for my vote. I refuse your gift of 125 gold.";
                using (ReserveGovernmentPaymentOwnership(payload, response, deniedPlayer, deniedMember))
                {
                    var deniedGift = payment("ruler", "member", 125);
                    deniedGift["purpose"] = "gift";
                    deniedGift["playerConsentQuote"] = "Do not pay me a gift of 125 gold.";
                    deniedGift["memberConsentQuote"] = "I refuse your gift of 125 gold.";
                    check("actual negative quotes cannot authorize an independent gift", new Dictionary<string, object>(), deniedGift, "transfer_gold", true);
                }
                var mixedResponse = new Dictionary<string, object>
                {
                    ["governmentCommitment"] = new Dictionary<string, object> { ["method"] = "bribe", ["gold"] = 125 }
                };
                foreach (var exchange in new[]
                {
                    new { name = "player gift prefix omits trailing condition", playerText = "I offer a gift of 125 gold only after the harvest.", memberText = "I accept your gift of 125 gold.", blocked = true },
                    new { name = "member gift prefix omits trailing condition", playerText = "I offer a gift of 125 gold.", memberText = "I accept your gift of 125 gold if you win.", blocked = true },
                    new { name = "member gift prefix omits line-wrapped condition", playerText = "I offer a gift of 125 gold.", memberText = "I accept your gift of 125 gold\nif you win.", blocked = true },
                    new { name = "member gift prefix omits Windows line-wrapped condition", playerText = "I offer a gift of 125 gold.", memberText = "I accept your gift of 125 gold\r\nif you win.", blocked = true },
                    new { name = "refused vote in another sentence does not block independent gift", playerText = "I refuse to pay for your vote. I offer a gift of 125 gold.", memberText = "I will not sell my vote. I accept your gift of 125 gold.", blocked = false }
                })
                {
                    using (ReserveGovernmentPaymentOwnership(payload, mixedResponse, exchange.playerText, exchange.memberText))
                    {
                        var quotedGift = payment("ruler", "member", 125);
                        quotedGift["purpose"] = "gift";
                        quotedGift["playerConsentQuote"] = "I offer a gift of 125 gold";
                        quotedGift["memberConsentQuote"] = "I accept your gift of 125 gold";
                        check(exchange.name, new Dictionary<string, object>(), quotedGift, "transfer_gold", exchange.blocked);
                    }
                }
                using (ReserveGovernmentPaymentOwnership(new Dictionary<string, object>(), new Dictionary<string, object>(), player, reply))
                    check("nested unrelated request does not inherit ownership", new Dictionary<string, object>(), payment("ruler", "member", 125), "transfer_gold", false);
                check("nested scope disposal restores outer ownership", new Dictionary<string, object> { ["reason"] = "senate vote" }, payment("ruler", "member", 125), "transfer_gold", true);
            }
            add("government.private_payment_ownership_matrix", correct,
                "Production payment ownership prevents duplicate political transfers, including invalid receipts and package directions, while retaining separately evidenced economic payments. Quote containment proves provenance only; native dialogue acceptance remains separately gated.", cases);
            var afterScope = new List<string>();
            ValidateGovernmentPaymentOwnership(new Dictionary<string, object> { ["reason"] = "government vote" }, payment("ruler", "member", 125), "transfer_gold", afterScope);
            add("government.private_payment_scope_cleanup", afterScope.Count == 0,
                "Disposing the actual server turn scope removes payment ownership and leaves later requests independent.", new { errors = afterScope.ToArray() });
            var normalizedCases = new List<object>();
            bool normalizedCorrect = true;
            using (ReserveGovernmentPaymentOwnership(payload, response, player, reply))
            {
                foreach (string alias in new[] { "transfer_gold", "TransferGold", "RegularTransferGold", "trade_package", "ransom_package", "diplomatic_package" })
                {
                    var raw = new Dictionary<string, object>
                    {
                        ["command"] = alias, ["actorHeroStringId"] = "ruler", ["targetHeroStringId"] = "member",
                        ["GoldAmount"] = 125, ["reason"] = "Payment for the government vote",
                        ["terms"] = new Dictionary<string, object>
                        {
                            ["gold"] = 125, ["goldFromHeroStringId"] = "ruler", ["goldToHeroStringId"] = "member",
                            ["fromHeroStringId"] = "ruler", ["toHeroStringId"] = "member"
                        }
                    };
                    var record = NormalizeActionCommand(raw, "government_self_test", out List<string> errors);
                    bool owned = errors.Any(x => x.StartsWith("government_payment_", StringComparison.Ordinal));
                    normalizedCorrect &= owned;
                    normalizedCases.Add(new { alias, owned, recordReturned = record != null, errors });
                }
            }
            add("government.private_payment_normalizer_aliases", normalizedCorrect,
                "The real action normalizer applies the ownership guard after command aliases and payer/payee resolution, including compound payment packages. This verifies the rejection reason without claiming unrelated package prerequisites are satisfied.", normalizedCases);
        }
    }
}
