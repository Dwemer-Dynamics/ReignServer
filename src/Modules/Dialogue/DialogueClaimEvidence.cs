using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly string[] DialogueFactualWriteFields = {
            "memoryWrites", "beliefWrites", "comprehensionWrites", "continuityWrites", "obligationWrites", "dynamicCharacteristicWrites" };
        private const string DialogueNonFactTypes = "subjective_belief|hypothesis|future_intention";

        private static bool DialogueClaimIsUncertain(string text) => Regex.IsMatch(text ?? "",
            @"\b(?:if|perhaps|maybe|imagine|suppose|whether|might|could|would|intend|intends|intended|plans?|hopes?|wishes?|suspect|suspects|believe|believes|I think|I thought|not sure|lying|deliberately mislead)\b",
            RegexOptions.IgnoreCase);

        // These candidates cover declarative facts in addition to explicit speech
        // recalls. Extraction is coverage, never proof that a statement is false.
        private static List<RecalledDialogueClaim> ExtractDialogueFactualClaims(string text,
            Dictionary<string, object> payload, Dictionary<string, object> parsed, string speaker,
            string field = "reply", int writeIndex = -1, Dictionary<string, object> write = null)
        {
            text ??= "";
            bool lasting = field != "reply";
            var claims = ExtractRecalledDialogueClaims(text, payload, lasting ? TestDict() : parsed, speaker);
            claims.ForEach(c => { c.Field = field; c.WriteIndex = writeIndex; c.Write = write; });
            if (!lasting && Regex.IsMatch(ReadString(parsed, "intent", ""), @"\b(?:deceive|deception|lie|lying|mislead|deliberate_deception)\b", RegexOptions.IgnoreCase)) return new();
            string player = FirstNonEmpty(ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"), "current_player");
            string addressee = ReadStringList(parsed, "addressedHeroStringIds").Count == 1
                ? ReadStringList(parsed, "addressedHeroStringIds")[0] : GroupConversationParticipantIds(payload).Count > 2 ? "" : player;
            string visible = lasting ? text : DialogueClaimSpeech(ConversationSpeechForRecall(text));
            foreach (Match sentence in Regex.Matches(visible, @"[^.!?\r\n]+(?:[.!?]+|$)"))
            {
                string value = sentence.Value.Trim();
                if (value.Length < 12 || value.EndsWith("?", StringComparison.Ordinal)) continue;
                if (DialogueClaimIsUncertain(value)) continue;
                if (!lasting && !Regex.IsMatch(value, @"\b(?:met|married|unmarried|refused|accepted|arranged|left|departed|wrote|sent|received|promised|agreed|answered|asked|told|said|before|after|first|last|never|yesterday|previously|remember)\b", RegexOptions.IgnoreCase)) continue;
                // Split independent finite clauses. Keep shared-subject fragments
                // attached: a missing actor must not be invented during splitting.
                foreach (Match part in Regex.Matches(sentence.Value,
                    @"(?:^|(?<=;)|(?<=\band ))[^;]+?(?=;|\band\s+(?:I|you|he|she|they|[A-Z][\p{L}-]+)\s+(?:said|asked|met|left|wrote|sent|received|was|is|had|agreed)\b|$)"))
                {
                    string clause = part.Value.Trim().TrimEnd('.', '!');
                    if (clause.Length < 12 || claims.Any(c => c.Quote == clause || c.Start >= sentence.Index + part.Index && c.Start < sentence.Index + part.Index + part.Length)) continue;
                    int clauseStart = sentence.Index + part.Index + part.Value.IndexOf(clause, StringComparison.Ordinal);
                    claims.Add(new RecalledDialogueClaim { Quote = clause, Clause = clause, Subject = "", ClaimedActor = speaker,
                        Canonical = CanonicalDialogueClause(clause, speaker, addressee), Start = clauseStart,
                        Kind = "factual_statement", Field = field, WriteIndex = writeIndex, Write = write });
                }
            }
            return claims;
        }

        private static List<RecalledDialogueClaim> DialogueAllFactualClaims(Dictionary<string, object> parsed,
            Dictionary<string, object> payload, string speaker)
        {
            var result = new List<RecalledDialogueClaim>();
            // Durable claims get first access to the bounded worker; visible recall
            // remains represented even when there is insufficient scoring capacity.
            foreach (string field in DialogueFactualWriteFields)
            {
                int index = 0;
                foreach (var write in ReadDictionaryList(parsed, field).SelectMany(w => field.StartsWith("obligation", StringComparison.Ordinal)
                    ? AtomicCommitmentWrites(w) : new[] { w }))
                {
                    string type = ReadString(write, "evidenceType", ReadString(ReadDictionary(write, "sourceEvidence"), "evidenceType", ""));
                    if (!Regex.IsMatch(type, "^(?:" + DialogueNonFactTypes + ")$"))
                        result.AddRange(ExtractDialogueFactualClaims(ReadFirstString(write, "text", "summary", "claim", "description"), payload, parsed, speaker, field, index, write));
                    index++;
                }
            }
            result.AddRange(ExtractDialogueFactualClaims(ReadFirstString(parsed, "reply", "body", "response", "text"), payload, parsed, speaker));
            return result;
        }

        private static List<Dictionary<string, object>> FindDialogueNativeSpouseContradictions(string reply,
            Dictionary<string, object> payload, string speaker, string addressee)
        {
            var result = new List<Dictionary<string, object>>();
            foreach (Match sentence in Regex.Matches(DialogueClaimSpeech(reply), @"[^.!?\r\n]+(?:[.!?]+|$)"))
            {
                if (sentence.Value.TrimEnd().EndsWith("?") || DialogueClaimIsUncertain(sentence.Value)
                    || Regex.IsMatch(sentence.Value, @"\b(?:said|told|used to|then|before|after|years? ago|yesterday|refused|arranged|failed)\b", RegexOptions.IgnoreCase)) continue;
                string normalized = DialogueFactIdentityText(sentence.Value, payload, speaker, addressee);
                foreach (var profile in MergedInteractionParticipantProfiles(payload))
                {
                    string actor = CharacterIdFrom(profile).ToLowerInvariant(), spouse = ReadString(profile, "spouseId", "").ToLowerInvariant();
                    // Empty native fields never erase commoner households, prove
                    // someone has never married or establish their historical choices.
                    if (actor.Length == 0 || spouse.Length == 0) continue;
                    bool falseSingle = Regex.IsMatch(normalized, @"\b" + Regex.Escape(actor) + @"\s+(?:is|am|are)\s+(?:currently\s+)?(?:unmarried|not married)\s*$");
                    var other = Regex.Match(normalized, @"\b" + Regex.Escape(actor) + @"\s+(?:is|am|are)\s+(?:currently\s+)?married to (?<spouse>[\p{L}\p{N}_]+)\b");
                    var knownIds = MergedInteractionParticipantProfiles(payload).Select(CharacterIdFrom).Select(id => id.ToLowerInvariant()).ToList();
                    bool falseSpouse = other.Success && knownIds.Contains(other.Groups["spouse"].Value) && other.Groups["spouse"].Value != spouse;
                    if (!falseSingle && !falseSpouse) continue;
                    string fact = actor + " has native spouse " + spouse + ".";
                    string quote = sentence.Value.Trim();
                    result.Add(TestDict("type", "source_proven_native_spouse_conflict", "field", "reply", "currentText", quote,
                        "currentStart", sentence.Index + sentence.Value.IndexOf(quote, StringComparison.Ordinal), "currentLength", quote.Length, "sourceId", "native_profile_" + actor,
                        "sourceHash", MemorySelectionTextHash(CanonicalJson(profile)).ToLowerInvariant(), "originatingNpcLine", fact,
                        "sourceStart", 0, "sourceLength", fact.Length, "proof", "nonempty_current_native_spouse_id",
                        "evidenceKind", "native_current_result", "sourceSpeakerId", "native_state"));
                }
            }
            return result;
        }

        private static List<string> DialogueClaimTerms(string text) => Regex.Matches((text ?? "").ToLowerInvariant(), @"[\p{L}]{3,}|\d+")
            .Select(m => m.Value).Where(t => !Regex.IsMatch(t,
                @"^(?:said|told|asked|that|this|with|from|have|has|had|was|were|are|the|and|but|for|you|your|myself|person|current|player|npc|she|her|him|his|they|their|them|would|will|can|not|before|after|first|last|only|very|there|here|about|been|being|because|which|when|then)$"))
            .Select(t => t switch { "leaving" or "departed" or "departure" => "left", "meeting" => "met", "writing" or "written" => "wrote", "receiving" => "received", _ => t })
            .Distinct(StringComparer.Ordinal).ToList();

        private static List<Dictionary<string, object>> DialogueClaimPinnedSpans(RecalledDialogueClaim claim,
            List<Dictionary<string, object>> sources)
        {
            if (claim.Write == null) return new();
            var pins = ReadDictionaryList(claim.Write, "sourceEvidenceSpans").Select(CloneDictionary).ToList();
            var primary = ReadDictionary(claim.Write, "sourceEvidence");
            if (primary != null) pins.Insert(0, primary);
            var spans = new List<Dictionary<string, object>>();
            foreach (var pin in pins)
            {
                string id = ReadFirstString(pin, "turnId", "sourceId", "id");
                string quote = ReadFirstString(pin, "quote", "text");
                foreach (var source in sources.Where(s => id.Length == 0 || ReadString(s, "id", "") == id))
                {
                    string text = ReadString(source, "text", "");
                    string actor = ReadFirstString(pin, "speakerId", "speakerHeroStringId");
                    if (quote.Length == 0 || actor.Length > 0 && actor != ReadString(source, "speakerId", "")) continue;
                    int start = text.IndexOf(quote, StringComparison.Ordinal);
                    if (start < 0 || text.IndexOf(quote, start + quote.Length, StringComparison.Ordinal) >= 0) continue;
                    spans.Add(TestDict("source", source, "start", start, "length", quote.Length, "text", quote, "pinned", true));
                }
            }
            return spans;
        }

        private static List<Dictionary<string, object>> DialogueClaimEvidenceSpans(RecalledDialogueClaim claim,
            List<Dictionary<string, object>> sources)
        {
            var pinned = DialogueClaimPinnedSpans(claim, sources);
            var terms = DialogueClaimTerms(claim.Clause);
            var candidates = new List<Dictionary<string, object>>();
            foreach (var source in sources)
            {
                string text = DialogueClaimSpeech(ReadString(source, "text", ""));
                var sentences = Regex.Matches(text, @"[^.!?\r\n]+(?:[.!?]+|$)").Cast<Match>().ToList();
                for (int i = 0; i < sentences.Count; i++)
                {
                    var match = sentences[i];
                    var sourceTerms = DialogueClaimTerms(match.Value);
                    int overlap = terms.Count(sourceTerms.Contains);
                    bool exact = CanonicalDialogueClause(match.Value, ReadString(source, "speakerId", ""), "") == claim.Canonical;
                    if (!exact && (overlap == 0 || terms.Count > 3 && overlap < 2)) continue;
                    int start = match.Index, end = start + match.Length;
                    // Include adjacent context when it qualifies the assertion or
                    // supplies its question/answer. Never silently clip a sentence.
                    if (i > 0 && (sentences[i - 1].Value.TrimEnd().EndsWith("?")
                        || Regex.IsMatch(match.Value, @"^\s*(?:No|Yes|That|But|Actually|Correct|Instead)\b", RegexOptions.IgnoreCase))) start = sentences[i - 1].Index;
                    if (i + 1 < sentences.Count && Regex.IsMatch(sentences[i + 1].Value,
                        @"^\s*(?:But|However|Except|Unless|Instead|Actually|Not|No)\b", RegexOptions.IgnoreCase)) end = sentences[i + 1].Index + sentences[i + 1].Length;
                    while (start < end && char.IsWhiteSpace(text[start])) start++;
                    while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
                    string quote = ReadString(source, "text", "").Substring(start, end - start);
                    candidates.Add(TestDict("source", source, "start", start, "length", end - start, "text", quote,
                        "pinned", false, "score", exact ? 1000 : overlap * 10 + (ReadString(source, "id", "") == "current_player" ? 3 : 0)));
                }
            }
            return pinned.Concat(candidates.OrderByDescending(c => ReadInt(c, "score", 0)))
                .GroupBy(c => ReadString(ReadDictionary(c, "source"), "id", "") + ":" + ReadInt(c, "start", -1) + ":" + ReadInt(c, "length", 0))
                .Select(g => g.First()).ToList();
        }

        private static Dictionary<string, object> BuildDialogueClaimPlan(Dictionary<string, object> parsed,
            Dictionary<string, object> payload, List<Dictionary<string, object>> prior, string speaker)
        {
            var claims = DialogueAllFactualClaims(parsed, payload, speaker);
            var sources = DialogueClaimOriginalSources(payload, prior, speaker);
            var pairs = new List<Dictionary<string, object>>();
            var coverage = new List<Dictionary<string, object>>();
            string player = FirstNonEmpty(ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"), "current_player");
            var profiles = MergedInteractionParticipantProfiles(payload);
            var actors = sources.Select(s => ReadString(s, "speakerId", "")).Concat(profiles.Select(CharacterIdFrom))
                .Concat(claims.Select(c => c.ClaimedActor)).Append(player).Append(speaker).Where(s => s.Length > 0).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
            string Identity(string actor) => "person " + (char)('A' + actors.IndexOf(actor));
            string IdentityText(string text, string actor, string to)
            {
                // Preserve the natural document punctuation and negation for
                // MiniCheck. Exact matching still uses its separate canonical form.
                string normalized = (text ?? "").Replace('’', '\'');
                normalized = Regex.Replace(normalized, @"\bI'm\b", "I am", RegexOptions.IgnoreCase);
                normalized = Regex.Replace(normalized, @"\byou're\b", "you are", RegexOptions.IgnoreCase);
                normalized = Regex.Replace(normalized, @"\b(?:I|me|myself|mine)\b", actor, RegexOptions.IgnoreCase);
                normalized = Regex.Replace(normalized, @"\bmy\b", actor + "'s", RegexOptions.IgnoreCase);
                normalized = Regex.Replace(normalized, @"\b(?:you|yourself|yours)\b", to, RegexOptions.IgnoreCase);
                normalized = Regex.Replace(normalized, @"\byour\b", to + "'s", RegexOptions.IgnoreCase);
                foreach (var profile in profiles.Where(p => ReadString(p, "name", "").Length > 0).OrderByDescending(p => ReadString(p, "name", "").Length))
                    normalized = Regex.Replace(normalized, @"\b" + Regex.Escape(ReadString(profile, "name", "")) + @"\b", CharacterIdFrom(profile), RegexOptions.IgnoreCase);
                foreach (string id in actors.OrderByDescending(a => a.Length))
                    normalized = Regex.Replace(normalized, @"\b" + Regex.Escape(id) + @"\b", Identity(id), RegexOptions.IgnoreCase);
                return normalized;
            }
            int number = 0;
            foreach (var claim in claims)
            {
                string claimId = "factual_" + number++;
                string claimHash = MemorySelectionTextHash(claim.Quote).ToLowerInvariant();
                var row = TestDict("claimId", claimId, "claimHash", claimHash, "field", claim.Field, "writeIndex", claim.WriteIndex,
                    "kind", claim.Kind, "claim", claim.Quote, "start", claim.Start, "length", claim.Quote.Length, "status", "evidence_missing");
                coverage.Add(row);
                if (claim.ClaimedActor.Length == 0) { row["status"] = "actor_unresolved"; continue; }
                if (pairs.Count >= 8) { row["status"] = "comparison_capacity"; continue; }
                var citation = claim.Write == null ? null : ReadDictionary(claim.Write, "sourceEvidence");
                bool explicitCitation = citation != null && ReadFirstString(citation, "turnId", "sourceId", "id", "quote", "text").Length > 0
                    || claim.Write != null && ReadDictionaryList(claim.Write, "sourceEvidenceSpans").Count > 0;
                var pins = DialogueClaimPinnedSpans(claim, sources);
                if (explicitCitation && pins.Count == 0) { row["status"] = "explicit_citation_not_yet_bound"; continue; }
                var spans = explicitCitation ? pins : DialogueClaimEvidenceSpans(claim, sources);
                foreach (var span in spans)
                {
                    var source = ReadDictionary(span, "source");
                    string actor = ReadString(source, "speakerId", "");
                    var addressed = ReadStringList(source, "addressedIds");
                    string to = addressed.Count == 1 ? addressed[0] : GroupConversationParticipantIds(payload).Count > 2 ? "" : actor == speaker ? player : speaker;
                    if (to.Length == 0 && Regex.IsMatch(ReadString(span, "text", ""), @"\b(?:you|your|yours|yourself)\b", RegexOptions.IgnoreCase)) { row["status"] = "source_audience_unresolved"; continue; }
                    string premise = Identity(actor) + " said: " + IdentityText(ReadString(span, "text", ""), actor, to);
                    var evidenceSpans = new List<Dictionary<string, object>> { TestDict("sourceId", source["id"], "sourceHash", source["sourceHash"],
                        "speakerId", actor, "start", span["start"], "length", span["length"], "quote", span["text"]) };
                    // An explicit accepted-turn link can bring a question from
                    // another speaker with its answer. Mere neighboring records
                    // do not prove the answer belongs to that question.
                    string questionId = ReadString(source, "replyToTurnId", "");
                    if (questionId.Length == 0 && ReadString(source, "role", "") == "npc" && ReadString(source, "exchangeId", "").Length > 0)
                    {
                        var exchangeQuestions = sources.Where(s => ReadString(s, "role", "") == "player"
                            && ReadString(s, "exchangeId", "") == ReadString(source, "exchangeId", "")
                            && ReadString(s, "conversationId", "") == ReadString(source, "conversationId", "")).ToList();
                        if (exchangeQuestions.Count == 1) questionId = ReadString(exchangeQuestions[0], "id", "");
                    }
                    var questions = sources.Where(s => ReadString(s, "id", "") == questionId).ToList();
                    if (questionId.Length > 0 && questions.Count == 1)
                    {
                        var question = questions[0];
                        string questionActor = ReadString(question, "speakerId", "");
                        var audience = ReadStringList(question, "addressedIds");
                        string questionTo = audience.Count == 1 ? audience[0] : GroupConversationParticipantIds(payload).Count > 2 ? "" : actor;
                        var questionClauses = Regex.Matches(DialogueClaimSpeech(ReadString(question, "text", "")), @"[^.!?\r\n]+\?").Cast<Match>().ToList();
                        var lastQuestion = questionClauses.Count == 1 ? questionClauses[0] : null;
                        string conversation = ReadString(source, "conversationId", ""), questionConversation = ReadString(question, "conversationId", "");
                        bool ordered = ReadLong(question, "acceptedSequence", 0) <= ReadLong(source, "acceptedSequence", long.MaxValue)
                            && ReadLong(question, "timestamp", 0) <= ReadLong(source, "timestamp", long.MaxValue);
                        if (lastQuestion != null && questionTo == actor && ordered
                            && (conversation.Length == 0 || questionConversation.Length == 0 || conversation == questionConversation))
                        {
                            string quote = lastQuestion.Value.Trim();
                            premise = Identity(questionActor) + " asked: " + IdentityText(quote, questionActor, questionTo) + "\n" + premise;
                            evidenceSpans.Insert(0, TestDict("sourceId", question["id"], "sourceHash", question["sourceHash"], "speakerId", questionActor,
                                "start", lastQuestion.Index + lastQuestion.Value.IndexOf(quote, StringComparison.Ordinal), "length", quote.Length, "quote", quote));
                        }
                    }
                    var targets = ReadStringList(parsed, "addressedHeroStringIds");
                    string draftTo = targets.Count == 1 ? targets[0] : GroupConversationParticipantIds(payload).Count > 2 ? "" : player;
                    if (draftTo.Length == 0 && Regex.IsMatch(claim.Clause, @"\b(?:you|your|yours|yourself)\b", RegexOptions.IgnoreCase)) { row["status"] = "actor_unresolved"; continue; }
                    string hypothesis = (claim.Kind == "spoken_recall" ? Identity(claim.ClaimedActor) + " said: " : "")
                        + IdentityText(claim.Clause, claim.LiteralQuotation ? claim.ClaimedActor : speaker,
                            claim.LiteralQuotation && claim.ClaimedActor != speaker ? speaker : draftTo);
                    // Worker enforces its exact tokenizer limit. This early bound
                    // protects coherent source spans, rather than clipping input.
                    if (premise.Length + hypothesis.Length > 1400) { row["status"] = "complete_evidence_over_budget"; continue; }
                    var existing = pairs.FirstOrDefault(p => ReadString(p, "premise", "") == premise && ReadString(p, "hypothesis", "") == hypothesis);
                    string pairId = existing == null ? "claim_" + pairs.Count : ReadString(existing, "id", "");
                    if (existing == null)
                        pairs.Add(TestDict("id", pairId, "claimId", claimId, "sourceId", source["id"], "sourceHash", source["sourceHash"],
                            "claimHash", claimHash, "field", claim.Field, "writeIndex", claim.WriteIndex, "premise", premise, "hypothesis", hypothesis,
                            "sourceStart", span["start"], "sourceLength", span["length"], "sourceQuote", span["text"], "evidenceSelection", ReadBool(span, "pinned", false) ? "exact_write_citation" : "complete_relevant_source_span",
                            "sourceSpans", evidenceSpans,
                            "premiseHash", MemorySelectionTextHash(premise).ToLowerInvariant(), "hypothesisHash", MemorySelectionTextHash(hypothesis).ToLowerInvariant()));
                    row["pairId"] = pairId; row["sourceId"] = source["id"]; row["sourceHash"] = source["sourceHash"]; row["status"] = "selected_not_scored";
                    break; // One grounded document per atomic claim, not two competing guesses.
                }
            }
            return TestDict("claims", coverage, "pairs", pairs, "sourceCount", sources.Count,
                "claimCount", claims.Count, "uncomparedClaims", coverage.Count(c => !c.ContainsKey("pairId")),
                "unresolvedAddressees", coverage.Count(c => ReadString(c, "status", "") == "actor_unresolved"),
                "identityAliases", actors.Select(actor => TestDict("alias", Identity(actor), "heroStringId", actor)).ToList());
        }

        // A quotation establishes its content, not any more specific sequence,
        // count or location that a summary adds. Holding an incomplete update is
        // different from declaring the character's spoken recollection false.
        private static List<Dictionary<string, object>> QuarantineUncoveredDialogueFacts(Dictionary<string, object> parsed,
            Dictionary<string, object> payload, List<Dictionary<string, object>> prior, string speaker)
        {
            var sources = DialogueClaimOriginalSources(payload, prior, speaker);
            string proposedReply = ReadFirstString(parsed, "reply", "body", "response", "text");
            if (proposedReply.Length > 0)
                sources.Add(TestDict("id", "current_npc", "speakerId", speaker, "text", proposedReply,
                    "sourceHash", MemorySelectionTextHash(proposedReply).ToLowerInvariant(), "provisionalDraft", true));
            // Provisional draft text is available only to this write-support
            // guard. It never becomes an original-history proof or native receipt.
            var held = new List<Dictionary<string, object>>();
            foreach (string field in DialogueFactualWriteFields)
            {
                if (!parsed.ContainsKey(field)) continue;
                int index = 0;
                var kept = new List<Dictionary<string, object>>();
                foreach (var write in ReadDictionaryList(parsed, field))
                {
                    string type = ReadString(write, "evidenceType", ReadString(ReadDictionary(write, "sourceEvidence"), "evidenceType", ""));
                    var claims = Regex.IsMatch(type, "^(?:" + DialogueNonFactTypes + ")$") ? new List<RecalledDialogueClaim>()
                        : ExtractDialogueFactualClaims(ReadFirstString(write, "text", "summary", "claim", "description"), payload, parsed, speaker, field, index, write);
                    var gaps = new List<Dictionary<string, object>>();
                    foreach (var claim in claims)
                    {
                        // Consequential specificity must exist in source content.
                        // Ordinary paraphrases remain under the existing source
                        // validator; absent checker scores never manufacture error.
                        var qualifiers = Regex.Matches(claim.Clause,
                            @"\b(?i:before|after|first|last|only|never|\d+)\b|\b(?i:leaving|left|departing|departed(?: from)?)\s+[A-Z][\p{L}-]+").Cast<Match>().Select(m => m.Value).ToList();
                        if (qualifiers.Count == 0) continue;
                        var spans = DialogueClaimPinnedSpans(claim, sources);
                        if (spans.Count == 0) continue; // Existing source binding owns missing citations.
                        string supporting = string.Join("\n", spans.Select(s => ReadString(s, "text", "")));
                        var terms = DialogueClaimTerms(claim.Clause);
                        var identityTerms = MergedInteractionParticipantProfiles(payload).SelectMany(p => DialogueClaimTerms(ReadString(p, "name", ""))).ToList();
                        var contentTerms = terms.Where(t => !Regex.IsMatch(t, @"^\d+$") && !identityTerms.Contains(t)).ToList();
                        bool anchoredContent = contentTerms.Count > 0
                            && contentTerms.Count(t => DialogueClaimTerms(supporting).Contains(t)) >= Math.Min(2, contentTerms.Count);
                        // The same "before" or number in an unrelated directive
                        // is not support for this claim's relation or quantity.
                        var missing = qualifiers.Where(q => !supporting.Contains(q, StringComparison.OrdinalIgnoreCase) || !anchoredContent).ToList();
                        if (missing.Count == 0) continue;
                        // Another eligible original can justify added detail. Do
                        // not mistake one narrow citation for the whole evidence.
                        bool otherSupport = sources.Where(s => !ReadBool(s, "provisionalDraft", false)).Any(s => missing.All(q => ReadString(s, "text", "").Contains(q, StringComparison.OrdinalIgnoreCase))
                            && terms.Count(t => DialogueClaimTerms(ReadString(s, "text", "")).Contains(t)) >= Math.Max(2, terms.Count - 1));
                        if (!otherSupport) gaps.Add(TestDict("claim", claim.Quote, "missingSpecificity", missing,
                            "sourceIds", spans.Select(s => ReadString(ReadDictionary(s, "source"), "id", "")).ToList()));
                    }
                    if (gaps.Count == 0) kept.Add(write);
                    else held.Add(TestDict("field", field, "index", index, "proposed", write, "status", "withheld_incomplete",
                        "reason", "added_specificity_not_established_by_cited_originals", "evidence", gaps));
                    index++;
                }
                parsed[field] = kept;
            }
            AddExplicitCommitmentFallback(parsed, payload, speaker);
            return held;
        }

        private static string DialogueFactIdentityText(string text, Dictionary<string, object> payload, string speaker, string addressee)
        {
            string value = CanonicalDialogueClause(text, speaker, addressee);
            foreach (var profile in MergedInteractionParticipantProfiles(payload).Where(p => ReadString(p, "name", "").Length > 0)
                .OrderByDescending(p => ReadString(p, "name", "").Length))
                value = Regex.Replace(value, @"\b" + Regex.Escape(ReadString(profile, "name", "").ToLowerInvariant()) + @"\b", CharacterIdFrom(profile).ToLowerInvariant());
            return value;
        }

        private static List<Dictionary<string, object>> DialogueExplicitMeetingOrder(string text, Dictionary<string, object> payload,
            string actor, string addressee)
        {
            var identities = MergedInteractionParticipantProfiles(payload).Select(CharacterIdFrom).Append(actor).Append(addressee)
                .Where(s => s.Length > 0).Distinct().OrderByDescending(s => s.Length).Select(Regex.Escape).ToList();
            if (identities.Count == 0) return new();
            string entity = "(?:" + string.Join("|", identities) + ")";
            var result = new List<Dictionary<string, object>>();
            foreach (Match sentence in Regex.Matches(DialogueClaimSpeech(text), @"[^.!?\r\n]+(?:[.!?]+|$)"))
            {
                if (sentence.Value.TrimEnd().EndsWith("?") || DialogueClaimIsUncertain(sentence.Value)
                    || Regex.IsMatch(sentence.Value, @"\b(?:not|never|didn't|did not|years? ago|last year)\b", RegexOptions.IgnoreCase)) continue;
                string normalized = DialogueFactIdentityText(sentence.Value, payload, actor, addressee);
                foreach (Match match in Regex.Matches(normalized,
                    @"(?<actor>" + entity + @")\s+(?:first\s+)?met\s+(?<a>" + entity + @")\s+(?<order>before|after)\s+(?:(?:" + entity + @"|he|she)\s+)?(?:meeting\s+|met\s+)?(?<b>" + entity + @")\b", RegexOptions.IgnoreCase))
                {
                    string a = match.Groups["a"].Value, b = match.Groups["b"].Value;
                    if (a == b) continue;
                    string quote = sentence.Value.Trim();
                    result.Add(TestDict("actor", match.Groups["actor"].Value, "earlier", match.Groups["order"].Value == "before" ? a : b,
                        "later", match.Groups["order"].Value == "before" ? b : a, "quote", quote,
                        "start", sentence.Index + sentence.Value.IndexOf(quote, StringComparison.Ordinal), "length", quote.Length));
                }
            }
            return result;
        }

        private static List<Dictionary<string, object>> FindDialogueFactualContradictions(Dictionary<string, object> parsed,
            Dictionary<string, object> payload, List<Dictionary<string, object>> prior, string speaker)
        {
            string reply = ReadFirstString(parsed, "reply", "body", "response", "text");
            if (Regex.IsMatch(ReadString(parsed, "intent", ""), @"\b(?:deceive|deception|lie|lying|mislead|deliberate_deception)\b", RegexOptions.IgnoreCase)) return new();
            string player = FirstNonEmpty(ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"), "current_player");
            var targets = ReadStringList(parsed, "addressedHeroStringIds");
            string to = targets.Count == 1 ? targets[0] : GroupConversationParticipantIds(payload).Count > 2 ? "" : player;
            if (to.Length == 0) return new();
            var sources = DialogueClaimOriginalSources(payload, prior, speaker);
            var result = new List<Dictionary<string, object>>();
            foreach (var fact in DialogueExplicitMeetingOrder(reply, payload, speaker, to))
            {
                var orders = new List<Dictionary<string, object>>();
                foreach (var source in sources)
                {
                    string actor = ReadString(source, "speakerId", "");
                    var addressed = ReadStringList(source, "addressedIds");
                    string recipient = addressed.Count == 1 ? addressed[0] : GroupConversationParticipantIds(payload).Count > 2 ? "" : actor == speaker ? player : speaker;
                    if (recipient.Length == 0) continue;
                    foreach (var order in DialogueExplicitMeetingOrder(ReadString(source, "text", ""), payload, actor, recipient))
                        if (ReadString(order, "actor", "") == ReadString(fact, "actor", "")
                            && new[] { ReadString(order, "earlier", ""), ReadString(order, "later", "") }.Order().SequenceEqual(
                                new[] { ReadString(fact, "earlier", ""), ReadString(fact, "later", "") }.Order()))
                        { order["source"] = source; orders.Add(order); }
                }
                // Conflicting originals require an explicit resolution. Do not
                // choose whichever side happens to have the greatest word overlap.
                var current = orders.Where(o => ReadString(ReadDictionary(o, "source"), "id", "") == "current_player").ToList();
                var ownReports = orders.Where(o => ReadString(o, "actor", "") == ReadString(ReadDictionary(o, "source"), "speakerId", "")).ToList();
                var eligible = current.Count > 0 ? current : ownReports.Count > 0 ? ownReports : orders;
                if (eligible.Count == 0 || eligible.Select(o => ReadString(o, "earlier", "")).Distinct().Count() != 1
                    || ReadString(eligible[0], "earlier", "") == ReadString(fact, "earlier", "")) continue;
                var original = eligible.Last(); var sourceProof = ReadDictionary(original, "source");
                result.Add(TestDict("type", "source_proven_meeting_order_reversed", "field", "reply", "currentText", fact["quote"],
                    "currentStart", fact["start"], "currentLength", fact["length"], "sourceId", sourceProof["id"], "sourceHash", sourceProof["sourceHash"],
                    "originatingNpcLine", original["quote"], "sourceStart", original["start"], "sourceLength", original["length"],
                    "proof", "explicit_observed_same_actor_meeting_order", "evidenceKind", "original_spoken_statement", "sourceSpeakerId", sourceProof["speakerId"]));
            }
            result.AddRange(FindDialogueNativeSpouseContradictions(reply, payload, speaker, to));
            return result;
        }

        private static string BuildDialogueAcknowledgedCorrectionLedger(Dictionary<string, object> payload,
            List<Dictionary<string, object>> prior, string observer)
        {
            var sources = DialogueClaimOriginalSources(payload, prior, observer);
            string player = FirstNonEmpty(ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"), "current_player");
            var records = new List<Dictionary<string, object>>();
            for (int i = 0; i + 1 < sources.Count; i++)
            {
                var correction = sources[i]; var acknowledgment = sources[i + 1];
                if (ReadString(correction, "speakerId", "") != player || ReadString(acknowledgment, "speakerId", "") != observer) continue;
                string text = ReadString(correction, "text", ""), accepted = ReadString(acknowledgment, "text", "");
                if (text.Length > 2400 || !Regex.IsMatch(text,
                    @"\b(?:clarify|correction|actually|we met first|not what I said|you (?:are|have.{0,40}) wrong|I (?:did not|didn't) meet.{0,100}before|I met.{0,100}before)\b", RegexOptions.IgnoreCase)) continue;
                var ack = Regex.Match(DialogueClaimSpeech(accepted),
                    @"[^.!?\r\n]*\b(?:I was wrong|I got.{0,50}wrong|I misstated|I had.{0,50}wrong|I stand corrected|you are right|you're right)\b[^.!?\r\n]*[.!?]?", RegexOptions.IgnoreCase);
                if (!ack.Success) continue;
                records.Add(TestDict("correctionSourceId", correction["id"], "correctionSourceHash", correction["sourceHash"],
                    "speakerId", player, "quote", text, "acknowledgmentSourceId", acknowledgment["id"], "acknowledgmentSourceHash", acknowledgment["sourceHash"],
                    "acknowledgment", ack.Value.Trim(), "authority", "acknowledged_report_correction_not_native_result"));
            }
            if (records.Count == 0) return "";
            return "\n\nACKNOWLEDGED SOURCE CORRECTIONS\n" + Json.Serialize(records.TakeLast(3).ToList())
                + "\nThese later explanations supersede conflicting earlier NPC recollections about the same issue. Keep the earlier mistaken utterance as history of a mistake; "
                + "do not call its disputed detail something the player disclosed. Apply corrections only to their subject, preserve separate unanswered concerns, and do not infer native results or new facts from the acknowledgment.";
        }
    }
}
