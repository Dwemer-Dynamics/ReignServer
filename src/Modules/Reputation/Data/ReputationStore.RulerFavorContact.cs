using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class ReputationStore
    {
        internal static List<Dictionary<string, object>> SelectCourtRulerFavorContactReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 FROM court_ruler_favor_contact_receipts WHERE campaign_id=$campaign
AND timeline_id=$timeline AND ruler_id=$ruler AND favorite_id=$favorite AND contact_id=$contact;", parameters, transaction);
        }

        internal static void InsertOrIgnoreCourtRulerFavorContactReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_ruler_favor_contact_receipts
(campaign_id,timeline_id,ruler_id,favorite_id,contact_id,world_day) VALUES($campaign,$timeline,$ruler,$favorite,$contact,$day)
ON CONFLICT(campaign_id,timeline_id,ruler_id,favorite_id,contact_id) DO NOTHING;", parameters, transaction);
        }

        internal static void UpsertCourtRulerFavorContact(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_ruler_favor_contact
(campaign_id,timeline_id,ruler_id,favorite_id,last_contact_day,grace_day,contact_id)
VALUES($campaign,$timeline,$ruler,$favorite,$day,$day,$contact)
ON CONFLICT(campaign_id,timeline_id,ruler_id,favorite_id) DO UPDATE SET
last_contact_day=CASE WHEN court_ruler_favor_contact.last_contact_day IS NULL OR court_ruler_favor_contact.last_contact_day<$day THEN $day ELSE court_ruler_favor_contact.last_contact_day END,
contact_id=$contact,expired_day=CASE WHEN court_ruler_favor_contact.expired_day IS NOT NULL AND $day<court_ruler_favor_contact.expired_day THEN court_ruler_favor_contact.expired_day ELSE NULL END;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT cr.subject_id,cr.tag_id,cr.snapshot_json FROM character_reputations cr
WHERE cr.campaign_id=$campaign AND cr.timeline_id=$timeline AND cr.status='active'
AND (cr.tag_id='ruler_favoring' OR cr.tag_id LIKE 'ruler_favoring:%')
UNION SELECT rst.subject_id,rst.tag_id,rst.snapshot_json FROM rumor_subject_tags rst
JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline AND rst.status='active' AND ro.status='active' AND ro.expires_day>$day
AND (rst.tag_id='ruler_favoring' OR rst.tag_id LIKE 'ruler_favoring:%');", parameters, transaction);
        }

        internal static void InsertOrIgnoreCourtRulerFavorContact(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_ruler_favor_contact
(campaign_id,timeline_id,ruler_id,favorite_id,last_contact_day,grace_day)
SELECT $campaign,$timeline,ids.ruler_id,ids.favorite_id,
(SELECT last_world_day FROM court_player_favoring_exchanges exchanges
 WHERE exchanges.campaign_id=$campaign AND exchanges.timeline_id=$timeline
 AND exchanges.ruler_id=ids.ruler_id AND exchanges.favorite_id=ids.favorite_id AND exchange_count>0),$day
FROM jsonb_to_recordset(CAST($keys AS jsonb)) AS ids(ruler_id TEXT,favorite_id TEXT)
WHERE TRUE
ON CONFLICT(campaign_id,timeline_id,ruler_id,favorite_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtRulerFavorContact(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT contact.* FROM court_ruler_favor_contact contact
JOIN jsonb_to_recordset(CAST($keys AS jsonb)) AS ids(ruler_id TEXT,favorite_id TEXT)
 ON contact.ruler_id=ids.ruler_id AND contact.favorite_id=ids.favorite_id
WHERE contact.campaign_id=$campaign AND contact.timeline_id=$timeline;", parameters, transaction);
        }

        internal static void UpdateCharacterReputations8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE character_reputations SET status='forgotten',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$ruler AND tag_id=$tag AND status='active';", parameters, transaction);
        }

        internal static void UpdateRumorSubjectTags3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE rumor_subject_tags SET status='forgotten',updated_ts=$ts
WHERE subject_id=$ruler AND tag_id=$tag AND status='active' AND occurrence_id IN
(SELECT occurrence_id FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline);", parameters, transaction);
        }

        internal static void UpdateRumorOccurrences2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE rumor_occurrences SET status='forgotten',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='active'
AND occurrence_id IN (SELECT occurrence_id FROM rumor_subject_tags WHERE subject_id=$ruler AND tag_id=$tag AND status='forgotten')
AND NOT EXISTS (SELECT 1 FROM rumor_subject_tags live WHERE live.occurrence_id=rumor_occurrences.occurrence_id AND live.status='active');", parameters, transaction);
        }

        internal static void UpdateCourtRulerFavorContact(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE court_ruler_favor_contact SET expired_day=$day
WHERE campaign_id=$campaign AND timeline_id=$timeline AND ruler_id=$ruler AND favorite_id=$favorite;", parameters, transaction);
        }

        internal static void UpdateCourtPlayerFavoringExchanges(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE court_player_favoring_exchanges SET exchange_count=0
WHERE campaign_id=$campaign AND timeline_id=$timeline AND ruler_id=$ruler AND favorite_id=$favorite;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtRulerFavorContact2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 FROM court_ruler_favor_contact
WHERE campaign_id=$campaign AND timeline_id=$timeline AND ruler_id=$ruler AND favorite_id=$favorite
AND expired_day IS NOT NULL AND (last_contact_day IS NULL OR last_contact_day<=expired_day) LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM letters WHERE letter_id=$id AND sender_id=$recipient
AND recipient_id=$sender AND status IN ('delivered','read') LIMIT 1;", parameters, transaction);
        }
    }
}
