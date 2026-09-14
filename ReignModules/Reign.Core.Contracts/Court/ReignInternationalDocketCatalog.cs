using System;
using System.Collections.Generic;
using System.Linq;

namespace Reign.Core.Contracts.Court
{
    public sealed class ReignInternationalTemplate
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public ReignNobleMatterSeverity Severity { get; set; }
        public string Premise { get; set; } = string.Empty;
        public string SceneGuidance { get; set; } = string.Empty;
        public string Channel { get; set; } = "treaty_strain";
        public string Remedy { get; set; } = "compensation";
        public bool BorderOnly { get; set; }
        public bool Constructive { get; set; }
        public bool Extortion { get; set; }
        public bool RequiresCaptive { get; set; }
        public int Gold { get; set; }
        public string[] ResolutionIds { get; set; } = Array.Empty<string>();
    }

    public static class ReignInternationalDocketRules
    {
        public static ReignNobleMatterSeverity SeverityForRoll(int roll)
        {
            if (roll < 0 || roll >= 100) throw new ArgumentOutOfRangeException(nameof(roll));
            return roll < 50 ? ReignNobleMatterSeverity.Petty : roll < 80 ? ReignNobleMatterSeverity.Serious
                : roll < 95 ? ReignNobleMatterSeverity.Grave : ReignNobleMatterSeverity.Exceptional;
        }
        public static int PressureMagnitude(ReignNobleMatterSeverity severity) => new[] { 6, 10, 16, 24 }[(int)severity];
        public static int RulerRelation(int nobleEffect) => (int)Math.Round(nobleEffect * .25d, MidpointRounding.AwayFromZero);
        public static int AmbassadorCharmBand(int charm) => charm >= 200 ? 4 : charm >= 150 ? 3 : charm >= 100 ? 2 : charm >= 50 ? 1 : 0;
        public static bool ExtortionEligible(double ownStrength, double playerStrength, int boldness, int honor)
            => ownStrength > 0d && playerStrength > 0d && ownStrength / playerStrength + 1e-12d >= 1.5d && boldness >= 61 && honor <= 40;

        public static bool CounterTermsWithinBounds(long? gold, long? durationDays)
            => (gold.HasValue || durationDays.HasValue) && (!gold.HasValue || gold.Value >= 0 && gold.Value <= 1000000)
                && (!durationDays.HasValue || durationDays.Value >= 1 && durationDays.Value <= 365);
        public static bool RequiresForeignAcceptance(string optionId, string remedy, bool ransom)
        {
            if (string.Equals(optionId, "compromise", StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.Equals(optionId, "accept", StringComparison.OrdinalIgnoreCase)) return false;
            return !string.Equals(remedy, "prisoner", StringComparison.OrdinalIgnoreCase) || ransom;
        }
        public static int NpcPressureDelta(ReignNobleMatterSeverity severity, bool favorsPlayer, bool hostile)
            => favorsPlayer || !hostile ? -PressureMagnitude(severity) : PressureMagnitude(severity);

        public static bool PaymentPartiesAllowed(string payerId, string recipientId, string playerId,
            string foreignRulerId, string domesticLordId, string foreignLordId, bool incoming, bool extortion)
        {
            if (string.IsNullOrWhiteSpace(payerId) || string.IsNullOrWhiteSpace(recipientId)
                || string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(foreignRulerId)
                || payerId == recipientId) return false;
            if (incoming) return payerId == foreignRulerId && recipientId == playerId;
            return payerId == playerId && (recipientId == foreignRulerId
                || (!extortion && (recipientId == domesticLordId || recipientId == foreignLordId)));
        }
    }

    public static class ReignInternationalDocketCatalog
    {
        private static readonly IReadOnlyList<ReignInternationalTemplate> Values = Build();
        public static IReadOnlyList<ReignInternationalTemplate> Templates => Values;
        public static ReignInternationalTemplate? Find(string id) => Values.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        public static IReadOnlyList<ReignInternationalTemplate> ForSeverity(ReignNobleMatterSeverity severity)
            => Values.Where(x => x.Severity == severity).ToArray();

        private static IReadOnlyList<ReignInternationalTemplate> Build()
        {
            var rows = new List<ReignInternationalTemplate>();
            Add(rows, 0, "grazing", "Straying Herds", "A border lord alleges that a foreign household's herds used disputed pasture.", true);
            Add(rows, 0, "hunt", "A Hunt Across the Boundary", "A hunt crossed a neighbor's claimed grounds; the households disagree over courtesy and compensation.", true);
            Add(rows, 0, "toll", "A Traveler's Toll", "A noble disputes a small toll requested by the other realm's gate attendants.", true);
            Add(rows, 0, "mill", "The Miller's Account", "Two households dispute the fee for milling grain brought across their border.", true);
            Add(rows, 0, "fishing", "Nets in Shared Water", "Neighboring estates dispute where their dependents may set fishing nets.", true);
            Add(rows, 0, "wood", "Fallen Timber", "A household seeks restitution for timber gathered on the other side of an uncertain boundary.", true);
            Add(rows, 0, "bridge", "The Bridge Keeper", "A visiting household challenges a bridge keeper's extra charge.", true);
            Add(rows, 0, "ferry", "A Ferry Fare", "Retainers of two houses dispute payment for a river crossing.", true);
            Add(rows, 0, "market", "Market Place Precedence", "A foreign noble's merchant was denied a customary market position.");
            Add(rows, 0, "lodging", "The Reserved Chambers", "A traveling noble claims a host gave promised lodgings to another household.");
            Add(rows, 0, "seat", "Seats at a Foreign Feast", "A noble asks whether a foreign host's seating arrangement was an intentional slight.", remedy: "apology");
            Add(rows, 0, "title", "A Title Left Unsaid", "A foreign court used an abbreviated title that a noble considers disrespectful.", remedy: "apology");
            Add(rows, 0, "banner", "The Lowered Banner", "A household complains about the placement of its banner during a neighboring realm's ceremony.", remedy: "apology");
            Add(rows, 0, "gift", "The Unacknowledged Gift", "A ceremonial gift received no acknowledgment, and its giver seeks an explanation.", remedy: "apology");
            Add(rows, 0, "letter", "A Brusque Letter", "An ambiguous phrase in a foreign lord's letter has offended its recipient.", remedy: "apology");
            Add(rows, 0, "retainer", "Words Between Retainers", "Retainers traded insults while their lords were visiting the same town.", remedy: "apology");
            Add(rows, 0, "falcon", "A Falcon's Return", "A falcon landed in a foreign mews and the households dispute a recovery fee.");
            Add(rows, 0, "horse", "A Horse Hire Account", "A visiting noble disputes a foreign stable's hire bill.");
            Add(rows, 0, "hounds", "Hounds in the Orchard", "A border household alleges that a visiting hunt damaged an orchard.", true);
            Add(rows, 0, "artisan", "An Artisan's Deposit", "A foreign commission ended in a disagreement over an artisan's deposit.");
            Add(rows, 0, "musician", "The Double Invitation", "A performer accepted invitations from patrons in two kingdoms for the same evening.");
            Add(rows, 0, "credit", "Credit for a Courtesy", "Two lords dispute which household arranged hospitality for travelers.", remedy: "apology");
            Add(rows, 0, "translation", "An Unfortunate Translation", "A translated greeting appears insulting, but its intended meaning is disputed.", remedy: "apology");
            Add(rows, 0, "escort", "An Escort's Expenses", "A household contests the cost of a courtesy escort supplied by a foreign lord.");
            Add(rows, 0, "boundary-marker", "The Moved Marker", "Neighbors ask for mediation after a minor boundary marker was allegedly moved.", true);
            Add(rows, 0, "watering", "The Watering Place", "Two households disagree over access to a watering place used by travelers.", true);
            Add(rows, 0, "welcome-gift", "A Token of Welcome", "An envoy offers a modest gift from their ruler to open a warmer relationship.", constructive: true, remedy: "gift");
            Add(rows, 0, "scribe", "An Exchange of Scribes", "An envoy proposes a ceremonial exchange of written histories and greetings.", constructive: true, remedy: "goodwill");
            Add(rows, 0, "fair", "An Invitation to the Fair", "An envoy invites the realm's nobles to cultivate peaceful commercial relations.", constructive: true, remedy: "goodwill");
            Add(rows, 0, "condolence", "Condolences and Courtesy", "An envoy offers a formal message of sympathy or goodwill appropriate to recorded events.", constructive: true, remedy: "goodwill");

            Add(rows, 1, "cargo-account", "A Disputed Cargo Account", "A lord alleges substantial unpaid charges arising from foreign commerce.");
            Add(rows, 1, "estate-damage", "Damage Across the Border", "Neighboring estates submit competing claims for damaged property.", true);
            Add(rows, 1, "loan", "A Noble's Unpaid Loan", "Two nobles seek royal mediation over a disputed personal loan across kingdoms.");
            Add(rows, 1, "public-accusation", "An Accusation Before Witnesses", "A foreign lord publicly accused a noble of dishonesty; evidence and retraction are contested.", remedy: "apology");
            Add(rows, 1, "safe-conduct-fee", "The Price of Safe Conduct", "A foreign household's requested passage payment has become a diplomatic complaint.");
            Add(rows, 1, "inheritance-payment", "An Inheritance Across Kingdoms", "Related houses dispute a monetary portion of an inheritance held abroad.");
            Add(rows, 1, "marriage-gift", "A Marriage Gift Disputed", "An earlier family agreement left a ceremonial payment disputed between kingdoms.");
            Add(rows, 1, "merchant-patron", "A Merchant's Noble Patron", "A ruler's lord is accused of shielding a merchant from a legitimate debt.");
            Add(rows, 1, "slander", "A Story Traveling Too Far", "A lord asks the ruler to challenge a damaging foreign accusation without treating rumor as proof.", remedy: "apology");
            Add(rows, 1, "grain-offer", "Aid for a Neighbor", "An envoy proposes a funded relief contribution under exact monetary terms.", constructive: true, remedy: "gift");
            Add(rows, 1, "trade", "A Commercial Opening", "An ambassador offers a national trade agreement within their charter or subject to referral.", constructive: true, remedy: "sign_trade_agreement");
            Add(rows, 1, "festival-aid", "A Shared Celebration", "An envoy offers funding for a ceremony acknowledging friendship between the realms.", constructive: true, remedy: "gift");
            Add(rows, 1, "prisoner", "A Captive's Petition", "An envoy seeks the lawful release of a real captive presently held by the ruler's side.", remedy: "prisoner");
            Add(rows, 1, "ransom", "A Ransom Negotiation", "An envoy asks to settle the price of a real noble captive's release.", remedy: "prisoner");
            Add(rows, 1, "tribute-demand", "A Price for Quiet Borders", "A dominant ruler uses an envoy to demand payment under an implied threat.", extortion: true);

            Add(rows, 2, "repeated-exactions", "Repeated Exactions", "A foreign ruler submits accumulated compensation claims against a lord's retainers.");
            Add(rows, 2, "warehouse", "A Warehouse Claim", "Noble patrons dispute responsibility for a substantial commercial loss; the allegation requires examination.");
            Add(rows, 2, "envoy-insult", "An Envoy Publicly Humiliated", "A ruler demands redress for a serious insult attributed to the other realm's noble.", remedy: "apology");
            Add(rows, 2, "estate-restitution", "Restitution for an Estate", "Border nobles bring competing monetary claims for alleged estate damage.", true);
            Add(rows, 2, "road-claims", "A Route Under Dispute", "Nobles dispute repeated charges along a commercially important frontier road.", true);
            Add(rows, 2, "broken-guarantee", "A Broken Assurance", "An envoy challenges a recorded undertaking and requests a defined monetary settlement.");
            Add(rows, 2, "collective-debt", "The Debts of Several Houses", "An envoy represents several creditors seeking a bounded settlement from a noble patron.");
            Add(rows, 2, "captured-relative", "A Household's Captive", "An important household petitions for a real captive's release without inventing the capture.", remedy: "prisoner");
            Add(rows, 2, "ransom-intercession", "Royal Ransom Intercession", "A ruler seeks an exact settlement for a currently held foreign noble.", remedy: "prisoner");
            Add(rows, 2, "trade-renewal", "Restoring Commerce", "An ambassador offers a trade agreement to ease accumulated hostility.", constructive: true, remedy: "sign_trade_agreement");
            Add(rows, 2, "relief-fund", "Royal Relief Funding", "A neighboring ruler offers a substantial gold contribution as a gesture of reconciliation.", constructive: true, remedy: "gift");
            Add(rows, 2, "peaceful-recognition", "Recognition After Rivalry", "An envoy seeks a public acknowledgment that would repair a damaged diplomatic relationship.", constructive: true, remedy: "goodwill");
            Add(rows, 2, "protection-demand", "Protection at a Price", "An opportunistic dominant ruler demands money while implying consequences for refusal.", extortion: true);
            Add(rows, 2, "compelled-gift", "A Gift Expected", "A powerful ruler dresses a coercive demand in the language of friendship.", extortion: true);
            Add(rows, 2, "border-ultimatum", "A Frontier Ultimatum", "A dominant ruler presses a monetary demand tied to existing frontier tensions.", true, extortion: true);

            Add(rows, 3, "royal-reparations", "A Royal Reparations Claim", "A sovereign brings a major, contested compensation demand through an authorized envoy.");
            Add(rows, 3, "dynastic-debt", "A Dynastic Debt", "A disputed inherited obligation between great houses has become a matter between their sovereigns.",
                sceneGuidance: "The defining dispute is an alleged obligation inherited from an earlier generation. Name the deceased or former office-holders only by supplied household roles, never by invented personal names. State what the earlier undertaking allegedly promised, how each side says it passed or did not pass to the current houses, and the competing documentary, witness, performance, release, limitation, or succession evidence. The current claim may seek the supplied monetary settlement, but it must arise from that inherited undertaking. Do not replace the dynastic debt with a contemporary gate, escort, storage, toll, cargo-damage, service-fee, or retainer dispute.");
            Add(rows, 3, "frontier-crisis", "A Frontier Crisis", "Accumulated claims between border lords demand a definitive monetary or diplomatic ruling.", true);
            Add(rows, 3, "honor-crisis", "A Sovereign's Honor", "A grave public accusation against a lord threatens relations unless evidence and redress are addressed.", remedy: "apology");
            Add(rows, 3, "treaty-redress", "Redress for a Recorded Undertaking", "An envoy seeks compensation tied to an actually recorded diplomatic undertaking.");
            Add(rows, 3, "great-house-captive", "The Captive of a Great House", "An envoy seeks release of an eligible real noble captive whose standing makes the matter exceptional.", remedy: "prisoner",
                sceneGuidance: "The supplied sceneContext.captive is the defining subject. Name that real captive and their supplied house and standing, explain why that standing raises the political stakes, and preserve the supplied fact that the captive is currently held by the player. The exact request is release without ransom. Do not invent how the captive was taken, a crime, mistreatment, a treaty right, a payment, a prior promise, or a different prisoner. If capture circumstances are not supplied, say they are outside the evidence rather than leaving the captive's identity and house vague.");
            Add(rows, 3, "royal-ransom", "A Sovereign's Ransom Offer", "A ruler offers a major ransom for an eligible captive; exact payment and release must be simultaneous.", remedy: "prisoner",
                sceneGuidance: "The supplied sceneContext.captive is the defining subject. Name that real captive and their supplied house and standing, preserve the supplied current custody, and make the political reason for the sovereign's intercession concrete without inventing how the captive was taken, a crime, mistreatment, or a treaty right. Preserve the exact offered ransom, payer, recipient and simultaneous release requirement from the available action. Do not substitute a different prisoner, amount, payment direction, or no-ransom release.");
            Add(rows, 3, "shared-future", "A New Commercial Settlement", "A ruler offers national trade terms intended to replace prolonged rivalry.", constructive: true, remedy: "sign_trade_agreement");
            Add(rows, 3, "royal-endowment", "A Royal Endowment", "An envoy offers a major real gold contribution to mark a new diplomatic understanding.", constructive: true, remedy: "gift");
            Add(rows, 3, "public-reconciliation", "Reconciliation Before the Realms", "An envoy proposes a formal public reconciliation while leaving unsupported treaty effects unclaimed.", constructive: true, remedy: "goodwill");
            Add(rows, 3, "national-debt", "The Crown and a Noble's Debt", "Foreign creditors ask the crown to settle a lord's disputed major obligation.");
            Add(rows, 3, "command-responsibility", "Responsibility for Retainers", "A sovereign asks whether the ruler will accept financial responsibility for a lord's alleged conduct.");
            Add(rows, 3, "submission-payment", "A Payment of Submission", "A dominant dishonorable ruler demands a conspicuous payment meant to demonstrate submission.", extortion: true);
            Add(rows, 3, "price-of-peace", "The Price of Continued Peace", "A stronger opportunistic ruler demands gold while invoking the possibility of future hostility.", extortion: true);
            Add(rows, 3, "final-demand", "The Ambassador's Final Demand", "A dominant ruler issues an exceptional monetary demand; rejection informs diplomacy rather than automatically starting war.", extortion: true);
            return rows.AsReadOnly();
        }

        private static void Add(List<ReignInternationalTemplate> rows, int severity, string id, string title, string premise,
            bool border = false, bool constructive = false, bool extortion = false, string remedy = "compensation",
            string sceneGuidance = "")
        {
            rows.Add(new ReignInternationalTemplate
            {
                Id = "international-" + new[] { "petty", "serious", "grave", "exceptional" }[severity] + "-" + id,
                Title = title, Premise = premise, SceneGuidance = sceneGuidance,
                Severity = (ReignNobleMatterSeverity)severity,
                BorderOnly = border, Constructive = constructive, Extortion = extortion,
                RequiresCaptive = remedy == "prisoner", Remedy = remedy,
                Channel = extortion ? "coercion" : constructive ? "trade" : "treaty_strain",
                Gold = new[] { 500, 2000, 5000, 10000 }[severity],
                ResolutionIds = constructive || remedy == "prisoner" ? new[] { "accept", "refuse", "refer" }
                    : extortion ? new[] { "compensate", "refuse", "refer" }
                    : new[] { "favor_domestic", "favor_foreign", "compensate", "compromise", "refer" }
            });
        }
    }
}
