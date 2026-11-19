using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationStore
    {
        internal static List<Dictionary<string, object>> SelectSchemaMeta3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='world_history_retention_repair_v2' LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateWorldHistoryEvents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE world_history_events
SET retention_class='routine'
WHERE retention_class='consequential' AND event_type IN (
'settlement_entered','settlement_left','troops_recruited',
'troops_given_to_settlement','troops_deserted','items_looted',
'item_sold','loot_distributed','caravan_transaction_completed',
'prisoners_sold','mobile_party_created','mobile_party_destroyed',
'party_leader_changed','ship_created','ship_destroyed','ship_repaired',
'ship_owner_changed','issue_updated');", parameters, transaction);
        }

        internal static void UpsertSchemaMeta5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO schema_meta(key,value)
VALUES('world_history_retention_repair_v2','1')
ON CONFLICT(key) DO UPDATE SET value='1';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='world_history_retention_repair_v3' LIMIT 1;", parameters, transaction);
        }

        internal static void DeleteWorldHistoryRemovedNpcLootIds(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_removed_npc_loot_ids;", parameters, transaction);
        }

        internal static void InsertOrIgnoreWorldHistoryRemovedNpcLootIds(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO world_history_removed_npc_loot_ids(event_id)
SELECT event_id FROM world_history_events
WHERE event_type IN ('items_looted','loot_distributed') AND player_involved=0
AND NOT EXISTS (
    SELECT 1 FROM world_history_entities entity
    WHERE entity.event_id=world_history_events.event_id
      AND (LOWER(entity.entity_id) IN ('main_hero','main_party')
           OR LOWER(entity.party_id)='main_party'));", parameters, transaction);
        }

        internal static void DeleteWorldHistoryFts4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_fts WHERE event_id IN (SELECT event_id FROM world_history_removed_npc_loot_ids);", parameters, transaction);
        }

        internal static void DeleteWorldHistoryKnowledgeRules3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_knowledge_rules WHERE event_id IN (SELECT event_id FROM world_history_removed_npc_loot_ids);", parameters, transaction);
        }

        internal static void DeleteWorldHistoryEntities3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_entities WHERE event_id IN (SELECT event_id FROM world_history_removed_npc_loot_ids);", parameters, transaction);
        }

        internal static void DeleteEmbeddingJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM embedding_jobs WHERE source_type='world_history_event' AND source_id IN (SELECT event_id FROM world_history_removed_npc_loot_ids);", parameters, transaction);
        }

        internal static void DeleteEmbeddingDocuments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM embedding_documents WHERE source_type='world_history_event' AND source_id IN (SELECT event_id FROM world_history_removed_npc_loot_ids);", parameters, transaction);
        }

        internal static void DeleteWorldHistoryEvents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_events WHERE event_id IN (SELECT event_id FROM world_history_removed_npc_loot_ids);", parameters, transaction);
        }

        internal static void DeleteWorldHistoryDailyAggregates(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_daily_aggregates WHERE event_type IN ('items_looted','loot_distributed') AND player_involved=0;", parameters, transaction);
        }

        internal static void DeleteWorldHistorySeasonalAggregates(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_seasonal_aggregates WHERE event_type IN ('items_looted','loot_distributed') AND player_involved=0;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO schema_meta(key,value)
VALUES('world_history_retention_repair_v3','1')
ON CONFLICT(key) DO UPDATE SET value='1';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents11(ReignDbConnection connection, object shortRoutineWorldHistorySqlTypes, object shortRoutineWorldHistorySqlTypes2, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT event_id,campaign_id,timeline_id,world_day,event_type,category,retention_class,player_involved,payload_json
FROM world_history_events
WHERE timeline_id=$timeline AND (
    (retention_class='routine' AND (
        (event_type IN (" + shortRoutineWorldHistorySqlTypes + @") AND world_day<$shortRoutineCutoff)
     OR (event_type NOT IN (" + shortRoutineWorldHistorySqlTypes2 + @") AND world_day<$routineCutoff)))
 OR (retention_class='significant' AND world_day<$significantCutoff)
)
ORDER BY world_day,event_id LIMIT " + part + ";", parameters, transaction);
        }

        internal static void DeleteWorldHistoryExpiredIds(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_expired_ids;", parameters, transaction);
        }

        internal static void InsertOrIgnoreWorldHistoryExpiredIds(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR IGNORE INTO world_history_expired_ids(event_id) VALUES"
                        + part + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEntities4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT entity.event_id,entity.ordinal,entity.kingdom_id,entity.quantity
FROM world_history_entities entity
JOIN world_history_expired_ids expired ON expired.event_id=entity.event_id
ORDER BY entity.event_id,entity.ordinal;", parameters, transaction);
        }

        internal static void UpsertWorldHistoryDailyAggregates(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_daily_aggregates(
campaign_id,timeline_id,day_key,event_type,category,retention_class,kingdom_id,player_involved,
event_count,quantity_total,first_world_day,last_world_day,updated_ts)
VALUES($campaign,$timeline,$dayKey,$type,$category,$retention,$kingdom,$player,$count,$quantity,$first,$last,$ts)
ON CONFLICT(campaign_id,timeline_id,day_key,event_type,category,retention_class,kingdom_id,player_involved)
DO UPDATE SET event_count=world_history_daily_aggregates.event_count+$count,quantity_total=world_history_daily_aggregates.quantity_total+$quantity,
first_world_day=MIN(world_history_daily_aggregates.first_world_day,$first),last_world_day=MAX(world_history_daily_aggregates.last_world_day,$last),updated_ts=$ts;", parameters, transaction);
        }

        internal static void DeleteWorldHistoryFts5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_fts WHERE event_id IN (SELECT event_id FROM world_history_expired_ids);", parameters, transaction);
        }

        internal static void DeleteWorldHistoryKnowledgeRules4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_knowledge_rules WHERE event_id IN (SELECT event_id FROM world_history_expired_ids);", parameters, transaction);
        }

        internal static void DeleteWorldHistoryEntities4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_entities WHERE event_id IN (SELECT event_id FROM world_history_expired_ids);", parameters, transaction);
        }

        internal static void DeleteEmbeddingJobs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM embedding_jobs WHERE source_type='world_history_event' AND source_id IN (SELECT event_id FROM world_history_expired_ids);", parameters, transaction);
        }

        internal static void DeleteEmbeddingDocuments2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM embedding_documents WHERE source_type='world_history_event' AND source_id IN (SELECT event_id FROM world_history_expired_ids);", parameters, transaction);
        }

        internal static void DeleteWorldHistoryEvents4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_events WHERE event_id IN (SELECT event_id FROM world_history_expired_ids);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistoryEvents(ReignDbConnection connection, object shortRoutineWorldHistorySqlTypes, object shortRoutineWorldHistorySqlTypes2, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM world_history_events WHERE timeline_id=$timeline AND (
    (retention_class='routine' AND (
        (event_type IN (" + shortRoutineWorldHistorySqlTypes + @") AND world_day<$shortRoutineCutoff)
     OR (event_type NOT IN (" + shortRoutineWorldHistorySqlTypes2 + @") AND world_day<$routineCutoff)))
 OR (retention_class='significant' AND world_day<$significantCutoff));", parameters, transaction);
        }

        internal static void Analyze(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"ANALYZE world_history_events;
ANALYZE world_history_entities;
ANALYZE world_history_knowledge_rules;
ANALYZE world_history_daily_aggregates;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryDailyAggregates(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM world_history_daily_aggregates
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key<$cutoff;", parameters, transaction);
        }

        internal static void UpsertWorldHistorySeasonalAggregates(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_seasonal_aggregates(
campaign_id,timeline_id,season_start_day,event_type,category,retention_class,kingdom_id,player_involved,
event_count,quantity_total,first_world_day,last_world_day,updated_ts)
VALUES($campaign,$timeline,$season,$type,$category,$retention,$kingdom,$player,$count,$quantity,$first,$last,$ts)
ON CONFLICT(campaign_id,timeline_id,season_start_day,event_type,category,retention_class,kingdom_id,player_involved)
DO UPDATE SET event_count=world_history_seasonal_aggregates.event_count+$count,quantity_total=world_history_seasonal_aggregates.quantity_total+$quantity,
first_world_day=MIN(world_history_seasonal_aggregates.first_world_day,$first),last_world_day=MAX(world_history_seasonal_aggregates.last_world_day,$last),updated_ts=$ts;", parameters, transaction);
        }

        internal static void DeleteWorldHistoryDailyAggregates2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM world_history_daily_aggregates
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key<$cutoff;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSqliteMaster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT name FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistoryDailyAggregates(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM world_history_daily_aggregates
WHERE timeline_id=$timeline AND last_world_day>=$from AND first_world_day<=$to;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistorySeasonalAggregates(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM world_history_seasonal_aggregates
WHERE timeline_id=$timeline AND last_world_day>=$from AND first_world_day<=$to;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT retention_class AS metric,COUNT(*) AS count
FROM world_history_events WHERE timeline_id=$timeline GROUP BY retention_class ORDER BY retention_class;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT MIN(world_day) AS day FROM world_history_events WHERE timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistoryDailyAggregates2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM world_history_daily_aggregates WHERE timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistorySeasonalAggregates2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM world_history_seasonal_aggregates WHERE timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPgClass(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT c.relname AS table_name,
pg_total_relation_size(c.oid) AS total_bytes,
pg_relation_size(c.oid) AS table_bytes,
pg_indexes_size(c.oid) AS index_bytes,
COALESCE(s.n_live_tup,0) AS live_rows,
COALESCE(s.n_dead_tup,0) AS dead_rows,
COALESCE(s.last_analyze,s.last_autoanalyze)::text AS last_analyze
FROM pg_class c
JOIN pg_namespace n ON n.oid=c.relnamespace
LEFT JOIN pg_stat_user_tables s ON s.relid=c.oid
WHERE n.nspname=current_schema() AND c.relkind IN ('r','p')
ORDER BY pg_total_relation_size(c.oid) DESC LIMIT 20;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT retention_class FROM world_history_events WHERE event_id='routine_visit';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents15(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT retention_class FROM world_history_events WHERE event_id='ordinary_battle';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents16(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT retention_class FROM world_history_events WHERE event_id='noble_wounded';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents17(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT retention_class FROM world_history_events WHERE event_id='failed_marriage';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents18(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT retention_class FROM world_history_events WHERE event_id='failed_diplomacy';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents19(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT retention_class FROM world_history_events WHERE event_id='consequential_battle';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents20(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT retention_class FROM world_history_events WHERE event_id='correlated_loot';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents21(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT retention_class FROM world_history_events WHERE event_id='player_party_loot';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistoryEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM world_history_events WHERE event_id='ephemeral_projection';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistoryEntities(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM world_history_entities WHERE event_id='routine_visit';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistoryKnowledgeRules(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM world_history_knowledge_rules WHERE event_id='routine_visit';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistoryFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM world_history_fts WHERE event_id='routine_visit';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistoryEvents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM world_history_events WHERE event_id='day10_sale';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistoryEvents4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM world_history_events WHERE event_id='day10_visit';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistoryEvents5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM world_history_events WHERE event_id='routine_visit';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryDailyAggregates2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COALESCE(SUM(event_count),0) AS count FROM world_history_daily_aggregates
WHERE timeline_id=$timeline AND event_type='settlement_entered' AND day_key=1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistoryEvents6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM world_history_events WHERE event_id='ordinary_battle';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistoryEvents7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM world_history_events
WHERE event_id IN ('noble_wounded','failed_marriage','failed_diplomacy','consequential_battle','correlated_wound');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistoryDailyAggregates3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM world_history_daily_aggregates WHERE day_key=1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistorySeasonalAggregates(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COALESCE(SUM(event_count),0) AS count FROM world_history_seasonal_aggregates;", parameters, transaction);
        }
    }
}
