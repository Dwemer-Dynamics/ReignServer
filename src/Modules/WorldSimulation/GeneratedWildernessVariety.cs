using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int WildernessRecentEventLimit = 8;
        private const int WildernessParticipantLimit = 3;
        private const int WildernessRecentApproachLimit = 400;
        private const int WildernessRecentOpeningLimit = 600;

        // Directions, not scripted events. The model supplies the actual situation
        // from the current terrain, company and character facts.
        private static readonly string[][] WildernessDirections =
        {
            new[] { "humor_mishap", "humor", "A harmless practical mishap becomes funny; nobody is injured or loses equipment." },
            new[] { "humor_imitation", "humor", "An affectionate imitation or an absurd comparison invites playful disagreement without humiliating anyone." },
            new[] { "humor_misheard", "humor", "A misheard remark produces a brief, concrete misunderstanding that can be cleared up." },
            new[] { "skill_demonstration", "skill", "Someone demonstrates a modest travel skill and invites another person to try it." },
            new[] { "skill_teaching", "skill", "A practical question becomes an informal lesson; let competence and teaching style shape the encounter." },
            new[] { "skill_contest", "skill", "Someone proposes a harmless contest of observation or dexterity, without stakes, prizes or a declared winner." },
            new[] { "custom_greeting", "culture", "Different greeting customs create a specific, good-natured question about manners." },
            new[] { "custom_saying", "culture", "A local saying seems strange to someone from another tradition; invite an interpretation." },
            new[] { "custom_hospitality", "culture", "Different expectations of hospitality or travel courtesy prompt a small social negotiation." },
            new[] { "debate_fairness", "debate", "An ordinary shared inconvenience raises a concrete disagreement about fairness." },
            new[] { "debate_courage", "debate", "A clearly hypothetical question tests competing ideas of courage and good judgment, without creating danger." },
            new[] { "debate_status", "debate", "A mundane travel courtesy exposes different opinions about rank and earned respect." },
            new[] { "nature_sound", "nature", "An audible natural sound draws different explanations or imitations from the company." },
            new[] { "nature_pattern", "nature", "A visible pattern in the landscape invites curiosity and competing observations, not a lost relative or hidden wound." },
            new[] { "nature_weather", "nature", "A small change in weather prompts practical ingenuity or contrasting preferences, without forcing movement." },
            new[] { "game_riddle", "play", "A traveler poses an original riddle or word game and leaves it unanswered for the player." },
            new[] { "game_story", "play", "Someone begins an explicitly fictional tale and invites others to choose its next turn." },
            new[] { "game_guessing", "play", "A small guessing game about an observable detail invites everyone to participate." },
            new[] { "taste_music", "taste", "A tune, rhythm or disagreement about singing opens an enjoyable exchange; do not invent a lifelong musical career." },
            new[] { "taste_comfort", "taste", "Contrasting preferences for ordinary travel comforts lead to a concrete choice to discuss." },
            new[] { "taste_food", "taste", "Different opinions about a familiar food or recipe start a lively exchange without consuming or transferring supplies." },
            new[] { "trust_request", "trust", "A participant asks for a small piece of advice about an immediate, nonbinding social choice." },
            new[] { "trust_appreciation", "trust", "Someone notices another present person's small considerate act and chooses how openly to appreciate it." },
            new[] { "trust_admission", "trust", "A modest present-tense admission of uncertainty creates room for support; avoid tragic invented backstory." },
            new[] { "aspiration_future", "aspiration", "A participant speculates about an ordinary pleasure they might want after the journey; no promises or new quest." },
            new[] { "aspiration_learning", "aspiration", "A present observation sparks interest in learning something unfamiliar and asks the company where to begin." },
            new[] { "aspiration_values", "aspiration", "An explicitly hypothetical choice between two modest ambitions reveals priorities without rewriting established goals." },
            new[] { "friction_habit", "friction", "A minor traveling habit annoys someone enough to address it directly; offer a workable compromise." },
            new[] { "friction_credit", "friction", "Two interpretations of a just-observed small success create a gentle dispute over credit." },
            new[] { "friction_etiquette", "friction", "A minor breach of travel etiquette is noticed and invites apology, explanation or humor." },
            new[] { "reflection_place", "reflection", "The present landscape prompts a specific observation about belonging; use established history only if relevant." },
            new[] { "reflection_change", "reflection", "Someone notices a small change in their own current preference and wonders aloud about it; no compulsory confession." },
            new[] { "reflection_silence", "reflection", "A comfortable shared silence is broken by a concrete, unguarded observation, without a mystery to uncover." },
            new[] { "cooperation_problem", "cooperation", "A harmless practical puzzle needs two different perspectives; let the player suggest an approach." },
            new[] { "cooperation_roles", "cooperation", "A voluntary division of an ordinary travel task prompts a light negotiation; no assigned player actions." },
            new[] { "cooperation_invention", "cooperation", "Someone proposes an amusing improvement to a travel routine and invites criticism or refinement." }
        };

        private static List<Dictionary<string, object>> WildernessRecentEvents(Dictionary<string, object> payload)
        {
            return ReadDictionaryList(payload, "recentEvents").Take(WildernessRecentEventLimit)
                .Select(row => new Dictionary<string, object>
                {
                    ["varietyKey"] = LimitText(ReadString(row, "varietyKey", ""), 80),
                    ["title"] = LimitText(ReadString(row, "title", ""), 120),
                    ["approachDescription"] = LimitText(ReadString(row, "approachDescription", ""), WildernessRecentApproachLimit),
                    ["openingText"] = LimitText(ReadString(row, "openingText", ""), WildernessRecentOpeningLimit)
                }).ToList();
        }

        private static string[] SelectWildernessDirection(Dictionary<string, object> payload, Func<int, int> choose = null)
        {
            var recent = WildernessRecentEvents(payload);
            var used = new HashSet<string>(recent.Select(row => ReadString(row, "varietyKey", "")), StringComparer.Ordinal);
            var recentFamilies = new HashSet<string>(recent.Take(3)
                .Select(row => WildernessDirections.FirstOrDefault(direction => direction[0] == ReadString(row, "varietyKey", ""))?[1])
                .Where(family => family != null), StringComparer.Ordinal);
            var eligible = WildernessDirections.Where(direction => !used.Contains(direction[0]) && !recentFamilies.Contains(direction[1])).ToList();
            // There are 12 families, so eight bounded entries cannot exhaust the pool.
            return eligible[(choose ?? RandomNumberGenerator.GetInt32)(eligible.Count)];
        }

        private static Dictionary<string, object> WildernessCharacterBrief(Dictionary<string, object> stack, Dictionary<string, object> participant)
        {
            stack = stack ?? new Dictionary<string, object>();
            var traits = ReadDictionary(stack, "traits");
            var background = ReadDictionary(stack, "background");
            var brief = new Dictionary<string, object>
            {
                ["id"] = LimitText(CharacterIdFrom(participant), 160),
                ["name"] = LimitText(ReadString(participant, "name", ""), 120),
                ["role"] = LimitText(ReadString(participant, "generatedRole", "party_member"), 80),
                ["culture"] = LimitText(ReadFirstString(participant, "cultureName", "culture"), 120),
                ["personality"] = LimitText(ReadString(traits, "basePersonalitySummary", ""), 700),
                ["background"] = LimitText(FirstNonEmpty(ReadString(background, "summary", ""), ReadString(stack, "nativeBiography", "")), 900),
                ["scope"] = "Selected character facts for a new opening, not a transcript or a complete biography. Missing facts are unknown."
            };
            // Whole bounded structural projections, never a serialized full stack.
            // Dynamic characteristics, constructed copies, archives and repeated
            // narrative projections previously made three people exceed 312k chars.
            foreach (string section in new[] { "motivations", "voice", "pressure" })
            {
                int budget = section == "voice" ? 400 : section == "pressure" ? 600 : 800;
                brief[section] = ProjectWildernessFacts(ReadDictionary(stack, section), ref budget, 0);
            }
            int traitBudget = 1200;
            var scores = ReadDictionary(traits, "traitPercentages");
            if (scores == null || scores.Count == 0) scores = ReadDictionary(stack, "nativeTraits");
            brief["traits"] = ProjectWildernessFacts(
                scores, ref traitBudget, 0);
            return brief;
        }

        private static object ProjectWildernessFacts(object value, ref int budget, int depth)
        {
            if (value == null || budget <= 0 || depth > 3) return null;
            if (value is IDictionary<string, object> fields)
            {
                var result = new Dictionary<string, object>();
                foreach (var pair in fields.Take(16))
                {
                    if (budget < 100) break;
                    string key = LimitText(pair.Key, 60);
                    budget -= key.Length + 8;
                    result[key] = ProjectWildernessFacts(pair.Value, ref budget, depth + 1);
                }
                return result;
            }
            if (value is IEnumerable sequence && !(value is string))
            {
                var result = new List<object>();
                foreach (object item in sequence)
                {
                    if (result.Count == 4 || budget < 100) break;
                    result.Add(ProjectWildernessFacts(item, ref budget, depth + 1));
                }
                return result;
            }
            string text = LimitText(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), Math.Min(400, budget));
            budget -= text.Length;
            return text;
        }

        private static void AppendWildernessVarietyPrompt(System.Text.StringBuilder builder, Dictionary<string, object> payload, string[] direction)
        {
            builder.AppendLine("SCENE DIRECTION FOR THIS EVENT: " + direction[0] + " — " + direction[2]);
            builder.AppendLine("Make this a concrete incident already underway, with an observable activity and a specific conversational choice. The direction is inspiration, not a fixed script; adapt it to the actual terrain, time and participants.");
            builder.AppendLine("Vary the activity, emotional tone, initiator, social dynamic and player opportunity. A familiar hobby is not an obligatory plot. Do not default to someone stopping, staring away, folded arms, an unexplained personal association or a hidden family wound.");
            builder.AppendLine("Warmth, delight, humor, curiosity, cooperation and ordinary disagreement are valid. A pleasant event needs no secret or distress. Hidden context may simply explain a current preference; never manufacture trauma, shared history or a relationship change.");
            builder.AppendLine("Recent openings (newest first; avoid their premise, central prop, disagreement and opening action, even with different names or wording):");
            builder.AppendLine(Json.Serialize(WildernessRecentEvents(payload)));
            builder.AppendLine();
        }

        private static string WildernessOpeningRejection(Dictionary<string, object> response, Dictionary<string, object> payload, List<Dictionary<string, object>> participants)
        {
            string approach = ReadString(response, "approachDescription", "");
            string opening = ReadString(response, "openingText", "");
            if (string.IsNullOrWhiteSpace(approach) || string.IsNullOrWhiteSpace(opening)
                || string.IsNullOrWhiteSpace(ReadString(response, "playerHook", ""))) return "incomplete_opening";
            string[] allowedIds = participants.Select(CharacterIdFrom).ToArray();
            if (!ReadStringList(response, "participantHeroIds").Any(id => allowedIds.Contains(id, StringComparer.Ordinal))) return "no_eligible_participants";
            foreach (var recent in WildernessRecentEvents(payload))
            {
                string priorApproach = ReadString(recent, "approachDescription", "");
                string priorOpening = ReadString(recent, "openingText", "");
                if (WildernessNormalizedText(LimitText(approach, WildernessRecentApproachLimit)) == WildernessNormalizedText(priorApproach)
                    || WildernessNormalizedText(LimitText(opening, WildernessRecentOpeningLimit)) == WildernessNormalizedText(priorOpening)) return "repeated_opening";
                var currentWords = WildernessContentWords(approach + " " + opening, participants);
                var priorWords = WildernessContentWords(priorApproach + " " + priorOpening, participants);
                int shared = currentWords.Intersect(priorWords).Count();
                int union = currentWords.Union(priorWords).Count();
                // Conservative lexical guard. Paraphrased semantic repetition also
                // relies on the direction rotation and explicit prompt history.
                if (shared >= 12 && union > 0 && (double)shared / union >= 0.62) return "repeated_premise_words";
            }
            return "";
        }

        private static string WildernessNormalizedText(string text)
        {
            return Regex.Replace((text ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
        }

        private static HashSet<string> WildernessContentWords(string text, List<Dictionary<string, object>> participants)
        {
            var ignored = new HashSet<string>(("this that with from their there they them your while into then about where what have been before after "
                + "party road travel player time light around across along through beside toward "
                + string.Join(" ", participants.Select(row => WildernessNormalizedText(ReadString(row, "name", ""))))).Split(' '), StringComparer.Ordinal);
            return new HashSet<string>(WildernessNormalizedText(text).Split(' ').Where(word => word.Length >= 4 && !ignored.Contains(word)), StringComparer.Ordinal);
        }
    }
}
