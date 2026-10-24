using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void SchedulePersonalGossip(string campaign)
        {
            if (campaign.StartsWith("__gossip_test_", StringComparison.Ordinal) || !string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)
                || Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE") == "1" || !PersonalGossipWorkers.TryAdd(campaign, 0)) return;
            _ = EnqueuePriorityBackgroundWork("personal_gossip." + campaign, "normal", () =>
            {
                try { RunPersonalGossipWork(campaign, ComposePersonalGossip); }
                finally { PersonalGossipWorkers.TryRemove(campaign, out _); }
            });
        }

        private static bool GossipSourcesActive(ReignDbConnection c, Dictionary<string, object> data)
        {
            if (ReadInt(data, "hop", 1) > 1 || ReadBool(data, "reply", false))
                return QuerySql(c, "SELECT receipt_id FROM personal_gossip_history WHERE message_id=$id AND owner_id=$owner LIMIT 1;",
                    GossipFields("id", ReadString(data, "receivedMessageId", ""), "owner", ReadString(data, "speakerId", ""))).Count > 0;
            var ids = ReadStringList(data, "sourceTurnIds");
            if (ids.Count == 0) return false;
            var text = new StringBuilder();
            foreach (string id in ids)
            {
                var row = QuerySql(c, "SELECT text,status FROM conversation_turns WHERE turn_id=$id;", GossipFields("id", id)).FirstOrDefault();
                if (row == null || ReadString(row, "status", "") != "active") return false;
                text.AppendLine(ReadString(row, "text", ""));
            }
            return ReadString(data, "type", "") == "impression" || GossipQuoteSupported(ReadString(data, "sourceQuote", ""), text.ToString());
        }

        private static void RunPersonalGossipWork(string campaign, Func<Dictionary<string, object>, bool, Dictionary<string, object>> compose)
        {
            // Provider waits hold no campaign/database lock. Both restore generations and the
            // persisted lease must still match before accepting a result into the loaded save.
            for (int item = 0; item < 8 && ReadBool(PersonalGossipSettings(), "enabled", true); item++)
            {
                Dictionary<string, object> row = null, data = null;
                string token = Guid.NewGuid().ToString("N"), generation = "";
                long processGeneration = Interlocked.Read(ref CampaignReplacementGeneration);
                CampaignDataGate.EnterReadLock();
                try
                {
                    using (EnterCampaignWorkLock(GossipGate(campaign)))
                    using (var c = OpenCampaignConnection(campaign))
                    {
                        EnsurePersonalGossipSchema(c);
                        EnsureMemoryPrecisionSchema(c);
                        generation = ReadString(ReadMemoryPrecisionState(c), "restore_generation", "");
                        token = generation + ":" + token;
                        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        row = QuerySql(c, @"SELECT m.* FROM personal_gossip_messages m JOIN personal_gossip_clock k ON k.timeline_id=m.timeline_id
WHERE m.timeline_id=$timeline AND m.attempts<3 AND m.expires_day>=k.world_day
AND (m.status='pending' OR m.status='generating' AND (m.lease_until<$now OR m.lease_token NOT LIKE $generation))
ORDER BY m.created_day,m.message_id LIMIT 1;", GossipFields("now", now, "timeline", ActiveWorldHistoryTimeline(c), "generation", generation + ":%")).FirstOrDefault();
                        if (row == null) return;
                        data = GossipJson(row);
                        if (!GossipSourcesActive(c, data))
                        {
                            ExecuteSql(c, "UPDATE personal_gossip_messages SET status='cancelled_source' WHERE message_id=$id;",
                                GossipFields("id", ReadString(row, "message_id", "")));
                            continue;
                        }
                        if (ReadInt(row, "is_reply", 0) != 0)
                        {
                            var currentPresence = GossipSnapshot(c, ReadString(row, "timeline_id", "main"));
                            var replyingPerson = ReadDictionaryList(currentPresence, "people")
                                .FirstOrDefault(p => GossipSameId(GossipHeroId(p), ReadString(row, "speaker_id", "")));
                            if (replyingPerson == null && !ReadBool(currentPresence, "rosterComplete", false)) return;
                            if (!GossipCommunicationAvailable(replyingPerson))
                            {
                                CancelUnavailablePersonalGossip(c, row, data, "reply_speaker_unavailable", replyingPerson,
                                    ReadDouble(GossipClock(c, ReadString(row, "timeline_id", "main")), "world_day", 0));
                                continue;
                            }
                        }
                        data["operatorAttempt"] = ReadInt(row, "attempts", 0) + 1;
                        data["campaignId"] = campaign;
                        data["messageId"] = ReadString(row, "message_id", "");
                        data["currentDay"] = ReadDouble(GossipClock(c, ReadString(row, "timeline_id", "main")), "world_day", ReadDouble(row, "created_day", 0));
                        if (ReadInt(row, "is_reply", 0) == 0 && ReadInt(data, "hop", 1) == 1)
                        {
                            string sourceQuote = ReadString(data, "sourceQuote", "");
                            var sourceTurns = ReadStringList(data, "sourceTurnIds").Take(2)
                                .SelectMany(id => QuerySql(c, "SELECT speaker_id,text FROM conversation_turns WHERE turn_id=$id AND status='active';", GossipFields("id", id)))
                                .ToList();
                            BindPersonalGossipEncounterTurns(data, sourceTurns);
                            var quotedSpeakers = sourceTurns.Where(turn => GossipQuoteSupported(sourceQuote, ReadString(turn, "text", "")))
                                .Select(turn => ReadString(turn, "speaker_id", "")).Distinct().ToList();
                            data["sourceQuoteSpeakerId"] = quotedSpeakers.Count == 1 ? quotedSpeakers[0] : "";
                        }
                        if (ReadInt(row, "is_reply", 0) != 0) PreparePersonalGossipReply(c, campaign, row, data);
                        ExecuteSql(c, @"UPDATE personal_gossip_messages SET status='generating',attempts=attempts+1,lease_token=$token,lease_until=$until
WHERE message_id=$id;", GossipFields("id", ReadString(row, "message_id", ""), "token", token, "until", now + 600));
                    }
                }
                finally { CampaignDataGate.ExitReadLock(); }
                if (row == null || data == null) continue;
                Dictionary<string, object> result;
                try
                {
                    result = compose(data, ReadInt(row, "is_reply", 0) != 0);
                    // Fixed post-result latency tests the causal clock boundary without
                    // provider timing, injected prose, or an arbitrary operator sleep.
                    int delaySeconds = PersonalGossipCompositionDelaySeconds(data);
                    if (delaySeconds > 0) Thread.Sleep(delaySeconds * 1000);
                }
                catch (Exception ex) { result = GossipFields("error", LimitText(ex.Message, 500)); }
                CampaignDataGate.EnterReadLock();
                try
                {
                    using (EnterCampaignWorkLock(GossipGate(campaign)))
                    using (var c = OpenCampaignConnection(campaign))
                    {
                        EnsurePersonalGossipSchema(c);
                        EnsureMemoryPrecisionSchema(c);
                        if (processGeneration != Interlocked.Read(ref CampaignReplacementGeneration)
                            || generation != ReadString(ReadMemoryPrecisionState(c), "restore_generation", "")) continue;
                        var current = QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE message_id=$id AND lease_token=$token;",
                            GossipFields("id", ReadString(row, "message_id", ""), "token", token)).FirstOrDefault();
                        if (current == null || ReadString(current, "status", "") != "generating") continue;
                        bool isReply = ReadInt(current, "is_reply", 0) != 0;
                        bool accepted = GossipSourcesActive(c, data) && ValidatePersonalGossipComposition(c, data, result, isReply);
                        if (!accepted)
                        {
                            data["lastFailure"] = ReadString(result, "error", "Composition failed source, privacy, length, or distortion validation.");
                            ExecuteSql(c, @"UPDATE personal_gossip_messages SET status=$status,lease_token='',lease_until=0,payload_json=$json WHERE message_id=$id;",
                                GossipFields("id", ReadString(current, "message_id", ""), "status", ReadInt(current, "attempts", 0) >= 3 ? "generation_failed" : "pending", "json", Json.Serialize(data)));
                            continue;
                        }
                        data["body"] = ReadString(result, "body", "").Trim();
                        data["account"] = ReadString(result, "account", ReadInt(data, "hop", 1) > 1 ? "secondhand" : "firsthand");
                        if (result.TryGetValue(GossipCompositionApprovalKey, out object approvalValue)
                            && approvalValue is GossipCompositionApproval approval)
                            data["compositionReview"] = approval.Evidence;
                        result.Remove(GossipCompositionApprovalKey);
                        data["reaction"] = result;
                        // A provider result cannot date itself using the clock captured before
                        // its request. Native must first observe this nonce, then capture time.
                        data["readyNonce"] = Guid.NewGuid().ToString("N");
                        ExecuteSql(c, @"UPDATE personal_gossip_messages SET status='awaiting_native_clock',lease_token='',lease_until=0,payload_json=$json WHERE message_id=$id;",
                            GossipFields("id", ReadString(current, "message_id", ""), "json", Json.Serialize(data)));
                    }
                }
                finally { CampaignDataGate.ExitReadLock(); }
            }
        }

        private static int PersonalGossipCompositionDelaySeconds(Dictionary<string, object> data)
        {
            if (ReadInt(data, "operatorAttempt", 1) != 1) return 0;
            string mode = ReadString(ReadDictionary(data, "operatorTestControls"), "compositionMode", "production");
            return mode == "delay_once" ? 15 : mode == "delay_past_watch_once"
                && !ReadBool(data, "reply", false) && ReadInt(data, "hop", 1) == 1 ? 135 : 0;
        }

        private static int PublishReadyPersonalGossip(ReignDbConnection c, string campaign, Dictionary<string, object> payload)
        {
            if (ReadString(payload, "gossipClockEpoch", "") != GossipClockEpoch(c)) return 0;
            var nonces = ReadStringList(payload, "gossipReadyNonces").Take(32).ToHashSet(StringComparer.Ordinal);
            if (nonces.Count == 0) return 0;
            string timeline = GossipTimeline(payload);
            double day = ReadDouble(payload, "worldDay", 0);
            var people = ReadDictionaryList(ReadDictionary(payload, "gossipPresence"), "people");
            int published = 0;
            foreach (var row in QuerySql(c, @"SELECT * FROM personal_gossip_messages WHERE timeline_id=$timeline
AND status='awaiting_native_clock' ORDER BY created_day,message_id LIMIT 32;", GossipFields("timeline", timeline)))
            {
                var data = GossipJson(row);
                if (!nonces.Contains(ReadString(data, "readyNonce", ""))) continue;
                if (!GossipSourcesActive(c, data))
                {
                    ExecuteSql(c, "UPDATE personal_gossip_messages SET status='cancelled_source' WHERE message_id=$id;", GossipFields("id", ReadString(row, "message_id", "")));
                    continue;
                }
                bool reply = ReadInt(row, "is_reply", 0) != 0;
                if (!reply && day > ReadDouble(row, "expires_day", day))
                {
                    ExecuteSql(c, "UPDATE personal_gossip_messages SET status='expired' WHERE message_id=$id;", GossipFields("id", ReadString(row, "message_id", "")));
                    continue;
                }
                if (reply)
                {
                    // Return correspondence starts when the answer exists. A local
                    // exchange remains local even when its participants have parted.
                    var speaker = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), ReadString(row, "speaker_id", "")));
                    if (speaker == null && !ReadBool(ReadDictionary(payload, "gossipPresence"), "rosterComplete", false)) continue;
                    if (!GossipCommunicationAvailable(speaker))
                    {
                        CancelUnavailablePersonalGossip(c, row, data, "reply_speaker_unavailable", speaker, day);
                        continue;
                    }
                    var recipient = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), ReadString(row, "recipient_id", ""))) ?? new Dictionary<string, object>();
                    double transit = ReadBool(data, "local", false) ? 0 : GossipTransitDays(speaker, recipient, ReadDictionary(data, "policy"), false);
                    data["dueDay"] = day + transit;
                    data["returnRoute"] = GossipFields("local", ReadBool(data, "local", false), "delayDays", transit, "queuedDay", day,
                        "speakerPresence", speaker, "recipientPresence", recipient);
                    // The listener already owns the heard report and its own reaction.
                    // Retelling takes its own transaction; its unique topic prevents
                    // a publication retry from granting a second outward opportunity.
                    UpdatePersonalGossipReaction(c, campaign, data, ReadDictionary(data, "reaction"), day);
                }
                using (var tx = c.BeginTransaction())
                {
                    data.Remove("recipientMemory"); data.Remove("replyEvidence"); data.Remove("readyNonce");
                    data["publishedDay"] = day;
                    ExecuteSql(c, @"UPDATE personal_gossip_messages SET status='in_transit',due_day=$due,expires_day=CASE WHEN is_reply=1 AND expires_day<$due THEN $due ELSE expires_day END,payload_json=$json WHERE message_id=$id;",
                        GossipFields("id", ReadString(row, "message_id", ""), "due", ReadDouble(data, "dueDay", day), "json", Json.Serialize(data)));
                    if (reply || !ReadBool(data, "local", false))
                        StorePersonalGossipHistory(c, row, data, ReadString(row, "speaker_id", ""), ReadString(row, "recipient_id", ""), day, 50, true);
                    tx.Commit();
                }
                published++;
            }
            return published;
        }

        private static Dictionary<string, object> PersonalGossipCompositionBrief(Dictionary<string, object> data)
        {
            string speaker = ReadString(data, "speakerId", ""), player = ReadString(data, "playerId", "");
            string speakerName = ReadString(ReadDictionary(data, "speaker"), "name", speaker);
            string playerName = ReadString(ReadDictionary(data, "playerReference"), "label", "the traveler");
            string quoteSpeaker = ReadString(data, "sourceQuoteSpeakerId", "");
            var sourceFrame = PersonalGossipOriginalSourceFrame(data);
            return GossipFields("speaker", speakerName, "speakerId", speaker,
                "recipient", ReadString(ReadDictionary(data, "recipient"), "name", ReadString(data, "recipientId", "")),
                "recipientId", ReadString(data, "recipientId", ""), "player", playerName,
                "sourceQuoteSpeaker", quoteSpeaker == player ? playerName : quoteSpeaker == speaker ? speakerName : "an attributed earlier speaker",
                "originalSourceFrame", ReadString(sourceFrame, "body", ""),
                "sourceQuoteAddressee", ReadInt(data, "hop", 1) > 1 ? speakerName : quoteSpeaker == speaker ? playerName : speakerName,
                "playerIdentityState", ReadString(ReadDictionary(data, "playerReference"), "identityState", "encountered_unknown"),
                "personality", ReadDictionary(ReadDictionary(data, "traits"), "traitPercentages") ?? new Dictionary<string, object>(),
                "intent", ReadString(data, "type", "positive"), "claimKind", ReadString(data, "claimKind", "assertion"),
                "local", ReadBool(data, "local", false), "uncertain", ReadBool(data, "uncertain", false),
                "allowedSource", ReadString(data, "sourceQuote", ""), "distortionSeverity", ReadString(data, "distortion", "none"),
                "acceptedEncounter", PersonalGossipEncounterContext(data));
        }

        private static bool PersonalGossipSubstantiveQuestion(string body) => Regex.Matches(body ?? "", @"[^.!?]*\?", RegexOptions.IgnoreCase)
            .Cast<Match>().Any(m => !Regex.IsMatch(m.Value.Trim(), @"^(?:(?:did you hear|have you heard)(?: about (?:it|that|this))?|what do you (?:make|think) of (?:it|that|this))\?$", RegexOptions.IgnoreCase));

        private static bool PersonalGossipNeedsQuestionComposition(Dictionary<string, object> data) =>
            ReadString(data, "type", "") != "impression" && ReadString(data, "distortion", "none") == "none"
            && ReadString(data, "claimKind", "") == "question";

        private static bool PersonalGossipOutcomeSupported(string body, string source, string severity)
        {
            if (severity != "none") return true;
            const string positive = @"\b(?:went well|good (?:time|impression)|enjoy(?:ed|able)?|wonderful|pleasant)\b";
            const string negative = @"\b(?:went badly|bad (?:time|impression)|terrible|unpleasant)\b";
            // Asking about an unknown outcome is permitted; asserting it needs source support.
            string assertions = Regex.Replace(body ?? "", @"[^.!?]*\?", "");
            return new[] { positive, negative }.All(pattern => !Regex.IsMatch(assertions, pattern, RegexOptions.IgnoreCase)
                || Regex.IsMatch(source ?? "", pattern, RegexOptions.IgnoreCase));
        }

        private static bool PersonalGossipNeedsSourceFraming(Dictionary<string, object> data) =>
            // Even a quote without pronouns can be assigned to the wrong participant.
            // Bind every undistorted report to the verified turn author, not model inference.
            ReadString(data, "type", "") != "impression" && ReadString(data, "distortion", "none") == "none";

        private static Dictionary<string, object> PersonalGossipOriginalSourceFrame(Dictionary<string, object> data)
        {
            // The original turn's author/addressee stay fixed even when a later
            // malicious claim is permitted to change what happened in that encounter.
            var original = new Dictionary<string, object>(data) { ["distortion"] = "none" };
            return PersonalGossipFramedSource(original);
        }

        private static bool PersonalGossipNamesSubject(Dictionary<string, object> data, string body)
        {
            string label = ReadString(ReadDictionary(data, "playerReference"), "label", "the traveler").Trim();
            if (label.Length == 0) return false;
            if (Regex.IsMatch(body, @"(?<!\w)" + Regex.Escape(label) + @"(?!\w)", RegexOptions.IgnoreCase)) return true;
            // Ordinary anonymous references may omit the article without gaining
            // access to a canonical name or binding 'you' to the wrong participant.
            return label.StartsWith("the ", StringComparison.OrdinalIgnoreCase) && label.Length > 4
                && Regex.IsMatch(body, @"(?<!\w)" + Regex.Escape(label.Substring(4)) + @"(?!\w)", RegexOptions.IgnoreCase);
        }

        private static bool PersonalGossipRebindsSourceAddressee(Dictionary<string, object> data, string body)
        {
            var frame = PersonalGossipOriginalSourceFrame(data);
            string framedQuote = ReadString(frame, "body", "");
            // A correctly attributed verbatim quotation keeps its own addressee.
            if (framedQuote.Length > 0) body = body.Replace(framedQuote, "");
            Func<string, string[]> words = text => Regex.Matches(text ?? "", @"[\p{L}\p{N}]+(?:['\u2019][\p{L}]+)?")
                .Cast<Match>().Select(m => m.Value.ToLowerInvariant()).ToArray();
            var source = words(ReadString(data, "sourceQuote", ""));
            string rewritten = " " + string.Join(" ", words(body)) + " ";
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] != "you" && source[i] != "your" && source[i] != "yours") continue;
                int start = Math.Max(0, i - 2), end = Math.Min(source.Length, i + 4);
                if (end - start >= 4 && rewritten.Contains(" " + string.Join(" ", source.Skip(start).Take(end - start)) + " ")) return true;
            }
            return false;
        }

        private static Dictionary<string, object> PersonalGossipFramedSource(Dictionary<string, object> data)
        {
            if (!PersonalGossipNeedsSourceFraming(data)) return null;
            string author = ReadString(data, "sourceQuoteSpeakerId", ""), speaker = ReadString(data, "speakerId", "");
            string player = ReadString(data, "playerId", ""), playerLabel = ReadString(ReadDictionary(data, "playerReference"), "label", "the traveler");
            bool retell = ReadInt(data, "hop", 1) > 1;
            string introduction;
            string account = "secondhand";
            if (retell)
            {
                if (author.Length == 0 || author == speaker || ReadString(data, "sourceQuoteAddresseeId", "") != speaker) return null;
                introduction = ReadString(data, "sourceQuoteSpeakerName", "Someone") + " told me this: ";
            }
            else if (author.Length > 0 && author == player) introduction = playerLabel + " told me this: ";
            else if (author.Length > 0 && author == speaker) { introduction = "I told " + playerLabel + ": "; account = "firsthand"; }
            else return null; // An ambiguous source never acquires an invented narrator.
            string body = introduction + "\u201c" + ReadString(data, "sourceQuote", "") + "\u201d";
            if (ReadString(data, "claimKind", "") == "question") body += " What do you make of that?";
            return GossipFields("body", body, "sourceQuote", ReadString(data, "sourceQuote", ""), "distortionSeverity", "none", "account", account);
        }

        private static Dictionary<string, object> ComposePersonalGossip(Dictionary<string, object> data, bool reply)
            => ComposePersonalGossip(data, reply, ChatWithLlm);

        private static Dictionary<string, object> ComposePersonalGossip(Dictionary<string, object> data, bool reply,
            Func<Dictionary<string, object>, Dictionary<string, object>> respond)
        {
            string fault = ReadString(ReadDictionary(data, "operatorTestControls"), "compositionMode", "production");
            if (fault == "fail_always" || fault == "fail_once" && ReadInt(data, "operatorAttempt", 1) == 1)
                throw new InvalidOperationException("Bounded operator composition fault.");
            if (!reply && ReadString(data, "type", "") == "impression")
                return GossipFields("body", PersonalGossipImpressionBody(data), "sourceQuote", ReadString(data, "sourceQuote", ""),
                    "distortionSeverity", "none", "account", "firsthand");
            if (!reply && PersonalGossipNeedsSourceFraming(data) && !PersonalGossipNeedsQuestionComposition(data))
                return PersonalGossipFramedSource(data) ?? GossipFields("error", "Source has no unambiguous original speaker/addressee.");
            if (!reply && PersonalGossipOriginalSourceFrame(data) == null)
                return GossipFields("error", "A distorted claim still needs an unambiguous original speaker/addressee.");
            string campaign = ReadString(data, "campaignId", ""), speaker = ReadString(data, "speakerId", "");
            var brief = PersonalGossipCompositionBrief(data);
            string instruction;
            if (!reply)
            {
                instruction = "Write one brief NPC-to-NPC gossip message in natural Bannerlord dialogue, 1-3 sentences and at most 100 words. "
                    + "You are speaker, speaking directly to recipient about player. Use their supplied names naturally; never call a person 'the player'. "
                    + "allowedSource is a quotation by sourceQuoteSpeaker, not automatically your own experience. Resolve I/me/my and you/your from that original speaker's perspective before paraphrasing. "
                    + "Preserve the actual people. Address the recipient as you; a speaker's parent does not become the recipient's parent. Use supplied identities rather than inventing a kinship. "
                    + "originalSourceFrame records who actually said the source words to whom. The source's you/your refers to sourceQuoteAddressee, which may be the player and is not automatically today's recipient. "
                    + "Write a new message to today's recipient. Refer to the player by the exact supplied player label in the body; never carry the source's you/your over as if today's recipient were the player. "
                    + "Every sentence must fit today's addressee. Omit the source's plans or instructions to the player about telling the recipient; they are motive evidence, not sentences to copy into this message. "
                    + "For example, when the source tells the player 'let him hear it', do not tell that very recipient 'let him hear it' or ask them to pass the report to themselves. Address the recipient directly or omit that clause. "
                    + "Return JSON {body, sourceQuote, distortionSeverity, account}. Copy sourceQuote and distortionSeverity from the brief. "
                    + "Never mention game systems or percentages. Use acceptedEncounter to understand the actual spoken exchange and who each person is. It is reported speech, not independent proof of its factual claims. "
                    + "allowedSource is the selected quotation; it may describe a grievance or intention instead of the encounter. Do not turn the recipient's past actions into the player's history. "
                    + "Never invent people or change their roles. The frozen distortion severity determines whether to report the encounter faithfully or deliberately lie about what happened. "
                    + "At loaded_exaggeration, give the real encounter a clearly loaded interpretation, for example portraying an ordinary visitor as clingy or overfamiliar. Keep the underlying event intact; do not invent a consequential new event. A neutral account of arriving, sitting or talking, or ordinary elapsed-time wording alone, does not fulfill this assignment. "
                    + "At false_escalation, actually assert a false escalated interpretation of the encounter, for example recasting ordinary friendliness as deliberate romantic advances. A faithful summary or plan to spread it is insufficient. "
                    + "invented_detail permits a consequential false detail about the same encounter and people, such as claiming a night together after a private conversation. "
                    + "When that permission is selected, actually make the permitted distorted claim; repeating a plan to tell the story is not an invented event. Deception never changes who the source's participants were. "
                    + "At invented_detail your assignment is a concrete consequential lie, not a faithful summary with suggestive wording. Assert a specific invented event or detail involving the same encounter participants. "
                    + "Merely repeating accepted actions, saying they lasted longer, hinting at closeness, or adding 'closer than anyone imagines' does not satisfy invented_detail. "
                    + "For example, after a shared drink and conversation, an explicit false claim of spending the night together is permitted even though no overnight stay occurred. Choose a consequential claim suited to this encounter; do not replace it with a promise to gossip later. "
                    + "This exception is intentional deception by the speaker, not world truth. With severity none, preserve the actual account. "
                    + "For claimKind question, ask a substantive question about the supported encounter without asserting its unknown answer. A rhetorical 'Did you hear?' followed by an assertion is insufficient. "
                    + "Carry the actual unresolved question from acceptedEncounter to today's recipient. A generic 'What do you make of that?' after an adjacent quotation loses the consultation and is forbidden. "
                    + "An agreement to ask a mother what her earlier questions meant must become that question addressed to her; do not merely quote the player's reason for a proposal. Preserve all qualifications and refusals. "
                    + "A local exchange may occur while parting. A remote exchange is a carried message. Never assert present co-location. "
                    + "If uncertain, explicitly attribute and qualify the report. account is firsthand, secondhand or speculation; never upgrade a received story to firsthand.";
            }
            else
            {
                brief["receivedMessage"] = ReadString(data, "receivedBody", "");
                brief["ownAccessibleMemory"] = ReadString(data, "recipientMemory", "");
                brief["independentEvidence"] = ReadDictionaryList(data, "replyEvidence").Select(e => PersonalGossipReplyEvidenceBrief(data, e)).ToList();
                brief["beliefConfidence"] = ReadInt(data, "credibility", 50);
                instruction = "Reply briefly to the received NPC message in natural Bannerlord dialogue, 1-3 sentences, at most 100 words. "
                    + "Use only your own accessible memory and the message. Answer a question if known; otherwise acknowledge, refuse or express uncertainty. "
                    + "Hearsay is not proof. Never invent a romantic attachment or treat a report as your own experience. "
                    + "Return JSON {body, sourceQuote, privacy, existencePrivacy, confidenceOwnerIds:[], promisedSecrecy:false, ownCompromising:false, "
                    + "answerKind:'answered|refused|evaded|acknowledged', feeling:0, personalStake:25, beliefEvidence:[], gossipSignals:[]}. "
                    + "Any factual answer must quote its exact supporting passage from ownAccessibleMemory in sourceQuote. With no supporting fact, use an acknowledgement/refusal and an empty quote. "
                    + "privacy and existencePrivacy are public, personal, intimate, or secret; never classify a confidence as public merely because you know it. "
                    + "Feeling and personalStake use only 0,25,50,75,100. Supply reactionEvidenceQuote copied from your received message or independentEvidence for feeling, "
                    + "and concernEvidenceQuote from independentEvidence for a personal stake above ordinary news (25). Never invent attachments. "
                    + "beliefEvidence entries are {kind:'inconsistent|exposed_liar|corroborated|disproved|confirmed',sourceQuote,sourceRootId}; "
                    + "quote exact independentEvidence entries and copy their sourceRootId; never use the received report itself. Conclusive disproof requires personal knowledge; corroboration/confirmation requires independent evidence. "
                    + PersonalGossipSignalContract;
            }
            var request = GossipFields("campaignId", campaign, "heroStringId", speaker, "requestType", "personal_gossip", "maxTokens", 1800,
                "temperature", .65, "messages", new List<Dictionary<string, object>> {
                    GossipFields("role", "system", "content", instruction), GossipFields("role", "user", "content", Json.Serialize(brief)) },
                "response_format", GossipFields("type", "json_object"));
            var response = respond(request);
            if (!ReadBool(response, "ok", false)) return GossipFields("error", ReadString(response, "error", "Provider failed."));
            var draft = TryParseJsonObject(ReadString(response, "content", "")) ?? GossipFields("error", "Malformed gossip JSON.");
            draft.Remove(GossipCompositionApprovalKey);
            return reply ? draft : ReviewPersonalGossipComposition(data, draft, respond);
        }

        private static bool ValidatePersonalGossipComposition(ReignDbConnection c, Dictionary<string, object> data,
            Dictionary<string, object> result, bool reply)
        {
            string body = ReadString(result, "body", ""), quote = ReadString(result, "sourceQuote", "");
            if (result == null || result.ContainsKey("error") || string.IsNullOrWhiteSpace(body)) return false;
            if (!reply)
            {
                if (ReadString(data, "type", "") == "impression")
                {
                    // A vague fallback is not a second opportunity to disclose the original
                    // confidence. No private source text reaches this bounded wording.
                    result["body"] = PersonalGossipImpressionBody(data);
                    result["account"] = "firsthand";
                    return ReadDouble(data, "delta", 0) != 0 && ReadString(data, "distortion", "none") == "none";
                }
                string source = ReadString(data, "sourceQuote", ""), severity = ReadString(data, "distortion", "none");
                if (severity != "none" && (!PersonalGossipCompositionApproved(data, result)
                    || PersonalGossipOriginalSourceFrame(data) == null || !PersonalGossipNamesSubject(data, body)
                    || PersonalGossipRebindsSourceAddressee(data, body))) return false;
                if (PersonalGossipNeedsQuestionComposition(data))
                {
                    if (!PersonalGossipCompositionApproved(data, result) || PersonalGossipOriginalSourceFrame(data) == null
                        || !PersonalGossipNamesSubject(data, body) || PersonalGossipRebindsSourceAddressee(data, body)) return false;
                }
                else if (PersonalGossipNeedsSourceFraming(data))
                {
                    var framed = PersonalGossipFramedSource(data);
                    if (framed == null || body != ReadString(framed, "body", "")
                        || ReadString(result, "account", "") != ReadString(framed, "account", "")) return false;
                }
                if (ReadString(data, "claimKind", "") == "question" && !PersonalGossipSubstantiveQuestion(body)) return false;
                if (ReadBool(data, "uncertain", false))
                {
                    body = "I heard this, though I cannot be sure: " + body;
                    result["body"] = body;
                    result["account"] = "secondhand";
                }
                return quote == source && ReadString(result, "distortionSeverity", "") == severity
                    && PersonalGossipOutcomeSupported(body, source, severity)
                    && GossipBodyAllowed(body, source, severity)
                    && (ReadInt(data, "hop", 1) == 1 || ReadString(result, "account", "secondhand") != "firsthand");
            }
            string memory = ReadString(data, "recipientMemory", ""), kind = ReadString(result, "answerKind", "acknowledged");
            if (kind == "answered" && !GossipQuoteSupported(quote, memory)) return false;
            if (quote.Length > 0 && !GossipQuoteSupported(quote, memory)) return false;
            if (quote.Length == 0)
            {
                result["body"] = kind == "refused" ? "I would rather keep that to myself."
                    : kind == "evaded" ? "Let us leave that there." : "I will think about what you said.";
                result["answerKind"] = kind == "refused" || kind == "evaded" ? kind : "acknowledged";
                return true;
            }
            if (!GossipBodyAllowed(body, quote + "\n" + ReadString(data, "receivedBody", ""), "none")) return false;
            var self = ReadDictionary(data, "speaker");
            var recipient = ReadDictionary(data, "recipient");
            var traits = ReadDictionary(data, "traits");
            var people = ReadDictionaryList(GossipSnapshot(c, ReadString(data, "timelineId", "main")), "people");
            double affinity = GossipAffinity(c, GossipHeroId(self), GossipHeroId(recipient), self, recipient);
            string confidencePlayer = ReadString(data, "playerId", "");
            var confidenceEvidence = ReadDictionaryList(data, "replyEvidence")
                .Where(e => GossipQuoteSupported(quote, ReadString(e, "text", ""))).ToList();
            var confidenceMentions = confidenceEvidence.SelectMany(e => GossipMentionedIds(quote,
                people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), ReadString(e, "speakerId", ""))), self, people));
            var confidenceMetadata = new Dictionary<string, object>(result) { ["evidenceQuote"] = quote };
            result["confidenceOwnerIds"] = GossipResolveConfidenceOwners(confidenceMetadata, GossipHeroId(self), confidencePlayer,
                confidenceMentions, string.Join("\n", confidenceEvidence.Where(e => GossipSameId(ReadString(e, "speakerId", ""), confidencePlayer))
                    .Select(e => ReadString(e, "text", ""))));
            double bond = GossipConfidenceBond(c, self, people, ReadStringList(result, "confidenceOwnerIds"), confidencePlayer);
            int chance = GossipDetailChance(traits, ReadString(result, "privacy", "personal"), GossipFeeling(result, "feeling"), affinity, bond,
                ReadBool(result, "ownCompromising", false), ReadBool(result, "promisedSecrecy", false), 0);
            chance = GossipAdjustedChance(chance, "disclosure", ReadDictionary(data, "policy"));
            if (quote.Length > 0 && GossipDecisionRoll(data, ReadString(data, "messageId", ""), "reply_disclosure") > chance)
            {
                // A refusal contains no confidence; it does not invite another recipient or provider attempt.
                result["body"] = "I would rather keep that to myself.";
                result["sourceQuote"] = ""; result["answerKind"] = "refused"; result["gossipSignals"] = new List<object>();
            }
            return true;
        }

        private static string PersonalGossipImpressionBody(Dictionary<string, object> data) =>
            "My conversation with " + ReadString(ReadDictionary(data, "playerReference"), "label", "the traveler") + " left me with a "
            + (ReadDouble(data, "delta", 0) > 0 ? "good" : "bad") + " impression.";

        private static void PreparePersonalGossipReply(ReignDbConnection c, string campaign, Dictionary<string, object> row, Dictionary<string, object> data)
        {
            string speaker = ReadString(data, "speakerId", ""), player = ReadString(data, "playerId", "");
            double day = ReadDouble(data, "currentDay", 0);
            data["replyEvidence"] = QuerySql(c, @"SELECT t.turn_id,t.text,t.speaker_id,t.session_id,t.payload_json FROM conversation_turns t
JOIN conversation_sessions s ON s.session_id=t.session_id WHERE t.status='active' AND t.world_day<=$day AND t.channel<>'npc_gossip' AND "
                + MemorySessionObserverSql("s", "t") + " ORDER BY t.ts DESC,t.turn_order DESC LIMIT 60;", GossipFields("npc", speaker, "day", day))
                .Where(r => { var p = TryParseJsonObject(ReadString(r, "payload_json", "{}"));
                    return p == null || !p.ContainsKey("timelineId") || GossipTimeline(p) == ReadString(data, "timelineId", "main"); })
                .Select(r => GossipFields("sourceId", ReadString(r, "turn_id", ""), "sourceRootId", "conversation:" + ReadString(r, "session_id", ""),
                    "speakerId", ReadString(r, "speaker_id", ""), "text", ReadString(r, "text", ""))).ToList();
            var memory = new StringBuilder();
            var selectedEvidence = new List<Dictionary<string, object>>();
            foreach (var evidence in ReadDictionaryList(data, "replyEvidence"))
            {
                string line = Json.Serialize(PersonalGossipReplyEvidenceBrief(data, evidence));
                if (memory.Length + line.Length > 9000) continue;
                memory.AppendLine(line); selectedEvidence.Add(evidence);
            }
            data["replyEvidence"] = selectedEvidence;
            data["recipientMemory"] = memory.ToString();
            data["traits"] = ReadJsonObject(CharacterFile(campaign, speaker, "traits.json"));
        }

        private static Dictionary<string, object> PersonalGossipReplyEvidenceBrief(Dictionary<string, object> data, Dictionary<string, object> evidence) =>
            GossipFields("sourceId", ReadString(evidence, "sourceId", ""), "sourceRootId", ReadString(evidence, "sourceRootId", ""),
                "speaker", GossipSameId(ReadString(evidence, "speakerId", ""), ReadString(data, "speakerId", "")) ? "me" : "an earlier interlocutor",
                "text", ReadString(evidence, "text", ""));

        private static bool GossipCommunicationAvailable(Dictionary<string, object> person) => person != null
            && ReadBool(person, "isAlive", false) && !ReadBool(person, "isPrisoner", true)
            && ReadBool(person, "available", false);

        private static void CancelUnavailablePersonalGossip(ReignDbConnection c, Dictionary<string, object> row,
            Dictionary<string, object> data, string reason, Dictionary<string, object> person, double day)
        {
            data["cancellationReason"] = reason; data["cancelledDay"] = day;
            data["cancellationPresence"] = person ?? new Dictionary<string, object>();
            data.Remove("recipientMemory"); data.Remove("replyEvidence"); data.Remove("readyNonce");
            ExecuteSql(c, "UPDATE personal_gossip_messages SET status='cancelled_unavailable',lease_token='',lease_until=0,payload_json=$json WHERE message_id=$id;",
                GossipFields("id", ReadString(row, "message_id", ""), "json", Json.Serialize(data)));
        }

        private static int DeliverPersonalGossip(ReignDbConnection c, string campaign, string timeline, double day)
        {
            int delivered = 0;
            var snapshot = GossipSnapshot(c, timeline);
            var people = ReadDictionaryList(snapshot, "people");
            ExecuteSql(c, @"UPDATE personal_gossip_messages SET status='expired' WHERE timeline_id=$timeline AND expires_day<$day
AND status IN ('pending','generating','awaiting_native_clock','in_transit');", GossipFields("timeline", timeline, "day", day));
            ExecuteSql(c, "UPDATE personal_gossip_messages SET status='generation_failed' WHERE status='generating' AND attempts>=3 AND lease_until<$now;",
                GossipFields("now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            foreach (var row in QuerySql(c, @"SELECT * FROM personal_gossip_messages WHERE timeline_id=$timeline AND status='in_transit'
AND due_day<=$day AND expires_day>=$day ORDER BY due_day,message_id LIMIT 32;", GossipFields("timeline", timeline, "day", day)))
            {
                var data = GossipJson(row);
                string recipient = ReadString(row, "recipient_id", ""), speaker = ReadString(row, "speaker_id", "");
                var current = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), recipient));
                if (current == null && !ReadBool(snapshot, "rosterComplete", false)) continue;
                bool sourcesActive = GossipSourcesActive(c, data);
                if (!GossipCommunicationAvailable(current) || !sourcesActive)
                {
                    CancelUnavailablePersonalGossip(c, row, data, sourcesActive ? "recipient_unavailable_at_delivery" : "source_inactive", current, day);
                    continue;
                }
                bool reply = ReadInt(row, "is_reply", 0) != 0;
                var source = ReadDictionary(data, "speaker");
                var target = ReadDictionary(data, "recipient");
                var recipientTraits = ReadJsonObject(CharacterFile(campaign, recipient, "traits.json"));
                int credibility = ReadString(data, "claimKind", "") == "question" ? 50
                    : GossipCredibility(GossipAffinity(c, recipient, speaker, target, source), GossipTrait(recipientTraits, "socialTrust"),
                        ReadString(data, "account", "firsthand"), Math.Max(0, ReadInt(row, "hop", 1) - 1), false, false, false);
                data["credibility"] = credibility;
                using (var tx = c.BeginTransaction())
                {
                    StorePersonalGossipHistory(c, row, data, recipient, speaker, day, credibility, false);
                    StorePersonalGossipHistory(c, row, data, speaker, recipient, day, credibility, true);
                    ExecuteSql(c, "UPDATE personal_gossip_messages SET status='delivered',payload_json=$json WHERE message_id=$id;",
                        GossipFields("id", ReadString(row, "message_id", ""), "json", Json.Serialize(data)));
                    if (!reply)
                    {
                        var response = new Dictionary<string, object>(data, StringComparer.OrdinalIgnoreCase);
                        response["speakerId"] = recipient; response["recipientId"] = speaker;
                        response["speaker"] = current; response["recipient"] = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), speaker)) ?? source;
                        response["receivedMessageId"] = ReadString(row, "message_id", "");
                        response["receivedBody"] = ReadString(data, "body", ""); response["reply"] = true;
                        response["sourceQuote"] = ""; response["sourceText"] = ""; response["sourceTurnIds"] = new List<string>();
                        response.Remove("sourceEncounterTurns"); response.Remove("sourceContextOmittedWholeTurns"); response.Remove("compositionReview");
                        response["distortion"] = "none"; response["worldDay"] = day; response["dueDay"] = day;
                        response["expiresDay"] = Math.Max(ReadDouble(row, "expires_day", day), day + 7);
                        response["signal"] = new Dictionary<string, object>(); response["candidateAudit"] = new Dictionary<string, object>();
                        response["campaignId"] = campaign;
                        InsertPersonalGossipMessage(c, response, ReadString(row, "message_id", "") + "_reply", true);
                    }
                    tx.Commit();
                }
                delivered++;
            }
            return delivered;
        }

        private static void StorePersonalGossipHistory(ReignDbConnection c, Dictionary<string, object> row, Dictionary<string, object> data,
            string owner, string peer, double day, int credibility, bool authored)
        {
            string id = ReadString(row, "message_id", "") + "_" + PromptHash(owner).Substring(0, 12).ToLowerInvariant();
            string speaker = ReadString(row, "speaker_id", ""), name = ReadString(ReadDictionary(data, "speaker"), "name", speaker);
            string kind = ReadInt(row, "is_reply", 0) != 0 ? "reply" : ReadString(data, "claimKind", "assertion");
            var reference = ReadDictionary(data, "playerReference");
            bool ownEncounter = GossipSameId(owner, ReadString(reference, "originalObserverId", ""));
            var visibleReference = GossipFields("label", ReadString(reference, "label", "the stranger"),
                "named", ReadBool(reference, "named", false), "identityState", ReadString(reference, "identityState", "encountered_unknown"));
            // Only the communicated claim goes into searchable histories. Root source links,
            // private transcript, recipient traits and the hidden lie flag stay in the job table.
            var visible = GossipFields("channel", "npc_gossip", "messageId", ReadString(row, "message_id", ""),
                "speakerId", speaker, "speakerName", name, "recipientId", ReadString(row, "recipient_id", ""),
                "body", ReadString(data, "body", ""), "claimKind", kind, "credibility", authored || kind == "question" || kind == "impression" ? (object)null : credibility,
                "authored", authored, "local", ReadBool(data, "local", false), "acquiredDay", day,
                "worldDay", day, "timelineId", ReadString(row, "timeline_id", "main"),
                "uncertain", ReadBool(data, "uncertain", false),
                "subjectReference", visibleReference, "subjectIsOwnEncounter", ownEncounter,
                "questionOpen", kind == "question", "feeling", 0, "personalStake", 25,
                "raiseRoll", GossipDecisionRoll(data, ReadString(row, "root_id", "") + "|" + owner, "raise"));
            if (authored && ReadString(data, "distortion", "none") != "none") visible["authorKnowsDistortion"] = true;
            ExecuteSql(c, @"INSERT INTO personal_gossip_history(receipt_id,message_id,root_id,timeline_id,owner_id,peer_id,acquired_day,payload_json)
VALUES($id,$message,$root,$timeline,$owner,$peer,$day,$json) ON CONFLICT(message_id,owner_id) DO NOTHING;",
                GossipFields("id", id, "message", ReadString(row, "message_id", ""), "root", ReadString(row, "root_id", ""),
                    "timeline", ReadString(row, "timeline_id", "main"), "owner", owner, "peer", peer, "day", day, "json", Json.Serialize(visible)));
            string session = id + "_session";
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ExecuteSql(c, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,participants_json,payload_json)
VALUES($id,$campaign,$owner,'','npc_gossip','closed',$ts,$ts,$day,$day,$participants,$json) ON CONFLICT(session_id) DO NOTHING;",
                GossipFields("id", session, "campaign", ReadString(data, "campaignId", ReadString(row, "campaign_id", "")), "owner", owner,
                    "ts", ts, "day", day, "participants", Json.Serialize(new[] { owner }), "json", Json.Serialize(visible)));
            if (QuerySql(c, "SELECT turn_id FROM conversation_turns WHERE turn_id=$id;", GossipFields("id", id)).Count == 0)
                InsertConversationTurn(c, id, session, id, 0, id, "npc", speaker, name,
                    "[NPC gossip; " + (authored ? "you said" : "you heard") + "; " + kind + ", not verified world history"
                        + (authored && ReadString(data, "distortion", "none") != "none" ? "; you deliberately distorted this claim" : "")
                        + "] " + ReadString(data, "body", ""),
                    "npc_gossip", day, ts, "", new List<string> { owner }, visible);
        }

        private static void UpdatePersonalGossipReaction(ReignDbConnection c, string campaign, Dictionary<string, object> replyData,
            Dictionary<string, object> result, double day)
        {
            string original = ReadString(replyData, "receivedMessageId", ""), owner = ReadString(replyData, "speakerId", "");
            var receipt = QuerySql(c, "SELECT * FROM personal_gossip_history WHERE message_id=$id AND owner_id=$owner;", GossipFields("id", original, "owner", owner)).FirstOrDefault();
            if (receipt == null) return;
            var visible = GossipJson(receipt);
            var evidence = ReadDictionaryList(replyData, "replyEvidence");
            var originalMessage = QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE message_id=$id;", GossipFields("id", original)).FirstOrDefault();
            var originalData = GossipJson(originalMessage);
            var acceptedEvidence = ReadDictionaryList(result, "beliefEvidence").Where(e => evidence.Any(s =>
                ReadString(e, "sourceRootId", "") == ReadString(s, "sourceRootId", "")
                && GossipQuoteSupported(ReadString(e, "sourceQuote", ""), ReadString(s, "text", ""))
                && (ReadString(e, "kind", "") != "corroborated" || ReadString(s, "sourceRootId", "") != "conversation:" + ReadString(originalData, "sessionId", ""))
                && (ReadString(e, "kind", "") != "disproved" && ReadString(e, "kind", "") != "confirmed"
                    || GossipSameId(ReadString(s, "speakerId", ""), owner)))).ToList();
            string conclusive = acceptedEvidence.Any(e => ReadString(e, "kind", "") == "disproved") ? "disproved"
                : acceptedEvidence.Any(e => ReadString(e, "kind", "") == "confirmed") ? "confirmed" : "";
            bool Has(string kind) => acceptedEvidence.Any(e => ReadString(e, "kind", "") == kind);
            if (ReadString(originalData, "claimKind", "") == "assertion")
                visible["credibility"] = GossipCredibility(GossipAffinity(c, owner, ReadString(replyData, "recipientId", ""),
                    ReadDictionary(replyData, "speaker"), ReadDictionary(replyData, "recipient")), GossipTrait(ReadDictionary(replyData, "traits"), "socialTrust"),
                    ReadString(originalData, "account", "firsthand"), Math.Max(0, ReadInt(originalData, "hop", 1) - 1), Has("corroborated"), Has("exposed_liar"), Has("inconsistent"), conclusive);
            visible["feeling"] = GossipQuoteSupported(ReadString(result, "reactionEvidenceQuote", ""),
                ReadString(replyData, "receivedBody", "") + "\n" + ReadString(replyData, "recipientMemory", "")) ? GossipFeeling(result, "feeling") : 0;
            visible["personalStake"] = GossipQuoteSupported(ReadString(result, "concernEvidenceQuote", ""), ReadString(replyData, "recipientMemory", ""))
                ? GossipFeeling(result, "personalStake") : 25;
            if (ReadString(result, "answerKind", "") == "answered") visible["questionOpen"] = false;
            ExecuteSql(c, "UPDATE personal_gossip_history SET payload_json=$json WHERE receipt_id=$id;",
                GossipFields("id", ReadString(receipt, "receipt_id", ""), "json", Json.Serialize(visible)));
            // Received stories get one onward opportunity, with the holder's own feelings and traits.
            if (ReadInt(replyData, "hop", 1) >= ReadInt(ReadDictionary(replyData, "policy"), "maximumOutwardHops", 2)) return;
            var originalRow = QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE message_id=$id;", GossipFields("id", original)).FirstOrDefault();
            if (originalRow == null) return;
            var source = GossipJson(originalRow);
            var signal = ReadDictionaryList(result, "gossipSignals").FirstOrDefault(s => ReadDouble(s, "confidence", 0) >= .8
                && GossipQuoteSupported(ReadString(s, "evidenceQuote", ""), ReadString(source, "body", "") + "\n" + ReadString(result, "body", "")));
            if (signal == null) return;
            var topic = new Dictionary<string, object>(source, StringComparer.OrdinalIgnoreCase);
            string id = ReadString(source, "rootId", "") + "_via_" + PromptHash(owner).Substring(0, 12).ToLowerInvariant();
            if (QuerySql(c, "SELECT topic_id FROM personal_gossip_topics WHERE topic_id=$id;", GossipFields("id", id)).Count > 0) return;
            topic["id"] = id; topic["speakerId"] = owner; topic["speaker"] = ReadDictionary(replyData, "speaker");
            topic["policy"] = new Dictionary<string, object>(ReadDictionary(source, "policy"));
            ReadDictionary(topic, "policy")["chanceAdjustments"] = PersonalGossipChanceOptions(LoadSettings());
            topic["receivedMessageId"] = original; topic["sourceQuote"] = ReadString(source, "body", "");
            topic.Remove("sourceEncounterTurns"); topic.Remove("sourceContextOmittedWholeTurns"); topic.Remove("compositionReview");
            // This quotation was addressed to the new holder by the previous holder.
            // Its I/you roles must not inherit the root conversation's participants.
            topic["sourceQuoteSpeakerId"] = ReadString(source, "speakerId", "");
            topic["sourceQuoteSpeakerName"] = ReadString(ReadDictionary(source, "speaker"), "name", "Someone");
            topic["sourceQuoteAddresseeId"] = owner;
            topic["sourceText"] = ReadString(source, "body", "") + "\n" + ReadString(result, "body", "");
            topic["sourceTurnIds"] = new List<string> { ReadString(receipt, "receipt_id", "") };
            topic["rootCreatedDay"] = ReadDouble(source, "rootCreatedDay", ReadDouble(source, "createdDay", day));
            topic["createdDay"] = day; topic["worldDay"] = day; topic["hop"] = ReadInt(source, "hop", 1) + 1;
            topic["signal"] = signal; topic["credibility"] = ReadString(source, "claimKind", "") == "question" ? 50 : ReadInt(visible, "credibility", 50);
            if (ReadString(source, "claimKind", "") == "question") signal["question"] = true;
            // A retelling cannot lower the original confidence's privacy or add travel targets.
            var originalSignal = ReadDictionary(source, "signal");
            foreach (string key in new[] { "privacy", "existencePrivacy", "confidenceOwnerIds", "promisedSecrecy", "ownCompromising" })
                if (originalSignal != null && originalSignal.ContainsKey(key)) signal[key] = originalSignal[key];
            topic["holders"] = ReadStringList(source, "holders").Concat(new[] { owner }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var snapshot = GossipSnapshot(c, ReadString(source, "timelineId", "main"));
            var carriedMentions = GossipMentionedIds(ReadString(source, "body", ""), ReadDictionary(source, "speaker"),
                ReadDictionary(source, "recipient"), ReadDictionaryList(snapshot, "people"));
            // The root validates permission to travel, but a holder cannot use a name
            // withheld from the actual message. New names invented in prose add no permission.
            topic["mentionedIds"] = ReadStringList(source, "mentionedIds").Where(mentionId => carriedMentions.Contains(mentionId, StringComparer.OrdinalIgnoreCase)).ToList();
            topic["presenceComplete"] = ReadBool(snapshot, "rosterComplete", false);
            topic["speaker"] = ReadDictionaryList(snapshot, "people").FirstOrDefault(p => GossipSameId(GossipHeroId(p), owner)) ?? ReadDictionary(replyData, "speaker");
            EvaluatePersonalGossip(c, campaign, topic, ReadDictionaryList(snapshot, "people"));
        }
    }
}
