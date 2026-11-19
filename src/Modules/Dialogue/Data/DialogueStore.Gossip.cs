using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueStore
    {
        internal static List<Dictionary<string, object>> SelectPersonalGossipClock(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_clock WHERE timeline_id=$timeline;", parameters, transaction);
        }

        internal static void UpsertPersonalGossipClock(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO personal_gossip_clock(timeline_id,world_day,snapshot_json) VALUES($timeline,$day,$json)
ON CONFLICT(timeline_id) DO UPDATE SET world_day=excluded.world_day,snapshot_json=excluded.snapshot_json;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAcquaintances(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT subject_id FROM acquaintances WHERE observer_id=$a AND subject_id=$b AND identity_state='verified' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT pair_key FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COALESCE(SUM(final_delta),0) AS delta FROM conversation_relationship_receipts r
WHERE r.observer_id=$speaker AND r.target_id=$player AND (r.timeline_id=$timeline OR r.timeline_id='')
AND r.status='applied' AND EXISTS(SELECT 1 FROM conversation_turns t WHERE t.session_id=$session AND t.exchange_id=r.exchange_id);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipTopics(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_topics WHERE topic_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipTopics2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM personal_gossip_topics WHERE status<>'queued' AND created_day>=$day;", parameters, transaction);
        }

        internal static void UpsertPersonalGossipTopics(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO personal_gossip_topics(topic_id,timeline_id,speaker_id,player_id,session_id,created_day,status,payload_json)
VALUES($id,$timeline,$speaker,$player,$session,$day,$status,$json) ON CONFLICT(topic_id) DO UPDATE SET status=excluded.status,payload_json=excluded.payload_json;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT message_id FROM personal_gossip_messages WHERE topic_id=$id AND is_reply=0 LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT m.is_reply,m.hop,m.created_day,t.session_id FROM personal_gossip_messages m
LEFT JOIN personal_gossip_topics t ON t.topic_id=m.topic_id WHERE m.timeline_id=$timeline AND m.speaker_id=$speaker AND m.is_reply=0
AND (m.created_day>$day OR t.session_id=$session);", parameters, transaction);
        }

        internal static void InsertOrIgnorePersonalGossipMessages(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO personal_gossip_messages(message_id,topic_id,root_id,timeline_id,speaker_id,recipient_id,player_id,is_reply,hop,
created_day,due_day,expires_day,status,payload_json) VALUES($id,$topic,$root,$timeline,$speaker,$recipient,$player,$reply,$hop,$day,$due,$expires,'pending',$json)
ON CONFLICT(message_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipDynamic(ReignDbConnection connection, object table, object filter, object idColumn, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_" + table + " WHERE " + filter + " ORDER BY " + idColumn + " LIMIT $limit OFFSET $offset;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountPersonalGossipDynamic(ReignDbConnection connection, object table, object filter, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS n FROM personal_gossip_" + table + " WHERE " + filter + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipTopics3(ReignDbConnection connection, object topicFilter, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT topic_id,speaker_id,status,created_day,payload_json FROM personal_gossip_topics WHERE "
                + topicFilter + " ORDER BY created_day DESC,topic_id LIMIT 64;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages3(ReignDbConnection connection, object messageFilter, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT message_id,topic_id,root_id,speaker_id,recipient_id,status,is_reply,hop,created_day,due_day,expires_day,attempts FROM personal_gossip_messages WHERE "
                + messageFilter + " ORDER BY created_day DESC,message_id LIMIT 64;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountPersonalGossipTopics(ReignDbConnection connection, object topicFilter, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS n FROM personal_gossip_topics WHERE " + topicFilter + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountPersonalGossipMessages(ReignDbConnection connection, object messageFilter, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS n FROM personal_gossip_messages WHERE " + messageFilter + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipTestFixtures(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT fixture_id FROM personal_gossip_test_fixtures WHERE fixture_id=$id;", parameters, transaction);
        }

        internal static void InsertPersonalGossipTestFixtures(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO personal_gossip_test_fixtures(fixture_id,timeline_id,speaker_id,marker,created_utc,payload_json) VALUES($id,$timeline,$actor,$marker,$utc,$json);", parameters, transaction);
        }

        internal static void InsertRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,affinity_a_to_b,affinity_b_to_a,first_day,last_day,updated_ts)
VALUES($pair,$a,$b,'','',$affinity,0,$day,$day,$ts);", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_pair_chemistry SET " + (part)
                    + "=$value WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterEditorHeads(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT revision_token FROM character_editor_heads WHERE hero_id=$hero LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipTestFixtures2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_test_fixtures WHERE timeline_id=$timeline AND speaker_id=$speaker AND consumed_topic='' ORDER BY created_utc LIMIT 32;", parameters, transaction);
        }

        internal static void UpdatePersonalGossipTestFixtures(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE personal_gossip_test_fixtures SET consumed_topic=$topic WHERE fixture_id=$id AND consumed_topic='';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipHistory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT receipt_id FROM personal_gossip_history WHERE message_id=$id AND owner_id=$owner LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns37(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id,speaker_id,text FROM conversation_turns WHERE turn_id=$id AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT m.* FROM personal_gossip_messages m JOIN personal_gossip_clock k ON k.timeline_id=m.timeline_id
WHERE m.timeline_id=$timeline AND m.attempts<3 AND m.expires_day>=k.world_day
AND (m.status='pending' OR m.status='generating' AND (m.lease_until<$now OR m.lease_token NOT LIKE $generation))
ORDER BY m.created_day,m.message_id LIMIT 1;", parameters, transaction);
        }

        internal static void UpdatePersonalGossipMessages(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE personal_gossip_messages SET status='cancelled_source' WHERE message_id=$id;", parameters, transaction);
        }

        internal static void UpdatePersonalGossipMessages2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE personal_gossip_messages SET status='generating',attempts=attempts+1,lease_token=$token,lease_until=$until
WHERE message_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_messages WHERE message_id=$id AND lease_token=$token;", parameters, transaction);
        }

        internal static void UpdatePersonalGossipMessages3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE personal_gossip_messages SET status=$status,lease_token='',lease_until=0,payload_json=$json WHERE message_id=$id;", parameters, transaction);
        }

        internal static void UpdatePersonalGossipMessages4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE personal_gossip_messages SET status='awaiting_native_clock',lease_token='',lease_until=0,payload_json=$json WHERE message_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM personal_gossip_messages WHERE timeline_id=$timeline
AND status='awaiting_native_clock' ORDER BY created_day,message_id LIMIT 32;", parameters, transaction);
        }

        internal static void UpdatePersonalGossipMessages5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE personal_gossip_messages SET status='expired' WHERE message_id=$id;", parameters, transaction);
        }

        internal static void UpdatePersonalGossipMessages6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE personal_gossip_messages SET status='in_transit',due_day=$due,expires_day=CASE WHEN is_reply=1 AND expires_day<$due THEN $due ELSE expires_day END,payload_json=$json WHERE message_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns38(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.turn_id,t.text,t.speaker_id,t.session_id,t.payload_json FROM conversation_turns t
JOIN conversation_sessions s ON s.session_id=t.session_id WHERE t.status='active' AND t.world_day<=$day AND t.channel<>'npc_gossip' AND "
                + part + " ORDER BY t.ts DESC,t.turn_order DESC LIMIT 60;", parameters, transaction);
        }

        internal static void UpdatePersonalGossipMessages7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE personal_gossip_messages SET status='cancelled_unavailable',lease_token='',lease_until=0,payload_json=$json WHERE message_id=$id;", parameters, transaction);
        }

        internal static void UpdatePersonalGossipMessages8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE personal_gossip_messages SET status='expired' WHERE timeline_id=$timeline AND expires_day<$day
AND status IN ('pending','generating','awaiting_native_clock','in_transit');", parameters, transaction);
        }

        internal static void UpdatePersonalGossipMessages9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE personal_gossip_messages SET status='generation_failed' WHERE status='generating' AND attempts>=3 AND lease_until<$now;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM personal_gossip_messages WHERE timeline_id=$timeline AND status='in_transit'
AND due_day<=$day AND expires_day>=$day ORDER BY due_day,message_id LIMIT 32;", parameters, transaction);
        }

        internal static void UpdatePersonalGossipMessages10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE personal_gossip_messages SET status='delivered',payload_json=$json WHERE message_id=$id;", parameters, transaction);
        }

        internal static void InsertOrIgnorePersonalGossipHistory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO personal_gossip_history(receipt_id,message_id,root_id,timeline_id,owner_id,peer_id,acquired_day,payload_json)
VALUES($id,$message,$root,$timeline,$owner,$peer,$day,$json) ON CONFLICT(message_id,owner_id) DO NOTHING;", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationSessions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,participants_json,payload_json)
VALUES($id,$campaign,$owner,'','npc_gossip','closed',$ts,$ts,$day,$day,$participants,$json) ON CONFLICT(session_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipHistory2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_history WHERE message_id=$id AND owner_id=$owner;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM personal_gossip_messages WHERE message_id=$id;", parameters, transaction);
        }

        internal static void UpdatePersonalGossipHistory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE personal_gossip_history SET payload_json=$json WHERE receipt_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipTopics4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT topic_id FROM personal_gossip_topics WHERE topic_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipHistory3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM personal_gossip_history WHERE timeline_id=$timeline AND owner_id=$owner
AND acquired_day<=$day ORDER BY acquired_day DESC,receipt_id DESC LIMIT 12;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipHistory4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT receipt_id FROM personal_gossip_history WHERE timeline_id=$timeline AND owner_id=$owner AND raise_session=$session LIMIT 1;", parameters, transaction);
        }

        internal static void UpdatePersonalGossipHistory2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE personal_gossip_history SET raise_session=$session WHERE receipt_id=$id AND timeline_id=$timeline
AND owner_id=$owner AND raise_session='' AND acquired_day<=$day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT message_id,root_id,timeline_id,speaker_id,recipient_id,is_reply,hop,created_day,due_day,expires_day,status,attempts FROM personal_gossip_messages ORDER BY created_day DESC,message_id LIMIT 100;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipTopics5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT topic_id,speaker_id,created_day,status,payload_json FROM personal_gossip_topics ORDER BY created_day DESC,topic_id LIMIT 30;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT status,COUNT(*) AS count FROM personal_gossip_messages
WHERE timeline_id=$timeline AND status IN ('pending','generating','awaiting_native_clock','generation_failed') GROUP BY status;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT MIN(due_day) AS next_day FROM personal_gossip_messages WHERE timeline_id=$timeline AND status='in_transit';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM personal_gossip_messages WHERE timeline_id=$timeline AND status='awaiting_native_clock' ORDER BY created_day,message_id LIMIT 32;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountPersonalGossipMessages2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM personal_gossip_messages WHERE timeline_id=$timeline AND status='in_transit' AND due_day<=$day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPersonalGossipMessages13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT m.payload_json FROM personal_gossip_messages m
JOIN personal_gossip_topics t ON t.topic_id=m.topic_id
WHERE m.timeline_id=$timeline AND t.session_id=$session AND m.is_reply=0 AND m.hop=1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns39(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT turn_id,speaker_id,text FROM conversation_turns
WHERE turn_id=$id AND session_id=$session AND exchange_id=$exchange AND status='active' AND world_day=$day;", parameters, transaction);
        }
    }
}
