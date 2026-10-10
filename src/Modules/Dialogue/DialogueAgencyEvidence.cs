using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private sealed class DialogueRequestEvidence
        {
            internal string Quote;
            internal int Start;
            internal string Owner;
            internal string Topic;
            internal string Performer;
            internal string Observer;
            internal string Kind;
        }

        private static string DialogueRelativeActor(string pronoun, string speaker, string interlocutor) =>
            pronoun.Equals("you", StringComparison.OrdinalIgnoreCase) ? interlocutor : speaker;

        private static List<DialogueRequestEvidence> DialogueRequestClaims(string text, string speaker, string interlocutor)
        {
            text ??= "";
            var result = new List<DialogueRequestEvidence>();
            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            foreach (Match sentence in Regex.Matches(text, @"[^.!?\r\n]+(?:[.!?]+|$)"))
            {
                // This guard handles a small, source-verifiable grammar. Other
                // paraphrases remain uncertain for the optional semantic checker.
                // A shared word, feeling or topic is never proof of authorship.
                var audience = Regex.Match(sentence.Value,
                    @"\bonly\s+person\s+(?<owner>I|you)\s+wanted\s+to\s+see\s+(?:(?<performer>me|you)\s+)?"
                    + @"(?<act>fight|dance|sing|perform|ride|win)\s+was\s+(?<focus>me|you)\b", options);
                if (audience.Success)
                {
                    if (!IsAssertedDialogueRequest(sentence.Value, audience.Index)) continue;
                    string owner = DialogueRelativeActor(audience.Groups["owner"].Value, speaker, interlocutor);
                    bool explicitPerformer = audience.Groups["performer"].Success;
                    string performer = DialogueRelativeActor(explicitPerformer ? audience.Groups["performer"].Value : audience.Groups["focus"].Value, speaker, interlocutor);
                    string observer = explicitPerformer ? DialogueRelativeActor(audience.Groups["focus"].Value, speaker, interlocutor) : owner;
                    result.Add(new DialogueRequestEvidence { Quote = sentence.Value.Trim(), Start = sentence.Index + sentence.Value.IndexOf(sentence.Value.Trim(), StringComparison.Ordinal),
                        Owner = owner, Topic = audience.Groups["act"].Value.ToLowerInvariant(), Performer = performer, Observer = observer, Kind = "desired_observation" });
                    continue;
                }
                var request = Regex.Match(sentence.Value,
                    @"\b(?<owner>I|you)\s+(?:would\s+like|want(?:ed)?|ask(?:ed)?|request(?:ed)?)\s+"
                    + @"(?:(?:you\s+to\s+)?(?:take|bring|show)\s+me\s+(?:to\s+)?|for\s+)(?:the\s+)?"
                    + @"(?<place>[\p{L}][\p{L}\s'-]{2,80})[.!?""”]*\s*$", options);
                if (!request.Success) continue;
                if (!IsAssertedDialogueRequest(sentence.Value, request.Index)) continue;
                string topic = NormalizeLookup(request.Groups["place"].Value);
                // Do not turn abstract needs into an invitation ownership alarm.
                if (Regex.IsMatch(topic, @"\b(?:answer|respect|truth|love|trust|fear|plan|help|understanding|attention|permission|chance|reason)\b", options)) continue;
                result.Add(new DialogueRequestEvidence { Quote = sentence.Value.Trim(), Start = sentence.Index + sentence.Value.IndexOf(sentence.Value.Trim(), StringComparison.Ordinal),
                    Owner = DialogueRelativeActor(request.Groups["owner"].Value, speaker, interlocutor),
                    Topic = topic, Kind = "requested_destination", Performer = "", Observer = "" });
            }
            return result;
        }

        private static bool IsAssertedDialogueRequest(string sentence, int claimStart)
        {
            string prefix = sentence.Substring(0, claimStart);
            // Questions, denied claims, imagined speech and third-party quotations
            // are not an assertion by the current speaker. Uncertain grammar is
            // left to evidence-based verification, never a mandatory rewrite.
            return !sentence.TrimEnd().EndsWith("?", StringComparison.Ordinal)
                && !prefix.Contains('*')
                && !Regex.IsMatch(prefix,
                    @"\b(?:if|perhaps|maybe|imagine|suppose|whether|he\s+said|she\s+said|they\s+said|not\s+true\s+that|never\s+said|did\s+not\s+say|didn't\s+say|not\s+claiming)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool SameDialogueRequest(DialogueRequestEvidence a, DialogueRequestEvidence b) =>
            a.Kind == b.Kind && a.Topic == b.Topic && a.Performer == b.Performer && a.Observer == b.Observer;

        private static Dictionary<string, object> MergeDialogueIntegrityProse(Dictionary<string, object> original,
            Dictionary<string, object> patch, List<Dictionary<string, object>> violations)
        {
            if (patch == null) return null;
            string visible = ReadFirstString(patch, "reply", "body", "response", "text");
            if (visible.Length == 0) return null;
            var merged = CloneDictionary(original);
            string prior = ReadFirstString(original, "reply", "body", "response", "text");
            string field = new[] { "reply", "body", "response", "text" }.FirstOrDefault(original.ContainsKey) ?? "reply";
            merged[field] = visible;
            // The repair owns prose. It cannot choose new relation values, replace
            // consent or erase unrelated correct memories by omitting those fields.
            // Changed explicit citations invalidate only the effects they support.
            var invalidated = new List<Dictionary<string, object>>();
            foreach (string key in new[] { "memoryWrites", "beliefWrites", "comprehensionWrites", "continuityWrites", "obligationWrites",
                "relationshipAssessments", "socialSignals", "drinkingEvents", "identityIntroductions", "proposalDecisions", "dynamicCharacteristicWrites" })
            {
                if (!original.ContainsKey(key)) continue;
                var kept = new List<Dictionary<string, object>>();
                int index = 0;
                foreach (var item in ReadDictionaryList(original, key))
                {
                    var quotes = new[] { ReadString(item, "evidenceQuote", ""), ReadString(item, "replyQuote", ""),
                        ReadString(item, "supportingQuote", ""), ReadString(ReadDictionary(item, "sourceEvidence"), "quote", "") }
                        .Concat(ReadDictionaryList(item, "evidenceSpans").Select(s => ReadString(s, "quote", "")))
                        .Concat(ReadDictionaryList(item, "sourceEvidenceSpans").Select(s => ReadString(s, "quote", "")))
                        .Where(q => q.Length > 0).ToList();
                    bool changedSource = quotes.Any(q => prior.Contains(q, StringComparison.Ordinal) && !visible.Contains(q, StringComparison.Ordinal));
                    if (changedSource) invalidated.Add(TestDict("field", key, "index", index, "reason", "cited_reply_span_changed"));
                    else kept.Add(item);
                    index++;
                }
                merged[key] = kept;
            }
            merged["integrityProseReceipt"] = TestDict("originalReplyHash", PromptHash(prior), "acceptedReplyHash", PromptHash(visible),
                "ownedFields", new[] { field }, "invalidatedEffects", invalidated);
            return merged;
        }

        private static List<Dictionary<string, object>> FindAttributedRequestInversions(string reply,
            Dictionary<string, object> payload, List<Dictionary<string, object>> priorLines, string speakerId)
        {
            string player = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            if (player.Length == 0) player = "current_player";
            var lines = priorLines ?? new List<Dictionary<string, object>>();
            var current = DialogueRequestClaims(reply, speakerId, player).Where(c => c.Owner == player).ToList();
            var result = new List<Dictionary<string, object>>();
            foreach (var claim in current)
            {
                var originals = new List<(Dictionary<string, object> Line, DialogueRequestEvidence Claim)>();
                bool playerAlsoRequested = false;
                foreach (var line in lines)
                {
                    string author = ReadFirstString(line, "speakerId", "speakerHeroStringId", "heroStringId");
                    if (author != speakerId && author != player) continue;
                    var addressees = ReadStringList(line, "addressedIds");
                    if (GroupConversationParticipantIds(payload).Count > 2
                        && !addressees.Contains(author == speakerId ? player : speakerId, StringComparer.OrdinalIgnoreCase)) continue;
                    foreach (var original in DialogueRequestClaims(ReadFirstString(line, "text", "reply", "content"), author, author == speakerId ? player : speakerId))
                    {
                        if (!SameDialogueRequest(claim, original)) continue;
                        if (original.Owner == player) playerAlsoRequested = true;
                        if (author == speakerId && original.Owner == speakerId) originals.Add((line, original));
                    }
                }
                if (playerAlsoRequested || originals.Count == 0) continue;
                var source = originals.Last();
                string sourceText = ReadFirstString(source.Line, "text", "reply", "content");
                result.Add(new Dictionary<string, object> { ["type"] = "request_agency_inverted",
                    ["currentText"] = claim.Quote, ["currentStart"] = claim.Start, ["currentLength"] = claim.Quote.Length,
                    ["originatingNpcLine"] = source.Claim.Quote, ["sourceStart"] = source.Claim.Start,
                    ["sourceLength"] = source.Claim.Quote.Length, ["sourceHash"] = PromptHash(sourceText),
                    ["sourceId"] = ReadFirstString(source.Line, "turnId", "id", "exchangeId"),
                    ["originalRequestOwner"] = speakerId, ["claimedRequestOwner"] = player,
                    ["performer"] = claim.Performer, ["observer"] = claim.Observer,
                    ["evidenceKind"] = claim.Kind, ["field"] = "reply" });
            }
            return result;
        }

        private static List<Dictionary<string, object>> RunDialogueAgencyEvidenceAssertions()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string name, bool pass, object evidence = null) => rows.Add(TestDict("name", "agency_source_" + name, "passed", pass, "detail", evidence));
            var payload = TestDict("playerHeroStringId", "player");
            var history = new List<Dictionary<string, object>> { TestDict("speakerId", "npc", "turnId", "source_1",
                "text", new string('x', 650) + ". I told you the only person I wanted to see me fight was you.") };
            string bad = new string('y', 650) + ". You don't get to tell me the only person you wanted to see fight was me and then leave.";
            var found = FindAttributedRequestInversions(bad, payload, history, "npc");
            Check("inversion_pins_decisive_clause_after_long_narration", found.Count == 1
                && ReadString(found[0], "currentText", "").Contains("only person you wanted")
                && ReadString(found[0], "originatingNpcLine", "").Contains("I wanted to see me fight was you")
                && ReadInt(found[0], "sourceStart", 0) > 600, found);
            Check("correct_wisher_is_not_flagged", FindAttributedRequestInversions("I told you the only person I wanted to see me fight was you.", payload, history, "npc").Count == 0);
            foreach (string uncertain in new[] {
                "It is not true that the only person you wanted to see fight was me.",
                "Perhaps the only person you wanted to see fight was me.",
                "She said the only person you wanted to see fight was me.",
                "Was the only person you wanted to see fight me?",
                "*I imagine the only person you wanted to see fight was me.*" })
                Check("nonasserted_claim_" + PromptHash(uncertain).Substring(0, 8),
                    FindAttributedRequestInversions(uncertain, payload, history, "npc").Count == 0);
            var fear = new List<Dictionary<string, object>> { TestDict("speakerId", "npc", "text", "I wanted to speak about my fear of losing our family.") };
            Check("shared_feeling_words_do_not_transfer_fear", FindAttributedRequestInversions("You wanted to discuss our family; my fear is mine to face.", payload, fear, "npc").Count == 0);
            var destination = new List<Dictionary<string, object>> { TestDict("speakerId", "npc", "text", "I would like you to take me to the waterfall.") };
            Check("explicit_invitation_owner", FindAttributedRequestInversions("You asked for the waterfall.", payload, destination, "npc").Count == 1);
            destination.Add(TestDict("speakerId", "player", "text", "I asked for the waterfall."));
            Check("both_asked_is_not_an_inversion", FindAttributedRequestInversions("You asked for the waterfall.", payload, destination, "npc").Count == 0);
            var group = TestDict("playerHeroStringId", "player", "activeHeroIds", new[] { "npc", "other", "player" });
            Check("group_you_needs_known_addressee", FindAttributedRequestInversions(bad, group, history, "npc").Count == 0);

            var complete = CompleteConversationStructuredResponse(TestDict("reply", bad,
                "relationshipAssessments", new List<Dictionary<string, object>> { TestDict("targetHeroStringId", "player", "valence", "positive",
                    "severityTier", "meaningful", "currentConductQuote", "I stayed away because I cared about your happiness.") },
                "memoryWrites", new List<Dictionary<string, object>> { TestDict("text", "The player said he cared.", "evidenceQuote", "I stayed away because I cared about your happiness.") }), "dialogue", out _);
            var request = TestDict("requestType", "dialogue", "model", "test_writer");
            int calls = 0;
            string fixedReply = "I told you the only person I wanted to see me fight was you. I hear why you stayed away.";
            var fixedResult = RetryDialogueIntegrityViolation(TestDict("ok", true, "content", Json.Serialize(complete)), request, payload, history,
                "agency_source_test", "revision", "dialogue", "npc", "", req => {
                    calls++;
                    string supplied = Json.Serialize(req);
                    Check("repair_receives_decisive_original_and_offending_clause", supplied.Contains("I wanted to see me fight was you")
                        && supplied.Contains("only person you wanted to see fight was me"));
                    return TestDict("ok", true, "content", Json.Serialize(TestDict("reply", fixedReply,
                        "relationshipAssessments", new List<Dictionary<string, object>>())));
                }, false);
            var fixedObject = TryParseJsonObject(ReadString(fixedResult, "content", ""));
            Check("prose_repair_preserves_unrelated_positive_metadata", calls == 1
                && ReadString(ReadDictionaryList(fixedObject, "relationshipAssessments").FirstOrDefault(), "valence", "") == "positive"
                && ReadDictionaryList(fixedObject, "memoryWrites").Count == 1
                && !ReadBool(fixedResult, "visibleRepairFailure", false), fixedResult);
            var unchanged = RetryDialogueIntegrityViolation(TestDict("ok", true, "content", Json.Serialize(complete)), request, payload, history,
                "agency_source_test", "unchanged", "dialogue", "npc", "", _ => TestDict("ok", true, "content", Json.Serialize(complete)), false);
            Check("unchanged_known_inversion_cannot_be_accepted", ReadBool(unchanged, "visibleRepairFailure", false)
                && !ReadString(TryParseJsonObject(ReadString(unchanged, "content", "")), "reply", "").Contains("only person you wanted"), unchanged);
            return rows;
        }
    }
}
