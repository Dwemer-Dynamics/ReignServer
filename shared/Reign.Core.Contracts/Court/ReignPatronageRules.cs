using System;
using System.Collections.Generic;
using System.Linq;

namespace Reign.Core.Contracts.Court
{
    public enum ReignPatronageObjective { Praise = 0, Loyalty = 1, PersonalWork = 2 }
    public enum ReignPatronageReach { Court = 0, Settlement = 1, Regional = 2, Kingdom = 3 }

    public sealed class ReignPatronageTemplate
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string CreatorRole { get; set; } = string.Empty;
        public string Premise { get; set; } = string.Empty;
        public string RequiredContext { get; set; } = string.Empty;
        public bool SupportsPublicPerformance { get; set; }
        public bool RequiresSpouse { get; set; }
        public bool RequiresChild { get; set; }
        public bool RequiresHistoricalSubject { get; set; }
    }

    public static class ReignPatronageRules
    {
        public const int PraiseRelation = 3;
        public const int LoyaltyIncrease = 5;
        public static bool HasEligiblePraiseAudience(int audienceCount) => audienceCount > 0;
        public static string PublicSummary(ReignPatronageTemplate template)
        {
            if (template == null) return "An artist seeks a commission from the crown.";
            return "The " + template.CreatorRole + " seeks patronage for \"" + template.Title
                + "\". The subject, treatment, and audience are yours to discuss.";
        }

        public static string AgreedWorkDescription(string title, string playerWords, string creatorWords)
        {
            // Preserve attributed evidence rather than asking another model to invent a finished work.
            if (string.IsNullOrWhiteSpace(playerWords) || string.IsNullOrWhiteSpace(creatorWords))
                return "Commissioned work: " + title + ". The audience transcript records any surviving discussion.";
            return "Agreed commission: " + title + "\n\nRuler: " + playerWords.Trim()
                + "\n\nCreator: " + creatorWords.Trim();
        }

        public static int Cost(ReignPatronageReach reach)
        {
            switch (reach) { case ReignPatronageReach.Court: return 500; case ReignPatronageReach.Settlement: return 2000;
                case ReignPatronageReach.Regional: return 5000; case ReignPatronageReach.Kingdom: return 8000;
                default: throw new ArgumentOutOfRangeException(nameof(reach)); }
        }
        public static int DeliveryDays(ReignPatronageReach reach, ReignPatronageObjective objective)
        {
            if (objective == ReignPatronageObjective.PersonalWork) return 1;
            switch (reach) { case ReignPatronageReach.Court: return 0; case ReignPatronageReach.Settlement: return 1;
                case ReignPatronageReach.Regional: return 3; case ReignPatronageReach.Kingdom: return 7;
                default: throw new ArgumentOutOfRangeException(nameof(reach)); }
        }
        public static bool Supports(ReignPatronageTemplate template, ReignPatronageObjective objective,
            ReignPatronageReach reach, int eligibleSettlements)
        {
            if (template == null || !Enum.IsDefined(typeof(ReignPatronageObjective), objective)
                || !Enum.IsDefined(typeof(ReignPatronageReach), reach)) return false;
            if (objective == ReignPatronageObjective.PersonalWork) return reach == ReignPatronageReach.Court;
            if (!template.SupportsPublicPerformance) return false;
            if (objective == ReignPatronageObjective.Loyalty && reach == ReignPatronageReach.Court) return false;
            if (reach == ReignPatronageReach.Regional) return eligibleSettlements >= 3;
            return reach == ReignPatronageReach.Court || eligibleSettlements > 0;
        }
        public static float ApplyLoyalty(float current) => Math.Max(0f, Math.Min(100f, current + LoyaltyIncrease));
        public static string PublicNativeHistoryContext(string eventType, bool ownWinner, bool ownReleasedPrisoner,
            bool ownVictim, bool ownNewOwner)
        {
            switch (eventType)
            {
                case "battle_completed": return ownWinner ? "victory" : string.Empty;
                case "hero_prisoner_released": return ownReleasedPrisoner ? "release" : string.Empty;
                case "hero_killed": return ownVictim ? "death" : string.Empty;
                case "settlement_owner_changed": return ownNewOwner ? "settlement" : string.Empty;
                case "peace_made": return "reconciliation";
                default: return string.Empty;
            }
        }
    }

    public static class ReignPatronageCatalog
    {
        public static IReadOnlyList<ReignPatronageTemplate> Templates { get; } = Build();
        public static ReignPatronageTemplate? Find(string id) => Templates.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        private static IReadOnlyList<ReignPatronageTemplate> Build()
        {
            var items = new List<ReignPatronageTemplate>();
            Add(items,"victory-ballad","A Victory Worth Remembering","balladeer","Offer to honor a verified victory; negotiate whose contribution receives credit.",true,"victory");
            Add(items,"generous-crown","A Song of the Generous Crown","singer","Offer a song about a recorded act of aid, with a choice between personal gratitude and mutual civic obligation.",true,"aid");
            Add(items,"ruler-biography","The Ruler's Life","historian","Seek the ruler's account and propose a biography, distinguishing witnessed history from flattering interpretation.",true);
            Add(items,"roadside-storyteller","Stories for the Road","storyteller","Ask for funding to carry a suitable account of the ruler or the kingdom to a named audience.",true);
            Add(items,"captive-homecoming","The Homecoming Verse","poet","Commemorate an actual return from captivity and ask whom the work should thank.",true,"release");
            Add(items,"recovered-town","A Town's New Chapter","singer","Offer a work commemorating an actual settlement's transfer to the kingdom, honoring its people as well as its ruler without inventing a conquest or earlier ownership.",true,"settlement");
            Add(items,"peace-verse","Verses for Peace","poet","Propose a performance about the value of an existing peace without inventing a new treaty.",true);
            Add(items,"honest-chronicle","An Honest Chronicle","historian","Ask whether to portray a difficult episode honestly or emphasize the lessons learned; do not invent historical failure.",true,"history");
            Add(items,"spouse-portrait","A Portrait for a Spouse","portraitist","Offer a personal portrait and invite the ruler to choose a dedication and emotional tone.",false,"",true);
            Add(items,"spouse-poem","A Private Dedication","poet","Offer a poem for the ruler's spouse; seek genuine details rather than assuming the gift repairs a relationship.",false,"",true);
            Add(items,"child-discovery-book","A Book of Small Discoveries","illustrator","Propose an age-appropriate illustrated book based on a child's interests and questions.",false,"",false,true);
            Add(items,"coming-of-age","A Coming of Age","poet","Offer a dedication celebrating an eligible child's transition toward adult responsibilities.",false,"",false,true);
            Add(items,"family-chronicle","The Household Chronicle","scribe","Collect approved family recollections and ask which memories should be preserved.",false);
            Add(items,"memorial-work","A Work of Remembrance","sculptor","Offer to commemorate a verified deceased person, asking which true qualities the ruler values.",false,"death");
            Add(items,"reconciliation-gift","Words to Mend a Rift","poet","Offer a gift intended as an opening to reconciliation; the recipient's acceptance remains a later personal decision.",false);
            Add(items,"shared-family-story","A Story Shared at Home","storyteller","Offer a household story adapted to the children's ages, without claiming a later family gathering has already happened.",false,"",false,true);
            Add(items,"unknown-singer","An Unheard Voice","singer","A little-known performer seeks a first commission and offers a sample before requesting payment.",true);
            Add(items,"noble-protege","A Noble's Protege","musician","An artist asks the ruler to judge their work while acknowledging a real noble's introduction, without automatically granting that noble credit.",true);
            Add(items,"competing-performers","Two Ways to Tell It","poet","Offer competing treatments of the same suitable subject and ask the ruler to select the tone before commissioning.",true);
            Add(items,"apprentice-sponsorship","An Apprentice's First Work","apprentice artisan","Request a modest commission that provides practice and creates a small durable work for the court.",false);
            Add(items,"master-commission","The Master's Commission","craftsperson","Offer a clearly described decorative work; do not claim an inventory object unless a supported native item is actually delivered.",false);
            Add(items,"instrument-funding","An Instrument and a Living","musician","Ask for a commission that funds the artist's equipment and a performance, not an unimplemented item transfer.",true);
            Add(items,"visiting-translator","Words Across Kingdoms","translator","Offer to adapt an approved work for another language or cultural audience while preserving its meaning.",true);
            Add(items,"patron-dedication","A Patron Named in Verse","poet","Ask permission to name the ruler as patron and negotiate how conspicuous that credit should be.",true);
            Add(items,"forgotten-commander","Credit Long Overdue","balladeer","Ask which verified commander or service record deserves recognition; invite corrections to the proposed emphasis.",true,"history");
            Add(items,"rival-house-praise","Praise for Another House","poet","Ask the ruler to name another real house and a specific contribution the ruler's records support. Until both are supplied, offer only a conditional outline and do not imply that a contribution, reconciliation, victory, service, or deed is already known.",true);
            Add(items,"loyal-retainer","The Retainer's Dedication","scribe","Offer a small work honoring a named retainer and ask the ruler for an authentic personal detail.",false);
            Add(items,"shared-achievement","Whose Name Comes First","balladeer","Ask how to divide credit for a verified shared achievement; other characters may react through ordinary social behavior.",true,"history");
            Add(items,"welcome-clan","A Place Among Us","singer","Offer a welcoming performance for an existing member clan that emphasizes belonging and mutual duties.",true);
            Add(items,"reconciled-houses","After Reconciliation","poet","Offer to commemorate an already accepted public peace between ruling houses; do not assume private friendship, and the performance cannot itself settle a dispute.",true,"reconciliation");
            Add(items,"soldiers-dedication","Names Below the Banner","balladeer","Offer a dedication to ordinary service and ask whether to emphasize the ruler's care or shared civic duty.",true);
            Add(items,"popular-performer","The Court's Familiar Voice","musician","A recurring performer offers another work, drawing on remembered commissions without automatically gaining a favor tag.",true);
            Add(items,"excess-flattery","Praise Beyond Belief","poet","A sample overstates the ruler's virtues; allow correction, modesty, or knowingly fictional allegory without corrupting history.",true);
            Add(items,"insult-in-verse","A Barbed Couplet","satirist","A proposed line could offend a real audience; negotiate revision, keep an agreed satire, or decline.",true);
            Add(items,"foreign-artist","An Artist from Elsewhere","storyteller","A visitor offers an outsider's perspective and asks whether the work should stress hospitality, unity, or the ruler's qualities.",true);
            Add(items,"disputed-authorship","Whose Words Are These","scribe","Bring a concrete proposed work containing an unsigned passage whose attribution is uncertain. Develop the passage, its artistic purpose and the crediting dilemma in this audience; ask whether to preserve it without attribution or replace it with new words. Do not invent a named claimant, theft, witness or proof. Keep any supplied allegations uncertain and agree attribution before payment.",false);
            Add(items,"unwanted-dedication","A Dedication Too Personal","poet","Propose a conspicuous dedication and let the ruler redirect or decline it; do not assume romance.",true);
            Add(items,"smaller-counteroffer","A Modest Purse","musician","Offer the standard reaches and accept a supported smaller commission when the ruler will not fund the original scope.",true);
            Add(items,"change-subject","A Better Subject","storyteller","Invite the ruler to change an unsuitable proposed subject before choosing an affordable supported commission.",true);
            Add(items,"returning-artist","The Next Commission","musician","A recurring creator refers only to actually completed work and offers a new commission; prior delivery needs no new audience slot.",true);
            return items.AsReadOnly();
        }
        private static void Add(List<ReignPatronageTemplate> list, string id, string title, string role,
            string premise, bool performance, string context = "", bool spouse = false, bool child = false)
        {
            list.Add(new ReignPatronageTemplate { Id = "patronage-" + id, Title = title, CreatorRole = role,
                Premise = premise, SupportsPublicPerformance = performance, RequiredContext = context,
                RequiresHistoricalSubject = context.Length > 0, RequiresSpouse = spouse, RequiresChild = child });
        }
    }
}
