using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using ReignBeta.Shared;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly ConcurrentDictionary<string, byte> CommunicationWorkers = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, (string Generation, string Hero)> CommunicationPlayers = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string CommunicationWorkerBoot = Guid.NewGuid().ToString("N");
        private static bool CommunicationPlayerControlled(string campaign, string generation, string hero, string originalPlayer) =>
            GossipSameId(hero, originalPlayer) || CommunicationPlayers.TryGetValue(campaign, out var current)
                && current.Generation == generation && GossipSameId(current.Hero, hero);
        private sealed class CommunicationDataReadLease : IDisposable
        {
            private readonly bool _owns;
            internal CommunicationDataReadLease()
            {
                _owns = !CampaignDataGate.IsReadLockHeld && !CampaignDataGate.IsWriteLockHeld;
                if (_owns) CampaignDataGate.EnterReadLock();
            }
            public void Dispose() { if (_owns) CampaignDataGate.ExitReadLock(); }
        }

        private static double CommunicationBaseTransit(Dictionary<string, object> sender, Dictionary<string, object> recipient)
        {
            if (GossipSameId(ReadString(sender, "settlementId", ""), ReadString(recipient, "settlementId", ""))
                || GossipSameId(ReadString(sender, "partyId", ""), ReadString(recipient, "partyId", ""))) return 0;
            return GossipTransitDays(sender, recipient, DefaultPersonalGossipSettings(), false);
        }

        private static string CommunicationBody(Dictionary<string, object> row, Dictionary<string, object> data, bool invitationOnly = false)
        {
            string sender = ReadString(data, "speakerName", ReadString(row, "speaker_id", ""));
            if (ReadString(row, "parent_id", "").Length > 0) return ReadString(data, "body", "");
            string invitation = "";
            if (invitationOnly || ReadString(row, "kind", "") == "summon")
            {
                string place = ReadString(data, "destinationName", "");
                if (place.Length == 0) place = ReadString(data, "destinationId", "").StartsWith("field:", StringComparison.Ordinal)
                    ? "the agreed camp" : "the agreed meeting place";
                double stay = ReadDouble(data, "stayDays", 0);
                invitation = sender + " sends for you to meet at " + place + " and remain for "
                    + (stay == 1 ? "one full day" : stay.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " days") + " after arrival. "
                    + "Come alone. If you lead a party, your troops will wait safely at their original camp throughout your travel and stay; you will return to them afterward.";
                if (invitationOnly) return invitation;
            }
            bool question = ReadString(row, "kind", "") == "ask";
            var body = new StringBuilder((invitation.Length > 0 ? invitation + "\n" : "") + sender + (question
                ? " asks you to answer the following request in your reply. The quoted request was originally addressed to the sender; references to the named recipient mean you.\n"
                : " sends this account of the conversation:\n"));
            foreach (var passage in ReadDictionaryList(data, "contentEvidence"))
            {
                string actor = ReadString(passage, "speakerId", "") == ReadString(row, "speaker_id", "") ? sender : ReadString(data, "playerName", "the other speaker");
                body.AppendLine(actor + " said: «" + ReadString(passage, "quote", "") + "»");
            }
            return body.ToString().Trim();
        }

        private static void CommunicationHistory(ReignDbConnection c, Dictionary<string, object> row, string owner, string peer, double day, string body)
        {
            string id = ReadString(row, "message_id", "");
            ExecuteSql(c, @"INSERT INTO communication_history(receipt_id,message_id,timeline_id,owner_id,peer_id,acquired_day,acquired_ts,payload_json)
VALUES($receipt,$id,$timeline,$owner,$peer,$day,$ts,$json) ON CONFLICT(message_id,owner_id) DO NOTHING;",
                TestDict("receipt", id + "_" + PromptHash(owner).Substring(0, 12).ToLowerInvariant(), "id", id,
                    "timeline", ReadString(row, "timeline_id", "main"), "owner", owner, "peer", peer, "day", day, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    "json", Json.Serialize(TestDict("body", body, "speakerId", ReadString(row, "speaker_id", ""),
                        "recipientId", ReadString(row, "recipient_id", ""), "kind", ReadString(row, "kind", ""), "evidenceType", "attributed_message"))));
            string receipt = id + "_" + PromptHash(owner).Substring(0, 12).ToLowerInvariant(), session = receipt + "_session";
            var sourceSession = QuerySql(c, "SELECT campaign_id FROM conversation_sessions WHERE session_id=$id;", TestDict("id", ReadString(row, "session_id", ""))).FirstOrDefault();
            var visible = TestDict("body", body, "timelineId", ReadString(row, "timeline_id", "main"), "messageId", id,
                "ownerId", owner, "peerId", peer, "evidenceType", "attributed_message", "visibility", "npc_private");
            ExecuteSql(c, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,participants_json,payload_json)
VALUES($id,$campaign,$owner,'','npc_correspondence','closed',$ts,$ts,$day,$day,$participants,$json) ON CONFLICT(session_id) DO NOTHING;",
                TestDict("id", session, "campaign", ReadString(sourceSession, "campaign_id", ""), "owner", owner,
                    "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "day", day, "participants", Json.Serialize(new[] { owner }), "json", Json.Serialize(visible)));
            if (QuerySql(c, "SELECT turn_id FROM conversation_turns WHERE turn_id=$id;", TestDict("id", receipt)).Count == 0)
                InsertConversationTurn(c, receipt, session, receipt, 0, receipt, "npc", ReadString(row, "speaker_id", ""),
                    ReadString(GossipJson(row), "speakerName", ReadString(row, "speaker_id", "")),
                    "[Private correspondence; " + (owner == ReadString(row, "speaker_id", "") ? "you sent" : "you received")
                        + "; attributed speech, not independently verified world facts] " + body,
                    "npc_correspondence", day, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", new List<string> { owner }, visible);
        }

        // Called only with a fresh complete native snapshot, never by a wall-clock worker.
        private static void AdvanceCommunications(ReignDbConnection c, string campaign, string timeline, double day,
            List<Dictionary<string, object>> people)
        {
            foreach (var row in QuerySql(c, @"SELECT * FROM communication_messages WHERE timeline_id=$timeline
AND status IN ('queued','awaiting_sender','in_transit','awaiting_recipient') ORDER BY created_day,message_id LIMIT 128;", TestDict("timeline", timeline)))
            {
                var data = GossipJson(row);
                var sender = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), ReadString(row, "speaker_id", "")));
                var recipient = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), ReadString(row, "recipient_id", "")));
                if (new[] { "queued", "awaiting_sender" }.Contains(ReadString(row, "status", "")))
                {
                    if (day < ReadDouble(data, "dispatchNotBefore", ReadDouble(row, "created_day", 0))) continue;
                    if (ReadString(row, "parent_id", "").Length == 0 && !CommunicationSourcesActive(c, data))
                    { SetCommunicationStatus(c, row, "cancelled_source", data); continue; }
                    if (!GossipCommunicationAvailable(sender)) { SetCommunicationStatus(c, row, "awaiting_sender", data); continue; }
                    if (recipient == null || !ReadBool(recipient, "isAlive", false))
                    { data["failure"] = "The recipient is no longer living or present in the native roster."; SetCommunicationStatus(c, row, "undeliverable", data); continue; }
                    double baseTransit = CommunicationBaseTransit(sender, recipient);
                    double transit = baseTransit == 0 ? 0 : CorrespondenceTransitPolicy.NewTransitDays(baseTransit, false,
                        CorrespondenceOption(LoadSettings(), "correspondenceTravelTimeMultiplier"));
                    data["baseTransitDays"] = baseTransit; data["transitDays"] = transit;
                    data["dispatchPresence"] = TestDict("speaker", sender, "recipient", recipient);
                    data["body"] = CommunicationBody(row, data, ReadString(row, "kind", "") == "summon_then_inform");
                    ExecuteSql(c, "UPDATE communication_messages SET status='in_transit',dispatch_day=$day,due_day=$due,payload_json=$json WHERE message_id=$id;",
                        TestDict("id", ReadString(row, "message_id", ""), "day", day, "due", day + transit, "json", Json.Serialize(data)));
                    row["status"] = "in_transit"; row["due_day"] = day + transit;
                    CommunicationHistory(c, row, ReadString(row, "speaker_id", ""), ReadString(row, "recipient_id", ""), day, ReadString(data, "body", ""));
                }
                if (day < ReadDouble(row, "due_day", double.PositiveInfinity)) continue;
                if (!GossipCommunicationAvailable(recipient)) { SetCommunicationStatus(c, row, "awaiting_recipient", data); continue; }
                string body = ReadString(data, "body", "");
                CommunicationHistory(c, row, ReadString(row, "recipient_id", ""), ReadString(row, "speaker_id", ""), day, body);
                bool original = ReadString(row, "parent_id", "").Length == 0;
                string status = original && ReadString(row, "recipient_id", "") != ReadString(row, "player_id", "") ? "reply_pending" : "delivered";
                ExecuteSql(c, "UPDATE communication_messages SET status=$status,acquired_day=$day WHERE message_id=$id;",
                    TestDict("id", ReadString(row, "message_id", ""), "status", status, "day", day));
                if (!original)
                    ExecuteSql(c, "UPDATE communication_messages SET status=CASE WHEN status='awaiting_reply' THEN 'replied' ELSE status END WHERE message_id=$id;",
                        TestDict("id", ReadString(row, "parent_id", "")));
            }
        }

        private static Dictionary<string, object> CommunicationApi(string operation, Dictionary<string, object> p)
        {
            string campaign = ReadString(p, "campaignId", ""), timeline = ContinuityTimeline(p);
            if (campaign.Length == 0) return TestDict("ok", false, "error", "An exact campaign is required.");
            using var ownership = EnterCampaignWorkLock(CampaignRelationshipWriteLock(campaign));
            using var c = OpenCampaignConnection(campaign);
            if (operation == "status")
            {
                if (ActiveWorldHistoryTimeline(c) != timeline) return TestDict("ok", false, "error", "The communication timeline is not active.");
                return ReadCommunicationSnapshot(c, timeline, ReadString(p, "heroId", ""), ReadInt(p, "offset", 0), ReadInt(p, "limit", 50));
            }
            EnsureCommunicationSchema(c); EnsureMemoryPrecisionSchema(c);
            string generation = NpcLivesGeneration(c);
            if (ActiveWorldHistoryTimeline(c) != timeline) return TestDict("ok", false, "error", "The communication timeline is not active.");
            if (operation == "scope") return TestDict("ok", true, "generation", generation);
            if (operation != "tick") return TestDict("ok", false, "error", "Unknown communication operation.");
            if (ReadString(p, "generation", "") != generation) return TestDict("ok", false, "status", "stale_generation", "generation", generation);
            EnsurePersonalGossipSchema(c);
            if (!GossipAcceptPresence(c, p)) return TestDict("ok", false, "error", "A fresh complete native presence snapshot is required.");
            CommunicationPlayers[campaign] = (generation, ReadFirstString(p, "playerId", "playerHeroStringId"));
            double day = ReadDouble(p, "worldDay", -1);
            using (var transaction = c.BeginTransaction())
            {
                ExpireCommunicationHostWaits(c, timeline, day);
                AdvanceCommunications(c, campaign, timeline, day, ReadDictionaryList(ReadDictionary(p, "gossipPresence"), "people"));
                transaction.Commit();
            }
            var ready = QuerySql(c, "SELECT * FROM communication_messages WHERE timeline_id=$timeline AND status='summons_ready' ORDER BY created_day LIMIT 8;", TestDict("timeline", timeline))
                .Select(row => TestDict("id", ReadString(row, "message_id", ""), "revision", ReadInt(row, "revision", 1),
                    "speakerId", ReadString(row, "speaker_id", ""), "recipientId", ReadString(row, "recipient_id", ""),
                    "destinationId", ReadString(GossipJson(row), "destinationId", ""), "stayDays", ReadDouble(GossipJson(row), "stayDays", 0),
                    "purpose", "Meeting requested by " + ReadString(GossipJson(row), "speakerName", ReadString(row, "speaker_id", "")),
                    "acceptanceHash", ReadString(GossipJson(row), "acceptanceHash", ""),
                    "hostReservationHash", ReadString(GossipJson(row), "hostReservationHash", ""))).ToList();
            ScheduleCommunicationReplies(campaign);
            return TestDict("ok", true, "generation", generation, "summons", ready, "hostReservations", CommunicationHostWork(c, timeline));
        }

        private static readonly string[] CommunicationHostWaitingStatuses = { "awaiting_host", "queued", "awaiting_sender", "in_transit", "awaiting_recipient",
            "reply_pending", "reply_generating", "summons_ready", "awaiting_reply" };

        private static List<Dictionary<string, object>> CommunicationHostWork(ReignDbConnection c, string timeline) =>
            QuerySql(c, "SELECT * FROM communication_messages WHERE timeline_id=$timeline AND parent_id='' AND kind IN ('summon','summon_then_inform') ORDER BY created_day,message_id;",
                TestDict("timeline", timeline)).Where(r => CommunicationHostWaitingStatuses.Contains(ReadString(r, "status", "")))
            .Where(r => ReadString(GossipJson(r), "hostReservationHash", "").Length > 0)
            .Select(r => { var d = GossipJson(r); return TestDict("id", ReadString(r, "message_id", ""), "speakerId", ReadString(r, "speaker_id", ""),
                "recipientId", ReadString(r, "recipient_id", ""), "destinationId", ReadString(d, "destinationId", ""),
                "reservationHash", ReadString(d, "hostReservationHash", ""), "expiresDay", ReadDouble(d, "hostWaitUntilDay", 0)); }).ToList();

        private static void ExpireCommunicationHostWaits(ReignDbConnection c, string timeline, double day)
        {
            foreach (var work in CommunicationHostWork(c, timeline).Where(w => day >= ReadDouble(w, "expiresDay", 0)))
            {
                var row = QuerySql(c, "SELECT * FROM communication_messages WHERE message_id=$id;", TestDict("id", ReadString(work, "id", ""))).Single();
                var data = GossipJson(row); data["failure"] = "The host's fourteen-day invitation wait expired before an accepted journey began.";
                SetCommunicationStatus(c, row, "expired", data);
            }
        }

        private static Dictionary<string, object> ReadCommunicationSnapshot(ReignDbConnection c, string timeline, string hero, int offset, int limit)
        {
            if (!TableExists(c, "communication_messages")) return TestDict("ok", true, "schemaReady", false, "messages", Array.Empty<object>(),
                "history", Array.Empty<object>(), "messageCount", 0, "historyCount", 0, "timelineId", timeline);
            var args = TestDict("timeline", timeline, "hero", hero, "offset", Math.Clamp(offset, 0, 1000000), "limit", Math.Clamp(limit, 1, 100));
            string messages = " FROM communication_messages WHERE timeline_id=$timeline AND ($hero='' OR speaker_id=$hero OR recipient_id=$hero)";
            string history = " FROM communication_history WHERE timeline_id=$timeline AND ($hero='' OR owner_id=$hero)";
            var admissions = TableExists(c, "conversation_accepted_units") ? QuerySql(c, @"SELECT a.unit_id,a.created_ts,
(a.receipt_json::jsonb->'communications')::text AS admission_json FROM conversation_accepted_units a
WHERE a.receipt_json::jsonb ? 'communications' AND EXISTS(SELECT 1 FROM conversation_turns t
JOIN conversation_sessions s ON s.session_id=t.session_id WHERE t.exchange_id=a.unit_id AND t.role='npc'
AND ($hero='' OR t.speaker_id=$hero)
AND COALESCE(t.payload_json::jsonb->>'timelineId',s.payload_json::jsonb->>'timelineId','main')=$timeline)
ORDER BY a.created_ts DESC,a.unit_id LIMIT $limit OFFSET $offset;", args) : new List<Dictionary<string, object>>();
            return TestDict("ok", true, "generation", NpcLivesGeneration(c), "timelineId", timeline,
                "admissions", admissions,
                "messages", QuerySql(c, "SELECT *" + messages + " ORDER BY created_day,message_id LIMIT $limit OFFSET $offset;", args),
                "history", QuerySql(c, "SELECT *" + history + " ORDER BY acquired_day,receipt_id LIMIT $limit OFFSET $offset;", args),
                "messageCount", ReadLong(QuerySql(c, "SELECT COUNT(*) AS n" + messages + ";", args).Single(), "n", 0),
                "historyCount", ReadLong(QuerySql(c, "SELECT COUNT(*) AS n" + history + ";", args).Single(), "n", 0),
                "coverage", "Read-only ledger evidence; receipt presence does not certify native travel or provider semantics.");
        }

        private static Dictionary<string, object> CommunicationReadiness(ReignDbConnection c, string timeline, double day)
        {
            if (!TableExists(c, "communication_messages")) return TestDict("active", false, "pending", 0L, "due", 0L, "failed", 0L);
            var counts = QuerySql(c, @"SELECT
COUNT(*) FILTER (WHERE status IN ('awaiting_host','reply_pending','reply_generating','summons_ready')) AS pending,
COUNT(*) FILTER (WHERE status='in_transit' AND due_day<=$day OR status='queued' AND COALESCE((payload_json::jsonb->>'dispatchNotBefore')::double precision,created_day)<=$day) AS due,
COUNT(*) FILTER (WHERE status='reply_failed') AS failed,
COUNT(*) AS total FROM communication_messages WHERE timeline_id=$timeline;", TestDict("timeline", timeline, "day", day)).Single();
            return TestDict("active", ReadLong(counts, "total", 0) > 0, "pending", ReadLong(counts, "pending", 0),
                "due", ReadLong(counts, "due", 0), "failed", ReadLong(counts, "failed", 0));
        }

        private static void ScheduleCommunicationReplies(string campaign)
        {
            if (!CommunicationWorkers.TryAdd(campaign, 0)) return;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { ProcessCommunicationReplies(campaign); }
                catch (Exception ex) { Console.Error.WriteLine("Reign communication worker: " + ex.GetType().Name); }
                finally { CommunicationWorkers.TryRemove(campaign, out _); }
            });
        }

        private static void ProcessCommunicationReplies(string campaign, Func<Dictionary<string, object>, Dictionary<string, object>> respond = null)
        {
            for (int count = 0; count < 8; count++)
            {
                Dictionary<string, object> row, data, replyContext;
                string generation, lease;
                using (new CommunicationDataReadLease())
                using (EnterCampaignWorkLock(CampaignRelationshipWriteLock(campaign)))
                using (var c = OpenCampaignConnection(campaign))
                {
                    EnsureCommunicationSchema(c); EnsureMemoryPrecisionSchema(c);
                    generation = NpcLivesGeneration(c);
                    string leasePrefix = CommunicationWorkerBoot + ":" + generation + ":";
                    lease = leasePrefix + Guid.NewGuid().ToString("N");
                    string timeline = ActiveWorldHistoryTimeline(c);
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    ExecuteSql(c, @"UPDATE communication_messages SET status='reply_failed' WHERE timeline_id=$timeline
AND status='reply_generating' AND attempts>=3 AND (lease_until<$now OR lease_token NOT LIKE $prefix);",
                        TestDict("timeline", timeline, "now", now, "prefix", leasePrefix + "%"));
                    row = QuerySql(c, @"SELECT * FROM communication_messages WHERE timeline_id=$timeline AND attempts<3
AND (status='reply_pending' OR status='reply_generating' AND (lease_until<$now OR lease_token NOT LIKE $prefix)) ORDER BY acquired_day,message_id LIMIT 1;",
                        TestDict("timeline", timeline, "now", now, "prefix", leasePrefix + "%")).FirstOrDefault();
                    if (row == null) return;
                    data = GossipJson(row);
                    if (CommunicationPlayerControlled(campaign, generation, ReadString(row, "recipient_id", ""), ReadString(row, "player_id", "")))
                    { SetCommunicationStatus(c, row, "awaiting_player", data); continue; }
                    replyContext = TestDict("campaignId", campaign, "timelineId", timeline,
                        "speakerId", ReadString(row, "recipient_id", ""), "playerId", ReadString(row, "player_id", ""),
                        "currentDay", ReadDouble(row, "acquired_day", 0), "memoryAsOfTimestamp", now,
                        "memoryAsOfAcceptedSequence", ReadAcceptedMemorySourceWatermark(c));
                    PrepareCommunicationReply(c, campaign, row, replyContext);
                    ExecuteSql(c, "UPDATE communication_messages SET status='reply_generating',attempts=attempts+1,lease_token=$lease,lease_until=$until WHERE message_id=$id;",
                        TestDict("id", ReadString(row, "message_id", ""), "lease", lease, "until", now + 600));
                }
                Dictionary<string, object> result;
                try { result = ComposeCommunicationReply(campaign, row, data, replyContext, respond ?? ChatWithLlm); }
                catch (Exception ex) { result = TestDict("ok", false, "error", "Reply generation failed: " + ex.GetType().Name); }
                using (new CommunicationDataReadLease())
                using (EnterCampaignWorkLock(CampaignRelationshipWriteLock(campaign)))
                using (var c = OpenCampaignConnection(campaign))
                {
                    if (generation != NpcLivesGeneration(c)) return;
                    var current = QuerySql(c, "SELECT * FROM communication_messages WHERE message_id=$id AND lease_token=$lease AND status='reply_generating';",
                        TestDict("id", ReadString(row, "message_id", ""), "lease", lease)).FirstOrDefault();
                    if (current == null) continue;
                    if (CommunicationPlayerControlled(campaign, generation, ReadString(row, "recipient_id", ""), ReadString(row, "player_id", "")))
                    { SetCommunicationStatus(c, current, "awaiting_player", data); continue; }
                    if (ReadBool(result, "ok", false) && !CommunicationReplyDisclosureAllowed(c, row, replyContext, result))
                        result = TestDict("ok", false, "error", "The proposed reply would disclose a protected confidence.");
                    if (!ReadBool(result, "ok", false))
                    {
                        data["lastError"] = ReadString(result, "error", "The reply was not source supported.");
                        SetCommunicationStatus(c, current, ReadInt(current, "attempts", 0) < 3 ? "reply_pending" : "reply_failed", data); continue;
                    }
                    string answer = ReadString(result, "answerKind", "acknowledged"), replyId = ReadString(row, "message_id", "") + "_reply";
                    double day = ReadDouble(GossipClock(c, ReadString(row, "timeline_id", "main")), "world_day", ReadDouble(row, "acquired_day", 0));
                    bool summons = ReadString(row, "kind", "").StartsWith("summon", StringComparison.Ordinal) && answer == "accepted";
                    data["replyBody"] = ReadString(result, "body", ""); data["replyKind"] = answer;
                    data["replySourceQuote"] = ReadString(result, "sourceQuote", "");
                    data["replyEvidenceSourceIds"] = ReadDictionaryList(replyContext, "replyEvidence")
                        .Where(e => ReadString(result, "sourceQuote", "").Length > 0
                            && ReadString(e, "text", "").Contains(ReadString(result, "sourceQuote", ""), StringComparison.Ordinal))
                        .Select(e => ReadString(e, "sourceId", "")).Distinct().ToList();
                    if (data.ContainsKey("lastError")) { data["previousCompositionError"] = data["lastError"]; data.Remove("lastError"); }
                    if (summons) data["acceptanceHash"] = PromptHash(CanonicalJson(TestDict("messageId", ReadString(row, "message_id", ""),
                        "revision", ReadInt(row, "revision", 1), "source", data, "reply", result)));
                    using var transaction = c.BeginTransaction();
                    var reply = TestDict("body", ReadString(result, "body", ""), "speakerName", ReadString(ReadDictionary(data, "recipient"), "name", ""),
                        "dispatchNotBefore", day, "answerKind", answer);
                    ExecuteSql(c, @"INSERT INTO communication_messages(message_id,root_id,parent_id,timeline_id,logical_id,session_id,speaker_id,recipient_id,player_id,
topic_key,kind,status,created_day,payload_json) VALUES($id,$root,$root,$timeline,$logical,$session,$speaker,$recipient,$player,'reply','reply','queued',$day,$json)
ON CONFLICT(message_id) DO NOTHING;", TestDict("id", replyId, "root", ReadString(row, "message_id", ""), "timeline", ReadString(row, "timeline_id", ""),
                        "logical", ReadString(row, "logical_id", ""), "session", ReadString(row, "session_id", ""), "speaker", ReadString(row, "recipient_id", ""),
                        "recipient", ReadString(row, "speaker_id", ""), "player", ReadString(row, "player_id", ""), "day", day, "json", Json.Serialize(reply)));
                    SetCommunicationStatus(c, current, summons ? "summons_ready" : "awaiting_reply", data);
                    transaction.Commit();
                }
            }
        }

        private static void PrepareCommunicationReply(ReignDbConnection c, string campaign, Dictionary<string, object> row, Dictionary<string, object> context)
        {
            PreparePersonalGossipReply(c, campaign, row, context);
            string owner = ReadString(context, "speakerId", ""), peer = ReadString(row, "speaker_id", "");
            var scope = CloneDictionary(context); scope["worldDay"] = ReadDouble(context, "currentDay", 0);
            var access = BuildKnowledgeAccessContext(owner, ReadString(context, "playerId", ""), "", scope);
            // Only correspondence already exchanged with this addressee belongs
            // in this reply. Another NPC's private letters grant no disclosure.
            var acquired = QuerySql(c, @"SELECT * FROM communication_history WHERE owner_id=$owner AND peer_id=$peer AND timeline_id=$timeline
AND acquired_day<=$day AND acquired_ts<=$ts AND accepted_sequence<=$sequence ORDER BY acquired_day DESC,receipt_id LIMIT 30;",
                TestDict("owner", owner, "peer", peer, "timeline", ReadString(context, "timelineId", "main"), "day", ReadDouble(context, "currentDay", 0),
                    "ts", access.AsOfTimestamp, "sequence", access.AsOfAcceptedSequence))
                .Select(r => { var p = GossipJson(r); return TestDict("sourceId", ReadString(r, "receipt_id", ""),
                    "sourceRootId", "communication:" + ReadString(r, "message_id", ""), "speakerId", ReadString(p, "speakerId", ""),
                    "text", ReadString(p, "body", "")); }).Where(e => ReadString(e, "text", "").Length > 0);
            var selected = new List<Dictionary<string, object>>(); var memory = new System.Text.StringBuilder();
            var conversation = ReadDictionaryList(context, "replyEvidence").Where(e =>
            {
                var source = QuerySql(c, "SELECT * FROM conversation_turns WHERE turn_id=$id;", TestDict("id", ReadString(e, "sourceId", ""))).FirstOrDefault();
                return source != null && PrecisionTurnVisible(source, owner, access, c);
            });
            foreach (var evidence in acquired.Concat(conversation))
            {
                string line = Json.Serialize(PersonalGossipReplyEvidenceBrief(context, evidence));
                if (memory.Length + line.Length + Environment.NewLine.Length > 9000) continue;
                memory.AppendLine(line); selected.Add(evidence);
            }
            context["replyEvidence"] = selected; context["recipientMemory"] = memory.ToString();
        }

        private static Dictionary<string, object> ComposeCommunicationReply(string campaign, Dictionary<string, object> row,
            Dictionary<string, object> data, Dictionary<string, object> context,
            Func<Dictionary<string, object>, Dictionary<string, object>> respond)
        {
            if (ReadString(row, "recipient_id", "") == ReadString(row, "player_id", "") && ReadString(row, "player_id", "").Length > 0)
                return TestDict("ok", false, "error", "A background communication cannot speak or decide for the player.");
            string instruction = "You are the named NPC receiving this private communication now, after its travel. Write one short reply in your own voice. "
                + "Use only the message, your personality and your own accessible memory. Treat quotations as attributed speech, not independently established facts. "
                + "For a summons choose accepted, refused or delayed based on your own duties and willingness; acceptance commits only to the supplied meeting terms. "
                + "Otherwise answer its actual question if you know, offer an explicitly framed personal opinion, refuse, or acknowledge. "
                + "This body IS your actual return letter. For kind ask, answer the request now; do not promise to write another reply later or treat a quoted 'ask him/her' as an instruction to contact somebody else. The named recipient is you. "
                + "Return JSON {body,answerKind:'answered|opinion|refused|delayed|acknowledged|accepted',sourceQuote,privacy:'public|personal|intimate|secret'}. "
                + "A factual answer must copy a complete supporting passage from your own accessible memory into sourceQuote. Do not expose another person's confidence. "
                + "An opinion is your present view, not an invented past event. Do not issue resource transfers, claim someone arrived, or start more messages.";
            var brief = TestDict("name", ReadString(ReadDictionary(data, "recipient"), "name", ReadString(row, "recipient_id", "")),
                "personality", ReadDictionary(context, "traits"), "message", ReadString(data, "body", ""), "kind", ReadString(row, "kind", ""),
                "destinationId", ReadString(data, "destinationId", ""), "stayDays", ReadDouble(data, "stayDays", 0),
                "ownAccessibleMemory", ReadString(context, "recipientMemory", ""));
            var response = respond(TestDict("campaignId", campaign, "heroStringId", ReadString(row, "recipient_id", ""),
                "requestType", "correspondence", "maxTokens", 1200, "temperature", .5,
                "messages", new[] { TestDict("role", "system", "content", instruction), TestDict("role", "user", "content", Json.Serialize(brief)) },
                "response_format", TestDict("type", "json_object")));
            if (!ReadBool(response, "ok", false)) return TestDict("ok", false, "error", ReadString(response, "error", "Reply provider failed."));
            var result = TryParseJsonObject(ReadString(response, "content", "")) ?? TestDict();
            string body = ReadString(result, "body", ""), quote = ReadString(result, "sourceQuote", ""), kind = ReadString(result, "answerKind", "");
            bool supported = kind != "answered" || GossipQuoteSupported(quote, ReadString(context, "recipientMemory", ""));
            bool allowed = new[] { "answered", "opinion", "refused", "delayed", "acknowledged", "accepted" }.Contains(kind)
                && (kind != "accepted" || ReadString(row, "kind", "").StartsWith("summon", StringComparison.Ordinal));
            string privacy = ReadString(result, "privacy", "secret").ToLowerInvariant();
            if (body.Length < 1 || body.Length > 2000 || !supported || !allowed || !new[] { "public", "personal", "intimate" }.Contains(privacy))
                return TestDict("ok", false, "error", "Reply content, evidence, decision or confidentiality was invalid.");
            result["ok"] = true; return result;
        }

        private static bool CommunicationReplyDisclosureAllowed(ReignDbConnection c, Dictionary<string, object> message,
            Dictionary<string, object> context, Dictionary<string, object> result)
        {
            string quote = ReadString(result, "sourceQuote", ""), receiver = ReadString(message, "speaker_id", "");
            if (quote.Length == 0) return true;
            foreach (var evidence in ReadDictionaryList(context, "replyEvidence").Where(e => ReadString(e, "text", "").Contains(quote, StringComparison.Ordinal)))
            {
                string session = ReadString(evidence, "sourceRootId", "").Replace("conversation:", "");
                var sources = QuerySql(c, "SELECT text,participants_json,speaker_id FROM conversation_turns WHERE session_id=$id AND status='active';", TestDict("id", session));
                if (sources.Any(s => TextListFromJson(ReadString(s, "participants_json", "[]")).Contains(receiver, StringComparer.OrdinalIgnoreCase))) continue;
                if (sources.Any(s => System.Text.RegularExpressions.Regex.IsMatch(ReadString(s, "text", ""),
                    @"\b(?:keep\s+(?:this|that|it)\s+between\s+us|(?:do\s+not|don't)\s+tell|keep\s+.{0,40}\bsecret|promise\s+.{0,30}\bnot\s+to\s+tell)\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))) return false;
            }
            return true;
        }

        private static void AcceptCommunicationNativeReceipt(ReignDbConnection c, NpcLivesState state, Dictionary<string, object> p, double day)
        {
            EnsureCommunicationSchema(c);
            string id = ReadString(p, "communicationId", ""), kind = ReadString(p, "kind", "");
            var row = QuerySql(c, "SELECT * FROM communication_messages WHERE message_id=$id AND timeline_id=$timeline;", TestDict("id", id, "timeline", state.TimelineId)).FirstOrDefault();
            var data = GossipJson(row); var meeting = ReadDictionary(p, "meeting");
            if (kind.StartsWith("host_", StringComparison.Ordinal))
            {
                if (row == null || !new[] { "host_reserved", "host_blocked", "host_expired", "host_lost" }.Contains(kind)
                    || ReadString(p, "reservationHash", "").Length == 0
                    || ReadString(p, "reservationHash", "") != ReadString(data, "hostReservationHash", "")
                    || ReadString(p, "hostId", "") != ReadString(row, "speaker_id", "")
                    || ReadString(p, "destinationId", "") != ReadString(data, "destinationId", ""))
                    throw new InvalidOperationException("The host reservation does not match the exact invitation.");
                if (kind == "host_reserved")
                {
                    if (ReadString(row, "status", "") == "awaiting_host" && day < ReadDouble(data, "hostWaitUntilDay", 0))
                    { data["hostReservedDay"] = day; SetCommunicationStatus(c, row, "queued", data); }
                }
                else if (CommunicationHostWaitingStatuses.Contains(ReadString(row, "status", "")))
                {
                    data["failure"] = ReadString(p, "reason", "The host could not keep the agreed invitation wait.");
                    SetCommunicationStatus(c, row, kind == "host_expired" ? "expired" : "blocked", data);
                }
                return;
            }
            if (row != null && kind == "summons_blocked" && ReadString(row, "status", "") == "summons_ready"
                && ReadString(p, "acceptanceHash", "").Length > 0 && ReadString(p, "acceptanceHash", "") == ReadString(data, "acceptanceHash", ""))
            {
                data["failure"] = ReadString(p, "reason", "The summons could not begin."); SetCommunicationStatus(c, row, "blocked", data); return;
            }
            if (row == null || meeting == null || ReadString(p, "acceptanceHash", "") != ReadString(data, "acceptanceHash", "")
                || ReadString(data, "acceptanceHash", "").Length == 0 || ReadString(meeting, "IssuerId", "") != ReadString(row, "speaker_id", "")
                || ReadString(meeting, "TargetId", "") != ReadString(row, "recipient_id", "")
                || ReadString(meeting, "DestinationId", "") != ReadString(data, "destinationId", "")
                || ReadDouble(meeting, "StayDays", -1) != ReadDouble(data, "stayDays", 0))
                throw new InvalidOperationException("The native summons does not match the exact accepted communication.");
            if (kind == "meeting_agreed" && ReadString(row, "status", "") != "summons_ready"
                && state.Meetings.All(m => m.Id != ReadString(p, "id", ""))) throw new InvalidOperationException("That summons is not ready.");
            bool replay = state.OperationReceipts.ContainsKey(ReadString(p, "operationId", ""));
            NpcLivesNativeReceipt(c, state, p, ReadString(row, "player_id", ""), day);
            if (replay) return;
            string status = kind == "meeting_arrived" ? "meeting" : kind == "meeting_completed" ? "completed"
                : kind == "meeting_interrupted" || kind == "meeting_cancelled" ? "interrupted" : "summons_travelling";
            data["meetingId"] = ReadString(p, "id", "");
            if (kind == "meeting_arrived" && ReadString(row, "kind", "") == "summon_then_inform")
            {
                var briefing = new Dictionary<string, object>(row) { ["message_id"] = id + "_briefing" };
                CommunicationHistory(c, briefing, ReadString(row, "recipient_id", ""), ReadString(row, "speaker_id", ""), day, CommunicationBody(row, data));
            }
            SetCommunicationStatus(c, row, status, data);
        }
    }
}
