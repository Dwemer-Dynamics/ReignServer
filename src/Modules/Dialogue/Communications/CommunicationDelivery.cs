using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Reign.Core.Contracts.Dialogue;
using ReignBeta.Shared;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class DialogueService
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
                _owns = !Program.CampaignDataGate.IsReadLockHeld && !Program.CampaignDataGate.IsWriteLockHeld;
                if (_owns) Program.CampaignDataGate.EnterReadLock();
            }
            public void Dispose() { if (_owns) Program.CampaignDataGate.ExitReadLock(); }
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
            DialogueStore.InsertOrIgnoreCommunicationHistory(c, TestDict("receipt", id + "_" + PromptHash(owner).Substring(0, 12).ToLowerInvariant(), "id", id,
                    "timeline", ReadString(row, "timeline_id", "main"), "owner", owner, "peer", peer, "day", day, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    "json", Json.Serialize(TestDict("body", body, "speakerId", ReadString(row, "speaker_id", ""),
                        "recipientId", ReadString(row, "recipient_id", ""), "kind", ReadString(row, "kind", ""), "evidenceType", "attributed_message"))));
            string receipt = id + "_" + PromptHash(owner).Substring(0, 12).ToLowerInvariant(), session = receipt + "_session";
            var sourceSession = DialogueStore.SelectConversationSessions3(c, TestDict("id", ReadString(row, "session_id", ""))).FirstOrDefault();
            var visible = TestDict("body", body, "timelineId", ReadString(row, "timeline_id", "main"), "messageId", id,
                "ownerId", owner, "peerId", peer, "evidenceType", "attributed_message", "visibility", "npc_private");
            DialogueStore.InsertOrIgnoreConversationSessions(c, TestDict("id", session, "campaign", ReadString(sourceSession, "campaign_id", ""), "owner", owner,
                    "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "day", day, "participants", Json.Serialize(new[] { owner }), "json", Json.Serialize(visible)));
            if (DialogueStore.SelectConversationTurns8(c, TestDict("id", receipt)).Count == 0)
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
            foreach (var row in DialogueStore.SelectCommunicationMessages7(c, TestDict("timeline", timeline)))
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
                        SettingsService.CorrespondenceOption(SettingsService.LoadSettings(), "correspondenceTravelTimeMultiplier"));
                    data["baseTransitDays"] = baseTransit; data["transitDays"] = transit;
                    data["dispatchPresence"] = TestDict("speaker", sender, "recipient", recipient);
                    data["body"] = CommunicationBody(row, data, ReadString(row, "kind", "") == "summon_then_inform");
                    DialogueStore.UpdateCommunicationMessages4(c, TestDict("id", ReadString(row, "message_id", ""), "day", day, "due", day + transit, "json", Json.Serialize(data)));
                    row["status"] = "in_transit"; row["due_day"] = day + transit;
                    CommunicationHistory(c, row, ReadString(row, "speaker_id", ""), ReadString(row, "recipient_id", ""), day, ReadString(data, "body", ""));
                }
                if (day < ReadDouble(row, "due_day", double.PositiveInfinity)) continue;
                if (!GossipCommunicationAvailable(recipient)) { SetCommunicationStatus(c, row, "awaiting_recipient", data); continue; }
                string body = ReadString(data, "body", "");
                CommunicationHistory(c, row, ReadString(row, "recipient_id", ""), ReadString(row, "speaker_id", ""), day, body);
                bool original = ReadString(row, "parent_id", "").Length == 0;
                string status = original && ReadString(row, "recipient_id", "") != ReadString(row, "player_id", "") ? "reply_pending" : "delivered";
                DialogueStore.UpdateCommunicationMessages5(c, TestDict("id", ReadString(row, "message_id", ""), "status", status, "day", day));
                if (!original)
                    DialogueStore.UpdateCommunicationMessages6(c, TestDict("id", ReadString(row, "parent_id", "")));
            }
        }

        internal static Dictionary<string, object> CommunicationApi(string operation, Dictionary<string, object> p)
        {
            string campaign = ReadString(p, "campaignId", ""), timeline = ContinuityTimeline(p);
            if (campaign.Length == 0) return TestDict("ok", false, "error", "An exact campaign is required.");
            using var ownership = PersistenceService.EnterCampaignWorkLock(RelationshipsService.CampaignRelationshipWriteLock(campaign));
            using var c = ReignDatabase.OpenCampaignConnection(campaign);
            if (operation == "status")
            {
                if (WorldSimulationService.ActiveWorldHistoryTimeline(c) != timeline) return CommunicationStatusProtocol.Failure(CommunicationStatusProtocol.TimelineNotActiveError);
                CommunicationStatusRequest request = CommunicationStatusRequest.FromDictionary(p);
                return ReadCommunicationSnapshot(c, timeline, request.HeroId, request.Offset, request.Limit);
            }
            EnsureCommunicationSchema(c); EnsureMemoryPrecisionSchema(c);
            string generation = CharactersService.NpcLivesGeneration(c);
            if (WorldSimulationService.ActiveWorldHistoryTimeline(c) != timeline) return TestDict("ok", false, "error", "The communication timeline is not active.");
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
            var ready = DialogueStore.SelectCommunicationMessages8(c, TestDict("timeline", timeline))
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
            DialogueStore.SelectCommunicationMessages9(c, TestDict("timeline", timeline)).Where(r => CommunicationHostWaitingStatuses.Contains(ReadString(r, "status", "")))
            .Where(r => ReadString(GossipJson(r), "hostReservationHash", "").Length > 0)
            .Select(r => { var d = GossipJson(r); return TestDict("id", ReadString(r, "message_id", ""), "speakerId", ReadString(r, "speaker_id", ""),
                "recipientId", ReadString(r, "recipient_id", ""), "destinationId", ReadString(d, "destinationId", ""),
                "reservationHash", ReadString(d, "hostReservationHash", ""), "expiresDay", ReadDouble(d, "hostWaitUntilDay", 0)); }).ToList();

        private static void ExpireCommunicationHostWaits(ReignDbConnection c, string timeline, double day)
        {
            foreach (var work in CommunicationHostWork(c, timeline).Where(w => day >= ReadDouble(w, "expiresDay", 0)))
            {
                var row = DialogueStore.SelectCommunicationMessages3(c, TestDict("id", ReadString(work, "id", ""))).Single();
                var data = GossipJson(row); data["failure"] = "The host's fourteen-day invitation wait expired before an accepted journey began.";
                SetCommunicationStatus(c, row, "expired", data);
            }
        }

        private static Dictionary<string, object> ReadCommunicationSnapshot(ReignDbConnection c, string timeline, string hero, int offset, int limit)
        {
            if (!WorldSimulationService.TableExists(c, "communication_messages")) return CommunicationStatusResponse.NoSchema(timeline).ToDictionary();
            var args = TestDict("timeline", timeline, "hero", hero, "offset", Math.Clamp(offset, 0, 1000000), "limit", Math.Clamp(limit, 1, 100));
            string messages = " FROM communication_messages WHERE timeline_id=$timeline AND ($hero='' OR speaker_id=$hero OR recipient_id=$hero)";
            string history = " FROM communication_history WHERE timeline_id=$timeline AND ($hero='' OR owner_id=$hero)";
            var admissions = WorldSimulationService.TableExists(c, "conversation_accepted_units") ? DialogueStore.SelectConversationAcceptedUnits(c, args) : new List<Dictionary<string, object>>();
            return CommunicationStatusResponse.Snapshot(CharactersService.NpcLivesGeneration(c), timeline,
                admissions,
                DialogueStore.SelectStatement2(c, messages,args),
                DialogueStore.SelectStatement3(c, history,args),
                ReadLong(DialogueStore.CountStatement(c, messages,args).Single(), "n", 0),
                ReadLong(DialogueStore.CountStatement(c, history,args).Single(), "n", 0)).ToDictionary();
        }

        private static Dictionary<string, object> CommunicationReadiness(ReignDbConnection c, string timeline, double day)
        {
            if (!WorldSimulationService.TableExists(c, "communication_messages")) return TestDict("active", false, "pending", 0L, "due", 0L, "failed", 0L);
            var counts = DialogueStore.CountCommunicationMessages(c, TestDict("timeline", timeline, "day", day)).Single();
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
                using (PersistenceService.EnterCampaignWorkLock(RelationshipsService.CampaignRelationshipWriteLock(campaign)))
                using (var c = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    EnsureCommunicationSchema(c); EnsureMemoryPrecisionSchema(c);
                    generation = CharactersService.NpcLivesGeneration(c);
                    string leasePrefix = CommunicationWorkerBoot + ":" + generation + ":";
                    lease = leasePrefix + Guid.NewGuid().ToString("N");
                    string timeline = WorldSimulationService.ActiveWorldHistoryTimeline(c);
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    DialogueStore.UpdateCommunicationMessages7(c, TestDict("timeline", timeline, "now", now, "prefix", leasePrefix + "%"));
                    row = DialogueStore.SelectCommunicationMessages10(c, TestDict("timeline", timeline, "now", now, "prefix", leasePrefix + "%")).FirstOrDefault();
                    if (row == null) return;
                    data = GossipJson(row);
                    if (CommunicationPlayerControlled(campaign, generation, ReadString(row, "recipient_id", ""), ReadString(row, "player_id", "")))
                    { SetCommunicationStatus(c, row, "awaiting_player", data); continue; }
                    replyContext = TestDict("campaignId", campaign, "timelineId", timeline,
                        "speakerId", ReadString(row, "recipient_id", ""), "playerId", ReadString(row, "player_id", ""),
                        "currentDay", ReadDouble(row, "acquired_day", 0), "memoryAsOfTimestamp", now,
                        "memoryAsOfAcceptedSequence", ReadAcceptedMemorySourceWatermark(c));
                    PrepareCommunicationReply(c, campaign, row, replyContext);
                    DialogueStore.UpdateCommunicationMessages8(c, TestDict("id", ReadString(row, "message_id", ""), "lease", lease, "until", now + 600));
                }
                Dictionary<string, object> result;
                try { result = ComposeCommunicationReply(campaign, row, data, replyContext, respond ?? LlmProviderService.ChatWithLlm); }
                catch (Exception ex) { result = TestDict("ok", false, "error", "Reply generation failed: " + ex.GetType().Name); }
                using (new CommunicationDataReadLease())
                using (PersistenceService.EnterCampaignWorkLock(RelationshipsService.CampaignRelationshipWriteLock(campaign)))
                using (var c = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    if (generation != CharactersService.NpcLivesGeneration(c)) return;
                    var current = DialogueStore.SelectCommunicationMessages11(c, TestDict("id", ReadString(row, "message_id", ""), "lease", lease)).FirstOrDefault();
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
                    DialogueStore.InsertOrIgnoreCommunicationMessages(c, TestDict("id", replyId, "root", ReadString(row, "message_id", ""), "timeline", ReadString(row, "timeline_id", ""),
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
            var scope = VerificationService.CloneDictionary(context); scope["worldDay"] = ReadDouble(context, "currentDay", 0);
            var access = DialogueService.BuildKnowledgeAccessContext(owner, ReadString(context, "playerId", ""), "", scope);
            // Only correspondence already exchanged with this addressee belongs
            // in this reply. Another NPC's private letters grant no disclosure.
            var acquired = DialogueStore.SelectCommunicationHistory3(c, TestDict("owner", owner, "peer", peer, "timeline", ReadString(context, "timelineId", "main"), "day", ReadDouble(context, "currentDay", 0),
                    "ts", access.AsOfTimestamp, "sequence", access.AsOfAcceptedSequence))
                .Select(r => { var p = GossipJson(r); return TestDict("sourceId", ReadString(r, "receipt_id", ""),
                    "sourceRootId", "communication:" + ReadString(r, "message_id", ""), "speakerId", ReadString(p, "speakerId", ""),
                    "text", ReadString(p, "body", "")); }).Where(e => ReadString(e, "text", "").Length > 0);
            var selected = new List<Dictionary<string, object>>(); var memory = new System.Text.StringBuilder();
            var conversation = ReadDictionaryList(context, "replyEvidence").Where(e =>
            {
                var source = DialogueStore.SelectConversationTurns6(c, TestDict("id", ReadString(e, "sourceId", ""))).FirstOrDefault();
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
                var sources = DialogueStore.SelectConversationTurns9(c, TestDict("id", session));
                if (sources.Any(s => DialogueService.TextListFromJson(ReadString(s, "participants_json", "[]")).Contains(receiver, StringComparer.OrdinalIgnoreCase))) continue;
                if (sources.Any(s => System.Text.RegularExpressions.Regex.IsMatch(ReadString(s, "text", ""),
                    @"\b(?:keep\s+(?:this|that|it)\s+between\s+us|(?:do\s+not|don't)\s+tell|keep\s+.{0,40}\bsecret|promise\s+.{0,30}\bnot\s+to\s+tell)\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))) return false;
            }
            return true;
        }

        internal static void AcceptCommunicationNativeReceipt(ReignDbConnection c, NpcLivesState state, Dictionary<string, object> p, double day)
        {
            EnsureCommunicationSchema(c);
            string id = ReadString(p, "communicationId", ""), kind = ReadString(p, "kind", "");
            var row = DialogueStore.SelectCommunicationMessages12(c, TestDict("id", id, "timeline", state.TimelineId)).FirstOrDefault();
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
            CharactersService.NpcLivesNativeReceipt(c, state, p, ReadString(row, "player_id", ""), day);
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
