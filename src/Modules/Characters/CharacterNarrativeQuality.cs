using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static string PolishNarrativeText(string value)
        {
            // Mechanical copy edits only: retain meaning, native facts, names,
            // quantities and selection. Never repair a fabricated fact here.
            string text = Regex.Replace(value ?? "", @"(?<article>\ba\s+)?\btier[- ](?:one|two|three|four|five|six|seven|eight|nine|ten|\d+)\s+(?=(?<following>(?:(?:noble|imperial|sturgian|khuzait|battanian|vlandian|aserai|nordic|northern|southern|western|eastern|landless|minor|ruling|powerful|old|influential|prominent)\s+){0,3}(?:clan|house|family)\b))",
                match => {
                    string article = match.Groups["article"].Value;
                    return article.Length > 0 && Regex.IsMatch(match.Groups["following"].Value, @"^(?:imperial|aserai|old|influential|eastern)\b", RegexOptions.IgnoreCase)
                        ? (char.IsUpper(article[0]) ? "An " : "an ") : article;
                }, RegexOptions.IgnoreCase);
            return Regex.Replace(text, @"\b(?:at|by) the start of play\b|\b(?:at|by) campaign start\b", match => char.IsUpper(match.Value[0])
                ? "In the present day" : "in the present day", RegexOptions.IgnoreCase);
        }

        private static void PolishNarrativeProse(Dictionary<string, object> narrative)
        {
            foreach (string part in new[] { "life", "voice" })
            {
                var section = ReadDictionary(narrative, part);
                if (section == null) continue;
                foreach (string key in section.Keys.ToList())
                    if (section[key] is string) section[key] = PolishNarrativeText((string)section[key]);
                if (part == "voice" && section.ContainsKey("tells")) section["tells"] = ReadStringList(section, "tells").Select(PolishNarrativeText).ToList();
                narrative[part] = section;
            }
            var items = ReadDictionaryList(narrative, "items");
            foreach (var item in items)
                foreach (string key in new[] { "description", "personalMeaning" })
                    if (item.ContainsKey(key)) item[key] = PolishNarrativeText(ReadString(item, key, ""));
            narrative["items"] = items;
            narrative["proseNormalizationVersion"] = 1;
        }

        private static Dictionary<string, object> AuditNarrativeQuality(List<Dictionary<string, object>> narratives)
        {
            var prose = narratives.SelectMany(n => ReadDictionaryList(n, "items").SelectMany(item => new[] {
                ReadString(item, "description", ""), ReadString(item, "personalMeaning", "") })).Where(x => x.Length > 0).ToList();
            var repeated = prose.GroupBy(NormalizeLookup).Where(g => g.Count() > 1).OrderByDescending(g => g.Count()).Take(20)
                .Select(g => new Dictionary<string, object> { ["text"] = g.First(), ["count"] = g.Count() }).ToList();
            var lives = narratives.Select(n => new {
                Hero = ReadString(n, "heroStringId", ""),
                Words = Regex.Matches(ReadString(ReadDictionary(n, "life"), "summary", "").ToLowerInvariant(), @"[a-z]+")
                    .Cast<Match>().Select(m => m.Value).ToList()
            }).Select(x => new { x.Hero, Shingles = new HashSet<string>(Enumerable.Range(0, Math.Max(0, x.Words.Count - 3))
                .Select(i => string.Join(" ", x.Words.Skip(i).Take(4)))) }).ToList();
            var nearDuplicates = new List<Dictionary<string, object>>();
            int pairCount = 0;
            for (int a = 0; a < lives.Count; a++)
                for (int b = a + 1; b < lives.Count; b++)
                {
                    int smaller = Math.Min(lives[a].Shingles.Count, lives[b].Shingles.Count), larger = Math.Max(lives[a].Shingles.Count, lives[b].Shingles.Count);
                    if (larger == 0 || smaller / (double)larger < 0.85) continue;
                    int intersection = lives[a].Shingles.Count(lives[b].Shingles.Contains);
                    double similarity = intersection / (double)(lives[a].Shingles.Count + lives[b].Shingles.Count - intersection);
                    if (similarity < 0.85) continue;
                    pairCount++;
                    if (nearDuplicates.Count < 20) nearDuplicates.Add(new Dictionary<string, object> { ["firstHeroId"] = lives[a].Hero, ["secondHeroId"] = lives[b].Hero, ["similarity"] = similarity });
                }
            int gameWording = narratives.Count(n => {
                string text = Json.Serialize(ReadDictionary(n, "life"));
                return Regex.IsMatch(text, @"\btier[- ](?:one|two|three|four|five|six|seven|eight|nine|ten|\d+)\b|\bstart of play\b|\bcampaign start\b", RegexOptions.IgnoreCase);
            });
            var fieldDiversity = new List<Dictionary<string, object>>();
            foreach (string field in new[] { "speechStyle", "socialMask", "tells", "privateBackstory" })
            {
                var texts = narratives.Select(n => field == "tells"
                    ? string.Join(" | ", ReadStringList(ReadDictionary(n, "voice"), "tells"))
                    : ReadString(ReadDictionary(n, field == "privateBackstory" ? "life" : "voice"), field, ""))
                    .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                var groups = texts.GroupBy(NormalizeLookup).OrderByDescending(g => g.Count()).ToList();
                fieldDiversity.Add(new Dictionary<string, object> { ["field"] = field, ["populatedCount"] = texts.Count,
                    ["distinctCount"] = groups.Count, ["largestRepeatedCount"] = groups.Count == 0 ? 0 : groups[0].Count(),
                    ["mostRepeated"] = groups.Where(g => g.Count() > 1).Take(5)
                        .Select(g => new Dictionary<string, object> { ["text"] = g.First(), ["count"] = g.Count() }).ToList() });
            }
            return new Dictionary<string, object> {
                ["schema"] = "reign-narrative-quality-v1", ["fieldDiversity"] = fieldDiversity,
                ["nearDuplicateLifePairCount"] = pairCount, ["nearDuplicateLifePairs"] = nearDuplicates,
                ["lifeSimilarityMethod"] = "Four-word shingle Jaccard >= 0.85; diagnostic, not a claim of semantic uniqueness.",
                ["mostRepeatedConcernSentences"] = repeated, ["profilesWithGameWording"] = gameWording,
                ["normalizationVersion"] = 1 };
        }
    }
}
