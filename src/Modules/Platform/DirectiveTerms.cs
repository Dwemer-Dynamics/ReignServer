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
        internal static void CompleteTestDirectiveActionDefaults(Dictionary<string, object> action, Dictionary<string, object> payload, Dictionary<string, object> hero, string directiveText, bool includeTestValues = true)
        {
            string command = CanonicalCommand(ReadFirstString(action, "command", "commandType", "intent", "type"));
            if (string.IsNullOrWhiteSpace(command))
            {
                command = CommandFromDirectiveText(directiveText);
                if (string.IsNullOrWhiteSpace(command) && LooksLikeNamedSettlementTransferDirective(payload, directiveText))
                {
                    command = "demand_settlement_peace";
                }
            }

            if (!string.IsNullOrWhiteSpace(command))
            {
                action["command"] = command;
            }

            string playerKingdomId = FirstNonEmpty(
                ReadFirstString(payload, "playerKingdomId", "actorKingdomId", "actorKingdomStringId"),
                ReadString(hero, "playerKingdomId", ""));
            string speakerKingdomId = FirstNonEmpty(
                ReadFirstString(payload, "speakerKingdomId", "targetKingdomId", "targetKingdomStringId"),
                ReadString(hero, "kingdomId", ""));
            string playerClanId = FirstNonEmpty(
                ReadFirstString(payload, "playerClanId", "actorClanId", "actorClanStringId"),
                ReadString(hero, "playerClanId", ""));
            string speakerClanId = FirstNonEmpty(
                ReadFirstString(payload, "speakerClanId", "targetClanId", "targetClanStringId"),
                ReadString(hero, "clanId", ""));
            string speakerHeroId = FirstNonEmpty(
                ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ReadString(hero, "heroStringId", ""));
            string playerHeroId = FirstNonEmpty(
                ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"),
                ReadFirstString(hero, "playerHeroStringId", "mainHeroStringId"));

            // This command is spoken and performed by the NPC against the player.
            // The generic conversation defaults below are player-to-NPC diplomacy
            // defaults, so applying them here reverses the clans in the audit record
            // even though actorHeroId correctly names the NPC.
            bool npcJoinsPlayerClan = string.Equals(command, "join_clan", StringComparison.OrdinalIgnoreCase);
            bool npcClanJoinsPlayerKingdom = string.Equals(command, "join_kingdom", StringComparison.OrdinalIgnoreCase);
            bool npcClanLeavesKingdom = string.Equals(command, "leave_kingdom", StringComparison.OrdinalIgnoreCase);
            bool npcControlsPlayerService = string.Equals(command, "hire_player_as_mercenary", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "dismiss_player_mercenary", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "offer_player_vassalage", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "dismiss_player_vassal", StringComparison.OrdinalIgnoreCase);
            bool npcTargetsPlayer = string.Equals(command, "attack_player_party", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "duel_player", StringComparison.OrdinalIgnoreCase);
            if (npcControlsPlayerService)
            {
                if (!string.IsNullOrWhiteSpace(speakerHeroId)) action["actorHeroId"] = speakerHeroId;
                if (!string.IsNullOrWhiteSpace(speakerClanId)) action["actorClanId"] = speakerClanId;
                if (!string.IsNullOrWhiteSpace(speakerKingdomId))
                {
                    action["actorKingdomId"] = speakerKingdomId;
                    action["targetKingdomId"] = speakerKingdomId;
                }
                if (!string.IsNullOrWhiteSpace(playerClanId)) action["targetClanId"] = playerClanId;
            }
            else if (npcJoinsPlayerClan || npcClanJoinsPlayerKingdom || npcClanLeavesKingdom)
            {
                if (!string.IsNullOrWhiteSpace(speakerHeroId)) action["actorHeroId"] = speakerHeroId;
                if (!string.IsNullOrWhiteSpace(speakerClanId)) action["actorClanId"] = speakerClanId;
                if (npcJoinsPlayerClan && !string.IsNullOrWhiteSpace(playerClanId))
                    action["targetClanId"] = playerClanId;
                else if ((npcClanJoinsPlayerKingdom || npcClanLeavesKingdom) && !string.IsNullOrWhiteSpace(speakerClanId))
                    action["targetClanId"] = speakerClanId;
                if (!string.IsNullOrWhiteSpace(speakerKingdomId)) action["actorKingdomId"] = speakerKingdomId;
                else action.Remove("actorKingdomId");
                if (!npcClanLeavesKingdom && !string.IsNullOrWhiteSpace(playerKingdomId))
                    action["targetKingdomId"] = playerKingdomId;
                else
                {
                    action.Remove("targetKingdomId");
                    action.Remove("targetKingdomStringId");
                    action.Remove("TargetKingdom");
                }
            }
            else if (npcTargetsPlayer)
            {
                string requestedActor = ReadFirstString(action, "actorHeroId", "ActorHero", "FromHero");
                bool actorIsCurrentSpeaker = string.Equals(command, "duel_player", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(requestedActor)
                    || requestedActor.Equals("speaker", StringComparison.OrdinalIgnoreCase)
                    || requestedActor.Equals("npc", StringComparison.OrdinalIgnoreCase)
                    || requestedActor.Equals(speakerHeroId, StringComparison.OrdinalIgnoreCase);
                if (actorIsCurrentSpeaker && !string.IsNullOrWhiteSpace(speakerHeroId))
                    action["actorHeroId"] = speakerHeroId;
                if (!string.IsNullOrWhiteSpace(playerHeroId)) action["TargetHero"] = playerHeroId;
                if (!string.IsNullOrWhiteSpace(playerClanId)) action["targetClanId"] = playerClanId;
                if (actorIsCurrentSpeaker)
                {
                    if (!string.IsNullOrWhiteSpace(speakerClanId)) action["actorClanId"] = speakerClanId;
                    if (!string.IsNullOrWhiteSpace(speakerKingdomId)) action["actorKingdomId"] = speakerKingdomId;
                    else action.Remove("actorKingdomId");
                }
                else
                {
                    action.Remove("actorClanId");
                    action.Remove("actorKingdomId");
                }
                if (!string.IsNullOrWhiteSpace(playerKingdomId)) action["targetKingdomId"] = playerKingdomId;
                else action.Remove("targetKingdomId");
            }
            else
            {
                AddIfMissing(action, "actorKingdomId", playerKingdomId);
                AddIfMissing(action, "targetKingdomId", speakerKingdomId);
                AddIfMissing(action, "actorClanId", command == "encourage_clan_defection" || command == "exile_clan" || command == "restore_exiled_clan" || command == "support_claimant" || command == "mediate_clan_dispute" ? speakerClanId : playerClanId);
                AddIfMissing(action, "targetClanId", speakerClanId);
            }

            if (string.Equals(command, "start_ruling_clan_rebellion", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "recruit_lord_to_rebellion", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "join_rebellion", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "surrender_rebellion", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "consent_government_reduction", StringComparison.OrdinalIgnoreCase))
            {
                AddIfMissing(action, "actorHeroId", playerHeroId);
                AddIfMissing(action, "TargetHero", speakerHeroId);
            }
            else if (string.Equals(command, "duel_player", StringComparison.OrdinalIgnoreCase))
            {
                action["actorHeroId"] = speakerHeroId;
                action["TargetHero"] = playerHeroId;
            }
            else if (PromptService.ActionFamilyForId(command) == "strategy" || PromptService.ActionFamilyForId(command) == "regular")
            {
                AddIfMissing(action, "actorHeroId", speakerHeroId);
            }

            bool canUseContextSettlement = CanInferSettlementFromText(command)
                && (includeTestValues || DialogueService.HasExplicitCurrentSettlementReference(directiveText));
            string mentionedSettlement = CanInferSettlementFromText(command)
                && (command != "trade_package"
                    || LooksLikeSettlementTransferDirective(directiveText)
                    || LooksLikeNamedPaidSettlementTradeDirective(payload, directiveText))
                ? FindMentionedSettlementName(payload, directiveText, "") : "";
            string settlementId = string.Empty;
            if (!string.IsNullOrWhiteSpace(mentionedSettlement))
            {
                AddIfMissing(action, "TargetSettlement", mentionedSettlement);
            }
            else if (canUseContextSettlement)
            {
                settlementId = FirstNonEmpty(
                    ReadFirstString(payload, "targetSettlementId", "targetSettlementStringId", "settlementId"),
                    ReadString(hero, "currentSettlementId", ""),
                    FindSettlementIdFromContextBundles(ReadDictionaryList(payload, "contextBundles"), speakerKingdomId, "current_or_target"));
            }

            if (!string.IsNullOrWhiteSpace(settlementId))
            {
                AddIfMissing(action, "targetSettlementId", settlementId);
            }

            if (command == "supply_agreement" && LooksLikeNavalAssetDirective(directiveText) && !string.IsNullOrWhiteSpace(playerKingdomId) && !string.IsNullOrWhiteSpace(speakerKingdomId))
            {
                action["actorKingdomId"] = speakerKingdomId;
                action["targetKingdomId"] = playerKingdomId;
                AddIfMissing(action, "actorHeroId", speakerHeroId);
                AddIfMissing(action, "TargetHero", playerHeroId);
            }

            Dictionary<string, object> terms = ReadDictionary(action, "terms") ?? new Dictionary<string, object>();
            if (!includeTestValues)
            {
                action["terms"] = terms;
                return;
            }
            switch (command)
            {
                case "offer_tribute_peace":
                    AddIfMissing(terms, "dailyTribute", 150);
                    AddIfMissing(terms, "durationDays", 30);
                    break;
                case "demand_reparations_peace":
                    AddIfMissing(terms, "reparationsGold", 15000);
                    AddIfMissing(terms, "dailyTribute", 200);
                    AddIfMissing(terms, "durationDays", 30);
                    break;
                case "demand_settlement_peace":
                case "demand_surrender_peace":
                    if (!string.IsNullOrWhiteSpace(settlementId) && ReadStringList(terms, "settlementIds").Count == 0)
                    {
                        terms["settlementIds"] = new ArrayList { settlementId };
                    }
                    if (command == "demand_surrender_peace")
                    {
                        AddIfMissing(terms, "reparationsGold", 25000);
                        AddIfMissing(terms, "dailyTribute", 300);
                        AddIfMissing(terms, "durationDays", 45);
                    }
                    break;
                case "pay_to_join_war":
                    AddIfMissing(terms, "enemyKingdomId", FindThirdKingdomIdFromContextBundles(ReadDictionaryList(payload, "contextBundles"), playerKingdomId, speakerKingdomId));
                    AddIfMissing(terms, "gold", 25000);
                    break;
                case "war_indemnity":
                case "loan_or_subsidy":
                case "pay_to_stay_neutral":
                    AddIfMissing(terms, "gold", 15000);
                    break;
                case "sign_trade_agreement":
                case "sign_non_aggression_pact":
                case "sign_defensive_pact":
                case "supply_agreement":
                case "demilitarized_border":
                    if (command == "supply_agreement" && LooksLikeNavalAssetDirective(directiveText))
                    {
                        AddIfMissing(terms, "asset", "ships");
                        AddIfMissing(terms, "assetClass", "naval");
                        AddIfMissing(terms, "assetName", "ships");
                        AddIfMissing(terms, "allMatchingAssets", true);
                        AddIfMissing(terms, "mechanicalEffect", "ledger_only_pending_navaldlc_adapter");
                    }

                    AddIfMissing(terms, "durationDays", 90);
                    break;
                case "sign_alliance":
                case "guarantee_independence":
                case "protectorate_or_vassalage":
                    AddIfMissing(terms, "durationDays", 180);
                    break;
                case "duel_player":
                    AddIfMissing(terms, "duelMode", InferDuelModeFromDirective(directiveText));
                    string duelMode = ReadString(terms, "duelMode", "training");
                    if (IsLethalDuelMode(duelMode))
                    {
                        terms["lethal"] = true;
                    }
                    else
                    {
                        AddIfMissing(terms, "lethal", false);
                    }
                    break;
                case "trade_package":
                    ApplyPaidAssetPackageTerms(action, terms, payload, hero, directiveText);
                    break;
                case "ransom_package":
                    ApplyRansomPackageTerms(action, terms, payload, hero, directiveText);
                    break;
                case "diplomatic_package":
                    ApplyCompoundDiplomaticPackageTerms(action, terms, payload, hero, directiveText);
                    break;
                case "marriage_alliance":
                    ApplyMarriageDirectiveTerms(terms, payload, directiveText);
                    break;
                case "consent_government_reduction":
                    AddIfMissing(terms, "consentConfirmed", true);
                    break;
                case "transfer_gold":
                case "transfer_item":
                case "give_gold_to_player":
                    ApplyTransferDirectionDefaults(action, terms, payload, hero, directiveText, command);
                    break;
            }

            if (terms.Count > 0)
            {
                action["terms"] = terms;
            }

            if (string.IsNullOrWhiteSpace(ReadString(action, "source", "")))
            {
                action["source"] = "prompt_override";
            }
            action["requiresAcceptance"] = false;
            if (string.IsNullOrWhiteSpace(ReadString(action, "reason", "")))
            {
                action["reason"] = "Prompt override: " + directiveText;
            }
        }

        internal static void CompleteFallbackDirectiveTerms(Dictionary<string, object> action, Dictionary<string, object> payload, Dictionary<string, object> hero, string directiveText, bool includeTestValues = true)
        {
            if (action == null)
            {
                return;
            }

            string command = CanonicalCommand(ReadFirstString(action, "command", "commandType", "intent", "type"));
            Dictionary<string, object> terms = LlmProviderService.ToCaseInsensitiveDictionary(ReadDictionary(action, "terms") ?? new Dictionary<string, object>());
            string speakerHeroId = FirstNonEmpty(
                ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ReadString(hero, "heroStringId", ""));
            string playerHeroId = ReadString(payload, "playerHeroStringId", "");
            string playerKingdomId = FirstNonEmpty(
                ReadFirstString(payload, "playerKingdomId", "actorKingdomId", "actorKingdomStringId"),
                ReadString(hero, "playerKingdomId", ""));
            string speakerKingdomId = FirstNonEmpty(
                ReadFirstString(payload, "speakerKingdomId", "targetKingdomId", "targetKingdomStringId"),
                ReadString(hero, "kingdomId", ""));

            switch (command)
            {
                case "demand_reparations_peace":
                    int reparationsGold = ExtractMoneyAmount(directiveText, includeTestValues ? ExtractFirstInteger(directiveText, 0) : 0);
                    if (reparationsGold > 0)
                    {
                        AddIfMissing(terms, "reparationsGold", reparationsGold);
                    }

                    if (includeTestValues) AddIfMissing(terms, "durationDays", 30);
                    break;
                case "give_gold_to_player":
                    if (includeTestValues || DialogueService.TransferDirectionNeedsResolution(action, terms)) ApplyTransferDirectionDefaults(action, terms, payload, hero, directiveText, command);
                    if (!includeTestValues && (ReadFirstString(action, "GoldAmount", "gold").Length > 0
                        || ReadFirstString(terms, "GoldAmount", "gold").Length > 0)) break;
                    int giftGold = ExtractMoneyAmount(directiveText, includeTestValues ? ExtractFirstInteger(directiveText, 1000) : 0);
                    if (giftGold <= 0) break;
                    AddIfMissing(action, "GoldAmount", giftGold);
                    AddIfMissing(terms, "gold", giftGold);
                    AddIfMissing(terms, "GoldAmount", giftGold);
                    break;
                case "transfer_gold":
                    if (includeTestValues || DialogueService.TransferDirectionNeedsResolution(action, terms)) ApplyTransferDirectionDefaults(action, terms, payload, hero, directiveText, command);
                    if (!includeTestValues && (ReadFirstString(action, "GoldAmount", "gold").Length > 0
                        || ReadFirstString(terms, "GoldAmount", "gold").Length > 0)) break;
                    int transferGold = ExtractMoneyAmount(directiveText, includeTestValues ? ExtractFirstInteger(directiveText, 1000) : 0);
                    if (transferGold <= 0) break;
                    AddIfMissing(action, "GoldAmount", transferGold);
                    AddIfMissing(terms, "gold", transferGold);
                    AddIfMissing(terms, "GoldAmount", transferGold);
                    break;
                case "transfer_item":
                    if (includeTestValues || DialogueService.TransferDirectionNeedsResolution(action, terms)) ApplyTransferDirectionDefaults(action, terms, payload, hero, directiveText, command);
                    string item = FirstNonEmpty(ReadFirstString(action, "Item", "itemId"),
                        ReadFirstString(terms, "itemId", "item", "Item"), ExtractDirectiveItemName(directiveText));
                    AddIfMissing(action, "Item", item);
                    AddIfMissing(terms, "item", item);
                    int itemAmount = ReadInt(action, "Amount", 0);
                    if (itemAmount <= 0)
                    {
                        itemAmount = ExtractInventoryItemAmount(directiveText, item);
                    }
                    if (itemAmount <= 0
                        && Regex.IsMatch(item, @"^(?:armor|armour|helmet|sword|shield|cloak|boots|gloves|bow|spear|axe)$", RegexOptions.IgnoreCase)
                        && Regex.IsMatch(directiveText,
                            @"\b(?:my|your|his|her|the|this|that|a|an)\s+" + Regex.Escape(item) + @"\b",
                            RegexOptions.IgnoreCase))
                    {
                        itemAmount = 1;
                    }
                    if (itemAmount > 0 || includeTestValues)
                        AddIfMissing(terms, "amount", itemAmount > 0 ? itemAmount : ExtractFirstInteger(directiveText, 1));
                    break;
                case "follow":
                    if (DirectiveHasAny(directiveText, "follow me", "follow us", "follow my party", "escort me", "escort my party", "come with me"))
                    {
                        AddIfMissing(action, "TargetParty", "player party");
                        AddIfMissing(terms, "targetParty", "player party");
                    }
                    break;
                case "trade_package":
                    ApplyPaidAssetPackageTerms(action, terms, payload, hero, directiveText);
                    break;
                case "ransom_package":
                    ApplyRansomPackageTerms(action, terms, payload, hero, directiveText);
                    break;
                case "diplomatic_package":
                    ApplyCompoundDiplomaticPackageTerms(action, terms, payload, hero, directiveText);
                    break;
                case "marriage_alliance":
                    ApplyMarriageDirectiveTerms(terms, payload, directiveText);
                    break;
                case "supply_agreement":
                    if (LooksLikeNavalAssetDirective(directiveText))
                    {
                        if (!string.IsNullOrWhiteSpace(speakerKingdomId))
                        {
                            action["actorKingdomId"] = speakerKingdomId;
                        }

                        if (!string.IsNullOrWhiteSpace(playerKingdomId))
                        {
                            action["targetKingdomId"] = playerKingdomId;
                        }

                        AddIfMissing(action, "actorHeroId", speakerHeroId);
                        AddIfMissing(action, "TargetHero", playerHeroId);
                        AddIfMissing(terms, "asset", "ships");
                        AddIfMissing(terms, "assetClass", "naval");
                        AddIfMissing(terms, "assetName", "ships");
                        AddIfMissing(terms, "allMatchingAssets", true);
                        AddIfMissing(terms, "mechanicalEffect", "ledger_only_pending_navaldlc_adapter");
                    }
                    break;
            }

            if (terms.Count > 0)
            {
                action["terms"] = terms;
            }
        }

        internal static string CommandFromDirectiveText(string text)
        {
            string q = (text ?? string.Empty).ToLowerInvariant();
            if (LooksLikeGovernmentReductionConsentRequest(q)) return "consent_government_reduction";
            if (LooksLikeFullSurrenderDirective(q)) return "demand_surrender_peace";
            if (LooksLikeReparationsPeaceDirective(q)) return "demand_reparations_peace";
            if (!LooksLikeGiveGoldDirective(q) && !DirectiveHasAny(q, "gold", "denar", "denars", "coin", "coins", "money", "silver") && LooksLikeSettlementForPeaceDirective(q)) return "demand_settlement_peace";
            if (LooksLikeCompoundPoliticalPackageDirective(q)) return "diplomatic_package";
            if (q.Contains("trade agreement")) return "sign_trade_agreement";
            if (q.Contains("non aggression") || q.Contains("non-aggression")) return "sign_non_aggression_pact";
            if (q.Contains("defensive pact")) return "sign_defensive_pact";
            if (q.Contains("marriage") || q.Contains("marry") || q.Contains("betroth")) return "marriage_alliance";
            if (q.Contains("alliance") || Regex.IsMatch(q, @"\ball(?:y|ies|ied)\b", RegexOptions.IgnoreCase)) return "sign_alliance";
            if (q.Contains("ransom")) return "ransom_package";
            if (LooksLikePaidAssetTradeDirective(q)) return "trade_package";
            if (LooksLikeNamedPaidExchangeDirective(q)) return "trade_package";
            if (q.Contains("break treaty") || q.Contains("end treaty") || q.Contains("cancel treaty")) return "break_treaty";
            if (q.Contains("exchange prisoner")) return "exchange_prisoners";
            if (q.Contains("hostage")) return "hostage_guarantee";
            if (q.Contains("indemnity")) return "war_indemnity";
            if (q.Contains("recognize") && (q.Contains("conquest") || q.Contains("claim"))) return "recognize_conquest";
            if (q.Contains("return") && q.Contains("settlement")) return "return_occupied_settlement";
            if (q.Contains("demilitarized") || q.Contains("demilitarised")) return "demilitarized_border";
            if (LooksLikeNavalAssetDirective(q)) return "trade_package";
            if (LooksLikeSettlementTransferDirective(q)) return "demand_settlement_peace";
            if (q.Contains("supply")) return "supply_agreement";
            if (q.Contains("loan") || q.Contains("subsidy")) return "loan_or_subsidy";
            if (q.Contains("stay neutral") || q.Contains("neutrality")) return "pay_to_stay_neutral";
            if (q.Contains("join war")) return "pay_to_join_war";
            if (q.Contains("guarantee") && q.Contains("independence")) return "guarantee_independence";
            if (q.Contains("hire") && q.Contains("mercenary") && q.Contains("clan")) return "hire_mercenary_clan";
            if (q.Contains("hire") && q.Contains("mercenary")) return "hire_player_as_mercenary";
            if (q.Contains("dismiss") && q.Contains("mercenary")) return "dismiss_player_mercenary";
            if (q.Contains("offer") && q.Contains("vassal")) return "offer_player_vassalage";
            if (q.Contains("dismiss") && q.Contains("vassal")) return "dismiss_player_vassal";
            if (q.Contains("join kingdom") || q.Contains("join my kingdom") || q.Contains("join our kingdom")) return "join_kingdom";
            if (q.Contains("leave kingdom")) return "leave_kingdom";
            if (q.Contains("join clan")) return "join_clan";
            if (q.Contains("leave clan")) return "leave_clan";
            if (q.Contains("protectorate") || q.Contains("vassalage arrangement")) return "protectorate_or_vassalage";
            if (q.Contains("stop following") || q.Contains("break off the escort") || q.Contains("go your own way") || q.Contains("no need to trail") || q.Contains("trail us any longer")) return "stop_following";
            if (q.Contains("follow on map") || q.Contains("follow in scene") || q.Contains("follow me on the map") || q.Contains("follow my party") || q.Contains("follow our party") || q.Contains("follow me") || q.Contains("follow us") || q.Contains("escort me") || q.Contains("escort us") || q.Contains("escort my party") || q.Contains("come with me") || q.Contains("walk with me") || q.Contains("stay by my side") || q.Contains("stay close") || q.Contains("walk after me") || q.Contains("stay behind our party") || q.Contains("keep to our route")) return "follow";
            if (DirectiveHasAny(q, "go to", "head to", "head for", "travel to", "ride to", "march to", "move to", "proceed to", "return to", "make for", "take your party to", "bring your warband to")) return "go_to_settlement";
            if (DirectiveHasAny(q, "patrol", "circle", "make a circuit", "watch the approaches", "watch its approaches")) return "patrol_around_settlement";
            if (DirectiveHasAny(q, "wait near", "wait outside", "hold near", "hold your position near", "remain near", "remain in the vicinity of")) return "wait_near_settlement";
            if (q.Contains("raid") && q.Contains("village")) return "raid_village";
            if (q.Contains("besiege")) return "besiege_settlement";
            if (q.Contains("create party") || q.Contains("make a party")) return "create_party";
            if (q.Contains("show the way")) return "show_the_way";
            if (q.Contains("attack player party") || q.Contains("attack my party")) return "attack_player_party";
            if (q.Contains("attack party")) return "attack_party";
            if (q.Contains("surrender to player") || q.Contains("surrender to me")) return "surrender_to_player";
            if (q.Contains("leave player alone") || q.Contains("leave me alone")) return "leave_player_alone";
            if (q.Contains("kill")) return "kill_character";
            if (q.Contains("duel")) return "duel_player";
            if (q.Contains("reparation")) return "demand_reparations_peace";
            if (q.Contains("transfer gold") || q.Contains("transfer denars")) return "transfer_gold";
            if (LooksLikeGiveGoldDirective(q)) return LooksLikePlayerToSpeakerTransfer(q) ? "transfer_gold" : "give_gold_to_player";
            if (q.Contains("transfer workshop") || q.Contains("give workshop")) return "transfer_workshop";
            if (q.Contains("transfer prisoner") || q.Contains("release prisoner")) return "transfer_prisoner";
            if (LooksLikeGiveItemDirective(q)) return "transfer_item";
            if (q.Contains("declare war")) return "declare_war";
            if (q.Contains("full surrender") || q.Contains("surrender terms")) return "demand_surrender_peace";
            if (q.Contains("settlement") && (q.Contains("peace") || q.Contains("surrender"))) return "demand_settlement_peace";
            if (q.Contains("tribute")) return "offer_tribute_peace";
            if (q.Contains("peace")) return "make_peace";
            if (q.Contains("promise")) return "record_promise";
            if (q.Contains("recruit") || q.Contains("recover")) return "recruit_and_recover";
            if (q.Contains("form army") || q.Contains("build an army")) return "form_army";
            if (q.Contains("capture")) return "capture_settlement_plan";
            if (q.Contains("attack") || q.Contains("besiege")) return "attack_settlement";
            if ((q.Contains("join my rebellion") || q.Contains("join our rebellion") || q.Contains("support my rebellion"))) return "recruit_lord_to_rebellion";
            if (q.Contains("join your rebellion") || q.Contains("fight for your rebellion") || q.Contains("join the rebels")) return "join_rebellion";
            if (q.Contains("surrender the rebellion") || q.Contains("yield the rebellion") || q.Contains("rebellion surrender")) return "surrender_rebellion";
            if (q.Contains("rebellion") || q.Contains("revolt")) return "start_ruling_clan_rebellion";
            if (q.Contains("install") && q.Contains("ruling")) return "install_ruling_clan";
            if (q.Contains("claimant")) return "support_claimant";
            if (q.Contains("defect") || q.Contains("defection")) return "encourage_clan_defection";
            if (q.Contains("restore") && q.Contains("exile")) return "restore_exiled_clan";
            if (q.Contains("exile")) return "exile_clan";
            if (q.Contains("mediate")) return "mediate_clan_dispute";
            return string.Empty;
        }

        private static bool LooksLikeNamedPaidExchangeDirective(string text)
        {
            string q = (text ?? string.Empty).ToLowerInvariant();
            bool hasMoney = DirectiveHasAny(q, "gold", "denar", "denars", "coin", "coins", "money", "silver");
            bool hasExchangeShape = DirectiveHasAny(q, " for ", "in exchange", "buy", "purchase", "sell", "sold", "price");
            bool treatyLike = DirectiveHasAny(q, "for peace", "peace treaty", "trade agreement", "alliance", "tribute", "reparations", "indemnity");
            return hasMoney && hasExchangeShape && !treatyLike;
        }

        private static bool LooksLikeCompoundPoliticalPackageDirective(string text)
        {
            string q = (text ?? string.Empty).ToLowerInvariant();
            bool hasPoliticalTerm = DirectiveHasAny(q,
                "marriage", "marry", "betroth", "dowry", "family member", "clan alliance",
                "alliance package", "non-aggression", "non aggression", "recognition", "treaty package",
                "diplomatic package", "public alliance", "public non-aggression", "protectorate", "neutrality");
            bool hasExchangeShape = DirectiveHasAny(q, " in exchange", "exchange for", " as dowry", "dowry", " package", " bargain", " price for", " for a public", " for the release");
            bool hasAssetTerm = DirectiveHasAny(q, "gold", "denar", "denars", "coin", "coins", "money", "grain", "horses", "horse", "supplies", "prisoner", "captive", "hostage", "settlement", "town", "castle", "fief");
            return hasPoliticalTerm && hasExchangeShape && hasAssetTerm;
        }

        private static bool DirectiveHasAny(string text, params string[] phrases)
        {
            foreach (string phrase in phrases ?? new string[0])
            {
                if (!string.IsNullOrWhiteSpace(phrase) && text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool LooksLikeGiveGoldDirective(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            // Bannerlord exposes Silver as a transferable inventory good. Treat currency
            // as denars/gold/coins here so requests such as "five units of Silver" are
            // not collapsed into a gold transfer.
            bool hasMoney = DirectiveHasAny(text, "gold", "denar", "denars", "coin", "coins", "money");
            bool hasGiveVerb = DirectiveHasAny(text, "give", "pay", "hand", "transfer", "send", "grant", "gift", "offer");
            return hasMoney && hasGiveVerb;
        }

        internal static bool LooksLikeGiveItemDirective(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (LooksLikeSettlementTransferDirective(text) || LooksLikeNavalAssetDirective(text))
            {
                return false;
            }

            bool hasGiveVerb = DirectiveHasAny(text, "give", "hand", "transfer", "send", "grant", "take", "offer", "provide", "gift", "donate");
            bool hasItemWord = DirectiveHasAny(text,
                "item", "armor", "armour", "helmet", "boots", "gloves", "cloak", "cape", "weapon", "sword", "axe", "spear", "bow", "horse", "mount", "shield",
                "grain", "food", "supplies", "supply", "goods", "tools", "hardwood", "iron", "silver", "linen", "wool", "velvet", "pottery", "leather", "fur", "jewelry",
                "beer", "wine", "oil", "dates", "fish", "meat", "butter", "cheese");
            return hasGiveVerb && hasItemWord;
        }

        private static bool LooksLikeSettlementTransferDirective(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (DirectiveHasAny(text, "full surrender", "capitulation", "all towns", "all castles"))
            {
                return false;
            }

            bool hasTransferVerb = DirectiveHasAny(text, "give", "grant", "hand over", "transfer", "cede", "surrender", "yield", "turn over", "award");
            bool hasSettlementWord = DirectiveHasAny(text, "settlement", "city", "town", "castle", "fortress", "keep", "fief");
            return hasTransferVerb && hasSettlementWord;
        }

        private static bool LooksLikeNavalAssetDirective(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            bool hasTransferVerb = DirectiveHasAny(text, "give", "grant", "hand over", "transfer", "cede", "surrender", "loan", "lend", "provide", "assign", "offer", "sell", "trade", "buy", "purchase", "gift");
            return hasTransferVerb && HasNavalAssetWord(text);
        }

        private static bool HasNavalAssetWord(string text)
        {
            return Regex.IsMatch(text ?? string.Empty, @"\b(?:ship|ships|fleet|flagship|vessel|vessels|boat|boats|naval|war\s+sails|warsails)\b", RegexOptions.IgnoreCase);
        }

        private static bool LooksLikePaidAssetTradeDirective(string text)
        {
            string q = (text ?? string.Empty).ToLowerInvariant();
            bool hasMoney = DirectiveHasAny(q, "gold", "denar", "denars", "coin", "coins", "money", "silver", "price");
            bool hasTradeShape = Regex.IsMatch(q, @"\bfor\b", RegexOptions.IgnoreCase) || DirectiveHasAny(q, "price", "trade", "sell", "sold", "buy", "purchase", "deal", "bargain", "in exchange", "pay", "payment", "transfer");
            bool hasAsset = HasNavalAssetWord(q) || DirectiveHasAny(q, "grain", "food", "supplies", "goods", "planks", "felt", "meat", "hides", "tools", "iron", "hardwood", "charcoal", "steel", "horses", "mount", "armor", "armour", "helmet", "shield", "weapon", "sword", "bow", "prisoner", "settlement", "town", "city", "castle", "fief", "workshop");
            return hasMoney && hasTradeShape && hasAsset;
        }

        private static bool LooksLikePlayerToSpeakerTransfer(string text)
        {
            string q = (text ?? string.Empty).ToLowerInvariant();
            return LooksLikePlayerAssetToSpeaker(q)
                || DirectiveHasAny(q, "i pay you", "from me to you");
        }

        private static bool LooksLikeSpeakerToPlayerTransfer(string text)
        {
            string q = (text ?? string.Empty).ToLowerInvariant();
            return LooksLikeSpeakerAssetToPlayer(q)
                || DirectiveHasAny(q, "give me", "give us", "hand me", "hand over to me", "transfer to me", "send me", "grant me", "provide me");
        }

        private static bool LooksLikePlayerAssetToSpeaker(string text)
        {
            string q = (text ?? string.Empty).ToLowerInvariant();
            if (DirectiveHasAny(q,
                    "here take", "here are", "here is", "here's", "they are yours", "take my", "take this", "i give you", "i'll give you", "i will give you",
                    "give them to you", "i offer you", "i send you", "i sell you", "i will sell you", "i'll sell you", "i sold you", "buy my", "purchase my", "from me to you",
                    "my grain", "my goods", "my supplies", "my ships", "my fleet"))
            {
                return true;
            }

            return Regex.IsMatch(q, @"\bi\s+want\s+\d[\d,]*\s*(denars?|gold|coins?|silver|money)\s+for\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(q, @"\bfor\s+\d[\d,]*\s*(denars?|gold|coins?|silver|money)\b", RegexOptions.IgnoreCase) && DirectiveHasAny(q, "my ships", "my fleet", "my grain", "my goods", "my supplies")
                || Regex.IsMatch(q, @"\b(?:take|receive|accept)\b.{0,160}\bfrom\s+me\b", RegexOptions.IgnoreCase);
        }

        private static bool LooksLikeSpeakerAssetToPlayer(string text)
        {
            string q = (text ?? string.Empty).ToLowerInvariant();
            return DirectiveHasAny(q,
                "sell me", "offer me", "your armor", "your armour", "your sword", "your weapon", "your horse", "your shield", "your grain", "your goods", "your ships", "your fleet")
                || Regex.IsMatch(q, @"\bi\s+(?:will\s+)?(?:pay|give)\s+you\s+\d[\d,]*\s*(denars?|gold|coins?|silver|money)\s+for\s+your\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(q, @"\bi\s+want\s+your\b", RegexOptions.IgnoreCase);
        }

        internal static int ExtractMoneyAmount(string text, int fallback)
        {
            string q = text ?? string.Empty;
            Match amountThenCurrency = Regex.Match(q, @"\b(\d[\d,]*)\s*(denars?|gold|coins?|silver|money)\b", RegexOptions.IgnoreCase);
            if (amountThenCurrency.Success)
            {
                string cleaned = amountThenCurrency.Groups[1].Value.Replace(",", "");
                if (int.TryParse(cleaned, out int value) && value > 0)
                {
                    return value;
                }
            }

            Match currencyThenAmount = Regex.Match(q, @"\b(denars?|gold|coins?|silver|money)\s*(?:of|for|worth)?\s*(\d[\d,]*)\b", RegexOptions.IgnoreCase);
            if (currencyThenAmount.Success)
            {
                string cleaned = currencyThenAmount.Groups[2].Value.Replace(",", "");
                if (int.TryParse(cleaned, out int value) && value > 0)
                {
                    return value;
                }
            }

            string numberWords = @"(?:a|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred|thousand|million|and)";
            Match wordsThenCurrency = Regex.Match(q, @"\b(" + numberWords + @"(?:[\s-]+" + numberWords + @")*)\s+(?:denars?|gold|coins?|silver|money)\b", RegexOptions.IgnoreCase);
            if (wordsThenCurrency.Success && TryParseEnglishNumber(wordsThenCurrency.Groups[1].Value, out int spokenValue) && spokenValue > 0)
            {
                return spokenValue;
            }

            Match currencyThenWords = Regex.Match(q, @"\b(?:denars?|gold|coins?|silver|money)\s*(?:of|for|worth)?\s+(" + numberWords + @"(?:[\s-]+" + numberWords + @")*)\b", RegexOptions.IgnoreCase);
            if (currencyThenWords.Success && TryParseEnglishNumber(currencyThenWords.Groups[1].Value, out spokenValue) && spokenValue > 0)
            {
                return spokenValue;
            }

            return fallback;
        }

        private static bool TryParseEnglishNumber(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            Dictionary<string, int> small = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["a"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
                ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
                ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15,
                ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
                ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
                ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90
            };
            long total = 0;
            long current = 0;
            bool sawNumber = false;
            foreach (string rawToken in Regex.Split(text.ToLowerInvariant().Replace('-', ' '), @"\s+"))
            {
                string token = rawToken.Trim();
                if (string.IsNullOrWhiteSpace(token) || token == "and")
                {
                    continue;
                }

                if (small.TryGetValue(token, out int smallValue))
                {
                    current += smallValue;
                    sawNumber = true;
                    continue;
                }

                if (token == "hundred")
                {
                    current = Math.Max(1, current) * 100;
                    sawNumber = true;
                    continue;
                }

                if (token == "thousand" || token == "million")
                {
                    long scale = token == "thousand" ? 1000L : 1000000L;
                    total += Math.Max(1, current) * scale;
                    current = 0;
                    sawNumber = true;
                    continue;
                }

                return false;
            }

            long parsed = total + current;
            if (!sawNumber || parsed <= 0 || parsed > int.MaxValue)
            {
                return false;
            }

            value = (int)parsed;
            return true;
        }
    }
}
