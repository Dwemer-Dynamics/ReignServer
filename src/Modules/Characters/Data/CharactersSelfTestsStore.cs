using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Characters module's SQL statements used only by its self-tests (verification build only); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class CharactersSelfTestStore
    {
        internal static void InsertConversationNegotiationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO conversation_negotiation_turns(receipt_id,timeline_id,owner_id,peer_id,turn_id,payload_json) VALUES($id,'main',$owner,'player',$turn,$payload);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountDynamicCharacteristics(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) n FROM dynamic_characteristics WHERE category='life_transition';", parameters, transaction);
        }

        internal static void InsertWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_events(event_id,campaign_id,timeline_id,sequence,world_day,event_type,summary,source,created_utc)
VALUES('marriage',$campaign,'main',1,9,'heroes_married','Ira married Maxax.','bannerlord_native','1970-01-01T00:00:09Z');
INSERT INTO world_history_entities(event_id,ordinal,entity_id,entity_type,role) VALUES('marriage',0,'npc','hero','spouse');
INSERT INTO world_history_knowledge_rules(rule_id,event_id,audience_type,audience_id,available_day) VALUES('marriage_npc','marriage','entity','npc',9);", parameters, transaction);
        }

        internal static void UpdateWorldHistoryEntities(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE world_history_entities SET role='observer' WHERE event_id='marriage';", parameters, transaction);
        }

        internal static void UpdateWorldHistoryEntities2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE world_history_entities SET role='' WHERE event_id='marriage';", parameters, transaction);
        }

        internal static void UpdateWorldHistoryEntities3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE world_history_entities SET role='spouse' WHERE event_id='marriage';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamicCharacteristics(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM dynamic_characteristics WHERE owner_id='npc' AND category='life_transition';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamicCharacteristics2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM dynamic_characteristics WHERE source_event_id='pledge_npc';", parameters, transaction);
        }

        internal static void InsertCharacterEditorHeads(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO character_editor_heads(hero_id,revision_token,updated_ts) VALUES($hero,'before',0);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamicCharacteristics3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM dynamic_characteristics WHERE owner_id=$hero;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterEditorHeads(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT revision_token FROM character_editor_heads WHERE hero_id=$hero;", parameters, transaction);
        }

        internal static void InsertDynamicCharacteristics(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO dynamic_characteristics(characteristic_id,owner_id,category,text,normalized_key,status,importance,first_ts,last_ts) VALUES($id,$hero,'habit','An ordinary stored habit.',$id,'active',2,400,400);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamicCharacteristics4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM dynamic_characteristics WHERE owner_id=$hero AND category='narrative_development';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM identity_evidence WHERE observer_id=$observer AND evidence_type='contextual_self_introduction';", parameters, transaction);
        }

        internal static void InsertConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES($session,$campaign,$hero,'player','open',1,1,$audience,$payload);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE session_id=$session;", parameters, transaction);
        }

        internal static void InsertConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_turns(turn_id,session_id,turn_order,role,speaker_id,text,ts)
VALUES('repeat_original','repeat_session',0,'player','player','Add unsalted broth. Then taste it.',1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE session_id=$id AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text,participants_json FROM conversation_turns WHERE session_id=$id AND status='source_only';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM conversation_turns WHERE turn_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationInputReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT input_id FROM conversation_input_receipts WHERE session_id=$id;", parameters, transaction);
        }

        internal static void InsertConversationSessions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('opening_session',$campaign,'companion','player','open',1,1,'[""player"",""companion""]','{""timelineId"":""test""}');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE session_id='opening_session';", parameters, transaction);
        }

        internal static void UpdateConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_sessions SET payload_json=$payload,participants_json=$audience WHERE session_id='opening_session';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turns;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$session AND speaker_id=$hero;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE session_id=$id AND role='player';", parameters, transaction);
        }

        internal static void UpdateConversationSessions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_sessions SET payload_json='{}' WHERE session_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE session_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationSessions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_sessions SET payload_json=$payload WHERE session_id=$id;", parameters, transaction);
        }
    }
}
