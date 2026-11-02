using System;
using System.Collections.Generic;
using System.Linq;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static bool NpcLivesFixtureAuthorized(Dictionary<string, object> p, Dictionary<string, object> run,
            Dictionary<string, object> runtime, Dictionary<string, object> arm)
        {
            string campaign = ReadString(p, "campaignId", ""), instance = ReadString(p, "gameInstanceId", "");
            var enrollment = ReadDictionary(run, "enrollment");
            var command = ReadDictionaryList(run, "commands").SingleOrDefault(c => ReadString(c, "commandId", "") == ReadString(p, "commandId", ""));
            var supplied = ReadDictionary(p, "testCase"); var recorded = ReadDictionary(command, "testCase");
            var saveSync = ReadDictionary(runtime, "saveSync");
            return campaign.Length > 0 && instance.Length > 0 && IsLiveTestArmed(arm) && IsFreshLiveTestRuntime(runtime)
                && NpcLivesEnrollmentValid(enrollment, campaign) && ReadString(run, "mode", "") == "npc_lives"
                && new[] { "running", "queued" }.Contains(ReadString(run, "status", ""))
                && ReadString(run, "runId", "") == ReadString(p, "runId", "") && ReadString(run, "campaignId", "") == campaign
                && ReadString(run, "gameInstanceId", "") == instance && ReadString(runtime, "gameInstanceId", "") == instance
                && ReadString(runtime, "campaignId", "") == campaign
                && ReadString(runtime, "activeSaveName", "") == ReadString(enrollment, "disposableSaveName", "")
                && ReadBool(saveSync, "ready", false) && !ReadBool(saveSync, "alignmentPending", true)
                && ReadString(saveSync, "timelineId", "") == ReadString(enrollment, "timelineId", "")
                && !ReadBool(ReadDictionary(runtime, "nativeSave"), "isSaving", true)
                && ContinuityTimeline(p) == ReadString(enrollment, "timelineId", "")
                && ReadString(p, "activeSaveName", "") == ReadString(enrollment, "disposableSaveName", "")
                && ReadString(p, "timeMode", "") == "Stop" && !ReadBool(p, "nativeSaveInProgress", true)
                && command != null && ReadString(command, "operation", "") == "npc_lives_test"
                // Native dispatch acknowledges the leased command before invoking this route.
                && new[] { "leased", "accepted", "running" }.Contains(ReadString(command, "status", ""))
                && ReadString(p, "leaseId", "").Length > 0
                && ReadString(command, "leaseId", "") == ReadString(p, "leaseId", "")
                && ParseLiveTestUtc(ReadString(command, "leaseExpiresUtc", "")) > DateTimeOffset.UtcNow
                && supplied.Count > 0 && CanonicalJson(supplied) == CanonicalJson(recorded)
                && ReadString(supplied, "phase", "") == "prepare";
        }
        private static string PrepareNpcLivesFixture(ReignDbConnection c, NpcLivesState state,
            Dictionary<string, object> p, NpcLivesOptions options, double day)
        {
            if (!NpcLivesFixtureAuthorized(p, LoadLiveTestRun(state.CampaignId, ReadString(p, "runId", "")),
                LoadLiveTestRuntime(state.CampaignId), ReadJsonObject(LiveTestArmPath())))
                throw new InvalidOperationException("An exact armed native NPC Lives command lease and disposable Current are required for invitation setup.");
            var test = ReadDictionary(p, "testCase");
            string hero = ReadString(test, "heroId", ""), player = ReadFirstString(p, "playerId", "playerHeroStringId"), party = ReadString(p, "partyId", "");
            var member = ReadDictionaryList(p, "members").SingleOrDefault(h => ReadString(h, "heroId", "") == hero);
            if (hero.Length == 0 || hero == player || member == null || party.Length == 0 || !ReadBool(member, "inMainParty", false)
                || ReadString(member, "partyId", "") != party || !ReadBool(member, "alive", false)
                || !ReadBool(member, "free", false) || !ReadBool(member, "adult", false))
                throw new InvalidOperationException("Invitation setup requires an actual living free adult companion in the enrolled main party.");
            var definition = ReadNpcStoryDefinitions().SingleOrDefault(d => d.Id == ReadString(test, "templateId", ""));
            if (definition == null || !new[] { "brief", "outing", "major" }.Contains(definition.Category)
                || !options.Allows(definition.Category)) throw new InvalidOperationException("Choose an enabled authored brief, outing or major story template.");
            int chapter = definition.Phases.FindIndex(s => s.Id == ReadString(test, "chapterId", ""));
            if (chapter < 0) throw new InvalidOperationException("Choose an exact authored chapter identity.");
            string invitationKind = ReadString(test, "invitationKind", "story");
            if (invitationKind != "story" && invitationKind != "leave") throw new InvalidOperationException("Choose story or leave invitation setup.");
            if (invitationKind == "leave" && (definition.Category != "outing" || chapter != 0 || !options.Allows("leave")))
                throw new InvalidOperationException("Leave setup requires an enabled personal leave and the first chapter of an authored outing.");
            var locations = ReadDictionaryList(p, "locations").Take(2048).Select(l => new NpcStoryLocation {
                Id = ReadString(l, "id", ""), Name = ReadString(l, "name", ""), Kind = ReadString(l, "kind", ""),
                Available = ReadBool(l, "available", false), Coastal = ReadBool(l, "coastal", false), TravelDays = ReadDouble(l, "travelDays", double.MaxValue) }).ToList();
            var selectedLocations = SelectNpcLivesFixtureLocations(definition.Phases[chapter], locations,
                ReadString(p, "locationId", ""), ReadString(test, "locationId", ""));
            if (invitationKind == "leave")
            {
                var traits = ReadJsonObject(CharacterFile(state.CampaignId, hero, "traits.json"));
                var candidate = new NpcLifeCandidate { HeroId = hero,
                    Affinity = NpcLivesAffinity(c, hero, player, ReadInt(member, "nativeRelation", 0)),
                    Honor = NarrativePersonalityScore(traits, member, "honor"), Compassion = NarrativePersonalityScore(traits, member, "compassion"),
                    UnsafeLeaveLocationIds = ReadStringList(member, "unsafeLeaveLocationIds").Take(2048).ToList(),
                    DutyPressure = ReadBool(member, "isGovernor", false) || ReadBool(member, "isPartyLeader", false) ? 1 : 0 };
                return PrepareNpcLivesLeaveInvitation(state, definition, candidate, selectedLocations, ReadString(p, "locationId", ""),
                    ReadString(p, "runId", "") + "|" + ReadString(p, "commandId", ""), day);
            }
            return PrepareNpcLivesStoryInvitation(state, definition, chapter, hero, selectedLocations, ReadString(p, "locationId", ""),
                ReadString(p, "runId", "") + "|" + ReadString(p, "commandId", ""), day);
        }
        private static string PrepareNpcLivesLeaveInvitation(NpcLivesState state, NpcStoryDefinition definition,
            NpcLifeCandidate npc, IReadOnlyList<NpcStoryLocation> locations, string current, string fixture, double day)
        {
            string operation = "npc_leave_fixture:" + fixture, id = "leave_fixture_" + PromptHash(fixture).Substring(0, 32);
            string hash = PromptHash(CanonicalJson(TestDict("hero", npc.HeroId, "template", definition.Id, "version", definition.Version)));
            if (state.OperationReceipts.TryGetValue(operation, out var prior))
            {
                if (prior != hash || !state.Leaves.Any(l => l.Id == id)) throw new InvalidOperationException("This leave setup identity has different terms.");
                return id;
            }
            if (definition.Category != "outing" || definition.Phases.Count == 0 || !NpcLivesPolicy.Finite(day) || day < 0
                || state.Stories.Any(s => s.HeroId == npc.HeroId && (s.Status == "active" || s.Status == "invited" || s.Status == "between_phases"))
                || state.Leaves.Any(l => l.HeroId == npc.HeroId && l.Status != "returned" && l.Status != "completed" && l.Status != "cancelled"))
                throw new InvalidOperationException("Resolve the companion's existing commitment before authored leave setup.");
            double absence = 3 + Math.Floor(NpcLivesEngine.Draw(fixture + "|absence") * 5);
            string location = NpcLivesEngine.BindLocation(definition.Phases[0],
                NpcLivesEngine.LeaveDestinations(npc, locations).Where(l => l.TravelDays <= (absence - 1) / 2), current, id);
            if (location.Length == 0) throw new InvalidOperationException("The authored leave has no safe native destination for this NPC within its absence.");
            var leave = new NpcPersonalLeave { Id = id, HeroId = npc.HeroId, Trust = npc.Affinity,
                Honor = npc.Honor, Compassion = npc.Compassion, DutyPressure = npc.DutyPressure,
                Urgency = 1 + (int)(NpcLivesEngine.Draw(fixture + "|urgency") * 3),
                Purpose = definition.Title + ": " + definition.Phases[0].Objectives.Last().Instruction,
                DestinationId = location, AbsenceDays = absence, ProposalId = "proposal_" + PromptHash(id + "|1").Substring(0, 32) };
            state.Leaves.Add(leave); state.OperationReceipts[operation] = hash;
            return id;
        }
        private static IReadOnlyList<NpcStoryLocation> SelectNpcLivesFixtureLocations(NpcStoryPhase phase,
            IReadOnlyList<NpcStoryLocation> locations, string current, string selected)
        {
            if (selected.Length == 0) return locations;
            var matches = locations.Where(l => l.Id == selected).ToList();
            if (matches.Count != 1 || !matches[0].Available
                || NpcLivesEngine.BindLocation(phase, matches, current, "operator_destination") != selected)
                throw new InvalidOperationException("The selected setup destination must be an actual available native location matching the authored chapter.");
            return matches;
        }
        private static string PrepareNpcLivesStoryInvitation(NpcLivesState state, NpcStoryDefinition definition, int chapter,
            string hero, IReadOnlyList<NpcStoryLocation> locations, string current, string fixture, double day)
        {
            string operation = "npc_fixture:" + fixture, id = "story_fixture_" + PromptHash(fixture).Substring(0, 32);
            string hash = PromptHash(CanonicalJson(TestDict("hero", hero, "template", definition.Id, "version", definition.Version, "chapter", chapter)));
            if (state.OperationReceipts.TryGetValue(operation, out var prior))
            {
                if (prior != hash || !state.Stories.Any(s => s.Id == id)) throw new InvalidOperationException("This invitation setup identity has different terms.");
                return id;
            }
            if (chapter < 0 || chapter >= definition.Phases.Count || !NpcLivesPolicy.Finite(day) || day < 0
                || state.Stories.Any(s => s.HeroId == hero && (s.Status == "active" || s.Status == "invited" || s.Status == "between_phases"))
                || state.Leaves.Any(l => l.HeroId == hero && l.Status != "returned" && l.Status != "completed" && l.Status != "cancelled"))
                throw new InvalidOperationException("Resolve the companion's existing personal commitment before invitation setup.");
            string location = NpcLivesEngine.BindLocation(definition.Phases[chapter], locations, current, id);
            if (location.Length == 0) throw new InvalidOperationException("The authored chapter has no available native destination.");
            var story = new NpcStoryInstance { Id = id, HeroId = hero, Definition = Json.Deserialize<NpcStoryDefinition>(Json.Serialize(definition)),
                PhaseIndex = chapter, LocationId = location, LocationName = locations.FirstOrDefault(l => l.Id == location)?.Name ?? "",
                StartedDay = day, Status = "invited", ProposalId = "proposal_" + PromptHash(id + "|1").Substring(0, 32) };
            // This is explicit operator setup, never evidence of natural scheduling,
            // earlier completed chapters, player agreement or earned progress.
            state.Stories.Add(story); state.Character(hero).PendingStoryId = id;
            if (definition.Category == "major") state.Character(hero).ActiveMajorId = id;
            state.OperationReceipts[operation] = hash;
            return id;
        }
    }
}
