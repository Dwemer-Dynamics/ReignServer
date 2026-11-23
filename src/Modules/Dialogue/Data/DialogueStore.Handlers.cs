using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueStore
    {
        internal static List<Dictionary<string, object>> SelectEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT event_id FROM events
WHERE event_type='daily_world_snapshot';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT hero_a_id,hero_b_id FROM relationship_pair_chemistry
WHERE hero_a_id=$npc OR hero_b_id=$npc ORDER BY last_day DESC LIMIT 8;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamic8(ReignDbConnection connection, object table, object whereValue, object order, object key, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM " + table + " WHERE " + whereValue
                    + " ORDER BY " + order + " DESC," + key + " LIMIT $batch OFFSET $offset;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamic9(ReignDbConnection connection, object table, object idColumn, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM " + table + " WHERE status='active' AND " + idColumn
                    + " IN (" + part + ");", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamic10(ReignDbConnection connection, object ftsTable, object table, object key, object key2, object part, object key3, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT m.*,ts_rank(to_tsvector('simple',COALESCE(f.text,'') || ' ' || COALESCE(f.tags,'') || ' ' || COALESCE(f.entities,'')),websearch_to_tsquery('simple',$query)) AS lexical_rank"
                    + " FROM " + ftsTable + " f JOIN " + table + " m ON m." + key + "=f." + key2
                    + " WHERE m.status='active' AND to_tsvector('simple',COALESCE(f.text,'') || ' ' || COALESCE(f.tags,'') || ' ' || COALESCE(f.entities,'')) @@ websearch_to_tsquery('simple',$query)"
                    + (part)
                    + " ORDER BY lexical_rank DESC,m." + key3 + " LIMIT 128 OFFSET $offset;", parameters, transaction);
        }

        internal static void UpsertEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO events(
    event_id,campaign_id,ts,world_day,event_type,event_category,event_subtype,location_id,summary,participants_json,witnesses_json,about_entities_json,
    known_by_json,heard_as_rumor_by_json,hidden_from_json,visibility,importance,source_reliability,confidence,payload_json,vector_id,embedding_status,created_utc)
VALUES(
    $event_id,$campaign_id,$ts,$world_day,$event_type,$event_category,$event_subtype,$location_id,$summary,$participants_json,$witnesses_json,$about_entities_json,
    $known_by_json,$heard_as_rumor_by_json,$hidden_from_json,$visibility,$importance,$source_reliability,$confidence,$payload_json,$vector_id,$embedding_status,$created_utc);", parameters, transaction);
        }

        internal static void DeleteMemoryFts4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM memory_fts WHERE memory_id IN (SELECT memory_id FROM memories WHERE event_id=$event_id);", parameters, transaction);
        }

        internal static void DeleteEventFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM event_fts WHERE event_id=$event_id;", parameters, transaction);
        }

        internal static void DeleteMemories3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM memories WHERE event_id=$event_id;", parameters, transaction);
        }

        internal static void DeleteKnowledgeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM knowledge_receipts WHERE event_id=$event_id;", parameters, transaction);
        }

        internal static void DeleteBeliefs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM beliefs WHERE event_id=$event_id;", parameters, transaction);
        }

        internal static void DeleteObligations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM obligations WHERE event_id=$event_id;", parameters, transaction);
        }

        internal static void DeleteComprehension(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM comprehension WHERE event_id=$event_id;", parameters, transaction);
        }

        internal static void UpsertMemories(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO memories(
    memory_id,event_id,owner_id,memory_type,ts,world_day,location_id,summary,participants_json,witnesses_json,about_entities_json,
    known_by_json,heard_as_rumor_by_json,hidden_from_json,visibility,importance,emotional_weight,confidence,tags_json,status,source,vector_id,embedding_status,payload_json)
VALUES(
    $memory_id,$event_id,$owner_id,$memory_type,$ts,$world_day,$location_id,$summary,$participants_json,$witnesses_json,$about_entities_json,
    $known_by_json,$heard_as_rumor_by_json,$hidden_from_json,$visibility,$importance,$emotional_weight,$confidence,$tags_json,$status,$source,$vector_id,$embedding_status,$payload_json);", parameters, transaction);
        }

        internal static void InsertMemoryFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO memory_fts(memory_id, text, tags, entities) VALUES($memory_id, $text, $tags, $entities);", parameters, transaction);
        }

        internal static void InsertEventFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO event_fts(event_id, text, tags, entities) VALUES($event_id, $text, $tags, $entities);", parameters, transaction);
        }

        internal static void UpsertBeliefs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO beliefs(belief_id,event_id,believer_id,claim,confidence,source,known_by_json,about_entities_json,hidden_from_json,visibility,ts,payload_json)
VALUES($belief_id,$event_id,$believer_id,$claim,$confidence,$source,$known_by_json,$about_entities_json,$hidden_from_json,$visibility,$ts,$payload_json);", parameters, transaction);
        }

        internal static void UpsertObligations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO obligations(obligation_id,event_id,owed_by,owed_to,description,status,created_day,importance,participants_json,about_entities_json,known_by_json,hidden_from_json,visibility,ts,payload_json,timeline_id)
VALUES($obligation_id,$event_id,$owed_by,$owed_to,$description,$status,$created_day,$importance,$participants_json,$about_entities_json,$known_by_json,$hidden_from_json,$visibility,$ts,$payload_json,$timeline);", parameters, transaction);
        }

        internal static void UpsertComprehension(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO comprehension(comprehension_id,event_id,owner_id,text,stance,confidence,known_by_json,about_entities_json,hidden_from_json,ts,payload_json)
VALUES($comprehension_id,$event_id,$owner_id,$text,$stance,$confidence,$known_by_json,$about_entities_json,$hidden_from_json,$ts,$payload_json);", parameters, transaction);
        }

        internal static void DeleteMemoryProjections3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM memory_projections WHERE owner_id=$owner;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM memories
WHERE owner_id=$owner_id AND status='active'
ORDER BY ts DESC LIMIT 400;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryConsolidationPage(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT memory_id,ts,source,tags_json,participants_json,location_id,payload_json
FROM memories WHERE owner_id=$owner_id AND status='active'
AND (NOT $has_cursor OR ts < $ts OR (ts = $ts AND memory_id COLLATE ""C"" > $id))
ORDER BY ts DESC,memory_id COLLATE ""C"" LIMIT $limit;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryConsolidationRows(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM memories
WHERE owner_id=$owner_id AND status='active'
AND memory_id IN (SELECT jsonb_array_elements_text(CAST($ids AS jsonb)));", parameters, transaction);
        }

        internal static void UpsertSummaries3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO summaries(
summary_id,scope,owner_id,summary_type,summary,source_events_json,start_ts,end_ts,event_count,location_id,
participants_json,about_entities_json,known_by_json,hidden_from_json,visibility,importance,confidence,tags_json,
status,source,vector_id,embedding_status,updated_ts,payload_json)
VALUES($summary_id,$scope,$owner_id,'middle_term',$summary,$source_events_json,$start_ts,$end_ts,$event_count,$location_id,
$participants_json,$about_entities_json,$known_by_json,'[]','private',$importance,$confidence,$tags_json,
'active',$source,'','not_indexed',$updated_ts,$payload_json)
ON CONFLICT(summary_id) DO UPDATE SET summary=$summary,payload_json=$payload_json,updated_ts=$updated_ts;", parameters, transaction);
        }

        internal static void UpdateMemories7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memories SET status='consolidated' WHERE memory_id=$memory_id;", parameters, transaction);
        }

        internal static void DeleteSummaryFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM summary_fts WHERE summary_id=$summary_id;", parameters, transaction);
        }

        internal static void InsertSummaryFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO summary_fts(summary_id,text,tags,entities) VALUES($summary_id,$text,$tags,$entities);", parameters, transaction);
        }
    }
}
