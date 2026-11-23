using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Reputation module's SQL statements used only by its self-tests (verification build only); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class ReputationSelfTestStore
    {
        internal static List<Dictionary<string, object>> CountCourtRulerFavorContact(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM court_ruler_favor_contact;", parameters, transaction);
        }

        internal static void InsertOrIgnoreIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO identity_roster(hero_id,canonical_name,is_alive,is_adult,is_player,is_ruler,kingdom_id)
VALUES($ruler,$ruler,1,1,0,1,'fixture_realm') ON CONFLICT(hero_id) DO NOTHING;", parameters, transaction);
        }

        internal static void InsertCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_reputations
(campaign_id,timeline_id,subject_id,tag_id,acquired_day,catalog_revision,snapshot_json,updated_ts)
VALUES($campaign,$timeline,$ruler,$tag,0,1,$snapshot,0);", parameters, transaction);
        }

        internal static void InsertOrIgnoreCourtRulerFavorContact(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_ruler_favor_contact
(campaign_id,timeline_id,ruler_id,favorite_id,last_contact_day,grace_day)
VALUES($campaign,$timeline,$ruler,$favorite,$anchor,10)
ON CONFLICT(campaign_id,timeline_id,ruler_id,favorite_id) DO NOTHING;", parameters, transaction);
        }

        internal static void InsertCourtRulerFavorContact(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_ruler_favor_contact
(campaign_id,timeline_id,ruler_id,favorite_id,last_contact_day,grace_day)
VALUES($campaign,'main','null_anchor','favorite',NULL,60);
UPDATE court_ruler_favor_contact SET expired_day=80 WHERE ruler_id='old';
INSERT INTO court_player_favoring_exchanges
(campaign_id,timeline_id,ruler_id,favorite_id,exchange_count,last_world_day,updated_ts)
VALUES($campaign,'main','zero_exchange','favorite',0,1,0),
($campaign,'main','exchange','favorite',1,50,0);
INSERT INTO rumor_occurrences
(occurrence_id,campaign_id,timeline_id,archetype_id,thread_key,world_day,expires_day,catalog_revision,created_ts,updated_ts)
VALUES('mixed',$campaign,'main','ruler_favoring_dialogue','mixed',0,200,1,0,0),
('only_favor',$campaign,'main','ruler_favoring_dialogue','only',0,200,1,0,0);
INSERT INTO rumor_subject_tags(occurrence_id,subject_id,tag_id,snapshot_json,updated_ts)
VALUES('mixed','duplicate','ruler_favoring:duplicate_a','{""linkedHeroId"":""favorite"",""extra"":true}',0),
('mixed','duplicate','neutral_fixture','{}',0),
('only_favor','old','ruler_favoring:old','{""linkedHeroId"":""favorite""}',0);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamic(ReignDbConnection connection, object item1, object item2, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM " + item1 + " ORDER BY " + item2 + ";", parameters, transaction);
        }

        internal static void InsertOrIgnoreCourtRulerFavorContact2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_ruler_favor_contact
(campaign_id,timeline_id,ruler_id,favorite_id,last_contact_day,grace_day)
VALUES($campaign,$timeline,$ruler,$favorite,
(SELECT last_world_day FROM court_player_favoring_exchanges WHERE campaign_id=$campaign AND timeline_id=$timeline AND ruler_id=$ruler AND favorite_id=$favorite AND exchange_count>0),$day)
ON CONFLICT(campaign_id,timeline_id,ruler_id,favorite_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtRulerFavorContact(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM court_ruler_favor_contact WHERE campaign_id=$campaign AND timeline_id=$timeline AND ruler_id=$ruler AND favorite_id=$favorite;", parameters, transaction);
        }

        internal static void UpdateCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE character_reputations SET status='forgotten',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$ruler AND tag_id=$tag AND status='active';", parameters, transaction);
        }

        internal static void UpdateRumorSubjectTags(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE rumor_subject_tags SET status='forgotten',updated_ts=$ts
WHERE subject_id=$ruler AND tag_id=$tag AND status='active' AND occurrence_id IN
(SELECT occurrence_id FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline);", parameters, transaction);
        }

        internal static void UpdateRumorOccurrences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
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

        internal static List<Dictionary<string, object>> SelectWhoremongerActivity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT visit_count FROM whoremonger_activity WHERE subject_id='wm_player' AND timeline_id='whoremonger_test';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWhoremongerActivity2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT visit_count,last_activity_day FROM whoremonger_activity WHERE subject_id='wm_player' AND timeline_id='whoremonger_test';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWhoremongerEventReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT subject_id,world_day,result_json
FROM whoremonger_event_receipts WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_id='entry:unavailable_npc_entry';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWhoremongerActivity3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT last_event_day FROM whoremonger_activity
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id='wm_npc';", parameters, transaction);
        }

        internal static void InsertSocialOutcomeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO social_outcome_receipts(
event_id,campaign_id,timeline_id,subject_id,archetype_id,counter_applied,
completed,result_json,attempt_count,last_error,updated_ts)
VALUES('late_npc_recovery',$campaign,$timeline,'','',0,-1,'{}',3,$error,1);", parameters, transaction);
        }

        internal static void InsertSocialOutcomeReceipts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO social_outcome_receipts(
event_id,campaign_id,timeline_id,subject_id,archetype_id,counter_applied,
completed,result_json,attempt_count,last_error,updated_ts)
VALUES('departed_npc_recovery',$campaign,$timeline,'','',0,-1,'{}',3,$error,1);", parameters, transaction);
        }

        internal static void InsertSocialOutcomeReceipts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO social_outcome_receipts(
event_id,campaign_id,timeline_id,subject_id,archetype_id,counter_applied,
completed,result_json,attempt_count,last_error,updated_ts)
VALUES('stalled_npc_recovery',$campaign,$timeline,'','',0,0,'{}',0,'',1);", parameters, transaction);
        }

        internal static void UpsertWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_events(
event_id,campaign_id,timeline_id,sequence,world_day,event_type,subject_id,created_utc)
VALUES('late_npc_recovery',$campaign,$timeline,900001,10,'social_outcome','wm_npc',$utc),
('departed_npc_recovery',$campaign,$timeline,900002,11,'social_outcome','wm_npc',$utc),
('stalled_npc_recovery',$campaign,$timeline,900003,12,'social_outcome','wm_npc',$utc);
UPDATE world_history_events SET correlation_id='whoremonger_town_entry|wm_npc|3|town_a'
WHERE event_id='stalled_npc_recovery';
INSERT INTO social_subject_work_queue(campaign_id,timeline_id,subject_id,status,
first_sequence,pending_count,claim_owner,claim_expires_ts,updated_ts,last_error)
VALUES($campaign,$timeline,'wm_npc','completed',900001,0,'',0,1,'')
ON CONFLICT(campaign_id,timeline_id,subject_id) DO UPDATE SET
status='completed',first_sequence=900001,pending_count=0,claim_owner='',
claim_expires_ts=0,updated_ts=1,last_error='';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialOutcomeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT event_id,completed,attempt_count,last_error
FROM social_outcome_receipts WHERE event_id IN ('late_npc_recovery','departed_npc_recovery','stalled_npc_recovery')
ORDER BY event_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialSubjectWorkQueue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT status,first_sequence,pending_count,last_error
FROM social_subject_work_queue WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id='wm_npc';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWhoremongerActivity4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT visit_count,last_event_day,last_activity_day FROM whoremonger_activity
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWhoremongerEventReceipts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT event_id,world_day FROM whoremonger_event_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorExposureStreaks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT streak_count,last_exposure_day FROM rumor_exposure_streaks
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id='whoremonger'
AND thread_key=$subject||'|whoremonger';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWhoremongerActivity5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT visit_count,last_activity_day FROM whoremonger_activity
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject;", parameters, transaction);
        }
    }
}
