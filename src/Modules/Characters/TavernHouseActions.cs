using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Reign.Core.Contracts.Actions;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
        private static readonly AsyncLocal<TavernHouseActionContext> TavernActionScope = new();
        internal static bool TavernActionScopeEnabled => TavernActionScope.Value != null;
        private sealed class TavernActionScopeLease : IDisposable
        {
            private readonly TavernHouseActionContext previous;
            public TavernActionScopeLease(TavernHouseActionContext scope) { previous = TavernActionScope.Value; TavernActionScope.Value = scope; }
            public void Dispose() { TavernActionScope.Value = previous; }
        }
        private static TavernHouseActionContext TavernVisitContext(Dictionary<string, object> state)
            => new TavernHouseActionContext { CampaignId = ReadString(state, "campaignId", ""), TimelineId = ReadString(state, "timelineId", ""),
                TownId = ReadString(state, "townId", ""), SessionId = ReadString(state, "conversationSessionId", ""),
                PlayerId = ReadString(state, "playerHeroStringId", ""), MadamId = ReadString(state, "madamId", ""),
                Roster = ReadDictionaryList(state, "roster").Select(p => new TavernHouseActionPerson { HeroId = CampaignFileService.CharacterIdFrom(p),
                    Name = ReadString(p, "name", ""), Adult = ReadDouble(p, "age", 0) >= 18 && !ReadBool(p, "isChild", false),
                    Available = TavernHouseAdultAvailable(p) }).ToList() };

        internal static void ValidateTavernActionDecision(ActionAgreement offered, ActionDecision decision, string player, string npc, string conversation, double day)
        {
            if (TavernHouseVisitAction.Contains(offered) && TavernActionScope.Value == null)
                throw new InvalidOperationException("A Tavern House proposal cannot be carried into another conversation.");
            var candidate = decision.Kind == "propose" || decision.Kind == "counter"
                ? ActionNegotiation.CreateProposal(offered, decision, player, npc, conversation, day) : offered;
            if (!TavernHouseVisitAction.Contains(candidate)) return;
            if (TavernActionScope.Value == null) throw new InvalidOperationException("Visit payment is available only in the active Tavern House conversation.");
            var errors = TavernHouseVisitAction.Validate(candidate, TavernActionScope.Value);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        }

        internal static string TavernVisitActionPrompt()
        {
            var scope = TavernActionScope.Value;
            if (scope == null) return "";
            return "TAVERN HOUSE BOOKING: The dedicated " + TavernHouseVisitAction.Id + " action already includes the complete visit payment. Never add give.gold for that booking. "
                + "Negotiate naturally. For a definite offer use propose/counter with the player as actor and the madam as recipient, exact participant1/price1 through participant4/price4 pairs and totalGold. "
                + "Use one to four distinct available adults, including yourself only if the player specifically asked for you and you choose. Use exact names in speech and supplied IDs in terms. Prices are nonnegative whole denars; explicitly free offers use zero. "
                + "Supply automatic campaignId, timelineId, townId, sessionId and rosterHash exactly from the facts below. This is one immediate standalone action, without extra clauses, conditions, scheduling or installments. "
                + "For an attached proposal whose player and NPC hashes already match its termsHash, both have accepted that exact offer: return none and acknowledge the agreed booking awaiting native payment/confirmation. "
                + "Otherwise accept/reject/counter normally. Speech alone never charges gold. Do not claim payment complete, participants arrived, or the paid visit started before the matching confirmed native visit receipt. "
                + "Booking does not compel later intimate acts, recruit anyone or make absent staff witnesses.\n"
                + "CURRENT TAVERN FACTS: " + Json.Serialize(TestDict("campaignId", scope.CampaignId, "timelineId", scope.TimelineId,
                    "townId", scope.TownId, "sessionId", scope.SessionId, "rosterHash", scope.RosterHash, "playerId", scope.PlayerId,
                    "madamId", scope.MadamId, "roster", scope.Roster)) + "\n"
                + "TAVERN-ONLY REGISTERED ACTION: " + DialogueService.StructuredActionCatalogPrompt(true) + "\n";
        }
        private static Dictionary<string, object> TavernVisitQuoteData(TavernHouseVisitQuote quote)
            => TestDict("agreementId", quote.AgreementId, "quoteRevision", quote.QuoteRevision, "townId", quote.TownId,
                "campaignId", quote.CampaignId, "timelineId", quote.TimelineId, "playerHeroId", quote.PlayerHeroId,
                "totalGold", quote.TotalGold, "charges", quote.Charges.Select(c => TestDict("heroId", c.HeroId, "displayName", c.DisplayName, "gold", c.Gold)).ToList());

        // Runs under the exact session lock after the ordinary response has passed
        // action validation. No extra model call and no native effects occur here.
        private static void PrepareTavernVisitAction(Dictionary<string, object> state, Dictionary<string, object> chat, Dictionary<string, object> response)
        {
            if (!DialogueService.UsesStructuredActions(chat)) return;
            var envelope = ReadDictionary(Json.Deserialize<Dictionary<string, object>>(Json.Serialize(response)), "structuredActionDecision");
            if (!ReadBool(envelope, "valid", false)) return;
            var native = ReadDictionary(chat, "structuredActionContext");
            var offeredData = ReadDictionary(native, "proposal");
            var offered = offeredData == null ? null : Json.Deserialize<ActionAgreement>(Json.Serialize(offeredData));
            var decision = Json.Deserialize<ActionDecision>(Json.Serialize(ReadDictionary(envelope, "decision") ?? TestDict("kind", "none")));
            var scope = TavernVisitContext(state);
            var decisionErrors = ActionNegotiation.ValidateDecision(offered, decision, scope.PlayerId, scope.MadamId,
                ReadString(native, "conversationId", ""), ReadDouble(chat, "worldDay", 0));
            if (decisionErrors.Count > 0) throw new InvalidOperationException(string.Join(" ", decisionErrors));
            if (decision.Kind == "reject") state.Remove("structuredVisitOffer");
            if (decision.Kind == "propose" || decision.Kind == "counter")
            {
                var proposal = ActionNegotiation.CreateProposal(offered, decision, scope.PlayerId, scope.MadamId,
                    ReadString(native, "conversationId", ""), ReadDouble(chat, "worldDay", 0));
                if (TavernHouseVisitAction.Contains(proposal))
                {
                    var errors = TavernHouseVisitAction.Validate(proposal, scope);
                    if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
                    state["structuredVisitOffer"] = TavernHouseVisitAction.Fingerprint(proposal);
                }
                return;
            }
            if (!TavernHouseVisitAction.Contains(offered) || decision.Kind == "reject") return;
            var problems = TavernHouseVisitAction.Validate(offered, scope);
            if (problems.Count > 0) throw new InvalidOperationException(string.Join(" ", problems));
            var accepted = Json.Deserialize<ActionAgreement>(Json.Serialize(offered));
            var preparations = ReadDictionaryList(state, "preparedVisits");
            var prepared = preparations.SingleOrDefault(p => ReadString(p, "agreementId", "") == accepted.Id);
            string hash = accepted.TermsHash();
            if (prepared != null && ReadString(prepared, "termsHash", "") != hash) throw new InvalidOperationException("This payment operation already belongs to different terms.");
            if (prepared == null && preparations.Count > 0)
            {
                response["preparedVisitError"] = "A visit is already prepared in this negotiation. Retry that exact booking, or close this unpaid negotiation before changing it.";
                return;
            }
            if (offered.HasBothConsents)
            {
                if ((decision.Kind != "none" && decision.Kind != "accept") || prepared == null && ReadString(state, "structuredVisitOffer", "") != TavernHouseVisitAction.Fingerprint(offered))
                { response["preparedVisitError"] = "The madam's saved offer has changed. Review the current names and prices before accepting."; return; }
            }
            else if (decision.Kind != "accept" || !AgreementRules.Accept(accepted, accepted.NpcId, decision.TermsHash)) return;
            if (!accepted.HasBothConsents) return;
            if (prepared == null)
            {
                state.Remove("structuredVisitOffer"); // Consumed once; exact operation replay uses the preparation above.
                var quote = TavernHouseVisitAction.Quote(accepted, scope);
                prepared = TestDict("schema", "reign-tavern-prepared-visit-v1", "agreementId", accepted.Id, "termsHash", hash,
                    "preparationId", TavernHouseVisitAction.Hash(new[] { accepted.Id, hash, scope.CampaignId, scope.TimelineId, scope.SessionId }),
                    "fingerprint", TavernHouseVisitAction.Fingerprint(accepted), "quote", TavernVisitQuoteData(quote), "madamId", scope.MadamId);
                preparations.Add(prepared); state["preparedVisits"] = preparations;
            }
            response["preparedVisit"] = prepared;
        }

        private static Dictionary<string, object> ValidateTavernPreparedReceipt(Dictionary<string, object> state, Dictionary<string, object> incoming)
        {
            string hash = ReadString(incoming, "actionTermsHash", "");
            if (hash.Length == 0)
            {
                if (ReadInt(state, "visitActionProtocolVersion", 0) != 0 || ReadDictionaryList(state, "preparedVisits").Count > 0
                    || ReadString(incoming, "preparationId", "").Length > 0)
                    throw new InvalidOperationException("This structured visit requires its exact saved payment preparation.");
                return null; // Only sessions predating this protocol can recover legacy paid receipts.
            }
            var prepared = ReadDictionaryList(state, "preparedVisits").SingleOrDefault(p => ReadString(p, "agreementId", "") == ReadString(incoming, "agreementId", ""));
            if (prepared == null || ReadString(prepared, "termsHash", "") != hash
                || ReadString(prepared, "preparationId", "") != ReadString(incoming, "preparationId", "")
                || ReadString(prepared, "madamId", "") != ReadString(incoming, "payeeHeroId", "")
                || ReadString(incoming, "paymentState", "") != "paid")
                throw new InvalidOperationException("The saved native payment does not match the prepared visit. Payment recovery is required.");
            return ReadDictionary(prepared, "quote");
        }
    }
}
