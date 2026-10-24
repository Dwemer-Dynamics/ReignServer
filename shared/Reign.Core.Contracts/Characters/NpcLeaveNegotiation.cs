using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBeta.Shared.Characters
{
    public static class NpcLeaveNegotiation
    {
        public static readonly string[] Reasons = { "medical_need", "imminent_danger", "prior_commitment", "alternative_arrangement", "emotional_appeal", "authority_demand" };
        public static bool Persuaded(NpcPersonalLeave leave, string reason, bool compelling, string seed)
        {
            if (!Reasons.Contains(reason)) return false;
            double trait = reason == "medical_need" || reason == "emotional_appeal" ? leave.Compassion
                : reason == "prior_commitment" || reason == "authority_demand" ? leave.Honor : 50;
            double chance = compelling ? .48 + leave.Trust / 300d + (trait - 50) / 250d
                + leave.DutyPressure * .08 - leave.Urgency * .09 : .02;
            chance = Math.Max(.02, Math.Min(.95, chance));
            return NpcLivesEngine.Draw(seed + "|" + leave.Id + "|" + reason) < chance;
        }
        public static string Response(bool persuaded, double reaskDays) => persuaded
            ? "I will stay for now. This still matters to me, and I will ask again after " + reaskDays.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " days."
            : "I understand your reason, but I still intend to make this visit. We can agree on a specific wait before I leave; my purpose has not changed.";
        public static bool LeavesIndependently(NpcPersonalLeave leave, string seed) =>
            leave.Trust < 20 && NpcLivesEngine.Draw(seed + "|" + leave.Id + "|independent") <
                Math.Max(0, Math.Min(.6, .12 + leave.Urgency * .08 - leave.Honor / 500d - leave.Trust / 500d));
        public static string PrivacyRule(int affinity) => affinity <= 20
            ? "Keep the purpose and destination private unless you choose a small disclosure; say personal business. Do not invent a different purpose."
            : affinity < 40 ? "You may give limited context, but keep intimate family details and the exact destination private."
            : affinity < 60 ? "You may explain the personal purpose and destination. This is your visit, not an invitation for the player to accompany you."
            : "You may explain the purpose and invite company, but accompanying you requires a separate authored story or explicit action; it does not cancel your own visit.";
    }
}
