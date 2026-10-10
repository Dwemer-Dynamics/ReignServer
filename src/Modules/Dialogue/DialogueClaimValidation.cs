using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string DialogueClaimSchema = "reign-dialogue-claim-validation-v1";
        private const string DialogueNliHash = "7105da41f625c42eca24e9465ec99150d02a80e644659d7a1daa93a6357155d4";
        private const string DialogueNliTokenizerHash = "5124ef2ead1a10a717703bc436de7f353da76d6340e4587719b42b1693707964";
        private const string DialogueGroundingHash = "0697ebdc2fab6c085b02f6ad97af8652160f751b3882dc530719c8a7bc58b775";
        private const string DialogueGroundingWeightsHash = "b9480694aa5f5a902833a99f6a91fe3f68e75ce6a27a80ac5ea48c3aa0c42cb2";
        private const string DialogueGroundingTokenizerHash = "8bfb874b90f3b3ea9481289bf351eef77462f0935f526ea107f53789708cc5d4";
        private static readonly AsyncLocal<Func<Dictionary<string, object>, Dictionary<string, object>>> DialogueClaimHelperReplay = new();
        private static readonly AsyncLocal<Func<Dictionary<string, object>, Dictionary<string, object>>> DialogueClaimExtractorReplay = new();
        private static long DialogueClaimHelperUnavailableUntil;
        private static long DialogueClaimExtractorUnavailableUntil;

        private sealed class RecalledDialogueClaim
        {
            internal string Quote, Clause, Subject, ClaimedActor, Canonical;
            internal string Field = "reply";
            internal string Kind = "spoken_recall";
            internal int WriteIndex = -1;
            internal Dictionary<string, object> Write;
            internal int Start, SubjectStart, SubjectLength;
            internal bool LiteralQuotation;
        }

        // Keep positions in the actual draft. Private/stage narration is not an
        // assertion that someone said something; caller supplies observer projections.
        private static string DialogueClaimSpeech(string text) => Regex.Replace(text ?? "", @"\*[^*]*\*", m => new string(' ', m.Length));

        private static string CanonicalDialogueClause(string text, string speaker, string addressee)
        {
            text = (text ?? "").Replace('’', '\'');
            text = Regex.Replace(text, @"\bI'm\b", "I am", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\byou're\b", "you are", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b(?:I|me|myself|mine)\b", speaker, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bmy\b", speaker + " possessive", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b(?:you|yourself|yours)\b", addressee, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\byour\b", addressee + " possessive", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"[^\p{L}\p{N}_]+", " ").Trim().ToLowerInvariant();
            return Regex.Replace(text, @"\s+", " ");
        }

        private static List<RecalledDialogueClaim> ExtractRecalledDialogueClaims(string text,
            Dictionary<string, object> payload, Dictionary<string, object> parsed, string speaker)
        {
            string player = FirstNonEmpty(ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"), "current_player");
            var names = MergedInteractionParticipantProfiles(payload).Where(p => CharacterIdFrom(p).Length > 0)
                .Select(p => (Name: ReadString(p, "name", ""), Id: CharacterIdFrom(p))).Where(p => p.Name.Length > 0).ToList();
            var participants = GroupConversationParticipantIds(payload).Concat(names.Select(n => n.Id)).Append(player).Append(speaker).Distinct().ToList();
            var targets = ReadStringList(parsed, "addressedHeroStringIds").Distinct().ToList();
            bool validTarget = targets.Count == 1 && participants.Contains(targets[0]) && targets[0] != speaker;
            bool ambiguousYou = GroupConversationParticipantIds(payload).Count > 2 && !validTarget;
            string addressee = validTarget ? targets[0] : player;
            string subjectPattern = "you|I" + (names.Count == 0 ? "" : "|" + string.Join("|", names.Select(n => Regex.Escape(n.Name))));
            var claims = new List<RecalledDialogueClaim>();
            // Declared deception is not permission for an action or a durable
            // false fact. It does, however, make the spoken assertion unsuitable
            // for an automatic factual-ownership correction.
            if (Regex.IsMatch(ReadString(parsed, "intent", ""), @"\b(?:deceive|deception|lie|lying|mislead|deliberate_deception)\b", RegexOptions.IgnoreCase)) return claims;
            string spoken = DialogueClaimSpeech(text);
            foreach (Match sentence in Regex.Matches(spoken, @"[^.!?\r\n]+(?:[.!?]+|$)"))
            {
                if (sentence.Value.TrimEnd().EndsWith("?", StringComparison.Ordinal)) continue;
                var match = Regex.Match(sentence.Value, @"\b(?<subject>" + subjectPattern
                    + @")\s+(?:said|told\s+(?:me|you|us)|asked(?:\s+(?:me|you|us))?)\s+(?:that\s+|to\s+)?(?<clause>[^.!?\r\n]+)", RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                string prefix = sentence.Value.Substring(0, match.Index);
                if (Regex.IsMatch(prefix, @"\b(?:if|perhaps|maybe|imagine|suppose|whether|think|thought|believe|suspect|remember\s+wrong|not\s+true|never|did\s+not|didn't|he\s+said|she\s+said|they\s+said)\b|[""“]", RegexOptions.IgnoreCase)) continue;
                if (Regex.IsMatch(prefix, @"\b(?:yesterday|today|tomorrow|last\s+(?:night|week|month|year)|years?\s+ago)\b", RegexOptions.IgnoreCase)) continue;
                // The character may openly describe a lie or an uncertain impression.
                if (Regex.IsMatch(sentence.Value, @"\b(?:lying|lie\s+about|pretend|deliberately\s+mislead)\b", RegexOptions.IgnoreCase)) continue;
                string subject = match.Groups["subject"].Value;
                var namedActors = names.Where(n => n.Name.Equals(subject, StringComparison.OrdinalIgnoreCase)).Select(n => n.Id).Distinct().ToList();
                string actor = subject.Equals("I", StringComparison.OrdinalIgnoreCase) ? speaker
                    : subject.Equals("you", StringComparison.OrdinalIgnoreCase) ? ambiguousYou ? "" : addressee
                    : namedActors.Count == 1 ? namedActors[0] : "";
                string clause = match.Groups["clause"].Value.Trim().Trim('"', '“', '”');
                bool literal = match.Groups["clause"].Value.TrimStart().StartsWith("\"", StringComparison.Ordinal)
                    || match.Groups["clause"].Value.TrimStart().StartsWith("“", StringComparison.Ordinal);
                // Inside a literal quotation, I/me belong to the quoted speaker.
                // Group quote recipients need their own evidence; a draft's
                // current audience is not proof of the original quote audience.
                if (literal && GroupConversationParticipantIds(payload).Count > 2
                    && Regex.IsMatch(clause, @"\b(?:you|your|yours|yourself)\b", RegexOptions.IgnoreCase)) continue;
                string canonical = CanonicalDialogueClause(clause, literal ? actor : speaker,
                    literal && actor != speaker ? speaker : addressee);
                if (canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 3 || canonical.Length < 12) continue;
                claims.Add(new RecalledDialogueClaim { Quote = match.Value.Trim(), Clause = clause, Subject = subject,
                    ClaimedActor = actor, Canonical = canonical, Start = sentence.Index + match.Index,
                    LiteralQuotation = literal,
                    SubjectStart = sentence.Index + match.Groups["subject"].Index, SubjectLength = subject.Length });
                if (claims.Count == 8) break;
            }
            return claims;
        }

        private static List<Dictionary<string, object>> DialogueClaimOriginalSources(Dictionary<string, object> payload,
            List<Dictionary<string, object>> prior, string observer)
        {
            var recent = ProjectConversationLinesForObserver(prior ?? new(), observer, payload);
            recent.AddRange(ReadDictionaryList(payload, "memoryEvidenceRecentTurns"));
            string player = FirstNonEmpty(ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"), "current_player");
            string input = ReadFirstString(payload, "playerText", "text", "message");
            // Current input is an original contribution, even though the accepted
            // exchange has not been stored yet. A correspondence trigger is not
            // necessarily player-authored speech; use its actual letter sources.
            if (input.Length > 0 && ReadString(payload, "interactionMode", "") != "correspondence")
            {
                string privateAudience = ReadString(payload, "privateAudienceHeroStringId", "");
                var addressed = privateAudience.Length > 0 ? new List<string> { privateAudience }
                    : GroupConversationParticipantIds(payload).Count <= 2 ? new List<string> { observer } : new List<string>();
                recent.AddRange(ProjectConversationLinesForObserver(new List<Dictionary<string, object>> {
                    TestDict("id", "current_player", "speakerId", player, "role", "player", "text", input, "addressedIds", addressed)
                }, observer, payload));
            }
            var sources = new List<Dictionary<string, object>>();
            foreach (var record in ReadDictionaryList(ReadDictionary(payload, "memoryPrecisionEvidence"), "records"))
                sources.AddRange(ReadDictionaryList(record, "sourceTurns"));
            // Recent originals survive a large retrieved bundle. No blind tail of
            // the combined list may discard the preceding question or answer.
            sources = recent.TakeLast(30).Concat(sources.Take(30)).ToList();
            var result = new List<Dictionary<string, object>>();
            double day = ReadDouble(payload, "worldDay", double.NaN);
            long sequence = ReadLong(payload, "asOfAcceptedSequence", long.MaxValue);
            long timestamp = ReadLong(payload, "asOfTimestamp", long.MaxValue);
            foreach (var source in sources)
            {
                string actor = ReadFirstString(source, "speakerHeroStringId", "speakerId", "speaker_id", "heroStringId");
                string text = ReadFirstString(source, "text", "reply", "content");
                if (actor.Length == 0 || text.Length == 0 || text.Length > 32000) continue;
                // Knowing one's own thought is not evidence of having said it.
                if (ReadBool(source, "privateMental", false)
                    || new[] { "subjective_belief", "hypothesis", "future_intention" }.Contains(ReadString(source, "evidenceType", ""))) continue;
                if (double.IsFinite(day) && ReadDouble(source, "worldDay", day) > day) continue;
                if (ReadLong(source, "acceptedSequence", 0) > sequence || ReadLong(source, "timestamp", ReadLong(source, "ts", 0)) > timestamp) continue;
                string visible = ObserverConversationText(text, actor, observer, ReadString(source, "role", "") == "player");
                string id = FirstNonEmpty(ReadFirstString(source, "turnId", "id", "sourceId", "exchangeId"), "prior_" + PromptHash(actor + "|" + visible));
                result.Add(TestDict("id", id, "speakerId", actor, "text", visible,
                    "sourceHash", MemorySelectionTextHash(visible).ToLowerInvariant(), "addressedIds", ReadStringList(source, "addressedIds"),
                    "worldDay", ReadDouble(source, "worldDay", double.NaN), "acceptedSequence", ReadLong(source, "acceptedSequence", 0),
                    "timestamp", ReadLong(source, "timestamp", ReadLong(source, "ts", 0)),
                    "role", ReadString(source, "role", ""), "exchangeId", ReadFirstString(source, "exchangeId", "exchange_id"),
                    "conversationId", ReadFirstString(source, "conversationId", "conversation_id", "sessionId"),
                    "replyToTurnId", ReadFirstString(source, "replyToTurnId", "questionTurnId", "inReplyToTurnId")));
            }
            return result.GroupBy(s => ReadString(s, "id", "") + "|" + ReadString(s, "sourceHash", ""), StringComparer.Ordinal).Select(g => g.First()).ToList();
        }

        private static void AttachDialogueClaimSpanCoverage(Dictionary<string, object> write,
            List<Dictionary<string, object>> sources, string owner, string eventId)
        {
            var bound = ReadDictionary(write, "sourceEvidence");
            var spans = new List<Dictionary<string, object>>();
            if (ReadString(bound, "status", "") == "supported_span") spans.Add(bound);
            foreach (var candidate in ReadDictionaryList(write, "sourceEvidenceSpans").Take(8))
            {
                var extra = BindConversationSource(TestDict("sourceEvidence", candidate), sources, owner, eventId);
                if (ReadString(extra, "status", "") == "supported_span") spans.Add(extra);
            }
            spans = spans.GroupBy(s => ReadString(s, "turnId", "") + "|" + ReadInt(s, "start", -1) + "|" + ReadInt(s, "length", 0)).Select(g => g.First()).ToList();
            write["sourceEvidenceSpans"] = spans;
            var actors = ReadStringList(bound, "claimedActorIds");
            string summary = ReadFirstString(write, "text", "summary", "claim", "description");
            bool compound = actors.Distinct().Count() > 1 && Regex.IsMatch(summary, @"\b(?:said|asked|answered|agreed|replied|acknowledged)\b.{1,160}\band\b.{1,160}\b(?:said|asked|answered|agreed|replied|acknowledged)\b", RegexOptions.IgnoreCase)
                && spans.Select(s => ReadString(s, "speakerId", "")).Distinct().Count() < 2;
            write["claimSourceCoverage"] = TestDict("schema", DialogueClaimSchema, "supportedSpanCount", spans.Count,
                "claimEntailmentVerified", false, "status", compound ? "compound_claims_need_more_evidence" : "quoted_spans_only");
            if (compound)
            {
                write["sourceValidation"] = "incomplete";
                if (ReadString(write, "sourceValidationReason", "").Length == 0)
                    write["sourceValidationReason"] = "compound_actor_claims_need_additional_spans";
                write["originalSources"] = sources;
            }
        }

        private static List<Dictionary<string, object>> FindRecalledSpeakerInversions(Dictionary<string, object> parsed,
            Dictionary<string, object> payload, List<Dictionary<string, object>> prior, string speaker)
        {
            string reply = ReadFirstString(parsed, "reply", "body", "response", "text");
            string player = FirstNonEmpty(ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"), "current_player");
            var sources = DialogueClaimOriginalSources(payload, prior, speaker);
            var violations = new List<Dictionary<string, object>>();
            foreach (var claim in ExtractRecalledDialogueClaims(reply, payload, parsed, speaker))
            {
                if (claim.ClaimedActor.Length == 0) continue;
                var matches = new List<Dictionary<string, object>>();
                bool currentInputMayReferToSameClause = false;
                foreach (var source in sources)
                {
                    string actor = ReadString(source, "speakerId", "");
                    var addressed = ReadStringList(source, "addressedIds");
                    // A current quotation, denial or vocative-prefixed instruction
                    // can defeat exclusive ownership without proving ownership
                    // itself. Abstain rather than changing correct current recall.
                    if (ReadString(source, "id", "") == "current_player")
                    {
                        string currentClause = CanonicalDialogueClause(DialogueClaimSpeech(ReadString(source, "text", "")), actor, speaker);
                        currentInputMayReferToSameClause |= (" " + currentClause + " ").Contains(" " + claim.Canonical + " ", StringComparison.Ordinal);
                    }
                    if (GroupConversationParticipantIds(payload).Count > 2 && addressed.Count != 1) continue;
                    string to = addressed.Count == 1 ? addressed[0] : actor == speaker ? player : speaker;
                    foreach (Match line in Regex.Matches(DialogueClaimSpeech(ReadString(source, "text", "")), @"[^.!?\r\n]+(?:[.!?]+|$)"))
                    {
                        if (line.Value.TrimEnd().EndsWith("?", StringComparison.Ordinal)) continue;
                        // A hypothetical, denial or report of someone else's speech
                        // cannot prove ownership, even with exactly matching words.
                        if (Regex.IsMatch(line.Value, @"\b(?:if|imagine|suppose|perhaps|maybe|said|told|asked|never\s+said|didn't\s+say|did\s+not\s+say)\b|[""“]", RegexOptions.IgnoreCase)) continue;
                        string canonical = CanonicalDialogueClause(line.Value, actor, to);
                        canonical = Regex.Replace(canonical, @"^(?:(?:now|please)\s+)+", "");
                        // Embedded wording is not an owned instruction. A denial,
                        // conditional intention or acknowledgement may contain the
                        // same phrase without proving who originally requested it.
                        const string instructions = "stop|kiss|keep|stay|come|go|leave|wait|listen|tell|show|hold|let|bring|send|write|visit|meet|remember|forget|take|give|promise";
                        bool instruction = Regex.IsMatch(canonical, @"^(?:" + instructions + @")\b");
                        var roots = instruction ? Regex.Split(canonical, @"\s+and\s+(?=(?:" + instructions + @")\b)") : new[] { canonical };
                        bool owned = instruction && roots.Any(root => root == claim.Canonical
                            || root.StartsWith(claim.Canonical + " ", StringComparison.Ordinal));
                        owned |= claim.LiteralQuotation && canonical == claim.Canonical;
                        string question = Regex.Replace(canonical, @"^the question (?:is|was)\s+", "");
                        owned |= question != canonical && Regex.IsMatch(claim.Canonical, @"^(?:what|why|how|whether|where|when)\b")
                            && (question == claim.Canonical || question.StartsWith(claim.Canonical + " ", StringComparison.Ordinal));
                        if (!owned) continue;
                        matches.Add(TestDict("sourceId", source["id"], "speakerId", actor, "sourceHash", source["sourceHash"],
                            "quote", line.Value.Trim(), "sourceStart", line.Index, "sourceLength", line.Length));
                    }
                }
                var owners = matches.Select(m => ReadString(m, "speakerId", "")).Distinct(StringComparer.Ordinal).ToList();
                if (currentInputMayReferToSameClause || owners.Count != 1 || owners[0] == claim.ClaimedActor) continue;
                var original = matches.Last();
                violations.Add(TestDict("type", "recalled_speaker_inverted", "field", "reply", "currentText", claim.Quote,
                    "currentStart", claim.Start, "currentLength", claim.Quote.Length,
                    "subjectStart", claim.SubjectStart, "subjectLength", claim.SubjectLength, "subject", claim.Subject,
                    "sourceId", original["sourceId"], "sourceHash", original["sourceHash"],
                    "originatingNpcLine", original["quote"], "sourceStart", original["sourceStart"], "sourceLength", original["sourceLength"],
                    "originalRequestOwner", owners[0], "claimedRequestOwner", claim.ClaimedActor,
                    "evidenceKind", "original_spoken_statement", "proof", claim.LiteralQuotation
                        ? "exclusive_observed_exact_quotation" : "exclusive_observed_instruction_clause"));
            }
            return violations;
        }

        private static List<Dictionary<string, object>> QuarantineDialogueClaimEffects(Dictionary<string, object> parsed,
            Dictionary<string, object> payload, List<Dictionary<string, object>> prior, string speaker)
        {
            var withheld = new List<Dictionary<string, object>>();
            foreach (string field in new[] { "memoryWrites", "beliefWrites", "comprehensionWrites", "continuityWrites", "obligationWrites", "dynamicCharacteristicWrites", "relationshipAssessments" })
            {
                if (!parsed.ContainsKey(field)) continue;
                var kept = new List<Dictionary<string, object>>();
                int index = 0;
                foreach (var item in ReadDictionaryList(parsed, field))
                {
                    string text = ReadFirstString(item, "text", "summary", "claim", "description", "reason", "rationale");
                    bool subjective = new[] { "subjective_belief", "hypothesis", "future_intention" }.Contains(ReadString(item, "evidenceType", ReadString(ReadDictionary(item, "sourceEvidence"), "evidenceType", "")));
                    var errors = subjective ? new List<Dictionary<string, object>>() : FindRecalledSpeakerInversions(TestDict("reply", text), payload, prior, speaker)
                        .Concat(FindDialogueFactualContradictions(TestDict("reply", text), payload, prior, speaker)).ToList();
                    if (errors.Count == 0) kept.Add(item);
                    else withheld.Add(TestDict("field", field, "index", index, "proposed", item, "status", "withheld", "reason", "source_proven_factual_error", "evidence", errors));
                    index++;
                }
                parsed[field] = kept;
            }
            return withheld;
        }

        private static bool TryCorrectExactDialogueOwnership(Dictionary<string, object> parsed, Dictionary<string, object> payload,
            List<Dictionary<string, object>> prior, string speaker, List<Dictionary<string, object>> violations,
            out Dictionary<string, object> corrected)
        {
            corrected = null;
            if (violations.Count == 0 || violations.Any(v => ReadString(v, "type", "") != "recalled_speaker_inverted")) return false;
            // Changing the subject of "you told/asked me" would create "I told
            // myself" and leave the recipient inverted. Delegate these to the
            // bounded writer repair rather than pretending one word is enough.
            if (violations.Any(v => !Regex.IsMatch(ReadString(v, "currentText", ""), @"^(?:I|You)\s+(?:said|asked(?!\s+(?:me|you|us)\b))\b", RegexOptions.IgnoreCase))) return false;
            string field = new[] { "reply", "body", "response", "text" }.FirstOrDefault(parsed.ContainsKey) ?? "reply";
            string visible = ReadString(parsed, field, "");
            string player = FirstNonEmpty(ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"), "current_player");
            foreach (var violation in violations.OrderByDescending(v => ReadInt(v, "subjectStart", -1)))
            {
                string owner = ReadString(violation, "originalRequestOwner", "");
                string replacement = owner == speaker ? "I" : owner == player && GroupConversationParticipantIds(payload).Count <= 2 ? "You" : "";
                int start = ReadInt(violation, "subjectStart", -1), length = ReadInt(violation, "subjectLength", 0);
                if (replacement.Length == 0 || start < 0 || start + length > visible.Length
                    || visible.Substring(start, length) != ReadString(violation, "subject", "")
                    || !new[] { "you", "i" }.Contains(visible.Substring(start, length).ToLowerInvariant())) return false;
                visible = visible.Remove(start, length).Insert(start, replacement);
            }
            corrected = MergeDialogueIntegrityProse(parsed, TestDict("reply", visible), violations);
            return corrected != null && FindRecalledSpeakerInversions(corrected, payload, prior, speaker).Count == 0;
        }

        private static Dictionary<string, object> CompareDialogueRecallClaims(Dictionary<string, object> parsed,
            Dictionary<string, object> payload, List<Dictionary<string, object>> prior, string speaker,
            Dictionary<string, object> settings = null)
        {
            var clock = Stopwatch.StartNew();
            string reply = ReadFirstString(parsed, "reply", "body", "response", "text");
            var plan = BuildDialogueClaimPlan(parsed, payload, prior, speaker);
            var pairs = ReadDictionaryList(plan, "pairs");
            var claims = ReadDictionaryList(plan, "claims");
            settings ??= LoadSettings();
            int defaultBudget = Math.Clamp(1500 + pairs.Sum(p => ReadString(p, "premise", "").Length + ReadString(p, "hypothesis", "").Length) * 2, 8000, 15000);
            int workBudget = Math.Clamp(ReadInt(settings, "dialogueClaimWorkBudgetMs", defaultBudget), 500, 15000);
            int callerBudget = workBudget + 2000;
            var receipt = TestDict("schema", DialogueClaimSchema, "claimCount", plan["claimCount"], "comparisonCount", pairs.Count,
                "unresolvedAddressees", plan["unresolvedAddressees"], "remoteCalls", 0,
                "draftReplyHash", MemorySelectionTextHash(reply).ToLowerInvariant(), "sourceCount", plan["sourceCount"],
                "authority", "comparison_hints_only", "coverage", "atomic_facts_with_explicit_gaps", "uncomparedClaims", plan["uncomparedClaims"],
                "claimCoverage", claims, "workBudgetMs", workBudget, "callerBudgetMs", callerBudget,
                "budgetPolicy", settings.ContainsKey("dialogueClaimWorkBudgetMs") ? "configured" : "bounded_content_allowance",
                "coverageStage", "proposed_response_before_effect_quarantine", "claimExtractionScope", "bounded_declarative_speech_and_factual_writes",
                "excludedNarrationSpans", Regex.Matches(reply, @"\*[^*]*\*").Cast<Match>().Select(m => TestDict("start", m.Index, "length", m.Length,
                    "textHash", MemorySelectionTextHash(m.Value).ToLowerInvariant(), "status", "not_a_spoken_claim_not_semantically_certified")).ToList(),
                "semanticAccuracyCertified", false, "scoredClaims", 0, "unresolvedClaims", claims.Count,
                "status", pairs.Count == 0 ? "no_comparable_claims" : "pending");
            receipt["extraction"] = ExtractDialogueClaimCandidates(reply, settings);
            if (pairs.Count == 0) return receipt;
            if (DialogueClaimHelperReplay.Value == null && (!string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)
                || Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE") == "1"
                || ReadBool(settings, "validationMode", false))) { receipt["status"] = "isolated_no_capture"; return receipt; }
            if (DialogueClaimHelperReplay.Value == null && Interlocked.Read(ref DialogueClaimHelperUnavailableUntil) > DateTime.UtcNow.Ticks)
            { receipt["status"] = "unavailable_backoff"; return receipt; }
            try
            {
                settings ??= LoadSettings();
                var request = TestDict("schema", DialogueClaimSchema, "pairs", pairs, "workBudgetMs", workBudget);
                var response = DialogueClaimHelperReplay.Value?.Invoke(request) ?? TryParseJsonObject(PostJsonToUrl(
                    SemanticWorkerUrl(settings, "/dialogue/claims/compare"), Json.Serialize(request), callerBudget));
                var rows = ReadDictionaryList(response, "rows");
                bool grounding = ReadString(response, "modelHash", "") == DialogueGroundingHash;
                string[] scoreLabels = grounding ? new[] { "unsupported", "supported" } : new[] { "contradiction", "entailment", "neutral" };
                bool valid = ReadBool(response, "ok", false) && ReadString(response, "schema", "") == DialogueClaimSchema
                    && (grounding ? ReadString(response, "tokenizerHash", "") == DialogueGroundingTokenizerHash
                        && ReadString(response, "weightsHash", "") == DialogueGroundingWeightsHash
                        && ReadString(response, "scoringKind", "") == "grounding_binary"
                        : ReadString(response, "modelHash", "") == DialogueNliHash && ReadString(response, "tokenizerHash", "") == DialogueNliTokenizerHash
                        && new[] { "", "nli_three_way" }.Contains(ReadString(response, "scoringKind", "")))
                    && ReadString(response, "backend", "") == "cpu" && ReadInt(response, "remoteCalls", -1) == 0
                    && rows.Count == pairs.Count && rows.Select(r => ReadString(r, "id", "")).Distinct().Count() == pairs.Count;
                foreach (var pair in pairs)
                {
                    var row = rows.SingleOrDefault(r => ReadString(r, "id", "") == ReadString(pair, "id", ""));
                    if (row == null || new[] { "sourceId", "sourceHash", "claimHash", "premiseHash", "hypothesisHash" }.Any(k => ReadString(row, k, "") != ReadString(pair, k, ""))) { valid = false; break; }
                    if (ReadString(row, "status", "") == "scored")
                    {
                        var scores = ReadDictionary(row, "scores");
                        valid &= scores.Count == scoreLabels.Length && scoreLabels.All(k => double.IsFinite(ReadDouble(scores, k, double.NaN)) && ReadDouble(scores, k, -1) >= 0 && ReadDouble(scores, k, 2) <= 1);
                        valid &= Math.Abs(scoreLabels.Sum(k => ReadDouble(scores, k, 0)) - 1) <= .001;
                        string label = ReadString(row, "label", "");
                        string outcome = grounding ? ReadDouble(scores, "supported", 0) >= .9 ? "supported" : ReadDouble(scores, "unsupported", 0) >= .9 ? "unsupported" : "unresolved"
                            : ReadDouble(scores, "entailment", 0) >= .9 ? "supported" : ReadDouble(scores, "contradiction", 0) >= .9 ? "contradicted" : "unresolved";
                        valid &= scoreLabels.Contains(label)
                            && ReadDouble(scores, label, -1) >= scoreLabels.Max(k => ReadDouble(scores, k, 0)) - .00001
                            && ReadString(row, "outcome", "") == outcome && ReadString(row, "coverage", "") == "complete_pair"
                            && ReadInt(row, "inputTokens", 0) > 0 && ReadInt(row, "inputTokens", 513) <= (grounding ? 256 : 512);
                    }
                    else valid &= new[] { "token_limit", "time_budget", "late_scored" }.Contains(ReadString(row, "status", "")) && ReadString(row, "outcome", "") == "unresolved";
                    valid &= ReadString(row, "authority", "") == "comparison_hint_only";
                }
                foreach (var claim in claims)
                {
                    var row = valid ? rows.SingleOrDefault(r => ReadString(r, "id", "") == ReadString(claim, "pairId", "")) : null;
                    if (row != null) { claim["status"] = ReadString(row, "status", "unresolved"); claim["outcome"] = ReadString(row, "outcome", "unresolved"); }
                    else if (claim.ContainsKey("pairId")) claim["status"] = "helper_unverified";
                }
                receipt["scoredClaims"] = claims.Count(c => ReadString(c, "status", "") == "scored");
                receipt["supportedClaims"] = claims.Count(c => ReadString(c, "status", "") == "scored" && ReadString(c, "outcome", "") == "supported");
                receipt["unsupportedHints"] = claims.Count(c => ReadString(c, "status", "") == "scored" && new[] { "unsupported", "contradicted" }.Contains(ReadString(c, "outcome", "")));
                receipt["unresolvedClaims"] = claims.Count(c => ReadString(c, "outcome", "unresolved") == "unresolved" || ReadString(c, "status", "") != "scored");
                if (valid)
                    receipt["workerTiming"] = TestDict("status", response.ContainsKey("loadDurationMs") && response.ContainsKey("coldLoad") ? "reported" : "not_reported",
                        "durationMs", ReadLong(response, "durationMs", -1), "loadDurationMs", ReadLong(response, "loadDurationMs", -1),
                        "coldLoad", response.ContainsKey("coldLoad") ? (object)ReadBool(response, "coldLoad", false) : null,
                        "workBudgetMs", ReadInt(response, "workBudgetMs", -1));
                receipt["status"] = valid ? rows.All(r => ReadString(r, "status", "") == "scored") && ReadInt(plan, "uncomparedClaims", 0) == 0 ? "compared" : "partial" : "unsupported_helper_response";
                receipt["comparisons"] = valid ? rows : new();
                receipt["repairEvidence"] = valid ? pairs.Where(pair => rows.Any(row =>
                        ReadString(row, "id", "") == ReadString(pair, "id", "") && ReadString(row, "status", "") == "scored"))
                    .Select(pair => TestDict("sourceId", pair["sourceId"], "sourceHash", pair["sourceHash"],
                        "claimHash", pair["claimHash"], "field", pair["field"], "premise", pair["premise"], "hypothesis", pair["hypothesis"],
                        "premiseHash", pair["premiseHash"], "hypothesisHash", pair["hypothesisHash"],
                        "comparison", CloneDictionary(rows.Single(row => ReadString(row, "id", "") == ReadString(pair, "id", ""))),
                        "identityAliases", plan["identityAliases"])).ToList()
                    : new List<Dictionary<string, object>>();
                receipt["modelHash"] = valid ? ReadString(response, "modelHash", "") : "";
                receipt["scoringKind"] = valid ? grounding ? "grounding_binary" : "nli_three_way" : "unverified";
                // Even a valid contradiction score cannot establish actors, actions
                // or a correction. Exact local source proof remains authoritative.
                receipt["semanticAccuracyCertified"] = false;
                if (!valid) Interlocked.Exchange(ref DialogueClaimHelperUnavailableUntil, DateTime.UtcNow.AddSeconds(30).Ticks);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) && !(ex is CampaignRequestReplacedException))
            {
                receipt["status"] = "helper_unavailable"; receipt["errorType"] = ex.GetType().Name;
                Interlocked.Exchange(ref DialogueClaimHelperUnavailableUntil, DateTime.UtcNow.AddSeconds(30).Ticks);
            }
            receipt["durationMs"] = clock.ElapsedMilliseconds;
            return receipt;
        }

        private static string BuildDialogueClaimRepairGuidance(Dictionary<string, object> receipt,
            List<Dictionary<string, object>> violations)
        {
            if (!new[] { "compared", "partial" }.Contains(ReadString(receipt, "status", ""))) return string.Empty;
            var evidence = ReadDictionaryList(receipt, "repairEvidence").Take(8).Where(item =>
                ReadString(item, "field", "") == "reply" && (violations ?? new()).Any(violation =>
                    ReadString(violation, "type", "") == "recalled_speaker_inverted"
                    && ReadString(item, "sourceId", "") == ReadString(violation, "sourceId", "")
                    && ReadString(item, "sourceHash", "") == ReadString(violation, "sourceHash", "")
                    && ReadString(item, "claimHash", "") == MemorySelectionTextHash(ReadString(violation, "currentText", "")).ToLowerInvariant()))
                .ToList();
            receipt["repairGuidanceCount"] = evidence.Count;
            if (evidence.Count == 0) return string.Empty;
            return "\n\nLOCAL SOURCE-BOUND COMPARISON GUIDANCE\n" + Json.Serialize(TestDict(
                "modelHash", ReadString(receipt, "modelHash", ""), "authority", "comparison_hints_only", "evidence", evidence))
                + "\nUse these complete comparisons to check the supplied source-proven errors. Identity aliases refer to the listed hero IDs. "
                + "The original clauses and agency ledger remain authoritative. Unsupported is not proof of a false statement; "
                + "uncertain or contrary scores must not override source proof or expand the repair to other claims. Preserve independent valid content and metadata.";
        }

        private static Dictionary<string, object> ExtractDialogueClaimCandidates(string text, Dictionary<string, object> settings)
        {
            var receipt = TestDict("status", "not_configured", "authority", "claim_candidates_only", "remoteCalls", 0,
                "coverage", "none", "candidateCount", 0);
            string qualified;
            try { settings ??= LoadSettings(); qualified = QualifiedMemoryExtractorHash(settings); }
            catch (Exception ex) when (!(ex is OperationCanceledException) && !(ex is CampaignRequestReplacedException))
            { receipt["status"] = "configuration_unavailable"; receipt["errorType"] = ex.GetType().Name; return receipt; }
            if (qualified.Length == 0 && DialogueClaimExtractorReplay.Value == null) return receipt;
            if (text.Length == 0 || text.Length > 2400) { receipt["status"] = "input_limit"; return receipt; }
            if (DialogueClaimExtractorReplay.Value == null && (!string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)
                || Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE") == "1" || ReadBool(settings, "validationMode", false)))
            { receipt["status"] = "isolated_no_capture"; return receipt; }
            if (DialogueClaimExtractorReplay.Value == null && Interlocked.Read(ref DialogueClaimExtractorUnavailableUntil) > DateTime.UtcNow.Ticks)
            { receipt["status"] = "unavailable_backoff"; return receipt; }
            try
            {
                string hash = MemorySelectionTextHash(text).ToLowerInvariant();
                var request = TestDict("sourceId", "draft_" + hash, "text", text, "textHash", hash);
                var result = DialogueClaimExtractorReplay.Value?.Invoke(request) ?? TryParseJsonObject(PostJsonToUrl(
                    SemanticWorkerUrl(settings, "/dialogue/claims/extract"), Json.Serialize(request), 2000));
                var claims = ReadDictionaryList(result, "claims");
                bool valid = ReadBool(result, "ok", false) && ReadString(result, "schema", "") == "reign-dialogue-claim-extraction-v1"
                    && ReadString(result, "sourceId", "") == ReadString(request, "sourceId", "") && ReadString(result, "textHash", "") == hash
                    && ReadString(result, "modelHash", "") == qualified && ReadInt(result, "remoteCalls", -1) == 0
                    && ReadString(result, "offsetEncoding", "") == "utf16" && ReadString(result, "authority", "") == "claim_candidates_only"
                    && claims.Count <= 8;
                foreach (var claim in claims)
                    foreach (string name in new[] { "speaker", "statement" })
                    {
                        var span = ReadDictionary(claim, name);
                        int start = ReadInt(span, "start", -1), end = ReadInt(span, "end", -1);
                        double confidence = ReadDouble(span, "confidence", double.NaN);
                        valid &= start >= 0 && end > start && end <= text.Length && end - start <= 2400
                            && double.IsFinite(confidence) && confidence >= .5 && confidence <= 1;
                        if (start >= 0 && end > start && end <= text.Length)
                            valid &= text.Substring(start, end - start) == ReadString(span, "text", "")
                                && !char.IsLowSurrogate(text[start]) && (end == text.Length || !char.IsLowSurrogate(text[end]));
                    }
                receipt["status"] = valid ? "source_bound_candidates" : "unsupported_helper_response";
                receipt["coverage"] = valid ? "bounded_input_only" : "none";
                receipt["candidateCount"] = valid ? claims.Count : 0;
                receipt["candidates"] = valid ? claims : new();
                receipt["modelHash"] = qualified;
                receipt["durationMs"] = ReadLong(result, "durationMs", 0);
                // Extraction is observable for qualification. It cannot assign
                // canonical actors or introduce corrections without local proof.
                if (!valid) Interlocked.Exchange(ref DialogueClaimExtractorUnavailableUntil, DateTime.UtcNow.AddSeconds(30).Ticks);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) && !(ex is CampaignRequestReplacedException))
            {
                receipt["status"] = "helper_unavailable"; receipt["errorType"] = ex.GetType().Name;
                Interlocked.Exchange(ref DialogueClaimExtractorUnavailableUntil, DateTime.UtcNow.AddSeconds(30).Ticks);
            }
            return receipt;
        }
    }
}
