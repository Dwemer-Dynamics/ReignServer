using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Reign.Core.Contracts.Actions
{
    public static partial class ReignActionCatalog
    {
        public const int Version = 1;
        private static readonly ActionDefinition[] Definitions = Build();
        public static IReadOnlyList<ActionDefinition> All => Array.AsReadOnly(Definitions);
        public static ActionDefinition? Find(string id) => Definitions.FirstOrDefault(x => x.Id == id);
        // Every native world-action enum value must be either exposed by a definition
        // or explicitly classified here. Coverage tests fail when that boundary drifts.
        public static IReadOnlyDictionary<string, string> WorldActionExclusions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Unknown"] = "Sentinel; never executable.",
            ["DiplomacySignTemporaryTruce"] = "Retired: native validator rejects its unreliable game effect. Use a defined non-aggression agreement.",
            ["DiplomacyTradeEmbargo"] = "Retired: native validator rejects its absent meaningful game effect.",
            ["RegularFollowInScene"] = "Compatibility alias of follow; the persistent service spans scene and campaign contexts."
        };
        public static string ObjectiveFor(ActionDefinition definition, bool targetIsVillage) =>
            definition.Id == "attack_settlement" && targetIsVillage ? "raid" : definition.NativeObjective;

        private static ActionDefinition[] Build()
        {
            var actions = new List<ActionDefinition>
            {
            Legacy("declare_war", "diplomacy", "DiplomacyDeclareWar", "actorKingdomId,targetKingdomId,reason", "Actor kingdom declares war on target kingdom."),
            Legacy("back_rebellion", "diplomacy", "DiplomacyBackRebellion", "actorKingdomId,targetKingdomId,terms.rebelKingdomId,terms.movementId", "The sponsoring ruler backs the participating rebel ruler's active civil war and declares war on the parent realm."),
            Legacy("resolve_civil_war", "politics", "PoliticsResolveCivilWar", "actorKingdomId,targetKingdomId,terms.movementId", "The participating rebel and parent rulers recognize independent sovereignty and settle their active civil war. The acting side is the rebel realm."),
            Legacy("make_peace", "diplomacy", "DiplomacyMakePeace", "actorKingdomId,targetKingdomId,reason", "Actor kingdom makes peace with target kingdom."),
            Legacy("offer_tribute_peace", "diplomacy", "DiplomacyOfferTributePeace", "actorKingdomId,targetKingdomId,reason,terms.dailyTribute,terms.durationDays", "Actor kingdom makes peace with tribute terms."),
            Legacy("record_promise", "diplomacy", "DiplomacyRecordPromise", "actorKingdomId,targetKingdomId,reason", "Records a diplomatic promise or obligation for future memory."),
            Legacy("demand_reparations_peace", "diplomacy", "DiplomacyDemandReparationsPeace", "actorKingdomId,targetKingdomId,reason,terms.reparationsGold,terms.dailyTribute,terms.durationDays", "Target kingdom pays gold and/or daily tribute to receive peace."),
            Legacy("demand_settlement_peace", "diplomacy", "DiplomacyDemandSettlementPeace", "actorKingdomId,targetKingdomId,targetSettlementId or TargetSettlement,reason,terms.settlementIds", "Target kingdom surrenders one or more towns/castles in exchange for peace."),
            Legacy("demand_surrender_peace", "diplomacy", "DiplomacyDemandSurrenderPeace", "actorKingdomId,targetKingdomId,reason,terms.allTargetFortifications or TargetSettlement or terms.settlementIds,terms.reparationsGold,terms.dailyTribute", "Target kingdom accepts capitulation: all or selected towns/castles, gold, tribute, peace, and possible kingdom destruction when landless."),
            Legacy("sign_trade_agreement", "diplomacy", "DiplomacySignTradeAgreement", "actorKingdomId,targetKingdomId,reason,terms.durationDays", "Kingdoms sign a native trade agreement plus a Bannerlord Reign treaty record."),
            Legacy("sign_non_aggression_pact", "diplomacy", "DiplomacySignNonAggressionPact", "actorKingdomId,targetKingdomId,reason,terms.durationDays", "Kingdoms promise not to declare war, raid, or support hostile actions for a duration."),
            Legacy("sign_alliance", "diplomacy", "DiplomacySignAlliance", "actorKingdomId,targetKingdomId,reason,terms.durationDays", "Kingdoms sign a native alliance plus a Bannerlord Reign treaty record."),
            Legacy("sign_defensive_pact", "diplomacy", "DiplomacySignDefensivePact", "actorKingdomId,targetKingdomId,reason,terms.durationDays", "Kingdoms promise defensive support without a full offensive alliance."),
            Legacy("break_treaty", "diplomacy", "DiplomacyBreakTreaty", "actorKingdomId,targetKingdomId,reason", "Ends one or more treaty records, and native trade/alliance records when applicable."),
            Legacy("exchange_prisoners", "diplomacy", "DiplomacyExchangePrisoners", "actorKingdomId,targetKingdomId,reason", "Both kingdoms release held noble prisoners up to the requested limit."),
            Legacy("ransom_package", "diplomacy", "DiplomacyRansomPackage", "actorKingdomId,targetKingdomId,reason,terms.gold", "Target kingdom pays for release of nobles held by actor kingdom."),
            Legacy("hostage_guarantee", "diplomacy", "DiplomacyHostageGuarantee", "actorKingdomId,targetKingdomId,reason", "Records a hostage-style guarantee or security obligation."),
            Legacy("war_indemnity", "diplomacy", "DiplomacyWarIndemnity", "actorKingdomId,targetKingdomId,reason,terms.gold", "Target kingdom pays gold to actor kingdom as war indemnity."),
            Legacy("recognize_conquest", "diplomacy", "DiplomacyRecognizeConquest", "actorKingdomId,targetKingdomId,reason", "One kingdom formally recognizes another kingdom's conquest or claim."),
            Legacy("return_occupied_settlement", "diplomacy", "DiplomacyReturnOccupiedSettlement", "actorKingdomId,targetKingdomId,targetSettlementId or TargetSettlement,reason", "Actor kingdom returns a currently held town or castle to target kingdom."),
            Legacy("demilitarized_border", "diplomacy", "DiplomacyDemilitarizedBorder", "actorKingdomId,targetKingdomId,reason,terms.durationDays", "Kingdoms record a temporary no-raid/no-siege border restriction."),
            Legacy("caravan_protection_agreement", "diplomacy", "DiplomacyCaravanProtectionAgreement", "actorKingdomId,targetKingdomId,reason,terms.durationDays", "Kingdoms formally protect one another's caravans for a fixed duration."),
            Legacy("supply_agreement", "diplomacy", "DiplomacySupplyAgreement", "actorKingdomId,targetKingdomId,reason,terms.asset,terms.amount", "Actor kingdom records supplies, grain, horses, troops, ships/naval assets, or support owed to target kingdom."),
            Legacy("loan_or_subsidy", "diplomacy", "DiplomacyLoanOrSubsidy", "actorKingdomId,targetKingdomId,reason,terms.gold", "Actor kingdom transfers gold and records a loan, debt, or subsidy."),
            Legacy("pay_to_stay_neutral", "diplomacy", "DiplomacyPayToStayNeutral", "actorKingdomId,targetKingdomId,reason,terms.gold,terms.durationDays", "Actor kingdom pays target kingdom to stay out of a war or dispute."),
            Legacy("pay_to_join_war", "diplomacy", "DiplomacyPayToJoinWar", "actorKingdomId,targetKingdomId,reason,terms.enemyKingdomId,terms.gold", "Actor kingdom pays target kingdom to declare war on a third kingdom."),
            Legacy("guarantee_independence", "diplomacy", "DiplomacyGuaranteeIndependence", "actorKingdomId,targetKingdomId,reason,terms.durationDays", "Actor kingdom promises to defend target kingdom's independence."),
            Legacy("protectorate_or_vassalage", "diplomacy", "DiplomacyProtectorateOrVassalage", "actorKingdomId,targetKingdomId,reason,terms.durationDays", "Records a protectorate, overlord, or vassal-style diplomatic arrangement."),
            Legacy("diplomatic_package", "diplomacy", "DiplomacyPackage", "actorKingdomId,targetKingdomId,reason,terms.gold,terms.item,terms.amount,terms.prisonerHero,terms.settlementIds", "Records and validates a compound bargain such as gold for a prisoner, settlement for peace, prisoners for peace, or resources for alliance."),
            Legacy("trade_package", "regular", "RegularTradePackage", "FromHero,ToHero,GoldAmount,Item,Amount,PrisonerHero,TargetSettlement,Workshop,terms.assetClass,terms.shipCount,reason", "Appraises, validates, and executes an atomic barter package: gold, items, equipped gear, prisoners, workshops, settlements, ships, tribute-like gold, and mixed-value exchanges."),
            Legacy("follow", "regular", "RegularFollowOnMap", "actorHeroId,TargetHero or targetPartyId,reason", "Follow the resolved target in the current scene when both are present; otherwise follow or escort the resolved party on the campaign map."),
            Legacy("stop_following", "regular", "RegularStopFollowing", "actorHeroId,reason", "Stop the actor's current scene follow or campaign-map escort behavior."),
            Legacy("go_to_settlement", "regular", "RegularGoToSettlement", "actorHeroId,targetSettlementId or TargetSettlement,reason", "Order a hero party to travel to a named settlement."),
            Legacy("patrol_around_settlement", "regular", "RegularPatrolAroundSettlement", "actorHeroId,targetSettlementId or TargetSettlement,reason", "Order a hero party to patrol around a named settlement."),
            Legacy("wait_near_settlement", "regular", "RegularWaitNearSettlement", "actorHeroId,targetSettlementId or TargetSettlement,reason", "Order a hero party to wait near a named settlement."),
            Legacy("raid_village", "regular", "RegularRaidVillage", "actorHeroId,targetSettlementId or TargetSettlement,reason", "Order a hero party to raid a specific village."),
            Legacy("besiege_settlement", "regular", "RegularBesiegeSettlement", "actorHeroId,targetSettlementId or TargetSettlement,reason", "Order a hero party or army leader to besiege a town or castle."),
            Legacy("create_party", "regular", "RegularCreateParty", "actorHeroId,targetSettlementId or TargetSettlement,reason", "Create a lord party for a hero who currently has none."),
            Legacy("show_the_way", "regular", "RegularShowTheWay", "actorHeroId,TargetHero or terms.destinationLocationId,reason", "Guide the player to a present hero or unique native scene passage, wait for the player and report observed arrival."),
            Legacy("attack_party", "regular", "RegularAttackParty", "actorHeroId,targetPartyId or TargetHero,reason", "Order a hero party to attack a resolved party."),
            Legacy("attack_player_party", "regular", "RegularAttackPlayerParty", "actorHeroId,reason", "Order a hero party to attack the player's party."),
            Legacy("surrender_to_player", "regular", "RegularSurrenderToPlayer", "actorHeroId,reason", "Mark a party as willing to surrender to the player."),
            Legacy("leave_player_alone", "regular", "RegularLeavePlayerAlone", "actorHeroId,reason", "Stop a party from pursuing or threatening the player."),
            Legacy("kill_character", "regular", "RegularKillCharacter", "KillerHero or actorHeroId,TargetHero,reason", "Kill or execute a resolved target hero when validation allows it."),
            Legacy("duel_player", "regular", "RegularDuelPlayer", "actorHeroId or TargetHero,reason,terms.duelMode", "Arm a one-on-one conversation duel with the current NPC."),
            Legacy("prepare_arrest", "regular", "RegularPrepareArrest", "actorHeroId,TargetHero,reason,terms.accusation,terms.chargeCategory,terms.severity", "Record a player accusation and prepare the current NPC for arrest; a second explicit confirmation is required."),
            Legacy("confirm_arrest", "regular", "RegularConfirmArrest", "actorHeroId,TargetHero,reason,terms.caseId", "Confirm a prepared arrest and place a willing/currently reachable accused in player custody."),
            Legacy("release_arrested_character", "regular", "RegularReleaseArrestedCharacter", "actorHeroId,TargetHero,reason,terms.caseId", "Release a character arrested by the player while preserving the accusation."),
            Legacy("rescind_arrest_accusation", "regular", "RegularRescindArrestAccusation", "actorHeroId,TargetHero,reason,terms.caseId", "Rescind the player's accusation, clear its reputation, and release the accused when that case is the basis for custody."),
            Legacy("player_attack_party", "regular", "RegularPlayerAttackParty", "actorHeroId,TargetHero,reason,terms.caseId", "Order the player's party to attack the current NPC party after arrest surrender is refused."),
            Legacy("issue_campaign_order", "regular", "RegularIssueCampaignOrder", "actorHeroId,reason,terms.steps[] preferred or terms.objective for one step; each step carries its own target, region, duration, recruitment goals, and armyDetachmentAccepted", "Draft, confirm, then create a durable hourly supervised multi-stage NPC party or army order. Partyless eligible lords may establish a party as the first engine-managed step; the human main party is never eligible."),
            Legacy("revise_campaign_order", "regular", "RegularReviseCampaignOrder", "reason,terms.orderId,terms.objective optional,terms.region optional,targetSettlementId optional,terms.durationHours optional", "Revise an active supervised campaign order while preserving its durable identity and evidence history."),
            Legacy("respond_to_order_report", "regular", "RegularRespondToOrderReport", "reason,terms.orderId,terms.response", "Respond to an urgent commander's report with continue, adapt, hold, withdraw, or cancel guidance."),
            Legacy("cancel_campaign_order", "regular", "RegularCancelCampaignOrder", "reason,terms.orderId", "Safely cancel a supervised campaign order and stop reasserting its native AI behavior."),
            Legacy("recruit_encountered_resident", "regular", "RegularRecruitEncounteredResident", "actorHeroId (current resident),terms.agreementKind=permanent,terms.consentConfirmed,terms.agreedGold (exact integer, zero when free)", "A known resident explicitly agrees to become a permanent player companion, preserving identity, skills and equipment. Military residents require prior authorized duty release."),
            Legacy("create_clan_accord", "regular", "RegularCreateClanAccord", "actorHeroId (current noble),TargetHero (player),terms.accordType,terms.consentConfirmed,terms.playerConfirmed", "Establish one fixed beneficial clan accord after explicit mutual acceptance; lesser nobles may arrange it for their clan. Player ruler and native eligibility required. No kingdom treaty or vote support."),
            Legacy("cancel_clan_accord", "regular", "RegularCancelClanAccord", "actorHeroId (current noble),TargetHero (player),terms.accordId,terms.playerConfirmed", "End a named active accord with the current noble's clan at the player's explicit request, removing benefits and applying the cancellation consequence."),
            Legacy("release_resident_from_duty", "regular", "RegularReleaseResidentFromDuty", "actorHeroId (current speaker),TargetHero (resident),terms.releaseConfirmed", "The resident's source commander or home-owning clan leader explicitly releases them from duty, consuming one saved source troop exactly once."),
            Legacy("accept_temporary_party_guest", "regular", "RegularAcceptTemporaryPartyGuest", "actorHeroId,reason,terms.purpose,terms.termKind=fixed|open_ended,terms.durationDays=1..126 only for fixed; preserve fractional seasons using campaignCalendar,terms.reviewIntervalDays=30 for open_ended,terms.consentConfirmed,terms.armyExitAccepted when applicable,terms.agreedGold for resident payment", "A consenting foreign noble or known resident temporarily joins the player party without changing permanent affiliations. Military residents require prior duty release."),
            Legacy("renew_temporary_party_guest", "regular", "RegularRenewTemporaryPartyGuest", "actorHeroId,reason,terms.purpose,terms.termKind=fixed|open_ended,terms.durationDays=1..126 only for fixed; preserve fractional seasons using campaignCalendar,terms.reviewIntervalDays=30 for open_ended,terms.consentConfirmed", "Renew or revise an active temporary noble guest agreement with fresh consent."),
            Legacy("end_temporary_party_guest", "regular", "RegularEndTemporaryPartyGuest", "actorHeroId,reason,terms.departureReason", "End an active temporary noble guest agreement and begin the protected return journey."),
            Legacy("acknowledge_own_faction_combat_risk", "regular", "RegularAcknowledgeOwnFactionCombatRisk", "actorHeroId,reason,terms.consentConfirmed", "Explicitly accept that participating against the guest's own faction causes native succession and banishment into the player clan."),
            Legacy("give_gold_to_player", "regular", "RegularGiveGoldToPlayer", "actorHeroId,GoldAmount,reason", "Transfer denars from an actor hero to the player."),
            Legacy("transfer_gold", "regular", "RegularTransferGold", "FromHero,ToHero,GoldAmount,reason", "Transfer denars between two resolved heroes."),
            Legacy("transfer_item", "regular", "RegularTransferItem", "FromHero,ToHero,Item,Amount,reason", "Transfer an item stack or equipped item between two resolved heroes."),
            Legacy("transfer_workshop", "regular", "RegularTransferWorkshop", "FromHero or actorHeroId,ToHero or TargetHero,Workshop,reason", "Transfer ownership of a resolved workshop."),
            Legacy("transfer_prisoner", "regular", "RegularTransferPrisoner", "FromHero or actorHeroId,ToHero or TargetHero,PrisonerHero,reason", "Release or transfer a resolved prisoner hero."),
            Legacy("hire_player_as_mercenary", "regular", "RegularHirePlayerAsMercenary", "targetKingdomId or TargetKingdom,reason", "Put the player's clan into mercenary service for a kingdom."),
            Legacy("dismiss_player_mercenary", "regular", "RegularDismissPlayerMercenary", "targetKingdomId or TargetKingdom,reason", "End the player's mercenary service."),
            Legacy("offer_player_vassalage", "regular", "RegularOfferPlayerVassalage", "targetKingdomId or TargetKingdom,reason", "Put the player's clan into vassalage under a kingdom."),
            Legacy("dismiss_player_vassal", "regular", "RegularDismissPlayerVassal", "targetKingdomId or TargetKingdom,reason", "Release the player's clan from regular vassalage."),
            Legacy("join_clan", "regular", "RegularJoinClan", "actorHeroId,targetClanId or TargetClan,reason", "Move an eligible living hero into a resolved target clan."),
            Legacy("leave_clan", "regular", "RegularLeaveClan", "actorHeroId,reason", "Remove an eligible living hero from their current clan."),
            Legacy("join_kingdom", "regular", "RegularJoinKingdom", "actorClanId or TargetClan,targetKingdomId or TargetKingdom,reason", "Move a resolved clan into a kingdom."),
            Legacy("leave_kingdom", "regular", "RegularLeaveKingdom", "actorClanId or TargetClan,reason", "Move a resolved clan out of its current kingdom."),
            Legacy("hire_mercenary_clan", "regular", "RegularHireMercenaryClan", "actorClanId or TargetClan,targetKingdomId or TargetKingdom,reason", "Put a resolved clan into mercenary service for a kingdom."),
            Legacy("recruit_and_recover", "strategy", "StrategyRecruitAndRecover", "actorHeroId,targetSettlementId or TargetSettlement,reason,minimumTroops", "Lord party recruits, gathers food, and recovers before a later order."),
            Legacy("form_army", "strategy", "StrategyFormArmy", "actorHeroId,targetSettlementId or TargetSettlement,reason,desiredStrength", "Lord attempts to form an army near a target."),
            Legacy("attack_settlement", "strategy", "StrategyAttackSettlement", "actorHeroId,targetSettlementId or TargetSettlement,reason", "Lord moves to raid or besiege a hostile settlement."),
            Legacy("capture_settlement_plan", "strategy", "StrategyCaptureSettlement", "actorHeroId,targetSettlementId or TargetSettlement,reason,minimumTroops,desiredStrength", "Multi-stage plan: validate war, recruit, form army, move, and besiege."),
            Legacy("start_ruling_clan_rebellion", "politics", "PoliticsStartRulingClanRebellion", "actorKingdomId,actorClanId,reason,terms.supporterClanIds", "A claimant clan and supporters break away in rebellion against the ruling clan."),
            Legacy("pledge_rebellion_support", "politics", "PoliticsResolveRebellionPledge", "actorKingdomId,terms.challengedRulerId", "The accepting lord pledges their clan to the player's future rebellion against the exact named ruler; allegiance changes only when that rebellion is declared."),
            Legacy("recruit_lord_to_rebellion", "politics", "PoliticsRecruitLordToRebellion", "actorHeroId,actorKingdomId,actorClanId,TargetHero,targetClanId,reason", "An accepting lord commits their clan to the player's active rebellion until it ends."),
            Legacy("join_rebellion", "politics", "PoliticsJoinRebellion", "actorHeroId,actorKingdomId,actorClanId,TargetHero,reason", "The player clan joins an accepting NPC rebel leader."),
            Legacy("surrender_rebellion", "politics", "PoliticsSurrenderRebellion", "actorHeroId,actorKingdomId,actorClanId,reason,terms.movementId optional", "A named rebel leader or challenged ruler explicitly surrenders the active rebellion."),
            Legacy("resolve_rebellion_pledge", "politics", "PoliticsResolveRebellionPledge", "actorHeroId,actorKingdomId,actorClanId,targetHeroId,targetClanId,reason,terms.decision", "Persists a lord's pledge, refusal, or report during secret rebellion preparation."),
            Legacy("resolve_rebellion_summons", "politics", "PoliticsResolveRebellionSummons", "actorHeroId,actorKingdomId,actorClanId,targetHeroId,reason,terms.playerResponse,terms.rulerVerdict", "Resolves an explicit answer and ruler judgment at a delivered rebellion summons."),
            Legacy("consent_government_reduction", "politics", "PoliticsConsentGovernmentReduction", "actorHeroId,actorKingdomId,targetHeroId,reason,terms.consentConfirmed", "Records an NPC's explicit consent to the ruler's next one-level government-authority reduction; a clan leader consents for the entire clan, while a clanless notable consents only for themself."),
            Legacy("install_ruling_clan", "politics", "PoliticsInstallRulingClan", "actorKingdomId,actorClanId,reason", "Installs a claimant clan as the kingdom ruling clan after a successful internal challenge."),
            Legacy("marriage_alliance", "politics", "PoliticsMarriageAlliance", "actorKingdomId,actorClanId,targetClanId,terms.marriageHero1StringId,terms.marriageHero2StringId,reason", "Complete marriage for the exact named couple. For personal dialogue use the player and consenting NPC hero IDs from resolver hints, in terms.marriageHero1StringId and terms.marriageHero2StringId. Never substitute relatives or infer personal consent from a clan alliance."),
            Legacy("support_claimant", "politics", "PoliticsSupportClaimant", "actorKingdomId,actorClanId,reason", "Records support for a claimant clan or pretender."),
            Legacy("encourage_clan_defection", "politics", "PoliticsEncourageClanDefection", "actorKingdomId,actorClanId,reason", "Moves a clan from its current kingdom to actor kingdom by defection."),
            Legacy("exile_clan", "politics", "PoliticsExileClan", "actorKingdomId,actorClanId,reason", "Expels a clan from actor kingdom."),
            Legacy("restore_exiled_clan", "politics", "PoliticsRestoreExiledClan", "actorKingdomId,actorClanId,reason", "Restores an independent/exiled clan to actor kingdom."),
            Legacy("mediate_clan_dispute", "politics", "PoliticsMediateClanDispute", "actorKingdomId,actorClanId,targetClanId,reason", "Records mediation between clans and future obligation memory."),
            };
            foreach (string kind in new[] { "gold", "item", "troop", "prisoner", "settlement", "workshop", "ship" })
            {
                var d = new ActionDefinition { Id = "give." + kind, Label = "Give " + (kind == "settlement" ? "land" : kind),
                    Family = "exchange", Adapter = "asset:" + kind, Source = "ReignAssetTransfer",
                    Description = "Transfer an exact owned asset, as one term of an agreement." };
                if (kind != "gold") d.Parameters.Add(P("assetId", kind == "settlement" ? "Land" : kind, kind, true));
                if (kind == "item") d.Parameters.Add(P("variantId", "Item modifier", "itemModifier", false));
                if (kind == "item") { d.Parameters.Add(P("equipmentSet", "Equipment set", "text", false)); d.Parameters.Add(P("equipmentSlot", "Equipment slot", "text", false)); }
                if (kind == "gold" || kind == "item" || kind == "troop" || kind == "prisoner") d.Parameters.Add(P("quantity", "Amount", "quantity", true));
                actions.Add(d);
            }
            actions.Add(Political("join_party", "Join political party", "partyId", "politicalParty"));
            actions.Add(Political("back_platform", "Back a party platform", "partyId", "politicalParty"));
            actions.Last().Parameters.Add(P("durationDays", "Days", "quantity", true));
            actions.Add(Political("support_vote", "Support a proposal", "businessId", "governmentBusiness"));
            actions.Last().Parameters.Add(P("optionId", "Vote", "governmentOption", true));
            actions.First(d => d.Id == "government.back_platform").DurationParameter = "durationDays";
            AddCampaignActions(actions);
            AddGovernmentActions(actions);
            AddCourtActions(actions);
            return actions.ToArray();
        }

        private static ActionDefinition Political(string id, string label, string key, string kind) =>
            new ActionDefinition { Id = "government." + id, Label = label, Family = "government",
                Adapter = "government:" + id, Source = "ReignGovernmentCampaignBehavior",
                Description = label + " through the government's actual membership and voting rules.",
                Parameters = new List<ActionParameter> { P(key, label, kind, true) } };

        private static ActionParameter P(string key, string label, string kind, bool required) =>
            new ActionParameter { Key = key, Label = label, Kind = kind, Required = required };

        private static ActionDefinition Legacy(string id, string family, string adapter, string required, string description)
        {
            var d = new ActionDefinition { Id = id, Family = family, Adapter = adapter,
                Label = id == "go_to_settlement" ? "Go to" : id == "recruit_encountered_resident" ? "Join as a permanent companion" : char.ToUpperInvariant(id[0]) + id.Substring(1).Replace('_', ' '),
                Description = description, Source = "ReignWorldActionType." + adapter };
            if (id == "start_ruling_clan_rebellion") d.Label = "Declare rebellion against your rule";
            if (id == "pledge_rebellion_support") d.Label = "Support me when I declare rebellion";
            d.MovesActor = new[] { "follow", "go_to_settlement", "patrol_around_settlement", "wait_near_settlement",
                "raid_village", "besiege_settlement", "attack_party", "attack_player_party", "show_the_way", "capture_settlement_plan", "attack_settlement" }.Contains(id);
            // Internal multi-step receipts and legacy single-resource transfers have explicit replacements.
            d.ConversationEligible = !new[] { "trade_package", "give_gold_to_player", "transfer_gold", "transfer_item", "transfer_workshop", "transfer_prisoner",
                "confirm_arrest", "resolve_rebellion_pledge", "resolve_rebellion_summons", "respond_to_order_report" }.Contains(id);
            d.Parameters = ExplicitParameters(id);
            if (d.Parameters.Any(p => p.Key == "terms.durationDays")) d.DurationParameter = "terms.durationDays";
            ConfigureSupervisedAction(d);
            return d;
        }
    }
}
