using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string ConversationCorrectionContract =
            "CORRECTIONS AND DISPUTED RELEVANCE: A stored agenda is this NPC's fallible intention, not an order the player owes. " +
            "A player correction, refusal, deferral or objection to relevance must be considered before repeating a demand. " +
            "Separate the underlying concern from the challenged investigative step. Do not call a correction evasion, an unpaid toll or an improved confession. " +
            "The player need not prove an unrelated itinerary to let you investigate your own courier. Do not invent a causal link. " +
            "You may disagree, retain uncertainty or maintain a real boundary. If you repeat a challenged request, explain its independently supported connection visibly, " +
            "and return correctionEvidence:[{turnId,quote,replyQuote}]. Quote an exact earlier player contribution or another witnessed speaker, not your own demand; " +
            "replyQuote must exactly quote the challenged request together with its visible causal explanation. Source quotations prove what was said, not its truth. " +
            "Correct dependent currentPlan, comprehension, beliefs and continuity writes too. Use existing concern IDs and revisions; defer the disputed step without falsely resolving the underlying concern. " +
            "When a contestedCurrentPlan is supplied, explicitly replace stateUpdates.currentPlan with your reconciled plan, or an empty string if no immediate plan remains. " +
            "Do not create a new unanswered question just to resurrect the challenged demand. [] is normal for correctionEvidence.";

        private static string CorrectionSpeech(string text) => ConversationSpeech(text).Replace('\u2019', '\'');
        private static bool DisputesConversationRelevance(string text) => Regex.IsMatch(CorrectionSpeech(text),
            @"\b(?:nothing to do with|(?:isn'?t|is not) relevant|irrelevant|unrelated|(?:doesn'?t|does not) (?:really )?(?:matter|relate)|different (?:directions|routes)|(?:stop|quit) asking|(?:discuss|deal with|answer) (?:that|this|it) later)\b", RegexOptions.IgnoreCase);

        private static bool IsConversationCorrection(string text) => DisputesConversationRelevance(text)
            || Regex.IsMatch(CorrectionSpeech(text), @"\b(?:wasn'?t implying|was not implying|(?:that is|that's|thats) not what|already (?:said|told|answered)|because of what you said|(?:you|I) misunderstood|I (?:didn'?t|did not) (?:say|mean|imply))\b", RegexOptions.IgnoreCase);

        private static HashSet<string> CorrectionSubjectTerms(string text)
        {
            var terms = ExpressionTerms(CorrectionSpeech(text));
            if (terms.Remove("inns")) terms.Add("inn");
            terms.ExceptWith(new[] { "matter", "relevant", "irrelevant", "unrelated", "different", "direction", "reason", "nothing", "really", "actually", "clearly", "question", "answer", "asking", "asked", "later", "stop", "quit" });
            return terms;
        }

        private static string ActiveConversationCorrection(string playerText, List<Dictionary<string, object>> history)
        {
            if (IsConversationCorrection(playerText)) return playerText;
            int players = 0;
            foreach (var row in (history ?? new()).AsEnumerable().Reverse().Take(8))
            {
                string text = ReadString(row, "text", "");
                if (ReadString(row, "role", "") == "npc"
                    && Regex.IsMatch(CorrectionSpeech(text), @"\b(?:you are right|you're right|unrelated|irrelevant|set aside|defer(?:red)?|will not ask|won't ask)\b", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(CorrectionSpeech(text), @"\b(?:but|however|yet|still)\b", RegexOptions.IgnoreCase)) return "";
                if (ReadString(row, "role", "") != "player" || text == playerText) continue;
                if (++players > 1) break;
                if (IsConversationCorrection(text)) return text;
            }
            return "";
        }

        private static HashSet<string> CorrectionScope(string correction, List<Dictionary<string, object>> history, string contested)
        {
            var subjects = CorrectionSubjectTerms(correction);
            subjects.UnionWith(CorrectionSubjectTerms(contested));
            // A deictic "that is irrelevant" refers to the immediately preceding
            // demand, not every old question or memory in the campaign.
            if (subjects.Count < 3)
            {
                var prior = (history ?? new()).FindLast(row => ReadString(row, "role", "") == "npc"
                    && Regex.IsMatch(CorrectionSpeech(ReadString(row, "text", "")), @"\b(?:name|tell me|give me|ask|demand|need|answer)\b", RegexOptions.IgnoreCase));
                subjects.UnionWith(CorrectionSubjectTerms(ReadString(prior, "text", "")));
            }
            return subjects;
        }

        // Projection only. Testimony such as "I wasn't at the inn" never erases
        // a plan. A challenged plan must be explicitly reconciled on acceptance.
        private static Dictionary<string, object> ReconcileConversationPlanForPrompt(Dictionary<string, object> state,
            string playerText, Dictionary<string, object> payload)
        {
            var result = CloneDictionary(state ?? TestDict());
            string correction = FirstNonEmpty(ReadString(payload, "currentConversationCorrection", ""), IsConversationCorrection(playerText) ? playerText : "");
            string plan = ReadString(result, "currentPlan", "");
            if (correction.Length > 0 && plan.Length > 0
                && (CorrectionSubjectTerms(plan).Intersect(CorrectionSubjectTerms(correction)).Any() || CorrectionSubjectTerms(correction).Count < 2)
                && Regex.IsMatch(plan, @"\b(?:demand|ask (?:him|her|them|the player)|name|account|inns?|dates?)\b", RegexOptions.IgnoreCase))
            {
                payload["contestedCurrentPlan"] = plan;
                result["currentPlan"] = "";
                result["contestedPriorPlan"] = "Fallible intention challenged by this contribution; reconcile before repeating: " + plan;
            }
            return result;
        }

        private static List<Dictionary<string, object>> FindConversationCorrectionViolations(Dictionary<string, object> parsed,
            string playerText, List<Dictionary<string, object>> history, string heroId, Dictionary<string, object> payload = null)
        {
            var issues = new List<Dictionary<string, object>>();
            string correction = ActiveConversationCorrection(playerText, history);
            if (correction.Length == 0) return issues;
            string contested = ReadString(payload, "contestedCurrentPlan", "");
            var subjects = CorrectionScope(correction, history, contested);
            string reply = ReadFirstString(parsed, "reply", "response", "text", "content");
            IEnumerable<string> Clauses(string text) => Regex.Split(CorrectionSpeech(text),
                @"[.!?;\r\n]+|\s+[—–]\s+|,\s*(?:just|but|yet|however)\s+|\s+(?:but|yet|however)\s+", RegexOptions.IgnoreCase)
                .Where(s => s.Trim().Length > 0);
            bool Demand(string text)
            {
                if (subjects.Intersect(CorrectionSubjectTerms(text)).Count() < 2) return false;
                // Keep genuine NPC refusals/conditions and uncertain beliefs.
                if (Regex.IsMatch(text, @"^\s*(?:I|we)\s+(?:will not|won't|cannot|can't|refuse)\b", RegexOptions.IgnoreCase)) return false;
                var demand = Regex.Match(text, @"\b(?:name (?:the|your|that)|tell me|give me|I (?:still )?need|we (?:still )?need|(?:you|he|she) must|demand|send for|waits? (?:on|until))\b", RegexOptions.IgnoreCase);
                if (!demand.Success) return false;
                string prefix = text.Substring(Math.Max(0, demand.Index - 32), demand.Index - Math.Max(0, demand.Index - 32));
                return !Regex.IsMatch(prefix, @"\b(?:need not|do not need(?: you to)?|don't need(?: you to)?|no need to|will not|won't|not)\s*$", RegexOptions.IgnoreCase);
            }
            bool Supported(string demand) => ReadDictionaryList(parsed, "correctionEvidence").Any(e => {
                string id = ReadString(e, "turnId", ""), quote = ReadString(e, "quote", ""), visible = ReadString(e, "replyQuote", "");
                var demanded = CorrectionSubjectTerms(demand);
                return id.Length > 0 && quote.Length >= 12 && visible.Length >= 12 && reply.Contains(visible, StringComparison.Ordinal)
                    && visible.Contains(demand.Trim(), StringComparison.Ordinal)
                    && demanded.Intersect(CorrectionSubjectTerms(quote)).Count() >= 2
                    && CorrectionSubjectTerms(visible).Intersect(CorrectionSubjectTerms(quote)).Count() >= 3
                    && Regex.IsMatch(visible, @"\b(?:because|since|reason|connect|linked)\b", RegexOptions.IgnoreCase)
                    && (history ?? new()).Any(row => ReadFirstString(row, "originalTurnId", "turn_id", "turnId", "id") == id
                        && !IsNaturalnessSpeaker(row, heroId, "") && ReadString(row, "text", "") != correction
                        && ReadString(row, "text", "").Contains(quote, StringComparison.Ordinal) && !ConversationRepairFailed(row));
            });
            void Issue(string text, string field) => issues.Add(TestDict("type", "critical_continuity_correction_conflict", "match", text,
                "field", field, "playerCorrection", correction, "detail", ConversationCorrectionContract));
            if (DisputesConversationRelevance(correction))
                foreach (string clause in Clauses(reply))
                    if ((Demand(clause) && !Supported(clause))
                        || Regex.IsMatch(clause, @"\b(?:it does matter|(?:your|the) account has improved under questioning|answered in proverb)\b", RegexOptions.IgnoreCase))
                    { Issue(clause, "reply"); break; }

            var updates = ReadDictionary(parsed, "stateUpdates");
            string plan = ReadString(updates, "currentPlan", "");
            if (contested.Length > 0 && (updates == null || !updates.ContainsKey("currentPlan")
                || NormalizeLookup(plan) == NormalizeLookup(contested)))
                Issue(contested, "unreconciled_currentPlan");
            if (DisputesConversationRelevance(correction))
            {
                foreach (string clause in Clauses(plan))
                    if (Demand(clause) && !Supported(clause)) Issue(clause, "stateUpdates.currentPlan");
                foreach (var write in ReadDictionaryList(parsed, "continuityWrites")
                    .Where(w => new[] { "agenda", "question" }.Contains(ReadString(w, "kind", "")) && !ContinuityClosed(ReadString(w, "status", ""))))
                    foreach (string field in new[] { "subject", "nextStep", "meaning" })
                        foreach (string clause in Clauses(ReadString(write, field, "")))
                            if ((Demand(clause) || ReadString(write, "kind", "") == "question"
                                && subjects.Intersect(CorrectionSubjectTerms(clause)).Count() >= 2
                                && Regex.IsMatch(clause, @"\b(?:which|whose|where|when)\b", RegexOptions.IgnoreCase))
                                && !Supported(clause)) Issue(clause, "continuityWrites." + field);
                foreach (string field in new[] { "beliefWrites", "comprehensionWrites" })
                    foreach (var write in ReadDictionaryList(parsed, field))
                        foreach (string text in write.Values.OfType<string>())
                            if (subjects.Intersect(CorrectionSubjectTerms(text)).Count() >= 2
                                && Regex.IsMatch(text, @"\b(?:owes? (?:me|her|him|an answer)|refused to answer|evasiv(?:e|on))\b", RegexOptions.IgnoreCase))
                                Issue(text, field);
            }
            return issues.Take(6).ToList();
        }

        private static List<Dictionary<string, object>> FindGroupExpressionEchoes(Dictionary<string, object> parsed,
            string playerText, List<Dictionary<string, object>> shared, string heroId, string heroName)
        {
            var result = new List<Dictionary<string, object>>();
            if (RequestsExpressionRepeat(playerText) || shared == null) return result;
            int current = shared.FindLastIndex(row => ReadString(row, "role", "") == "player"
                && ReadString(row, "text", "").Trim() == (playerText ?? "").Trim());
            if (current < 0) return result;
            string reply = ReadFirstString(parsed, "reply", "response", "text", "content");
            var requested = ExpressionTerms(ConversationSpeech(playerText));
            foreach (var other in shared.Skip(current + 1).Where(row => ReadString(row, "role", "") == "npc"
                && !IsNaturalnessSpeaker(row, heroId, heroName) && !ConversationRepairFailed(row)))
            {
                string old = ReadString(other, "text", "");
                var gestures = Regex.Matches(old, @"\*[^*]+\*").Cast<Match>().Select(m => m.Value).ToList();
                foreach (Match gesture in Regex.Matches(reply, @"\*[^*]+\*"))
                {
                    var terms = ExpressionTerms(gesture.Value, heroName);
                    foreach (string previous in gestures)
                    {
                        var prior = ExpressionTerms(previous, ReadFirstString(other, "speaker", "speaker_name"));
                        var common = terms.Intersect(prior).ToList();
                        if (common.Count < 4 || common.Count / (double)Math.Max(1, Math.Min(terms.Count, prior.Count)) < .32d
                            || !common.Any(t => Regex.IsMatch(t, @"^(?:drink|sip|swallow|pour|smile|laugh|nod|turn|fold|touch|rub|press|lean|set|take|accept)$"))) continue;
                        result.Add(TestDict("type", "group_conversation_gesture_echo", "match", gesture.Value,
                            "priorEvidence", previous, "detail", "Another speaker just performed this distinctive gesture in the same player beat. Give this NPC an independent reaction; do not clone the choreography. Preserve necessary handling of an offered object and actual completed drink quantities."));
                        break;
                    }
                    if (result.Count > 0) break;
                }
                if (RequestsLiteralAccounting(playerText)) continue;
                foreach (string sentence in ConversationExpressionSentences(reply).Where(s => !IsExpressionFunctionalSentence(s)))
                {
                    var terms = ExpressionTerms(sentence, heroName); terms.ExceptWith(requested);
                    if (terms.Count < 4) continue;
                    foreach (string previous in ConversationExpressionSentences(old).Where(s => !IsExpressionFunctionalSentence(s)))
                    {
                        var prior = ExpressionTerms(previous); prior.ExceptWith(requested);
                        var common = terms.Intersect(prior).ToList();
                        if (common.Count < 3 || common.Count / (double)Math.Max(1, Math.Min(terms.Count, prior.Count)) < .65d
                            || !common.Any(t => t.StartsWith("@", StringComparison.Ordinal))) continue;
                        result.Add(TestDict("type", "group_conversation_framing_echo", "match", sentence,
                            "priorEvidence", previous, "detail", "This NPC repeats the previous speaker's distinctive framing in the same beat. Keep independently necessary facts, agreement and refusal, but contribute something distinct instead of a second version of the lecture."));
                        break;
                    }
                    if (result.Any(v => ReadString(v, "type", "") == "group_conversation_framing_echo")) break;
                }
            }
            return result.Take(3).ToList();
        }
    }
}
