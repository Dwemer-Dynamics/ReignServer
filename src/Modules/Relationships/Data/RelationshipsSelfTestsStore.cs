using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Relationships module's SQL statements used only by its self-tests (verification build only); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class RelationshipsSelfTestStore
    {
        internal static List<Dictionary<string, object>> SelectLetters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM letters WHERE status='read' AND letter_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCorrespondenceDeliveryJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM correspondence_delivery_jobs WHERE letter_id=$id;", parameters, transaction);
        }

        internal static void UpdateCorrespondenceDeliveryJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE correspondence_delivery_jobs SET next_attempt_ts=0 WHERE letter_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM letters WHERE letter_id=$id;", parameters, transaction);
        }

        internal static void UpdateCorrespondenceDeliveryJobs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE correspondence_delivery_jobs SET status='running',attempt_count=1,lease_token='old_worker',lease_until=$until WHERE letter_id=$id;", parameters, transaction);
        }

        internal static void UpdateCorrespondenceDeliveryJobs3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE correspondence_delivery_jobs SET status='running',attempt_count=2 WHERE letter_id=$id;", parameters, transaction);
        }

        internal static void UpdateLetters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE letters SET transit_policy_version=0,base_transit_days=0,delivery_day=105.3893225703 WHERE letter_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM letters WHERE source='campaign_command_report' AND reason='scout_report_receipt';", parameters, transaction);
        }

        internal static void UpdateLetters2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE letters SET status='read' WHERE letter_id=$id;", parameters, transaction);
        }

        internal static void InsertLetters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO letters(letter_id,thread_id,sender_id,recipient_id,body,status,source,dispatch_day,created_ts,updated_ts)
SELECT 'bulk_'||i,'bulk_thread','player','bulk','Fixture read body','read','player',400+i,i,i FROM generate_series(1,1001) AS i;
INSERT INTO letters(letter_id,thread_id,sender_id,recipient_id,body,status,source,dispatch_day,created_ts,updated_ts) VALUES
('old_unread','old_thread','old_contact','player','Old unread body','delivered','npc_initiated',1,1,1),
('hidden_mail','hidden_thread','hidden_contact','player','Unseen body','in_transit','npc_introduction',5000,5000,5000),
('foreign_mail','foreign_thread','other_npc','other_player','Private body','delivered','npc_initiated',6000,6000,6000);", parameters, transaction);
        }

        internal static void UpdateLetters3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE letters SET payload_json=$payload WHERE letter_id IN ('old_unread','bulk_1001');", parameters, transaction);
        }

        internal static void InsertLetters2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO letters(letter_id,thread_id,sender_id,recipient_id,body,status,source,dispatch_day,created_ts,updated_ts) VALUES
('arranged_arrived','arranged_thread','unmet_leader','player','Marriage terms','delivered','reign_marriage_offer',5001,5001,5001),
('arranged_courier','arranged_other_thread','unmet_other_leader','player','In transit','in_transit','reign_marriage_offer',5002,5002,5002),
('arranged_answer','arranged_thread','player','unmet_leader','Signed answer','in_transit','reign_marriage_answer',5003,5003,5003),
('arranged_meeting','arranged_thread','player','unmet_leader','Request to meet','read','reign_marriage_meeting',5004,5004,5004);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT letter_id,status,read_day,payload_json FROM letters WHERE letter_id IN ('old_unread','hidden_mail','foreign_mail','bulk_1001');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT read_day FROM letters WHERE letter_id='old_unread';", parameters, transaction);
        }

        internal static void InsertConversationRelationshipReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_relationship_receipts(receipt_id,campaign_id,exchange_id,observer_id,target_id,
final_delta,prior_native_relation,resulting_native_relation,native_pair_delta,created_ts)
VALUES($id,$campaign,$id,$observer,'player',3,10,13,3,1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_relationship_receipts WHERE native_application_status='applied';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_relationship_receipts WHERE receipt_id='a';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_relationship_receipts WHERE receipt_id='legacy';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_relationship_receipts WHERE receipt_id='replay';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE session_id=$session AND role='player';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE session_id=$session AND role='npc';", parameters, transaction);
        }

        internal static void DeletePlayerAttentionEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM player_attention_events WHERE timeline_id='individual_opening_test';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPlayerAttentionEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM player_attention_events;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPlayerAttentionEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM player_attention_events WHERE observer_id='newcomer';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPlayerAttentionEvents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM player_attention_events WHERE calendar_day=11;", parameters, transaction);
        }

        internal static void Begin(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "BEGIN;", parameters, transaction);
        }

        internal static void Rollback(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "ROLLBACK;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPlayerAttentionEvents4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM player_attention_events WHERE calendar_day=12;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPlayerAttentionEvents5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM player_attention_events WHERE observer_id='observer' AND sequence_id>$sequence AND exchange_id<>$exchange;", parameters, transaction);
        }

        internal static void InsertRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,
affinity_a_to_b,affinity_b_to_a,first_day,last_day,updated_ts) VALUES($pair,$hero,'player','ENFP','INTJ',100,0,1,20,123);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPlayerApproachState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM player_approach_state WHERE timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT affinity_a_to_b FROM relationship_pair_chemistry WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountPlayerAttentionEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM player_attention_events;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPlayerAttentionEvents6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT MAX(sequence_id) AS sequence FROM player_attention_events;", parameters, transaction);
        }

        internal static void DeletePlayerAttentionEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM player_attention_events;", parameters, transaction);
        }

        internal static void InsertConceptionContexts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO conception_contexts(timeline_id,pregnancy_id,facts_json,world_day) VALUES('test','pregnancy',$facts,28);", parameters, transaction);
        }

        internal static void UpdateConceptionContexts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conception_contexts SET facts_json=$facts WHERE timeline_id='test' AND pregnancy_id='pregnancy';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConceptionKnowledge(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conception_knowledge WHERE timeline_id='test' AND observer_id='player';", parameters, transaction);
        }

        internal static void InsertWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_events(event_id,campaign_id,timeline_id,sequence,world_day,event_type,created_utc)
VALUES('stale_conception',$campaign,'stale',1,28,'child_conceived','2026-10-10T00:00:00Z');
INSERT INTO world_history_knowledge_rules(rule_id,event_id,audience_type,audience_id,available_day,acquisition_mode)
VALUES('stale_conception_news','stale_conception','kingdom','realm',28,'realm_news');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryKnowledgeRules(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT rule_id FROM world_history_knowledge_rules WHERE rule_id='stale_conception_news';", parameters, transaction);
        }

        internal static void UpdateConceptionContexts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conception_contexts SET world_day=40 WHERE timeline_id='test' AND pregnancy_id='pregnancy';", parameters, transaction);
        }

        internal static void UpdateConceptionContexts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conception_contexts SET world_day=28 WHERE timeline_id='test' AND pregnancy_id='pregnancy';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT subject_id,reputation_value FROM character_reputations WHERE timeline_id='test' AND tag_id='the_unchaste' AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectReputationLifecycleEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM reputation_lifecycle_events WHERE timeline_id='test';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM identity_roster;", parameters, transaction);
        }

        internal static void InsertAcquaintances(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO acquaintances(observer_id,subject_id,identity_state,canonical_name,verification_source,confidence,first_met_day,last_met_day,updated_ts)
VALUES('hero_0','hero_5','verified','Known name','explicit',1,1,2,123),
('hero_0','hero_1','claimed','Preserved claim','claim',0.6,1,2,123);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAcquaintances(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM acquaintances;", parameters, transaction);
        }

        internal static void Begin2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "BEGIN IMMEDIATE;", parameters, transaction);
        }

        internal static void InsertRelationshipPairLifecycle(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_lifecycle(pair_key,hero_a_id,hero_b_id,last_processed_day,flirt_attempted,flirt_passed,updated_ts)
VALUES('hero_0|hero_1','hero_0','hero_1',1,0,0,123),
('hero_2|hero_3','hero_2','hero_3',1,1,0,123),
('hero_4|hero_5','hero_4','hero_5',1,0,0,123),
('hero_6|hero_7','hero_6','hero_7',9,0,0,123);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairLifecycle(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_lifecycle WHERE last_processed_day<5;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairLifecycle(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_pair_lifecycle SET romance_stage=$stage WHERE pair_key='hero_2|hero_3';", parameters, transaction);
        }

        internal static void InsertRelationshipPairChemistry2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,first_day,last_day,projected_native_relation,native_action_pending,updated_ts)
VALUES('hero_0|hero_1','hero_0','hero_1','INTJ','ENFP',1,5,42,1,123);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT requires_observation FROM relationship_native_targets;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT native_action_pending FROM relationship_pair_chemistry;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_pair_chemistry SET projected_native_relation=51,native_action_pending=1;", parameters, transaction);
        }

        internal static void UpdateIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE identity_roster SET current_charm=current_charm+1;", parameters, transaction);
        }

        internal static void UpdateIdentityRoster2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE identity_roster SET kingdom_id='transferred' WHERE hero_id='hero_1';", parameters, transaction);
        }

        internal static void UpdateIdentityRoster3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE identity_roster SET kingdom_id='one_realm';", parameters, transaction);
        }

        internal static void InsertOrIgnoreRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,
affinity_a_to_b,affinity_b_to_a,first_day,last_day,updated_ts)
SELECT 'hero_0|'||hero_id,'hero_0',hero_id,'ENFP','INTJ',0,60,1,5,123 FROM identity_roster
WHERE is_lord=1 AND hero_id<>'hero_0' ON CONFLICT(pair_key) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT subject_id,tag_id,status,reputation_value,description FROM character_reputations ORDER BY subject_id,tag_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterPublicStanding(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT subject_id,standing_value,revision,sources_json FROM character_public_standing ORDER BY subject_id;", parameters, transaction);
        }

        internal static void UpdateIdentityRoster4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE identity_roster SET is_player=1 WHERE hero_id='hero_2';", parameters, transaction);
        }

        internal static void UpdateIdentityRoster5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE identity_roster SET kingdom_id='elsewhere' WHERE hero_id='hero_4';", parameters, transaction);
        }

        internal static void UpdateIdentityRoster6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE identity_roster SET is_alive=0 WHERE hero_id='hero_6';", parameters, transaction);
        }

        internal static void UpdateIdentityRoster7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE identity_roster SET is_notable=1 WHERE hero_id='hero_8';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPersonalities(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT traits_json,assignment_day FROM relationship_personalities WHERE hero_id='hero_0';", parameters, transaction);
        }

        internal static void InsertRelationshipDailyInputs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_daily_inputs(campaign_id,timeline_id,day_key,world_day,status,received_ts,started_ts)
VALUES($campaign,'main',7,7,'processing',1,$now);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDailyInputs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT status FROM relationship_daily_inputs WHERE day_key=7;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipHeroObservations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT observed_day FROM relationship_hero_observations WHERE timeline_id='main' ORDER BY observed_day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipObservedHeroes(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_observed_heroes WHERE hero_id='history_a';", parameters, transaction);
        }

        internal static void InsertRelationshipDailyInputs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_daily_inputs(campaign_id,timeline_id,day_key,world_day,status,received_ts)
VALUES($campaign,'main',18,18,'pending',1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipHeroObservations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT MIN(observed_day) AS day FROM relationship_hero_observations WHERE timeline_id='main';", parameters, transaction);
        }

        internal static void DeleteRelationshipDailyInputs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM relationship_daily_inputs WHERE day_key=18;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDailyInputs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT hero_changes_json FROM relationship_daily_inputs WHERE day_key=21;", parameters, transaction);
        }

        internal static void UpdateRelationshipDailyInputs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_daily_inputs SET status='processing',started_ts=123,pair_cursor='history_a|history_b' WHERE day_key=22;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDailyInputs3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_daily_inputs WHERE day_key=22;", parameters, transaction);
        }

        internal static void InsertRelationshipPairChemistry3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,base_chance_a_to_b,base_chance_b_to_a,
affinity_a_to_b,affinity_b_to_a,first_day,last_day,projected_native_relation,native_action_pending,compatibility_version,updated_ts)
VALUES('native_a|native_b','native_a','native_b','INTJ','INTJ',100,100,100,100,1,1,100,1,$version,1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeTargets2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_native_targets WHERE requires_observation=1;", parameters, transaction);
        }

        internal static void DeleteRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM relationship_native_targets;", parameters, transaction);
        }

        internal static void InsertRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,world_day,updated_ts,requires_observation)
VALUES('native_a|native_b','native_a','native_b',100,100,'pending',1,1,1);", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_pair_chemistry SET native_action_pending=1;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET affinity_a_to_b=0,affinity_b_to_a=0,
projected_native_relation=0,native_action_pending=1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void InsertConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('history_sources',$campaign,'a','player','closed',1,2,'[""a"",""b"",""player""]','{""timelineId"":""main""}');", parameters, transaction);
        }

        internal static void UpdateLetters4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE letters SET status='read',read_day=5,delivery_timeline_id='main' WHERE letter_id=$id;", parameters, transaction);
        }
    }
}
