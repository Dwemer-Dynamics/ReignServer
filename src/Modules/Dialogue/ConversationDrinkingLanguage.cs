using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string DrinkNumberWords = "zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty";
        private const string DrinkVesselWords = @"cup|glass|goblet|mug|tankard|bottle|drinking horn|horn|bowl|chalice|flagon|flask|jug|pitcher|ewer|wine[- ]?skin|waterskin|skin|amphora";
        private const string DrinkPortionWords = @"sip|mouthful|swig|pull|gulp|swallow|draught|draft";
        private const string DrinkVerbPattern =
            @"\b(?:takes?|has|have)\s+(?:(?:a|an|another|half(?:\s+a)?|quarter(?:\s+of\s+a)?|\d+|" + DrinkNumberWords + @")\s+)?(?:(?:slow|long|deep|small|little|tiny|careful|measured|deliberate|tentative|cautious|hearty|large|quick|swift|generous|solid|real)\s+){0,4}(?:sip|mouthful|swig|pull|gulp|swallow|draught|draft|drink)s?\b"
            + @"|\b(?:knocks?|throws?|tosses?)\s+back\b|\bpolishes?\s+off\b"
            + @"|\b(?:drinks?|drank|drinking|sips?|sipped|sipping|swallows?|swallowed|swallowing|gulps?|gulped|gulping|quaffs?|quaffed|quaffing|swigs?|swigged|swigging|imbibes?|imbibed|imbibing|consumes?|consumed|consuming|chugs?|chugged|chugging|sups?|supped|supping|slurps?|slurped|slurping|downs|downed|downing|drains?|drained|draining|finishes?|finished|finishing|empties|emptied|emptying)\b";

        private sealed class NarratedDrink
        {
            public int Index, Occurrence, Start, Length;
            public string Action = "", Evidence = "", ObjectText = "", Vessel = "";
            public string Serving = "sip", QuantitySource = "default_partial";
            public int Count = 1;
            public double? ExplicitAmount;
            public bool Completion, RemainderFraction;
            public double Fraction = 1;
            public BeverageIdentity Beverage = new BeverageIdentity();
            public bool ExplicitAlcohol => Beverage.Alcohol == DrinkAlcohol.Yes;
            public bool NonAlcohol => Beverage.Alcohol == DrinkAlcohol.No;
            public double Amount => ExplicitAmount ?? .25;
        }

        private static double DrinkingUnit(string serving) => serving == "drink" ? 1 : serving == "half" ? .5 : serving == "sip" ? .25 : 0;

        private static string MaskDrinkQuotations(string action) => Regex.Replace(action,
            "\"[^\"]*(?:\"|$)|“[^”]*(?:”|$)|(?<![\\p{L}])'[^'\\r\\n]+'",
            m => new string(' ', m.Length));

        private static bool CurrentDrinkSubject(string prefix, string heroName, bool? isFemale, out int subjectEnd)
        {
            subjectEnd = 0;
            string pronouns = isFemale.HasValue ? (isFemale.Value ? "she|they" : "he|they") : "he|she|they";
            var names = new[] { heroName, FirstName(heroName) }.Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Length).Select(Regex.Escape);
            string subjects = string.Join("|", new[] { pronouns }.Concat(names));
            // Fronted descriptive clauses end at a comma; a second explicit subject
            // supersedes the earlier one. Never infer another actor from pronoun gender.
            var matches = Regex.Matches(prefix, @"(?:^\s*|[,;]\s*|\b(?:and|but|while|then|as)\s+)(?<who>" + subjects + @")\b(?!['’]s)\s+",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (matches.Count == 0) return false;
            Match subject = matches[matches.Count - 1];
            subjectEnd = subject.Index + subject.Length;
            string predicate = prefix.Substring(subjectEnd);
            // A title attached to a verified name is not a second actor.
            if (!DrinkHas(subject.Groups["who"].Value, @"^(?:he|she|they)$"))
                predicate = Regex.Replace(predicate, @"^the\s+[\p{Lu}][\p{L}]+\s+", "");
            if (DrinkHas(predicate, @"\b(?:he|she|they|you|him|her|them|who)\s*$|\b(?:and|while|as|but)\s+(?:he|she|they|you|the\s+\w+|a\s+\w+)\b")
                || Regex.IsMatch(predicate, @"\b(?:and|while|as|but)\s+[\p{Lu}][\p{L}]+\b")
                || Regex.IsMatch(predicate, @"\b[\p{Lu}][\p{L}]+\s*$"))
                return false;
            // Only the latest coordinated clause controls negation. "Does not answer,
            // but takes a sip" does not turn into a refusal to drink.
            string local = Regex.Split(predicate, @"(?:,\s*)?\b(?:then|but)\b|,\s*(?:and\s+)?",
                RegexOptions.IgnoreCase).Last();
            if (DrinkHas(local, @"\b(?:not|never|no|refus\w*|declin\w*|pretend\w*|imagin\w*|recall\w*|remember\w*|would|could|might|will|shall|wants?|plans?|tries?|attempts?|almost|nearly|makes?|lets?|helps?|forces?|orders?|watches?|watching|hears?|sees?|thinks?|says?|tells?|quotes?)\b|n['’]t\b|\bused\s+to\b|\bonce\s*$|\blong\s+ago\b"))
                return false;
            if (DrinkHas(prefix, @"\b(?:yesterday|earlier|previously|tomorrow|recall\w*|remember\w*|used\s+to)\b")) return false;
            return true;
        }

        private static int DrinkNumber(string value)
        {
            if (int.TryParse(value, out int number)) return number;
            return Array.IndexOf(DrinkNumberWords.Split('|'), (value ?? "").ToLowerInvariant());
        }

        private static List<NarratedDrink> ReadNarratedDrinks(string action, string heroName = "", bool? isFemale = null)
        {
            var result = new List<NarratedDrink>();
            if (string.IsNullOrWhiteSpace(action) || action.Length > 4000) return result;
            string narration = MaskDrinkQuotations(action);
            foreach (Match sentenceMatch in Regex.Matches(narration, @"[^.!?;]+"))
            {
                string sentence = sentenceMatch.Value;
                if (DrinkHas(sentence, @"\b(?:yesterday|earlier|previously|tomorrow|if|unless|would|could|might|will|shall)\b")) continue;
                var verbs = Regex.Matches(sentence, DrinkVerbPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100)).Cast<Match>().Where(v =>
                        !DrinkHas(sentence.Substring(0, v.Index), @"\b(?:a|an|the|her|his|their|another|some|of|single|" + DrinkNumberWords + @"|\d+)\s+(?:(?:small|slow|long|deep|measured|full|single|little|real|solid)\s+){0,4}$"))
                    .ToList();
                for (int n = 0; n < verbs.Count; n++)
                {
                    var verb = verbs[n];
                    string prefix = sentence.Substring(0, verb.Index);
                    if (!CurrentDrinkSubject(prefix, heroName, isFemale, out int subjectEnd)) continue;
                    if (DrinkHas(prefix, @"\b(?:counts?|pours?|fills?|hands?|passes?|buys?|holds?|raises?|carries?|serves?|spills?|delivers?|lists?|describes?)\s*$")) continue;
                    if (DrinkHas(prefix, @"\b(?:a|an|the|his|her|their|another|some|of|" + DrinkNumberWords + @"|\d+)\s*$")
                        && !DrinkHas(prefix, @"\btakes?\s+(?:a|another|" + DrinkNumberWords + @"|\d+)\s*$")) continue;
                    int end = n + 1 < verbs.Count ? verbs[n + 1].Index : sentence.Length;
                    string consumed = sentence.Substring(verb.Index, end - verb.Index);
                    // Keep the consumed preparation but stop before an unrelated clause/object.
                    consumed = Regex.Split(consumed, @"\b(?:while|whereas|beside|near|talking|speaking|watching|discussing|studying)\b|,\s*(?:then|and|but)\b|\s+(?:and|but)\s+(?=(?:he|she|they|you|sets?|puts?|refills?|fills?|pours?|hands?|passes?|picks?|smiles?|turns?|fixes?|wipes?)\b)",
                        RegexOptions.IgnoreCase)[0].Trim();
                    if (DrinkHas(consumed, @"\b(?:onto|into|on)\s+(?:the\s+)?(?:floor|ground|fire|sink|bowl|bucket)\b|\b(?:spits?|spitting)\b|\bwithout\s+swallowing\b|\bcannot\s+swallow\b")
                        || DrinkHas(consumed, @"\bempty\s+(?:(?:wine|ale|beer|mead)\s+)?(?:" + DrinkVesselWords + @")\b")
                        || DrinkHas(consumed, @"\bswallows?\s+(?:her|his|their)\s+pride\b")
                        || DrinkHas(consumed, @"\bfinishes?\s+(?:her|his|their|the|a)\s+(?:sentence|story|tale|thought)\b")) continue;
                    if (DrinkHas(consumed, @"\btakes?\s+a\s+drink\s+from\s+(?:the\s+(?:waitress|waiter|server)|him|her(?!\s+(?:own\s+)?(?:" + DrinkVesselWords + @")\b))\b")) continue;
                    if (DrinkHas(prefix, @"\bbefore\s*$") && !DrinkHas(consumed, @"\b(?:" + DrinkPortionWords + @")s?\b")) continue;
                    bool completion = DrinkHas(verb.Value, @"^(?:drain|finish|empt|polish)")
                        || DrinkHas(consumed, @"\b(?:entire|whole|all\s+(?:of\s+)?(?:the|her|his)|until\s+it\s+is\s+empty|last\s+of|remainder|rest\s+of)\b");
                    string vessel = DrinkMatch(consumed, @"\b(?:" + DrinkVesselWords + @")s?\b").Value.ToLowerInvariant();
                    var beverage = ResolveDrinkBeverage(consumed);
                    bool portion = DrinkHas(consumed, @"\b(?:" + DrinkPortionWords + @")s?\b");
                    bool reference = DrinkHas(consumed, @"\b(?:it|contents|rest|remainder)\b");
                    if (DrinkHas(verb.Value, @"^(?:finish|empt|drain|down|swallow|consume|sup)")
                        && vessel.Length == 0 && beverage.Alcohol == DrinkAlcohol.Unknown && !portion && !reference) continue;
                    if (DrinkHas(consumed, @"\b(?:pride|words|anger|breath|food|meal|stew)\b") && !portion) continue;
                    var candidate = new NarratedDrink
                    {
                        Action = action, Start = sentenceMatch.Index + verb.Index, Length = consumed.Length,
                        Evidence = action.Substring(sentenceMatch.Index + verb.Index, consumed.Length), ObjectText = consumed,
                        Vessel = vessel, Beverage = beverage, Completion = completion
                    };
                    ReadDrinkQuantity(candidate, verb.Value);
                    if (candidate.Count < 1 || candidate.Count > 20) continue;
                    // A swallow of "it/the mouthful" completes the previous action; "another"
                    // or a second independently numbered portion establishes a new occurrence.
                    if (result.Count > 0 && DrinkHas(verb.Value, @"^swallow") && !DrinkHas(consumed, @"\b(?:another|again|second|\d+|two|three)\b")
                        && (DrinkHas(consumed, @"\b(?:it|that|the\s+mouthful)\b") || consumed.TrimEnd().Equals(verb.Value, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    candidate.Occurrence = result.Count;
                    result.Add(candidate);
                }
            }
            return result;
        }

        private static NarratedDrink ReadNarratedDrink(string action, string heroName = "", bool? isFemale = null)
            => ReadNarratedDrinks(action, heroName, isFemale).FirstOrDefault();

        private static void ReadDrinkQuantity(NarratedDrink candidate, string verb)
        {
            string text = Regex.Replace(candidate.ObjectText, @"\bnot\s+(?:just\s+|merely\s+)?(?:a\s+)?sip\b", "", RegexOptions.IgnoreCase);
            var number = DrinkMatch(text, @"\b(\d+|" + DrinkNumberWords + @")\s+(?:(?:full|slow|measured|small|long|deep)\s+){0,3}(?:(?:" + DrinkPortionWords + @")s|cups|drinks|glasses|goblets|mugs|tankards|halves)\b");
            if (number.Success) candidate.Count = DrinkNumber(number.Groups[1].Value);
            bool partial = DrinkHas(text, @"\b(?:sips?|sipped|sipping|mouthfuls?|swigs?|pulls?|gulps?|swallows?|draughts?|drafts?)\b");
            var fraction = DrinkMatch(text, @"\b(?:half|three[- ]quarters|one[- ]quarter|quarter)\b|\b(?:1\s*/\s*2|[123]\s*/\s*4)\b");
            if (fraction.Success)
            {
                string value = fraction.Value.ToLowerInvariant().Replace(" ", "");
                candidate.Fraction = value == "half" || value == "1/2" || value == "2/4" ? .5
                    : value.StartsWith("three") || value == "3/4" ? .75 : .25;
                candidate.RemainderFraction = DrinkHas(text, @"\b(?:remaining|remains|left|rest)\b");
                candidate.ExplicitAmount = candidate.Fraction * (partial ? .25 : 1) * candidate.Count;
                candidate.Serving = "half";
                candidate.QuantitySource = "explicit_fraction";
            }
            else if (partial)
            {
                candidate.ExplicitAmount = .25 * candidate.Count;
                candidate.Serving = "sip";
                candidate.QuantitySource = "explicit_portion";
            }
            else if (DrinkHas(text, @"\b(?:a|one|two|three|\d+|full|whole|entire)\s+(?:(?:full|new|freshly\s+filled)\s+){0,3}(?:cup|glass|goblet|mug|tankard|drink)s?\b")
                || candidate.Completion)
            {
                candidate.ExplicitAmount = 1d * candidate.Count;
                candidate.Serving = "drink";
                candidate.QuantitySource = candidate.Completion ? "completion" : "explicit_serving";
            }
            // A compound action with explicit new sipping is a separate occurrence, but
            // the default amount for an otherwise unspecified "drinks" stays conservative.
        }
    }
}
