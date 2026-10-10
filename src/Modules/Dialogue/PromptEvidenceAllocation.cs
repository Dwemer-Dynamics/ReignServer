using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // This expectation is assembled locally, never accepted from a client or
        // a model. Each section is captured before budget compaction/adapters.
        private static readonly ConditionalWeakTable<Dictionary<string, object>, Dictionary<string, object>> RequiredPromptEvidence = new();

        private static void ResetRequiredPromptEvidence(Dictionary<string, object> payload)
        {
            if (payload != null) RequiredPromptEvidence.Remove(payload);
        }

        private static void CaptureRequiredPromptEvidence(Dictionary<string, object> payload, string section, string text)
        {
            if (payload == null) return;
            var sections = RequiredPromptEvidence.GetOrCreateValue(payload);
            sections[section] = ContinuityPromptRecords(text, section == "canonicalTranscript").Where(r => r.Required)
                .Select(r => TestDict("id", r.SourceId, "section", section, "text", NormalizePromptSegment(r.Text),
                    "hash", PromptHash(r.Text), "kind", r.Kind)).ToList();
        }

        private static List<Dictionary<string, object>> CapturedRequiredPromptEvidence(Dictionary<string, object> payload)
        {
            if (payload == null || !RequiredPromptEvidence.TryGetValue(payload, out var sections)) return new();
            return sections.Values.OfType<List<Dictionary<string, object>>>().SelectMany(r => r)
                .Select(r => new Dictionary<string, object>(r)).ToList();
        }

        private static string ProtectSharedGroupPrompt(string text, string observer, Dictionary<string, object> payload)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            return RenderContinuityRecord("group_state_" + PromptHash(text).Substring(0, 24), "shared_group_state", observer,
                GroupConversationSessionId(payload), ReadDouble(payload, "worldDay", 0d), 115, true, text);
        }

        // Keep the locally validated rerank order through final prompt packing.
        // Legacy discovery scores are a fallback, not a second relevance judge.
        // Use this same order in reverse when serialized-token pressure requires
        // removing optional records, so the two allocation passes cannot disagree.
        private static IOrderedEnumerable<(ContinuityPromptRecord Record, string Section)> OrderPromptEvidenceRecords(
            IEnumerable<(ContinuityPromptRecord Record, string Section)> records) =>
            records.OrderByDescending(r => r.Record.Required)
                .ThenBy(r => r.Record.SelectionRank.HasValue ? 0 : 1)
                .ThenBy(r => r.Record.SelectionRank ?? int.MaxValue)
                .ThenByDescending(r => r.Record.Priority)
                .ThenBy(r => r.Section, StringComparer.Ordinal).ThenBy(r => r.Record.Order);

        private static (string Context, string Transcript) SelectSharedPromptRecords(string context, string transcript,
            int charAllowance, List<object> diagnostics)
        {
            var candidates = ContinuityPromptRecords(context, false).Select(r => (Record: r, Section: "selectedContextAndMemory"))
                .Concat(ContinuityPromptRecords(transcript, true).Select(r => (Record: r, Section: "canonicalTranscript"))).ToList();
            var selected = new HashSet<ContinuityPromptRecord>();
            int available = Math.Max(0, charAllowance);
            foreach (var item in OrderPromptEvidenceRecords(candidates))
            {
                var record = item.Record;
                bool fits = record.Required || record.Text.Length + 2 <= available;
                diagnostics.Add(TestDict("section", item.Section, "sourceId", record.SourceId, "kind", record.Kind,
                    "observer", record.Observer, "sessionId", record.Session, "worldDay", record.WorldDay,
                    "required", record.Required, "contentHash", PromptHash(record.Text), "retained", fits,
                    "reason", fits ? "selected_whole_record" : "lower_priority_record_exceeds_shared_allowance", "characters", record.Text.Length,
                    "priority", record.Priority, "selectionRank", record.SelectionRank));
                if (!fits) continue;
                selected.Add(record);
                available -= record.Text.Length + 2;
            }
            string Render(string section) => string.Join("\n\n", candidates.Where(c => c.Section == section && selected.Contains(c.Record))
                .OrderBy(c => c.Record.Order).Select(c => c.Record.Text));
            return (Render("selectedContextAndMemory"), Render("canonicalTranscript"));
        }
    }
}
