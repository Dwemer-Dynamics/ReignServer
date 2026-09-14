using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string ConversationNaturalnessContract =
            "Let the actual exchange set the stakes. World danger and court intrigue are possibilities, not a requirement to turn every greeting, hobby, kindness, apology, or personal question into a test, bargain, threat, or hidden price. " +
            "Preserve the speaker's individual temperament, loyalties, suspicion, humor, warmth, pride and ability to refuse. Answer the player's immediate meaning before pursuing another motive. Plain answers and ordinary conversation are compatible with nobility. " +
            "If the player asks what you mean, explain the concrete request, person, event or concern in ordinary words; do not answer confusion with another metaphor or treat clarification as evasion. " +
            "Read both sides of the recent exchange. An answer already received remains received; a correction already acknowledged remains acknowledged. Distinguish that settled point from any separate outstanding request. Old comprehension, suspicions and summaries are fallible past interpretations; update them from later dialogue instead of treating them as facts about the player's intent. " +
            "Let an accepted apology close its issue. A refusal can remain firm without repeating the entire accusation or adding another test. Advance the conversation, or end it naturally when there is nothing more to say. " +
            "Use quantities when they convey actual requested information, including prices, debts, inventory and troop counts. Do not keep tallying answers, questions, apologies or favors, or repeatedly express conversation as coins, ledgers, tolls and payments. Earlier figurative wording is not a permanent personality rule. " +
            "In a group, respond from this speaker's own knowledge, interests and relationship. Another NPC's accusation is their view, not established fact or a verdict the group must repeat. Agreement may be brief; add a distinct relevant contribution rather than another version of the same lecture. " +
            "These rules scope general tone instructions to the present conversation; they do not override verified facts, character identity, explicit boundaries, or action authority. Apply corrections to private comprehension, state and memory as well as the visible reply.";

        // These exact reviewed legacy sentences are scoped at composition time, including
        // installed prompt copies. No prompt file or unfamiliar surrounding customization is rewritten.
        private static readonly string[,] ConversationToneReplacements =
        {
            { "Every scene must carry social danger, political consequence, moral compromise, and the constant possibility of erotic leverage.",
              "Let social danger, political consequence and moral compromise arise when the people and situation support them; ordinary conversation can remain ordinary." },
            { "Characters speak with courtly polish while concealing literal and figurative knives behind every word, smile, and caress.",
              "Characters may use courtly polish, guarded speech or direct sincerity according to their personality, relationship and present stakes." },
            { "Keep dialogue sharp, restrained, and dense with implication and double meaning; nobles almost never say exactly what they mean.",
              "Keep dialogue clear and specific. Use implication when the speaker has a reason to conceal something; nobles can say exactly what they mean." },
            { "Layered motives drive every action.", "Layered motives matter when the scene gives them a reason to surface." },
            { "Every option exacts a cost—reputation, flesh, loyalty, heirs, or power.",
              "Serious political choices can exact costs in reputation, loyalty, heirs or power; a casual exchange need not become a transaction." },
            { "Every NPC evaluates situations through survival pressure first. They ask themselves:",
              "When a situation threatens their survival or security, NPCs consider the actual pressure. In those situations they may ask themselves:" }
        };

        private static string ScopeConversationTone(string text)
        {
            string result = text ?? "";
            for (int index = 0; index < ConversationToneReplacements.GetLength(0); index++)
                result = result.Replace(ConversationToneReplacements[index, 0], ConversationToneReplacements[index, 1]);
            return result;
        }

        private static string ConversationSpeech(string text)
        {
            return Regex.Replace(text ?? "", @"\*[^*]*\*", " ").Replace('\u2019', '\'');
        }

        private static bool IsConversationAccounting(string text)
        {
            string speech = ConversationSpeech(text);
            const string account = @"\b(?:count(?:ed|ing|s)?|tall(?:y|ies|ied|ying)|ledger[s]?|debts?|coins?|tolls?|price|pay|paid|payment|owe[sd]?|spen[dt])\b";
            const string social = @"\b(?:answers?|questions?|apolog(?:y|ies|ize)|courtesy|names?|words?|want|say|said|intentions?|kindness|affection|honesty|sincerity|flattery)\b";
            if (Regex.IsMatch(speech, @"\bcount\w*\s+(?:the\s+)?names\s+(?:on|in|from)\s+(?:the\s+)?(?:roll|roster|register)\b", RegexOptions.IgnoreCase)) return false;
            return Regex.IsMatch(speech, account + @"[^.!?;\r\n]{0,130}" + social + "|" + social + @"[^.!?;\r\n]{0,130}" + account, RegexOptions.IgnoreCase)
                || Regex.IsMatch(speech, @"\b(?:third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|three|four|five|six|seven|eight|nine|ten)\s+times?\b[^.!?\r\n]{0,100}\b(?:answer|question|ask|apolog|said|say)\w*\b|\b(?:answer|question|ask|apolog)\w*\b[^.!?\r\n]{0,100}\b(?:third|fourth|fifth|sixth)\s+time\b", RegexOptions.IgnoreCase);
        }

        private static bool RequestsLiteralAccounting(string text)
        {
            // An explicit question about quantities or a current financial/military subject
            // keeps the existing continuity rules without the figurative-loop heuristic.
            return Regex.IsMatch(ConversationSpeech(text), @"\b(?:how many|how much|count|counting|number of|price|prices|cost|costs|denars?|gold|coins?|debt|debts|loan|loans|payment|payments|wages|salary|tax|taxes|inventory|troops|soldiers|rations|grain|buy|sell|bargain|trade)\b", RegexOptions.IgnoreCase);
        }

        private static bool IsNaturalnessSpeaker(Dictionary<string, object> line, string heroId, string heroName)
        {
            if (!ReadString(line, "role", "").Equals("npc", StringComparison.OrdinalIgnoreCase)) return false;
            string id = ReadFirstString(line, "speaker_id", "speakerHeroStringId", "heroStringId");
            return !string.IsNullOrWhiteSpace(id)
                ? string.Equals(id, heroId, StringComparison.OrdinalIgnoreCase)
                : string.Equals(ReadFirstString(line, "speaker", "speaker_name"), heroName, StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> BuildConversationNaturalnessContext(string playerText, bool group)
        {
            string speech = ConversationSpeech(playerText);
            bool clarification = Regex.IsMatch(speech, @"\b(?:what (?:do|did) you (?:mean|ask)|what did you ask|what are you (?:asking|talking)|(?:do not|don't|dont|didn't|didnt) understand|(?:confused|confusing)|explain (?:that|what|it))\b", RegexOptions.IgnoreCase);
            bool correction = Regex.IsMatch(speech, @"\b(?:wasn'?t implying|was not implying|(?:that is|that's|thats) not what|already (?:said|told|answered)|because of what you said|(?:you|I) misunderstood)\b", RegexOptions.IgnoreCase);
            bool apology = Regex.IsMatch(speech, @"\b(?:sorry|apologi[sz]e|my mistake|forgive me)\b", RegexOptions.IgnoreCase);
            var hints = new List<string>();
            if (clarification) hints.Add("The latest player message asks for clarification. Explain your actual meaning and any outstanding request concretely before moving on. Confusion alone is not refusal or evasion.");
            if (correction) hints.Add("The latest player message corrects an interpretation. Reconcile it with both sides of the transcript and any earlier acknowledgement; do not promote your older suspicion into an established fact.");
            if (apology) hints.Add("The latest player message includes an apology. Decide whether to accept it in character; avoid adding a new tally, debt or repeated demand merely to continue the exchange.");
            if (group) hints.Add("This is a shared conversation. Treat earlier speakers' opinions as their own; give this NPC a distinct response to the current player message and relevant contributions.");
            return new Dictionary<string, object>
            {
                ["schema"] = "reign-conversation-naturalness-v1",
                ["clarification"] = clarification, ["correction"] = correction, ["apology"] = apology,
                ["group"] = group,
                ["prompt"] = hints.Count == 0 ? "" : "CURRENT CONVERSATION GUIDANCE\n" + string.Join("\n", hints)
            };
        }

        private static List<Dictionary<string, object>> FindConversationNaturalnessViolations(
            Dictionary<string, object> parsed, string playerText, List<Dictionary<string, object>> ownHistory,
            List<Dictionary<string, object>> sharedHistory, string heroId, string heroName)
        {
            var result = new List<Dictionary<string, object>>();
            string reply = ReadFirstString(parsed, "reply", "response", "text", "content");
            if (!IsConversationAccounting(reply) || RequestsLiteralAccounting(playerText)) return result;
            var own = (ownHistory ?? new List<Dictionary<string, object>>())
                .Where(row => IsNaturalnessSpeaker(row, heroId, heroName)).Reverse().Take(3).ToList();
            var repeated = own.Where(row => IsConversationAccounting(ReadString(row, "text", ""))).ToList();
            var shared = sharedHistory ?? new List<Dictionary<string, object>>();
            // Only another NPC's contribution after this exact current player message
            // qualifies as a same-beat echo. Never borrow private or older group claims.
            int currentPlayer = shared.FindLastIndex(row => ReadString(row, "role", "").Equals("player", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ReadString(row, "text", "").Trim(), (playerText ?? "").Trim(), StringComparison.Ordinal));
            var echoes = currentPlayer < 0 ? new List<Dictionary<string, object>>() : shared.Skip(currentPlayer + 1)
                .Where(row => ReadString(row, "role", "").Equals("npc", StringComparison.OrdinalIgnoreCase)
                    && !IsNaturalnessSpeaker(row, heroId, heroName)
                    && IsConversationAccounting(ReadString(row, "text", ""))).Take(1).ToList();
            bool escalatedTally = Regex.IsMatch(ConversationSpeech(reply), @"\b(?:third|fourth|fifth|sixth|three|four|five|six)\s+times?\b", RegexOptions.IgnoreCase);
            if (repeated.Count < 2 && !(repeated.Count == 1 && escalatedTally) && echoes.Count == 0) return result;
            result.Add(new Dictionary<string, object>
            {
                ["type"] = echoes.Count > 0 ? "group_conversation_accounting_echo" : "repeated_conversation_accounting",
                ["match"] = LimitText(ConversationSpeech(reply).Trim(), 700),
                ["detail"] = "The reply repeats a recent conversational tally or debt/payment metaphor. Answer the current message plainly in this character's voice, respecting received answers, corrections and any separate unresolved request. Preserve legitimate refusal and actual financial facts; remove the repeated framing rather than merely changing its words.",
                ["priorEvidence"] = repeated.Concat(echoes).Take(3).Select(row => new Dictionary<string, object>
                {
                    ["speaker"] = ReadFirstString(row, "speaker", "speaker_name"),
                    ["turnId"] = ReadFirstString(row, "turn_id", "turnId", "id"),
                    ["text"] = LimitText(ConversationSpeech(ReadString(row, "text", "")).Trim(), 1100)
                }).ToList()
            });
            return result;
        }

        private static bool IsGeneratedConversationRhetoric(Dictionary<string, object> row)
        {
            // Authored foundations and unfamiliar provenance are never silently reclassified.
            string provenance = NormalizeLookup(ReadFirstString(row, "provenance", "sourceBasis")).Replace(' ', '_');
            return provenance == "npc_generated_personal_history" && IsConversationCharacteristicRhetoric(ReadString(row, "text", ""));
        }

        private static bool IsConversationCharacteristicRhetoric(string text)
        {
            // A merchant's views on grain prices or a soldier's inventory habit can be
            // literal personal characterization. Prefer retaining an uncertain detail.
            bool materialSubject = Regex.IsMatch(text ?? "", @"\b(?:denars?|wages|taxes|loans?|inventory|merchandise|grain|rations|supplies|troops|soldiers|livestock)\b", RegexOptions.IgnoreCase);
            return !materialSubject && IsConversationAccounting(text);
        }

        private static bool DynamicCharacteristicsEquivalent(string left, string right)
        {
            if (NormalizeLookup(left) == NormalizeLookup(right)) return true;
            // Keep polarity, quantities and changed particulars: lexical similarity alone
            // must not merge contradictions or different memories on the same topic.
            Func<string, string> qualifiers = text => string.Join("|", Regex.Matches(NormalizeLookup(text),
                @"\b(?:not|never|no|without|dislike\w*|hate\w*|fear\w*|avoid\w*|\d+)\b").Cast<Match>().Select(m => m.Value).Distinct().OrderBy(x => x));
            if (qualifiers(left) != qualifiers(right)) return false;
            HashSet<string> a = DynamicEvidenceTerms(left), b = DynamicEvidenceTerms(right);
            int common = a.Count(b.Contains);
            return common >= 6 && a.SetEquals(b);
        }

        private static List<Dictionary<string, object>> SelectNaturalDynamicCharacteristics(
            Dictionary<string, object> document, string relevanceQuery)
        {
            var all = ReadDictionaryList(document, "active")
                .Where(row => ReadString(row, "status", "active").Equals("active", StringComparison.OrdinalIgnoreCase)
                    && ReadString(row, "category", "") != "narrative_development"
                    && !IsGeneratedConversationRhetoric(row)).ToList();
            string query = NormalizeLookup(ConversationSpeech(relevanceQuery));
            bool first = Regex.IsMatch(query, @"\b(first|earliest|oldest|initial)\b");
            bool habit = Regex.IsMatch(query, @"\b(habit|habits|routine|ritual)\b");
            bool interests = Regex.IsMatch(query, @"\b(hobbies|hobby|enjoy|enjoys|interests|yourself|free time|like to do|want for yourself)\b");
            var requestedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (interests) requestedCategories.UnionWith(new[] { "habit", "preference", "skill_practice", "aspiration" });
            if (Regex.IsMatch(query, @"\b(childhood|upbringing|youth|grew up|were raised|your past|your history)\b"))
                requestedCategories.UnionWith(new[] { "personal_history", "formative_experience", "local_connection" });
            if (Regex.IsMatch(query, @"\b(your values|your principles|believe in|matters to you|care about)\b"))
                requestedCategories.UnionWith(new[] { "personal_value", "social_tendency" });
            if (Regex.IsMatch(query, @"\b(dislike|dislikes|afraid of|your fears|cannot stand|hate|hates)\b")) requestedCategories.Add("aversion");
            if (Regex.IsMatch(query, @"\b(prefer|favorite|favourite|like best)\b")) requestedCategories.Add("preference");
            if (Regex.IsMatch(query, @"\b(your ambitions|your aspirations|hope for|dream of|what (?:do )?you (?:really )?want)\b")) requestedCategories.Add("aspiration");
            var generic = new HashSet<string>(new[] { "tell", "about", "more", "said", "say", "just", "sorry", "really", "know", "think", "need", "could", "would", "here", "there", "some", "only", "does", "been", "much", "still", "again", "yourself", "first", "earliest", "oldest", "initial", "shared", "meeting", "small" }, StringComparer.OrdinalIgnoreCase);
            var terms = DynamicEvidenceTerms(query); terms.ExceptWith(generic);
            var ranked = all.Select(row =>
            {
                string category = ReadString(row, "category", "");
                var searchable = DynamicEvidenceTerms(ReadString(row, "text", "") + " " + ReadFirstString(row, "topic_key", "topicKey").Replace('_', ' '));
                int matches = terms.Count(searchable.Contains);
                bool intent = habit ? category == "habit" || category == "skill_practice"
                    : requestedCategories.Contains(category);
                bool relevant = string.IsNullOrWhiteSpace(query) || matches > 0 || intent || first && !habit && requestedCategories.Count == 0;
                return new { row, relevant, matches, intent };
            }).Where(x => x.relevant)
                .OrderByDescending(x => first ? 0 : x.matches * 10 + (x.intent ? 20 : 0))
                .ThenBy(x => first ? ReadLong(x.row, "first_ts", ReadLong(x.row, "firstTs", 0)) : 0)
                .ThenByDescending(x => ReadDouble(x.row, "importance", 0.5d))
                .ThenByDescending(x => ReadLong(x.row, "last_ts", ReadLong(x.row, "lastTs", 0))).ToList();
            var selected = new List<Dictionary<string, object>>();
            foreach (var item in ranked)
            {
                if (selected.Any(row => DynamicCharacteristicsEquivalent(ReadString(row, "text", ""), ReadString(item.row, "text", "")))) continue;
                selected.Add(item.row);
                if (selected.Count == 6) break;
            }
            return selected;
        }
    }
}
