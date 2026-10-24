using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBeta.Shared.Characters
{
    public sealed class NpcLifeCandidate
    {
        public string HeroId { get; set; } = "";
        public string Interests { get; set; } = "";
        public int Affinity { get; set; }
        public double Honor { get; set; } = 50;
        public double Compassion { get; set; } = 50;
        public int DutyPressure { get; set; }
        public bool Available { get; set; }
    }
    public sealed class NpcStoryLocation
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public bool Available { get; set; } = true;
        public bool Coastal { get; set; }
        public double TravelDays { get; set; }
    }

    // Identical scheduling and evidence policy in native saves, server and tests.
    // A persisted daily check cannot be rerolled by reopening UI or changing sliders.
    public static class NpcLivesEngine
    {
        public static double Draw(string key)
        {
            using (var hash = SHA256.Create())
            {
                byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(key));
                // Explicit byte order keeps Windows and Linux choices identical.
                uint value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
                return value / 4294967296d;
            }
        }

        public static string BindLocation(NpcStoryPhase phase, IEnumerable<NpcStoryLocation> locations, string current, string seed)
        {
            if (phase.LocationKind == "current") return current;
            var eligible = locations.Where(l => l.Available && (!phase.RequiresCoast || l.Coastal) && (phase.LocationKind == "settlement"
                ? l.Kind == "town" || l.Kind == "village" || l.Kind == "castle" : l.Kind == phase.LocationKind))
                .OrderBy(l => Draw(seed + "|" + l.Id)).ToList();
            return eligible.FirstOrDefault(l => l.Id != current)?.Id ?? eligible.FirstOrDefault()?.Id ?? "";
        }

        public static NpcStoryDefinition? Choose(NpcLivesState state, NpcLifeCandidate npc, string category,
            IEnumerable<NpcStoryDefinition> definitions, double day, string seed)
        {
            var character = state.Character(npc.HeroId);
            return definitions.Where(d => d.Category == category && d.Phases.Count > 0
                && d.Phases[0].MinimumAffinity <= npc.Affinity && !state.RecentTemplates.Contains(d.Id)
                && !character.RecentFamilies.Contains(d.Family)
                && (category != "brief" || !state.RecentBriefFamilies.Contains(d.Family))
                && (!character.CompletedTemplates.Contains(d.Id) || (d.Repeatable && state.Stories.Any(s => s.HeroId == npc.HeroId
                    && s.Definition.Id == d.Id && s.CompletedDay.HasValue && day - s.CompletedDay.Value >= 31.5)
                    && !state.Stories.Any(s => s.HeroId == npc.HeroId && s.Definition.Id == d.Id && s.CompletedDay.HasValue && day - s.CompletedDay.Value < 31.5)))
                && (category != "major" || (character.ActiveMajorId.Length == 0 && day >= character.NextMajorDay && !state.UsedMajorTemplates.Contains(d.Id))))
                .OrderBy(d => -Math.Log(Math.Max(.000000001, Draw(seed + "|" + d.Id))) /
                    ((npc.Interests.IndexOf(d.Interest, StringComparison.OrdinalIgnoreCase) >= 0 && d.Interest.Length > 0 ? 3d : 1d)
                        * (state.Stories.Any(s => s.HeroId == npc.HeroId && s.Status == "completed" && s.Definition.RelatedMajorId == d.Id) ? 4d : 1d)))
                .FirstOrDefault();
        }

        public static string Evaluate(NpcLivesState state, IReadOnlyList<NpcLifeCandidate> roster, IReadOnlyList<NpcStoryDefinition> definitions,
            IReadOnlyList<NpcStoryLocation> locations, NpcLivesOptions options, double day, string current)
        {
            if (!NpcLivesPolicy.Finite(day) || day < 0) return "";
            // Upgrade labels from the same native location roster used to bind
            // destinations. A missing label never changes the bound identity.
            foreach (var saved in state.Stories.Where(s => s.LocationName.Length == 0 && s.LocationId.Length > 0))
                saved.LocationName = locations.FirstOrDefault(l => l.Id == saved.LocationId)?.Name ?? "";
            var ids = new HashSet<string>(roster.Where(h => h.Available).Select(h => h.HeroId), StringComparer.Ordinal);
            // Accepted commitments survive disablement. Unseen proposals do not turn
            // into a burst of invitations when a feature is re-enabled.
            foreach (var pending in state.Stories.Where(s => s.Status == "invited"))
            {
                if (ids.Contains(pending.HeroId) && options.Allows(pending.Definition.Category)) return pending.Id;
            }
            if (options.Allows("leave")) foreach (var leave in state.Leaves.Where(l => l.Status == "reconsider")) leave.Reopen(day);
            var dueLeave = state.Leaves.FirstOrDefault(l => options.Allows("leave") && l.Status == "discussing" && ids.Contains(l.HeroId));
            if (dueLeave != null) return dueLeave.Id;
            int today = (int)Math.Floor(day);
            foreach (var npc in roster.Where(h => h.Available).OrderBy(h => Draw(state.TimelineId + "|" + today + "|" + h.HeroId)))
            {
                var record = state.Character(npc.HeroId);
                if (record.LastRollDay >= today) continue;
                record.LastRollDay = today;
                if (!options.Preference(NpcLivesOptions.MasterKey) || day < record.NextInvitationDay
                    || state.Stories.Any(s => s.HeroId == npc.HeroId && (s.Status == "active" || s.Status == "invited" && options.Allows(s.Definition.Category)))
                    || state.Leaves.Any(l => l.HeroId == npc.HeroId && l.Status != "returned" && l.Status != "cancelled"
                        && l.Status != "interrupted" && (l.Status == "waiting" || l.Status == "away" || options.Allows("leave")))) continue;
                string seed = state.CampaignId + "|" + state.TimelineId + "|" + npc.HeroId + "|" + today;
                var feasible = definitions.Where(d => d.Phases.Count > 0 && (d.Phases[0].LocationKind == "current"
                    || BindLocation(d.Phases[0], locations, current, seed).Length > 0)).ToList();
                var eligibleWeights = NpcLivesPolicy.CategoryWeights.Where(w => w.Key == "leave"
                    ? locations.Any(l => l.Available && l.Kind != "hideout" && l.TravelDays <= 3)
                        && Choose(state, new NpcLifeCandidate { HeroId = npc.HeroId, Affinity = 100, Interests = npc.Interests }, "outing", feasible, day, seed) != null
                    : Choose(state, npc, w.Key == "independent" ? "brief" : w.Key, feasible, day, seed) != null
                        || state.Stories.Any(s => s.HeroId == npc.HeroId && s.Status == "between_phases" && s.Definition.Category == w.Key
                            && s.Definition.Phases[s.PhaseIndex].MinimumAffinity <= npc.Affinity)).ToDictionary(w => w.Key, w => w.Value);
                string category = NpcLivesPolicy.SelectCategory(NpcLivesPolicy.DailyProbabilities(npc.Affinity, options, eligibleWeights), Draw(seed + "|category"));
                if (category.Length == 0) continue;
                if (category == "independent")
                {
                    var hobby = Choose(state, npc, "brief", definitions, day, seed);
                    if (hobby != null)
                    {
                        record.NextInvitationDay = day + NpcLivesPolicy.InvitationDelay(options);
                        record.Facts.RemoveAll(f => f.Kind == "recent_activity");
                        record.Facts.Add(new NpcLifeFact { Id = "activity:" + seed, Kind = "recent_activity", SourceId = seed,
                            Text = "During a quiet interval you spent time on your own interest: " + hobby.Title + ". You may talk about that personal experience; do not invent player participation or mechanical rewards." });
                        Remember(state, record, hobby);
                    }
                    continue;
                }
                if (category == "leave")
                {
                    var purpose = Choose(state, new NpcLifeCandidate { HeroId = npc.HeroId, Affinity = 100, Interests = npc.Interests }, "outing", feasible, day, seed);
                    if (purpose == null) continue;
                    double absence = 3 + Math.Floor(Draw(seed + "|absence") * 5);
                    string location = BindLocation(purpose.Phases[0], locations.Where(l => l.TravelDays <= (absence - 1) / 2), current, seed);
                    if (location.Length == 0) continue;
                    var leave = new NpcPersonalLeave { Id = "leave_" + ShortId(seed), HeroId = npc.HeroId,
                        Trust = npc.Affinity, Honor = npc.Honor, Compassion = npc.Compassion, DutyPressure = npc.DutyPressure,
                        Urgency = 1 + (int)(Draw(seed + "|urgency") * 3),
                        Purpose = purpose.Title + ": " + purpose.Phases[0].Objectives.Last().Instruction,
                        DestinationId = location, AbsenceDays = absence, ProposalId = "proposal_" + ShortId(seed) };
                    record.NextInvitationDay = day + NpcLivesPolicy.InvitationDelay(options);
                    state.Leaves.Add(leave); Remember(state, record, purpose); return leave.Id;
                }
                var continuing = state.Stories.FirstOrDefault(s => s.HeroId == npc.HeroId && s.Status == "between_phases" && s.Definition.Category == category
                    && options.Allows(s.Definition.Category) && s.PhaseIndex < s.Definition.Phases.Count && s.Definition.Phases[s.PhaseIndex].MinimumAffinity <= npc.Affinity);
                var definition = continuing?.Definition ?? Choose(state, npc, category, feasible, day, seed);
                if (definition == null) continue;
                var story = continuing ?? new NpcStoryInstance { Id = "story_" + ShortId(seed), HeroId = npc.HeroId, Definition = definition, StartedDay = day };
                string link = definition.Category == "major" ? definition.Id : definition.RelatedMajorId;
                if (continuing == null && link.Length > 0)
                {
                    var earlier = state.Stories.Where(s => s.HeroId == npc.HeroId && s.Status == "completed" && s.Definition.RelatedMajorId == link).ToList();
                    story.EarlierExperiences = earlier.Select(s => s.Definition.Title).ToList();
                    bool coast = definitions.Any(d => d.RelatedMajorId == link && d.Phases.Any(p => p.RequiresCoast));
                    var place = earlier.Select(s => locations.FirstOrDefault(l => l.Id == s.LinkedLocationId && l.Available && (!coast || l.Coastal))).FirstOrDefault(l => l != null)
                        ?? locations.FirstOrDefault(l => l.Id == BindLocation(new NpcStoryPhase { LocationKind = "settlement", RequiresCoast = coast }, locations, current, seed + "|linked"));
                    if (place != null) { story.LinkedLocationId = place.Id; story.LinkedLocationName = place.Name; }
                }
                var phase = definition.Phases[story.PhaseIndex];
                string bound = BindLocation(phase, locations, current, seed + "|" + phase.Id);
                if (phase.LocationKind == "settlement" && !phase.RequiresCoast && story.LinkedLocationId.Length > 0
                    && locations.Any(l => l.Id == story.LinkedLocationId && l.Available)) bound = story.LinkedLocationId;
                if (phase.LocationKind != "current" && bound.Length == 0) continue;
                record.NextInvitationDay = day + NpcLivesPolicy.InvitationDelay(options);
                story.LocationId = bound; story.LocationName = locations.FirstOrDefault(l => l.Id == bound)?.Name ?? "";
                story.Status = "invited"; story.ProposalId = "proposal_" + ShortId(story.Id + "|" + story.Revision);
                if (continuing == null)
                {
                    state.Stories.Add(story); Remember(state, record, definition);
                    if (definition.Category == "major")
                    {
                        record.ActiveMajorId = story.Id; state.UsedMajorTemplates.Add(definition.Id);
                        record.NextMajorDay = day + NpcLivesPolicy.MajorDelay(options, Draw(seed + "|major"));
                    }
                }
                record.PendingStoryId = story.Id; return story.Id;
            }
            return "";
        }

        public static string CategorySwitch(string category) => category == "brief" ? "npcLivesBriefEnabled" : category == "major" ? "npcLivesMajorEnabled" : "npcLivesOutingsEnabled";
        private static string ShortId(string key) { using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(key)).Take(16).Select(b => b.ToString("x2"))); }
        private static void Remember(NpcLivesState state, NpcLifeRecord npc, NpcStoryDefinition definition)
        {
            state.RecentTemplates.Remove(definition.Id); state.RecentTemplates.Add(definition.Id);
            if (definition.Category == "brief")
            {
                state.RecentBriefFamilies.Remove(definition.Family); state.RecentBriefFamilies.Add(definition.Family);
                while (state.RecentBriefFamilies.Count > 8) state.RecentBriefFamilies.RemoveAt(0);
            }
            while (state.RecentTemplates.Count > 20) state.RecentTemplates.RemoveAt(0);
            npc.RecentFamilies.Remove(definition.Family); npc.RecentFamilies.Add(definition.Family);
            while (npc.RecentFamilies.Count > 3) npc.RecentFamilies.RemoveAt(0);
        }
    }
}
