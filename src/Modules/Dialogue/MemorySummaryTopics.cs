using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly HashSet<string> MemoryTopicMetadata = new(StringComparer.OrdinalIgnoreCase)
        {
            "dialogue", "dialogue-derived", "conversation", "middle_term", "middle-term", "scene", "event",
            "player", "npc", "speaker",
            "said", "visibly", "did", "she", "her", "his", "him", "was", "were", "are", "not", "but",
            "had", "has", "its", "it's", "all", "can", "does", "don't", "doesn't", "can't", "than", "some"
        };

        private static readonly string[] MemoryTopicProvenanceNotices =
        {
            "[DIALOGUE-DERIVED REPORTED SPEECH; NOT INDEPENDENT WORLD-FACT EVIDENCE]",
            "Conversation record (reported speech; not independent proof of world facts):",
            "Original source records (reported speech remains attributed):",
            "Selected original source records; partial coverage. Other answers, conditions and corrections may remain in the linked history; absence here is not evidence they never occurred."
        };

        private static readonly HashSet<string> MemoryTopicProvenanceWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "reported", "speech", "independent", "world-fact", "evidence", "record", "records", "source",
            "sources", "summary", "original", "selected", "coverage", "partial"
        };

        // Tags help discovery only. They neither establish a fact nor change its
        // speaker, visibility, memory lane, uncertainty or original source text.
        private static List<string> BuildMemorySummaryTopics(IEnumerable<Dictionary<string, object>> rows, int maximum)
        {
            int limit = Math.Clamp(maximum <= 0 ? 5 : maximum, 1, 12);
            var explicitCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var textCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var latest = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var lexicalPriority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int ordinal = 0;
            foreach (var row in rows ?? Enumerable.Empty<Dictionary<string, object>>())
            {
                ordinal++;
                string source = ReadString(row, "summary", "");
                string body = MemoryTopicSourceText(source);
                bool hasProvenanceNotice = MemoryTopicProvenanceNotices.Any(notice => source.Contains(notice, StringComparison.OrdinalIgnoreCase));
                var tags = TextListFromJson(ReadString(row, "tags_json", "[]"))
                    .Concat(ReadStringList(row, "tags"))
                    .Select(NormalizeMemoryTopic).Where(IsUsefulMemoryTopic)
                    // Older summaries extracted their tags from the generated
                    // notice. Discard a notice word only when actual content
                    // does not support it; "independent" can be a real topic.
                    .Where(tag => !hasProvenanceNotice || !MemoryTopicProvenanceWords.Contains(tag)
                        || Regex.IsMatch(body, @"(?<![\p{L}\p{Nd}_])" + Regex.Escape(tag) + @"(?![\p{L}\p{Nd}_])",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (string tag in tags)
                {
                    explicitCounts[tag] = explicitCounts.GetValueOrDefault(tag) + 1;
                    latest[tag] = ordinal;
                }

                // Long observations cannot crowd out the final record. This is
                // only a lexical hint window; stored/citable content is unchanged.
                if (body.Length > 8000) body = body.Substring(0, 4000) + "\n" + body.Substring(body.Length - 4000);
                var negated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                body = Regex.Replace(body, @"\b(?:not|never|no)\s+([\p{L}]{3,})\b", match =>
                {
                    string word = match.Groups[1].Value.ToLowerInvariant();
                    negated.Add(word);
                    return "not_" + word;
                }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                int termRank = 0;
                foreach (string term in MemoryQueryTerms(body).Select(NormalizeMemoryTopic)
                    .Where(t => IsUsefulMemoryTopic(t) && !negated.Contains(t)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    textCounts[term] = textCounts.GetValueOrDefault(term) + 1;
                    latest[term] = ordinal;
                    // Preserve the common query extractor's entity/title priority
                    // instead of alphabetically promoting generic narrative words.
                    lexicalPriority[term] = Math.Min(lexicalPriority.GetValueOrDefault(term, int.MaxValue), termRank++);
                }
            }

            // Existing content tags take precedence over unclassified words.
            // Count distinct records, so a repeated gesture in one long turn
            // cannot outweigh a topic mentioned by several source records.
            return explicitCounts.Keys.Concat(textCounts.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(t => explicitCounts.ContainsKey(t))
                .ThenByDescending(t => explicitCounts.GetValueOrDefault(t) + textCounts.GetValueOrDefault(t))
                .ThenByDescending(t => latest[t])
                .ThenBy(t => lexicalPriority.GetValueOrDefault(t, int.MaxValue)).ThenBy(t => t, StringComparer.Ordinal)
                .Take(limit).ToList();
        }

        private static string NormalizeMemoryTopic(string value) => (value ?? "").Trim().ToLowerInvariant();

        private static bool IsUsefulMemoryTopic(string value) => value.Length >= 3 && value.Length <= 64
            && !MemoryTopicMetadata.Contains(value)
            && Regex.IsMatch(value, @"^[\p{L}][\p{L}\p{Nd}_ -]*$", RegexOptions.CultureInvariant);

        private static string MemoryTopicSourceText(string value)
        {
            string text = value ?? "";
            foreach (string notice in MemoryTopicProvenanceNotices)
                text = text.Replace(notice, "", StringComparison.OrdinalIgnoreCase);
            return Regex.Replace(text, @"(?m)^\s*(?:Player|Speaker [^\r\n:]+) said or visibly did:\s*", "", RegexOptions.CultureInvariant);
        }
    }
}
