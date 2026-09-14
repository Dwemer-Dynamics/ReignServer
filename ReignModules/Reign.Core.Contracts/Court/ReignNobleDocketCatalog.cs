using System;
using System.Collections.Generic;
using System.Linq;

namespace Reign.Core.Contracts.Court
{
    public enum ReignNobleMatterSeverity
    {
        Petty = 0,
        Serious = 1,
        Grave = 2,
        Exceptional = 3
    }

    public enum ReignNobleMatterCategory
    {
        Etiquette = 0,
        Property = 1,
        Finance = 2,
        Military = 3,
        Family = 4,
        Marriage = 5,
        Divorce = 6,
        Scandal = 7,
        Crime = 8,
        Governance = 9,
        Dynastic = 10
    }

    public sealed class ReignNobleMatterTemplate
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public ReignNobleMatterSeverity Severity { get; set; }
        public ReignNobleMatterCategory Category { get; set; }
        public string Premise { get; set; } = string.Empty;
        public string DemandA { get; set; } = string.Empty;
        public string DemandB { get; set; } = string.Empty;
        public int MinimumPrincipals { get; set; } = 2;
        public int MaximumPrincipals { get; set; } = 2;
        public bool RequiresSpouses { get; set; }
        public bool RequiresUnmarriedPair { get; set; }
        public bool RequiresLoverAffinity { get; set; }
        public bool RequiresClanLeader { get; set; }
        public bool CreatesStateOnActivation { get; set; }
        public bool UsesHiddenTruth { get; set; }
        public bool IsMurderInvestigation { get; set; }
        public string[] ApplicableNegativeTags { get; set; } = Array.Empty<string>();
        public bool NegativeTagsApplyOnSideA { get; set; } = true;
        public bool NegativeTagsApplyOnSideB { get; set; } = true;
        public bool ApplyNegativeTagsToAllRejectedParticipants { get; set; }
    }

    public static class ReignNobleDocketCatalog
    {
        private static readonly IReadOnlyList<ReignNobleMatterTemplate> TemplatesValue = Build();

        public static IReadOnlyList<ReignNobleMatterTemplate> Templates => TemplatesValue;

        public static ReignNobleMatterTemplate? Find(string id)
        {
            return TemplatesValue.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public static IReadOnlyList<ReignNobleMatterTemplate> ForSeverity(ReignNobleMatterSeverity severity)
        {
            return TemplatesValue.Where(x => x.Severity == severity).ToList();
        }

        private static IReadOnlyList<ReignNobleMatterTemplate> Build()
        {
            var items = new List<ReignNobleMatterTemplate>();

            Add(items, "petty-seat-precedence", "Precedence at the High Table", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Etiquette,
                "Two nobles dispute which household is entitled to the more honored seat.", "Recognize the first house's precedence.", "Recognize the second house's precedence.");
            Add(items, "petty-banner-position", "The Banner Above the Gate", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Etiquette,
                "Two houses claim the more prominent place for their banner during court.", "Order the first banner displayed above the second.", "Order the second banner displayed above the first.");
            Add(items, "petty-hunt-quarry", "The Stolen Quarry", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Etiquette,
                "One noble claims another took credit for a stag brought down during a royal hunt.", "Publicly credit the first hunter.", "Publicly credit the second hunter.");
            Add(items, "petty-feast-insult", "An Insult at Supper", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Scandal,
                "A cutting remark at a feast has become a question of honor.", "Require a public apology to the insulted noble.", "Declare the words too slight for royal remedy.");
            Add(items, "petty-livery-color", "Colors Too Similar", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Etiquette,
                "Two households accuse one another of copying livery colors.", "Reserve the disputed colors for the first house.", "Reserve the disputed colors for the second house.");
            Add(items, "petty-hawk-ownership", "The Falconer's Error", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Property,
                "A prized hawk returned to the wrong mews and both nobles claim it.", "Award the hawk to the first claimant.", "Award the hawk to the second claimant.");
            Add(items, "petty-musician-contract", "A Minstrel's Promise", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Finance,
                "A celebrated performer accepted invitations from two houses for the same evening.", "Enforce the first invitation.", "Enforce the second invitation.");
            Add(items, "petty-training-yard", "Hours in the Training Yard", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Military,
                "Two retinues demand exclusive use of the keep's training yard.", "Grant the preferred hours to the first retinue.", "Grant the preferred hours to the second retinue.");
            Add(items, "petty-kennel-damage", "Hounds in the Garden", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Property,
                "Hunting hounds damaged a noble garden and responsibility is disputed.", "Order the hounds' owner to compensate the gardener.", "Dismiss the damage as an ordinary risk of court life.");
            Add(items, "petty-tourney-credit", "The Contested Tourney Blow", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Etiquette,
                "Two nobles claim the decisive blow in a recent melee.", "Recognize the first combatant.", "Recognize the second combatant.");
            Add(items, "petty-chapel-pew", "The Chapel Pew", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Etiquette,
                "Two families claim an ancestral place of honor in the chapel.", "Confirm the first family's place.", "Confirm the second family's place.");
            Add(items, "petty-servant-poaching", "A Valet Enticed Away", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Finance,
                "One household hired a valued servant away from another.", "Return the servant or pay compensation.", "Uphold the servant's new employment.");
            Add(items, "petty-wedding-gift", "The Missing Wedding Gift", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Family,
                "A promised ceremonial gift was never delivered.", "Order immediate delivery or payment.", "Release the alleged giver from the promise.");
            Add(items, "petty-road-toll", "A Petty Toll", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Property,
                "Neighboring estates dispute a small customary road toll.", "Confirm the first estate's right to collect.", "End the toll in favor of the second estate.");
            Add(items, "petty-market-stall", "The Festival Stall", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Property,
                "Two noble patrons promised the same prime festival stall to different merchants.", "Honor the first patron's grant.", "Honor the second patron's grant.");
            Add(items, "petty-poem-likeness", "A Satirical Likeness", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Scandal,
                "A court poem is said to mock one noble, while its patron denies the likeness.", "Censure the patron and suppress the poem.", "Protect the poem as harmless wit.");
            Add(items, "petty-horse-breeding", "The Stallion's Service", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Finance,
                "Payment for breeding a prized warhorse is disputed.", "Enforce the stud fee claimed by the owner.", "Reduce or void the fee for the mare's owner.");
            Add(items, "petty-escort-order", "Place in the Procession", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Etiquette,
                "Two nobles demand the place nearest the ruler in a procession.", "Grant precedence to the first noble.", "Grant precedence to the second noble.");
            Add(items, "petty-library-book", "The Borrowed Chronicle", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Property,
                "A rare family chronicle was lent and not returned.", "Order its immediate return.", "Recognize it as a completed gift.");
            Add(items, "petty-toast-omission", "The Forgotten Toast", ReignNobleMatterSeverity.Petty, ReignNobleMatterCategory.Etiquette,
                "A house was omitted from a ceremonial toast and claims deliberate humiliation.", "Require a corrective public toast.", "Declare the omission accidental and closed.");

            Add(items, "serious-boundary-stones", "Moved Boundary Stones", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Property,
                "Neighboring estates accuse each other of moving their boundary markers.", "Recognize the first survey and boundary.", "Recognize the second survey and boundary.");
            Add(items, "serious-water-rights", "Rights to the Millstream", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Property,
                "Two lords claim priority over water needed by their villages and mills.", "Grant priority to the first estate.", "Grant priority to the second estate.");
            Add(items, "serious-debt-guarantee", "A Noble's Guarantee", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Finance,
                "A lord denies guaranteeing another house's debt.", "Enforce immediate payment by the guarantor.", "Void the disputed guarantee.");
            Add(items, "serious-dowry-default", "The Unpaid Dowry", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Family,
                "A marriage alliance is strained by an unpaid dowry.", "Order the bride's clan to pay now.", "Release the clan from the disputed balance.");
            Add(items, "serious-betrothal-compensation", "A Broken Betrothal", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Marriage,
                "A house broke a negotiated betrothal after gifts changed hands.", "Compel the marriage if valid or exact compensation.", "Permit the refusal and deny further claim.", false, true);
            Add(items, "serious-love-marriage-denied", "A Marriage for Love", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Marriage,
                "Two unmarried adults ask the ruler to permit their marriage after a clan leader refused them.", "Permit and solemnize the marriage.", "Uphold the clan leader's refusal.", true, true, true, new string[0]);
            Add(items, "serious-heir-marriage", "The Heir's Refusal", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Marriage,
                "An adult heir resists a politically purchased betrothal.", "Compel the technically valid marriage.", "Release the heir from the arrangement.", false, true, true, new string[0]);
            Add(items, "serious-ward-custody", "Custody of a Noble Ward", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Family,
                "Two relatives dispute who should shelter and educate a young noble ward.", "Recognize the first household's guardianship.", "Recognize the second household's guardianship.");
            Add(items, "serious-inheritance-chattel", "The Divided Inheritance", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Property,
                "Heirs dispute valuable movable property excluded from a clear land succession.", "Award the goods to the first heir.", "Award the goods to the second heir.");
            Add(items, "serious-village-grazing", "Common Grazing Rights", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Property,
                "Two lords' villages contest access to seasonal pasture.", "Confirm grazing for the first village.", "Confirm grazing for the second village.");
            Add(items, "serious-levy-credit", "Credit for the Levy", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Military,
                "Two commanders claim to have supplied the same soldiers to the realm.", "Recognize the first commander's contribution.", "Recognize the second commander's contribution.");
            Add(items, "serious-campaign-supplies", "Spoiled Campaign Stores", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Military,
                "A commander and quartermaster blame each other for lost supplies.", "Hold the commander responsible.", "Hold the quartermaster responsible.", false, false, false, new[] { "incompetent" });
            Add(items, "serious-ransom-debt", "The Ransom Debt", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Finance,
                "A rescued noble refuses to repay the house that funded a ransom.", "Order immediate repayment.", "Declare the payment a voluntary gift.");
            Add(items, "serious-raid-reprisals", "Reprisals Across the Border", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Military,
                "One lord's reprisals damaged tenants claimed by another.", "Require compensation from the raiding lord.", "Validate the reprisals as militarily necessary.", false, false, false, new[] { "cruel" }, negativeTagsApplyOnSideB: false);
            Add(items, "serious-duel-injury", "The Duel's Physician", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Finance,
                "After a lawful duel, the injured party demands the victor pay medical costs.", "Order the victor to compensate the wounded party.", "Hold that the duel settled all claims.");
            Add(items, "serious-command-insult", "Insult Before the Ranks", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Military,
                "A public insult between officers threatens discipline.", "Censure the first officer.", "Censure the second officer.");
            Add(items, "serious-tax-collection", "Taxes Collected Twice", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Governance,
                "Two authorities each claim the same tenants owed them tax.", "Recognize the first collector and require restitution.", "Recognize the second collector and require restitution.");
            Add(items, "serious-merchant-patronage", "A Merchant Under Protection", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Finance,
                "Two houses claim exclusive patronage over a wealthy merchant.", "Recognize the first house's contract.", "Recognize the second house's contract.");
            Add(items, "serious-fostered-child", "A Fosterling Recalled", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Family,
                "A parent demands the return of a child fostered under an earlier compact.", "Return the child to the birth household.", "Uphold the fostering compact.");
            Add(items, "serious-private-letters", "Letters Read Aloud", ReignNobleMatterSeverity.Serious, ReignNobleMatterCategory.Scandal,
                "Private letters were obtained and recited at court.", "Censure the reader and suppress the letters.", "Admit the letters as relevant testimony.", false, false, false, new[] { "dishonorable" }, negativeTagsApplyOnSideB: false);

            Add(items, "grave-divorce-refused", "A Marriage Beyond Repair", ReignNobleMatterSeverity.Grave, ReignNobleMatterCategory.Divorce,
                "One spouse petitions for divorce while the other refuses.", "Grant an immediate divorce.", "Deny the divorce and preserve the marriage.", false, false, false, new[] { "divorcee" });
            Add(items, "grave-adultery-accusation", "The Adultery Accusation", ReignNobleMatterSeverity.Grave, ReignNobleMatterCategory.Scandal,
                "A spouse accuses their partner and an alleged lover of adultery.", "Find the accused pair responsible.", "Reject the accusation as unproven or false.", false, false, false, new[] { "disloyal", "promiscuous" }, false, true, 3, negativeTagsApplyOnSideB: false, applyNegativeTagsToAllRejectedParticipants: true);
            Add(items, "grave-illegitimate-heir", "The Child's Paternity", ReignNobleMatterSeverity.Grave, ReignNobleMatterCategory.Dynastic,
                "A noble child's paternity is challenged, threatening inheritance and alliance.", "Uphold the acknowledged parentage.", "Recognize the challenge and disinherit the claim.", false, false, true, new[] { "the_unchaste" }, false, true, 3, negativeTagsApplyOnSideA: false);
            Add(items, "grave-corruption", "Coin Beneath the Table", ReignNobleMatterSeverity.Grave, ReignNobleMatterCategory.Scandal,
                "A noble official is accused of taking payments to bend royal business.", "Convict and censure the official.", "Reject the accusation and censure the accuser.", false, false, false, new[] { "corrupt" }, false, true, negativeTagsApplyOnSideB: false);
            Add(items, "grave-cowardice", "Flight from the Field", ReignNobleMatterSeverity.Grave, ReignNobleMatterCategory.Military,
                "A commander is accused of abandoning allies during battle.", "Find the commander guilty of cowardice.", "Accept the retreat as necessary.", false, false, false, new[] { "coward" }, false, true, negativeTagsApplyOnSideB: false);
            Add(items, "grave-stolen-relief", "Relief Stores Diverted", ReignNobleMatterSeverity.Grave, ReignNobleMatterCategory.Crime,
                "Food meant for suffering tenants vanished under noble supervision.", "Hold the supervising noble responsible.", "Accept evidence that another party caused the loss.", false, false, false, new[] { "corrupt", "cruel" }, false, true, negativeTagsApplyOnSideB: false);
            Add(items, "grave-forged-deed", "The Forged Charter", ReignNobleMatterSeverity.Grave, ReignNobleMatterCategory.Property,
                "Two houses present incompatible deeds to valuable property.", "Recognize the first deed and condemn the second.", "Recognize the second deed and condemn the first.", false, false, false, new[] { "dishonorable" }, false, true);
            Add(items, "grave-false-accusation", "A Calculated Falsehood", ReignNobleMatterSeverity.Grave, ReignNobleMatterCategory.Scandal,
                "One noble claims another invented a grave accusation to destroy their standing.", "Condemn the original accuser as a liar.", "Uphold the original accusation.", false, false, false, new[] { "dishonorable" }, false, true);
            Add(items, "grave-hostage-treatment", "A Hostage Mistreated", ReignNobleMatterSeverity.Grave, ReignNobleMatterCategory.Crime,
                "A noble hostage alleges unlawful cruelty while held by another house.", "Condemn the captor and order redress.", "Reject the hostage's account.", false, false, false, new[] { "cruel" }, false, true, negativeTagsApplyOnSideB: false);
            Add(items, "grave-deserted-garrison", "The Abandoned Garrison", ReignNobleMatterSeverity.Grave, ReignNobleMatterCategory.Military,
                "A castellan and relieving lord blame each other for a fortress left undefended.", "Hold the castellan responsible.", "Hold the relieving lord responsible.", false, false, false, new[] { "coward", "incompetent" }, false, true);
            Add(items, "grave-blood-feud", "Blood Price Refused", ReignNobleMatterSeverity.Grave, ReignNobleMatterCategory.Family,
                "Two houses teeter on renewed violence after one rejects an offered blood price.", "Enforce the offered settlement.", "Permit the aggrieved house to reject it.");
            Add(items, "grave-heir-disinheritance", "A Disinherited Heir", ReignNobleMatterSeverity.Grave, ReignNobleMatterCategory.Dynastic,
                "A clan leader asks the ruler to ratify an heir's disinheritance.", "Ratify the clan leader's decision.", "Protect the heir's standing.", false, false, true);
            Add(items, "grave-forced-betrothal", "The Purchased Betrothal", ReignNobleMatterSeverity.Grave, ReignNobleMatterCategory.Marriage,
                "Two clan leaders demand enforcement of a paid betrothal resisted by one adult child.", "Compel the valid marriage.", "Break the contract and protect the unwilling adult.", false, true, true);
            Add(items, "grave-secret-marriage", "A Marriage Kept Secret", ReignNobleMatterSeverity.Grave, ReignNobleMatterCategory.Marriage,
                "Two nobles claim they privately married, while a clan leader denies its legitimacy.", "Recognize and solemnize the union if technically valid.", "Reject the claimed marriage.", true, true, true, new[] { "dishonorable" }, negativeTagsApplyOnSideA: false, applyNegativeTagsToAllRejectedParticipants: true);
            Add(items, "grave-embezzled-ransom", "The Missing Ransom", ReignNobleMatterSeverity.Grave, ReignNobleMatterCategory.Crime,
                "Coin raised for prisoners disappeared between two noble custodians.", "Hold the first custodian responsible.", "Hold the second custodian responsible.", false, false, false, new[] { "corrupt" }, false, true);
            Add(items, "grave-raid-civilians", "The Burned Hamlet", ReignNobleMatterSeverity.Grave, ReignNobleMatterCategory.Military,
                "A lord is accused of burning allied homes during a punitive raid.", "Condemn the raiding lord.", "Accept the action as unavoidable warfare.", false, false, false, new[] { "cruel" }, true, true, negativeTagsApplyOnSideB: false);
            Add(items, "grave-treasonous-correspondence", "Letters to the Enemy", ReignNobleMatterSeverity.Grave, ReignNobleMatterCategory.Crime,
                "Intercepted letters appear to show a lord bargaining with an enemy.", "Declare the correspondence disloyal.", "Accept the explanation that it served the realm.", false, false, false, new[] { "disloyal", "traitor" }, false, true, negativeTagsApplyOnSideB: false);
            Add(items, "grave-succession-oath", "The Broken Succession Oath", ReignNobleMatterSeverity.Grave, ReignNobleMatterCategory.Dynastic,
                "A lord is accused of repudiating a sworn succession compact.", "Enforce the oath and censure the lord.", "Release the lord from the disputed oath.", false, false, true, new[] { "disloyal" }, negativeTagsApplyOnSideB: false);
            Add(items, "exceptional-murder", "News of Murder", ReignNobleMatterSeverity.Exceptional, ReignNobleMatterCategory.Crime,
                "A protected noble has just been murdered. The first reports name several nobles whose testimony and evidence must be tested before judgment.", "Convict the noble the evidence proves guilty.", "Acquit all or defer the investigation.", false, false, false, new[] { "murderer", "convicted_murderer" }, true, true, 4, true);
            Add(items, "exceptional-regicide-plot", "A Plot Against the Crown", ReignNobleMatterSeverity.Exceptional, ReignNobleMatterCategory.Crime,
                "Evidence suggests nobles discussed killing or replacing their ruler.", "Convict the principal conspirator.", "Reject the evidence or identify another culprit.", false, false, true, new[] { "traitor", "murderous" }, false, true, 4, negativeTagsApplyOnSideB: false);
            Add(items, "exceptional-treason", "The Enemy's Bargain", ReignNobleMatterSeverity.Exceptional, ReignNobleMatterCategory.Crime,
                "A clan leader is accused of bargaining away the realm's security.", "Declare the accused a traitor.", "Accept the bargain as sanctioned statecraft.", false, false, true, new[] { "traitor", "disloyal" }, false, true, 3, negativeTagsApplyOnSideB: false);
            Add(items, "exceptional-dynastic-rival", "Two Heirs, One Legacy", ReignNobleMatterSeverity.Exceptional, ReignNobleMatterCategory.Dynastic,
                "Rival branches demand the ruler recognize their preferred successor.", "Recognize the first claimant's precedence.", "Recognize the second claimant's precedence.", false, false, true, new string[0], false, true, 4);
            Add(items, "exceptional-clan-schism", "A Clan Divided", ReignNobleMatterSeverity.Exceptional, ReignNobleMatterCategory.Dynastic,
                "A great clan's leader and senior relatives ask the ruler to decide a schism.", "Confirm the existing leader's authority.", "Support the dissident branch's demands.", false, false, true, new[] { "disloyal" }, false, true, 4, negativeTagsApplyOnSideB: false, applyNegativeTagsToAllRejectedParticipants: true);
            Add(items, "exceptional-grand-corruption", "The Realm's Coin", ReignNobleMatterSeverity.Exceptional, ReignNobleMatterCategory.Scandal,
                "Several nobles accuse one another of diverting resources meant for the realm.", "Convict the principal accused.", "Find the counter-accusation more credible.", false, false, true, new[] { "corrupt" }, false, true, 4);
            Add(items, "exceptional-mass-starvation", "The Granaries Were Full", ReignNobleMatterSeverity.Exceptional, ReignNobleMatterCategory.Crime,
                "A lord is accused of withholding tracked food while subjects starved.", "Condemn the lord responsible for withholding relief.", "Accept that the loss had another cause.", false, false, true, new[] { "cruel", "corrupt" }, true, true, 3, negativeTagsApplyOnSideB: false);
            Add(items, "exceptional-battle-betrayal", "Betrayal in Battle", ReignNobleMatterSeverity.Exceptional, ReignNobleMatterCategory.Military,
                "A catastrophic tracked battle loss is blamed on deliberate noble betrayal.", "Condemn the accused commander.", "Accept a rival explanation for the defeat.", false, false, true, new[] { "traitor", "coward" }, false, true, 4, negativeTagsApplyOnSideB: false);
            Add(items, "exceptional-lethal-duel", "A Duel to the Death", ReignNobleMatterSeverity.Exceptional, ReignNobleMatterCategory.Family,
                "Two houses demand permission for a lethal duel to settle an entrenched blood feud.", "Forbid the duel and impose settlement.", "Authorize only a staged, non-executable judgment in this release.", false, false, true, new[] { "murderous" }, false, true, negativeTagsApplyOnSideB: false);
            Add(items, "exceptional-land-seizure", "Forfeiture of a Great Holding", ReignNobleMatterSeverity.Exceptional, ReignNobleMatterCategory.Property,
                "A house demands permanent seizure of another's major holding.", "Reject seizure and impose a revocable censure or claim ruling.", "Record only a staged, non-executable seizure judgment in this release.", false, false, true, new[] { "traitor", "corrupt" }, false, true, negativeTagsApplyOnSideB: false);

            if (items.Count != 68) throw new InvalidOperationException("The noble docket catalog must contain exactly 68 templates.");
            return items.AsReadOnly();
        }

        private static void Add(List<ReignNobleMatterTemplate> items, string id, string title,
            ReignNobleMatterSeverity severity, ReignNobleMatterCategory category, string premise,
            string demandA, string demandB, bool requiresLovers = false, bool requiresUnmarried = false,
            bool requiresClanLeader = false, string[]? tags = null, bool createsState = false,
            bool hiddenTruth = false, int principals = 2, bool murder = false,
            bool negativeTagsApplyOnSideA = true, bool negativeTagsApplyOnSideB = true,
            bool applyNegativeTagsToAllRejectedParticipants = false)
        {
            items.Add(new ReignNobleMatterTemplate
            {
                Id = id,
                Title = title,
                Severity = severity,
                Category = category,
                Premise = premise,
                DemandA = demandA,
                DemandB = demandB,
                MinimumPrincipals = principals,
                MaximumPrincipals = principals,
                RequiresSpouses = category == ReignNobleMatterCategory.Divorce,
                RequiresUnmarriedPair = requiresUnmarried,
                RequiresLoverAffinity = requiresLovers,
                RequiresClanLeader = requiresClanLeader,
                CreatesStateOnActivation = createsState,
                UsesHiddenTruth = hiddenTruth,
                IsMurderInvestigation = murder,
                ApplicableNegativeTags = tags ?? Array.Empty<string>(),
                NegativeTagsApplyOnSideA = negativeTagsApplyOnSideA,
                NegativeTagsApplyOnSideB = negativeTagsApplyOnSideB,
                ApplyNegativeTagsToAllRejectedParticipants = applyNegativeTagsToAllRejectedParticipants
            });
        }
    }
}
