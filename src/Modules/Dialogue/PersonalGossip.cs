using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly ConcurrentDictionary<string, object> PersonalGossipLocks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, byte> PersonalGossipWorkers = new(StringComparer.OrdinalIgnoreCase);
        private static object GossipGate(string campaign) => PersonalGossipLocks.GetOrAdd(campaign, _ => new object());

        private static void EnsurePersonalGossipSchema(ReignDbConnection c)
        {
            const string marker = "personal_gossip_schema";
            if (IsPostgreSqlComponentSchemaReady(c, marker, 1)) return;
            // Save Sync snapshots the whole schema, including decisions, reservations and receipts.
            ExecuteSql(c, @"CREATE TABLE IF NOT EXISTS personal_gossip_clock (
timeline_id TEXT PRIMARY KEY,world_day REAL NOT NULL,snapshot_json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS personal_gossip_topics (
topic_id TEXT PRIMARY KEY,timeline_id TEXT NOT NULL,speaker_id TEXT NOT NULL,player_id TEXT NOT NULL,
session_id TEXT NOT NULL,created_day REAL NOT NULL,status TEXT NOT NULL,payload_json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_gossip_topics_speaker ON personal_gossip_topics(timeline_id,speaker_id,created_day);
CREATE TABLE IF NOT EXISTS personal_gossip_messages (
message_id TEXT PRIMARY KEY,topic_id TEXT NOT NULL,root_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
speaker_id TEXT NOT NULL,recipient_id TEXT NOT NULL,player_id TEXT NOT NULL,is_reply INTEGER NOT NULL DEFAULT 0,
hop INTEGER NOT NULL,created_day REAL NOT NULL,due_day REAL NOT NULL,expires_day REAL NOT NULL,
status TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,lease_token TEXT NOT NULL DEFAULT '',
lease_until INTEGER NOT NULL DEFAULT 0,payload_json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_gossip_due ON personal_gossip_messages(timeline_id,status,due_day);
CREATE INDEX IF NOT EXISTS idx_gossip_caps ON personal_gossip_messages(timeline_id,speaker_id,created_day);
CREATE TABLE IF NOT EXISTS personal_gossip_history (
receipt_id TEXT PRIMARY KEY,message_id TEXT NOT NULL,root_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
owner_id TEXT NOT NULL,peer_id TEXT NOT NULL,acquired_day REAL NOT NULL,raise_session TEXT NOT NULL DEFAULT '',
payload_json TEXT NOT NULL,UNIQUE(message_id,owner_id));
CREATE INDEX IF NOT EXISTS idx_gossip_history_owner ON personal_gossip_history(timeline_id,owner_id,acquired_day DESC);");
            ExecuteSql(c, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES($key,'1');", GossipFields("key", marker));
            MarkPostgreSqlComponentSchemaReady(c, marker, 1);
        }

        private static Dictionary<string, object> GossipJson(Dictionary<string, object> row) =>
            TryParseJsonObject(ReadString(row, "payload_json", "{}")) ?? new Dictionary<string, object>();
        private static string GossipTimeline(Dictionary<string, object> payload) => ReadString(payload, "timelineId", "main");
        private static Dictionary<string, object> GossipClock(ReignDbConnection c, string timeline) =>
            QuerySql(c, "SELECT * FROM personal_gossip_clock WHERE timeline_id=$timeline;", GossipFields("timeline", timeline)).FirstOrDefault();
        private static Dictionary<string, object> GossipSnapshot(ReignDbConnection c, string timeline) =>
            TryParseJsonObject(ReadString(GossipClock(c, timeline), "snapshot_json", "{}")) ?? new Dictionary<string, object>();

        private static bool GossipAcceptPresence(ReignDbConnection c, Dictionary<string, object> payload)
        {
            var snapshot = ReadDictionary(payload, "gossipPresence");
            double day = ReadDouble(payload, "worldDay", -1), captured = ReadDouble(snapshot, "worldDay", -2);
            string timeline = GossipTimeline(payload);
            if (snapshot == null || ReadString(snapshot, "schema", "") != "reign-gossip-presence-v1"
                || !double.IsFinite(day) || !double.IsFinite(captured) || day < 0 || Math.Abs(day - captured) > 1d / 1440
                || GossipTimeline(snapshot) != timeline || ReadDictionaryList(snapshot, "people").Count > 8192) return false;
            if (ActiveWorldHistoryTimeline(c) != timeline) return false;
            var old = GossipClock(c, timeline);
            if (old != null && ReadDouble(old, "world_day", 0) > day) return false;
            ExecuteSql(c, @"INSERT INTO personal_gossip_clock(timeline_id,world_day,snapshot_json) VALUES($timeline,$day,$json)
ON CONFLICT(timeline_id) DO UPDATE SET world_day=excluded.world_day,snapshot_json=excluded.snapshot_json;",
                GossipFields("timeline", timeline, "day", day, "json", Json.Serialize(snapshot)));
            return true;
        }

        private static Dictionary<string, object> PersonalGossipTickApi(Dictionary<string, object> payload)
        {
            string campaign = ReadString(payload, "campaignId", "");
            if (string.IsNullOrWhiteSpace(campaign)) return GossipFields("ok", false, "error", "Campaign is required.");
            if (!ReadBool(PersonalGossipSettings(), "enabled", true)) return GossipFields("ok", true, "enabled", false);
            int delivered;
            using (EnterCampaignWorkLock(GossipGate(campaign)))
            using (var c = OpenCampaignConnection(campaign))
            {
                EnsurePersonalGossipSchema(c);
                if (!GossipAcceptPresence(c, payload)) return GossipFields("ok", false, "error", "A current native gossip presence snapshot is required.");
                delivered = DeliverPersonalGossip(c, campaign, GossipTimeline(payload), ReadDouble(payload, "worldDay", 0));
            }
            SchedulePersonalGossip(campaign);
            return GossipFields("ok", true, "delivered", delivered);
        }

        private static double GossipAffinity(ReignDbConnection c, string observer, string subject,
            Dictionary<string, object> observerProfile, Dictionary<string, object> subjectProfile)
        {
            var row = QuerySql(c, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                GossipFields("pair", AmbientPairKey(observer, subject))).FirstOrDefault();
            return row == null ? InitialReignAffinity(observerProfile, subjectProfile)
                : ReadDouble(row, GossipSameId(ReadString(row, "hero_a_id", ""), observer) ? "affinity_a_to_b" : "affinity_b_to_a", 0);
        }

        private static bool GossipKnown(ReignDbConnection c, string observer, string target,
            Dictionary<string, object> a, Dictionary<string, object> b)
        {
            if (GossipFamily(a, b)) return true;
            if (TableExists(c, "acquaintances") && QuerySql(c,
                "SELECT subject_id FROM acquaintances WHERE observer_id=$a AND subject_id=$b AND identity_state='verified' LIMIT 1;",
                GossipFields("a", observer, "b", target)).Count > 0) return true;
            // Persisted social contact, unlike a raw native relation, establishes a known pair.
            return QuerySql(c, "SELECT pair_key FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                GossipFields("pair", AmbientPairKey(observer, target))).Count > 0;
        }

        private static List<string> GossipMentionedIds(string spoken, Dictionary<string, object> speaker,
            Dictionary<string, object> player, IEnumerable<Dictionary<string, object>> people)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var names in people.Where(p => !string.IsNullOrWhiteSpace(GossipHeroId(p)))
                .GroupBy(p => ReadString(p, "name", ""), StringComparer.OrdinalIgnoreCase))
            {
                string name = names.Key;
                var identities = names.Select(GossipHeroId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (identities.Count == 1 && name.Length >= 2 && Regex.IsMatch(spoken ?? "", @"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase))
                    result.Add(identities[0]);
            }
            // Resolve possessives separately for the NPC and player lines at the call site.
            foreach (var kin in new[] { ("mother", "motherId"), ("father", "fatherId"), ("wife", "spouseId"), ("husband", "spouseId"), ("spouse", "spouseId") })
            {
                if (Regex.IsMatch(spoken ?? "", @"\bmy\s+" + kin.Item1 + @"\b", RegexOptions.IgnoreCase)) result.Add(ReadString(speaker, kin.Item2, ""));
                if (Regex.IsMatch(spoken ?? "", @"\byour\s+" + kin.Item1 + @"\b", RegexOptions.IgnoreCase)) result.Add(ReadString(player, kin.Item2, ""));
            }
            result.RemoveWhere(string.IsNullOrWhiteSpace);
            return result.OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        private static double GossipConversationDelta(ReignDbConnection c, string timeline, string session, string speaker, string player)
        {
            if (!TableExists(c, "conversation_relationship_receipts")) return 0;
            var row = QuerySql(c, @"SELECT COALESCE(SUM(final_delta),0) AS delta FROM conversation_relationship_receipts r
WHERE r.observer_id=$speaker AND r.target_id=$player AND (r.timeline_id=$timeline OR r.timeline_id='')
AND r.status='applied' AND EXISTS(SELECT 1 FROM conversation_turns t WHERE t.session_id=$session AND t.exchange_id=r.exchange_id);",
                GossipFields("speaker", speaker, "player", player, "timeline", timeline, "session", session)).FirstOrDefault();
            return ReadDouble(row, "delta", 0);
        }

        private static void AcceptPersonalGossip(string campaign, Dictionary<string, object> payload,
            Dictionary<string, object> parsed, Dictionary<string, object> exchange, string speaker, string player,
            string playerText, string reply, Dictionary<string, object> episodeExchange = null)
        {
            try
            {
                if (ReadBool(payload, "officialMemoryFirewall", false) || string.IsNullOrWhiteSpace(speaker)
                    || string.IsNullOrWhiteSpace(player) || !ReadBool(PersonalGossipSettings(), "enabled", true)) return;
                using (EnterCampaignWorkLock(GossipGate(campaign)))
                using (var c = OpenCampaignConnection(campaign))
                {
                    EnsurePersonalGossipSchema(c);
                    if (!GossipAcceptPresence(c, payload)) return;
                    var episode = episodeExchange ?? exchange;
                    string timeline = GossipTimeline(payload), session = ReadString(episode, "sessionId", "");
                    double day = ReadDouble(payload, "worldDay", 0);
                    var people = ReadDictionaryList(ReadDictionary(payload, "gossipPresence"), "people");
                    var self = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), speaker));
                    var main = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), player));
                    if (self == null || session.Length == 0) return;
                    var mentioned = GossipMentionedIds(playerText, main, self, people)
                        .Concat(GossipMentionedIds(reply, self, main, people)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    mentioned.RemoveAll(id => GossipSameId(id, speaker) || GossipSameId(id, player));
                    var signals = ReadDictionaryList(parsed, "gossipSignals").Take(8)
                        .Where(s => ReadDouble(s, "confidence", 0) >= .80
                            && GossipQuoteSupported(ReadString(s, "evidenceQuote", ""), playerText + "\n" + reply)).ToList();
                    // Receipt-only impressions still work when a provider omits optional metadata.
                    if (signals.Count == 0) signals.Add(GossipFields("topic", "impression", "evidenceQuote", "", "confidence", 1d));
                    foreach (var signal in signals)
                    {
                        // Identities are resolved from the accepted words, never model-proposed
                        // names in background context. Keep the accepted source references.
                        signal["resolvedMentionedIds"] = mentioned;
                        signal["sourceTurnIds"] = ReadStringList(exchange, "turnIds");
                        string category = GossipTopicCategory(ReadString(signal, "topic", "conversation"));
                        if (category != "impression" && (ReadDouble(signal, "confidence", 0) < .80
                            || !GossipQuoteSupported(ReadString(signal, "evidenceQuote", ""), playerText + "\n" + reply))) continue;
                        string subject = ReadString(signal, "subjectId", "");
                        if (!mentioned.Contains(subject, StringComparer.OrdinalIgnoreCase)) subject = "";
                        string id = GossipTopicId(timeline, speaker, player, day, category, subject);
                        var prior = QuerySql(c, "SELECT * FROM personal_gossip_topics WHERE topic_id=$id;", GossipFields("id", id)).FirstOrDefault();
                        if (prior != null && ReadString(prior, "status", "") == "queued") continue;
                        var topic = GossipFields("id", id, "timelineId", timeline, "speakerId", speaker, "playerId", player,
                            "sessionId", session, "createdDay", prior == null ? day : ReadDouble(prior, "created_day", day),
                            "worldDay", day, "category", category, "subjectId", subject, "signal", signal,
                            "playerName", ReadString(main, "name", "the traveler"),
                            "mentionedIds", mentioned, "sourceTurnIds", ReadStringList(exchange, "turnIds"),
                            "sourceExchangeId", ReadString(episode, "exchangeId", ""), "sourceText", playerText + "\n" + reply,
                            "sourceSessionId", ReadString(exchange, "sessionId", ""), "sourceEvidenceExchangeId", ReadString(exchange, "exchangeId", ""),
                            "sourceQuote", ReadString(signal, "evidenceQuote", ""), "speaker", self,
                            "policy", prior == null ? PersonalGossipSettings() : ReadDictionary(GossipJson(prior), "policy") ?? PersonalGossipSettings(),
                            "rootId", id, "hop", 1, "holders", new List<string> { speaker },
                            "presenceComplete", ReadBool(ReadDictionary(payload, "gossipPresence"), "rosterComplete", false));
                        EvaluatePersonalGossip(c, campaign, topic, people);
                    }
                    MarkPersonalGossipRaised(c, timeline, speaker, session, parsed, reply, payload);
                    DeliverPersonalGossip(c, campaign, timeline, day);
                }
                SchedulePersonalGossip(campaign);
            }
            catch (Exception ex) { LogOperational("gossip.accept_failed", GossipFields("campaignId", campaign, "error", LimitText(ex.Message, 500))); }
        }

        private static void PersonalGossipRelationshipApplied(string campaign, Dictionary<string, object> payload)
        {
            try
            {
                if (!ReadBool(PersonalGossipSettings(), "enabled", true)) return;
                using (EnterCampaignWorkLock(GossipGate(campaign)))
                using (var c = OpenCampaignConnection(campaign))
                {
                    EnsurePersonalGossipSchema(c);
                    string exchange = ReadFirstString(payload, "exchangeId", "turnId", "sceneTurnId");
                    foreach (var row in QuerySql(c, @"SELECT * FROM personal_gossip_topics WHERE status<>'queued' AND created_day>=$day;",
                        GossipFields("day", ReadDouble(payload, "worldDay", 0) - 1)))
                    {
                        var topic = GossipJson(row);
                        if (ReadString(topic, "sourceExchangeId", "") != exchange) continue;
                        var snapshot = GossipSnapshot(c, ReadString(topic, "timelineId", "main"));
                        if (Math.Abs(ReadDouble(snapshot, "worldDay", -1) - ReadDouble(topic, "worldDay", 0)) > 1d / 1440) continue;
                        EvaluatePersonalGossip(c, campaign, topic, ReadDictionaryList(snapshot, "people"));
                    }
                }
                SchedulePersonalGossip(campaign);
            }
            catch (Exception ex) { LogOperational("gossip.relationship_failed", GossipFields("campaignId", campaign, "error", LimitText(ex.Message, 500))); }
        }

        private static List<GossipCandidate> BuildPersonalGossipCandidates(ReignDbConnection c, Dictionary<string, object> topic,
            List<Dictionary<string, object>> people, string type)
        {
            string speaker = ReadString(topic, "speakerId", ""), player = ReadString(topic, "playerId", "");
            var self = ReadDictionary(topic, "speaker");
            var signal = ReadDictionary(topic, "signal");
            var holders = ReadStringList(topic, "holders");
            var mentioned = ReadStringList(topic, "mentionedIds");
            var targets = ReadDictionaryList(signal, "targets");
            string accessible = ReadString(topic, "sourceText", "");
            var result = new List<GossipCandidate>();
            foreach (var person in people.GroupBy(GossipHeroId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                string id = GossipHeroId(person);
                var target = targets.FirstOrDefault(t => GossipSameId(ReadString(t, "heroId", ""), id)
                    && GossipQuoteSupported(ReadString(t, "evidenceQuote", ""), accessible));
                var candidate = new GossipCandidate { Id = id, Person = person,
                    Family = GossipFamily(self, person), Local = GossipLocal(self, person),
                    Mentioned = mentioned.Contains(id, StringComparer.OrdinalIgnoreCase),
                    Relevant = target != null && ReadBool(target, "relevant", false),
                    Hurt = target == null ? 0 : GossipFeeling(target, "hurt"), Grievance = target == null ? 0 : GossipFeeling(target, "grievance") };
                if (string.IsNullOrWhiteSpace(id) || GossipSameId(id, speaker) || GossipSameId(id, player)
                    || holders.Contains(id, StringComparer.OrdinalIgnoreCase)) candidate.Exclusion = "self_player_or_previous_holder";
                else if (!ReadBool(person, "isAlive", false) || ReadBool(person, "isPrisoner", true)
                    || !ReadBool(person, "available", false)) candidate.Exclusion = "unavailable";
                else if (!candidate.Local && !candidate.Mentioned) candidate.Exclusion = "unnamed_remote";
                else if (!GossipKnown(c, speaker, id, self, person)) candidate.Exclusion = "unknown_identity";
                else candidate.Affinity = GossipAffinity(c, speaker, id, self, person);
                result.Add(candidate);
            }
            return result;
        }

        private static void SavePersonalGossipTopic(ReignDbConnection c, Dictionary<string, object> topic, string status)
        {
            ExecuteSql(c, @"INSERT INTO personal_gossip_topics(topic_id,timeline_id,speaker_id,player_id,session_id,created_day,status,payload_json)
VALUES($id,$timeline,$speaker,$player,$session,$day,$status,$json) ON CONFLICT(topic_id) DO UPDATE SET status=excluded.status,payload_json=excluded.payload_json;",
                GossipFields("id", ReadString(topic, "id", ""), "timeline", ReadString(topic, "timelineId", "main"),
                    "speaker", ReadString(topic, "speakerId", ""), "player", ReadString(topic, "playerId", ""),
                    "session", ReadString(topic, "sessionId", ""), "day", ReadDouble(topic, "createdDay", 0), "status", status, "json", Json.Serialize(topic)));
        }

        private static void EvaluatePersonalGossip(ReignDbConnection c, string campaign, Dictionary<string, object> topic,
            List<Dictionary<string, object>> people)
        {
            string id = ReadString(topic, "id", ""), speaker = ReadString(topic, "speakerId", ""), timeline = ReadString(topic, "timelineId", "main");
            string player = ReadString(topic, "playerId", ""), session = ReadString(topic, "sessionId", "");
            var policy = ReadDictionary(topic, "policy");
            var signal = ReadDictionary(topic, "signal");
            var traits = ReadJsonObject(CharacterFile(campaign, speaker, "traits.json"));
            double day = ReadDouble(topic, "worldDay", 0);
            if (QuerySql(c, "SELECT message_id FROM personal_gossip_messages WHERE topic_id=$id AND is_reply=0 LIMIT 1;", GossipFields("id", id)).Count > 0) return;
            if (ReadInt(topic, "hop", 1) > ReadInt(policy, "maximumOutwardHops", 2)
                || day > ReadDouble(topic, "rootCreatedDay", ReadDouble(topic, "createdDay", day)) + ReadDouble(policy, "expiryDays", 14))
            { SavePersonalGossipTopic(c, topic, "expired_or_hop_limit"); return; }
            if (!ReadBool(topic, "presenceComplete", true)) { SavePersonalGossipTopic(c, topic, "presence_incomplete"); return; }
            topic["policyVersion"] = ReadString(policy, "policyVersion", PersonalGossipVersion);
            topic["savedRolls"] = GossipFields("share", GossipRoll(id, "share"), "negative", GossipRoll(id, "negative"),
                "disclosure", GossipRoll(id, "disclosure"), "distortion", GossipRoll(id, "distortion"), "severity", GossipRoll(id, "severity"),
                "negativeRecipient", GossipDraw(id, "negative_recipient"), "friendlyRecipient", GossipDraw(id, "friendly_recipient"));
            double delta = GossipConversationDelta(c, timeline, session, speaker, player);
            int positiveFeeling = Math.Max(GossipFeeling(signal, "excitement"), GossipFeeling(signal, "curiosity"));
            double supportedHurt = ReadDictionaryList(signal, "targets")
                .Where(t => GossipQuoteSupported(ReadString(t, "evidenceQuote", ""), ReadString(topic, "sourceText", "")))
                .Select(t => GossipFeeling(t, "hurt")).DefaultIfEmpty(0).Max();
            double emotion = Math.Max(positiveFeeling, Math.Max(Math.Max(supportedHurt, GossipFeeling(signal, "hurt")), Math.Min(100, 20 * Math.Abs(delta))));
            topic["delta"] = delta; topic["emotion"] = emotion; topic["traits"] = traits;
            if (ReadString(topic, "category", "") == "impression") emotion = Math.Min(100, 20 * Math.Abs(delta));
            topic["emotion"] = emotion;
            if (!GossipEmotionEligible(emotion, traits)) { SavePersonalGossipTopic(c, topic, "insufficient_feeling"); return; }
            var friendlyAudit = BuildPersonalGossipCandidates(c, topic, people, "positive");
            var negativeAudit = BuildPersonalGossipCandidates(c, topic, people, "negative");
            var friendly = GossipRecipients(friendlyAudit, "positive", GossipTrait(traits, "familyMotivation"), policy);
            var negative = GossipRecipients(negativeAudit, "negative", GossipTrait(traits, "familyMotivation"), policy);
            topic["candidateAudit"] = GossipFields("friendly", friendlyAudit.Where(x => x.Local || x.Mentioned).Select(x => x.Audit()).ToList(),
                "negative", negativeAudit.Where(x => x.Local || x.Mentioned).Select(x => x.Audit()).ToList(),
                "exclusionCounts", friendlyAudit.Where(x => x.Exclusion.Length > 0).GroupBy(x => x.Exclusion)
                    .ToDictionary(g => g.Key, g => (object)g.Count()));
            if (friendly.Count + negative.Count == 0) { SavePersonalGossipTopic(c, topic, ReadBool(topic, "presenceComplete", true) ? "no_audience" : "presence_incomplete"); return; }
            int share = GossipShareChance(emotion, traits);
            bool retell = ReadInt(topic, "hop", 1) > 1;
            if (retell) share = GossipRetellChance(share, ReadInt(topic, "credibility", 50));
            topic["shareChance"] = share; topic["shareRoll"] = GossipRoll(id, "share");
            if (GossipRoll(id, "share") > share) { SavePersonalGossipTopic(c, topic, "sharing_declined"); return; }
            var selected = GossipChooseRecipient(negative, GossipDraw(id, "negative_recipient"));
            string type = "";
            int negativeChance = 0;
            if (selected != null)
            {
                var target = ReadDictionaryList(signal, "targets").FirstOrDefault(t => GossipSameId(ReadString(t, "heroId", ""), selected.Id));
                negativeChance = GossipNegativeChance(traits, selected.Hurt, selected.Affinity, selected.Grievance,
                    ReadBool(target, "knownJealousy", false), ReadBool(target, "knownEnvy", false));
                if (GossipRoll(id, "negative") <= negativeChance) type = "negative";
            }
            if (type.Length == 0)
            {
                selected = GossipChooseRecipient(friendly, GossipDraw(id, "friendly_recipient"));
                negativeChance = 0;
                if (selected != null && GossipEmotionEligible(positiveFeeling, traits) && ReadString(topic, "category", "") != "impression") type = "positive";
                else if (selected != null && delta != 0) type = "impression";
            }
            if (type.Length == 0 || selected == null) { SavePersonalGossipTopic(c, topic, "no_eligible_intent"); return; }
            topic["attemptRecipientId"] = selected.Id; topic["attemptType"] = type; topic["negativeChance"] = negativeChance;
            if (retell && type != "negative" && ReadInt(topic, "credibility", 50) < 25
                && GossipTrait(traits, "curiosity") < 60) { SavePersonalGossipTopic(c, topic, "doubtful_retelling_declined"); return; }
            double bond = GossipConfidenceBond(c, ReadDictionary(topic, "speaker"), people, ReadStringList(signal, "confidenceOwnerIds"));
            string privacy = GossipPrivacy(ReadString(signal, "privacy", "personal"));
            if (type == "impression") privacy = GossipPrivacy(ReadString(signal, "existencePrivacy", "personal"));
            int detail = GossipDetailChance(traits, privacy, emotion, selected.Affinity, bond,
                ReadBool(signal, "ownCompromising", false), ReadBool(signal, "promisedSecrecy", false), negativeChance);
            int disclosureRoll = GossipRoll(id, "disclosure");
            topic["detailChance"] = detail; topic["disclosureRoll"] = disclosureRoll;
            if (disclosureRoll > detail)
            {
                int existence = GossipDetailChance(traits, GossipPrivacy(ReadString(signal, "existencePrivacy", privacy)), emotion,
                    selected.Affinity, bond, ReadBool(signal, "ownCompromising", false), ReadBool(signal, "promisedSecrecy", false), 0);
                if (type == "negative" || delta == 0 || disclosureRoll > existence) { SavePersonalGossipTopic(c, topic, "privacy_declined"); return; }
                type = "impression";
            }
            var capRows = QuerySql(c, @"SELECT m.is_reply,m.hop,m.created_day,t.session_id FROM personal_gossip_messages m
LEFT JOIN personal_gossip_topics t ON t.topic_id=m.topic_id WHERE m.timeline_id=$timeline AND m.speaker_id=$speaker AND m.is_reply=0
AND (m.created_day>$day OR t.session_id=$session);", GossipFields("timeline", timeline, "speaker", speaker, "day", day - 1, "session", session));
            if (capRows.Count(r => ReadDouble(r, "created_day", 0) > day - 1) >= ReadInt(policy, "maximumOutgoingPerDay", 3)
                || !retell && capRows.Count(r => ReadString(r, "session_id", "") == session && ReadInt(r, "hop", 1) == 1) >= ReadInt(policy, "maximumOriginalsPerConversation", 2))
            { SavePersonalGossipTopic(c, topic, "capacity_reached"); return; }
            string distortion = "none";
            if (type == "negative" && GossipRoll(id, "distortion") <= GossipDistortionChance(traits, selected.Hurt))
                distortion = GossipDistortionSeverity(GossipDistortionScore(traits, selected.Hurt), GossipRoll(id, "severity"));
            topic["type"] = type; topic["distortion"] = distortion; topic["negativeChance"] = negativeChance;
            topic["detailChance"] = detail; topic["disclosureRoll"] = disclosureRoll; topic["recipient"] = selected.Person;
            topic["recipientId"] = selected.Id; topic["local"] = selected.Local;
            double transit = GossipTransitDays(ReadDictionary(topic, "speaker"), selected.Person, policy, selected.Local);
            topic["dueDay"] = day + transit;
            topic["route"] = GossipFields("local", selected.Local, "delayDays", transit, "queuedDay", day,
                "speakerPresence", ReadDictionary(topic, "speaker"), "recipientPresence", selected.Person);
            topic["expiresDay"] = ReadDouble(topic, "rootCreatedDay", ReadDouble(topic, "createdDay", day)) + ReadDouble(policy, "expiryDays", 14);
            topic["claimKind"] = type == "impression" ? "impression" : ReadBool(signal, "question", false)
                || retell && ReadInt(topic, "credibility", 50) < 25 && type != "negative" ? "question" : "assertion";
            topic["uncertain"] = retell && type != "negative" && ReadInt(topic, "credibility", 50) < 60;
            using (var tx = c.BeginTransaction())
            {
                SavePersonalGossipTopic(c, topic, "queued");
                InsertPersonalGossipMessage(c, topic, id + "_out", false);
                tx.Commit();
            }
        }

        private static double GossipConfidenceBond(ReignDbConnection c, Dictionary<string, object> self,
            List<Dictionary<string, object>> people, List<string> owners) => owners.Where(id => !GossipSameId(id, GossipHeroId(self)))
            .Select(id => { var person = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), id));
                return person == null ? 0 : Math.Max(0, GossipAffinity(c, GossipHeroId(self), id, self, person)); }).DefaultIfEmpty(0).Max();

        private static void InsertPersonalGossipMessage(ReignDbConnection c, Dictionary<string, object> data, string id, bool reply)
        {
            ExecuteSql(c, @"INSERT INTO personal_gossip_messages(message_id,topic_id,root_id,timeline_id,speaker_id,recipient_id,player_id,is_reply,hop,
created_day,due_day,expires_day,status,payload_json) VALUES($id,$topic,$root,$timeline,$speaker,$recipient,$player,$reply,$hop,$day,$due,$expires,'pending',$json)
ON CONFLICT(message_id) DO NOTHING;", GossipFields("id", id, "topic", ReadString(data, "id", ""), "root", ReadString(data, "rootId", ""),
                "timeline", ReadString(data, "timelineId", "main"), "speaker", ReadString(data, "speakerId", ""), "recipient", ReadString(data, "recipientId", ""),
                "player", ReadString(data, "playerId", ""), "reply", reply ? 1 : 0, "hop", ReadInt(data, "hop", 1),
                "day", ReadDouble(data, "worldDay", 0), "due", ReadDouble(data, "dueDay", 0), "expires", ReadDouble(data, "expiresDay", 0), "json", Json.Serialize(data)));
        }
    }
}
