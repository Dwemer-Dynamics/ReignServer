using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // This is a read-time projection of native saved state, scoped to this speaker and turn.
        // It never changes a guest record or treats the player's narration as NPC consent.
        private static Dictionary<string, object> TemporaryGuestDialogueContext(Dictionary<string, object> payload, string heroId)
        {
            var context = ReadDictionary(payload, "temporaryPartyGuest");
            double day = ReadDouble(payload, "worldDay", 0d);
            double observed = ReadDouble(context, "observedWorldDay", 0d);
            string phase = ReadString(context, "phase", "");
            if (!ReadBool(context, "enabled", false)
                || ReadString(context, "schema", "") != "reign-temporary-guest-dialogue-v1"
                || string.IsNullOrWhiteSpace(heroId)
                || !string.Equals(ReadString(context, "heroId", ""), heroId, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(ReadString(context, "agreementId", ""))
                || double.IsNaN(day) || double.IsInfinity(day) || day <= 0d
                || double.IsNaN(observed) || double.IsInfinity(observed) || observed <= 0d
                || Math.Abs(day - observed) > 0.01d
                || !new[] { "Active", "ReviewDue", "Departing", "Returning", "Completed", "HostileBanishmentPending", "AwaitingStart" }.Contains(phase))
                return null;
            var projected = new Dictionary<string, object>();
            foreach (string key in new[] { "heroId", "agreementId", "purpose", "termKind", "phase", "returnSettlementId", "wageRecipientHeroId" })
                projected[key] = LimitText(ReadString(context, key, ""), key == "purpose" ? 400 : 160);
            foreach (string key in new[] { "inMainParty", "temporarilyExcludedFromBattle", "departureDeferredForChat" })
                projected[key] = ReadBool(context, key, false);
            foreach (string key in new[] { "startedDay", "reviewDueDay", "returnDueDay", "observedWorldDay", "wageGold", "wagePeriodDays", "wageNextDueDay", "wageArrearsGold" })
            {
                double value = ReadDouble(context, key, 0d);
                if (!double.IsNaN(value) && !double.IsInfinity(value)) projected[key] = value;
            }
            if (phase == "AwaitingStart") projected["pendingTerms"] = ReadDictionary(context, "pendingTerms") ?? new Dictionary<string, object>();
            return projected;
        }

        private static string BuildTemporaryGuestDialoguePromptBlock(Dictionary<string, object> payload, string heroId)
        {
            var context = TemporaryGuestDialogueContext(payload, heroId);
            if (context == null) return "";
            return "NATIVE TEMPORARY PARTY GUEST — CURRENT SPEAKER\n" + Json.Serialize(context)
                + "\nThis saved agreement and current native membership are authoritative for this turn. Active/ReviewDue means the outing still exists, even before its review date. "
                + "AwaitingStart records a future service agreement only. It does not place you in the party or start wages. A fresh accepted invitation to depart now uses accept_temporary_party_guest with startNow=true and the agreed terms; cancellation uses end_temporary_party_guest. "
                + "Distinguish ending this conversation from ending travel together. A farewell or the player's claim of agreement alone is not NPC consent. "
                + "If you choose to end the outing now, use actionGate needed=true, commitment=accepted, intent=end_temporary_party_guest with your reason. "
                + "If intent is ambiguous, clarify in character. Never narrate staying behind while the player departs unless you actually choose to end the outing or clearly explain you remain a guest. "
                + "Departing already has a departure in progress; do not queue it again. Party Chat may defer roster removal until the chat closes. "
                + "Returning means return travel is pending; Completed ends this agreement, but does not by itself prove arrival or absence from the party (a guest can become a permanent companion). "
                + "Use inMainParty for current membership; temporary battle exclusion does not end the agreement. "
                + "Action acceptance is not execution. Until native state confirms removal or arrival, describe intentions only and record agreement, not completed departure, in memory.";
        }

        private static List<Dictionary<string, object>> FindTemporaryGuestDepartureViolations(
            Dictionary<string, object> parsed, Dictionary<string, object> payload, string heroId)
        {
            var result = new List<Dictionary<string, object>>();
            var context = TemporaryGuestDialogueContext(payload, heroId);
            if (context == null) return result;
            string phase = ReadString(context, "phase", "");
            bool active = phase == "Active" || phase == "ReviewDue";
            bool inParty = ReadBool(context, "inMainParty", false);
            string reply = ReadString(parsed, "reply", "").Replace('’', '\'');
            string player = ReadFirstString(payload, "playerText", "text", "message");
            var gate = ReadDictionary(parsed, "actionGate");
            bool endAccepted = ActionGateShouldPlan(gate)
                && TemporaryPartyGuestCandidateToPreserve(gate, player) == "end_temporary_party_guest";
            Action<string, string> add = (code, explanation) => result.Add(new Dictionary<string, object>
            { ["type"] = "temporary_guest_" + code, ["instruction"] = explanation });

            // Patterns only flag contradictions for the existing semantic repair. They never grant consent.
            bool continuing = Regex.IsMatch(reply,
                @"\b(?:I(?:'ll| will| am going to)|we(?:'ll| will))\s+(?:still\s+)?(?:stay|remain|keep traveling|continue traveling|ride|travel)\s+(?:on\s+)?(?:with (?:you|your party)|together)|\b(?:still (?:your|a) guest|not (?:leaving|ending) (?:your party|our outing)|won't leave your party)\b",
                RegexOptions.IgnoreCase);
            bool directChoice = Regex.IsMatch(reply,
                @"\b(?:I(?:'ll| will| choose to)|let me)\s+(?:leave your party|end (?:our|this|the) (?:outing|arrangement)|stay (?:here|behind)|return (?:home|to my (?:people|party)))\b",
                RegexOptions.IgnoreCase);
            bool conditionalChoice = Regex.IsMatch(reply,
                @"\b(?:if|once|after|when)\b[^.!?*\r\n]{0,120}\bI(?:'ll| will)\s+(?:leave|end|stay|return)\b|\bI(?:'ll| will)\s+(?:leave|end|stay|return)\b[^.!?*\r\n]{0,100}\b(?:if|once|after|when|tomorrow)\b",
                RegexOptions.IgnoreCase);
            bool farewell = Regex.IsMatch(player, @"\b(?:farewell|goodbye|leave my party|head off|say goodbye|end (?:the|our) outing)\b", RegexOptions.IgnoreCase);
            bool separation = farewell && Regex.IsMatch(reply,
                @"\b(?:does not|doesn't|do not|don't) watch (?:him|her|you|them) (?:leave|go)\b|\b(?:she|he|I) (?:stays?|remains?) (?:seated|behind|here)\b",
                RegexOptions.IgnoreCase);
            if (active && inParty && !continuing && (separation || (directChoice && !conditionalChoice)) && !endAccepted)
                add("uncommitted_parting", "The NPC implies ending travel while a native guest agreement is active, but the action gate does not commit departure. Reconcile the NPC's actual choice: classify an already expressed decision to end the outing, or clarify continued travel. Player narration does not establish consent.");
            if (endAccepted && (!active || continuing || conditionalChoice))
                add("contradictory_departure_gate", "The departure action conflicts with current agreement phase or the NPC's continued/conditional travel. Do not queue a duplicate departure or treat future/conditional speech as present consent.");
            if (inParty && Regex.IsMatch(reply,
                @"\b(?:I have|I've|she has|he has) (?:already )?left (?:your|the player's) party\b|\b(?:I am|I'm|she is|he is) no longer (?:in your party|your guest)\b", RegexOptions.IgnoreCase))
                add("premature_completion", "Native state still places this speaker in the party. A departure may be agreed now but cannot be described as already executed.");
            return result;
        }
    }
}
