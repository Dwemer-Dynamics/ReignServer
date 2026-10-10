using System;
using System.Text.RegularExpressions;

namespace Reign.Core.Contracts.Actions
{
    // Conversation direction reflects the adapter's actual actor, not who selected
    // the menu. Console/native operator capabilities retain their existing scope.
    public static class ReignActionConversationPolicy
    {
        public static bool Allows(ActionDefinition definition, bool playerActs, int existingTerms = 0)
        {
            if (!definition.ConversationEligible || definition.IsSetup) return false;
            if (definition.Id == "start_ruling_clan_rebellion") return playerActs && existingTerms == 0;
            if (definition.MovesActor && playerActs) return false;
            if (definition.Family == "court") return !playerActs;
            switch (definition.Id)
            {
                case "prepare_arrest": case "confirm_arrest": case "release_arrested_character":
                case "rescind_arrest_accusation": case "player_attack_party": case "join_rebellion":
                    return playerActs;
                case "duel_player": case "surrender_to_player": case "leave_player_alone":
                case "stop_following": case "recruit_encountered_resident":
                case "accept_temporary_party_guest": case "renew_temporary_party_guest":
                case "end_temporary_party_guest": case "acknowledge_own_faction_combat_risk":
                case "hire_player_as_mercenary": case "dismiss_player_mercenary":
                case "offer_player_vassalage": case "dismiss_player_vassal":
                case "pledge_rebellion_support":
                    return !playerActs;
                default: return true;
            }
        }

        public static bool AllowsParticipantSelection(string actionId, string key, string value, string actorId, string recipientId)
        {
            if (actionId == "marriage_alliance")
            {
                if (key == "terms.marriageHero1StringId") return value == actorId;
                if (key == "terms.marriageHero2StringId") return value == recipientId;
            }
            if (key == "TargetHero" || key == "targetHeroId" || key == "terms.targetHeroStringId")
            {
                if (value == actorId) return false;
                if (actionId == "prepare_arrest" || actionId == "confirm_arrest" || actionId == "release_arrested_character"
                    || actionId == "rescind_arrest_accusation" || actionId == "player_attack_party"
                    || actionId == "join_rebellion" || actionId == "recruit_lord_to_rebellion") return value == recipientId;
            }
            return true;
        }
    }

    public static class ReignActionLanguage
    {
        public static string Subject(bool playerActs, int termIndex) => termIndex == 0
            ? playerActs ? "I wish to " : "I would like you to "
            : playerActs ? "and I will " : "and you will ";

        // These are presentation phrases only. Signed action IDs, arguments and
        // native adapters remain unchanged and are never inferred from the wording.
        public static string Verb(ActionDefinition definition, bool playerActs)
        {
            switch (definition.Id)
            {
                case "duel_player": return "duel me";
                case "attack_player_party": return "attack my party";
                case "surrender_to_player": return "surrender to me";
                case "leave_player_alone": return "leave me alone";
                case "player_attack_party": return "attack your party";
                case "hire_player_as_mercenary": return "hire me as a mercenary";
                case "dismiss_player_mercenary": return "release me from mercenary service";
                case "offer_player_vassalage": return "accept my oath of vassalage";
                case "dismiss_player_vassal": return "release me from vassalage";
                case "give_gold_to_player": return "give me denars";
                case "recruit_encountered_resident": return "join my party as a permanent companion";
                case "accept_temporary_party_guest": return "join my party as a temporary guest";
                case "renew_temporary_party_guest": return "extend your stay in my party";
                case "end_temporary_party_guest": return "leave my party";
                case "acknowledge_own_faction_combat_risk": return "accept the risks of fighting your own faction";
                default:
                    string verb = definition.Label.TrimEnd('.', '…').ToLowerInvariant();
                    verb = Regex.Replace(verb, @"\bthe player['’]s\b|\bplayer['’]s\b", "my", RegexOptions.IgnoreCase);
                    return Regex.Replace(verb, @"\b(?:the )?player\b", "me", RegexOptions.IgnoreCase);
            }
        }

        public static string ChoiceLabel(ActionDefinition definition, bool playerActs)
        {
            string phrase = Verb(definition, playerActs);
            return phrase.Length == 0 ? phrase : char.ToUpperInvariant(phrase[0]) + phrase.Substring(1);
        }
    }
}
