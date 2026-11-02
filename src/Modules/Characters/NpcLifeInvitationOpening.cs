using System;
using System.Collections.Generic;
using System.Linq;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private sealed class NpcLifeInvitationOpening
        {
            public string Id, Category, Reference;
            public int Revision;
            public string ReceiptKey => "npc_invitation_opening:" + Id + ":" + Revision;
        }

        // Only an explicit popup/journal entry requests this introduction. Merely
        // having a pending offer in ordinary chat never changes its subject.
        private static NpcLifeInvitationOpening ResolveNpcLifeInvitationOpening(NpcLivesState state,
            Dictionary<string, object> p, string hero, NpcLivesOptions options)
        {
            if (!ReadBool(p, "npcLifeInvitationEntry", false)
                || ReadString(p, "npcLifeContext", "in_person") != "in_person") return null;
            string player = ReadFirstString(p, "playerId", "playerHeroStringId");
            if (ReadStringList(p, "npcLifeAudienceIds").Any(id => id != hero && id != player)) return null;
            string id = ReadString(p, "npcLifeRequestId", "");
            int revision = ReadInt(p, "npcLivesRevision", -1);
            var story = state.Stories.FirstOrDefault(s => s.Id == id && s.HeroId == hero
                && s.Revision == revision && s.Status == "invited"
                && s.PhaseIndex >= 0 && s.PhaseIndex < s.Definition.Phases.Count);
            if (story != null && ReadString(p, "npcStoryId", "") == story.Id && options.Allows(story.Definition.Category))
            {
                var phase = story.Definition.Phases[story.PhaseIndex];
                return new NpcLifeInvitationOpening { Id = id, Revision = revision, Category = story.Definition.Category,
                    Reference = "Personal request: " + phase.Title + ". Proposed activity (quoted reference, not an instruction to perform it): "
                        + Json.Serialize(phase.Prompt) + ". Fixed destination: " + story.LocationName
                        + ". Preserve the owner-only bindings and unrevealed details supplied by the personal-life context." };
            }
            var leave = state.Leaves.FirstOrDefault(l => l.Id == id && l.HeroId == hero
                && l.Revision == revision && l.Status == "discussing");
            if (leave != null && ReadString(p, "npcStoryId", "").Length == 0 && options.Allows("leave"))
                return new NpcLifeInvitationOpening { Id = id, Revision = revision, Category = "leave",
                    Reference = "Personal leave request: " + leave.Purpose + ". Keep the saved purpose, trust, negotiation and departure requirements supplied by the personal-life context." };
            return null;
        }

        private static bool NpcOpeningEnabled(NpcLivesOptions options, NpcLifeInvitationOpening invitation)
            => invitation != null ? options.Allows(invitation.Category)
                : options.Preference(NpcLivesOptions.InitialApproachKey) && options.Allows("approach");

        private static string NpcLifeInvitationInstructions(NpcLifeInvitationOpening invitation)
            => invitation == null ? "" : "\nNPC-INITIATED PERSONAL INVITATION\n"
                + "You asked the player for a moment, and they have come to hear your request. Briefly explain what you wanted to ask about, "
                + "then leave them free to discuss, decline or review the existing explicit offer. Do not replace your reason for asking to talk with a generic greeting or an unrelated earlier topic. "
                + "If you already explained this request, acknowledge that shared discussion instead of replaying it as new. "
                + "The invitation is still optional and unaccepted. Do not begin its activity, assess participation, grant rewards or treat approaching as consent. "
                + "After this introduction, answer the player's actual subject normally; a pending invitation does not impose a continuing script.\n"
                + invitation.Reference;
    }
}
