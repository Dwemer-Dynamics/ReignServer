using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationStore
    {
        internal static List<Dictionary<string, object>> SelectSchemaMeta(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT value FROM schema_meta
WHERE key='world_history_social_subject_index_v1' LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE world_history_events
SET subject_id=COALESCE(CAST(payload_json AS jsonb)->>'subjectId',
                        CAST(payload_json AS jsonb)->>'heroId','')
WHERE event_type='social_outcome' AND subject_id='';", parameters, transaction);
        }

        internal static void UpsertSchemaMeta(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO schema_meta(key,value)
VALUES('world_history_social_subject_index_v1','1');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='world_history_version' LIMIT 1;", parameters, transaction);
        }

        internal static void Savepoint(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "SAVEPOINT world_history_storage_upgrade;", parameters, transaction);
        }

        internal static void DeleteWorldHistoryNoiseEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_noise_events;", parameters, transaction);
        }

        internal static void InsertOrIgnoreWorldHistoryNoiseEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO world_history_noise_events(event_id)
SELECT e.event_id FROM world_history_events e
WHERE e.event_type IN (
    'ship_owner_changed','ship_created','ship_repaired','ship_destroyed',
    'settlement_entered','mobile_party_created'
)
AND NOT EXISTS (
    SELECT 1 FROM world_history_entities n
    WHERE n.event_id=e.event_id AND n.entity_type='hero' AND n.entity_id<>''
)
AND NOT EXISTS (
    SELECT 1 FROM world_history_entities n
    WHERE n.event_id=e.event_id AND (n.entity_id='main_party' OR n.party_id='main_party')
);", parameters, transaction);
        }

        internal static void DeleteWorldHistoryFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_fts WHERE event_id IN (SELECT event_id FROM world_history_noise_events);", parameters, transaction);
        }

        internal static void DeleteWorldHistoryKnowledgeRules(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_knowledge_rules WHERE event_id IN (SELECT event_id FROM world_history_noise_events);", parameters, transaction);
        }

        internal static void DeleteWorldHistoryEntities(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_entities WHERE event_id IN (SELECT event_id FROM world_history_noise_events);", parameters, transaction);
        }

        internal static void DeleteWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_events WHERE event_id IN (SELECT event_id FROM world_history_noise_events);", parameters, transaction);
        }

        internal static void DeleteWorldHistoryFts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_fts WHERE event_id IN (SELECT event_id FROM world_history_events WHERE event_type='unattributed_state_change');", parameters, transaction);
        }

        internal static void DeleteWorldHistoryKnowledgeRules2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_knowledge_rules WHERE event_id IN (SELECT event_id FROM world_history_events WHERE event_type='unattributed_state_change');", parameters, transaction);
        }

        internal static void DeleteWorldHistoryEntities2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_entities WHERE event_id IN (SELECT event_id FROM world_history_events WHERE event_type='unattributed_state_change');", parameters, transaction);
        }

        internal static void DeleteWorldHistoryEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_events WHERE event_type='unattributed_state_change';", parameters, transaction);
        }

        internal static void UpsertSchemaMeta2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('world_history_version','3');", parameters, transaction);
        }

        internal static void UpsertSchemaMeta3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('campaign_compaction_required','1');", parameters, transaction);
        }

        internal static void Release(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "RELEASE world_history_storage_upgrade;", parameters, transaction);
        }

        internal static void Rollback2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "ROLLBACK TO world_history_storage_upgrade;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT campaign_id FROM world_history_events WHERE campaign_id<>'' LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO schema_meta(key,value)
VALUES('postgresql_world_history_schema_revision',$revision)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents3(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT event_id FROM world_history_events WHERE event_id IN ("
                    + part + ");", parameters, transaction);
        }

        internal static void UpsertWorldHistoryTimelines(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_timelines(timeline_id,campaign_id,parent_timeline_id,fork_event_id,history_complete_from_day,head_sequence,head_event_id,is_active,created_utc,updated_utc)
VALUES($timeline,$campaign,$parent,$fork,$complete,0,'',1,$now,$now)
ON CONFLICT(timeline_id) DO UPDATE SET
campaign_id=$campaign,
history_complete_from_day=CASE
    WHEN world_history_timelines.history_complete_from_day<=0 THEN $complete
    ELSE MIN(world_history_timelines.history_complete_from_day,$complete)
END,
is_active=1,updated_utc=$now;", parameters, transaction);
        }

        internal static void UpdateWorldHistoryTimelines(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE world_history_timelines SET is_active=CASE WHEN timeline_id=$timeline THEN 1 ELSE 0 END WHERE campaign_id=$campaign;", parameters, transaction);
        }

        internal static void InsertWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_events(event_id,campaign_id,timeline_id,sequence,world_day,event_type,category,phase,correlation_id,location_id,location_name,dissemination_class,retention_class,player_involved,summary,semantic_text,source,is_complete,checksum,payload_json,created_utc,subject_id)
VALUES($id,$campaign,$timeline,$sequence,$day,$type,$category,$phase,$correlation,$location,$locationName,$dissemination,$retention,$player,$summary,$semantic,$source,$complete,$checksum,$payload,$created,$subject);", parameters, transaction);
        }

        internal static void UpdateWorldHistoryEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE world_history_events
SET retention_class='consequential'
WHERE timeline_id=$timeline AND correlation_id<>'' AND correlation_id IN (
    SELECT correlation_id FROM world_history_events
    WHERE timeline_id=$timeline AND correlation_id<>'' AND retention_class='consequential'
)
AND event_type IN ('battle_completed','raid_completed','siege_completed');", parameters, transaction);
        }

        internal static void UpdateWorldHistoryTimelines2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE world_history_timelines SET head_sequence=MAX(head_sequence,$sequence),head_event_id=$event,updated_utc=$now WHERE timeline_id=$timeline;", parameters, transaction);
        }

        internal static void UpsertWorldHistoryIngestState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_ingest_state(client_id,timeline_id,last_sequence,last_event_id,updated_utc)
VALUES($client,$timeline,$sequence,$event,$now) ON CONFLICT(client_id,timeline_id) DO UPDATE SET last_sequence=MAX(world_history_ingest_state.last_sequence,$sequence),last_event_id=$event,updated_utc=$now;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_events WHERE event_id=$id AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_events WHERE event_id=$id AND timeline_id=$timeline AND (event_type IN ('battle_completed','tournament_finished') OR (event_type='social_outcome' AND source='reign_social' AND payload_json LIKE '%\"duelMode\"%'));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryTimelines(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_timelines WHERE timeline_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertWorldHistoryTimelines2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_timelines(timeline_id,campaign_id,parent_timeline_id,fork_event_id,history_complete_from_day,head_sequence,head_event_id,is_active,created_utc,updated_utc)
VALUES($id,$campaign,$parent,$fork,$complete,$sequence,$head,1,$now,$now)
ON CONFLICT(timeline_id) DO UPDATE SET is_active=1,updated_utc=$now;", parameters, transaction);
        }

        internal static void UpdateWorldHistoryTimelines3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE world_history_timelines SET is_active=CASE WHEN timeline_id=$id THEN 1 ELSE 0 END WHERE campaign_id=$campaign;", parameters, transaction);
        }

        internal static void InsertWorldHistoryEntities(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_entities(event_id,ordinal,entity_id,entity_type,name_snapshot,role,side,party_id,clan_id,kingdom_id,quantity,before_json,after_json,payload_json)
VALUES($event,$ordinal,$id,$type,$name,$role,$side,$party,$clan,$kingdom,$quantity,$before,$after,$payload);", parameters, transaction);
        }

        internal static void UpsertWorldHistoryKnowledgeRules(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO world_history_knowledge_rules(rule_id,event_id,audience_type,audience_id,available_day,acquisition_mode,confidence)
VALUES($id,$event,$type,$audience,$day,$mode,$confidence);", parameters, transaction);
        }

        internal static void DeleteWorldHistoryFts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_fts WHERE event_id=$id;", parameters, transaction);
        }

        internal static void InsertWorldHistoryFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO world_history_fts(event_id,text,entities,roles,location,event_type) VALUES($id,$text,$entities,$roles,$location,$type);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT DISTINCT e.* FROM world_history_events e
LEFT JOIN world_history_entities n ON n.event_id=e.event_id
WHERE e.timeline_id=$timeline AND ($entity='' OR n.entity_id=$entity) AND ($type='' OR e.event_type=$type)
AND ($from<0 OR e.world_day>=$from) AND e.world_day<=$to ORDER BY e.sequence DESC LIMIT $limit;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_events WHERE event_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryKnowledgeRules(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_knowledge_rules WHERE event_id=$id ORDER BY available_day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_events WHERE timeline_id=$timeline AND correlation_id=$id ORDER BY sequence;", parameters, transaction);
        }

        internal static void InsertWorldHistoryClaimChecks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_claim_checks(check_id,campaign_id,timeline_id,world_day,claimant_id,speaker_id,raw_claim,purpose,claim_family,objective_verdict,speaker_verdict,explanation,constraints_json,evidence_event_ids_json,speaker_event_ids_json,minime_json,created_ts)
VALUES($id,$campaign,$timeline,$day,$claimant,$speaker,$claim,$purpose,$family,$objective,$speakerVerdict,$explanation,$constraints,$evidence,$speakerEvidence,$minime,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT e.* FROM world_history_events e INDEXED BY idx_wh_event_evidence
WHERE e.timeline_id=$timeline AND ($location='' OR e.location_id=$location) AND ($from<0 OR e.world_day>=$from) AND e.world_day<=$to
ORDER BY e.sequence DESC LIMIT $limit;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEntities(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT e.* FROM world_history_entities n INDEXED BY idx_wh_entity_event
JOIN world_history_events e ON e.event_id=n.event_id WHERE n.entity_id=$claimant AND e.timeline_id=$timeline
AND ($location='' OR e.location_id=$location) AND ($from<0 OR e.world_day>=$from) AND e.world_day<=$to
ORDER BY e.sequence DESC LIMIT $limit;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT e.* FROM world_history_fts JOIN world_history_events e ON e.event_id=world_history_fts.event_id
WHERE world_history_fts MATCH $query AND e.timeline_id=$timeline AND ($from<0 OR e.world_day>=$from) AND e.world_day<=$to LIMIT 128;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_events WHERE timeline_id=$timeline AND correlation_id=$correlation ORDER BY sequence;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEntities2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_entities WHERE event_id=$id ORDER BY ordinal;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryTimelines2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT timeline_id FROM world_history_timelines WHERE is_active=1 ORDER BY updated_utc DESC LIMIT 1;", parameters, transaction);
        }

        internal static void InsertWorldHistoryTimelines(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_timelines(timeline_id,campaign_id,history_complete_from_day,head_sequence,head_event_id,is_active,created_utc,updated_utc)
VALUES($timeline,$campaign,1,$head,'stress_last',1,$now,$now);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEntities3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT e.event_id,e.sequence,e.world_day FROM world_history_entities n INDEXED BY idx_wh_entity_event
JOIN world_history_events e ON e.event_id=n.event_id WHERE n.entity_id='main_hero' AND e.timeline_id=$timeline
AND e.event_type='battle_completed' AND e.world_day BETWEEN $from AND $to ORDER BY e.world_day DESC LIMIT 256;", parameters, transaction);
        }

        internal const string InsertWorldHistoryEventsSql = @"INSERT INTO world_history_events(event_id,campaign_id,timeline_id,sequence,world_day,event_type,category,phase,correlation_id,location_id,location_name,dissemination_class,summary,semantic_text,source,is_complete,checksum,payload_json,created_utc)
VALUES($id,$campaign,$timeline,$sequence,$day,$type,'warfare','completed',$correlation,$location,$location,'ordinary',$summary,$summary,'stress',1,'','{}',$created);";

        internal const string InsertWorldHistoryEntitiesSql = @"INSERT INTO world_history_entities(event_id,ordinal,entity_id,entity_type,name_snapshot,role,side,party_id,clan_id,kingdom_id,quantity,before_json,after_json,payload_json)
VALUES($event,0,'main_hero','hero','Player','participant winner','attacker','main_party','player_clan','kingdom_a',1,'{}','{}','{}');";
    }
}
