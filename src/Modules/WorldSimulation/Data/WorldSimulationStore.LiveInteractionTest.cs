using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationStore
    {
        internal static List<Dictionary<string, object>> CountDynamicCharacteristics(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM dynamic_characteristics WHERE owner_id=$owner AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM summaries WHERE summary_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM conversation_turns WHERE session_id=$id AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConversationTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(DISTINCT exchange_id) count FROM conversation_turns WHERE session_id=$id AND status='active' AND exchange_id<>'';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountMemorySources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM memory_sources WHERE document_type='summary' AND document_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountSummaryFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM summary_fts WHERE summary_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountEmbeddingJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM embedding_jobs WHERE source_type='summary' AND source_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountMemoryBackgroundJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM memory_background_jobs WHERE job_type='memory_consolidation' AND session_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountSummaries(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) count FROM summaries
WHERE owner_id=$owner AND summary_type='scene' AND status='active' AND memory_lane=$lane
AND ($player='' OR participants_json LIKE $playerLike);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT summary_id FROM summaries
WHERE owner_id=$owner AND summary_type='arc' AND status='active' AND memory_lane=$lane
AND ($player='' OR participants_json LIKE $playerLike)
ORDER BY updated_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountMemorySources2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM memory_sources WHERE document_type='summary' AND document_id=$id AND source_type='summary';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryBackgroundJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM memory_background_jobs
WHERE job_type='memory_consolidation' AND session_id=$session ORDER BY owner_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountMemories(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(DISTINCT m.event_id) count
FROM memories m JOIN conversation_turns t ON t.event_id=m.event_id
WHERE t.session_id=$session AND m.owner_id=$owner AND m.event_id<>'';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM summaries
WHERE scope=$scope AND owner_id=$owner AND summary_type='middle_term'
ORDER BY updated_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountMemorySources3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) count FROM memory_sources
WHERE document_type='summary' AND document_id=$id AND source_type='memory';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountMemorySources4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) count FROM memory_sources
WHERE document_type='summary' AND document_id=$id AND source_type='event';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountMemorySources5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) count
FROM memory_sources s JOIN memories m ON m.memory_id=s.source_id
WHERE s.document_type='summary' AND s.document_id=$id AND s.source_type='memory'
AND m.status<>'consolidated';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountEmbeddingJobs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) count FROM embedding_jobs
WHERE source_type='summary' AND source_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT summary_id FROM summaries WHERE summary_type='arc' AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountMemorySources6(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) count FROM memory_sources
WHERE document_type='summary' AND document_id=$id AND source_type='summary'
AND source_id IN (" + part + ");", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_relationship_receipts WHERE receipt_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryLieChecks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_lie_checks WHERE lie_check_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipBenefits(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_relationship_benefits WHERE benefit_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSharedRelationshipHistoryChapters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM shared_relationship_history_chapters WHERE history_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT affinity_a_to_b,affinity_b_to_a FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }
    }
}
