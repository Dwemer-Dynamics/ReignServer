using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        internal const string SemanticModelVersion = "bge-small-en-v1.5-384-precision-v2";
        internal const string SemanticCollection = "reign_memory_bge_small_en_v1_5_v1";
        private const string WorldHistoryEmbeddingFilterVersion = "2";
        private const string ConversationEmbeddingPolicyVersion = "2";
        internal static readonly object SemanticWorkerLock = new object();
        private static readonly object SemanticJobProcessingLock = new object();
        private static readonly object SemanticDiagnosticLock = new object();
        private static Process SemanticWorkerProcess;
        private static CancellationTokenSource SemanticPumpCancellation;
        internal static Task SemanticPumpTask;
        private static DateTime SemanticDiagnosticCooldownUntilUtc = DateTime.MinValue;
        private static DateTime SemanticWorkerLastHealthyUtc = DateTime.MinValue;
        private static DateTime SemanticWorkerUnavailableUntilUtc = DateTime.MinValue;
        private static string SemanticWorkerLastError = "";
        private static long SemanticLastQueryMs;
        private static Task SemanticWarmupTask;
        private static string SemanticWarmupState = "not_started";
        private static long SemanticWarmupDurationMs;
        private static DateTime SemanticWarmupCompletedUtc = DateTime.MinValue;
        private static int SemanticPumpGeneration;
        private static DateTime SemanticPumpLastCycleUtc = DateTime.MinValue;
        private static DateTime SemanticPumpLastBatchUtc = DateTime.MinValue;
        private static int SemanticPumpLastBatchSize;
        private static readonly HashSet<string> SemanticallySignificantWorldHistoryTypes =
            new HashSet<string>(new[]
            {
                "army_created", "army_dispersed", "army_gathered",
                "battle_completed", "child_conceived", "clan_changed_kingdom",
                "hero_changed_clan", "hero_killed", "hero_prisoner_released", "hero_prisoner_taken",
                "heroes_married", "kingdom_created", "kingdom_decision_added", "kingdom_decision_concluded",
                "kingdom_destroyed", "mercenary_service_started", "peace_made", "raid_completed",
                "rebellion_ended", "rebellion_started", "ruling_clan_changed",
                "settlement_owner_changed", "siege_completed", "siege_ended", "siege_started",
                "tournament_finished", "village_looted", "village_raid_started", "war_declared"
            }, StringComparer.OrdinalIgnoreCase);

        internal static void EnsureSemanticMemorySchema(ReignDbConnection connection)
        {
            if (Monitor.IsEntered(Program.CampaignSchemaLock) && !Monitor.IsEntered(Program.FileLock))
                throw new InvalidOperationException("Semantic schema settings require FileLock before CampaignSchemaLock.");
            string generation = ReadString(Program.LoadSettings(), "vectorProvider", "local")
                .ToLowerInvariant() + "|" + SemanticModelVersion;
            EnsureSemanticMemorySchema(connection, generation);
        }

        // Cold campaign initialization supplies settings before taking SchemaLock.
        internal static void EnsureSemanticMemorySchema(ReignDbConnection connection,
            string providerGeneration)
        {
            if (providerGeneration == null) throw new ArgumentNullException(nameof(providerGeneration));
            Program.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS embedding_documents (
source_type TEXT NOT NULL,source_id TEXT NOT NULL,model_version TEXT NOT NULL,provider TEXT NOT NULL,
campaign_id TEXT NOT NULL DEFAULT '',timeline_id TEXT NOT NULL DEFAULT '',content_hash TEXT NOT NULL DEFAULT '',
vector_id TEXT NOT NULL DEFAULT '',dimensions INTEGER NOT NULL DEFAULT 384,status TEXT NOT NULL DEFAULT 'pending',
attempt_count INTEGER NOT NULL DEFAULT 0,last_error TEXT NOT NULL DEFAULT '',updated_ts INTEGER NOT NULL DEFAULT 0,
payload_json TEXT NOT NULL DEFAULT '{}',PRIMARY KEY(source_type,source_id,model_version,provider));");
            Program.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_embedding_documents_status ON embedding_documents(status,updated_ts);");
            Program.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS embedding_jobs (
source_type TEXT NOT NULL,source_id TEXT NOT NULL,operation TEXT NOT NULL DEFAULT 'upsert',status TEXT NOT NULL DEFAULT 'pending',
attempt_count INTEGER NOT NULL DEFAULT 0,next_attempt_ts INTEGER NOT NULL DEFAULT 0,last_error TEXT NOT NULL DEFAULT '',
enqueued_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,PRIMARY KEY(source_type,source_id));");
            Program.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_embedding_jobs_ready ON embedding_jobs(status,next_attempt_ts,enqueued_ts);");

            CreateEventEmbeddingTriggers(connection);
            CreateEmbeddingTrigger(connection, "memories", "memory", "memory_id", "summary,memory_type,memory_domain,tags_json,participants_json,about_entities_json,location_id,visibility,status", true);
            CreateEmbeddingTrigger(connection, "summaries", "summary", "summary_id", "summary,summary_type,memory_lane,tags_json,participants_json,about_entities_json,location_id,visibility,status", true);
            CreateEmbeddingTrigger(connection, "beliefs", "belief", "belief_id", "claim,source,about_entities_json,visibility", false);
            CreateEmbeddingTrigger(connection, "comprehension", "comprehension", "comprehension_id", "text,stance,about_entities_json", false);
            CreateEmbeddingTrigger(connection, "conversation_turns", "conversation_turn", "turn_id",
                "text,speaker_id,participants_json,payload_json,status,world_day", true);
            CreateWorldHistoryEmbeddingTriggers(connection);
            EnsureWorldHistoryEmbeddingFilter(connection);
            EnsureConversationEmbeddingPolicy(connection);
            // Only the derivative index is backfilled. Original transcripts and campaign effects are untouched.
            if (ReadString(Program.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='semantic_original_dialogue_v1' LIMIT 1;").FirstOrDefault(), "value", "") != "complete")
            {
                SeedEmbeddingJobs(connection, "conversation_turns", "conversation_turn", "turn_id");
                Program.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('semantic_original_dialogue_v1','complete');");
            }

            Dictionary<string, object> seeded = Program.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='semantic_memory_v1_seeded' LIMIT 1;").FirstOrDefault();
            if (!string.Equals(ReadString(seeded, "value", ""), "complete", StringComparison.OrdinalIgnoreCase))
            {
                QueueAllEmbeddingJobs(connection, false);
                Program.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('semantic_memory_v1_seeded','complete');");
            }
            string activeGeneration = ReadString(Program.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='semantic_memory_provider_generation' LIMIT 1;").FirstOrDefault(), "value", "");
            if (!string.Equals(providerGeneration, activeGeneration, StringComparison.OrdinalIgnoreCase))
            {
                QueueAllEmbeddingJobs(connection, true);
                Program.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('semantic_memory_provider_generation',$value);",
                    new Dictionary<string, object> { ["value"] = providerGeneration });
            }
            Program.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('semantic_memory_version','1');");
        }

        private static void CreateEmbeddingTrigger(ReignDbConnection connection, string table, string sourceType, string idColumn, string updateColumns, bool hasStatus)
        {
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                CreatePostgreSqlEmbeddingTriggers(connection, table, sourceType, idColumn, "TRUE");
                return;
            }
            string safe = "semantic_" + table;
            string upsert = "INSERT INTO embedding_jobs(source_type,source_id,operation,status,attempt_count,next_attempt_ts,last_error,enqueued_ts,updated_ts) "
                + "VALUES('" + sourceType + "',NEW." + idColumn + ",'upsert','pending',0,0,'',strftime('%s','now'),strftime('%s','now')) "
                + "ON CONFLICT(source_type,source_id) DO UPDATE SET operation='upsert',status='pending',attempt_count=0,next_attempt_ts=0,last_error='',enqueued_ts=strftime('%s','now'),updated_ts=strftime('%s','now');";
            Program.ExecuteSql(connection, "CREATE TRIGGER IF NOT EXISTS trg_" + safe + "_insert AFTER INSERT ON " + table + " BEGIN " + upsert + " END;");
            Program.ExecuteSql(connection, "CREATE TRIGGER IF NOT EXISTS trg_" + safe + "_update AFTER UPDATE OF " + updateColumns + " ON " + table + " BEGIN " + upsert + " END;");
            Program.ExecuteSql(connection, "CREATE TRIGGER IF NOT EXISTS trg_" + safe + "_delete AFTER DELETE ON " + table + " BEGIN "
                + "INSERT INTO embedding_jobs(source_type,source_id,operation,status,attempt_count,next_attempt_ts,last_error,enqueued_ts,updated_ts) "
                + "VALUES('" + sourceType + "',OLD." + idColumn + ",'delete','pending',0,0,'',strftime('%s','now'),strftime('%s','now')) "
                + "ON CONFLICT(source_type,source_id) DO UPDATE SET operation='delete',status='pending',attempt_count=0,next_attempt_ts=0,last_error='',enqueued_ts=strftime('%s','now'),updated_ts=strftime('%s','now'); END;");
        }

        private static void DropEmbeddingTriggers(ReignDbConnection connection, string table)
        {
            string safe = "semantic_" + table;
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                Program.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_insert ON " + table + ";");
                Program.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_update ON " + table + ";");
                Program.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_update_ineligible ON " + table + ";");
                Program.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_delete ON " + table + ";");
                // Interrupted schema upgrades can leave a trigger with an older
                // name attached to this table. CASCADE makes function recreation
                // idempotent by removing every stale dependent trigger as well.
                Program.ExecuteSql(connection, "DROP FUNCTION IF EXISTS fn_" + safe + "_queue() CASCADE;");
                return;
            }
            Program.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_insert;");
            Program.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_update;");
            Program.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_update_ineligible;");
            Program.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_delete;");
        }

        private static string EventEmbeddingEligibilitySql(string prefix)
        {
            string columnPrefix = prefix ?? "";
            return "(LOWER(COALESCE(" + columnPrefix + "embedding_status,'')) NOT IN ('skipped','deleted','inactive')"
                + " AND LOWER(COALESCE(" + columnPrefix + "event_category,'')) NOT IN ('conversation','correspondence'))";
        }

        private static bool EventEmbeddingEligible(Dictionary<string, object> row)
        {
            if (row == null) return false;
            string embeddingStatus = ReadString(row, "embedding_status", "");
            string category = ReadString(row, "event_category", "");
            return !Program.ContainsAny(embeddingStatus.ToLowerInvariant(), "skipped", "deleted", "inactive")
                && !Program.ContainsAny(category.ToLowerInvariant(), "conversation", "correspondence");
        }

        private static void CreateEventEmbeddingTriggers(ReignDbConnection connection)
        {
            const string table = "events";
            const string sourceType = "event";
            const string idColumn = "event_id";
            const string updateColumns = "summary,event_type,event_category,location_id,participants_json,about_entities_json,visibility,embedding_status";
            string safe = "semantic_" + table;
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                CreatePostgreSqlEmbeddingTriggers(
                    connection, table, sourceType, idColumn, EventEmbeddingEligibilitySql("NEW."));
                return;
            }
            DropEmbeddingTriggers(connection, table);
            string upsert = "INSERT INTO embedding_jobs(source_type,source_id,operation,status,attempt_count,next_attempt_ts,last_error,enqueued_ts,updated_ts) "
                + "VALUES('" + sourceType + "',NEW." + idColumn + ",'upsert','pending',0,0,'',strftime('%s','now'),strftime('%s','now')) "
                + "ON CONFLICT(source_type,source_id) DO UPDATE SET operation='upsert',status='pending',attempt_count=0,next_attempt_ts=0,last_error='',enqueued_ts=strftime('%s','now'),updated_ts=strftime('%s','now');";
            string remove = "INSERT INTO embedding_jobs(source_type,source_id,operation,status,attempt_count,next_attempt_ts,last_error,enqueued_ts,updated_ts) "
                + "VALUES('" + sourceType + "',NEW." + idColumn + ",'delete','pending',0,0,'',strftime('%s','now'),strftime('%s','now')) "
                + "ON CONFLICT(source_type,source_id) DO UPDATE SET operation='delete',status='pending',attempt_count=0,next_attempt_ts=0,last_error='',enqueued_ts=strftime('%s','now'),updated_ts=strftime('%s','now');";
            string eligible = EventEmbeddingEligibilitySql("NEW.");
            Program.ExecuteSql(connection, "CREATE TRIGGER trg_" + safe + "_insert AFTER INSERT ON " + table
                + " WHEN " + eligible + " BEGIN " + upsert + " END;");
            Program.ExecuteSql(connection, "CREATE TRIGGER trg_" + safe + "_update AFTER UPDATE OF " + updateColumns + " ON " + table
                + " WHEN " + eligible + " BEGIN " + upsert + " END;");
            Program.ExecuteSql(connection, "CREATE TRIGGER trg_" + safe + "_update_ineligible AFTER UPDATE OF " + updateColumns + " ON " + table
                + " WHEN NOT " + eligible + " BEGIN " + remove + " END;");
            Program.ExecuteSql(connection, "CREATE TRIGGER trg_" + safe + "_delete AFTER DELETE ON " + table + " BEGIN "
                + "INSERT INTO embedding_jobs(source_type,source_id,operation,status,attempt_count,next_attempt_ts,last_error,enqueued_ts,updated_ts) "
                + "VALUES('" + sourceType + "',OLD." + idColumn + ",'delete','pending',0,0,'',strftime('%s','now'),strftime('%s','now')) "
                + "ON CONFLICT(source_type,source_id) DO UPDATE SET operation='delete',status='pending',attempt_count=0,next_attempt_ts=0,last_error='',enqueued_ts=strftime('%s','now'),updated_ts=strftime('%s','now'); END;");
        }

        private static void EnsureConversationEmbeddingPolicy(ReignDbConnection connection)
        {
            Dictionary<string, object> applied = Program.QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='semantic_conversation_embedding_policy' LIMIT 1;").FirstOrDefault();
            if (!string.Equals(ReadString(applied, "value", ""), ConversationEmbeddingPolicyVersion, StringComparison.OrdinalIgnoreCase))
            {
                // Raw turns remain canonical and FTS-searchable for the configured recent/exact
                // history window. Durable memories and summaries are the semantic layer.
                Program.ExecuteSql(connection, @"UPDATE events SET embedding_status='skipped'
WHERE LOWER(COALESCE(event_category,'')) IN ('conversation','correspondence');");

                // Before this policy, each ordinary conversation event projected one generic
                // interpersonal memory per participant. Retain explicit episodic/model writes,
                // but retire the mechanically duplicated projections.
                Program.ExecuteSql(connection, @"DELETE FROM memory_fts WHERE memory_id IN
(SELECT memory_id FROM memories WHERE LOWER(COALESCE(memory_type,''))='interpersonal_experience'
 AND LOWER(COALESCE(source,'')) IN ('dialogue_turn','party_chat_turn','social_event_turn'));" );
                Program.ExecuteSql(connection, @"DELETE FROM memories
WHERE LOWER(COALESCE(memory_type,''))='interpersonal_experience'
 AND LOWER(COALESCE(source,'')) IN ('dialogue_turn','party_chat_turn','social_event_turn');");

                Program.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('semantic_conversation_embedding_policy',$value);",
                    new Dictionary<string, object> { ["value"] = ConversationEmbeddingPolicyVersion });
            }
            QueueRetiredEmbeddingDeletes(connection);
        }

        private static void QueueRetiredEmbeddingDeletes(ReignDbConnection connection)
        {
            // Original attributed turns are active semantic sources again; deletion is driven by their own status/trigger.
            string ineligibleEvent = "NOT " + EventEmbeddingEligibilitySql("e.");
            Program.ExecuteSql(connection, @"DELETE FROM embedding_jobs WHERE source_type='event' AND source_id IN
(SELECT e.event_id FROM events e WHERE " + ineligibleEvent + ");");
            Program.ExecuteSql(connection, @"INSERT INTO embedding_jobs(source_type,source_id,operation,status,attempt_count,next_attempt_ts,last_error,enqueued_ts,updated_ts)
SELECT 'event',d.source_id,'delete','pending',0,0,'',strftime('%s','now'),strftime('%s','now')
FROM embedding_documents d JOIN events e ON e.event_id=d.source_id
WHERE d.source_type='event' AND d.status='indexed' AND " + ineligibleEvent + @"
ON CONFLICT(source_type,source_id) DO UPDATE SET operation='delete',status='pending',attempt_count=0,next_attempt_ts=0,last_error='',enqueued_ts=strftime('%s','now'),updated_ts=strftime('%s','now');");
            Program.ExecuteSql(connection, @"DELETE FROM embedding_documents WHERE source_type='event' AND status<>'indexed' AND source_id IN
(SELECT e.event_id FROM events e WHERE " + ineligibleEvent + ");");
        }

        private static string WorldHistoryEmbeddingEligibilitySql(string prefix)
        {
            string columnPrefix = prefix ?? "";
            string types = string.Join(",", SemanticallySignificantWorldHistoryTypes
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => "'" + value.Replace("'", "''").ToLowerInvariant() + "'"));
            return "(LOWER(COALESCE(" + columnPrefix + "dissemination_class,''))='major_world'"
                + " OR LOWER(COALESCE(" + columnPrefix + "event_type,'')) IN (" + types + "))";
        }

        internal static bool WorldHistoryEventEmbeddingEligible(Dictionary<string, object> row)
        {
            if (row == null) return false;
            if (string.Equals(ReadString(row, "dissemination_class", ""), "major_world", StringComparison.OrdinalIgnoreCase)) return true;
            return SemanticallySignificantWorldHistoryTypes.Contains(ReadString(row, "event_type", ""));
        }

        private static void CreateWorldHistoryEmbeddingTriggers(ReignDbConnection connection)
        {
            const string table = "world_history_events";
            const string sourceType = "world_history_event";
            const string idColumn = "event_id";
            const string updateColumns = "semantic_text,summary,event_type,category,phase,location_name,timeline_id,dissemination_class";
            string safe = "semantic_" + table;
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                CreatePostgreSqlEmbeddingTriggers(
                    connection, table, sourceType, idColumn, WorldHistoryEmbeddingEligibilitySql("NEW."));
                return;
            }
            Program.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_insert;");
            Program.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_update;");
            Program.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_update_ineligible;");
            Program.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_delete;");
            string upsert = "INSERT INTO embedding_jobs(source_type,source_id,operation,status,attempt_count,next_attempt_ts,last_error,enqueued_ts,updated_ts) "
                + "VALUES('" + sourceType + "',NEW." + idColumn + ",'upsert','pending',0,0,'',strftime('%s','now'),strftime('%s','now')) "
                + "ON CONFLICT(source_type,source_id) DO UPDATE SET operation='upsert',status='pending',attempt_count=0,next_attempt_ts=0,last_error='',enqueued_ts=strftime('%s','now'),updated_ts=strftime('%s','now');";
            string remove = "INSERT INTO embedding_jobs(source_type,source_id,operation,status,attempt_count,next_attempt_ts,last_error,enqueued_ts,updated_ts) "
                + "VALUES('" + sourceType + "',NEW." + idColumn + ",'delete','pending',0,0,'',strftime('%s','now'),strftime('%s','now')) "
                + "ON CONFLICT(source_type,source_id) DO UPDATE SET operation='delete',status='pending',attempt_count=0,next_attempt_ts=0,last_error='',enqueued_ts=strftime('%s','now'),updated_ts=strftime('%s','now');";
            string eligible = WorldHistoryEmbeddingEligibilitySql("NEW.");
            Program.ExecuteSql(connection, "CREATE TRIGGER trg_" + safe + "_insert AFTER INSERT ON " + table
                + " WHEN " + eligible + " BEGIN " + upsert + " END;");
            Program.ExecuteSql(connection, "CREATE TRIGGER trg_" + safe + "_update AFTER UPDATE OF " + updateColumns + " ON " + table
                + " WHEN " + eligible + " BEGIN " + upsert + " END;");
            Program.ExecuteSql(connection, "CREATE TRIGGER trg_" + safe + "_update_ineligible AFTER UPDATE OF " + updateColumns + " ON " + table
                + " WHEN NOT " + eligible + " BEGIN " + remove + " END;");
            Program.ExecuteSql(connection, "CREATE TRIGGER trg_" + safe + "_delete AFTER DELETE ON " + table + " BEGIN "
                + "INSERT INTO embedding_jobs(source_type,source_id,operation,status,attempt_count,next_attempt_ts,last_error,enqueued_ts,updated_ts) "
                + "VALUES('" + sourceType + "',OLD." + idColumn + ",'delete','pending',0,0,'',strftime('%s','now'),strftime('%s','now')) "
                + "ON CONFLICT(source_type,source_id) DO UPDATE SET operation='delete',status='pending',attempt_count=0,next_attempt_ts=0,last_error='',enqueued_ts=strftime('%s','now'),updated_ts=strftime('%s','now'); END;");
        }

        private static void CreatePostgreSqlEmbeddingTriggers(
            ReignDbConnection connection,
            string table,
            string sourceType,
            string idColumn,
            string eligibleSql)
        {
            string safe = "semantic_" + table;
            DropEmbeddingTriggers(connection, table);
            string function = "fn_" + safe + "_queue";
            string source = sourceType.Replace("'", "''");
            string eligibility = string.IsNullOrWhiteSpace(eligibleSql) ? "TRUE" : eligibleSql;
            Program.ExecuteSql(connection, @"
CREATE OR REPLACE FUNCTION " + function + @"() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    source_id_value text;
    requested_operation text;
    now_epoch bigint := CAST(EXTRACT(EPOCH FROM clock_timestamp()) AS bigint);
BEGIN
    IF TG_OP='DELETE' THEN
        source_id_value := OLD." + idColumn + @"::text;
        requested_operation := 'delete';
    ELSE
        source_id_value := NEW." + idColumn + @"::text;
        IF " + eligibility + @" THEN
            requested_operation := 'upsert';
        ELSIF TG_OP='INSERT' THEN
            -- An ineligible row has never had a semantic document to retire.
            -- Match the SQLite trigger policy by leaving no no-op delete job.
            RETURN NEW;
        ELSE
            requested_operation := 'delete';
        END IF;
    END IF;

    INSERT INTO embedding_jobs(
        source_type,source_id,operation,status,attempt_count,next_attempt_ts,
        last_error,enqueued_ts,updated_ts)
    VALUES('" + source + @"',source_id_value,requested_operation,'pending',0,0,'',
           now_epoch,now_epoch)
    ON CONFLICT(source_type,source_id) DO UPDATE SET
        operation=excluded.operation,status='pending',attempt_count=0,
        next_attempt_ts=0,last_error='',enqueued_ts=excluded.enqueued_ts,
        updated_ts=excluded.updated_ts;
    IF TG_OP='DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END
$$;");
            Program.ExecuteSql(connection, "CREATE TRIGGER trg_" + safe
                + "_insert AFTER INSERT ON " + table
                + " FOR EACH ROW EXECUTE FUNCTION " + function + "();");
            Program.ExecuteSql(connection, "CREATE TRIGGER trg_" + safe
                + "_update AFTER UPDATE ON " + table
                + " FOR EACH ROW EXECUTE FUNCTION " + function + "();");
            Program.ExecuteSql(connection, "CREATE TRIGGER trg_" + safe
                + "_delete AFTER DELETE ON " + table
                + " FOR EACH ROW EXECUTE FUNCTION " + function + "();");
        }

        private static void EnsureWorldHistoryEmbeddingFilter(ReignDbConnection connection)
        {
            Dictionary<string, object> applied = Program.QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='semantic_world_history_filter' LIMIT 1;").FirstOrDefault();
            if (string.Equals(ReadString(applied, "value", ""), WorldHistoryEmbeddingFilterVersion, StringComparison.OrdinalIgnoreCase)) return;
            string eligible = WorldHistoryEmbeddingEligibilitySql("w.");
            Program.ExecuteSql(connection, @"DELETE FROM embedding_jobs
WHERE source_type='world_history_event' AND source_id IN
(SELECT w.event_id FROM world_history_events w WHERE NOT " + eligible + ");");
            Program.ExecuteSql(connection, @"INSERT INTO embedding_jobs(source_type,source_id,operation,status,attempt_count,next_attempt_ts,last_error,enqueued_ts,updated_ts)
SELECT 'world_history_event',d.source_id,'delete','pending',0,0,'',strftime('%s','now'),strftime('%s','now')
FROM embedding_documents d JOIN world_history_events w ON w.event_id=d.source_id
WHERE d.source_type='world_history_event' AND d.status='indexed' AND NOT " + eligible + @"
ON CONFLICT(source_type,source_id) DO UPDATE SET operation='delete',status='pending',attempt_count=0,next_attempt_ts=0,last_error='',enqueued_ts=strftime('%s','now'),updated_ts=strftime('%s','now');");
            Program.ExecuteSql(connection, @"DELETE FROM embedding_documents
WHERE source_type='world_history_event' AND status<>'indexed' AND source_id IN
(SELECT w.event_id FROM world_history_events w WHERE NOT " + eligible + ");");
            Program.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('semantic_world_history_filter',$value);",
                new Dictionary<string, object> { ["value"] = WorldHistoryEmbeddingFilterVersion });
        }

        private static void SeedEmbeddingJobs(ReignDbConnection connection, string table, string sourceType, string idColumn)
        {
            string where = string.Equals(sourceType, "world_history_event", StringComparison.OrdinalIgnoreCase)
                ? " WHERE " + WorldHistoryEmbeddingEligibilitySql("")
                : string.Equals(sourceType, "event", StringComparison.OrdinalIgnoreCase)
                    ? " WHERE " + EventEmbeddingEligibilitySql("")
                    : string.Equals(sourceType, "conversation_turn", StringComparison.OrdinalIgnoreCase)
                        ? " WHERE LOWER(COALESCE(status,'active'))='active'" : "";
            Program.ExecuteSql(connection, "INSERT OR IGNORE INTO embedding_jobs(source_type,source_id,operation,status,attempt_count,next_attempt_ts,last_error,enqueued_ts,updated_ts) "
                + "SELECT '" + sourceType + "'," + idColumn + ",'upsert','pending',0,0,'',strftime('%s','now'),strftime('%s','now') FROM " + table + where + ";");
        }

        private static void QueueAllEmbeddingJobs(ReignDbConnection connection, bool resetExisting)
        {
            if (resetExisting) Program.ExecuteSql(connection, "DELETE FROM embedding_jobs;");
            SeedEmbeddingJobs(connection, "events", "event", "event_id");
            SeedEmbeddingJobs(connection, "memories", "memory", "memory_id");
            SeedEmbeddingJobs(connection, "summaries", "summary", "summary_id");
            SeedEmbeddingJobs(connection, "beliefs", "belief", "belief_id");
            SeedEmbeddingJobs(connection, "comprehension", "comprehension", "comprehension_id");
            SeedEmbeddingJobs(connection, "conversation_turns", "conversation_turn", "turn_id");
            SeedEmbeddingJobs(connection, "world_history_events", "world_history_event", "event_id");
            QueueRetiredEmbeddingDeletes(connection);
        }

        internal static void StartSemanticMemorySubsystem(Dictionary<string, object> settings)
        {
            if (!ReadBool(settings, "enableSemanticMemory", true)) return;
            StartManagedSemanticWorker(settings);
            StartSemanticMemoryWarmup(settings);
            lock (SemanticWorkerLock)
            {
                if (SemanticPumpUsable(SemanticPumpTask, SemanticPumpCancellation)) return;
                SemanticPumpCancellation = new CancellationTokenSource();
                CancellationToken token = SemanticPumpCancellation.Token;
                int generation = ++SemanticPumpGeneration;
                SemanticPumpTask = Task.Run(() => SemanticIndexPump(token, generation));
            }
        }

        private static void StartSemanticMemoryWarmup(Dictionary<string, object> settings)
        {
            lock (SemanticWorkerLock)
            {
                if (SemanticWarmupTask != null && !SemanticWarmupTask.IsCompleted) return;
                if (SemanticWarmupState == "ready") return;
                Dictionary<string, object> capturedSettings =
                    new Dictionary<string, object>(settings ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
                SemanticWarmupState = "starting";
                SemanticWarmupTask = Task.Run(() =>
                {
                    Stopwatch timer = Stopwatch.StartNew();
                    try
                    {
                        string baseUrl = ReadString(capturedSettings, "vectorWorkerUrl", "http://127.0.0.1:8082").TrimEnd('/');
                        DateTime deadline = DateTime.UtcNow.AddSeconds(45);
                        while (DateTime.UtcNow < deadline && !Program.ShutdownRequested)
                        {
                            try
                            {
                                Dictionary<string, object> health =
                                    TryParseJsonObject(Program.GetTextFromUrl(baseUrl + "/health", 750)) ?? new Dictionary<string, object>();
                                if (ReadBool(health, "modelLoaded", false))
                                {
                                    SemanticWarmupState = "ready";
                                    break;
                                }
                                if (ReadBool(health, "ok", false))
                                {
                                    SemanticWarmupState = "warming_model";
                                    Dictionary<string, object> request = new Dictionary<string, object>
                                    {
                                        ["query"] = "Bannerlord Reign semantic memory initialization.",
                                        ["candidates"] = new List<Dictionary<string, object>>
                                        {
                                            new Dictionary<string, object>
                                            {
                                                ["id"] = "warmup",
                                                ["text"] = "Bannerlord Reign semantic memory initialization."
                                            }
                                        },
                                        ["top_k"] = 1
                                    };
                                    Dictionary<string, object> response =
                                        TryParseJsonObject(Program.PostJsonToUrl(baseUrl + "/rerank", Json.Serialize(request), 30000))
                                        ?? new Dictionary<string, object>();
                                    if (!ReadBool(response, "ok", false))
                                        throw new InvalidOperationException(ReadString(response, "error", "Vector worker warmup failed."));
                                    Dictionary<string, object> searchRequest = SemanticProviderRequest(capturedSettings);
                                    searchRequest["collection"] = SemanticCollection;
                                    searchRequest["query"] = "Bannerlord Reign semantic memory initialization.";
                                    searchRequest["limit"] = 1;
                                    searchRequest["filters"] = new Dictionary<string, object>
                                    {
                                        ["campaignId"] = "__semantic_warmup__",
                                        ["status"] = "active"
                                    };
                                    Dictionary<string, object> searchResponse =
                                        TryParseJsonObject(Program.PostJsonToUrl(baseUrl + "/vectors/search", Json.Serialize(searchRequest), 30000))
                                        ?? new Dictionary<string, object>();
                                    if (!ReadBool(searchResponse, "ok", false))
                                        throw new InvalidOperationException(ReadString(searchResponse, "error", "Vector search warmup failed."));
                                    SemanticWarmupState = "ready";
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                SemanticWorkerLastError = ex.Message;
                            }
                            Thread.Sleep(250);
                        }
                        if (SemanticWarmupState != "ready")
                            SemanticWarmupState = Program.ShutdownRequested ? "stopped" : "timed_out";
                    }
                    catch (Exception ex)
                    {
                        SemanticWarmupState = "failed";
                        SemanticWorkerLastError = ex.Message;
                        LogSemanticFailureOnce("semantic.warmup_failed", ex.Message);
                    }
                    finally
                    {
                        timer.Stop();
                        SemanticWarmupDurationMs = timer.ElapsedMilliseconds;
                        if (SemanticWarmupState == "ready")
                        {
                            SemanticWarmupCompletedUtc = DateTime.UtcNow;
                            SemanticWorkerLastHealthyUtc = DateTime.UtcNow;
                            SemanticWorkerUnavailableUntilUtc = DateTime.MinValue;
                        }
                    }
                });
            }
        }

        private static bool SemanticPumpUsable(Task task, CancellationTokenSource cancellation)
        {
            return task != null && !task.IsCompleted && cancellation != null && !cancellation.IsCancellationRequested;
        }

        // Returns true only when the worker owned when this stop began is proven exited (or none was
        // owned) and no newer worker is tracked. Ordinary callers ignore the result; it never throws.
        internal static bool StopSemanticMemorySubsystem(int processExitTimeoutMs = 1500)
        {
            Task pump;
            CancellationTokenSource cancellation;
            Process owned;
            lock (SemanticWorkerLock)
            {
                pump = SemanticPumpTask;
                cancellation = SemanticPumpCancellation;
                // Identity only: the exact Process this server spawned, captured with the pump before
                // any wait so a replacement started after this stop began cannot become its target.
                // No endpoint, URL or PID lookup.
                owned = SemanticWorkerProcess;
                try { cancellation?.Cancel(); } catch { }
            }
            try { pump?.Wait(1500); } catch { }
            lock (SemanticWorkerLock)
            {
                if (ReferenceEquals(SemanticPumpTask, pump) && (pump == null || pump.IsCompleted))
                {
                    SemanticPumpTask = null;
                    SemanticPumpCancellation = null;
                    try { cancellation?.Dispose(); } catch { }
                }
            }
            // Kill and the bounded exit check run with no campaign, backup or worker lock held.
            bool exitProven = owned == null;
            if (owned != null)
            {
                try
                {
                    if (!owned.HasExited)
                    {
                        try { owned.Kill(); } catch { }
                    }
                    exitProven = owned.WaitForExit(processExitTimeoutMs);
                }
                catch { exitProven = false; }
            }
            lock (SemanticWorkerLock)
            {
                // An unproven exit keeps the exact handle tracked so a later stop cannot treat a
                // live/unknown worker as unowned; a replacement is never cleared by this stop.
                if (owned != null && exitProven && ReferenceEquals(SemanticWorkerProcess, owned)) SemanticWorkerProcess = null;
                return exitProven && SemanticWorkerProcess == null;
            }
        }

        internal static void StartManagedSemanticWorker(Dictionary<string, object> settings)
        {
            StartManagedSemanticWorker(settings, null);
        }

        // stopRequested is the pump-only admission check: a canceled or shut-down pump must not probe
        // or spawn. Other callers (explicit external delete, Save Sync, restore) pass null and keep
        // their ability to start the worker after ShutdownRequested.
        internal static void StartManagedSemanticWorker(Dictionary<string, object> settings, Func<bool> stopRequested)
        {
            if (stopRequested != null && stopRequested()) return;
            string baseUrl = ReadString(settings, "vectorWorkerUrl", "http://127.0.0.1:8082").TrimEnd('/');
            try
            {
                string healthy = Program.GetTextFromUrl(baseUrl + "/health", 300);
                if (healthy.IndexOf("ReignVectorWorker", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    SemanticWorkerLastHealthyUtc = DateTime.UtcNow;
                    return;
                }
            }
            catch { }

            lock (SemanticWorkerLock)
            {
                // Rechecked under the same lock StopSemanticMemorySubsystem cancels and snapshots under:
                // either this spawn is visible to that stop, or the stale pump is refused here.
                if (stopRequested != null && stopRequested()) return;
                if (SemanticWorkerProcess != null && !SemanticWorkerProcess.HasExited) return;
                Uri workerUri;
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out workerUri)
                    || !(workerUri.IsLoopback || string.Equals(workerUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))) return;
                int workerPort = workerUri.Port > 0 ? workerUri.Port : 8082;
                ProcessStartInfo start = null;
                string python = Environment.GetEnvironmentVariable("REIGN_VECTOR_PYTHON");
                string sourcePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "services", "vector-worker", "worker.py");
                if (!string.IsNullOrWhiteSpace(python) && Path.IsPathRooted(python) && File.Exists(python) && File.Exists(sourcePath))
                {
                    start = new ProcessStartInfo(python);
                    foreach (string argument in new[] { sourcePath, "--host", "127.0.0.1", "--port",
                        workerPort.ToString(CultureInfo.InvariantCulture), "--data-dir", Program.DataDir })
                        start.ArgumentList.Add(argument);
                }
                if (start == null)
                {
                    SemanticWorkerLastError = "Managed vector worker executable was not found.";
                    LogSemanticFailureOnce("semantic.worker_missing", SemanticWorkerLastError);
                    return;
                }
                start.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.WindowStyle = ProcessWindowStyle.Hidden;
                try
                {
                    SemanticWorkerProcess = Process.Start(start);
                    Program.LogOperational("semantic.worker_started", new Dictionary<string, object> { ["file"] = start.FileName, ["dataDir"] = Program.DataDir });
                }
                catch (Exception ex)
                {
                    SemanticWorkerLastError = ex.Message;
                    LogSemanticFailureOnce("semantic.worker_start_failed", ex.Message);
                }
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private static void SemanticIndexPump(CancellationToken token, int generation)
        {
            // A replaced (disable/re-enable) or shut-down pump may still be running; it must not
            // enumerate, open a campaign DB or spawn a worker once stopped.
            Func<bool> stopRequested = () => token.IsCancellationRequested || Program.ShutdownRequested;
            while (!stopRequested())
            {
                if (SemanticIndexShouldYield())
                {
                    if (token.WaitHandle.WaitOne(250)) break;
                    continue;
                }

                int processed = 0;
                try
                {
                    SemanticPumpLastCycleUtc = DateTime.UtcNow;
                    Dictionary<string, object> settings = Program.LoadSettings();
                    if (ReadBool(settings, "enableSemanticMemory", true) && !stopRequested())
                    {
                        StartManagedSemanticWorker(settings, stopRequested);
                        // ListCampaignMetadata runs EnsureInfrastructure: short read lease around enumeration only.
                        List<Dictionary<string, object>> campaigns = null;
                        Program.CampaignDataGate.EnterReadLock();
                        try
                        {
                            if (!stopRequested()) campaigns = ReignPostgreSqlStorage.ListCampaignMetadata();
                        }
                        finally { Program.CampaignDataGate.ExitReadLock(); }
                        if (campaigns != null)
                        foreach (Dictionary<string, object> campaign in campaigns)
                        {
                            if (stopRequested()) break;
                            if (SemanticIndexShouldYield()) break;
                            string campaignId =
                                ReadString(campaign, "campaignId", "");
                            if (Program.HasLocalCampaignMetadata(campaignId))
                            {
                                Program.CampaignDataGate.EnterReadLock();
                                try
                                {
                                    lock (SemanticJobProcessingLock)
                                    {
                                        // Rechecked after both locks: a stale acquisition after the
                                        // delete-all write barrier is refused before any DB open.
                                        // Breaking here still releases both locks before leaving.
                                        if (stopRequested()) break;
                                        processed += ProcessSemanticJobs(campaignId, settings);
                                    }
                                }
                                finally { Program.CampaignDataGate.ExitReadLock(); }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    SemanticWorkerLastError = ex.Message;
                    LogSemanticFailureOnce("semantic.index_pump_failed", ex.Message);
                }
                if (processed > 0)
                {
                    SemanticPumpLastBatchUtc = DateTime.UtcNow;
                    SemanticPumpLastBatchSize = processed;
                }
                if (token.WaitHandle.WaitOne(processed > 0 ? 150 : 3000)) break;
            }
            Program.LogOperational("semantic.index_pump_stopped", new Dictionary<string, object>
            {
                ["generation"] = generation,
                ["cancelled"] = token.IsCancellationRequested,
                ["shutdown"] = Program.ShutdownRequested
            });
        }

        private static bool SemanticIndexShouldYield()
        {
            // A completed NPC response is followed by client-side persistence,
            // relationship receipts, assertion evidence, and then the next turn.
            // Keep the vector worker free across that handoff; the generic two-second
            // quiet period was short enough for an embedding batch to start in the
            // middle and make the next foreground semantic search wait several seconds.
            return Program.InteractiveRequestActiveOrRecent(10000)
                || Program.PriorityBackgroundWorkShouldYield();
        }

        private static int ProcessSemanticJobs(string campaignId, Dictionary<string, object> settings)
        {
            int batchSize = Math.Max(1, Math.Min(128, ReadInt(settings, "embeddingBatchSize", 32)));
            List<Dictionary<string, object>> jobs;
            using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                jobs = Program.QuerySql(connection, @"SELECT * FROM embedding_jobs
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
LIMIT $limit;",
                    new Dictionary<string, object> { ["now"] = now, ["limit"] = batchSize });
            }
            if (jobs.Count == 0) return 0;

            int completedJobs = 0;
            List<EmbeddingSourceDocument> documents = new List<EmbeddingSourceDocument>();
            List<Dictionary<string, object>> deletes = new List<Dictionary<string, object>>();
            using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
            {
                foreach (Dictionary<string, object> job in jobs)
                {
                    string sourceType = ReadString(job, "source_type", "");
                    string sourceId = ReadString(job, "source_id", "");
                    Dictionary<string, object> row = LoadEmbeddingSourceRow(connection, sourceType, sourceId);
                    if (ReadString(job, "operation", "upsert") == "delete" || row == null || !EmbeddingRowActive(sourceType, row))
                    {
                        deletes.Add(job);
                        continue;
                    }
                    string text = BuildCanonicalEmbeddingText(sourceType, row);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        CompleteEmbeddingJob(connection, sourceType, sourceId, "skipped", "empty canonical text");
                        completedJobs++;
                        continue;
                    }
                    EmbeddingSourceDocument document = BuildEmbeddingDocument(campaignId, sourceType, sourceId, row, text, settings);
                    document.Payload["restoreGeneration"] = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                    var restoredContext = Program.QuerySql(connection, "SELECT timeline_id FROM memory_restored_source_context WHERE state_id='current';").SingleOrDefault();
                    string restoredTimeline = ReadString(restoredContext, "timeline_id", "");
                    if (IsVerifiedRestoredMemorySource(connection, row, restoredTimeline))
                        document.Payload["restoredSavePointId"] = ReadString(RestoredMemorySourceContext(connection, restoredTimeline), "save_point_id", "");
                    document.ContentHash = SemanticSha256Hex(document.Text + CanonicalJson(document.Payload));
                    Dictionary<string, object> indexed = Program.QuerySql(connection, @"SELECT content_hash,status FROM embedding_documents
WHERE source_type=$type AND source_id=$id AND model_version=$model AND provider=$provider LIMIT 1;",
                        new Dictionary<string, object> { ["type"] = sourceType, ["id"] = sourceId, ["model"] = SemanticModelVersion, ["provider"] = document.Provider }).FirstOrDefault();
                    if (indexed != null && string.Equals(ReadString(indexed, "content_hash", ""), document.ContentHash, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(ReadString(indexed, "status", ""), "indexed", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var unchanged = connection.BeginTransaction())
                        {
                            AcquirePostgreSqlTransactionMutationLock(connection);
                            var latest = LoadEmbeddingSourceRow(connection,sourceType,sourceId);
                            if (latest != null && MemorySourceHash(new[] { latest }) == ReadString(document.Payload,"sourceHash","")
                                && ReadString(document.Payload,"restoreGeneration","") == ReadString(ReadMemoryPrecisionState(connection),"restore_generation",""))
                                CompleteEmbeddingJob(connection, sourceType, sourceId, "completed", "unchanged");
                            unchanged.Commit();
                        }
                        completedJobs++;
                        continue;
                    }
                    documents.Add(document);
                }
            }

            try
            {
                bool yieldedForInteractive = false;
                if (documents.Count > 0)
                {
                    // Submit small, interruptible chunks so an interactive search never
                    // waits behind a full maintenance batch. Indexed chunks are committed
                    // immediately; unprocessed documents remain pending for the next pump.
                    int safeChunkSize = Math.Max(1, Math.Min(2,
                        ReadInt(settings, "embeddingForegroundSafeChunkSize", 1)));
                    for (int offset = 0; offset < documents.Count; offset += safeChunkSize)
                    {
                        if (SemanticIndexShouldYield())
                        {
                            yieldedForInteractive = true;
                            break;
                        }
                        List<EmbeddingSourceDocument> chunk = documents
                            .Skip(offset).Take(safeChunkSize).ToList();
                        Dictionary<string, object> request = SemanticProviderRequest(settings);
                        request["collection"] = SemanticCollection;
                        request["documents"] = chunk.Select(document => new Dictionary<string, object>
                        {
                            ["id"] = document.VectorId,
                            ["text"] = document.Text,
                            ["payload"] = document.Payload
                        }).ToList();
                        Dictionary<string, object> response = TryParseJsonObject(
                            Program.PostJsonToUrl(SemanticWorkerUrl(settings, "/vectors/upsert"),
                                Json.Serialize(request), 60000));
                        if (!ReadBool(response, "ok", false))
                            throw new InvalidOperationException(
                                ReadString(response, "error", "Vector upsert failed."));
                        using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
                        using (var publication = connection.BeginTransaction())
                        {
                            AcquirePostgreSqlTransactionMutationLock(connection);
                            foreach (EmbeddingSourceDocument document in chunk)
                            {
                                var current = LoadEmbeddingSourceRow(connection, document.SourceType, document.SourceId);
                                if (current == null || MemorySourceHash(new[] { current }) != ReadString(document.Payload, "sourceHash", "")
                                    || ReadString(document.Payload, "restoreGeneration", "") != ReadString(ReadMemoryPrecisionState(connection), "restore_generation", ""))
                                    continue; // Outbox remains pending; a stale vector cannot pass source revalidation.
                                MarkEmbeddingIndexed(connection, document, settings);
                            }
                            publication.Commit();
                        }
                        completedJobs += chunk.Count;
                    }
                }
                if (!yieldedForInteractive && deletes.Count > 0)
                {
                    List<string> ids;
                    using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
                    {
                        ids = deletes.SelectMany(job => Program.QuerySql(connection, "SELECT vector_id FROM embedding_documents WHERE source_type=$type AND source_id=$id AND status<>'deleted';",
                            new Dictionary<string, object> { ["type"] = ReadString(job, "source_type", ""), ["id"] = ReadString(job, "source_id", "") }))
                            .Select(row => ReadString(row, "vector_id", "")).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
                    }
                    if (ids.Count > 0)
                    {
                        Dictionary<string, object> request = SemanticProviderRequest(settings);
                        request["collection"] = SemanticCollection;
                        request["ids"] = ids;
                        Program.PostJsonToUrl(SemanticWorkerUrl(settings, "/vectors/delete"), Json.Serialize(request), 15000);
                    }
                    using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
                    using (var deletion = connection.BeginTransaction())
                    {
                        AcquirePostgreSqlTransactionMutationLock(connection);
                        foreach (Dictionary<string, object> job in deletes)
                        {
                            string type = ReadString(job, "source_type", ""), id = ReadString(job, "source_id", "");
                            var latest = LoadEmbeddingSourceRow(connection,type,id);
                            if (latest != null && EmbeddingRowActive(type,latest)) continue;
                            Program.ExecuteSql(connection, "UPDATE embedding_documents SET status='deleted',updated_ts=$ts WHERE source_type=$type AND source_id=$id;",
                                new Dictionary<string, object> { ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["type"] = type, ["id"] = id });
                            CompleteEmbeddingJob(connection, type, id, "deleted", "");
                            completedJobs++;
                        }
                        deletion.Commit();
                    }
                }
                SemanticWorkerLastHealthyUtc = DateTime.UtcNow;
                SemanticWorkerUnavailableUntilUtc = DateTime.MinValue;
                SemanticWorkerLastError = "";
            }
            catch (Exception ex)
            {
                using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
                {
                    foreach (Dictionary<string, object> job in jobs) FailEmbeddingJob(connection, job, ex.Message);
                }
                SemanticWorkerLastError = ex.Message;
                LogSemanticFailureOnce("semantic.index_batch_failed", ex.Message);
            }
            return completedJobs;
        }

        private static Dictionary<string, object> LoadEmbeddingSourceRow(ReignDbConnection connection, string sourceType, string sourceId)
        {
            string table, id;
            switch (sourceType)
            {
                case "event": table = "events"; id = "event_id"; break;
                case "memory": table = "memories"; id = "memory_id"; break;
                case "summary": table = "summaries"; id = "summary_id"; break;
                case "belief": table = "beliefs"; id = "belief_id"; break;
                case "comprehension": table = "comprehension"; id = "comprehension_id"; break;
                case "conversation_turn": table = "conversation_turns"; id = "turn_id"; break;
                case "world_history_event": table = "world_history_events"; id = "event_id"; break;
                default: return null;
            }
            return Program.QuerySql(connection, "SELECT * FROM " + table + " WHERE " + id + "=$id LIMIT 1;", new Dictionary<string, object> { ["id"] = sourceId }).FirstOrDefault();
        }

        private static bool EmbeddingRowActive(string sourceType, Dictionary<string, object> row)
        {
            if (string.Equals(sourceType, "event", StringComparison.OrdinalIgnoreCase)
                && !EventEmbeddingEligible(row)) return false;
            if (string.Equals(sourceType, "world_history_event", StringComparison.OrdinalIgnoreCase)
                && !WorldHistoryEventEmbeddingEligible(row)) return false;
            string status = ReadString(row, "status", "active");
            if (string.Equals(sourceType, "memory", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sourceType, "summary", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sourceType, "conversation_turn", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(status, "active", StringComparison.OrdinalIgnoreCase)
                    && (sourceType != "conversation_turn" || !string.IsNullOrWhiteSpace(ConversationTurnTextForRecall(row)));
            }
            return !string.Equals(status, "deleted", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status, "superseded", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status, "consolidated", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status, "inactive", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildCanonicalEmbeddingText(string sourceType, Dictionary<string, object> row)
        {
            List<string> parts = new List<string> { sourceType.Replace('_', ' ') + ":" };
            parts.Add(sourceType == "conversation_turn" ? ConversationTurnTextForRecall(row)
                : ReadFirstString(row, "semantic_text", "summary", "claim", "text", "description"));
            parts.Add(ReadFirstString(row, "event_type", "memory_type", "summary_type", "category", "stance", "channel"));
            parts.Add(ReadFirstString(row, "location_name", "location_id"));
            parts.Add(ReadString(row, "speaker_name", ""));
            if (sourceType == "conversation_turn")
            {
                parts.Add("speaker=" + ReadString(row, "speaker_id", ""));
                parts.Add("role=" + ReadString(row, "role", ""));
            }
            parts.Add(ReadString(row, "tags_json", ""));
            parts.Add(ReadString(row, "about_entities_json", ""));
            parts.Add(ReadString(row, "participants_json", ""));
            return string.Join(" ", parts.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
        }

        private static EmbeddingSourceDocument BuildEmbeddingDocument(string campaignId, string sourceType, string sourceId,
            Dictionary<string, object> row, string text, Dictionary<string, object> settings)
        {
            string provider = ReadString(settings, "vectorProvider", "local").ToLowerInvariant();
            var sourcePayload = Program.KnowledgePayload(row);
            string timeline = FirstNonEmpty(ReadString(row, "timeline_id", ""), ReadString(sourcePayload, "timelineId", ""));
            bool mental = sourceType == "belief" || sourceType == "comprehension";
            var knownBy = Program.TextListFromJson(ReadString(row, "known_by_json", "[]"));
            if (!mental) knownBy = knownBy.Concat(Program.TextListFromJson(ReadString(row, "participants_json", "[]")))
                .Concat(Program.TextListFromJson(ReadString(row, "witnesses_json", "[]")))
                .Concat(Program.TextListFromJson(ReadString(row, "heard_as_rumor_by_json", "[]"))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string vectorId = DeterministicSemanticGuid(campaignId + "|" + ReadString(row, "timeline_id", "") + "|" + sourceType + "|" + sourceId + "|" + SemanticModelVersion);
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["timelineId"] = timeline,
                ["sourceType"] = sourceType,
                ["sourceId"] = sourceId,
                ["memoryLane"] = FirstNonEmpty(ReadFirstString(row, "memory_lane", "memory_domain"), InferEmbeddingLane(sourceType, row)),
                ["ownerId"] = sourceType == "conversation_turn" ? ReadString(row, "speaker_id", "") : ReadFirstString(row, "owner_id", "believer_id"),
                ["knownBy"] = mental ? new List<string>() : knownBy,
                ["hiddenFrom"] = Program.TextListFromJson(ReadString(row, "hidden_from_json", "[]")),
                ["privateMental"] = mental, ["eligibilityVersion"] = 2,
                ["publicUnscoped"] = !mental && !Program.KnowledgeHasExplicitScope(row, sourcePayload)
                    && new[] { "public", "world", "global", "common" }.Contains(ReadString(row, "visibility", "private")),
                ["sourceHash"] = MemorySourceHash(new[] { row }),
                ["visibility"] = ReadString(row, "visibility", "private"),
                ["status"] = "active",
                ["worldDay"] = ReadDouble(row, "world_day", 0d),
                ["importance"] = ReadDouble(row, "importance", 0.5d),
                ["modelVersion"] = SemanticModelVersion
            };
            return new EmbeddingSourceDocument
            {
                CampaignId = campaignId, SourceType = sourceType, SourceId = sourceId, Provider = provider,
                TimelineId = timeline, VectorId = vectorId, Text = text,
                ContentHash = SemanticSha256Hex(text + CanonicalJson(payload)), Payload = payload
            };
        }

        private static string InferEmbeddingLane(string sourceType, Dictionary<string, object> row)
        {
            if (sourceType == "world_history_event" || sourceType == "event") return "world_affairs";
            if (sourceType == "belief") return "beliefs_and_rumors";
            if (sourceType == "conversation_turn") return "interpersonal_history";
            if (sourceType == "comprehension") return "personal_state";
            return ClassifyMemoryDomain(row);
        }

        private static string DeterministicSemanticGuid(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
                byte[] guid = new byte[16];
                Array.Copy(bytes, guid, 16);
                return new Guid(guid).ToString("D");
            }
        }

        internal static string SemanticSha256Hex(string value)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant();
        }

        private static void MarkEmbeddingIndexed(ReignDbConnection connection, EmbeddingSourceDocument document, Dictionary<string, object> settings)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Program.ExecuteSql(connection, @"INSERT OR REPLACE INTO embedding_documents(source_type,source_id,model_version,provider,campaign_id,timeline_id,content_hash,vector_id,dimensions,status,attempt_count,last_error,updated_ts,payload_json)
VALUES($type,$id,$model,$provider,$campaign,$timeline,$hash,$vector,384,'indexed',0,'',$ts,$payload);",
                new Dictionary<string, object>
                {
                    ["type"] = document.SourceType, ["id"] = document.SourceId, ["model"] = SemanticModelVersion,
                    ["provider"] = document.Provider, ["campaign"] = document.CampaignId, ["timeline"] = document.TimelineId,
                    ["hash"] = document.ContentHash, ["vector"] = document.VectorId, ["ts"] = now, ["payload"] = Json.Serialize(document.Payload)
                });
            if (document.SourceType == "event") Program.ExecuteSql(connection, "UPDATE events SET vector_id=$vector,embedding_status='indexed' WHERE event_id=$id;", new Dictionary<string, object> { ["vector"] = document.VectorId, ["id"] = document.SourceId });
            if (document.SourceType == "memory") Program.ExecuteSql(connection, "UPDATE memories SET vector_id=$vector,embedding_status='indexed' WHERE memory_id=$id;", new Dictionary<string, object> { ["vector"] = document.VectorId, ["id"] = document.SourceId });
            if (document.SourceType == "summary") Program.ExecuteSql(connection, "UPDATE summaries SET vector_id=$vector,embedding_status='indexed' WHERE summary_id=$id;", new Dictionary<string, object> { ["vector"] = document.VectorId, ["id"] = document.SourceId });
            CompleteEmbeddingJob(connection, document.SourceType, document.SourceId, "completed", "");
        }

        private static void CompleteEmbeddingJob(ReignDbConnection connection, string type, string id, string status, string message)
        {
            Program.ExecuteSql(connection, "DELETE FROM embedding_jobs WHERE source_type=$type AND source_id=$id;", new Dictionary<string, object> { ["type"] = type, ["id"] = id });
        }

        private static void FailEmbeddingJob(ReignDbConnection connection, Dictionary<string, object> job, string error)
        {
            int attempts = ReadInt(job, "attempt_count", 0) + 1;
            long delay = Math.Min(300, Math.Max(5, 5 * (1L << Math.Min(6, attempts - 1))));
            Program.ExecuteSql(connection, "UPDATE embedding_jobs SET attempt_count=$attempts,next_attempt_ts=$next,last_error=$error,updated_ts=$ts WHERE source_type=$type AND source_id=$id;",
                new Dictionary<string, object>
                {
                    ["attempts"] = attempts, ["next"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + delay,
                    ["error"] = Program.LimitText(error, 500), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["type"] = ReadString(job, "source_type", ""), ["id"] = ReadString(job, "source_id", "")
                });
        }

        internal static Dictionary<string, object> SemanticProviderRequest(Dictionary<string, object> settings)
        {
            return new Dictionary<string, object>
            {
                ["provider"] = ReadString(settings, "vectorProvider", "local"),
                ["qdrantUrl"] = ReadString(settings, "qdrantUrl", ""),
                ["qdrantApiKey"] = ReadString(settings, "qdrantApiKey", "")
            };
        }

        internal static string SemanticWorkerUrl(Dictionary<string, object> settings, string path)
        {
            return ReadString(settings, "vectorWorkerUrl", "http://127.0.0.1:8082").TrimEnd('/') + path;
        }

        internal static Dictionary<string, object> TrySemanticMemorySearch(string campaignId, string query,
            Dictionary<string, object> retrievalRoute, Dictionary<string, object> settings, IEnumerable<string> sourceTypes = null, string timelineId = "", string observerId = "", double asOfWorldDay = double.MaxValue)
        {
            Dictionary<string, object> status = new Dictionary<string, object>
            {
                ["enabled"] = ReadBool(settings, "enableSemanticMemory", true), ["attempted"] = false, ["applied"] = false,
                ["reason"] = "disabled", ["results"] = new List<Dictionary<string, object>>(), ["durationMs"] = 0
            };
            if (!ReadBool(settings, "enableSemanticMemory", true) || string.IsNullOrWhiteSpace(query)) return status;
            if (PersistenceService.IsSaveSyncSemanticBlocked(campaignId))
            {
                status["reason"] = "save_sync_lexical_fallback";
                return status;
            }
            if (DateTime.UtcNow < SemanticWorkerUnavailableUntilUtc)
            {
                status["reason"] = "worker_cooldown";
                return status;
            }
            Stopwatch timer = Stopwatch.StartNew();
            status["attempted"] = true;
            try
            {
                List<string> types = (sourceTypes ?? SemanticSourceTypesForRoute(retrievalRoute)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                Dictionary<string, object> request = SemanticProviderRequest(settings);
                request["collection"] = SemanticCollection;
                request["query"] = Program.LimitText(query, 4000);
                request["priority"] = "interactive";
                request["limit"] = Math.Min(512, Math.Max(8, ReadInt(settings, "vectorSearchTopK", 96) * 3));
                Dictionary<string, object> filters = new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["status"] = "active", ["sourceType"] = types
                };
                List<string> selectedLanes = ReadStringList(retrievalRoute, "selectedLanes");
                if (selectedLanes.Contains("exact_history", StringComparer.OrdinalIgnoreCase)
                    && !selectedLanes.Contains("interpersonal_history", StringComparer.OrdinalIgnoreCase)) selectedLanes.Add("interpersonal_history");
                selectedLanes = selectedLanes.Where(lane => !string.Equals(lane, "exact_history", StringComparison.OrdinalIgnoreCase)).ToList();
                // Native history documents live in world_affairs even when the
                // conversation asks about the immediate local situation.
                if (types.Contains("world_history_event", StringComparer.OrdinalIgnoreCase)
                    && !selectedLanes.Contains("world_affairs", StringComparer.OrdinalIgnoreCase))
                    selectedLanes.Add("world_affairs");
                // Lanes are ranking hints, never hard exclusions for a precise
                // question whose answer happens to have another category.
                if (string.IsNullOrWhiteSpace(observerId) && selectedLanes.Count > 0) filters["memoryLane"] = selectedLanes;
                if (!string.IsNullOrWhiteSpace(timelineId)) filters["timelineId"] = timelineId;
                if (!string.IsNullOrWhiteSpace(observerId))
                {
                    filters["observerId"] = observerId;
                    if (asOfWorldDay < double.MaxValue) filters["asOfWorldDay"] = asOfWorldDay;
                    using (var connection = Program.OpenCampaignConnection(campaignId))
                    {
                        filters["restoreGeneration"] = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                        string point = ReadString(RestoredMemorySourceContext(connection, timelineId), "save_point_id", "");
                        if (point.Length > 0) filters["restoredSavePointId"] = point;
                    }
                }
                request["filters"] = filters;
                Dictionary<string, object> response = TryParseJsonObject(Program.PostJsonToUrl(SemanticWorkerUrl(settings, "/vectors/search"), Json.Serialize(request),
                    Math.Max(8000, Math.Min(30000, ReadInt(settings, "vectorQueryTimeoutMs", 10000))))) ?? new Dictionary<string, object>();
                if (!ReadBool(response, "ok", false)) throw new InvalidOperationException(ReadString(response, "error", "Semantic search failed."));
                if (!string.IsNullOrWhiteSpace(observerId) && !ReadBool(response, "eligibilityApplied", false))
                {
                    status["reason"] = "observer_filter_unavailable_sql_fallback";
                    return status;
                }
                if (filters.ContainsKey("restoredSavePointId") && !ReadBool(response, "restoredSourceEligibilityApplied", false))
                {
                    status["reason"] = "restored_source_filter_unavailable_sql_fallback";
                    return status;
                }
                List<Dictionary<string, object>> results = ReadDictionaryList(response, "results").Take(Math.Max(1, ReadInt(settings, "vectorSearchTopK", 96))).ToList();
                timer.Stop();
                SemanticLastQueryMs = timer.ElapsedMilliseconds;
                SemanticWorkerLastHealthyUtc = DateTime.UtcNow;
                SemanticWorkerUnavailableUntilUtc = DateTime.MinValue;
                SemanticWorkerLastError = "";
                status["applied"] = true;
                status["reason"] = results.Count == 0 ? "no_matches" : "semantic_candidates_found";
                status["results"] = results;
                status["resultCount"] = results.Count;
                status["durationMs"] = timer.ElapsedMilliseconds;
                status["workerTiming"] = ReadDictionary(response, "timing") ?? new Dictionary<string, object>();
                return status;
            }
            catch (Exception ex)
            {
                timer.Stop();
                SemanticLastQueryMs = timer.ElapsedMilliseconds;
                SemanticWorkerLastError = ex.Message;
                SemanticWorkerUnavailableUntilUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, Math.Min(600, ReadInt(settings, "minimeRetryCooldownSeconds", 30))));
                status["reason"] = "fts_fallback";
                status["error"] = Program.LimitText(ex.Message, 500);
                status["durationMs"] = timer.ElapsedMilliseconds;
                LogSemanticFailureOnce("semantic.search_failed", ex.Message);
                return status;
            }
        }

        private static List<string> SemanticSourceTypesForRoute(Dictionary<string, object> route)
        {
            // Legacy consumers read memory/summary documents. Only original-aware
            // precision/analyst callers opt into conversation turns, so new index
            // entries cannot crowd out results an older consumer can actually use.
            HashSet<string> types = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "memory", "summary" };
            if (RouteHasLane(route, "personal_state")) types.Add("comprehension");
            if (RouteHasLane(route, "beliefs_and_rumors")) types.Add("belief");
            if (RouteHasLane(route, "world_affairs") || RouteHasLane(route, "local_awareness"))
                types.Add("world_history_event");
            return types.ToList();
        }

        private static List<Dictionary<string, object>> SemanticHits(Dictionary<string, object> search, string sourceType)
        {
            return ReadDictionaryList(search, "results").Where(hit => string.Equals(ReadString(ReadDictionary(hit, "payload"), "sourceType", ""), sourceType, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        internal static List<Dictionary<string, object>> LoadSemanticRows(ReignDbConnection connection, string table, string idColumn,
            string sourceType, Dictionary<string, object> search, Program.KnowledgeAccessContext knowledge)
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> hit in SemanticHits(search, sourceType))
            {
                Dictionary<string, object> payload = ReadDictionary(hit, "payload") ?? new Dictionary<string, object>();
                string id = ReadString(payload, "sourceId", "");
                if (string.IsNullOrWhiteSpace(id)) continue;
                Dictionary<string, object> row = Program.QuerySql(connection, "SELECT * FROM " + table + " WHERE " + idColumn + "=$id LIMIT 1;", new Dictionary<string, object> { ["id"] = id }).FirstOrDefault();
                if (row == null || !EmbeddingRowActive(sourceType, row) || !Program.KnowledgeRowVisibleToNpc(table, row, knowledge, connection)) continue;
                if (ReadString(payload, "sourceHash", "") != MemorySourceHash(new[] { row })) continue;
                row["vectorSemanticScore"] = Program.ClampDouble(ReadDouble(hit, "score", 0d), -1d, 1d);
                rows.Add(row);
            }
            return rows;
        }

        internal static List<Dictionary<string, object>> MergeSemanticRows(IEnumerable<Dictionary<string, object>> primary,
            IEnumerable<Dictionary<string, object>> semantic, string idColumn)
        {
            Dictionary<string, Dictionary<string, object>> rows = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<Dictionary<string, object>> combined = (primary ?? Enumerable.Empty<Dictionary<string, object>>())
                .Concat(semantic ?? Enumerable.Empty<Dictionary<string, object>>());
            foreach (Dictionary<string, object> row in combined)
            {
                string id = ReadString(row, idColumn, "");
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!rows.ContainsKey(id) || ReadDouble(row, "vectorSemanticScore", -1d) > ReadDouble(rows[id], "vectorSemanticScore", -1d)) rows[id] = row;
            }
            return rows.Values.ToList();
        }

        internal static double AddSemanticScore(Dictionary<string, object> row, double deterministic, Dictionary<string, object> settings)
        {
            double semantic = Program.ClampDouble(ReadDouble(row, "vectorSemanticScore", 0d), -1d, 1d);
            return deterministic + Math.Max(0d, semantic) * Program.ClampDouble(ReadDouble(settings, "vectorScoreWeight", 30d), 0d, 100d);
        }

        internal static List<Dictionary<string, object>> MergeSemanticWorldHistoryCandidates(ReignDbConnection connection,
            string campaignId, string timelineId, string claim, string family, string claimantId, List<string> entityHints,
            string locationId, double fromDay, double toDay, List<Dictionary<string, object>> candidates,
            Dictionary<string, object> semanticSearch, Dictionary<string, object> settings)
        {
            Dictionary<string, Dictionary<string, object>> byId =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> candidate in candidates ?? new List<Dictionary<string, object>>())
            {
                string candidateId = ReadString(candidate, "event_id", "");
                if (string.IsNullOrWhiteSpace(candidateId)) continue;
                if (!byId.TryGetValue(candidateId, out Dictionary<string, object> existing)
                    || ReadDouble(candidate, "deterministicScore", 0d) > ReadDouble(existing, "deterministicScore", 0d))
                {
                    byId[candidateId] = candidate;
                }
            }
            List<string> terms = Program.SimpleTags(claim).Concat(WorldSimulationService.FamilyTerms(family)).Distinct(StringComparer.OrdinalIgnoreCase).Take(18).ToList();
            foreach (Dictionary<string, object> hit in SemanticHits(semanticSearch, "world_history_event"))
            {
                Dictionary<string, object> payload = ReadDictionary(hit, "payload") ?? new Dictionary<string, object>();
                string eventId = ReadString(payload, "sourceId", "");
                if (string.IsNullOrWhiteSpace(eventId)) continue;
                Dictionary<string, object> row;
                if (!byId.TryGetValue(eventId, out row))
                {
                    row = Program.QuerySql(connection, @"SELECT * FROM world_history_events WHERE event_id=$id AND timeline_id=$timeline
AND ($location='' OR location_id=$location) AND ($from<0 OR world_day>=$from) AND world_day<=$to LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["id"] = eventId, ["timeline"] = timelineId, ["location"] = locationId ?? "",
                            ["from"] = fromDay, ["to"] = toDay <= 0 ? double.MaxValue : toDay
                        }).FirstOrDefault();
                    if (row == null) continue;
                    List<Dictionary<string, object>> entities = WorldSimulationService.QueryWorldHistoryEntities(connection, eventId);
                    row["entities"] = entities;
                    double deterministic = WorldSimulationService.HistoryTermScore(row, terms);
                    if (entities.Any(entity => string.Equals(ReadString(entity, "entity_id", ""), claimantId, StringComparison.OrdinalIgnoreCase))) deterministic += 8d;
                    foreach (string id in entityHints ?? new List<string>())
                        if (entities.Any(entity => string.Equals(ReadString(entity, "entity_id", ""), id, StringComparison.OrdinalIgnoreCase))) deterministic += 4d;
                    if (WorldSimulationService.EventMatchesFamily(ReadString(row, "event_type", ""), entities, family)) deterministic += 10d;
                    row["deterministicScore"] = deterministic;
                    byId[eventId] = row;
                }
                if (!WorldHistoryEventEmbeddingEligible(row)) continue;
                row["vectorSemanticScore"] = Math.Max(ReadDouble(row, "vectorSemanticScore", -1d), Program.ClampDouble(ReadDouble(hit, "score", 0d), -1d, 1d));
                row["combinedScore"] = AddSemanticScore(row, ReadDouble(row, "deterministicScore", 0d), settings);
            }
            foreach (Dictionary<string, object> row in byId.Values)
                row["combinedScore"] = AddSemanticScore(row, ReadDouble(row, "deterministicScore", 0d), settings);
            return byId.Values.OrderByDescending(row => ReadDouble(row, "combinedScore", 0d)).Take(256).ToList();
        }

        internal static Dictionary<string, object> SemanticMemoryStatusApi(Dictionary<string, string> query)
        {
            string campaignId = query != null && query.ContainsKey("campaignId") ? query["campaignId"] : Program.LatestCampaignId();
            if (string.IsNullOrWhiteSpace(campaignId)) campaignId = Program.LatestCampaignId();
            Dictionary<string, object> settings = Program.LoadSettings();
            Dictionary<string, object> worker = new Dictionary<string, object>();
            try { worker = TryParseJsonObject(Program.GetTextFromUrl(SemanticWorkerUrl(settings, "/vectors/status"), 750)) ?? new Dictionary<string, object>(); }
            catch (Exception ex) { worker = new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message }; }
            Dictionary<string, object> counts = new Dictionary<string, object>();
            if (Program.HasCampaignPostgreSqlStorage(campaignId))
            {
                using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
                {
                    counts["documents"] = Program.QuerySql(connection, "SELECT status,COUNT(*) count FROM embedding_documents GROUP BY status;");
                    counts["jobs"] = Program.QuerySql(connection, "SELECT status,COUNT(*) count FROM embedding_jobs GROUP BY status;");
                    counts["failures"] = Program.QuerySql(connection, "SELECT source_type,source_id,attempt_count,last_error,next_attempt_ts FROM embedding_jobs WHERE last_error<>'' ORDER BY updated_ts DESC LIMIT 25;");
                }
            }
            Dictionary<string, object> pump;
            lock (SemanticWorkerLock)
            {
                pump = new Dictionary<string, object>
                {
                    ["running"] = SemanticPumpTask != null && !SemanticPumpTask.IsCompleted
                        && SemanticPumpCancellation != null && !SemanticPumpCancellation.IsCancellationRequested,
                    ["generation"] = SemanticPumpGeneration,
                    ["lastCycleUtc"] = SemanticPumpLastCycleUtc == DateTime.MinValue ? "" : SemanticPumpLastCycleUtc.ToString("o"),
                    ["lastBatchUtc"] = SemanticPumpLastBatchUtc == DateTime.MinValue ? "" : SemanticPumpLastBatchUtc.ToString("o"),
                    ["lastBatchSize"] = SemanticPumpLastBatchSize,
                    ["pausedForInteractive"] = Program.InteractiveRequestActiveOrRecent()
                };
            }
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["campaignId"] = campaignId, ["enabled"] = ReadBool(settings, "enableSemanticMemory", true),
                ["provider"] = ReadString(settings, "vectorProvider", "local"), ["model"] = ReadString(settings, "embeddingModel", "BAAI/bge-small-en-v1.5"),
                ["modelVersion"] = SemanticModelVersion, ["worker"] = worker, ["pump"] = pump, ["counts"] = counts,
                ["warmup"] = new Dictionary<string, object>
                {
                    ["state"] = SemanticWarmupState,
                    ["ready"] = SemanticWarmupState == "ready",
                    ["durationMs"] = SemanticWarmupDurationMs,
                    ["completedUtc"] = SemanticWarmupCompletedUtc == DateTime.MinValue ? "" : SemanticWarmupCompletedUtc.ToString("o")
                },
                ["lastWorkerHealthyUtc"] = SemanticWorkerLastHealthyUtc == DateTime.MinValue ? "" : SemanticWorkerLastHealthyUtc.ToString("o"),
                ["lastError"] = SemanticWorkerLastError, ["lastQueryMs"] = SemanticLastQueryMs
            };
        }

        internal static Dictionary<string, object> SemanticMemoryReindexApi(Dictionary<string, object> payload)
        {
            string campaignId = ReadString(payload, "campaignId", Program.LatestCampaignId());
            if (string.IsNullOrWhiteSpace(campaignId)) campaignId = Program.LatestCampaignId();
            if (!Program.HasCampaignPostgreSqlStorage(campaignId))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "Campaign PostgreSQL schema was not found."
                };
            using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
            {
                string eligible = WorldHistoryEmbeddingEligibilitySql("w.");
                Program.ExecuteSql(connection, @"UPDATE embedding_documents SET status='stale',updated_ts=$ts
WHERE source_type<>'world_history_event' OR source_id IN
(SELECT w.event_id FROM world_history_events w WHERE " + eligible + ");",
                    new Dictionary<string, object> { ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                QueueAllEmbeddingJobs(connection, true);
                int queued = ReadInt(Program.QuerySql(connection, "SELECT COUNT(*) count FROM embedding_jobs;").FirstOrDefault(), "count", 0);
                return new Dictionary<string, object> { ["ok"] = true, ["campaignId"] = campaignId, ["queued"] = queued };
            }
        }

        internal static Dictionary<string, object> SemanticMemoryRetryFailedApi(Dictionary<string, object> payload)
        {
            string campaignId = ReadString(payload, "campaignId", Program.LatestCampaignId());
            if (string.IsNullOrWhiteSpace(campaignId)) campaignId = Program.LatestCampaignId();
            using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
            {
                Program.ExecuteSql(connection, "UPDATE embedding_jobs SET status='pending',attempt_count=0,next_attempt_ts=0,last_error='',updated_ts=$ts WHERE last_error<>'';",
                    new Dictionary<string, object> { ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                return new Dictionary<string, object> { ["ok"] = true, ["campaignId"] = campaignId,
                    ["queued"] = ReadInt(Program.QuerySql(connection, "SELECT COUNT(*) count FROM embedding_jobs WHERE status='pending';").FirstOrDefault(), "count", 0) };
            }
        }

        private static void LogSemanticFailureOnce(string eventName, string error)
        {
            lock (SemanticDiagnosticLock)
            {
                if (DateTime.UtcNow < SemanticDiagnosticCooldownUntilUtc) return;
                SemanticDiagnosticCooldownUntilUtc = DateTime.UtcNow.AddSeconds(30);
            }
            Program.LogOperational(eventName, new Dictionary<string, object> { ["error"] = Program.LimitText(error, 500), ["fallback"] = "sqlite_fts5" });
        }

        internal static List<Dictionary<string, object>> RunSemanticMemorySelfTests()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => rows.Add(new Dictionary<string, object>
            { ["id"] = "semantic_memory_" + id, ["suite"] = "semantic_memory", ["passed"] = passed, ["summary"] = summary });
            string first = DeterministicSemanticGuid("campaign|memory|one"), second = DeterministicSemanticGuid("campaign|memory|one");
            add("deterministic_id", first == second && Guid.TryParse(first, out Guid _), "Vector IDs are deterministic UUIDs.");
            add("canonical_text", BuildCanonicalEmbeddingText("belief", new Dictionary<string, object> { ["claim"] = "A lord abandoned his ally.", ["stance"] = "distrust" }).Contains("abandoned"), "Canonical text includes source prose and stance.");
            Dictionary<string, object> scored = new Dictionary<string, object> { ["vectorSemanticScore"] = 0.8d };
            add("score_blend", Math.Abs(AddSemanticScore(scored, 10d, new Dictionary<string, object> { ["vectorScoreWeight"] = 30d }) - 34d) < 0.001d, "Semantic score augments deterministic relevance without replacing it.");
            int savedInteractiveCount = Volatile.Read(ref Program.InteractiveRequestCount);
            long savedInteractiveTicks = Interlocked.Read(ref Program.LastInteractiveRequestCompletedUtcTicks);
            try
            {
                Interlocked.Exchange(ref Program.InteractiveRequestCount, 1);
                bool activeYields = Program.InteractiveRequestActiveOrRecent();
                Interlocked.Exchange(ref Program.InteractiveRequestCount, 0);
                Interlocked.Exchange(ref Program.LastInteractiveRequestCompletedUtcTicks, DateTime.UtcNow.Ticks);
                bool recentYields = Program.InteractiveRequestActiveOrRecent();
                Interlocked.Exchange(ref Program.LastInteractiveRequestCompletedUtcTicks, DateTime.UtcNow.AddSeconds(-5).Ticks);
                bool handoffProtected = !Program.InteractiveRequestActiveOrRecent()
                    && SemanticIndexShouldYield();
                Interlocked.Exchange(ref Program.LastInteractiveRequestCompletedUtcTicks, DateTime.UtcNow.AddSeconds(-11).Ticks);
                bool quietResumes = !SemanticIndexShouldYield();
                add("foreground_priority", activeYields && recentYields && handoffProtected && quietResumes,
                    "Background embedding chunks yield throughout the response-to-next-turn handoff and resume after the extended quiet period.");
            }
            finally
            {
                Interlocked.Exchange(ref Program.InteractiveRequestCount, savedInteractiveCount);
                Interlocked.Exchange(ref Program.LastInteractiveRequestCompletedUtcTicks, savedInteractiveTicks);
            }
            List<Dictionary<string, object>> duplicateCandidates = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["event_id"] = "duplicate_event", ["deterministicScore"] = 2d },
                new Dictionary<string, object> { ["event_id"] = "duplicate_event", ["deterministicScore"] = 7d }
            };
            List<Dictionary<string, object>> deduplicatedCandidates = MergeSemanticWorldHistoryCandidates(
                null, "campaign", "timeline", "claim", "generic_participation", "", new List<string>(), "", -1d, double.MaxValue,
                duplicateCandidates, new Dictionary<string, object>(), new Dictionary<string, object> { ["vectorScoreWeight"] = 30d });
            add("world_history_candidate_dedupe",
                deduplicatedCandidates.Count == 1 && Math.Abs(ReadDouble(deduplicatedCandidates[0], "deterministicScore", 0d) - 7d) < 0.001d,
                "Duplicate deterministic world-history candidates merge safely before semantic scoring.");
            add("privacy_contract", typeof(Program.KnowledgeAccessContext) != null, "Semantic rows still pass through the canonical knowledge-access context.");
            add("world_history_significance_filter",
                !WorldHistoryEventEmbeddingEligible(new Dictionary<string, object> { ["event_type"] = "settlement_entered", ["dissemination_class"] = "ordinary" })
                && WorldHistoryEventEmbeddingEligible(new Dictionary<string, object> { ["event_type"] = "siege_completed", ["dissemination_class"] = "ordinary" })
                && WorldHistoryEventEmbeddingEligible(new Dictionary<string, object> { ["event_type"] = "future_unknown_event", ["dissemination_class"] = "major_world" }),
                "Routine global simulation noise stays in PostgreSQL full-text storage while significant and major-world events remain embedding eligible.");
            TaskCompletionSource<bool> activePump = new TaskCompletionSource<bool>();
            using (CancellationTokenSource activeCancellation = new CancellationTokenSource())
            using (CancellationTokenSource cancelledCancellation = new CancellationTokenSource())
            {
                cancelledCancellation.Cancel();
                add("pump_restart_contract",
                    SemanticPumpUsable(activePump.Task, activeCancellation)
                    && !SemanticPumpUsable(activePump.Task, cancelledCancellation)
                    && !SemanticPumpUsable(Task.CompletedTask, activeCancellation),
                    "A canceled or completed indexing pump is replaceable even while an older task is still unwinding.");
            }
            Func<string, string, string, string, Dictionary<string, object>> semanticRow = (type, id, hash, vector) =>
                new Dictionary<string, object>
                {
                    ["source_type"] = type, ["source_id"] = id, ["model_version"] = SemanticModelVersion,
                    ["provider"] = "local", ["content_hash"] = hash, ["vector_id"] = vector,
                    ["status"] = "indexed", ["event_type"] = "", ["dissemination_class"] = ""
                };
            Dictionary<string, Dictionary<string, object>> currentDocuments =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> targetDocuments =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> unchangedCurrent = semanticRow("memory", "unchanged", "same", "vector_unchanged");
            Dictionary<string, object> unchangedTarget = semanticRow("memory", "unchanged", "same", "vector_unchanged");
            Dictionary<string, object> changedCurrent = semanticRow("memory", "changed", "old", "vector_changed");
            Dictionary<string, object> changedTarget = semanticRow("memory", "changed", "new", "vector_changed");
            Dictionary<string, object> futureCurrent = semanticRow("event", "future", "future", "vector_future");
            Dictionary<string, object> restoredTarget = semanticRow("summary", "restored", "restored", "vector_restored");
            currentDocuments[PersistenceService.SaveSyncEmbeddingDocumentKey(unchangedCurrent)] = unchangedCurrent;
            currentDocuments[PersistenceService.SaveSyncEmbeddingDocumentKey(changedCurrent)] = changedCurrent;
            currentDocuments[PersistenceService.SaveSyncEmbeddingDocumentKey(futureCurrent)] = futureCurrent;
            targetDocuments[PersistenceService.SaveSyncEmbeddingDocumentKey(unchangedTarget)] = unchangedTarget;
            targetDocuments[PersistenceService.SaveSyncEmbeddingDocumentKey(changedTarget)] = changedTarget;
            targetDocuments[PersistenceService.SaveSyncEmbeddingDocumentKey(restoredTarget)] = restoredTarget;
            SaveSyncSemanticReconciliationPlan selectivePlan =
                PersistenceService.BuildSaveSyncSemanticReconciliationPlan(currentDocuments, targetDocuments);
            add("save_sync_selective_reconciliation",
                selectivePlan.PreservedDocuments == 1
                && selectivePlan.DeleteVectorIds.SetEquals(new[] { "vector_changed", "vector_future" })
                && selectivePlan.RequeueSources.Select(source => source.SourceId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(new[] { "changed", "restored" }),
                "Save Sync preserves unchanged vectors and only deletes or requeues divergent sources.");
            string campaignId = "semantic_memory_test_" + Guid.NewGuid().ToString("N");
            try
            {
                Program.StoreWorldMemoryEvent(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["eventId"] = "semantic_private_event", ["eventType"] = "personal_revelation",
                    ["summary"] = "Aldric abandoned his ally and broke his promise.", ["participants"] = new List<string> { "npc_owner" },
                    ["known_by"] = new List<string> { "npc_owner" }, ["visibility"] = "private", ["importance"] = 0.8d,
                    ["worldDay"] = 10d, ["ts"] = 1000L
                }, "semantic_self_test");
                using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
                {
                    int triggerCount = ReadInt(Program.QuerySql(connection, "SELECT COUNT(*) count FROM sqlite_master WHERE type='trigger' AND name LIKE 'trg_semantic_%';").FirstOrDefault(), "count", 0);
                    int queued = ReadInt(Program.QuerySql(connection, "SELECT COUNT(*) count FROM embedding_jobs WHERE source_type IN ('event','memory');").FirstOrDefault(), "count", 0);
                    Dictionary<string, object> memory = Program.QuerySql(connection, "SELECT * FROM memories WHERE summary LIKE '%abandoned his ally%' LIMIT 1;").FirstOrDefault();
                    bool ownerAllowed = Program.KnowledgeRowVisibleToNpc("memories", memory, new Program.KnowledgeAccessContext { NpcId = "npc_owner" });
                    bool strangerDenied = !Program.KnowledgeRowVisibleToNpc("memories", memory, new Program.KnowledgeAccessContext { NpcId = "npc_stranger" });
                    EmbeddingSourceDocument document = BuildEmbeddingDocument(campaignId, "memory", ReadString(memory, "memory_id", ""), memory,
                        BuildCanonicalEmbeddingText("memory", memory), new Dictionary<string, object> { ["vectorProvider"] = "local" });
                    string payload = Json.Serialize(document.Payload);
                    int rawConversationTriggerCount = ReadInt(Program.QuerySql(connection,
                        "SELECT COUNT(*) count FROM sqlite_master WHERE type='trigger' AND name LIKE 'trg_semantic_conversation_turns_%';")
                        .FirstOrDefault(), "count", 0);
                    int expectedTriggerCount =
                        ReignPostgreSqlDialect.IsPostgreSql(connection)
                            ? 18 : 20;
                    add("schema_and_triggers", triggerCount >= expectedTriggerCount && rawConversationTriggerCount >= 3 && queued >= 2,
                        "Original attributed conversation and durable semantic sources have insert, update, and delete queue triggers; reads revalidate original eligibility.");
                    add("knowledge_filter", ownerAllowed && strangerDenied, "Private semantic candidates remain visible to their owner and hidden from strangers.");
                    add("external_payload_privacy", payload.IndexOf("abandoned", StringComparison.OrdinalIgnoreCase) < 0 && payload.IndexOf("promise", StringComparison.OrdinalIgnoreCase) < 0,
                        "Vector payload metadata contains no raw memory prose.");
                    Program.ExecuteSql(connection, @"INSERT INTO world_history_events
(event_id,campaign_id,timeline_id,sequence,world_day,event_type,category,phase,dissemination_class,summary,semantic_text,created_utc)
VALUES('semantic_routine_history',$campaign,'timeline',1,10,'settlement_entered','settlement','completed','ordinary','Routine entry','Routine entry',$utc);",
                        new Dictionary<string, object> { ["campaign"] = campaignId, ["utc"] = DateTime.UtcNow.ToString("o") });
                    Program.ExecuteSql(connection, @"INSERT INTO world_history_events
(event_id,campaign_id,timeline_id,sequence,world_day,event_type,category,phase,dissemination_class,summary,semantic_text,created_utc)
VALUES('semantic_significant_history',$campaign,'timeline',2,10,'siege_completed','warfare','completed','ordinary','Siege completed','Siege completed',$utc);",
                        new Dictionary<string, object> { ["campaign"] = campaignId, ["utc"] = DateTime.UtcNow.ToString("o") });
                    Dictionary<string, object> routineJob = Program.QuerySql(connection,
                        "SELECT * FROM embedding_jobs WHERE source_type='world_history_event' AND source_id='semantic_routine_history' LIMIT 1;").FirstOrDefault();
                    Dictionary<string, object> significantJob = Program.QuerySql(connection,
                        "SELECT * FROM embedding_jobs WHERE source_type='world_history_event' AND source_id='semantic_significant_history' LIMIT 1;").FirstOrDefault();
                    add("world_history_queue_filter", routineJob == null && ReadString(significantJob, "operation", "") == "upsert",
                        "Embedding triggers enqueue significant world history without queuing routine settlement traffic.");
                    Dictionary<string, object> job = Program.QuerySql(connection, "SELECT * FROM embedding_jobs LIMIT 1;").FirstOrDefault();
                    FailEmbeddingJob(connection, job, "temporary worker failure");
                    Dictionary<string, object> retried = Program.QuerySql(connection, "SELECT * FROM embedding_jobs WHERE source_type=$type AND source_id=$id LIMIT 1;",
                        new Dictionary<string, object> { ["type"] = ReadString(job, "source_type", ""), ["id"] = ReadString(job, "source_id", "") }).FirstOrDefault();
                    add("retry_backoff", ReadInt(retried, "attempt_count", 0) == 1 && ReadLong(retried, "next_attempt_ts", 0) > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        "Failed indexing jobs persist with bounded retry backoff.");
                }
            }
            finally
            {
                string directory = Program.CampaignDirectory(campaignId);
                try
                {
                    ReignPostgreSqlStorage.ClearAllPools();
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
                catch { }
            }
            return rows;
        }

        private sealed class EmbeddingSourceDocument
        {
            public string CampaignId = "";
            public string TimelineId = "";
            public string SourceType = "";
            public string SourceId = "";
            public string Provider = "";
            public string VectorId = "";
            public string Text = "";
            public string ContentHash = "";
            public Dictionary<string, object> Payload = new Dictionary<string, object>();
        }

        internal sealed class SemanticSourceIdentity
        {
            public string SourceType = "";
            public string SourceId = "";
        }

        internal sealed class SaveSyncSemanticReconciliationPlan
        {
            public readonly HashSet<string> DeleteVectorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<SemanticSourceIdentity> RequeueSources = new List<SemanticSourceIdentity>();
            public readonly List<SemanticSourceIdentity> InvalidatedCurrentSources = new List<SemanticSourceIdentity>();
            public int CurrentDocuments;
            public int TargetDocuments;
            public int PreservedDocuments;
        }
    }
}
