using System.Collections.Generic;

namespace ReignBeta.Shared.Characters
{
    public sealed class TavernHouseCastCatalog
    {
        public int Version { get; set; } = 1;
        public List<TavernHouseCastTown> Towns { get; set; } = new List<TavernHouseCastTown>();
    }

    public sealed class TavernHouseCastTown
    {
        public string TownId { get; set; } = "";
        public string TownName { get; set; } = "";
        public string CultureId { get; set; } = "";
        public List<TavernHouseCastPerson> People { get; set; } = new List<TavernHouseCastPerson>();
    }

    public sealed class TavernHouseCastPerson
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Female { get; set; }
        public bool Madam { get; set; }
        public int Age { get; set; }
        public int Charm { get; set; }
        public int Roguery { get; set; }
        public int Honor { get; set; }
        public int Calculating { get; set; }
        public int Mercy { get; set; }
        public int Generosity { get; set; }
        public int Valor { get; set; }
        public string AppearanceSeed { get; set; } = "";
        public string BodyKey { get; set; } = "";
        public double BodyWeight { get; set; } = .5;
        public double BodyBuild { get; set; } = .5;
        public string NativeAppearanceBasisId { get; set; } = "";
        public List<TavernHouseEquipmentPart> CivilianEquipment { get; set; } = new List<TavernHouseEquipmentPart>();
        public string Biography { get; set; } = "";
        public string Personality { get; set; } = "";
        public string CivilianStyle { get; set; } = "";
    }

    public sealed class TavernHouseEquipmentPart
    {
        public string Slot { get; set; } = "";
        public string ItemId { get; set; } = "";
    }

    public sealed class TavernHouseVisitCharge
    {
        public string HeroId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int Gold { get; set; }
    }

    public sealed class TavernHouseVisitQuote
    {
        public string AgreementId { get; set; } = "";
        public string TownId { get; set; } = "";
        public string CampaignId { get; set; } = "";
        public string TimelineId { get; set; } = "main";
        public string PlayerHeroId { get; set; } = "";
        public List<TavernHouseVisitCharge> Charges { get; set; } = new List<TavernHouseVisitCharge>();
        public int TotalGold { get; set; }
        public int QuoteRevision { get; set; }
    }

    public sealed class TavernHouseVisitReceipt
    {
        public string VisitId { get; set; } = "";
        public string AgreementId { get; set; } = "";
        public string TownId { get; set; } = "";
        public string CampaignId { get; set; } = "";
        public string TimelineId { get; set; } = "main";
        public string PlayerHeroId { get; set; } = "";
        public List<TavernHouseVisitCharge> Charges { get; set; } = new List<TavernHouseVisitCharge>();
        public List<string> ParticipantHeroIds { get; set; } = new List<string>();
        public int QuoteRevision { get; set; }
        public int PaidGold { get; set; }
        public double StartedDay { get; set; }
        public bool Paid { get; set; }
        public bool ServerConfirmed { get; set; }
        public string ConversationId { get; set; } = "";
        public bool Ended { get; set; }
    }

    public sealed class TavernHousePerson
    {
        public string Id { get; set; } = "";
        public string CastId { get; set; } = "";
        public string HeroId { get; set; } = "";
        public string TownId { get; set; } = "";
        public int Slot { get; set; }
        public int Generation { get; set; }
        public string CultureId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Body { get; set; } = "";
        public List<TavernHouseEquipmentPart> CivilianEquipment { get; set; } = new List<TavernHouseEquipmentPart>();
        public string Biography { get; set; } = "";
        public string Personality { get; set; } = "";
        public bool Female { get; set; }
        public bool Madam { get; set; }
        public bool Initialized { get; set; }
        public bool Retired { get; set; }
        public bool Recruited { get; set; }
        public bool PortraitComplete { get; set; }
        public double CreatedDay { get; set; }
        public double LeftDay { get; set; }
        public string DepartureReason { get; set; } = "";
        public string RecruitmentReceipt { get; set; } = "";
        public int RecruitmentGold { get; set; }
        public bool RecruitmentGoldPaid { get; set; }
        public bool RecruitmentComplete { get; set; }
    }

    public sealed class TavernHouseSlot
    {
        public string TownId { get; set; } = "";
        public int Index { get; set; }
        public bool Female { get; set; }
        public string PersonId { get; set; } = "";
        public int Generation { get; set; }
        public double ReplacementDay { get; set; }
    }

    public sealed class TavernHouseState
    {
        public int Version { get; set; } = 1;
        public List<string> InitializedTownIds { get; set; } = new List<string>();
        public List<TavernHouseSlot> Slots { get; set; } = new List<TavernHouseSlot>();
        public List<TavernHousePerson> People { get; set; } = new List<TavernHousePerson>();
        public List<TavernHouseVisitReceipt> Visits { get; set; } = new List<TavernHouseVisitReceipt>();
    }
}
