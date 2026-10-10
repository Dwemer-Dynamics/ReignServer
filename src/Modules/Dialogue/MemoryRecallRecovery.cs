using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string MemoryRecallRankingPolicy = "observer_asof_clause_rarity_bounded_original_pool_v5";
        private const int MemoryRecallLexicalLimit = 24;
        private const int MemoryRecallSupplementLimit = 8;
        private static IEnumerable<string> DeliveredRecallWriteFields => DialogueFactualWriteFields
            .Concat(new[] { "stateUpdates", "relationshipAssessments", "relationshipUpdates", "suggestedActions" })
            .Distinct(StringComparer.Ordinal).ToArray();

        private sealed class DeliveredMemoryRecallEvidence
        {
            public string Signature;
            public string ObserverName;
            public List<Dictionary<string, object>> Originals = new();
            public string ReviewedResponseHash = "";
        }
        private static readonly ConditionalWeakTable<Dictionary<string, object>, Dictionary<string, DeliveredMemoryRecallEvidence>> DeliveredMemoryRecall = new();

        private static string MemoryRecallTurnSignature(Dictionary<string, object> payload, string observer) => PromptHash(CanonicalJson(TestDict(
            "observer", observer, "timeline", ContinuityTimeline(payload), "worldDay", ReadDouble(payload, "worldDay", 0),
            "turn", ReadFirstString(payload, "sceneTurnId", "turnId", "requestId"), "query", ReadFirstString(payload, "playerText", "text", "message"))));

        // Expected records come from the server-only allocation capture. A
        // client/model field cannot supply this receipt. Intersect with final
        // message content so selection alone never proves actual delivery.
        private static Dictionary<string, object> BindDeliveredMemoryRecallEvidence(Dictionary<string, object> payload,
            string observer, List<Dictionary<string, object>> messages, string observerName = "",
            List<Dictionary<string, object>> canonicalLines = null, Func<string, string> identityProjection = null)
        {
            var receipt = new DeliveredMemoryRecallEvidence { Signature = MemoryRecallTurnSignature(payload, observer), ObserverName = observerName ?? "" };
            string final = NormalizePromptSegment(string.Join("\n", messages.Select(m => ReadString(m, "content", ""))));
            var query = MemoryRecallTerms(ReadFirstString(payload, "playerText", "text", "message"));
            foreach (var expected in CapturedRequiredPromptEvidence(payload))
            {
                string raw = ReadString(expected, "text", "");
                // Apply the same trusted name projection as the actual messages.
                // Keep the server capture intact and never adopt rendered text as
                // expected evidence, which could bless an arbitrary omission.
                string text = NormalizePromptSegment(identityProjection == null ? raw : identityProjection(raw));
                bool canonical = ReadString(expected, "section", "") == "canonicalTranscript"
                    && ReadString(expected, "kind", "") == "accepted_exchange";
                if ((!canonical && !new[] { "targeted_historical_exchange", "reported_exchange", "resolved_sequence" }.Contains(ReadString(expected, "kind", "")))
                    || text.Length == 0 || !final.Contains(text, StringComparison.Ordinal)) continue;
                var record = ContinuityPromptRecords(text, false).FirstOrDefault();
                if (record == null || (canonical ? record.Observer != "current_observer" : record.Observer != observer)) continue;
                var terms = MemoryRecallTerms(text);
                int overlap = query.Intersect(terms).Count();
                // Retrieval already selected and protected this complete source.
                // Query-term sampling can miss the middle of a long record, and
                // paired context need not repeat the query's names. Relevance may
                // order delivery proof, but must never discard delivered evidence.
                var original = TestDict("id", ReadString(expected, "id", ""), "text", text, "overlap", overlap);
                if (canonical)
                {
                    // These are the observer-projected server rows passed to the
                    // real formatter, not roles inferred from visible names or
                    // client diagnostics. A compacted/undelivered body cannot bind.
                    var ids = record.SourceId.Split(',').ToHashSet(StringComparer.Ordinal);
                    var turns = new List<Dictionary<string, object>>();
                    foreach (var line in canonicalLines ?? new())
                    {
                        string rawBody = ReadString(line, "text", "");
                        string body = NormalizePromptSegment(identityProjection == null ? rawBody : identityProjection(rawBody));
                        string id = FirstNonEmpty(ReadFirstString(line, "id", "turnId", "turn_id"), PromptHash(ReadString(line, "text", "")).Substring(0, 20));
                        id = Regex.Replace(id, @"[^\p{L}\p{Nd}_:.,-]", "_");
                        string role = ReadString(line, "role", "");
                        if (!ids.Contains(id) || body.Length == 0 || !text.Contains(body, StringComparison.Ordinal)
                            || (role != "player" && role != "npc")) continue;
                        turns.Add(TestDict("role", role, "speaker", ReadString(line, "speaker", ""),
                            "speakerId", RecallSourceSpeakerId(line), "text", body));
                    }
                    if (turns.Count == 0) continue;
                    original["turns"] = turns;
                    var npcLines = (canonicalLines ?? new()).Where(t => ReadString(t, "role", "") == "npc").ToList();
                    original["dyad"] = turns.Any(t => ReadString(t, "role", "") == "player") && npcLines.Count > 0
                        && npcLines.All(t => RecallTurnIsObserver(TestDict("role", "npc", "speaker", ReadString(t, "speaker", ""),
                            "speakerId", RecallSourceSpeakerId(t)), observer, receipt.ObserverName));
                }
                receipt.Originals.Add(original);
            }
            receipt.Originals = receipt.Originals.DistinctBy(r => ReadString(r, "id", ""))
                .OrderByDescending(r => ReadInt(r, "overlap", 0)).ToList();
            var byObserver = DeliveredMemoryRecall.GetOrCreateValue(payload);
            lock (byObserver) byObserver[observer] = receipt;
            return TestDict("schema", "reign-delivered-recall-v1", "observer", observer, "signature", receipt.Signature,
                "sourceIds", receipt.Originals.Select(r => ReadString(r, "id", "")).ToArray(), "sourceCount", receipt.Originals.Count);
        }

        private static string RecallSourceSpeakerId(Dictionary<string, object> line) =>
            ReadFirstString(line, "speakerId", "speaker_id", "speakerHeroStringId", "heroStringId");

        private static bool RecallTurnIsObserver(Dictionary<string, object> turn, string observer, string observerName)
        {
            string id = ReadString(turn, "speakerId", "");
            return ReadString(turn, "role", "") == "npc" && (id.Length > 0 ? id == observer
                : observerName.Length > 0 && ReadString(turn, "speaker", "").Equals(observerName, StringComparison.OrdinalIgnoreCase));
        }

        private static List<Dictionary<string, object>> DeliveredRecallOriginalTurns(Dictionary<string, object> original)
        {
            if (original.ContainsKey("turns")) return ReadDictionaryList(original, "turns");
            return Regex.Matches(ReadString(original, "text", ""),
                @"(?:\A|\n)- \[Day [^\]\r\n]+\] \[source=[^\]\r\n]* role=(?<role>player|npc)\] (?<speaker>[^:\r\n]+):\s*(?<body>[\s\S]*?)(?=\n- \[Day |\n\[\[/continuity\]\]|\z)")
                .Cast<Match>().Select(turn => TestDict("role", turn.Groups["role"].Value,
                    "speaker", turn.Groups["speaker"].Value.Trim(), "text", Regex.Replace(turn.Groups["body"].Value,
                        @"\s*\[(?:Recall source status|Source status):[^\]]*\]", ""))).ToList();
        }

        private const string RecallNarrativeWords = @"\b(?:memor\w*|remember\w*|recall\w*|forget\w*|forgot\w*)\b";
        private const string LiteralPageWords = @"\b(?:book|parchment|manuscript|ink|scroll)\b";

        private static bool HasLiteralPageContext(string text) =>
            Regex.IsMatch(text ?? "", LiteralPageWords, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static bool HasRecallNarrativeContext(string text) =>
            Regex.IsMatch(text ?? "", RecallNarrativeWords, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && !HasLiteralPageContext(text);

        private static string BroadRecallLossNarrative(string text, bool recallContext = false, bool literalPageContext = false)
        {
            text ??= "";
            const RegexOptions flags = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            var direct = Regex.Matches(text,
                @"\b(?:memory|recall)\s+(?:gap|loss|failure|damage|recovery)\b|\b(?:lost|missing|damaged|inaccessible|restored|recovered|reconstructed)\s+(?:(?:shared|full|my|your|her|his|our|their)\s+)?memor(?:y|ies)\b"
                + @"|\b(?:recover(?:ing|ed|s)?|reconstruct(?:ing|ed|s)?|restor(?:e|ing|ed|es)|rebuild(?:ing|s)?)\b[^;.!?\r\n]{0,70}\bmemor(?:y|ies)\b"
                + @"|\bmemor(?:y|ies)\b[^;.!?\r\n]{0,50}\b(?:lost|missing|inaccessible|restored|recovered|reconstructed|buried)\b", flags)
                .Cast<Match>().FirstOrDefault(m => !NegatedRecallLossMatch(text, m));
            if (direct != null) return direct.Value;
            if (!recallContext && !Regex.IsMatch(text, RecallNarrativeWords, flags)) return "";
            // Figurative lost pages must not become a neurological event. Literal
            // books/scrolls remain ordinary story objects, not recall failures.
            if (literalPageContext || HasLiteralPageContext(text)) return "";
            var metaphor = Regex.Matches(text,
                @"\bpage\b[^.!?\r\n]{0,70}\b(?:missing|lost|torn|intact|back)\b|\b(?:missing|lost|torn|intact)\b[^.!?\r\n]{0,40}\bpage\b"
                + @"|\bfiled\b[^.!?\r\n]{0,60}\bwrong\s+(?:shelf|heading|place)\b|\bgap\s+(?:where|around)\b", flags)
                .Cast<Match>().FirstOrDefault(m => !NegatedRecallLossMatch(text, m));
            if (metaphor != null) return metaphor.Value;
            if (Regex.IsMatch(text, @"\bhandwriting\b", flags))
            {
                var access = Regex.Match(text, @"\b(?:don['’]t|do not|can['’]t|cannot)\s+(?:have|see|find|reach)\b[^.!?\r\n]{0,50}\b(?:page|paddock|moment|memory)\b", flags);
                if (access.Success) return access.Value;
            }
            return "";
        }

        private static bool NegatedRecallLossMatch(string text, Match match)
        {
            string before = text.Substring(Math.Max(0, match.Index - 65), Math.Min(65, match.Index));
            string after = text.Substring(match.Index + match.Length);
            return Regex.IsMatch(before, @"\b(?:not|never|no|haven['’]t|hasn['’]t|isn['’]t|aren['’]t|wasn['’]t|weren['’]t)\s+(?:(?:a|any|actually|really|ever|truly|simply|just)\s+){0,3}$", RegexOptions.IgnoreCase)
                || Regex.IsMatch(match.Value, @"\bmemor(?:y|ies)\s+(?:(?:was|were|is|are|has|have|had|been)\s+){0,3}(?:not|never)\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(match.Value, @"^(?:no|without)\s+memor(?:y|ies)$", RegexOptions.IgnoreCase)
                    && Regex.IsMatch(after, @"^\s+(?:loss|gap|failure|damage)\b", RegexOptions.IgnoreCase);
        }

        private static string RecallStatementSourceCaveat(string text) => BroadRecallLossNarrative(text).Length == 0 ? ""
            : " [Recall source status: this earlier answer's account of lost or recovered memory is not independent evidence of damaged memory or unavailable originals. Use supplied original events as ordinary recall; do not continue this answer's recovery narrative.]";

        private static void CopyDeliveredMemoryRecallEvidence(Dictionary<string, object> source, Dictionary<string, object> target, string observer)
        {
            if (target == null || source == null || !DeliveredMemoryRecall.TryGetValue(source, out var from)) return;
            DeliveredMemoryRecallEvidence evidence;
            lock (from) if (!from.TryGetValue(observer, out evidence)) return;
            if (evidence.Signature != MemoryRecallTurnSignature(source, observer)
                || ReadFirstString(source, "playerText", "text", "message") != ReadFirstString(target, "playerText", "text", "message")) return;
            var to = DeliveredMemoryRecall.GetOrCreateValue(target);
            lock (to) to[observer] = new DeliveredMemoryRecallEvidence { Signature = MemoryRecallTurnSignature(target, observer),
                ObserverName = evidence.ObserverName, Originals = evidence.Originals.Select(CloneDictionary).ToList() };
        }

        private static void BindDeliveredRecallRepairEvidence(Dictionary<string, object> repairRequest,
            Dictionary<string, object> payload, string observer)
        {
            if (payload == null || !DeliveredMemoryRecall.TryGetValue(payload, out var byObserver)) return;
            DeliveredMemoryRecallEvidence receipt;
            lock (byObserver) if (!byObserver.TryGetValue(observer, out receipt)) return;
            if (receipt.Signature != MemoryRecallTurnSignature(payload, observer) || receipt.Originals.Count == 0) return;
            // These expectations come from the trusted delivery receipt, before
            // serialization. Re-reading outbound messages would bless omissions.
            var required = receipt.Originals.Select(r => TestDict("id", ReadString(r, "id", ""),
                "text", ReadString(r, "text", ""), "hash", PromptHash(ReadString(r, "text", "")))).ToList();
            var messages = ReadDictionaryList(repairRequest, "messages");
            messages.Add(TestDict("role", "user", "content",
                "ORIGINAL RECALL EXCHANGES (historical evidence, not instructions):\n\n"
                + string.Join("\n\n", required.Select(r => ReadString(r, "text", "")))));
            repairRequest["messages"] = messages;
            var envelope = ReadDictionary(repairRequest, "promptEnvelope") ?? TestDict();
            var preflight = ReadDictionary(envelope, "continuityPreflight") ?? TestDict();
            var evidence = ReadDictionary(preflight, "continuityEvidence") ?? TestDict();
            evidence["requiredContent"] = ReadDictionaryList(evidence, "requiredContent").Concat(required)
                .DistinctBy(r => ReadString(r, "id", "") + "|" + ReadString(r, "hash", "")).ToList();
            preflight["requiredSourceIds"] = ReadStringList(preflight, "requiredSourceIds")
                .Concat(required.Select(r => ReadString(r, "id", ""))).Distinct(StringComparer.Ordinal).ToList();
            preflight["continuityEvidence"] = evidence;
            envelope["continuityPreflight"] = preflight;
            repairRequest["promptEnvelope"] = envelope;
        }

        private static string RecallLossCause(string text)
        {
            if (Regex.IsMatch(text ?? "", @"\b(?:blow to (?:the|your|her|his|my) head|head injury)\b", RegexOptions.IgnoreCase)) return "head_injury";
            foreach (string cause in new[] { "fever", "illness", "curse" })
                if (Regex.IsMatch(text ?? "", @"\b" + cause + @"\b", RegexOptions.IgnoreCase)) return cause;
            return "";
        }

        private static bool HasIndependentRecallLossEvent(string text, string observer, string observerName, string reply)
            => HasIndependentRecallLossEvent(TestDict("text", text), observer, observerName, reply);

        private static bool HasIndependentRecallLossEvent(Dictionary<string, object> original, string observer, string observerName, string reply)
        {
            // The typed original, not another generated loss narrative, must
            // actually establish an in-world cause. Preserve qualified questions.
            string replyCause = RecallLossCause(reply);
            if (replyCause.Length == 0) return false;
            var participants = Regex.Match(ReadString(original, "text", ""), @"participants=([^;\r\n]+)").Groups[1].Value.Split(',').Select(p => p.Trim()).ToArray();
            bool dyad = ReadBool(original, "dyad", false) || (participants.Length == 2 && participants.Contains(observer));
            string namedSubject = Regex.Match(reply ?? "", @"\b(?<name>[\p{Lu}][\p{L}'’-]*(?:\s+[\p{Lu}][\p{L}'’-]*){0,3})\s+(?:(?:has|have|had|suffered|were|was|is|are)\s+){0,3}(?:lost|amnesia|memor\w*)\b").Groups["name"].Value;
            if (namedSubject.EndsWith("'s", StringComparison.Ordinal) || namedSubject.EndsWith("’s", StringComparison.Ordinal)) namedSubject = namedSubject.Substring(0, namedSubject.Length - 2);
            if (new[] { "I", "My", "You", "Your", "He", "His", "She", "Her", "They", "Their", "We", "Our", "The" }.Contains(namedSubject, StringComparer.OrdinalIgnoreCase)) namedSubject = "";
            bool otherSubject = namedSubject.Length > 0 && !namedSubject.Equals(observerName, StringComparison.OrdinalIgnoreCase);
            string subject = otherSubject ? Regex.Escape(namedSubject) + @"(?:['’]s)?"
                : observerName.Length > 0 ? Regex.Escape(observerName) + @"(?:['’]s)?" : @"(?!)";
            if (dyad && !otherSubject) subject += @"|you|your memory|your memories";
            foreach (var player in DeliveredRecallOriginalTurns(original).Where(t => ReadString(t, "role", "") == "player"))
            {
                string statement = ReadString(player, "text", "");
                foreach (string sentence in Regex.Split(statement, @"(?<=[.!?])\s+|\r?\n"))
                {
                    string plain = Regex.Replace(sentence, @"\*[^*]*\*", " ").Trim();
                    string cause = RecallLossCause(plain);
                    if (cause == replyCause
                        && Regex.IsMatch(plain, @"^(?:" + subject + @")\s+(?:(?:have|had|has|suffered|lost|were|was|is|are|a|the|your|her|his)\s+){0,5}(?:memor\w*|amnesia|lost)\b", RegexOptions.IgnoreCase)
                        && BroadRecallLossNarrative(plain).Length > 0
                        && Regex.IsMatch(plain, @"\b(?:after|because|following|caused by)\b", RegexOptions.IgnoreCase)
                        && !Regex.IsMatch(plain, @"\b(?:if|maybe|might|did you|do you|what if)\b|\?", RegexOptions.IgnoreCase)) return true;
                }
            }
            return false;
        }

        private static HashSet<string> RecallDenialTerms(string text)
        {
            var grammar = new HashSet<string>("recall recollection memory memories any anything something everything nothing him her his hers its our myself herself himself happening happened happen whether who was were not now ever again also first please actually really simply just well but because cannot don does have has had all told tell say said".Split(' '));
            return MemoryRecallTerms(text).Where(t => !grammar.Contains(t))
                .Select(t => t.Length > 4 && t.EndsWith("s", StringComparison.Ordinal) && !t.EndsWith("ss", StringComparison.Ordinal) ? t.Substring(0, t.Length - 1) : t)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static HashSet<string> PositiveDeliveredRecallTerms(IEnumerable<Dictionary<string, object>> originals)
        {
            // Only actual declarative source prose can contradict a denial.
            // Source IDs, headers, prior recall questions and failed denials are
            // not positive evidence merely because they contain the query name.
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var original in originals)
            foreach (var turn in DeliveredRecallOriginalTurns(original))
            foreach (string clause in Regex.Split(ReadString(turn, "text", ""), @"(?<=[.!?])\s+|\r?\n"))
                if (!clause.Contains('?') && !IsPureConversationRecallRequest(clause) && !IsUnsupportedRecallAbsenceState(clause))
                    known.UnionWith(RecallDenialTerms(clause));
            return known;
        }

        private static string UnsupportedDeliveredRecallDenial(string text, string query, string observerName,
            List<Dictionary<string, object>> originals, HashSet<string> known = null)
        {
            string subject = "I" + (observerName.Length > 0 ? "|" + Regex.Escape(observerName) : "");
            string pattern = @"\b(?:" + subject + @")\s+(?:(?:(?:do|does)\s+not|don['’]t|doesn['’]t|cannot|can['’]t)\s+(?:remember|recall)"
                + @"|(?:have|has)\s+no\s+(?:memory|recollection)\s+of|(?:don['’]t|do not)\s+have\s+(?:any\s+)?(?:memory|recollection)\s+of)\s+(?<object>[^.!?;\r\n]+)";
            var denials = Regex.Matches(text ?? "", pattern, RegexOptions.IgnoreCase);
            if (denials.Count == 0) return "";
            known ??= PositiveDeliveredRecallTerms(originals);
            if (known.Count == 0) return "";
            foreach (Match denial in denials)
            {
                string prefix = text.Substring(Math.Max(0, denial.Index - 160), Math.Min(160, denial.Index));
                if (Regex.IsMatch(prefix, @"\b(?:said|saying|wrote|asked|claimed|wrong|mistake|mistaken|not true)\b[^.!?;]{0,100}$", RegexOptions.IgnoreCase)) continue;
                string target = Regex.Split(denial.Groups["object"].Value, @",|\b(?:but|although)\b", RegexOptions.IgnoreCase)[0];
                var terms = RecallDenialTerms(target);
                if (terms.Count == 0)
                {
                    // Resolve a bare 'him/it/that' only when the question and
                    // local context contain no unsupported detail. A nearby
                    // unknown groom/name must not turn into a denial of the horse.
                    var contextTerms = RecallDenialTerms(prefix);
                    terms = RecallDenialTerms(query);
                    if (contextTerms.Except(known).Any()) continue;
                }
                if (terms.Count > 0 && terms.All(known.Contains)) return denial.Value;
            }
            return "";
        }

        private static bool HasConflictingDeliveredRecallHistory(DeliveredMemoryRecallEvidence receipt, string observer, string query,
            HashSet<string> known)
        {
            // A contaminated source context needs one evidence-grounded rewrite
            // even when the next writer invents a new metaphor for the old error.
            // This is a source condition, not an expanding list of bad replies.
            if (!Regex.IsMatch(query ?? "", @"\b(?:remember|recall|recollect|then|earlier)\b|\b(?:when|where|what|how|which)\b[^?\r\n]{0,220}\b(?:did|had|told|said|met|found|fell|was|were|meant|made)\b", RegexOptions.IgnoreCase)) return false;
            foreach (var original in receipt.Originals)
            foreach (var turn in DeliveredRecallOriginalTurns(original))
            {
                if (!RecallTurnIsObserver(turn, observer, receipt.ObserverName)) continue;
                string body = ReadString(turn, "text", "");
                // A narrated missing page and its memory context may be in
                // separate sentences of this same NPC turn. Never borrow that
                // context from another speaker or turn, or from a literal book.
                bool recallContext = HasRecallNarrativeContext(body);
                bool literalPageContext = HasLiteralPageContext(body);
                foreach (string clause in Regex.Split(body, @"(?<=[;.!?])\s+|\r?\n"))
                {
                    if (BroadRecallLossNarrative(clause, recallContext, literalPageContext).Length == 0
                        && UnsupportedDeliveredRecallDenial(clause, query, receipt.ObserverName, receipt.Originals, known).Length == 0) continue;
                    if (!receipt.Originals.Any(r => HasIndependentRecallLossEvent(r, observer, receipt.ObserverName, clause))) return true;
                }
            }
            return false;
        }

        private static string DeliveredRecallResponseHash(Dictionary<string, object> payload, string observer, Dictionary<string, object> parsed)
        {
            var content = TestDict("reply", RenderGroundedSceneHeading(payload, observer, parsed,
                ReadFirstString(parsed, "reply", "body", "response", "text")));
            foreach (string key in DeliveredRecallWriteFields)
                content[key] = parsed.GetValueOrDefault(key);
            return PromptHash(CanonicalJson(content));
        }

        private static void BindDeliveredRecallRewrite(Dictionary<string, object> payload, string observer, Dictionary<string, object> parsed)
        {
            if (payload == null || !DeliveredMemoryRecall.TryGetValue(payload, out var byObserver)) return;
            lock (byObserver)
                if (byObserver.TryGetValue(observer, out var receipt) && receipt.Signature == MemoryRecallTurnSignature(payload, observer))
                    receipt.ReviewedResponseHash = parsed == null ? "" : DeliveredRecallResponseHash(payload, observer, parsed);
        }

        private static List<Dictionary<string, object>> FindDeliveredMemoryRecallViolations(Dictionary<string, object> parsed,
            Dictionary<string, object> payload, string observer)
        {
            var found = new List<Dictionary<string, object>>();
            if (payload == null || !DeliveredMemoryRecall.TryGetValue(payload, out var byObserver)) return found;
            DeliveredMemoryRecallEvidence receipt;
            lock (byObserver) if (!byObserver.TryGetValue(observer, out receipt)) return found;
            if (receipt.Signature != MemoryRecallTurnSignature(payload, observer) || receipt.Originals.Count == 0) return found;
            string reply = ReadFirstString(parsed, "reply", "body", "response", "text");
            // Reuse this check's evidence vocabulary; rebuilding it for every
            // ordinary clause makes long delivered histories quadratic.
            var originalEvents = new List<Dictionary<string, object>>();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var original in receipt.Originals)
            {
                var terms = PositiveDeliveredRecallTerms(new[] { original });
                if (terms.Count == 0) continue;
                originalEvents.Add(original);
                known.UnionWith(terms);
            }
            if (originalEvents.Count == 0) return found;
            bool contextReview = receipt.ReviewedResponseHash != DeliveredRecallResponseHash(payload, observer, parsed)
                && HasConflictingDeliveredRecallHistory(receipt, observer, ReadFirstString(payload, "playerText", "text", "message"), known);
            var fields = new List<string> { reply };
            foreach (string key in DeliveredRecallWriteFields)
            {
                if (!parsed.TryGetValue(key, out var value)) continue;
                // Each proposed record supplies only its own literal context.
                // A book in one write cannot exempt another write or the reply.
                if (value is System.Collections.IEnumerable entries && value is not string && value is not System.Collections.IDictionary)
                    foreach (object entry in entries) fields.Add(FlattenFinalRoleplayWriteText(entry));
                else fields.Add(FlattenFinalRoleplayWriteText(value));
            }
            string match = null;
            foreach (string field in fields)
            {
                bool literalPageContext = HasLiteralPageContext(field);
                foreach (string clause in Regex.Split(field, @"(?<=[;.!?])\s+|\r?\n"))
                {
                    string narrative = BroadRecallLossNarrative(clause, true, literalPageContext);
                    if (narrative.Length == 0
                        || receipt.Originals.Any(r => HasIndependentRecallLossEvent(r, observer, receipt.ObserverName, clause))) continue;
                    match = narrative;
                    break;
                }
                if (!string.IsNullOrEmpty(match)) break;
            }
            if (string.IsNullOrEmpty(match)) match = fields.Select(t => UnsupportedDeliveredRecallDenial(t,
                ReadFirstString(payload, "playerText", "text", "message"), receipt.ObserverName, originalEvents, known)).FirstOrDefault(t => t.Length > 0);
            if (string.IsNullOrEmpty(match) && !contextReview) return found;
            // Selection and the original writer's capacity gate already bounded
            // these sources. A second count/character cutoff would silently lose
            // the very evidence the correction must preserve.
            var sources = originalEvents.Select(source => TestDict("sourceId", source["id"],
                "original", ReadString(source, "text", ""))).ToList();
            found.Add(TestDict("type", contextReview ? "critical_continuity_recall_context" : "critical_continuity_recall_loss", "match", LimitText(match ?? "", 220),
                "detail", "Original exchanges below were actually delivered to this observer. "
                    + (contextReview ? "The supplied context also contains earlier unreliable recall: reassess the entire answer and all proposed writes from original events. " : "The draft conflicts with that evidence. ")
                    + "Do not deny remembering their supported topics or ask for the supplied story again. Earlier failed recall, its metaphors and subsequent retellings are not independent evidence of amnesia. Answer from these originals as ordinary memory. Remove every lost/kept/recovered-page contrast and dependent mental, memory, mood, plan or continuity write. Acknowledge a prior mistaken answer briefly only if needed. Recalling an existing event does not create a new event; omit redundant writes whose only purpose is to announce recall or reconstruction. Keep unsupported details narrowly uncertain. An invitation, promise or plan never proves its later fulfillment; describe what was actually said and do not invent arrival, completion, witnesses, dates or a new cause. Any retained source quote must be one exact contiguous span of the corrected visible reply or supplied original, with the correct speaker.",
                "deliveredOriginals", sources));
            return found;
        }

        private const string MemoryRecallAuthorityContract = "Supplied original exchanges are available memories: recall their supported details normally, preserving who said, heard or witnessed them. "
            + "An earlier failed or incorrect recall reply is not evidence of damaged memory. Correct that mistake briefly when relevant, then answer from the originals. "
            + "Do not explain a prior recall failure as missing pages, inaccessible, restored or reconstructed memories, or create lasting beliefs, mood crises, plans or continuity about memory loss without independent story evidence. "
            + "Do not ask for details already supplied. Uncertainty about one unsupported detail does not erase the rest. The latest eligible correction replaces the same fact only; separate older episodes remain available. "
            + "An invitation or promise establishes what was proposed, not where a later reunion happened or whether the promise was fulfilled. Require the later event's own evidence.";

        private const string MemoryRecallSourcePolicy = "RECALL SOURCE PRECEDENCE: Original speaker-labeled exchanges establish what was said, including the NPC's own reply and explicitly observed actions. "
            + "An earlier failure to recall, a belief about lost memories, or a plan to reconstruct them does not disprove those exchanges. Use their concrete details as ordinary recall, not as memories being recovered or reconstructed. Correct an earlier mistaken answer briefly when needed, without inventing a memory-loss explanation or asking the player to retell supplied evidence. "
            + "The latest valid revision controls the same fact or agreement; a newer conversation does not erase separate older events. Preserve who knew each fact and when. If a requested detail is unsupported, leave that detail uncertain without inventing either the detail or an enduring memory loss.";

        private static string ProtectMemoryRecallSourcePolicy(string npc) => RenderContinuityRecord(
            "recall_source_precedence", "recall_source_policy", npc, "", 0d, 122, true, MemoryRecallSourcePolicy);

        private static List<Dictionary<string, object>> PreserveMemoryRecallCandidatePool(
            List<Dictionary<string, object>> ranked, List<Dictionary<string, object>> lexical, int lexicalLimit)
        {
            // Rank fusion can promote a broad topic above a precise original.
            // Keep the bounded lexical pool intact and reserve separate slots
            // for independent semantic/topic discovery. Fusion still orders the
            // retained evidence; it cannot spend an original's slot twice.
            var ids = lexical.Take(lexicalLimit).Select(row => ReadString(row, "turn_id", "")).ToHashSet(StringComparer.Ordinal);
            var supplements = ranked.Where(row => !ids.Contains(ReadString(row, "turn_id", "")))
                .Take(Math.Min(MemoryRecallSupplementLimit, lexicalLimit)).ToList();
            ids.UnionWith(supplements.Select(row => ReadString(row, "turn_id", "")));
            return ranked.Where(row => ids.Contains(ReadString(row, "turn_id", "")))
                .DistinctBy(row => ReadString(row, "turn_id", "")).ToList();
        }

        private static List<Dictionary<string, object>> ExpandMemoryRecallPrecedingTurn(ReignDbConnection connection,
            List<Dictionary<string, object>> exchange, KnowledgeAccessContext knowledge)
        {
            if (knowledge == null || exchange.Count == 0) return exchange;
            var first = exchange.OrderBy(row => ReadLong(row, "turn_order", 0)).First();
            if (ReadString(first, "role", "") != "player") return exchange;
            // A player's callback can point to the preceding NPC observation,
            // even when that observation was not phrased as a question. Inspect
            // the actual neighbor before filtering: never jump over an unheard,
            // future or other-branch turn to manufacture a conversation link.
            var previous = QuerySql(connection, @"SELECT * FROM conversation_turns
WHERE session_id=$session AND status='active' AND turn_order<$order ORDER BY turn_order DESC LIMIT 1;",
                TestDict("session", ReadString(first, "session_id", ""), "order", ReadLong(first, "turn_order", 0))).FirstOrDefault();
            if (previous == null || ReadString(previous, "role", "") != "npc"
                || !PrecisionTurnVisible(previous, knowledge.NpcId, knowledge, connection)) return exchange;
            return exchange.Append(previous).DistinctBy(row => ReadString(row, "turn_id", ""))
                .OrderBy(row => ReadLong(row, "turn_order", 0)).ToList();
        }

        private static List<string> MemoryRecallTerms(string text)
        {
            var terms = MemoryQueryTerms(text).Where(t => !ExactRecallStopWords.Contains(t, StringComparer.OrdinalIgnoreCase)
                && !new[] { "can", "did", "does", "still", "please" }.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
            // The game and ordinary dialogue use both names for the same event.
            // Expansion affects discovery only, never source text or truth.
            if (terms.Contains("tournament") && !terms.Contains("tourney")) terms.Add("tourney");
            if (terms.Contains("tourney") && !terms.Contains("tournament")) terms.Add("tournament");
            return terms;
        }

        private static List<string> MemoryRecallQueryGroups(string query)
        {
            string spoken = Regex.Replace(query ?? "", @"\*[^*]*\*", " ");
            string primary = MemoryQueryTerms(spoken).Count >= 3 ? spoken : query ?? "";
            var groups = new List<string> { BuildFtsQuery(MemoryRecallTerms(primary)) };
            // A long reminder can ask about several episodes. Search each
            // substantive sentence as well as the whole contribution, so a long
            // preamble cannot crowd its final question out of the candidate set.
            groups.AddRange(Regex.Split(primary, @"(?<=[.!?;])\s+").Select(MemoryRecallTerms)
                .Where(terms => terms.Count >= 2).Select(BuildFtsQuery).TakeLast(5));
            return groups.Where(g => g.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        }

        private static bool MemoryRecallAnswerCandidate(Dictionary<string, object> row, MemoryQueryPlan plan)
        {
            string text = ReadString(row, "text", "");
            if (ReadString(row, "role", "") == "npc" && BroadRecallLossNarrative(text).Length > 0) return false;
            var terms = MemoryRecallTerms(plan.Query);
            bool removedAttempt = false;
            var statements = new List<string>();
            foreach (string clause in Regex.Split(text, @"(?<=[.!?;])\s+"))
            {
                bool attempt = IsPureConversationRecallRequest(clause) || IsUnsupportedRecallAbsenceState(clause)
                    || Regex.IsMatch(clause, @"\b(?:do|did|can|could|would)\s+you\s+(?:still\s+)?(?:remember|recall)\b|\b(?:tell|remind)\s+me\s+(?:again|about|where|what|how|[^.!?;]{0,100}\bagain)\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (attempt) removedAttempt = true;
                else statements.Add(clause);
            }
            if (!removedAttempt) return true;
            // A mixed contribution can still supply a new fact. Only move it to
            // fallback when all topical content belongs to its recall attempt.
            // Merely echoing the query's labels before asking for the story is
            // not another answer. Repeated echoes otherwise outrank the longer
            // original as history grows. This affects priority, never storage.
            return statements.Any(clause => !MemoryRecallQueryEcho(clause, terms)
                && terms.Any(term => Regex.IsMatch(clause, @"\b" + Regex.Escape(term) + @"\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)));
        }

        private static bool MemoryRecallQueryEcho(string clause, List<string> terms)
        {
            var words = Regex.Matches(clause ?? "", @"[\p{L}\p{Nd}_'-]+")
                .Cast<Match>().Select(match => match.Value.ToLowerInvariant())
                .Where(word => !Regex.IsMatch(word, @"\A(?:a|an|the|and|or|of|my|your|our|his|her|their|this|that|those|these|ah|oh|yes|right|well)\z"))
                .ToList();
            return words.Count > 0 && words.All(word => terms.Contains(word, StringComparer.OrdinalIgnoreCase));
        }

        // Legacy and precision must search the same eligible originals. Compute
        // rarity before pagination, using the observer/branch/as-of boundary;
        // a long recent monologue cannot win by repeating common scene words.
        // Spoken sentences share candidate slots; length normalization prevents
        // verbose incidental narration from winning through generic overlap.
        // Action/topic terms remain fallback candidates, and action-only input
        // uses its complete contribution.
        private static List<Dictionary<string, object>> FindLexicalMemoryRecallTurns(ReignDbConnection connection,
            MemoryQueryPlan plan, KnowledgeAccessContext knowledge, int limit,
            int maximumCandidatePages = int.MaxValue, bool requirePlayerSource = true, bool excludeCurrentSession = false)
        {
            if (plan.Terms.Count == 0) return new();
            var groups = MemoryRecallQueryGroups(plan.Query);
            if (groups.Count == 0) groups.Add(BuildFtsQuery(plan.Terms));
            var parameters = TestDict("query", BuildFtsQuery(plan.Terms.Concat(MemoryRecallTerms(plan.Query)).Distinct().ToList()), "npc", plan.Observer,
                "queryGroups", Json.Serialize(groups),
                "session", plan.Session, "day", plan.WorldDay, "timeline", plan.Timeline,
                "asOfSequence", knowledge.AsOfAcceptedSequence, "asOfTimestamp", knowledge.AsOfTimestamp);
            string eligible = MemorySessionObserverSql("s", "t")
                + " AND t.world_day<=$day AND t.status='active'"
                + " AND t.accepted_sequence<=$asOfSequence AND t.ts<=$asOfTimestamp"
                + " AND " + MemorySourceTimelineSql("conversation_turns", "t")
                + (excludeCurrentSession && plan.Session.Length > 0 ? " AND t.session_id<>$session" : "");
            string sql = @"WITH eligible AS MATERIALIZED (
 SELECT t.turn_id,length(t.text) AS characters,to_tsvector('english',t.text) AS document
 FROM conversation_turns t JOIN conversation_sessions s ON s.session_id=t.session_id WHERE " + eligible + @"
), query_groups AS (
 SELECT value AS query,ordinality AS group_index FROM jsonb_array_elements_text($queryGroups::jsonb) WITH ORDINALITY
), query_terms AS (
 SELECT DISTINCT group_index,unnest(tsvector_to_array(to_tsvector('english',query))) AS term FROM query_groups
), weights AS MATERIALIZED (
 SELECT q.group_index,plainto_tsquery('simple',q.term) AS term_query,
 ln(1.0+(SELECT count(*) FROM eligible)::double precision/(1.0+(SELECT count(*) FROM eligible e
      WHERE e.document @@ plainto_tsquery('simple',q.term)))) AS weight FROM query_terms q
), group_scores AS (
 SELECT e.turn_id,g.group_index,
 COALESCE((SELECT sum(w.weight) FROM weights w WHERE w.group_index=g.group_index AND e.document @@ w.term_query),0)
 /GREATEST(0.25,0.25+0.75*e.characters/NULLIF((SELECT avg(characters) FROM eligible),0)) AS score
 FROM eligible e CROSS JOIN query_groups g WHERE e.document @@ websearch_to_tsquery('english',g.query)
), ranked_groups AS (
 SELECT turn_id,group_index,score,row_number() OVER(PARTITION BY group_index ORDER BY score DESC,turn_id) AS query_rank FROM group_scores
), best_rank AS (
 SELECT DISTINCT ON(turn_id) turn_id,group_index,query_rank,score FROM ranked_groups ORDER BY turn_id,query_rank,group_index
)
SELECT t.*,COALESCE(5.0/(r.query_rank+1),0) AS relevance,r.group_index AS recall_query_group,r.query_rank AS recall_query_rank
FROM eligible e JOIN conversation_turns t ON t.turn_id=e.turn_id
LEFT JOIN best_rank r ON r.turn_id=t.turn_id
WHERE e.document @@ websearch_to_tsquery('english',$query)
ORDER BY r.query_rank NULLS LAST,r.group_index NULLS LAST,r.score DESC,t.ts DESC,t.turn_order DESC,t.turn_id";
            // Recall attempts can occur inside otherwise substantive sessions.
            // Prefer answer-bearing originals before spending candidate slots on
            // repeated questions or failed-lookup replies. This is retrieval
            // priority only: preserve all speech and retain attempts as fallback.
            var answers = QuerySourceBearingPrecisionTurns(connection, sql, parameters, knowledge, limit,
                plan.SuppliedSources, maximumCandidatePages, requirePlayerSource, row => MemoryRecallAnswerCandidate(row, plan));
            if (answers.Count >= limit) return answers;
            var attempts = QuerySourceBearingPrecisionTurns(connection, sql, parameters, knowledge, limit,
                plan.SuppliedSources, maximumCandidatePages, requirePlayerSource);
            return answers.Concat(attempts).DistinctBy(row => ReadString(row, "session_id", "") + "|"
                + FirstNonEmpty(ReadString(row, "exchange_id", ""), ReadString(row, "turn_id", ""))).Take(limit).ToList();
        }
    }
}
