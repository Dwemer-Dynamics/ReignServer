using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int PostgreSqlWorldHistorySchemaRevision = 3;
        private static readonly object WorldHistoryCacheLock = new object();
        private static readonly Dictionary<string, Dictionary<string, object>> WorldHistoryClaimCache =
            new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] WorldHistoryPurposes =
        {
            "self_claim", "credit_or_boast", "alibi", "accusation", "casual_recall", "identity_check"
        };

        private static readonly string[] WorldHistoryFamilies =
        {
            "presence", "leadership", "victory", "defeat", "rescue", "release", "capture", "killing", "wounding",
            "raid", "siege", "conquest", "defense", "ownership", "marriage", "diplomacy", "payment", "trade",
            "tournament", "travel", "meeting", "generic_participation"
        };

        private static void EnsureWorldHistorySchema(ReignDbConnection connection)
        {
            const string marker =
                "postgresql_world_history_schema_revision";
            if (IsPostgreSqlComponentSchemaReady(connection, marker,
                PostgreSqlWorldHistorySchemaRevision))
                return;
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_history_timelines (
timeline_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,parent_timeline_id TEXT NOT NULL DEFAULT '',fork_event_id TEXT NOT NULL DEFAULT '',
history_complete_from_day REAL NOT NULL DEFAULT 0,head_sequence INTEGER NOT NULL DEFAULT 0,head_event_id TEXT NOT NULL DEFAULT '',
is_active INTEGER NOT NULL DEFAULT 1,created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL); ");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_history_events (
event_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,sequence INTEGER NOT NULL,world_day REAL NOT NULL,
event_type TEXT NOT NULL,category TEXT NOT NULL DEFAULT '',phase TEXT NOT NULL DEFAULT 'completed',correlation_id TEXT NOT NULL DEFAULT '',
location_id TEXT NOT NULL DEFAULT '',location_name TEXT NOT NULL DEFAULT '',dissemination_class TEXT NOT NULL DEFAULT 'ordinary',
summary TEXT NOT NULL DEFAULT '',semantic_text TEXT NOT NULL DEFAULT '',source TEXT NOT NULL DEFAULT 'native',is_complete INTEGER NOT NULL DEFAULT 1,
checksum TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',created_utc TEXT NOT NULL); ");
            EnsureDatabaseColumn(connection, "world_history_events", "subject_id",
                "TEXT NOT NULL DEFAULT ''");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_history_entities (
event_id TEXT NOT NULL,ordinal INTEGER NOT NULL,entity_id TEXT NOT NULL DEFAULT '',entity_type TEXT NOT NULL DEFAULT '',
name_snapshot TEXT NOT NULL DEFAULT '',role TEXT NOT NULL DEFAULT '',side TEXT NOT NULL DEFAULT '',party_id TEXT NOT NULL DEFAULT '',
clan_id TEXT NOT NULL DEFAULT '',kingdom_id TEXT NOT NULL DEFAULT '',quantity REAL NOT NULL DEFAULT 0,before_json TEXT NOT NULL DEFAULT '{}',
after_json TEXT NOT NULL DEFAULT '{}',payload_json TEXT NOT NULL DEFAULT '{}',PRIMARY KEY(event_id,ordinal)); ");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_history_knowledge_rules (
rule_id TEXT PRIMARY KEY,event_id TEXT NOT NULL,audience_type TEXT NOT NULL,audience_id TEXT NOT NULL DEFAULT '',
available_day REAL NOT NULL,acquisition_mode TEXT NOT NULL DEFAULT '',confidence REAL NOT NULL DEFAULT 1); ");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_history_claim_checks (
check_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,world_day REAL NOT NULL,claimant_id TEXT NOT NULL DEFAULT '',
speaker_id TEXT NOT NULL DEFAULT '',raw_claim TEXT NOT NULL,purpose TEXT NOT NULL,claim_family TEXT NOT NULL,
objective_verdict TEXT NOT NULL,speaker_verdict TEXT NOT NULL,explanation TEXT NOT NULL DEFAULT '',constraints_json TEXT NOT NULL DEFAULT '{}',
evidence_event_ids_json TEXT NOT NULL DEFAULT '[]',speaker_event_ids_json TEXT NOT NULL DEFAULT '[]',minime_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL); ");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_history_ingest_state (
client_id TEXT NOT NULL,timeline_id TEXT NOT NULL,last_sequence INTEGER NOT NULL DEFAULT 0,last_event_id TEXT NOT NULL DEFAULT '',
updated_utc TEXT NOT NULL,PRIMARY KEY(client_id,timeline_id)); ");
            bool postgres = ReignPostgreSqlDialect.IsPostgreSql(connection);
            if (!postgres)
                ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_event_timeline_type_day ON world_history_events(timeline_id,event_type,world_day DESC);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_event_location_day ON world_history_events(timeline_id,location_id,world_day DESC);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_event_correlation ON world_history_events(timeline_id,correlation_id,sequence);");
            ExecuteSql(connection, "CREATE UNIQUE INDEX IF NOT EXISTS idx_wh_event_sequence ON world_history_events(timeline_id,sequence,event_id);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_entity_lookup ON world_history_entities(entity_id,role,event_id);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_entity_event ON world_history_entities(entity_id,event_id);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_event_evidence ON world_history_events(timeline_id,event_type,world_day DESC,event_id);");
            if (postgres)
            {
                // idx_wh_event_evidence has the complete prefix used by the
                // former timeline/type/day index. The social-outcome worker
                // needs subject ordering, but only for social_outcome rows.
                // These changes preserve every event and every evidence path;
                // they remove only redundant index maintenance and unrelated
                // rows from the specialized worker index.
                ExecuteSql(connection, @"DROP INDEX IF EXISTS idx_wh_event_timeline_type_day;
DROP INDEX IF EXISTS idx_wh_social_subject_sequence;");
                ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_wh_social_subject_sequence_v2
ON world_history_events(timeline_id,subject_id,sequence,event_id)
WHERE event_type='social_outcome';");
            }
            else
                ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_social_subject_sequence ON world_history_events(timeline_id,event_type,subject_id,sequence,event_id);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_entity_kingdom ON world_history_entities(kingdom_id,event_id);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_rule_event ON world_history_knowledge_rules(event_id,available_day);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_rule_audience ON world_history_knowledge_rules(audience_type,audience_id,available_day);");
            ExecuteSql(connection, "CREATE VIRTUAL TABLE IF NOT EXISTS world_history_fts USING fts5(event_id UNINDEXED,text,entities,roles,location,event_type);");
            EnsureWorldHistoryRetentionSchema(connection);
            if (ReignPostgreSqlDialect.IsPostgreSql(connection)
                && ReadString(QuerySql(connection, @"SELECT value FROM schema_meta
WHERE key='world_history_social_subject_index_v1' LIMIT 1;")
                    .FirstOrDefault(), "value", "") != "1")
            {
                ExecuteSql(connection, @"UPDATE world_history_events
SET subject_id=COALESCE(CAST(payload_json AS jsonb)->>'subjectId',
                        CAST(payload_json AS jsonb)->>'heroId','')
WHERE event_type='social_outcome' AND subject_id='';");
                ExecuteSql(connection, @"INSERT OR REPLACE INTO schema_meta(key,value)
VALUES('world_history_social_subject_index_v1','1');");
            }
            int historyVersion = ReadInt(QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='world_history_version' LIMIT 1;").FirstOrDefault(), "value", 0);
            if (historyVersion < 3)
            {
                ExecuteSql(connection, "SAVEPOINT world_history_storage_upgrade;");
                try
                {
                    ExecuteSql(connection, @"CREATE TEMP TABLE IF NOT EXISTS world_history_noise_events(event_id TEXT PRIMARY KEY);");
                    ExecuteSql(connection, "DELETE FROM world_history_noise_events;");
                    ExecuteSql(connection, @"INSERT OR IGNORE INTO world_history_noise_events(event_id)
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
);");
                    ExecuteSql(connection, "DELETE FROM world_history_fts WHERE event_id IN (SELECT event_id FROM world_history_noise_events);");
                    ExecuteSql(connection, "DELETE FROM world_history_knowledge_rules WHERE event_id IN (SELECT event_id FROM world_history_noise_events);");
                    ExecuteSql(connection, "DELETE FROM world_history_entities WHERE event_id IN (SELECT event_id FROM world_history_noise_events);");
                    ExecuteSql(connection, "DELETE FROM world_history_events WHERE event_id IN (SELECT event_id FROM world_history_noise_events);");
                    ExecuteSql(connection, "DELETE FROM world_history_fts WHERE event_id IN (SELECT event_id FROM world_history_events WHERE event_type='unattributed_state_change');");
                    ExecuteSql(connection, "DELETE FROM world_history_knowledge_rules WHERE event_id IN (SELECT event_id FROM world_history_events WHERE event_type='unattributed_state_change');");
                    ExecuteSql(connection, "DELETE FROM world_history_entities WHERE event_id IN (SELECT event_id FROM world_history_events WHERE event_type='unattributed_state_change');");
                    ExecuteSql(connection, "DELETE FROM world_history_events WHERE event_type='unattributed_state_change';");
                    ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('world_history_version','3');");
                    ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('campaign_compaction_required','1');");
                    ExecuteSql(connection, "RELEASE world_history_storage_upgrade;");
                }
                catch
                {
                    try { ExecuteSql(connection, "ROLLBACK TO world_history_storage_upgrade;"); } catch { }
                    try { ExecuteSql(connection, "RELEASE world_history_storage_upgrade;"); } catch { }
                    throw;
                }
            }
            else ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('world_history_version','3');");
            EnsureImmediateTournamentKnowledge(connection);
            string storedCampaignId = ReadString(QuerySql(connection,
                "SELECT campaign_id FROM world_history_events WHERE campaign_id<>'' LIMIT 1;").FirstOrDefault(), "campaign_id", "");
            if (!string.IsNullOrWhiteSpace(storedCampaignId))
                TryDeleteFile(CampaignFile(storedCampaignId, "world", "history.jsonl"));
            if (postgres)
            {
                ExecuteSql(connection, @"INSERT INTO schema_meta(key,value)
VALUES('postgresql_world_history_schema_revision',$revision)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                    new Dictionary<string, object>
                    {
                        ["revision"] = PostgreSqlWorldHistorySchemaRevision
                            .ToString(CultureInfo.InvariantCulture)
                    });
                MarkPostgreSqlComponentSchemaReady(connection, marker,
                    PostgreSqlWorldHistorySchemaRevision);
            }
        }

        private const int WorldHistoryDuplicateLookupChunkSize = 400;

        private static HashSet<string> LoadExistingWorldHistoryEventIds(
            ReignDbConnection connection,
            IEnumerable<Dictionary<string, object>> events,
            ReignDbTransaction transaction)
        {
            List<string> eventIds = (events
                    ?? Enumerable.Empty<Dictionary<string, object>>())
                .Select(item => ReadFirstString(item, "eventId", "event_id", "id"))
                .Where(eventId => !string.IsNullOrWhiteSpace(eventId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            HashSet<string> existing = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            for (int offset = 0; offset < eventIds.Count;
                offset += WorldHistoryDuplicateLookupChunkSize)
            {
                List<string> chunk = eventIds.Skip(offset)
                    .Take(WorldHistoryDuplicateLookupChunkSize).ToList();
                Dictionary<string, object> parameters =
                    new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                List<string> placeholders = new List<string>();
                for (int index = 0; index < chunk.Count; index++)
                {
                    string name = "event" + index.ToString(
                        CultureInfo.InvariantCulture);
                    parameters[name] = chunk[index];
                    placeholders.Add("$" + name);
                }

                foreach (Dictionary<string, object> row in QuerySql(connection,
                    "SELECT event_id FROM world_history_events WHERE event_id IN ("
                    + string.Join(",", placeholders) + ");",
                    parameters, transaction))
                {
                    string eventId = ReadString(row, "event_id", "");
                    if (!string.IsNullOrWhiteSpace(eventId)) existing.Add(eventId);
                }
            }

            return existing;
        }

        private static Dictionary<string, object> WorldHistoryIngestBatchApi(Dictionary<string, object> payload)
        {
            Stopwatch ingestTimer = Stopwatch.StartNew();
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = FirstNonEmpty(ReadFirstString(payload, "timelineId", "timeline_id"), "main");
            string clientId = FirstNonEmpty(ReadFirstString(payload, "clientId", "client_id"), "bannerlord");
            double completeFrom = ReadDouble(payload, "historyCompleteFromWorldDay", ReadDouble(payload, "history_complete_from_day", 0d));
            List<Dictionary<string, object>> events = ReadDictionaryList(payload, "events");
            int accepted = 0;
            int ephemeral = 0;
            int duplicates = 0;
            long headSequence = 0;
            string headEventId = string.Empty;
            List<string> errors = new List<string>();
            Dictionary<string, object> compaction =
                new Dictionary<string, object> { ["compacted"] = 0, ["remainingBacklog"] = 0 };
            int directSocialOutcomesAcknowledged = 0;
            double latestIngestedDay = events.Select(item =>
                ReadDouble(item, "worldDay", ReadDouble(item, "world_day", 0d)))
                .DefaultIfEmpty(0d).Max();

            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                // Schema helpers take the process-wide PostgreSQL schema-mutation
                // advisory lock. Complete them before the ingest transaction so
                // retention compaction cannot retain that lock for its entire batch.
                EnsureWorldHistorySchema(connection);
                using (ReignDbTransaction transaction = connection.BeginTransaction())
                {
                    HashSet<string> seenEventIds =
                        LoadExistingWorldHistoryEventIds(connection, events,
                            transaction);
                    string now = DateTime.UtcNow.ToString("o");
                    ExecuteSql(connection, @"INSERT INTO world_history_timelines(timeline_id,campaign_id,parent_timeline_id,fork_event_id,history_complete_from_day,head_sequence,head_event_id,is_active,created_utc,updated_utc)
VALUES($timeline,$campaign,$parent,$fork,$complete,0,'',1,$now,$now)
ON CONFLICT(timeline_id) DO UPDATE SET
campaign_id=$campaign,
history_complete_from_day=CASE
    WHEN world_history_timelines.history_complete_from_day<=0 THEN $complete
    ELSE MIN(world_history_timelines.history_complete_from_day,$complete)
END,
is_active=1,updated_utc=$now;",
                    new Dictionary<string, object>
                    {
                        ["timeline"] = timelineId, ["campaign"] = campaignId,
                        ["parent"] = ReadFirstString(payload, "parentTimelineId", "parent_timeline_id"),
                        ["fork"] = ReadFirstString(payload, "forkEventId", "fork_event_id"), ["complete"] = completeFrom, ["now"] = now
                    });
                ExecuteSql(connection, "UPDATE world_history_timelines SET is_active=CASE WHEN timeline_id=$timeline THEN 1 ELSE 0 END WHERE campaign_id=$campaign;",
                    new Dictionary<string, object> { ["timeline"] = timelineId, ["campaign"] = campaignId });

                List<PreparedWorldHistoryEvent> preparedEvents =
                    new List<PreparedWorldHistoryEvent>();
                foreach (Dictionary<string, object> item in events.OrderBy(x => ReadLong(x, "sequence", 0)))
                {
                    string eventId = ReadFirstString(item, "eventId", "event_id", "id");
                    if (string.IsNullOrWhiteSpace(eventId))
                    {
                        errors.Add("An event was rejected because eventId was empty.");
                        continue;
                    }
                    if (!seenEventIds.Add(eventId))
                    {
                        duplicates++;
                        continue;
                    }

                    PreparedWorldHistoryEvent prepared =
                        PrepareWorldHistoryEvent(item, now);
                    if (prepared.Sequence >= headSequence)
                    {
                        headSequence = prepared.Sequence;
                        headEventId = eventId;
                    }
                    if (prepared.Retention == "ephemeral")
                    {
                        ephemeral++;
                        continue;
                    }
                    preparedEvents.Add(prepared);
                }

                if (ReignPostgreSqlDialect.IsPostgreSql(connection))
                {
                    accepted = InsertWorldHistoryPostgreSqlBulk(connection,
                        transaction, campaignId, timelineId, preparedEvents);
                    duplicates += preparedEvents.Count - accepted;
                }
                else
                {
                    foreach (PreparedWorldHistoryEvent prepared in preparedEvents)
                    {
                        ExecuteSql(connection, @"INSERT INTO world_history_events(event_id,campaign_id,timeline_id,sequence,world_day,event_type,category,phase,correlation_id,location_id,location_name,dissemination_class,retention_class,player_involved,summary,semantic_text,source,is_complete,checksum,payload_json,created_utc,subject_id)
VALUES($id,$campaign,$timeline,$sequence,$day,$type,$category,$phase,$correlation,$location,$locationName,$dissemination,$retention,$player,$summary,$semantic,$source,$complete,$checksum,$payload,$created,$subject);",
                            prepared.EventParameters(campaignId, timelineId));
                        StoreWorldHistoryEntities(connection, prepared.EventId,
                            prepared.Entities);
                        StoreWorldHistoryKnowledgeRules(connection,
                            prepared.EventId, prepared.EventType,
                            prepared.WorldDay, prepared.Dissemination,
                            prepared.Entities, prepared.KnowledgeRules);
                        InsertWorldHistoryFts(connection, prepared.EventId,
                            prepared.SemanticText + " " + prepared.Summary,
                            prepared.Entities, prepared.LocationName,
                            prepared.EventType);
                        accepted++;
                    }
                }

                ExecuteSql(connection, @"UPDATE world_history_events
SET retention_class='consequential'
WHERE timeline_id=$timeline AND correlation_id<>'' AND correlation_id IN (
    SELECT correlation_id FROM world_history_events
    WHERE timeline_id=$timeline AND correlation_id<>'' AND retention_class='consequential'
)
AND event_type IN ('battle_completed','raid_completed','siege_completed');",
                    new Dictionary<string, object> { ["timeline"] = timelineId });
                if (headSequence > 0)
                {
                    ExecuteSql(connection, "UPDATE world_history_timelines SET head_sequence=MAX(head_sequence,$sequence),head_event_id=$event,updated_utc=$now WHERE timeline_id=$timeline;",
                        new Dictionary<string, object> { ["sequence"] = headSequence, ["event"] = headEventId, ["now"] = now, ["timeline"] = timelineId });
                    ExecuteSql(connection, @"INSERT INTO world_history_ingest_state(client_id,timeline_id,last_sequence,last_event_id,updated_utc)
VALUES($client,$timeline,$sequence,$event,$now) ON CONFLICT(client_id,timeline_id) DO UPDATE SET last_sequence=MAX(world_history_ingest_state.last_sequence,$sequence),last_event_id=$event,updated_utc=$now;",
                        new Dictionary<string, object> { ["client"] = clientId, ["timeline"] = timelineId, ["sequence"] = headSequence, ["event"] = headEventId, ["now"] = now });
                }
                    transaction.Commit();
                }

                // A direct dynamic reputation is an immediate authoritative
                // consequence (for example, a ruler's public judgment), not a
                // probabilistic rumor. Apply it before acknowledging the World
                // History upload so the client cannot observe a durable judgment
                // whose corresponding reputation is still absent. The receipt
                // path is idempotent, so upload retries remain safe.
                directSocialOutcomesAcknowledged =
                    ProcessDirectDynamicSocialOutcomesBeforeAcknowledgement(
                        connection, campaignId, timelineId, events, errors);

                if (latestIngestedDay > 0d)
                {
                    compaction = ReadBool(payload, "waitForRetention", false)
                        ? CompactWorldHistorySynchronously(campaignId,
                            timelineId, latestIngestedDay)
                        : ScheduleWorldHistoryCompaction(campaignId,
                            timelineId, latestIngestedDay);
                }
            }

            SchedulePendingSocialWorldHistoryOutcomes(campaignId, timelineId);

            lock (WorldHistoryCacheLock) WorldHistoryClaimCache.Clear();
            ingestTimer.Stop();
            double eventsPerSecond = events.Count <= 0
                ? 0d
                : events.Count / Math.Max(0.001d,
                    ingestTimer.Elapsed.TotalSeconds);
            return new Dictionary<string, object>
            {
                ["ok"] = errors.Count == 0, ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["requested"] = events.Count,
                ["accepted"] = accepted, ["ephemeral"] = ephemeral, ["duplicates"] = duplicates,
                ["durationMs"] = ingestTimer.ElapsedMilliseconds,
                ["eventsPerSecond"] = Math.Round(eventsPerSecond, 2),
                ["headSequence"] = headSequence, ["headEventId"] = headEventId,
                ["directSocialOutcomesAcknowledged"] = directSocialOutcomesAcknowledged,
                ["compaction"] = compaction, ["errors"] = errors
            };
        }

        private static int ProcessDirectDynamicSocialOutcomesBeforeAcknowledgement(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            IEnumerable<Dictionary<string, object>> events,
            List<string> errors)
        {
            List<Dictionary<string, object>> directOutcomes = (events
                    ?? Enumerable.Empty<Dictionary<string, object>>())
                .Where(item => string.Equals(ReadFirstString(item,
                        "eventType", "event_type", "type"),
                    "social_outcome", StringComparison.OrdinalIgnoreCase))
                .Where(item =>
                {
                    Dictionary<string, object> outcome = DictionaryOrDefault(
                        item, "payload", item);
                    return !string.IsNullOrWhiteSpace(ReadString(outcome,
                               "dynamicReputationTagId", ""))
                           && !string.IsNullOrWhiteSpace(ReadString(outcome,
                               "directReputationProducer", ""));
                })
                .ToList();
            if (directOutcomes.Count == 0) return 0;

            EnsureSocialReputationSchema(connection);
            int acknowledged = 0;
            foreach (Dictionary<string, object> item in directOutcomes)
            {
                string eventId = ReadFirstString(item, "eventId", "event_id", "id");
                if (string.IsNullOrWhiteSpace(eventId)) continue;
                try
                {
                    ProcessSocialWorldHistoryEvent(campaignId, timelineId,
                        item, true, connection);
                    acknowledged++;
                }
                catch (Exception ex)
                {
                    RecordSocialWorldHistoryFailure(campaignId, timelineId,
                        eventId, ex, connection);
                    errors.Add("Direct social outcome " + eventId
                        + " failed before acknowledgment: "
                        + LimitText(ex.Message, 500));
                }
            }
            return acknowledged;
        }

        private static Dictionary<string, object> WorldHistoryOpenTimelineApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string requested = FirstNonEmpty(ReadFirstString(payload, "timelineId", "timeline_id"), "main");
            long savedSequence = ReadLong(payload, "savedSequence", ReadLong(payload, "sequence", 0));
            string savedHead = ReadFirstString(payload, "savedHeadEventId", "headEventId");
            double completeFrom = ReadDouble(payload, "historyCompleteFromWorldDay", 0d);
            double currentWorldDay = ReadDouble(payload, "currentWorldDay", completeFrom);
            string timelineId = requested;
            bool branched = false;
            bool historyMissing = false;
            Dictionary<string, object> result;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureWorldHistorySchema(connection);
                Dictionary<string, object> existing = QuerySql(connection, "SELECT * FROM world_history_timelines WHERE timeline_id=$id LIMIT 1;", new Dictionary<string, object> { ["id"] = requested }).FirstOrDefault();
                long serverHead = ReadLong(existing, "head_sequence", 0);
                if (existing == null && savedSequence > 0)
                {
                    timelineId = requested + "_recovered_" + Guid.NewGuid().ToString("N").Substring(0, 10);
                    completeFrom = currentWorldDay;
                    historyMissing = true;
                }
                else if (existing != null && savedSequence < serverHead)
                {
                    timelineId = requested + "_branch_" + Guid.NewGuid().ToString("N").Substring(0, 10);
                    branched = true;
                }
                string now = DateTime.UtcNow.ToString("o");
                ExecuteSql(connection, @"INSERT INTO world_history_timelines(timeline_id,campaign_id,parent_timeline_id,fork_event_id,history_complete_from_day,head_sequence,head_event_id,is_active,created_utc,updated_utc)
VALUES($id,$campaign,$parent,$fork,$complete,$sequence,$head,1,$now,$now)
ON CONFLICT(timeline_id) DO UPDATE SET is_active=1,updated_utc=$now;", new Dictionary<string, object>
                {
                    ["id"] = timelineId, ["campaign"] = campaignId, ["parent"] = branched ? requested : ReadString(existing, "parent_timeline_id", ""),
                    ["fork"] = savedHead, ["complete"] = completeFrom, ["sequence"] = branched ? savedSequence : Math.Max(savedSequence, serverHead),
                    ["head"] = savedHead, ["now"] = now
                });
                ExecuteSql(connection, "UPDATE world_history_timelines SET is_active=CASE WHEN timeline_id=$id THEN 1 ELSE 0 END WHERE campaign_id=$campaign;",
                    new Dictionary<string, object> { ["id"] = timelineId, ["campaign"] = campaignId });
                result = new Dictionary<string, object>
                {
                    ["ok"] = true, ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["branched"] = branched,
                    ["historyMissing"] = historyMissing,
                    ["parentTimelineId"] = branched ? requested : ReadString(existing, "parent_timeline_id", ""),
                    ["savedSequence"] = savedSequence, ["previousServerHeadSequence"] = serverHead, ["historyCompleteFromWorldDay"] = completeFrom
                };
            }
            ResumePendingSocialWorldHistoryOutcomes(campaignId, timelineId);
            return result;
        }

        private static void StoreWorldHistoryEntities(ReignDbConnection connection, string eventId, List<Dictionary<string, object>> entities)
        {
            int ordinal = 0;
            foreach (Dictionary<string, object> entity in entities ?? new List<Dictionary<string, object>>())
            {
                ExecuteSql(connection, @"INSERT INTO world_history_entities(event_id,ordinal,entity_id,entity_type,name_snapshot,role,side,party_id,clan_id,kingdom_id,quantity,before_json,after_json,payload_json)
VALUES($event,$ordinal,$id,$type,$name,$role,$side,$party,$clan,$kingdom,$quantity,$before,$after,$payload);",
                    new Dictionary<string, object>
                    {
                        ["event"] = eventId, ["ordinal"] = ordinal++, ["id"] = ReadFirstString(entity, "entityId", "entity_id", "id"),
                        ["type"] = ReadFirstString(entity, "entityType", "entity_type", "type"), ["name"] = ReadFirstString(entity, "name", "nameSnapshot", "name_snapshot"),
                        ["role"] = ReadString(entity, "role", "participant"), ["side"] = ReadString(entity, "side", ""),
                        ["party"] = ReadFirstString(entity, "partyId", "party_id"), ["clan"] = ReadFirstString(entity, "clanId", "clan_id"),
                        ["kingdom"] = ReadFirstString(entity, "kingdomId", "kingdom_id"), ["quantity"] = ReadDouble(entity, "quantity", 0d),
                        ["before"] = Json.Serialize(entity.ContainsKey("before") ? entity["before"] : new Dictionary<string, object>()),
                        ["after"] = Json.Serialize(entity.ContainsKey("after") ? entity["after"] : new Dictionary<string, object>()), ["payload"] = Json.Serialize(entity)
                    });
            }
        }

        private static List<Dictionary<string, object>>
            CompactWorldHistoryEntities(string eventType,
                List<Dictionary<string, object>> entities)
        {
            if (entities == null || entities.Count == 0)
                return entities ?? new List<Dictionary<string, object>>();
            HashSet<string> compactTypes = new HashSet<string>(new[]
            {
                "battle_completed", "raid_completed", "items_looted",
                "loot_distributed", "prisoners_sold", "troops_deserted",
                "troops_given_to_settlement"
            }, StringComparer.OrdinalIgnoreCase);
            if (!compactTypes.Contains(eventType ?? string.Empty))
                return entities;

            List<Dictionary<string, object>> result = entities
                .Where(entity =>
                {
                    string type = ReadFirstString(entity, "entityType",
                        "entity_type", "type");
                    return !type.Equals("troop", StringComparison.OrdinalIgnoreCase)
                        && !type.Equals("item", StringComparison.OrdinalIgnoreCase);
                }).ToList();
            IEnumerable<IGrouping<string, Dictionary<string, object>>> groups =
                entities.Where(entity =>
                {
                    string type = ReadFirstString(entity, "entityType",
                        "entity_type", "type");
                    return type.Equals("troop", StringComparison.OrdinalIgnoreCase)
                        || type.Equals("item", StringComparison.OrdinalIgnoreCase);
                }).GroupBy(entity => string.Join("|", new[]
                {
                    ReadFirstString(entity, "entityType", "entity_type", "type"),
                    ReadString(entity, "role", "participant"),
                    ReadString(entity, "side", ""),
                    ReadFirstString(entity, "partyId", "party_id")
                }), StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, Dictionary<string, object>> group in groups)
            {
                Dictionary<string, object> first = group.First();
                string entityType = ReadFirstString(first, "entityType",
                    "entity_type", "type");
                string role = ReadString(first, "role", "participant");
                string side = ReadString(first, "side", "");
                string partyId = ReadFirstString(first, "partyId", "party_id");
                result.Add(new Dictionary<string, object>
                {
                    ["entityId"] = "aggregate:" + entityType + ":"
                        + DeterministicSocialId(group.Key),
                    ["entityType"] = "aggregate",
                    ["name"] = role + " aggregate",
                    ["role"] = role,
                    ["side"] = side,
                    ["partyId"] = partyId,
                    ["quantity"] = group.Sum(entity =>
                        ReadDouble(entity, "quantity", 0d)),
                    ["payload"] = new Dictionary<string, object>
                    {
                        ["aggregatedEntityType"] = entityType,
                        ["distinctEntryCount"] = group.Count()
                    }
                });
            }
            return result;
        }

        private static void StoreWorldHistoryKnowledgeRules(ReignDbConnection connection, string eventId, string eventType, double day, string dissemination,
            List<Dictionary<string, object>> entities, List<Dictionary<string, object>> supplied)
        {
            List<Dictionary<string, object>> rules =
                BuildWorldHistoryKnowledgeRules(eventType, day,
                    dissemination, entities, supplied);

            int ordinal = 0;
            foreach (Dictionary<string, object> rule in rules)
            {
                string audienceType = FirstNonEmpty(ReadFirstString(rule, "audienceType", "audience_type"), "entity");
                string audienceId = ReadFirstString(rule, "audienceId", "audience_id");
                double available = ReadDouble(rule, "availableDay", ReadDouble(rule, "available_day", day));
                string ruleId = eventId + ":" + ordinal++ + ":" + audienceType + ":" + audienceId;
                ExecuteSql(connection, @"INSERT OR REPLACE INTO world_history_knowledge_rules(rule_id,event_id,audience_type,audience_id,available_day,acquisition_mode,confidence)
VALUES($id,$event,$type,$audience,$day,$mode,$confidence);", new Dictionary<string, object>
                {
                    ["id"] = ruleId, ["event"] = eventId, ["type"] = audienceType, ["audience"] = audienceId, ["day"] = available,
                    ["mode"] = ReadFirstString(rule, "acquisitionMode", "acquisition_mode"), ["confidence"] = ReadDouble(rule, "confidence", 1d)
                });
            }
        }

        private static bool IsImmediateHistoryParticipant(string eventType, Dictionary<string, object> entity)
        {
            string type = ReadFirstString(entity, "entityType", "entity_type", "type");
            return (type == "hero" || type == "character") && IsFirsthandWorldHistoryRole(eventType, ReadString(entity, "role", ""));
        }

        private static void InsertWorldHistoryFts(ReignDbConnection connection, string eventId, string text, List<Dictionary<string, object>> entities, string location, string eventType)
        {
            ExecuteSql(connection, "DELETE FROM world_history_fts WHERE event_id=$id;", new Dictionary<string, object> { ["id"] = eventId });
            ExecuteSql(connection, "INSERT INTO world_history_fts(event_id,text,entities,roles,location,event_type) VALUES($id,$text,$entities,$roles,$location,$type);",
                new Dictionary<string, object>
                {
                    ["id"] = eventId, ["text"] = text ?? "", ["entities"] = string.Join(" ", entities.Select(x => ReadFirstString(x, "entityId", "name", "nameSnapshot"))),
                    ["roles"] = string.Join(" ", entities.Select(x => ReadString(x, "role", ""))), ["location"] = location ?? "", ["type"] = eventType ?? ""
                });
        }

        private static Dictionary<string, object> WorldHistoryQueryApi(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>();
            string campaignId = query.ContainsKey("campaignId") ? query["campaignId"] : LatestCampaignId();
            string timelineId = query.ContainsKey("timelineId") ? query["timelineId"] : string.Empty;
            string entityId = query.ContainsKey("entityId") ? query["entityId"] : string.Empty;
            string type = query.ContainsKey("eventType") ? query["eventType"] : string.Empty;
            string text = query.ContainsKey("text") ? query["text"] : string.Empty;
            double fromDay = ParseDoubleInvariant(query.ContainsKey("fromDay") ? query["fromDay"] : "", -1d);
            double toDay = ParseDoubleInvariant(query.ContainsKey("toDay") ? query["toDay"] : "", double.MaxValue);
            int limit = Math.Max(1, Math.Min(500, ParseIntInvariant(query.ContainsKey("limit") ? query["limit"] : "", 100)));
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureWorldHistorySchema(connection);
                timelineId = FirstNonEmpty(timelineId, ActiveWorldHistoryTimeline(connection));
                List<string> terms = MemoryQueryTerms(text).Take(16).ToList();
                List<Dictionary<string, object>> rows = QuerySql(connection, @"SELECT DISTINCT e.* FROM world_history_events e
LEFT JOIN world_history_entities n ON n.event_id=e.event_id
WHERE e.timeline_id=$timeline AND ($entity='' OR n.entity_id=$entity) AND ($type='' OR e.event_type=$type)
AND ($from<0 OR e.world_day>=$from) AND e.world_day<=$to ORDER BY e.sequence DESC LIMIT $limit;",
                    new Dictionary<string, object> { ["timeline"] = timelineId, ["entity"] = entityId, ["type"] = type, ["from"] = fromDay, ["to"] = toDay, ["limit"] = Math.Max(limit, terms.Count > 0 ? Math.Min(500, limit * 5) : limit) });
                if (terms.Count > 0)
                {
                    rows = rows.Where(x => HistoryTermScore(x, terms) > 0d).OrderByDescending(x => HistoryTermScore(x, terms)).ThenByDescending(x => ReadLong(x, "sequence", 0)).Take(limit).ToList();
                }
                foreach (Dictionary<string, object> row in rows) row["entities"] = QueryWorldHistoryEntities(connection, ReadString(row, "event_id", ""));
                return new Dictionary<string, object> { ["ok"] = true, ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["count"] = rows.Count, ["events"] = rows };
            }
        }

        private static Dictionary<string, object> WorldHistoryEventApi(Dictionary<string, string> query, string eventId)
        {
            string campaignId = query != null && query.ContainsKey("campaignId") ? query["campaignId"] : LatestCampaignId();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureWorldHistorySchema(connection);
                Dictionary<string, object> row = QuerySql(connection, "SELECT * FROM world_history_events WHERE event_id=$id LIMIT 1;", new Dictionary<string, object> { ["id"] = eventId }).FirstOrDefault();
                if (row == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "World-history event was not found." };
                row["entities"] = QueryWorldHistoryEntities(connection, eventId);
                row["knowledgeRules"] = QuerySql(connection, "SELECT * FROM world_history_knowledge_rules WHERE event_id=$id ORDER BY available_day;", new Dictionary<string, object> { ["id"] = eventId });
                return new Dictionary<string, object> { ["ok"] = true, ["event"] = row };
            }
        }

        private static Dictionary<string, object> WorldHistoryCorrelationApi(Dictionary<string, string> query, string correlationId)
        {
            string campaignId = query != null && query.ContainsKey("campaignId") ? query["campaignId"] : LatestCampaignId();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureWorldHistorySchema(connection);
                string timeline = query != null && query.ContainsKey("timelineId") ? query["timelineId"] : ActiveWorldHistoryTimeline(connection);
                List<Dictionary<string, object>> rows = QuerySql(connection, "SELECT * FROM world_history_events WHERE timeline_id=$timeline AND correlation_id=$id ORDER BY sequence;",
                    new Dictionary<string, object> { ["timeline"] = timeline, ["id"] = correlationId });
                foreach (Dictionary<string, object> row in rows) row["entities"] = QueryWorldHistoryEntities(connection, ReadString(row, "event_id", ""));
                return new Dictionary<string, object> { ["ok"] = true, ["correlationId"] = correlationId, ["events"] = rows };
            }
        }

        private static Dictionary<string, object> WorldHistoryVerifyApi(Dictionary<string, object> payload)
        {
            Stopwatch timer = Stopwatch.StartNew();
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string claim = ReadFirstString(payload, "claim", "claimText", "playerText", "text", "message");
            string claimantId = ReadFirstString(payload, "claimantId", "playerId", "mainHeroStringId");
            string speakerId = ReadFirstString(payload, "speakerId", "npcId", "heroStringId", "speakerHeroStringId");
            string speakerKingdomId = ReadFirstString(payload, "speakerKingdomId", "npcKingdomId", "kingdomId");
            double worldDay = ReadDouble(payload, "worldDay", ReadDouble(payload, "currentWorldDay", 0d));
            if (string.IsNullOrWhiteSpace(claim)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "claim text is required." };
            bool lookupOnly = ReadBool(payload, "lookupOnly", false) || string.IsNullOrWhiteSpace(claimantId);

            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureWorldHistorySchema(connection);
                string timelineId = FirstNonEmpty(ReadFirstString(payload, "timelineId", "timeline_id"), ActiveWorldHistoryTimeline(connection));
                Dictionary<string, object> timeline = QuerySql(connection, "SELECT * FROM world_history_timelines WHERE timeline_id=$id LIMIT 1;", new Dictionary<string, object> { ["id"] = timelineId }).FirstOrDefault()
                    ?? new Dictionary<string, object>();
                long head = ReadLong(timeline, "head_sequence", 0);
                string cacheKey = NormalizeLookup(claim) + "|" + claimantId + "|" + speakerId + "|" + speakerKingdomId + "|" + timelineId + "|" + head + "|" + Math.Floor(worldDay) + "|lookup=" + lookupOnly;
                lock (WorldHistoryCacheLock)
                {
                    if (WorldHistoryClaimCache.TryGetValue(cacheKey, out Dictionary<string, object> cached))
                    {
                        return new Dictionary<string, object>(cached, StringComparer.OrdinalIgnoreCase) { ["cached"] = true };
                    }
                }

                Dictionary<string, object> route = ClassifyWorldHistoryClaim(claim);
                string purpose = ReadString(route, "purpose", "casual_recall");
                string family = ReadString(route, "claimFamily", "generic_participation");
                Dictionary<string, object> minimeRoute = TryMinimeHistoryRoute(claim, purpose, family);
                purpose = FirstNonEmpty(ReadString(minimeRoute, "purpose", ""), purpose);
                family = FirstNonEmpty(ReadString(minimeRoute, "claimFamily", ""), family);
                List<string> entityHints = ReadStringList(payload, "entityIds");
                string locationId = ReadFirstString(payload, "locationId", "settlementId");
                double fromDay = ReadDouble(payload, "fromDay", -1d);
                double toDay = ReadDouble(payload, "toDay", worldDay > 0 ? worldDay : double.MaxValue);
                double completeFrom = ReadDouble(timeline, "history_complete_from_day", 0d);

                string candidateClaimantId = lookupOnly ? "" : claimantId;
                List<Dictionary<string, object>> candidates = QueryWorldHistoryCandidates(connection, timelineId, claim, family, candidateClaimantId, entityHints, locationId, fromDay, toDay, 256);
                Dictionary<string, object> semanticHistory = TrySemanticMemorySearch(campaignId, claim,
                    new Dictionary<string, object>(), LoadSettings(), new[] { "world_history_event" }, timelineId);
                candidates = MergeSemanticWorldHistoryCandidates(connection, campaignId, timelineId, claim, family, claimantId,
                    entityHints, locationId, fromDay, toDay, candidates, semanticHistory, LoadSettings());
                Dictionary<string, object> minimeRerank = TryMinimeHistoryRerank(claim, purpose, family, candidates.Take(64).ToList());
                ApplyWorldHistoryMinimeScores(candidates, minimeRerank);
                candidates = candidates.OrderByDescending(x => ReadDouble(x, "combinedScore", ReadDouble(x, "deterministicScore", 0d))).Take(12).ToList();
                ExpandHistoryCorrelations(connection, timelineId, candidates);

                Dictionary<string, object> verdict;
                if (lookupOnly)
                {
                    double bestLexicalScore = candidates.Count == 0 ? 0d : candidates.Max(x => ReadDouble(x, "deterministicScore", 0d));
                    double threshold = Math.Max(2d, bestLexicalScore - 2d);
                    List<string> lookupEvidenceIds = candidates
                        .Where(x => ReadDouble(x, "deterministicScore", 0d) >= threshold)
                        .Take(6)
                        .Select(x => ReadString(x, "event_id", ""))
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    verdict = new Dictionary<string, object>
                    {
                        ["verdict"] = lookupEvidenceIds.Count > 0 ? "found" : "not_found",
                        ["explanation"] = lookupEvidenceIds.Count > 0
                            ? "Canonical world-history records matched the question. Only speaker-visible evidence is exposed."
                            : "No canonical world-history record matched the question.",
                        ["evidenceEventIds"] = lookupEvidenceIds,
                        ["matchedComponents"] = lookupEvidenceIds.Count > 0 ? new List<string> { "historical lookup" } : new List<string>(),
                        ["mismatchedComponents"] = new List<string>()
                    };
                }
                else
                {
                    verdict = AdjudicateWorldHistoryClaim(claim, family, claimantId, candidates, completeFrom, fromDay, toDay);
                }
                bool requestedPeriodWasCompacted =
                    WorldHistoryPeriodWasCompacted(connection, timelineId, fromDay, toDay);
                if (requestedPeriodWasCompacted)
                {
                    verdict["verdict"] = "history_expired";
                    verdict["explanation"] =
                        "Raw records for part of the requested period expired under the campaign retention policy. Compact totals cannot verify or contradict an individual claim.";
                    verdict["evidenceEventIds"] = new List<string>();
                    verdict["matchedComponents"] = new List<string>();
                    verdict["mismatchedComponents"] = new List<string>();
                }
                List<string> evidenceIds = ReadStringList(verdict, "evidenceEventIds");
                List<string> speakerIds = evidenceIds.Where(id => WorldHistoryEventVisibleToSpeaker(connection, id, speakerId, speakerKingdomId, worldDay)).ToList();
                foreach (string visibleEventId in speakerIds)
                {
                    UpsertKnowledgeReceipt(connection, visibleEventId, speakerId, "world_history_rule", 1d, 1d, worldDay, "world_history_verification", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                }
                List<string> reconciledBeliefIds = ReconcileWorldHistoryClaimBeliefs(connection, speakerId, speakerIds, worldDay);
                string objectiveVerdict = ReadString(verdict, "verdict", "insufficient_evidence");
                string speakerVerdict = string.IsNullOrWhiteSpace(speakerId) ? "unknown" : speakerIds.Count == 0 ? "unknown" : objectiveVerdict;
                string checkId = "whc_" + Guid.NewGuid().ToString("N");
                timer.Stop();
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = true, ["checkId"] = checkId, ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                    ["claim"] = claim, ["claimantId"] = claimantId, ["speakerId"] = speakerId, ["purpose"] = lookupOnly ? "historical_lookup" : purpose, ["claimFamily"] = family,
                    ["lookupOnly"] = lookupOnly,
                    ["objectiveVerdict"] = objectiveVerdict, ["speakerVerdict"] = speakerVerdict,
                    ["explanation"] = ReadString(verdict, "explanation", ""), ["evidenceEventIds"] = evidenceIds,
                    ["speakerEvidenceEventIds"] = speakerIds, ["matchedComponents"] = ReadStringList(verdict, "matchedComponents"),
                    ["mismatchedComponents"] = ReadStringList(verdict, "mismatchedComponents"),
                    ["speakerKnowledge"] = BuildWorldHistoryKnowledgeSummary(connection, speakerIds, speakerId, speakerKingdomId, worldDay),
                    ["reconciledBeliefIds"] = reconciledBeliefIds,
                    ["coverage"] = new Dictionary<string, object> { ["historyCompleteFromWorldDay"] = completeFrom, ["requestedFromDay"] = fromDay, ["complete"] = fromDay < 0 || completeFrom <= 0 || fromDay >= completeFrom },
                    ["minime"] = new Dictionary<string, object> { ["route"] = minimeRoute, ["rerank"] = minimeRerank },
                    ["semanticRetrieval"] = new Dictionary<string, object>
                    {
                        ["attempted"] = ReadBool(semanticHistory, "attempted", false), ["applied"] = ReadBool(semanticHistory, "applied", false),
                        ["reason"] = ReadString(semanticHistory, "reason", ""), ["resultCount"] = ReadInt(semanticHistory, "resultCount", 0),
                        ["durationMs"] = ReadLong(semanticHistory, "durationMs", 0)
                    },
                    ["candidateCount"] = candidates.Count, ["candidateEventIds"] = candidates.Select(x => ReadString(x, "event_id", "")).ToList(),
                    ["durationMs"] = timer.ElapsedMilliseconds, ["cached"] = false
                };
                ExecuteSql(connection, @"INSERT INTO world_history_claim_checks(check_id,campaign_id,timeline_id,world_day,claimant_id,speaker_id,raw_claim,purpose,claim_family,objective_verdict,speaker_verdict,explanation,constraints_json,evidence_event_ids_json,speaker_event_ids_json,minime_json,created_ts)
VALUES($id,$campaign,$timeline,$day,$claimant,$speaker,$claim,$purpose,$family,$objective,$speakerVerdict,$explanation,$constraints,$evidence,$speakerEvidence,$minime,$ts);",
                    new Dictionary<string, object>
                    {
                        ["id"] = checkId, ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = worldDay,
                        ["claimant"] = claimantId, ["speaker"] = speakerId, ["claim"] = claim, ["purpose"] = purpose, ["family"] = family,
                        ["objective"] = objectiveVerdict, ["speakerVerdict"] = speakerVerdict, ["explanation"] = ReadString(verdict, "explanation", ""),
                        ["constraints"] = Json.Serialize(payload), ["evidence"] = Json.Serialize(evidenceIds), ["speakerEvidence"] = Json.Serialize(speakerIds),
                        ["minime"] = Json.Serialize(result["minime"]), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    });
                lock (WorldHistoryCacheLock)
                {
                    if (WorldHistoryClaimCache.Count >= 256) WorldHistoryClaimCache.Remove(WorldHistoryClaimCache.Keys.First());
                    WorldHistoryClaimCache[cacheKey] = result;
                }
                return result;
            }
        }

        private static List<Dictionary<string, object>> QueryWorldHistoryCandidates(ReignDbConnection connection, string timelineId, string claim,
            string family, string claimantId, List<string> entityHints, string locationId, double fromDay, double toDay, int limit)
        {
            Dictionary<string, object> parameters = new Dictionary<string, object>
            {
                ["timeline"] = timelineId, ["location"] = locationId ?? "", ["from"] = fromDay, ["to"] = toDay <= 0 ? double.MaxValue : toDay,
                ["claimant"] = claimantId ?? "", ["limit"] = Math.Max(1, Math.Min(256, limit))
            };
            List<Dictionary<string, object>> rows = string.IsNullOrWhiteSpace(claimantId)
                ? QuerySql(connection, @"SELECT e.* FROM world_history_events e INDEXED BY idx_wh_event_evidence
WHERE e.timeline_id=$timeline AND ($location='' OR e.location_id=$location) AND ($from<0 OR e.world_day>=$from) AND e.world_day<=$to
ORDER BY e.sequence DESC LIMIT $limit;", parameters)
                : QuerySql(connection, @"SELECT e.* FROM world_history_entities n INDEXED BY idx_wh_entity_event
JOIN world_history_events e ON e.event_id=n.event_id WHERE n.entity_id=$claimant AND e.timeline_id=$timeline
AND ($location='' OR e.location_id=$location) AND ($from<0 OR e.world_day>=$from) AND e.world_day<=$to
ORDER BY e.sequence DESC LIMIT $limit;", parameters);
            List<string> ftsTerms = MemoryQueryTerms(claim).Where(x => x.Length > 2).Take(16).ToList();
            string ftsQuery = BuildFtsQuery(ftsTerms);
            if (!string.IsNullOrWhiteSpace(ftsQuery))
            {
                try
                {
                    List<Dictionary<string, object>> semanticRows = QuerySql(connection, @"SELECT e.* FROM world_history_fts JOIN world_history_events e ON e.event_id=world_history_fts.event_id
WHERE world_history_fts MATCH $query AND e.timeline_id=$timeline AND ($from<0 OR e.world_day>=$from) AND e.world_day<=$to LIMIT 128;",
                        new Dictionary<string, object> { ["query"] = ftsQuery, ["timeline"] = timelineId, ["from"] = fromDay, ["to"] = toDay <= 0 ? double.MaxValue : toDay });
                    HashSet<string> seen = new HashSet<string>(rows.Select(x => ReadString(x, "event_id", "")), StringComparer.OrdinalIgnoreCase);
                    rows.AddRange(semanticRows.Where(x => seen.Add(ReadString(x, "event_id", ""))));
                }
                catch { }
            }
            List<string> terms = MemoryQueryTerms(claim).Concat(FamilyTerms(family)).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList();
            foreach (Dictionary<string, object> row in rows)
            {
                List<Dictionary<string, object>> entities = QueryWorldHistoryEntities(connection, ReadString(row, "event_id", ""));
                row["entities"] = entities;
                double score = HistoryTermScore(row, terms);
                if (entities.Any(x => string.Equals(ReadString(x, "entity_id", ""), claimantId, StringComparison.OrdinalIgnoreCase))) score += 8d;
                foreach (string id in entityHints ?? new List<string>()) if (entities.Any(x => string.Equals(ReadString(x, "entity_id", ""), id, StringComparison.OrdinalIgnoreCase))) score += 4d;
                if (EventMatchesFamily(ReadString(row, "event_type", ""), entities, family)) score += 10d;
                row["deterministicScore"] = score;
                row["combinedScore"] = score;
            }
            return rows.OrderByDescending(x => ReadDouble(x, "deterministicScore", 0d)).Take(limit).ToList();
        }

        private static Dictionary<string, object> AdjudicateWorldHistoryClaim(string claim, string family, string claimantId,
            List<Dictionary<string, object>> candidates, double completeFrom, double fromDay, double toDay)
        {
            List<string> matched = new List<string>();
            List<string> mismatched = new List<string>();
            double bestDeterministic = candidates.Count == 0 ? 0d : candidates.Max(x => ReadDouble(x, "deterministicScore", 0d));
            List<Dictionary<string, object>> familyMatches = candidates.Where(x => EventMatchesFamily(ReadString(x, "event_type", ""), ReadDictionaryList(x, "entities"), family)
                && ReadDouble(x, "deterministicScore", 0d) >= Math.Max(1d, bestDeterministic - 4d)).ToList();
            List<Dictionary<string, object>> claimantMatches = familyMatches.Where(x => ReadDictionaryList(x, "entities").Any(e => string.Equals(ReadString(e, "entity_id", ""), claimantId, StringComparison.OrdinalIgnoreCase))).ToList();
            string verdict;
            string explanation;
            bool incomplete = fromDay >= 0 && completeFrom > 0 && fromDay < completeFrom;
            if (familyMatches.Count == 0)
            {
                verdict = incomplete ? "history_incomplete" : "not_found";
                explanation = incomplete ? "The requested period begins before reliable world-history capture." : "No canonical event matched the claim within the complete requested period.";
            }
            else if (string.IsNullOrWhiteSpace(claimantId))
            {
                verdict = "ambiguous_identity";
                explanation = "The claimant could not be resolved to a canonical character id.";
            }
            else if (family == "presence")
            {
                bool present = claimantMatches.Any(x => HasHistoryRole(x, claimantId, "participant", "leader", "commander", "attacker", "defender", "winner", "loser"));
                verdict = present ? "verified" : familyMatches.All(x => ReadBool(x, "is_complete", false)) ? "contradicted" : "insufficient_evidence";
                explanation = present ? "The claimant appears in the complete participant roster." : verdict == "contradicted" ? "Complete matching event rosters do not include the claimant." : "Matching events exist, but their participant evidence is incomplete.";
                (present ? matched : mismatched).Add("battle presence");
            }
            else if (family == "leadership")
            {
                bool led = claimantMatches.Any(x => HasHistoryRole(x, claimantId, "leader", "commander", "army_leader", "side_leader"));
                bool participated = claimantMatches.Any();
                verdict = led ? "verified" : participated ? "partially_verified" : familyMatches.All(x => ReadBool(x, "is_complete", false)) ? "contradicted" : "insufficient_evidence";
                explanation = led ? "The claimant is recorded as the relevant leader or commander." : participated ? "The claimant participated but is not recorded as the leader." : "No complete matching record assigns the claimant a command role.";
                if (participated) matched.Add("participation");
                if (!led) mismatched.Add("leadership");
            }
            else if (family == "rescue" || family == "release")
            {
                bool direct = claimantMatches.Any(x => HasHistoryRole(x, claimantId, "direct_rescuer", "rescuer", "releaser"));
                bool assisted = claimantMatches.Any(x => HasHistoryRole(x, claimantId, "assisting_rescuer", "rescuing_side_participant", "participant"));
                int beneficiaries = claimantMatches.SelectMany(x => ReadDictionaryList(x, "entities")).Where(x => RoleContains(ReadString(x, "role", ""), "beneficiary", "rescued_prisoner", "released_prisoner")).Select(x => ReadString(x, "entity_id", "")).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                bool severalClaim = ContainsAny(NormalizeLookup(claim), "several", "many", "multiple", "three", "four", "five");
                verdict = direct && (!severalClaim || beneficiaries >= 3) ? "verified" : direct || assisted ? "partially_verified" : familyMatches.All(x => ReadBool(x, "is_complete", false)) ? "contradicted" : "insufficient_evidence";
                explanation = direct ? "The claimant is connected to a direct release; beneficiary quantity was checked." : assisted ? "The claimant assisted the rescuing side but is not the direct recorded rescuer." : "No complete matching event credits the claimant with the rescue.";
                if (direct || assisted) matched.Add("rescue participation");
                if (severalClaim && beneficiaries < 3) mismatched.Add("several beneficiaries");
                if (!direct && assisted) mismatched.Add("direct rescue credit");
            }
            else
            {
                bool involved = claimantMatches.Any(x => HasFamilyRole(x, claimantId, family));
                verdict = involved ? "verified" : claimantMatches.Any() ? "partially_verified" : familyMatches.All(x => ReadBool(x, "is_complete", false)) ? "contradicted" : "insufficient_evidence";
                explanation = involved ? "Canonical event roles support the material claim." : claimantMatches.Any() ? "The claimant is connected to the event, but the claimed role is not fully supported." : "No complete matching event assigns the claimed role to the claimant.";
                (involved ? matched : mismatched).Add(family);
            }
            List<string> evidence = (claimantMatches.Count > 0 ? claimantMatches : familyMatches).Select(x => ReadString(x, "event_id", "")).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
            return new Dictionary<string, object> { ["verdict"] = verdict, ["explanation"] = explanation, ["evidenceEventIds"] = evidence, ["matchedComponents"] = matched, ["mismatchedComponents"] = mismatched };
        }

        private static Dictionary<string, object> ClassifyWorldHistoryClaim(string claim)
        {
            string q = NormalizeLookup(claim);
            string purpose = ContainsAny(q, "i did", "i was", "i fought", "i rescued", "i captured", "i killed") ? "self_claim"
                : ContainsAny(q, "because of me", "my victory", "my doing", "credit", "hero") ? "credit_or_boast"
                : ContainsAny(q, "was not there", "wasnt there", "elsewhere", "alibi") ? "alibi"
                : ContainsAny(q, "he did", "she did", "they did", "accuse", "blame") ? "accusation"
                : ContainsAny(q, "who was", "who did", "which lord") ? "identity_check" : "casual_recall";
            string family = ContainsAny(q, "rescued", "rescue", "freed", "saved the prisoners") ? "rescue"
                : ContainsAny(q, "released", "set free") ? "release"
                : ContainsAny(q, "led", "commanded", "my army") ? "leadership"
                : ContainsAny(q, "was at", "present at", "fought at", "took part") ? "presence"
                : ContainsAny(q, "won", "victory", "defeated") ? "victory"
                : ContainsAny(q, "lost", "defeat") ? "defeat"
                : ContainsAny(q, "captured", "took prisoner") ? "capture"
                : ContainsAny(q, "killed", "slew", "executed") ? "killing"
                : ContainsAny(q, "wounded", "injured") ? "wounding"
                : ContainsAny(q, "raided", "burned", "looted the village") ? "raid"
                : ContainsAny(q, "besieged", "siege") ? "siege"
                : ContainsAny(q, "conquered", "took the city", "captured the castle") ? "conquest"
                : ContainsAny(q, "defended", "held the walls") ? "defense"
                : ContainsAny(q, "owned", "owner", "gave me the fief") ? "ownership"
                : ContainsAny(q, "married", "wedding") ? "marriage"
                : ContainsAny(q, "declared war", "made peace", "alliance", "treaty") ? "diplomacy"
                : ContainsAny(q, "paid", "gave gold") ? "payment"
                : ContainsAny(q, "traded", "sold", "bought") ? "trade"
                : ContainsAny(q, "tournament", "champion") ? "tournament"
                : ContainsAny(q, "traveled", "went to", "arrived", "left the city") ? "travel"
                : ContainsAny(q, "met", "spoke with") ? "meeting" : "generic_participation";
            return new Dictionary<string, object> { ["purpose"] = purpose, ["claimFamily"] = family, ["method"] = "deterministic" };
        }

        private static Dictionary<string, object> TryMinimeHistoryRoute(string claim, string fallbackPurpose, string fallbackFamily)
        {
            Dictionary<string, object> status = new Dictionary<string, object> { ["applied"] = false, ["purpose"] = fallbackPurpose, ["claimFamily"] = fallbackFamily, ["method"] = "deterministic_fallback" };
            Dictionary<string, object> settings = LoadSettings();
            if (!ReadBool(settings, "enableMinimeMemoryWorker", true) || !ReadBool(settings, "enableMinimeMemoryReranking", true)) return status;
            try
            {
                List<Dictionary<string, object>> candidates = WorldHistoryPurposes.Select(x => new Dictionary<string, object> { ["id"] = "purpose:" + x, ["text"] = PurposeDescription(x) })
                    .Concat(WorldHistoryFamilies.Select(x => new Dictionary<string, object> { ["id"] = "family:" + x, ["text"] = FamilyDescription(x) })).ToList();
                string raw = PostJsonToUrl(ReadString(settings, "minimeRerankUrl", "http://127.0.0.1:8082/rerank"), Json.Serialize(new Dictionary<string, object>
                {
                    ["query"] = LimitText(claim, 1800), ["candidates"] = candidates, ["top_k"] = candidates.Count
                }), Math.Max(250, Math.Min(1500, ReadInt(settings, "minimeRerankTimeoutMs", 1500))));
                Dictionary<string, object> decoded = TryParseJsonObject(raw);
                List<Dictionary<string, object>> results = ReadDictionaryList(decoded, "results").Concat(ReadDictionaryList(decoded, "ranked")).Concat(ReadDictionaryList(decoded, "items")).ToList();
                List<Dictionary<string, object>> rankedPurposes = results.Where(x => ReadFirstString(x, "id", "candidate_id", "candidateId").StartsWith("purpose:"))
                    .OrderByDescending(x => ReadDouble(x, "score", ReadDouble(x, "similarity", 0d))).ToList();
                List<Dictionary<string, object>> rankedFamilies = results.Where(x => ReadFirstString(x, "id", "candidate_id", "candidateId").StartsWith("family:"))
                    .OrderByDescending(x => ReadDouble(x, "score", ReadDouble(x, "similarity", 0d))).ToList();
                Dictionary<string, object> bestPurpose = rankedPurposes.FirstOrDefault();
                Dictionary<string, object> bestFamily = rankedFamilies.FirstOrDefault();
                double purposeScore = bestPurpose == null ? 0d : ReadDouble(bestPurpose, "score", ReadDouble(bestPurpose, "similarity", 0d));
                double purposeMargin = rankedPurposes.Count < 2 ? purposeScore : purposeScore - ReadDouble(rankedPurposes[1], "score", ReadDouble(rankedPurposes[1], "similarity", 0d));
                double familyScore = bestFamily == null ? 0d : ReadDouble(bestFamily, "score", ReadDouble(bestFamily, "similarity", 0d));
                double familyMargin = rankedFamilies.Count < 2 ? familyScore : familyScore - ReadDouble(rankedFamilies[1], "score", ReadDouble(rankedFamilies[1], "similarity", 0d));
                string suggestedPurpose = bestPurpose == null ? "" : ReadFirstString(bestPurpose, "id", "candidate_id", "candidateId").Substring(8);
                string suggestedFamily = bestFamily == null ? "" : ReadFirstString(bestFamily, "id", "candidate_id", "candidateId").Substring(7);

                // Semantic routing may clarify an otherwise generic claim, but it never replaces an explicit deterministic classification.
                bool purposeOverride = fallbackPurpose == "casual_recall" && purposeScore >= 0.60d && purposeMargin >= 0.03d && suggestedPurpose != fallbackPurpose;
                bool familyOverride = fallbackFamily == "generic_participation" && familyScore >= 0.60d && familyMargin >= 0.03d && suggestedFamily != fallbackFamily;
                if (purposeOverride) status["purpose"] = suggestedPurpose;
                if (familyOverride) status["claimFamily"] = suggestedFamily;
                status["suggestedPurpose"] = suggestedPurpose;
                status["suggestedPurposeScore"] = Math.Round(purposeScore, 6);
                status["suggestedPurposeMargin"] = Math.Round(purposeMargin, 6);
                status["suggestedClaimFamily"] = suggestedFamily;
                status["suggestedClaimFamilyScore"] = Math.Round(familyScore, 6);
                status["suggestedClaimFamilyMargin"] = Math.Round(familyMargin, 6);
                status["purposeOverrideApplied"] = purposeOverride;
                status["familyOverrideApplied"] = familyOverride;
                status["applied"] = purposeOverride || familyOverride;
                status["method"] = ReadBool(status, "applied", false) ? "minime_generic_assist" : "deterministic_authoritative";
                status["results"] = results.Take(12).ToList();
            }
            catch (Exception ex) { status["error"] = LimitText(ex.Message, 500); }
            return status;
        }

        private static Dictionary<string, object> TryMinimeHistoryRerank(string claim, string purpose, string family, List<Dictionary<string, object>> candidates)
        {
            Dictionary<string, object> status = new Dictionary<string, object> { ["applied"] = false, ["scores"] = new Dictionary<string, object>(), ["candidateCount"] = candidates.Count };
            Dictionary<string, object> settings = LoadSettings();
            if (candidates.Count < 2 || !ReadBool(settings, "enableMinimeMemoryWorker", true) || !ReadBool(settings, "enableMinimeMemoryReranking", true)) return status;
            try
            {
                List<Dictionary<string, object>> outbound = candidates.Take(64).Select(x => new Dictionary<string, object>
                {
                    ["id"] = ReadString(x, "event_id", ""), ["text"] = LimitText(ReadString(x, "semantic_text", ReadString(x, "summary", "")), 1800)
                }).ToList();
                string query = "Purpose: " + purpose + ". Claim family: " + family + ". Find canonical evidence for: " + claim;
                string raw = PostJsonToUrl(ReadString(settings, "minimeRerankUrl", "http://127.0.0.1:8082/rerank"), Json.Serialize(new Dictionary<string, object>
                {
                    ["query"] = LimitText(query, 1800), ["candidates"] = outbound, ["top_k"] = outbound.Count
                }), Math.Max(250, Math.Min(1500, ReadInt(settings, "minimeRerankTimeoutMs", 1500))));
                Dictionary<string, object> decoded = TryParseJsonObject(raw);
                List<Dictionary<string, object>> results = ReadDictionaryList(decoded, "results").Concat(ReadDictionaryList(decoded, "ranked")).Concat(ReadDictionaryList(decoded, "items")).ToList();
                Dictionary<string, object> scores = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (Dictionary<string, object> result in results)
                {
                    string id = ReadFirstString(result, "id", "candidate_id", "candidateId");
                    if (!string.IsNullOrWhiteSpace(id)) scores[id] = ClampDouble(ReadDouble(result, "score", ReadDouble(result, "similarity", 0d)), -1d, 1d);
                }
                status["applied"] = scores.Count > 0; status["scores"] = scores; status["results"] = results.Take(64).ToList();
            }
            catch (Exception ex) { status["error"] = LimitText(ex.Message, 500); }
            return status;
        }

        private static void ApplyWorldHistoryMinimeScores(List<Dictionary<string, object>> candidates, Dictionary<string, object> rerank)
        {
            Dictionary<string, object> scores = ReadDictionary(rerank, "scores") ?? new Dictionary<string, object>();
            foreach (Dictionary<string, object> candidate in candidates)
            {
                string id = ReadString(candidate, "event_id", "");
                double semantic = scores.ContainsKey(id) ? Convert.ToDouble(scores[id], CultureInfo.InvariantCulture) : 0d;
                candidate["minimeScore"] = semantic;
                candidate["combinedScore"] = ReadDouble(candidate, "deterministicScore", 0d)
                    + Math.Max(0d, ReadDouble(candidate, "vectorSemanticScore", 0d)) * ReadDouble(LoadSettings(), "vectorScoreWeight", 30d)
                    + Math.Max(0d, semantic) * 5d;
            }
        }

        private static bool WorldHistoryEventVisibleToSpeaker(ReignDbConnection connection, string eventId, string speakerId, string speakerKingdomId, double worldDay)
        {
            if (string.IsNullOrWhiteSpace(speakerId)) return false;
            Dictionary<string, object> knowledge = DetermineWorldHistoryKnowledge(connection, new List<string> { eventId }, speakerId, speakerKingdomId, worldDay);
            return !string.Equals(ReadString(knowledge, "basis", "none"), "none", StringComparison.OrdinalIgnoreCase);
        }

        private static void ExpandHistoryCorrelations(ReignDbConnection connection, string timelineId, List<Dictionary<string, object>> candidates)
        {
            HashSet<string> present = new HashSet<string>(candidates.Select(x => ReadString(x, "event_id", "")), StringComparer.OrdinalIgnoreCase);
            foreach (string correlation in candidates.Select(x => ReadString(x, "correlation_id", "")).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList())
            {
                foreach (Dictionary<string, object> row in QuerySql(connection, "SELECT * FROM world_history_events WHERE timeline_id=$timeline AND correlation_id=$correlation ORDER BY sequence;", new Dictionary<string, object> { ["timeline"] = timelineId, ["correlation"] = correlation }))
                {
                    string id = ReadString(row, "event_id", "");
                    if (present.Add(id)) { row["entities"] = QueryWorldHistoryEntities(connection, id); row["deterministicScore"] = 1d; row["combinedScore"] = 1d; candidates.Add(row); }
                }
            }
        }

        private static List<Dictionary<string, object>> QueryWorldHistoryEntities(ReignDbConnection connection, string eventId)
        {
            return QuerySql(connection, "SELECT * FROM world_history_entities WHERE event_id=$id ORDER BY ordinal;", new Dictionary<string, object> { ["id"] = eventId });
        }

        private static string ActiveWorldHistoryTimeline(ReignDbConnection connection)
        {
            return ReadString(QuerySql(connection, "SELECT timeline_id FROM world_history_timelines WHERE is_active=1 ORDER BY updated_utc DESC LIMIT 1;").FirstOrDefault(), "timeline_id", "main");
        }

        private static string BuildWorldHistorySemanticText(string eventType, string phase, string summary, List<Dictionary<string, object>> entities, string location)
        {
            string roles = string.Join("; ", (entities ?? new List<Dictionary<string, object>>()).Take(80).Select(x =>
                FirstNonEmpty(ReadFirstString(x, "name", "nameSnapshot", "entityId", "id"), "unknown") + " was " + ReadString(x, "role", "participant") +
                (string.IsNullOrWhiteSpace(ReadString(x, "side", "")) ? "" : " on " + ReadString(x, "side", "") + " side")));
            return LimitText(eventType + " " + phase + " at " + location + ". " + summary + " Roles: " + roles, 12000);
        }

        private static string ClassifyWorldHistoryCategory(string eventType)
        {
            string q = NormalizeLookup(eventType);
            if (ContainsAny(q, "battle", "raid", "siege", "war", "army", "prisoner", "wounded", "killed")) return "warfare";
            if (ContainsAny(q, "marriage", "birth", "child", "relation", "clan")) return "personal_and_clan";
            if (ContainsAny(q, "trade", "item", "gold", "workshop", "caravan")) return "economy";
            if (ContainsAny(q, "settlement", "village", "governor", "building")) return "settlement";
            return "campaign";
        }

        private static bool IsMajorWorldHistoryEvent(string eventType)
        {
            return ContainsAny(NormalizeLookup(eventType), "war declared", "make peace", "alliance", "kingdom created", "kingdom destroyed", "ruling clan", "settlement owner", "marriage", "given birth", "hero killed", "rebellion");
        }

        private static bool EventMatchesFamily(string eventType, List<Dictionary<string, object>> entities, string family)
        {
            string q = NormalizeLookup(eventType + " " + string.Join(" ", (entities ?? new List<Dictionary<string, object>>()).Select(x => ReadString(x, "role", ""))));
            switch (family)
            {
                case "presence": case "leadership": case "victory": case "defeat": return ContainsAny(q, "battle", "raid", "siege", "hideout", "blockade");
                case "rescue": return ContainsAny(q, "rescue", "released prisoner", "direct rescuer", "assisting rescuer");
                case "release": return ContainsAny(q, "release", "freed");
                case "capture": return ContainsAny(q, "prisoner taken", "capture", "captor");
                case "killing": return ContainsAny(q, "killed", "killer", "execution");
                case "wounding": return ContainsAny(q, "wounded", "wound");
                case "raid": return ContainsAny(q, "raid", "village looted");
                case "siege": case "conquest": case "defense": return ContainsAny(q, "siege", "settlement owner", "conquest");
                case "ownership": return ContainsAny(q, "owner", "ownership", "workshop");
                case "marriage": return ContainsAny(q, "marriage", "married");
                case "diplomacy": return ContainsAny(q, "war declared", "peace", "alliance", "diplomacy", "treaty");
                case "payment": return ContainsAny(q, "gold", "payment", "paid");
                case "trade": return ContainsAny(q, "trade", "item sold", "barter");
                case "tournament": return ContainsAny(q, "tournament");
                case "travel": return ContainsAny(q, "settlement entered", "settlement left", "travel");
                case "meeting": return ContainsAny(q, "met", "conversation", "meeting");
                default: return true;
            }
        }

        private static bool HasHistoryRole(Dictionary<string, object> evt, string entityId, params string[] roles)
        {
            return ReadDictionaryList(evt, "entities").Any(x => string.Equals(ReadString(x, "entity_id", ""), entityId, StringComparison.OrdinalIgnoreCase) && RoleContains(ReadString(x, "role", ""), roles));
        }

        private static bool HasFamilyRole(Dictionary<string, object> evt, string entityId, string family)
        {
            string[] roles = family == "victory" ? new[] { "winner", "winning_side_participant", "leader", "participant" }
                : family == "defeat" ? new[] { "loser", "defeated", "losing_side_participant" }
                : family == "capture" ? new[] { "captor" }
                : family == "killing" ? new[] { "killer", "executor" }
                : family == "wounding" ? new[] { "attacker", "wounder" }
                : family == "raid" ? new[] { "raider", "attacker" }
                : family == "siege" || family == "conquest" ? new[] { "besieger", "attacker", "conqueror", "winner" }
                : family == "defense" ? new[] { "defender", "defending_leader", "winner" }
                : new[] { "actor", "participant", family };
            return HasHistoryRole(evt, entityId, roles);
        }

        private static bool RoleContains(string role, params string[] values)
        {
            string q = NormalizeLookup(role);
            return values.Any(x => q.Contains(NormalizeLookup(x)));
        }

        private static double HistoryTermScore(Dictionary<string, object> row, List<string> terms)
        {
            string text = NormalizeLookup(ReadString(row, "semantic_text", "") + " " + ReadString(row, "summary", "") + " " + ReadString(row, "event_type", "") + " " + ReadString(row, "location_name", ""));
            return (terms ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Sum(x => text.Contains(NormalizeLookup(x)) ? 1d : 0d);
        }

        private static List<string> FamilyTerms(string family)
        {
            return FamilyDescription(family).Split(new[] { ' ', ',', '.', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(NormalizeLookup).Where(x => x.Length > 3).Distinct().ToList();
        }

        private static string PurposeDescription(string purpose)
        {
            switch (purpose) { case "self_claim": return "The speaker claims personally to have done or witnessed a past act."; case "credit_or_boast": return "The speaker claims credit, leadership, heroism, or sole responsibility."; case "alibi": return "The speaker claims presence or absence to establish an alibi."; case "accusation": return "The speaker accuses another person of an act."; case "identity_check": return "The request asks which person performed an act."; default: return "The request casually recalls or asks about past events."; }
        }

        private static string FamilyDescription(string family)
        {
            return family.Replace('_', ' ') + " historical evidence, including exact actor, target, participant role, outcome, quantity, location, and time.";
        }

        private static double ParseDoubleInvariant(string value, double fallback) { return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : fallback; }
        private static int ParseIntInvariant(string value, int fallback) { return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback; }

        private static Dictionary<string, object> RunWorldHistorySelfTests()
        {
            string campaignId = "wh_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            string timelineId = "main";
            List<Dictionary<string, object>> events = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["eventId"]="battle_test",["sequence"]=1,["worldDay"]=100d,["eventType"]="battle_completed",["phase"]="completed",["isComplete"]=true,
                    ["locationId"]="settlement_test",["locationName"]="Test Field",["summary"]="The player fought and won a battle.",
                    ["entities"]=new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>{{"entityId","main_hero"},{"entityType","hero"},{"name","Player"},{"role","participant winner"},{"side","attacker"},{"kingdomId","kingdom_a"}},
                        new Dictionary<string, object>{{"entityId","lord_a"},{"entityType","hero"},{"name","Lord A"},{"role","leader commander winner"},{"side","attacker"},{"kingdomId","kingdom_a"}},
                        new Dictionary<string, object>{{"entityId","bandit_a"},{"entityType","party"},{"name","Bandits"},{"role","loser defender"},{"side","defender"}}
                    }
                },
                new Dictionary<string, object>
                {
                    ["eventId"]="rescue_test",["sequence"]=2,["worldDay"]=101d,["eventType"]="hero_prisoner_released rescue",["phase"]="completed",["isComplete"]=true,
                    ["summary"]="The player directly rescued three captured lords.",
                    ["entities"]=new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>{{"entityId","main_hero"},{"entityType","hero"},{"name","Player"},{"role","direct_rescuer"},{"kingdomId","kingdom_a"}},
                        new Dictionary<string, object>{{"entityId","lord_1"},{"entityType","hero"},{"name","Lord One"},{"role","rescued_prisoner beneficiary"}},
                        new Dictionary<string, object>{{"entityId","lord_2"},{"entityType","hero"},{"name","Lord Two"},{"role","rescued_prisoner beneficiary"}},
                        new Dictionary<string, object>{{"entityId","lord_3"},{"entityType","hero"},{"name","Lord Three"},{"role","rescued_prisoner beneficiary"}}
                    }
                }
            };
            Dictionary<string, object> ingest = WorldHistoryIngestBatchApi(new Dictionary<string, object>{{"campaignId",campaignId},{"timelineId",timelineId},{"historyCompleteFromWorldDay",90d},{"events",events}});
            Dictionary<string, object> duplicateIngest = WorldHistoryIngestBatchApi(new Dictionary<string, object>{{"campaignId",campaignId},{"timelineId",timelineId},{"historyCompleteFromWorldDay",90d},{"events",events}});
            Dictionary<string, object> presence = WorldHistoryVerifyApi(new Dictionary<string, object>{{"campaignId",campaignId},{"timelineId",timelineId},{"claim","I was at the battle at Test Field."},{"claimantId","main_hero"},{"speakerId","main_hero"},{"worldDay",101d}});
            Dictionary<string, object> leadership = WorldHistoryVerifyApi(new Dictionary<string, object>{{"campaignId",campaignId},{"timelineId",timelineId},{"claim","I led the battle at Test Field."},{"claimantId","main_hero"},{"speakerId","main_hero"},{"worldDay",101d}});
            Dictionary<string, object> rescue = WorldHistoryVerifyApi(new Dictionary<string, object>{{"campaignId",campaignId},{"timelineId",timelineId},{"claim","I rescued several captured lords."},{"claimantId","main_hero"},{"speakerId","main_hero"},{"worldDay",101d}});
            Dictionary<string, object> lookup = WorldHistoryVerifyApi(new Dictionary<string, object>{{"campaignId",campaignId},{"timelineId",timelineId},{"claim","What happened in the battle at Test Field according to canonical campaign history?"},{"claimantId",""},{"speakerId","main_hero"},{"worldDay",101d},{"lookupOnly",true}});
            List<Dictionary<string, object>> assertions = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>{{"name","ingest"},{"passed",ReadInt(ingest,"accepted",0)==2}},
                new Dictionary<string, object>{{"name","batched_duplicate_ingest"},{"passed",ReadInt(duplicateIngest,"accepted",-1)==0
                    && ReadInt(duplicateIngest,"duplicates",0)==2}},
                new Dictionary<string, object>{{"name","presence"},{"passed",ReadString(presence,"objectiveVerdict","")=="verified"}},
                new Dictionary<string, object>{{"name","leadership_not_granted"},{"passed",ReadString(leadership,"objectiveVerdict","")=="partially_verified"}},
                new Dictionary<string, object>{{"name","multi_lord_rescue"},{"passed",ReadString(rescue,"objectiveVerdict","")=="verified"}},
                new Dictionary<string, object>{{"name","generic_lookup"},{"passed",ReadString(lookup,"objectiveVerdict","")=="found"
                    && ReadStringList(lookup,"evidenceEventIds").Contains("battle_test",StringComparer.OrdinalIgnoreCase)
                    && ReadDictionaryList(ReadDictionary(lookup,"speakerKnowledge"),"events").Any(x=>ReadString(x,"event_id","")=="battle_test")}}
            };
            assertions.AddRange(RunLieDetectionAssertions(campaignId, timelineId));
            assertions.AddRange(RunWorldHistoryRetentionAssertions());
            assertions.AddRange(RunWorldHistoryDialogueAssertions(campaignId, timelineId));
            try { ReignPostgreSqlStorage.DropCampaign(campaignId); } catch { }
            try { Directory.Delete(CampaignDirectory(campaignId), true); } catch { }
            return new Dictionary<string, object> { ["ok"] = assertions.All(x => ReadBool(x, "passed", false)), ["passed"] = assertions.All(x => ReadBool(x, "passed", false)), ["assertions"] = assertions };
        }

        private static Dictionary<string, object> RunWorldHistoryStressTests(int rowCount = 1000000)
        {
            rowCount = Math.Max(1000, Math.Min(1000000, rowCount));
            string campaignId = "ws_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            string timelineId = "stress_main";
            Stopwatch total = Stopwatch.StartNew();
            long queryMs = long.MaxValue;
            int queryCount = 0;
            try
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureWorldHistorySchema(connection);
                    ExecuteSql(connection, @"INSERT INTO world_history_timelines(timeline_id,campaign_id,history_complete_from_day,head_sequence,head_event_id,is_active,created_utc,updated_utc)
VALUES($timeline,$campaign,1,$head,'stress_last',1,$now,$now);", new Dictionary<string, object>
                    {
                        ["timeline"] = timelineId, ["campaign"] = campaignId, ["head"] = rowCount, ["now"] = DateTime.UtcNow.ToString("o")
                    });
                    using (ReignDbTransaction transaction = connection.BeginTransaction())
                    using (ReignDbCommand eventCommand = connection.CreateCommand())
                    using (ReignDbCommand entityCommand = connection.CreateCommand())
                    {
                        eventCommand.Transaction = transaction;
                        eventCommand.CommandText = @"INSERT INTO world_history_events(event_id,campaign_id,timeline_id,sequence,world_day,event_type,category,phase,correlation_id,location_id,location_name,dissemination_class,summary,semantic_text,source,is_complete,checksum,payload_json,created_utc)
VALUES($id,$campaign,$timeline,$sequence,$day,$type,'warfare','completed',$correlation,$location,$location,'ordinary',$summary,$summary,'stress',1,'','{}',$created);";
                        foreach (string name in new[] { "$id", "$campaign", "$timeline", "$sequence", "$day", "$type", "$correlation", "$location", "$summary", "$created" }) eventCommand.Parameters.AddWithValue(name, "");
                        entityCommand.Transaction = transaction;
                        entityCommand.CommandText = @"INSERT INTO world_history_entities(event_id,ordinal,entity_id,entity_type,name_snapshot,role,side,party_id,clan_id,kingdom_id,quantity,before_json,after_json,payload_json)
VALUES($event,0,'main_hero','hero','Player','participant winner','attacker','main_party','player_clan','kingdom_a',1,'{}','{}','{}');";
                        entityCommand.Parameters.AddWithValue("$event", "");
                        string created = DateTime.UtcNow.ToString("o");
                        for (int i = 1; i <= rowCount; i++)
                        {
                            string id = "stress_" + i.ToString("D7", CultureInfo.InvariantCulture);
                            eventCommand.Parameters["$id"].Value = id;
                            eventCommand.Parameters["$campaign"].Value = campaignId;
                            eventCommand.Parameters["$timeline"].Value = timelineId;
                            eventCommand.Parameters["$sequence"].Value = i;
                            eventCommand.Parameters["$day"].Value = 1d + i / 100d;
                            eventCommand.Parameters["$type"].Value = i % 4 == 0 ? "battle_completed" : i % 4 == 1 ? "raid_completed" : i % 4 == 2 ? "settlement_entered" : "hero_prisoner_released";
                            eventCommand.Parameters["$correlation"].Value = "stress_corr_" + (i / 3).ToString(CultureInfo.InvariantCulture);
                            eventCommand.Parameters["$location"].Value = "settlement_" + (i % 250).ToString(CultureInfo.InvariantCulture);
                            eventCommand.Parameters["$summary"].Value = "Synthetic canonical history event " + i.ToString(CultureInfo.InvariantCulture);
                            eventCommand.Parameters["$created"].Value = created;
                            eventCommand.ExecuteNonQuery();
                            if (i % 10 == 0)
                            {
                                entityCommand.Parameters["$event"].Value = id;
                                entityCommand.ExecuteNonQuery();
                            }
                        }
                        transaction.Commit();
                    }
                    Stopwatch queryTimer = Stopwatch.StartNew();
                    queryCount = QuerySql(connection, @"SELECT e.event_id,e.sequence,e.world_day FROM world_history_entities n INDEXED BY idx_wh_entity_event
JOIN world_history_events e ON e.event_id=n.event_id WHERE n.entity_id='main_hero' AND e.timeline_id=$timeline
AND e.event_type='battle_completed' AND e.world_day BETWEEN $from AND $to ORDER BY e.world_day DESC LIMIT 256;",
                        new Dictionary<string, object> { ["timeline"] = timelineId, ["from"] = 5000d, ["to"] = 10001d }).Count;
                    queryTimer.Stop();
                    queryMs = queryTimer.ElapsedMilliseconds;
                }
            }
            finally
            {
                total.Stop();
                try { ReignPostgreSqlStorage.DropCampaign(campaignId); } catch { }
                try { Directory.Delete(CampaignDirectory(campaignId), true); } catch { }
            }
            bool passed = queryCount > 0 && queryMs < 250;
            return new Dictionary<string, object>
            {
                ["ok"] = passed, ["passed"] = passed, ["rowCount"] = rowCount, ["queryCount"] = queryCount,
                ["queryMs"] = queryMs, ["totalMs"] = total.ElapsedMilliseconds, ["targetQueryMs"] = 250
            };
        }
    }
}
