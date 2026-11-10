using System;
using System.Collections.Generic;
using System.Linq;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static bool IsQualifyingFavorConversationTurn(Dictionary<string, object> turn)
        {
            var payload = TryParseJsonObject(ReadString(turn, "payload_json", "{}"));
            if (ReadString(payload, "mode", "") != "court_life" && ReadString(payload, "conversationMode", "") != "court_life") return true;
            return ReadBool(payload, "actualPlayerTurn", false) && ReadString(payload, "courtLifePhase", "") == "conversation";
        }
        private static void EnsureRulerFavorContactSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_ruler_favor_contact (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,ruler_id TEXT NOT NULL,favorite_id TEXT NOT NULL,
last_contact_day REAL,grace_day REAL NOT NULL,expired_day REAL,contact_id TEXT NOT NULL DEFAULT '',
PRIMARY KEY(campaign_id,timeline_id,ruler_id,favorite_id));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_ruler_favor_contact_receipts (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,ruler_id TEXT NOT NULL,favorite_id TEXT NOT NULL,
contact_id TEXT NOT NULL,world_day REAL NOT NULL,PRIMARY KEY(campaign_id,timeline_id,ruler_id,favorite_id,contact_id));");
        }

        private static void RecordRulerFavorContact(ReignDbConnection connection, string campaignId, string timelineId,
            string firstId, string secondId, double day, string contactId)
        {
            if (string.IsNullOrWhiteSpace(contactId) || firstId == secondId) return;
            EnsureSocialReputationSchema(connection);
            ExpireRulerFavorContact(connection, campaignId, timelineId, day);
            foreach (string rulerId in new[] { firstId, secondId })
            {
                string favoriteId = rulerId == firstId ? secondId : firstId;
                var ruler = CourtConversationParticipant(connection, rulerId);
                if (ruler == null || !ReadBool(ruler, "isRuler", false)) continue;
                var receipt = new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["ruler"] = rulerId, ["favorite"] = favoriteId, ["day"] = day, ["contact"] = contactId };
                if (QuerySql(connection, @"SELECT 1 FROM court_ruler_favor_contact_receipts WHERE campaign_id=$campaign
AND timeline_id=$timeline AND ruler_id=$ruler AND favorite_id=$favorite AND contact_id=$contact;", receipt).Any()) continue;
                ExecuteSql(connection, @"INSERT INTO court_ruler_favor_contact_receipts
(campaign_id,timeline_id,ruler_id,favorite_id,contact_id,world_day) VALUES($campaign,$timeline,$ruler,$favorite,$contact,$day)
ON CONFLICT(campaign_id,timeline_id,ruler_id,favorite_id,contact_id) DO NOTHING;", receipt);
                ExecuteSql(connection, @"INSERT INTO court_ruler_favor_contact
(campaign_id,timeline_id,ruler_id,favorite_id,last_contact_day,grace_day,contact_id)
VALUES($campaign,$timeline,$ruler,$favorite,$day,$day,$contact)
ON CONFLICT(campaign_id,timeline_id,ruler_id,favorite_id) DO UPDATE SET
last_contact_day=CASE WHEN court_ruler_favor_contact.last_contact_day IS NULL OR court_ruler_favor_contact.last_contact_day<$day THEN $day ELSE court_ruler_favor_contact.last_contact_day END,
contact_id=$contact,expired_day=CASE WHEN court_ruler_favor_contact.expired_day IS NOT NULL AND $day<court_ruler_favor_contact.expired_day THEN court_ruler_favor_contact.expired_day ELSE NULL END;", new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["ruler"] = rulerId, ["favorite"] = favoriteId, ["day"] = day, ["contact"] = contactId });
            }
        }

        private static List<Dictionary<string, object>> ActiveRulerFavorRows(ReignDbConnection connection, string campaignId, string timelineId, double day)
        {
            return QuerySql(connection, @"SELECT cr.subject_id,cr.tag_id,cr.snapshot_json FROM character_reputations cr
WHERE cr.campaign_id=$campaign AND cr.timeline_id=$timeline AND cr.status='active'
AND (cr.tag_id='ruler_favoring' OR cr.tag_id LIKE 'ruler_favoring:%')
UNION SELECT rst.subject_id,rst.tag_id,rst.snapshot_json FROM rumor_subject_tags rst
JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline AND rst.status='active' AND ro.status='active' AND ro.expires_day>$day
AND (rst.tag_id='ruler_favoring' OR rst.tag_id LIKE 'ruler_favoring:%');",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = day });
        }

        private const int RulerFavorContactLookupBatchSize = 256;

        private static Dictionary<(string RulerId, string FavoriteId), Dictionary<string, object>> InitializeRulerFavorContacts(
            ReignDbConnection connection, string campaignId, string timelineId, double day,
            List<Dictionary<string, object>> tags)
        {
            // PostgreSQL's text keys are case sensitive. Dedupe only lookup keys;
            // the original tag order and repeated expiry writes remain authoritative.
            var keys = tags.Select(tag => (RulerId: ReadString(tag, "subject_id", ""),
                    FavoriteId: ReadString(TryParseJsonObject(ReadString(tag, "snapshot_json", "{}")), "linkedHeroId", "")))
                .Where(key => !string.IsNullOrWhiteSpace(key.FavoriteId)).Distinct()
                .OrderBy(key => key.RulerId, StringComparer.Ordinal)
                .ThenBy(key => key.FavoriteId, StringComparer.Ordinal).ToList();
            var contacts = new Dictionary<(string RulerId, string FavoriteId), Dictionary<string, object>>();
            for (int offset = 0; offset < keys.Count; offset += RulerFavorContactLookupBatchSize)
            {
                var batch = keys.Skip(offset).Take(RulerFavorContactLookupBatchSize)
                    .Select(key => new Dictionary<string, object> {
                        ["ruler_id"] = key.RulerId, ["favorite_id"] = key.FavoriteId }).ToList();
                var parameters = new Dictionary<string, object> {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["day"] = day, ["keys"] = Json.Serialize(batch) };
                // Expiring one pair never changes another pair's exchange anchor.
                // Initialize all anchors before expiry writes, then retain the
                // existing per-tag effects, including duplicate-pair tags.
                ExecuteSql(connection, @"INSERT INTO court_ruler_favor_contact
(campaign_id,timeline_id,ruler_id,favorite_id,last_contact_day,grace_day)
SELECT $campaign,$timeline,ids.ruler_id,ids.favorite_id,
(SELECT last_world_day FROM court_player_favoring_exchanges exchanges
 WHERE exchanges.campaign_id=$campaign AND exchanges.timeline_id=$timeline
 AND exchanges.ruler_id=ids.ruler_id AND exchanges.favorite_id=ids.favorite_id AND exchange_count>0),$day
FROM jsonb_to_recordset(CAST($keys AS jsonb)) AS ids(ruler_id TEXT,favorite_id TEXT)
WHERE TRUE
ON CONFLICT(campaign_id,timeline_id,ruler_id,favorite_id) DO NOTHING;", parameters);
                foreach (var contact in QuerySql(connection, @"SELECT contact.* FROM court_ruler_favor_contact contact
JOIN jsonb_to_recordset(CAST($keys AS jsonb)) AS ids(ruler_id TEXT,favorite_id TEXT)
 ON contact.ruler_id=ids.ruler_id AND contact.favorite_id=ids.favorite_id
WHERE contact.campaign_id=$campaign AND contact.timeline_id=$timeline;", parameters))
                    contacts.Add((ReadString(contact, "ruler_id", ""), ReadString(contact, "favorite_id", "")), contact);
            }
            return contacts;
        }

        private static List<string> ExpireRulerFavorContact(ReignDbConnection connection, string campaignId, string timelineId, double day)
        {
            EnsureRulerFavorContactSchema(connection);
            var changed = new List<string>();
            var tags = ActiveRulerFavorRows(connection, campaignId, timelineId, day);
            var contacts = InitializeRulerFavorContacts(connection, campaignId, timelineId, day, tags);
            foreach (var tag in tags)
            {
                var snapshot = TryParseJsonObject(ReadString(tag, "snapshot_json", "{}"));
                string rulerId = ReadString(tag, "subject_id", ""), favoriteId = ReadString(snapshot, "linkedHeroId", "");
                if (string.IsNullOrWhiteSpace(favoriteId)) continue;
                var p = new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["ruler"] = rulerId, ["favorite"] = favoriteId, ["day"] = day, ["tag"] = ReadString(tag, "tag_id", ""),
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                if (!contacts.TryGetValue((rulerId, favoriteId), out var contact))
                    throw new InvalidOperationException("Ruler-favor contact initialization returned no matching row.");
                object contactDay;
                double anchor = contact.TryGetValue("last_contact_day", out contactDay) && contactDay != null && contactDay != DBNull.Value
                    ? ReadDouble(contact, "last_contact_day", day) : ReadDouble(contact, "grace_day", day);
                if (!ReignFamilyVisitRules.FavorExpired(anchor, day)) continue;
                ExecuteSql(connection, @"UPDATE character_reputations SET status='forgotten',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$ruler AND tag_id=$tag AND status='active';", p);
                ExecuteSql(connection, @"UPDATE rumor_subject_tags SET status='forgotten',updated_ts=$ts
WHERE subject_id=$ruler AND tag_id=$tag AND status='active' AND occurrence_id IN
(SELECT occurrence_id FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline);", p);
                ExecuteSql(connection, @"UPDATE rumor_occurrences SET status='forgotten',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='active'
AND occurrence_id IN (SELECT occurrence_id FROM rumor_subject_tags WHERE subject_id=$ruler AND tag_id=$tag AND status='forgotten')
AND NOT EXISTS (SELECT 1 FROM rumor_subject_tags live WHERE live.occurrence_id=rumor_occurrences.occurrence_id AND live.status='active');", p);
                ExecuteSql(connection, @"UPDATE court_ruler_favor_contact SET expired_day=$day
WHERE campaign_id=$campaign AND timeline_id=$timeline AND ruler_id=$ruler AND favorite_id=$favorite;", p);
                ExecuteSql(connection, @"UPDATE court_player_favoring_exchanges SET exchange_count=0
WHERE campaign_id=$campaign AND timeline_id=$timeline AND ruler_id=$ruler AND favorite_id=$favorite;", p);
                changed.Add(rulerId);
            }
            var subjects = changed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (subjects.Count > 0) RecomputePublicStandingForSubjects(connection, campaignId, timelineId, subjects, day);
            return subjects;
        }

        private static bool RulerFavorRequiresFreshContact(ReignDbConnection connection, string campaignId, string timelineId,
            string rulerId, string favoriteId)
        {
            EnsureRulerFavorContactSchema(connection);
            return QuerySql(connection, @"SELECT 1 FROM court_ruler_favor_contact
WHERE campaign_id=$campaign AND timeline_id=$timeline AND ruler_id=$ruler AND favorite_id=$favorite
AND expired_day IS NOT NULL AND (last_contact_day IS NULL OR last_contact_day<=expired_day) LIMIT 1;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["ruler"] = rulerId, ["favorite"] = favoriteId }).Any();
        }

        private static List<string> CurrentRulerFavorites(ReignDbConnection connection, string campaignId, string timelineId, string rulerId, double day)
        {
            return ActiveRulerFavorRows(connection, campaignId, timelineId, day)
                .Where(row => ReadString(row, "subject_id", "").Equals(rulerId, StringComparison.OrdinalIgnoreCase))
                .Select(row => ReadString(TryParseJsonObject(ReadString(row, "snapshot_json", "{}")), "linkedHeroId", ""))
                .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void RecordCompletedFavorCorrespondence(ReignDbConnection connection, string campaignId,
            string timelineId, Dictionary<string, object> reply, double day)
        {
            string parentId = ReadString(reply, "parent_letter_id", "");
            string sender = ReadString(reply, "sender_id", ""), recipient = ReadString(reply, "recipient_id", "");
            if (string.IsNullOrWhiteSpace(parentId) || string.IsNullOrWhiteSpace(ReadString(reply, "body", "")) || !IsPersonalFavorLetter(reply)) return;
            var parent = QuerySql(connection, @"SELECT * FROM letters WHERE letter_id=$id AND sender_id=$recipient
AND recipient_id=$sender AND status IN ('delivered','read') LIMIT 1;",
                new Dictionary<string, object> { ["id"] = parentId, ["sender"] = sender, ["recipient"] = recipient }).FirstOrDefault();
            if (parent == null || string.IsNullOrWhiteSpace(ReadString(parent, "body", "")) || !IsPersonalFavorLetter(parent)) return;
            RecordRulerFavorContact(connection, campaignId, timelineId, sender, recipient, day,
                "correspondence:" + ReadString(reply, "letter_id", ""));
        }

        private static bool IsPersonalFavorLetter(Dictionary<string, object> letter)
        {
            string source = ReadString(letter, "source", "");
            return source == "player" || source == "npc_reply" || source == "npc_initiated" || source == "relationship_development";
        }
        private static string SharedFavorJealousyContext(ReignDbConnection connection, string campaignId, string timelineId,
            string observerId, string subjectId, double day)
        {
            var byRuler = ActiveRulerFavorRows(connection, campaignId, timelineId, day).GroupBy(row => ReadString(row, "subject_id", ""));
            foreach (var group in byRuler)
            {
                var favorites = group.Select(row => ReadString(TryParseJsonObject(ReadString(row, "snapshot_json", "{}")), "linkedHeroId", "")).ToList();
                if (favorites.Contains(observerId) && favorites.Contains(subjectId)
                    && IdentityStateVerified(ReadAcquaintance(connection, observerId, group.Key)))
                    return "You and this person both currently receive the same ruler's favor. You have less reason to resent lack of attention. Moderate attention-based jealousy while preserving other motives and your temperament; favor is not evidence of an affair.";
            }
            return "";
        }
    }
}
