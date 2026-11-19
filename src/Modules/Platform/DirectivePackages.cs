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
        private static void ApplyPaidAssetPackageTerms(Dictionary<string, object> action, Dictionary<string, object> terms, Dictionary<string, object> payload, Dictionary<string, object> hero, string directiveText)
        {
            if (action == null || terms == null)
            {
                return;
            }

            string directive = LatestPlayerDirectiveOrText(directiveText);
            bool latestHasConcreteAsset = LooksLikePaidAssetTradeDirective(directive)
                || LooksLikeNamedPaidExchangeDirective(directive)
                || LooksLikeNavalAssetDirective(directive)
                || LooksLikeSettlementTransferDirective(directive)
                || LooksLikeGiveItemDirective(directive)
                || DirectiveHasAny(directive, "ransom", "prisoner", "workshop");
            if (!latestHasConcreteAsset && !string.Equals(directive, directiveText, StringComparison.Ordinal))
            {
                directive = directiveText;
            }

            string q = directive.ToLowerInvariant();
            bool isValuableAssetPackage = LooksLikePaidAssetTradeDirective(q)
                || LooksLikeNamedPaidExchangeDirective(q)
                || LooksLikeNavalAssetDirective(q)
                || LooksLikeSettlementTransferDirective(q)
                || LooksLikeGiveItemDirective(q)
                || DirectiveHasAny(q, "ransom", "prisoner", "workshop");
            if (!isValuableAssetPackage)
            {
                return;
            }

            bool isNavalAssetPackage = HasNavalAssetWord(q)
                || DirectiveHasAny(ReadFirstString(terms, "asset", "assetClass", "assetName", "item", "Item"), "ship", "fleet", "vessel", "boat", "naval")
                || DirectiveHasAny(ReadFirstString(action, "asset", "assetClass", "assetName", "item", "Item"), "ship", "fleet", "vessel", "boat", "naval");
            bool isSettlementPackage = LooksLikeSettlementTransferDirective(q)
                || !string.IsNullOrWhiteSpace(ReadFirstString(action, "TargetSettlement", "targetSettlementId", "targetSettlementStringId"))
                || !string.IsNullOrWhiteSpace(ReadFirstString(terms, "TargetSettlement", "targetSettlementId", "targetSettlementStringId"));
            bool isPrisonerPackage = DirectiveHasAny(q, "ransom", "prisoner", "captive", "hostage")
                || !string.IsNullOrWhiteSpace(ReadFirstString(action, "PrisonerHero", "prisonerHero", "prisonerHeroStringId"))
                || !string.IsNullOrWhiteSpace(ReadFirstString(terms, "PrisonerHero", "prisonerHero", "prisonerHeroStringId"));
            bool isWorkshopPackage = DirectiveHasAny(q, "workshop")
                || !string.IsNullOrWhiteSpace(ReadFirstString(action, "Workshop", "workshop", "workshopId"))
                || !string.IsNullOrWhiteSpace(ReadFirstString(terms, "Workshop", "workshop", "workshopId"));

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
            bool playerToSpeaker = LooksLikePlayerAssetToSpeaker(q);
            bool speakerToPlayer = LooksLikeSpeakerAssetToPlayer(q);
            if (playerToSpeaker && speakerToPlayer)
            {
                speakerToPlayer = !DirectiveHasAny(q, "my ships", "my fleet", "my grain", "my goods", "my supplies", "they are yours", "give them to you");
                playerToSpeaker = !speakerToPlayer;
            }

            string assetFromHero = playerToSpeaker ? playerHeroId : speakerHeroId;
            string assetToHero = playerToSpeaker ? speakerHeroId : playerHeroId;
            string goldFromHero = playerToSpeaker ? speakerHeroId : playerHeroId;
            string goldToHero = playerToSpeaker ? playerHeroId : speakerHeroId;
            string actorKingdomId = playerToSpeaker ? playerKingdomId : speakerKingdomId;
            string targetKingdomId = playerToSpeaker ? speakerKingdomId : playerKingdomId;

            if (!string.IsNullOrWhiteSpace(actorKingdomId))
            {
                action["actorKingdomId"] = actorKingdomId;
            }

            if (!string.IsNullOrWhiteSpace(targetKingdomId))
            {
                action["targetKingdomId"] = targetKingdomId;
            }

            if (!string.IsNullOrWhiteSpace(assetFromHero))
            {
                action["actorHeroId"] = assetFromHero;
                action["FromHero"] = assetFromHero;
                terms["assetFromHeroStringId"] = assetFromHero;
                terms["itemFromHeroStringId"] = assetFromHero;
            }

            if (!string.IsNullOrWhiteSpace(assetToHero))
            {
                action["TargetHero"] = assetToHero;
                action["ToHero"] = assetToHero;
                terms["assetToHeroStringId"] = assetToHero;
                terms["itemToHeroStringId"] = assetToHero;
                terms["settlementToHeroStringId"] = assetToHero;
                terms["workshopToHeroStringId"] = assetToHero;
                terms["prisonerToHeroStringId"] = assetToHero;
            }

            if (!string.IsNullOrWhiteSpace(goldFromHero))
            {
                terms["goldFromHeroStringId"] = goldFromHero;
            }

            if (!string.IsNullOrWhiteSpace(goldToHero))
            {
                terms["goldToHeroStringId"] = goldToHero;
            }

            int gold = ExtractMoneyAmount(directive, 0);
            if (gold > 0)
            {
                AddIfMissing(action, "GoldAmount", gold);
                AddIfMissing(terms, "gold", gold);
                AddIfMissing(terms, "GoldAmount", gold);
            }

            if (HasNavalAssetWord(q))
            {
                AddIfMissing(terms, "asset", "ships");
                AddIfMissing(terms, "assetClass", "naval");
                AddIfMissing(terms, "assetName", "ships");
                AddIfMissing(terms, "allMatchingAssets", true);
            }

            if (isSettlementPackage)
            {
                string settlement = FirstNonEmpty(
                    ReadFirstString(action, "TargetSettlement", "targetSettlementStringId", "targetSettlementId"),
                    ReadFirstString(terms, "TargetSettlement", "targetSettlementStringId", "targetSettlementId"),
                    FindMentionedSettlementName(payload, directive, ""));
                if (!string.IsNullOrWhiteSpace(settlement))
                {
                    action["TargetSettlement"] = settlement;
                    terms["targetSettlementStringId"] = settlement;
                }
            }

            if (isPrisonerPackage)
            {
                string prisoner = FirstNonEmpty(
                    ReadFirstString(action, "PrisonerHero", "prisonerHero", "prisonerHeroStringId", "TargetHero"),
                    ReadFirstString(terms, "PrisonerHero", "prisonerHero", "prisonerHeroStringId"),
                    FindMentionedHeroName(payload, directive));
                if (string.IsNullOrWhiteSpace(prisoner) && DirectiveHasAny(q, "prisoner", "captive", "hostage"))
                {
                    prisoner = "prisoner";
                }

                if (!string.IsNullOrWhiteSpace(prisoner))
                {
                    action["PrisonerHero"] = prisoner;
                    terms["prisonerHeroStringId"] = prisoner;
                }
            }

            if (isWorkshopPackage)
            {
                string workshop = FirstNonEmpty(
                    ReadFirstString(action, "Workshop", "workshop", "workshopId"),
                    ReadFirstString(terms, "Workshop", "workshop", "workshopId"));
                if (!string.IsNullOrWhiteSpace(workshop))
                {
                    action["Workshop"] = workshop;
                    terms["workshop"] = workshop;
                }
            }

            if (!isNavalAssetPackage && !isSettlementPackage && !isPrisonerPackage && !isWorkshopPackage)
            {
                List<Dictionary<string, object>> itemTerms = BuildDirectiveItemTerms(directive, assetFromHero, assetToHero, payload);
                if (itemTerms.Count > 1)
                {
                    terms["items"] = itemTerms;
                }
                else
                {
                    Dictionary<string, object> itemTerm = itemTerms.FirstOrDefault();
                    string item = FirstNonEmpty(
                        ReadFirstString(action, "Item", "item", "itemId"),
                        ReadFirstString(terms, "Item", "item", "itemId"),
                        ReadString(itemTerm, "itemId", string.Empty),
                        ExtractDirectiveItemName(directive));
                    int amount = itemTerm == null ? ExtractNonMoneyAmount(directive, 1) : ReadInt(itemTerm, "amount", ExtractNonMoneyAmount(directive, 1));
                    AddIfMissing(action, "Item", item);
                    AddIfMissing(terms, "item", item);
                    AddIfMissing(terms, "amount", amount);
                }
            }

            AddIfMissing(terms, "packageText", DialogueService.LimitText(StripDirectiveMarkers(directive), 800));
        }

        private static void ApplyRansomPackageTerms(Dictionary<string, object> action, Dictionary<string, object> terms, Dictionary<string, object> payload, Dictionary<string, object> hero, string directiveText)
        {
            if (action == null || terms == null)
            {
                return;
            }

            string directive = LatestPlayerDirectiveOrText(directiveText);
            Dictionary<string, object> index = ReadDictionary(payload, "actionResolutionIndex") ?? new Dictionary<string, object>();
            string speakerHeroId = FirstNonEmpty(
                ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ReadString(hero, "heroStringId", ""));
            string playerHeroId = ReadString(payload, "playerHeroStringId", "");
            string speakerKingdomId = FirstNonEmpty(
                ReadFirstString(payload, "speakerKingdomId", "targetKingdomId", "targetKingdomStringId"),
                ReadString(hero, "kingdomId", ""),
                ResolveAlias(index, "kingdom", "speaker_kingdom"));
            string playerKingdomId = FirstNonEmpty(
                ReadFirstString(payload, "playerKingdomId", "actorKingdomId", "actorKingdomStringId"),
                ReadString(hero, "playerKingdomId", ""),
                ResolveAlias(index, "kingdom", "player_kingdom"));
            string speakerClanId = FirstNonEmpty(
                ReadFirstString(payload, "speakerClanId", "targetClanId", "targetClanStringId"),
                ReadString(hero, "clanId", ""));
            string playerClanId = FirstNonEmpty(
                ReadFirstString(payload, "playerClanId", "actorClanId", "actorClanStringId"),
                ReadString(hero, "playerClanId", ""));

            // Ransom semantics are actor=captor/payee and target=payer/recipient. Preserve
            // explicit manual/test direction, but repair planner defaults so clan and gold
            // direction agree with whichever conversation participant is the actor.
            string source = ReadString(action, "source", "");
            bool conversationPlanner = string.Equals(source, "hidden_action_planner", StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, "test_lab_action_gate", StringComparison.OrdinalIgnoreCase);
            bool autonomousWorldDiplomacy = string.Equals(source,
                "world_diplomacy_director", StringComparison.OrdinalIgnoreCase);
            if (conversationPlanner)
            {
                if (!string.IsNullOrWhiteSpace(speakerKingdomId)) action["actorKingdomId"] = speakerKingdomId;
                if (!string.IsNullOrWhiteSpace(playerKingdomId)) action["targetKingdomId"] = playerKingdomId;
            }

            string actorKingdomReference = ReadFirstString(action, "actorKingdomStringId", "actorKingdomId", "kingdomId");
            string targetKingdomReference = ReadFirstString(action, "targetKingdomStringId", "targetKingdomId", "TargetKingdom");
            if (string.IsNullOrWhiteSpace(actorKingdomReference) && !string.IsNullOrWhiteSpace(speakerKingdomId))
            {
                action["actorKingdomId"] = speakerKingdomId;
                actorKingdomReference = speakerKingdomId;
            }
            if (string.IsNullOrWhiteSpace(targetKingdomReference) && !string.IsNullOrWhiteSpace(playerKingdomId))
            {
                action["targetKingdomId"] = playerKingdomId;
                targetKingdomReference = playerKingdomId;
            }

            string resolvedActorKingdomId = FirstNonEmpty(
                ResolveAlias(index, "kingdom", actorKingdomReference),
                ResolveKingdomIdLenient(index, actorKingdomReference));
            string resolvedTargetKingdomId = FirstNonEmpty(
                ResolveAlias(index, "kingdom", targetKingdomReference),
                ResolveKingdomIdLenient(index, targetKingdomReference));
            bool speakerIsCaptor = string.Equals(resolvedActorKingdomId, speakerKingdomId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(resolvedTargetKingdomId, playerKingdomId, StringComparison.OrdinalIgnoreCase);
            bool playerIsCaptor = string.Equals(resolvedActorKingdomId, playerKingdomId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(resolvedTargetKingdomId, speakerKingdomId, StringComparison.OrdinalIgnoreCase);

            if (speakerIsCaptor)
            {
                if (!string.IsNullOrWhiteSpace(speakerClanId)) action["actorClanId"] = speakerClanId;
                if (!string.IsNullOrWhiteSpace(playerClanId)) action["targetClanId"] = playerClanId;
                if (!string.IsNullOrWhiteSpace(speakerHeroId)) action["actorHeroId"] = speakerHeroId;
            }
            else if (playerIsCaptor)
            {
                if (!string.IsNullOrWhiteSpace(playerClanId)) action["actorClanId"] = playerClanId;
                if (!string.IsNullOrWhiteSpace(speakerClanId)) action["targetClanId"] = speakerClanId;
                if (!string.IsNullOrWhiteSpace(playerHeroId)) action["actorHeroId"] = playerHeroId;
            }

            string prisoner = FirstNonEmpty(
                ReadFirstString(action, "PrisonerHero", "prisonerHero", "prisonerHeroStringId"),
                ReadFirstString(terms, "PrisonerHero", "prisonerHero", "prisonerHeroStringId"),
                autonomousWorldDiplomacy ? "" : FindMentionedHeroName(payload, directive));
            if (!string.IsNullOrWhiteSpace(prisoner))
            {
                action["PrisonerHero"] = prisoner;
                if (!autonomousWorldDiplomacy)
                    action["TargetHero"] = prisoner;
                terms["prisonerHero"] = prisoner;
                terms["prisonerHeroStringId"] = prisoner;
            }

            int gold = ExtractMoneyAmount(directive, ReadInt(terms, "gold", ReadInt(action, "GoldAmount", 0)));
            if (gold > 0)
            {
                action["GoldAmount"] = gold;
                terms["gold"] = gold;
                terms["GoldAmount"] = gold;
            }

            string payerHeroId = playerIsCaptor ? speakerHeroId : playerHeroId;
            string payeeHeroId = playerIsCaptor ? playerHeroId : speakerHeroId;
            if (!string.IsNullOrWhiteSpace(payerHeroId)) terms["goldFromHeroStringId"] = payerHeroId;
            if (!string.IsNullOrWhiteSpace(payeeHeroId)) terms["goldToHeroStringId"] = payeeHeroId;
            AddIfMissing(terms, "packageText", DialogueService.LimitText(StripDirectiveMarkers(directive), 800));
        }

        private static void ApplyCompoundDiplomaticPackageTerms(Dictionary<string, object> action, Dictionary<string, object> terms, Dictionary<string, object> payload, Dictionary<string, object> hero, string directiveText)
        {
            if (action == null || terms == null)
            {
                return;
            }

            string directive = LatestPlayerDirectiveOrText(directiveText);
            string q = directive.ToLowerInvariant();
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

            bool playerPays = DirectiveHasAny(q, "i will pay you", "i pay you", "i give you", "i will give you", "from my stores", "from my party", "as dowry", "my clan")
                || LooksLikePlayerToSpeakerTransfer(q);
            string fromHeroId = playerPays ? playerHeroId : speakerHeroId;
            string toHeroId = playerPays ? speakerHeroId : playerHeroId;

            AddIfMissing(action, "actorKingdomId", playerKingdomId);
            AddIfMissing(action, "targetKingdomId", speakerKingdomId);
            AddIfMissing(terms, "fromHeroStringId", fromHeroId);
            AddIfMissing(terms, "toHeroStringId", toHeroId);

            int gold = ExtractMoneyAmount(directive, 0);
            if (gold > 0)
            {
                AddIfMissing(action, "GoldAmount", gold);
                AddIfMissing(terms, "gold", gold);
                AddIfMissing(terms, "GoldAmount", gold);
            }

            List<Dictionary<string, object>> itemTerms = BuildDirectiveItemTerms(directive, fromHeroId, toHeroId, payload);
            if (itemTerms.Count > 0)
            {
                terms["items"] = itemTerms;
                Dictionary<string, object> firstItem = itemTerms[0];
                AddIfMissing(action, "Item", ReadString(firstItem, "itemId", string.Empty));
                AddIfMissing(action, "Amount", ReadInt(firstItem, "amount", 1));
                AddIfMissing(terms, "item", ReadString(firstItem, "itemId", string.Empty));
                AddIfMissing(terms, "amount", ReadInt(firstItem, "amount", 1));
            }

            if (DirectiveHasAny(q, "marriage", "marry", "betroth", "dowry"))
            {
                AddIfMissing(terms, "treatyKind", "marriage_alliance_dowry");
                AddIfMissing(terms, "promiseText", "Marriage alliance backed by the material dowry in packageText.");
                ApplyMarriageDirectiveTerms(terms, payload, directive);
            }
            else if (DirectiveHasAny(q, "alliance"))
            {
                AddIfMissing(terms, "treatyKind", "alliance_resource_package");
            }
            else if (DirectiveHasAny(q, "non-aggression", "non aggression"))
            {
                AddIfMissing(terms, "treatyKind", "non_aggression_package");
            }

            AddIfMissing(terms, "packageText", DialogueService.LimitText(StripDirectiveMarkers(directive), 800));
        }

        private static void ApplyMarriageDirectiveTerms(Dictionary<string, object> terms, Dictionary<string, object> payload, string directiveText)
        {
            if (terms == null)
            {
                return;
            }

            string directive = LatestPlayerDirectiveOrText(directiveText);
            AddIfMissing(terms, "marriageRequestText", DialogueService.LimitText(StripDirectiveMarkers(directive), 800));
            AddIfMissing(terms, "packageText", DialogueService.LimitText(StripDirectiveMarkers(directive), 800));
            Dictionary<string, object> namedHero = FindMentionedMarriageHero(payload, directiveText);
            if (namedHero.Count > 0)
            {
                AddIfMissing(terms, "requestedMarriageHeroStringId", ReadString(namedHero, "heroStringId", string.Empty));
                AddIfMissing(terms, "requestedMarriageHero", ReadString(namedHero, "name", ReadString(namedHero, "heroStringId", string.Empty)));
            }
        }

        private static int ExtractNonMoneyAmount(string text, int fallback)
        {
            foreach (Match match in Regex.Matches(text ?? string.Empty, @"\b(\d[\d,]*)\s+([A-Za-z]+)\b", RegexOptions.IgnoreCase))
            {
                string unit = match.Groups[2].Value.ToLowerInvariant();
                if (DirectiveHasAny(unit, "denar", "denars", "gold", "coin", "coins", "silver", "money"))
                {
                    continue;
                }

                string cleaned = match.Groups[1].Value.Replace(",", "");
                if (int.TryParse(cleaned, out int value) && value > 0)
                {
                    return value;
                }
            }

            return fallback;
        }

        private static List<Dictionary<string, object>> BuildDirectiveItemTerms(string text, string fromHeroId, string toHeroId, Dictionary<string, object> payload)
        {
            List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();
            AddInventoryMentionedItemTerms(items, text, fromHeroId, toHeroId, payload);
            if (items.Count > 0)
            {
                return items;
            }

            AddDirectiveItemTerm(items, text, "grain", "grain|food|supplies|supply", fromHeroId, toHeroId);
            AddDirectiveItemTerm(items, text, "horse", "horse|horses|mount|mounts", fromHeroId, toHeroId);
            AddDirectiveItemTerm(items, text, "tools", "tools?", fromHeroId, toHeroId);
            AddDirectiveItemTerm(items, text, "hardwood", "hardwood", fromHeroId, toHeroId);
            AddDirectiveItemTerm(items, text, "iron", "iron", fromHeroId, toHeroId);
            AddDirectiveItemTerm(items, text, "silver", "silver", fromHeroId, toHeroId);
            AddDirectiveItemTerm(items, text, "linen", "linen", fromHeroId, toHeroId);
            AddDirectiveItemTerm(items, text, "wool", "wool", fromHeroId, toHeroId);
            AddDirectiveItemTerm(items, text, "velvet", "velvet", fromHeroId, toHeroId);
            AddDirectiveItemTerm(items, text, "pottery", "pottery", fromHeroId, toHeroId);
            AddDirectiveItemTerm(items, text, "leather", "leather", fromHeroId, toHeroId);
            AddDirectiveItemTerm(items, text, "fur", "fur|furs", fromHeroId, toHeroId);
            return items;
        }

        private static void AddInventoryMentionedItemTerms(List<Dictionary<string, object>> items, string text, string fromHeroId, string toHeroId, Dictionary<string, object> payload)
        {
            if (items == null || string.IsNullOrWhiteSpace(text) || payload == null)
            {
                return;
            }

            Dictionary<string, object> index = ReadDictionary(payload, "actionResolutionIndex") ?? new Dictionary<string, object>();
            Dictionary<string, object> assets = ReadDictionary(index, "assets") ?? new Dictionary<string, object>();
            string playerHeroId = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId");
            string speakerHeroId = ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId");
            List<Dictionary<string, object>> inventories = new List<Dictionary<string, object>>();
            if (string.Equals(fromHeroId, playerHeroId, StringComparison.OrdinalIgnoreCase))
            {
                inventories.Add(ReadDictionary(ReadDictionary(assets, "player"), "inventory"));
            }
            else if (string.Equals(fromHeroId, speakerHeroId, StringComparison.OrdinalIgnoreCase))
            {
                inventories.Add(ReadDictionary(ReadDictionary(assets, "speaker"), "inventory"));
            }
            else
            {
                inventories.Add(ReadDictionary(ReadDictionary(assets, "player"), "inventory"));
                inventories.Add(ReadDictionary(ReadDictionary(assets, "speaker"), "inventory"));
            }

            foreach (Dictionary<string, object> inventory in inventories.Where(x => x != null))
            {
                foreach (Dictionary<string, object> candidate in DialogueService.ActionInventoryItems(inventory)
                    .OrderByDescending(x => ReadString(x, "name", string.Empty).Length))
                {
                    string itemId = ReadFirstString(candidate, "itemId", "item");
                    string name = ReadFirstString(candidate, "name", "itemId", "item");
                    int amount = ExtractInventoryItemAmount(text, name);
                    if (string.IsNullOrWhiteSpace(itemId) || amount <= 0 || items.Any(x => string.Equals(ReadString(x, "itemId", string.Empty), itemId, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    Dictionary<string, object> item = new Dictionary<string, object>
                    {
                        ["itemId"] = itemId,
                        ["item"] = itemId,
                        ["amount"] = amount
                    };
                    if (!string.IsNullOrWhiteSpace(fromHeroId))
                    {
                        item["fromHeroStringId"] = fromHeroId;
                        item["itemFromHeroStringId"] = fromHeroId;
                    }

                    if (!string.IsNullOrWhiteSpace(toHeroId))
                    {
                        item["toHeroStringId"] = toHeroId;
                        item["itemToHeroStringId"] = toHeroId;
                    }

                    items.Add(item);
                }
            }
        }

        internal static int ExtractInventoryItemAmount(string text, string itemName)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(itemName))
            {
                return 0;
            }

            Match match = Regex.Match(text, @"\b(\d[\d,]*)\s+" + Regex.Escape(itemName) + @"(?:s|es)?\b", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string cleaned = match.Groups[1].Value.Replace(",", string.Empty);
                if (int.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericAmount) && numericAmount > 0)
                {
                    return numericAmount;
                }
            }

            Match noun = Regex.Match(text, @"\b" + Regex.Escape(itemName) + @"(?:s|es)?\b", RegexOptions.IgnoreCase);
            return noun.Success ? ExtractEnglishNumberBefore(text, noun.Index) : 0;
        }

        private static void AddDirectiveItemTerm(List<Dictionary<string, object>> items, string text, string itemId, string nounPattern, string fromHeroId, string toHeroId)
        {
            if (items == null || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(nounPattern))
            {
                return;
            }

            int amount = ExtractItemAmount(text, nounPattern);
            if (amount <= 0)
            {
                return;
            }

            if (items.Any(x => string.Equals(ReadString(x, "itemId", string.Empty), itemId, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            Dictionary<string, object> item = new Dictionary<string, object>
            {
                ["itemId"] = itemId,
                ["item"] = itemId,
                ["amount"] = amount
            };
            if (!string.IsNullOrWhiteSpace(fromHeroId))
            {
                item["fromHeroStringId"] = fromHeroId;
                item["itemFromHeroStringId"] = fromHeroId;
            }

            if (!string.IsNullOrWhiteSpace(toHeroId))
            {
                item["toHeroStringId"] = toHeroId;
                item["itemToHeroStringId"] = toHeroId;
            }

            items.Add(item);
        }

        internal static int ExtractItemAmount(string text, string nounPattern)
        {
            string q = text ?? string.Empty;
            foreach (Match match in Regex.Matches(q, @"\b(\d[\d,]*)\s+(?:" + nounPattern + @")\b", RegexOptions.IgnoreCase))
            {
                string cleaned = match.Groups[1].Value.Replace(",", "");
                if (int.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0)
                {
                    return value;
                }
            }

            Match noun = Regex.Match(q, @"\b(?:" + nounPattern + @")\b", RegexOptions.IgnoreCase);
            if (noun.Success)
            {
                int spoken = ExtractEnglishNumberBefore(q, noun.Index);
                if (spoken > 0)
                {
                    return spoken;
                }
            }

            return 0;
        }

        private static int ExtractEnglishNumberBefore(string text, int endIndex)
        {
            if (string.IsNullOrWhiteSpace(text) || endIndex <= 0)
            {
                return 0;
            }

            string prefix = text.Substring(0, Math.Min(endIndex, text.Length));
            List<string> words = Regex.Matches(prefix, @"[A-Za-z]+")
                .Cast<Match>()
                .Select(match => match.Value)
                .ToList();
            int maxWords = Math.Min(7, words.Count);
            for (int count = maxWords; count >= 1; count--)
            {
                string candidate = string.Join(" ", words.Skip(words.Count - count));
                if (TryParseEnglishNumber(candidate, out int value) && value > 0)
                {
                    return value;
                }
            }

            return 0;
        }

        private static int ExtractFirstInteger(string text, int fallback)
        {
            Match match = Regex.Match(text ?? string.Empty, @"\b\d[\d,]*\b");
            if (!match.Success)
            {
                return fallback;
            }

            string cleaned = match.Value.Replace(",", "");
            return int.TryParse(cleaned, out int value) && value > 0 ? value : fallback;
        }

        private static string ExtractDirectiveItemName(string text)
        {
            string q = (text ?? string.Empty).ToLowerInvariant();
            if (q.Contains("grain")) return "grain";
            if (q.Contains("food") || q.Contains("supplies") || q.Contains("supply")) return "grain";
            if (q.Contains("tools")) return "tools";
            if (q.Contains("hardwood")) return "hardwood";
            if (q.Contains("iron")) return "iron";
            if (q.Contains("silver")) return "silver";
            if (q.Contains("linen")) return "linen";
            if (q.Contains("wool")) return "wool";
            if (q.Contains("velvet")) return "velvet";
            if (q.Contains("pottery")) return "pottery";
            if (q.Contains("leather")) return "leather";
            if (q.Contains("fur")) return "fur";
            if (q.Contains("jewelry") || q.Contains("jewellery")) return "jewelry";
            if (q.Contains("beer")) return "beer";
            if (q.Contains("wine")) return "wine";
            if (q.Contains("oil")) return "oil";
            if (q.Contains("dates")) return "dates";
            if (q.Contains("fish")) return "fish";
            if (q.Contains("meat")) return "meat";
            if (q.Contains("butter")) return "butter";
            if (q.Contains("cheese")) return "cheese";
            if (DirectiveHasAny(q, "armor", "armour")) return "armor";
            if (q.Contains("helmet")) return "helmet";
            if (q.Contains("boots")) return "boots";
            if (q.Contains("gloves")) return "gloves";
            if (q.Contains("cloak")) return "cloak";
            if (q.Contains("cape")) return "cape";
            if (q.Contains("shield")) return "shield";
            if (q.Contains("horse") || q.Contains("mount")) return "horse";
            if (q.Contains("sword")) return "sword";
            if (q.Contains("axe")) return "axe";
            if (q.Contains("spear")) return "spear";
            if (q.Contains("bow")) return "bow";
            if (q.Contains("weapon")) return "weapon";
            return "item";
        }

        internal static void ApplyTransferDirectionDefaults(Dictionary<string, object> action, Dictionary<string, object> terms, Dictionary<string, object> payload, Dictionary<string, object> hero, string directiveText, string command)
        {
            if (action == null || terms == null)
            {
                return;
            }

            string speakerHeroId = FirstNonEmpty(
                ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ReadString(hero, "heroStringId", ""),
                "speaker");
            string playerHeroId = FirstNonEmpty(ReadString(payload, "playerHeroStringId", ""), "player");
            string playerSegment = ExtractConversationSegment(directiveText, "player");
            string npcSegment = ExtractConversationSegment(directiveText, "npc");
            bool hasSpeakerSegments = !string.IsNullOrWhiteSpace(playerSegment) || !string.IsNullOrWhiteSpace(npcSegment);
            bool playerToSpeaker = hasSpeakerSegments
                ? LooksLikePlayerToSpeakerTransfer(playerSegment) || LooksLikeSpeakerToPlayerTransfer(npcSegment)
                : LooksLikePlayerToSpeakerTransfer(directiveText);
            bool speakerToPlayer = hasSpeakerSegments
                ? LooksLikeSpeakerToPlayerTransfer(playerSegment) || LooksLikePlayerToSpeakerTransfer(npcSegment)
                : LooksLikeSpeakerToPlayerTransfer(directiveText);
            if (playerToSpeaker && speakerToPlayer)
            {
                string q = (directiveText ?? string.Empty).ToLowerInvariant();
                speakerToPlayer = !DirectiveHasAny(q, "my ships", "my fleet", "my grain", "my goods", "my supplies", "they are yours", "give them to you", "take my");
                playerToSpeaker = !speakerToPlayer;
            }
            bool textGivesDirection = playerToSpeaker || speakerToPlayer;

            string fromHero = speakerHeroId;
            string toHero = playerHeroId;
            if (playerToSpeaker && !speakerToPlayer)
            {
                fromHero = playerHeroId;
                toHero = speakerHeroId;
            }

            if (string.Equals(command, "give_gold_to_player", StringComparison.OrdinalIgnoreCase))
            {
                fromHero = speakerHeroId;
                toHero = playerHeroId;
            }

            if (!string.Equals(command, "give_gold_to_player", StringComparison.OrdinalIgnoreCase))
            {
                string explicitFrom = FirstNonEmpty(ReadFirstString(action, "FromHero", "fromHero"), ReadFirstString(terms, "FromHero", "fromHero", "fromHeroStringId"));
                string explicitTo = FirstNonEmpty(ReadFirstString(action, "ToHero", "toHero"), ReadFirstString(terms, "ToHero", "toHero", "toHeroStringId"));
                if (!textGivesDirection && !string.IsNullOrWhiteSpace(explicitFrom))
                {
                    fromHero = explicitFrom;
                }
                else if (!textGivesDirection)
                {
                    fromHero = FirstNonEmpty(ReadFirstString(action, "actorHeroStringId", "actorHeroId"), ReadFirstString(terms, "actorHeroStringId", "actorHeroId"), fromHero);
                }

                if (!textGivesDirection && !string.IsNullOrWhiteSpace(explicitTo))
                {
                    toHero = explicitTo;
                }
                else if (!textGivesDirection)
                {
                    toHero = FirstNonEmpty(ReadFirstString(action, "targetHeroStringId", "targetHeroId", "TargetHero"), ReadFirstString(terms, "targetHeroStringId", "targetHeroId", "TargetHero"), toHero);
                }
            }

            action["actorHeroId"] = fromHero;
            action["FromHero"] = fromHero;
            action["ToHero"] = toHero;
            action["TargetHero"] = toHero;
            terms["fromHeroStringId"] = fromHero;
            terms["toHeroStringId"] = toHero;
            terms["targetHeroStringId"] = toHero;
        }

        private static void ApplyTemporaryPartyGuestActorBinding(
            Dictionary<string, object> action,
            Dictionary<string, object> terms,
            Dictionary<string, object> payload,
            Dictionary<string, object> hero,
            string command)
        {
            string canonical = CanonicalCommand(command);
            if (canonical != "accept_temporary_party_guest"
                && canonical != "renew_temporary_party_guest"
                && canonical != "end_temporary_party_guest"
                && canonical != "acknowledge_own_faction_combat_risk")
            {
                return;
            }

            string speakerHeroId = FirstNonEmpty(
                ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ReadString(hero, "heroStringId", ""));
            if (string.IsNullOrWhiteSpace(speakerHeroId))
            {
                return;
            }

            // Temporary-guest lifecycle actions always operate on the consenting/current NPC.
            // The planner may describe the player's request with the player as actor, but the
            // native executor resolves the guest record from ActorHeroStringId.
            action["actorHeroStringId"] = speakerHeroId;
            action["actorHeroId"] = speakerHeroId;
            action["FromHero"] = speakerHeroId;
            terms["fromHeroStringId"] = speakerHeroId;
            DialogueService.BindTemporaryGuestActionTerms(canonical, terms, payload, hero);
        }

        private static string ExtractConversationSegment(string text, string label)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(label))
            {
                return string.Empty;
            }

            string wanted = label.Trim().TrimEnd(':').ToLowerInvariant() + ":";
            StringBuilder builder = new StringBuilder();
            foreach (string rawLine in (text ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                string line = rawLine.Trim();
                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                string prefix = line.Substring(0, colon + 1).ToLowerInvariant();
                if (prefix == wanted)
                {
                    builder.AppendLine(line.Substring(colon + 1).Trim());
                }
            }

            return builder.ToString().Trim();
        }

        private static string StripDirectiveMarkers(string text)
        {
            string value = (text ?? string.Empty).Trim();
            if (value.Length >= 2 && value.StartsWith("@", StringComparison.Ordinal) && value.EndsWith("@", StringComparison.Ordinal))
            {
                return value.Substring(1, value.Length - 2).Trim();
            }

            return value.Replace("@", "").Trim();
        }

        internal static string LatestPlayerDirectiveOrText(string text)
        {
            string value = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string latestPlayerLine = string.Empty;
            foreach (string rawLine in value.Replace("\r\n", "\n").Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("player:", StringComparison.OrdinalIgnoreCase))
                {
                    latestPlayerLine = line.Substring("player:".Length).Trim();
                }
            }

            return string.IsNullOrWhiteSpace(latestPlayerLine) ? value : latestPlayerLine;
        }

        private static bool LooksLikeNamedSettlementTransferDirective(Dictionary<string, object> payload, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || LooksLikeNavalAssetDirective(text))
            {
                return false;
            }

            if (DirectiveHasAny(text.ToLowerInvariant(), "full surrender", "capitulation", "all towns", "all castles"))
            {
                return false;
            }

            bool hasTransferVerb = DirectiveHasAny(text.ToLowerInvariant(), "give", "grant", "hand over", "transfer", "cede", "surrender", "yield", "turn over", "award");
            return hasTransferVerb && !string.IsNullOrWhiteSpace(FindMentionedSettlementName(payload, text, ""));
        }

        private static bool LooksLikeNamedPaidSettlementTradeDirective(Dictionary<string, object> payload, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string q = text.ToLowerInvariant();
            bool hasMoney = DirectiveHasAny(q, "gold", "denar", "denars", "coin", "coins", "money", "silver", "price");
            bool hasExchangeShape = DirectiveHasAny(q, " for ", "price", "trade", "sell", "sold", "buy", "purchase", "deal", "bargain", "in exchange", "pay", "payment");
            return hasMoney && hasExchangeShape && !string.IsNullOrWhiteSpace(FindMentionedSettlementName(payload, text, ""));
        }

        private static bool LooksLikeNamedHeroGoldTransferDirective(Dictionary<string, object> payload, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || !LooksLikeGiveGoldDirective(text))
            {
                return false;
            }

            string q = text.ToLowerInvariant();
            if (LooksLikePaidAssetTradeDirective(q)
                || LooksLikeNamedPaidExchangeDirective(q)
                || DirectiveHasAny(q, "prisoner", "ransom", "captive", "hostage", " for ", "in exchange", "buy", "purchase", "sell", "sold", "price"))
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(FindMentionedHeroName(payload, text))
                || !string.IsNullOrWhiteSpace(FindMentionedRelatedHeroName(payload, text));
        }

        private static string FindMentionedRelatedHeroName(Dictionary<string, object> payload, string text)
        {
            Dictionary<string, object> hero = ReadDictionary(payload, "hero") ?? new Dictionary<string, object>();
            string normalizedText = NormalizeLookup(text);
            if (hero.Count == 0 || string.IsNullOrWhiteSpace(normalizedText))
            {
                return string.Empty;
            }

            var names = new List<string>();
            foreach (string key in new[] { "spouseName", "fatherName", "motherName", "sovereignName" })
            {
                string name = ReadString(hero, key, "").Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
            names.AddRange(ReadStringList(hero, "childrenNames"));

            string playerName = ReadString(hero, "mainHeroName", "");
            List<string> matches = names
                .Where(name => !string.IsNullOrWhiteSpace(name)
                    && !string.Equals(name, playerName, StringComparison.OrdinalIgnoreCase))
                .Where(name =>
                {
                    string normalizedName = NormalizeLookup(name);
                    return normalizedName.Length >= 3 && ContainsNormalizedPhrase(normalizedText, normalizedName);
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(name => NormalizeLookup(name).Length)
                .ToList();

            return matches.Count == 1 ? matches[0] : string.Empty;
        }

        private static string FindMentionedSettlementName(Dictionary<string, object> payload, string text, string targetKingdomId)
        {
            Dictionary<string, object> index = ReadDictionary(payload, "actionResolutionIndex") ?? new Dictionary<string, object>();
            string normalizedText = NormalizeLookup(text);
            if (index.Count == 0 || string.IsNullOrWhiteSpace(normalizedText))
            {
                return string.Empty;
            }

            List<Dictionary<string, object>> matches = ReadDictionaryList(index, "settlements")
                .Where(x => string.IsNullOrWhiteSpace(targetKingdomId) || string.Equals(ReadString(x, "kingdomId", ""), targetKingdomId, StringComparison.OrdinalIgnoreCase))
                .Where(x =>
                {
                    string name = NormalizeLookup(ReadString(x, "name", ""));
                    return name.Length >= 3 && ContainsNormalizedPhrase(normalizedText, name);
                })
                .OrderByDescending(x => NormalizeLookup(ReadString(x, "name", "")).Length)
                .ToList();

            return matches.Count == 1 ? ReadString(matches[0], "name", ReadString(matches[0], "settlementId", "")) : string.Empty;
        }

        private static string FindMentionedHeroName(Dictionary<string, object> payload, string text)
        {
            Dictionary<string, object> index = ReadDictionary(payload, "actionResolutionIndex") ?? new Dictionary<string, object>();
            string normalizedText = NormalizeLookup(text);
            if (index.Count == 0 || string.IsNullOrWhiteSpace(normalizedText))
            {
                return string.Empty;
            }

            string speakerHeroId = ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId");
            string playerHeroId = ReadString(payload, "playerHeroStringId", "");
            List<Dictionary<string, object>> matches = ReadDictionaryList(index, "heroes")
                .Where(x =>
                {
                    string heroId = ReadString(x, "heroStringId", "");
                    if (string.Equals(heroId, speakerHeroId, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(heroId, playerHeroId, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    string name = NormalizeLookup(ReadString(x, "name", ""));
                    return name.Length >= 3 && ContainsNormalizedPhrase(normalizedText, name);
                })
                .OrderByDescending(x => NormalizeLookup(ReadString(x, "name", "")).Length)
                .ToList();

            return matches.Count == 1 ? ReadString(matches[0], "name", ReadString(matches[0], "heroStringId", "")) : string.Empty;
        }

        private static Dictionary<string, object> FindMentionedMarriageHero(Dictionary<string, object> payload, string text)
        {
            Dictionary<string, object> index = ReadDictionary(payload, "actionResolutionIndex") ?? new Dictionary<string, object>();
            string normalizedText = NormalizeLookup(text);
            if (index.Count == 0 || string.IsNullOrWhiteSpace(normalizedText))
            {
                return new Dictionary<string, object>();
            }

            List<Dictionary<string, object>> matches = ReadDictionaryList(index, "heroes")
                .Where(x =>
                {
                    string name = NormalizeLookup(ReadString(x, "name", ""));
                    return name.Length >= 3 && ContainsNormalizedPhrase(normalizedText, name);
                })
                .OrderByDescending(x => NormalizeLookup(ReadString(x, "name", "")).Length)
                .ToList();
            return matches.Count == 1 ? matches[0] : new Dictionary<string, object>();
        }

        internal static bool ContainsNormalizedPhrase(string normalizedText, string normalizedPhrase)
        {
            if (string.IsNullOrWhiteSpace(normalizedText) || string.IsNullOrWhiteSpace(normalizedPhrase))
            {
                return false;
            }

            return (" " + normalizedText.Trim() + " ").IndexOf(
                " " + normalizedPhrase.Trim() + " ",
                StringComparison.Ordinal) >= 0;
        }

        internal static string InferDuelModeFromDirective(string text)
        {
            string q = (text ?? string.Empty).ToLowerInvariant();
            if (Regex.IsMatch(q, @"\btrain(?:ing)? together\b|\btraining duel\b")) return "training_together";
            if (ContainsAnyNormalized(q, "practice") || Regex.IsMatch(q, @"\bspar(?:ring)?\b")) return "practice";
            if (q.Contains("lethal")
                || q.Contains("to the death")
                || q.Contains("death")
                || q.Contains("honor")
                || q.Contains("honour"))
            {
                return "lethal";
            }

            return "training";
        }

        internal static bool IsLethalDuelMode(string mode)
        {
            string normalized = (mode ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            return normalized == "lethal"
                || normalized == "death"
                || normalized == "to_the_death"
                || normalized == "honor"
                || normalized == "honour";
        }

        internal static void AddIfMissing(Dictionary<string, object> target, string key, object value)
        {
            if (target == null || string.IsNullOrWhiteSpace(key) || value == null)
            {
                return;
            }

            if (target.TryGetValue(key, out object existing) && existing != null && !string.IsNullOrWhiteSpace(Convert.ToString(existing)))
            {
                return;
            }

            if (value is string text && string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            target[key] = value;
        }

        private static string FindSettlementIdFromContextBundles(List<Dictionary<string, object>> bundles, string factionId, string mode)
        {
            foreach (Dictionary<string, object> bundle in bundles ?? new List<Dictionary<string, object>>())
            {
                string id = ReadString(bundle, "id", "");
                Dictionary<string, object> data = ReadDictionary(bundle, "data") ?? new Dictionary<string, object>();
                if (string.Equals(id, "current_settlement_facts", StringComparison.OrdinalIgnoreCase))
                {
                    Dictionary<string, object> settlement = ReadDictionary(data, "settlement") ?? new Dictionary<string, object>();
                    string settlementId = ReadString(settlement, "settlementId", "");
                    if (!string.IsNullOrWhiteSpace(settlementId))
                    {
                        return settlementId;
                    }
                }

                if (string.Equals(id, "nearby_settlements", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (Dictionary<string, object> settlement in ReadDictionaryList(data, "items"))
                    {
                        string settlementId = ReadString(settlement, "settlementId", "");
                        string settlementFactionId = ReadString(settlement, "factionId", "");
                        if (!string.IsNullOrWhiteSpace(settlementId)
                            && (string.IsNullOrWhiteSpace(factionId) || string.Equals(settlementFactionId, factionId, StringComparison.OrdinalIgnoreCase)))
                        {
                            return settlementId;
                        }
                    }
                }
            }

            return string.Empty;
        }

        private static string FindThirdKingdomIdFromContextBundles(List<Dictionary<string, object>> bundles, string playerKingdomId, string speakerKingdomId)
        {
            foreach (Dictionary<string, object> bundle in bundles ?? new List<Dictionary<string, object>>())
            {
                if (!string.Equals(ReadString(bundle, "id", ""), "kingdom_diplomacy_status", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Dictionary<string, object> data = ReadDictionary(bundle, "data") ?? new Dictionary<string, object>();
                foreach (string listKey in new[] { "playerWars", "speakerWars" })
                {
                    foreach (Dictionary<string, object> war in ReadDictionaryList(data, listKey))
                    {
                        string id = ReadString(war, "factionId", "");
                        if (!string.IsNullOrWhiteSpace(id)
                            && !string.Equals(id, playerKingdomId, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(id, speakerKingdomId, StringComparison.OrdinalIgnoreCase))
                        {
                            return id;
                        }
                    }
                }
            }

            return string.Empty;
        }

        internal static IEnumerable<object> ObjectsFromValue(object value)
        {
            if (value == null)
            {
                return Enumerable.Empty<object>();
            }

            if (value is ArrayList array)
            {
                return array.Cast<object>();
            }

            if (value is IEnumerable<object> enumerable)
            {
                return enumerable;
            }

            return new[] { value };
        }
    }
}
