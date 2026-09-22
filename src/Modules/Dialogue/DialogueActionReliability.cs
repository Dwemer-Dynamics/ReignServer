using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const double MaximumGuestTermDays = 126d;
        private const string DialogueActionExecutionRules =
            "ACTION MEANING AND NATIVE EXECUTION\n"
            + "A present accepted invitation to join the player's traveling group uses accept_temporary_party_guest, not a map order or permanent clan recruitment. "
            + "Preserve the agreed duration using campaignCalendar.daysPerSeason/daysPerWeek. With no fixed end date use open_ended, reviewed every five days. "
            + "A guest already in the player's party needs no repeated join action. Renew and end require that speaker's active native agreement. "
            + "Discussing a destination, traveling together, 'we will', a disguise, or another person's scouting duties does not order the addressed NPC's party. "
            + "Map commands require an actual direction to an eligible separate NPC party; never move the human main party. The follow command instead uses a present scene agent when native scene movement is available, and otherwise follows the resolved party on the campaign map. "
            + "Employment or banner cover does not grant clan membership, faction-combat consent, banishment, or settlement ownership. "
            + "For paid temporary service keep wageGold, wagePeriodDays and wageRecipientHeroStringId (or wageRecipientRelation=self/father/mother) in guest terms, with exact accepted amounts. Wages are paid after each completed period. Clarify a different payment schedule. Never turn wages into barter for a town. "
            + "An accepted service agreement that starts later uses accept_temporary_party_guest with deferStart=true; this records terms without moving the NPC. AwaitingStart needs a fresh accepted departure with startNow=true. Preserve pending terms unless a change is explicitly accepted. "
            + "A personal item gift uses transfer_item with delivery=personal. Use delivery=equip only when wearing/equipping the item was expressly requested and accepted; otherwise personal ownership must still change. "
            + "Keep item modifiers, quantity, source equipment slot/set and target equipment slot/set when specified. Do not sell or remove protected guest gear without that hero's explicit consent. "
            + "follow is context aware: a present scene agent follows the resolved scene target, otherwise an eligible separate hero party follows the resolved map party. show_the_way leads to a resolved scene hero or an exact native destination tag from scene hints. Never invent coordinates or tags. "
            + "Missing required terms need clarification; never invent payment amounts, duration, assets or identifiers. The planner records an accepted request, not completed effects. Only a native receipt establishes execution.";

        private static readonly HashSet<string> SeparatePartyCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "follow", "go_to_settlement", "patrol_around_settlement", "wait_near_settlement",
            "raid_village", "besiege_settlement", "attack_party", "attack_player_party",
            "surrender_to_player", "leave_player_alone", "recruit_and_recover", "form_army",
            "attack_settlement", "capture_settlement_plan", "issue_campaign_order"
        };

        private static bool SpeakerInPlayerParty(Dictionary<string, object> payload, Dictionary<string, object> hero)
        {
            string party = FirstNonEmpty(ReadFirstString(hero, "currentPartyId", "partyId"),
                ReadFirstString(payload, "speakerPartyId", "npcPartyId"));
            string playerParty = FirstNonEmpty(ReadFirstString(payload, "playerPartyId", "mainPartyId"), "player_party");
            return !string.IsNullOrWhiteSpace(party) && party.Equals(playerParty, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasDirectCampaignOrderRequest(string text)
        {
            // Scene narration is not a spoken order. The semantic planner still
            // determines acceptance, exact targets and authority after this gate.
            string spoken = Regex.Replace(text ?? "", @"\*[^*]*\*", " ");
            return Regex.IsMatch(spoken,
                @"(?:^|[.!?;]\s*)(?:\s*[\p{L}'-]+\s*,\s*)?\s*(?:(?:please|now)\s+|(?:can|could|will|would)\s+you\s+(?:please\s+)?|(?:I\s+(?:want|need|order|command)\s+you\s+to\s+)|you\s+(?:must|should|will)\s+)?"
                + @"(?:follow|patrol|scout|raid|besiege|defend|escort|withdraw|recruit|resupply|hunt|hold|wait|camp|go|move|head|march|return|attack|raise|gather|form|summon|relieve|take\s+your\s+(?:party|men|troops|army)|keep\s+an\s+eye\s+on|get\s+your\s+(?:men|troops|party)\s+together|call\s+(?:the|your)\s+(?:lords|army)|break\s+the\s+siege)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool IsSharedTravelOnly(Dictionary<string, object> gate, string playerText)
        {
            string text = NormalizeLookup(playerText);
            if (HasDirectCampaignOrderRequest(playerText)
                || !string.IsNullOrWhiteSpace(TemporaryPartyGuestCandidateToPreserve(gate, playerText))) return false;
            string intent = NormalizeLookup(ReadString(gate, "intent", ""));
            return Regex.IsMatch(text, @"\b(?:we|our route|our trip|together|let s)\b")
                && Regex.IsMatch(intent, @"\b(?:travel|detour|itinerary|destination|tournament|go to|ride to)\b")
                && !Regex.IsMatch(intent, @"\b(?:transfer|gift|marriage|clan membership|arrest|declare war|peace treaty)\b");
        }

        private static bool DialoguePlannerCandidateEligible(string command, Dictionary<string, object> payload,
            Dictionary<string, object> hero, Dictionary<string, object> gate, string playerText)
        {
            command = CanonicalCommand(command);
            bool sceneFollow = command == "follow"
                && ReadBool(ReadDictionary(payload, "nativeSceneMovement"), "available", false);
            if (SeparatePartyCommands.Contains(command) && !sceneFollow
                && (SpeakerInPlayerParty(payload, hero) || IsSharedTravelOnly(gate, playerText))) return false;
            if (command == "acknowledge_own_faction_combat_risk")
            {
                var guest = TemporaryGuestDialogueContext(payload,
                    FirstNonEmpty(ReadFirstString(payload, "speakerHeroStringId", "heroStringId"), ReadString(hero, "heroStringId", "")));
                return guest != null && ReadBool(guest, "inMainParty", false)
                    && Regex.IsMatch(NormalizeLookup(playerText), @"\b(?:accept (?:banishment|that risk)|fight(?:ing)? (?:your|our|my|their) own (?:realm|faction|clan)|own faction combat)\b");
            }
            return true;
        }

        private static void CompleteConversationActionTerms(Dictionary<string, object> action,
            Dictionary<string, object> payload, Dictionary<string, object> hero, string text)
        {
            // The hidden planner supplies negotiated terms. Test fixture defaults
            // must never add money, durations, fiefs or an unrelated target here.
            CompleteTestDirectiveActionDefaults(action, payload, hero, text, includeTestValues: false);
            var terms = ToCaseInsensitiveDictionary(ReadDictionary(action, "terms") ?? new Dictionary<string, object>());
            string command = CanonicalCommand(ReadString(action, "command", ""));
            if ((command == "transfer_item" || command == "transfer_gold" || command == "give_gold_to_player")
                && TransferDirectionNeedsResolution(action, terms))
                ApplyTransferDirectionDefaults(action, terms, payload, hero, text, command);
            action["terms"] = terms;
            if (command == "transfer_item") CompleteNamedGiftTerms(action, terms, payload, text);
            CompleteFallbackDirectiveTerms(action, payload, hero, text, includeTestValues: false);
        }

        private static bool HasExplicitCurrentSettlementReference(string text)
            => Regex.IsMatch(NormalizeLookup(text), @"\b(?:this|current|our present) (?:town|village|castle|settlement)|\b(?:wait|stay|hold|patrol|defend) here\b");

        private static bool TransferDirectionNeedsResolution(Dictionary<string, object> action, Dictionary<string, object> terms)
            => FirstNonEmpty(ReadFirstString(action, "FromHero", "fromHero", "actorHeroStringId", "actorHeroId"),
                    ReadFirstString(terms, "fromHeroStringId", "FromHero")).Length == 0
                || FirstNonEmpty(ReadFirstString(action, "ToHero", "toHero", "targetHeroStringId", "targetHeroId", "TargetHero"),
                    ReadFirstString(terms, "toHeroStringId", "ToHero")).Length == 0;

        private static string ClassifyDialogueQueueOutcome(int candidateCount, int queuedCount, bool actionable, List<string> errors)
        {
            if (queuedCount > 0) return "queued_awaiting_native";
            if (errors != null && errors.Count > 0) return ClassifyActionFailure(candidateCount, errors);
            return actionable ? "no_executable_action" : "no_action_detected";
        }

        private static List<Dictionary<string, object>> ExecutableRouterCandidates(
            List<Dictionary<string, object>> candidates, string campaignId,
            Dictionary<string, object> payload, string resolutionText)
        {
            return candidates.Where(candidate =>
            {
                var normalized = NormalizeActionCommand(CloneDictionary(candidate), campaignId,
                    out List<string> errors, payload, resolutionText);
                return normalized != null && errors.Count == 0;
            }).ToList();
        }

        private static List<Dictionary<string, object>> PreferResolvedRouterCandidates(
            List<Dictionary<string, object>> resolved, List<Dictionary<string, object>> fallback)
        {
            var result = new List<Dictionary<string, object>>();
            foreach (var candidate in resolved.Concat(fallback)) AppendUniqueActionCandidate(result, candidate);
            return result;
        }

        private static bool TryCompleteAcceptedGuestSchedule(Dictionary<string, object> terms,
            Dictionary<string, object> payload, string exchange)
        {
            string text = NormalizeLookup(exchange);
            var calendar = ReadDictionary(payload, "campaignCalendar");
            bool openEnded = ContainsAnyNormalized(text, "open ended", "no fixed end date", "every five days");
            double days = openEnded ? 0 : ExtractItemAmount(text, "days?");
            bool fixedSeason = Regex.IsMatch(text, @"\bfor (?:the|a|one|1|this) season\b|\b(?:full|one) season (?:of )?(?:service|travel)\b");
            if (!openEnded && fixedSeason) days = ReadDouble(calendar, "daysPerSeason", 0);
            var explicitDays = Regex.Match(exchange ?? "", @"(?<![\w.\d])(\d+(?:\.\d+)?)\s+days?\b", RegexOptions.IgnoreCase);
            if (!openEnded && explicitDays.Success && double.TryParse(explicitDays.Groups[1].Value,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double exactDays)) days = exactDays;
            if ((!openEnded && fixedSeason && days <= 0) || double.IsNaN(days) || double.IsInfinity(days)
                || days > MaximumGuestTermDays || (explicitDays.Success && days < 1)) return false;
            if (days > 0) { terms["termKind"] = "fixed"; terms["durationDays"] = days; }
            else
            {
                // An unspecified period has no invented end date. Explicit but
                // unresolved dates/lengths must be clarified by the semantic planner.
                if (!openEnded && Regex.IsMatch(text, @"\b(?:until|for \w+ (?:weeks?|months?|years?))\b")) return false;
                terms["termKind"] = "open_ended";
                terms["reviewIntervalDays"] = 5;
            }
            bool deferred = Regex.IsMatch(text,
                @"\b(?:return for you|come back for you|back for you|when (?:I|he|she|the player) returns?|when you (?:do )?join|after (?:the |that )?tournament|future start)\b", RegexOptions.IgnoreCase);
            if (deferred) terms["deferStart"] = true;
            else terms["startNow"] = true;
            const string numberWord = @"(?:one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred|thousand|million|and)";
            var wages = Regex.Matches(exchange ?? "", @"\b(\d[\d,]*|" + numberWord + @"(?:[\s-]+" + numberWord + @")*)\s*(?:denars?|gold)?\s*(?:a|per|each)\s*(day|week|season|year)\b", RegexOptions.IgnoreCase);
            if (wages.Count > 0)
            {
                var distinct = wages.Cast<Match>().Select(x => ExtractMoneyAmount(x.Groups[1].Value + " gold", 0)
                    + ":" + x.Groups[2].Value.ToLowerInvariant()).Distinct().ToList();
                int amount = ExtractMoneyAmount(wages[0].Groups[1].Value + " gold", 0);
                if (distinct.Count != 1 || amount <= 0) return false;
                string unit = wages[0].Groups[2].Value.ToLowerInvariant();
                double period = unit == "day" ? 1 : ReadDouble(calendar,
                    unit == "week" ? "daysPerWeek" : unit == "season" ? "daysPerSeason" : "daysPerYear", 0);
                if (period <= 0 || period > MaximumGuestTermDays || double.IsNaN(period) || double.IsInfinity(period)
                    || Regex.IsMatch(text, @"\b(?:up front|upfront|in advance|before (?:we |you |I )?leave)\b")) return false;
                terms["wageGold"] = amount;
                terms["wagePeriodDays"] = period;
                bool father = Regex.IsMatch(text, @"\b(?:to|paid to) (?:my|her|his|your) father\b");
                bool mother = Regex.IsMatch(text, @"\b(?:to|paid to) (?:my|her|his|your) mother\b");
                if (father && mother) return false;
                terms["wageRecipientRelation"] = father ? "father" : mother ? "mother" : "self";
            }
            else if (Regex.IsMatch(text, @"\b(?:wages?|salary|denars?|gold|paid service)\b"))
                return false; // Never recover a paid agreement as free service.
            return true;
        }

        private static void BindAcceptedItemDelivery(Dictionary<string, object> action, string speaker)
        {
            if (CanonicalCommand(ReadString(action, "command", "")) != "transfer_item" || string.IsNullOrWhiteSpace(speaker)) return;
            action["acceptedByHeroStringId"] = speaker;
            var terms = ToCaseInsensitiveDictionary(ReadDictionary(action, "terms") ?? new Dictionary<string, object>());
            // Native validation compares the accepted speaker to the actual source
            // or recipient. A third party's permission cannot change protected gear.
            terms["sourceConsentConfirmed"] = true;
            if (ReadString(terms, "delivery", "").Equals("equip", StringComparison.OrdinalIgnoreCase))
                terms["recipientConsentConfirmed"] = true;
            action["terms"] = terms;
        }

        private static void ValidateGuestServiceTerms(Dictionary<string, object> terms, List<string> errors)
        {
            if (ReadBool(terms, "startNow", false) && ReadBool(terms, "deferStart", false))
                errors.Add("A guest agreement cannot both start now and defer its start.");
            if (!terms.ContainsKey("wageGold") && !terms.ContainsKey("wagePeriodDays")) return;
            double gold = ReadDouble(terms, "wageGold", 0), period = ReadDouble(terms, "wagePeriodDays", 0);
            if (double.IsNaN(gold) || double.IsInfinity(gold) || gold <= 0 || gold > int.MaxValue || gold != Math.Truncate(gold))
                errors.Add("Guest wages require an exact positive integer wageGold.");
            if (double.IsNaN(period) || double.IsInfinity(period) || period < 1 || period > 365)
                errors.Add("Guest wages require a resolved wagePeriodDays from 1 through 365.");
            string relation = ReadString(terms, "wageRecipientRelation", "self");
            if (!new[] { "self", "father", "mother" }.Contains(relation))
                errors.Add("Guest wages require self, father, mother or an exact wageRecipientHeroStringId.");
        }

        private static List<Dictionary<string, object>> ActionInventoryItems(Dictionary<string, object> inventory)
            => inventory != null && inventory.ContainsKey("items")
                ? ReadDictionaryList(inventory, "items") : ReadDictionaryList(inventory, "topItems");

        private static List<Dictionary<string, object>> MentionedActionItems(Dictionary<string, object> index, string text)
        {
            var rows = new List<Dictionary<string, object>>();
            string normalized = NormalizeLookup(text);
            var assets = ReadDictionary(index, "assets");
            foreach (string side in new[] { "player", "speaker" })
            {
                var holder = ReadDictionary(assets, side);
                var appearance = ReadDictionary(holder, "appearance");
                foreach (var row in ActionInventoryItems(ReadDictionary(holder, "inventory"))
                    .Concat(ReadDictionaryList(appearance, "civilianEquipment")).Concat(ReadDictionaryList(appearance, "battleEquipment")))
                {
                    string name = NormalizeLookup(ReadString(row, "name", ""));
                    string id = NormalizeLookup(ReadString(row, "itemId", ""));
                    if ((name.Length < 3 || !NormalizedTextContainsPhrase(normalized, name))
                        && (id.Length < 3 || !NormalizedTextContainsPhrase(normalized, id))) continue;
                    var copy = CloneDictionary(row);
                    copy["holder"] = side;
                    rows.Add(copy);
                }
            }
            return rows.Take(20).ToList();
        }

        private static bool HasNativeItemGiftRequest(Dictionary<string, object> payload, string text)
            => Regex.IsMatch(NormalizeLookup(Regex.Replace(text ?? "", @"\*[^*]*\*", " ")),
                @"\b(?:give|gift|hand|take|offer|donate|here is|here s)\b")
                && MentionedActionItems(ReadDictionary(payload, "actionResolutionIndex"), text).Count > 0;

        private static Dictionary<string, object> BuildAcceptedNamedGiftFallback(Dictionary<string, object> gate,
            Dictionary<string, object> payload, Dictionary<string, object> hero, string playerText, HashSet<string> allowed)
        {
            if (!allowed.Contains("transfer_item") || !HasNativeItemGiftRequest(payload, playerText)
                || !ShouldPreserveAcceptedItemGift(gate, playerText, CommandFromDirectiveText(playerText))) return null;
            var action = TestDict("command", "transfer_item", "source", "accepted_named_gift_fallback",
                "reason", "The speaker accepted the specifically offered native item.");
            CompleteConversationActionTerms(action, payload, hero, playerText);
            var terms = ReadDictionary(action, "terms");
            if (ReadString(terms, "itemId", "").Length == 0 || ReadInt(terms, "amount", 0) <= 0) return null;
            BindAcceptedItemDelivery(action, ReadFirstString(payload, "speakerHeroStringId", "heroStringId"));
            return ExecutableRouterCandidates(new List<Dictionary<string, object>> { action },
                ReadString(payload, "campaignId", ""), payload, playerText).FirstOrDefault();
        }

        private static void CompleteNamedGiftTerms(Dictionary<string, object> action, Dictionary<string, object> terms,
            Dictionary<string, object> payload, string text)
        {
            if (ReadFirstString(action, "Item", "itemId").Length > 0 || ReadFirstString(terms, "itemId", "item").Length > 0) return;
            string from = FirstNonEmpty(ReadFirstString(terms, "fromHeroStringId"), ReadFirstString(action, "FromHero", "actorHeroStringId", "actorHeroId"));
            string holder = from == ReadString(payload, "playerHeroStringId", "") ? "player"
                : from == ReadFirstString(payload, "speakerHeroStringId", "heroStringId") ? "speaker" : "";
            if (holder.Length == 0) return;
            var rows = MentionedActionItems(ReadDictionary(payload, "actionResolutionIndex"), text)
                .Where(row => ReadString(row, "holder", "") == holder).ToList();
            if (rows.Select(row => ReadString(row, "itemId", "")).Distinct().Count() != 1) return;
            var row = rows[0];
            string itemId = ReadString(row, "itemId", ""), name = ReadString(row, "name", itemId);
            action["Item"] = itemId;
            terms["itemId"] = itemId;
            if (rows.Count == 1) ApplyResolvedItemTerms(terms, row);
            int amount = ExtractInventoryItemAmount(text, name);
            if (amount == 0 && Regex.IsMatch(text ?? "", @"\b(?:the|a|an|one|my|this|that)\s+" + Regex.Escape(name) + @"\b", RegexOptions.IgnoreCase)) amount = 1;
            if (amount > 0) AddIfMissing(terms, "amount", amount);
        }
    }
}
