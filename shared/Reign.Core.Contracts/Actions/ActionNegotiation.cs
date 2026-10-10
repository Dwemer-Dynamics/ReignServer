using System;
using System.Collections.Generic;
using System.Linq;

namespace Reign.Core.Contracts.Actions
{
    // Prompt discovery is a sample, never an inventory or an execution authority.
    // Explicit terms are pinned outside the sample limit, including exact variants.
    public sealed class ActionPromptAsset
    {
        public ActionClause Clause { get; set; } = new ActionClause();
        public string Name { get; set; } = "";
        public int Available { get; set; }
    }

    public static class ActionPromptScope
    {
        public const int AlternativesPerActorAndKind = 4;
        public const string Policy = "eligible-assets-v1";

        public static bool CanGiveSettlement(bool clanLeader, bool ownedByClan, bool townOrCastle)
            => clanLeader && ownedByClan && townOrCastle;

        private static string Arg(ActionClause clause, string key)
            => clause.Arguments.TryGetValue(key, out string value) ? value : "";

        public static bool SameAsset(ActionClause left, ActionClause right)
            => left.ActionId == right.ActionId && left.ActorId == right.ActorId
                && new[] { "assetId", "variantId", "equipmentSet", "equipmentSlot" }
                    .All(key => Arg(left, key) == Arg(right, key));

        public static IReadOnlyList<ActionPromptAsset> Select(IEnumerable<ActionPromptAsset> candidates,
            ActionAgreement? offered, string conversationText = "")
        {
            var terms = offered?.Clauses ?? new List<ActionClause>();
            var selected = new List<ActionPromptAsset>();
            foreach (var group in candidates.GroupBy(x => new { x.Clause.ActorId, x.Clause.ActionId })
                .OrderBy(g => g.Key.ActorId, StringComparer.Ordinal).ThenBy(g => g.Key.ActionId, StringComparer.Ordinal))
            {
                var rows = group.OrderBy(x => x.Name, StringComparer.Ordinal)
                    .ThenBy(x => Arg(x.Clause, "assetId"), StringComparer.Ordinal)
                    .ThenBy(x => Arg(x.Clause, "variantId"), StringComparer.Ordinal)
                    .ThenBy(x => Arg(x.Clause, "equipmentSet"), StringComparer.Ordinal)
                    .ThenBy(x => Arg(x.Clause, "equipmentSlot"), StringComparer.Ordinal).ToList();
                var pinned = rows.Where(x => terms.Any(t => SameAsset(t, x.Clause))).ToList();
                selected.AddRange(pinned);
                // Speech only ranks discovery candidates. It cannot create consent,
                // change terms, or cause an action to execute.
                selected.AddRange(rows.Except(pinned)
                    .OrderByDescending(x => Mentioned(x, conversationText))
                    .Take(AlternativesPerActorAndKind));
            }
            return selected;
        }

        private static bool Mentioned(ActionPromptAsset asset, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string id = Arg(asset.Clause, "assetId");
            string name = asset.Name.Split('(')[0].Trim();
            return (id.Length > 2 && text.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0)
                || (name.Length > 2 && text.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    public sealed class ActionArgument
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    // Arguments are pairs on the model wire so strict schemas can reject unknown
    // structure without permitting arbitrary JSON inside an executable parameter.
    public sealed class ActionProposedTerm
    {
        public string ActionId { get; set; } = "";
        public string ActorId { get; set; } = "";
        public string RecipientId { get; set; } = "";
        public List<ActionArgument> Arguments { get; set; } = new List<ActionArgument>();
        public double DueDay { get; set; }
        public double DurationDays { get; set; }
        public int Installments { get; set; } = 1;
        public double IntervalDays { get; set; }
        public string ConditionKind { get; set; } = "";
        public string ConditionTargetId { get; set; } = "";
    }

    public sealed class ActionDecision
    {
        public string Kind { get; set; } = "none";
        public string ProposalId { get; set; } = "";
        public int Revision { get; set; }
        public string TermsHash { get; set; } = "";
        public string Reason { get; set; } = "";
        public List<ActionProposedTerm> Clauses { get; set; } = new List<ActionProposedTerm>();
    }

    public static class ActionNegotiation
    {
        public const int ProtocolVersion = 1;

        public static List<string> ValidateDecision(ActionAgreement? offered, ActionDecision decision,
            string playerId, string npcId, string conversationId, double now)
        {
            var errors = new List<string>();
            if (decision == null || decision.Clauses == null || decision.Reason == null || decision.Kind == null
                || decision.ProposalId == null || decision.TermsHash == null)
                return new List<string> { "The action decision is malformed." };
            if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(npcId) || playerId == npcId || string.IsNullOrWhiteSpace(conversationId))
                errors.Add("Two participants and a conversation identity are required.");
            if (decision.Reason.Length > 2000) errors.Add("The decision reason is too long.");
            if (!new[] { "none", "accept", "reject", "counter", "propose" }.Contains(decision.Kind)) errors.Add("Unknown action decision.");
            if (offered != null)
            {
                errors.AddRange(AgreementRules.Validate(offered, ReignActionCatalog.Find, now));
                if (errors.Count > 0) return errors.Distinct().ToList();
                if (offered.Origin != ActionOrigin.Conversation || offered.PlayerId != playerId
                    || offered.NpcId != npcId || offered.ConversationId != conversationId
                    || offered.State != AgreementState.Offered || offered.PlayerAcceptedHash != offered.TermsHash())
                    errors.Add("The offered revision is not authorized by this player in this conversation.");
            }
            if (decision.Kind == "accept" || decision.Kind == "reject" || decision.Kind == "counter")
            {
                if (offered == null || decision.ProposalId != offered.Id || decision.Revision != offered.Revision
                    || decision.TermsHash != offered.TermsHash()) errors.Add("The decision does not identify the exact offered revision.");
            }
            if (decision.Kind == "propose" && offered != null) errors.Add("Respond to the offered revision before proposing another agreement.");
            if (decision.Kind == "propose" && (decision.ProposalId.Length > 0 || decision.TermsHash.Length > 0 || decision.Revision != 0))
                errors.Add("A new NPC proposal cannot claim an existing identity or consent.");
            if (decision.Kind == "none" || decision.Kind == "accept" || decision.Kind == "reject")
            {
                if (decision.Clauses.Count != 0) errors.Add("An unchanged decision cannot smuggle replacement terms.");
            }
            else if (decision.Kind == "propose" || decision.Kind == "counter")
            {
                try { errors.AddRange(AgreementRules.Validate(CreateProposal(offered, decision, playerId, npcId, conversationId, now), ReignActionCatalog.Find, now)); }
                catch (ArgumentException ex) { errors.Add(ex.Message); }
            }
            return errors.Distinct().ToList();
        }

        public static ActionAgreement CreateProposal(ActionAgreement? offered, ActionDecision decision,
            string playerId, string npcId, string conversationId, double now)
        {
            if (decision.Kind != "counter" && decision.Kind != "propose") throw new ArgumentException("No new proposal was supplied.");
            if (decision.Clauses == null || decision.Clauses.Count == 0 || decision.Clauses.Count > 64)
                throw new ArgumentException("A proposal requires one to 64 complete terms.");
            var result = new ActionAgreement
            {
                Id = offered?.Id ?? Guid.NewGuid().ToString("N"), Revision = offered == null ? 1 : checked(offered.Revision + 1),
                PlayerId = playerId, NpcId = npcId, ConversationId = conversationId, ProposerId = npcId,
                Origin = ActionOrigin.Conversation, State = AgreementState.Counteroffered, CreatedDay = now,
                // Consent is deliberately absent until native facts, identity, wording and
                // game-derived quote bounds have been checked before player presentation.
                PlayerAcceptedHash = "", NpcAcceptedHash = ""
            };
            foreach (var term in decision.Clauses)
            {
                if (term == null || term.ActionId == null || term.ActorId == null || term.RecipientId == null
                    || term.ConditionKind == null || term.ConditionTargetId == null || term.Arguments == null || term.Arguments.Count > 64
                    || term.Arguments.Any(a => a == null || string.IsNullOrEmpty(a.Key) || a.Value == null))
                    throw new ArgumentException("Malformed action arguments.");
                var arguments = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var argument in term.Arguments)
                    if (arguments.ContainsKey(argument.Key)) throw new ArgumentException("Duplicate action argument: " + argument.Key);
                    else arguments.Add(argument.Key, argument.Value);
                result.Clauses.Add(new ActionClause
                {
                    ActionId = term.ActionId, ActorId = term.ActorId, RecipientId = term.RecipientId, Arguments = arguments,
                    DueDay = term.DueDay, DurationDays = term.DurationDays, Installments = term.Installments,
                    IntervalDays = term.IntervalDays, ConditionKind = term.ConditionKind, ConditionTargetId = term.ConditionTargetId,
                    Timing = term.Installments > 1 ? TermTiming.Installment : !string.IsNullOrEmpty(term.ConditionKind) ? TermTiming.OnCondition : TermTiming.Immediate
                });
            }
            return result;
        }
    }
}
