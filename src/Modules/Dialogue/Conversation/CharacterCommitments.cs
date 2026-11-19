using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private const string CharacterCommitmentContract = @"
ACTIVE PROMISES, REQUESTS AND PREFERENCES: Quotes bind wording/authorship, not proposer, acceptance or fulfillment. Interpretations remain uncertain; they never execute actions or override consent.
In obligationWrites, set recordKind=promise/request/preference, interpreted owed_by/owed_to, and description/scope retaining all conditions. Cite exact words and speaker in sourceEvidence; cite responses separately in sourceEvidenceSpans. Use acceptanceStatus=unknown unless counterparty evidence supports accepted/refused. Requests/counteroffers are not agreements. Preserve refusals; do not infer consent from sarcasm, hypotheticals, hearsay or another NPC's agreement. Separate distinct matters without losing conditions. Preferences survive compliant replies and violations. Revise with obligation_id and newer evidence; status=unresolved/active/fulfilled/withdrawn/superseded. Mention alone never closes a record; preserve original terms and uncertainty.";

        // Preserve older and native rows without dialogue-validation metadata.
        // When validation exists, an exact quote alone cannot turn a hypothesis into a promise.
        private const string PromptObligationPayloadSql = "COALESCE(NULLIF(BTRIM(payload_json),''),'{}')::jsonb";
        private const string PromptObligationEvidenceSql = $@"(
    (COALESCE({PromptObligationPayloadSql}->>'sourceValidation','')=''
        AND COALESCE({PromptObligationPayloadSql}->'sourceEvidence'->>'status','')='')
    OR ({PromptObligationPayloadSql}->>'sourceValidation'='supported_span'
        AND {PromptObligationPayloadSql}->'sourceEvidence'->>'status'='supported_span')
)
AND COALESCE({PromptObligationPayloadSql}->>'evidenceType','') NOT IN ('hypothesis','unverified_summary','subjective_belief')
AND COALESCE({PromptObligationPayloadSql}->'sourceEvidence'->>'evidenceType','') NOT IN ('hypothesis','unverified_summary','subjective_belief')";

        internal const string PromptActiveObligationSql = "status IN ('unresolved','active','open','pending') AND (" + PromptObligationEvidenceSql
            + " OR (" + PromptObligationPayloadSql + "->>'interpretationStatus'='model_inferred' AND "
            + PromptObligationPayloadSql + "->>'bindingStatus'='bound'))";

        internal static void EnsureCharacterCommitmentTimeline(ReignDbConnection connection)
        {
            // Dialogue predates the Court timeline column. Its accepted-source
            // payload is authoritative only for untouched default-column rows.
            DialogueSchema.EnsureColumnObligationsTimelineId(connection);
            const string marker = "character_commitment_timeline_v1";
            if (ReadString(DialogueStore.SelectSchemaMeta(connection, TestDict("key", marker)).FirstOrDefault(), "value", "") == "1") return;
            DialogueStore.UpdateObligations(connection);
            DialogueStore.UpsertSchemaMeta(connection, TestDict("key", marker));
        }

        // This fallback records only explicit promissory speech. Ordinary future
        // tense remains model-authored structured metadata, never an action cue.
        private static List<string> ExplicitPromiseClauses(string original)
        {
            var result = new List<string>();
            string spoken = Regex.Replace(original ?? "", @"\*[^*]*\*", m => new string(' ', m.Length));
            bool wordGiven = false;
            int lastPromiseStart = -1;
            foreach (Match match in Regex.Matches(spoken, @"[^.!?\r\n]+(?:[.!?]+|$)"))
            {
                string clause = match.Value.Trim();
                if (clause.Length == 0) continue;
                int clauseStart = match.Index + match.Value.IndexOf(clause, StringComparison.Ordinal);
                clause = original.Substring(clauseStart, clause.Length);
                if (result.Count > 0 && lastPromiseStart >= 0 && CommitmentConditionContinuation(clause))
                {
                    result[result.Count - 1] = original.Substring(lastPromiseStart, match.Index + match.Length - lastPromiseStart).Trim();
                    wordGiven = false; continue;
                }
                if (clause.Contains('*') || clause.IndexOfAny(new[] { '"', '“', '”', '«', '»' }) >= 0 || clause.EndsWith("?", StringComparison.Ordinal))
                { wordGiven = false; continue; }
                bool direct = Regex.IsMatch(clause, @"^I\s+(?:promise|swear|pledge|give\s+you\s+my\s+word)\b", RegexOptions.IgnoreCase);
                bool introduction = Regex.IsMatch(clause, @"^I(?:['’]ll|\s+will)\s+give\s+you\s+my\s+word\b", RegexOptions.IgnoreCase);
                bool future = Regex.IsMatch(clause, @"^I(?:['’]ll|\s+will|\s+shall)\s+", RegexOptions.IgnoreCase);
                if (Regex.IsMatch(clause, @"\b(?:cannot|can't|could|would|might|maybe|perhaps|pretend|imagine|not\s+promise|no\s+promise|promise\s+(?:nothing|no\b))\b", RegexOptions.IgnoreCase))
                { wordGiven = false; continue; }
                if (introduction)
                {
                    wordGiven = !Regex.IsMatch(clause, @"\b(?:if|unless|provided|once|until|after|before|when|as\s+long\s+as|on\s+condition)\b", RegexOptions.IgnoreCase);
                    lastPromiseStart = -1;
                    continue;
                }
                if (direct || wordGiven && future)
                {
                    if (clause.Length >= 12)
                    {
                        result.Add(clause);
                        lastPromiseStart = match.Index + match.Value.IndexOf(clause, StringComparison.Ordinal);
                    }
                    wordGiven = direct || wordGiven;
                }
                else { wordGiven = false; lastPromiseStart = -1; }
            }
            return result;
        }

        private static bool CommitmentConditionContinuation(string clause) => Regex.IsMatch(clause ?? "",
            @"^(?:(?:but|and)\s+)?(?:only\b|if\b|unless\b|provided\b|once\b|when\b|before\b|after\b|on\s+condition\b|(?:this|that|it)\s+(?:depends|requires|is\s+conditional)\b)", RegexOptions.IgnoreCase);

        private static IEnumerable<Dictionary<string, object>> AtomicCommitmentWrites(Dictionary<string, object> write)
        {
            if (ReadString(write, "recordKind", "promise") != "promise") { yield return write; yield break; }
            string description = ReadString(write, "description", "");
            // Split only sentence boundaries. Conditions within a promise stay
            // attached; the source guard still checks every resulting item.
            var clauses = Regex.Matches(description, @"[^.!?\r\n]+(?:[.!?]+|$)").Cast<Match>().Select(m => m.Value.Trim()).Where(s => s.Length > 0).ToList();
            for (int index = clauses.Count - 1; index > 0; index--)
                if (CommitmentConditionContinuation(clauses[index]))
                { clauses[index - 1] += " " + clauses[index]; clauses.RemoveAt(index); }
            if (clauses.Count < 2 || ReadFirstString(write, "obligation_id", "obligationId", "id").Length > 0
                || !new[] { "", "unresolved", "active", "open", "pending" }.Contains(ReadString(write, "status", "unresolved").ToLowerInvariant()))
            { yield return write; yield break; }
            foreach (string clause in clauses)
            {
                var copy = new Dictionary<string, object>(write);
                copy["description"] = clause;
                yield return copy;
            }
        }

        private static void AddExplicitCommitmentFallback(Dictionary<string, object> parsed, Dictionary<string, object> payload, string speaker)
        {
            var writes = ReadDictionaryList(parsed, "obligationWrites").ToList();
            string peer = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            string addressed = ReadString(parsed, "reactionTargetHeroStringId", "");
            if (addressed.Length > 0 && addressed != peer)
            {
                if (!MergedInteractionParticipantProfiles(payload).Select(CampaignFileService.CharacterIdFrom).Contains(addressed)) return;
                peer = addressed;
            }
            if (peer.Length == 0 || speaker.Length == 0) return;
            string reply = ReadFirstString(parsed, "reply", "body", "response", "text");
            var contributions = new List<(string actor, string recipient, string alias, string text)> { (speaker, peer, "current_npc", reply) };
            if (ReadStringList(payload, "participants").Count <= 2 && MergedInteractionParticipantProfiles(payload).Count <= 2)
                contributions.Add((peer, speaker, "current_player", ReadFirstString(payload, "playerText", "text", "message")));
            foreach (var contribution in contributions)
            {
                var clauses = ExplicitPromiseClauses(contribution.text);
                // A single grounded model summary can still contain several
                // independent promises. Preserve their exact scopes so closing
                // one promise never silently closes another.
                void SeparateExplicitWrites(List<Dictionary<string, object>> items)
                {
                    foreach (var item in items.ToList())
                    {
                        if (ReadFirstString(item, "owed_by", "owedBy") != contribution.actor
                            || ReadFirstString(item, "obligation_id", "obligationId", "id").Length > 0
                            || !new[] { "", "unresolved", "active", "open", "pending" }.Contains(ReadString(item, "status", "unresolved").ToLowerInvariant())) continue;
                        var cited = ReadDictionary(item, "sourceEvidence");
                        string quote = ReadString(cited, "quote", "");
                        var distinctPromises = clauses.Where(clause => quote.Contains(clause, StringComparison.Ordinal)).ToList();
                        if (distinctPromises.Count < 2) continue;
                        items.Remove(item);
                        foreach (string promise in distinctPromises)
                        {
                            var separate = CloneDictionary(item);
                            separate["description"] = promise;
                            separate["sourceEvidence"] = new Dictionary<string, object>(cited) { ["quote"] = promise, ["evidenceType"] = "future_intention" };
                            items.Add(separate);
                        }
                    }
                }
                SeparateExplicitWrites(writes);
                var snakeWrites = ReadDictionaryList(parsed, "obligation_writes").ToList();
                SeparateExplicitWrites(snakeWrites);
                if (parsed.ContainsKey("obligation_writes")) parsed["obligation_writes"] = snakeWrites;
                foreach (string clause in clauses)
                {
                    // A wider authored quote is still checked independently. It must
                    // not suppress the exact supported fallback if quarantined.
                    if (writes.Concat(ReadDictionaryList(parsed, "obligation_writes")).Any(w => ReadFirstString(w, "owed_by", "owedBy") == contribution.actor
                        && ReadString(ReadDictionary(w, "sourceEvidence"), "quote", "").Contains(clause, StringComparison.Ordinal))) continue;
                    writes.Add(TestDict("description", clause, "owed_by", contribution.actor, "owed_to", contribution.recipient, "status", "unresolved", "importance", 0.9d,
                        "sourceEvidence", TestDict("turnId", contribution.alias, "speakerId", contribution.actor, "actorIds", new[] { contribution.actor, contribution.recipient },
                            "evidenceType", "future_intention", "quote", clause)));
                }
            }
            parsed["obligationWrites"] = writes;
        }

        private static string BuildCharacterCommitmentPrompt(ReignDbConnection connection, string observer, string peer,
            Dictionary<string, object> payload, List<string> sources, IEnumerable<string> additionalObservers = null,
            string requiredTranscript = "")
        {
            if (!WorldSimulationService.TableExists(connection, "obligations")) return "";
            var access = DialogueService.BuildKnowledgeAccessContext(observer, peer, "", payload);
            var rows = DialogueStore.SelectObligations(connection, PromptActiveObligationSql, MemoryEligibilitySql("obligations"), MemorySourceTimelineSql("obligations", access: access),                new Dictionary<string, object> { ["observer"] = observer, ["timeline"] = access.TimelineId });
            var text = new StringBuilder("ACTIVE PROMISES, REQUESTS AND PREFERENCES — source-bound dialogue interpretations\n");
            text.AppendLine("Authorship is verified; meaning, acceptance and lifecycle remain interpretations. Native facts and original wording take precedence. Preserve conditions, corrections and refusals; a known preference is not an obligation automatically inherited by every listener.");
            int included = 0;
            var eligible = new List<Dictionary<string, object>>();
            var audience = new[] { access }.Concat((additionalObservers ?? Enumerable.Empty<string>())
                .Distinct().Where(other => other != observer).Select(other => DialogueService.BuildKnowledgeAccessContext(other, peer, "", payload))).ToList();
            foreach (var row in rows)
            {
                if (!DialogueService.KnowledgeRowVisibleToNpc("obligations", row, access, connection)) continue;
                if ((additionalObservers ?? Enumerable.Empty<string>()).Any(other => !DialogueService.KnowledgeRowVisibleToNpc("obligations", row,
                    DialogueService.BuildKnowledgeAccessContext(other, peer, "", payload), connection))) continue;
                if (ReadBool(payload, "officialMemoryFirewall", false)
                    && !new[] { "world", "global", "common", "public" }.Contains(ReadString(row, "visibility", "").ToLowerInvariant())) continue;
                var value = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                var evidence = ReadDictionary(value, "sourceEvidence");
                if ((ReadString(value, "sourceValidation", "") != "supported_span" && ReadString(value, "bindingStatus", "") != "bound")
                    || ReadString(evidence, "status", "") != "supported_span") continue;
                string id = ReadString(row, "obligation_id", "");
                sources.Add(id);
                text.AppendLine(RenderContinuityRecord(id, "active_commitment", observer, "", ReadDouble(row, "created_day", 0d), 121, true,
                    "obligation_id=" + id + "; kind=" + ReadString(value, "recordKind", "promise")
                    + "; interpreted status=" + ReadString(row, "status", "") + "; interpreted parties=" + ReadString(row, "owed_by", "")
                    + " -> " + ReadString(row, "owed_to", "") + "; interpretation (unverified): " + ReadString(row, "description", "")
                    + "; scope=" + ReadString(value, "scope", "unspecified") + "; interpreted acceptance=" + ReadString(value, "acceptanceStatus", "unknown")
                    + "; exact source quote: " + ReadString(evidence, "quote", "")
                    + "; source event=" + ReadString(evidence, "sourceEventId", "") + "; source speaker=" + ReadString(evidence, "speakerId", "")
                    + "; source turn=" + ReadString(evidence, "turnId", "")));
                included++; eligible.Add(row);
            }
            var diagnostics = new List<Dictionary<string, object>>();
            if (included > 0) text.Append(BuildRelevantCommitmentContext(connection, eligible, payload, audience, sources, diagnostics, requiredTranscript));
            payload["characterCommitmentEvidence"] = TestDict("activeCount", included, "observerId", observer, "timelineId", ContinuityTimeline(payload), "sourceContexts", diagnostics);
            return included == 0 ? "" : text.ToString();
        }

        internal static void NormalizeDialogueCommitment(Dictionary<string, object> item, string observer)
        {
            string kind = ReadString(item, "recordKind", "unclassified").ToLowerInvariant();
            item["recordKind"] = new[] { "promise", "request", "preference" }.Contains(kind) ? kind : "unclassified";
            item["bindingStatus"] = ReadString(ReadDictionary(item, "sourceEvidence"), "status", "") == "supported_span" ? "bound" : "unbound";
            item["interpretationStatus"] = "model_inferred";
            item["claimEntailmentVerified"] = false;
            item["quotedSpeakerId"] = ReadString(ReadDictionary(item, "sourceEvidence"), "speakerId", "");
            item["known_by"] = new[] { observer };
            item["knownBy"] = new[] { observer };
            item["visibility"] = "private";
            string acceptance = ReadString(item, "acceptanceStatus", "unknown").ToLowerInvariant();
            if (!new[] { "unknown", "accepted", "refused" }.Contains(acceptance)) acceptance = "unknown";
            string counterparty = ReadFirstString(item, "owed_to", "owedTo");
            if (acceptance != "unknown" && (counterparty.Length == 0 || !ReadDictionaryList(item, "sourceEvidenceSpans").Any(span =>
                ReadString(span, "status", "") == "supported_span" && ReadString(span, "speakerId", "") == counterparty
                && counterparty != ReadString(item, "quotedSpeakerId", "")))) acceptance = "unknown";
            item["acceptanceStatus"] = acceptance;
            if (ReadString(item, "recordKind", "") == "preference" && ReadString(item, "status", "") == "fulfilled")
                item["status"] = "active"; // One compliant reply does not expire a standing preference.
        }

        internal static string DialogueCommitmentTarget(ReignDbConnection connection, string id, Dictionary<string, object> item)
        {
            if (ReadString(item, "interpretationStatus", "") != "model_inferred") return id;
            DialogueStore.SelectStatement6(connection, TestDict("key", "dialogue_commitment:" + id));
            var previous = DialogueStore.SelectObligations2(connection, TestDict("id", id)).SingleOrDefault();
            if (previous == null) return id;
            var old = TryParseJsonObject(ReadString(previous, "payload_json", "{}"));
            if (ReadString(old, "interpretationStatus", "") == "model_inferred"
                && DialogueCommitmentIdentity(old) == DialogueCommitmentIdentity(item)) return null;
            // Dialogue can revise a source-bound interpretation in the same scope.
            // It cannot retire or overwrite a native debt or another observer's record.
            bool compatible = DialogueCommitmentRevisionAllowed(previous, item);
            if (!compatible)
            {
                item["proposedRevisionOf"] = id;
                item["status"] = "unresolved";
                string proposal = "interpretation_" + PromptHash(id + "|" + DialogueCommitmentIdentity(item)).Substring(0, 32);
                var existingProposal = DialogueStore.SelectObligations3(connection, TestDict("id", proposal)).SingleOrDefault();
                if (existingProposal != null)
                {
                    var proposed = TryParseJsonObject(ReadString(existingProposal, "payload_json", "{}"));
                    if (ReadString(proposed, "interpretationStatus", "") == "model_inferred"
                        && DialogueCommitmentIdentity(proposed) == DialogueCommitmentIdentity(item)) return null;
                    proposal += "_" + Guid.NewGuid().ToString("N"); // Actual different-content collision only.
                }
                return proposal;
            }
            var revisions = ReadDictionaryList(old, "interpretationHistory").ToList();
            revisions.Add(TestDict("description", ReadString(previous, "description", ""), "status", ReadString(previous, "status", ""),
                "sourceEvidence", ReadDictionary(old, "sourceEvidence") ?? TestDict(), "ts", ReadLong(previous, "ts", 0)));
            item["interpretationHistory"] = revisions;
            return id;
        }

        private static bool DialogueCommitmentRevisionAllowed(Dictionary<string, object> previous, Dictionary<string, object> item)
        {
            var old = TryParseJsonObject(ReadString(previous, "payload_json", "{}"));
            long sourceTime = ReadLong(ReadDictionary(item, "sourceEvidence"), "timestamp", 0);
            long oldTime = ReadLong(ReadDictionary(old, "sourceEvidence"), "timestamp", 0);
            return ReadString(old, "interpretationStatus", "") == "model_inferred"
                && ReadString(item, "bindingStatus", "") == "bound"
                && ReadString(old, "sourceValidation", "") == "supported_span"
                && sourceTime > oldTime && oldTime > 0
                && new[] { "active", "unresolved" }.Contains(ReadString(previous, "status", ""))
                && ReadString(previous, "timeline_id", "main") == ContinuityTimeline(item)
                && ReadString(previous, "owed_by", "") == ReadFirstString(item, "owed_by", "owedBy")
                && ReadString(previous, "owed_to", "") == ReadFirstString(item, "owed_to", "owedTo")
                && ReadString(old, "recordKind", "promise") == ReadString(item, "recordKind", "promise")
                && ReadString(old, "scope", "") == ReadString(item, "scope", "")
                && DialogueService.TextListFromJson(ReadString(previous, "known_by_json", "[]")).OrderBy(x => x)
                    .SequenceEqual(ReadStringList(item, "known_by").OrderBy(x => x));
        }

        private static string DialogueCommitmentIdentity(Dictionary<string, object> item) => CanonicalJson(TestDict(
            "timeline", ContinuityTimeline(item), "knownBy", ReadStringList(item, "known_by").OrderBy(x => x).ToArray(),
            "kind", ReadString(item, "recordKind", "unclassified"), "scope", ReadString(item, "scope", ""),
            "by", ReadFirstString(item, "owed_by", "owedBy"), "to", ReadFirstString(item, "owed_to", "owedTo"),
            "description", ReadFirstString(item, "description", "text"), "status", ReadString(item, "status", "unresolved"),
            "acceptance", ReadString(item, "acceptanceStatus", "unknown"), "source", ReadDictionary(item, "sourceEvidence")));

        // These references are server-owned. Proposed metadata cannot substitute
        // an unrelated conversation for the original terms of an existing debt.
        internal static void PreserveCommitmentOriginalSource(ReignDbConnection connection, string id,
            string eventId, Dictionary<string, object> item)
        {
            item.Remove("commitmentOriginalSource");
            var current = ReadDictionary(item, "sourceEvidence");
            if (ReadString(item, "sourceValidation", "") != "supported_span"
                || ReadString(current, "status", "") != "supported_span") return;
            var previous = DialogueStore.SelectObligations2(connection, TestDict("id", id)).SingleOrDefault();
            var old = TryParseJsonObject(ReadString(previous, "payload_json", "{}"));
            bool sameScope = previous != null && (ReadString(previous, "timeline_id", "") == ContinuityTimeline(item)
                || IsVerifiedRestoredMemorySource(connection, previous, ContinuityTimeline(item)))
                && ReadString(previous, "owed_by", "") == ReadFirstString(item, "owed_by", "owedBy", "actorId", "subject")
                && ReadString(previous, "owed_to", "") == ReadFirstString(item, "owed_to", "owedTo", "targetId", "target");
            var original = sameScope ? ReadDictionary(old, "commitmentOriginalSource") ?? TestDict() : TestDict();
            if (original.Count == 0 && sameScope && ReadString(old, "sourceValidation", "") == "supported_span")
                original = ReadDictionary(old, "sourceEvidence") ?? TestDict();
            if (original.Count == 0) original = current;
            item["commitmentOriginalSource"] = CloneDictionary(original);
        }

        private static bool CommitmentSourceTurnVisible(ReignDbConnection connection, Dictionary<string, object> turn,
            DialogueService.KnowledgeAccessContext access)
        {
            if (!PrecisionTurnVisible(turn, access.NpcId, access, connection)) return false;
            if (!new[] { "letter", "correspondence" }.Contains(ReadString(turn, "channel", ""))) return true;
            // A transcript audience is not a receipt that a letter was delivered.
            var p = TryParseJsonObject(ReadString(turn, "payload_json", "{}"));
            string letterId = ReadFirstString(p, "letterId", "sourceLetterId");
            if (letterId.Length == 0 || !WorldSimulationService.TableExists(connection, "letters")) return false;
            var letter = DialogueStore.SelectLetters2(connection, TestDict("id", letterId)).SingleOrDefault();
            return LetterVisibleToObserver(connection, letter, access.NpcId, access, out _);
        }

        private static string CommitmentVisibleSourceText(Dictionary<string, object> turn, DialogueService.KnowledgeAccessContext access)
        {
            if (HasValidatedPlayerSourceProjection(turn)) return ObserverConversationSourceText(turn, access.NpcId);
            string body = ConversationTurnTextForRecall(turn);
            if (ReadString(turn, "role", "") == "player" && ReadString(turn, "speaker_id", "") != access.NpcId)
                body = ProjectCurrentPlayerPromptText(body, access.NpcId, TryParseJsonObject(ReadString(turn, "payload_json", "{}")));
            return ObserverConversationText(body, ReadString(turn, "speaker_id", ""), access.NpcId, ReadString(turn, "role", "") == "player");
        }

        private static Dictionary<string, object> ResolveCommitmentSourceTurn(ReignDbConnection connection,
            Dictionary<string, object> reference, List<DialogueService.KnowledgeAccessContext> audience)
        {
            if (!WorldSimulationService.TableExists(connection, "conversation_turns")) return null;
            string id = ReadString(reference, "turnId", ""), sourceEvent = ReadString(reference, "sourceEventId", "");
            string speaker = ReadString(reference, "speakerId", ""), quote = ReadString(reference, "quote", "");
            string hash = ReadString(reference, "sourceHash", "");
            if (speaker.Length == 0 || quote.Length < 12 || hash.Length == 0 || id.Length == 0 && sourceEvent.Length == 0) return null;
            var matches = DialogueStore.SelectConversationTurns11(connection, TestDict("speaker", speaker, "turn", id, "event", sourceEvent)).Where(row =>
                PromptHash(ConversationTurnTextForRecall(row)) == hash
                && audience.All(a => CommitmentSourceTurnVisible(connection, row, a)
                    && CommitmentVisibleSourceText(row, a).Contains(quote, StringComparison.Ordinal))).ToList();
            return matches.Count == 1 ? matches[0] : null; // Ambiguity never becomes an invented source.
        }

        private static string BuildRelevantCommitmentContext(ReignDbConnection connection, List<Dictionary<string, object>> commitments,
            Dictionary<string, object> payload, List<DialogueService.KnowledgeAccessContext> audience, List<string> sources,
            List<Dictionary<string, object>> diagnostics, string requiredTranscript = "")
        {
            if (ReadBool(payload, "officialMemoryFirewall", false)) return "";
            string query = ReadFirstString(payload, "playerText", "text", "message") + " " + ReadString(payload, "memoryActiveTopic", "");
            // Player query is intact narrative context; no inferred-thought projection.
            var terms = DialogueService.MemoryQueryTerms(query).Where(t => !ExactRecallStopWords.Contains(t, StringComparer.OrdinalIgnoreCase)
                && !new[] { "promise", "promises", "promised", "commitment", "commitments", "word", "give", "shall" }.Contains(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool recall = Regex.IsMatch(query, @"\b(?:promis(?:e|es|ed)|commitments?|pledge[ds]?|oaths?|swore|sworn|agreed|(?:my|your|his|her|our|their|one['â€™]s)\s+word)\b", RegexOptions.IgnoreCase);
            var ranked = commitments.Select(row => new { Row = row, Score = DialogueService.MemoryQueryTerms(ReadString(row, "description", ""))
                .Count(t => terms.Contains(t)) + (recall ? 1 : 0) }).Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score).ThenByDescending(x => ReadDouble(x.Row, "created_day", 0d)).ThenBy(x => ReadString(x.Row, "obligation_id", "")).Take(8);
            var text = new StringBuilder(); var seen = new HashSet<string>(); int exchanges = 0;
            foreach (var candidate in ranked)
            {
                if (exchanges >= 3) break;
                var value = TryParseJsonObject(ReadString(candidate.Row, "payload_json", "{}"));
                var references = new[] { ReadDictionary(value, "commitmentOriginalSource"), ReadDictionary(value, "sourceEvidence") };
                foreach (var reference in references.Where(r => r != null && r.Count > 0))
                {
                    if (exchanges >= 3) break;
                    string obligation = ReadString(candidate.Row, "obligation_id", "");
                    var anchor = ResolveCommitmentSourceTurn(connection, reference, audience);
                    if (anchor == null) { diagnostics.Add(TestDict("obligationId", obligation, "status", "source_unavailable")); continue; }
                    string session = ReadString(anchor, "session_id", ""), exchange = ReadString(anchor, "exchange_id", "");
                    string key = session + "|" + exchange;
                    if (!seen.Add(key)) continue;
                    var rows = exchange.Length == 0 ? new List<Dictionary<string, object>> { anchor }
                        : DialogueStore.SelectConversationTurns12(connection, TestDict("session", session, "exchange", exchange));
                    rows = ExpandPrecisionQuestionContext(connection, rows, audience[0])
                        .Where(row => audience.All(a => CommitmentSourceTurnVisible(connection, row, a)))
                        .OrderBy(row => ReadLong(row, "turn_order", 0)).ToList();
                    // Joint prompts project every contribution to the common visible
                    // spans, including a speaking NPC's own private narration.
                    if (audience.Count > 1) rows = rows.Select(row => {
                        var copy = CloneDictionary(row);
                        string common = ConversationTurnTextForRecall(row);
                        if (ReadString(row, "role", "") == "player")
                            foreach (var a in audience) common = ProjectCurrentPlayerPromptText(common, a.NpcId, TryParseJsonObject(ReadString(row, "payload_json", "{}")));
                        copy["text"] = ObserverConversationText(common, ReadString(row, "speaker_id", ""), "", ReadString(row, "role", "") == "player");
                        return copy;
                    }).ToList();
                    string body = "EXACT ACCEPTED COMMITMENT CONTEXT; obligation_id=" + obligation
                        + "; session=" + session + "; exchange=" + exchange
                        + ". Historical wording explains the terms; the current ledger controls status. This is not a fresh promise or proof of fulfillment.\n"
                        + string.Join("\n", rows.Select(row => CommitmentContextTurn(row, audience[0], requiredTranscript, PlannedPromptSources(payload))));
                    string sourceId = "commitment_context_" + PromptHash(key).Substring(0, 24);
                    string record = RenderContinuityRecord(sourceId, "commitment_source_context", audience[0].NpcId, session,
                        ReadDouble(anchor, "world_day", 0d), 120, true, body);
                    if (exchanges >= 3 || text.Length + record.Length > 6000)
                    { diagnostics.Add(TestDict("obligationId", obligation, "status", "complete_exchange_over_budget", "characters", record.Length)); continue; }
                    text.AppendLine(record); exchanges++; sources.Add(sourceId);
                    diagnostics.Add(TestDict("obligationId", obligation, "status", "included", "sessionId", session, "exchangeId", exchange,
                        "sourceTurnIds", rows.Select(row => ReadString(row, "turn_id", "")).ToArray()));
                }
            }
            return text.ToString();
        }

        private static string CommitmentContextTurn(Dictionary<string, object> row, DialogueService.KnowledgeAccessContext observer, string transcript,
            Dictionary<string, string> supplied = null)
        {
            string id = ReadString(row, "turn_id", "");
            string visible = NormalizePromptSegment(CommitmentVisibleSourceText(row, observer));
            // Only required, whole exchanges survive both allocation passes. An
            // optional candidate, matching quote, or mismatched source ID cannot
            // authorize removing the original fallback from this protected block.
            bool delivered = MemorySourceAlreadySupplied(row, supplied) || id.Length > 0 && visible.Length > 0 && ContinuityPromptRecords(transcript, true)
                .Any(record => record.Required && record.Kind == "accepted_exchange"
                    && record.SourceId.Split(',').Contains(id, StringComparer.Ordinal)
                    && record.Text.Contains(visible, StringComparison.Ordinal));
            return "turn_id=" + id + "; " + (delivered
                ? "Full original supplied in the required chronological exchange; visible_source_hash=" + PromptHash(visible) + "."
                : RenderExactHistoryTurn(row, observer));
        }
        internal static string StableCommitmentId(Dictionary<string, object> item)
        {
            string id = ReadFirstString(item, "obligation_id", "obligationId", "id");
            if (id.Length > 0) return id;
            if (ReadString(item, "interpretationStatus", "") == "model_inferred")
                return "interpretation_" + PromptHash(DialogueCommitmentIdentity(item)).Substring(0, 32);
            // Retries and repeated exact promises do not create a new debt.
            return "obligation_" + PromptHash(Json.Serialize(new[] { ContinuityTimeline(item), ReadString(item, "recordKind", "promise"), ReadString(item, "scope", ""), ReadFirstString(item, "owed_by", "owedBy", "actorId", "subject"),
                ReadFirstString(item, "owed_to", "owedTo", "targetId", "target"), ReadFirstString(item, "description", "text", "promise", "threat", "summary").Trim() })).Substring(0, 32);
        }

        internal static void EnsureLatestAcceptedCommitments(ReignDbConnection connection)
        {
            const string marker = "explicit_accepted_commitment_bootstrap_v1";
            if (!WorldSimulationService.TableExists(connection, "conversation_turns") || !WorldSimulationService.TableExists(connection, "conversation_sessions")
                || ReadString(DialogueStore.SelectSchemaMeta(connection, TestDict("key", marker)).FirstOrDefault(), "value", "") == "1") return;
            // Bootstrap only the latest accepted private contribution per pair.
            // Older promises may have been fulfilled or withdrawn later; absence
            // of a closure record is never permission to resurrect those.
            var rows = DialogueStore.SelectConversationTurns13(connection);
            foreach (var row in rows)
            {
                var participants = DialogueService.TextListFromJson(ReadString(row, "participants_json", "[]"));
                if (participants.Count != 2) continue;
                string speaker = ReadString(row, "speaker_id", ""), peer = ReadString(row, "player_id", "");
                if (!participants.Contains(speaker) || !participants.Contains(peer)) continue;
                string original = ConversationTurnTextForRecall(row);
                var value = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                var session = TryParseJsonObject(ReadString(row, "session_payload", "{}"));
                string timeline = FirstNonEmpty(ReadString(value, "timelineId", ""), ReadString(session, "timelineId", ""));
                if (timeline.Length == 0) continue;
                string turn = ReadString(row, "turn_id", "");
                foreach (string clause in ExplicitPromiseClauses(original))
                {
                    // Existing records, including closed ones, remain authoritative.
                    if (DialogueStore.SelectObligations4(connection, TestDict("speaker", speaker, "peer", peer, "timeline", timeline))
                        .Any(o => ReadString(ReadDictionary(TryParseJsonObject(ReadString(o, "payload_json", "{}")), "sourceEvidence"), "quote", "").Contains(clause, StringComparison.Ordinal))) continue;
                    var write = TestDict("description", clause, "owed_by", speaker, "owed_to", peer, "status", "unresolved", "importance", 0.9d,
                        "participants", participants, "known_by", participants, "visibility", "private", "timelineId", timeline,
                        "worldDay", ReadDouble(row, "world_day", 0d), "created_day", ReadDouble(row, "world_day", 0d), "sourceAcceptedText", original,
                        "sourceEvidence", TestDict("turnId", turn, "speakerId", speaker, "evidenceType", "future_intention", "quote", clause));
                    AttachConversationWriteEvidence(new[] { write }, new List<Dictionary<string, object>> { TestDict("turnId", turn, "speakerId", speaker, "text", original) }, speaker, turn);
                    if (ReadString(write, "sourceValidation", "") == "supported_span") DialogueService.InsertObligation(connection, turn, write, ReadLong(row, "ts", 0), "accepted_commitment_bootstrap");
                }
            }
            DialogueStore.UpsertSchemaMeta(connection, TestDict("key", marker));
        }
    }
}
