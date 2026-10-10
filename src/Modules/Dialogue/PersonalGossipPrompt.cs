using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string PersonalGossipSignalContract = @"Optional private gossip metadata: gossipSignals is an array, normally empty.
Only a supported meaningful feeling or personal question creates a signal; do not manufacture a motive to fill this field.
Each entry is {topic:'family|relationship|intimacy|politics|shared_activity|gift|question|conversation',subjectId:'',
evidenceQuote:'exact current player/NPC words',confidence:0.0,excitement:0,curiosity:0,hurt:0,
privacy:'public|personal|intimate|secret',existencePrivacy:'public|personal|intimate|secret',
confidenceOwnerIds:[],promisedSecrecy:false,ownCompromising:false,question:false,
targets:[{heroId:'',evidenceQuote:'exact supporting current words',relevant:false,hurt:0,grievance:0,knownJealousy:false,knownEnvy:false}]}.
Feeling/grievance levels: 0 absent; 25 mild; 50 personally meaningful interest, grievance or competitive opportunity;
75 strong excitement/hurt/persistent curiosity or desire to provoke a rival; 100 exceptional or defining.
Confidence must be >=0.80. Targets use your own known relationships and concerns. Another person's hidden feelings are unavailable.
The topic must concern a real encounter or received report. Preserve fact vs question vs belief; a quoted insult or friendly correction is not automatically hostility.
Use a stable topic category and subject across repeated discussion. Do not invent probabilities, destinations or deliveries.
Classify the most private source fact actually disclosed and the privacy of the encounter's existence separately.
Family is not permission to reveal confidences. Entrusted secrets and explicit promises must be identified.
The server decides whether, to whom, when, and with what permitted exaggeration a story is told.
Do not narrate an offscreen conversation or incoming answer as having happened because you emitted a signal.
If you actually bring up a nominated received gossip topic in this reply, optionally include raisedGossip:[{receiptId,evidenceQuote}]
with the exact words of this reply. Do not mark something raised merely because it was available in memory.";

        private static string BuildPersonalGossipPrompt(string campaign, string hero, Dictionary<string, object> payload, List<string> sourceIds)
        {
            if (ReadBool(payload, "officialMemoryFirewall", false) || !ReadBool(PersonalGossipSettings(), "enabled", true)) return "";
            try
            {
                var text = new StringBuilder();
                string timeline = GossipTimeline(payload), player = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
                double day = ReadDouble(payload, "worldDay", 0);
                string session = ReadFirstString(payload, "conversationSessionId", "sessionId");
                using (EnterCampaignWorkLock(GossipGate(campaign)))
                using (var c = OpenCampaignConnection(campaign))
                {
                    EnsurePersonalGossipSchema(c);
                    bool fresh = GossipAcceptPresence(c, payload);
                    if (fresh) DeliverPersonalGossip(c, campaign, timeline, day);
                    text.AppendLine(PersonalGossipSignalContract);
                    if (fresh)
                    {
                        var people = ReadDictionaryList(ReadDictionary(payload, "gossipPresence"), "people");
                        var self = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), hero));
                        if (self != null)
                        {
                            var contacts = people.Where(p => !GossipSameId(GossipHeroId(p), hero) && !GossipSameId(GossipHeroId(p), player)
                                && (GossipLocal(self, p) || GossipFamily(self, p)))
                                .Where(p => GossipKnown(c, hero, GossipHeroId(p), self, p)).Select(p => new {
                                    Person = p, Relation = GossipAffinity(c, hero, GossipHeroId(p), self, p) })
                                .Where(p => p.Relation >= 30 || p.Relation <= -10 || GossipFamily(self, p.Person)).Take(24);
                            text.AppendLine("YOUR KNOWN SOCIAL CONTACTS (not conversation witnesses; names alone do not establish topic relevance):");
                            foreach (var contact in contacts)
                                text.AppendLine(ReadString(contact.Person, "name", "") + " [" + GossipHeroId(contact.Person) + "]: "
                                    + (GossipFamily(self, contact.Person) ? "family; " : "")
                                    + (contact.Relation <= -10 ? "you dislike them" : contact.Relation >= 30 ? "you consider them a friend" : "ordinary family relationship")
                                    + (GossipLocal(self, contact.Person) ? "; currently in the same settlement or party." : "; not established as nearby."));
                        }
                    }
                    var history = QuerySql(c, @"SELECT * FROM personal_gossip_history WHERE timeline_id=$timeline AND owner_id=$owner
AND acquired_day<=$day ORDER BY acquired_day DESC,receipt_id DESC LIMIT 12;", GossipFields("timeline", timeline, "owner", hero, "day", day));
                    if (history.Count > 0)
                    {
                        text.AppendLine("RECENT NPC EXCHANGES: You heard/said these words. Reports remain attributed claims, not witnessed events, consent, intimacy or promises. Never claim the player told you this.");
                        var nominations = new List<string>();
                        bool alreadyRaised = session.Length > 0 && QuerySql(c,
                            "SELECT receipt_id FROM personal_gossip_history WHERE timeline_id=$timeline AND owner_id=$owner AND raise_session=$session LIMIT 1;",
                            GossipFields("timeline", timeline, "owner", hero, "session", session)).Count > 0;
                        var traits = ReadJsonObject(CharacterFile(campaign, hero, "traits.json"));
                        int used = 0;
                        foreach (var row in history)
                        {
                            var data = GossipJson(row);
                            string id = ReadString(row, "receipt_id", ""), body = ReadString(data, "body", "");
                            string line = "[" + id + "; acquired day " + ReadDouble(row, "acquired_day", 0).ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)
                                + "] " + (ReadBool(data, "authored", false) ? "You said to " + ReadString(row, "peer_id", "") : ReadString(data, "speakerName", "Someone") + " told you")
                                + ": " + body + " (" + ReadString(data, "claimKind", "assertion")
                                + (ReadBool(data, "authored", false) ? "; your outgoing account, not proof of the listener's belief.)"
                                    : ReadString(data, "claimKind", "") == "question" || ReadString(data, "claimKind", "") == "impression" ? "; no factual belief score.)"
                                    : "; belief " + ReadInt(data, "credibility", 50) + "/100.)");
                            if (used + line.Length > 6000) break; // Whole records, never clipped claims/corrections.
                            text.AppendLine(line); used += line.Length; sourceIds.Add(id);
                            if (ReadBool(data, "authorKnowsDistortion", false)) text.AppendLine("You remember deliberately distorting that account. Do not mistake your own invention for an actual event.");
                            if (alreadyRaised || nominations.Count > 0 || ReadBool(data, "authored", false)
                                || ReadString(data, "claimKind", "") == "reply" || ReadString(row, "raise_session", "").Length > 0
                                || ReadString(data, "claimKind", "") == "question" && !ReadBool(data, "questionOpen", true)
                                || day - ReadDouble(row, "acquired_day", 0) > 14) continue;
                            double uncertainty = ReadString(data, "claimKind", "") == "question" ? (ReadBool(data, "questionOpen", true) ? 75 : 0)
                                : ReadString(data, "claimKind", "") == "impression" ? 0 : 100 - ReadInt(data, "credibility", 50);
                            int chance = GossipRaiseChance(GossipTrait(traits, "curiosity"), ReadInt(data, "personalStake", 25), uncertainty, ReadInt(data, "feeling", 0));
                            if (ReadInt(data, "raiseRoll", GossipRoll(ReadString(row, "root_id", "") + "|" + hero, "raise")) <= chance) nominations.Add(id);
                        }
                        payload["personalGossipAgenda"] = nominations;
                        text.AppendLine(nominations.Count == 0 ? "Do not initiate a gossip topic in this conversation. Answer normally if the player asks."
                            : "You may naturally raise this one topic when current business allows: " + nominations[0] + ". It need not be your first line. Other gossip is recall only unless the player asks.");
                    }
                }
                SchedulePersonalGossip(campaign);
                return text.ToString();
            }
            catch (Exception ex)
            {
                LogOperational("gossip.prompt_failed", GossipFields("campaignId", campaign, "error", LimitText(ex.Message, 500)));
                return "";
            }
        }

        private static void MarkPersonalGossipRaised(ReignDbConnection c, string timeline, string hero, string session,
            Dictionary<string, object> parsed, string reply, Dictionary<string, object> payload)
        {
            if (string.IsNullOrWhiteSpace(session)) return;
            var nominated = ReadStringList(payload, "personalGossipAgenda");
            if (QuerySql(c, "SELECT receipt_id FROM personal_gossip_history WHERE timeline_id=$timeline AND owner_id=$owner AND raise_session=$session LIMIT 1;",
                GossipFields("timeline", timeline, "owner", hero, "session", session)).Count > 0) return;
            foreach (var item in ReadDictionaryList(parsed, "raisedGossip").Take(1))
                if (nominated.Contains(ReadString(item, "receiptId", "")) && GossipQuoteSupported(ReadString(item, "evidenceQuote", ""), reply))
                    ExecuteSql(c, @"UPDATE personal_gossip_history SET raise_session=$session WHERE receipt_id=$id AND timeline_id=$timeline
AND owner_id=$owner AND raise_session='' AND acquired_day<=$day;", GossipFields("id", ReadString(item, "receiptId", ""), "timeline", timeline,
                        "owner", hero, "session", session, "day", ReadDouble(payload, "worldDay", 0)));
        }

        private static Dictionary<string, object> PersonalGossipStatusApi(Dictionary<string, string> query)
        {
            string campaign = query != null && query.TryGetValue("campaignId", out string id) ? id : "";
            if (string.IsNullOrWhiteSpace(campaign)) return GossipFields("ok", false, "error", "Campaign is required.");
            using (var c = OpenCampaignConnection(campaign))
            {
                if (!TableExists(c, "personal_gossip_messages")) return GossipFields("ok", true, "messages", new List<object>(), "topics", new List<object>());
                return GossipFields("ok", true, "policyVersion", PersonalGossipVersion,
                    "messages", QuerySql(c, "SELECT message_id,root_id,timeline_id,speaker_id,recipient_id,is_reply,hop,created_day,due_day,expires_day,status,attempts FROM personal_gossip_messages ORDER BY created_day DESC,message_id LIMIT 100;"),
                    "topics", QuerySql(c, "SELECT topic_id,speaker_id,created_day,status,payload_json FROM personal_gossip_topics ORDER BY created_day DESC,topic_id LIMIT 30;")
                        .Select(row => GossipFields("id", ReadString(row, "topic_id", ""), "speakerId", ReadString(row, "speaker_id", ""),
                            "status", ReadString(row, "status", ""), "shareChance", ReadInt(GossipJson(row), "shareChance", 0),
                            "shareRoll", ReadInt(GossipJson(row), "shareRoll", 0), "candidateAudit", ReadDictionary(GossipJson(row), "candidateAudit"))).ToList());
            }
        }
    }
}
