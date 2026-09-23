using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static bool IsPrivateMentalLayer(string kind) =>
            string.Equals(kind, "belief", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "comprehension", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "interpretation", StringComparison.OrdinalIgnoreCase);

        private static bool TemporaryGuestAlreadySatisfied(Dictionary<string, object> payload, Dictionary<string, object> hero)
        {
            string playerClan = ReadFirstString(payload, "playerClanId", "actorClanId");
            string speakerClan = FirstNonEmpty(ReadString(hero, "clanId", ""), ReadString(payload, "speakerClanId", ""));
            return SpeakerInPlayerParty(payload, hero)
                || (!string.IsNullOrWhiteSpace(playerClan) && KnowledgeIdEquals(playerClan, speakerClan));
        }

        private const string ConversationContinuityContract =
            "CONTINUITY AND CHARACTER PRIORITIES\n"
            + "Established shared intimacy, trust, attachments, promises and important shared experiences are foundational to this relationship. "
            + "Keep them strongly present in your manner and understanding even when the latest topic is unrelated. A recent irritation does not erase them. "
            + "History is not automatic present consent, a romance label, compulsory warmth or permission to invent an event. Preserve genuine disagreement and current boundaries. "
            + "Private beliefs are the named thinker's interpretation, not objective facts or another person's thoughts. A disclosed opinion is something that person said. "
            + "Respond to the meaning of I/we/our and ordinary imperfect phrasing in context. Do not turn a cooperative offer into a recurring lesson about pronouns, ownership or permission. "
            + "Raise a boundary when the proposed act actually conflicts with a current preference or established limit; do not manufacture a grievance from wording. "
            + "Family ties, clan membership, party membership and political authority are separate. Travel with companions is not recruitment or command over their relatives. "
            + "CURRENT facts override historical here/today/tomorrow and old scene plans. Distinguish a suggestion, an accepted intention, a completed narrated action and a confirmed native game effect. "
            + "A family home proves neither current presence nor witnessing. Use local presence evidence at its stated scope; unknown is not absent, town is not room, room is not witness. "
            + "Retain major unresolved concerns privately through unrelated talk. Bring them up naturally when a relevant person, place, news or time creates an opportunity; don't recite a checklist each turn.\n"
            + "Return continuityWrites: [] unless this accepted reply establishes or changes a durable concern, relationship milestone or important episode. "
            + "Each write has id (empty for new), kind (agenda/relationship/episode), subject, peerId, status (open/active/waiting/blocked/deferred/resolved/abandoned/invalidated), "
            + "meaning, nextStep, completionCriterion, triggerPeople, triggerPlaces, parentId (empty unless this is a step of an existing concern), dueWorldDay (0 when unset), importance (0..1), evidenceQuote (exact nonempty excerpt of your visible reply), "
            + "transitionReason, expectedRevision (0 for new). Use the existing id and revision for updates. Omission preserves a record. "
            + "Only your own accepted intention can create your agenda; a player's suggestion alone cannot. Do not resolve an emotional concern merely because travel ended. "
            + "A relationship milestone records only the participants who actually shared that experience, not everyone who later heard about it. "
            + "Completing a step never closes its parent concern. A blocked or deferred concern stays active; update its next step only with accepted new intent. Use episode records for completed narrated activities or settled discussions that must not be proposed again as if new. "
            + "Do not persist guesses about the player's personality, private motives or pronoun habits as milestones. Native effects require their native receipt. ";

        private static bool NormalizeAlreadySatisfiedContinuityGate(Dictionary<string, object> gate,
            Dictionary<string, object> payload, Dictionary<string, object> hero)
        {
            if (gate == null || !TemporaryGuestAlreadySatisfied(payload, hero)) return false;
            string intent = ReadString(gate, "intent", "");
            // Renewals, departure and actual orders retain their existing action gates.
            if (Regex.IsMatch(intent.Replace('_', ' '), @"\b(renew(?:s|ed|ing|al)?|extend(?:s|ed|ing)?|extension|review(?:s|ed|ing)?|end(?:s|ed|ing)?|leav(?:e|es|ing)|dismiss(?:ed|ing)?|release|order|transfer|give|more days|another term)\b", RegexOptions.IgnoreCase)) return false;
            if (!Regex.IsMatch(intent.Replace('_', ' '), @"\b(?:accept temporary party guest|join (?:the |your |player.?s )?party|travel together|accompany (?:the )?player)\b", RegexOptions.IgnoreCase)) return false;
            gate["needed"] = false;
            gate["commitment"] = "none";
            gate["reason"] = "Already satisfied by current clan/party membership; ordinary accompanying dialogue has no recruitment effect.";
            gate["continuityClassification"] = "already_satisfied";
            return true;
        }

        private static void EnsureConversationContinuitySchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_continuity (
record_id TEXT PRIMARY KEY, timeline_id TEXT NOT NULL, owner_id TEXT NOT NULL, peer_id TEXT NOT NULL,
kind TEXT NOT NULL, status TEXT NOT NULL, importance REAL NOT NULL, revision INTEGER NOT NULL,
source_event_id TEXT NOT NULL, source_turn_id TEXT NOT NULL, updated_ts INTEGER NOT NULL, payload_json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_continuity_owner ON conversation_continuity(timeline_id,owner_id,kind,status);
CREATE TABLE IF NOT EXISTS conversation_continuity_versions (
version_id TEXT PRIMARY KEY, record_id TEXT NOT NULL, source_event_id TEXT NOT NULL,
revision INTEGER NOT NULL, payload_json TEXT NOT NULL, created_ts INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS conversation_scene_handoffs (
handoff_id TEXT PRIMARY KEY, timeline_id TEXT NOT NULL, owner_id TEXT NOT NULL, anchor TEXT NOT NULL,
source_turn_id TEXT NOT NULL, world_day REAL NOT NULL, payload_json TEXT NOT NULL);");
        }

        private static string ContinuityTimeline(Dictionary<string, object> payload) =>
            FirstNonEmpty(ReadString(payload, "timelineId", ""), "main");

        private static bool ContinuityClosed(string status) =>
            status == "resolved" || status == "abandoned" || status == "invalidated";

        private static string ContinuityAgendaOpportunity(Dictionary<string, object> record, Dictionary<string, object> payload)
        {
            var opportunities = new List<string>();
            string place = ReadFirstString(payload, "locationId", "settlementId");
            if (ReadStringList(record, "triggerPlaces").Contains(place, StringComparer.OrdinalIgnoreCase)) opportunities.Add("relevant place reached");
            var present = ReadDictionaryList(CurrentContinuityPresence(payload), "people")
                .Where(p => ReadString(p, "settlementPresence", "") == "present" && ReadBool(p, "alive", true))
                .Select(p => ReadString(p, "heroStringId", "")).ToList();
            if (ReadStringList(record, "triggerPeople").Any(p => present.Contains(p, StringComparer.OrdinalIgnoreCase))) opportunities.Add("relevant person locally present (access not guaranteed)");
            double due = ReadDouble(record, "dueWorldDay", 0d);
            if (due > 0 && ReadDouble(payload, "worldDay", 0d) >= due) opportunities.Add("agreed time reached");
            return opportunities.Count == 0 ? "none established; retain concern without repeating it" : string.Join("; ", opportunities) + "; opportunity is not completion or consent";
        }

        private static string ContinuitySceneAnchor(Dictionary<string, object> payload, Dictionary<string, object> participant)
        {
            var native = ReadDictionary(payload, "localPresence");
            return PromptHash(ContinuityTimeline(payload) + "|" + ReadString(payload, "locationId", "") + "|"
                + ReadString(native, "roomId", "") + "|" + ReadString(payload, "playerPartyId", "") + "|"
                + ReadString(participant, "nativeLocationDescription", "") + "|"
                + ReadDouble(native, "partyPositionX", 0).ToString("0.00", CultureInfo.InvariantCulture) + "|"
                + ReadDouble(native, "partyPositionY", 0).ToString("0.00", CultureInfo.InvariantCulture));
        }

        private static Dictionary<string, object> LoadSceneContinuityHandoff(ReignDbConnection connection,
            Dictionary<string, object> payload, Dictionary<string, object> participant)
        {
            EnsureConversationContinuitySchema(connection);
            string owner = ReadString(participant, "heroStringId", "");
            string timeline = ContinuityTimeline(payload);
            var row = QuerySql(connection, "SELECT * FROM conversation_scene_handoffs WHERE handoff_id=$id;",
                new Dictionary<string, object> { ["id"] = timeline + ":" + owner }).FirstOrDefault();
            if (row == null) return null;
            if (ReadString(row, "anchor", "") != ContinuitySceneAnchor(payload, participant)
                || ReadDouble(payload, "worldDay", 0d) < ReadDouble(row, "world_day", 0d))
            {
                ExecuteSql(connection, "DELETE FROM conversation_scene_state WHERE hero_id=$id;",
                    new Dictionary<string, object> { ["id"] = owner });
                return new Dictionary<string, object>();
            }
            return TryParseJsonObject(ReadString(row, "payload_json", "{}"));
        }

        private static void SaveSceneContinuityHandoffs(ReignDbConnection connection, Dictionary<string, object> payload,
            List<Dictionary<string, object>> participants, string turnId, string speakerId, string playerText, string reply)
        {
            EnsureConversationContinuitySchema(connection);
            foreach (var participant in participants)
            {
                string owner = ReadString(participant, "heroStringId", "");
                var row = QuerySql(connection, "SELECT * FROM conversation_scene_state WHERE hero_id=$id;",
                    new Dictionary<string, object> { ["id"] = owner }).FirstOrDefault();
                string timeline = ContinuityTimeline(payload);
                var old = QuerySql(connection, "SELECT * FROM conversation_scene_handoffs WHERE handoff_id=$id;",
                    new Dictionary<string, object> { ["id"] = timeline + ":" + owner }).FirstOrDefault();
                var previous = TryParseJsonObject(ReadString(old, "payload_json", "{}"));
                row = row ?? (ReadString(old, "anchor", "") == ContinuitySceneAnchor(payload, participant)
                    ? new Dictionary<string, object>(previous ?? new Dictionary<string, object>()) : new Dictionary<string, object>());
                var exchanges = ReadDictionaryList(previous, "acceptedExchanges").Where(e => ReadString(e, "turnId", "") != turnId).ToList();
                exchanges.Add(new Dictionary<string, object> { ["turnId"] = turnId, ["speakerId"] = speakerId,
                    ["playerText"] = playerText ?? "", ["reply"] = reply ?? "", ["worldDay"] = ReadDouble(payload, "worldDay", 0d),
                    ["sessionId"] = ReadFirstString(payload, "conversationSessionId", "sessionId", "eventId") });
                row["acceptedExchanges"] = exchanges.Skip(Math.Max(0, exchanges.Count - 2)).ToList();
                ExecuteSql(connection, @"INSERT OR REPLACE INTO conversation_scene_handoffs
(handoff_id,timeline_id,owner_id,anchor,source_turn_id,world_day,payload_json) VALUES($id,$timeline,$owner,$anchor,$turn,$day,$payload);",
                    new Dictionary<string, object> { ["id"] = timeline + ":" + owner, ["timeline"] = timeline, ["owner"] = owner,
                        ["anchor"] = ContinuitySceneAnchor(payload, participant), ["turn"] = turnId,
                        ["day"] = ReadDouble(payload, "worldDay", 0d), ["payload"] = Json.Serialize(row) });
            }
        }

        // Only the accepted-turn path calls this writer. The quote is checked against
        // that exact reply, ownership is server supplied, and revisions reject stale
        // parallel preparations. No native action or relationship score is replayed.
        private static Dictionary<string, object> StoreAcceptedConversationContinuity(
            ReignDbConnection connection, Dictionary<string, object> payload, string ownerId, string peerId,
            string eventId, string acceptedReply, List<Dictionary<string, object>> writes, long ts)
        {
            if (writes == null || writes.Count == 0) return new Dictionary<string, object> { ["applied"] = new List<string>(), ["rejected"] = new List<object>() };
            EnsureConversationContinuitySchema(connection);
            string timeline = ContinuityTimeline(payload);
            var applied = new List<string>();
            var rejected = new List<Dictionary<string, object>>();
            foreach (var write in writes ?? new List<Dictionary<string, object>>())
            {
                string kind = ReadString(write, "kind", "").ToLowerInvariant();
                string quote = ReadString(write, "evidenceQuote", "").Trim();
                string subject = ReadString(write, "subject", "").Trim();
                string status = ReadString(write, "status", "open").ToLowerInvariant();
                string id = ReadString(write, "id", "").Trim();
                string pair = ReadString(write, "peerId", "");
                string reason = "";
                if (!new[] { "agenda", "relationship", "episode" }.Contains(kind)) reason = "invalid_kind";
                else if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(quote)
                    || quote.Length < 12 || (acceptedReply ?? "").IndexOf(quote, StringComparison.Ordinal) < 0) reason = "missing_accepted_source_quote";
                else if (!new[] { "open", "active", "waiting", "blocked", "deferred", "resolved", "abandoned", "invalidated" }.Contains(status)) reason = "invalid_status";
                else if (kind == "relationship" && (string.IsNullOrWhiteSpace(peerId) || !KnowledgeIdEquals(pair, peerId))) reason = "unverified_relationship_participant";
                else if (ContinuityClosed(status) && string.IsNullOrWhiteSpace(ReadString(write, "transitionReason", ""))) reason = "transition_requires_reason";
                else if (IsUnsupportedWordingJudgment(subject + " " + ReadString(write, "meaning", ""))) reason = "generated_wording_judgment";
                string parentId = ReadString(write, "parentId", "");
                if (!string.IsNullOrWhiteSpace(parentId)
                    && QuerySql(connection, "SELECT record_id FROM conversation_continuity WHERE record_id=$id AND owner_id=$owner AND timeline_id=$timeline AND kind='agenda';",
                        new Dictionary<string, object> { ["id"] = parentId, ["owner"] = ownerId, ["timeline"] = timeline }).Count == 0) reason = "unverified_parent_concern";
                if (string.IsNullOrWhiteSpace(id)) id = "continuity_" + PromptHash(timeline + "|" + ownerId + "|" + kind + "|" + pair + "|" + NormalizeLookup(subject)).Substring(0, 32);
                var previous = QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id=$id;",
                    new Dictionary<string, object> { ["id"] = id }).FirstOrDefault();
                if (previous != null && (ReadString(previous, "owner_id", "") != ownerId || ReadString(previous, "timeline_id", "") != timeline)) reason = "ownership_mismatch";
                if (previous != null && reason.Length == 0 && ReadString(previous, "source_event_id", "") == eventId) { applied.Add(id); continue; }
                int revision = ReadInt(previous, "revision", 0);
                if (ReadInt(write, "expectedRevision", 0) != revision) reason = "stale_revision";
                if (previous != null && (kind != ReadString(previous, "kind", "") || pair != ReadString(previous, "peer_id", ""))) reason = "record_identity_changed";
                if (previous != null && ContinuityClosed(ReadString(previous, "status", "")) && !ContinuityClosed(status)
                    && string.IsNullOrWhiteSpace(ReadString(write, "transitionReason", ""))) reason = "reopening_requires_new_evidence";
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    rejected.Add(new Dictionary<string, object> { ["id"] = id, ["reason"] = reason });
                    continue;
                }
                var value = new Dictionary<string, object>(TryParseJsonObject(ReadString(previous, "payload_json", "{}"))
                    ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
                foreach (var field in write) if (field.Value != null) value[field.Key] = field.Value;
                foreach (var field in new Dictionary<string, object>
                {
                    ["id"] = id, ["ownerId"] = ownerId, ["timelineId"] = timeline,
                    ["sourceEventId"] = eventId, ["sourceTurnId"] = ReadString(payload, "sceneTurnId", ""),
                    ["worldDay"] = ReadDouble(payload, "worldDay", 0d), ["revision"] = revision + 1,
                    ["evidenceQuote"] = quote, ["status"] = status
                }) value[field.Key] = field.Value;
                string json = Json.Serialize(value);
                var changed = QuerySql(connection, @"INSERT INTO conversation_continuity
(record_id,timeline_id,owner_id,peer_id,kind,status,importance,revision,source_event_id,source_turn_id,updated_ts,payload_json)
VALUES($id,$timeline,$owner,$peer,$kind,$status,$importance,$revision,$event,$turn,$ts,$payload)
ON CONFLICT(record_id) DO UPDATE SET status=excluded.status,importance=excluded.importance,
revision=excluded.revision,source_event_id=excluded.source_event_id,source_turn_id=excluded.source_turn_id,
updated_ts=excluded.updated_ts,payload_json=excluded.payload_json
WHERE conversation_continuity.revision=$expected AND conversation_continuity.owner_id=$owner
AND conversation_continuity.timeline_id=$timeline RETURNING record_id;",
                    new Dictionary<string, object> { ["id"] = id, ["timeline"] = timeline, ["owner"] = ownerId, ["peer"] = pair,
                        ["kind"] = kind, ["status"] = status, ["importance"] = ClampDouble(ReadDouble(write, "importance", 0.5d), 0d, 1d),
                        ["revision"] = revision + 1, ["expected"] = revision, ["event"] = eventId, ["turn"] = ReadString(payload, "sceneTurnId", ""), ["ts"] = ts, ["payload"] = json });
                if (changed.Count == 0)
                {
                    rejected.Add(new Dictionary<string, object> { ["id"] = id, ["reason"] = "concurrent_revision_changed" });
                    continue;
                }
                ExecuteSql(connection, @"INSERT INTO conversation_continuity_versions(version_id,record_id,source_event_id,revision,payload_json,created_ts)
VALUES($id,$record,$event,$revision,$payload,$ts);", new Dictionary<string, object> { ["id"] = id + "_" + (revision + 1),
                    ["record"] = id, ["event"] = eventId, ["revision"] = revision + 1, ["payload"] = json, ["ts"] = ts });
                applied.Add(id);
            }
            return new Dictionary<string, object> { ["applied"] = applied, ["rejected"] = rejected };
        }

        private static bool IsUnsupportedWordingJudgment(string text) => Regex.IsMatch(text ?? "",
            @"\b(?:learn(?:ed|t|ing)?\s+to\s+say\s+we|(?:uses?|says?|said|saying|word|pronoun)\s+['""‘’“”]?(?:I|we|our)['""‘’“”]?\s+(?:instead|rather)|(?:pronouns?|wording)\s+(?:prove|reveal|show)|speaks?\s+(?:of|as)\s+ownership)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static string BuildProtectedConversationContinuity(string campaignId, string heroId,
            Dictionary<string, object> payload, Dictionary<string, object> state)
        {
            var text = new StringBuilder(ConversationContinuityContract);
            bool official = ReadBool(payload, "officialMemoryFirewall", false);
            string peer = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            string timeline = ContinuityTimeline(payload);
            var sourceIds = new List<string>();
            var agency = ReadDictionary(payload, "conversationAgencyContext");
            if (agency != null)
            {
                text.AppendLine().Append(ConversationAgencyPrompt(agency));
                sourceIds.AddRange(ReadDictionaryList(agency, "records").Select(r => ReadString(r, "id", "")));
            }
            if (!official && !string.IsNullOrWhiteSpace(campaignId) && !string.IsNullOrWhiteSpace(heroId))
            {
                using (var connection = OpenCampaignConnection(campaignId))
                {
                    EnsureConversationContinuitySchema(connection);
                    text.Append(BuildEstablishedRelationshipContinuity(connection, campaignId, timeline, heroId, peer, sourceIds, ReadDouble(payload,"worldDay",0d)));
                    var records = QuerySql(connection, @"SELECT * FROM conversation_continuity WHERE timeline_id=$timeline AND owner_id=$owner
AND status<>'invalidated' AND (kind<>'agenda' OR status NOT IN ('resolved','abandoned')) AND (kind<>'relationship' OR peer_id=$peer)
ORDER BY CASE WHEN kind='relationship' THEN 0 WHEN kind='agenda' THEN 1 ELSE 2 END,importance DESC,updated_ts DESC;",
                        new Dictionary<string, object> { ["timeline"] = timeline, ["owner"] = heroId, ["peer"] = peer });
                    string section = "";
                    foreach (var row in records)
                    {
                        var record = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                        if (ReadDouble(record, "worldDay", 0d) > ReadDouble(payload, "worldDay", 0d)) continue;
                        string kind = ReadString(row, "kind", "");
                        if (kind != section)
                        {
                            section = kind;
                            text.AppendLine().AppendLine(kind == "relationship" ? "FOUNDATIONAL SHARED RELATIONSHIP"
                                : kind == "agenda" ? "ONGOING CONCERNS AND INTENTIONS" : "ESTABLISHED IMPORTANT EPISODES");
                        }
                        string id = ReadString(row, "record_id", "");
                        sourceIds.Add(id);
                        text.Append("- id=").Append(id).Append(" revision=").Append(ReadInt(row, "revision", 0))
                            .Append(" status=").Append(ReadString(row, "status", ""))
                            .Append("; ").Append(ReadString(record, "subject", ""))
                            .Append("; personal meaning: ").Append(ReadString(record, "meaning", ""))
                            .Append("; next step: ").Append(ReadString(record, "nextStep", ""))
                        .Append("; completion: ").Append(ReadString(record, "completionCriterion", ""))
                            .Append("; parent: ").Append(ReadString(record, "parentId", "none"))
                            .Append("; opportunity now: ").Append(ContinuityAgendaOpportunity(record, payload))
                            .Append("; people: ").Append(string.Join(", ", ReadStringList(record, "triggerPeople")))
                            .Append("; places: ").Append(string.Join(", ", ReadStringList(record, "triggerPlaces")))
                            .Append("; source=").Append(ReadString(row, "source_event_id", ""))
                            .Append("; accepted evidence: ").AppendLine(ReadString(record, "evidenceQuote", ""));
                    }
                    var handoff = QuerySql(connection, "SELECT * FROM conversation_scene_handoffs WHERE handoff_id=$id;",
                        new Dictionary<string, object> { ["id"] = timeline + ":" + heroId }).FirstOrDefault();
                    if (handoff != null && ReadDouble(handoff, "world_day", 0d) <= ReadDouble(payload, "worldDay", 0d))
                    {
                        var handoffValue = TryParseJsonObject(ReadString(handoff, "payload_json", "{}"));
                        var exchanges = ReadDictionaryList(handoffValue, "acceptedExchanges");
                        if (exchanges.Count > 0) text.AppendLine("LAST ACCEPTED SCENE HANDOFF — historical source, not a fresh proposal. Preserve completed discussion; interpret yesterday/tomorrow relative to the source day. Native movement and the current day remain authoritative.");
                        foreach (var exchange in exchanges)
                        {
                            string id = ReadString(exchange, "turnId", "");
                            sourceIds.Add(id);
                            text.Append("- turn=").Append(id).Append("; source day=").Append(ReadDouble(exchange, "worldDay", 0d).ToString("0.#####", CultureInfo.InvariantCulture))
                                .Append("; session=").AppendLine(ReadString(exchange, "sessionId", ""))
                                .Append("  Player: ").AppendLine(ReadString(exchange, "playerText", ""))
                                .Append("  ").Append(ReadString(exchange, "speakerId", "NPC")).Append(": ").AppendLine(ReadString(exchange, "reply", ""));
                        }
                    }
                }
                // Existing campaigns retain their unresolved immediate concern while
                // the reviewed migration reconstructs durable records from sources.
                string crisis = ReadString(state, "currentCrisis", "");
                if (!string.IsNullOrWhiteSpace(crisis)) text.AppendLine("CURRENT UNRESOLVED CONCERN (stored state; do not silently erase): " + crisis);
            }
            text.AppendLine().AppendLine("CURRENT NATIVE SCOPE (family does not confer clan or party authority):")
                .Append("player clan=").Append(ReadString(payload, "playerClanId", "unknown"))
                .Append("; speaker clan=").Append(ReadString(payload, "speakerClanId", "unknown"))
                .Append("; player party=").Append(ReadString(payload, "playerPartyId", "unknown"))
                .Append("; world day=").Append(ReadDouble(payload, "worldDay", 0d).ToString("0.#####", CultureInfo.InvariantCulture))
                .Append("; location=").AppendLine(ReadFirstString(payload, "locationId", "settlementId"));
            text.Append(BuildLocalPresenceContinuityPrompt(payload));
            payload["continuityEvidence"] = new Dictionary<string, object> { ["sourceIds"] = sourceIds,
                ["timelineId"] = timeline, ["ownerId"] = heroId, ["peerId"] = peer, ["projectionHash"] = PromptHash(text.ToString()) };
            payload["protectedContinuityPrompt"] = text.ToString();
            return text.ToString();
        }

        private static string BuildEstablishedRelationshipContinuity(ReignDbConnection connection, string campaignId,
            string timeline, string observer, string peer, List<string> sourceIds, double worldDay)
        {
            if (string.IsNullOrWhiteSpace(observer) || string.IsNullOrWhiteSpace(peer)) return "";
            var parameters = new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timeline,
                ["observer"] = observer, ["peer"] = peer, ["day"] = worldDay };
            var text = new StringBuilder();
            var intimacy = TableExists(connection, "court_social_signal_evidence") ? QuerySql(connection, @"SELECT * FROM court_social_signal_evidence
WHERE campaign_id=$campaign AND timeline_id=$timeline AND accepted=1 AND signal_type='sexual_intimacy_completed'
AND ((speaker_id=$observer AND target_id=$peer) OR (speaker_id=$peer AND target_id=$observer))
ORDER BY created_ts ASC;", parameters) : new List<Dictionary<string, object>>();
            // The underlying accepted signal store remains authoritative. This is
            // a read projection, never another relationship score or romance tag.
            if (intimacy.Count > 0)
            {
                text.AppendLine().AppendLine("FOUNDATIONAL SHARED INTIMACY — ACCEPTED PAIR EVIDENCE")
                    .AppendLine("This pair has established shared intimacy. Preserve that familiarity through unrelated topics. It does not predetermine today's consent, exclusivity or formal relationship label.");
                foreach (var row in intimacy.GroupBy(r => ReadString(r, "exchange_id", "")).Select(g => g.First()))
                {
                    string id = ReadString(row, "signal_id", "");
                    sourceIds.Add(id);
                    text.Append("- source=").Append(id).Append("; exchange=").Append(ReadString(row, "exchange_id", ""))
                        .Append("; participants=").Append(observer).Append(",").Append(peer)
                        .Append("; accepted quote: ").AppendLine(ReadString(row, "supporting_quote", ""));
                }
            }
            var milestones = TableExists(connection, "relationship_milestones") ? QuerySql(connection, @"SELECT * FROM relationship_milestones
WHERE subject_id=$observer AND target_id=$peer AND status='active' ORDER BY stability DESC,updated_ts DESC;", parameters) : new List<Dictionary<string, object>>();
            foreach (var row in milestones)
            {
                string id = ReadString(row, "milestone_id", "");
                sourceIds.Add(id);
                text.Append("- Existing relationship milestone [").Append(id).Append("]: ")
                    .Append(ReadString(row, "kind", "")).Append("; ").Append(ReadString(row, "reason", ""))
                    .Append("; original source=").Append(ReadString(row, "created_event_id", ""))
                    .Append("; latest source=").AppendLine(ReadString(row, "last_event_id", ""));
            }
            var receipts = TableExists(connection, "conversation_relationship_receipts") ? QuerySql(connection, @"SELECT * FROM conversation_relationship_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND observer_id=$observer AND target_id=$peer
AND status='applied' AND severity_tier<>'routine' ORDER BY created_ts;", parameters) : new List<Dictionary<string, object>>();
            foreach (var row in receipts.Where(r => !IsUnsupportedWordingJudgment(ReadString(r, "summary", "")))
                .GroupBy(r => ReadString(r, "act_kind", "") + ":" + ReadString(r, "valence", ""))
                .SelectMany(g => new[] { g.First(), g.Last() }).GroupBy(r => ReadString(r, "receipt_id", "")).Select(g => g.First()))
            {
                string id = ReadString(row, "receipt_id", "");
                sourceIds.Add(id);
                text.Append("- Significant shared conduct [").Append(id).Append("; ").Append(ReadString(row, "valence", ""))
                    .Append("]: ").Append(ReadString(row, "summary", "")).Append("; accepted conduct quote: ")
                    .AppendLine(ReadString(row, "current_conduct_quote", ""));
            }
            // Consolidation is not forgetting. These older, explicitly tagged
            // personal memories still have an accepted source in this exact pair's
            // history. Ordinary topical retrieval used to discard them after a
            // summary replaced the active rows, losing quiet care and closeness.
            bool hasAcceptedHistory = TableExists(connection, "conversation_turns") && TableExists(connection, "conversation_sessions");
            var sharedMemories = hasAcceptedHistory && TableExists(connection, "memories") ? QuerySql(connection, @"SELECT m.*,t.turn_id AS accepted_turn_id,t.world_day AS accepted_world_day,
t.session_id AS accepted_session_id FROM memories m JOIN conversation_turns t ON t.event_id=m.event_id
JOIN conversation_sessions s ON s.session_id=t.session_id
WHERE m.owner_id=$observer AND m.status IN ('active','consolidated') AND t.speaker_id=$observer
AND t.role='npc' AND t.status='active' AND s.player_id=$peer AND t.world_day<=$day
AND (lower(m.tags_json) LIKE '%intima%' OR lower(m.tags_json) LIKE '%milestone%'
 OR lower(m.tags_json) LIKE '%vulnerability%' OR lower(m.tags_json) LIKE '%trust%'
 OR lower(m.tags_json) LIKE '%betray%' OR lower(m.tags_json) LIKE '%boundary%') ORDER BY t.ts,m.ts;", parameters) : new List<Dictionary<string, object>>();
            foreach (var row in sharedMemories.GroupBy(r => ReadString(r,"memory_id","")).Select(g=>g.First()))
            {
                var memoryPayload = TryParseJsonObject(ReadString(row,"payload_json","{}"));
                var participants = TextListFromJson(ReadString(row,"participants_json","[]"))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (ReadBool(memoryPayload,"continuityQuarantined",false)
                    || IsUnsupportedWordingJudgment(ReadString(row,"summary",""))
                    || participants.Count != 2 || !participants.Contains(observer,StringComparer.OrdinalIgnoreCase)
                    || !participants.Contains(peer,StringComparer.OrdinalIgnoreCase)
                    || !KnowledgeRowVisibleToNpc("memories",row,new KnowledgeAccessContext { NpcId=observer })) continue;
                string id = ReadString(row,"memory_id","");
                sourceIds.Add(id);
                text.Append("- FOUNDATIONAL PAIR EXPERIENCE [").Append(id).Append("; accepted source=")
                    .Append(ReadString(row,"accepted_turn_id","")).Append("; historical day=")
                    .Append(ReadDouble(row,"accepted_world_day",0d).ToString("0.#####",CultureInfo.InvariantCulture))
                    .Append("; observer=").Append(observer).Append("; peer=").Append(peer)
                    .Append("]: ").AppendLine(ReadString(row,"summary",""));
            }
            // Older campaigns did not reliably emit intimacy signals. Preserve
            // explicit relational words from this NPC's own accepted pair history,
            // with the source exchange intact. They are attributed history, never
            // another NPC's private interpretation or a new score/consent decision.
            var prior = hasAcceptedHistory ? QuerySql(connection, @"SELECT t.* FROM conversation_turns t
JOIN conversation_sessions s ON s.session_id=t.session_id
WHERE t.speaker_id=$observer AND t.role='npc' AND t.status='active' AND s.player_id=$peer AND t.world_day<=$day
AND (lower(t.text) LIKE '%kiss%' OR lower(t.text) LIKE '%intima%' OR lower(t.text) LIKE '%slept together%'
 OR lower(t.text) LIKE '%our night%' OR lower(t.text) LIKE '%trust you%' OR lower(t.text) LIKE '%betrayed me%'
 OR lower(t.text) LIKE '%our partnership%' OR lower(t.text) LIKE '%shared a bed%') ORDER BY t.ts,t.turn_order;", parameters) : new List<Dictionary<string, object>>();
            var dimensions = prior.GroupBy(r => Regex.IsMatch(ReadString(r, "text", ""), @"\b(kiss\w*|intima\w*|slept together|our night|shared a bed)\b", RegexOptions.IgnoreCase)
                ? "physical affection and boundaries" : "trust, boundaries and partnership");
            foreach (var dimension in dimensions)
            {
                // First and latest complete exchanges show foundation and evolution.
                // The full source index is explicit; all originals remain retrievable.
                var allIds = dimension.Select(r => ReadString(r, "turn_id", "")).ToList();
                text.AppendLine().AppendLine("FOUNDATIONAL PAIR HISTORY — " + dimension.Key + ". These are this NPC's historical words. Preserve who actually participated, any refusal and uncertainty; a reference to somebody else's experience does not make it this pair's experience. Past intimacy does not establish today's consent or a formal romance label. Historical sex, clan, party and location claims never override CURRENT native identity and membership.");
                text.AppendLine("Source index: " + string.Join(",", allIds));
                sourceIds.AddRange(allIds);
                foreach (var row in new[] { dimension.First(), dimension.Last() }.GroupBy(r => ReadString(r, "turn_id", "")).Select(g => g.First()))
                {
                    string exchange = ReadString(row, "exchange_id", "");
                    var turns = exchange.Length == 0 ? new List<Dictionary<string, object>> { row } : QuerySql(connection,
                        "SELECT * FROM conversation_turns WHERE session_id=$session AND exchange_id=$exchange AND status='active' ORDER BY turn_order;",
                        TestDict("session", ReadString(row,"session_id",""), "exchange", exchange));
                    text.Append("HISTORICAL session=").Append(ReadString(row,"session_id", "")).Append("; source day=")
                        .Append(ReadDouble(row,"world_day",0d).ToString("0.#####",CultureInfo.InvariantCulture)).AppendLine(". Listed speakers heard this exchange; that is not evidence they witnessed events being described.");
                    foreach (var turn in turns) text.AppendLine(RenderExactHistoryTurn(turn, new KnowledgeAccessContext { NpcId = observer }));
                }
            }
            return text.ToString();
        }

        private static Dictionary<string, object> CurrentContinuityPresence(Dictionary<string, object> payload)
        {
            if (ReadString(payload, "mode", ReadString(payload, "interactionMode", "")) == "correspondence") return null;
            var presence = ReadDictionary(payload, "localPresence");
            if (presence == null || ReadString(presence, "schema", "") != "reign-local-presence-v1") return null;
            double day = ReadDouble(payload, "worldDay", -1d), captured = ReadDouble(presence, "worldDay", -2d);
            string location = ReadFirstString(payload, "locationId", "settlementId");
            // This is a request snapshot, not a cache. Paused game time remains valid;
            // movement, time reversal and a snapshot older than one game minute do not.
            if (double.IsNaN(day) || double.IsNaN(captured) || double.IsInfinity(day) || double.IsInfinity(captured)
                || day < captured || day - captured > 1d / 1440d
                || (!string.IsNullOrWhiteSpace(location) && location != ReadString(presence, "settlementId", ""))) return null;
            return presence;
        }

        private static string BuildLocalPresenceContinuityPrompt(Dictionary<string, object> payload)
        {
            var presence = CurrentContinuityPresence(payload);
            if (presence == null) return "Local presence snapshot unavailable. Do not assert unlisted people's attendance or absence.\n";
            var text = new StringBuilder("LOCAL PRESENCE: ");
            text.Append("settlement=").Append(ReadString(presence, "settlementId", "unknown"))
                .Append("; room=").Append(ReadString(presence, "roomId", "unknown"))
                .Append("; captured world day=").Append(ReadDouble(presence, "worldDay", 0d).ToString("0.#####", CultureInfo.InvariantCulture))
                .AppendLine(". This is present scope only; it does not establish prior-night attendance. The roster is partial: an omitted name is unknown, never proof of absence. Prisoners may be in town without being accessible.");
            foreach (var person in ReadDictionaryList(presence, "people"))
            {
                // Remote engine locations are intentionally excluded. The model may
                // avoid a false local claim without acquiring omniscient whereabouts.
                text.Append("- ").Append(ReadString(person, "name", "unknown")).Append(" [")
                    .Append(ReadString(person, "heroStringId", "")).Append("]: town=")
                    .Append(ReadString(person, "settlementPresence", "unknown")).Append("; room=")
                    .Append(ReadString(person, "roomPresence", "unknown")).Append("; conversation witness=")
                    .Append(ReadBool(person, "conversationWitness", false) && ReadBool(person, "alive", true) ? "yes" : "not established")
                    .Append("; alive=").Append(ContinuityPresenceFlag(person,"alive"))
                    .Append("; prisoner=").Append(ContinuityPresenceFlag(person,"prisoner")).AppendLine();
            }
            return text.ToString();
        }

        private static string ContinuityPresenceFlag(Dictionary<string,object> person,string key) =>
            person != null && person.TryGetValue(key,out var value) && value != null
                ? (ReadBool(person,key,false) ? "yes" : "no") : "unknown";

        private static List<Dictionary<string, object>> FindCriticalConversationContinuityViolations(
            Dictionary<string, object> parsed, Dictionary<string, object> payload, string heroId)
        {
            var found = new List<Dictionary<string, object>>();
            string reply = ReadFirstString(parsed, "reply", "response", "text", "content");
            foreach (var write in ReadDictionaryList(parsed, "continuityWrites"))
            {
                string quote = ReadString(write, "evidenceQuote", "");
                if (quote.Length < 12 || reply.IndexOf(quote, StringComparison.Ordinal) < 0)
                    found.Add(new Dictionary<string, object> { ["type"] = "critical_continuity_metadata",
                        ["detail"] = "A durable continuity write is not supported by an exact quote of the accepted reply. Regenerate or remove that write and every dependent assertion." });
            }
            var people = ReadDictionaryList(CurrentContinuityPresence(payload), "people");
            var speaker = MergedInteractionParticipantProfiles(payload).FirstOrDefault(p => KnowledgeIdEquals(CharacterIdFrom(p), heroId));
            foreach (var person in people)
            {
                string name = ReadString(person, "name", "");
                if (string.IsNullOrWhiteSpace(name)) continue;
                string id = ReadString(person, "heroStringId", "");
                string aliases = Regex.Escape(name);
                string relation = NativeRelationFromSpeaker(speaker, id);
                if (relation == "father" || relation == "mother") aliases += "|my " + relation;
                bool absent = ReadString(person, "settlementPresence", "unknown") == "absent";
                if (!absent) continue;
                var match = Regex.Match(reply, @"\b(?:" + aliases + @")\s+(?:is|is still|remains|sits|stands|waits)\s+(?:right\s+)?(?:here|with us|in this (?:room|hall|town|tavern)|under (?:this|the same) roof)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    int sentenceStart = Math.Max(reply.LastIndexOf('.', Math.Max(0, match.Index - 1)), reply.LastIndexOf('\n', Math.Max(0, match.Index - 1))) + 1;
                    string prefix = reply.Substring(sentenceStart, match.Index - sentenceStart);
                    string suffix = reply.Substring(match.Index + match.Length).TrimStart();
                    if (Regex.IsMatch(prefix, @"\b(said|told|thought|believed|if|whether|suppose|pretend|imagine|remember|yesterday)\b", RegexOptions.IgnoreCase)
                        || suffix.StartsWith("?", StringComparison.Ordinal)) continue;
                }
                if (match.Success) found.Add(new Dictionary<string, object> { ["type"] = "critical_continuity_presence",
                    ["match"] = match.Value, ["heroStringId"] = id,
                    ["detail"] = "Current native evidence excludes this person's local presence. Remove this claim without inventing their remote location or disclosing engine-only knowledge." });
            }
            string venue = ReadString(ReadDictionary(payload, "conversationSceneState"), "conversationVenue", "");
            string expectedSite = Regex.IsMatch(venue, @"\btent\b", RegexOptions.IgnoreCase) ? "tent"
                : Regex.IsMatch(venue, @"\btavern\b", RegexOptions.IgnoreCase) ? "tavern" : "";
            if (!string.IsNullOrEmpty(expectedSite))
            {
                string otherSite = expectedSite == "tent" ? "tavern" : "tent";
                string heading = (reply ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?.Trim() ?? "";
                // Scene headings assert the current place. Historical mentions later in
                // the reply may refer to other places and must remain available.
                bool conflictingHeading = heading.Length <= 160
                    && !heading.EndsWith(".", StringComparison.Ordinal)
                    && !heading.EndsWith("!", StringComparison.Ordinal)
                    && !heading.EndsWith("?", StringComparison.Ordinal)
                    && !heading.StartsWith("*", StringComparison.Ordinal)
                    && Regex.IsMatch(heading, @"\b" + otherSite + @"\b", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(heading, @"\b" + expectedSite + @"\b", RegexOptions.IgnoreCase);
                bool conflictingPresentClaim = Regex.IsMatch(reply ?? "",
                    @"(?:^|[.!?]\s+)\s*(?:we(?:'re| are)|here we are|I am)\s+(?:in\s+)?(?:the|this|our)\s+" + otherSite + @"\b",
                    RegexOptions.IgnoreCase);
                if (conflictingHeading || conflictingPresentClaim)
                    found.Add(new Dictionary<string, object> { ["type"] = "critical_continuity_scene",
                        ["match"] = conflictingHeading ? heading : otherSite,
                        ["detail"] = "The current accepted scene is " + venue + ". A reference to another episode does not relocate this conversation." });
            }
            return found;
        }

        private static bool IsBlockingContinuityViolation(Dictionary<string, object> violation)
        {
            string type = ReadString(violation, "type", "");
            return type.StartsWith("temporary_guest_", StringComparison.Ordinal)
                || type.StartsWith("critical_continuity_", StringComparison.Ordinal)
                || type == "current_speaker_claimed_absent" || type == "player_sex_misidentification"
                || type == "unverified_action_completion"
                || type == "repeated_wording_lecture";
        }

        private sealed class ContinuityPromptRecord
        {
            public string SourceId;
            public string Kind;
            public string Observer;
            public string Session;
            public double WorldDay;
            public bool Required;
            public string Text;
            public int Priority;
            public int Order;
        }

        private static string RenderContinuityRecord(string id, string kind, string observer, string session,
            double day, int priority, bool required, string body)
        {
            Func<string, string> key = s => Regex.Replace(s ?? "", @"[^\p{L}\p{Nd}_:.,-]", "_");
            return "[[continuity source=" + key(id) + " kind=" + key(kind) + " observer=" + key(observer)
                + " session=" + key(session) + " day=" + day.ToString("0.#####", CultureInfo.InvariantCulture)
                + " priority=" + priority + " required=" + (required ? "true" : "false") + "]]\n"
                + (body ?? "").Replace("[[continuity ", "[quoted continuity ").Replace("[[/continuity]]", "[quoted end continuity]")
                + "\n[[/continuity]]";
        }

        private static string FormatContinuityTranscript(List<Dictionary<string, object>> lines,
            Func<List<Dictionary<string, object>>, string> render)
        {
            var groups = new List<List<Dictionary<string, object>>>();
            foreach (var line in lines ?? new List<Dictionary<string, object>>())
            {
                string exchange = ReadFirstString(line, "exchangeId", "exchange_id");
                string previous = groups.Count == 0 ? "" : ReadFirstString(groups.Last().Last(), "exchangeId", "exchange_id");
                bool player = ReadString(line, "role", "").Equals("player", StringComparison.OrdinalIgnoreCase);
                if (groups.Count == 0 || (exchange.Length > 0 && exchange != previous) || (exchange.Length == 0 && player))
                    groups.Add(new List<Dictionary<string, object>>());
                groups.Last().Add(line);
            }
            return string.Join("\n\n", groups.Select((group, index) =>
            {
                string body = render(group);
                if (string.IsNullOrWhiteSpace(body) || body == "none") return "";
                var first = group.First();
                string ids = string.Join(",", group.Select(l => FirstNonEmpty(ReadFirstString(l, "id", "turnId", "turn_id"), PromptHash(ReadString(l, "text", "")).Substring(0, 20))));
                return RenderContinuityRecord(ids, "accepted_exchange", "current_observer", ReadFirstString(first, "sessionId", "session_id"),
                    ReadDouble(first, "worldDay", ReadDouble(first, "world_day", 0d)), 80 + index, index == groups.Count - 1, body);
            }).Where(s => s.Length > 0));
        }

        private static List<ContinuityPromptRecord> ContinuityPromptRecords(string text, bool newest)
        {
            string normalized = (text ?? "").Replace("\r\n", "\n").Trim();
            if (normalized.Length == 0) return new List<ContinuityPromptRecord>();
            var structured = Regex.Matches(normalized, @"\[\[continuity source=([^ ]*) kind=([^ ]*) observer=([^ ]*) session=([^ ]*) day=([^ ]*) priority=(\d+) required=(true|false)\]\]\n[\s\S]*?\[\[/continuity\]\]").Cast<Match>().ToList();
            if (structured.Count > 0)
            {
                var result = new List<ContinuityPromptRecord>();
                int cursor = 0;
                foreach (var match in structured)
                {
                    if (match.Index > cursor) result.AddRange(ContinuityPromptRecords(normalized.Substring(cursor, match.Index - cursor), newest));
                    result.Add(new ContinuityPromptRecord { SourceId = match.Groups[1].Value, Kind = match.Groups[2].Value,
                        Observer = match.Groups[3].Value, Session = match.Groups[4].Value,
                        WorldDay = double.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture),
                        Priority = int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture), Required = match.Groups[7].Value == "true", Text = match.Value });
                    cursor = match.Index + match.Length;
                }
                if (cursor < normalized.Length) result.AddRange(ContinuityPromptRecords(normalized.Substring(cursor), newest));
                for (int i = 0; i < result.Count; i++) result[i].Order = i;
                return result;
            }
            // A section carries its source header with all of its rows. Never
            // return an orphaned fragment or reinterpret an old speaker as current.
            var starts = Regex.Matches(normalized, @"(?m)^(?:- )?\[(?:In person|Party|Group|Letter|Social)[^\]\r\n]*\]\s+")
                .Cast<Match>().Select(m => m.Index).ToList();
            // A speaker's paragraphs are one record. Paragraph splitting is only
            // a fallback for non-transcript blocks with no attribution markers.
            string[] blocks;
            if (starts.Count > 0)
            {
                if (starts[0] > 0) starts.Insert(0, 0);
                blocks = starts.Select((start, i) => normalized.Substring(start,
                    (i + 1 < starts.Count ? starts[i + 1] : normalized.Length) - start).Trim()).ToArray();
            }
            else blocks = Regex.Split(normalized, @"\n\s*\n");
            return blocks.Where(b => !string.IsNullOrWhiteSpace(b)).Select((b, i) => new ContinuityPromptRecord
            {
                SourceId = "block_" + PromptHash(b).Substring(0, 20), Text = b,
                Kind = "legacy_complete_block", Priority = newest ? i : blocks.Length - i, Order = i
            }).ToList();
        }

        private static string SelectWholeContinuityRecords(string text, int charAllowance, bool newest,
            List<object> diagnostics, string section)
        {
            var records = ContinuityPromptRecords(text, newest);
            var retained = new List<ContinuityPromptRecord>();
            int available = Math.Max(0, charAllowance);
            foreach (var record in records.OrderByDescending(r => r.Required).ThenByDescending(r => r.Priority))
            {
                bool fits = record.Required || record.Text.Length + 2 <= available;
                diagnostics.Add(new Dictionary<string, object> { ["section"] = section, ["sourceId"] = record.SourceId,
                    ["kind"] = record.Kind, ["observer"] = record.Observer, ["sessionId"] = record.Session,
                    ["worldDay"] = record.WorldDay, ["required"] = record.Required, ["contentHash"] = PromptHash(record.Text),
                    ["retained"] = fits, ["reason"] = fits ? "selected_whole_record" : "lower_priority_record_exceeds_allowance",
                    ["characters"] = record.Text.Length });
                if (!fits) continue;
                retained.Add(record); available -= record.Text.Length + 2;
            }
            return string.Join("\n\n", retained.OrderBy(r => r.Order).Select(r => r.Text));
        }

        private static void FinalizeContinuityPromptBudget(PromptEnvelope envelope, Dictionary<string, object> payload)
        {
            var settings = LoadSettings();
            int characters = envelope.Messages.Sum(m => ReadString(m, "content", "").Length);
            int bytes = envelope.Messages.Sum(m => Encoding.UTF8.GetByteCount(ReadString(m, "content", "")));
            // The estimate is disclosed, not represented as an exact tokenizer.
            // The byte bound is retained for model-capacity certification. A verified
            // per-model ceiling can be supplied by an offline provider profile.
            int estimated = EstimateContinuityTokens(Json.Serialize(envelope.Messages));
            var expectedSources = ReadStringList(ReadDictionary(payload, "continuityEvidence"), "sourceIds").Distinct().ToList();
            string rendered = string.Join("\n", envelope.Messages.Select(m => ReadString(m, "content", "")));
            var missingSources = expectedSources.Where(id => !rendered.Contains(id, StringComparison.Ordinal)).ToList();
            string mode = ReadString(envelope.Diagnostics, "requestType", ReadString(payload, "mode", "dialogue"));
            string model = ModelForRequest(settings, mode);
            string provider = ReadString(settings, "llmProvider", "");
            var limits = ReadDictionary(settings, "verifiedConversationContextWindows");
            int capacity = 0; // Actual endpoint/model/route verification occurs after all adapters serialize the request.
            int outputReserve = ReadInt(payload, "continuityOutputReserve", StructuredDialogueResponseMaxTokens(settings));
            int allowance = 64000;
            int selected = estimated <= 32000 ? 32000 : estimated <= 48000 ? 48000 : (int)Math.Ceiling(estimated / 8000d) * 8000;
            bool fits = estimated <= allowance && missingSources.Count == 0;
            envelope.Diagnostics["continuityPreflight"] = new Dictionary<string, object>
            {
                ["schema"] = "reign-continuity-preflight-v1", ["finalSerializedMessageHash"] = PromptHash(Json.Serialize(envelope.Messages)),
                ["inputTokenEstimate"] = estimated, ["estimator"] = "ascii_characters_divided_by_three_plus_non_ascii_utf8_bytes_and_overhead",
                ["inputTokenByteUpperBound"] = bytes + envelope.Messages.Count * 12,
                ["verifiedContextTokens"] = capacity, ["capacityVerified"] = capacity > 0,
                ["selectedInputAllowance"] = Math.Min(selected, allowance), ["outputReserve"] = outputReserve,
                ["fits"] = fits, ["provisionalUntilProviderSerialization"] = true,
                ["continuityEvidence"] = ReadDictionary(payload, "continuityEvidence"),
                ["requiredSourceIds"] = expectedSources, ["missingSourceIds"] = missingSources,
                ["requiredSourceCoverageComplete"] = missingSources.Count == 0
            };
            if (missingSources.Count > 0) throw new InvalidOperationException("Required continuity evidence was lost during prompt assembly: " + string.Join(",", missingSources));
            if (!fits) throw new InvalidOperationException("The complete conversation context exceeds its input allowance. "
                + "The player message and protected relationship/agenda evidence were preserved; configure a verified larger model context before retrying.");
        }

        private static int EstimateContinuityTokens(string serialized)
        {
            int ascii = 0, unicodeBytes = 0;
            foreach (char c in serialized ?? "")
            {
                if (c <= 127) ascii++;
                else unicodeBytes += Encoding.UTF8.GetByteCount(new[] { c });
            }
            return (int)Math.Ceiling(ascii / 3d) + unicodeBytes + 128;
        }

        private static Dictionary<string, object> BuildContinuityProviderPreflight(Dictionary<string, object> settings,
            Dictionary<string, object> payload, Dictionary<string, object> body, string endpoint, string model)
        {
            var routing = new Dictionary<string, object>();
            foreach (string key in new[] { "provider", "routing", "stickyprovider" })
                if (body.ContainsKey(key)) routing[key] = body[key];
            string routeKey = NormalizeLlmProvider(ReadString(settings, "llmProvider", "")) + ":" + model + ":"
                + PromptHash(endpoint + "|" + CanonicalJson(routing)).Substring(0, 20);
            var certificate = ReadDictionary(ReadDictionary(settings, "verifiedConversationContextWindows"), routeKey);
            bool verified = certificate != null && ReadString(certificate, "endpoint", "") == endpoint
                && ReadString(certificate, "model", "") == model && !string.IsNullOrWhiteSpace(ReadString(certificate, "source", ""))
                && DateTimeOffset.TryParse(ReadString(certificate, "validUntilUtc", ""), out var expiry) && expiry > DateTimeOffset.UtcNow;
            int capacity = verified ? ReadInt(certificate, "contextTokens", 0) : 0;
            verified = verified && capacity > 0;
            string serialized = Json.Serialize(body);
            var expectedSources = ReadStringList(ReadDictionary(ReadDictionary(payload, "promptEnvelope"), "continuityPreflight"), "requiredSourceIds");
            var missingSources = expectedSources.Where(id => !serialized.Contains(id, StringComparison.Ordinal)).ToList();
            int estimate = EstimateContinuityTokens(serialized);
            int output = ReadInt(body, "max_tokens", ReadInt(payload, "maxTokens", 8000));
            int safety = 1024;
            int allowance = verified ? Math.Min(64000, Math.Max(0, capacity - output - safety)) : 48000;
            // A byte ceiling is used for the physical capacity when no certified
            // tokenizer is available. The estimate only chooses our working budget.
            int byteBound = Encoding.UTF8.GetByteCount(serialized) + 128;
            bool capacityFits = !verified || byteBound + output + safety <= capacity;
            return new Dictionary<string, object> {
                ["schema"] = "reign-continuity-provider-preflight-v1", ["routeKey"] = routeKey,
                ["finalSerializedRequestHash"] = PromptHash(serialized), ["requestCharacters"] = serialized.Length,
                ["inputTokenEstimate"] = estimate, ["inputTokenByteUpperBound"] = byteBound,
                ["capacityVerified"] = verified, ["verifiedContextTokens"] = capacity,
                ["inputAllowance"] = allowance, ["selectedInputAllowance"] = Math.Min(allowance, estimate <= 32000 ? 32000 : estimate <= 48000 ? 48000 : (int)Math.Ceiling(estimate / 8000d) * 8000),
                ["outputReserve"] = output, ["safetyReserve"] = safety,
                ["fits"] = estimate <= allowance && capacityFits && missingSources.Count == 0,
                ["missingSourceIds"] = missingSources, ["requiredSourceCoverageComplete"] = missingSources.Count == 0,
                ["estimator"] = "ascii_divided_by_three_plus_unicode_utf8_bytes_plus_128; not an exact tokenizer",
                ["requiredSources"] = ReadDictionary(ReadDictionary(payload, "promptEnvelope"), "continuityPreflight")
            };
        }
    }
}
