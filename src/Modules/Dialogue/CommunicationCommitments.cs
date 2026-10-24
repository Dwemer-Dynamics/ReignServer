using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string CommunicationContract = @"
MESSAGES AND SUMMONS THAT REALLY HAPPEN
This background dispatch is between NPCs. Never name the player as recipient or author a reply/decision for the player; use ordinary player conversation and existing player correspondence controls.
If YOU explicitly commit to telling, writing to, asking, or sending for a named person, return communicationPlans. Each item is {key, operation:'send|cancel', kind:'inform|ask|summon|summon_then_inform', recipientId, evidenceQuote, contentEvidence:[{turnId,quote}], destinationId, stayDays, delayDays}. Use [] when no such promise is made. key is a stable short topic key; revise the same key for changed terms instead of creating duplicates. evidenceQuote is YOUR exact unconditional first-person commitment in this reply. For cancellation quote your actual withdrawal and reuse the pending key/recipient. Do not schedule hypotheticals, third-party promises, conditional agreements, or a player's unaccepted request.
For evidenceQuote prefer the shortest exact first-person clause that states your commitment, excluding introductory commentary. Timing such as 'when we have finished here' means conversation closure, not a condition requiring payment or consent.
contentEvidence selects the exact passages the recipient is supposed to receive, including all agreed amounts, conditions, questions and dates. Copy complete relevant passages from accessible accepted turns, using their original turn IDs; current_player/current_npc name this turn. Never forward the whole transcript or a confidence excluded from the intended message. A question stays a question; relaying a proposal never grants the recipient's agreement. The system quotes and attributes these passages rather than inventing details.
Messages dispatch AFTER this conversation actually ends. delayDays is zero unless you explicitly promise a later dispatch (tomorrow=1). Replies take a return journey. A summons requires a real fixed destinationId and stayDays (0.25–31.5); use the already agreed meeting terms. A summon_then_inform conveys only the invitation/purpose until actual arrival, then shares its contentEvidence. Sending for someone does not itself prove acceptance, arrival or completion. Use existing explicit action controls for resource transfers or other gameplay decisions; a communication cannot authorize them.
The pending commitments below are your own private dispatch ledger. Mention a past sent message as sent only when its status proves dispatch; received and arrived have separate receipts.";

        private static Dictionary<string, object> CommunicationPlanExample() => TestDict(
            "key", "", "operation", "send|cancel", "kind", "inform|ask|summon|summon_then_inform", "recipientId", "",
            "evidenceQuote", "", "contentEvidence", new[] { TestDict("turnId", "current_player|current_npc|exact accepted turn ID", "quote", "") },
            "destinationId", "", "stayDays", 0d, "delayDays", 0d);

        private static List<string> CommunicationPlanReviewClauses(string reply)
        {
            // This only routes an omitted-metadata review. It never extracts a
            // recipient, invents a plan, or authorizes a dispatch from prose.
            string spoken = Regex.Replace(reply ?? "", @"\*[^*]*\*", "", RegexOptions.Singleline);
            return Regex.Split(spoken, @"(?<=[.!?])\s+|[\r\n]+")
                .Select(s => Regex.Replace(s.Trim(), @"^(?:yes|certainly|of\s+course|very\s+well)\s*[—–:]\s*", "", RegexOptions.IgnoreCase))
                .Where(clause => CommunicationPromise(clause, spoken)).ToList();
        }

        private static bool CommunicationReplyNeedsPlanReview(string reply) => CommunicationPlanReviewClauses(reply).Count > 0;

        // Only the bounded reviewer may attest to a noncanonical formulation.
        // Its boolean is never sufficient at publication: the server seals the
        // exact reviewed plan and accepted event with an in-process secret.
        private static readonly byte[] CommunicationSemanticKey = RandomNumberGenerator.GetBytes(32);

        private static bool CommunicationSemanticCandidate(string quote, string reply)
        {
            if (string.IsNullOrWhiteSpace(quote) || quote.Length < 8 || string.IsNullOrWhiteSpace(reply)) return false;
            int at = reply.IndexOf(quote, StringComparison.Ordinal);
            if (at < 0) return false;
            foreach (Match quoted in Regex.Matches(reply, @"\*[^*]*\*|«[^»]*»|“[^”]*”|""[^""]*""", RegexOptions.Singleline))
                if (at >= quoted.Index && at < quoted.Index + quoted.Length) return false;
            string prefix = reply.Substring(Math.Max(0, at - 180), Math.Min(at, 180));
            if (Regex.IsMatch(prefix, @"\b(?:she|he|they|\p{Lu}[\p{L}'’-]*)\s+(?:said|says|promised|wrote|told\s+me)\s*[:,—–]?\s*$")) return false;
            if (Regex.IsMatch(quote.Trim(), @"^[“""*]|^(?:if|unless|perhaps|provided|suppose|imagine|she\s+said|he\s+said|they\s+said)\b|\b(?:would|might|could|cannot|can['’]t|won['’]t)\b|\b(?:will|shall|am|is|are|do|does)\s+not\b|\bonly\s+if\b", RegexOptions.IgnoreCase)) return false;
            return true;
        }

        private static string CommunicationSemanticSeal(Dictionary<string, object> plan, string campaign, string hero,
            string eventId, string generation, string reply)
        {
            string body = Json.Serialize(new object[] { campaign, hero, eventId, generation, (reply ?? "").Trim(),
                ReadString(plan, "key", ""), ReadString(plan, "operation", "send"), ReadString(plan, "kind", "inform"),
                ReadString(plan, "recipientId", ""), ReadString(plan, "evidenceQuote", ""), ReadString(plan, "destinationId", ""),
                ReadDouble(plan, "stayDays", 0), ReadDouble(plan, "delayDays", 0),
                ReadDictionaryList(plan, "contentEvidence").Select(e => new[] { ReadString(e, "turnId", ""), ReadString(e, "quote", "") }).ToArray() });
            return Convert.ToHexString(HMACSHA256.HashData(CommunicationSemanticKey, Encoding.UTF8.GetBytes(body)));
        }

        private static bool CommunicationSemanticAccepted(Dictionary<string, object> plan, AcceptedConversationUnit unit)
        {
            string receipt = ReadString(plan, "semanticCommitmentReceipt", "");
            return receipt.Length == 64 && !string.IsNullOrWhiteSpace(unit.Generation)
                && CommunicationSemanticCandidate(ReadString(plan, "evidenceQuote", ""), unit.Reply)
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(receipt), Encoding.UTF8.GetBytes(
                    CommunicationSemanticSeal(plan, unit.Campaign, unit.Hero, unit.EventId, unit.Generation, unit.Reply)));
        }

        private static Dictionary<string, object> ApplyCommunicationPlanPatch(Dictionary<string, object> original, Dictionary<string, object> patch,
            bool allowSemanticReview = false)
        {
            if (original == null || patch == null || !patch.TryGetValue("communicationPlans", out object value)
                || !IsConversationObjectArray(value)) return null;
            string reply = ReadFirstString(original, "reply", "body");
            foreach (string field in new[] { "reply", "body" })
                if (patch.ContainsKey(field) && ReadString(patch, field, "") != reply) return null;
            var plans = ReadDictionaryList(patch, "communicationPlans");
            if (plans.Count < 1 || plans.Count > 4 || plans.Any(p =>
                ReadString(p, "evidenceQuote", "").Length == 0 || !reply.Contains(ReadString(p, "evidenceQuote", ""), StringComparison.Ordinal))) return null;
            if (plans.Any(p => ReadString(p, "operation", "send") == "send"
                && !CommunicationPromise(ReadString(p, "evidenceQuote", ""), reply)
                && !(allowSemanticReview && ReadBool(p, "unconditionalSpeakerCommitment", false)
                    && CommunicationSemanticCandidate(ReadString(p, "evidenceQuote", ""), reply)))) return null;
            foreach (var plan in plans) plan.Remove("semanticCommitmentReceipt");
            var before = ReadDictionaryList(original, "communicationPlans");
            if (before.Count > 0 && (before.Count != plans.Count || plans.Any(p => !before.Any(b =>
                ReadString(b, "key", "") == ReadString(p, "key", "") && ReadString(b, "recipientId", "") == ReadString(p, "recipientId", "")
                && ReadString(b, "operation", "send") == ReadString(p, "operation", "send") && ReadString(b, "kind", "inform") == ReadString(p, "kind", "inform")
                && ReadString(b, "destinationId", "") == ReadString(p, "destinationId", "") && ReadDouble(b, "stayDays", 0) == ReadDouble(p, "stayDays", 0)
                && ReadDouble(b, "delayDays", 0) == ReadDouble(p, "delayDays", 0))))) return null;
            var merged = CloneDictionary(original);
            merged["communicationPlans"] = plans;
            return merged;
        }

        private static Dictionary<string, object> RepairMissingCommunicationPlans(Dictionary<string, object> llm,
            Dictionary<string, object> payload, string campaign, string correlation, string mode, string hero,
            Func<Dictionary<string, object>, Dictionary<string, object>> respond = null)
        {
            if (!ReadBool(llm, "ok", false) || ReadBool(payload, "officialMemoryFirewall", false)) return llm;
            var parsed = TryParseJsonObject(ReadString(llm, "content", ""));
            if (parsed == null) return llm;
            var originalPlans = ReadDictionaryList(parsed, "communicationPlans");
            string currentReply = ReadFirstString(parsed, "reply", "body");
            bool unusableQuote = originalPlans.Any(p => ReadString(p, "operation", "send") == "send"
                && !CommunicationPromise(ReadString(p, "evidenceQuote", ""), currentReply));
            var drafts = new List<Dictionary<string, object>>();
            string generation = "";
            if (originalPlans.Count > 0)
            using (new CommunicationDataReadLease())
            using (EnterCampaignWorkLock(CampaignRelationshipWriteLock(campaign)))
            using (var c = OpenCampaignConnection(campaign))
            {
                EnsureCommunicationSchema(c); generation = NpcLivesGeneration(c);
                foreach (var plan in originalPlans.Where(p => ReadString(p, "operation", "send") == "send"))
                    drafts.AddRange(QuerySql(c, @"SELECT * FROM communication_messages WHERE timeline_id=$timeline AND speaker_id=$speaker
AND recipient_id=$recipient AND topic_key=$key AND status IN ('draft','needs_clarification') ORDER BY created_day,message_id LIMIT 1;",
                        TestDict("timeline", ContinuityTimeline(payload), "speaker", hero, "recipient", ReadString(plan, "recipientId", ""), "key", ReadString(plan, "key", ""))));
            }
            if (drafts.Count == 0 && (originalPlans.Count > 0 ? !unusableQuote : !CommunicationReplyNeedsPlanReview(currentReply))) return llm;
            var people = ReadDictionaryList(ReadDictionary(payload, "gossipPresence"), "people");
            var self = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), hero));
            string playerId = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            var player = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), playerId));
            bool ThirdPerson(string id) => !GossipSameId(id, hero) && !GossipSameId(id, playerId);
            var referenced = GossipMentionedIds(ReadFirstString(payload, "playerText", "text", "message"), player, self, people).Where(ThirdPerson).ToList();
            if (drafts.Count == 0 && originalPlans.Count == 0 && !CommunicationPlanReviewClauses(ReadFirstString(parsed, "reply", "body")).Any(clause =>
                GossipMentionedIds(clause, self, player, people).Any(ThirdPerson)
                || referenced.Count == 1 && (Regex.IsMatch(clause, @"\b(?:her|him|them)\b", RegexOptions.IgnoreCase)
                    || Regex.IsMatch(clause, @"^(?:it|this|that)\s+", RegexOptions.IgnoreCase)))) return llm;
            string context = CommunicationContract + "\nReferenced recipients: " + Json.Serialize(people.Where(p => referenced.Contains(GossipHeroId(p)))
                .Take(16).Select(p => TestDict("name", ReadString(p, "name", ""), "heroId", GossipHeroId(p))))
                + "\nCurrent fixed meeting location ID: " + CommunicationFixedLocation(self);
            context += "\nExecutable commitment clauses from this exact reply: " + Json.Serialize(CommunicationPlanReviewClauses(currentReply).Take(8))
                + "\nFor an omitted plan or unusable evidenceQuote, select an exact executable clause for the SAME recipient and decision. Future phrasing such as tomorrow I send and references to an already named invitation are valid. Never substitute another person's or another recipient's promise. Preserve all existing plan identity, place, duration, delay, operation and final content conditions. Return {} if no matching clause exists; do not change the character's speech.";
            context += "\nFor every send plan also return unconditionalSpeakerCommitment: true only if the current speaker actually undertakes this future communication in their own voice. Judge meaning rather than a preferred grammar: 'Tomorrow a sealed summons rides to Pharon' can undertake sending it. A past/completed act, quoted/reported speech, a hypothetical, an unresolved condition, a refusal, or somebody else's undertaking is false. The exact evidenceQuote must support the same recipient and terms. Never supply semanticCommitmentReceipt; the server owns that field.";
            var pendingTerms = drafts.Select(r => TestDict("key", ReadString(r, "topic_key", ""), "recipientId", ReadString(r, "recipient_id", ""),
                "contentEvidence", ReadDictionaryList(GossipJson(r), "contentEvidence"))).ToList();
            var request = TestDict("campaignId", campaign, "correlationId", correlation + "_communication_metadata", "heroStringId", hero,
                "requestType", "dialogue_communication_metadata_repair", "maxTokens", 2400, "temperature", 0d, "reasoningDisabled", true,
                "response_format", TestDict("type", "json_object"), "promptCacheEligible", false,
                "messages", new[] {
                    TestDict("role", "system", "content", context + "\nMETADATA REPAIR: Complete the omitted plan or reconcile the existing draft revision. Return only {communicationPlans:[...]}, at most four plans. Do not change the reply, any decision, recipient, key, operation, kind, native destination, stay, or other metadata. Select exact complete final terms from current_npc when it restates the revision. Preserve every still-agreed original condition (including dates, free entry, and spending restrictions); never forward only a change instruction or obsolete values. For an existing plan preserve its identity. Current_player/current_npc are the only new source IDs. The normal source, authority, agency and closure gates still apply."),
                    TestDict("role", "user", "content", Json.Serialize(TestDict("current_player", ReadFirstString(payload, "playerText", "text", "message"),
                        "current_npc", ReadFirstString(parsed, "reply", "body"), "originalPlans", originalPlans, "pendingDraftTerms", pendingTerms))) });
            Dictionary<string, object> answer;
            int providerCalls = 0;
            try
            {
                if (Json.Serialize(pendingTerms).Length > 24000 || ReadDictionaryList(request, "messages").Sum(m => ReadString(m, "content", "").Length) > 48000)
                    answer = TestDict("ok", false, "error", "Complete revision evidence exceeds the bounded review.");
                else { providerCalls++; answer = (respond ?? ChatWithLlm)(request); }
            }
            catch (Exception ex) { answer = TestDict("ok", false, "error", ex.GetType().Name); }
            var repaired = ReadBool(answer, "ok", false) ? ApplyCommunicationPlanPatch(parsed, TryParseJsonObject(ReadString(answer, "content", "")), allowSemanticReview: true) : null;
            if (repaired != null && drafts.Any(d => !ReadDictionaryList(repaired, "communicationPlans").Any(p =>
                ReadString(p, "key", "") == ReadString(d, "topic_key", "") && ReadString(p, "recipientId", "") == ReadString(d, "recipient_id", "")
                && ReadDictionaryList(p, "contentEvidence").Any(e => ReadString(e, "turnId", "") == "current_npc"
                    && ReadString(e, "quote", "").Length >= 8)))) repaired = null;
            int semanticPlans = 0;
            if (repaired != null)
            {
                if (generation.Length == 0)
                using (new CommunicationDataReadLease())
                using (EnterCampaignWorkLock(CampaignRelationshipWriteLock(campaign)))
                using (var c = OpenCampaignConnection(campaign)) generation = NpcLivesGeneration(c);
                foreach (var plan in ReadDictionaryList(repaired, "communicationPlans"))
                    if (ReadString(plan, "operation", "send") == "send" && !CommunicationPromise(ReadString(plan, "evidenceQuote", ""), currentReply))
                    {
                        plan["semanticCommitmentReceipt"] = CommunicationSemanticSeal(plan, campaign, hero,
                            AcceptedConversationEventId(campaign, hero, payload), generation, currentReply);
                        semanticPlans++;
                    }
            }
            var result = CloneDictionary(llm);
            var evidence = TestDict("attempted", true, "providerCalls", providerCalls, "completed", repaired != null, "replyUnchanged", true,
                "draftRevision", drafts.Count > 0, "semanticPlans", semanticPlans);
            result["communicationMetadataRepair"] = evidence;
            WriteAudit(campaign, correlation, "server", mode, "communication.metadata_repair", hero, "", "", repaired == null ? "failed" : "completed",
                ReadLong(answer, "durationMs", 0), "Bounded repair of omitted or unusable communication metadata; no dispatch is authorized by this review.", evidence);
            if (repaired != null) result["content"] = Json.Serialize(repaired);
            else
            {
                if (drafts.Count > 0)
                using (new CommunicationDataReadLease())
                using (EnterCampaignWorkLock(CampaignRelationshipWriteLock(campaign)))
                using (var c = OpenCampaignConnection(campaign))
                    if (NpcLivesGeneration(c) == generation)
                        foreach (var draft in drafts)
                        {
                            var current = QuerySql(c, @"SELECT * FROM communication_messages WHERE message_id=$id AND revision=$revision
AND payload_json=$json AND status IN ('draft','needs_clarification');", TestDict("id", ReadString(draft, "message_id", ""),
                                "revision", ReadInt(draft, "revision", 0), "json", ReadString(draft, "payload_json", ""))).FirstOrDefault();
                            if (current == null) continue;
                            var held = GossipJson(current); held["failure"] = "The proposed correction could not be reconciled; the old draft must not dispatch.";
                            SetCommunicationStatus(c, current, "needs_clarification", held);
                        }
                result["ok"] = false;
                result["errorCode"] = "communication_plan_missing";
                result["error"] = "The character's communication promise could not be scheduled. Your contribution has been retained for retry.";
            }
            return result;
        }

        private static void EnsureCommunicationSchema(ReignDbConnection c)
        {
            if (IsPostgreSqlComponentSchemaReady(c, "conversation_communication", 1)) return;
            EnsureMemoryPrecisionSchema(c);
            ExecuteSql(c, @"CREATE TABLE IF NOT EXISTS communication_messages (
message_id TEXT PRIMARY KEY, root_id TEXT NOT NULL, parent_id TEXT NOT NULL DEFAULT '',
timeline_id TEXT NOT NULL, logical_id TEXT NOT NULL, session_id TEXT NOT NULL,
speaker_id TEXT NOT NULL, recipient_id TEXT NOT NULL, player_id TEXT NOT NULL,
topic_key TEXT NOT NULL, kind TEXT NOT NULL, status TEXT NOT NULL,
created_day DOUBLE PRECISION NOT NULL, dispatch_day DOUBLE PRECISION NOT NULL DEFAULT 0,
due_day DOUBLE PRECISION NOT NULL DEFAULT 0, acquired_day DOUBLE PRECISION NOT NULL DEFAULT 0,
revision INTEGER NOT NULL DEFAULT 1, attempts INTEGER NOT NULL DEFAULT 0,
created_ts BIGINT NOT NULL DEFAULT 0, accepted_sequence BIGINT NOT NULL DEFAULT 0,
lease_token TEXT NOT NULL DEFAULT '', lease_until BIGINT NOT NULL DEFAULT 0,
payload_json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_communication_due ON communication_messages(timeline_id,status,due_day);
CREATE INDEX IF NOT EXISTS idx_communication_session ON communication_messages(timeline_id,logical_id,speaker_id);
CREATE TABLE IF NOT EXISTS communication_history (
receipt_id TEXT PRIMARY KEY,message_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
owner_id TEXT NOT NULL,peer_id TEXT NOT NULL,acquired_day DOUBLE PRECISION NOT NULL,
acquired_ts BIGINT NOT NULL DEFAULT 0,accepted_sequence BIGINT NOT NULL DEFAULT nextval('memory_accepted_turn_sequence'),
payload_json TEXT NOT NULL,UNIQUE(message_id,owner_id));");
            MarkPostgreSqlComponentSchemaReady(c, "conversation_communication", 1);
        }

        private static bool CommunicationPromise(string quote) => CommunicationPromise(quote, quote);

        private static bool CommunicationPromise(string quote, string reply)
        {
            if (string.IsNullOrWhiteSpace(quote)) return false;
            // Conversation closure is the dispatch boundary, not an outstanding
            // condition such as payment or another character's consent. Strip only
            // that bounded timing clause; the remaining promise must still be the
            // speaker's direct, unconditional first-person commitment.
            string promise = Regex.Replace(quote.Trim(),
                @"^(?:(?:yes|certainly|of\s+course|very\s+well)[,.!]\s*)?(?:(?:your\s+(?:majesty|grace|highness)|my\s+(?:lord|lady))[,.!]\s*)?", "", RegexOptions.IgnoreCase);
            const string closure = @"(?:we\s+(?:(?:have|are)\s+)?(?:finish(?:ed)?|end(?:ed)?|done)(?:\s+(?:here|talking|speaking|(?:with\s+)?(?:this|our|the)\s+(?:conversation|discussion|talk)))?|(?:our|this|the)\s+(?:conversation|discussion|talk)\s+(?:is\s+over|(?:has\s+)?(?:ended|finished)|ends|finishes))";
            promise = Regex.Replace(promise, @"^(?:after|once|when)\s+" + closure + @"\s*[,—–:]\s*", "", RegexOptions.IgnoreCase);
            const string futureTime = @"(?:tomorrow|tonight|later\s+today|this\s+(?:evening|afternoon)|at\s+(?:dawn|sunrise|sunset|first\s+light)|in\s+(?:one|two|three|\d+)\s+(?:days?|hours?))";
            bool timed = Regex.IsMatch(promise, "^" + futureTime + @"\b", RegexOptions.IgnoreCase);
            promise = Regex.Replace(promise, "^" + futureTime + @"\s*[,—–:]?\s+", "", RegexOptions.IgnoreCase);
            if (Regex.IsMatch(promise, @"^[“""*]|^\s*(?:if|unless|perhaps|once|provided)\b|\bI(?:\s+(?:will\s+not|would|might|could|cannot|can't)|['’]ll\s+not|\s+won['’]t)\b|\bonly\s+if\s+you\b", RegexOptions.IgnoreCase)) return false;
            bool personal = Regex.IsMatch(promise, @"^I(?:['’]ll|\s+(?:will|shall|promise\s+to|am\s+going\s+to|am))\s+(?:(?:personally|certainly|gladly|myself)\s+)*(?:write|writing|send|sending|tell|ask|inform|notify|summon|invite|pass\s+(?:that|this|word|it)|carry\s+(?:it|this|that|the\s+(?:message|letter|word))|let\s+.{1,60}\s+know|have\s+(?:a\s+)?(?:messenger|courier))\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(promise, @"^I(?:['’]ll|\s+(?:will|shall))\s+(?:make\s+sure|see\s+to\s+it)\b[^.!?\n]{0,100}\b(?:write|send|told|informed|asked|summoned|invited|letter|message|word)\b", RegexOptions.IgnoreCase);
            personal = personal || timed && Regex.IsMatch(promise, @"^I\s+(?:write|send|tell|ask|inform|notify|summon|invite|pass\s+(?:that|this|word|it)|carry\s+(?:it|this|that|the\s+(?:message|letter|word)))\b", RegexOptions.IgnoreCase);
            bool outgoing = Regex.IsMatch(promise, @"^(?:only\s+)?(?:the|this|my|our|both|these|those)\s+(?:[\p{L}\d_-]+\s+){0,5}(?:letters?|messages?|word|invitations?|summons|questions?|notes?|terms)\s+(?:goes|go|leaves|leave|travels?|will\s+(?:go|leave|reach|be\s+(?:sent|delivered))|shall\s+(?:go|leave|reach|be\s+(?:sent|delivered)))\b", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(promise.Split(new[] { ':', '.', '!', '?' }, 2)[0], @"\b(?:if|unless|provided|perhaps|might|could|would)\b", RegexOptions.IgnoreCase);
            bool receipt = Regex.IsMatch(promise, @"^\p{Lu}[\p{L}'’-]*(?:\s+\p{Lu}[\p{L}'’-]*){0,3}\s+(?:will|shall)\s+(?:receive|have|get|hear|be\s+(?:told|informed|notified))\b")
                && !Regex.IsMatch(promise.Split(new[] { ':', '.', '!', '?' }, 2)[0], @"\b(?:if|unless|provided|perhaps|might|could|would|not)\b", RegexOptions.IgnoreCase);
            bool referred = false;
            if (Regex.IsMatch(promise, @"^(?:it|this|that)\s+(?:goes\s+(?:out|to)|leaves|(?:will|shall)\s+(?:go|leave|be\s+(?:sent|delivered)))\b", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(promise.Split(new[] { ':', '.', '!', '?' }, 2)[0], @"\b(?:if|unless|provided|perhaps|might|could|would|not)\b", RegexOptions.IgnoreCase))
            {
                // A pronoun is only evidence in the same speaker's preceding
                // bounded speech. A lantern going out, a third party's letter,
                // quoted dialogue or an unresolved 'it' cannot authorize dispatch.
                string spoken = Regex.Replace(reply ?? "", @"\*[^*]*\*|«[^»]*»|“[^”]*”|""[^""]*""", "", RegexOptions.Singleline);
                int at = spoken.IndexOf(quote, StringComparison.Ordinal);
                if (at >= 0)
                {
                    string prefix = spoken.Substring(Math.Max(0, at - 1200), Math.Min(at, 1200));
                    var antecedents = Regex.Matches(prefix, @"(?:^|[.!?]\s+|[\r\n]+)\s*(?:the|my|our|this|that)\s+(?:(?:new|fresh|sealed|revised|same)\s+){0,3}(?:letter|message|invitation|summons|note)\b", RegexOptions.IgnoreCase);
                    if (antecedents.Count > 0)
                    {
                        var last = antecedents[antecedents.Count - 1];
                        string heading = prefix.Substring(last.Index).Split(new[] { '.', '!', '?', '\r', '\n' }, 2)[0];
                        string before = prefix.Substring(0, last.Index).TrimEnd();
                        referred = !Regex.IsMatch(heading, @"\b(?:if|unless|provided|perhaps|might|could|would)\b", RegexOptions.IgnoreCase)
                            && !Regex.IsMatch(before, @"\b(?:she|he|they)\s+(?:said|says|promised|told\s+me)\s*[:—–]\s*$", RegexOptions.IgnoreCase);
                    }
                }
            }
            return personal || outgoing || receipt || referred;
        }

        private static string CommunicationFixedLocation(Dictionary<string, object> person)
        {
            string settlement = ReadString(person, "settlementId", "");
            if (settlement.Length > 0) return settlement;
            double x = ReadDouble(person, "positionX", double.NaN), y = ReadDouble(person, "positionY", double.NaN);
            return ReadBool(person, "positionKnown", false) && ReadString(person, "partyId", "").Length > 0 && double.IsFinite(x) && double.IsFinite(y)
                ? "field:" + x.ToString("R", CultureInfo.InvariantCulture) + ":" + y.ToString("R", CultureInfo.InvariantCulture) + ":land" : "";
        }

        private static bool CommunicationMeetingTerms(Dictionary<string, object> self, string destination, double stay, string evidence)
        {
            if (destination.Length == 0 || destination != CommunicationFixedLocation(self) || !double.IsFinite(stay) || stay < .25 || stay > 31.5) return false;
            string[] ones = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve",
                "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
            string words = stay == Math.Floor(stay) ? (stay < 20 ? ones[(int)stay] : (stay < 30 ? "twenty" : "thirty")
                + ((int)stay % 10 == 0 ? "" : "[- ]" + ones[(int)stay % 10])) : "";
            string number = Regex.Escape(stay.ToString("0.##", CultureInfo.InvariantCulture)) + (words.Length == 0 ? "" : "|" + words);
            if (stay == 1) number += "|a|an";
            if (stay == .5) number += "|half(?:[- ]a)?";
            if (stay == .25) number += "|(?:a[- ])?quarter(?:[- ]of[- ]a)?";
            const string attendance = @"\b(?:stay(?:s|ing)?|remain(?:s|ing)?|attend(?:s|ing)?|meeting|host|keep|meet)\b";
            return Regex.IsMatch(evidence, attendance + @"[^.!?\n]{0,100}\b(?:" + number + @")[- ]+(?:(?:full|whole)[- ]+)?days?\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(evidence, attendance + @"[^.!?\n]{0,100}\b"
                    + Regex.Escape((stay * 24).ToString("0.##", CultureInfo.InvariantCulture)) + @"[- ]+hours?\b", RegexOptions.IgnoreCase);
        }

        private static bool CommunicationWithdrawal(string quote)
        {
            string value = (quote ?? "").Trim();
            if (Regex.IsMatch(value, @"^[“""*]|^(?:if|unless|provided|perhaps|she\s+said|he\s+said)\b", RegexOptions.IgnoreCase)) return false;
            if (Regex.IsMatch(value.Split(new[] { ':', '.', '!', '?' }, 2)[0], @"\b(?:if|unless|provided|perhaps)\b", RegexOptions.IgnoreCase)) return false;
            return Regex.IsMatch(value, @"\bI\s+(?:will\s+not|won['’]t|withdraw|cancel)|\bI['’]m\s+(?:not\s+(?:sending|telling|writing)|cancelling)", RegexOptions.IgnoreCase)
                || Regex.IsMatch(value, @"^(?:withdrawn|cancelled|canceled)\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(value, @"^(?:the|this|my|our|that)\s+(?:letter|message|note|invitation|summons)\s+(?:is|stands|has\s+been)\s+(?:withdrawn|cancelled|canceled)\b", RegexOptions.IgnoreCase);
        }

        private static string CommunicationLogicalId(Dictionary<string, object> session) =>
            ReadString(TryParseJsonObject(ReadString(session, "payload_json", "{}")), "logicalConversationId", ReadString(session, "session_id", ""));

        private static List<Dictionary<string, object>> CommunicationContent(ReignDbConnection c, string speaker,
            Dictionary<string, object> payload, string session, IEnumerable<Dictionary<string, object>> current,
            IEnumerable<Dictionary<string, object>> supplied)
        {
            var sources = ReadLogicalConversationTurns(c, session, 100).Concat(current)
                .GroupBy(r => ReadString(r, "turn_id", "")).Select(g => g.Last()).ToList();
            var result = new List<Dictionary<string, object>>();
            var access = BuildKnowledgeAccessContext(speaker, ReadFirstString(payload, "playerHeroStringId", "playerId"), "", payload);
            foreach (var item in supplied.Take(13))
            {
                if (result.Count == 12) return new List<Dictionary<string, object>>();
                string id = ReadString(item, "turnId", ""), quote = ReadString(item, "quote", "").Trim();
                var row = sources.LastOrDefault(r => id == ReadString(r, "turn_id", "")
                    || id == "current_npc" && ReadString(r, "status", "") == "active" && ReadString(r, "speaker_id", "") == speaker && current.Contains(r)
                    || id == "current_player" && ReadString(r, "status", "") == "active" && ReadString(r, "role", "") == "player" && current.Contains(r));
                // A renewed promise may cite an exact passage from an earlier
                // conversation. Resolve only the requested ID and retain the same
                // owner, timeline, time and accepted-sequence visibility check.
                if (row == null && id != "current_player" && id != "current_npc" && id.Length is > 0 and <= 160)
                    row = QuerySql(c, "SELECT * FROM conversation_turns WHERE turn_id=$id;", TestDict("id", id)).FirstOrDefault();
                if (row == null || quote.Length < 8 || quote.Length > 4000
                    || !ReadString(row, "text", "").Contains(quote, StringComparison.Ordinal)
                    || !current.Contains(row) && !PrecisionTurnVisible(row, speaker, access, c)) return new List<Dictionary<string, object>>();
                result.Add(TestDict("turnId", ReadString(row, "turn_id", ""), "speakerId", ReadString(row, "speaker_id", ""),
                    "quote", quote, "sourceHash", PromptHash(ReadString(row, "text", ""))));
            }
            return result.GroupBy(r => ReadString(r, "turnId", "") + "|" + ReadString(r, "quote", "")).Select(g => g.First()).ToList();
        }

        private static Dictionary<string, object> AcceptCommunicationCommitments(ReignDbConnection c,
            AcceptedConversationUnit unit, Dictionary<string, object> source, List<Dictionary<string, object>> current)
        {
            EnsureCommunicationSchema(c);
            var accepted = new List<string>(); var rejected = new List<string>();
            if (ReadBool(unit.Payload, "officialMemoryFirewall", false)) return TestDict("accepted", accepted, "status", "official_scope");
            string sessionId = ReadString(source, "sessionId", ""), timeline = ContinuityTimeline(unit.Payload);
            var session = QuerySql(c, "SELECT * FROM conversation_sessions WHERE session_id=$id;", TestDict("id", sessionId)).FirstOrDefault();
            if (session == null) return TestDict("accepted", accepted, "status", "no_session");
            string logical = CommunicationLogicalId(session);
            var people = ReadDictionaryList(ReadDictionary(unit.Payload, "gossipPresence"), "people");
            var self = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), unit.Hero));
            var main = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), unit.Player));
            double day = ReadDouble(unit.Payload, "worldDay", 0);
            foreach (var plan in ReadDictionaryList(unit.Parsed, "communicationPlans").Take(4))
            {
                string key = ReadString(plan, "key", ""), targetId = ReadString(plan, "recipientId", ""), quote = ReadString(plan, "evidenceQuote", "").Trim();
                string id = "comm_" + PromptHash(timeline + "|" + logical + "|" + unit.Hero + "|" + targetId + "|" + key).Substring(0, 32).ToLowerInvariant();
                var prior = QuerySql(c, "SELECT * FROM communication_messages WHERE message_id=$id;", TestDict("id", id)).FirstOrDefault();
                if (prior == null)
                {
                    prior = QuerySql(c, @"SELECT * FROM communication_messages WHERE timeline_id=$timeline AND speaker_id=$speaker
AND recipient_id=$recipient AND topic_key=$key AND status IN ('draft','needs_clarification') ORDER BY created_day,message_id LIMIT 1;",
                        TestDict("timeline", timeline, "speaker", unit.Hero, "recipient", targetId, "key", key)).FirstOrDefault();
                    if (prior != null) id = ReadString(prior, "message_id", id);
                }
                void Reject(string reason)
                {
                    rejected.Add(key + ":" + reason);
                    if (prior == null || !new[] { "draft", "needs_clarification" }.Contains(ReadString(prior, "status", ""))) return;
                    var held = GossipJson(prior); held["failure"] = "A proposed correction or withdrawal needs clarification: " + reason;
                    held["unresolvedQuote"] = quote;
                    SetCommunicationStatus(c, prior, "needs_clarification", held);
                }
                if (!Regex.IsMatch(key, @"^[a-zA-Z0-9_-]{1,80}$") || quote.Length < 8 || !unit.Reply.Contains(quote, StringComparison.Ordinal))
                { Reject("unsupported_commitment"); continue; }
                if (ReadString(plan, "operation", "send") == "cancel")
                {
                    if (prior == null && CommunicationWithdrawal(quote))
                    {
                        // The model's topic spelling is not the identity of an
                        // unsent letter. Bind a clear withdrawal only to its one
                        // pending recipient draft in this same conversation.
                        var pending = QuerySql(c, @"SELECT * FROM communication_messages WHERE timeline_id=$timeline AND logical_id=$logical
AND speaker_id=$speaker AND recipient_id=$recipient AND status IN ('draft','needs_clarification') ORDER BY created_day,message_id;",
                            TestDict("timeline", timeline, "logical", logical, "speaker", unit.Hero, "recipient", targetId));
                        if (pending.Count == 1) { prior = pending[0]; id = ReadString(prior, "message_id", id); }
                        else if (pending.Count > 1)
                        {
                            foreach (var candidate in pending)
                            {
                                var held = GossipJson(candidate); held["failure"] = "The withdrawal names more than one possible unsent note; clarify which note is withdrawn.";
                                held["unresolvedQuote"] = quote; SetCommunicationStatus(c, candidate, "needs_clarification", held);
                            }
                            Reject("ambiguous_draft_withdrawal"); continue;
                        }
                    }
                    if (prior == null || !new[] { "draft", "needs_clarification" }.Contains(ReadString(prior, "status", "")) || !CommunicationWithdrawal(quote)) { Reject("not_a_draft_withdrawal"); continue; }
                    var cancelled = GossipJson(prior); cancelled["withdrawalQuote"] = quote;
                    ExecuteSql(c, "UPDATE communication_messages SET status='cancelled',revision=revision+1,payload_json=$json WHERE message_id=$id;",
                        TestDict("id", id, "json", Json.Serialize(cancelled))); accepted.Add(id); continue;
                }
                if (!CommunicationPromise(quote, unit.Reply) && !CommunicationSemanticAccepted(plan, unit)) { Reject("not_an_unconditional_promise"); continue; }
                if (prior != null && !new[] { "draft", "needs_clarification" }.Contains(ReadString(prior, "status", ""))) { Reject("already_committed_or_cancelled"); continue; }
                var recipient = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), targetId));
                var mentioned = GossipMentionedIds(unit.PlayerText, main, self, people)
                    .Concat(GossipMentionedIds(quote, self, main, people))
                    .Concat(GossipMentionedIds(unit.Reply, self, main, people)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (self == null || recipient == null || targetId == unit.Hero || targetId == unit.Player || !ReadBool(recipient, "isAlive", false)
                    || prior == null && !mentioned.Contains(targetId, StringComparer.OrdinalIgnoreCase)) { Reject("unresolved_recipient"); continue; }
                string kind = ReadString(plan, "kind", "inform");
                if (!new[] { "inform", "ask", "summon", "summon_then_inform" }.Contains(kind)) { Reject("unknown_kind"); continue; }
                var content = CommunicationContent(c, unit.Hero, unit.Payload, sessionId, current, ReadDictionaryList(plan, "contentEvidence"));
                if (content.Count == 0) { Reject("unsupported_content"); continue; }
                double delay = ReadDouble(plan, "delayDays", 0), stay = ReadDouble(plan, "stayDays", 0);
                if (!double.IsFinite(delay) || delay < 0 || delay > 126 || delay > 0
                    && !(delay == 1 && Regex.IsMatch(quote, @"\btomorrow\b", RegexOptions.IgnoreCase)
                        || Regex.IsMatch(quote, @"\bin\s+" + Regex.Escape(delay.ToString("0.##", CultureInfo.InvariantCulture)) + @"\s+days?\b", RegexOptions.IgnoreCase)))
                { Reject("unsupported_dispatch_time"); continue; }
                string destination = ReadString(plan, "destinationId", "");
                if (kind.StartsWith("summon", StringComparison.Ordinal) && !CommunicationMeetingTerms(self, destination, stay,
                    unit.Reply + "\n" + string.Join("\n", content.Select(e => ReadString(e, "quote", "")))))
                { Reject("missing_meeting_terms"); continue; }
                var data = TestDict("schema", "reign-communication-v1", "sourceEventId", unit.EventId, "sourceTurnIds", ReadStringList(source, "turnIds"),
                    "evidenceQuote", quote, "contentEvidence", content, "speaker", self, "recipient", recipient,
                    "playerId", unit.Player, "speakerName", ReadString(self, "name", unit.Hero), "playerName", unit.PlayerName ?? unit.Player,
                    "kind", kind, "dispatchDelayDays", delay, "destinationId", destination, "stayDays", stay,
                    "destinationName", ReadString(unit.Profile, "currentSettlementId", "") == destination ? ReadString(unit.Profile, "currentSettlementName", "") : "",
                    "sourceGeneration", unit.Generation, "replyRequested", kind == "ask" || kind.StartsWith("summon", StringComparison.Ordinal));
                ExecuteSql(c, @"INSERT INTO communication_messages(message_id,root_id,timeline_id,logical_id,session_id,speaker_id,recipient_id,player_id,
topic_key,kind,status,created_day,created_ts,accepted_sequence,payload_json) VALUES($id,$id,$timeline,$logical,$session,$speaker,$recipient,$player,$key,$kind,'draft',$day,$ts,$sequence,$json)
ON CONFLICT(message_id) DO UPDATE SET status='draft',revision=communication_messages.revision+1,payload_json=EXCLUDED.payload_json,
logical_id=EXCLUDED.logical_id,session_id=EXCLUDED.session_id,kind=EXCLUDED.kind,created_ts=EXCLUDED.created_ts,accepted_sequence=EXCLUDED.accepted_sequence;",
                    TestDict("id", id, "timeline", timeline, "logical", logical, "session", sessionId, "speaker", unit.Hero,
                        "recipient", targetId, "player", unit.Player, "key", key, "kind", kind, "day", day, "ts", unit.Timestamp,
                        "sequence", current.Where(r => ReadString(r, "speaker_id", "") == unit.Hero).Select(r => ReadLong(r, "accepted_sequence", 0)).DefaultIfEmpty(0).Max(),
                        "json", Json.Serialize(data)));
                accepted.Add(id);
            }
            return TestDict("accepted", accepted, "rejected", rejected, "status", rejected.Count == 0 ? "complete" : "incomplete");
        }

        private static void ReleaseCommunicationSession(ReignDbConnection c, Dictionary<string, object> session, string reason, double day)
        {
            EnsureCommunicationSchema(c);
            if (!new[] { "closed_by_player", "conversation_ended", "goodbye" }.Contains(reason)) return;
            string logical = CommunicationLogicalId(session), timeline = ContinuityTimeline(TryParseJsonObject(ReadString(session, "payload_json", "{}")));
            foreach (var row in QuerySql(c, "SELECT * FROM communication_messages WHERE timeline_id=$timeline AND logical_id=$logical AND status='draft';",
                TestDict("timeline", timeline, "logical", logical)))
            {
                var data = GossipJson(row);
                if (!CommunicationSourcesActive(c, data)) { SetCommunicationStatus(c, row, "cancelled_source", data); continue; }
                data["dispatchNotBefore"] = day + ReadDouble(data, "dispatchDelayDays", 0);
                data["closedDay"] = day;
                bool summons = ReadString(row, "kind", "").StartsWith("summon", StringComparison.Ordinal);
                if (summons)
                {
                    // Dispatch requires a native host reservation, not merely a
                    // location seen during the preceding conversation.
                    data["hostWaitUntilDay"] = day + ReadDouble(data, "dispatchDelayDays", 0) + 14;
                    data["hostReservationHash"] = PromptHash(CanonicalJson(TestDict("id", ReadString(row, "message_id", ""),
                        "revision", ReadInt(row, "revision", 1), "host", ReadString(row, "speaker_id", ""),
                        "target", ReadString(row, "recipient_id", ""), "terms", data)));
                }
                SetCommunicationStatus(c, row, summons ? "awaiting_host" : "queued", data);
            }
        }

        private static bool CommunicationSourcesActive(ReignDbConnection c, Dictionary<string, object> data)
        {
            foreach (var evidence in ReadDictionaryList(data, "contentEvidence"))
            {
                var row = QuerySql(c, "SELECT text,status FROM conversation_turns WHERE turn_id=$id;", TestDict("id", ReadString(evidence, "turnId", ""))).FirstOrDefault();
                if (row == null || ReadString(row, "status", "") != "active" || PromptHash(ReadString(row, "text", "")) != ReadString(evidence, "sourceHash", "")) return false;
            }
            return ReadDictionaryList(data, "contentEvidence").Count > 0;
        }

        private static void SetCommunicationStatus(ReignDbConnection c, Dictionary<string, object> row, string status, Dictionary<string, object> data)
        {
            ExecuteSql(c, "UPDATE communication_messages SET status=$status,payload_json=$json WHERE message_id=$id;",
                TestDict("id", ReadString(row, "message_id", ""), "status", status, "json", Json.Serialize(data)));
            row["status"] = status; row["payload_json"] = Json.Serialize(data);
        }

        private static string BuildCommunicationPrompt(ReignDbConnection c, string hero, Dictionary<string, object> payload, List<string> sourceIds)
        {
            if (ReadBool(payload, "officialMemoryFirewall", false)) return "";
            EnsureCommunicationSchema(c);
            var text = new StringBuilder(CommunicationContract);
            var access = BuildKnowledgeAccessContext(hero, ReadFirstString(payload, "playerHeroStringId", "playerId"), "", payload);
            var people = ReadDictionaryList(ReadDictionary(payload, "gossipPresence"), "people");
            var self = people.FirstOrDefault(p => GossipHeroId(p) == hero);
            var player = people.FirstOrDefault(p => GossipHeroId(p) == ReadFirstString(payload, "playerHeroStringId", "playerId"));
            var mentioned = GossipMentionedIds(ReadFirstString(payload, "playerText", "text", "message"), player, self, people)
                .Concat(new[] { ReadString(self, "motherId", ""), ReadString(self, "fatherId", ""), ReadString(self, "spouseId", "") }).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var person in people.Where(p => mentioned.Contains(GossipHeroId(p))).Take(16))
                text.AppendLine("Referenced recipient: " + ReadString(person, "name", "") + " [" + GossipHeroId(person) + "].");
            text.AppendLine("Current fixed meeting location ID: " + CommunicationFixedLocation(self) + ". Summons may use this location only; explicitly agree the stay in your reply.");
            string timeline = ContinuityTimeline(payload); double day = ReadDouble(payload, "worldDay", 0);
            foreach (var row in QuerySql(c, @"SELECT * FROM communication_messages WHERE timeline_id=$timeline AND speaker_id=$hero
AND parent_id='' AND created_day<=$day AND created_ts<=$ts AND accepted_sequence<=$sequence ORDER BY created_day DESC,message_id LIMIT 24;",
                TestDict("timeline", timeline, "hero", hero, "day", day, "ts", access.AsOfTimestamp, "sequence", access.AsOfAcceptedSequence)))
            {
                var data = GossipJson(row); string id = ReadString(row, "message_id", "");
                string status = ReadString(row, "status", "");
                // A sender does not acquire the distant recipient's decision just
                // because the transport service has processed it internally.
                if (ReadDouble(row, "dispatch_day", 0) > 0 && !new[] { "meeting", "completed", "interrupted", "blocked" }.Contains(status))
                    status = CommunicationReplyKnown(c, id, hero, payload)
                        ? "reply_received" : "sent";
                sourceIds.Add(id);
                text.AppendLine("Dispatch " + id + ": key=" + ReadString(row, "topic_key", "") + "; recipient=" + ReadString(row, "recipient_id", "")
                    + "; status=" + status + "; promise=" + ReadString(data, "evidenceQuote", "")
                    + (status == "blocked" || status == "interrupted" || status == "needs_clarification" ? "; observed problem=" + ReadString(data, "failure", "") : ""));
            }
            foreach (var row in QuerySql(c, @"SELECT * FROM communication_history WHERE timeline_id=$timeline AND owner_id=$hero
AND acquired_day<=$day AND acquired_ts<=$ts AND accepted_sequence<=$sequence ORDER BY acquired_day DESC,receipt_id LIMIT 30;",
                TestDict("timeline", timeline, "hero", hero, "day", day, "ts", access.AsOfTimestamp, "sequence", access.AsOfAcceptedSequence)))
            {
                var data = GossipJson(row); string id = ReadString(row, "receipt_id", ""); sourceIds.Add(id);
                text.AppendLine(RenderContinuityRecord(id, "delivered_communication", hero, "", ReadDouble(row, "acquired_day", 0), 120, true,
                    "Private correspondence, attributed testimony rather than verified world facts: " + ReadString(data, "body", "")));
            }
            return text.ToString();
        }

        private static bool CommunicationReplyKnown(ReignDbConnection c, string id, string hero, Dictionary<string, object> payload)
        {
            if (c == null || !TableExists(c, "communication_history")) return false;
            var access = BuildKnowledgeAccessContext(hero, ReadFirstString(payload, "playerHeroStringId", "playerId"), "", payload);
            return QuerySql(c, @"SELECT receipt_id FROM communication_history WHERE message_id=$id AND timeline_id=$timeline AND owner_id=$hero
AND acquired_day<=$day AND acquired_ts<=$ts AND accepted_sequence<=$sequence LIMIT 1;",
                TestDict("id", id + "_reply", "timeline", ContinuityTimeline(payload), "hero", hero,
                    "day", ReadDouble(payload, "worldDay", 0), "ts", access.AsOfTimestamp, "sequence", access.AsOfAcceptedSequence)).Count > 0;
        }

        private static void BindRecoveredCommunications(ReignDbConnection c, string requested, string recovered, long sequence, string head)
        {
            if (requested == recovered || !TableExists(c, "communication_messages")) return;
            var restored = RestoredLetterContext(c, recovered);
            if (restored == null || ReadLong(restored, "head_sequence", -1) != sequence || ReadString(restored, "head_event_id", "") != head) return;
            var args = TestDict("old", requested, "new", recovered, "day", ReadDouble(restored, "world_day", -1));
            // This is an exact restored database, not permission to inherit a
            // similarly named timeline. IDs, content, deadlines and receipts stay fixed.
            ExecuteSql(c, @"UPDATE communication_messages SET timeline_id=$new WHERE timeline_id=$old AND created_day<=$day;
UPDATE communication_history SET timeline_id=$new WHERE timeline_id=$old AND acquired_day<=$day;", args);
            if (!TableExists(c, "character_npc_lives_state")) return;
            var old = QuerySql(c, "SELECT value_json FROM character_npc_lives_state WHERE timeline_id=$old;", args).FirstOrDefault();
            if (old == null) return;
            var state = Json.Deserialize<ReignBeta.Shared.Characters.NpcLivesState>(ReadString(old, "value_json", "{}"));
            if (state.TimelineId != requested) throw new InvalidOperationException("The restored meeting ledger has a different timeline.");
            state.TimelineId = recovered;
            ExecuteSql(c, @"INSERT INTO character_npc_lives_state(timeline_id,value_json) VALUES($timeline,$json)
ON CONFLICT(timeline_id) DO NOTHING;", TestDict("timeline", recovered, "json", Json.Serialize(state)));
        }
    }
}
