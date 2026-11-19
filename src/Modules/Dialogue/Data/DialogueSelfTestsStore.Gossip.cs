using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueSelfTestStore
    {
        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_messages WHERE message_id=$id AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipTopics(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_topics WHERE timeline_id=$timeline AND topic_id=$topic ORDER BY topic_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_messages WHERE timeline_id=$timeline AND root_id=$root ORDER BY message_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipHistory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_history WHERE timeline_id=$timeline AND root_id=$root ORDER BY receipt_id;", parameters, transaction);
        }

        internal static void UpsertRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,first_day,last_day,updated_ts)
VALUES($key,$a,$b,$ab,$ba,1,1,1) ON CONFLICT(pair_key) DO UPDATE SET affinity_a_to_b=excluded.affinity_a_to_b,affinity_b_to_a=excluded.affinity_b_to_a;", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,participants_json,payload_json)
VALUES($id,$campaign,'source','player','in_person','closed',1,1,$day,$day,'[""source"",""player""]','{}') ON CONFLICT(session_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipTestFixtures(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM personal_gossip_test_fixtures WHERE fixture_id='activation_contract:valid';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipTopics2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT topic_id FROM personal_gossip_topics;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT message_id FROM personal_gossip_messages;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipTestFixtures2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT fixture_id FROM personal_gossip_test_fixtures;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT message_id FROM personal_gossip_messages WHERE topic_id='incomplete_roster';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_messages;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipHistory2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_history;", parameters, transaction);
        }

        internal static void InsertPersonalGossipMessages(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO personal_gossip_messages
(message_id,topic_id,root_id,timeline_id,speaker_id,recipient_id,player_id,is_reply,hop,created_day,due_day,expires_day,status,attempts,lease_token,lease_until,payload_json)
SELECT $copy,topic_id,root_id,timeline_id,speaker_id,recipient_id,player_id,is_reply,hop,created_day,due_day,expires_day,status,attempts,lease_token,lease_until,payload_json
FROM personal_gossip_messages WHERE message_id=$original;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipHistory3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT receipt_id FROM personal_gossip_history;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT status FROM personal_gossip_messages WHERE message_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT attempts FROM personal_gossip_messages WHERE message_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipHistory4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_history WHERE owner_id='target';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipHistory5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_history WHERE owner_id='source';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipHistory6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT receipt_id FROM personal_gossip_history WHERE message_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipHistory7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_history WHERE message_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipHistory8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_history WHERE message_id=$message AND owner_id='target';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT message_id FROM personal_gossip_messages WHERE topic_id='no_audience';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipTopics3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT status FROM personal_gossip_topics WHERE topic_id='no_audience';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipHistory9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_history WHERE root_id=$root AND owner_id='target';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipHistory10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_history WHERE message_id=$id AND owner_id='source';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipHistory11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_history WHERE message_id=$id AND owner_id='target';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns31(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text FROM conversation_turns WHERE exchange_id=$id AND role='player';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipHistory12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_history WHERE root_id=$root;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_messages WHERE message_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationTurns26(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET status='invalidated' WHERE exchange_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT message_id FROM personal_gossip_messages WHERE message_id=$id;", parameters, transaction);
        }

        internal static void InsertConversationRelationshipReceipts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_relationship_receipts(receipt_id,campaign_id,timeline_id,exchange_id,observer_id,target_id,final_delta,status,native_application_status,created_ts)
VALUES($id,$campaign,'main',$exchange,'source','player',$delta,'applied','pending',1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_messages WHERE topic_id=$id;", parameters, transaction);
        }

        internal static void InsertConversationRelationshipReceipts4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_relationship_receipts(receipt_id,campaign_id,timeline_id,exchange_id,observer_id,target_id,final_delta,status,created_ts)
VALUES('mixed_receipt',$campaign,'main','mixed_zero_exchange','source','player',0,'applied',1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_messages WHERE topic_id='mixed_zero';", parameters, transaction);
        }

        internal static void InsertConversationRelationshipReceipts5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_relationship_receipts(receipt_id,campaign_id,timeline_id,exchange_id,observer_id,target_id,final_delta,status,created_ts)
VALUES($id,$campaign,'main',$exchange,'source','player',5,'applied',1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipTopics4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_topics WHERE topic_id=$id;", parameters, transaction);
        }

        internal static void InsertAcquaintances(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO acquaintances(observer_id,subject_id,identity_state,canonical_name,claimed_name)
VALUES('source','player','claimed','SecretCanonicalName','TravelingAlias'),('target','player','claimed','SecretCanonicalName','DifferentAlias');", parameters, transaction);
        }

        internal static void UpdateAcquaintances(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE acquaintances SET claimed_name='TravelingAlias' WHERE observer_id='target' AND subject_id='player';", parameters, transaction);
        }

        internal static void UpdateAcquaintances2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE acquaintances SET identity_state='verified' WHERE observer_id='source' AND subject_id='player';", parameters, transaction);
        }

        internal static void DeleteAcquaintances(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM acquaintances WHERE subject_id='player';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterEditorNativeCommands(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT command_id FROM character_editor_native_commands WHERE hero_id='profile_fixture';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterEditorNativeCommands2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT command_id FROM character_editor_native_commands WHERE hero_id=$hero;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipHistory13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_history WHERE root_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_messages WHERE root_id=$root AND is_reply=0;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT message_id FROM personal_gossip_messages WHERE topic_id IN ('past_hop_limit','expired_root');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages15(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_messages WHERE speaker_id='source' AND is_reply=0 AND created_day=90;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipTopics5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_topics WHERE session_id=$session AND status='queued';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipTopics6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT topic_id FROM personal_gossip_topics WHERE session_id=$session AND status='queued';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipTopics7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT topic_id,status FROM personal_gossip_topics WHERE session_id=$session;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages16(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT message_id FROM personal_gossip_messages WHERE topic_id=$id;", parameters, transaction);
        }

        internal static void InsertConversationRelationshipReceipts6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_relationship_receipts(receipt_id,campaign_id,timeline_id,exchange_id,observer_id,target_id,final_delta,status,native_application_status,created_ts)
VALUES('whisper_receipt',$campaign,'main',$exchange,'source','player',5,'applied','pending',1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipTopics8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT status,payload_json FROM personal_gossip_topics WHERE topic_id=$id;", parameters, transaction);
        }

        internal static void InsertConversationRelationshipReceipts7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_relationship_receipts(receipt_id,campaign_id,timeline_id,exchange_id,observer_id,target_id,final_delta,status,native_application_status,created_ts)
VALUES('source014_receipt',$campaign,'main',$exchange,'source','player',3,'applied','pending',1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages17(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT message_id FROM personal_gossip_messages WHERE topic_id='options_guaranteed';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages18(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT message_id FROM personal_gossip_messages WHERE topic_id='options_suppressed';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages19(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT message_id FROM personal_gossip_messages WHERE topic_id IN ('options_unclassified','options_no_audience');", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationSessions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,participants_json,payload_json)
VALUES($id,$campaign,'source','player','social_event','closed',1,1,$day,$day,'[""source"",""player""]','{}') ON CONFLICT(session_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages20(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_messages WHERE is_reply=0;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns32(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id,speaker_id,text FROM conversation_turns WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void InsertConversationRelationshipReceipts8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_relationship_receipts(receipt_id,campaign_id,timeline_id,exchange_id,observer_id,target_id,final_delta,status,native_application_status,created_ts)
VALUES('retained_source_late_receipt',$campaign,'main',$exchange,'source','player',2,'applied','pending',1);", parameters, transaction);
        }

        internal static void UpdateConversationTurns27(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET status='inactive' WHERE exchange_id='strong_exchange';", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_pair_chemistry SET social_modifier_a_to_b=10,social_modifier_b_to_a=-90 WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages21(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT m.* FROM personal_gossip_messages m JOIN personal_gossip_topics t ON t.topic_id=m.topic_id
WHERE t.session_id='named_remote_public_scene' AND m.is_reply=0;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages22(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT m.message_id FROM personal_gossip_messages m
JOIN personal_gossip_topics t ON t.topic_id=m.topic_id WHERE t.session_id='unnamed_remote_public_scene' AND m.is_reply=0;", parameters, transaction);
        }
    }
}
