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
    internal static partial class DialogueService
    {
        internal static Dictionary<string, object> SelectContextPulls(Dictionary<string, object> payload)
        {
            Stopwatch total = Stopwatch.StartNew();
            payload = payload ?? new Dictionary<string, object>();
            string mode = ReadString(payload, "mode", "dialogue");
            string campaignId = ReadString(payload, "campaignId", "default");
            string correlationId = OperationsService.EnsureCorrelationId(payload);
            string heroId = ReadFirstString(payload, "heroStringId", "heroId", "speakerHeroStringId", "characterId");
            string playerText = ReadFirstString(payload, "playerText", "text", "message", "query");
            string sceneContext = ReadString(payload, "sceneContext", "");
            List<string> available = NormalizeContextPullIds(ReadStringList(payload, "availablePullIds"));
            if (available.Count == 0)
            {
                available = Program.ContextPullIds.ToList();
            }

            Dictionary<string, object> settings = SettingsService.LoadSettings();
            List<string> minimeTopics = TryExtractMinimeTopics(settings, playerText + "\n" + sceneContext, ReadInt(settings, "minimeMaxTopics", 5));
            string selectorText = AppendMinimeTopics(playerText, minimeTopics);
            Dictionary<string, object> selector = new Dictionary<string, object>
            {
                ["ok"] = true, ["method"] = "deterministic_foreground_router", ["modelCallAvoided"] = true
            };
            bool fallbackUsed = false;
            string fallbackReason = "";
            List<Dictionary<string, object>> selected = DeterministicContextPullSelection(mode, selectorText, sceneContext, available);
            selected = PruneContextPullSelection(selectorText, selected);

            total.Stop();
            Dictionary<string, object> response = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["mode"] = mode,
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["heroStringId"] = heroId,
                ["selectedPulls"] = selected,
                ["minimeTopics"] = minimeTopics,
                ["fallbackUsed"] = fallbackUsed,
                ["fallbackReason"] = fallbackUsed ? fallbackReason : "",
                ["selector"] = selector ?? new Dictionary<string, object>(),
                ["timing"] = new Dictionary<string, object>
                {
                    ["totalMs"] = total.ElapsedMilliseconds,
                    ["selectedCount"] = selected.Count,
                    ["availableCount"] = available.Count
                }
            };

            OperationsService.LogOperational("context.select", new Dictionary<string, object>
            {
                ["mode"] = mode,
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["heroStringId"] = heroId,
                ["playerText"] = LimitText(playerText, 180),
                ["minimeTopics"] = minimeTopics,
                ["selectedPulls"] = selected.Select(x => ReadString(x, "id", "")).ToList(),
                ["selectedReasons"] = selected.Select(x => ReadString(x, "reason", "")).Where(x => !string.IsNullOrWhiteSpace(x)).ToList(),
                ["fallbackUsed"] = fallbackUsed,
                ["fallbackReason"] = fallbackUsed ? fallbackReason : "",
                ["timing"] = response["timing"]
            });
            OperationsService.WriteAudit(campaignId, correlationId, "server", mode, "context.select", heroId, "", "", "completed", total.ElapsedMilliseconds, "Selected context pulls for " + mode + ".", new Dictionary<string, object>
            {
                ["playerText"] = playerText,
                ["sceneContext"] = sceneContext,
                ["minimeTopics"] = minimeTopics,
                ["selectedPulls"] = selected,
                ["fallbackUsed"] = fallbackUsed,
                ["fallbackReason"] = fallbackUsed ? fallbackReason : "",
                ["selector"] = selector ?? new Dictionary<string, object>(),
                ["timing"] = response["timing"]
            });
            return response;
        }

        internal static List<Dictionary<string, object>> DeterministicContextPullSelection(string mode, string playerText, string sceneContext, List<string> available)
        {
            string query = (playerText ?? "").ToLowerInvariant();
            bool worldHistoryLookup = IsWorldHistoryLookupRequest(query);
            bool recallFocused = DialogueService.LooksLikeMostRecentConversationRecall(query) || ContainsAny(query,
                "do you remember", "can you remember", "what do you remember", "can you recall", "what can you recall",
                "remind me", "remind us",
                "what did i tell", "earlier i told", "i told this group", "i told you before", "reconstruct our prior",
                "recall the preceding", "recall our earlier", "remember our earlier", "immediately preceding conversation",
                "immediately preceding group", "previous conversation", "prior conversation", "last conversation",
                "raw conversation", "raw transcript", "stored summary conflicts")
                || ContainsAnyTerm(query, "reconstruct", "reconstruction");
            List<Dictionary<string, object>> selected = new List<Dictionary<string, object>>();
            Action<string, string, int, int> add = (id, reason, radius, maxCount) =>
            {
                if (!available.Contains(id) || selected.Any(x => string.Equals(ReadString(x, "id", ""), id, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                Dictionary<string, object> args = new Dictionary<string, object>();
                if (radius > 0) args["radius"] = radius;
                if (maxCount > 0) args["maxCount"] = maxCount;
                selected.Add(new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["reason"] = reason,
                    ["arguments"] = args
                });
            };

            if (!recallFocused && (ContainsAnyTerm(query, "look", "appearance", "appear", "seem", "wear", "wearing", "clothes", "clothing", "armor", "robe", "dress", "rags", "poor", "rich", "status", "rank", "disguise")
                || ContainsAny(query, "am i a lord", "am i a lady", "am i noble", "do i look noble", "do i seem noble")))
            {
                add("check_player_appearance_status", "player asked or implied how they appear socially", 0, 0);
                add("check_inventory_appearance", "appearance/status reading may depend on the speaker's material eye", 0, 0);
            }

            bool negatesTradeOffer = NegatesMaterialTradeOffer(query);
            bool materialGiftOffer = HasExplicitMaterialGiftOffer(query);
            bool occupationalTradeUsage = IsOccupationalTradeUsage(query);
            bool conversationalTradeUsage = IsConversationalTradeUsage(query);
            bool materialSuppliesQuestion = ContainsAnyTerm(query, "supplies", "supply")
                && ContainsAnyTerm(query, "inventory", "food", "goods", "grain", "caravan", "wares", "travel", "journey", "army", "party", "stock", "stocks", "provisions", "equipment", "horses", "mounts");
            bool materialInventoryRequest = materialSuppliesQuestion
                || ContainsAnyTerm(query, "inventory", "food", "goods", "grain", "caravan", "wares", "sell", "buy", "price", "coin", "money", "wealth", "horses", "mounts")
                || (!negatesTradeOffer && !occupationalTradeUsage && !conversationalTradeUsage && ContainsAnyTerm(query, "trade", "trading"));
            if (!worldHistoryLookup && !recallFocused && materialInventoryRequest)
            {
                add("check_inventory_appearance", "player asked about supplies, goods, wealth, or material presentation", 0, 0);
                add("clan_wealth_and_influence", "material questions may depend on clan credit and influence", 0, 0);
            }

            bool pricedAssetWorthQuestion = ContainsAnyTerm(query, "worth")
                && ContainsAnyTerm(query, "item", "cup", "weapon", "sword", "armor", "horse", "mount", "prisoner", "workshop", "ship", "fleet", "settlement", "town", "castle", "village", "gold", "denar", "goods", "wares");
            bool howMuchMaterialQuestion = ContainsAny(query, "how much")
                && ContainsAnyTerm(query, "item", "cup", "weapon", "sword", "armor", "horse", "mount",
                    "prisoner", "workshop", "ship", "fleet", "settlement", "town", "castle", "village",
                    "gold", "denar", "coin", "goods", "wares", "food", "grain", "price", "value", "worth");
            if (!worldHistoryLookup && (materialGiftOffer || pricedAssetWorthQuestion || howMuchMaterialQuestion
                || (!negatesTradeOffer && (ContainsAnyTerm(query, "sell", "buy", "price", "purchase", "barter", "ransom", "ships", "fleet", "workshop")
                    || ContainsAny(query, "trade for", "in exchange", "denars for", "gold for", "pay you", "pay me", "item value", "trade value", "market value")))))
            {
                add("appraise_trade_offer", "player proposed or asked about a priced asset exchange", 0, 0);
            }

            if (IsNearbySettlementRequest(query))
            {
                add("nearby_settlements", "player asked about nearby settlements or routes", 90, 8);
            }

            if (ContainsAny(query, "bandit", "bandits", "looter", "looters", "raider", "raiders", "outlaw", "outlaws", "hideout", "ambush", "safe road", "danger nearby", "danger on the road"))
            {
                add("nearby_bandit_parties", "player asked about bandits or road danger", 90, 10);
            }

            bool nearbyNamedForceQuestion = ContainsAnyTerm(query, "lord", "lords", "army", "armies", "party", "parties", "force", "forces", "troop", "troops", "patrol", "patrols")
                && ContainsAnyTerm(query, "near", "nearby", "around", "close", "closest", "local", "locally");
            if (nearbyNamedForceQuestion || ContainsAny(query, "lord nearby", "lords nearby", "nearby lord", "nearby lords", "army nearby", "armies nearby", "nearby army", "nearby armies",
                "party nearby", "parties nearby", "nearby party", "nearby parties", "nearby force", "nearby forces", "troops nearby", "nearby troops",
                "named forces", "troop groups", "patrol", "enemy party", "allied party", "who is near"))
            {
                add("nearby_lord_parties", "player asked about nearby lord parties or armies", 120, 10);
            }

            string sceneLocationName = SceneContextLocationName(sceneContext);
            bool namedCurrentSettlementMentioned = !string.IsNullOrWhiteSpace(sceneLocationName)
                && query.IndexOf(sceneLocationName.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase) >= 0;
            bool namedCurrentSettlementQuestion = namedCurrentSettlementMentioned
                && ContainsAnyTerm(query, "concern", "concerns", "condition", "conditions", "situation", "problem", "problems", "issue", "issues", "market", "economy", "prosperity", "security", "loyalty", "garrison", "militia");
            bool generalCurrentSettlementQuestion = namedCurrentSettlementMentioned
                && (ContainsAny(query, "right now", "currently", "current local", "local facts", "facts about", "conditions in", "situation in", "weather in", "weather and")
                    || ContainsAnyTerm(query, "today", "verify", "verified"))
                && ContainsAnyTerm(query, "what", "tell", "know", "fact", "facts", "condition", "conditions", "situation", "weather", "verify", "verified");
            bool namedCurrentSettlementOwnershipClaim = namedCurrentSettlementMentioned
                && (ContainsAnyTerm(query, "own", "owns", "owned", "owner", "ownership", "belong", "belongs", "belonged", "capture", "captured", "conquer", "conquered", "take", "took", "banner", "kingdom", "empire")
                    || ContainsAny(query, "who holds", "who controls", "which realm", "which kingdom"));
            bool localGovernanceOrSiegeQuestion = (namedCurrentSettlementMentioned
                    || ContainsAny(query, "this town", "this city", "this village", "this castle", "this settlement", "current settlement"))
                && (ContainsAnyTerm(query, "governor", "governs", "governance", "siege", "besieged")
                    || ContainsAny(query, "under siege", "siege status"));
            bool localOwnershipFactsRequested = namedCurrentSettlementOwnershipClaim
                || ContainsAny(query, "current settlement ownership", "who owns this", "who holds this", "who controls this");
            bool localFacts = namedCurrentSettlementQuestion || generalCurrentSettlementQuestion || localOwnershipFactsRequested || localGovernanceOrSiegeQuestion
                || ContainsAny(query, "current settlement ownership", "this town", "this city", "this village", "this castle", "this settlement", "current town", "current city", "current village", "current castle", "market", "walls", "garrison", "militia", "prosperity", "security", "loyalty")
                || (ContainsAny(query, "here", "local", "place") && ContainsAny(query, "market", "walls", "garrison", "militia", "prosperity", "security", "loyalty", "town", "city", "village", "castle", "settlement"));
            if (localFacts)
            {
                add("current_settlement_facts", "player asked about the current settlement or local conditions", 0, 0);
            }

            bool explicitDiplomacyQuestion = ContainsAnyTerm(query, "war", "peace", "tribute", "truce", "enemy", "enemies", "ally", "allies", "diplomacy", "surrender", "reparations");
            bool nonLocalKingdomQuestion = ContainsAnyTerm(query, "kingdom", "realm") && !localOwnershipFactsRequested;
            if ((!worldHistoryLookup && (explicitDiplomacyQuestion || nonLocalKingdomQuestion))
                || ContainsAny(query, "settlement demand", "currently at war", "at war now", "current diplomacy", "current war"))
            {
                add("kingdom_diplomacy_status", "player asked about diplomacy, war, peace, or surrender terms", 0, 0);
            }

            bool explicitClanResourcesQuestion = ContainsAnyTerm(query,
                    "influence", "renown", "supporters", "backing", "fief", "fiefs", "wealth", "money", "house")
                || ContainsAny(query, "clan support", "political support", "family power");
            if (explicitClanResourcesQuestion || (ContainsAnyTerm(query, "clan") && !localOwnershipFactsRequested))
            {
                add("clan_wealth_and_influence", "player asked about clan resources, standing, or influence", 0, 0);
            }

            bool personalMemoryRequested = ContainsAny(query,
                "memory", "remember", "last time", "earlier conversation", "earlier conversations", "first scene", "promise", "vow", "favor", "debt", "insult", "betray", "secret", "father", "mother", "husband", "wife", "child", "family",
                "we spoke before", "we met before", "said before", "mentioned before", "told you before", "asked you before", "earlier you", "earlier we")
                || (!worldHistoryLookup && ContainsAny(query, "history", "past"));
            if (recallFocused || personalMemoryRequested)
            {
                add("relevant_memory", "player invoked memory, backstory, promises, secrets, or past events", 0, 0);
            }

            if (worldHistoryLookup || ContainsHistoricalWorldAssertion(query))
            {
                add("verify_world_history", "the message makes a verifiable factual claim, disputes one, or explicitly admits deceptive intent", 0, 0);
            }

            if (ContainsAnyTerm(query, "relationship", "trust", "friend", "friends", "friendship", "enemy", "enemies", "hate", "love", "marry", "married", "marriage", "court", "courting", "jealous", "jealousy", "respect", "opinion", "flirt", "flirting", "kiss", "attract", "attracted", "attraction", "desire", "seduce", "seduction", "affair", "lover", "lovers", "intimate", "intimacy", "beautiful", "handsome"))
            {
                add("relationship_history", "player asked about relationship standing or social history", 0, 0);
            }

            return selected;
        }

        internal static string SceneContextLocationName(string sceneContext)
        {
            Match match = Regex.Match(sceneContext ?? "", @"(?im)^\s*(?:(?:current|active)\s+)?(?:location|settlement)\s*:\s*([^\r\n]+)");
            if (!match.Success) return "";
            string value = match.Groups[1].Value.Trim();
            int separator = value.IndexOfAny(new[] { '|', ';', '[', '(', '`' });
            return (separator >= 0 ? value.Substring(0, separator) : value).Trim();
        }

        private static bool IsNearbySettlementRequest(string query)
        {
            string q = (query ?? "").Trim().ToLowerInvariant();
            bool namedPlaceKind = ContainsAnyTerm(q,
                "town", "towns", "village", "villages", "castle", "castles",
                "city", "cities", "settlement", "settlements", "place", "places");
            bool spatialOrRouteIntent = ContainsAnyTerm(q,
                "near", "nearby", "around", "closest", "nearest", "road", "roads",
                "route", "routes", "path", "paths");
            return (namedPlaceKind && spatialOrRouteIntent)
                || ContainsAny(q,
                    "what towns", "which towns", "what villages", "which villages",
                    "where is", "road to", "route to");
        }

        internal static bool HasExplicitMaterialGiftOffer(string query)
        {
            string q = (query ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(q)
                || NegatesMaterialGiftOffer(q))
            {
                return false;
            }

            const string asset = @"(?:item|cup|silver|gold|coin|denars?|weapon|sword|armor|horse|mount|ring|necklace|jewel|gem|clothes|robe|food|grain|goods|wares|workshop|house|home|land|estate|property)";
            bool explicitlyNamedGift = Regex.IsMatch(q,
                @"\b(?:as\s+(?:a\s+)?(?:(?:genuine|sincere|free|freely\s+given|personal|valuable)\s+)?gift|personal\s+gift|gift\s+(?:for|to)\s+you|gift\s+you|present\s+(?:for|to)\s+you)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            bool directRecipientBeforeAsset = Regex.IsMatch(q,
                @"\b(?:i|we)\s+(?:(?:am|are|'m|'re)\s+)?(?:(?:will|would|want\s+to|mean\s+to)\s+)?(?:give|gave|giving|offer|offering|hand|handing)\s+(?:you|him|her|them)\b[^\r\n\.;:!?]{0,60}\b" + asset + @"\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            bool namedRecipientBeforeAsset = Regex.IsMatch(q,
                @"\b(?:i|we)\s+(?:(?:am|are|'m|'re)\s+)?(?:(?:will|would|want\s+to|mean\s+to)\s+)?(?:give|giving|offer|offering|hand|handing)\s+(?!out\b|away\b|over\b|back\b)[\p{L}][\p{L}'-]*(?:\s+[\p{L}][\p{L}'-]*){0,2}\s+(?:this|that|a|an|the|my)\s+" + asset + @"\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            bool assetBeforeRecipient = Regex.IsMatch(q,
                @"\b(?:i|we)\s+(?:(?:am|are|'m|'re)\s+)?(?:(?:will|would|want\s+to|mean\s+to)\s+)?(?:give|giving|offer|offering|hand|handing)\b[^\r\n\.;:!?]{0,60}\b" + asset + @"\b[^\r\n\.;:!?]{0,30}\b(?:to|for)\s+(?:you|him|her|them)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return explicitlyNamedGift || directRecipientBeforeAsset || namedRecipientBeforeAsset || assetBeforeRecipient;
        }

        internal static bool NegatesMaterialTradeOffer(string query)
        {
            string q = (query ?? "").Trim().ToLowerInvariant();
            return ContainsAny(q, "not a trade", "not trade", "no trade offer", "not for sale", "not an exchange", "no exchange")
                || Regex.IsMatch(q, @"\bnot\s+(?:a\s+)?(?:gift\s+or\s+)?(?:a\s+)?trade(?:\s+offer)?\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || Regex.IsMatch(q, @"\b(?:do\s+not|don't|not\s+to)\s+(?:treat|regard|read|interpret|take)\b[^\r\n\.;:!?]{0,80}\b(?:as\s+)?(?:a\s+)?(?:trade|sale|exchange)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || Regex.IsMatch(q, @"\bnot\s+(?:currently\s+)?(?:offering|proposing|trading|selling)\b[^\r\n\.;:!?]{0,80}\b(?:trade|sale|exchange)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool NegatesMaterialGiftOffer(string query)
        {
            string q = (query ?? "").Trim().ToLowerInvariant();
            return ContainsAny(q, "not a gift", "no gift", "not gifting", "not giving this", "not giving it")
                || Regex.IsMatch(q, @"\b(?:do\s+not|don't|not\s+to)\s+(?:treat|regard|read|interpret|take)\b[^\r\n\.;:!?]{0,80}\b(?:as\s+)?(?:a\s+)?(?:gift|present)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || Regex.IsMatch(q, @"\bnot\s+(?:currently\s+)?(?:offering|proposing|giving|gifting|presenting)\b[^\r\n\.;:!?]{0,80}\b(?:gift|present)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool IsOccupationalTradeUsage(string query)
        {
            string q = (query ?? "").Trim().ToLowerInvariant();
            return Regex.IsMatch(q,
                @"\b(?:my|your|his|her|their|our)\s+(?:own\s+)?trades?\b|\btrades?\s+(?:lead|leads|led|teach|teaches|taught)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        internal static bool IsConversationalTradeUsage(string query)
        {
            string q = (query ?? "").Trim().ToLowerInvariant();
            return Regex.IsMatch(q,
                @"\btrad(?:e|ing)\s+(?:(?:one|some|something|a\s+few)\s+)?(?:small\s+and\s+)?(?:personal\s+)?(?:stories|story|details|detail|answers|answer|questions|question|memories|memory|thoughts|thought|information|secrets|secret|experiences|experience|words|word|jokes|joke|insults|insult)\b"
                + @"|\btrad(?:e|ing)\s+something\b[^\r\n\.;:!?]{0,60}\bpersonal\b"
                + @"|\btrad(?:e|ing)\s+(?:places|names|ideas|opinions)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool ContainsHistoricalWorldAssertion(string query)
        {
            string raw = (query ?? "").Trim();
            string q = raw.ToLowerInvariant().Replace('-', ' ').Replace('_', ' ');
            if (string.IsNullOrWhiteSpace(q)) return false;
            bool namedCurrentLocationAssertion = Regex.IsMatch(raw,
                @"\b(?:I|we)\s+(?:am|are|'m|'re)\s+(?:(?:currently|presently)\s+)?(?:in|at)\s+(?:the\s+)?\p{Lu}[\p{L}'-]{2,}",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            bool namedForceStrengthAssertion = Regex.IsMatch(raw,
                @"\b\p{Lu}[\p{L}'-]{2,}\s+(?:has|commands|leads|fields)\s+(?:(?:exactly|about|roughly|nearly|only)\s+)?\d+\s+(?:troops|men|soldiers)\b",
                RegexOptions.CultureInvariant);
            if (namedCurrentLocationAssertion || namedForceStrengthAssertion)
            {
                return true;
            }
            if (ContainsExplicitDeceptionAdmission(q) || ContainsAny(q,
                "i know that account is false",
                "i know this account is false", "i know that claim is false", "i know this claim is false",
                "what i just said is false", "what i said is false", "that account is false", "this account is false",
                "prove it", "that is a lie", "you are lying", "he is lying", "she is lying", "he claims", "she claims",
                "they claim", "traveler claims", "traveller claims"))
            {
                return true;
            }

            return Regex.IsMatch(q,
                @"\b(?:i|we)\s+(?:(?:personally|deliberately|actually|supposedly|reportedly)\s+){0,3}(?:was\s+at|was\s+not\s+at|were\s+at|fought|led|commanded|won|defeated|rescued|freed|released|captured|killed|wounded|raided|besieged|conquered|defended|paid|bought|sold|traded|married|met|entered|arrived|never\s+entered|never\s+went\s+to|have\s+never\s+been\s+to|am\s+not\s+presently\s+in|am\s+not\s+currently\s+in)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        internal static bool ContainsExplicitDeceptionAdmission(string query)
        {
            string q = (query ?? "").Trim().ToLowerInvariant().Replace('\u2019', '\'');
            if (string.IsNullOrWhiteSpace(q)) return false;
            if (ContainsAny(q,
                "i admit i lied", "i admit that i lied", "i admit i am lying", "i admit i'm lying",
                "we admit we lied", "we admit that we lied", "this is an admitted deception",
                "i deliberately lied", "i knowingly lied", "i intentionally lied",
                "we deliberately lied", "we knowingly lied", "we intentionally lied",
                "i intended to deceive", "we intended to deceive",
                "i meant to deceive", "we meant to deceive"))
            {
                return true;
            }

            // Bare "I am lying" is grammatically ambiguous: in roleplay it very
            // often describes posture (lying here/down/beneath/on a bed) rather
            // than deceptive speech. Require either an unambiguous past-tense
            // admission, an intent modifier, or an explicit communicative object.
            if (Regex.IsMatch(q,
                @"\b(?:i|we)\s+(?:lied|deceived|misled)\s+(?:to\s+)?(?:you|him|her|them|everyone|the\s+(?:court|king|queen|lord|lady))\b|\b(?:i|we)\s+(?:was|were)\s+lying\s+(?:to\s+)?(?:you|him|her|them|everyone)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;
            return Regex.IsMatch(q,
                @"\b(?:i|we)\s+(?:(?:am|are|'m|'re)\s+)?(?:openly|deliberately|knowingly|intentionally)\s+(?:(?:trying|attempting|intending|meant)\s+to\s+)?(?:lie|lied|lying|deceive|deceived|deceiving|mislead|misled|misleading)\b|\b(?:i|we)\s+(?:(?:am|are|'m|'re)\s+)?(?:trying|attempting|intending|meant)\s+to\s+(?:lie|deceive|mislead)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        internal static bool IsWorldHistoryLookupRequest(string query)
        {
            string q = (query ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(q)) return false;
            // Player prose and generated tests commonly hyphenate labels such as
            // "canonical-history". Normalize separators before phrase routing so
            // punctuation cannot silently suppress the authoritative history pull.
            q = q.Replace('-', ' ').Replace('_', ' ');
            bool explicitHistory = ContainsAny(q, "world history", "campaign history", "campaign record", "canonical history", "canonical campaign", "historical record", "according to the record", "the record says");
            bool historicalContext = ContainsAny(q,
                    "established history", "verified history", "known history",
                    "recorded history", "historical context", "history about", "history of")
                && ContainsAnyTerm(q,
                    "empire", "kingdom", "realm", "settlement", "town", "city",
                    "village", "castle", "clan", "war", "siege", "campaign");
            bool pastEventQuestion = ContainsAny(q, "what happened to", "who was responsible", "whose party was responsible", "who destroyed", "who defeated", "who captured", "who killed", "who conquered", "who besieged", "who rescued", "who led", "who fought");
            bool reportedClaim = ContainsAny(q, "traveler claims", "traveller claims", "he claims", "she claims", "they claim", "reportedly", "was said to have");
            return explicitHistory || historicalContext || pastEventQuestion || reportedClaim;
        }

        internal static List<Dictionary<string, object>> PruneContextPullSelection(string playerText, List<Dictionary<string, object>> selected)
        {
            string query = (playerText ?? "").Trim().ToLowerInvariant();
            if (selected == null || selected.Count == 0)
            {
                return new List<Dictionary<string, object>>();
            }

            if (IsNoPullGreeting(query))
            {
                return new List<Dictionary<string, object>>();
            }

            if (DialogueService.IsPureConversationRecallRequest(query))
            {
                // A reconstruction probe must be answered from the canonical transcript and
                // stored memory evidence. Re-fetching live map or campaign state can silently
                // supply the answer again, masking a broken recall path and changing historical
                // answers when the world has moved on.
                return selected
                    .Where(x => string.Equals(ReadString(x, "id", ""), "relevant_memory", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(ReadString(x, "id", ""), "relationship_history", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return selected
                .Where(x => ContextPullSupportedByText(ReadString(x, "id", ""), query))
                .ToList();
        }

        private static bool IsNoPullGreeting(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            string compact = query.Trim(' ', '.', '!', '?', ',', ';', ':', '"', '\'');
            if (compact.Length > 80)
            {
                return false;
            }

            // Greeting suppression must match the whole short utterance. Substring
            // matching classified words such as "anything" as the greeting "hi"
            // and could discard legitimate relationship or memory pulls.
            string[] greetings =
            {
                "hello", "hello there", "hi", "hi there", "hail", "greetings",
                "good day", "good morning", "good evening", "how are you",
                "how do you fare", "well met", "peace to you", "just passing through",
                "there is something i'd like to discuss", "i want to speak", "i would speak with you"
            };
            return greetings.Any(greeting => compact.Equals(greeting, StringComparison.OrdinalIgnoreCase));
        }

        private static bool ContextPullSupportedByText(string id, string query)
        {
            bool worldHistoryLookup = IsWorldHistoryLookupRequest(query);
            bool materialGiftOffer = HasExplicitMaterialGiftOffer(query);
            bool negatesTradeOffer = NegatesMaterialTradeOffer(query);
            bool occupationalTradeUsage = IsOccupationalTradeUsage(query);
            bool conversationalTradeUsage = IsConversationalTradeUsage(query);
            switch (id)
            {
                case "check_player_appearance_status":
                    return ContainsAny(query, "look", "appearance", "appear", "seem", "wear", "wearing", "clothes", "clothing", "armor", "robe", "dress", "rags", "poor", "rich", "status", "rank", "disguise",
                        "am i a lord", "am i a lady", "am i noble", "do i look noble", "do i seem noble");
                case "check_inventory_appearance":
                    return !worldHistoryLookup && (ContainsAnyTerm(query, "look", "appearance", "wear", "wearing", "clothes", "clothing", "armor", "inventory", "supplies", "supply", "food", "goods", "grain", "caravan", "wares", "sell", "buy", "price", "coin", "money", "wealth", "horses", "mounts", "poor", "rich")
                        || (!negatesTradeOffer && !occupationalTradeUsage && !conversationalTradeUsage && ContainsAnyTerm(query, "trade", "trading")));
                case "nearby_settlements":
                    return IsNearbySettlementRequest(query);
                case "nearby_bandit_parties":
                    return ContainsAny(query, "bandit", "bandits", "looter", "looters", "raider", "raiders", "outlaw", "outlaws", "hideout", "ambush", "safe road", "danger nearby", "danger on the road");
                case "nearby_lord_parties":
                    return (ContainsAnyTerm(query, "lord", "lords", "army", "armies", "party", "parties", "force", "forces", "troop", "troops", "patrol", "patrols")
                            && ContainsAnyTerm(query, "near", "nearby", "around", "close", "closest", "local", "locally"))
                        || ContainsAny(query, "lord nearby", "lords nearby", "nearby lord", "nearby lords", "army nearby", "armies nearby", "nearby army", "nearby armies",
                        "party nearby", "parties nearby", "nearby party", "nearby parties", "nearby force", "nearby forces", "troops nearby", "nearby troops",
                        "named forces", "troop groups", "patrol", "enemy party", "allied party", "who is near");
                case "current_settlement_facts":
                    return ContainsAny(query, "current settlement ownership", "current native settlement facts", "current settlement facts", "this town", "this city", "this village", "this castle", "this settlement", "current town", "current city", "current village", "current castle", "current governor", "who is governor", "under siege", "siege status", "market", "walls", "garrison", "militia", "prosperity", "security", "loyalty")
                        || ContainsAny(query, "current local", "local facts", "facts about", "conditions in", "situation in", "weather in", "weather and")
                        || ((ContainsAny(query, "right now", "currently") || ContainsAnyTerm(query, "today", "verify", "verified"))
                            && ContainsAnyTerm(query, "what", "tell", "know", "fact", "facts", "condition", "conditions", "situation", "weather", "verify", "verified"))
                        || ContainsAnyTerm(query, "governor", "governs", "governance", "besieged")
                        || (ContainsAnyTerm(query, "capture", "captured", "conquer", "conquered", "own", "owns", "owned", "owner", "ownership", "belong", "belongs", "belonged", "control", "controls", "controlled", "banner")
                            && ContainsAnyTerm(query, "town", "city", "village", "castle", "settlement", "empire", "kingdom", "realm"))
                        || (ContainsAny(query, "here", "local", "place") && ContainsAny(query, "market", "walls", "garrison", "militia", "prosperity", "security", "loyalty", "town", "city", "village", "castle", "settlement"));
                case "kingdom_diplomacy_status":
                    return (!worldHistoryLookup && ContainsAnyTerm(query, "war", "peace", "tribute", "truce", "kingdom", "realm", "enemy", "enemies", "ally", "allies", "diplomacy", "surrender", "reparations", "demands", "demand"))
                        || ContainsAny(query, "settlement demand", "campaign situation", "war situation", "currently at war", "at war now", "current diplomacy", "current war");
                case "clan_wealth_and_influence":
                    return ContainsAnyTerm(query,
                            "clan", "influence", "renown", "supporters", "backing", "fief", "fiefs",
                            "wealth", "money", "house", "rich", "poor")
                        || ContainsAny(query, "clan support", "political support", "family power");
                case "appraise_trade_offer":
                    return !worldHistoryLookup && (materialGiftOffer
                        || ContainsAnyTerm(query, "worth")
                        || (!negatesTradeOffer && (ContainsAnyTerm(query, "sell", "buy", "price", "purchase", "barter", "ransom", "ships", "fleet", "workshop")
                            || ContainsAny(query, "trade for", "in exchange", "denars for", "gold for", "how much", "pay you", "pay me", "item value", "trade value", "market value", "settlement for", "town for", "castle for"))));
                case "verify_world_history":
                    return worldHistoryLookup || ContainsHistoricalWorldAssertion(query);
                case "relevant_memory":
                    return DialogueService.LooksLikeMostRecentConversationRecall(query)
                        || ContainsAny(query, "memory", "remember", "remind me", "remind us", "last time", "earlier conversation", "earlier conversations", "first conversation", "first scene", "before", "again", "repeat", "trail-name", "trail name", "promise", "vow", "favor", "debt", "insult", "betray", "secret", "history", "past", "father", "mother", "husband", "wife", "child", "family");
                case "relationship_history":
                    return ContainsAny(query, "relationship", "trust", "friend", "enemy", "hate", "love", "marry", "marriage", "court", "jealous", "jealousy", "respect", "opinion", "flirt", "kiss", "attract", "desire", "seduc", "affair", "lover", "intimate", "beautiful", "handsome");
                default:
                    return false;
            }
        }

        internal static List<Dictionary<string, object>> NormalizeSelectedContextPulls(List<Dictionary<string, object>> raw, List<string> available)
        {
            List<Dictionary<string, object>> selected = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> item in raw ?? new List<Dictionary<string, object>>())
            {
                string id = ReadFirstString(item, "id", "pullId", "contextPullId");
                if (string.IsNullOrWhiteSpace(id) || !available.Contains(id) || selected.Any(x => string.Equals(ReadString(x, "id", ""), id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                selected.Add(new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["reason"] = LimitText(ReadString(item, "reason", ""), 240),
                    ["arguments"] = ReadDictionary(item, "arguments") ?? new Dictionary<string, object>()
                });
            }

            return selected;
        }

        private static List<string> NormalizeContextPullIds(List<string> ids)
        {
            HashSet<string> allowed = new HashSet<string>(Program.ContextPullIds, StringComparer.OrdinalIgnoreCase);
            return (ids ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Where(x => allowed.Contains(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
