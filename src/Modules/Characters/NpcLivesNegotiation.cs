using System;
using System.Collections.Generic;
using System.Linq;
using Reign.Core.Contracts.Actions;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static bool IsDuplicateNpcStoryDuelProposal(NpcLivesState state, string hero, string player,
            Dictionary<string, object> payload, ActionDecision decision)
        {
            if (decision.Kind != "propose" || decision.Clauses.Count == 0
                || !decision.Clauses.Any(t => t.ActionId == "duel_player"
                    && t.ActorId == hero && t.RecipientId == player)) return false;
            string requested = ReadString(payload, "npcStoryId", "");
            string location = ReadFirstString(payload, "locationId", "currentSettlementId", "settlementId");
            return state.Stories.Any(s => s.HeroId == hero && (s.Status == "active" || s.Status == "invited")
                && s.PhaseIndex >= 0 && s.PhaseIndex < s.Definition.Phases.Count
                && (requested == s.Id || requested.Length == 0 && location == s.LocationId)
                && s.Definition.Phases[s.PhaseIndex].Objectives.Any(o => o.Kind == "duel"
                    && !s.Evidence.Any(e => e.ObjectiveId == o.Id)));
        }

        private static void ConstrainNpcStoryDuelProposal(Dictionary<string, object> payload, string hero,
            string player, ActionDecision decision)
        {
            if (decision.Kind != "propose") return;
            string campaign = ReadString(payload, "campaignId", "");
            if (campaign.Length == 0) return;
            using var c = OpenCampaignConnection(campaign);
            var state = ReadNpcLives(c, campaign, ContinuityTimeline(payload));
            if (!IsDuplicateNpcStoryDuelProposal(state, hero, player, payload, decision)) return;
            // Reject the whole proposed bundle; silently retaining only some terms
            // would change the NPC's offer. No consent or story evidence is granted.
            payload["npcStoryActionGuard"] = TestDict("status", "duplicate_native_story_duel", "stateChanged", false);
            throw new InvalidOperationException("This practice duel is already part of the personal story. Begin it at the agreed Story Location; no separate duel offer was published.");
        }

        // One bounded local repair aligns final accepted prose with the saved rules.
        // This preview never changes state. Only the accepted source transaction does.
        private static void ConstrainNpcLeaveReply(Dictionary<string, object> payload, string hero,
            Dictionary<string, object> parsed, ref string reply)
        {
            if (ReadString(ReadDictionary(parsed, "structuredActionDecision"), "kind", "none") != "none")
            { parsed.Remove("npcLeaveAssessment"); return; }
            var assessment = ReadDictionary(parsed, "npcLeaveAssessment");
            string campaign = ReadString(payload, "campaignId", "");
            if (assessment == null || campaign.Length == 0) return;
            using var c = OpenCampaignConnection(campaign);
            var state = ReadNpcLives(c, campaign, ContinuityTimeline(payload));
            var leave = state.Leaves.FirstOrDefault(l => l.HeroId == hero && l.Id == ReadString(assessment, "leaveId", "") && l.Status == "discussing");
            if (leave == null) { parsed.Remove("npcLeaveAssessment"); return; }
            string reason = ReadString(assessment, "reasonKind", ""), quote = ReadString(assessment, "evidenceQuote", "").Trim();
            string input = ReadString(payload, "playerText", "");
            bool valid = leave != null && leave.Revision == ReadInt(assessment, "revision", -1)
                && NpcLeaveNegotiation.Reasons.Contains(reason) && quote.Length >= 8 && input.IndexOf(quote, StringComparison.Ordinal) >= 0
                && !leave.AcceptedArgumentIds.Contains(reason);
            string sentence = valid ? NpcLeaveNegotiation.Response(NpcLeaveNegotiation.Persuaded(leave, reason,
                ReadBool(assessment, "compelling", false), state.CampaignId + "|" + state.TimelineId),
                NpcLivesPolicy.ReaskDelay(ReadNpcLivesOptions()))
                : "My personal request is still unresolved. We can discuss it or review the current terms; nothing has been agreed by changing the subject.";
            // A fixed result is deliberately rendered as a complete reply. Keeping
            // arbitrary adjacent prose could retain a contradictory promise.
            reply = RenderGroundedSceneHeading(payload, hero, parsed, sentence);
            // Everything inferred from the discarded draft must be discarded too.
            // The accepted argument is the only permitted effect of this repair.
            parsed.Clear();
            if (valid) parsed["npcLeaveAssessment"] = assessment;
            parsed["reply"] = reply;
            parsed["announcementReactions"] = new object[0];
            if (!valid) parsed.Remove("npcLeaveAssessment");
            payload["npcLeaveResponseGuard"] = TestDict("status", valid ? "matched" : "clarification", "revision", leave?.Revision ?? -1,
                "reason", reason, "repair", "bounded_local", "stateChanged", false);
        }
    }
}
