using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly string[] FinalGauntletReviewHarnessPhrases =
        {
            "production conversation path",
            "mapped requirement",
            "behavioral requirement",
            "respond naturally and remain grounded",
            "speak with me naturally about the matter at hand",
            "exercise every mapped requirement"
        };

        private static Dictionary<string, object>
            BuildFinalGauntletReviewPack(
                ReignDbConnection connection,
                string campaignId,
                string runId,
                List<Dictionary<string, object>> cases,
                List<Dictionary<string, object>> evidence)
        {
            string[] preferredFamilies =
            {
                "identity", "situational_awareness", "world_state",
                "personality", "relationships", "memory", "group",
                "actions", "adversarial", "long_horizon", "final_scene"
            };
            Dictionary<string, Dictionary<string, object>> evidenceByCase =
                (evidence ?? new List<Dictionary<string, object>>())
                .GroupBy(item =>
                    ReadString(item, "case_instance_id", ""),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last(),
                    StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> reviewableCases =
                (cases ?? new List<Dictionary<string, object>>())
                .Where(row =>
                {
                    string id =
                        ReadString(row, "case_instance_id", "");
                    return evidenceByCase.TryGetValue(
                            id, out Dictionary<string, object> item)
                        && HasReviewTranscript(item)
                        && !ReviewTranscriptContainsHarnessLanguage(item);
                })
                .ToList();
            int contaminatedCaseCount = (cases
                    ?? new List<Dictionary<string, object>>())
                .Count(row =>
                {
                    string id = ReadString(
                        row, "case_instance_id", "");
                    return evidenceByCase.TryGetValue(
                            id, out Dictionary<string, object> item)
                        && HasReviewTranscript(item)
                        && ReviewTranscriptContainsHarnessLanguage(item);
                });
            List<Dictionary<string, object>> selected =
                reviewableCases
                .OrderBy(row =>
                {
                    int index = Array.FindIndex(
                        preferredFamilies,
                        family => ReadString(row, "family", "")
                            .IndexOf(
                                family,
                                StringComparison.OrdinalIgnoreCase) >= 0);
                    return index < 0 ? int.MaxValue : index;
                })
                .ThenByDescending(row =>
                    ReadString(row, "state", "") == "Failed"
                    || ReadString(row, "state", "") == "ProviderExhausted")
                .ThenBy(row => ReadString(row, "case_instance_id", ""))
                .GroupBy(row => ReadString(row, "family", "unknown"))
                .Select(group => group.First())
                .Take(12)
                .ToList();
            if (selected.Count < 10)
                selected.AddRange(reviewableCases
                    .Where(candidate => !selected.Contains(candidate))
                    .Take(10 - selected.Count));

            ExecuteSql(
                connection,
                "DELETE FROM final_gauntlet_review_items WHERE run_id=$run;",
                new Dictionary<string, object> { ["run"] = runId });
            List<object> blinded = new List<object>();
            List<object> answerKey = new List<object>();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int ordinal = 0;
            foreach (Dictionary<string, object> row in selected)
            {
                string caseId = ReadString(
                    row, "case_instance_id", "");
                string reviewId = "REVIEW-"
                    + PromptHash(runId + "|" + caseId)
                        .Substring(0, 10).ToUpperInvariant();
                evidenceByCase.TryGetValue(
                    caseId,
                    out Dictionary<string, object> evidenceRow);
                Dictionary<string, object> reviewer =
                    new Dictionary<string, object>
                    {
                        ["reviewId"] = reviewId,
                        ["ordinal"] = ++ordinal,
                        ["mode"] = ReadString(row, "mode", ""),
                        ["transcript"] = ExtractReviewTranscript(evidenceRow),
                        ["immersionEligible"] = true,
                        ["rubric"] = new[]
                        {
                            "factual grounding", "epistemic realism",
                            "character fidelity", "social awareness",
                            "emotional continuity", "agency",
                            "situational embodiment", "memory integration",
                            "responsiveness", "linguistic naturalness",
                            "specificity", "non-repetition"
                        },
                        ["scoreRange"] = "0-4",
                        ["reviewerNotes"] = string.Empty
                    };
                Dictionary<string, object> key =
                    new Dictionary<string, object>
                    {
                        ["reviewId"] = reviewId,
                        ["caseInstanceId"] = caseId,
                        ["family"] = ReadString(row, "family", ""),
                        ["state"] = ReadString(row, "state", ""),
                        ["behavioralRequirement"] =
                            ReadString(row, "behavioral_requirement", ""),
                        ["hardProhibitions"] =
                            ReadString(row, "hard_prohibitions", "")
                    };
                blinded.Add(reviewer);
                answerKey.Add(key);
                ExecuteSql(
                    connection,
                    @"INSERT INTO final_gauntlet_review_items(
run_id,review_id,case_instance_id,blinded_payload_json,answer_key_json,created_ts)
VALUES($run,$review,$case,$blind,$key,$ts);",
                    new Dictionary<string, object>
                    {
                        ["run"] = runId,
                        ["review"] = reviewId,
                        ["case"] = caseId,
                        ["blind"] = Json.Serialize(reviewer),
                        ["key"] = Json.Serialize(key),
                        ["ts"] = now
                    });
            }
            string root = FinalGauntletRunArtifactRoot(campaignId, runId);
            Dictionary<string, object> pack =
                new Dictionary<string, object>
                {
                    ["schema"] = "reign-final-gauntlet-review-pack-v2",
                    ["runId"] = runId,
                    ["blinded"] = true,
                    ["excludedHarnessContaminatedCases"] =
                        contaminatedCaseCount,
                    ["items"] = blinded,
                    ["instructions"] =
                        "Score each dimension 0-4 without viewing the private answer key."
                };
            WriteJsonObject(Path.Combine(root, "review-pack.json"), pack);
            File.WriteAllText(
                Path.Combine(root, "review-pack.md"),
                RenderFinalGauntletReviewMarkdown(blinded),
                System.Text.Encoding.UTF8);
            WriteJsonObject(
                Path.Combine(root, "review-answer-key.private.json"),
                new Dictionary<string, object>
                {
                    ["schema"] =
                        "reign-final-gauntlet-review-answer-key-v1",
                    ["runId"] = runId,
                    ["items"] = answerKey
                });
            return new Dictionary<string, object>
            {
                ["itemCount"] = blinded.Count,
                ["excludedHarnessContaminatedCases"] =
                    contaminatedCaseCount,
                ["path"] = Path.Combine(root, "review-pack.json"),
                ["markdownPath"] =
                    Path.Combine(root, "review-pack.md"),
                ["answerKeyPath"] =
                    Path.Combine(root, "review-answer-key.private.json")
            };
        }

        private static object ExtractReviewTranscript(
            Dictionary<string, object> evidenceRow)
        {
            if (evidenceRow == null) return new List<object>();
            string payload = ReadString(evidenceRow, "payload_json", "");
            if (string.IsNullOrWhiteSpace(payload)) return new List<object>();
            try
            {
                Dictionary<string, object> parsed =
                    Json.Deserialize<Dictionary<string, object>>(payload);
                List<object> transcript = new List<object>();
                HashSet<string> seen =
                    new HashSet<string>(StringComparer.Ordinal);
                if (parsed.TryGetValue(
                        "sourceAuditEntries", out object entries))
                    CollectReviewTranscript(
                        entries, transcript, seen, 0);
                if (transcript.Count == 0)
                    CollectReviewTranscript(
                        parsed, transcript, seen, 0);
                if (transcript.Count <= 40)
                    return transcript;
                List<object> compact = transcript.Take(20).ToList();
                compact.Add(new Dictionary<string, object>
                {
                    ["role"] = "editorial",
                    ["speaker"] = "",
                    ["text"] = "[Earlier middle transcript omitted from this review view; the complete evidence remains in the case report.]"
                });
                compact.AddRange(transcript.Skip(
                    Math.Max(20, transcript.Count - 20)));
                return compact;
            }
            catch
            {
                return new List<object>();
            }
        }

        private static bool HasReviewTranscript(
            Dictionary<string, object> evidenceRow)
        {
            object transcript =
                ExtractReviewTranscript(evidenceRow);
            return transcript is IEnumerable sequence
                && sequence.Cast<object>().Any();
        }

        private static bool ReviewTranscriptContainsHarnessLanguage(
            Dictionary<string, object> evidenceRow)
        {
            object extracted = ExtractReviewTranscript(evidenceRow);
            if (!(extracted is IEnumerable sequence)) return false;
            foreach (Dictionary<string, object> row in sequence
                .Cast<object>()
                .OfType<Dictionary<string, object>>())
            {
                if (!ReadString(row, "role", "").Equals(
                        "player", StringComparison.OrdinalIgnoreCase))
                    continue;
                string text = ReadString(row, "text", "");
                if (FinalGauntletReviewHarnessPhrases.Any(phrase =>
                    text.IndexOf(
                        phrase,
                        StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
            }
            return false;
        }

        private static void CollectReviewTranscript(
            object value,
            List<object> transcript,
            HashSet<string> seen,
            int depth)
        {
            if (value == null || transcript.Count >= 250 || depth > 16)
                return;
            if (value is Dictionary<string, object> row)
            {
                string text = ReadString(row, "text", "");
                string role = ReadString(row, "role", "");
                string speaker = FirstNonEmpty(
                    ReadString(row, "speaker", ""),
                    ReadString(row, "speakerName", ""),
                    ReadString(row, "name", ""));
                if (!string.IsNullOrWhiteSpace(text)
                    && (!string.IsNullOrWhiteSpace(role)
                        || !string.IsNullOrWhiteSpace(speaker)))
                {
                    string normalized = NormalizeFinalGauntletReviewText(text);
                    string key = role.Trim().ToLowerInvariant()
                        + "\n" + speaker.Trim().ToLowerInvariant()
                        + "\n" + normalized;
                    if (seen.Add(key))
                    {
                        transcript.Add(
                            new Dictionary<string, object>
                            {
                                ["role"] = role,
                                ["speaker"] = speaker,
                                ["text"] = text.Trim()
                            });
                    }
                }
                foreach (object nested in row.Values)
                    CollectReviewTranscript(
                        nested, transcript, seen, depth + 1);
                return;
            }
            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                    CollectReviewTranscript(
                        entry.Value, transcript, seen, depth + 1);
                return;
            }
            if (value is IEnumerable sequence
                && !(value is string))
            {
                foreach (object item in sequence)
                    CollectReviewTranscript(
                        item, transcript, seen, depth + 1);
            }
        }

        private static string NormalizeFinalGauntletReviewText(
            string text)
        {
            return string.Join(
                " ",
                (text ?? string.Empty).Split(
                    (char[])null,
                    StringSplitOptions.RemoveEmptyEntries));
        }

        private static string RenderFinalGauntletReviewMarkdown(
            IEnumerable<object> items)
        {
            System.Text.StringBuilder markdown =
                new System.Text.StringBuilder();
            markdown.AppendLine("# Reign Final Conversation Review");
            markdown.AppendLine();
            markdown.AppendLine(
                "Each scene is blinded. Score the twelve dimensions from 0 to 4; consult the private answer key only after scoring.");
            int ordinal = 0;
            foreach (Dictionary<string, object> item in
                (items ?? Enumerable.Empty<object>())
                    .OfType<Dictionary<string, object>>())
            {
                ordinal++;
                markdown.AppendLine();
                markdown.AppendLine("## " + ordinal.ToString()
                    + ". " + ReadString(item, "mode", "conversation"));
                markdown.AppendLine();
                foreach (Dictionary<string, object> line in
                    ReadObjectList(item, "transcript")
                        .OfType<Dictionary<string, object>>())
                {
                    string speaker = FirstNonEmpty(
                        ReadString(line, "speaker", ""),
                        ReadString(line, "role", ""),
                        "Unknown");
                    markdown.Append("**")
                        .Append(speaker)
                        .Append(":** ")
                        .AppendLine(ReadString(line, "text", ""));
                    markdown.AppendLine();
                }
                markdown.AppendLine(
                    "Scores: grounding __ / epistemic __ / character __ / social __ / emotion __ / agency __ / embodiment __ / memory __ / responsiveness __ / naturalness __ / specificity __ / repetition __");
            }
            return markdown.ToString();
        }
    }
}
