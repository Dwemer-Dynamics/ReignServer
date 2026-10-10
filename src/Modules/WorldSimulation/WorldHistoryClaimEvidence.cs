using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static bool HistoryClaimContainsTerm(string text, params string[] terms) =>
            terms.Any(term => Regex.IsMatch(text ?? "", @"\b" + Regex.Escape(term) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

        private static bool HasMilitaryLeadershipClaim(string text) => Regex.IsMatch(text ?? "",
            @"\b(?:led|commanded)\s+(?:(?:the|a|an|my|our|his|her|their|these|those)\s+)?"
            + @"(?:(?:imperial|victorious|defending|attacking|enemy)\s+)?(?:battle|army|troops|soldiers|siege|raid|cavalry|infantry|forces|men)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static bool HistoryEventRosterComplete(Dictionary<string, object> row) =>
            ReadBool(row, "is_complete", false) || ReadInt(row, "is_complete", 0) == 1;

        private static List<Dictionary<string, object>> ExtractAssertedWorldHistorySpans(string source)
        {
            source ??= "";
            var result = new List<Dictionary<string, object>>();
            var narration = Regex.Matches(source, @"\*[^*]*\*", RegexOptions.Singleline).Cast<Match>().ToList();
            foreach (Match sentence in Regex.Matches(source, @"[^.!?\r\n]+(?:[.!?]+|$)"))
            {
                if (sentence.Value.TrimEnd().EndsWith("?", StringComparison.Ordinal)) continue;
                var assertion = Regex.Match(sentence.Value,
                    @"\bI\s+(?:(?:personally|alone|also|actually|really|once|just|recently|have|had)\s+)*"
                    + @"(?:led|commanded|fought|rescued|freed|released|won|defeated|lost|captured|killed|slew|executed|wounded|injured|raided|burned|besieged|conquered|defended|owned|married|paid|traded|sold|bought|traveled|travelled|went|arrived|entered|met|spoke|was\s+(?:present\s+)?at|took\s+part|set\s+free|took\s+prisoner|gave\s+gold)\b[^.!?\r\n]*",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!assertion.Success) continue;
                int start = sentence.Index + assertion.Index;
                if (narration.Any(n => start >= n.Index && start < n.Index + n.Length)) continue;
                string prefix = sentence.Value.Substring(0, assertion.Index);
                // A quoted third-party boast, conditional, denial or imagined act
                // cannot become this claimant's completed deed.
                if (Regex.IsMatch(prefix, @"\b(?:if|suppose|supposing|imagine|imagined|wish|wished|pretend|pretended|whether|said|says|told|claimed|claims|wrote|denied|never|not|dreamed|dreamt)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
                string quote = assertion.Value.TrimEnd(' ', '\t', '"', '”');
                if (Regex.IsMatch(quote, @"\b(?:in\s+(?:a|my|the)\s+dream|in\s+(?:a|the)\s+story|tomorrow|would\s+have)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
                // Do not swallow another clause, stage direction or attribution.
                var boundary = Regex.Match(quote, @"\*|[""”]|;|,?\s+(?:and|but)\s+(?:I|he|she|we|they|you)\b", RegexOptions.IgnoreCase);
                if (boundary.Success) quote = quote.Substring(0, boundary.Index).TrimEnd();
                var route = ClassifyWorldHistoryClaim(quote);
                string family = ReadString(route, "claimFamily", "generic_participation");
                if (family == "generic_participation") continue;
                if (Regex.IsMatch(quote, @"\bI\s+(?:led|commanded)\b", RegexOptions.IgnoreCase) && family != "leadership") continue;
                if (family == "payment" && !Regex.IsMatch(quote, @"\b(?:gold|coins?|denars?|money|payment|tribute|wages?|rent)\b|\d", RegexOptions.IgnoreCase)) continue;
                if (family == "meeting" && Regex.IsMatch(quote, @"\bmet\s+(?:(?:the|your|my|their|our)\s+)?(?:expectations?|requirements?|conditions?|needs?|standards?)\b", RegexOptions.IgnoreCase)) continue;
                result.Add(new Dictionary<string, object> { ["quote"] = quote, ["start"] = start,
                    ["length"] = quote.Length, ["sourceHash"] = PromptHash(source), ["claimFamily"] = family,
                    ["actor"] = "claimant", ["evidenceType"] = "spoken_statement" });
            }
            return result;
        }

        private static bool HistoryClaimMatchesNamedLocation(string claim, Dictionary<string, object> row)
        {
            var place = Regex.Match(claim ?? "", @"\b(?:at|near|outside|inside)\s+(?:the\s+)?(?<place>[A-Z][\p{L}'’-]*(?:\s+[A-Z][\p{L}'’-]*){0,4})",
                RegexOptions.CultureInvariant);
            if (!place.Success) return true;
            string named = NormalizeLookup(place.Groups["place"].Value);
            string actual = NormalizeLookup(ReadString(row, "location_name", ""));
            return actual.Length > 0 && named == actual;
        }
    }
}
