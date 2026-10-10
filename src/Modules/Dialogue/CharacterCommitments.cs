using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string CharacterCommitmentContract = @"
PERSISTENT PROMISES AND COMMITMENTS: Treat the protected ledger as current character knowledge, separate from recalled conversation. Keep promises, conditions, refusals and fulfilled or withdrawn commitments distinct. Do not renew a closed commitment or claim an intention has happened. A promise does not execute a gameplay action or override current consent or native authority.
When this exchange creates or changes a durable promise, use hidden obligationWrites. Write one independently supported commitment per item; this collection may contain several items when several distinct promises are made. Keep conditions in the same item as the promise they qualify and quote the entire qualified promise in sourceEvidence. For closure or revision use the supplied obligation_id and status (fulfilled, withdrawn, superseded, unresolved or active). Never close a promise merely because its subject was mentioned. Only create a promise actually expressed by its owner, not an expectation, hypothetical, request or third-party report.";

        private static void EnsureCharacterCommitmentTimeline(ReignDbConnection connection)
        {
            // Dialogue predates the Court timeline column. Its accepted-source
            // payload is authoritative only for untouched default-column rows.
            EnsureDatabaseColumn(connection, "obligations", "timeline_id", "TEXT NOT NULL DEFAULT 'main'");
            const string marker = "character_commitment_timeline_v1";
            if (ReadString(QuerySql(connection, "SELECT value FROM schema_meta WHERE key=$key;", TestDict("key", marker)).FirstOrDefault(), "value", "") == "1") return;
            ExecuteSql(connection, @"UPDATE obligations o SET timeline_id=o.payload_json::jsonb->>'timelineId'
WHERE o.timeline_id='main' AND COALESCE((to_jsonb(o)->>'revision')::bigint,0)=0
AND COALESCE(o.payload_json::jsonb->>'timelineId','') NOT IN ('','main')
AND o.payload_json::jsonb->>'sourceValidation'='supported_span'
AND o.payload_json::jsonb->'sourceEvidence'->>'status'='supported_span';");
            ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES($key,'1');", TestDict("key", marker));
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
                if (!MergedInteractionParticipantProfiles(payload).Select(CharacterIdFrom).Contains(addressed)) return;
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
            Dictionary<string, object> payload, List<string> sources, IEnumerable<string> additionalObservers = null)
        {
            if (!TableExists(connection, "obligations")) return "";
            var access = BuildKnowledgeAccessContext(observer, peer, "", payload);
            var rows = QuerySql(connection, "SELECT * FROM obligations WHERE status IN ('unresolved','active','open','pending') AND "
                + MemoryEligibilitySql("obligations") + " AND " + MemorySourceTimelineSql("obligations", access: access)
                + " AND COALESCE(payload_json,'{}') NOT LIKE '%\"continuityQuarantined\":true%' ORDER BY created_day,obligation_id;",
                new Dictionary<string, object> { ["observer"] = observer, ["timeline"] = access.TimelineId });
            var text = new StringBuilder("CURRENT KNOWN PROMISES AND COMMITMENTS — independent of conversation history\n");
            int included = 0;
            var eligible = new List<Dictionary<string, object>>();
            var audience = new[] { access }.Concat((additionalObservers ?? Enumerable.Empty<string>())
                .Distinct().Where(other => other != observer).Select(other => BuildKnowledgeAccessContext(other, peer, "", payload))).ToList();
            foreach (var row in rows)
            {
                if (!KnowledgeRowVisibleToNpc("obligations", row, access, connection)) continue;
                if ((additionalObservers ?? Enumerable.Empty<string>()).Any(other => !KnowledgeRowVisibleToNpc("obligations", row,
                    BuildKnowledgeAccessContext(other, peer, "", payload), connection))) continue;
                if (ReadBool(payload, "officialMemoryFirewall", false)
                    && !new[] { "world", "global", "common", "public" }.Contains(ReadString(row, "visibility", "").ToLowerInvariant())) continue;
                var value = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                var evidence = ReadDictionary(value, "sourceEvidence");
                if (ReadString(value, "sourceValidation", "") != "supported_span"
                    || ReadString(evidence, "status", "") != "supported_span") continue;
                string id = ReadString(row, "obligation_id", "");
                sources.Add(id);
                text.AppendLine(RenderContinuityRecord(id, "active_commitment", observer, "", ReadDouble(row, "created_day", 0d), 121, true,
                    "obligation_id=" + id + "; status=" + ReadString(row, "status", "") + "; owed by=" + ReadString(row, "owed_by", "")
                    + "; owed to=" + ReadString(row, "owed_to", "") + "; commitment: " + ReadString(row, "description", "")
                    + "; accepted source quote: " + ReadString(evidence, "quote", "")
                    + "; source event=" + ReadString(evidence, "sourceEventId", "") + "; source speaker=" + ReadString(evidence, "speakerId", "")
                    + "; source turn=" + ReadString(evidence, "turnId", "")));
                included++; eligible.Add(row);
            }
            var diagnostics = new List<Dictionary<string, object>>();
            if (included > 0) text.Append(BuildRelevantCommitmentContext(connection, eligible, payload, audience, sources, diagnostics));
            payload["characterCommitmentEvidence"] = TestDict("activeCount", included, "observerId", observer, "timelineId", ContinuityTimeline(payload), "sourceContexts", diagnostics);
            return included == 0 ? "" : text.ToString();
        }

        // These references are server-owned. Proposed metadata cannot substitute
        // an unrelated conversation for the original terms of an existing debt.
        private static void PreserveCommitmentOriginalSource(ReignDbConnection connection, string id,
            string eventId, Dictionary<string, object> item)
        {
            item.Remove("commitmentOriginalSource");
            var current = ReadDictionary(item, "sourceEvidence");
            if (ReadString(item, "sourceValidation", "") != "supported_span"
                || ReadString(current, "status", "") != "supported_span") return;
            var previous = QuerySql(connection, "SELECT * FROM obligations WHERE obligation_id=$id;", TestDict("id", id)).SingleOrDefault();
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
            KnowledgeAccessContext access)
        {
            if (!PrecisionTurnVisible(turn, access.NpcId, access, connection)) return false;
            if (!new[] { "letter", "correspondence" }.Contains(ReadString(turn, "channel", ""))) return true;
            // A transcript audience is not a receipt that a letter was delivered.
            var p = TryParseJsonObject(ReadString(turn, "payload_json", "{}"));
            string letterId = ReadFirstString(p, "letterId", "sourceLetterId");
            if (letterId.Length == 0 || !TableExists(connection, "letters")) return false;
            var letter = QuerySql(connection, "SELECT * FROM letters WHERE letter_id=$id;", TestDict("id", letterId)).SingleOrDefault();
            return LetterVisibleToObserver(connection, letter, access.NpcId, access, out _);
        }

        private static string CommitmentVisibleSourceText(Dictionary<string, object> turn, KnowledgeAccessContext access)
        {
            if (HasValidatedPlayerSourceProjection(turn)) return ObserverConversationSourceText(turn, access.NpcId);
            string body = ConversationTurnTextForRecall(turn);
            if (ReadString(turn, "role", "") == "player" && ReadString(turn, "speaker_id", "") != access.NpcId)
                body = ProjectCurrentPlayerPromptText(body, access.NpcId, TryParseJsonObject(ReadString(turn, "payload_json", "{}")));
            return ObserverConversationText(body, ReadString(turn, "speaker_id", ""), access.NpcId, ReadString(turn, "role", "") == "player");
        }

        private static Dictionary<string, object> ResolveCommitmentSourceTurn(ReignDbConnection connection,
            Dictionary<string, object> reference, List<KnowledgeAccessContext> audience)
        {
            if (!TableExists(connection, "conversation_turns")) return null;
            string id = ReadString(reference, "turnId", ""), sourceEvent = ReadString(reference, "sourceEventId", "");
            string speaker = ReadString(reference, "speakerId", ""), quote = ReadString(reference, "quote", "");
            string hash = ReadString(reference, "sourceHash", "");
            if (speaker.Length == 0 || quote.Length < 12 || hash.Length == 0 || id.Length == 0 && sourceEvent.Length == 0) return null;
            var matches = QuerySql(connection, @"SELECT * FROM conversation_turns
WHERE status='active' AND speaker_id=$speaker AND (turn_id=$turn OR event_id=$event);",
                TestDict("speaker", speaker, "turn", id, "event", sourceEvent)).Where(row =>
                PromptHash(ConversationTurnTextForRecall(row)) == hash
                && audience.All(a => CommitmentSourceTurnVisible(connection, row, a)
                    && CommitmentVisibleSourceText(row, a).Contains(quote, StringComparison.Ordinal))).ToList();
            return matches.Count == 1 ? matches[0] : null; // Ambiguity never becomes an invented source.
        }

        private static string BuildRelevantCommitmentContext(ReignDbConnection connection, List<Dictionary<string, object>> commitments,
            Dictionary<string, object> payload, List<KnowledgeAccessContext> audience, List<string> sources,
            List<Dictionary<string, object>> diagnostics)
        {
            if (ReadBool(payload, "officialMemoryFirewall", false)) return "";
            string query = ReadFirstString(payload, "playerText", "text", "message") + " " + ReadString(payload, "memoryActiveTopic", "");
            // Player query is intact narrative context; no inferred-thought projection.
            var terms = MemoryQueryTerms(query).Where(t => !ExactRecallStopWords.Contains(t, StringComparer.OrdinalIgnoreCase)
                && !new[] { "promise", "promises", "promised", "commitment", "commitments", "word", "give", "shall" }.Contains(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool recall = Regex.IsMatch(query, @"\b(?:promis(?:e|es|ed)|commitments?|pledge[ds]?|oaths?|swore|sworn|agreed|(?:my|your|his|her|our|their|one['â€™]s)\s+word)\b", RegexOptions.IgnoreCase);
            var ranked = commitments.Select(row => new { Row = row, Score = MemoryQueryTerms(ReadString(row, "description", ""))
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
                        : QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$session AND exchange_id=$exchange AND status='active' ORDER BY turn_order;",
                            TestDict("session", session, "exchange", exchange));
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
                        + string.Join("\n", rows.Select(row => "turn_id=" + ReadString(row, "turn_id", "") + "; " + RenderExactHistoryTurn(row, audience[0])));
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
        private static string StableCommitmentId(Dictionary<string, object> item)
        {
            string id = ReadFirstString(item, "obligation_id", "obligationId", "id");
            if (id.Length > 0) return id;
            // Retries and repeated exact promises do not create a new debt.
            return "obligation_" + PromptHash(Json.Serialize(new[] { ContinuityTimeline(item), ReadFirstString(item, "owed_by", "owedBy", "actorId", "subject"),
                ReadFirstString(item, "owed_to", "owedTo", "targetId", "target"), ReadFirstString(item, "description", "text", "promise", "threat", "summary").Trim() })).Substring(0, 32);
        }

        private static void EnsureLatestAcceptedCommitments(ReignDbConnection connection)
        {
            const string marker = "explicit_accepted_commitment_bootstrap_v1";
            if (!TableExists(connection, "conversation_turns") || !TableExists(connection, "conversation_sessions")
                || ReadString(QuerySql(connection, "SELECT value FROM schema_meta WHERE key=$key;", TestDict("key", marker)).FirstOrDefault(), "value", "") == "1") return;
            // Bootstrap only the latest accepted private contribution per pair.
            // Older promises may have been fulfilled or withdrawn later; absence
            // of a closure record is never permission to resurrect those.
            var rows = QuerySql(connection, @"SELECT * FROM (
SELECT t.*,s.player_id,s.payload_json AS session_payload,
ROW_NUMBER() OVER(PARTITION BY t.speaker_id,s.player_id ORDER BY t.world_day DESC,t.ts DESC,t.turn_order DESC) AS latest
FROM conversation_turns t JOIN conversation_sessions s ON s.session_id=t.session_id
WHERE t.status='active' AND t.role='npc' AND t.channel NOT IN ('letter','correspondence') AND t.speaker_id<>s.player_id AND s.player_id<>''
) latest_turns WHERE latest=1;");
            foreach (var row in rows)
            {
                var participants = TextListFromJson(ReadString(row, "participants_json", "[]"));
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
                    if (QuerySql(connection, "SELECT * FROM obligations WHERE owed_by=$speaker AND owed_to=$peer AND timeline_id=$timeline;", TestDict("speaker", speaker, "peer", peer, "timeline", timeline))
                        .Any(o => ReadString(ReadDictionary(TryParseJsonObject(ReadString(o, "payload_json", "{}")), "sourceEvidence"), "quote", "").Contains(clause, StringComparison.Ordinal))) continue;
                    var write = TestDict("description", clause, "owed_by", speaker, "owed_to", peer, "status", "unresolved", "importance", 0.9d,
                        "participants", participants, "known_by", participants, "visibility", "private", "timelineId", timeline,
                        "worldDay", ReadDouble(row, "world_day", 0d), "created_day", ReadDouble(row, "world_day", 0d), "sourceAcceptedText", original,
                        "sourceEvidence", TestDict("turnId", turn, "speakerId", speaker, "evidenceType", "future_intention", "quote", clause));
                    AttachConversationWriteEvidence(new[] { write }, new List<Dictionary<string, object>> { TestDict("turnId", turn, "speakerId", speaker, "text", original) }, speaker, turn);
                    if (ReadString(write, "sourceValidation", "") == "supported_span") InsertObligation(connection, turn, write, ReadLong(row, "ts", 0), "accepted_commitment_bootstrap");
                }
            }
            ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES($key,'1');", TestDict("key", marker));
        }
    }
}
