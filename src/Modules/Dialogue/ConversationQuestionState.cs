using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string ConversationQuestionContract = @"
QUESTION AND ANSWER STATE: Answer receipt and agreement are separate. An answer may be understood but unconvincing. Do not say it was never supplied. A narrower follow-up is a NEW question; do not move the original demand. Other NPCs speaking in the same player beat do not give the player an opportunity to answer. Emit up to four source-linked changes when needed to retain an answer and a distinct new follow-up together.
Use continuityWrites kind=question to link an important question or explicit request for information and its answers. Requests such as tell me how or please explain need not end in a question mark. A declarative recollection, private thought, reported request or rhetorical observation is not a new demand. Supply questionState {question:{turnId,speakerId,quote}, addresseeIds:[], answerState:unanswered/answered/partial/deferred/refused/uncertain, answers:[{turnId,speakerId,quote}], evaluation:unassessed/convincing/unconvincing, followUpTo:""""}. Source quotes are exact, including punctuation. current_player/current_npc refer to this exchange; otherwise use supplied canonical turn IDs. Use status=resolved for answered/refused, active for partial/unanswered/uncertain, deferred for deferred. The outer evidenceQuote must cite your current reply acknowledging this update. Never infer evasion merely from uncertainty or disagreement. Give a new narrower question a different record; followUpTo may link its predecessor. Never assign another person's answer or invent an acknowledgment. Empty/omitted writes preserve prior answers. For other continuity kinds, omit questionState or return null.
";

        // This only identifies a supported speech form. It does not establish
        // motive, evasion, agreement, or whether a rhetorical question needs an answer.
        // Check the original boundary so quoting part of reported speech cannot
        // manufacture a new request by stripping its introductory words.
        private static string ConversationInformationRequestForm(string source, string quote, int start = -1)
        {
            source ??= ""; quote ??= "";
            if (start < 0) start = source.IndexOf(quote, StringComparison.Ordinal);
            if (quote.Length < 4 || start < 0 || start + quote.Length > source.Length
                || !string.Equals(source.Substring(start, quote.Length), quote, StringComparison.Ordinal)) return "";
            if (quote.Contains('?')) return "interrogative";
            // Information requests can take a nominal object as well as a wh-clause:
            // "tell me something" and "tell us your reason" still invite an answer.
            // Keep physical "show/give me" imperatives outside that extension.
            const string request = @"(?:(?:tell|show)\s+(?:me|us)\s+(?:what|how|why|where|when|who|which|whether|about)\b|tell\s+(?:me|us)\s+(?:(?:something|anything|everything|more)\b|(?:a|an|the|your|our|one|some)\s+\S)|(?:explain|describe|clarify)\s+(?:to\s+(?:me|us)\s+)?\S)";
            foreach (Match match in Regex.Matches(quote, @"(?:^|[.!\r\n])\s*(?<request>(?:please\s+)?(?<conditional>if\s+you\s+(?:(?:can|could|would|will)\s+)?)?" + request + ")",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                int requestStart = start + match.Groups["request"].Index;
                int previous = requestStart - 1;
                bool lineBoundary = false;
                while (previous >= 0 && char.IsWhiteSpace(source[previous]))
                { lineBoundary |= source[previous] == '\r' || source[previous] == '\n'; previous--; }
                if (!lineBoundary && previous >= 0 && source[previous] != '.' && source[previous] != '!' && source[previous] != '?') continue;
                return match.Groups["conditional"].Success ? "conditional_information_request" : "direct_information_request";
            }
            return "";
        }

        private static Dictionary<string, object> ResolveQuestionSource(ReignDbConnection connection,
            Dictionary<string, object> requested, Dictionary<string, object> payload, string owner, string peer,
            string eventId, string reply)
        {
            string id = ReadString(requested, "turnId", ""), quote = ReadString(requested, "quote", "");
            if (quote.Length < 4 || quote.Length > 12000) return null;
            string requestedSpeaker = ReadString(requested, "speakerId", "");
            string letterSourceId = id.StartsWith("letter_", StringComparison.Ordinal) ? id
                : id == "current_npc" && eventId.StartsWith("letter_", StringComparison.Ordinal) ? eventId : "";
            if (letterSourceId.Length > 0)
                return ResolveLetterQuestionSource(connection, letterSourceId, quote, requestedSpeaker, owner, peer, payload);
            Dictionary<string, object> row = null;
            string text = "", speaker = "";
            if (id == "current_player" || id == "current_npc")
            {
                speaker = id == "current_npc" ? owner : peer;
                text = id == "current_npc" ? reply : ReadFirstString(payload, "playerText", "text", "message");
                if (speaker.Length == 0 || !string.Equals(requestedSpeaker, speaker, StringComparison.OrdinalIgnoreCase)) return null;
                if (TableExists(connection, "conversation_turns"))
                    row = QuerySql(connection, @"SELECT * FROM conversation_turns WHERE event_id=$event AND speaker_id=$speaker
AND text=$text AND status='active' ORDER BY turn_order DESC LIMIT 1;",
                        TestDict("event", eventId, "speaker", speaker, "text", text)).FirstOrDefault();
            }
            else if (id.Length > 0 && TableExists(connection, "conversation_turns"))
            {
                row = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id AND status='active';", TestDict("id", id)).FirstOrDefault();
                if (row == null || !PrecisionTurnVisible(row, owner, BuildKnowledgeAccessContext(owner, peer, "", payload), connection)) return null;
                text = ReadString(row, "text", ""); speaker = ReadString(row, "speaker_id", "");
                if (!string.Equals(requestedSpeaker, speaker, StringComparison.OrdinalIgnoreCase)) return null;
            }
            else return null;
            int start = text.IndexOf(quote, StringComparison.Ordinal);
            if (start < 0) return null;
            // A question in someone's internal narration is not a spoken demand.
            if (Regex.Matches(text, @"\*[^*]*\*", RegexOptions.Singleline).Cast<Match>()
                .Any(m => start < m.Index + m.Length && start + quote.Length > m.Index)) return null;
            return TestDict("turnId", row == null ? "" : ReadString(row, "turn_id", ""),
                "sourceAlias", id, "eventId", row == null ? eventId : ReadString(row, "event_id", ""),
                "speakerId", speaker, "quote", quote, "start", start, "length", quote.Length,
                "sourceHash", PromptHash(text), "requestForm", ConversationInformationRequestForm(text, quote, start),
                "worldDay", row == null ? ReadDouble(payload, "worldDay", 0d) : ReadDouble(row, "world_day", 0d),
                "sequence", row == null ? long.MaxValue : ReadLong(row, "accepted_sequence", 0),
                "sessionId", row == null ? GroupConversationSessionId(payload) : ReadString(row, "session_id", ""),
                "beatId", row == null ? ReadFirstString(payload, "sceneTurnId", "exchangeId", "turnId")
                    : ReadFirstString(TryParseJsonObject(ReadString(row, "payload_json", "{}")), "sceneTurnId", "exchangeId", "turnId"),
                "audienceIds", row == null ? new List<string> { owner, peer }.Where(s => s.Length > 0).Distinct().ToList()
                    : TextListFromJson(ReadString(row, "participants_json", "[]")));
        }

        private static bool QuestionSourceAfter(Dictionary<string, object> answer, Dictionary<string, object> question)
        {
            if (ReadString(answer, "eventId", "") == ReadString(question, "eventId", ""))
            {
                string a = ReadString(answer, "sourceAlias", ""), q = ReadString(question, "sourceAlias", "");
                if (q == "current_npc" && a == "current_player") return false;
                if (q == "current_player" && a == "current_npc") return true;
                if (ReadString(answer, "turnId", "") == ReadString(question, "turnId", ""))
                    return ReadInt(answer, "start", 0) >= ReadInt(question, "start", 0) + ReadInt(question, "length", 0);
            }
            return ReadDouble(answer, "worldDay", 0d) >= ReadDouble(question, "worldDay", 0d)
                && ReadLong(answer, "sequence", 0) > ReadLong(question, "sequence", 0);
        }

        private static string PrepareQuestionContinuityWrite(ReignDbConnection connection, Dictionary<string, object> write,
            Dictionary<string, object> payload, string owner, string peer, string eventId, string reply)
        {
            var state = ReadDictionary(write, "questionState");
            if (state == null) return "question_state_missing";
            var question = ResolveQuestionSource(connection, ReadDictionary(state, "question"), payload, owner, peer, eventId, reply);
            if (question == null || ReadString(question, "requestForm", "").Length == 0) return "question_source_unverified";
            var addressees = ReadStringList(state, "addresseeIds").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var audience = ReadStringList(question, "audienceIds");
            string asker = ReadString(question, "speakerId", "");
            if (addressees.Count == 0 || addressees.Any(a => a == asker || !audience.Contains(a, StringComparer.OrdinalIgnoreCase)))
                return "question_addressee_unverified";
            string answerState = ReadString(state, "answerState", "uncertain");
            string evaluation = ReadString(state, "evaluation", "unassessed");
            if (!new[] { "unanswered", "answered", "partial", "deferred", "refused", "uncertain" }.Contains(answerState)
                || !new[] { "unassessed", "convincing", "unconvincing" }.Contains(evaluation)) return "question_invalid_state";
            var answers = new List<Dictionary<string, object>>();
            if (ReadDictionaryList(state, "answers").Count > 8) return "question_too_many_answer_spans";
            foreach (var supplied in ReadDictionaryList(state, "answers"))
            {
                var source = ResolveQuestionSource(connection, supplied, payload, owner, peer, eventId, reply);
                if (source == null || !addressees.Contains(ReadString(source, "speakerId", ""), StringComparer.OrdinalIgnoreCase))
                    return "question_answer_source_unverified";
                if (!QuestionSourceAfter(source, question)) return "question_answer_precedes_question";
                answers.Add(source);
            }
            if ((answerState == "answered" || answerState == "partial" || answerState == "deferred" || answerState == "refused") && answers.Count == 0)
                return "question_answer_required";
            if (answerState == "unanswered" && answers.Count > 0) return "question_answer_cannot_be_erased";
            if (evaluation != "unassessed" && answers.Count == 0) return "question_evaluation_without_answer";
            string id = ReadString(write, "id", "");
            if (id.Length == 0) id = "question_" + PromptHash(ContinuityTimeline(payload) + "|" + owner + "|"
                + ReadString(question, "sourceHash", "") + "|" + ReadString(question, "eventId", "") + "|" + ReadString(question, "quote", "")).Substring(0, 32);
            id = CurrentContinuityRecordId(connection, ContinuityTimeline(payload), owner, id);
            var old = QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id=$id;", TestDict("id", id)).FirstOrDefault();
            if (old != null)
            {
                var oldState = ReadDictionary(TryParseJsonObject(ReadString(old, "payload_json", "{}")), "questionState");
                var oldQuestion = ReadDictionary(oldState, "question");
                if (oldQuestion == null || ReadString(oldQuestion, "sourceHash", "") != ReadString(question, "sourceHash", "")
                    || ReadString(oldQuestion, "quote", "") != ReadString(question, "quote", "")
                    || ReadString(oldQuestion, "speakerId", "") != asker) return "question_identity_changed";
                if (!ReadStringList(oldState, "addresseeIds").OrderBy(x => x).SequenceEqual(addressees.OrderBy(x => x))) return "question_addressee_changed";
                string previousAnswer = ReadString(oldState, "answerState", "uncertain");
                if (previousAnswer == "answered" && answerState != "answered"
                    && !HasExplicitConversationReopening(ReadString(write, "evidenceQuote", ""), ReadString(payload, "playerText", "")))
                    return "answered_question_requires_explicit_reopening";
            }
            string followUp = CurrentContinuityRecordId(connection, ContinuityTimeline(payload), owner, ReadString(state, "followUpTo", ""));
            if (followUp.Length > 0 && (followUp == id || QuerySql(connection,
                "SELECT record_id FROM conversation_continuity WHERE record_id=$id AND owner_id=$owner AND " + MemorySourceTimelineSql("conversation_continuity", access: BuildKnowledgeAccessContext(owner, peer, "", payload)) + " AND kind='question';",
                TestDict("id", followUp, "timeline", ContinuityTimeline(payload), "owner", owner)).Count != 1)) return "question_followup_unverified";
            write["id"] = id;
            write["status"] = answerState == "answered" || answerState == "refused" ? "resolved" : answerState == "deferred" ? "deferred" : "active";
            if (ReadString(write, "status", "") == "resolved" && ReadString(write, "transitionReason", "").Length == 0)
                write["transitionReason"] = "Source-linked " + answerState + "; evaluation remains separate.";
            write["questionState"] = TestDict("schema", "reign-question-state-v1", "question", question, "askerId", asker,
                "addresseeIds", addressees, "answerState", answerState, "answers", answers, "evaluation", evaluation,
                "followUpTo", followUp, "interpretation", "speaker_proposed_source_link");
            return "";
        }

        private static bool QuestionOriginalStillVerified(ReignDbConnection connection, Dictionary<string, object> source,
            string observer, KnowledgeAccessContext access)
        {
            string id = ReadString(source, "turnId", "");
            if (id.Length == 0) return false; // Keep the record recoverable, but do not certify a missing original.
            if (id.StartsWith("letter_", StringComparison.Ordinal))
            {
                var letter = QuerySql(connection, "SELECT * FROM letters WHERE letter_id=$id;", TestDict("id", id)).FirstOrDefault();
                string body = ReadString(letter, "body", ""), span = ReadString(source, "quote", "");
                return LetterVisibleToObserver(connection, letter, observer, access, out _)
                    && ReadString(letter, "sender_id", "") == ReadString(source, "speakerId", "")
                    && PromptHash(body) == ReadString(source, "sourceHash", "") && span.Length >= 4 && body.Contains(span, StringComparison.Ordinal);
            }
            var row = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id AND status='active';", TestDict("id", id)).FirstOrDefault();
            string text = ReadString(row, "text", ""), quote = ReadString(source, "quote", "");
            return row != null && PrecisionTurnVisible(row, observer, access, connection)
                && ReadString(row, "speaker_id", "") == ReadString(source, "speakerId", "")
                && PromptHash(text) == ReadString(source, "sourceHash", "") && quote.Length >= 4
                && text.Contains(quote, StringComparison.Ordinal);
        }

        private static string BuildQuestionContinuityPrompt(ReignDbConnection connection, Dictionary<string, object> payload,
            string owner, string peer, List<string> sourceIds)
        {
            var terms = MemoryQueryTerms(ReadFirstString(payload, "playerText", "text", "message") + " " + ReadString(payload, "memoryActiveTopic", ""))
                .Where(t => !ExactRecallStopWords.Contains(t, StringComparer.OrdinalIgnoreCase)).Distinct().Take(16).ToList();
            var access = BuildKnowledgeAccessContext(owner, peer, "", payload);
            var parameters = TestDict("owner", owner, "peer", peer, "timeline", ContinuityTimeline(payload),
                "day", access.WorldDay, "timestamp", access.AsOfTimestamp);
            string eligible = @"SELECT * FROM conversation_continuity WHERE owner_id=$owner AND " + MemorySourceTimelineSql("conversation_continuity", access: access) + @"
AND kind='question' AND status<>'invalidated' AND (peer_id='' OR peer_id=$peer)
AND COALESCE((payload_json::jsonb->>'worldDay')::double precision,0)<=$day AND updated_ts<=$timestamp";
            var candidates = QuerySql(connection, eligible + " ORDER BY updated_ts DESC,record_id LIMIT 4;", parameters);
            // Search the full eligible lifetime, then bound the ranked result.
            // No recency-only candidate cap makes early answers unreachable.
            if (terms.Count > 0)
            {
                var scores = new List<string>();
                for (int i = 0; i < terms.Count; i++)
                {
                    parameters["term" + i] = "%" + terms[i].ToLowerInvariant() + "%";
                    scores.Add("CASE WHEN lower(payload_json) LIKE $term" + i + " THEN 1 ELSE 0 END");
                }
                string score = "(" + string.Join("+", scores) + ")";
                candidates.AddRange(QuerySql(connection, eligible + " AND " + score + ">0 ORDER BY " + score
                    + " DESC,updated_ts DESC,record_id LIMIT 8;", parameters));
            }
            var selected = candidates.Where(r => MemoryTimeVisible(r, access, connection))
                .GroupBy(r => ReadString(r, "record_id", "")).Select(g => g.First()).Take(12);
            var result = new StringBuilder();
            var gaps = new List<string>();
            var selectedIds = new List<string>();
            foreach (var row in selected)
            {
                var record = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                var state = ReadDictionary(record, "questionState");
                var question = ReadDictionary(state, "question");
                var answers = ReadDictionaryList(state, "answers");
                string id = ReadString(row, "record_id", "");
                if (!QuestionOriginalStillVerified(connection, question, owner, access)
                    || answers.Any(a => !QuestionOriginalStillVerified(connection, a, owner, access))) { gaps.Add(id); continue; }
                var body = new StringBuilder("QUESTION STATE — source-linked interpretation, not a motive verdict\n")
                    .Append("id=").Append(id).Append(" revision=").Append(ReadInt(row, "revision", 0))
                    .Append("; asker=").Append(ReadString(state, "askerId", ""))
                    .Append("; speech form=").Append(ReadString(question, "requestForm", "historical_question"))
                    .Append("; addressees=").Append(string.Join(",", ReadStringList(state, "addresseeIds")))
                    .Append("; answer received=").Append(ReadString(state, "answerState", "uncertain"))
                    .Append("; convincing to this observer=").AppendLine(ReadString(state, "evaluation", "unassessed"))
                    .Append("Question [").Append(ReadString(question, "turnId", "")).Append("]: ").AppendLine(ReadString(question, "quote", ""));
                foreach (var answer in answers)
                    body.Append("Answer by ").Append(ReadString(answer, "speakerId", "")).Append(" [")
                        .Append(ReadString(answer, "turnId", "")).Append("]: ").AppendLine(ReadString(answer, "quote", ""));
                if (ReadString(state, "answerState", "") == "answered") body.AppendLine("This question has an answer. Disagreement does not make it unanswered; a new detail needs its own follow-up.");
                if (ReadString(state, "followUpTo", "").Length > 0) body.AppendLine("New narrower follow-up to " + ReadString(state, "followUpTo", "") + "; preserve the prior answer.");
                sourceIds.Add(id);
                selectedIds.Add(id);
                result.AppendLine(RenderContinuityRecord(id, "question_answer_bundle", owner, ReadString(question, "sessionId", ""),
                    ReadDouble(record, "worldDay", 0d), 120, true, body.ToString()));
            }
            payload["questionStateEvidence"] = TestDict("sourceIds", selectedIds,
                "unverifiedOriginals", gaps, "coverage", gaps.Count == 0 ? "selected_originals_verified" : "incomplete");
            if (gaps.Count > 0) result.AppendLine("Some stored question links lack verified original evidence. Do not treat them as proof of evasion or a completed answer.");
            return result.ToString();
        }
    }
}
