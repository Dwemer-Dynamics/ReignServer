using System;

namespace Reign.Core.Contracts.Actions
{
    // The native client supplies freshly resolved identities at preview and execution.
    // No role, consent, or authority fact comes from a model or editable action argument.
    public sealed class ActionAuthorityFacts
    {
        public string ActorId { get; set; } = "";
        public string CounterpartyId { get; set; } = "";
        public string SelectedTargetHeroId { get; set; } = "";
        public string ActingRulerId { get; set; } = "";
        public string DestinationRulerId { get; set; } = "";
        public string RebelRulerId { get; set; } = "";
        public string AffectedClanLeaderId { get; set; } = "";
        public string ReceivingClanLeaderId { get; set; } = "";
        public string ActorActualClanLeaderId { get; set; } = "";
        public string CounterpartyActualClanLeaderId { get; set; } = "";
        public string PlayerClanLeaderId { get; set; } = "";
        public string PlayerCurrentRulerId { get; set; } = "";
    }

    public static class ReignActionAuthority
    {
        public static string Validate(string nativeAction, ActionAuthorityFacts f)
        {
            bool Actor(string required) => required.Length > 0 && required == f.ActorId;
            bool Counterparty(string required) => required.Length > 0 && required == f.CounterpartyId;
            bool Participates(string required) => Actor(required) || Counterparty(required);
            if (f.ActorId.Length == 0 || f.CounterpartyId.Length == 0 || f.ActorId == f.CounterpartyId)
                return "Two distinct living agreement participants are required.";
            if (nativeAction.StartsWith("Diplomacy", StringComparison.Ordinal))
            {
                if (!Actor(f.ActingRulerId)) return "Only the current ruler can commit the acting kingdom.";
                if (nativeAction == "DiplomacyBackRebellion") return Counterparty(f.RebelRulerId) ? "" : "The current sponsoring ruler and the active rebel ruler must participate.";
                if (nativeAction != "DiplomacyDeclareWar" && nativeAction != "DiplomacyBreakTreaty" && nativeAction != "DiplomacyRecordPromise"
                    && !Counterparty(f.DestinationRulerId)) return "This settlement or treaty requires the current rulers of both named kingdoms.";
                return "";
            }
            switch (nativeAction)
            {
                case "PoliticsResolveCivilWar":
                    return Actor(f.ActingRulerId) && Counterparty(f.DestinationRulerId) ? "" : "The current rebel and parent rulers must both approve independent peace.";
                case "RegularJoinKingdom": case "RegularHireMercenaryClan":
                    return Actor(f.AffectedClanLeaderId) && Counterparty(f.DestinationRulerId) ? ""
                        : "The joining clan leader and the receiving kingdom's current ruler must agree.";
                case "RegularLeaveKingdom":
                    return Actor(f.AffectedClanLeaderId) ? "" : "Only that clan's current leader can leave its kingdom.";
                case "RegularHirePlayerAsMercenary": case "RegularOfferPlayerVassalage":
                    return Participates(f.PlayerClanLeaderId) && Participates(f.DestinationRulerId) ? ""
                        : "The player clan leader and the receiving kingdom's current ruler must agree.";
                case "RegularDismissPlayerMercenary": case "RegularDismissPlayerVassal":
                    return Participates(f.PlayerClanLeaderId) && Participates(f.PlayerCurrentRulerId) ? ""
                        : "The player clan leader and their current ruler must agree to this dismissal.";
                case "RegularJoinClan":
                    return Counterparty(f.ReceivingClanLeaderId) ? "" : "The receiving clan's current leader must accept the joining character.";
                case "PoliticsExileClan":
                    return Actor(f.ActingRulerId) ? "" : "Only the current ruler can propose exiling a clan through the government process.";
                case "PoliticsEncourageClanDefection": case "PoliticsRestoreExiledClan": case "PoliticsInstallRulingClan":
                    return Participates(f.ActingRulerId) && Participates(f.AffectedClanLeaderId) ? ""
                        : "The current ruler and the affected clan's current leader must both participate.";
                case "PoliticsMediateClanDispute":
                    return Participates(f.AffectedClanLeaderId) && Participates(f.ReceivingClanLeaderId) ? ""
                        : "Both affected clan leaders must participate in this settlement.";
                case "PoliticsRecruitLordToRebellion": case "PoliticsJoinRebellion":
                case "PoliticsResolveRebellionPledge": case "PoliticsResolveRebellionSummons":
                    return f.SelectedTargetHeroId == f.CounterpartyId && Actor(f.ActorActualClanLeaderId) && Counterparty(f.CounterpartyActualClanLeaderId) ? ""
                        : "A rebellion agreement can commit only its participating clan leaders.";
                default: return ""; // Personal actions retain their adapter's native eligibility rules.
            }
        }
    }
}
