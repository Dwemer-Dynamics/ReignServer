using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Reign.Core.Contracts.Dialogue
{
    public sealed class GroupTurnParticipant
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public sealed class GroupTurnLine
    {
        public string SpeakerId { get; set; } = "";
        public string Role { get; set; } = "";
        public string Text { get; set; } = "";
        public string AudienceId { get; set; } = "";
    }

    public sealed class GroupTurnPlan
    {
        public List<string> AddressedIds { get; set; } = new List<string>();
        public List<string> ResponseOrder { get; set; } = new List<string>();
    }

    // Scheduling only: naming someone does not prove knowledge, assent, evasion,
    // or that a question was answered. Each observer's evidence stays separate.
    public static class GroupConversationTurnOrder
    {
        // Order response opportunities only. Every present participant reaches
        // their contextual writer; the writer owns speech versus quiet.
        public static GroupTurnPlan Plan(IEnumerable<GroupTurnParticipant>? participants, string playerText,
            string seed, IEnumerable<GroupTurnLine>? transcript = null, string replyFocusId = "")
        {
            var roster = Roster(participants);
            string speech = PublicSpeech(playerText);
            var prior = (transcript ?? Enumerable.Empty<GroupTurnLine>()).Where(l => l != null).ToList();
            if (prior.Count > 0 && IsPlayer(prior[prior.Count - 1])
                && string.Equals(prior[prior.Count - 1].Text?.Trim(), playerText?.Trim(), StringComparison.Ordinal))
                prior.RemoveAt(prior.Count - 1);
            var addressed = DirectInvitations(roster, speech);
            foreach (string id in NarratedResponseInvitations(roster, playerText ?? ""))
                if (!addressed.Contains(id)) addressed.Add(id);
            bool collective = Regex.IsMatch(speech,
                @"(?:^|[.!?;]\s*)(?:everyone|both|all\s+of\s+you|you\s+all)\s*[,!:?]|\b(?:both\s+of\s+you|each\s+of\s+you|you\s+(?:two|three|both))\s+(?:think|tell|answer|share|say|give|explain)\b",
                RegexOptions.IgnoreCase);
            if (collective && addressed.Count == 0) addressed.AddRange(roster.Select(p => p.Id));
            if (addressed.Count == 0 && speech.Length > 0)
            {
                var previous = prior.LastOrDefault(l => string.Equals(l.Role, "npc", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrEmpty(l.AudienceId) && roster.Any(p => p.Id.Equals(l.SpeakerId, StringComparison.OrdinalIgnoreCase)));
                bool followUp = Regex.IsMatch(speech,
                    @"^\s*(?:why|how|yes|no|perhaps|maybe|because|exactly|indeed|of\s+course|I\s+(?:agree|disagree|prefer|would\s+choose)|what\s+do\s+you\s+mean)\b", RegexOptions.IgnoreCase);
                if (previous != null && (followUp || PublicSpeech(previous.Text).EndsWith("?", StringComparison.Ordinal)))
                    addressed.Add(previous.SpeakerId);
                else if (roster.Any(p => p.Id.Equals(replyFocusId, StringComparison.OrdinalIgnoreCase)))
                    addressed.Add(replyFocusId);
            }
            return new GroupTurnPlan { AddressedIds = addressed,
                ResponseOrder = Order(roster, speech, seed, addressed) };
        }

        private static string PublicSpeech(string text)
        {
            string speech = Regex.Replace(text ?? "", @"\*[^*]*\*", " ");
            string trimmed = speech.Trim();
            if (trimmed.Length > 1 && ((trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
                || (trimmed[0] == '“' && trimmed[trimmed.Length - 1] == '”')))
                speech = trimmed.Substring(1, trimmed.Length - 2);
            speech = Regex.Replace(speech, "[\"“][^\"”]*[\"”]", " ");
            return Regex.Replace(speech, @"(?<![\p{L}\p{N}])['‘][^'’\r\n]*['’](?![\p{L}\p{N}])", " ").Trim();
        }

        private static List<string> NarratedResponseInvitations(List<GroupTurnParticipant> roster, string text)
        {
            var result = new List<string>();
            // Strip externally quoted narration before interpreting current actions.
            text = Regex.Replace(text ?? "", "[\"“][^\"”]*[\"”]", " ");
            foreach (Match action in Regex.Matches(text, @"\*([^*]+)\*"))
            {
                string current = action.Groups[1].Value.Trim();
                if (Regex.IsMatch(current, @"\b(?:imagin\w*|remember\w*|recall\w*|dream\w*|thoughts?|mind|pretend\w*|rehears\w*)\b", RegexOptions.IgnoreCase)) continue;
                foreach (var person in roster)
                    foreach (string name in ParticipantNames(person, roster))
                    {
                        string who = Regex.Escape(name) + @"(?![\p{L}\p{N}])";
                        bool addressed = Regex.IsMatch(current,
                            @"^I\s+(?:wait\s+for\s+" + who + @"\s+to\s+(?:respond|answer|reply)\b|(?:look|turn)\s+(?:over\s+)?(?:at|to|toward|towards)\s+" + who + @"(?:\s*[,.;]|\s+(?:and|with)\b|$)|(?:address|ask|greet)\s+" + who + @"(?:\s*[,.;]|\s+|$))",
                            RegexOptions.IgnoreCase);
                        if (addressed && !result.Contains(person.Id)) result.Add(person.Id);
                    }
            }
            return result;
        }

        private static List<string> DirectInvitations(List<GroupTurnParticipant> roster, string speech)
        {
            var names = roster.ToDictionary(p => p.Id, p => ParticipantNames(p, roster), StringComparer.OrdinalIgnoreCase);
            string allNames = string.Join("|", names.Values.SelectMany(x => x).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Length).Select(Regex.Escape));
            if (allNames.Length == 0) return new List<string>();
            var invitations = new List<KeyValuePair<string, int>>();
            foreach (var participant in roster)
                foreach (string name in names[participant.Id])
                    foreach (Match match in Regex.Matches(speech, @"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase))
                    {
                        int start = speech.LastIndexOfAny(new[] { '.', '!', '?', ';', '\n' }, Math.Max(0, match.Index - 1));
                        string before = speech.Substring(start + 1, match.Index - start - 1);
                        string after = speech.Substring(match.Index + match.Length);
                        bool leading = Regex.IsMatch(before, @"^\s*(?:(?:only|just)\s+)?(?:(?:" + allNames + @")\s*(?:,|and|&)\s*)*$", RegexOptions.IgnoreCase)
                            && Regex.IsMatch(after, @"^\s*(?:(?:,\s*|\s+(?:and|&)\s+)(?:" + allNames + @"))*\s*[,!.:?]|^\s+(?:please|tell|can|could|would|will|what|do|did|how)\b", RegexOptions.IgnoreCase);
                        bool trailing = before.TrimEnd().EndsWith(",", StringComparison.Ordinal)
                            && Regex.IsMatch(after, @"^\s*[.?!:](?:\s|$)|^\s*$");
                        if (leading || trailing) invitations.Add(new KeyValuePair<string, int>(participant.Id, match.Index));
                    }
            return invitations.OrderBy(p => p.Value).Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> ParticipantNames(GroupTurnParticipant person, List<GroupTurnParticipant> roster)
        {
            string full = (person.Name ?? "").Trim(), first = FirstName(full);
            var names = new List<string>();
            if (full.Length > 0 && roster.Count(p => (p.Name ?? "").Trim().Equals(full, StringComparison.OrdinalIgnoreCase)) == 1) names.Add(full);
            if (first.Length >= 3 && roster.Count(p => FirstName(p.Name).Equals(first, StringComparison.OrdinalIgnoreCase)) == 1) names.Add(first);
            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<string> ResolveAddressed(IEnumerable<GroupTurnParticipant>? participants,
            string playerText, IEnumerable<GroupTurnLine>? transcript = null)
        {
            var roster = Roster(participants);
            var named = Named(roster, playerText);
            bool group = Regex.IsMatch(playerText ?? "",
                @"\b(?:both\s+of\s+you|all\s+of\s+you|each\s+of\s+you|you\s+(?:all|two|three|both))\b|(?:^|[.!?]\s+)(?:everyone|both)\s*[,!:?]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (group) return named.Concat(roster.Select(p => p.Id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (named.Count > 0) return named;

            // Only an explicit short reply cue can carry forward focus, and only
            // from the immediately preceding NPC block. Never search past a
            // player boundary for a historic unanswered-looking question mark.
            if (Regex.IsMatch(playerText ?? "", @"^\s*(?:yes|no|perhaps|maybe|because|exactly|indeed|of\s+course|I\s+(?:agree|disagree|prefer|would\s+choose))\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var lines = (transcript ?? Enumerable.Empty<GroupTurnLine>()).Where(l => l != null).ToList();
                if (lines.Count > 0 && IsPlayer(lines[lines.Count - 1])
                    && string.Equals(lines[lines.Count - 1].Text?.Trim(), playerText?.Trim(), StringComparison.Ordinal))
                    lines.RemoveAt(lines.Count - 1); // caller may already have appended current input
                var askers = new List<string>();
                for (int index = lines.Count - 1; index >= 0; index--)
                {
                    var line = lines[index];
                    if (IsPlayer(line)) break;
                    if (!string.Equals(line.Role, "npc", StringComparison.OrdinalIgnoreCase)) continue;
                    string speech = Regex.Replace(line.Text ?? "", @"\*[^*]*\*", "");
                    if (!speech.Contains("?") || !roster.Any(p => p.Id.Equals(line.SpeakerId, StringComparison.OrdinalIgnoreCase))) continue;
                    askers.Insert(0, line.SpeakerId);
                }
                if (askers.Count > 0) return askers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            // Ambiguous focus is an open floor, never an extra LLM-director call.
            return roster.Select(p => p.Id).ToList();
        }

        public static List<string> Order(IEnumerable<GroupTurnParticipant>? participants, string playerText,
            string seed, IEnumerable<string>? addressed = null)
        {
            var roster = Roster(participants);
            var all = roster.Select(p => p.Id).ToList();
            var priorities = Named(roster, playerText);
            if (priorities.Count == 0 && addressed != null)
            {
                var focus = addressed.Where(id => all.Contains(id, StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (focus.Count < all.Count) priorities = focus;
            }
            return priorities.Concat(all.Where(id => !priorities.Contains(id, StringComparer.OrdinalIgnoreCase))
                .OrderBy(id => StableKey(seed, id)).ThenBy(id => id, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        private static bool IsPlayer(GroupTurnLine line) => string.Equals(line.Role, "player", StringComparison.OrdinalIgnoreCase);
        private static List<GroupTurnParticipant> Roster(IEnumerable<GroupTurnParticipant>? participants) =>
            (participants ?? Enumerable.Empty<GroupTurnParticipant>()).Where(p => p != null && !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

        private static List<string> Named(List<GroupTurnParticipant> roster, string text)
        {
            var firstCounts = roster.GroupBy(p => FirstName(p.Name), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var fullCounts = roster.GroupBy(p => (p.Name ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            return roster.Select(p => {
                string full = (p.Name ?? "").Trim(), first = FirstName(full);
                int fullIndex = fullCounts[full] == 1 ? PhraseIndex(text, full) : int.MaxValue;
                int firstIndex = first.Length >= 3 && firstCounts[first] == 1 ? PhraseIndex(text, first) : int.MaxValue;
                return new { p.Id, Index = Math.Min(fullIndex, firstIndex) };
            }).Where(p => p.Index != int.MaxValue).OrderBy(p => p.Index)
                .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase).Select(p => p.Id).ToList();
        }

        private static string FirstName(string name) => (name ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        private static int PhraseIndex(string text, string phrase)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(phrase)) return int.MaxValue;
            var match = Regex.Match(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(phrase) + @"(?![\p{L}\p{N}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? match.Index : int.MaxValue;
        }

        private static ulong StableKey(string seed, string id)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                foreach (char ch in ((seed ?? "") + "|" + id).ToLowerInvariant()) { hash ^= ch; hash *= 1099511628211UL; }
                return hash;
            }
        }
    }
}
