using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly ConcurrentDictionary<string, string> NpcOpeningJobs = new();
        // Request-local authority cannot be supplied by a client or a provider.
        // Weak keys retain no completed conversation or campaign indefinitely.
        private sealed class NpcAnnouncementReviewWindow
        {
            public NpcAnnouncementReviewWindow() { }
            public string CampaignId { get; set; } = "";
            public string TimelineId { get; set; } = "";
            public string HeroId { get; set; } = "";
            public string PlayerId { get; set; } = "";
            public double WorldDay { get; set; }
            public List<string> AnnouncementIds { get; set; } = new();
        }
        private static readonly ConditionalWeakTable<Dictionary<string, object>, ConcurrentDictionary<string, NpcAnnouncementReviewWindow>> NpcAnnouncementReviewWindows = new();
        private static readonly ConditionalWeakTable<Dictionary<string, object>, ConcurrentDictionary<string, string>> NpcPurposePromptWindows = new();
        private static NpcAnnouncementReviewWindow CapturedNpcAnnouncementReview(Dictionary<string, object> p, string hero, string player) =>
            p != null && NpcAnnouncementReviewWindows.TryGetValue(p, out var windows)
                && windows.TryGetValue(hero + "|" + player, out var window) ? window : null;
        private static Func<Dictionary<string, object>, Func<bool>, Dictionary<string, object>> NpcOpeningTestProvider;
        private static string NpcLifeContextKey(Dictionary<string, object> p, string player, string hero)
        {
            string scope = ReadString(p, "npcLifeContext", "in_person");
            var audience = ReadStringList(p, "npcLifeAudienceIds");
            if (scope == "in_person") return scope;
            return scope + ":" + PromptHash(string.Join("|", audience.Concat(new[] { player, hero }).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal)));
        }
        private static NpcConversationEncounter NpcEncounter(NpcLivesState state, string player, string hero, Dictionary<string, object> p)
        {
            string scope = NpcLifeContextKey(p, player, hero);
            string id = player + ":" + hero + (scope == "in_person" ? "" : ":" + scope);
            var encounter = state.Encounters.FirstOrDefault(e => e.Id == id);
            if (encounter == null) { encounter = new NpcConversationEncounter { Id = id, HeroId = hero, ContextKey = scope }; state.Encounters.Add(encounter); }
            return encounter;
        }
        private static Dictionary<string, object> NpcLivesOpenApi(string operation, Dictionary<string, object> p, NpcLivesOptions options = null)
        {
            string campaign = ReadString(p, "campaignId", ""), timeline = ContinuityTimeline(p),
                player = ReadFirstString(p, "playerId", "playerHeroStringId"), hero = ReadFirstString(p, "heroId", "heroStringId");
            if (hero.Length == 0 || hero == player) return TestDict("ok", false, "error", "Approach needs a living NPC identity.");
            double day = ReadDouble(p, "worldDay", -1); options ??= ReadNpcLivesOptions();
            using var ownership = EnterCampaignWorkLock(CampaignRelationshipWriteLock(campaign));
            using var c = OpenCampaignConnection(campaign); var state = ReadNpcLives(c, campaign, timeline);
            var encounter = NpcEncounter(state, player, hero, p);
            string generation = NpcLivesGeneration(c), jobKey = campaign + "|" + timeline + "|" + encounter.Id;
            // This endpoint owns only the new NPC Lives Approach opener. Existing
            // wilderness/social/event and explicit invitation openings use their own routes.
            if (!options.Preference(NpcLivesOptions.InitialApproachKey))
            {
                DiscardNpcOpening(c, state, encounter);
                return TestDict("ok", true, "status", "disabled", "generation", generation, "sessionId", encounter.SessionId);
            }
            bool HasCurrentJob() => NpcOpeningJobs.TryGetValue(jobKey, out var owner) && owner == encounter.OpeningOperationId + "|" + generation;
            if (encounter.OpeningPending && encounter.OpeningGeneration != generation)
            {
                encounter.OpeningPending = false; encounter.OpeningText = ""; encounter.OpeningOperationId = "";
                WriteNpcLives(c, state); // A restored draft cannot publish into this generation.
            }
            if (operation == "opening")
            {
                if (ReadString(p, "operationId", "") != encounter.OpeningOperationId || ReadString(p, "generation", "") != generation)
                    return TestDict("ok", false, "status", "superseded");
                if (encounter.OpeningPending && !HasCurrentJob())
                    CompleteNpcOpening(c, state, encounter, p, TestDict(), generation);
                return NpcOpeningStatus(encounter, generation);
            }
            // Seed upgraded saves from their accepted in-person transcript, never
            // from letters, failed drafts or a wall-clock timestamp.
            if (!encounter.LastAcceptedDay.HasValue && encounter.ContextKey == "in_person")
            {
                var last = QuerySql(c, @"SELECT t.world_day FROM conversation_turns t WHERE t.speaker_id=$hero AND t.role='npc'
AND t.status='active' AND t.world_day<=$day AND t.channel IN ('in_person','dialogue','individual') AND jsonb_array_length(t.participants_json::jsonb)=2
AND t.participants_json::jsonb @> $player::jsonb AND t.payload_json::jsonb->>'timelineId'=$timeline
ORDER BY t.world_day DESC LIMIT 1;", TestDict("hero", hero, "day", day, "player", Json.Serialize(new[] { player }), "timeline", timeline)).FirstOrDefault();
                if (last != null) encounter.LastAcceptedDay = ReadDouble(last, "world_day", day);
            }
            if (encounter.OpeningPending)
            {
                if (!HasCurrentJob()) CompleteNpcOpening(c, state, encounter, p, TestDict(), generation);
                return NpcOpeningStatus(encounter, generation);
            }
            if (!NpcLivesPolicy.NewOpeningDue(encounter.LastAcceptedDay, day, options.Number("npcLivesResumeHours")))
                return TestDict("ok", true, "status", "resume", "generation", generation, "sessionId", encounter.SessionId);
            if (!options.Allows("approach")) return TestDict("ok", true, "status", "disabled", "generation", generation);
            string session = ReadFirstString(p, "conversationSessionId", "sessionId");
            var audience = ReadStringList(p, "npcLifeAudienceIds").Concat(new[] { player, hero }).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
            if (session.Length == 0 || !QuerySql(c, @"SELECT session_id FROM conversation_sessions WHERE session_id=$id AND player_id=$player
AND participants_json::jsonb @> $audience::jsonb AND $audience::jsonb @> participants_json::jsonb
AND payload_json::jsonb->>'timelineId'=$timeline;",
                TestDict("id", session, "audience", Json.Serialize(audience), "player", player, "timeline", timeline)).Any()) return TestDict("ok", false, "error", "The Approach audience or timeline has changed. Reopen the matching conversation.");
            string id = "opening_" + Guid.NewGuid().ToString("N");
            if (!encounter.BeginOpening(id, day, options.Number("npcLivesResumeHours"))) return TestDict("ok", true, "status", "resume");
            encounter.SessionId = session; encounter.OpeningDay = day; encounter.OpeningGeneration = generation;
            // Old saves retain this field, but elapsed wall time never substitutes
            // an ordinary Approach fallback for a provider that is still working.
            encounter.OpeningDeadlineUtcMs = 0;
            encounter.OpeningPayload = Json.Serialize(TestDict("campaignId", campaign, "timelineId", timeline, "playerId", player, "heroId", hero,
                "playerName", ReadString(p, "playerName", "Player"), "heroName", ReadString(p, "heroName", hero), "worldDay", day,
                "locationId", ReadString(p, "locationId", ""), "location", ReadString(p, "location", "the road"),
                "npcLifeContext", ReadString(p, "npcLifeContext", "in_person"), "npcLifeAudienceIds", ReadStringList(p, "npcLifeAudienceIds")));
            WriteNpcLives(c, state);
            p["npcEffectiveAffinity"] = NpcLivesAffinity(c, hero, player, ReadInt(ReadDictionary(p, "profile"), "relationToPlayer", 0));
            var request = BuildNpcOpeningRequest(state, encounter, p, player, hero, id, day);
            NpcOpeningJobs[jobKey] = id + "|" + generation;
            _ = Task.Run(() => GenerateNpcOpening(campaign, timeline, player, hero, id, generation, request, jobKey, encounter.Id));
            return TestDict("ok", true, "status", "pending", "operationId", id, "generation", generation);
        }
        private static Dictionary<string, object> BuildNpcOpeningRequest(NpcLivesState state, NpcConversationEncounter encounter,
            Dictionary<string, object> p, string player, string hero, string id, double day) =>
            CreateNpcApproachPayload(state, encounter, p, player, hero, id, day);

        private static async Task GenerateNpcOpening(string campaign, string timeline, string player, string hero, string id,
            string generation, Dictionary<string, object> request, string jobKey, string encounterId)
        {
            using var cancellation = new CancellationTokenSource();
            Dictionary<string, object> draft = TestDict();
            try
            {
                draft = await Task.Run(() => {
                    // The isolated seam exercises publication/recovery without a
                    // provider. Production always uses the complete dialogue path.
                    if (NpcOpeningTestProvider != null && !string.IsNullOrWhiteSpace(CampaignsRootOverride.Value))
                    {
                        var result = NpcOpeningTestProvider(request, () => cancellation.IsCancellationRequested);
                        return ReadBool(result, "ok", false)
                            ? SealNpcApproachDraft(request, TryParseJsonObject(ReadString(result, "content", "{}")) ?? TestDict())
                            : TestDict();
                    }
                    return RunNpcApproachDialogue(request);
                }).ConfigureAwait(false);
            }
            catch { cancellation.Cancel(); }
            try
            {
                var access = EnterCampaignRequestAccess("/npc-lives/opening");
                try
                {
                    using var ownership = EnterCampaignWorkLock(CampaignRelationshipWriteLock(campaign));
                    using var c = OpenCampaignConnection(campaign);
                    if (NpcLivesGeneration(c) != generation) return;
                    var state = ReadNpcLives(c, campaign, timeline); var encounter = state.Encounters.FirstOrDefault(e => e.Id == encounterId);
                    if (encounter == null) return;
                    if (!encounter.OpeningPending || encounter.OpeningOperationId != id || encounter.OpeningGeneration != generation) return;
                    CompleteNpcOpening(c, state, encounter, TestDict(), draft, generation);
                }
                finally { ExitCampaignRequestAccess(access); }
            }
            catch (Exception ex) { Console.Error.WriteLine("NPC opening publication will recover on reconnect: " + ex.GetType().Name); }
            finally { ((ICollection<KeyValuePair<string, string>>)NpcOpeningJobs).Remove(new KeyValuePair<string, string>(jobKey, id + "|" + generation)); }
        }
        private static void DiscardNpcOpening(ReignDbConnection c, NpcLivesState state, NpcConversationEncounter encounter)
        {
            if (!encounter.OpeningPending) return;
            string key = state.CampaignId + "|" + state.TimelineId + "|" + encounter.Id;
            ((ICollection<KeyValuePair<string, string>>)NpcOpeningJobs).Remove(
                new KeyValuePair<string, string>(key, encounter.OpeningOperationId + "|" + encounter.OpeningGeneration));
            encounter.OpeningPending = false; encounter.OpeningText = ""; encounter.OpeningOperationId = "";
            encounter.OpeningGeneration = ""; encounter.OpeningPayload = ""; encounter.OpeningDeadlineUtcMs = 0;
            WriteNpcLives(c, state);
        }
        private static void CompleteNpcOpening(ReignDbConnection c, NpcLivesState state, NpcConversationEncounter encounter,
            Dictionary<string, object> unused, Dictionary<string, object> draft, string generation, NpcLivesOptions options = null)
        {
            if (!encounter.OpeningPending || encounter.OpeningGeneration != generation) return;
            // Recheck on publication as well as dispatch: turning the switch off
            // while a provider is working must not publish a greeting or consume reactions.
            if (!(options ?? ReadNpcLivesOptions()).Preference(NpcLivesOptions.InitialApproachKey))
            {
                DiscardNpcOpening(c, state, encounter);
                return;
            }
            var p = TryParseJsonObject(encounter.OpeningPayload);
            string player = ReadString(p, "playerId", ""), hero = encounter.HeroId;
            string activity = ReadString(draft, "activity", "").Trim(), reply = ReadString(draft, "reply", "").Trim();
            if (!NpcApproachReplyShape(draft)
                || !NpcApproachDraftMatches(draft, state, encounter, p, generation, out var receipt))
            {
                FailNpcOpening(c, state, encounter);
                return;
            }
            var audience = ReadStringList(p, "npcLifeAudienceIds").Concat(new[] { player, hero }).Distinct(StringComparer.Ordinal).ToList();
            if (!QuerySql(c, @"SELECT session_id FROM conversation_sessions WHERE session_id=$id AND player_id=$player
AND participants_json::jsonb @> $audience::jsonb AND $audience::jsonb @> participants_json::jsonb
AND payload_json::jsonb->>'timelineId'=$timeline;",
                TestDict("id", encounter.SessionId, "player", player, "audience", Json.Serialize(audience), "timeline", state.TimelineId)).Any())
            {
                FailNpcOpening(c, state, encounter);
                return;
            }
            using var transaction = c.BeginTransaction();
            p.Remove("openingError");
            foreach (var field in receipt.PublicationContext) p[field.Key] = field.Value;
            encounter.Receptiveness = new[] { "busy", "neutral", "receptive", "needs_help" }.Contains(ReadString(draft, "receptiveness", ""))
                ? ReadString(draft, "receptiveness", "") : "neutral";
            string source = encounter.OpeningOperationId + "_npc";
            int ordinal = ReadInt(QuerySql(c, "SELECT COALESCE(MAX(turn_order),-1)+1 AS n FROM conversation_turns WHERE session_id=$id;",
                TestDict("id", encounter.SessionId)).First(), "n", 0);
            InsertConversationTurn(c, source, encounter.SessionId, encounter.OpeningOperationId, ordinal, encounter.OpeningOperationId,
                "npc", hero, ReadString(p, "heroName", hero), reply, encounter.ContextKey == "in_person" ? "in_person" : "party_chat", encounter.OpeningDay, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ReadString(p, "locationId", ""), audience, p);
            if (!encounter.AcceptOpening(encounter.OpeningOperationId, reply, activity, encounter.OpeningDay))
                throw new InvalidOperationException("The opening was superseded before acceptance.");
            AcceptNpcAnnouncementReactions(state, hero, player, source, reply, draft, encounter.OpeningDay, receipt.Review);
            WriteNpcLives(c, state); transaction.Commit();
        }

        private static string NpcLivesPrompt(string campaign, string hero, Dictionary<string, object> p)
        {
            if (p == null || string.IsNullOrWhiteSpace(campaign) || string.IsNullOrWhiteSpace(hero)) return "";
            using var c = OpenCampaignConnection(campaign);
            var state = ReadNpcLives(c, campaign, ContinuityTimeline(p));
            string player = ReadFirstString(p, "playerHeroStringId", "playerId", "mainHeroStringId");
            string body = NpcLivesPromptBody(state, hero, player, p, ReadNpcLivesOptions(), c);
            // Context compaction retains complete typed records. Unmarked text next
            // to memory records was silently lost before the actual provider request.
            // Owner facts, current story rules and the entire review window are a
            // mandatory source: final preflight must preserve them or refuse dispatch.
            return body.Length == 0 ? "" : RenderContinuityRecord("npc_life_" + PromptHash(state.TimelineId + "|" + hero + "|" + player + "|" + state.Revision).Substring(0, 24),
                "npc_personal_life", hero, state.TimelineId, ReadDouble(p, "worldDay", 0), 140, true, body);
        }
        private static string NpcLivesPromptBody(NpcLivesState state, string hero, string player, Dictionary<string, object> p, NpcLivesOptions options, ReignDbConnection communicationConnection = null)
        {
            var b = new StringBuilder();
            var person = state.Characters.FirstOrDefault(n => n.HeroId == hero);
            var facts = person?.Facts.Where(f => f.Active).ToList() ?? new List<NpcLifeFact>();
            // Durable possessions and relationships always remain present. Keep recent
            // experiences concise; their full accepted transcripts remain in memory.
            foreach (var fact in facts.Where(f => f.Kind != "experience").Concat(facts.Where(f => f.Kind == "experience").TakeLast(8)))
                b.AppendLine("Established personal fact [" + fact.Kind + "]: " + fact.Text);
            foreach (var meeting in state.Meetings.Where(m => (m.IssuerId == hero || m.TargetId == hero)
                && m.Status != "completed" && m.Status != "cancelled" && m.Status != "interrupted"
                && (m.ProposalId?.StartsWith("comm_", StringComparison.Ordinal) != true || m.TargetId == hero
                    || CommunicationReplyKnown(communicationConnection, m.ProposalId, hero, p))))
                b.AppendLine("Accepted meeting commitment [" + meeting.Id + "]: " + meeting.Purpose + "; destination " + meeting.DestinationId
                    + "; scheduled arrival day " + meeting.ScheduledArrivalDay + "; actual arrival day " + meeting.ArrivalDay
                    + "; full stay " + meeting.StayDays + " days after actual arrival; release day " + meeting.ReleaseDay + ". "
                    + (meeting.SoloAttendance ? "The summoned person attends alone; their party stays protected at its original camp throughout the wait and meeting, and they teleport back after the final commitment. " : "This historical agreement retains its original party-attendance arrangement. ")
                    + "Both participants must respect the accepted stay. Dialogue cannot reschedule, cancel or free a prisoner; changing these terms requires the explicit agreed action.");
            string scope = NpcLifeContextKey(p, player, hero);
            var encounter = state.Encounters.FirstOrDefault(e => e.HeroId == hero && e.ContextKey == scope);
            if (!string.IsNullOrWhiteSpace(encounter?.Activity)) b.AppendLine("Last observed activity: " + encounter.Activity + "; receptiveness then: " + encounter.Receptiveness + ". Use current native circumstances if time or duties have changed.");
            var heard = state.Announcements.Where(a => a.CanRead(hero) && a.PlayerId == player
                && a.WorldDay <= ReadDouble(p, "worldDay", double.MaxValue)).ToList();
            var pending = heard.Where(a => a.ReactionPending(hero)).ToList();
            NpcAnnouncementReviewWindows.GetValue(p, _ => new ConcurrentDictionary<string, NpcAnnouncementReviewWindow>())[hero + "|" + player] =
                new NpcAnnouncementReviewWindow { CampaignId = state.CampaignId, TimelineId = state.TimelineId, HeroId = hero,
                    PlayerId = player, WorldDay = ReadDouble(p, "worldDay", double.MaxValue),
                    AnnouncementIds = options.Allows("reaction") ? pending.Select(a => a.Id).ToList() : new List<string>() };
            // Pending announcements are never truncated by the recent-memory cap.
            // The final identity freezes this review window even if another arrives
            // while the provider is working; later sources stay pending.
            foreach (var a in heard.Where(a => !a.ReactionPending(hero)).TakeLast(12))
                b.AppendLine("Previously considered party announcement [" + a.Id + "]: " + a.Text + " Do not repeat a reaction merely because this memory is present.");
            foreach (var a in pending)
            {
                b.AppendLine("You personally heard this as-yet-unconsidered party announcement [" + a.Id + "]: " + a.Text);
            }
            if (pending.Count > 0 && options.Allows("reaction"))
                b.AppendLine("Consider ALL " + pending.Count + " pending announcements above together in light of your personality, interests, relationship and current activity. You may comment on some or all, combine your thoughts, or choose not to discuss any of them and simply continue your day. Do not invent a reason to care or an obligatory acknowledgment. No one outside each recorded audience heard it. Return announcementReview:{reviewedAll:true,disposition:'commented' or 'no_comment',evidenceQuote:'an exact 8–40 character excerpt of your visible reply if commenting; otherwise empty'}. The server already captured exactly this supplied batch; do not copy announcement IDs into the review. This does not claim you spoke about every announcement. Use no_comment when deliberately leaving them alone. Do not claim review in an empty or unpublished reply.");
            CaptureNpcPurposePrompt(state, hero, p);
            AppendNpcStoryPrompt(b, state, hero, p);
            return b.Length == 0 ? "" : "\nNPC PERSONAL LIFE — authoritative owner-only context. Never share private facts merely because they appear here.\n" + b;
        }
        private static void AcceptNpcAnnouncementReactions(NpcLivesState state, string hero, string player, string source, string reply, Dictionary<string, object> parsed, double day,
            NpcAnnouncementReviewWindow window = null)
        {
            if (string.IsNullOrWhiteSpace(reply) || string.IsNullOrWhiteSpace(source) || IsTechnicalConversationNotice(reply)
                || !ReadNpcLivesOptions().Allows("reaction")) return;
            var review = ReadDictionary(parsed, "announcementReview");
            string through = ReadString(review, "throughId", ""), disposition = ReadString(review, "disposition", ""),
                quote = ReadString(review, "evidenceQuote", "");
            var pending = state.Announcements.Where(a => a.PlayerId == player && a.WorldDay <= day && a.ReactionPending(hero)).ToList();
            int last = pending.FindIndex(a => a.Id == through);
            bool addressed = quote.Trim().Length >= 8 && reply.IndexOf(quote, StringComparison.Ordinal) >= 0;
            bool validChoice = disposition == "no_comment" || disposition == "commented" && addressed;
            if (ReadBool(review, "reviewedAll", false))
            {
                if (validChoice && window != null && window.CampaignId == state.CampaignId && window.TimelineId == state.TimelineId
                    && window.HeroId == hero && window.PlayerId == player && window.WorldDay == day)
                {
                    var captured = new HashSet<string>(window.AnnouncementIds, StringComparer.Ordinal);
                    foreach (var a in pending.Where(a => captured.Contains(a.Id))) a.MarkReviewed(hero, source, disposition, true);
                }
            }
            else if (last >= 0 && validChoice)
                foreach (var a in pending.Take(last + 1)) a.MarkReviewed(hero, source, disposition, true);
            // Retain compatibility with already in-flight per-announcement replies.
            foreach (var row in ReadDictionaryList(parsed, "announcementReactions"))
            {
                string legacyQuote = ReadString(row, "evidenceQuote", "");
                if (legacyQuote.Trim().Length < 8 || reply.IndexOf(legacyQuote, StringComparison.Ordinal) < 0) continue;
                state.Announcements.FirstOrDefault(a => a.Id == ReadString(row, "id", "") && a.PlayerId == player && a.WorldDay <= day)?.MarkReacted(hero, source, true);
            }
        }
        private static Dictionary<string, object> RecordAcceptedNpcLives(ReignDbConnection c, AcceptedConversationUnit unit, List<Dictionary<string, object>> sources)
        {
            string mode = ReadFirstString(unit.Payload, "channel", "conversationMode");
            if (mode.Contains("letter") || mode.Contains("correspondence")) return TestDict("status", "excluded");
            var npcSource = sources.LastOrDefault(r => ReadString(r, "speaker_id", "") == unit.Hero && ReadString(r, "role", "") == "npc");
            if (npcSource == null) return TestDict("status", "no_source");
            var state = ReadNpcLives(c, unit.Campaign, ContinuityTimeline(unit.Payload));
            string source = ReadString(npcSource, "turn_id", "");
            var encounter = NpcEncounter(state, unit.Player, unit.Hero, unit.Payload);
            if (encounter.AcceptTurn(source, ReadDouble(unit.Payload, "worldDay", 0))) encounter.OpeningPending = false;
            encounter.SessionId = ReadFirstString(unit.Payload, "conversationSessionId", "sessionId");
            AcceptNpcAnnouncementReactions(state, unit.Hero, unit.Player, source, unit.Reply, unit.Parsed, ReadDouble(unit.Payload, "worldDay", 0),
                CapturedNpcAnnouncementReview(unit.Payload, unit.Hero, unit.Player));
            AcceptNpcStoryAssessment(c, state, unit, sources);
            WriteNpcLives(c, state);
            return TestDict("status", "committed", "sourceId", source, "revision", state.Revision);
        }
    }
}
