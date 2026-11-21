using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationService
    {
        private static bool HistoryClaimContainsTerm(string text, params string[] terms) =>
            terms.Any(term => Regex.IsMatch(text ?? "", @"\b" + Regex.Escape(term) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

        private static bool HasMilitaryLeadershipClaim(string text) => Regex.IsMatch(text ?? "",
            @"\b(?:led|commanded)\s+(?:(?:the|a|an|my|our|his|her|their|these|those)\s+)?"
            + @"(?:(?:imperial|victorious|defending|attacking|enemy)\s+)?(?:battle|army|troops|soldiers|siege|raid|cavalry|infantry|forces|men)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex ClaimantDeed = new Regex(
            @"\bI\s+(?:(?:personally|alone|also|actually|really|once|just|recently|have|had)\s+)*"
            + @"(?<verb>led|commanded|fought|rescued|freed|released|won|defeated|lost|captured|killed|slew|executed|wounded|injured|raided|burned|besieged|conquered|defended|owned|married|paid|traded|sold|bought|traveled|travelled|went|arrived|entered|met|spoke|was\s+(?:present\s+)?at|took\s+part|set\s+free|took\s+prisoner|gave\s+gold)\b[^.!?\r\n]*",
            ClaimOptions);

        internal static bool MentionsFirstPersonDeed(string text) => ClaimantDeed.IsMatch(text);

        internal static bool HasReportedHistoryTag(string text) => Regex.IsMatch(text,
            @"(?:\bor\s+so\s+|[,;:—–]\s*)(?!I\b|we\b)(?:he|she|they|you|(?-i:\p{Lu}[\p{L}'’-]+)"
            + @"|(?:the|my|our|your|his|her|their)\s+[\p{L}'’-]+)\s+(?:said|says?|claimed|claims?|wrote)\b|\baccording\s+to\b", ClaimOptions);

        internal static List<Match> HistoryQuotationAndNarration(string source) =>
            Regex.Matches(source, @"\*[^*]*(?:\*|\z)|[""“][^""”]*(?:[""”]|\z)|(?<![\p{L}\p{N}])['‘](?=\S).*?(?:['’](?![\p{L}\p{N}])|\z)",
                RegexOptions.Singleline | RegexOptions.CultureInvariant).Cast<Match>().ToList();

        internal static List<Dictionary<string, object>> ExtractAssertedWorldHistorySpans(string source)
        {
            source ??= "";
            var result = new List<Dictionary<string, object>>();
            var nonAssertions = HistoryQuotationAndNarration(source);
            foreach (Match sentence in Regex.Matches(source, @"[^.!?\r\n]+(?:[.!?]+|$)"))
            {
                if (sentence.Value.Contains('?')) continue;
                var assertion = ClaimantDeed.Match(sentence.Value);
                if (!assertion.Success) continue;
                int start = sentence.Index + assertion.Index;
                if (nonAssertions.Any(n => start >= n.Index && start < n.Index + n.Length)) continue;
                // A trailing attribution belongs to the original sentence, even after a semicolon.
                if (HasReportedHistoryTag(sentence.Value.Substring(assertion.Index))) continue;
                int clauseStart = sentence.Index;
                foreach (Match aside in nonAssertions.Where(n => n.Value[0] == '*' && n.Index + n.Length > sentence.Index && n.Index + n.Length <= start))
                {
                    if (aside.Index > clauseStart && !string.IsNullOrWhiteSpace(source.Substring(clauseStart, aside.Index - clauseStart))) break;
                    clauseStart = aside.Index + aside.Length;
                }
                string prefix = source.Substring(clauseStart, start - clauseStart);
                // A quoted third-party boast, conditional, denial or imagined act
                // cannot become this claimant's completed deed.
                if (Regex.IsMatch(prefix, @"\b(?:if|suppose|supposing|imagine|imagined|wish|wished|pretend|pretended|whether|said|says|told|claimed|claims|wrote|denied|never|not|dreamed|dreamt)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
                // Keep a leading time, place or other condition attached to the assertion.
                int prefixStart = prefix.Length - prefix.TrimStart().Length;
                string quote = source.Substring(clauseStart + prefixStart, start + assertion.Length - clauseStart - prefixStart).TrimEnd(' ', '\t', '"', '”');
                start = clauseStart + prefixStart;
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
                    ["length"] = quote.Length, ["sourceHash"] = DialogueService.PromptHash(source), ["claimFamily"] = family,
                    ["actor"] = "claimant", ["evidenceType"] = "spoken_statement" });
            }
            return result;
        }

        private const RegexOptions ClaimOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private const string PlaceAndDay = @"(?:\s+at\s+(?<place>[\p{L}][\p{L}'’ -]*?))?(?:\s+on\s+day\s+(?<day>[0-9]{1,8}))?";
        private static readonly Regex BattleClaim = new Regex(
            @"\AI\s+(?<action>led|commanded|was\s+(?:present\s+)?at|won|lost)\s+a\s+battle" + PlaceAndDay + @"\z", ClaimOptions);
        private static readonly Regex PrisonerClaim = new Regex(
            @"\AI\s+(?:have\s+)?(?:rescued|freed)\s+(?:(?<bound>(?<minimum>at\s+least)|exactly)\s+)?"
            + @"(?<quantity>[0-9]{1,8}|one|two|three|four|five|six|seven|eight|nine|ten|a|several|multiple)\s+(?:prisoners?|captives?)"
            + @"(?:\s+(?:(?<battle>in\s+(?:one|a\s+single)\s+battle)|(?<career>(?:in|over)\s+my\s+(?:entire\s+)?career)|in\s+total))?"
            + PlaceAndDay + @"\z", ClaimOptions);

        private static bool MatchesClaimScope(Match claim, Dictionary<string, object> row)
        {
            return (!claim.Groups["place"].Success || string.Equals(claim.Groups["place"].Value, ReadString(row, "location_name", ""), StringComparison.OrdinalIgnoreCase))
                && (!claim.Groups["day"].Success || Math.Floor(ReadDouble(row, "world_day", -1)) == int.Parse(claim.Groups["day"].Value, CultureInfo.InvariantCulture));
        }

        private static bool HasTypedRole(Dictionary<string, object> entity, params string[] roles) =>
            ReadString(entity, "role", "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Intersect(roles, StringComparer.OrdinalIgnoreCase).Any();

        private static bool IsHero(Dictionary<string, object> entity) => ReadString(entity, "entity_type", "") == "hero"
            && !string.IsNullOrWhiteSpace(ReadString(entity, "entity_id", ""));

        private static bool HasClaimantRole(Dictionary<string, object> row, string claimant, params string[] roles) =>
            ReadDictionaryList(row, "entities").Any(e => IsHero(e) && string.Equals(ReadString(e, "entity_id", ""), claimant, StringComparison.OrdinalIgnoreCase) && HasTypedRole(e, roles));

        private static Dictionary<string, object> HistoryFinding(string verdict, string explanation, IEnumerable<Dictionary<string, object>> rows,
            string matched = "", string unverified = "", string mismatched = "")
        {
            static string[] Component(string text) => text.Length == 0 ? Array.Empty<string>() : new[] { text };
            return new Dictionary<string, object>
            {
                ["verdict"] = verdict, ["explanation"] = explanation,
                ["evidenceEventIds"] = rows.Select(row => ReadString(row, "event_id", "")).Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ["matchedComponents"] = Component(matched), ["unverifiedComponents"] = Component(unverified), ["mismatchedComponents"] = Component(mismatched)
            };
        }

        private static Dictionary<string, object> AssessPrisonerClaim(string claim, bool rescue, string claimant, List<Dictionary<string, object>> rows)
        {
            if (!rescue)
            {
                // OnHeroPrisonerReleased identifies the former captor's leader, even when another hero arranged the release.
                var custody = rows.Where(row => ReadString(row, "event_type", "") == "hero_prisoner_released"
                    && HasClaimantRole(row, claimant, "former_captor")).ToList();
                return custody.Count == 0 ? HistoryFinding("insufficient_evidence", "No native record identifies the releasing actor.", custody)
                    : HistoryFinding("partially_verified", "The release records the claimant as the former captor's leader.", custody, "former custody", "releasing actor");
            }
            const string role = "direct rescue";
            var credited = rows.Where(row => ReadString(row, "event_type", "") == "battle_completed" && HasClaimantRole(row, claimant, "direct_rescuer")).ToList();
            if (credited.Count == 0)
            {
                var assistance = rows.Where(row => ReadString(row, "event_type", "") == "battle_completed"
                    && HasClaimantRole(row, claimant, "assisting_rescuer", "rescuing_side_participant")).ToList();
                return assistance.Count > 0
                    ? HistoryFinding("partially_verified", "The recorded role is assistance in a rescue.", assistance, "rescue assistance", "direct rescue")
                    : HistoryFinding("insufficient_evidence", "No native event establishes the claimed role.", credited);
            }
            var proposition = PrisonerClaim.Match(claim);
            var scoped = credited.Where(row => proposition.Success && MatchesClaimScope(proposition, row)).ToList();
            if (scoped.Count == 0)
                return HistoryFinding("partially_verified", "A native event records the role. The other details of this assertion are unverified.", credited, role, "claim qualifiers");

            string quantity = proposition.Groups["quantity"].Value.ToLowerInvariant();
            if (proposition.Groups["bound"].Success && (quantity is "a" or "several" or "multiple"))
                return HistoryFinding("partially_verified", "The assertion does not specify a numerical bound.", scoped, role, "prisoner count");
            int count = quantity switch
            {
                "a" or "one" => 1, "two" or "multiple" => 2, "three" or "several" => 3, "four" => 4, "five" => 5,
                "six" => 6, "seven" => 7, "eight" => 8, "nine" => 9, "ten" => 10, _ => int.Parse(quantity, CultureInfo.InvariantCulture)
            };
            bool minimum = proposition.Groups["minimum"].Success
                || !proposition.Groups["bound"].Success && (quantity is "a" or "multiple" or "several");
            bool exact = proposition.Groups["bound"].Success && !proposition.Groups["minimum"].Success;
            IEnumerable<string> Beneficiaries(Dictionary<string, object> row) => ReadDictionaryList(row, "entities")
                .Where(e => IsHero(e) && HasTypedRole(e, "rescued_prisoner")).Select(e => ReadString(e, "entity_id", ""));
            int observed = proposition.Groups["battle"].Success
                ? scoped.Max(row => Beneficiaries(row).Distinct(StringComparer.OrdinalIgnoreCase).Count())
                : scoped.SelectMany(Beneficiaries).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Dictionary<string, object> WithCount(Dictionary<string, object> finding)
            {
                finding["prisonerCountEvidence"] = new Dictionary<string, object>
                {
                    ["recordedRole"] = role, ["scope"] = proposition.Groups["battle"].Success ? "single_battle" : proposition.Groups["career"].Success ? "career" : "unspecified",
                    ["quantifier"] = minimum ? "at_least" : exact ? "exact" : "unspecified", ["claimedCount"] = count, ["observedMinimum"] = observed
                };
                return finding;
            }
            if (minimum && count > 0 && observed >= count)
                return WithCount(HistoryFinding("verified", "Directly credited native records establish the stated minimum in its claimed scope.", scoped, role));
            if (exact && proposition.Groups["career"].Success && observed > count)
                return WithCount(HistoryFinding("contradicted", "Distinct prisoners in directly credited records exceed the claimed total.", scoped, role, mismatched: "claimed total"));
            // The battle snapshot counts heroes only. Its event-wide is_complete flag is not a census
            // of every freed prisoner, and the retrieval window cannot establish an exact career total.
            return WithCount(HistoryFinding("partially_verified", "The records establish a lower bound. The stated quantity remains unverified.", scoped, role, "prisoner count"));
        }
    }
}
