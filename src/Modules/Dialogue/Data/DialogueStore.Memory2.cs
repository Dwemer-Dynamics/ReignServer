using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueStore
    {
        internal static List<Dictionary<string, object>> SelectConversationTurns79(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT speaker_id,MAX(world_day) AS day FROM conversation_turns WHERE status='active' AND role='npc' GROUP BY speaker_id ORDER BY MAX(ts) DESC LIMIT 16;", parameters, transaction);
        }

        internal static string SelectMemorySpecialistSourcesSql(object score, object part, object part2, object part3)
        {
            return @"SELECT t.*,i.source_hash AS specialist_source_hash,(" + score + @") AS specialist_score
FROM memory_specialist_sources i JOIN conversation_turns t ON t.turn_id=i.turn_id
JOIN conversation_sessions s ON s.session_id=t.session_id WHERE i.schema_version=$schema AND (" + part + ") AND "
                + part2 + " AND t.status='active' AND t.world_day<=$day AND t.accepted_sequence<=$asOfSequence AND t.ts<=$asOfTimestamp AND "
                + part3 + " ORDER BY specialist_score DESC,t.ts DESC,t.turn_order DESC,t.turn_id";
        }

        internal const string SelectConversationTurnsSql5 = @"SELECT t.*,t.xmin::text AS source_revision FROM conversation_turns t LEFT JOIN memory_specialist_sources i ON t.turn_id=i.turn_id
WHERE t.status='active' AND t.text<>'' AND (i.turn_id IS NULL OR i.schema_version<>$schema OR i.source_revision<>t.xmin::text)";

        internal static List<Dictionary<string, object>> SelectEmbeddingJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM embedding_jobs
WHERE status='pending' AND next_attempt_ts<=$now
ORDER BY CASE WHEN operation='delete' THEN 0 ELSE 1 END,
CASE source_type
    WHEN 'summary' THEN 0
    WHEN 'conversation_turn' THEN 1
    WHEN 'memory' THEN 2
    WHEN 'event' THEN 3
    WHEN 'belief' THEN 4
    WHEN 'comprehension' THEN 5
    WHEN 'world_history_event' THEN 7
    ELSE 8 END,
    CASE WHEN source_type IN ('summary','conversation_turn','memory','event') THEN -enqueued_ts ELSE enqueued_ts END
LIMIT $limit;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryRestoredSourceContext4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT timeline_id FROM memory_restored_source_context WHERE state_id='current';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEmbeddingDocuments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT content_hash,status FROM embedding_documents
WHERE source_type=$type AND source_id=$id AND model_version=$model AND provider=$provider LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEmbeddingDocuments2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT vector_id FROM embedding_documents WHERE source_type=$type AND source_id=$id AND status<>'deleted';", parameters, transaction);
        }

        internal static void UpdateEmbeddingDocuments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE embedding_documents SET status='deleted',updated_ts=$ts WHERE source_type=$type AND source_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamic7(ReignDbConnection connection, object table, object id, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM " + table + " WHERE " + id + "=$id LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertEmbeddingDocuments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO embedding_documents(source_type,source_id,model_version,provider,campaign_id,timeline_id,content_hash,vector_id,dimensions,status,attempt_count,last_error,updated_ts,payload_json)
VALUES($type,$id,$model,$provider,$campaign,$timeline,$hash,$vector,384,'indexed',0,'',$ts,$payload);", parameters, transaction);
        }

        internal static void UpdateEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE events SET vector_id=$vector,embedding_status='indexed' WHERE event_id=$id;", parameters, transaction);
        }

        internal static void UpdateMemories6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memories SET vector_id=$vector,embedding_status='indexed' WHERE memory_id=$id;", parameters, transaction);
        }

        internal static void UpdateSummaries4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE summaries SET vector_id=$vector,embedding_status='indexed' WHERE summary_id=$id;", parameters, transaction);
        }

        internal static void DeleteEmbeddingJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM embedding_jobs WHERE source_type=$type AND source_id=$id;", parameters, transaction);
        }

        internal static void UpdateEmbeddingJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE embedding_jobs SET attempt_count=$attempts,next_attempt_ts=$next,last_error=$error,updated_ts=$ts WHERE source_type=$type AND source_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountSqliteMaster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM sqlite_master WHERE type='trigger' AND name LIKE 'trg_semantic_%';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountEmbeddingJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM embedding_jobs WHERE source_type IN ('event','memory');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memories WHERE summary LIKE '%abandoned his ally%' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountSqliteMaster2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM sqlite_master WHERE type='trigger' AND name LIKE 'trg_semantic_conversation_turns_%';", parameters, transaction);
        }

        internal static void InsertWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_events
(event_id,campaign_id,timeline_id,sequence,world_day,event_type,category,phase,dissemination_class,summary,semantic_text,created_utc)
VALUES('semantic_routine_history',$campaign,'timeline',1,10,'settlement_entered','settlement','completed','ordinary','Routine entry','Routine entry',$utc);", parameters, transaction);
        }

        internal static void InsertWorldHistoryEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_events
(event_id,campaign_id,timeline_id,sequence,world_day,event_type,category,phase,dissemination_class,summary,semantic_text,created_utc)
VALUES('semantic_significant_history',$campaign,'timeline',2,10,'siege_completed','warfare','completed','ordinary','Siege completed','Siege completed',$utc);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEmbeddingJobs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM embedding_jobs WHERE source_type='world_history_event' AND source_id='semantic_routine_history' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEmbeddingJobs3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM embedding_jobs WHERE source_type='world_history_event' AND source_id='semantic_significant_history' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEmbeddingJobs4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM embedding_jobs LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEmbeddingJobs5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM embedding_jobs WHERE source_type=$type AND source_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='semantic_original_dialogue_v1' LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('semantic_original_dialogue_v1','complete');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='semantic_memory_v1_seeded' LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('semantic_memory_v1_seeded','complete');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='semantic_memory_provider_generation' LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('semantic_memory_provider_generation',$value);", parameters, transaction);
        }

        internal static void UpsertSchemaMeta12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('semantic_memory_version','1');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='semantic_conversation_embedding_policy' LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateEvents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE events SET embedding_status='skipped'
WHERE LOWER(COALESCE(event_category,'')) IN ('conversation','correspondence');", parameters, transaction);
        }

        internal static void DeleteMemoryFts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM memory_fts WHERE memory_id IN
(SELECT memory_id FROM memories WHERE LOWER(COALESCE(memory_type,''))='interpersonal_experience'
 AND LOWER(COALESCE(source,'')) IN ('dialogue_turn','party_chat_turn','social_event_turn'));", parameters, transaction);
        }

        internal static void DeleteMemories2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM memories
WHERE LOWER(COALESCE(memory_type,''))='interpersonal_experience'
 AND LOWER(COALESCE(source,'')) IN ('dialogue_turn','party_chat_turn','social_event_turn');", parameters, transaction);
        }

        internal static void UpsertSchemaMeta13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('semantic_conversation_embedding_policy',$value);", parameters, transaction);
        }

        internal static void DeleteEmbeddingJobs2(ReignDbConnection connection, object ineligibleEvent, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM embedding_jobs WHERE source_type='event' AND source_id IN
(SELECT e.event_id FROM events e WHERE " + ineligibleEvent + ");", parameters, transaction);
        }

        internal static void UpsertEmbeddingJobs(ReignDbConnection connection, object ineligibleEvent, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO embedding_jobs(source_type,source_id,operation,status,attempt_count,next_attempt_ts,last_error,enqueued_ts,updated_ts)
SELECT 'event',d.source_id,'delete','pending',0,0,'',strftime('%s','now'),strftime('%s','now')
FROM embedding_documents d JOIN events e ON e.event_id=d.source_id
WHERE d.source_type='event' AND d.status='indexed' AND " + ineligibleEvent + @"
ON CONFLICT(source_type,source_id) DO UPDATE SET operation='delete',status='pending',attempt_count=0,next_attempt_ts=0,last_error='',enqueued_ts=strftime('%s','now'),updated_ts=strftime('%s','now');", parameters, transaction);
        }

        internal static void DeleteEmbeddingDocuments(ReignDbConnection connection, object ineligibleEvent, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM embedding_documents WHERE source_type='event' AND status<>'indexed' AND source_id IN
(SELECT e.event_id FROM events e WHERE " + ineligibleEvent + ");", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='semantic_world_history_filter' LIMIT 1;", parameters, transaction);
        }

        internal static void DeleteEmbeddingJobs3(ReignDbConnection connection, object eligible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM embedding_jobs
WHERE source_type='world_history_event' AND source_id IN
(SELECT w.event_id FROM world_history_events w WHERE NOT " + eligible + ");", parameters, transaction);
        }

        internal static void UpsertEmbeddingJobs2(ReignDbConnection connection, object eligible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO embedding_jobs(source_type,source_id,operation,status,attempt_count,next_attempt_ts,last_error,enqueued_ts,updated_ts)
SELECT 'world_history_event',d.source_id,'delete','pending',0,0,'',strftime('%s','now'),strftime('%s','now')
FROM embedding_documents d JOIN world_history_events w ON w.event_id=d.source_id
WHERE d.source_type='world_history_event' AND d.status='indexed' AND NOT " + eligible + @"
ON CONFLICT(source_type,source_id) DO UPDATE SET operation='delete',status='pending',attempt_count=0,next_attempt_ts=0,last_error='',enqueued_ts=strftime('%s','now'),updated_ts=strftime('%s','now');", parameters, transaction);
        }

        internal static void DeleteEmbeddingDocuments2(ReignDbConnection connection, object eligible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM embedding_documents
WHERE source_type='world_history_event' AND status<>'indexed' AND source_id IN
(SELECT w.event_id FROM world_history_events w WHERE NOT " + eligible + ");", parameters, transaction);
        }

        internal static void UpsertSchemaMeta14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('semantic_world_history_filter',$value);", parameters, transaction);
        }

        internal static void InsertOrIgnoreEmbeddingJobs(ReignDbConnection connection, object sourceType, object idColumn, object table, object whereValue, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR IGNORE INTO embedding_jobs(source_type,source_id,operation,status,attempt_count,next_attempt_ts,last_error,enqueued_ts,updated_ts) "
                + "SELECT '" + sourceType + "'," + idColumn + ",'upsert','pending',0,0,'',strftime('%s','now'),strftime('%s','now') FROM " + table + whereValue + ";", parameters, transaction);
        }

        internal static void DeleteEmbeddingJobs4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM embedding_jobs;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM world_history_events WHERE event_id=$id AND timeline_id=$timeline
AND ($location='' OR location_id=$location) AND ($from<0 OR world_day>=$from) AND world_day<=$to LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEmbeddingDocuments3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT status,COUNT(*) count FROM embedding_documents GROUP BY status;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEmbeddingJobs6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT status,COUNT(*) count FROM embedding_jobs GROUP BY status;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEmbeddingJobs7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT source_type,source_id,attempt_count,last_error,next_attempt_ts FROM embedding_jobs WHERE last_error<>'' ORDER BY updated_ts DESC LIMIT 25;", parameters, transaction);
        }

        internal static void UpdateEmbeddingDocuments2(ReignDbConnection connection, object eligible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE embedding_documents SET status='stale',updated_ts=$ts
WHERE source_type<>'world_history_event' OR source_id IN
(SELECT w.event_id FROM world_history_events w WHERE " + eligible + ");", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountEmbeddingJobs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM embedding_jobs;", parameters, transaction);
        }

        internal static void UpdateEmbeddingJobs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE embedding_jobs SET status='pending',attempt_count=0,next_attempt_ts=0,last_error='',updated_ts=$ts WHERE last_error<>'';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountEmbeddingJobs3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM embedding_jobs WHERE status='pending';", parameters, transaction);
        }

        internal static string UpsertEmbeddingJobsSql(object sourceType, object idColumn)
        {
            return "INSERT INTO embedding_jobs(source_type,source_id,operation,status,attempt_count,next_attempt_ts,last_error,enqueued_ts,updated_ts) "
                + "VALUES('" + sourceType + "',NEW." + idColumn + ",'upsert','pending',0,0,'',strftime('%s','now'),strftime('%s','now')) "
                + "ON CONFLICT(source_type,source_id) DO UPDATE SET operation='upsert',status='pending',attempt_count=0,next_attempt_ts=0,last_error='',enqueued_ts=strftime('%s','now'),updated_ts=strftime('%s','now');";
        }

        internal static string UpsertEmbeddingJobsSql2(object sourceType, object idColumn)
        {
            return "INSERT INTO embedding_jobs(source_type,source_id,operation,status,attempt_count,next_attempt_ts,last_error,enqueued_ts,updated_ts) "
                + "VALUES('" + sourceType + "',NEW." + idColumn + ",'delete','pending',0,0,'',strftime('%s','now'),strftime('%s','now')) "
                + "ON CONFLICT(source_type,source_id) DO UPDATE SET operation='delete',status='pending',attempt_count=0,next_attempt_ts=0,last_error='',enqueued_ts=strftime('%s','now'),updated_ts=strftime('%s','now');";
        }

        internal static void InsertOrIgnoreSharedActivityEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO shared_activity_events
(activity_id,timeline_id,player_id,kind,world_day,sequence,location_id,source_id,source_hash,payload_json)
VALUES($id,$timeline,$player,$kind,$day,$sequence,$location,$id,$hash,$json);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSharedActivityEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT timeline_id,world_day,source_hash FROM shared_activity_events WHERE activity_id=$id;", parameters, transaction);
        }

        internal static void InsertOrIgnoreSharedActivityObservers(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO shared_activity_observers(activity_id,owner_id,role) VALUES($id,$owner,$role);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSharedTravelContact(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM shared_travel_contact WHERE timeline_id=$timeline
AND player_id=$player AND owner_id=$owner AND party_id=$party AND end_day>=$start AND start_day<=$start
ORDER BY end_day DESC LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateSharedTravelContact(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE shared_travel_contact SET end_day=MAX(end_day,$day),source_id=$event WHERE interval_id=$id;", parameters, transaction);
        }

        internal static void InsertOrIgnoreSharedTravelContact(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO shared_travel_contact
(interval_id,timeline_id,player_id,owner_id,party_id,start_day,end_day,source_id,motif)
VALUES($id,$timeline,$player,$owner,$party,$start,$day,$event,$motif);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectStatement10(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM (" + part
                + ") ranked WHERE family_rank<=8 ORDER BY world_day DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT payload_json FROM conversation_continuity WHERE timeline_id=$timeline AND owner_id=$owner
AND kind='agenda' AND status IN ('open','active','waiting','blocked','deferred');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> Statement6(ReignDbConnection connection, object selectValue, object order, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, selectValue + " AND a.payload_json ILIKE $topic" + order, parameters, transaction);
        }

        internal static void InsertOrIgnoreSharedActivityDeliveries(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO shared_activity_deliveries(activity_id,owner_id,turn_id,world_day)
SELECT activity_id,$owner,$turn,$day FROM shared_activity_events a WHERE activity_id=$id AND timeline_id=$timeline
AND EXISTS(SELECT 1 FROM shared_activity_observers o WHERE o.activity_id=a.activity_id AND o.owner_id=$owner);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents8(ReignDbConnection connection, object supported, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT e.* FROM world_history_events e
WHERE e.timeline_id=$timeline AND e.world_day<=$day
AND e.source IN ('bannerlord_native','arena_overhaul') AND e.event_type IN (" + supported + @")
AND EXISTS(SELECT 1 FROM world_history_entities n WHERE n.event_id=e.event_id AND n.entity_id=$owner)
AND EXISTS(SELECT 1 FROM world_history_entities p WHERE p.event_id=e.event_id AND p.entity_id=$player)
AND NOT EXISTS(SELECT 1 FROM shared_activity_events a WHERE a.activity_id=e.event_id)
ORDER BY e.world_day DESC,e.sequence DESC LIMIT 80;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns80(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.*,s.player_id,s.payload_json AS session_payload FROM conversation_turns t
JOIN conversation_sessions s ON s.session_id=t.session_id
WHERE t.speaker_id=$owner AND s.player_id=$player AND t.status='active' AND t.world_day<=$day
AND t.role='npc' AND t.text<>'' AND t.channel NOT IN ('letter','correspondence')
AND COALESCE(NULLIF(json_extract(t.payload_json,'$.timelineId'),''),json_extract(s.payload_json,'$.timelineId'))=$timeline
ORDER BY t.world_day DESC,t.ts DESC,t.turn_order DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents9(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT event_id FROM world_history_events WHERE timeline_id=$timeline AND event_id IN ("
                + part + ");", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_continuity WHERE timeline_id=$timeline
AND owner_id=$owner AND kind='agenda' AND status IN ('open','active','waiting','blocked','deferred');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectChanged(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"WITH changed AS (UPDATE conversation_continuity SET status=$status,revision=$next,payload_json=$json,updated_ts=$ts
WHERE record_id=$id AND revision=$expected AND owner_id=$owner AND timeline_id=$timeline RETURNING record_id)
INSERT INTO conversation_continuity_versions(version_id,record_id,source_event_id,revision,payload_json,created_ts)
SELECT $version,record_id,$event,$next,$json,$ts FROM changed ON CONFLICT DO NOTHING RETURNING record_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSharedTravelContact2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM shared_travel_contact WHERE timeline_id=$timeline AND player_id=$player
AND owner_id=$owner AND end_day<=$day ORDER BY end_day DESC LIMIT 4;", parameters, transaction);
        }

        internal const string SelectSharedActivityDeliveriesSql = @"SELECT a.*,o.role AS observer_role,
CASE WHEN EXISTS(SELECT 1 FROM shared_activity_deliveries d WHERE d.activity_id=a.activity_id AND d.owner_id=$owner) THEN 1 ELSE 0 END AS delivered
FROM shared_activity_events a
JOIN shared_activity_observers o ON o.activity_id=a.activity_id
WHERE a.timeline_id=$timeline AND a.player_id=$player AND o.owner_id=$owner AND a.world_day<=$day ";

        internal static List<Dictionary<string, object>> SelectTemporalKnowledgeAssertions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM temporal_knowledge_assertions
WHERE campaign_id=$campaign AND timeline_id=$timeline AND perspective_owner_id=$owner AND fact_key=$fact AND valid_to_ts IS NULL
ORDER BY revision DESC LIMIT 1 FOR UPDATE;", parameters, transaction);
        }

        internal static void UpdateTemporalKnowledgeAssertions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE temporal_knowledge_assertions SET valid_to_ts=$ts,valid_to_day=$day WHERE assertion_id=$id AND valid_to_ts IS NULL;", parameters, transaction);
        }

        internal static void InsertTemporalKnowledgeAssertions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO temporal_knowledge_assertions
(assertion_id,campaign_id,fact_key,subject_id,predicate,object_id,claim,perspective_owner_id,assertion_kind,confidence,truth_status,
valid_from_ts,valid_to_ts,observed_ts,supersedes_assertion_id,source_event_id,source_record_id,known_by_json,hidden_from_json,visibility,payload_json,
timeline_id,revision,source_hash,event_day,learned_day,recorded_ts,cardinality,processing_version)
VALUES($id,$campaign,$fact,$subject,$predicate,$object,$claim,$owner,$kind,$confidence,$truth,$ts,NULL,$ts,$supersedes,$event,$record,$known,$hidden,$visibility,$payload,
$timeline,$revision,$hash,$day,$learned,$ts,$cardinality,$version);", parameters, transaction);
        }

        internal static void UpsertTemporalKnowledgeNodes(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO temporal_knowledge_nodes(node_id,node_type,label,payload_json,created_ts,updated_ts)
VALUES($id,$type,$label,$payload,$ts,$ts) ON CONFLICT(node_id) DO UPDATE SET label=$label,updated_ts=$ts;", parameters, transaction);
        }

        internal static void InsertOrIgnoreTemporalKnowledgeEdges(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO temporal_knowledge_edges(edge_id,from_node_id,to_node_id,edge_type,assertion_id,valid_from_ts,valid_to_ts,payload_json)
VALUES($id,$from,$to,$type,$assertion,$ts,NULL,'{}');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectTemporalKnowledgeAssertions2(ReignDbConnection connection, object eligible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM temporal_knowledge_assertions " + eligible + @"
AND ($query='' OR to_tsvector('simple',claim || ' ' || subject_id || ' ' || predicate || ' ' || object_id) @@ websearch_to_tsquery('simple',$query))
ORDER BY ts_rank(to_tsvector('simple',claim),websearch_to_tsquery('simple',$query)) DESC,learned_day DESC,revision DESC,assertion_id LIMIT $batch OFFSET $offset;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectTemporalKnowledgeAssertions3(ReignDbConnection connection, object eligible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM temporal_knowledge_assertions " + eligible + @"
AND fact_key=$fact AND perspective_owner_id=$owner
ORDER BY learned_day DESC,COALESCE(NULLIF(recorded_ts,0),observed_ts) DESC,
CASE WHEN timeline_id=$timeline THEN 1 ELSE 0 END DESC,revision DESC,assertion_id LIMIT $batch OFFSET $offset;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents10(ReignDbConnection connection, object publicWorldHistorySql, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT e.* FROM world_history_events e WHERE e.is_complete=1 AND " + publicWorldHistorySql, parameters, transaction);
        }

        internal static void InsertOrIgnoreWorldHistoryKnowledgeRules(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_knowledge_rules
(rule_id,event_id,audience_type,audience_id,available_day,acquisition_mode,confidence)
VALUES($rule,$event,'global','all',$day,'public_world_news',1) ON CONFLICT(rule_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents11(ReignDbConnection connection, object visible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT e.* FROM world_history_events e WHERE " + visible
                + " ORDER BY CASE WHEN e.event_type LIKE 'tournament_%' THEN 1 ELSE 0 END,e.world_day DESC,e.sequence DESC LIMIT 24;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEntities2(ReignDbConnection connection, object visible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT DISTINCT n.entity_id,n.name_snapshot FROM world_history_entities n
JOIN world_history_events e ON e.event_id=n.event_id WHERE " + visible + @"
AND LENGTH(n.name_snapshot)>=3 AND LOWER($topic) LIKE '%' || LOWER(n.name_snapshot) || '%';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents12(ReignDbConnection connection, object visible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT e.* FROM world_history_events e WHERE " + visible + @"
AND EXISTS(SELECT 1 FROM world_history_entities n JOIN jsonb_array_elements_text(CAST($subjects AS jsonb)) s
ON s.value=n.entity_id WHERE n.event_id=e.event_id) ORDER BY e.world_day DESC,e.sequence DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryFts(ReignDbConnection connection, object visible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT e.* FROM world_history_fts JOIN world_history_events e ON e.event_id=world_history_fts.event_id
WHERE world_history_fts MATCH $query AND " + visible + " ORDER BY e.world_day DESC,e.sequence DESC LIMIT 24;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryTimelines4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_timelines WHERE timeline_id=$branch;", parameters, transaction);
        }

        internal static void UpsertWorldHistoryKnowledgeRules(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_knowledge_rules
(rule_id,event_id,audience_type,audience_id,available_day,acquisition_mode,confidence)
SELECT event_id || ':public_tournament_news',event_id,'global','all',world_day,'public_tournament_news',1
FROM world_history_events WHERE event_type IN ('tournament_started','tournament_finished')
AND dissemination_class IN ('ordinary','major_world')
ON CONFLICT(rule_id) DO UPDATE SET available_day=excluded.available_day;", parameters, transaction);
        }

        internal static void UpdateWorldHistoryKnowledgeRules(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE world_history_knowledge_rules SET available_day=
(SELECT e.world_day FROM world_history_events e WHERE e.event_id=world_history_knowledge_rules.event_id)
WHERE acquisition_mode IN ('realm_news','major_world_news') AND event_id IN
(SELECT event_id FROM world_history_events WHERE event_type IN ('tournament_started','tournament_finished')
AND dissemination_class IN ('ordinary','major_world'));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistoryEvents(ReignDbConnection connection, object visible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(DISTINCT e.event_id) AS count FROM world_history_events e WHERE " + visible + @"
AND e.event_type='tournament_finished'
AND EXISTS(SELECT 1 FROM world_history_entities n WHERE n.event_id=e.event_id
AND n.entity_id=$player AND n.role LIKE '%winner%');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents13(ReignDbConnection connection, object visible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT e.* FROM world_history_events e WHERE " + visible + @"
AND e.event_type='tournament_finished'
AND EXISTS(SELECT 1 FROM world_history_entities n WHERE n.event_id=e.event_id
AND n.entity_id=$player AND n.role LIKE '%winner%')
ORDER BY CASE WHEN e.location_id=$location THEN 0 ELSE 1 END,e.sequence DESC LIMIT 3;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents14(ReignDbConnection connection, object visible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT e.* FROM world_history_events e WHERE " + visible
                        + @" AND e.event_type IN ('tournament_started','tournament_finished')
ORDER BY CASE WHEN e.location_id=$location THEN 0 ELSE 1 END,e.sequence DESC LIMIT 24;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryFts2(ReignDbConnection connection, object visible, object eligible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT e.* FROM world_history_fts
JOIN world_history_events e ON e.event_id=world_history_fts.event_id
WHERE world_history_fts MATCH $query AND " + visible + " AND " + eligible
                        + " ORDER BY e.sequence DESC LIMIT 48;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents15(ReignDbConnection connection, object visible, object eligible, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT e.* FROM world_history_events e WHERE " + visible
                        + " AND " + eligible + " AND e.event_id IN (" + part + ");", parameters, transaction);
        }
    }
}
