using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string MarriageDialogueCompletionContract =
            "Native spouse identity is authoritative. Accepted proposals, vows, a narrated priest or ceremony, "
            + "and earlier dialogue are not proof that a marriage completed. Never call the player your spouse "
            + "unless current native spouse data confirms it. If both people consent to completing their marriage "
            + "now and it is not confirmed, mark actionGate needed=true, commitment=accepted and identify the exact "
            + "couple in intent. A request to perform the agreed ceremony is still executable, not roleplay_only. "
            + "Questions, hypotheticals, refusals and agreements about other relatives do not authorize this couple.";

        private static bool IsCanonicalRouterArgument(string key)
        {
            string compact = Regex.Replace(key ?? "", "[^a-zA-Z0-9]", "").ToLowerInvariant();
            if (compact.StartsWith("terms", StringComparison.Ordinal)) return true;
            switch (compact)
            {
                case "source": case "reason": case "confidence": case "command": case "action":
                case "type": case "intent": case "capability": case "requiresacceptance":
                case "accepted": case "committed": case "executenow": case "npcaccepted":
                case "actionid": case "serveractionid": case "correlationid": case "requestid":
                case "maxattempts": case "executeafterdays": case "authorizationmode":
                case "negotiationid": case "acceptedbyherostringid":
                case "actorheroid": case "actorherostringid": case "targetheroid": case "targetherostringid":
                case "actorkingdomid": case "actorkingdomstringid": case "targetkingdomid": case "targetkingdomstringid":
                case "actorclanid": case "actorclanstringid": case "targetclanid": case "targetclanstringid":
                case "targetsettlementid": case "targetsettlementstringid":
                case "marriagehero1stringid": case "marriagehero2stringid":
                    return true;
                default: return false;
            }
        }

        private static void BindDialogueMarriageParticipants(Dictionary<string, object> raw,
            Dictionary<string, object> terms, Dictionary<string, object> payload,
            string command, List<string> errors)
        {
            if (command != "marriage_alliance" || !IsDialogueActionSource(ReadString(raw, "source", ""))) return;
            Dictionary<string, object> hero = ReadDictionary(payload, "hero") ?? new Dictionary<string, object>();
            string player = FirstNonEmpty(ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"),
                ReadFirstString(hero, "mainHeroStringId", "playerHeroStringId"));
            string speaker = FirstNonEmpty(ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ReadString(hero, "heroStringId", ""));
            string actor = ReadFirstString(raw, "actorHeroStringId", "actorHeroId", "FromHero");
            string target = ReadFirstString(raw, "targetHeroStringId", "targetHeroId", "TargetHero", "ToHero");
            string first = FirstNonEmpty(ReadString(terms, "marriageHero1StringId", ""), ReadString(raw, "marriageHero1StringId", ""), actor);
            string second = FirstNonEmpty(ReadString(terms, "marriageHero2StringId", ""), ReadString(raw, "marriageHero2StringId", ""), target);
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            {
                errors.Add("Personal marriage requires both exact spouses: terms.marriageHero1StringId and terms.marriageHero2StringId from the live hero index. Clan IDs or spouse names alone cannot identify the consenting couple.");
                return;
            }
            if ((!string.IsNullOrWhiteSpace(actor) && !actor.Equals(first, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(target) && !target.Equals(second, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("Marriage actor/target identities conflict with the exact spouse terms.");
                return;
            }
            if (!IsPersonalMarriagePair(first, second, player, speaker))
            {
                errors.Add("Personal marriage consent binds only the player and the agreeing NPC; a relative or third party cannot be substituted.");
                return;
            }
            foreach (string key in new[] { "requestedMarriageHeroStringId", "marriageHeroStringId",
                "playerMarriageHeroStringId", "playerFamilyHeroStringId", "groomHeroStringId", "proposingHeroStringId",
                "npcMarriageHeroStringId", "otherFamilyHeroStringId", "brideHeroStringId", "spouseHeroStringId", "proposedSpouseHeroStringId" })
            {
                string named = FirstNonEmpty(ReadString(terms, key, ""), ReadString(raw, key, ""));
                if (!string.IsNullOrWhiteSpace(named) && !named.Equals(player, StringComparison.OrdinalIgnoreCase)
                    && !named.Equals(speaker, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("An additional marriage identity names someone outside the consenting couple: " + key + ".");
                    return;
                }
            }
            Dictionary<string, object> index = ReadDictionary(payload, "actionResolutionIndex");
            Dictionary<string, object> playerRow = FindIndexedRow(index, "heroes", "heroStringId", player);
            Dictionary<string, object> speakerRow = FindIndexedRow(index, "heroes", "heroStringId", speaker);
            if (playerRow == null || speakerRow == null)
            {
                errors.Add("Both consenting spouses must exist in the live hero index.");
                return;
            }
            // Use one canonical ordering and native affiliations. A planner's swapped
            // clans or stale kingdom cannot move this personal agreement to another pair.
            raw["actorHeroStringId"] = player;
            raw["targetHeroStringId"] = speaker;
            raw["actorClanStringId"] = ReadFirstString(playerRow, "clanId", "clanStringId");
            raw["targetClanStringId"] = ReadFirstString(speakerRow, "clanId", "clanStringId");
            raw["actorKingdomStringId"] = ReadFirstString(playerRow, "kingdomId", "kingdomStringId");
            raw["targetKingdomStringId"] = ReadFirstString(speakerRow, "kingdomId", "kingdomStringId");
            terms["marriageHero1StringId"] = player;
            terms["marriageHero2StringId"] = speaker;
        }

        private static bool IsPersonalMarriagePair(string first, string second, string player, string speaker)
        {
            return !string.IsNullOrWhiteSpace(player) && !string.IsNullOrWhiteSpace(speaker)
                && !player.Equals(speaker, StringComparison.OrdinalIgnoreCase)
                && ((player.Equals(first, StringComparison.OrdinalIgnoreCase) && speaker.Equals(second, StringComparison.OrdinalIgnoreCase))
                    || (player.Equals(second, StringComparison.OrdinalIgnoreCase) && speaker.Equals(first, StringComparison.OrdinalIgnoreCase)));
        }

        private static string BindPersonalMarriageConsent(Dictionary<string, object> record,
            Dictionary<string, object> terms, string speaker, string player, List<string> errors)
        {
            if (!IsPersonalMarriagePair(ReadString(record, "actorHeroStringId", ""),
                    ReadString(record, "targetHeroStringId", ""), player, speaker)
                || !player.Equals(ReadString(terms, "marriageHero1StringId", ""), StringComparison.OrdinalIgnoreCase)
                || !speaker.Equals(ReadString(terms, "marriageHero2StringId", ""), StringComparison.OrdinalIgnoreCase))
                errors.Add("Marriage authority requires the exact consenting player/NPC couple in both the action and spouse terms.");
            return "personal_marriage_consent";
        }

        private static bool NativePlayerMarriageConfirmed(Dictionary<string, object> payload, Dictionary<string, object> hero)
        {
            string player = FirstNonEmpty(ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"),
                ReadFirstString(hero, "mainHeroStringId", "playerHeroStringId"));
            string speaker = FirstNonEmpty(ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ReadString(hero, "heroStringId", ""));
            if (string.IsNullOrWhiteSpace(player) || string.IsNullOrWhiteSpace(speaker)) return false;
            Dictionary<string, object> index = ReadDictionary(payload, "actionResolutionIndex");
            Dictionary<string, object> playerRow = FindIndexedRow(index, "heroes", "heroStringId", player);
            Dictionary<string, object> speakerRow = FindIndexedRow(index, "heroes", "heroStringId", speaker) ?? hero;
            return player.Equals(ReadString(speakerRow, "spouseId", ""), StringComparison.OrdinalIgnoreCase)
                && speaker.Equals(ReadString(playerRow, "spouseId", ""), StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasUnconfirmedMarriageClaim(string reply, Dictionary<string, object> payload,
            Dictionary<string, object> hero)
        {
            string unquoted = Regex.Replace(reply ?? "", "[\\\"“][^\\\"”]*[\\\"”]|(?<!\\w)'[^']*'(?!\\w)", "");
            return Regex.IsMatch(unquoted,
                @"(?:^|[.!?]\s+|\n\s*)(?:(?:I am|I'm)(?: now)? your (?:wife|husband|spouse)(?: now)?|(?:we are|we're) (?:now )?married(?: now)?)[.!](?:\s|$)",
                RegexOptions.IgnoreCase) && !NativePlayerMarriageConfirmed(payload, hero);
        }

        private static string FinalizeDialogueActionOutcome(string reply, Dictionary<string, object> payload,
            Dictionary<string, object> hero, List<Dictionary<string, object>> queued, List<string> errors)
        {
            bool queuedMarriage = (queued ?? new List<Dictionary<string, object>>()).Any(row =>
                ReadString(ReadDictionary(row, "record") ?? row, "command", "") == "marriage_alliance");
            bool unconfirmedClaim = HasUnconfirmedMarriageClaim(reply, payload, hero);
            if (unconfirmedClaim)
            {
                // A fabricated completion must not enter the transcript as NPC fact.
                return queuedMarriage
                    ? "[Reign: Marriage agreed; awaiting confirmation from the game.]"
                    : "[Reign: The marriage has not been confirmed in the game. The ceremony described in dialogue did not complete it.]";
            }
            if ((queued == null || queued.Count == 0) && errors != null && errors.Count > 0)
                return (reply ?? "").TrimEnd() + "\n\n[Reign could not carry out the agreed action. No game action was queued.]";
            return reply;
        }
    }
}
