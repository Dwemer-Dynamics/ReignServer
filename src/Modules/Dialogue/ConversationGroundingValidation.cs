using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> CurrentObservableActions(Dictionary<string, object> payload, string observer)
        {
            if (!PlayerInputSources.TryGetValue(payload, out var source)
                || ReadString(source.Interpretation, "status", "") != "validated") return new();
            string projection = ProjectCurrentPlayerPromptText(source.Original, observer, payload);
            return ReadDictionaryList(source.Interpretation, "spans").Where(span => ReadString(span, "kind", "") == "observable"
                && projection.Contains(ReadString(span, "text", ""), StringComparison.Ordinal)).ToList();
        }

        private static Dictionary<string, object> CurrentConversationPartyContext(Dictionary<string, object> payload)
        {
            var party = ReadDictionary(payload, "partyContext") ?? ReadDictionary(ReadDictionary(payload, "localPresence"), "partyContext");
            double day = ReadDouble(payload, "worldDay", -1d), captured = ReadDouble(party, "worldDay", -2d);
            string id = ReadString(payload, "playerPartyId", "");
            return ReadString(party, "schema", "") == "reign-party-context-v1"
                && double.IsFinite(day) && double.IsFinite(captured) && day >= captured && day - captured <= 1d / 1440d
                && ReadString(party, "partyId", "").Length > 0 && (id.Length == 0 || id == ReadString(party, "partyId", "")) ? party : null;
        }

        private static string BuildCurrentConversationGrounding(Dictionary<string, object> payload, string observer)
        {
            var text = new StringBuilder("CURRENT CONTRIBUTION AND SCENE GROUNDING\n");
            text.AppendLine("Player dialogue and narration are complete story input. Respond naturally to the whole contribution; no per-gesture actionResponses or reaction quotes are required. Narration is not a native execution receipt, consent, or a formal agreement. Historical, hypothetical and negated wording keeps its meaning; do not turn it into a new completed event. Do not infer hidden thoughts and discard the surrounding story.");
            text.AppendLine("Preserve who said and did each thing: the player owns player contributions, and each NPC owns their own replies. Before saying 'you asked', 'you said' or 'you did', check the original speaker/actor; your own earlier question remains yours. A memory paraphrase is not independent proof of ownership.");
            text.Append("COMPLETE CURRENT PLAYER CONTRIBUTION: ").AppendLine(ProjectCurrentPlayerPromptText(ReadString(payload, "playerText", ""), observer, payload));
            var party = CurrentConversationPartyContext(payload);
            text.AppendLine("PARTY MEMBERSHIP IS INDEPENDENT OF TOWN, ROOM, CLAN, ARMY AND CONVERSATION ATTENDANCE.");
            text.AppendLine(party == null ? "Current party membership is unknown; do not invent membership or absence."
                : "Native party roster (" + (ReadBool(party, "complete", false) ? "complete" : "partial; omissions unknown") + "): " + Json.Serialize(party));
            text.AppendLine("A member or guest is not automatically a scout, guard or a witness of earlier travel. Present roster membership never proves historical travel. Require separately dated source evidence for those claims. An older NPC assertion is an attributed statement, not independent corroboration. When corrected, reassess against the supplied facts.");
            var scene = ReadDictionary(payload, "conversationSceneState");
            if (scene != null)
            {
                text.Append("CURRENT NARRATIVE VENUE: ").AppendLine(ReadString(scene, "conversationVenue", ""));
                text.AppendLine("Native room defaults do not reset this narrative venue. Keep the body here. The server supplies the final date/location heading; omit a heading from your reply.");
                text.AppendLine("For each scene proposal affecting YOU return sceneDecisions:[{proposalId,outcome,replyQuote}], outcome accepted or refused, with exact visible supporting words. Establishment describes the current scene; movement with another person is a proposal and never forces that person's consent. Do not relocate anyone else or supply a different venue in sceneStateUpdates.");
                text.Append("SOURCE-BOUND SCENE PROPOSALS: ").AppendLine(Json.Serialize(ReadDictionaryList(scene, "proposals")));
            }
            return text.ToString();
        }

        private static IEnumerable<string> PartyAssertionsForNamedPerson(IEnumerable<string> values, string name)
        {
            foreach (string value in values)
                foreach (string paragraph in Regex.Split(value ?? "", @"\r?\n\s*\r?\n"))
                {
                    var sentences = Regex.Split(paragraph, @"(?<=[.!?])\s+|\r?\n");
                    for (int index = 0; index < sentences.Length; index++)
                    {
                        string sentence = sentences[index];
                        if (Regex.IsMatch(sentence, @"\b" + Regex.Escape(name) + @"\b", RegexOptions.IgnoreCase))
                            yield return sentence;
                        else if (index > 0
                            && sentences[index - 1].Trim().Trim('*', '"', '\'', '“', '”', '‘', '’', '.', ':', '!').Trim()
                                .Equals(name, StringComparison.OrdinalIgnoreCase)
                            && Regex.IsMatch(sentence, @"\A\s*(?:(?:She|He|They)\b|The\s+(?:[\p{L}'’-]+\s+){0,4}(?:woman|man|girl|boy|companion|scout)\b)", RegexOptions.IgnoreCase))
                            // A bare name followed by its description is one
                            // claim. Do not infer pronouns across paragraphs or
                            // attach another named person's sentence to it.
                            yield return name + " — " + sentence;
                    }
                }
        }

        private static List<Dictionary<string, object>> FindConversationGroundingViolations(Dictionary<string, object> parsed,
            Dictionary<string, object> payload, string observer)
        {
            var issues = new List<Dictionary<string, object>>();
            issues.AddRange(FindDeliveredMemoryRecallViolations(parsed, payload, observer));
            void Issue(string category, string detail) => issues.Add(TestDict("type", "critical_continuity_" + category, "detail", detail));
            string reply = ReadFirstString(parsed, "reply", "body", "response", "text");
            foreach (var proposal in ReadDictionaryList(ReadDictionary(payload, "conversationSceneState"), "proposals")
                .Where(p => ReadString(p, "subject_id", "") == observer && ReadString(p, "kind", "") == "move"))
            {
                var decisions = ReadDictionaryList(parsed, "sceneDecisions").Where(d => ReadString(d, "proposalId", "") == ReadString(proposal, "proposal_id", "")).ToList();
                var decision = decisions.Count == 1 ? decisions[0] : null;
                string quote = ReadString(decision, "replyQuote", "");
                if (!new[] { "accepted", "refused" }.Contains(ReadString(decision, "outcome", "")) || quote.Length < 8 || !reply.Contains(quote, StringComparison.Ordinal))
                    Issue("scene_decision", "Movement proposal " + ReadString(proposal, "proposal_id", "") + " needs your independent accepted/refused sceneDecision and exact visible supporting quote.");
            }
            var party = CurrentConversationPartyContext(payload);
            var members = ReadDictionaryList(party, "members").Select(r => ReadString(r, "heroStringId", "")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var knownPeople = ReadDictionaryList(CurrentContinuityPresence(payload), "people")
                .Concat(MergedInteractionParticipantProfiles(payload)).Concat(ReadDictionaryList(party, "members"))
                .GroupBy(p => ReadString(p, "heroStringId", ""), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            var assertions = new List<string>(); CollectConversationStringValues(parsed, assertions);
            foreach (var person in knownPeople)
            {
                string name = ReadString(person, "name", ""), id = ReadString(person, "heroStringId", "");
                if (name.Length == 0) continue;
                foreach (string sentence in PartyAssertionsForNamedPerson(assertions, name).Distinct())
                {
                    if (!Regex.IsMatch(sentence, @"\b" + Regex.Escape(name) + @"\b", RegexOptions.IgnoreCase)
                        || Regex.IsMatch(sentence, @"\b(?:not|never|isn't|wasn't|mistaken|assumed|thought|wrong|don't know|do not know|if|might|could)\b", RegexOptions.IgnoreCase)) continue;
                    bool past = Regex.IsMatch(sentence, @"\b(?:was|were|used to|yesterday|last (?:year|season|week)|remember)\b", RegexOptions.IgnoreCase);
                    var roleClaim = Regex.Match(sentence, @"\b(?:our|party's|party’s)\s+(?<role>scout|surgeon|engineer|quartermaster|guard)\b", RegexOptions.IgnoreCase);
                    var member = ReadDictionaryList(party, "members").FirstOrDefault(m => ReadString(m, "heroStringId", "") == id);
                    if (!past && roleClaim.Success && !ReadStringList(member, "roleEvidence").Contains(roleClaim.Groups["role"].Value, StringComparer.OrdinalIgnoreCase))
                        Issue("party_role", name + " has no supplied native evidence for the current " + roleClaim.Groups["role"].Value + " role. Membership alone does not establish a job.");
                    if (!past && !members.Contains(id) && Regex.IsMatch(sentence, @"\b(?:our (?:party|scout|column|camp|travell?ing companion)|with (?:our party|us)|rides? ahead|riding ahead|scouting for us|front of (?:the|our) column)\b", RegexOptions.IgnoreCase))
                        Issue("party_membership", name + (ReadBool(party, "complete", false) ? " is outside the complete current party roster." : " has no supplied current membership evidence.")
                            + " Town presence or prior conversation does not establish membership, scouting work or days of shared travel. Correct: " + sentence);
                }
            }
            return issues;
        }

        private static List<Dictionary<string, object>> NarratedSceneProposals(Dictionary<string, object> payload)
        {
            if (!PlayerInputSources.TryGetValue(payload, out var source)
                || ReadString(source.Interpretation, "status", "") != "validated") return new();
            var proposals = new List<Dictionary<string, object>>();
            foreach (var change in ReadDictionaryList(source.Interpretation, "sceneChanges"))
            {
                string group = "scene_change_" + ConversationExactTextHash(source.SourceId + "|" + Json.Serialize(change)).Substring(0, 24);
                foreach (string subject in ReadStringList(change, "subjectIds"))
                    proposals.Add(TestDict("proposal_id", group + "_" + subject, "groupId", group, "subject_id", subject,
                        "kind", ReadString(change, "kind", ""), "location_value", ReadString(change, "location", ""),
                        "location_class", ReadString(change, "locationClass", ""), "clothing_value", "",
                        "linked_player", ReadBool(change, "linkedWithPlayer", false) ? 1 : 0,
                        "sourceId", source.SourceId, "sourceQuote", ReadString(change, "quote", "")));
            }
            return proposals;
        }

        private static bool SceneProposalRefused(Dictionary<string, object> proposal, Dictionary<string, object> parsed, string reply)
        {
            var decisions = ReadDictionaryList(parsed, "sceneDecisions").Where(d => ReadString(d, "proposalId", "") == ReadString(proposal, "proposal_id", "")).ToList();
            return decisions.Count == 1 && ReadString(decisions[0], "outcome", "") == "refused"
                && ReadString(decisions[0], "replyQuote", "").Length >= 8
                && reply.Contains(ReadString(decisions[0], "replyQuote", ""), StringComparison.Ordinal);
        }

        private static string ResolvedNarrativeVenue(Dictionary<string, object> payload, string observer, Dictionary<string, object> parsed, string reply)
        {
            var scene = ReadDictionary(payload, "conversationSceneState");
            var selfMove = ReadDictionaryList(parsed, "sceneStateUpdates").LastOrDefault(update => AcceptedSpeakerMovement(update, observer, reply));
            if (selfMove != null) return ReadString(selfMove, "location", "");
            var proposal = ReadDictionaryList(scene, "proposals").LastOrDefault(p => ReadString(p, "subject_id", "") == observer);
            if (proposal != null && SceneProposalRefused(proposal, parsed, reply))
                return ReadString(ReadDictionaryList(scene, "current").FirstOrDefault(s => ReadString(s, "heroStringId", "") == observer), "location", "");
            return ReadString(scene, "conversationVenue", "");
        }

        private static bool AcceptedSpeakerMovement(Dictionary<string, object> update, string speaker, string reply)
        {
            string venue = ReadString(update, "location", ""), supplied = ReadString(update, "supportingQuote", "");
            if (ReadString(update, "heroStringId", "") != speaker || venue.Length == 0) return false;
            return InputNarrationBlocks(reply).SelectMany(block => Regex.Split(ReadString(block, "text", ""), @"(?<=[.!?])\s+")).Any(words => {
                return (supplied.Length == 0 || words.Contains(supplied, StringComparison.Ordinal))
                    && Regex.IsMatch(words, @"\b(?:I|she|he)\s+(?:walk\w*|step\w*|move\w*|enter\w*|return\w*)\s+(?:into|to|inside)\s+(?:the |a |our )?" + Regex.Escape(venue) + @"\b", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(words, @"\b(?:would|could|if|imagin\w*|dream\w*|remember\w*|not)\b", RegexOptions.IgnoreCase);
            });
        }

        private static string RenderGroundedSceneHeading(Dictionary<string, object> payload, string observer, Dictionary<string, object> parsed, string reply)
        {
            if (ReadString(payload, "channel", "").Contains("correspond", StringComparison.OrdinalIgnoreCase)) return reply;
            string venue = ResolvedNarrativeVenue(payload, observer, parsed, reply);
            if (venue.Length == 0 || string.IsNullOrWhiteSpace(reply) || IsTechnicalConversationNotice(reply)) return reply;
            int modelHeadingLength = ConversationVenueHeadingLength(reply);
            string body = modelHeadingLength > 0 ? reply.Substring(modelHeadingLength) : reply;
            string date = ReadString(payload, "conversationDateLabel", "");
            string owner = ReadString(MergedInteractionParticipantProfiles(payload).FirstOrDefault(p => CharacterIdFrom(p) == observer), "name", "");
            string label = owner.Length > 0 ? Regex.Replace(venue, @"^(?:her|his|their)\s+", owner + "'s ", RegexOptions.IgnoreCase) : venue;
            string settlement = ReadFirstString(payload, "currentSettlementName", "settlementName");
            if (settlement.Length > 0 && !label.Contains(settlement, StringComparison.OrdinalIgnoreCase)) label = settlement + ", " + label;
            string header = (date.Length == 0 ? "" : date + " — ") + label;
            if (reply.StartsWith(header + "\n\n", StringComparison.Ordinal)) return reply;
            return header + "\n\n" + body;
        }

        private static readonly string[] ConversationVenueKinds = { "stables?", "tavern", "tent", "bedchamber", "throne room", "courtyard", @"(?:(?:lord['’]s|great|feasting|main)\s+)?hall" };

        private static int ConversationVenueHeadingLength(string reply)
        {
            var heading = Regex.Match(reply ?? "", @"\A[^\r\n]{1,180}(?:\r?\n){2,}");
            if (!heading.Success) return 0;
            string rawLine = heading.Value.Trim();
            if (rawLine.StartsWith("*", StringComparison.Ordinal) && !rawLine.StartsWith("**", StringComparison.Ordinal)) return 0;
            string line = rawLine.Trim('*', '[', ']');
            bool venueLabel = Regex.IsMatch(line, @"(?:—|–| - )") && ConversationVenueKinds.Any(site => Regex.IsMatch(line, @"\b" + site + @"\b", RegexOptions.IgnoreCase));
            bool datedLabel = Regex.IsMatch(line, @"^\d{3,5}\b.{0,150}(?:—|–| - )");
            bool spokenLine = Regex.IsMatch(line, @"\b(?:I|we|you|he|she|they|am|is|are|was|were|that's|that’s|welcome|said|met)\b", RegexOptions.IgnoreCase);
            return (venueLabel || datedLabel) && !spokenLine && !Regex.IsMatch(line, @"[.!?*]|\b(?:remember|yesterday|tomorrow|would|could|if)\b", RegexOptions.IgnoreCase)
                ? heading.Length : 0;
        }

        private static List<Dictionary<string, object>> CurrentSceneBodyContradictions(string venue, string reply)
        {
            var issues = new List<Dictionary<string, object>>();
            string[] sites = ConversationVenueKinds;
            var current = sites.Where(site => Regex.IsMatch(venue, @"\b" + site + @"\b", RegexOptions.IgnoreCase)).ToList();
            if (current.Count == 0) return issues;
            int headingLength = ConversationVenueHeadingLength(reply);
            string heading = headingLength > 0 ? reply.Substring(0, headingLength) : "";
            // Keep narration boundaries separate from adjacent spoken words.
            // "*She enters the tavern.* I accept" is a current venue claim even
            // though its period is followed by a closing star before the space.
            string body = reply ?? "";
            var segments = new List<(string Text, bool Narrated)>();
            int cursor = 0;
            foreach (var block in InputNarrationBlocks(body))
            {
                int start = ReadInt(block, "outerStart", 0), length = ReadInt(block, "outerLength", 0);
                if (start > cursor) segments.Add((body.Substring(cursor, start - cursor), false));
                segments.Add((ReadString(block, "text", ""), true));
                cursor = start + length;
            }
            if (cursor < body.Length) segments.Add((body.Substring(cursor), false));
            foreach (string site in sites.Except(current))
                foreach (var segment in segments)
                foreach (string sentence in Regex.Split(segment.Text, @"(?<=[.!?])\s+|\r?\n|;|[,—–]?\s+(?:but\s+)?now\s+"))
                {
                    if (Regex.IsMatch(sentence, @"\b(?:remember(?:s|ed|ing)?|recall(?:s|ed|ing)?|yesterday|last|earlier|ago|used to|tomorrow|might|could|would|if|not|never)\b", RegexOptions.IgnoreCase)) continue;
                    bool narrated = segment.Narrated;
                    bool present = heading.Contains(sentence, StringComparison.Ordinal) && Regex.IsMatch(sentence, @"\b" + site + @"\b", RegexOptions.IgnoreCase)
                        || Regex.IsMatch(sentence, @"\b(?:here (?:in|at)|around (?:the|our)|inside (?:the|our)|(?:we(?:'re| are)|I am) (?:in|at) (?:the|our|this|a|an))\s+" + site + @"\b", RegexOptions.IgnoreCase)
                        || Regex.IsMatch(sentence, @"\b(?:you(?:'re| are)|we(?:'re| are)|I am)\s+here\b[^.!?\r\n]{0,100}\b(?:in|inside|at)\s+(?:the|our|this|a|an)\s+" + site + @"\b", RegexOptions.IgnoreCase)
                        || (narrated && (Regex.IsMatch(sentence, @"\b(?:in|inside|across|through|around|against|cross(?:es)?|enter(?:s)?)\s+(?:the|this|our|a|an)\s+" + site + @"\b", RegexOptions.IgnoreCase)
                            || Regex.IsMatch(sentence, @"\b(?:the|this|our)\s+" + site + @"\s+(?:noise|chatter|music|walls|rafters|ceiling)\b[^.!?\r\n]{0,65}\b(?:fills?|surrounds?|looms?|enclos\w*)\b[^.!?\r\n]{0,60}\b(?:around|above|over)\s+(?:us|them|him|her)\b", RegexOptions.IgnoreCase)));
                    if (present) issues.Add(TestDict("type", "critical_continuity_scene_body", "match", sentence,
                        "detail", "The accepted current venue is " + venue + ". Correct the body's current setting; changing only its heading is insufficient."));
                }
            return issues;
        }

        private static bool FinalizeConversationGrounding(Dictionary<string, object> payload, string observer,
            Dictionary<string, object> parsed, ref string reply)
        {
            var candidate = CloneDictionary(parsed ?? TestDict()); candidate["reply"] = reply;
            var issues = FindCriticalConversationContinuityViolations(candidate, payload, observer);
            bool ok = issues.Count == 0 && ReadBool(ReadDictionary(candidate, "repairStatus"), "allChecksCleared", true);
            payload["conversationGrounding"] = TestDict("schema", "reign-conversation-grounding-v1", "status", ok ? "checked" : "unresolved",
                "issues", issues, "originalSourceId", ReadString(payload, "inputSourceId", ""), "replyHash", ConversationExactTextHash(reply),
                "actionResponses", ReadDictionaryList(candidate, "actionResponses"), "sceneDecisions", ReadDictionaryList(candidate, "sceneDecisions"),
                "partyContextHash", ConversationExactTextHash(CanonicalJson(CurrentConversationPartyContext(payload) ?? TestDict())),
                "semanticCoverage", "deterministic_checks_with_measured_model_acceptance");
            if (!ok)
            {
                var safe = VisibleFailedRepairWithoutEffects(candidate, "conversation_grounding");
                parsed.Clear(); foreach (var pair in safe) parsed[pair.Key] = pair.Value;
                var noDecision = TestDict("version", 1, "kind", "none", "valid", false,
                    "error", "The response did not pass conversation grounding; no NPC agreement is confirmed.");
                parsed["structuredActionDecision"] = noDecision;
                payload["validatedStructuredActionDecision"] = noDecision;
                reply = ReadString(parsed, "reply", reply);
                payload["conversationGroundingFailed"] = true;
            }
            ReadDictionary(payload, "conversationGrounding")["replyHash"] = ConversationExactTextHash(reply);
            return ok;
        }
    }
}
