using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueStore
    {
        internal static void InsertOrIgnoreChatViewFrames(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO chat_view_frames(turn_id,session_id,exchange_id,timeline_id,frame_json)
VALUES($turn,$session,$exchange,$timeline,$frame);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$session AND player_id=$player;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT MAX(turn_order) AS boundary FROM conversation_turns WHERE session_id=$session AND exchange_id=$exchange AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT exchange_id,MAX(turn_order) AS last_order FROM conversation_turns
WHERE session_id=$session AND status='active' GROUP BY exchange_id HAVING MAX(turn_order)<=$boundary
ORDER BY last_order DESC LIMIT 64;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.*,f.frame_json,f.timeline_id FROM conversation_turns t
LEFT JOIN chat_view_frames f ON f.turn_id=t.turn_id WHERE t.session_id=$session AND t.exchange_id=$exchange
AND t.status='active' ORDER BY t.turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id AND speaker_id=$player AND status='source_only';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM communication_messages WHERE timeline_id=$timeline AND speaker_id=$speaker
AND recipient_id=$recipient AND topic_key=$key AND status IN ('draft','needs_clarification') ORDER BY created_day,message_id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM communication_messages WHERE message_id=$id AND revision=$revision
AND payload_json=$json AND status IN ('draft','needs_clarification');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_messages WHERE message_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM communication_messages WHERE timeline_id=$timeline AND logical_id=$logical
AND speaker_id=$speaker AND recipient_id=$recipient AND status IN ('draft','needs_clarification') ORDER BY created_day,message_id;", parameters, transaction);
        }

        internal static void UpdateCommunicationMessages(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE communication_messages SET status='cancelled',revision=revision+1,payload_json=$json WHERE message_id=$id;", parameters, transaction);
        }

        internal static void UpsertCommunicationMessages(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO communication_messages(message_id,root_id,timeline_id,logical_id,session_id,speaker_id,recipient_id,player_id,
topic_key,kind,status,created_day,created_ts,accepted_sequence,payload_json) VALUES($id,$id,$timeline,$logical,$session,$speaker,$recipient,$player,$key,$kind,'draft',$day,$ts,$sequence,$json)
ON CONFLICT(message_id) DO UPDATE SET status='draft',revision=communication_messages.revision+1,payload_json=EXCLUDED.payload_json,
logical_id=EXCLUDED.logical_id,session_id=EXCLUDED.session_id,kind=EXCLUDED.kind,created_ts=EXCLUDED.created_ts,accepted_sequence=EXCLUDED.accepted_sequence;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_messages WHERE timeline_id=$timeline AND logical_id=$logical AND status='draft';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text,status FROM conversation_turns WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void UpdateCommunicationMessages2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE communication_messages SET status=$status,payload_json=$json WHERE message_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM communication_messages WHERE timeline_id=$timeline AND speaker_id=$hero
AND parent_id='' AND created_day<=$day AND created_ts<=$ts AND accepted_sequence<=$sequence ORDER BY created_day DESC,message_id LIMIT 24;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationHistory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM communication_history WHERE timeline_id=$timeline AND owner_id=$hero
AND acquired_day<=$day AND acquired_ts<=$ts AND accepted_sequence<=$sequence ORDER BY acquired_day DESC,receipt_id LIMIT 30;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationHistory2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT receipt_id FROM communication_history WHERE message_id=$id AND timeline_id=$timeline AND owner_id=$hero
AND acquired_day<=$day AND acquired_ts<=$ts AND accepted_sequence<=$sequence LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateCommunicationMessages3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE communication_messages SET timeline_id=$new WHERE timeline_id=$old AND created_day<=$day;
UPDATE communication_history SET timeline_id=$new WHERE timeline_id=$old AND acquired_day<=$day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterNpcLivesState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value_json FROM character_npc_lives_state WHERE timeline_id=$old;", parameters, transaction);
        }

        internal static void InsertOrIgnoreCharacterNpcLivesState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_npc_lives_state(timeline_id,value_json) VALUES($timeline,$json)
ON CONFLICT(timeline_id) DO NOTHING;", parameters, transaction);
        }

        internal static void InsertOrIgnoreCommunicationHistory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO communication_history(receipt_id,message_id,timeline_id,owner_id,peer_id,acquired_day,acquired_ts,payload_json)
VALUES($receipt,$id,$timeline,$owner,$peer,$day,$ts,$json) ON CONFLICT(message_id,owner_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT campaign_id FROM conversation_sessions WHERE session_id=$id;", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,participants_json,payload_json)
VALUES($id,$campaign,$owner,'','npc_correspondence','closed',$ts,$ts,$day,$day,$participants,$json) ON CONFLICT(session_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE turn_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM communication_messages WHERE timeline_id=$timeline
AND status IN ('queued','awaiting_sender','in_transit','awaiting_recipient') ORDER BY created_day,message_id LIMIT 128;", parameters, transaction);
        }

        internal static void UpdateCommunicationMessages4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE communication_messages SET status='in_transit',dispatch_day=$day,due_day=$due,payload_json=$json WHERE message_id=$id;", parameters, transaction);
        }

        internal static void UpdateCommunicationMessages5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE communication_messages SET status=$status,acquired_day=$day WHERE message_id=$id;", parameters, transaction);
        }

        internal static void UpdateCommunicationMessages6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE communication_messages SET status=CASE WHEN status='awaiting_reply' THEN 'replied' ELSE status END WHERE message_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_messages WHERE timeline_id=$timeline AND status='summons_ready' ORDER BY created_day LIMIT 8;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_messages WHERE timeline_id=$timeline AND parent_id='' AND kind IN ('summon','summon_then_inform') ORDER BY created_day,message_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationAcceptedUnits(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT a.unit_id,a.created_ts,
(a.receipt_json::jsonb->'communications')::text AS admission_json FROM conversation_accepted_units a
WHERE a.receipt_json::jsonb ? 'communications' AND EXISTS(SELECT 1 FROM conversation_turns t
JOIN conversation_sessions s ON s.session_id=t.session_id WHERE t.exchange_id=a.unit_id AND t.role='npc'
AND ($hero='' OR t.speaker_id=$hero)
AND COALESCE(t.payload_json::jsonb->>'timelineId',s.payload_json::jsonb->>'timelineId','main')=$timeline)
ORDER BY a.created_ts DESC,a.unit_id LIMIT $limit OFFSET $offset;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectStatement2(ReignDbConnection connection, object messages, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT *" + messages + " ORDER BY created_day,message_id LIMIT $limit OFFSET $offset;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectStatement3(ReignDbConnection connection, object history, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT *" + history + " ORDER BY acquired_day,receipt_id LIMIT $limit OFFSET $offset;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountStatement(ReignDbConnection connection, object messages, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS n" + messages + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountCommunicationMessages(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
COUNT(*) FILTER (WHERE status IN ('awaiting_host','reply_pending','reply_generating','summons_ready')) AS pending,
COUNT(*) FILTER (WHERE status='in_transit' AND due_day<=$day OR status='queued' AND COALESCE((payload_json::jsonb->>'dispatchNotBefore')::double precision,created_day)<=$day) AS due,
COUNT(*) FILTER (WHERE status='reply_failed') AS failed,
COUNT(*) AS total FROM communication_messages WHERE timeline_id=$timeline;", parameters, transaction);
        }

        internal static void UpdateCommunicationMessages7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE communication_messages SET status='reply_failed' WHERE timeline_id=$timeline
AND status='reply_generating' AND attempts>=3 AND (lease_until<$now OR lease_token NOT LIKE $prefix);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM communication_messages WHERE timeline_id=$timeline AND attempts<3
AND (status='reply_pending' OR status='reply_generating' AND (lease_until<$now OR lease_token NOT LIKE $prefix)) ORDER BY acquired_day,message_id LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateCommunicationMessages8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE communication_messages SET status='reply_generating',attempts=attempts+1,lease_token=$lease,lease_until=$until WHERE message_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_messages WHERE message_id=$id AND lease_token=$lease AND status='reply_generating';", parameters, transaction);
        }

        internal static void InsertOrIgnoreCommunicationMessages(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO communication_messages(message_id,root_id,parent_id,timeline_id,logical_id,session_id,speaker_id,recipient_id,player_id,
topic_key,kind,status,created_day,payload_json) VALUES($id,$root,$root,$timeline,$logical,$session,$speaker,$recipient,$player,'reply','reply','queued',$day,$json)
ON CONFLICT(message_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationHistory3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM communication_history WHERE owner_id=$owner AND peer_id=$peer AND timeline_id=$timeline
AND acquired_day<=$day AND acquired_ts<=$ts AND accepted_sequence<=$sequence ORDER BY acquired_day DESC,receipt_id LIMIT 30;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text,participants_json,speaker_id FROM conversation_turns WHERE session_id=$id AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCommunicationMessages12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM communication_messages WHERE message_id=$id AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static void DeleteMemoryRestoredLetters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM memory_restored_letters; DELETE FROM memory_restored_letter_context;", parameters, transaction);
        }

        internal static void InsertMemoryRestoredLetterContext(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO memory_restored_letter_context
(state_id,restore_generation,timeline_id,save_point_id,world_day,head_sequence,head_event_id)
VALUES('current',$generation,$timeline,$point,$day,$sequence,$head);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM letters WHERE dispatch_day<=$day;", parameters, transaction);
        }

        internal static void InsertMemoryRestoredLetters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO memory_restored_letters(letter_id,restore_generation,content_hash,received_day)
VALUES($id,$generation,$hash,$received);", parameters, transaction);
        }

        internal static void UpdateMemoryRestoredLetterContext(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE memory_restored_letter_context SET timeline_id=$recovered
WHERE state_id='current' AND restore_generation=$generation AND timeline_id=$requested
AND head_sequence=$sequence AND head_event_id=$head;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryRestoredLetterContext(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT c.* FROM memory_restored_letter_context c
JOIN memory_precision_state s ON c.restore_generation=s.restore_generation AND s.state_id='current'
WHERE c.state_id='current' AND c.timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryRestoredLetters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM memory_restored_letters WHERE letter_id=$id
AND restore_generation=$generation;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectStatement4(ReignDbConnection connection, object eligible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT l.*" + eligible + " ORDER BY dispatch_day DESC,created_ts DESC,letter_id LIMIT 2;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectStatement5(ReignDbConnection connection, object eligible, object rank, object rank2, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT l.*" + eligible + " AND " + rank + ">0 ORDER BY " + rank2 + " DESC,dispatch_day DESC,letter_id LIMIT 12;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM letters WHERE letter_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationBanterEpisodes(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM conversation_banter_episodes WHERE session_id=$id;", parameters, transaction);
        }

        internal static void UpsertConversationBanterEpisodes(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_banter_episodes(session_id,timeline_id,payload_json,updated_ts)
VALUES($id,$timeline,$payload,$ts) ON CONFLICT(session_id) DO UPDATE SET payload_json=EXCLUDED.payload_json,updated_ts=EXCLUDED.updated_ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationBanterEpisodes2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM conversation_banter_episodes WHERE timeline_id=$timeline ORDER BY COALESCE((payload_json::jsonb->>'createdUtc')::bigint,updated_ts) DESC,updated_ts DESC LIMIT 64;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns10(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.* FROM conversation_turns t
WHERE t.participants_json::jsonb @> $audience::jsonb AND t.role='npc' AND t.status='active' AND length(t.text)<=400
AND t.world_day<=$day AND t.accepted_sequence<=$sequence AND t.ts<=$timestamp AND " + part + @"
ORDER BY t.accepted_sequence DESC,t.ts DESC,t.turn_order DESC LIMIT 16;", parameters, transaction);
        }

        internal static void UpdateConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_sessions SET status='closed',close_reason='ui_continuation',end_world_day=$day WHERE session_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationSessions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_sessions SET status='open',close_reason='' WHERE session_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT status FROM conversation_sessions WHERE session_id=$id;", parameters, transaction);
        }
    }
}
