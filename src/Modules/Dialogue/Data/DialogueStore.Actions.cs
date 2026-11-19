using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Dialogue module's SQL statements; every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class DialogueStore
    {
        internal static List<Dictionary<string, object>> SelectMemoryPrecisionState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT restore_generation FROM memory_precision_state WHERE state_id='current' FOR SHARE;", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationIntoxication(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR IGNORE INTO conversation_intoxication(hero_id,state_json) VALUES($hero,'{}');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationIntoxication(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT state_json FROM conversation_intoxication WHERE hero_id=$hero FOR UPDATE;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationDrinkingTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT receipt_json FROM conversation_drinking_turns WHERE hero_id=$hero AND turn_key=$turn;", parameters, transaction);
        }

        internal static void UpdateConversationIntoxication(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_intoxication SET state_json=$state WHERE hero_id=$hero;", parameters, transaction);
        }

        internal static void InsertConversationDrinkingTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO conversation_drinking_turns(hero_id,turn_key,receipt_json) VALUES($hero,$turn,$receipt);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryPrecisionState2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT restore_generation FROM memory_precision_state WHERE state_id='current';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryRestoredSourceContext(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT to_jsonb(c)->>'load_session_id' AS load_session_id FROM memory_restored_source_context c WHERE c.state_id='current';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationActionOutbox(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT action_id FROM conversation_action_outbox WHERE action_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationActionOutbox2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM conversation_action_outbox WHERE action_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationActionOutbox3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT fingerprint,payload_json FROM conversation_action_outbox WHERE action_id=$id FOR UPDATE;", parameters, transaction);
        }

        internal static void UpsertConversationActionOutbox(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_action_outbox(action_id,timeline_id,fingerprint,payload_json) VALUES($id,$timeline,$fingerprint,$payload)
ON CONFLICT(action_id) DO UPDATE SET payload_json=excluded.payload_json
WHERE conversation_action_outbox.fingerprint=excluded.fingerprint
AND (COALESCE(conversation_action_outbox.payload_json::jsonb->>'acceptedResponseHash','')=''
 OR COALESCE(excluded.payload_json::jsonb->>'acceptedResponseHash','')=''
 OR conversation_action_outbox.payload_json::jsonb->>'acceptedResponseHash'=excluded.payload_json::jsonb->>'acceptedResponseHash');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationActionOutbox4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT fingerprint,payload_json FROM conversation_action_outbox WHERE action_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationActionOutbox5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM conversation_action_outbox;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryPrecisionState3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT restore_generation FROM memory_precision_state WHERE state_id='current' FOR NO KEY UPDATE;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationActionOutbox6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT timeline_id,fingerprint,payload_json FROM conversation_action_outbox WHERE action_id=$id FOR UPDATE;", parameters, transaction);
        }

        internal static void UpdateConversationActionOutbox(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_action_outbox
SET payload_json=jsonb_set(payload_json::jsonb,'{terminalReport,admission}',CAST($admission AS jsonb))::text
WHERE action_id=$id AND payload_json::jsonb->'terminalReport'->>'sha256'=$sha;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipInteractionReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT event_id FROM relationship_interaction_receipts WHERE event_id=$event AND subject_id=$subject AND target_id=$target LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryRestoredSourceContext2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT to_jsonb(c)::text AS context FROM memory_restored_source_context c WHERE c.state_id='current';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationActionOutbox7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT timeline_id,fingerprint,payload_json FROM conversation_action_outbox WHERE action_id=$id;", parameters, transaction);
        }

        internal static void Set(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "SET TRANSACTION READ ONLY;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectStatement(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT set_config('search_path', $path, true) AS path;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamic(ReignDbConnection connection, object part, object part2, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT 1 AS hit FROM " + part + " WHERE " + part2 + "=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationActionOutbox8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM conversation_action_outbox WHERE action_id=$id FOR UPDATE;", parameters, transaction);
        }

        internal static void UpdateConversationActionOutbox2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_action_outbox
SET payload_json=jsonb_set(jsonb_set(payload_json::jsonb,'{terminalReport,projectedTs}',
 COALESCE(payload_json::jsonb->'terminalReport'->'projectedTs',to_jsonb(CAST($ts AS bigint)))),
 '{terminalReport,projectedShared}',to_jsonb(CAST($ts AS bigint)))::text
WHERE action_id=$id AND payload_json::jsonb->'terminalReport'->>'sha256'=$sha;", parameters, transaction);
        }
    }
}
