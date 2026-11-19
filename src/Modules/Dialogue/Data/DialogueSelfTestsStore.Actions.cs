using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Dialogue module's SQL statements used only by its self-tests (verification build only); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class DialogueSelfTestStore
    {
        internal static List<Dictionary<string, object>> SelectConversationNegotiationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_negotiation_turns;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationDrinkingTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_drinking_turns;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationIntoxication(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT state_json FROM conversation_intoxication WHERE hero_id='npc';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterPublicStanding(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT subject_id,standing_value,revision FROM character_public_standing WHERE campaign_id=$campaign AND timeline_id='player_actions_timeline' AND subject_id IN ($actor,$target) ORDER BY subject_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT subject_id,tag_id,status,reputation_value FROM character_reputations WHERE campaign_id=$campaign AND timeline_id='player_actions_timeline' AND subject_id IN ($actor,$target) ORDER BY subject_id,tag_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationActionOutbox(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM conversation_action_outbox WHERE action_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationActionOutbox(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_action_outbox SET payload_json=jsonb_set(payload_json::jsonb, '{record}', (payload_json::jsonb->'record') || $patch::jsonb)::text WHERE action_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationActionOutbox2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_action_outbox SET payload_json=$payload WHERE action_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterPublicStanding2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT standing_value,sources_json,revision,calculated_revision,last_changed_day,updated_ts,calculation_status,last_error
FROM character_public_standing WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject;", parameters, transaction);
        }

        internal static void UpsertCharacterPublicStanding(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_public_standing(campaign_id,timeline_id,subject_id,standing_value,sources_json,revision,calculated_revision,last_changed_day,updated_ts)
VALUES($campaign,$timeline,$subject,-5,$sources,1,1,20,$ts) ON CONFLICT(campaign_id,timeline_id,subject_id) DO UPDATE SET
standing_value=-5,sources_json=$sources,revision=character_public_standing.revision+1,calculated_revision=character_public_standing.revision+1,updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPgLocks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT DISTINCT pid FROM pg_locks WHERE NOT granted AND pg_backend_pid()=ANY(pg_blocking_pids(pid));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectStatement(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT pg_backend_pid() AS pid;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPgLocks2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT pg_cancel_backend(pid) AS cancelled FROM (SELECT DISTINCT pid FROM pg_locks
WHERE pid=$peer AND NOT granted AND $acceptance=ANY(pg_blocking_pids(pid))) AS waiting;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,
effective_affinity_a_to_b,effective_affinity_b_to_a FROM relationship_pair_chemistry WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void DeleteCharacterPublicStanding(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM character_public_standing WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject;", parameters, transaction);
        }

        internal static void UpdateCharacterPublicStanding(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE character_public_standing SET standing_value=$value,sources_json=$priorSources,revision=$revision,
calculated_revision=$calculated,last_changed_day=$day,updated_ts=$updated,calculation_status=$status,last_error=$error
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipInteractionReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT native_delta FROM relationship_interaction_receipts WHERE event_id LIKE 'action_relationship_' || $id || '_%';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipInteractionReceipts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT target_id FROM relationship_interaction_receipts WHERE event_id LIKE 'action_relationship_' || $id || '_%' AND native_delta<>0;", parameters, transaction);
        }

        internal static void UpdateConversationActionOutbox3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_action_outbox SET payload_json=(payload_json::jsonb #- '{terminalReport,projectedTs}')::text WHERE action_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryPrecisionState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT source_revision FROM memory_precision_state WHERE state_id='current';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryPrecisionState2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT restore_generation FROM memory_precision_state WHERE state_id='current' FOR SHARE;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationActionOutbox2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT action_id FROM conversation_action_outbox WHERE action_id=$id FOR UPDATE;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountPgStatActivity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS n FROM pg_stat_activity WHERE pg_backend_pid()=ANY(pg_blocking_pids(pid));", parameters, transaction);
        }

        internal static void DeleteMemories(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM memories WHERE false;", parameters, transaction);
        }

        internal static void UpdateMemoryRestoredSourceContext(ReignDbConnection connection, object column, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memory_restored_source_context SET " + column + "=$v WHERE state_id='current';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationActionOutbox3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT timeline_id,payload_json FROM conversation_action_outbox WHERE action_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryRestoredSourceContext(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT to_jsonb(c)::text AS saved FROM memory_restored_source_context c WHERE c.state_id='current';", parameters, transaction);
        }

        internal static void DeleteMemoryRestoredSourceContext(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM memory_restored_source_context; INSERT INTO memory_restored_source_context SELECT * FROM jsonb_populate_record(CAST(NULL AS memory_restored_source_context), CAST($saved AS jsonb));", parameters, transaction);
        }

        internal static void DeleteMemoryRestoredSourceContext2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM memory_restored_source_context;", parameters, transaction);
        }

        internal static void UpdateConversationActionOutbox4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_action_outbox SET payload_json=(payload_json::jsonb #- '{terminalReport,projectedShared}')::text WHERE action_id=$id;", parameters, transaction);
        }

        internal static void DeleteSharedActivityObservers(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM shared_activity_observers WHERE activity_id=$a; DELETE FROM shared_activity_events WHERE activity_id=$a;", parameters, transaction);
        }

        internal static void UpdateMemoryPrecisionState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memory_precision_state SET restore_generation='stage3_injected_restore' WHERE state_id='current';", parameters, transaction);
        }

        internal static void DeleteMemoryContinuityBranches(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM memory_continuity_branches WHERE timeline_id=$t;", parameters, transaction);
        }

        internal const string SelectRelationshipInteractionReceiptsSql = "SELECT event_id FROM relationship_interaction_receipts WHERE event_id LIKE 'action_relationship_' || $id || '_%';";

        internal const string SelectConversationRelationshipReceiptsSql = "SELECT receipt_id FROM conversation_relationship_receipts WHERE source_turn_id=$id AND mode='completed_action' AND status='action_effect' AND severity_tier='routine';";

        internal const string SelectCourtDynamicReputationMetricsSql = "SELECT subject_id,positive_count,negative_count,calculated_day FROM court_dynamic_reputation_metrics WHERE campaign_id=$campaign AND timeline_id='player_actions_timeline' AND domain='popularity' AND subject_id IN ($actor,$target) ORDER BY subject_id;";

        internal const string InsertNegotiatedActionDraftsSql = @"INSERT INTO negotiated_action_drafts(negotiation_id,campaign_id,timeline_id,broker_hero_id,first_kingdom_id,
second_kingdom_id,first_ruler_id,second_ruler_id,command,terms_json,terms_hash,status,created_day,updated_day,expires_day,queued_action_id)
VALUES($n,$campaign,'player_actions_timeline','b','k1','k2','r1','r2','make_peace','{}','h','queued',20,20,30,$id);";

        internal const string DeleteNegotiatedActionDraftsSql = "DELETE FROM negotiated_action_drafts WHERE negotiation_id=$n AND campaign_id=$campaign AND queued_action_id=$id;";

        internal const string SelectMemoriesSql = "SELECT owner_id,memory_id FROM memories WHERE event_id='action_' || $id ORDER BY owner_id,memory_id;";

        internal const string SelectSharedActivityEventsSql = "SELECT activity_id FROM shared_activity_events WHERE source_id LIKE 'action_receipt_%' AND (payload_json::jsonb->'facts'->>'actionId')=$id;";

        /// <summary>Runs a statement this module composes at run time from text kept in this class.</summary>
        internal static List<Dictionary<string, object>> QuerySql(ReignDbConnection connection, string sql, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, sql, parameters, transaction);
        }

        /// <summary>Runs a statement this module composes at run time from text kept in this class.</summary>
        internal static void ExecuteSql(ReignDbConnection connection, string sql, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, sql, parameters, transaction);
        }
    }
}
