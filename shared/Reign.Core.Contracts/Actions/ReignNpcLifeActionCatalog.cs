using System.Collections.Generic;

namespace Reign.Core.Contracts.Actions
{
    public static partial class ReignActionCatalog
    {
        private static IEnumerable<ActionDefinition> NpcLifeDefinitions()
        {
            ActionParameter Bound(string key) => new ActionParameter { Key = key, Label = key, Required = true, Automatic = true };
            ActionParameter Number(string key, string label, double min, double max) => new ActionParameter { Key = key, Label = label, Kind = "number", Required = true, Minimum = min, Maximum = max };
            yield return new ActionDefinition { Id = "npc_life.accept_story", Label = "Share this personal experience", Family = "personal",
                Adapter = "npc_life:story", Source = "ReignNpcLivesBehavior", Description = "Accept this NPC's current, versioned personal-story offer.",
                Parameters = new List<ActionParameter> { Bound("matterId"), Bound("revision") } };
            yield return new ActionDefinition { Id = "npc_life.personal_leave", Label = "Take personal leave", Family = "personal",
                Adapter = "npc_life:leave", Source = "ReignNpcLivesBehavior", Description = "Wait for the agreed period, then take personal leave and return after the agreed time away.",
                Parameters = new List<ActionParameter> { Bound("matterId"), Bound("revision"),
                    Number("waitDays", "Wait before departure (days)", 0, 31.5), Number("awayDays", "Time away after departure (days)", .25, 31.5) } };
            yield return new ActionDefinition { Id = "npc_life.summon_meeting", Label = "Summon a subordinate for a meeting", Family = "personal",
                Adapter = "npc_life:meeting", Source = "ReignNpcLivesBehavior", Description = "A clan leader or ruler quotes a fixed arrival wait. The noble attends alone while their party remains safely camped at its original position; both participants stay for the full agreed duration, then the noble returns to the camp.",
                Parameters = new List<ActionParameter> {
                    new ActionParameter { Key = "targetHeroId", Label = "Person to summon", Kind = "hero", Required = true },
                    new ActionParameter { Key = "destinationId", Label = "Meeting location", Kind = "npcMeetingPlace", Required = true },
                    new ActionParameter { Key = "arrivalDays", Label = "Agreed arrival wait (days)", Kind = "number", Required = true, Automatic = true, Minimum = 0, Maximum = 126 },
                    new ActionParameter { Key = "attendanceMode", Label = "Attendance arrangement", Automatic = true, Choices = new[] { "solo_camp", "legacy_party" } },
                    Number("stayDays", "Both remain after arrival (days)", .25, 31.5),
                    new ActionParameter { Key = "purpose", Label = "Purpose of the meeting", Required = true },
                    new ActionParameter { Key = "releaseArmy", Label = "Release target from army duties when authorized", Kind = "text", Choices = new[] { "no", "yes" }, Required = true }
                } };
            yield return new ActionDefinition { Id = "npc_life.change_meeting", Label = "Change an agreed meeting", Family = "personal",
                Adapter = "npc_life:meeting_revision", Source = "ReignNpcLivesBehavior", Description = "Both sides accept a new fixed destination and full stay from the next verified arrival.",
                Parameters = new List<ActionParameter> {
                    new ActionParameter { Key = "meetingId", Label = "Meeting to change", Kind = "npcMeeting", Required = true }, Bound("meetingRevision"),
                    new ActionParameter { Key = "destinationId", Label = "New meeting location", Kind = "npcMeetingPlace", Required = true },
                    new ActionParameter { Key = "arrivalDays", Label = "New agreed arrival wait (days)", Kind = "number", Required = true, Automatic = true, Minimum = 0, Maximum = 126 },
                    new ActionParameter { Key = "attendanceMode", Label = "Attendance arrangement", Automatic = true, Choices = new[] { "solo_camp", "legacy_party" } },
                    Number("stayDays", "Both remain after arrival (days)", .25, 31.5) } };
            yield return new ActionDefinition { Id = "npc_life.cancel_meeting", Label = "Cancel an agreed meeting", Family = "personal",
                Adapter = "npc_life:meeting_cancellation", Source = "ReignNpcLivesBehavior", Description = "Both sides agree to cancel this meeting and release only its movement commitments.",
                Parameters = new List<ActionParameter> {
                    new ActionParameter { Key = "meetingId", Label = "Meeting to cancel", Kind = "npcMeeting", Required = true }, Bound("meetingRevision") } };
        }
    }
}
