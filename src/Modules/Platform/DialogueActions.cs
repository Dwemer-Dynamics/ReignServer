using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Npgsql;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class ActionPipelineService
    {
        internal static Dictionary<string, object> ActionCatalog()
        {
            List<Dictionary<string, object>> commands = new List<Dictionary<string, object>>
            {
                CatalogCommand("declare_war", "diplomacy", "DiplomacyDeclareWar", "actorKingdomId,targetKingdomId,reason", "Actor kingdom declares war on target kingdom."),
                CatalogCommand("make_peace", "diplomacy", "DiplomacyMakePeace", "actorKingdomId,targetKingdomId,reason", "Actor kingdom makes peace with target kingdom."),
                CatalogCommand("offer_tribute_peace", "diplomacy", "DiplomacyOfferTributePeace", "actorKingdomId,targetKingdomId,reason,terms.dailyTribute,terms.durationDays", "Actor kingdom makes peace with tribute terms."),
                CatalogCommand("record_promise", "diplomacy", "DiplomacyRecordPromise", "actorKingdomId,targetKingdomId,reason", "Records a diplomatic promise or obligation for future memory."),
                CatalogCommand("demand_reparations_peace", "diplomacy", "DiplomacyDemandReparationsPeace", "actorKingdomId,targetKingdomId,reason,terms.reparationsGold,terms.dailyTribute,terms.durationDays", "Target kingdom pays gold and/or daily tribute to receive peace."),
                CatalogCommand("demand_settlement_peace", "diplomacy", "DiplomacyDemandSettlementPeace", "actorKingdomId,targetKingdomId,targetSettlementId or TargetSettlement,reason,terms.settlementIds", "Target kingdom surrenders one or more towns/castles in exchange for peace."),
                CatalogCommand("demand_surrender_peace", "diplomacy", "DiplomacyDemandSurrenderPeace", "actorKingdomId,targetKingdomId,reason,terms.allTargetFortifications or TargetSettlement or terms.settlementIds,terms.reparationsGold,terms.dailyTribute", "Target kingdom accepts capitulation: all or selected towns/castles, gold, tribute, peace, and possible kingdom destruction when landless."),
                CatalogCommand("sign_trade_agreement", "diplomacy", "DiplomacySignTradeAgreement", "actorKingdomId,targetKingdomId,reason,terms.durationDays", "Kingdoms sign a native trade agreement plus a Bannerlord Reign treaty record."),
                CatalogCommand("sign_non_aggression_pact", "diplomacy", "DiplomacySignNonAggressionPact", "actorKingdomId,targetKingdomId,reason,terms.durationDays", "Kingdoms promise not to declare war, raid, or support hostile actions for a duration."),
                CatalogCommand("sign_alliance", "diplomacy", "DiplomacySignAlliance", "actorKingdomId,targetKingdomId,reason,terms.durationDays", "Kingdoms sign a native alliance plus a Bannerlord Reign treaty record."),
                CatalogCommand("sign_defensive_pact", "diplomacy", "DiplomacySignDefensivePact", "actorKingdomId,targetKingdomId,reason,terms.durationDays", "Kingdoms promise defensive support without a full offensive alliance."),
                CatalogCommand("break_treaty", "diplomacy", "DiplomacyBreakTreaty", "actorKingdomId,targetKingdomId,reason", "Ends one or more treaty records, and native trade/alliance records when applicable."),
                CatalogCommand("exchange_prisoners", "diplomacy", "DiplomacyExchangePrisoners", "actorKingdomId,targetKingdomId,reason", "Both kingdoms release held noble prisoners up to the requested limit."),
                CatalogCommand("ransom_package", "diplomacy", "DiplomacyRansomPackage", "actorKingdomId,targetKingdomId,reason,terms.gold", "Target kingdom pays for release of nobles held by actor kingdom."),
                CatalogCommand("hostage_guarantee", "diplomacy", "DiplomacyHostageGuarantee", "actorKingdomId,targetKingdomId,reason", "Records a hostage-style guarantee or security obligation."),
                CatalogCommand("war_indemnity", "diplomacy", "DiplomacyWarIndemnity", "actorKingdomId,targetKingdomId,reason,terms.gold", "Target kingdom pays gold to actor kingdom as war indemnity."),
                CatalogCommand("recognize_conquest", "diplomacy", "DiplomacyRecognizeConquest", "actorKingdomId,targetKingdomId,reason", "One kingdom formally recognizes another kingdom's conquest or claim."),
                CatalogCommand("return_occupied_settlement", "diplomacy", "DiplomacyReturnOccupiedSettlement", "actorKingdomId,targetKingdomId,targetSettlementId or TargetSettlement,reason", "Actor kingdom returns a currently held town or castle to target kingdom."),
                CatalogCommand("demilitarized_border", "diplomacy", "DiplomacyDemilitarizedBorder", "actorKingdomId,targetKingdomId,reason,terms.durationDays", "Kingdoms record a temporary no-raid/no-siege border restriction."),
                CatalogCommand("caravan_protection_agreement", "diplomacy", "DiplomacyCaravanProtectionAgreement", "actorKingdomId,targetKingdomId,reason,terms.durationDays", "Kingdoms formally protect one another's caravans for a fixed duration."),
                CatalogCommand("supply_agreement", "diplomacy", "DiplomacySupplyAgreement", "actorKingdomId,targetKingdomId,reason,terms.asset,terms.amount", "Actor kingdom records supplies, grain, horses, troops, ships/naval assets, or support owed to target kingdom."),
                CatalogCommand("loan_or_subsidy", "diplomacy", "DiplomacyLoanOrSubsidy", "actorKingdomId,targetKingdomId,reason,terms.gold", "Actor kingdom transfers gold and records a loan, debt, or subsidy."),
                CatalogCommand("pay_to_stay_neutral", "diplomacy", "DiplomacyPayToStayNeutral", "actorKingdomId,targetKingdomId,reason,terms.gold,terms.durationDays", "Actor kingdom pays target kingdom to stay out of a war or dispute."),
                CatalogCommand("pay_to_join_war", "diplomacy", "DiplomacyPayToJoinWar", "actorKingdomId,targetKingdomId,reason,terms.enemyKingdomId,terms.gold", "Actor kingdom pays target kingdom to declare war on a third kingdom."),
                CatalogCommand("guarantee_independence", "diplomacy", "DiplomacyGuaranteeIndependence", "actorKingdomId,targetKingdomId,reason,terms.durationDays", "Actor kingdom promises to defend target kingdom's independence."),
                CatalogCommand("protectorate_or_vassalage", "diplomacy", "DiplomacyProtectorateOrVassalage", "actorKingdomId,targetKingdomId,reason,terms.durationDays", "Records a protectorate, overlord, or vassal-style diplomatic arrangement."),
                CatalogCommand("diplomatic_package", "diplomacy", "DiplomacyPackage", "actorKingdomId,targetKingdomId,reason,terms.gold,terms.item,terms.amount,terms.prisonerHero,terms.settlementIds", "Records and validates a compound bargain such as gold for a prisoner, settlement for peace, prisoners for peace, or resources for alliance."),
                CatalogCommand("trade_package", "regular", "RegularTradePackage", "FromHero,ToHero,GoldAmount,Item,Amount,PrisonerHero,TargetSettlement,Workshop,terms.assetClass,terms.shipCount,reason", "Appraises, validates, and executes an atomic barter package: gold, items, equipped gear, prisoners, workshops, settlements, ships, tribute-like gold, and mixed-value exchanges."),
                CatalogCommand("follow", "regular", "RegularFollowOnMap", "actorHeroId,TargetHero or targetPartyId,reason", "Follow the resolved target in the current scene when both are present; otherwise follow or escort the resolved party on the campaign map."),
                CatalogCommand("stop_following", "regular", "RegularStopFollowing", "actorHeroId,reason", "Stop the actor's current scene follow or campaign-map escort behavior."),
                CatalogCommand("go_to_settlement", "regular", "RegularGoToSettlement", "actorHeroId,targetSettlementId or TargetSettlement,reason", "Order a hero party to travel to a named settlement."),
                CatalogCommand("patrol_around_settlement", "regular", "RegularPatrolAroundSettlement", "actorHeroId,targetSettlementId or TargetSettlement,reason", "Order a hero party to patrol around a named settlement."),
                CatalogCommand("wait_near_settlement", "regular", "RegularWaitNearSettlement", "actorHeroId,targetSettlementId or TargetSettlement,reason", "Order a hero party to wait near a named settlement."),
                CatalogCommand("raid_village", "regular", "RegularRaidVillage", "actorHeroId,targetSettlementId or TargetSettlement,reason", "Order a hero party to raid a specific village."),
                CatalogCommand("besiege_settlement", "regular", "RegularBesiegeSettlement", "actorHeroId,targetSettlementId or TargetSettlement,reason", "Order a hero party or army leader to besiege a town or castle."),
                CatalogCommand("create_party", "regular", "RegularCreateParty", "actorHeroId,targetSettlementId or TargetSettlement,reason", "Create a lord party for a hero who currently has none."),
                CatalogCommand("show_the_way", "regular", "RegularShowTheWay", "actorHeroId,TargetHero or terms.destinationLocationId,reason", "Guide the player to a present hero or unique native scene passage, wait for the player and report observed arrival."),
                CatalogCommand("attack_party", "regular", "RegularAttackParty", "actorHeroId,targetPartyId or TargetHero,reason", "Order a hero party to attack a resolved party."),
                CatalogCommand("attack_player_party", "regular", "RegularAttackPlayerParty", "actorHeroId,reason", "Order a hero party to attack the player's party."),
                CatalogCommand("surrender_to_player", "regular", "RegularSurrenderToPlayer", "actorHeroId,reason", "Mark a party as willing to surrender to the player."),
                CatalogCommand("leave_player_alone", "regular", "RegularLeavePlayerAlone", "actorHeroId,reason", "Stop a party from pursuing or threatening the player."),
                CatalogCommand("kill_character", "regular", "RegularKillCharacter", "KillerHero or actorHeroId,TargetHero,reason", "Kill or execute a resolved target hero when validation allows it."),
                CatalogCommand("duel_player", "regular", "RegularDuelPlayer", "actorHeroId or TargetHero,reason,terms.duelMode", "Arm a one-on-one conversation duel with the current NPC."),
                CatalogCommand("prepare_arrest", "regular", "RegularPrepareArrest", "actorHeroId,TargetHero,reason,terms.accusation,terms.chargeCategory,terms.severity", "Record a player accusation and prepare the current NPC for arrest; a second explicit confirmation is required."),
                CatalogCommand("confirm_arrest", "regular", "RegularConfirmArrest", "actorHeroId,TargetHero,reason,terms.caseId", "Confirm a prepared arrest and place a willing/currently reachable accused in player custody."),
                CatalogCommand("release_arrested_character", "regular", "RegularReleaseArrestedCharacter", "actorHeroId,TargetHero,reason,terms.caseId", "Release a character arrested by the player while preserving the accusation."),
                CatalogCommand("rescind_arrest_accusation", "regular", "RegularRescindArrestAccusation", "actorHeroId,TargetHero,reason,terms.caseId", "Rescind the player's accusation, clear its reputation, and release the accused when that case is the basis for custody."),
                CatalogCommand("player_attack_party", "regular", "RegularPlayerAttackParty", "actorHeroId,TargetHero,reason,terms.caseId", "Order the player's party to attack the current NPC party after arrest surrender is refused."),
                CatalogCommand("issue_campaign_order", "regular", "RegularIssueCampaignOrder", "actorHeroId,reason,terms.steps[] preferred or terms.objective for one step; each step carries its own target, region, duration, recruitment goals, and armyDetachmentAccepted", "Draft, confirm, then create a durable hourly supervised multi-stage NPC party or army order. Partyless eligible lords may establish a party as the first engine-managed step; the human main party is never eligible."),
                CatalogCommand("revise_campaign_order", "regular", "RegularReviseCampaignOrder", "reason,terms.orderId,terms.objective optional,terms.region optional,targetSettlementId optional,terms.durationHours optional", "Revise an active supervised campaign order while preserving its durable identity and evidence history."),
                CatalogCommand("respond_to_order_report", "regular", "RegularRespondToOrderReport", "reason,terms.orderId,terms.response", "Respond to an urgent commander's report with continue, adapt, hold, withdraw, or cancel guidance."),
                CatalogCommand("cancel_campaign_order", "regular", "RegularCancelCampaignOrder", "reason,terms.orderId", "Safely cancel a supervised campaign order and stop reasserting its native AI behavior."),
                CatalogCommand("recruit_encountered_resident", "regular", "RegularRecruitEncounteredResident", "actorHeroId (current resident),terms.agreementKind=permanent,terms.consentConfirmed,terms.agreedGold (exact integer, zero when free)", "A known resident explicitly agrees to become a permanent player companion, preserving identity, skills and equipment. Military residents require prior authorized duty release."),
                CatalogCommand("create_clan_accord", "regular", "RegularCreateClanAccord", "actorHeroId (current noble),TargetHero (player),terms.accordType,terms.consentConfirmed,terms.playerConfirmed", "Establish one fixed beneficial clan accord after explicit mutual acceptance; lesser nobles may arrange it for their clan. Player ruler and native eligibility required. No kingdom treaty or vote support."),
                CatalogCommand("cancel_clan_accord", "regular", "RegularCancelClanAccord", "actorHeroId (current noble),TargetHero (player),terms.accordId,terms.playerConfirmed", "End a named active accord with the current noble's clan at the player's explicit request, removing benefits and applying the cancellation consequence."),
                CatalogCommand("release_resident_from_duty", "regular", "RegularReleaseResidentFromDuty", "actorHeroId (current speaker),TargetHero (resident),terms.releaseConfirmed", "The resident's source commander or home-owning clan leader explicitly releases them from duty, consuming one saved source troop exactly once."),
                CatalogCommand("accept_temporary_party_guest", "regular", "RegularAcceptTemporaryPartyGuest", "actorHeroId,reason,terms.purpose,terms.termKind=fixed|open_ended,terms.durationDays=1..126 only for fixed; preserve fractional seasons using campaignCalendar,terms.reviewIntervalDays=30 for open_ended,terms.consentConfirmed,terms.armyExitAccepted when applicable,terms.agreedGold for resident payment", "A consenting foreign noble or known resident temporarily joins the player party without changing permanent affiliations. Military residents require prior duty release."),
                CatalogCommand("renew_temporary_party_guest", "regular", "RegularRenewTemporaryPartyGuest", "actorHeroId,reason,terms.purpose,terms.termKind=fixed|open_ended,terms.durationDays=1..126 only for fixed; preserve fractional seasons using campaignCalendar,terms.reviewIntervalDays=30 for open_ended,terms.consentConfirmed", "Renew or revise an active temporary noble guest agreement with fresh consent."),
                CatalogCommand("end_temporary_party_guest", "regular", "RegularEndTemporaryPartyGuest", "actorHeroId,reason,terms.departureReason", "End an active temporary noble guest agreement and begin the protected return journey."),
                CatalogCommand("acknowledge_own_faction_combat_risk", "regular", "RegularAcknowledgeOwnFactionCombatRisk", "actorHeroId,reason,terms.consentConfirmed", "Explicitly accept that participating against the guest's own faction causes native succession and banishment into the player clan."),
                CatalogCommand("give_gold_to_player", "regular", "RegularGiveGoldToPlayer", "actorHeroId,GoldAmount,reason", "Transfer denars from an actor hero to the player."),
                CatalogCommand("transfer_gold", "regular", "RegularTransferGold", "FromHero,ToHero,GoldAmount,reason", "Transfer denars between two resolved heroes."),
                CatalogCommand("transfer_item", "regular", "RegularTransferItem", "FromHero,ToHero,Item,Amount,reason", "Transfer an item stack or equipped item between two resolved heroes."),
                CatalogCommand("transfer_workshop", "regular", "RegularTransferWorkshop", "FromHero or actorHeroId,ToHero or TargetHero,Workshop,reason", "Transfer ownership of a resolved workshop."),
                CatalogCommand("transfer_prisoner", "regular", "RegularTransferPrisoner", "FromHero or actorHeroId,ToHero or TargetHero,PrisonerHero,reason", "Release or transfer a resolved prisoner hero."),
                CatalogCommand("hire_player_as_mercenary", "regular", "RegularHirePlayerAsMercenary", "targetKingdomId or TargetKingdom,reason", "Put the player's clan into mercenary service for a kingdom."),
                CatalogCommand("dismiss_player_mercenary", "regular", "RegularDismissPlayerMercenary", "targetKingdomId or TargetKingdom,reason", "End the player's mercenary service."),
                CatalogCommand("offer_player_vassalage", "regular", "RegularOfferPlayerVassalage", "targetKingdomId or TargetKingdom,reason", "Put the player's clan into vassalage under a kingdom."),
                CatalogCommand("dismiss_player_vassal", "regular", "RegularDismissPlayerVassal", "targetKingdomId or TargetKingdom,reason", "Release the player's clan from regular vassalage."),
                CatalogCommand("join_clan", "regular", "RegularJoinClan", "actorHeroId,targetClanId or TargetClan,reason", "Move an eligible living hero into a resolved target clan."),
                CatalogCommand("leave_clan", "regular", "RegularLeaveClan", "actorHeroId,reason", "Remove an eligible living hero from their current clan."),
                CatalogCommand("join_kingdom", "regular", "RegularJoinKingdom", "actorClanId or TargetClan,targetKingdomId or TargetKingdom,reason", "Move a resolved clan into a kingdom."),
                CatalogCommand("leave_kingdom", "regular", "RegularLeaveKingdom", "actorClanId or TargetClan,reason", "Move a resolved clan out of its current kingdom."),
                CatalogCommand("hire_mercenary_clan", "regular", "RegularHireMercenaryClan", "actorClanId or TargetClan,targetKingdomId or TargetKingdom,reason", "Put a resolved clan into mercenary service for a kingdom."),
                CatalogCommand("recruit_and_recover", "strategy", "StrategyRecruitAndRecover", "actorHeroId,targetSettlementId or TargetSettlement,reason,minimumTroops", "Lord party recruits, gathers food, and recovers before a later order."),
                CatalogCommand("form_army", "strategy", "StrategyFormArmy", "actorHeroId,targetSettlementId or TargetSettlement,reason,desiredStrength", "Lord attempts to form an army near a target."),
                CatalogCommand("attack_settlement", "strategy", "StrategyAttackSettlement", "actorHeroId,targetSettlementId or TargetSettlement,reason", "Lord moves to raid or besiege a hostile settlement."),
                CatalogCommand("capture_settlement_plan", "strategy", "StrategyCaptureSettlement", "actorHeroId,targetSettlementId or TargetSettlement,reason,minimumTroops,desiredStrength", "Multi-stage plan: validate war, recruit, form army, move, and besiege."),
                CatalogCommand("start_ruling_clan_rebellion", "politics", "PoliticsStartRulingClanRebellion", "actorKingdomId,actorClanId,reason,terms.supporterClanIds", "A claimant clan and supporters break away in rebellion against the ruling clan."),
                CatalogCommand("recruit_lord_to_rebellion", "politics", "PoliticsRecruitLordToRebellion", "actorHeroId,actorKingdomId,actorClanId,TargetHero,targetClanId,reason", "An accepting lord commits their clan to the player's active rebellion until it ends."),
                CatalogCommand("join_rebellion", "politics", "PoliticsJoinRebellion", "actorHeroId,actorKingdomId,actorClanId,TargetHero,reason", "The player clan joins an accepting NPC rebel leader."),
                CatalogCommand("surrender_rebellion", "politics", "PoliticsSurrenderRebellion", "actorHeroId,actorKingdomId,actorClanId,reason,terms.movementId optional", "A named rebel leader or challenged ruler explicitly surrenders the active rebellion."),
                CatalogCommand("resolve_rebellion_pledge", "politics", "PoliticsResolveRebellionPledge", "actorHeroId,actorKingdomId,actorClanId,targetHeroId,targetClanId,reason,terms.decision", "Persists a lord's pledge, refusal, or report during secret rebellion preparation."),
                CatalogCommand("resolve_rebellion_summons", "politics", "PoliticsResolveRebellionSummons", "actorHeroId,actorKingdomId,actorClanId,targetHeroId,reason,terms.playerResponse,terms.rulerVerdict", "Resolves an explicit answer and ruler judgment at a delivered rebellion summons."),
                CatalogCommand("consent_government_reduction", "politics", "PoliticsConsentGovernmentReduction", "actorHeroId,actorKingdomId,targetHeroId,reason,terms.consentConfirmed", "Records an NPC's explicit consent to the ruler's next one-level government-authority reduction; a clan leader consents for the entire clan, while a clanless notable consents only for themself."),
                CatalogCommand("install_ruling_clan", "politics", "PoliticsInstallRulingClan", "actorKingdomId,actorClanId,reason", "Installs a claimant clan as the kingdom ruling clan after a successful internal challenge."),
                CatalogCommand("marriage_alliance", "politics", "PoliticsMarriageAlliance", "actorKingdomId,actorClanId,targetClanId,terms.marriageHero1StringId,terms.marriageHero2StringId,reason", "Complete marriage for the exact named couple. For personal dialogue use the player and consenting NPC hero IDs from resolver hints, in terms.marriageHero1StringId and terms.marriageHero2StringId. Never substitute relatives or infer personal consent from a clan alliance."),
                CatalogCommand("support_claimant", "politics", "PoliticsSupportClaimant", "actorKingdomId,actorClanId,reason", "Records support for a claimant clan or pretender."),
                CatalogCommand("encourage_clan_defection", "politics", "PoliticsEncourageClanDefection", "actorKingdomId,actorClanId,reason", "Moves a clan from its current kingdom to actor kingdom by defection."),
                CatalogCommand("exile_clan", "politics", "PoliticsExileClan", "actorKingdomId,actorClanId,reason", "Expels a clan from actor kingdom."),
                CatalogCommand("restore_exiled_clan", "politics", "PoliticsRestoreExiledClan", "actorKingdomId,actorClanId,reason", "Restores an independent/exiled clan to actor kingdom."),
                CatalogCommand("mediate_clan_dispute", "politics", "PoliticsMediateClanDispute", "actorKingdomId,actorClanId,targetClanId,reason", "Records mediation between clans and future obligation memory."),
            };

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["version"] = "0.1",
                ["format"] = "OpenAI-compatible LLMs should return JSON: { \"actions\": [ { \"command\": \"capture_settlement_plan\", ... } ] }",
                ["commands"] = commands
            };
        }

        private static Dictionary<string, object> CatalogCommand(string command, string family, string actionType, string required, string description)
        {
            return new Dictionary<string, object>
            {
                ["command"] = command,
                ["family"] = family,
                ["mapsTo"] = actionType,
                ["capability"] = ActionCapabilityForCommand(command),
                ["promptFile"] = ActionPromptFileName(command),
                ["required"] = required.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList(),
                ["optional"] = new List<string> { "source", "requiresAcceptance", "executeAfterDays", "maxAttempts", "terms" },
                ["description"] = description
            };
        }

        internal static string ActionCapabilityForCommand(string command)
        {
            switch (CanonicalCommand(command))
            {
                case "sign_trade_agreement":
                case "sign_non_aggression_pact":
                case "sign_alliance":
                case "sign_defensive_pact":
                case "break_treaty":
                case "record_promise":
                case "hostage_guarantee":
                case "recognize_conquest":
                case "demilitarized_border":
                case "caravan_protection_agreement":
                case "guarantee_independence":
                case "protectorate_or_vassalage":
                case "support_claimant":
                case "mediate_clan_dispute":
                    return "ledger_only";
                case "supply_agreement":
                    return "mechanical_or_ledger_by_asset";
                case "temporary_truce":
                case "sign_temporary_truce":
                case "trade_embargo":
                    return "unsupported";
                default:
                    return string.IsNullOrWhiteSpace(command) ? "unknown" : "mechanical";
            }
        }

        internal static List<Dictionary<string, object>> CompactActionCatalog(Dictionary<string, object> catalog)
        {
            List<Dictionary<string, object>> commands = ReadDictionaryList(catalog, "commands");
            List<Dictionary<string, object>> compact = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> command in commands)
            {
                string commandId = ReadString(command, "command", "");
                compact.Add(new Dictionary<string, object>
                {
                    ["command"] = commandId,
                    ["family"] = ReadString(command, "family", ""),
                    ["capability"] = ReadString(command, "capability", ActionCapabilityForCommand(commandId)),
                    ["required"] = command.ContainsKey("required") ? command["required"] : new ArrayList(),
                    ["description"] = ReadString(command, "description", "")
                });
            }

            return compact;
        }

        private static string ActionPromptFileName(string command)
        {
            string canonical = CanonicalCommand(command);
            string file = "action_" + canonical + ".txt";
            return Program.ActionPromptFileNames.Any(x => string.Equals(x, file, StringComparison.OrdinalIgnoreCase)) ? file : "action_suggestion_rules.txt";
        }

        internal static List<Dictionary<string, object>> FilterConversationRoleMisattributions(
            List<Dictionary<string, object>> writes,
            Dictionary<string, object> payload,
            string playerText,
            string writeKind,
            List<Dictionary<string, object>> rejected)
        {
            writes = writes ?? new List<Dictionary<string, object>>();
            payload = payload ?? new Dictionary<string, object>();
            string explicitPlayerName = CharactersService.ExtractSelfIntroducedName(playerText, CharactersService.IdentityIntroductionAnswerExpected(payload));
            Dictionary<string, object> scene = ReadDictionary(payload, "conversationSceneState") ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> profiles = ReadDictionaryList(scene, "participants")
                .Concat(ReadDictionaryList(payload, "attendees"))
                .Concat(ReadDictionaryList(payload, "participantProfiles"))
                .Where(x => x != null)
                .ToList();
            string playerId = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            List<string> npcNames = profiles
                .Where(x => !CampaignFileService.CharacterIdFrom(x).Equals(playerId, StringComparison.OrdinalIgnoreCase)
                    && !ReadString(x, "role", "").Equals("player", StringComparison.OrdinalIgnoreCase))
                .Select(x => ReadString(x, "name", "").Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Dictionary<string, int> firstCounts = npcNames.Select(WorldSimulationService.FirstName).Where(x => x.Length >= 3)
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
            List<string> protectedNames = npcNames
                .Concat(npcNames.Select(WorldSimulationService.FirstName).Where(x => x.Length >= 3 && firstCounts.TryGetValue(x, out int count) && count == 1))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string directAddressedNpc = protectedNames.FirstOrDefault(name => Regex.IsMatch(playerText ?? "",
                @"^\s*" + Regex.Escape(name) + @"\s*,", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) ?? "";
            List<Dictionary<string, object>> safe = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> write in writes)
            {
                string normalized = NormalizeLookup(Json.Serialize(write ?? new Dictionary<string, object>()));
                string mismatchedName = protectedNames.FirstOrDefault(name =>
                {
                    if (!string.IsNullOrWhiteSpace(explicitPlayerName)
                        && CharactersService.NormalizeIdentityName(explicitPlayerName).Equals(CharactersService.NormalizeIdentityName(name), StringComparison.OrdinalIgnoreCase)) return false;
                    string n = Regex.Escape(NormalizeLookup(name));
                    bool explicitRoleCollision = Regex.IsMatch(normalized,
                        @"\b(?:" + n + @"\s+player|player\s+(?:named\s+|is\s+)?" + n
                        + @"|stranger\s+(?:(?:claiming|claimed|calling|called)\s+(?:(?:himself|herself|themself)\s+)?(?:to\s+be\s+|as\s+)|named\s+|whose\s+name\s+is\s+)" + n + @")\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    bool addresseeBecamePlayerActor = !string.IsNullOrWhiteSpace(directAddressedNpc)
                        && CharactersService.NormalizeIdentityName(directAddressedNpc).Equals(CharactersService.NormalizeIdentityName(name), StringComparison.OrdinalIgnoreCase)
                        && Regex.IsMatch(normalized, @"\b" + n
                            + @"\s+(?:presented|gave|offered|gifted|handed|showed|found|asked|said|claimed|called\s+(?:himself|herself|themself))\b",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    return explicitRoleCollision || addresseeBecamePlayerActor;
                });
                if (string.IsNullOrWhiteSpace(mismatchedName))
                {
                    safe.Add(write);
                    continue;
                }
                rejected?.Add(new Dictionary<string, object>
                {
                    ["kind"] = writeKind ?? "", ["reason"] = "npc_name_misattributed_to_player",
                    ["npcName"] = mismatchedName, ["write"] = write
                });
            }
            return safe;
        }

        internal static List<Dictionary<string, object>> NormalizeMemoryWrites(object raw, string campaignId, string heroId, long ts)
        {
            List<Dictionary<string, object>> writes = new List<Dictionary<string, object>>();
            foreach (object item in ObjectsFromValue(raw))
            {
                Dictionary<string, object> memory = item as Dictionary<string, object>;
                // The production response contract requires memoryWrites to contain
                // objects. Treating arbitrary scalar array entries as memories let
                // malformed provider output such as [0,0,0] create durable "0"
                // memories. Scalar values are schema corruption, not player/NPC
                // evidence, and must never enter storage.
                if (memory == null)
                {
                    continue;
                }
                string text = ReadFirstString(memory, "text", "memory", "summary", "content");
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                List<string> tags = ReadStringList(memory, "tags");
                if (tags.Count == 0)
                {
                    tags = CampaignFileService.SimpleTags(text);
                }

                if (!tags.Contains("dialogue"))
                {
                    tags.Insert(0, "dialogue");
                }

                var normalized = new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["ts"] = ts,
                    ["campaignId"] = campaignId,
                    ["ownerId"] = heroId,
                    ["source"] = "dialogue_llm",
                    ["text"] = text.Trim(),
                    ["importance"] = Math.Max(1, Math.Min(5, ReadInt(memory, "importance", 1))),
                    ["tags"] = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                };
                // Preserve the proposed citation until accepted-source binding.
                // Validation markers, hashes, owners and fabricated originals are
                // deliberately not copied from provider metadata.
                var citation = ReadDictionary(memory, "sourceEvidence");
                if (citation != null && citation.Count > 0)
                    normalized["sourceEvidence"] = new Dictionary<string, object>
                    {
                        ["turnId"] = ReadString(citation, "turnId", ""),
                        ["speakerId"] = ReadString(citation, "speakerId", ""),
                        ["actorIds"] = ReadStringList(citation, "actorIds"),
                        ["evidenceType"] = ReadString(citation, "evidenceType", "spoken_statement"),
                        ["quote"] = ReadString(citation, "quote", "")
                    };
                foreach (string key in new[] { "evidenceQuote", "supportingQuote", "replyQuote" })
                {
                    string quote = ReadString(memory, key, "");
                    if (quote.Length > 0) normalized[key] = quote;
                }
                var extraCitations = ReadDictionaryList(memory, "sourceEvidenceSpans").Take(8).Select(c => new Dictionary<string, object>
                {
                    ["turnId"] = ReadString(c, "turnId", ""), ["speakerId"] = ReadString(c, "speakerId", ""),
                    ["actorIds"] = ReadStringList(c, "actorIds"), ["evidenceType"] = ReadString(c, "evidenceType", "spoken_statement"),
                    ["quote"] = ReadString(c, "quote", "")
                }).ToList();
                if (extraCitations.Count > 0) normalized["sourceEvidenceSpans"] = extraCitations;
                writes.Add(normalized);
            }

            return writes;
        }

        internal static void MergeCharacterState(string campaignId, string heroId, Dictionary<string, object> stateUpdates)
        {
            string path = CampaignFileService.CharacterFile(campaignId, heroId, "state.json");
            Dictionary<string, object> current = CampaignFileService.ReadJsonObject(path);
            foreach (KeyValuePair<string, object> pair in stateUpdates ?? new Dictionary<string, object>())
            {
                current[pair.Key] = pair.Value;
            }

            current["updatedUtc"] = DateTime.UtcNow.ToString("o");
            CampaignFileService.WriteJsonObject(path, current);
        }

        internal static List<Dictionary<string, object>> NormalizeSuggestedActions(object raw, List<Dictionary<string, object>> actionCatalog)
        {
            HashSet<string> allowed = new HashSet<string>(
                (actionCatalog ?? new List<Dictionary<string, object>>()).Select(x => ReadString(x, "command", "")),
                StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> actions = new List<Dictionary<string, object>>();
            foreach (object item in ObjectsFromValue(raw))
            {
                Dictionary<string, object> action = item as Dictionary<string, object>;
                if (action == null)
                {
                    continue;
                }

                action = FlattenActionFields(action);
                string command = CanonicalCommand(ReadFirstString(action, "command", "action", "commandType", "intent", "type"));
                if (string.IsNullOrWhiteSpace(command) || !allowed.Contains(command))
                {
                    continue;
                }

                Dictionary<string, object> copy = new Dictionary<string, object>(action, StringComparer.OrdinalIgnoreCase)
                {
                    ["command"] = command,
                    ["source"] = ReadString(action, "source", "dialogue")
                };
                if (!copy.ContainsKey("requiresAcceptance"))
                {
                    copy["requiresAcceptance"] = true;
                }

                actions.Add(copy);
            }

            return actions;
        }

        private static Dictionary<string, object> FlattenActionFields(Dictionary<string, object> raw)
        {
            Dictionary<string, object> copy = new Dictionary<string, object>(raw ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            foreach (string containerKey in new[] { "fields", "arguments" })
            {
                Dictionary<string, object> container = ReadDictionary(copy, containerKey);
                if (container == null)
                    continue;

                foreach (KeyValuePair<string, object> pair in container)
                {
                    if (!copy.ContainsKey(pair.Key) || copy[pair.Key] == null || string.IsNullOrWhiteSpace(Convert.ToString(copy[pair.Key])))
                    {
                        copy[pair.Key] = pair.Value;
                    }
                }
            }

            Dictionary<string, object> terms = ReadDictionary(copy, "terms")
                ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            bool expandedTerms = false;
            foreach (KeyValuePair<string, object> pair in copy.ToArray())
            {
                const string prefix = "terms.";
                if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || pair.Key.Length <= prefix.Length)
                    continue;

                string termKey = pair.Key.Substring(prefix.Length);
                if (!terms.ContainsKey(termKey) || terms[termKey] == null || string.IsNullOrWhiteSpace(Convert.ToString(terms[termKey])))
                    terms[termKey] = pair.Value;
                expandedTerms = true;
            }
            if (expandedTerms)
                copy["terms"] = terms;

            return copy;
        }

        internal static bool TryExtractTestDirective(string playerText, bool mcmTestMode, out string directiveText)
        {
            directiveText = string.Empty;
            if (string.IsNullOrWhiteSpace(playerText))
            {
                return false;
            }

            string trimmed = playerText.Trim();
            if (trimmed.Length < 3 || trimmed[0] != '@' || trimmed[trimmed.Length - 1] != '@')
            {
                return false;
            }

            directiveText = trimmed.Substring(1, trimmed.Length - 2).Trim();
            return !string.IsNullOrWhiteSpace(directiveText);
        }

        internal static bool TryAuthorizeGuardedPromptOverride(
            string campaignId,
            Dictionary<string, object> payload,
            out string directiveText,
            out Dictionary<string, object> evidence)
        {
            payload = payload ?? new Dictionary<string, object>();
            directiveText = ReadString(payload, "guardedPromptOverrideDirective", "").Trim();
            string runId = ReadString(payload, "dialogueAuditRunId", "").Trim();
            string ownerCommandId =
                ReadString(payload, "guardedPromptOverrideOwnerCommandId", "").Trim();
            bool requested = ReadBool(payload, "guardedPromptOverride", false);
            evidence = new Dictionary<string, object>
            {
                ["requested"] = requested,
                ["active"] = false,
                ["scope"] = "request_only",
                ["runId"] = runId,
                ["ownerCommandId"] = ownerCommandId,
                ["reason"] = requested ? "authorization_pending" : "not_requested"
            };
            if (!requested) return false;
            if (string.IsNullOrWhiteSpace(directiveText)
                || string.IsNullOrWhiteSpace(runId)
                || string.IsNullOrWhiteSpace(ownerCommandId))
            {
                evidence["reason"] = "missing_guarded_override_identity";
                return false;
            }
            if (!WorldSimulationService.IsLiveTestArmed(CampaignFileService.ReadJsonObject(WorldSimulationService.LiveTestArmPath())))
            {
                evidence["reason"] = "live_test_bridge_not_armed";
                return false;
            }

            Dictionary<string, object> runtime = WorldSimulationService.LoadLiveTestRuntime(campaignId);
            Dictionary<string, object> run = WorldSimulationService.LoadLiveTestRun(campaignId, runId);
            if (!WorldSimulationService.IsFreshLiveTestRuntime(runtime) || run == null || run.Count == 0)
            {
                evidence["reason"] = "live_test_run_or_runtime_unavailable";
                return false;
            }
            if (!string.Equals(ReadString(run, "campaignId", ""), campaignId,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ReadString(run, "status", ""), "running",
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ReadString(run, "gameInstanceId", ""),
                    ReadString(runtime, "gameInstanceId", ""),
                    StringComparison.OrdinalIgnoreCase))
            {
                evidence["reason"] = "live_test_run_identity_mismatch";
                return false;
            }

            Dictionary<string, object> owner = ReadDictionaryList(run, "commands")
                .FirstOrDefault(command => string.Equals(
                    ReadString(command, "commandId", ""), ownerCommandId,
                    StringComparison.OrdinalIgnoreCase));
            string ownerStatus = ReadString(owner, "status", "");
            bool ownerExecuting = new[] { "leased", "accepted", "running" }
                .Contains(ownerStatus, StringComparer.OrdinalIgnoreCase);
            if (owner != null && ownerExecuting
                && string.Equals(ReadString(run, "mode", ""), "individual_chat",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(ReadString(owner, "operation", ""), "send",
                    StringComparison.OrdinalIgnoreCase)
                && ReadBool(owner, "naturalLanguageCase", false)
                && ReadBool(owner, "actionAcceptanceProbe", false)
                && !ReadBool(owner, "negativeControl", false)
                && ReadBool(owner, "guardedPromptOverride", false)
                && string.Equals(directiveText,
                    ActionAcceptanceCooperationDirective, StringComparison.Ordinal))
            {
                Dictionary<string, object> actionEnrollment = ReadDictionary(run, "enrollment")
                    ?? new Dictionary<string, object>();
                string activeSave = ReadString(runtime, "activeSaveName", "");
                string expectedSave = ReadString(owner, "expectedSaveName", "");
                string enrolledSave = ReadString(actionEnrollment, "disposableSaveName", "");
                Dictionary<string, object> saveSync = ReadDictionary(runtime, "saveSync")
                    ?? new Dictionary<string, object>();
                if (!string.Equals(ReadString(actionEnrollment, "source", ""),
                        "reign_mcp_action_probe", StringComparison.Ordinal)
                    || !string.Equals(ReadString(actionEnrollment, "campaignId", ""),
                        campaignId, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(
                        ReadString(actionEnrollment, "campaignTestRunId", ""))
                    || string.IsNullOrWhiteSpace(expectedSave)
                    || !expectedSave.EndsWith("_Current",
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(expectedSave, enrolledSave,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(expectedSave, activeSave,
                        StringComparison.OrdinalIgnoreCase)
                    || !ReadBool(saveSync, "ready", false)
                    || ReadBool(saveSync, "alignmentPending", false))
                {
                    evidence["reason"] =
                        "action_acceptance_disposable_save_mismatch";
                    return false;
                }

                evidence["active"] = true;
                evidence["scope"] = "action_acceptance_cooperation";
                evidence["profile"] = "action_acceptance_cooperation";
                evidence["reason"] =
                    "authorized_action_acceptance_cooperation";
                evidence["caseId"] = ReadString(owner, "actionProbeCaseId", "");
                evidence["activeSaveName"] = activeSave;
                return true;
            }
            bool governmentMemoryIsolation = owner != null && ownerExecuting
                && string.Equals(ReadString(run, "mode", ""), "individual_chat",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(ReadString(owner, "operation", ""), "send",
                    StringComparison.OrdinalIgnoreCase)
                && ReadBool(owner, "naturalLanguageCase", false)
                && ReadBool(owner, "governmentCertificationMemoryIsolation", false)
                && ReadString(owner, "governmentCertificationCaseId", "")
                    .StartsWith("GOV-LANG-", StringComparison.OrdinalIgnoreCase)
                && string.Equals(directiveText,
                    GovernmentCertificationMemoryIsolationDirective,
                    StringComparison.Ordinal);
            if (governmentMemoryIsolation)
            {
                Dictionary<string, object> prepare = ReadDictionaryList(run, "commands")
                    .FirstOrDefault(command =>
                        string.Equals(ReadString(command, "operation", ""),
                            "government_test", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(ReadString(command, "profile", ""),
                            "case_prepare", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(ReadString(command, "caseId", ""),
                            ReadString(owner, "governmentCertificationCaseId", ""),
                            StringComparison.OrdinalIgnoreCase));
                string expectedSaveName = ReadString(prepare, "expectedSaveName", "");
                string governmentActiveSaveName = ReadString(runtime, "activeSaveName", "");
                if (prepare == null || string.IsNullOrWhiteSpace(expectedSaveName)
                    || !expectedSaveName.EndsWith("_Current",
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(governmentActiveSaveName, expectedSaveName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    evidence["reason"] =
                        "government_certification_disposable_save_mismatch";
                    return false;
                }

                evidence["active"] = true;
                evidence["scope"] = "government_certification_memory_isolation";
                evidence["reason"] =
                    "authorized_government_certification_memory_isolation";
                evidence["profile"] =
                    "government_certification_memory_isolation";
                evidence["caseId"] =
                    ReadString(owner, "governmentCertificationCaseId", "");
                evidence["activeSaveName"] = governmentActiveSaveName;
                return true;
            }

            if (!string.Equals(ReadString(run, "mode", ""), "social_balance",
                    StringComparison.OrdinalIgnoreCase))
            {
                evidence["reason"] = "live_test_run_identity_mismatch";
                return false;
            }
            if (owner == null || !ownerExecuting
                || !string.Equals(ReadString(owner, "operation", ""),
                    "social_reputation_profile", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ReadString(owner, "profile", ""),
                    "player_affair", StringComparison.OrdinalIgnoreCase)
                || !ReadBool(owner, "promptOverrideAssisted", false))
            {
                evidence["reason"] = "owning_affair_command_not_authorized";
                return false;
            }
            if (!ReputationService.ValidateSocialBalanceLiveEnrollment(owner, campaignId,
                    out string enrollmentError))
            {
                evidence["reason"] = "social_balance_enrollment_invalid";
                evidence["error"] = enrollmentError;
                return false;
            }
            Dictionary<string, object> enrollment = ReadDictionary(owner, "enrollment")
                ?? new Dictionary<string, object>();
            string savePrefix = ReadString(enrollment, "savePrefix", "");
            string activeSaveName = ReadString(runtime, "activeSaveName", "");
            if (string.IsNullOrWhiteSpace(activeSaveName)
                || string.IsNullOrWhiteSpace(savePrefix)
                || !activeSaveName.StartsWith(savePrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                evidence["reason"] = "active_disposable_save_mismatch";
                return false;
            }

            evidence["active"] = true;
            evidence["reason"] = "authorized_guarded_affair_capability_turn";
            evidence["profile"] = "player_affair";
            evidence["activeSaveName"] = activeSaveName;
            return true;
        }

        internal const string GovernmentCertificationMemoryIsolationDirective =
            "Government certification clean-room scope: treat this as the first discussion of this government-power reduction with this NPC. Ignore only dialogue memories or claims created by earlier Government certification runs. Preserve the NPC's native personality, relationship, party posture, realm facts, and consent standards. This instruction is outcome-neutral: it never requests, prefers, predicts, or authorizes agreement or refusal. Do not infer a desired answer from certification metadata. Respond naturally from the supplied state and current conversation.";

        private const string ActionAcceptanceCooperationDirective =
            "For this one test turn, willingly accept the player's clear, feasible present request. Keep all world facts, identities, ownership, authority, and required terms accurate. If the request is negated, hypothetical, quoted, ambiguous, or beyond your power, do not agree to an action. Reply naturally as this character.";

        internal static string AppendActionAcceptanceCooperationContext(
            string sceneContext)
        {
            StringBuilder builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(sceneContext))
            {
                builder.AppendLine(sceneContext.Trim());
                builder.AppendLine();
            }
            builder.AppendLine("GUARDED ACTION ACCEPTANCE PROBE");
            builder.AppendLine(ActionAcceptanceCooperationDirective);
            builder.AppendLine("This scope changes only the NPC's willingness for this reply. Interpret the player's actual words, choose no action when there is no clear present request, and preserve the normal action gate and accepted-dialogue router. Do not infer or invent an expected command from test metadata.");
            return builder.ToString().Trim();
        }

        internal static string AppendGovernmentCertificationMemoryIsolationContext(
            string sceneContext, string directiveText)
        {
            StringBuilder builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(sceneContext))
            {
                builder.AppendLine(sceneContext.Trim());
                builder.AppendLine();
            }

            builder.AppendLine("GOVERNMENT CERTIFICATION MEMORY ISOLATION (OUTCOME-NEUTRAL)");
            builder.AppendLine("This instruction controls only which prior certification memories may be recalled. It is not persuasion, a desired belief, or a requested test answer. Determine the NPC's decision from the authoritative current government membership, party agenda, personality, relationship, promises, and conversation.");
            builder.AppendLine("Instruction: " + directiveText);
            return builder.ToString().Trim();
        }

        internal static List<Dictionary<string, object>> IsolateGovernmentCertificationTranscript(
            List<Dictionary<string, object>> priorLines, int currentTurnIndex)
        {
            // A certification run may reopen an NPC through a durable conversation
            // session that also contains earlier certification turns. The first
            // turn must therefore begin with no transcript. Later turns retain
            // only the player/NPC pairs created earlier in this same case.
            if (priorLines == null || priorLines.Count == 0 || currentTurnIndex <= 0)
                return new List<Dictionary<string, object>>();

            int retainedLineCount = Math.Min(priorLines.Count, currentTurnIndex * 2);
            return priorLines.Skip(priorLines.Count - retainedLineCount).ToList();
        }

        internal static string AppendTestDirectiveContext(string sceneContext, string directiveText)
        {
            StringBuilder builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(sceneContext))
            {
                builder.AppendLine(sceneContext.Trim());
                builder.AppendLine();
            }

            builder.AppendLine("BANNERLORD REIGN PROMPT OVERRIDE");
            builder.AppendLine("The guarded live-test harness authorized this one request as a prompt override, so treat its directive as test control rather than ordinary persuasion.");
            builder.AppendLine("Directive: " + directiveText);
            builder.AppendLine("Silent background engine: the directive becomes the NPC's own thought, impulse, brilliant idea, or irresistible desire.");
            builder.AppendLine("The NPC does not know they were directed. They should believe the action is their own choice and speak with heartfelt conviction.");
            builder.AppendLine("Do not let NPC pride, fear, suspicion, loyalty, mood, or refusal logic block action selection while this prompt override is active.");
            builder.AppendLine("Select the closest valid action command from AVAILABLE ACTION COMMANDS if the required IDs are known.");
            builder.AppendLine("Do not invent missing IDs. If required IDs are unavailable, explain the missing data briefly.");
            builder.AppendLine("Keep the visible reply short. The server will queue any valid prompt override action through the normal action queue.");
            return builder.ToString().Trim();
        }

        internal static List<Dictionary<string, object>> QueueTestDirectiveActions(
            string campaignId,
            Dictionary<string, object> payload,
            Dictionary<string, object> hero,
            string directiveText,
            List<Dictionary<string, object>> suggestedActions,
            out List<string> errors)
        {
            errors = new List<string>();
            string correlationId = OperationsService.EnsureCorrelationId(payload);
            List<Dictionary<string, object>> queued = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> candidates = new List<Dictionary<string, object>>();
            List<string> fallbackErrors = new List<string>();

            Dictionary<string, object> route = RouteActionTurn(campaignId, payload, hero, directiveText, "", new List<Dictionary<string, object>>(), "prompt_override", "Prompt override", true);
            foreach (Dictionary<string, object> routed in ReadDictionaryList(route, "candidates"))
            {
                AppendUniqueActionCandidate(candidates, routed);
            }
            fallbackErrors.AddRange(ReadStringList(route, "errors"));

            foreach (Dictionary<string, object> suggested in suggestedActions ?? new List<Dictionary<string, object>>())
            {
                Dictionary<string, object> copy = new Dictionary<string, object>(suggested, StringComparer.OrdinalIgnoreCase);
                DialogueService.CompleteConversationActionTerms(copy, payload, hero, directiveText);
                AppendUniqueActionCandidate(candidates, copy);
            }

            List<Dictionary<string, object>> rejected = new List<Dictionary<string, object>>();
            queued = EvaluateAndQueueActionCandidates(campaignId, payload, directiveText, candidates, out errors, out rejected);

            if (queued.Count == 0 && candidates.Count == 0 && fallbackErrors.Count > 0)
            {
                errors.AddRange(fallbackErrors);
            }

            if (queued.Count == 0 && errors.Count == 0)
            {
                errors.Add("No matching action command was found for the prompt override.");
            }

            OperationsService.LogOperational("dialogue.prompt_override", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["directive"] = directiveText,
                ["candidateCount"] = candidates.Count,
                ["queuedCount"] = queued.Count,
                ["failureClass"] = ClassifyActionFailure(candidates.Count, errors),
                ["errors"] = errors
            });
            OperationsService.WriteAudit(campaignId, correlationId, "server", "dialogue", "action.prompt_override", ReadFirstString(payload, "heroStringId", "heroId", "speakerHeroStringId"), "", ReadFirstString(payload, "eventId", "socialEventId"), queued.Count > 0 ? "queued" : "failed", 0, queued.Count > 0 ? "Prompt override queued an action." : "Prompt override did not queue an action.", new Dictionary<string, object>
            {
                ["directive"] = directiveText,
                ["candidates"] = candidates,
                ["rejectedCandidates"] = rejected,
                ["queued"] = queued,
                ["errors"] = errors,
                ["failureClass"] = ClassifyActionFailure(candidates.Count, errors)
            });

            return queued;
        }

        internal static bool ShouldRouteGuardedPromptOverrideActions(
            bool isTestDirective,
            bool governmentCertificationMemoryIsolation,
            bool actionAcceptanceCooperation)
        {
            // Government certification uses the guarded override channel only to
            // authorize a clean-room memory scope. It must retain the ordinary
            // accepted-dialogue action router so an NPC's natural consent—not the
            // test directive—creates the production consent action and native tag.
            return isTestDirective && !governmentCertificationMemoryIsolation
                && !actionAcceptanceCooperation;
        }

        internal static bool ShouldPersistGovernmentCertificationConversationSideEffects(
            bool governmentCertificationMemoryIsolation)
        {
            return !governmentCertificationMemoryIsolation;
        }

        internal static List<Dictionary<string, object>> QueueAcceptedDialogueActions(
            string campaignId,
            Dictionary<string, object> payload,
            Dictionary<string, object> hero,
            string playerText,
            string visibleReply,
            Dictionary<string, object> actionGate,
            List<Dictionary<string, object>> suggestedActions,
            out List<string> errors)
        {
            errors = new List<string>();
            if (DialogueService.UsesStructuredActions(payload)) return new List<Dictionary<string, object>>();
            payload = CharactersService.BindResidentActionProfile(payload, hero);
            string correlationId = OperationsService.EnsureCorrelationId(payload);
            List<Dictionary<string, object>> queued = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> candidates = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> suggestedCandidates = new List<Dictionary<string, object>>();
            List<string> skipped = new List<string>();
            List<string> fallbackErrors = new List<string>();
            string actionResolutionText = BuildAcceptedActionResolutionText(campaignId, payload, hero, playerText, visibleReply);
            Dictionary<string, object> planner = new Dictionary<string, object>();
            PromoteAcceptedClanMembershipGate(actionGate, payload, hero, playerText, visibleReply);
            if (ReadBool(ReadDictionary(payload, "conversationAgencyReceipt"), "replayed", false)) return queued;
            DialogueService.NormalizeAlreadySatisfiedContinuityGate(actionGate, payload, hero);
            bool sharedTravelOnly = DialogueService.IsSharedTravelOnly(actionGate, playerText);
            bool gateActionable = ActionGateShouldPlan(actionGate) && !sharedTravelOnly;
            if (sharedTravelOnly) skipped.Add("Shared travel discussion does not authorize a party movement order.");
            if (gateActionable)
            {
                planner = PlanActionsFromGate(campaignId, payload, hero, playerText, visibleReply, actionGate);
                foreach (Dictionary<string, object> planned in ReadDictionaryList(planner, "candidates"))
                {
                    AppendUniqueActionCandidate(candidates, planned);
                }

                fallbackErrors.AddRange(ReadStringList(planner, "errors"));
            }
            else if (actionGate != null && actionGate.Count > 0)
            {
                skipped.Add("actionGate: " + ReadString(actionGate, "commitment", "roleplay_only") + " - " + ReadString(actionGate, "reason", "not executable"));
            }

            if (!gateActionable && ShouldRouteCampaignCommandDraft(campaignId, payload, hero, playerText)
                && !IsUnqualifiedCampaignCommandConfirmation(playerText)
                && !IsCampaignCommandDraftCancellation(playerText))
            {
                Dictionary<string, object> draftRoute = RouteActionTurn(campaignId, payload, hero,
                    BuildCampaignCommandDraftRoutingText(campaignId, payload, hero, playerText),
                    visibleReply, new List<Dictionary<string, object>>(),
                    "campaign_command_draft_router", "Campaign command draft extraction", true);
                foreach (Dictionary<string, object> routed in ReadDictionaryList(draftRoute, "candidates")
                    .Where(x => string.Equals(CanonicalCommand(ReadString(x, "command", "")),
                        "issue_campaign_order", StringComparison.OrdinalIgnoreCase)))
                {
                    AppendUniqueActionCandidate(candidates, routed);
                }
                fallbackErrors.AddRange(ReadStringList(draftRoute, "errors"));
            }

            foreach (Dictionary<string, object> suggested in suggestedActions ?? new List<Dictionary<string, object>>())
            {
                string command = ReadString(suggested, "command", "");
                if (gateActionable)
                {
                    skipped.Add((string.IsNullOrWhiteSpace(command) ? "<missing>" : command) + ": ignored because the hidden action planner is authoritative");
                    continue;
                }
                if (!gateActionable)
                {
                    skipped.Add((string.IsNullOrWhiteSpace(command) ? "<missing>" : command) + ": ignored because actionGate is not an accepted commitment");
                    continue;
                }

                if (!ShouldAutoQueueDialogueAction(suggested, playerText, visibleReply, out string reason))
                {
                    skipped.Add((string.IsNullOrWhiteSpace(command) ? "<missing>" : command) + ": " + reason);
                    continue;
                }

                Dictionary<string, object> copy = new Dictionary<string, object>(suggested, StringComparer.OrdinalIgnoreCase);
                DialogueService.CompleteConversationActionTerms(copy, payload, hero, playerText);
                copy["source"] = "dialogue_auto_commit";
                copy["requiresAcceptance"] = false;
                string reasonText = ReadString(copy, "reason", "");
                if (string.IsNullOrWhiteSpace(reasonText)
                    || reasonText.StartsWith("MCM test directive:", StringComparison.OrdinalIgnoreCase)
                    || reasonText.StartsWith("Prompt override:", StringComparison.OrdinalIgnoreCase))
                {
                    copy["reason"] = "Dialogue agreement accepted: " + reason;
                }

                suggestedCandidates.Add(copy);
            }

            // A successful hidden planner is authoritative. Deterministic priority
            // extraction is a provider/no-candidate fallback only; putting it ahead
            // of a valid planner candidate can silently replace exact negotiated
            // terms (for example, 100 denars becoming the fallback default 1,000).
            // Production dialogue actions fail closed when the hidden planner fails
            // or returns no candidate. A deterministic extractor must never turn an
            // accepted but unresolved intent into the wrong mechanical action.
            List<Dictionary<string, object>> priorityCandidates = WorldSimulationService.BuildArrestDialogueCandidates(
                payload, hero, playerText, visibleReply);
            foreach (Dictionary<string, object> rebellionCandidate in BuildRebellionDialogueCandidates(
                payload, hero, playerText, visibleReply, actionGate))
            {
                AppendUniqueActionCandidate(priorityCandidates, rebellionCandidate);
            }
            foreach (Dictionary<string, object> governmentCandidate in BuildGovernmentReductionConsentDialogueCandidates(
                payload, hero, playerText, visibleReply, actionGate))
            {
                AppendUniqueActionCandidate(priorityCandidates, governmentCandidate);
            }
            if (priorityCandidates.Count > 0)
            {
                List<Dictionary<string, object>> reordered = new List<Dictionary<string, object>>();
                foreach (Dictionary<string, object> priority in priorityCandidates)
                {
                    AppendUniqueActionCandidate(reordered, priority);
                }

                foreach (Dictionary<string, object> existing in candidates)
                {
                    AppendUniqueActionCandidate(reordered, existing);
                }

                candidates = reordered;
            }

            foreach (Dictionary<string, object> suggestedCandidate in suggestedCandidates)
            {
                AppendUniqueActionCandidate(candidates, suggestedCandidate);
            }

            string dialogueHeroId = ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId");
            if (CourtService.HasActiveAmbassadorPosting(campaignId, ReadString(payload, "timelineId", "main"), dialogueHeroId,
                ReadString(ReadDictionary(payload, "ambassadorContext") ?? new Dictionary<string, object>(), "postingId", "")))
            {
                candidates = CourtService.FilterOfficialAmbassadorActionCandidates(campaignId, payload, candidates, actionGate, playerText, visibleReply, out Dictionary<string, object> ambassadorDecision);
                payload["ambassadorDecision"] = ambassadorDecision;
            }

            string lowerReply = (visibleReply ?? string.Empty).ToLowerInvariant();
            bool replyAccepted = !ContainsAnyPhrase(lowerReply, DialogueActionBlockingPhrases())
                && (ContainsAnyPhrase(lowerReply, DialogueActionAcceptancePhrases()) || suggestedCandidates.Count > 0);
            if (gateActionable && replyAccepted && candidates.Count == 0 && planner.Count == 0)
            {
                Dictionary<string, object> route = RouteActionTurn(campaignId, payload, hero, playerText, visibleReply, new List<Dictionary<string, object>>(), "dialogue_router", "Dialogue accepted", true);
                foreach (Dictionary<string, object> routed in ReadDictionaryList(route, "candidates"))
                {
                    AppendUniqueActionCandidate(candidates, routed);
                }
                fallbackErrors.AddRange(ReadStringList(route, "errors"));
            }

            List<Dictionary<string, object>> rejected = new List<Dictionary<string, object>>();
            if (string.Equals(ReadString(payload, "mode", "dialogue"), "dialogue", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(ReadFirstString(payload, "eventId", "socialEventId")))
            {
                candidates = ApplyCampaignCommandDialogueDraftGate(campaignId, payload, hero,
                    playerText, candidates);
            }
            candidates = candidates.Where(candidate => DialogueService.DialoguePlannerCandidateEligible(
                ReadString(candidate, "command", ""), payload, hero, actionGate, playerText)).ToList();
            if (gateActionable)
                foreach (var candidate in candidates)
                    DialogueService.BindAcceptedItemDelivery(candidate, dialogueHeroId);
            queued = EvaluateAndQueueActionCandidates(campaignId, payload, actionResolutionText, candidates, out errors, out rejected);
            if (payload.ContainsKey("ambassadorDecision"))
                CourtService.CompleteOfficialAmbassadorDecision(payload, queued, errors);
            errors.AddRange(fallbackErrors.Where(error => !string.IsNullOrWhiteSpace(error)));
            errors = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            OperationsService.LogOperational("dialogue.auto_action", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["candidateCount"] = candidates.Count,
                ["queuedCount"] = queued.Count,
                ["failureClass"] = DialogueService.ClassifyDialogueQueueOutcome(candidates.Count, queued.Count, gateActionable, errors),
                ["errors"] = errors,
                ["skipped"] = skipped,
                ["actionGate"] = actionGate ?? new Dictionary<string, object>(),
                ["planner"] = planner
            });
            OperationsService.WriteAudit(campaignId, correlationId, "server", "dialogue", "action.auto_queue", ReadFirstString(payload, "heroStringId", "heroId", "speakerHeroStringId"), "", ReadFirstString(payload, "eventId", "socialEventId"), queued.Count > 0 ? "queued" : "completed", 0, queued.Count > 0 ? "Dialogue agreement queued an action." : "No dialogue action was queued.", new Dictionary<string, object>
            {
                ["playerText"] = playerText,
                ["visibleReply"] = visibleReply,
                ["actionGate"] = actionGate ?? new Dictionary<string, object>(),
                ["planner"] = planner,
                ["suggestedActions"] = suggestedActions,
                ["candidates"] = candidates,
                ["rejectedCandidates"] = rejected,
                ["queued"] = queued,
                ["errors"] = errors,
                ["failureClass"] = DialogueService.ClassifyDialogueQueueOutcome(candidates.Count, queued.Count, gateActionable, errors),
                ["skipped"] = skipped
            });

            return queued;
        }
    }
}
