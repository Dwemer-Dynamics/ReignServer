using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string ConversationSourceContract = @"
SOURCE ACCOUNTABILITY: Preserve the original speaker, actor, sequence and uncertainty. A reported event is not a witnessed or native-confirmed event. Beliefs, hypotheses, intentions and narrated actions are different evidence types. Do not make a temporary feeling, metaphor or qualified claim into a permanent trait. An answered question stays answered unless explicitly reopened; a reciprocal question does not change who asked the original. Keep separate unresolved concerns separate.
For each lasting write, provide sourceEvidence {turnId,speakerId,actorIds,evidenceType,quote}. Quote an exact span from the supplied source. In turnId use current_player/current_npc for the present exchange; in speakerId use that speaker's supplied canonical character ID, not the turn alias or display name. Valid evidence types are spoken_statement, observation, subjective_belief, hypothesis, future_intention, narrated_action. Only the native engine can establish confirmed_native_result.
For a summary covering contributions from different speakers, also supply sourceEvidenceSpans with each speaker's own exact quote. One real quotation does not establish all clauses in a compound summary. For an addressed factual recall in group dialogue, supply addressedHeroStringIds only when the addressee is unambiguous; never guess whom an ambiguous you means.
For a continuity update linking earlier dialogue, include relatedTurnId, relationKind (answer, reciprocal_question, correction, activity_completion, abandonment) and relatedQuote from that earlier turn. Mark an agenda resolved only for the actual concern satisfied; completing an activity does not settle a separate emotional concern. Prefer original dialogue and acknowledged corrections over an older summary. Do not reconstruct missing wording or invent source IDs.
";

        private static string PairHistoryDimension(string text)
        {
            text ??= "";
            // Candidate SQL is broad; promotion into a permanent foundation is not.
            // In particular, "not intimate, just less performative" is a question
            // about conversation, not evidence of physical intimacy.
            if (Regex.IsMatch(text, @"\b(?:trust you|betrayed me|our partnership)\b", RegexOptions.IgnoreCase))
                return "trust, boundaries and partnership";
            foreach (Match span in Regex.Matches(text, @"[^.!?\r\n]+(?:[.!?]+|$)"))
            {
                string sentence = span.Value.Trim();
                if (sentence.EndsWith("?", StringComparison.Ordinal)
                    || Regex.IsMatch(sentence, @"\b(?:if|imagine|imagined|imagining|suppose|supposing|perhaps|maybe|might|could|would|pretend|pretending|dreamed|dreamt)\b", RegexOptions.IgnoreCase)) continue;
                if (Regex.IsMatch(sentence, @"\b(?:we\s+(?:kissed|slept together|shared a bed|shared a kiss)|kiss\s+we\s+shared|(?:I|you)\s+(?:kissed|kiss)\s+(?:you|me)|(?:don't|do not|won't|will not)\s+kiss\s+(?:you|me)|our\s+(?:first\s+)?kiss)\b", RegexOptions.IgnoreCase))
                    return "physical affection and boundaries";
            }
            return "";
        }

        private static bool HasUncertainty(string text) => Regex.IsMatch(text ?? "",
            @"\b(?:maybe|perhaps|possibly|probably|might|could|seems?|appears?|suspects?|suspected|thinks?|believes?|guesses?)\b", RegexOptions.IgnoreCase);

        private static bool HasExplicitConversationReopening(string citedReply, string currentPlayerText)
        {
            // An unrelated question, or a complaint that an answer was forgotten,
            // does not reopen the resolved record. Require an explicit new request.
            const string verb = @"(?:reopen|revisit|return\s+to|ask(?:\s+(?:you|me|us))?\s+again)";
            string pattern = @"\b(?:(?:can|could|may|shall|should)\s+(?:we|I|you)\s+" + verb
                + @"|(?:let\s+us|let['’]s)\s+" + verb
                + @"|(?:I|we)\s+(?:want|need|would\s+like)\s+to\s+" + verb
                + @"|I(?:['’]ve|\s+have)?\s+changed\s+my\s+mind)\b";
            return Regex.IsMatch(citedReply ?? "", pattern, RegexOptions.IgnoreCase)
                || Regex.IsMatch(currentPlayerText ?? "", pattern, RegexOptions.IgnoreCase);
        }

        private static List<Dictionary<string, object>> CurrentConversationSources(string player, string npc,
            string playerText, string reply, string eventId, string currentTurnId = "")
            => new List<Dictionary<string, object>> {
                new Dictionary<string, object> { ["turnId"] = "current_player", ["speakerId"] = player, ["text"] = playerText ?? "", ["eventId"] = eventId,
                    ["turnAliases"] = new[] { "current", eventId, eventId + "_player", currentTurnId, !string.IsNullOrEmpty(currentTurnId) ? currentTurnId + "_player" : "" } },
                new Dictionary<string, object> { ["turnId"] = "current_npc", ["speakerId"] = npc, ["text"] = reply ?? "", ["eventId"] = eventId,
                    ["turnAliases"] = new[] { "current", eventId, eventId + "_npc", currentTurnId, !string.IsNullOrEmpty(currentTurnId) ? currentTurnId + "_npc_" + npc : "" } }
            };

        private static Dictionary<string, object> BindConversationSource(Dictionary<string, object> write,
            List<Dictionary<string, object>> sources, string fallbackSpeaker, string eventId)
        {
            var claimed = ReadDictionary(write, "sourceEvidence");
            string quote = ReadFirstString(claimed, "quote");
            if (quote.Length == 0) quote = ReadFirstString(write, "evidenceQuote", "supportingQuote", "replyQuote");
            string speaker = ReadString(claimed, "speakerId", fallbackSpeaker);
            string id = ReadString(claimed, "turnId", "");
            string speakerAlias = "";
            // Older instructions left the current turn aliases ambiguous across
            // fields. Resolve only the two server-owned current speakers; this
            // cannot redirect a historical turn, another NPC or a mismatched quote.
            if (speaker == "current_player" || speaker == "current_npc")
            {
                var current = sources.Where(s => ReadString(s, "turnId", "") == speaker
                    && ReadString(s, "eventId", "") == eventId).ToList();
                if (current.Count == 1 && !string.IsNullOrEmpty(ReadString(current[0], "speakerId", "")))
                {
                    speakerAlias = speaker;
                    speaker = ReadString(current[0], "speakerId", "");
                }
            }
            var source = sources.FirstOrDefault(s => ReadString(s, "speakerId", "") == speaker
                // Aliases come only from the accepted server exchange, never from
                // a proposed citation. A current group beat is a valid identifier;
                // a historical/other beat still cannot borrow today's quotation.
                && (id.Length == 0 || id == ReadString(s, "turnId", "") || ReadStringList(s, "turnAliases").Contains(id, StringComparer.Ordinal)) && quote.Length >= 12
                && ReadString(s, "text", "").Contains(quote, StringComparison.Ordinal));
            if (source == null) return new Dictionary<string, object> { ["status"] = "incomplete", ["reason"] = "unverified_source_span", ["sourceEventId"] = eventId };
            string original = ReadString(source, "text", "");
            int start = original.IndexOf(quote, StringComparison.Ordinal);
            string type = ReadString(claimed, "evidenceType", "spoken_statement");
            string[] types = { "spoken_statement", "observation", "subjective_belief", "hypothesis", "future_intention", "narrated_action" };
            if (!types.Contains(type)) type = "spoken_statement";
            // A model cannot promote a quote into a confirmed game result.
            if (HasUncertainty(quote) && type != "subjective_belief") type = "hypothesis";
            // Direct first-person intentions cannot become completed native facts.
            // Do not reclassify every recalled/quoted occurrence of "will".
            else if (Regex.IsMatch(quote.TrimStart(), @"^(?:I|we)\s+(?:will|shall|plan to|intend to|would like to)\b", RegexOptions.IgnoreCase))
                type = "future_intention";
            var bound = new Dictionary<string, object> { ["status"] = "supported_span", ["sourceEventId"] = eventId,
                ["turnId"] = ReadString(source, "turnId", ""), ["speakerId"] = speaker,
                // Actor assertions remain claims; presence is not proof of acting.
                ["claimedActorIds"] = ReadStringList(claimed, "actorIds"), ["evidenceType"] = type,
                ["quote"] = quote, ["start"] = start, ["length"] = quote.Length, ["sourceHash"] = PromptHash(original) };
            if (speakerAlias.Length > 0) bound["resolvedSpeakerAlias"] = speakerAlias;
            return bound;
        }

        private static void AttachConversationWriteEvidence(IEnumerable<Dictionary<string, object>> writes,
            List<Dictionary<string, object>> sources, string owner, string eventId)
        {
            foreach (var write in writes ?? Enumerable.Empty<Dictionary<string, object>>())
            {
                var bound = BindConversationSource(write, sources, owner, eventId);
                // Proposed writes cannot carry their own trusted originals or a
                // stale rejection into the new accepted-source decision.
                write.Remove("originalSources");
                write.Remove("sourceValidationReason");
                write["sourceEvidence"] = bound;
                write["sourceValidation"] = ReadString(bound, "status", "incomplete");
                write["evidenceType"] = ReadString(bound, "evidenceType", "unverified_summary");
                // Retain recoverable source material when the summary cannot be
                // proven; retrieval must prefer this to a confident paraphrase.
                if (ReadString(bound, "status", "") != "supported_span")
                    write["originalSources"] = sources;
                string summary = ReadFirstString(write, "text", "summary", "claim", "meaning", "description");
                if (HasUncertainty(ReadString(bound, "quote", "")) && !HasUncertainty(summary))
                {
                    write["sourceValidation"] = "incomplete";
                    write["sourceValidationReason"] = "qualification_lost";
                    write["originalSources"] = sources;
                }
                AttachDialogueClaimSpanCoverage(write, sources, owner, eventId);
            }
        }

        private static string ConversationContinuityLinkError(ReignDbConnection connection, Dictionary<string, object> write,
            string owner, string timeline, double worldDay)
        {
            string id = ReadString(write, "relatedTurnId", ""), kind = ReadString(write, "relationKind", "");
            if (id.Length == 0 && kind.Length == 0) return "";
            if (!new[] { "answer", "reciprocal_question", "correction", "activity_completion", "abandonment" }.Contains(kind)) return "invalid_relation_kind";
            var row = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id AND status='active';",
                new Dictionary<string, object> { ["id"] = id }).FirstOrDefault();
            if (row == null || !MemoryTimeVisible(row, new KnowledgeAccessContext { NpcId = owner, TimelineId = timeline, WorldDay = worldDay }, connection)
                || (ReadString(row, "speaker_id", "") != owner && !TextListFromJson(ReadString(row, "participants_json", "[]")).Contains(owner)))
                return "related_source_unavailable";
            string quote = ReadString(write, "relatedQuote", "");
            if (quote.Length < 12 || !ConversationTurnTextForRecall(row).Contains(quote, StringComparison.Ordinal)) return "related_source_quote_mismatch";
            if (!ObserverConversationText(ConversationTurnTextForRecall(row), ReadString(row, "speaker_id", ""), owner, ReadString(row, "role", "") == "player").Contains(quote, StringComparison.Ordinal))
                return "related_source_outside_observer_scope";
            if ((kind == "answer" || kind == "reciprocal_question")
                && ConversationInformationRequestForm(ConversationTurnTextForRecall(row), quote).Length == 0) return "related_source_not_question";
            write["relatedSpeakerId"] = ReadString(row, "speaker_id", "");
            write["relatedSourceHash"] = PromptHash(ReadString(row, "text", ""));
            return "";
        }

        private static List<Dictionary<string, object>> ExpandConversationSequence(ReignDbConnection connection,
            List<Dictionary<string, object>> anchors, KnowledgeAccessContext knowledge)
        {
            var result = new List<Dictionary<string, object>>(anchors);
            foreach (var group in anchors.GroupBy(r => ReadString(r, "session_id", "")))
            {
                // Adjacent *complete* exchanges keep questions with answers and
                // reciprocal questions. Historical evaluation still uses as-of guards.
                long first = group.Min(r => ReadLong(r, "turn_order", 0)), last = group.Max(r => ReadLong(r, "turn_order", 0));
                var nearby = QuerySql(connection, @"SELECT * FROM conversation_turns WHERE session_id=$session AND status='active'
AND turn_order BETWEEN $first AND $last ORDER BY turn_order;", new Dictionary<string, object> {
                    ["session"] = group.Key, ["first"] = Math.Max(0, first - 2), ["last"] = last + 4 });
                result.AddRange(nearby.Where(r => MemoryTimeVisible(r, knowledge, connection)
                    && (ReadString(r, "speaker_id", "") == knowledge.NpcId || TextListFromJson(ReadString(r, "participants_json", "[]")).Contains(knowledge.NpcId))));
            }
            return result.GroupBy(r => ReadString(r, "turn_id", "")).Select(g => g.First())
                .OrderBy(r => ReadLong(r, "accepted_sequence", 0)).ThenBy(r => ReadLong(r, "ts", 0)).ThenBy(r => ReadInt(r, "turn_order", 0)).ToList();
        }

        // Search hits keep their complete exchange. Adjacent context is evidence
        // for interpretation, not proof that a question was answered; that belongs
        // to the separate source-linked question state. A returned question needs
        // its answer too, but unrelated neighboring exchanges do not belong here.
        private static List<Dictionary<string, object>> ExpandPrecisionQuestionContext(ReignDbConnection connection,
            List<Dictionary<string, object>> anchors, KnowledgeAccessContext knowledge)
        {
            var result = new List<Dictionary<string, object>>(anchors);
            string Spoken(Dictionary<string, object> row) => Regex.Replace(ObserverConversationText(ReadString(row, "text", ""),
                ReadString(row, "speaker_id", ""), knowledge.NpcId), @"\*[^*]*\*", "");
            bool IsSpokenQuestion(Dictionary<string, object> row)
            {
                string spoken = Spoken(row);
                return ConversationInformationRequestForm(spoken, spoken).Length > 0;
            }
            bool IsReturnQuestion(Dictionary<string, object> row) => Regex.IsMatch(Spoken(row),
                @"\b(?:(?:how|what)\s+about\s+you\b[^?.!]{0,40}|(?:and|but)\s+you\b[^?.!]{0,40}|(?:and|but)\s+(?:what|which|how|where|when|why)\b[^?.!]{0,160}\byou\b[^?.!]{0,80})\?",
                RegexOptions.IgnoreCase);
            bool IsClarification(Dictionary<string, object> row) => Regex.IsMatch(Spoken(row),
                @"\b(?:I\s+(?:meant|mean\b|(?:was\s+not|wasn['’]?t|am\s+not|did\s+not|didn['’]?t)\s+(?:talking|saying|mean|say|suggest|ask))|what\s+I\s+(?:meant|mean)|to\s+(?:clarify|be\s+clear)|that(?:['’]s|\s+is)\s+not\s+what\s+I)\b",
                RegexOptions.IgnoreCase);
            // These are discourse cues, not agreement or resolution labels. A
            // refusal, qualified acceptance or uncertain reply needs its actual
            // preceding statement just as a short answer needs its question.
            // Require a spoken opening; private/quoted/hypothetical cues cannot
            // manufacture a link. Keep at most one such adjacent reply exchange.
            bool IsExplicitReply(Dictionary<string, object> row) => Regex.IsMatch(Spoken(row).TrimStart(),
                @"^(?:yes|no|okay|ok|all\s+right|alright|very\s+well|agreed|understood|fair\s+enough|not\s+yet|I\s+(?:agree|accept|decline|refuse|understand))\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (var group in anchors.GroupBy(row => ReadString(row, "session_id", "")))
            {
                var ordered = group.OrderBy(row => ReadLong(row, "turn_order", 0)).ToList();
                var first = ordered.First(); var last = ordered.Last();
                long start = ReadLong(first, "turn_order", 0), end = ReadLong(last, "turn_order", 0);
                var nearby = QuerySql(connection, @"SELECT * FROM conversation_turns WHERE session_id=$session AND status='active'
AND turn_order BETWEEN $first AND $last ORDER BY turn_order;", TestDict("session", group.Key, "first", Math.Max(0, start - 2), "last", end + 4));
                // Inspect actual neighboring order before visibility filtering:
                // hidden dialogue cannot be skipped to invent adjacency.
                var previous = nearby.LastOrDefault(row => ReadLong(row, "turn_order", 0) < start);
                bool priorQuestion = previous != null && ReadString(first, "role", "") == "player" && ReadString(previous, "role", "") == "npc"
                    && PrecisionTurnVisible(previous, knowledge.NpcId, knowledge, connection) && IsSpokenQuestion(previous);
                if (priorQuestion) result.Add(previous);
                // A correction may answer a mistaken statement with no question
                // mark. Keep its actual preceding exchange, without treating
                // adjacency or these words as proof of accepted supersession.
                bool priorClarification = previous != null && ReadString(first, "role", "") == "player" && IsClarification(first)
                    && ReadString(previous, "role", "") == "npc" && PrecisionTurnVisible(previous, knowledge.NpcId, knowledge, connection);
                if (priorClarification)
                {
                    result.Add(previous);
                    var priorPlayer = nearby.LastOrDefault(row => ReadLong(row, "turn_order", 0) < ReadLong(previous, "turn_order", 0));
                    if (priorPlayer != null && ReadString(priorPlayer, "role", "") == "player"
                        && PrecisionTurnVisible(priorPlayer, knowledge.NpcId, knowledge, connection)) result.Add(priorPlayer);
                }
                bool priorReply = previous != null && ReadString(first, "role", "") == "player" && IsExplicitReply(first)
                    && ReadString(previous, "role", "") == "npc" && PrecisionTurnVisible(previous, knowledge.NpcId, knowledge, connection);
                if (priorReply) result.Add(previous);
                bool waitingForPlayer = ReadString(last, "role", "") == "npc" && IsSpokenQuestion(last);
                bool waitingForNpc = (priorQuestion || priorClarification || priorReply) && ReadString(last, "role", "") == "player";
                bool afterAnswer = priorQuestion && ReadString(last, "role", "") == "npc";
                bool afterNpcStatement = ReadString(last, "role", "") == "npc";
                bool explicitReplyUsed = priorReply;
                foreach (var next in nearby.Where(row => ReadLong(row, "turn_order", 0) > end).Take(4))
                {
                    if (!PrecisionTurnVisible(next, knowledge.NpcId, knowledge, connection)) break;
                    string role = ReadString(next, "role", "");
                    bool explicitReply = role == "player" && afterNpcStatement && !explicitReplyUsed && IsExplicitReply(next);
                    if (role == "player" && (waitingForPlayer || waitingForNpc || afterAnswer && IsReturnQuestion(next)
                        || afterNpcStatement && IsClarification(next) || explicitReply))
                    {
                        result.Add(next);
                        explicitReplyUsed |= explicitReply;
                        waitingForPlayer = false; waitingForNpc = true; afterAnswer = false; afterNpcStatement = false;
                    }
                    else if (role == "npc" && waitingForNpc)
                    {
                        result.Add(next); // actual acknowledgment or return answer, not a resolution inference
                        waitingForNpc = false; afterAnswer = true; afterNpcStatement = true;
                    }
                    else break;
                }
            }
            return result.GroupBy(row => ReadString(row, "turn_id", "")).Select(group => group.First())
                .OrderBy(row => ReadLong(row, "accepted_sequence", 0)).ThenBy(row => ReadLong(row, "ts", 0))
                .ThenBy(row => ReadInt(row, "turn_order", 0)).ToList();
        }

        private static List<MemoryEvidence> LinkedConversationEvidence(ReignDbConnection connection, string owner, string peer,
            string timeline, string query, KnowledgeAccessContext knowledge, IEnumerable<string> linkedSourceIds = null)
        {
            var results = new List<MemoryEvidence>();
            var terms = MemoryQueryTerms(query ?? "").Where(t => !ExactRecallStopWords.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
            knowledge ??= new KnowledgeAccessContext { NpcId = owner, PlayerId = peer, TimelineId = timeline };
            string document = "to_tsvector('english',COALESCE(payload_json::jsonb->>'relatedQuote','') || ' ' || "
                + "COALESCE(payload_json::jsonb->>'subject','') || ' ' || COALESCE(payload_json::jsonb->>'evidenceQuote',''))";
            var parameters = TestDict("owner", owner, "peer", peer, "timeline", timeline, "query", BuildFtsQuery(terms),
                "day", knowledge.WorldDay, "asOf", knowledge.AsOfTimestamp, "offset", 0);
            var relatedIds = (linkedSourceIds ?? Enumerable.Empty<string>()).Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal).Take(64).ToList();
            var relatedParameters = relatedIds.Select((id, i) => {
                string key = "related" + i; parameters[key] = id; return "$" + key;
            }).ToList();
            string relatedMatch = relatedParameters.Count == 0 ? "" : " OR payload_json::jsonb->>'relatedTurnId' IN ("
                + string.Join(",", relatedParameters) + ")";
            string search = @"SELECT * FROM conversation_continuity WHERE owner_id=$owner AND " + MemorySourceTimelineSql("conversation_continuity", access: knowledge) + @"
AND (peer_id='' OR peer_id=$peer) AND payload_json::jsonb->>'relatedTurnId'<>'' AND status<>'invalidated'
AND updated_ts<=$asOf AND CASE WHEN jsonb_typeof(payload_json::jsonb->'worldDay')='number'
THEN (payload_json::jsonb->>'worldDay')::double precision ELSE 0 END<=$day"
                + (terms.Count == 0 ? "" : " AND (" + document + " @@ websearch_to_tsquery('english',$query)" + relatedMatch + ")")
                + " ORDER BY " + (terms.Count == 0 ? "" : "ts_rank_cd(" + document + ",websearch_to_tsquery('english',$query),32) DESC,")
                + "updated_ts DESC,record_id LIMIT 64 OFFSET $offset;";
            // Rank the eligible lifetime history first. Page past unavailable
            // originals instead of letting 32 newer links hide an old answer.
            for (int offset = 0; results.Count < 6; offset += 64)
            {
                parameters["offset"] = offset;
                var rows = QuerySql(connection, search, parameters);
                foreach (var row in rows.Where(r => MemoryTimeVisible(r, knowledge, connection)))
                {
                var link = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                string related = ReadString(link, "relatedTurnId", ""), relation = ReadString(link, "relationKind", "");
                string quote = ReadString(link, "relatedQuote", "");
                var source = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id AND status='active';",
                    new Dictionary<string, object> { ["id"] = related }).FirstOrDefault();
                if (source == null || !PrecisionTurnVisible(source, owner, knowledge, connection)) continue;
                if (quote.Length < 12 || !ReadString(source, "text", "").Contains(quote, StringComparison.Ordinal)
                    || !ObserverConversationText(ReadString(source, "text", ""), ReadString(source, "speaker_id", ""), owner, ReadString(source, "role", "") == "player").Contains(quote, StringComparison.Ordinal)
                    || ReadString(link, "relatedSpeakerId", "") != ReadString(source, "speaker_id", "")
                    || ReadString(link, "relatedSourceHash", "") != PromptHash(ReadString(source, "text", ""))) continue;
                var response = QuerySql(connection, "SELECT * FROM conversation_turns WHERE event_id=$event AND status='active' ORDER BY turn_order;",
                    new Dictionary<string, object> { ["event"] = ReadString(row, "source_event_id", "") })
                    .Where(r => PrecisionTurnVisible(r, owner, knowledge, connection)).ToList();
                if (response.Count == 0) continue; // Never substitute a derivative for missing original evidence.
                var originals = new[] { source }.Concat(response).GroupBy(r => ReadString(r, "turn_id", "")).Select(g => g.First()).ToList();
                string text = "LINK " + relation + "; status=" + ReadString(row, "status", "")
                    + ". This applies only to the linked question/claim/activity, not separate concerns.\n"
                    + string.Join("\n", originals.Select(r => RenderExactHistoryTurn(r, knowledge)));
                var evidence = EvidenceFromRow(row, "record_id", text, "resolved_sequence", owner, true, 105);
                evidence.SourceTurns = originals.Select(r => TestDict("id", ReadString(r, "turn_id", ""),
                    "speakerId", ReadString(r, "speaker_id", ""), "role", ReadString(r, "role", ""),
                    "hash", MemoryTranscriptSourceHash(r), "text", RenderExactHistoryTurn(r, knowledge))).ToList();
                results.Add(evidence);
                if (results.Count == 6) break;
                }
                if (rows.Count < 64) break;
            }
            return results;
        }
    }
}
