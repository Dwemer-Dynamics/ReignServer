using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string BeverageCatalogueVersion = "reign-beverages-v1";
        private enum DrinkAlcohol { Unknown, No, Yes, VariantRequired }

        private sealed class BeverageType
        {
            public string Family;
            public DrinkAlcohol Alcohol;
            public string[] Names;
            public string Provenance;
            public BeverageType(string family, DrinkAlcohol alcohol, string provenance, params string[] names)
            { Family = family; Alcohol = alcohol; Provenance = provenance; Names = names; }
        }

        private sealed class BeverageIdentity
        {
            public string Family = "";
            public string Name = "";
            public DrinkAlcohol Alcohol;
            public BeverageIdentity Copy() => new BeverageIdentity { Family = Family, Name = Name, Alcohol = Alcohol };
        }

        // One source for recognition, recovery, diagnostics and catalogue contracts.
        // Availability belongs to the scene, not to this compatibility vocabulary.
        private static readonly BeverageType[] ConversationBeverages =
        {
            new BeverageType("beer", DrinkAlcohol.Yes, "Native beer item and ale dialogue",
                "beer", "ale", "small beer", "small ale", "strong ale", "barley beer", "wheat beer", "rye beer", "millet beer"),
            new BeverageType("wine", DrinkAlcohol.Yes, "Native wine; Reign imperial/Vlandian prompts",
                "wine", "red wine", "white wine", "sweet wine", "dry wine", "spiced wine", "mulled wine",
                "watered wine", "diluted wine", "honeyed wine", "hippocras", "hypocras", "house wine"),
            new BeverageType("mead", DrinkAlcohol.Yes, "Reign northern/Battanian prompts",
                "mead", "honey wine", "spiced mead", "hot mead"),
            new BeverageType("kumis", DrinkAlcohol.Yes, "Native Khuzait dialogue; Reign prompts; airag aliases",
                "kumis", "kumiss", "kumys", "kumyz", "koumis", "koumiss", "coumis", "airag", "fermented mare's milk"),
            new BeverageType("kvass", DrinkAlcohol.VariantRequired, "Native tavern and Reign Sturgian prompts",
                "kvass", "kvas", "bread kvass", "rye kvass"),
            new BeverageType("fruit_wine", DrinkAlcohol.Yes, "Compatibility family; not a claim of native item availability",
                "cider", "hard cider", "apple cider", "perry", "pear cider", "date wine", "berry wine",
                "plum wine", "fruit wine", "pomegranate wine", "palm wine"),
            new BeverageType("grain_ferment", DrinkAlcohol.VariantRequired, "Preparation-dependent compatibility",
                "boza", "buza", "bouza", "fermented grain drink"),
            new BeverageType("spirits", DrinkAlcohol.Yes, "Legacy recognition and compatibility",
                "brandy", "spirits", "liquor", "whisky", "whiskey", "rum", "vodka", "arak", "arrack", "arkhi", "firewater"),
            new BeverageType("unclassified", DrinkAlcohol.Unknown, "Requires an established beverage identity",
                "brew", "local brew", "house brew", "vintage", "reserve", "the red", "the white", "tavern special",
                "strong drink", "grog", "cordial", "tonic", "draught", "draft", "fermented milk"),
            new BeverageType("nonalcoholic", DrinkAlcohol.No, "Ordinary drink and explicit nonalcohol controls",
                "water", "spring water", "rosewater", "juice", "fresh cider", "unfermented cider",
                "milk", "fresh mare's milk", "tea", "coffee", "broth", "soup", "sherbet", "sharbat",
                "ayran", "doogh", "vinegar", "wine vinegar", "cider vinegar", "honey water")
        };

        private static Match DrinkMatch(string text, string pattern) => Regex.Match(text ?? "", pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        private static bool DrinkHas(string text, string pattern) => DrinkMatch(text, pattern).Success;

        private static BeverageIdentity ResolveDrinkBeverage(string phrase)
        {
            phrase = (phrase ?? "").Replace('’', '\'');
            if (phrase.Length > 4000) return new BeverageIdentity();
            var found = new List<Tuple<int, int, BeverageType, string>>();
            foreach (var type in ConversationBeverages)
                foreach (string name in type.Names)
                    foreach (Match match in Regex.Matches(phrase, @"\b" + Regex.Escape(name) + @"(?:s)?\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                        found.Add(Tuple.Create(match.Index, match.Length, type, match.Value));
            // Longest matching preparation owns its ingredients: honey wine, wine vinegar,
            // and fermented mare's milk must not also match bare wine/milk.
            var selected = new List<Tuple<int, int, BeverageType, string>>();
            foreach (var match in found.OrderByDescending(x => x.Item2).ThenBy(x => x.Item1))
                if (!selected.Any(x => match.Item1 < x.Item1 + x.Item2 && x.Item1 < match.Item1 + match.Item2))
                    selected.Add(match);
            selected = selected.OrderBy(x => x.Item1).ToList();
            if (selected.Count == 0) return new BeverageIdentity();
            var identities = selected.Select((match, i) =>
            {
                int previousEnd = i == 0 ? 0 : selected[i - 1].Item1 + selected[i - 1].Item2;
                int end = match.Item1 + match.Item2;
                int nextStart = i + 1 < selected.Count ? selected[i + 1].Item1 : phrase.Length;
                string before = phrase.Substring(previousEnd, match.Item1 - previousEnd);
                string after = phrase.Substring(end, nextStart - end);
                DrinkAlcohol alcohol = match.Item3.Alcohol;
                if (DrinkHas(before, @"\b(?:non[- ]?alcoholic|alcohol[- ]free|unfermented)\b")
                    || DrinkHas(after, @"\b(?:without\s+(?:any\s+)?alcohol|contains?\s+no\s+alcohol)\b")) alcohol = DrinkAlcohol.No;
                else if ((alcohol == DrinkAlcohol.VariantRequired || alcohol == DrinkAlcohol.Unknown)
                    && (DrinkHas(before, @"\b(?:alcoholic|intoxicating)\b")
                        || DrinkHas(after, @"\b(?:is|brewed)\s+(?:explicitly\s+)?alcoholic\b"))) alcohol = DrinkAlcohol.Yes;
                return new BeverageIdentity { Family = match.Item3.Family, Name = match.Item4, Alcohol = alcohol };
            }).ToList();
            var first = identities[0];
            // An explicit local name may be followed by its actual preparation.
            if (first.Alcohol == DrinkAlcohol.Unknown)
                first = identities.FirstOrDefault(x => x.Alcohol != DrinkAlcohol.Unknown) ?? first;
            bool mixture = DrinkHas(phrase, @"\b(?:mixed|spiked|laced|diluted|watered|fortified|blended)\b|\b(?:wine|brandy|rum)\s+(?:and|with)\s+water\b|\bwater\s+(?:and|with)\s+wine\b");
            if (mixture)
                first = identities.FirstOrDefault(x => x.Alcohol == DrinkAlcohol.Yes) ?? first;
            return first;
        }

        private static BeverageIdentity CurrentTurnSharedBeverage(Dictionary<string, object> parsed)
        {
            var identities = new List<BeverageIdentity>();
            foreach (string fact in ReadStringList(ReadDictionary(parsed, "decisionBrief"), "facts"))
            {
                if (DrinkHas(fact, @"\b(?:not|never|yesterday|earlier|previously|tomorrow|would|could|might|if|unless)\b")) continue;
                var sharing = DrinkMatch(fact, @"\b(?:are|is)\s+(?:sharing|drinking)\s+(.+)");
                if (!sharing.Success) continue;
                var beverage = ResolveDrinkBeverage(sharing.Groups[1].Value);
                if (beverage.Alcohol != DrinkAlcohol.Unknown) identities.Add(beverage);
            }
            return identities.Count > 0 && identities.All(x => x.Alcohol == DrinkAlcohol.Yes && x.Family == identities[0].Family)
                ? identities[0] : new BeverageIdentity();
        }
    }
}
