using System;
using System.Collections.Generic;
using System.Linq;

namespace Reign.Core.Contracts.Actions
{
    public static class ReignSpouseSeparation
    {
        public const string ActionId = "leave_spouse";
        public const string Source = "explicit_spouse_letter";
        public const int RelationLoss = 50;
        public const string DecisionGuidance = "Leaving your spouse is a major and consequential decision. "
            + "Acceptance means writing to your current spouse that your marriage is over and ending it, with a severe loss of relations with your spouse and their clan. "
            + "Weigh your personality, marriage, attachments, duties and consequences; you may refuse or negotiate. "
            + "This action alone promises no romance, marriage, travel, party membership or future with the player. "
            + "Do not claim the letter was sent or the marriage ended before a successful native receipt.";

        // Only an unsigned draft can acquire a binding. A signed offer never follows a new spouse/clan.
        public static void Bind(ActionAgreement agreement, ActionClause clause, string spouseId, string clanId)
        {
            if (clause.ActionId != ActionId) return;
            if (clause.Arguments.ContainsKey("spouseId") && clause.Arguments.ContainsKey("spouseClanId")) return;
            if (clause.Arguments.ContainsKey("spouseId") || clause.Arguments.ContainsKey("spouseClanId")
                || agreement.PlayerAcceptedHash.Length > 0 || agreement.NpcAcceptedHash.Length > 0)
                throw new ArgumentException("Build a new request bound to the current spouse and clan.");
            clause.Arguments["spouseId"] = spouseId;
            clause.Arguments["spouseClanId"] = clanId;
        }

        public static IEnumerable<string> Validate(ActionAgreement agreement, ActionClause clause)
        {
            if (clause.ActionId != ActionId) yield break;
            if (clause.ActorId != agreement.NpcId || clause.RecipientId != agreement.PlayerId)
                yield return "Only the consenting NPC may end their own marriage.";
            if (!clause.Arguments.TryGetValue("spouseId", out var spouse) || string.IsNullOrWhiteSpace(spouse)
                || spouse == clause.ActorId)
                yield return "The exact current spouse is required.";
            if (!clause.Arguments.ContainsKey("spouseClanId"))
                yield return "The current spouse's clan binding is required.";
            if (clause.Timing != TermTiming.Immediate || clause.DueDay != 0 || clause.DurationDays != 0
                || clause.Installments != 1 || clause.IntervalDays != 0 || clause.ConditionKind.Length > 0)
                yield return "Leaving a spouse is one immediate decision, without future conditions or installments.";
            if (agreement.Clauses.Count(c => c.ActionId == ActionId) > 1)
                yield return "One agreement cannot end the same marriage twice.";
            if (agreement.Clauses.Any(c => c.ActionId == "marriage_alliance"))
                yield return "Ending a marriage and agreeing to a new marriage require separate agreements.";
        }

        public static string Letter(string spouseName) => spouseName + ", our marriage is over. I am ending it by this letter.";

        public static IReadOnlyList<string> AffectedHeroes(string actorId, string spouseId, IEnumerable<string> clanHeroes)
            => new[] { spouseId }.Concat(clanHeroes).Where(id => !string.IsNullOrWhiteSpace(id) && id != actorId)
                .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();

        public static int RelationAfter(int before) => Math.Max(-100, Math.Min(100, before) - RelationLoss);
    }
}
