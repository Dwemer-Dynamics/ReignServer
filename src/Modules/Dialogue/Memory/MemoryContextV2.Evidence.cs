using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static MemoryEvidence EvidenceFromRow(Dictionary<string, object> row, string idKey, string text, string kind,
            string owner, bool required, double score)
        {
            return new MemoryEvidence { SourceId = ReadString(row, idKey, ""), SourceHash = MemorySourceHash(new[] { row }),
                Text = text ?? "", Kind = kind, Owner = owner, Required = required, Score = score,
                Revision = ReadInt(row, "revision", 1), Day = ReadDouble(row, "world_day", ReadDouble(row, "learned_day", 0d)),
                Session = ReadString(row, "session_id", "") };
        }

        internal static List<MemoryEvidence> LoadRecentConversationWindow(ReignDbConnection connection,
            MemoryQueryPlan plan, Dictionary<string, object> context, DialogueService.KnowledgeAccessContext knowledge,
            Dictionary<string, object> settings, Dictionary<string, object> diagnostics)
        {
            const int summaryLimit = 30;
            int compactLimit = CompactConversationSummaryLimit(settings);
            int rawLimit = Math.Clamp(ReadInt(settings, "recentRawConversationTurns", 30), 2, 100);
            var records = new List<MemoryEvidence>();
            var audit = TestDict("schema", "reign-recent-memory-window-v1", "requestedMessages", rawLimit,
                "requestedSummaries", summaryLimit, "eligibleMessages", 0, "eligibleSummaries", 0,
                "additionalProviderCalls", 0, "summaryAuthority", "derived_recollection_originals_and_corrections_control",
                "compactSummaryBodyLimit", compactLimit, "summaryViews", new List<Dictionary<string, object>>());
            diagnostics["recentMemoryWindow"] = audit;
            if (!knowledge.HasNpc || knowledge.PlayerIdentityUnknown || ReadBool(context, "officialMemoryFirewall", false))
            {
                audit["status"] = "restricted_by_observer_or_route";
                return records;
            }
            var parameters = TestDict("npc", plan.Observer, "day", knowledge.WorldDay,
                "asOfSequence", knowledge.AsOfAcceptedSequence, "asOfTimestamp", knowledge.AsOfTimestamp,
                "timeline", knowledge.TimelineId, "batch", 128, "offset", 0);
            var turns = new List<Dictionary<string, object>>();
            for (int offset = 0; turns.Count < rawLimit; offset += 128)
            {
                parameters["offset"] = offset;
                var page = DialogueStore.SelectConversationTurns59(connection, MemorySessionObserverSql("s", "t"), MemorySourceTimelineSql("conversation_turns", "t"),parameters);
                turns.AddRange(page.Where(t => PrecisionTurnVisible(t, plan.Observer, knowledge, connection))
                    .Take(rawLimit - turns.Count));
                if (page.Count < 128) break;
            }
            foreach (var row in turns.AsEnumerable().Reverse())
            {
                string text = RenderExactHistoryTurn(row, knowledge)
                    + (ConversationRepairFailed(TryParseJsonObject(ReadString(row, "payload_json", "{}")), ReadString(row, "text", ""))
                        ? " [visible failed repair; speech only, no accepted effects]" : "");
                var source = TestDict("id", ReadString(row, "turn_id", ""), "hash", MemoryTranscriptSourceHash(row),
                    "viewHash", MemoryProjectedTranscriptHash(row, knowledge),
                    "speakerId", ReadString(row, "speaker_id", ""), "role", ReadString(row, "role", ""),
                    "sessionId", ReadString(row, "session_id", ""), "exchangeId", ReadString(row, "exchange_id", ""),
                    "acceptedSequence", ReadLong(row, "accepted_sequence", 0), "turnOrder", ReadInt(row, "turn_order", 0),
                    "timestamp", ReadLong(row, "ts", 0), "worldDay", ReadDouble(row, "world_day", 0d), "text", text);
                var evidence = EvidenceFromRow(row, "turn_id", text, "reported_exchange", plan.Observer, true, 121);
                evidence.SourceTurns.Add(source);
                records.Add(evidence);
            }
            audit["eligibleMessages"] = turns.Count;
            var summaries = new List<MemoryEvidence>();
            var episodes = new HashSet<string>(StringComparer.Ordinal);
            parameters["observer"] = plan.Observer;
            parameters["compactLimit"] = compactLimit;
            int withheld = 0, duplicates = 0;
            for (int offset = 0; summaries.Count < summaryLimit; offset += 128)
            {
                parameters["offset"] = offset;
                var page = DialogueStore.SelectSummaries5(connection, MemoryEligibilitySql("summaries"), MemorySourceTimelineSql("summaries"),parameters);
                foreach (var original in page)
                {
                    if (!DialogueService.KnowledgeRowVisibleToNpc("summaries", original, knowledge, connection)
                        || ReadLong(original, "end_ts", 0) > knowledge.AsOfTimestamp)
                    { withheld++; continue; }
                    var row = ProjectRetrievedConversationMemories(connection,
                        new List<Dictionary<string, object>> { original }, knowledge).SingleOrDefault();
                    string text = ReadString(row, "summary", "");
                    if (text.Length == 0 || IsUnsupportedWordingJudgment(text) || IsRoleplayFeedbackLoopEvidence(row, text))
                    { withheld++; continue; }
                    var payload = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                    string scope = ReadString(row, "scope", "");
                    string session = ReadFirstString(payload, "sessionId", "conversationSessionId");
                    if (session.Length == 0 && scope.StartsWith("scene:", StringComparison.Ordinal)) session = scope.Substring(6);
                    var events = DialogueService.TextListFromJson(ReadString(row, "source_events_json", "[]"))
                        .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
                    string identity = session.Length > 0 ? "session:" + session
                        : events.Count > 0 ? "events:" + PromptHash(Json.Serialize(events)) : "id:" + ReadString(row, "summary_id", "");
                    if (!episodes.Add(identity)) { duplicates++; continue; }
                    text = CompactRecentSummaryView(text, payload, plan.Terms, compactLimit, out var view);
                    view["summaryId"] = ReadString(row, "summary_id", "");
                    ((List<Dictionary<string, object>>)audit["summaryViews"]).Add(view);
                    string heading = "RECENT CONVERSATION SUMMARY — type=" + ReadString(row, "summary_type", "") + "; sourceStart=" + ReadLong(row, "start_ts", 0)
                        + "; sourceEnd=" + ReadLong(row, "end_ts", 0) + "; compaction=" + ReadString(payload, "compactionStatus", "unverified")
                        + "; sourceSummaryId=" + ReadString(row, "summary_id", "")
                        + "; sourceEventCount=" + events.Count + "; sourceSetHash=" + PromptHash(Json.Serialize(events)) + "\n";
                    var evidence = EvidenceFromRow(row, "summary_id", heading + text, "recent_summary", plan.Observer, true, 118);
                    evidence.Session = session;
                    summaries.Add(evidence);
                    if (summaries.Count == summaryLimit) break;
                }
                if (page.Count < 128) break;
            }
            // Bind the common policy to one protected summary, so whole-record
            // allocation and final evidence checks cannot drop it independently.
            if (summaries.Count > 0) summaries.Last().Text = RecentSummaryPromptPolicy + "\n" + summaries.Last().Text;
            records.AddRange(summaries.AsEnumerable().Reverse());
            audit["eligibleSummaries"] = summaries.Count;
            audit["duplicateViewsSkipped"] = duplicates;
            audit["ineligibleOrUnsupportedRowsSkipped"] = withheld;
            audit["summaryIds"] = summaries.Select(e => e.SourceId).ToList();
            audit["rawTurnIds"] = turns.Select(t => ReadString(t, "turn_id", "")).ToList();
            var views = (List<Dictionary<string, object>>)audit["summaryViews"];
            audit["storedSummaryCharacters"] = views.Sum(v => ReadInt(v, "storedCharacters", 0));
            audit["suppliedSummaryCharacters"] = views.Sum(v => ReadInt(v, "suppliedCharacters", 0));
            audit["summarySourceCoverageComplete"] = views.All(v => ReadString(v, "sourceCoverage", "") == "all_records");
            audit["summaryStoredTextCoverageComplete"] = views.All(v => ReadString(v, "view", "") == "complete_stored_summary"
                || ReadString(v, "sourceCoverage", "") == "all_records");
            audit["status"] = views.Any(v => ReadString(v, "view", "") != "complete_stored_summary")
                ? "selected_compact_views_originals_retained" : "selected_complete_eligible_records";
            return records;
        }


        private static List<MemoryEvidence> LoadPrecisionFoundation(ReignDbConnection connection, string campaign,
            MemoryQueryPlan plan, Dictionary<string, object> context, DialogueService.KnowledgeAccessContext knowledge)
        {
            var evidence = new List<MemoryEvidence>();
            if (plan.Session.Length > 0)
            {
                var continuing = ReadLogicalConversationTurns(connection, plan.Session, 2, knowledge);
                if (continuing.Count > 0 && !continuing.All(r => MemorySourceAlreadySupplied(r, plan.SuppliedSources)))
                    evidence.Add(new MemoryEvidence { SourceId = "active_conversation:" + plan.Session,
                        SourceHash = MemorySourceHash(continuing), Kind = "active_conversation", Owner = plan.Observer,
                        Session = plan.Session, Required = true, Score = 110,
                        Text = "CURRENT CONVERSATION (explicit continuation chain; original speakers and sequence):\n"
                            + string.Join("\n", continuing.Select(r => RenderExactHistoryTurn(r, knowledge))),
                        SourceTurns = continuing.Select(r => TestDict("id", ReadString(r, "turn_id", ""),
                            "hash", MemoryTranscriptSourceHash(r), "viewHash", MemoryProjectedTranscriptHash(r, knowledge),
                            "speakerId", ReadString(r, "speaker_id", ""), "role", ReadString(r, "role", ""),
                            "text", RenderExactHistoryTurn(r, knowledge))).ToList() });
            }
            evidence.AddRange(LinkedConversationEvidence(connection, plan.Observer, plan.Peer, plan.Timeline, plan.Query, knowledge));
            var parameters = new Dictionary<string, object> { ["owner"] = plan.Observer, ["peer"] = plan.Peer,
                ["timeline"] = plan.Timeline, ["campaign"] = campaign, ["day"] = plan.WorldDay };
            var continuity = DialogueStore.SelectConversationContinuity10(connection, MemorySourceTimelineSql("conversation_continuity", access: knowledge),parameters);
            var episodeSources = LoadContinuityEpisodeSources(connection, continuity, plan.Observer);
            foreach (var row in continuity)
            {
                var value = TryParseJsonObject(ReadString(row, "payload_json", "{}")) ?? new Dictionary<string, object>();
                if (!MemoryTimeVisible(row, knowledge, connection)) continue;
                string kind = ReadString(row, "kind", "");
                if (kind == "episode")
                {
                    episodeSources.TryGetValue(ReadString(row, "source_turn_id", ""), out var source);
                    string episode = RenderContinuityEpisodeEvidence(connection, row, value, source, knowledge, plan.SuppliedSources);
                    if (episode.Length > 0)
                        evidence.Add(EvidenceFromRow(row, "record_id", episode, kind, plan.Observer, false, 90 + ReadDouble(row, "importance", 0d)));
                    continue;
                }
                bool required = kind == "relationship" || kind == "agenda";
                string opportunity = kind == "agenda" ? ContinuityAgendaOpportunity(value, context) : "";
                string text = "id=" + ReadString(row, "record_id", "") + "; revision=" + ReadInt(row, "revision", 1)
                    + "; " + kind + " " + ReadString(row, "status", "") + ": " + ReadString(value, "subject", "")
                    + ". Meaning: " + ReadString(value, "meaning", "") + ". Next: " + ReadString(value, "nextStep", "")
                    + ". Completion: " + ReadString(value, "completionCriterion", "")
                    + (opportunity.Length == 0 ? "" : ". Opportunity: " + opportunity)
                    + ". Accepted source: " + ReadString(value, "evidenceQuote", "");
                if (ReadString(ReadDictionary(value, "activityStep"), "status", "") == "completed")
                    text += ". Mechanical step already completed; source=" + ReadString(value, "completionEventId", "") + "; only the separate concern remains open.";
                evidence.Add(EvidenceFromRow(row, "record_id", text, kind, plan.Observer, required, 90 + ReadDouble(row, "importance", 0d)));
            }
            // Preserve foundation and development, including present boundaries,
            // without replaying every reinforcing incident or creating a score.
            if (plan.Peer.Length > 0 && WorldSimulationService.TableExists(connection, "court_social_signal_evidence"))
            {
                var signals = DialogueStore.SelectCourtSocialSignalEvidence2(connection, MemorySourceTimelineSql("court_social_signal_evidence"),parameters)
                    .Where(r => MemoryTimeVisible(r, knowledge, connection)).ToList();
                foreach (var row in signals.Take(1).Concat(signals.TakeLast(1)).GroupBy(r => ReadString(r, "signal_id", "")).Select(g => g.First()))
                    evidence.Add(EvidenceFromRow(row, "signal_id", "Established shared intimacy with " + plan.Peer
                        + "; history is not present consent or a relationship label. Accepted quote: " + ReadString(row, "supporting_quote", ""),
                        "relationship", plan.Observer, true, 100));
            }
            if (plan.Peer.Length > 0 && WorldSimulationService.TableExists(connection, "relationship_milestones"))
                foreach (var row in DialogueStore.SelectRelationshipMilestones2(connection, parameters).Where(r => MemoryTimeVisible(r, knowledge, connection)))
                    evidence.Add(EvidenceFromRow(row, "milestone_id", ReadString(row, "kind", "") + ": " + ReadString(row, "reason", ""),
                        "relationship", plan.Observer, true, 95));
            if (plan.Peer.Length > 0 && WorldSimulationService.TableExists(connection, "conversation_relationship_receipts"))
            {
                var receipts = DialogueStore.SelectConversationRelationshipReceipts5(connection, MemorySourceTimelineSql("conversation_relationship_receipts"),parameters)
                    .Where(r => MemoryTimeVisible(r, knowledge, connection) && !IsUnsupportedWordingJudgment(ReadString(r, "summary", "")));
                foreach (var row in receipts.GroupBy(r => ReadString(r, "act_kind", "") + ":" + ReadString(r, "valence", ""))
                    .SelectMany(g => g.Take(1).Concat(g.TakeLast(1))).GroupBy(r => ReadString(r, "receipt_id", "")).Select(g => g.First()))
                    evidence.Add(EvidenceFromRow(row, "receipt_id", ReadString(row, "valence", "") + ": " + ReadString(row, "summary", "")
                        + ". Accepted conduct: " + ReadString(row, "current_conduct_quote", ""), "relationship", plan.Observer, true, 94));
            }
            // Existing campaigns can lack structured milestones. Recover only
            // this pair's actual attributed words, keeping refusals/negation.
            var pairTurns = DialogueStore.SelectConversationTurns60(connection, parameters).Where(r => MemoryTimeVisible(r, knowledge, connection)
                    && DialogueService.TextListFromJson(ReadString(r, "participants_json", "[]")).Contains(plan.Peer, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var row in pairTurns.Where(r => PairHistoryDimension(ReadString(r, "text", "")).Length > 0)
                .GroupBy(r => PairHistoryDimension(ReadString(r, "text", ""))).SelectMany(g => g.Take(1).Concat(g.TakeLast(1)))
                .GroupBy(r => ReadString(r, "turn_id", "")).Select(g => g.First()))
            {
                var foundation = PrecisionExchangeEvidence(connection, row, knowledge, true, 93);
                if (foundation == null) continue;
                foundation.RelationshipFoundation = true;
                evidence.Add(foundation);
            }
            return evidence.Where(e => e != null).ToList();
        }

        private static List<MemoryEvidence> LoadPrecisionFoundationProjection(ReignDbConnection connection, string campaign,
            MemoryQueryPlan plan, Dictionary<string, object> context, DialogueService.KnowledgeAccessContext knowledge, bool shadow, Dictionary<string, object> audit)
        {
            // Include the knowledge and opportunity context in the cache key.
            // Source revision is conservative across the campaign; no stale pair
            // can survive a late group contribution or a changed boundary.
            string key = PromptHash("source-linked-foundation-v10|" + CanonicalJson(plan.SuppliedSources) + "|" + plan.Timeline + "|" + plan.Observer + "|" + plan.Peer
                + "|" + plan.Query + "|" + plan.Session + "|" + CanonicalJson(context));
            using (var transaction = connection.BeginTransaction(System.Data.IsolationLevel.RepeatableRead))
            {
                var state = ReadMemoryPrecisionState(connection);
                long revision = ReadLong(state, "source_revision", 0);
                var cached = DialogueStore.SelectMemoryProjections(connection, new Dictionary<string, object> { ["id"] = key, ["generation"] = ReadString(state, "restore_generation", ""),
                        ["projection"] = ReadInt(state, "projection_generation", 1), ["revision"] = revision.ToString(CultureInfo.InvariantCulture) }).FirstOrDefault();
                if (cached != null)
                {
                    var payload = TryParseJsonObject(ReadString(cached, "payload_json", "{}"));
                    audit["foundationCache"] = "fresh"; audit["projectionWatermark"] = ReadLong(cached, "watermark", 0);
                    transaction.Commit();
                    return ReadDictionaryList(payload, "evidence").Select(e => new MemoryEvidence { SourceId = ReadString(e,"id",""),
                        SourceHash = ReadString(e,"hash",""), Kind = ReadString(e,"kind",""), Text = ReadString(e,"text",""),
                        Owner = plan.Observer, Session = ReadString(e,"session",""), Revision = ReadInt(e,"revision",1),
                        Day = ReadDouble(e,"day",0), Score = ReadDouble(e,"score",0), Required = ReadBool(e,"required",false),
                        RelationshipFoundation = ReadBool(e,"relationshipFoundation",false),
                        SourceTurns = ReadDictionaryList(e,"sourceTurns") }).ToList();
                }
                var evidence = LoadPrecisionFoundation(connection, campaign, plan, context, knowledge);
                long watermark = ReadLong(DialogueStore.SelectConversationTurns61(connection).Single(), "watermark", 0);
                if (!shadow)
                    DialogueStore.UpsertMemoryProjections(connection, new Dictionary<string, object> { ["id"] = key, ["timeline"] = plan.Timeline, ["owner"] = plan.Observer, ["peer"] = plan.Peer,
                            ["generation"] = ReadString(state, "restore_generation", ""), ["projection"] = ReadInt(state,"projection_generation",1),
                            ["revision"] = revision.ToString(CultureInfo.InvariantCulture), ["watermark"] = watermark,
                            ["payload"] = Json.Serialize(new Dictionary<string,object> { ["evidence"] = evidence.Select(e => new Dictionary<string,object> {
                                ["id"]=e.SourceId,["hash"]=e.SourceHash,["kind"]=e.Kind,["text"]=e.Text,["session"]=e.Session,
                                ["revision"]=e.Revision,["day"]=e.Day,["score"]=e.Score,["required"]=e.Required,
                                ["relationshipFoundation"]=e.RelationshipFoundation,["sourceTurns"]=e.SourceTurns }).ToList() }),
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                audit["foundationCache"] = "caught_up"; audit["projectionWatermark"] = watermark; audit["sourceRevision"] = revision;
                transaction.Commit();
                return evidence;
            }
        }

        private static MemoryEvidence PrecisionExchangeEvidence(ReignDbConnection connection, Dictionary<string, object> anchor,
            DialogueService.KnowledgeAccessContext knowledge, bool required, double score, bool compactAdjacentContext = false, bool preserveRecallContext = false)
        {
            string exchange = ReadString(anchor, "exchange_id", ""), session = ReadString(anchor, "session_id", "");
            var rows = exchange.Length == 0 ? new List<Dictionary<string, object>> { anchor }
                : DialogueStore.SelectConversationTurns12(connection, new Dictionary<string, object> { ["session"] = session, ["exchange"] = exchange });
            rows = rows.Where(r => MemoryTimeVisible(r, knowledge, connection) && (ReadString(r, "speaker_id", "") == knowledge.NpcId
                || DialogueService.TextListFromJson(ReadString(r, "participants_json", "[]")).Contains(knowledge.NpcId, StringComparer.OrdinalIgnoreCase))).ToList();
            if (rows.Count == 0) return null;
            var recallContext = preserveRecallContext ? ExpandMemoryRecallPrecedingTurn(connection, rows, knowledge) : new List<Dictionary<string, object>>();
            rows = compactAdjacentContext ? ExpandPrecisionQuestionContext(connection, rows, knowledge)
                : ExpandConversationSequence(connection, rows, knowledge);
            rows = rows.Concat(recallContext).DistinctBy(row => ReadString(row, "turn_id", ""))
                .OrderBy(row => ReadLong(row, "turn_order", 0)).ToList();
            // Source classification is attached to each speaker. Failed visible
            // repairs remain speech, never evidence of a validated commitment.
            var sourceTurns = rows.Select(r => new Dictionary<string, object> {
                ["id"] = ReadString(r, "turn_id", ""), ["hash"] = MemoryTranscriptSourceHash(r),
                ["viewHash"] = MemoryProjectedTranscriptHash(r, knowledge),
                ["speakerId"] = ReadString(r, "speaker_id", ""), ["role"] = ReadString(r, "role", ""),
                ["sessionId"] = ReadString(r, "session_id", ""), ["exchangeId"] = ReadString(r, "exchange_id", ""),
                ["acceptedSequence"] = ReadLong(r, "accepted_sequence", 0), ["turnOrder"] = ReadInt(r, "turn_order", 0),
                ["timestamp"] = ReadLong(r, "ts", 0), ["worldDay"] = ReadDouble(r, "world_day", 0),
                ["replyToTurnId"] = ReadFirstString(TryParseJsonObject(ReadString(r, "payload_json", "{}")), "replyToTurnId", "questionTurnId", "inReplyToTurnId"),
                ["addressedIds"] = ReadStringList(TryParseJsonObject(ReadString(r, "payload_json", "{}")), "addressedHeroStringIds"),
                ["text"] = RenderExactHistoryTurn(r, knowledge)
                    + (ConversationRepairFailed(TryParseJsonObject(ReadString(r, "payload_json", "{}")), ReadString(r, "text", ""))
                        ? " [visible failed repair; speech only, no accepted effects]" : "") }).ToList();
            string text = string.Join("\n", sourceTurns.Select(r => ReadString(r, "text", "")));
            return new MemoryEvidence { SourceId = string.Join(",", rows.Select(r => ReadString(r, "turn_id", ""))),
                SourceHash = MemorySourceHash(rows), Kind = "reported_exchange", Owner = knowledge.NpcId,
                Session = session, Day = rows.Max(r => ReadDouble(r, "world_day", 0d)), Text = text, Required = required, Score = score,
                SourceTurns = sourceTurns };
        }

        internal static List<MemoryEvidence> FindPrecisionExchanges(ReignDbConnection connection, MemoryQueryPlan plan,
            DialogueService.KnowledgeAccessContext knowledge, int limit, int maximumCandidatePages = int.MaxValue, bool requirePlayerSource = true,
            bool compactAdjacentContext = true, bool includeRecentFloor = true)
        {
            var parameters = new Dictionary<string, object> { ["query"] = DialogueService.BuildFtsQuery(plan.Terms), ["npc"] = plan.Observer,
                ["session"] = plan.Session, ["day"] = plan.WorldDay, ["timeline"] = plan.Timeline, ["limit"] = limit,
                ["asOfSequence"] = knowledge.AsOfAcceptedSequence, ["asOfTimestamp"] = knowledge.AsOfTimestamp };
            string eligible = MemorySessionObserverSql("s", "t")
                + " AND t.world_day<=$day AND t.status='active'"
                + " AND t.accepted_sequence<=$asOfSequence AND t.ts<=$asOfTimestamp"
                + " AND " + MemorySourceTimelineSql("conversation_turns", "t");
            var rows = FindLexicalMemoryRecallTurns(connection, plan, knowledge, limit, maximumCandidatePages, requirePlayerSource);
            rows = PreserveMemoryRecallCandidatePool(FuseMemorySearchRanks(int.MaxValue, rows, FindMemorySpecialistTurns(connection, plan, knowledge, limit,
                maximumCandidatePages, requirePlayerSource), FindLateInteractionTurns(connection, plan, knowledge, limit, requirePlayerSource)), rows, limit);
            // Rarity is computed only inside observer/timeline/as-of eligibility.
            // Repeated mentions in one monologue cannot inflate term frequency;
            // lexical relevance still does not establish the remembered fact.
            // One complete latest exchange is the continuity floor; it is not a
            // request to replay thirty old turns on every small-talk response.
            var recent = includeRecentFloor ? QuerySourceBearingPrecisionTurns(connection, DialogueStore.SelectConversationTurnsSql4(eligible), parameters, knowledge, 1, plan.SuppliedSources, maximumCandidatePages, requirePlayerSource).FirstOrDefault() : null;
            var sourceBearing = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var result = new List<MemoryEvidence>();
            foreach (var row in rows.Concat(recent == null ? Enumerable.Empty<Dictionary<string, object>>() : new[] { recent }))
            {
                if (requirePlayerSource && !ConversationSessionContainsSourceBearingPlayerTurn(connection, ReadString(row, "session_id", ""), sourceBearing)) continue;
                bool isRecent = recent != null && ReadString(row, "turn_id", "") == ReadString(recent, "turn_id", "");
                var evidence = PrecisionExchangeEvidence(connection, row, knowledge, isRecent || (plan.ExactWording && result.Count==0),
                    isRecent ? 75 : 80 + ReadDouble(row, "relevance", 0d), compactAdjacentContext: compactAdjacentContext,
                    preserveRecallContext: !isRecent);
                if (evidence != null) result.Add(evidence);
            }
            return result;
        }

        private static List<Dictionary<string,object>> QuerySourceBearingPrecisionTurns(ReignDbConnection connection,
            string orderedSql, Dictionary<string,object> parameters, DialogueService.KnowledgeAccessContext knowledge, int limit,
            Dictionary<string, string> suppliedSources = null, int maximumCandidatePages = int.MaxValue, bool requirePlayerSource = true,
            Func<Dictionary<string, object>, bool> candidateEligible = null)
        {
            var result = new List<Dictionary<string,object>>();
            var sourceSessions = new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
            var exchanges = new HashSet<string>(StringComparer.Ordinal);
            var pageParameters = new Dictionary<string,object>(parameters);
            const int batch = 128;
            for (int offset=0; result.Count<limit && offset / batch < maximumCandidatePages; offset+=batch)
            {
                pageParameters["batch"]=batch; pageParameters["offset"]=offset;
                var page=DialogueStore.Statement5(connection, orderedSql,pageParameters);
                foreach(var row in page)
                {
                    string session=ReadString(row,"session_id","");
                    if(!sourceSessions.TryGetValue(session,out bool sourceBearing))
                    {
                        sourceBearing=!requirePlayerSource || DialogueStore.SelectConversationTurns62(connection, new Dictionary<string,object>{{"session",session}}).Any(t=>PrecisionTurnVisible(t, knowledge.NpcId, knowledge, connection)
                                && !IsPureConversationRecallRequest(ReadString(t,"text","")));
                        sourceSessions[session]=sourceBearing;
                    }
                    string exchange=session+"|"+FirstNonEmpty(ReadString(row,"exchange_id",""),ReadString(row,"turn_id",""));
                    if(sourceBearing && PrecisionTurnVisible(row, knowledge.NpcId, knowledge, connection)
                        && (candidateEligible == null || candidateEligible(row))
                        && !MemorySourceAlreadySupplied(row, suppliedSources) && exchanges.Add(exchange)) result.Add(row);
                    if(result.Count>=limit) break;
                }
                if(page.Count<batch) break;
            }
            return result;
        }

        private static string RenderMemoryEvidence(MemoryEvidence evidence, string alias) =>
            RenderContinuityRecord(ConversationReferenceMode() == "off" || evidence.SourceId.Length == 0
                || !ConversationReferenceSourceKind(evidence.Kind) ? alias : evidence.SourceId,
                evidence.Kind, evidence.Owner, evidence.Session, evidence.Day,
                (int)evidence.Score, evidence.Required, "[" + alias + " revision=" + evidence.Revision + "] " + evidence.Text, evidence.SelectionRank);

        internal static MemoryEvidencePacket SelectPrecisionEvidence(IEnumerable<MemoryEvidence> candidates, int ordinaryBudget = 4000,
            Dictionary<string, string> suppliedSources = null)
        {
            var packet = new MemoryEvidencePacket { Budget = ordinaryBudget };
            var supplied = suppliedSources == null ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(suppliedSources, StringComparer.Ordinal);
            var unique = DistinctMemoryBundles(candidates, supplied)
                .OrderByDescending(e => e.Required).ThenBy(e => e.SelectionRank.HasValue ? 0 : 1)
                .ThenBy(e => e.SelectionRank ?? int.MaxValue).ThenByDescending(e => e.Score)
                .ThenBy(e => e.SourceId, StringComparer.Ordinal).ToList();
            // Finish the standard allocation before spending an expanded allowance.
            // Starting the greedy pass with more space can otherwise replace an
            // already useful answer with a larger, higher-ranked record.
            int initialOrdinaryBudget = ordinaryBudget > 4000
                && unique.Any(e => !e.Required && e.SelectionRank.HasValue && e.SelectionRelevance.HasValue)
                ? 4000 : ordinaryBudget;
            packet.Budget = initialOrdinaryBudget;
            var rendered = new List<string>();
            var deferred = new List<MemoryEvidence>();
            bool ordinaryAllocationStarted = false;
            foreach (var original in unique)
            {
                // Overlapping question/answer bundles share complete original turns.
                // Count each identical source once across the actual transcript and
                // selected memories; a changed speaker or text must still be included.
                var evidence = RemoveSuppliedMemoryTurns(original, supplied);
                if (evidence == null) continue;
                // Link status and letter authorship/delivery headers must survive
                // even when their quoted source also appears in another bundle.
                string block = RenderMemoryEvidence(evidence, "m" + (packet.Selected.Count + 1));
                int tokens = Math.Max(1, EstimateContinuityTokens(block) - 128);
                // Recent raw dialogue/summaries are a floor. Reserve an ordinary
                // older-evidence allowance even below the former 8k threshold.
                if (!evidence.Required && !ordinaryAllocationStarted)
                {
                    ordinaryAllocationStarted = true;
                    int allowance = Math.Min(2000, initialOrdinaryBudget);
                    // A validated rank identifies the first older bundle to preserve.
                    // The recent-history floor must not shrink its configured 4k
                    // allowance to 2k and discard it in favor of shorter, lower ranks.
                    // When that floor is insufficient, use the configured allowance
                    // once, retaining room for a separate answer or later correction.
                    // Never expand again for lower ranks. Final writer capacity is
                    // still enforced independently during serialization.
                    // Binary reranker scores are not calibrated across queries.
                    // Honor the relative order of a complete validated ranking;
                    // an absolute score must not reject its best complete bundle
                    // solely because that bundle is larger than the fallback floor.
                    // Missing scores still retain the ordinary fallback behavior.
                    if (evidence.SelectionRank.HasValue && evidence.SelectionRelevance.HasValue && tokens > allowance)
                        allowance = initialOrdinaryBudget;
                    // A protected record may have raised the reservation to 8k.
                    // Only its actual tokens are protected; unused reservation
                    // cannot enlarge the separately configured ordinary budget.
                    packet.Budget = Math.Min(packet.Tokens + initialOrdinaryBudget,
                        Math.Max(packet.Budget, packet.Tokens + allowance));
                }
                if (evidence.Required && packet.Tokens + tokens > packet.Budget)
                    packet.Budget = packet.Tokens + tokens <= 8000 ? 8000 : packet.Tokens + tokens;
                if (!evidence.Required && packet.Tokens + tokens > packet.Budget)
                {
                    if (evidence.PackingText.Length > 0 && evidence.PackingAudit != null)
                    {
                        var compact = CopyMemoryEvidence(evidence, evidence.PackingText, evidence.PackingTurns);
                        string compactBlock = RenderMemoryEvidence(compact, "m" + (packet.Selected.Count + 1));
                        int compactTokens = Math.Max(1, EstimateContinuityTokens(compactBlock) - 128);
                        if (packet.Tokens + compactTokens <= packet.Budget)
                        {
                            evidence = compact; block = compactBlock; tokens = compactTokens;
                            packet.Packing.Add(TestDict("sourceId", evidence.SourceId, "view", evidence.PackingAudit));
                        }
                    }
                    if (packet.Tokens + tokens > packet.Budget)
                    {
                        packet.Rejected.Add(TestDict("sourceId", evidence.SourceId, "reason", "whole_record_budget",
                            "tokens", tokens, "remainingTokens", packet.Budget - packet.Tokens,
                            "priority", evidence.Score, "selectionRank", evidence.SelectionRank,
                            "compactViewAvailable", evidence.PackingText.Length > 0));
                        deferred.Add(original);
                        continue;
                    }
                }
                packet.Selected.Add(evidence);
                foreach (var turn in evidence.SourceTurns)
                    supplied[ReadString(turn, "id", "")] = ReadString(turn, "viewCoverage", "").Length == 0
                        ? ReadString(turn, "hash", "") : PromptHash(ReadString(turn, "text", ""));
                rendered.Add(block);
                packet.Tokens += tokens;
                if (evidence.Required) packet.ProtectedTokens += tokens; else packet.OrdinaryTokens += tokens;
            }
            // Preserve the complete first-pass selection before using the rest of
            // the configured older-evidence allowance. Expanding the first greedy
            // pass can replace a useful short answer with a larger top-ranked
            // bundle. A second pass adds evidence without that displacement.
            // Rank scores order relevance; an uncalibrated yes/no threshold must
            // not turn a 2k fallback floor into a hard limit below the 4k setting.
            // The unranked/failure path retains its existing allocation behavior.
            var supplementaryBudgets = initialOrdinaryBudget < ordinaryBudget
                ? new[] { initialOrdinaryBudget, ordinaryBudget } : new[] { ordinaryBudget };
            foreach (int allowance in supplementaryBudgets)
            {
                if (deferred.Count == 0) break;
                if (allowance == initialOrdinaryBudget
                    && !deferred.Any(e => e.SelectionRank.HasValue && e.SelectionRelevance.HasValue)) continue;
                packet.Budget = Math.Max(packet.Budget, packet.ProtectedTokens + allowance);
                packet.Rejected.Clear();
                var remaining = new List<MemoryEvidence>();
                foreach (var original in deferred)
                {
                    var evidence = RemoveSuppliedMemoryTurns(original, supplied);
                    if (evidence == null) continue;
                    string block = RenderMemoryEvidence(evidence, "m" + (packet.Selected.Count + 1));
                    int tokens = Math.Max(1, EstimateContinuityTokens(block) - 128);
                    if (packet.Tokens + tokens > packet.Budget && evidence.PackingText.Length > 0 && evidence.PackingAudit != null)
                    {
                        var compact = CopyMemoryEvidence(evidence, evidence.PackingText, evidence.PackingTurns);
                        string compactBlock = RenderMemoryEvidence(compact, "m" + (packet.Selected.Count + 1));
                        int compactTokens = Math.Max(1, EstimateContinuityTokens(compactBlock) - 128);
                        if (packet.Tokens + compactTokens <= packet.Budget)
                        {
                            evidence = compact; block = compactBlock; tokens = compactTokens;
                            packet.Packing.Add(TestDict("sourceId", evidence.SourceId, "view", evidence.PackingAudit));
                        }
                    }
                    if (packet.Tokens + tokens > packet.Budget)
                    {
                        packet.Rejected.Add(TestDict("sourceId", evidence.SourceId, "reason", "whole_record_budget",
                            "tokens", tokens, "remainingTokens", packet.Budget - packet.Tokens,
                            "priority", evidence.Score, "selectionRank", evidence.SelectionRank,
                            "compactViewAvailable", evidence.PackingText.Length > 0));
                        remaining.Add(original);
                        continue;
                    }
                    packet.Selected.Add(evidence);
                    foreach (var turn in evidence.SourceTurns)
                        supplied[ReadString(turn, "id", "")] = ReadString(turn, "viewCoverage", "").Length == 0
                            ? ReadString(turn, "hash", "") : PromptHash(ReadString(turn, "text", ""));
                    rendered.Add(block);
                    packet.Tokens += tokens;
                    packet.OrdinaryTokens += tokens;
                }
                // Accepted records, including those without source-turn metadata,
                // must not be replayed during the larger supplementary pass.
                deferred = remaining;
            }
            packet.Text = string.Join("\n\n", rendered);
            return packet;
        }

        private static Dictionary<string, object> PrecisionEvidenceAudit(MemoryEvidencePacket packet, MemoryQueryPlan plan, string mode)
        {
            return new Dictionary<string, object> {
                ["schema"] = "reign-memory-evidence-v1", ["mode"] = mode, ["observer"] = plan.Observer, ["timeline"] = plan.Timeline,
                ["generation"] = plan.Generation, ["queryNeeds"] = plan.Needs, ["unresolvedNeeds"] = packet.Unresolved,
                ["dynamicMemoryTokenEstimate"] = packet.Tokens, ["selectedBudget"] = packet.Budget, ["rejected"] = packet.Rejected,
                ["protectedTokenEstimate"] = packet.ProtectedTokens, ["ordinaryTokenEstimate"] = packet.OrdinaryTokens,
                ["packingViews"] = packet.Packing, ["packingPolicy"] = "observer_view_dedup_linked_evidence_v2",
                ["records"] = packet.Selected.Select((e, i) => new Dictionary<string, object> {
                    ["alias"] = "m" + (i + 1), ["sourceId"] = e.SourceId, ["sourceHash"] = e.SourceHash, ["revision"] = e.Revision,
                    ["required"] = e.Required, ["kind"] = e.Kind, ["textHash"] = PromptHash(RenderMemoryEvidence(e, "m" + (i + 1))),
                    ["rendered"] = RenderMemoryEvidence(e, "m" + (i + 1)), ["sourceTurns"] = e.SourceTurns,
                    ["priority"] = e.Score, ["selectionRank"] = e.SelectionRank, ["selectionRelevance"] = e.SelectionRelevance }).ToList() };
        }


        private static string ConversationDerivativeTable(Dictionary<string, object> row) =>
            row.ContainsKey("summary_id") ? "summaries" : row.ContainsKey("memory_id") ? "memories"
            : row.ContainsKey("belief_id") ? "beliefs" : row.ContainsKey("comprehension_id") ? "comprehension"
            : row.ContainsKey("assertion_id") ? "temporal_knowledge_assertions" : "";

        private static List<Dictionary<string, object>> ReadConversationDerivativeOriginals(ReignDbConnection connection,
            string table, Dictionary<string, object> row)
        {
            string key = table == "summaries" ? "summary_id" : table == "memories" ? "memory_id" : table == "beliefs" ? "belief_id"
                : table == "temporal_knowledge_assertions" ? "assertion_id" : "comprehension_id";
            string id = ReadString(row, key, ""), eventId = ReadString(row, "event_id", ReadString(row, "source_event_id", ""));
            string documentType = table == "summaries" ? "summary" : table == "temporal_knowledge_assertions" ? "assertion" : "memory";
            return DialogueStore.SelectConversationTurns63(connection, TestDict("type", documentType, "id", id, "event", eventId));
        }

        private static List<MemoryEvidence> ExpandPrecisionSource(ReignDbConnection connection, string table,
            Dictionary<string, object> row, MemoryQueryPlan plan, DialogueService.KnowledgeAccessContext knowledge, double score)
        {
            string key = table == "summaries" ? "summary_id" : table == "memories" ? "memory_id" : table == "beliefs" ? "belief_id" : "comprehension_id";
            var result = new List<MemoryEvidence>();
            if (table == "summaries" && RequiresSharedSceneSourceProjection(row, knowledge.NpcId))
            {
                var scene = ProjectSharedSceneSummary(connection, row, knowledge);
                if (scene != null)
                {
                    // Relevance scores do not identify the bounded source view.
                    scene.Remove("precisionRank");
                    scene.Remove("vectorSemanticScore");
                    var evidence = EvidenceFromRow(scene, key, "Source-bound shared scene excerpt:\n" + ReadString(scene, "summary", ""),
                        "shared_scene_excerpt", plan.Observer, false, score);
                    // The excerpt grants neither full-turn credit nor a generic session expansion.
                    evidence.Session = "";
                    result.Add(evidence);
                }
                return result;
            }

            var originals = ReadConversationDerivativeOriginals(connection, table, row);
            var sources = originals.Where(t => PrecisionTurnVisible(t, plan.Observer, knowledge, connection)).ToList();
            bool subjective = table == "beliefs" || table == "comprehension";
            if (subjective && originals.Count == sources.Count)
                result.Add(EvidenceFromRow(row, key, "Private " + table + " of " + plan.Observer + ": " + ReadFirstString(row, "claim", "text"),
                    table == "beliefs" ? "belief" : "interpretation", plan.Observer, false, score));
            if (sources.Count > 0)
            {
                var sourceBearing = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                sources = sources.Where(t => ConversationSessionContainsSourceBearingPlayerTurn(connection, ReadString(t, "session_id", ""), sourceBearing)).ToList();
                var ranked = sources.OrderByDescending(t => plan.Terms.Count(term => ReadString(t, "text", "").Contains(term, StringComparison.OrdinalIgnoreCase)))
                    .ThenByDescending(t => ReadLong(t, "ts", 0)).ToList();
                foreach (var turn in ranked.Take(2).Concat(sources.TakeLast(1)).GroupBy(t => FirstNonEmpty(ReadString(t, "exchange_id", ""), ReadString(t, "turn_id", ""))).Select(g => g.First()))
                {
                    var evidence = PrecisionExchangeEvidence(connection, turn, knowledge, false, score);
                    if (evidence != null) result.Add(evidence);
                }
                return result;
            }
            // A known source outside the accepted boundary or observer scope is
            // not missing history. Its derivative cannot re-enter as "legacy".
            if (originals.Count > 0 || subjective) return result;
            // Genuinely unresolved legacy derivatives remain explicitly uncertain.
            string text = ReadFirstString(row, "claim", "summary", "text");
            if ((table == "memories" || table == "summaries") && ReadString(row, "owner_id", "") != plan.Observer
                && HasPrivateNarrativeMeaning(text)) return result;
            if (text.Length > 0)
                result.Add(EvidenceFromRow(row, key, (table == "beliefs" || table == "comprehension"
                    ? "Private " + table + " of " + plan.Observer + ": " : "Legacy recollection; original evidence unavailable, verify before asserting precise terms: ")
                    + text, table == "beliefs" ? "belief" : table == "comprehension" ? "interpretation" : "unverified", plan.Observer, false, score - 20));
            return result;
        }


        private static void ResolvePrecisionAmbiguity(MemoryQueryPlan plan, List<MemoryEvidence> candidates,
            List<string> unresolved, Dictionary<string, object> diagnostics)
        {
            diagnostics["helperCalls"] = 0;
            diagnostics["targetedExpansions"] = 0;
            if (plan.Needs.Count == 0) return;
            unresolved.AddRange(plan.Needs);
            if (candidates.Any(e => (e.Kind == "obligation" || e.Kind == "assertion" || e.Kind == "reported_exchange")
                && plan.Terms.Any(t => e.Text.Contains(t, StringComparison.OrdinalIgnoreCase))
                && Regex.IsMatch(e.Text, @"\b(accept|accepted|agree|agreed|owes|only after|promise|promised)\b", RegexOptions.IgnoreCase)))
                unresolved.Remove("commitment");
            if (candidates.Any(e => e.Day > 0 && plan.Terms.Any(t => e.Text.Contains(t,StringComparison.OrdinalIgnoreCase)))) unresolved.Remove("chronology");
            if (plan.Entities.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 2) unresolved.Remove("referent");
            if (unresolved.Count == 0) return;
        }
    }
}
