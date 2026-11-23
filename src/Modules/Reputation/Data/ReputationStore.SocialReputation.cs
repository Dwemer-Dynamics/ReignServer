using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class ReputationStore
    {
        internal static List<Dictionary<string, object>> SelectCharacterPublicStanding4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT subject_id,standing_value,revision,last_changed_day,sources_json
FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id='main'
ORDER BY subject_id;", parameters, transaction);
        }

        internal static void InsertOrIgnoreRelationshipPairChemistry2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,base_chance_a_to_b,base_chance_b_to_a,
chance_a_to_b,chance_b_to_a,affinity_a_to_b,affinity_b_to_a,
effective_affinity_a_to_b,effective_affinity_b_to_a,first_day,last_day,updated_ts)
VALUES($pair,$a,$b,'ENFJ','INTJ',50,50,50,50,0,0,0,0,1,1,$ts)
ON CONFLICT(pair_key) DO NOTHING;", parameters, transaction);
        }

        internal static void DeleteDirectionalSocialProjections(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM directional_social_projections
WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='player_subject';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT pair_key,affinity_a_to_b,affinity_b_to_a,updated_ts
FROM relationship_pair_chemistry
WHERE pair_key IN ($marriedPair,$loverPair)
ORDER BY pair_key;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountDirectionalSocialProjections(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM directional_social_projections
WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='player_subject';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT world_day,expires_day FROM rumor_occurrences WHERE occurrence_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations18(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT tag_id FROM character_reputations WHERE subject_id='married';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations19(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT tag_id FROM character_reputations WHERE subject_id='lover';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT 1 FROM rumor_subject_tags WHERE status='active';", parameters, transaction);
        }

        internal static void UpdateIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE identity_roster SET current_charm=100 WHERE hero_id='married';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations20(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT reputation_value FROM character_reputations WHERE subject_id='married' AND tag_id='divorcee';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations21(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT reputation_value FROM character_reputations WHERE subject_id='lover' AND tag_id='divorcee';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT 1 FROM rumor_occurrences WHERE occurrence_id=$id AND status='disproven';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations22(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT 1 FROM character_reputations WHERE status='active';", parameters, transaction);
        }

        internal static void UpsertRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,
effective_affinity_a_to_b,effective_affinity_b_to_a,first_day,last_day,updated_ts)
VALUES($pair,$a,$b,$ab,$ba,$ab,$ba,1,1,$ts)
ON CONFLICT(pair_key) DO UPDATE SET affinity_a_to_b=$ab,affinity_b_to_a=$ba,
effective_affinity_a_to_b=$ab,effective_affinity_b_to_a=$ba,updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtDynamicReputationMetrics2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT *
FROM court_dynamic_reputation_metrics WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='court_subject' AND domain='popularity' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations23(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT tag_id,reputation_value
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='court_subject' AND status='active'
AND tag_id IN ('well_regarded','court_favorite','beloved_of_the_court',
'ill_regarded','shunned_at_court','court_pariah') ORDER BY tag_id;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET social_modifier_a_to_b=50,social_modifier_b_to_a=-50,
effective_affinity_a_to_b=100,effective_affinity_b_to_a=-100
WHERE hero_a_id='court_subject' OR hero_b_id='court_subject';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations24(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT tag_id,
reputation_value,snapshot_json FROM character_reputations WHERE campaign_id=$campaign
AND timeline_id='main' AND subject_id='court_subject' AND status='active'
AND tag_id LIKE 'ruler_favoring:%' ORDER BY tag_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations25(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT reputation_value,status
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='court_subject' AND tag_id='dynasty_secure' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations26(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT tag_id,status
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='court_subject' AND tag_id IN ('incompetent','cruel','dishonorable','divorcee','disloyal',
'promiscuous','the_unchaste','corrupt','coward','traitor','murderous','murderer','convicted_murderer');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations27(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT ra.source_event_id,ra.evidence_json
FROM character_reputations cr JOIN reputation_activations ra
ON ra.activation_id=cr.reason_activation_id
WHERE cr.campaign_id=$campaign AND cr.timeline_id='main' AND cr.subject_id='court_subject'
AND cr.tag_id='incompetent' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations28(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT tag_id,status
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='unauthorized_judgment_subject' AND tag_id='flirt' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations29(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT reputation_value
FROM character_reputations WHERE campaign_id=$campaign AND subject_id='court_male'
AND tag_id='the_unchaste' AND status='active' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations30(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT reputation_value
FROM character_reputations WHERE campaign_id=$campaign AND subject_id='court_female'
AND tag_id='the_unchaste' AND status='active' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSharedTagExposureStreaks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
streak_count,last_exposure_day FROM shared_tag_exposure_streaks
WHERE campaign_id=$campaign AND timeline_id='main' AND subject_id='court_male'
AND tag_id='promiscuous' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountCourtConversationSignalReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM court_conversation_signal_receipts WHERE campaign_id=$campaign AND session_id='court_signal_unvalidated';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountCourtConversationSignalReceipts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM court_conversation_signal_receipts WHERE campaign_id=$campaign AND session_id='court_signal_validated';", parameters, transaction);
        }

        internal static void DeleteAcquaintances(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM acquaintances
WHERE (observer_id='group_intro_alpha' AND subject_id='group_intro_beta')
   OR (observer_id='group_intro_beta' AND subject_id='group_intro_alpha');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 FROM rumor_occurrences
WHERE campaign_id=$campaign AND archetype_id='strong_captain' AND thread_key='player_subject|strong_captain' AND world_day=30;", parameters, transaction);
        }

        internal static void UpsertCharacterReputations4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_reputations(
campaign_id,timeline_id,subject_id,tag_id,source_occurrence_id,archetype_id,
subject_role,description,reputation_value,acquired_day,catalog_revision,
snapshot_json,status,updated_ts)
VALUES($campaign,'main','player_subject','strong_captain','counter_fixture',
'strong_captain','commander','Counterevidence fixture reputation.',10,30.5,
$revision,'{}','active',$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,tag_id) DO UPDATE SET
status='active',updated_ts=excluded.updated_ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations31(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT campaign_id,timeline_id,subject_id,tag_id,status
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='player_subject' AND tag_id='strong_captain' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectReputationCounterevidence2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT consecutive_count,last_chance
FROM reputation_counterevidence WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='player_subject' AND tag_id='strong_captain' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectReputationCounterevidence3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT consecutive_count
FROM reputation_counterevidence WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='player_subject' AND tag_id='strong_captain' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT status,expires_day FROM rumor_occurrences
WHERE occurrence_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations32(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id='main' AND subject_id='player_subject'
AND tag_id='war_crowned' AND status='active' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations33(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 FROM character_reputations
WHERE campaign_id=$campaign AND subject_id='player_subject' AND tag_id='battlemaster' AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations34(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 FROM character_reputations
WHERE campaign_id=$campaign AND subject_id='player_subject' AND tag_id='steward_of_the_realm' AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectReputationReasonJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 FROM reputation_reason_jobs j
JOIN reputation_activations a ON a.activation_id=j.activation_id
WHERE a.subject_id='player_subject' AND a.tag_id='fallen_challenger';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations35(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 FROM character_reputations
WHERE subject_id='player_subject' AND tag_id='fallen_challenger'
AND reason_text='The challenger was decisively defeated before assembled witnesses.'
AND reason_status='recorded';", parameters, transaction);
        }

        internal static void InsertWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_events(
event_id,campaign_id,timeline_id,sequence,world_day,event_type,summary,payload_json,created_utc)
VALUES('social_batch_poison',$campaign,'main',9000,40,'social_outcome','Malformed social outcome','{}',$created);", parameters, transaction);
        }

        internal static void InsertWorldHistoryEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_events(
event_id,campaign_id,timeline_id,sequence,world_day,event_type,summary,payload_json,created_utc)
VALUES($event,$campaign,'main',$sequence,40,'social_outcome','Counter-only batch fixture',$payload,$created);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountSocialOutcomeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM social_outcome_receipts WHERE event_id LIKE 'social_batch_%' AND completed=1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialOutcomeReceipts5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT completed,attempt_count,last_error
FROM social_outcome_receipts WHERE event_id='social_batch_poison' LIMIT 1;", parameters, transaction);
        }

        internal static void InsertRumorOccurrences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO rumor_occurrences(occurrence_id,campaign_id,timeline_id,archetype_id,thread_key,
world_day,expires_day,status,catalog_revision,participants_json,created_ts,updated_ts)
VALUES($id,$campaign,'main','bulk_fixture',$id,25,55,'active',1,'[]',$ts,$ts);", parameters, transaction);
        }

        internal static void InsertRumorSubjectTags(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO rumor_subject_tags(occurrence_id,subject_id,tag_id,description,rumor_value,
reputation_value,status,updated_ts) VALUES($id,'bulk_subject',$tag,'Bulk performance fixture',-1,-1,'active',$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSqliteMaster4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT name FROM sqlite_master WHERE type='table';", parameters, transaction);
        }

        internal static void UpsertSocialCatalogRevisions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO social_catalog_revisions(revision,catalog_json,created_ts)
VALUES($revision,$catalog,$ts)
ON CONFLICT(revision) DO UPDATE SET catalog_json=excluded.catalog_json;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('social_reputation_schema_version',$version);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='character_owned_social_cleanup_v1' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSqliteMaster5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='conversation_sessions' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSqliteMaster6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='summaries' LIMIT 1;", parameters, transaction);
        }

        internal static void DeleteSummaries(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM summaries WHERE summary_id IN (
SELECT scene_summary_id FROM conversation_sessions
WHERE channel IN ('rumor_acquisition','rumor_correction') AND scene_summary_id<>'');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSqliteMaster7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='conversation_turns' LIMIT 1;", parameters, transaction);
        }

        internal static void DeleteConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM conversation_turns WHERE session_id IN (
SELECT session_id FROM conversation_sessions WHERE channel IN ('rumor_acquisition','rumor_correction'));", parameters, transaction);
        }

        internal static void DeleteConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM conversation_sessions WHERE channel IN ('rumor_acquisition','rumor_correction');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSqliteMaster8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='embedding_jobs' LIMIT 1;", parameters, transaction);
        }

        internal static void DeleteEmbeddingJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM embedding_jobs WHERE source_type IN ('court_knowledge','rumor_history');", parameters, transaction);
        }

        internal static void UpsertSchemaMeta7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('character_owned_social_cleanup_v1','1');", parameters, transaction);
        }

        internal static void InsertOrIgnoreSocialCatalogRevisions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO social_catalog_revisions(revision,catalog_json,created_ts)
VALUES($revision,$catalog,$ts);", parameters, transaction);
        }

        internal static void InsertSocialCatalogRevisions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO social_catalog_revisions(revision,catalog_json,created_ts) VALUES($revision,$json,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 FROM (
SELECT tag_id FROM rumor_subject_tags UNION ALL SELECT tag_id FROM character_reputations
) WHERE tag_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorExposureStreaks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM rumor_exposure_streaks
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id=$archetype AND thread_key=$thread LIMIT 1;", parameters, transaction);
        }

        internal static void InsertRumorOccurrences2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO rumor_occurrences(occurrence_id,campaign_id,timeline_id,archetype_id,thread_key,source_event_id,
world_day,expires_day,status,exposure_chance,exposure_roll,catalog_revision,participants_json,provenance_summary,snapshot_json,created_ts,updated_ts)
VALUES($id,$campaign,$timeline,$archetype,$thread,$event,$day,$expires,'active',$chance,$roll,$revision,$participants,$summary,$snapshot,$ts,$ts);", parameters, transaction);
        }

        internal static void InsertOrIgnoreCourtRulerFavorContact2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_ruler_favor_contact(campaign_id,timeline_id,ruler_id,favorite_id,grace_day)
VALUES($campaign,$timeline,$ruler,$favorite,$day) ON CONFLICT(campaign_id,timeline_id,ruler_id,favorite_id) DO NOTHING;", parameters, transaction);
        }

        internal static void InsertRumorSubjectTags2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO rumor_subject_tags(occurrence_id,subject_id,tag_id,subject_role,description,rumor_value,reputation_value,status,co_participants_json,snapshot_json,updated_ts)
VALUES($occurrence,$subject,$tag,$role,$description,$rumor,$reputation,'active',$others,$snapshot,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSharedTagExposureStreaks2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT *
FROM shared_tag_exposure_streaks WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$subject AND tag_id=$tag LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertSharedTagExposureStreaks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO shared_tag_exposure_streaks(
campaign_id,timeline_id,subject_id,tag_id,streak_count,last_exposure_day,last_occurrence_id,updated_ts)
VALUES($campaign,$timeline,$subject,$tag,$count,$day,$occurrence,$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,tag_id) DO UPDATE SET
streak_count=$count,last_exposure_day=$day,last_occurrence_id=$occurrence,updated_ts=$ts;", parameters, transaction);
        }

        internal static void UpsertRumorExposureStreaks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO rumor_exposure_streaks(campaign_id,timeline_id,archetype_id,thread_key,streak_count,last_exposure_day,last_occurrence_id,updated_ts)
VALUES($campaign,$timeline,$archetype,$thread,$count,$day,$occurrence,$ts);", parameters, transaction);
        }

        internal static void InsertRumorPromotionResults(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO rumor_promotion_results(occurrence_id,streak_count,promotion_chance,promotion_roll,promoted,promoted_tags_json,created_ts)
VALUES($occurrence,$count,$chance,$roll,$promoted,$tags,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRumorSubjectTags2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM rumor_subject_tags WHERE occurrence_id=$id;", parameters, transaction);
        }

        internal static void UpdateRumorOccurrences3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE rumor_occurrences
SET status='expired',updated_ts=$ts WHERE occurrence_id=$id AND status='active';
UPDATE rumor_subject_tags SET status='expired',updated_ts=$ts
WHERE occurrence_id=$id AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations36(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 AS found FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag AND status='active' LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertCharacterReputations5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_reputations(campaign_id,timeline_id,subject_id,tag_id,
source_occurrence_id,archetype_id,subject_role,description,reputation_value,acquired_day,catalog_revision,snapshot_json,status,updated_ts)
VALUES($campaign,$timeline,$subject,$tag,$occurrence,$archetype,$role,$description,$value,$day,$revision,$snapshot,'active',$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,tag_id) DO UPDATE SET
source_occurrence_id=$occurrence,archetype_id=$archetype,subject_role=$role,description=$description,
reputation_value=$value,acquired_day=$day,catalog_revision=$revision,snapshot_json=$snapshot,status='active',updated_ts=$ts
WHERE character_reputations.status<>'active';", parameters, transaction);
        }

        internal static void UpdateRumorSubjectTags4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE rumor_subject_tags SET status='superseded_by_reputation',updated_ts=$ts
WHERE subject_id=$subject AND tag_id=$tag AND status='active'
AND ($tag<>'whoremonger' OR occurrence_id IN (SELECT occurrence_id FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM rumor_occurrences WHERE occurrence_id=$id AND campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT subject_id,tag_id FROM rumor_subject_tags WHERE occurrence_id=$id;", parameters, transaction);
        }

        internal static void UpdateRumorOccurrences4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE rumor_occurrences SET status='disproven',updated_ts=$ts WHERE occurrence_id=$id;", parameters, transaction);
        }

        internal static void UpdateRumorSubjectTags5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE rumor_subject_tags SET status='disproven',updated_ts=$ts WHERE occurrence_id=$id AND status='active';", parameters, transaction);
        }

        internal static void DeleteRumorExposureStreaks2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM rumor_exposure_streaks WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id=$archetype AND thread_key=$thread AND last_occurrence_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT DISTINCT subject_id FROM rumor_subject_tags WHERE occurrence_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectReputationActivations4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT cr.tag_id,cr.description,cr.reputation_value,cr.archetype_id,cr.subject_role,cr.acquired_day,cr.reason_text,cr.reason_status,cr.reason_activation_id,
(SELECT ra.source_event_id FROM reputation_activations ra
 WHERE ra.activation_id=cr.reason_activation_id LIMIT 1) AS activation_source_event_id,
(SELECT ra.evidence_json FROM reputation_activations ra
 WHERE ra.activation_id=cr.reason_activation_id LIMIT 1) AS activation_evidence_json,
(SELECT rle.source_event_id FROM reputation_lifecycle_events rle
 WHERE rle.campaign_id=cr.campaign_id AND rle.timeline_id=cr.timeline_id
 AND rle.subject_id=cr.subject_id AND rle.tag_id=cr.tag_id
 ORDER BY rle.created_ts DESC,rle.lifecycle_id DESC LIMIT 1) AS lifecycle_source_event_id,
(SELECT whe.correlation_id FROM reputation_lifecycle_events rle
 JOIN world_history_events whe ON whe.campaign_id=rle.campaign_id
  AND whe.timeline_id=rle.timeline_id AND whe.event_id=rle.source_event_id
 WHERE rle.campaign_id=cr.campaign_id AND rle.timeline_id=cr.timeline_id
 AND rle.subject_id=cr.subject_id AND rle.tag_id=cr.tag_id
 ORDER BY rle.created_ts DESC,rle.lifecycle_id DESC LIMIT 1) AS lifecycle_source_correlation_id,
(SELECT rle.details_json FROM reputation_lifecycle_events rle
 WHERE rle.campaign_id=cr.campaign_id AND rle.timeline_id=cr.timeline_id
 AND rle.subject_id=cr.subject_id AND rle.tag_id=cr.tag_id
 ORDER BY rle.created_ts DESC,rle.lifecycle_id DESC LIMIT 1) AS lifecycle_details_json
FROM character_reputations cr WHERE cr.campaign_id=$campaign AND cr.timeline_id=$timeline AND cr.subject_id=$subject AND cr.status='active'
ORDER BY cr.acquired_day DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT rst.tag_id,rst.description,rst.rumor_value,ro.archetype_id,ro.world_day,ro.expires_day,ro.provenance_summary
FROM rumor_subject_tags rst JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline AND rst.subject_id=$subject AND rst.status='active' AND ro.status='active' AND ro.expires_day>$day
AND ro.world_day=(SELECT MAX(ro2.world_day) FROM rumor_subject_tags rst2
JOIN rumor_occurrences ro2 ON ro2.occurrence_id=rst2.occurrence_id
WHERE rst2.subject_id=rst.subject_id AND rst2.tag_id=rst.tag_id AND rst2.status='active'
AND ro2.campaign_id=ro.campaign_id AND ro2.timeline_id=ro.timeline_id AND ro2.status='active' AND ro2.expires_day>$day)
ORDER BY ro.world_day DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags15(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT rst.description,rst.snapshot_json,
ro.archetype_id,ro.world_day,ro.expires_day,ro.provenance_summary,ro.participants_json
FROM rumor_subject_tags rst JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline AND rst.subject_id=$subject
AND rst.status='active' AND ro.status='active' AND ro.expires_day>$day
AND ro.world_day=(SELECT MAX(ro2.world_day) FROM rumor_subject_tags rst2
JOIN rumor_occurrences ro2 ON ro2.occurrence_id=rst2.occurrence_id
WHERE rst2.subject_id=rst.subject_id AND rst2.tag_id=rst.tag_id AND rst2.status='active'
AND ro2.campaign_id=ro.campaign_id AND ro2.timeline_id=ro.timeline_id AND ro2.status='active' AND ro2.expires_day>$day)
ORDER BY ro.world_day DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations37(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT description,snapshot_json,
archetype_id,subject_role,acquired_day,source_occurrence_id,reason_text,reason_status FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND status='active'
ORDER BY acquired_day DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags16(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 'rumor' AS social_type,rst.subject_id,rst.description,
rst.snapshot_json,ro.provenance_summary,ro.world_day AS lifecycle_day,ro.expires_day
FROM rumor_subject_tags rst JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline AND rst.status='active' AND ro.status='active' AND ro.expires_day>$day
AND ro.world_day=(SELECT MAX(ro2.world_day) FROM rumor_subject_tags rst2
JOIN rumor_occurrences ro2 ON ro2.occurrence_id=rst2.occurrence_id
WHERE rst2.subject_id=rst.subject_id AND rst2.tag_id=rst.tag_id AND rst2.status='active'
AND ro2.campaign_id=ro.campaign_id AND ro2.timeline_id=ro.timeline_id AND ro2.status='active' AND ro2.expires_day>$day)
UNION ALL
SELECT 'reputation' AS social_type,cr.subject_id,cr.description,cr.snapshot_json,cr.reason_text AS provenance_summary,
cr.acquired_day AS lifecycle_day,0 AS expires_day FROM character_reputations cr
WHERE cr.campaign_id=$campaign AND cr.timeline_id=$timeline AND cr.status='active'
ORDER BY lifecycle_day DESC LIMIT 500;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT canonical_name FROM identity_roster WHERE hero_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags17(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT DISTINCT rst.subject_id
FROM rumor_subject_tags rst JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline
AND ro.status='active' AND ro.expires_day<=$day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,world_day,expires_day
FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='active' AND expires_day<=$day;", parameters, transaction);
        }

        internal static void UpdateRumorOccurrences5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE rumor_occurrences SET status='expired',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='active' AND expires_day<=$day;", parameters, transaction);
        }

        internal static void UpdateRumorSubjectTags6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE rumor_subject_tags SET status='expired',updated_ts=$ts
WHERE status='active' AND occurrence_id IN (SELECT occurrence_id FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='expired');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags18(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT rst.rumor_value,rst.co_participants_json,ro.archetype_id
FROM rumor_subject_tags rst JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline AND rst.subject_id=$subject
AND rst.status='active' AND ro.status='active' AND ro.expires_day>$day
AND ro.world_day=(SELECT MAX(ro2.world_day) FROM rumor_subject_tags rst2
JOIN rumor_occurrences ro2 ON ro2.occurrence_id=rst2.occurrence_id
WHERE rst2.subject_id=rst.subject_id AND rst2.tag_id=rst.tag_id AND rst2.status='active'
AND ro2.campaign_id=ro.campaign_id AND ro2.timeline_id=ro.timeline_id AND ro2.status='active' AND ro2.expires_day>$day);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations38(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT reputation_value FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND status='active';", parameters, transaction);
        }

        internal const string SelectRumorOccurrencesSql = "SELECT occurrence_id FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id=$archetype AND thread_key=$thread AND world_day=$day LIMIT 1;";

        internal const string SelectRumorOccurrencesSql2 = "SELECT occurrence_id FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id=$archetype AND thread_key=$thread AND source_event_id=$event LIMIT 1;";

        /// <summary>Runs a statement this module composes at run time from text kept in this class.</summary>
        internal static List<Dictionary<string, object>> QuerySql(ReignDbConnection connection, string sql, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, sql, parameters, transaction);
        }
    }
}
