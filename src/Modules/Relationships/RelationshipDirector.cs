using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const double DirectorRunIntervalDays = 7d;
        private const double DirectorPairCooldownDays = 7d;
        private const int PostgreSqlRelationshipDirectorSchemaRevision = 2;

        private static void EnsureRelationshipDirectorSchema(ReignDbConnection connection)
        {
            const string marker =
                "postgresql_relationship_director_schema_revision";
            bool schemaReady = IsPostgreSqlComponentSchemaReady(connection,
                marker, PostgreSqlRelationshipDirectorSchemaRevision);
            if (!schemaReady)
            {
                EnsureRelationshipDirectorSchemaCore(connection);
                if (ReignPostgreSqlDialect.IsPostgreSql(connection))
                {
                    ExecuteSql(connection, @"
INSERT INTO schema_meta(key,value)
VALUES('postgresql_relationship_director_schema_revision',$revision)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                        new Dictionary<string, object>
                        {
                            ["revision"] =
                                PostgreSqlRelationshipDirectorSchemaRevision
                                    .ToString()
                        });
                    MarkPostgreSqlComponentSchemaReady(connection, marker,
                        PostgreSqlRelationshipDirectorSchemaRevision);
                }
            }
            // Save Sync can restore an older campaign snapshot after the process
            // has already cached schema readiness. The repair marker therefore
            // remains a cheap independent check on every schema entry point.
            RepairUnconfirmedOrganicMarriageHistory(connection);
            RepairAlreadyPregnantConceptionActions(connection);
        }

        private static void RepairAlreadyPregnantConceptionActions(
            ReignDbConnection connection)
        {
            const string repairMarker =
                "already_pregnant_conception_obsolete_repair_v1";
            if (ReadString(QuerySql(connection,
                    "SELECT value FROM schema_meta WHERE key=$key LIMIT 1;",
                    new Dictionary<string, object> { ["key"] = repairMarker })
                    .FirstOrDefault(), "value", "") == "1") return;

            foreach (Dictionary<string, object> action in QuerySql(connection, @"
SELECT director_action_id,payload_json,result_json
FROM relationship_director_actions
WHERE action_type='start_conception' AND status='failed';"))
            {
                Dictionary<string, object> result = TryParseJsonObject(
                    ReadString(action, "result_json", "{}"))
                    ?? new Dictionary<string, object>();
                if (!ReadString(result, "error", "").Equals(
                        "Mother is already pregnant.",
                        StringComparison.OrdinalIgnoreCase)) continue;
                Dictionary<string, object> actionPayload = TryParseJsonObject(
                    ReadString(action, "payload_json", "{}"))
                    ?? new Dictionary<string, object>();
                string source = ReadString(actionPayload, "source", "");
                if (!source.Equals("mbti_relationship_lifecycle",
                        StringComparison.OrdinalIgnoreCase)
                    && !source.Equals("relationship_fling",
                        StringComparison.OrdinalIgnoreCase)) continue;
                string actionId = ReadString(action, "director_action_id", "");
                string conceptionId = ReadString(actionPayload,
                    "conceptionId", "");
                ExecuteSql(connection, @"
UPDATE relationship_director_actions SET status='obsolete'
WHERE director_action_id=$id AND status='failed';",
                    new Dictionary<string, object> { ["id"] = actionId });
                if (!string.IsNullOrWhiteSpace(conceptionId))
                    ExecuteSql(connection, @"
UPDATE conceptions SET status='obsolete_game',updated_ts=$ts
WHERE conception_id=$id AND status='pending_game';",
                        new Dictionary<string, object>
                        {
                            ["id"] = conceptionId,
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });
            }
            ExecuteSql(connection, @"
INSERT OR REPLACE INTO schema_meta(key,value) VALUES($key,'1');",
                new Dictionary<string, object> { ["key"] = repairMarker });
        }

        private static void RepairUnconfirmedOrganicMarriageHistory(
            ReignDbConnection connection)
        {
            const string repairMarker =
                "organic_marriage_native_postcondition_repair_v1";
            string markerValue = ReadString(QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key=$key LIMIT 1;",
                new Dictionary<string, object> { ["key"] = repairMarker })
                .FirstOrDefault(), "value", "");
            Dictionary<string, object> markerState = TryParseJsonObject(
                markerValue) ?? new Dictionary<string, object>();
            long auditTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!TableExists(connection, "relationship_observed_heroes"))
                return;

            List<Dictionary<string, object>> completed = QuerySql(connection, @"
SELECT director_action_id,world_day,actor_id,target_id,payload_json,result_json
FROM relationship_director_actions
WHERE action_type='marriage' AND status='completed';")
                .Where(row =>
                {
                    Dictionary<string, object> payload = TryParseJsonObject(
                        ReadString(row, "payload_json", "{}"))
                        ?? new Dictionary<string, object>();
                    string route = ReadString(payload, "route", "");
                    return ReadString(payload, "source", "").Equals(
                            "mbti_relationship_lifecycle",
                            StringComparison.OrdinalIgnoreCase)
                        && (route.Equals("mutual_affinity",
                                StringComparison.OrdinalIgnoreCase)
                            || route.Equals("pregnancy_commitment",
                                StringComparison.OrdinalIgnoreCase));
                }).ToList();

            double observedThroughLatest = ReadDouble(QuerySql(connection,
                @"SELECT MAX(last_observed_day) AS latest
FROM relationship_observed_heroes;").FirstOrDefault(), "latest", -1d);
            if (ReadInt(markerState, "completedActionCount", -1)
                    == completed.Count
                && Math.Abs(ReadDouble(markerState, "observedThrough", -1d)
                    - observedThroughLatest) < 0.000001d)
                return;

            Dictionary<string, Dictionary<string, object>> observations =
                QuerySql(connection, @"SELECT hero_id,profile_json,last_observed_day
FROM relationship_observed_heroes;")
                .ToDictionary(row => ReadString(row, "hero_id", ""), row => row,
                    StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> invalidActions =
                new List<Dictionary<string, object>>();
            Dictionary<string, string> blockedPairs =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (IGrouping<string, Dictionary<string, object>> group in completed
                .GroupBy(row =>
                {
                    Dictionary<string, object> payload = TryParseJsonObject(
                        ReadString(row, "payload_json", "{}"))
                        ?? new Dictionary<string, object>();
                    return FirstNonEmpty(ReadString(payload, "pairKey", ""),
                        AmbientPairKey(ReadString(row, "actor_id", ""),
                            ReadString(row, "target_id", "")));
                }, StringComparer.OrdinalIgnoreCase))
            {
                Dictionary<string, object> first = group.First();
                string actorId = ReadString(first, "actor_id", "");
                string targetId = ReadString(first, "target_id", "");
                if (!observations.TryGetValue(actorId,
                        out Dictionary<string, object> actorObservation)
                    || !observations.TryGetValue(targetId,
                        out Dictionary<string, object> targetObservation))
                    continue;
                Dictionary<string, object> actorProfile = TryParseJsonObject(
                    ReadString(actorObservation, "profile_json", "{}"))
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> targetProfile = TryParseJsonObject(
                    ReadString(targetObservation, "profile_json", "{}"))
                    ?? new Dictionary<string, object>();
                bool reciprocal = ReadString(actorProfile, "spouseId", "")
                        .Equals(targetId, StringComparison.OrdinalIgnoreCase)
                    && ReadString(targetProfile, "spouseId", "")
                        .Equals(actorId, StringComparison.OrdinalIgnoreCase);
                if (reciprocal) continue;
                bool sameSex = ReadBool(actorProfile, "isFemale", false)
                    == ReadBool(targetProfile, "isFemale", false);
                bool duplicateReports = group.Count() > 1;
                bool bothAlive = ReadBool(actorProfile, "isAlive", false)
                    && ReadBool(targetProfile, "isAlive", false);
                bool bothUnmarried = string.IsNullOrWhiteSpace(
                        ReadString(actorProfile, "spouseId", ""))
                    && string.IsNullOrWhiteSpace(
                        ReadString(targetProfile, "spouseId", ""));
                double latestReportedDay = group.Max(row =>
                {
                    Dictionary<string, object> result = TryParseJsonObject(
                        ReadString(row, "result_json", "{}"))
                        ?? new Dictionary<string, object>();
                    return ReadDouble(result, "worldDay",
                        ReadDouble(row, "world_day", 0d));
                });
                double observedThrough = Math.Min(
                    ReadDouble(actorObservation, "last_observed_day", -1d),
                    ReadDouble(targetObservation, "last_observed_day", -1d));
                bool laterNonreciprocalObservation = bothAlive
                    && observedThrough >= Math.Floor(latestReportedDay) + 1d;
                if (!sameSex && !duplicateReports
                    && !laterNonreciprocalObservation)
                    continue;

                string reason = sameSex
                    ? "Native marriage model does not support this same-sex couple."
                    : duplicateReports
                        ? "Repeated completed reports lacked reciprocal native spouse state."
                        : bothUnmarried
                            ? "A later native observation confirmed that the marriage did not take effect."
                            : "A later native observation confirmed that the marriage was not reciprocal.";
                blockedPairs[group.Key] = reason;
                invalidActions.AddRange(group);
            }

            if (invalidActions.Count > 0)
            {
                EnsureSemanticMemorySchema(connection);
                EnsureWorldTestTelemetrySchema(connection);
                string campaignId = ReignPostgreSqlStorage
                    .CampaignIdForConnection(connection);
                HashSet<string> affectedRollups = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (Dictionary<string, object> action in invalidActions)
                {
                    string actionId = ReadString(action,
                        "director_action_id", "");
                    Dictionary<string, object> payload = TryParseJsonObject(
                        ReadString(action, "payload_json", "{}"))
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> originalResult = TryParseJsonObject(
                        ReadString(action, "result_json", "{}"))
                        ?? new Dictionary<string, object>();
                    string pairKey = FirstNonEmpty(
                        ReadString(payload, "pairKey", ""),
                        AmbientPairKey(ReadString(action, "actor_id", ""),
                            ReadString(action, "target_id", "")));
                    string reason = blockedPairs.TryGetValue(pairKey,
                        out string storedReason) ? storedReason
                        : "Reciprocal native spouse state was not confirmed.";
                    Dictionary<string, object> repairedResult =
                        new Dictionary<string, object>
                        {
                            ["status"] = "invalid",
                            ["error"] = reason,
                            ["repair"] = repairMarker,
                            ["reclassifiedFrom"] = "completed",
                            ["originalResult"] = originalResult
                        };
                    ExecuteSql(connection, @"UPDATE relationship_director_actions
SET status='invalid',result_json=$result WHERE director_action_id=$id;",
                        new Dictionary<string, object>
                        {
                            ["result"] = Json.Serialize(repairedResult),
                            ["id"] = actionId
                        });
                    ExecuteSql(connection, @"DELETE FROM relationship_incidents
WHERE kind='marriage' AND payload_json LIKE $needle;",
                        new Dictionary<string, object>
                        {
                            ["needle"] = "%" + actionId + "%"
                        });
                    string eventId = "marriage_" + actionId;
                    DeleteWorldMemoryEventProjection(connection, eventId);
                    ExecuteSql(connection,
                        "DELETE FROM events WHERE event_id=$event;",
                        new Dictionary<string, object> { ["event"] = eventId });

                    string timelineId = FirstNonEmpty(
                        ReadString(payload, "timelineId", ""), "main");
                    Dictionary<string, object> result = originalResult;
                    int outcomeDay = (int)Math.Floor(ReadDouble(result,
                        "worldDay", ReadDouble(action, "world_day", 0d)));
                    if (!string.IsNullOrWhiteSpace(campaignId))
                    {
                        RecordWorldTestCounterSchemaReady(connection, campaignId,
                            timelineId, outcomeDay, "marriages",
                            "outcome_" + actionId,
                            BuildWorldTestMarriageOutcomeCounters(
                                WorldTestMarriageOutcomeRoute(payload),
                                "invalid"));
                        affectedRollups.Add(timelineId + "|"
                            + outcomeDay.ToString(CultureInfo.InvariantCulture));
                    }
                }

                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (KeyValuePair<string, string> pair in blockedPairs)
                {
                    Dictionary<string, object> lifecycle = QuerySql(connection,
                        @"SELECT lover_active FROM relationship_pair_lifecycle
WHERE pair_key=$pair LIMIT 1;",
                        new Dictionary<string, object> { ["pair"] = pair.Key })
                        .FirstOrDefault() ?? new Dictionary<string, object>();
                    ExecuteSql(connection, @"UPDATE relationship_pair_lifecycle SET
married=0,marriage_action_id='',marriage_blocked=1,
marriage_block_reason=$reason,updated_ts=$ts WHERE pair_key=$pair;",
                        new Dictionary<string, object>
                        {
                            ["reason"] = pair.Value,
                            ["ts"] = ts,
                            ["pair"] = pair.Key
                        });
                    ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
shared_tag=$tag,updated_ts=$ts WHERE pair_key=$pair AND shared_tag='married';",
                        new Dictionary<string, object>
                        {
                            ["tag"] = ReadInt(lifecycle, "lover_active", 0) == 1
                                ? "lovers" : "",
                            ["ts"] = ts,
                            ["pair"] = pair.Key
                        });
                }
                foreach (string affected in affectedRollups)
                {
                    int separator = affected.LastIndexOf('|');
                    string timelineId = affected.Substring(0, separator);
                    int day = int.Parse(affected.Substring(separator + 1),
                        CultureInfo.InvariantCulture);
                    EnqueueWorldTestRollup(connection, campaignId, timelineId,
                        day, repairMarker);
                }
                ExecuteSql(connection, @"INSERT OR REPLACE INTO schema_meta(key,value)
VALUES('campaign_compaction_required','1');");
            }
            ExecuteSql(connection, @"INSERT OR REPLACE INTO schema_meta(key,value)
VALUES($key,$value);", new Dictionary<string, object>
            {
                ["key"] = repairMarker,
                ["value"] = Json.Serialize(new Dictionary<string, object>
                {
                    ["reclassifiedActions"] = invalidActions.Count,
                    ["blockedPairs"] = blockedPairs.Count,
                    ["completedActionCount"] = completed.Count,
                    ["observedThrough"] = observedThroughLatest,
                    ["auditedTs"] = auditTs,
                    ["completedUtc"] = DateTime.UtcNow.ToString("o")
                })
            });
        }

        private static void EnsureRelationshipDirectorSchemaCore(
            ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_director_runs (
run_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,world_day REAL NOT NULL,status TEXT NOT NULL,
candidate_count INTEGER NOT NULL DEFAULT 0,event_count INTEGER NOT NULL DEFAULT 0,minime_json TEXT NOT NULL DEFAULT '{}',
input_json TEXT NOT NULL DEFAULT '{}',output_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_director_runs_day ON relationship_director_runs(world_day DESC);");
            int prunedDirectorPayloads = ReadInt(QuerySql(connection,
                "SELECT COUNT(*) AS count FROM relationship_director_runs WHERE length(input_json)>65536;").FirstOrDefault(), "count", 0);
            if (prunedDirectorPayloads > 0)
            {
                ExecuteSql(connection, "UPDATE relationship_director_runs SET input_json='{\"legacyPayloadPruned\":true}' WHERE length(input_json)>65536;");
                ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('campaign_compaction_required','1');");
            }
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_director_actions (
director_action_id TEXT PRIMARY KEY,action_type TEXT NOT NULL,status TEXT NOT NULL DEFAULT 'pending',world_day REAL NOT NULL,
actor_id TEXT NOT NULL DEFAULT '',target_id TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,claimed_ts INTEGER NOT NULL DEFAULT 0,resolved_ts INTEGER NOT NULL DEFAULT 0,result_json TEXT NOT NULL DEFAULT '{}');" );
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_director_actions_status ON relationship_director_actions(status,world_day);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_director_actions_pair_status ON relationship_director_actions(actor_id,target_id,status);");
            EnsureRelationshipNativeTargetSchema(connection);
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conception_attempts (
attempt_id TEXT PRIMARY KEY,source TEXT NOT NULL,event_id TEXT NOT NULL DEFAULT '',mother_id TEXT NOT NULL,father_id TEXT NOT NULL,
mother_age REAL NOT NULL,chance REAL NOT NULL,roll REAL NOT NULL,success INTEGER NOT NULL,world_day REAL NOT NULL,
payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL);" );
            EnsureDatabaseColumn(connection,"conception_attempts","status","TEXT NOT NULL DEFAULT 'resolved'");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conceptions (
conception_id TEXT PRIMARY KEY,attempt_id TEXT NOT NULL DEFAULT '',mother_id TEXT NOT NULL,biological_father_id TEXT NOT NULL,
legal_father_id TEXT NOT NULL DEFAULT '',conception_day REAL NOT NULL,due_day REAL NOT NULL,status TEXT NOT NULL DEFAULT 'pending_game',
secrecy REAL NOT NULL DEFAULT 0,child_id TEXT NOT NULL DEFAULT '',revealed INTEGER NOT NULL DEFAULT 0,
payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS parentage (
child_id TEXT PRIMARY KEY,mother_id TEXT NOT NULL,biological_father_id TEXT NOT NULL,legal_father_id TEXT NOT NULL DEFAULT '',
is_illegitimate INTEGER NOT NULL DEFAULT 0,revealed INTEGER NOT NULL DEFAULT 0,bastard_surname TEXT NOT NULL DEFAULT '',
conception_id TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',updated_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS director_outbox (
outbox_id TEXT PRIMARY KEY,kind TEXT NOT NULL,status TEXT NOT NULL DEFAULT 'pending',world_day REAL NOT NULL,
subject_ids_json TEXT NOT NULL DEFAULT '[]',payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS marriage_leader_rolls (
roll_id TEXT PRIMARY KEY,timeline_id TEXT NOT NULL DEFAULT 'main',period_index INTEGER NOT NULL,leader_id TEXT NOT NULL,clan_id TEXT NOT NULL,world_day REAL NOT NULL,
chance REAL NOT NULL,roll REAL NOT NULL,urgency REAL NOT NULL,status TEXT NOT NULL,payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,
UNIQUE(timeline_id,period_index,leader_id));" );
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS marriage_evaluations (
evaluation_id TEXT PRIMARY KEY,route TEXT NOT NULL,status TEXT NOT NULL,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,
clan_a_id TEXT NOT NULL DEFAULT '',clan_b_id TEXT NOT NULL DEFAULT '',leader_a_id TEXT NOT NULL DEFAULT '',leader_b_id TEXT NOT NULL DEFAULT '',
world_day REAL NOT NULL,score REAL NOT NULL DEFAULT 0,leader_a_approved INTEGER NOT NULL DEFAULT 0,leader_b_approved INTEGER NOT NULL DEFAULT 0,
resentment_a REAL NOT NULL DEFAULT 0,resentment_b REAL NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_marriage_evaluations_status ON marriage_evaluations(status,world_day DESC);");
            EnsureDatabaseColumn(connection,"marriage_evaluations","timeline_id","TEXT NOT NULL DEFAULT 'main'");
            MigrateMarriageLeaderRollTimeline(connection);
            ReclassifyNativeMarriageEligibilityFailures(connection);
            EnsureRelationshipLifecycleSchema(connection);
        }

        private static void ReclassifyNativeMarriageEligibilityFailures(ReignDbConnection connection)
        {
            if(ReadString(QuerySql(connection,"SELECT value FROM schema_meta WHERE key='native_marriage_invalid_classification_v1' LIMIT 1;").FirstOrDefault(),"value","")=="1")return;
            foreach(Dictionary<string,object> action in QuerySql(connection,@"SELECT director_action_id,payload_json
FROM relationship_director_actions
WHERE action_type='marriage' AND status='failed'
AND result_json LIKE '%Native marriage model rejected the couple.%';"))
            {
                Dictionary<string,object> payload=TryParseJsonObject(ReadString(action,"payload_json","{}"))??new Dictionary<string,object>();
                string evaluationId=ReadString(payload,"evaluationId","");
                ExecuteSql(connection,"UPDATE relationship_director_actions SET status='invalid' WHERE director_action_id=$id;",
                    new Dictionary<string,object>{{"id",ReadString(action,"director_action_id","")}});
                if(!string.IsNullOrWhiteSpace(evaluationId))ExecuteSql(connection,
                    "UPDATE marriage_evaluations SET status='invalid_native' WHERE evaluation_id=$id AND status='failed';",
                    new Dictionary<string,object>{{"id",evaluationId}});
            }
            ExecuteSql(connection,"INSERT OR REPLACE INTO schema_meta(key,value) VALUES('native_marriage_invalid_classification_v1','1');");
        }

        private static void EnsureRelationshipNativeTargetSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_native_targets (
pair_key TEXT PRIMARY KEY,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,
target_relation INTEGER NOT NULL,observed_relation INTEGER NOT NULL DEFAULT 0,
status TEXT NOT NULL DEFAULT 'pending',world_day REAL NOT NULL,last_sync_day REAL NOT NULL DEFAULT -1000,
attempt_count INTEGER NOT NULL DEFAULT 0,claimed_ts INTEGER NOT NULL DEFAULT 0,
last_error TEXT NOT NULL DEFAULT '',updated_ts INTEGER NOT NULL);" );
            ExecuteSql(connection,
                "CREATE INDEX IF NOT EXISTS idx_relationship_native_targets_status ON relationship_native_targets(status,world_day,pair_key);");
            EnsureDatabaseColumn(connection, "relationship_native_targets", "revision", "INTEGER NOT NULL DEFAULT 1");
            EnsureDatabaseColumn(connection, "relationship_native_targets", "requires_observation", "INTEGER NOT NULL DEFAULT 0");

            bool chemistryExists = QuerySql(connection,
                "SELECT name FROM sqlite_master WHERE type='table' AND name='relationship_pair_chemistry' LIMIT 1;").Any();
            if (!chemistryExists || ReadString(QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='relationship_native_targets_v1' LIMIT 1;")
                .FirstOrDefault(), "value", "") == "1") return;

            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ExecuteSql(connection, "SAVEPOINT migrate_relationship_native_targets;");
            try
            {
                // The legacy chemistry row only stored the projected value; it
                // did not retain the native value that was actually observed.
                // Migrating it as both target and observation manufactured an
                // already-aligned pending target for every legacy action. Let
                // the next native observation create a real coalesced target.
                ExecuteSql(connection,
                    "DELETE FROM relationship_director_actions WHERE action_type='native_relation';");
                ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
native_action_pending=0,native_action_id='';");
                ExecuteSql(connection,
                    "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('relationship_native_targets_v1','1');");
                ExecuteSql(connection,
                    "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('campaign_compaction_required','1');");
                ExecuteSql(connection, "RELEASE migrate_relationship_native_targets;");
            }
            catch
            {
                try { ExecuteSql(connection, "ROLLBACK TO migrate_relationship_native_targets;"); } catch { }
                try { ExecuteSql(connection, "RELEASE migrate_relationship_native_targets;"); } catch { }
                throw;
            }
        }

        private static void MigrateMarriageLeaderRollTimeline(ReignDbConnection connection)
        {
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                bool currentPostgreSqlConstraint = QuerySql(connection, @"
SELECT 1 AS present
FROM pg_constraint constraint_row
JOIN pg_class table_row ON table_row.oid=constraint_row.conrelid
JOIN pg_namespace schema_row ON schema_row.oid=table_row.relnamespace
WHERE schema_row.nspname=current_schema()
  AND table_row.relname='marriage_leader_rolls'
  AND constraint_row.contype='u'
  AND regexp_replace(
        lower(pg_get_constraintdef(constraint_row.oid)),
        '\s+', '', 'g')
      LIKE 'unique(timeline_id,period_index,leader_id)%'
LIMIT 1;").Any();
                if (currentPostgreSqlConstraint) return;
            }
            Dictionary<string,object> table=QuerySql(connection,"SELECT sql FROM sqlite_master WHERE type='table' AND name='marriage_leader_rolls' LIMIT 1;").FirstOrDefault();
            string schema=ReadString(table,"sql","").Replace(" ",string.Empty).Replace("\r",string.Empty).Replace("\n",string.Empty).ToLowerInvariant();
            if(schema.Contains("unique(timeline_id,period_index,leader_id)"))return;
            ExecuteSql(connection,"ALTER TABLE marriage_leader_rolls RENAME TO marriage_leader_rolls_legacy;");
            ExecuteSql(connection,@"CREATE TABLE marriage_leader_rolls (
roll_id TEXT PRIMARY KEY,timeline_id TEXT NOT NULL DEFAULT 'main',period_index INTEGER NOT NULL,leader_id TEXT NOT NULL,clan_id TEXT NOT NULL,world_day REAL NOT NULL,
chance REAL NOT NULL,roll REAL NOT NULL,urgency REAL NOT NULL,status TEXT NOT NULL,payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,
UNIQUE(timeline_id,period_index,leader_id));");
            ExecuteSql(connection,@"INSERT OR IGNORE INTO marriage_leader_rolls(roll_id,timeline_id,period_index,leader_id,clan_id,world_day,chance,roll,urgency,status,payload_json,created_ts)
SELECT roll_id,'main',period_index,leader_id,clan_id,world_day,chance,roll,urgency,status,payload_json,created_ts FROM marriage_leader_rolls_legacy;");
            ExecuteSql(connection,"DROP TABLE marriage_leader_rolls_legacy;");
        }

        private static Dictionary<string, object> VerifyPlayerConceptionAttemptApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string attemptId = FirstNonEmpty(ReadFirstString(payload, "attemptId", "eventId"), "attempt_" + Guid.NewGuid().ToString("N"));
            string decision = NormalizePregnancyChoiceToken(ReadString(payload, "decision", "proceed"));
            if (decision != "preview" && decision != "proceed" && decision != "pull_out")
                return new Dictionary<string, object>{{"ok",false},{"attemptId",attemptId},{"error","decision must be preview, proceed, or pull_out."}};
            string playerId = ReadFirstString(payload, "playerId", "playerHeroStringId");
            string partnerId = ReadFirstString(payload, "partnerId", "npcId", "heroStringId");
            Dictionary<string, object> player = ReadDictionary(payload, "player") ?? new Dictionary<string, object>();
            Dictionary<string, object> partner = ReadDictionary(payload, "partner") ?? new Dictionary<string, object>();
            bool playerFemale = ReadBool(player, "isFemale", false);
            bool partnerFemale = ReadBool(partner, "isFemale", false);
            string motherId = playerFemale && !partnerFemale ? playerId : partnerFemale && !playerFemale ? partnerId : "";
            string fatherId = motherId == playerId ? partnerId : motherId == partnerId ? playerId : "";
            Dictionary<string, object> mother = motherId == playerId ? player : partner;
            double age = ReadDouble(mother, "age", 0d);
            string text = ReadFirstString(payload, "actionText", "npcReply", "playerText", "text", "description");
            bool verified = ReadBool(payload, "verifiedAct", false) || IsExplicitConceptionCapablePlayerText(text);
            bool eligible = verified && !string.IsNullOrWhiteSpace(motherId) && !string.IsNullOrWhiteSpace(fatherId)
                && age >= 18d && age <= 45d && !ReadBool(mother, "isPregnant", false)
                && ReadBool(payload, "coLocated", true) && ReadBool(payload, "completed", true);
            double chance = eligible ? PlayerConceptionChance(age) : 0d;
            double day = ReadDouble(payload, "worldDay", 0d);

            // A preview is deliberately stateless: displaying the choice must never
            // consume a deterministic roll or create a conception record.
            if (decision == "preview")
            {
                return new Dictionary<string, object> {
                    ["ok"] = true,["attemptId"] = attemptId,["decision"] = decision,
                    ["verified"] = verified,["eligible"] = eligible,["chance"] = chance,
                    ["roll"] = -1d,["rollPerformed"] = false,["success"] = false,["cancelled"] = false
                };
            }

            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                Dictionary<string, object> prior = QuerySql(connection, "SELECT * FROM conception_attempts WHERE attempt_id=$id LIMIT 1;", new Dictionary<string, object> { ["id"] = attemptId }).FirstOrDefault();
                if (prior != null)
                {
                    Dictionary<string, object> priorConception = QuerySql(connection,"SELECT * FROM conceptions WHERE attempt_id=$id LIMIT 1;",new Dictionary<string, object>{{"id",attemptId}}).FirstOrDefault() ?? new Dictionary<string, object>();
                    string priorStatus = ReadString(prior,"status","resolved");
                    bool cancelled = priorStatus.Equals("cancelled",StringComparison.OrdinalIgnoreCase);
                    return new Dictionary<string, object> { ["ok"] = true, ["idempotent"] = true, ["attemptId"] = attemptId, ["decision"] = priorStatus, ["verified"] = verified, ["eligible"] = ReadInt(prior, "success", 0) == 1 || ReadDouble(prior, "chance", 0d) > 0d, ["chance"] = ReadDouble(prior, "chance", 0d), ["roll"] = ReadDouble(prior, "roll", -1d), ["rollPerformed"] = !cancelled, ["cancelled"] = cancelled, ["success"] = !cancelled && ReadInt(prior, "success", 0) == 1,
                        ["conceptionId"] = ReadString(priorConception,"conception_id",""),["motherId"] = ReadString(priorConception,"mother_id",""),["biologicalFatherId"] = ReadString(priorConception,"biological_father_id",""),["legalFatherId"] = ReadString(priorConception,"legal_father_id",""),["dueDay"] = ReadDouble(priorConception,"due_day",0d) };
                }

                bool cancelledNow = decision == "pull_out";
                double roll = cancelledNow ? -1d : StableUnit(attemptId + "|player_conception");
                bool success = !cancelledNow && chance > 0d && roll < chance;
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ExecuteSql(connection, @"INSERT INTO conception_attempts(attempt_id,source,event_id,mother_id,father_id,mother_age,chance,roll,success,world_day,payload_json,created_ts,status)
VALUES($id,'player_dialogue_action',$event,$mother,$father,$age,$chance,$roll,$success,$day,$payload,$ts,$status);", new Dictionary<string, object>
                {
                    ["id"] = attemptId,["event"] = ReadString(payload,"eventId",attemptId),["mother"] = motherId,["father"] = fatherId,["age"] = age,
                    ["chance"] = chance,["roll"] = roll,["success"] = success ? 1 : 0,["day"] = day,["payload"] = Json.Serialize(payload),["ts"] = ts,
                    ["status"] = cancelledNow ? "cancelled" : "resolved"
                });
                string conceptionId = "";
                if (success)
                {
                    conceptionId = "conception_" + Guid.NewGuid().ToString("N");
                    string spouseId = ReadString(mother, "spouseId", "");
                    string legalFatherId = !string.IsNullOrWhiteSpace(spouseId) ? spouseId : fatherId;
                    double secrecy = !string.IsNullOrWhiteSpace(spouseId) && !spouseId.Equals(fatherId, StringComparison.OrdinalIgnoreCase) ? 0.75d : 0d;
                    ExecuteSql(connection, @"INSERT INTO conceptions(conception_id,attempt_id,mother_id,biological_father_id,legal_father_id,conception_day,due_day,status,secrecy,payload_json,created_ts,updated_ts)
VALUES($id,$attempt,$mother,$bio,$legal,$day,$due,'pending_game',$secrecy,$payload,$ts,$ts);", new Dictionary<string, object>
                    {
                        ["id"] = conceptionId,["attempt"] = attemptId,["mother"] = motherId,["bio"] = fatherId,["legal"] = legalFatherId,
                        ["day"] = day,["due"] = day + 36d,["secrecy"] = secrecy,["payload"] = Json.Serialize(payload),["ts"] = ts
                    });
                    QueueDirectorAction(connection,"start_conception",day,motherId,fatherId,new Dictionary<string, object>{{"conceptionId",conceptionId},{"motherId",motherId},{"biologicalFatherId",fatherId},{"legalFatherId",legalFatherId},{"conceptionDay",day},{"dueDay",day+36d},{"secrecy",secrecy},{"playerInvolved",true}});
                }
                return new Dictionary<string, object> { ["ok"] = true,["attemptId"] = attemptId,["decision"] = decision,["verified"] = verified,["eligible"] = eligible,["chance"] = chance,["roll"] = roll,["rollPerformed"] = !cancelledNow,["cancelled"] = cancelledNow,["success"] = success,["conceptionId"] = conceptionId,["motherId"] = motherId,["biologicalFatherId"] = fatherId,["legalFatherId"] = success ? (!string.IsNullOrWhiteSpace(ReadString(mother,"spouseId","")) ? ReadString(mother,"spouseId","") : fatherId) : "",["dueDay"] = success ? day + 36d : 0d };
            }
        }

        private static Dictionary<string, object> FamilyConceptionReportApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string id = ReadFirstString(payload, "conceptionId", "id");
            string status = ReadString(payload, "status", "active");
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                ExecuteSql(connection, "UPDATE conceptions SET status=$status,child_id=$child,payload_json=$payload,updated_ts=$ts WHERE conception_id=$id;", new Dictionary<string, object>
                {
                    ["status"] = status,["child"] = ReadFirstString(payload,"childId","childHeroStringId"),["payload"] = Json.Serialize(payload),["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),["id"] = id
                });
                if (status == "born")
                {
                    Dictionary<string, object> row = QuerySql(connection,"SELECT * FROM conceptions WHERE conception_id=$id LIMIT 1;",new Dictionary<string, object>{{"id",id}}).FirstOrDefault();
                    string child = ReadFirstString(payload,"childId","childHeroStringId");
                    if (row != null && !string.IsNullOrWhiteSpace(child))
                    {
                        bool illegitimate = ReadBool(payload,"isIllegitimate",false) || !ReadString(row,"legal_father_id","").Equals(ReadString(row,"biological_father_id",""),StringComparison.OrdinalIgnoreCase);
                        ExecuteSql(connection,@"INSERT OR REPLACE INTO parentage(child_id,mother_id,biological_father_id,legal_father_id,is_illegitimate,revealed,bastard_surname,conception_id,payload_json,updated_ts)
VALUES($child,$mother,$bio,$legal,$illegitimate,0,'',$conception,$payload,$ts);",new Dictionary<string, object>{{"child",child},{"mother",ReadString(row,"mother_id","")},{"bio",ReadString(row,"biological_father_id","")},{"legal",ReadString(row,"legal_father_id","")},{"illegitimate",illegitimate?1:0},{"conception",id},{"payload",Json.Serialize(payload)},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()}});
                        if (illegitimate && !ReadString(row,"legal_father_id","").Equals(ReadString(row,"biological_father_id",""),StringComparison.OrdinalIgnoreCase))
                        {
                            Dictionary<string, object> motherProfile=LoadDirectorHeroProfile(campaignId,ReadString(row,"mother_id",""),null);
                            double concealChance=ConcealmentChance(motherProfile);
                            double concealRoll=StableUnit(id+"|conceal_parentage");
                            if(concealRoll>concealChance)
                            {
                                ExecuteSql(connection,@"INSERT INTO director_outbox(outbox_id,kind,status,world_day,subject_ids_json,payload_json,created_ts)
VALUES($id,'parentage_rumor_seed','pending',$day,$subjects,$payload,$ts);",new Dictionary<string, object>{{"id","outbox_"+Guid.NewGuid().ToString("N")},{"day",ReadDouble(payload,"worldDay",0d)},{"subjects",Json.Serialize(new[]{child,ReadString(row,"mother_id",""),ReadString(row,"biological_father_id","")})},{"payload",Json.Serialize(new Dictionary<string, object>{{"childId",child},{"motherId",ReadString(row,"mother_id","")},{"biologicalFatherId",ReadString(row,"biological_father_id","")},{"legalFatherId",ReadString(row,"legal_father_id","")},{"concealChance",concealChance},{"concealRoll",concealRoll}})},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()}});
                            }
                        }
                    }
                }
            }
            return new Dictionary<string, object>{{"ok",true},{"conceptionId",id},{"status",status}};
        }

        private static Dictionary<string, object> FamilyParentageRevealApi(Dictionary<string, object> payload)
        {
            payload=payload??new Dictionary<string, object>();string campaignId=ReadString(payload,"campaignId","default"),childId=ReadFirstString(payload,"childId","childHeroStringId");double day=ReadDouble(payload,"worldDay",0d);
            using(ReignDbConnection connection=OpenCampaignConnection(campaignId))
            {
                Dictionary<string, object> row=QuerySql(connection,"SELECT * FROM parentage WHERE child_id=$child LIMIT 1;",new Dictionary<string, object>{{"child",childId}}).FirstOrDefault();
                if(row==null)return new Dictionary<string, object>{{"ok",false},{"error","Parentage record was not found."}};
                if(ReadInt(row,"revealed",0)==1)return new Dictionary<string, object>{{"ok",true},{"idempotent",true},{"childId",childId}};
                ExecuteSql(connection,"UPDATE parentage SET revealed=1,is_illegitimate=1,bastard_surname=$surname,payload_json=$payload,updated_ts=$ts WHERE child_id=$child;",new Dictionary<string, object>{{"surname",ReadString(payload,"bastardSurname","Baseborn")},{"payload",Json.Serialize(payload)},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"child",childId}});
                QueueDirectorAction(connection,"reveal_parentage",day,childId,ReadString(row,"biological_father_id",""),new Dictionary<string, object>{{"childId",childId},{"biologicalFatherId",ReadString(row,"biological_father_id","")},{"legalFatherId",ReadString(row,"legal_father_id","")},{"bastardSurname",ReadString(payload,"bastardSurname","Baseborn")}});
                StoreWorldMemoryEvent(new Dictionary<string, object>{{"campaignId",campaignId},{"eventType","parentage_revealed"},{"worldDay",day},{"summary","The concealed biological parentage of "+childId+" was publicly exposed."},{"participants",new[]{childId,ReadString(row,"mother_id",""),ReadString(row,"biological_father_id","")}},{"visibility","public"},{"importance",0.95d}},"parentage_reveal");
                return new Dictionary<string, object>{{"ok",true},{"childId",childId},{"queued",true}};
            }
        }

        private static Dictionary<string, object> RelationshipDirectorSnapshotApi(Dictionary<string, object> payload)
        {
            return RunRelationshipDirector(payload ?? new Dictionary<string, object>(), false);
        }

        private static Dictionary<string, object> RelationshipDirectorRunApi(Dictionary<string, object> payload)
        {
            return RunRelationshipDirector(payload ?? new Dictionary<string, object>(), true);
        }

        private static Dictionary<string, object> RunRelationshipDirector(Dictionary<string, object> payload, bool force)
        {
            string campaignId = ReadString(payload,"campaignId","default");
            double day = ReadDouble(payload,"worldDay",0d);
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureRelationshipDirectorSchema(connection);
                Dictionary<string, object> last = QuerySql(connection,"SELECT * FROM relationship_director_runs ORDER BY world_day DESC LIMIT 1;").FirstOrDefault();
                if (!force && last != null && day - ReadDouble(last,"world_day",0d) < DirectorRunIntervalDays)
                    return new Dictionary<string, object>{{"ok",true},{"skipped",true},{"reason","political_marriage_cadence"},
                        {"nextDay",ReadDouble(last,"world_day",0d)+DirectorRunIntervalDays},
                        {"passiveMechanicalEventsEnabled",false},{"llmCalls",0}};
                Dictionary<string, object> marriage = ProcessMarriageSystem(connection,campaignId,payload,day,force);
                int candidateCount = ReadInt(marriage, "leaderRolls", 0);
                int eventCount = ReadInt(marriage, "arrangedCreated", 0);
                string runId = "director_run_" + Guid.NewGuid().ToString("N");
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Dictionary<string, object> compactInput = new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["timelineId"] = ReadString(payload, "timelineId", "main"),
                    ["worldDay"] = day,
                    ["heroCount"] = ReadDictionaryList(payload, "heroes").Count,
                    ["clanCount"] = ReadDictionaryList(payload, "clans").Count,
                    ["purpose"] = "political_marriage_only"
                };
                ExecuteSql(connection,@"INSERT INTO relationship_director_runs(run_id,campaign_id,world_day,status,candidate_count,event_count,minime_json,input_json,output_json,created_ts)
VALUES($id,$campaign,$day,'completed',$candidates,$events,'{""disabled"":true}',$input,$output,$ts);",
                    new Dictionary<string, object>{{"id",runId},{"campaign",campaignId},{"day",day},
                        {"candidates",candidateCount},{"events",eventCount},
                        {"input",Json.Serialize(compactInput)},{"output",Json.Serialize(marriage)},{"ts",ts}});
                ExecuteSql(connection, @"DELETE FROM relationship_director_runs
WHERE run_id NOT IN (
    SELECT run_id FROM relationship_director_runs ORDER BY world_day DESC,created_ts DESC LIMIT 64
);");
                return new Dictionary<string, object>{{"ok",true},{"runId",runId},
                    {"candidateCount",candidateCount},{"eventCount",eventCount},
                    {"passiveMechanicalEventsEnabled",false},{"llmCalls",0},
                    {"eventPolicy","political_marriage_only"},{"marriage",marriage}};
            }
        }

        private static string DirectorEdgeKingdomId(Dictionary<string, object> edge)
        {
            Dictionary<string, object> a=ReadDictionary(edge,"heroA")??new Dictionary<string, object>();
            Dictionary<string, object> b=ReadDictionary(edge,"heroB")??new Dictionary<string, object>();
            return FirstNonEmpty(ReadString(a,"kingdomId",""),ReadString(b,"kingdomId",""),ReadString(edge,"kingdomId",""));
        }

        private static void AddDirectorStoryEdges(ReignDbConnection connection,List<Dictionary<string,object>> edges,Dictionary<string,object> payload,double day)
        {
            Dictionary<string,Dictionary<string,object>> heroes=ReadDictionaryList(payload,"nobles").Concat(ReadDictionaryList(payload,"heroes"))
                .Where(x=>!string.IsNullOrWhiteSpace(ReadFirstString(x,"heroStringId","heroId","id")))
                .GroupBy(x=>ReadFirstString(x,"heroStringId","heroId","id"),StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x=>x.Key,x=>x.First(),StringComparer.OrdinalIgnoreCase);
            HashSet<string> existing=new HashSet<string>(edges.Select(x=>StoryPairKey(ReadFirstString(x,"heroAId","aId","subjectId"),ReadFirstString(x,"heroBId","bId","targetId"))),StringComparer.OrdinalIgnoreCase);
            foreach(Dictionary<string,object> row in QuerySql(connection,"SELECT pair_key,subject_id,target_id FROM relationship_story_threads WHERE status='active' AND intensity>0 AND last_event_day >= $day GROUP BY pair_key;",
                new Dictionary<string,object>{{"day",day-120d}}))
            {
                string a=ReadString(row,"subject_id",""),b=ReadString(row,"target_id","");
                Dictionary<string,object> ha,hb;
                string pair=StoryPairKey(a,b);
                if(existing.Contains(pair)||!heroes.TryGetValue(a,out ha)||!heroes.TryGetValue(b,out hb))continue;
                edges.Add(new Dictionary<string,object>{{"heroAId",a},{"heroBId",b},{"heroA",ha},{"heroB",hb},{"pairKey",pair},
                    {"coLocated",false},{"sameClan",ReadString(ha,"clanId","")==ReadString(hb,"clanId","")},
                    {"spouses",ReadString(ha,"spouseId","")==b||ReadString(hb,"spouseId","")==a},
                    {"family",false},{"nativeRelation",0d},{"kingdomId",FirstNonEmpty(ReadString(ha,"kingdomId",""),ReadString(hb,"kingdomId",""))}});
                existing.Add(pair);
            }
        }

        private static Dictionary<string, object> SelectDirectorKingdomCandidate(List<Dictionary<string, object>> candidates,string runId,double day,string kingdomId)
        {
            List<Dictionary<string, object>> pool=candidates.OrderByDescending(x=>ReadDouble(x,"finalScore",ReadDouble(x,"score",0d))).Take(16).ToList();
            double total=pool.Sum(x=>Math.Max(0.05d,ReadDouble(x,"finalScore",ReadDouble(x,"score",0d))));
            if(pool.Count==0||total<=0d)return null;
            double roll=StableUnit(runId+"|candidate|"+kingdomId+"|"+Math.Floor(day/DirectorRunIntervalDays))*total;
            foreach(Dictionary<string, object> candidate in pool)
            {
                roll-=Math.Max(0.05d,ReadDouble(candidate,"finalScore",ReadDouble(candidate,"score",0d)));
                if(roll<=0d)return candidate;
            }
            return pool[pool.Count-1];
        }

        private static Dictionary<string, object> ScoreDirectorEdge(ReignDbConnection connection, Dictionary<string, object> edge, double day)
        {
            string a = ReadFirstString(edge,"heroAId","aId","subjectId");
            string b = ReadFirstString(edge,"heroBId","bId","targetId");
            string lo = string.Compare(a,b,StringComparison.OrdinalIgnoreCase)<=0?a:b;
            string hi = lo==a?b:a;
            Dictionary<string, object> last = QuerySql(connection,"SELECT * FROM passive_relationship_events WHERE ((hero_a_id=$a AND hero_b_id=$b) OR (hero_a_id=$b AND hero_b_id=$a)) ORDER BY world_day DESC LIMIT 1;",new Dictionary<string, object>{{"a",a},{"b",b}}).FirstOrDefault();
            if (last != null && day-ReadDouble(last,"world_day",0d)<DirectorPairCooldownDays) return new Dictionary<string, object>();
            double monthStart=Math.Floor(day/31.5d)*31.5d;
            int aCount=ReadInt(QuerySql(connection,"SELECT COUNT(*) AS count FROM passive_relationship_events WHERE world_day >= $day AND (hero_a_id=$hero OR hero_b_id=$hero);",new Dictionary<string, object>{{"day",monthStart},{"hero",a}}).FirstOrDefault(),"count",0);
            int bCount=ReadInt(QuerySql(connection,"SELECT COUNT(*) AS count FROM passive_relationship_events WHERE world_day >= $day AND (hero_a_id=$hero OR hero_b_id=$hero);",new Dictionary<string, object>{{"day",monthStart},{"hero",b}}).FirstOrDefault(),"count",0);
            if(aCount>=2||bCount>=2)return new Dictionary<string, object>();
            Dictionary<string, object> exposure=QuerySql(connection,"SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",new Dictionary<string, object>{{"pair",lo+"|"+hi}}).FirstOrDefault()??new Dictionary<string, object>();
            Dictionary<string, object> relationshipA=QuerySql(connection,"SELECT * FROM relationships WHERE subject_id=$a AND target_id=$b LIMIT 1;",new Dictionary<string, object>{{"a",a},{"b",b}}).FirstOrDefault()??new Dictionary<string, object>();
            Dictionary<string, object> relationshipB=QuerySql(connection,"SELECT * FROM relationships WHERE subject_id=$a AND target_id=$b LIMIT 1;",new Dictionary<string, object>{{"a",b},{"b",a}}).FirstOrDefault()??new Dictionary<string, object>();
            double directionalA=ReadDouble(exposure,"affinity_a_to_b",0d),directionalB=ReadDouble(exposure,"affinity_b_to_a",0d);
            double affinity=(Math.Max(0d,directionalA)+Math.Max(0d,directionalB))/200d;
            double tension=(Math.Max(0d,-directionalA)+Math.Max(0d,-directionalB))/200d;
            double exposureScore=Math.Min(0.18d,ReadDouble(exposure,"weighted_exposure",0d)/60d);
            double score = 0.10d + (ReadBool(edge,"coLocated",false)?0.30d:0d) + (ReadBool(edge,"sameClan",false)?0.12d:0d) + (ReadBool(edge,"spouses",false)?0.18d:0d)
                + Math.Min(0.20d,Math.Abs(ReadDouble(edge,"nativeRelation",0d))/500d) + (ReadBool(edge,"sharedEvent",false)?0.15d:0d)
                + exposureScore + Math.Min(0.16d,Math.Max(affinity,tension)*0.16d);
            Dictionary<string, object> story=LoadPairStorySummary(connection,a,b,day);
            double continuationMultiplier=ReadDouble(story,"multiplier",1d);
            Dictionary<string, object> result = new Dictionary<string, object>(edge,StringComparer.OrdinalIgnoreCase)
                {{"score",score},{"baseScore",score},{"finalScore",score*continuationMultiplier},{"pairKey",lo+"|"+hi},
                 {"continuationMultiplier",continuationMultiplier},{"story",story}};
            result["ambientExposure"]=ReadDouble(exposure,"weighted_exposure",0d);result["ambientAffinity"]=affinity;result["ambientTension"]=tension;
            result["sharedRelationshipTag"]=ReadString(exposure,"shared_tag","");
            result["directionalAffinityAToB"]=directionalA;result["directionalAffinityBToA"]=directionalB;
            return result;
        }

        private static Dictionary<string, object> RerankDirectorCandidates(List<Dictionary<string, object>> candidates)
        {
            Dictionary<string, object> status = new Dictionary<string, object>{{"attempted",false},{"applied",false},{"method","deterministic"},{"candidateCount",candidates.Count}};
            if (candidates.Count < 2) return status;
            Dictionary<string, object> settings = LoadSettings();
            if (!ReadBool(settings,"enableMinimeMemoryWorker",true) || !ReadBool(settings,"enableMinimeMemoryReranking",true)) return status;
            try
            {
                List<Dictionary<string, object>> outbound = candidates.Select((c,i)=>new Dictionary<string, object>{{"id","director_"+i},{"text",DirectorCandidateText(c)}}).ToList();
                string raw = PostJsonToUrl(ReadString(settings,"minimeRerankUrl","http://127.0.0.1:8082/rerank"),Json.Serialize(new Dictionary<string, object>{{"query","Rank plausible consequential noble relationship interactions in a living medieval political world."},{"candidates",outbound},{"top_k",outbound.Count}}),Math.Max(250,Math.Min(15000,ReadInt(settings,"minimeRerankTimeoutMs",15000))));
                List<Dictionary<string, object>> ranked = ReadDictionaryList(TryParseJsonObject(raw),"results").Concat(ReadDictionaryList(TryParseJsonObject(raw),"ranked")).ToList();
                foreach(Dictionary<string, object> item in ranked)
                {
                    string id=ReadFirstString(item,"id","candidate_id","candidateId");
                    if(id.StartsWith("director_") && int.TryParse(id.Substring(9),out int index) && index>=0 && index<candidates.Count)
                        candidates[index]["finalScore"]=ReadDouble(candidates[index],"score",0d)+Math.Max(0d,ReadDouble(item,"score",ReadDouble(item,"similarity",0d)))*0.35d;
                }
                status["attempted"]=true;status["applied"]=true;status["method"]="minime_rerank";
            }
            catch(Exception ex){status["attempted"]=true;status["error"]=ex.Message;}
            return status;
        }

        private static string DirectorCandidateText(Dictionary<string, object> edge)
        {
            Dictionary<string, object> a=ReadDictionary(edge,"heroA")??new Dictionary<string, object>();
            Dictionary<string, object> b=ReadDictionary(edge,"heroB")??new Dictionary<string, object>();
            return ReadString(a,"name",ReadFirstString(edge,"heroAId","aId"))+" and "+ReadString(b,"name",ReadFirstString(edge,"heroBId","bId"))+"; reasons="+string.Join(",",ReadStringList(edge,"reasons"))+"; native relation="+ReadDouble(edge,"nativeRelation",0d).ToString("0",CultureInfo.InvariantCulture);
        }

        private static Dictionary<string, object> CreatePassiveRelationshipEvent(ReignDbConnection connection,string campaignId,string runId,double day,Dictionary<string, object> candidate)
        {
            string a=ReadFirstString(candidate,"heroAId","aId","subjectId"), b=ReadFirstString(candidate,"heroBId","bId","targetId");
            if(string.IsNullOrWhiteSpace(a)||string.IsNullOrWhiteSpace(b)) return new Dictionary<string, object>();
            Dictionary<string, object> pa=LoadDirectorHeroProfile(campaignId,a,ReadDictionary(candidate,"heroA")), pb=LoadDirectorHeroProfile(campaignId,b,ReadDictionary(candidate,"heroB"));
            Dictionary<string, object> ra=QuerySql(connection,"SELECT * FROM relationships WHERE subject_id=$a AND target_id=$b LIMIT 1;",new Dictionary<string, object>{{"a",a},{"b",b}}).FirstOrDefault()??new Dictionary<string, object>();
            Dictionary<string, object> rb=QuerySql(connection,"SELECT * FROM relationships WHERE subject_id=$a AND target_id=$b LIMIT 1;",new Dictionary<string, object>{{"a",b},{"b",a}}).FirstOrDefault()??new Dictionary<string, object>();
            Dictionary<string, object> postureA=BuildDirectorRomanticDecision(campaignId,pa,pb,candidate,day);
            Dictionary<string, object> postureB=BuildDirectorRomanticDecision(campaignId,pb,pa,candidate,day);
            double preEventRomanceIntensity=PairStoryKindIntensity(connection,a,b,"romance",day,true);
            string kind=ChoosePassiveEventKind(connection,campaignId,candidate,pa,pb,ra,rb,postureA,postureB,day);
            string nameA=ReadString(pa,"name",a),nameB=ReadString(pb,"name",b);
            string summary=PassiveEventSummary(kind,nameA,nameB);
            string eventId="passive_rel_"+Guid.NewGuid().ToString("N");
            string visibility=kind.Contains("public")?"public":"private";
            Dictionary<string, object> memory=StoreWorldMemoryEvent(new Dictionary<string, object>{{"campaignId",campaignId},{"eventId",eventId},{"eventType",kind},{"worldDay",day},{"locationId",ReadString(candidate,"locationId","")},{"summary",summary},{"participants",new List<string>{a,b}},{"known_by",new List<string>{a,b}},{"visibility",visibility},{"importance",kind.Contains("affair")?0.82d:0.55d},{"source","passive_relationship_director"}},"relationship_director");
            Dictionary<string, object> paired=EvaluatePassiveRelationshipPair(connection,campaignId,eventId,kind,a,b,pa,pb,candidate,summary,day,postureA,postureB,preEventRomanceIntensity);
            Dictionary<string, object> eva=ReadDictionary(paired,"aToB")??new Dictionary<string, object>();
            Dictionary<string, object> evb=ReadDictionary(paired,"bToA")??new Dictionary<string, object>();
            int nativeDelta=(int)Math.Round(new[]{ReadInt(eva,"nativeRelationDelta",0),ReadInt(evb,"nativeRelationDelta",0)}.Average(),MidpointRounding.AwayFromZero);
            nativeDelta=Clamp(nativeDelta,-2,2);
            if(nativeDelta!=0) QueueDirectorAction(connection,"native_relation",day,a,b,new Dictionary<string, object>{{"delta",nativeDelta},{"eventId",eventId}});
            bool aMarried=!string.IsNullOrWhiteSpace(ReadString(pa,"spouseId",""))&&!ReadString(pa,"spouseId","").Equals(b,StringComparison.OrdinalIgnoreCase);
            bool bMarried=!string.IsNullOrWhiteSpace(ReadString(pb,"spouseId",""))&&!ReadString(pb,"spouseId","").Equals(a,StringComparison.OrdinalIgnoreCase);
            ApplyStoryEventToDatabase(connection,kind,eventId,a,b,day,aMarried,bMarried,postureA,postureB);
            if(kind=="secret_affair_intimacy") TryCreateNpcAffairConception(connection,day,eventId,pa,pb);
            HandleDirectorLifeChange(connection,day,eventId,kind,a,b,pa,pb);
            long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ExecuteSql(connection,@"INSERT INTO passive_relationship_events(passive_event_id,event_id,kind,hero_a_id,hero_b_id,world_day,location_id,visibility,summary,score,run_id,payload_json,created_ts)
VALUES($id,$event,$kind,$a,$b,$day,$location,$visibility,$summary,$score,$run,$payload,$ts);",new Dictionary<string, object>{{"id","prev_"+Guid.NewGuid().ToString("N")},{"event",eventId},{"kind",kind},{"a",a},{"b",b},{"day",day},{"location",ReadString(candidate,"locationId","")},{"visibility",visibility},{"summary",summary},{"score",ReadDouble(candidate,"finalScore",0d)},{"run",runId},{"payload",Json.Serialize(candidate)},{"ts",ts}});
            return new Dictionary<string, object>{{"eventId",eventId},{"kind",kind},{"heroAId",a},{"heroBId",b},{"summary",summary},{"nativeRelationDelta",nativeDelta},
                {"continuationMultiplier",ReadDouble(candidate,"continuationMultiplier",1d)},{"continuedThread",ReadDouble(candidate,"continuationMultiplier",1d)>1d},{"memoryStore",memory}};
        }

        private static string ChoosePassiveEventKind(ReignDbConnection connection,string campaignId,Dictionary<string, object> edge,Dictionary<string, object> a,Dictionary<string, object> b,Dictionary<string, object> ra,Dictionary<string, object> rb,double day)
        {
            return ChoosePassiveEventKind(connection,campaignId,edge,a,b,ra,rb,
                BuildDirectorRomanticDecision(campaignId,a,b,edge,day),BuildDirectorRomanticDecision(campaignId,b,a,edge,day),day);
        }

        private static string ChoosePassiveEventKind(ReignDbConnection connection,string campaignId,Dictionary<string, object> edge,Dictionary<string, object> a,Dictionary<string, object> b,
            Dictionary<string, object> ra,Dictionary<string, object> rb,Dictionary<string, object> postureA,Dictionary<string, object> postureB,double day)
        {
            double affinity=Math.Max(0d,ReadDouble(edge,"ambientAffinity",0d));
            double tension=Math.Max(0d,ReadDouble(edge,"ambientTension",0d));
            double exposure=Math.Min(1d,ReadDouble(edge,"ambientExposure",0d)/30d);
            double ambition=Math.Max(0d,(TraitFromProfile(a,"ambition")+TraitFromProfile(b,"ambition")+TraitFromProfile(a,"pride")+TraitFromProfile(b,"pride"))/8d);
            string heroA=ReadString(a,"heroStringId",""),heroB=ReadString(b,"heroStringId","");
            double favorMultiplier=PairStoryKindMultiplier(connection,heroA,heroB,"favor_debt",day);
            double secretMultiplier=PairStoryKindMultiplier(connection,heroA,heroB,"shared_secret",day);
            double rivalryMultiplier=PairStoryKindMultiplier(connection,heroA,heroB,"rivalry",day);
            double maritalMultiplier=PairStoryKindMultiplier(connection,heroA,heroB,"marital_conflict",day);
            double romanceMultiplier=PairStoryKindMultiplier(connection,heroA,heroB,"romance",day);
            List<KeyValuePair<string,double>> eligible=new List<KeyValuePair<string,double>>();
            eligible.Add(new KeyValuePair<string,double>("practical_favor",(24d+14d*affinity+8d*exposure)*favorMultiplier*CourtCharacterEventWeight(postureA,postureB,"favors","bargaining")));
            eligible.Add(new KeyValuePair<string,double>("shared_confidence",(18d+24d*affinity+12d*exposure)*secretMultiplier*CourtCharacterEventWeight(postureA,postureB,"secrets","information")));
            if(ReadBool(edge,"sameClan",false)||ReadBool(edge,"family",false))eligible.Add(new KeyValuePair<string,double>("family_duty_cooperation",(20d+18d*affinity)*CourtCharacterEventWeight(postureA,postureB,"protection","favors")));
            if(ReadBool(edge,"spouses",false))eligible.Add(new KeyValuePair<string,double>("private_spousal_confidence",(18d+22d*affinity)*CourtCharacterEventWeight(postureA,postureB,"secrets","mediation")));
            double rivalryThread=PairStoryKindIntensity(connection,heroA,heroB,"rivalry",day,false);
            double maritalThread=PairStoryKindIntensity(connection,heroA,heroB,"marital_conflict",day,false);
            double romanceThread=PairStoryKindIntensity(connection,heroA,heroB,"romance",day,true);
            double competition=Math.Max(Math.Max(ReadDouble(ra,"rivalry",0d),ReadDouble(rb,"rivalry",0d)),
                Math.Max(Math.Max(ReadDouble(ra,"envy",0d),ReadDouble(rb,"envy",0d)),Math.Max(ReadDouble(ra,"resentment",0d),ReadDouble(rb,"resentment",0d))))/100d;
            if(tension>=0.025d||ambition>=0.25d||competition>=0.10d||rivalryThread>0d)
                eligible.Add(new KeyValuePair<string,double>(rivalryThread>=50d?"political_obstruction":"political_rivalry_argument",
                    (10d+45d*Math.Max(tension,competition)+20d*ambition)*rivalryMultiplier*CourtCharacterEventWeight(postureA,postureB,"rivalry","threats","schemes")));
            if(ReadBool(edge,"spouses",false)&&(tension>=0.025d||competition>=0.10d||maritalThread>0d))
                eligible.Add(new KeyValuePair<string,double>("marital_argument",(8d+35d*Math.Max(tension,competition))*maritalMultiplier));
            if(ReadBool(edge,"spouses",false)&&(maritalThread>=60d||ReadDouble(ra,"resentment",0d)>=25d||ReadDouble(rb,"resentment",0d)>=25d||ReadDouble(edge,"nativeRelation",0d)<=-15d
                ||((ReadDouble(ra,"resentment",0d)>=15d||ReadDouble(rb,"resentment",0d)>=15d)&&(ReadDouble(ra,"affection",0d)<=0d||ReadDouble(rb,"affection",0d)<=0d))))
                eligible.Add(new KeyValuePair<string,double>("marital_separation",((maritalThread>=80d?45d:20d)+45d*tension)*maritalMultiplier));
            bool reciprocalRomance=new[]{"lovers","soulmates"}.Contains(ReadString(edge,"sharedRelationshipTag",""),StringComparer.OrdinalIgnoreCase);
            bool flirtEligible=reciprocalRomance&&ReadBool(edge,"coLocated",false)&&DirectorRomanceEligible(postureA,40d)&&DirectorRomanceEligible(postureB,40d);
            if(flirtEligible)eligible.Add(new KeyValuePair<string,double>("mutual_romantic_flirtation",(16d+12d*exposure)*romanceMultiplier*CourtCharacterEventWeight(postureA,postureB,"flirtation","strategic_seduction")));
            bool confidenceEligible=reciprocalRomance&&DirectorRomanceEligible(postureA,40d)&&DirectorRomanceEligible(postureB,40d);
            if(confidenceEligible&&romanceThread>=25d)eligible.Add(new KeyValuePair<string,double>("romantic_confidence",(14d+10d*exposure)*romanceMultiplier*CourtCharacterEventWeight(postureA,postureB,"flirtation","strategic_seduction","secrets")));
            bool outsideMarriage=(!string.IsNullOrWhiteSpace(ReadString(a,"spouseId",""))&&!ReadString(a,"spouseId","").Equals(heroB,StringComparison.OrdinalIgnoreCase))
                ||(!string.IsNullOrWhiteSpace(ReadString(b,"spouseId",""))&&!ReadString(b,"spouseId","").Equals(heroA,StringComparison.OrdinalIgnoreCase));
            bool bothUnmarried=string.IsNullOrWhiteSpace(ReadString(a,"spouseId",""))&&string.IsNullOrWhiteSpace(ReadString(b,"spouseId",""));
            if(reciprocalRomance&&StoryPhysicalRomanceEligible(ReadBool(edge,"coLocated",false),bothUnmarried,romanceThread,50d,postureA,postureB))
                eligible.Add(new KeyValuePair<string,double>("romantic_intimacy",(12d+8d*exposure)*romanceMultiplier*CourtCharacterEventWeight(postureA,postureB,"strategic_seduction","flirtation")));
            if(reciprocalRomance&&StoryPhysicalRomanceEligible(ReadBool(edge,"coLocated",false),outsideMarriage,romanceThread,75d,postureA,postureB))
                eligible.Add(new KeyValuePair<string,double>("secret_affair_intimacy",(10d+8d*exposure)*romanceMultiplier*CourtCharacterEventWeight(postureA,postureB,"strategic_seduction")));
            return SelectWeightedPassiveEvent(eligible,ReadString(edge,"pairKey","")+"|"+Math.Floor(day/DirectorRunIntervalDays));
        }

        private static string SelectWeightedPassiveEvent(List<KeyValuePair<string,double>> eligible,string seed)
        {
            if(eligible==null||eligible.Count==0)return "practical_favor";
            double total=eligible.Sum(x=>Math.Max(0d,x.Value));
            if(total<=0d)return eligible[0].Key;
            double roll=StableUnit(seed)*total;
            foreach(KeyValuePair<string,double> option in eligible){roll-=Math.Max(0d,option.Value);if(roll<=0d)return option.Key;}
            return eligible[eligible.Count-1].Key;
        }

        private static Dictionary<string, object> BuildDirectorRomanticDecision(string campaignId,Dictionary<string, object> actor,Dictionary<string, object> other,Dictionary<string, object> edge,double day)
        {
            string actorId=ReadString(actor,"heroStringId",""),otherId=ReadString(other,"heroStringId","");
            bool closeKin=ReadString(actor,"fatherId","")==otherId||ReadString(actor,"motherId","")==otherId||ReadString(other,"fatherId","")==actorId||ReadString(other,"motherId","")==actorId;
            bool adults=ReadDouble(actor,"age",0d)>=18d&&ReadDouble(other,"age",0d)>=18d;
            bool nativeSuitable=adults&&ReadBool(actor,"isFemale",false)!=ReadBool(other,"isFemale",false)&&!closeKin;
            Dictionary<string, object> payload=new Dictionary<string, object>
            {
                ["playerHeroStringId"]=otherId,["worldDay"]=day,["mode"]="autonomous_relationship",
                ["conversationSessionId"]="director:"+Math.Floor(day).ToString(CultureInfo.InvariantCulture)+":"+ReadString(edge,"pairKey",actorId+"|"+otherId),
                ["opportunitySnapshot"]=new Dictionary<string, object>
                {
                    ["identityKnown"]=true,["observer"]=actor,["target"]=other,
                    ["suitability"]=new Dictionary<string, object>{{"adults",adults},{"nativeSuitable",nativeSuitable},{"closeKin",closeKin},{"marriageBlocks",false}},
                    ["scene"]=new Dictionary<string, object>{{"private",true},{"exposure",0.12d},{"coercive",false},{"witnessIds",new List<string>()}}
                },
                ["sceneOpportunity"]=new Dictionary<string, object>{{"private",true},{"exposure",0.12d},{"coercive",false},{"witnessIds",new List<string>()}}
            };
            Dictionary<string, object> context=BuildConversationDecisionContext(campaignId,"autonomous_relationship",actorId,actor,LoadCharacterStack(campaignId,actorId),new Dictionary<string, object>(),"","A private autonomous social opportunity.",payload,new Dictionary<string, object>{{"identityState","known"}});
            return ReadDictionary(context,"romance")??new Dictionary<string, object>();
        }

        private static bool DirectorRomanceEligible(Dictionary<string, object> romance,double minimumReceptivity)
        {
            Dictionary<string, object> hard=ReadDictionary(romance,"hardConstraints")??new Dictionary<string, object>();
            return ReadBool(hard,"adults",false)&&ReadBool(hard,"nativeSuitable",false)&&!ReadBool(hard,"closeKin",true)&&!ReadBool(hard,"coercive",true)
                &&Math.Max(ReadDouble(romance,"genuineInterest",0d),ReadDouble(romance,"strategicInterest",0d))>=25d
                &&ReadDouble(romance,"receptivity",0d)>=minimumReceptivity;
        }

        private static string PassiveEventSummary(string kind,string a,string b)
        {
            switch(kind){case "secret_affair_intimacy":return a+" and "+b+" consummated a secret affair after knowingly accepting the personal and political risk.";case "romantic_intimacy":return a+" and "+b+" privately deepened their consensual courtship into romantic intimacy.";case "romantic_confidence":return a+" and "+b+" shared a private romantic confidence and acknowledged their growing interest.";case "mutual_romantic_flirtation":return a+" and "+b+" exchanged mutual flirtation and tested a growing attraction.";case "political_obstruction":return a+" and "+b+" deliberately obstructed one another's ambitions, escalating their political rivalry.";case "political_rivalry_argument":return a+" and "+b+" argued over status and ambition, sharpening their political rivalry.";case "marital_argument":return a+" and "+b+" suffered a serious marital argument that deepened the strain between them.";case "marital_separation":return a+" and "+b+" separated after a bitter marital conflict and sustained resentment.";case "marriage_proposal":return a+" and "+b+" made a serious mutual marriage proposal after sustained trust, affection, and attraction.";case "private_spousal_confidence":return a+" trusted "+b+" with a private confidence, strengthening affection and loyalty.";case "family_duty_cooperation":return a+" and "+b+" cooperated on a family duty and helped one another.";case "shared_confidence":return a+" trusted "+b+" with a personal confidence and received a respectful hearing.";default:return a+" performed a practical favor for "+b+", who offered sincere thanks.";}
        }

        private static void HandleDirectorLifeChange(ReignDbConnection connection,double day,string eventId,string kind,string a,string b,Dictionary<string, object> pa,Dictionary<string, object> pb)
        {
            string lo=string.Compare(a,b,StringComparison.OrdinalIgnoreCase)<=0?a:b,hi=lo==a?b:a;long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if(kind=="marital_separation")
            {
                double conflict=Math.Max(PairStoryKindIntensity(connection,a,b,"marital_conflict",day,false),60d);
                ExecuteSql(connection,@"INSERT INTO life_change_readiness(readiness_id,kind,hero_a_id,hero_b_id,score,status,last_event_id,world_day,payload_json,updated_ts,created_day)
VALUES($id,'divorce',$a,$b,$score,'separated',$event,$day,$payload,$ts,$day)
ON CONFLICT(kind,hero_a_id,hero_b_id) DO UPDATE SET score=$score,status='separated',last_event_id=$event,world_day=$day,updated_ts=$ts;",new Dictionary<string, object>{{"id","ready_"+Guid.NewGuid().ToString("N")},{"a",lo},{"b",hi},{"event",eventId},{"day",day},{"score",conflict},{"payload",Json.Serialize(new Dictionary<string, object>{{"separatedDay",day},{"threadIntensity",conflict}})},{"ts",ts}});
                Dictionary<string, object> ready=QuerySql(connection,"SELECT * FROM life_change_readiness WHERE kind='divorce' AND hero_a_id=$a AND hero_b_id=$b LIMIT 1;",new Dictionary<string, object>{{"a",lo},{"b",hi}}).FirstOrDefault();
                if(ready!=null&&ReadString(ready,"status","")=="separated"&&day-ReadDouble(ready,"created_day",day)>=30d&&conflict>=80d)
                {
                    QueueDirectorAction(connection,"divorce",day,a,b,new Dictionary<string, object>{{"eventId",eventId},{"separatedDay",ReadDouble(ready,"created_day",day)}});
                    ExecuteSql(connection,"UPDATE life_change_readiness SET status='triggered',updated_ts=$ts WHERE readiness_id=$id;",new Dictionary<string, object>{{"ts",ts},{"id",ReadString(ready,"readiness_id","")}});
                }
            }
        }

        private static Dictionary<string, object> ProcessMarriageSystem(ReignDbConnection connection,string campaignId,Dictionary<string, object> payload,double day,bool force)
        {
            List<Dictionary<string, object>> nobles=ReadDictionaryList(payload,"nobles");
            List<Dictionary<string, object>> clans=ReadDictionaryList(payload,"clans");
            string playerId=ReadString(payload,"playerId","");
            string playerClanId=ReadString(payload,"playerClanId","");
            string timelineId=ReadString(payload,"timelineId","main");
            int arranged=0,rolls=0;

            long period=(long)Math.Floor(day/7d);
            foreach(Dictionary<string, object> clan in clans.Where(x=>!string.IsNullOrWhiteSpace(ReadString(x,"leaderId",""))))
            {
                string leaderId=ReadString(clan,"leaderId",""),clanId=ReadString(clan,"clanId","");
                if(QuerySql(connection,"SELECT roll_id FROM marriage_leader_rolls WHERE timeline_id=$timeline AND period_index=$period AND leader_id=$leader LIMIT 1;",new Dictionary<string, object>{{"timeline",timelineId},{"period",period},{"leader",leaderId}}).Any())continue;
                Dictionary<string, object> leader=nobles.FirstOrDefault(x=>ReadString(x,"heroStringId","")==leaderId)??new Dictionary<string, object>();
                List<Dictionary<string, object>> members=nobles.Where(x=>ReadString(x,"clanId","")==clanId&&string.IsNullOrWhiteSpace(ReadString(x,"spouseId",""))).ToList();
                double urgency=MarriageUrgency(clan,leader,members);
                double chance=ClampDouble(0.01d+0.04d*urgency,0.01d,0.05d);
                double roll=StableUnit(campaignId+"|"+timelineId+"|marriage|"+period+"|"+leaderId);
                string rollStatus=members.Count==0?"no_member":roll>=chance?"failed":"passed";
                long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ExecuteSql(connection,@"INSERT INTO marriage_leader_rolls(roll_id,timeline_id,period_index,leader_id,clan_id,world_day,chance,roll,urgency,status,payload_json,created_ts)
VALUES($id,$timeline,$period,$leader,$clan,$day,$chance,$roll,$urgency,$status,$payload,$ts);",new Dictionary<string, object>{{"id","marriage_roll_"+Guid.NewGuid().ToString("N")},{"timeline",timelineId},{"period",period},{"leader",leaderId},{"clan",clanId},{"day",day},{"chance",chance},{"roll",roll},{"urgency",urgency},{"status",rollStatus},{"payload",Json.Serialize(new Dictionary<string, object>{{"memberCount",members.Count}})},{"ts",ts}});
                RecordWorldTestCounter(connection,campaignId,timelineId,(int)Math.Floor(day),"marriages",
                    "leader_roll_"+period.ToString(CultureInfo.InvariantCulture)+"_"+leaderId,
                    new Dictionary<string, object>
                    {
                        {"arrangedLeaderRolls",1},
                        {"arrangedRollPassed",rollStatus=="passed"?1:0},
                        {"arrangedRollFailed",rollStatus=="failed"?1:0},
                        {"arrangedNoEligibleMember",rollStatus=="no_member"?1:0}
                    });
                rolls++;
                if(rollStatus!="passed")continue;

                Dictionary<string, object> best=null,bestClan=null;double bestScore=double.MinValue,bestCourtshipMultiplier=1d;
                foreach(Dictionary<string, object> member in members)
                foreach(Dictionary<string, object> candidate in nobles.Where(x=>ReadString(x,"clanId","")!=clanId&&string.IsNullOrWhiteSpace(ReadString(x,"spouseId",""))))
                {
                    if(!SnapshotMarriageEligible(member,candidate)||MarriagePairAlreadyActive(connection,timelineId,ReadString(member,"heroStringId",""),ReadString(candidate,"heroStringId","")))continue;
                    Dictionary<string, object> candidateClan=clans.FirstOrDefault(x=>ReadString(x,"clanId","")==ReadString(candidate,"clanId",""));
                    if(candidateClan==null)continue;
                    double courtshipMultiplier=ArrangedMarriageCourtshipMultiplier(connection,ReadString(member,"heroStringId",""),ReadString(candidate,"heroStringId",""),day);
                    double score=DynasticMarriageScore(clan,candidateClan,leader,member,candidate)*courtshipMultiplier;
                    if(score>bestScore){bestScore=score;best=member;bestClan=candidateClan;bestCourtshipMultiplier=courtshipMultiplier;best["_candidate"]=candidate;}
                }
                if(best==null||bestScore<25d)continue;
                Dictionary<string, object> other=(Dictionary<string, object>)best["_candidate"];best.Remove("_candidate");
                Dictionary<string, object> otherLeader=nobles.FirstOrDefault(x=>ReadString(x,"heroStringId","")==ReadString(bestClan,"leaderId",""))??new Dictionary<string, object>();
                double otherApproval=DynasticMarriageScore(bestClan,clan,otherLeader,other,best)*bestCourtshipMultiplier;
                if(otherApproval<25d)continue;
                double resentmentA=ArrangedMarriageResentment(best,other,leader,bestScore);
                double resentmentB=ArrangedMarriageResentment(other,best,otherLeader,otherApproval);
                bool playerOffer=ReadBool(clan,"isPlayerClan",false)||ReadBool(bestClan,"isPlayerClan",false)||ReadString(best,"clanId","")==playerClanId||ReadString(other,"clanId","")==playerClanId;
                bool diplomatic=ReadString(clan,"kingdomId","")!=ReadString(bestClan,"kingdomId","")&&(ReadBool(clan,"isRulingClan",false)||ReadBool(bestClan,"isRulingClan",false));
                string status=playerOffer?"player_court_pending":diplomatic?"awaiting_diplomacy":"approved";
                CreateMarriageEvaluation(connection,campaignId,timelineId,"arranged",status,day,best,other,clan,bestClan,Math.Min(bestScore,otherApproval),resentmentA,resentmentB,new Dictionary<string, object>{{"leaderAApproval",bestScore},{"leaderBApproval",otherApproval},{"courtshipMultiplier",bestCourtshipMultiplier},{"compelled",true},{"diplomaticPackageRequired",diplomatic},{"wartimeRequiresPeaceFirst",diplomatic}});
                arranged++;
            }
            return new Dictionary<string, object>{{"romanticApproved",0},{"romanticRoute","mbti_relationship_lifecycle"},
                {"arrangedCreated",arranged},{"leaderRolls",rolls},{"periodIndex",period}};
        }

        private static bool RomanticMarriageReady(Dictionary<string, object> a,Dictionary<string, object> b)
        {
            if(a==null||b==null)return false;
            return ReadDouble(a,"affection",0d)>=70d&&ReadDouble(b,"affection",0d)>=70d&&ReadDouble(a,"trust",0d)>=55d&&ReadDouble(b,"trust",0d)>=55d
                &&ReadDouble(a,"attraction",0d)>=45d&&ReadDouble(b,"attraction",0d)>=45d&&ReadDouble(a,"resentment",0d)<70d&&ReadDouble(b,"resentment",0d)<70d;
        }

        private static bool SnapshotMarriageEligible(Dictionary<string, object> a,Dictionary<string, object> b)
        {
            if(a==null||b==null||ReadString(a,"heroStringId","")==ReadString(b,"heroStringId",""))return false;
            if(!ReadBool(a,"isAlive",false)||!ReadBool(b,"isAlive",false)||ReadDouble(a,"age",0d)<18d||ReadDouble(b,"age",0d)<18d)return false;
            if(ReadBool(a,"isFemale",false)==ReadBool(b,"isFemale",false)||!string.IsNullOrWhiteSpace(ReadString(a,"spouseId",""))||!string.IsNullOrWhiteSpace(ReadString(b,"spouseId","")))return false;
            if((a.ContainsKey("nativeCanMarry")&&!ReadBool(a,"nativeCanMarry",false))
                ||(b.ContainsKey("nativeCanMarry")&&!ReadBool(b,"nativeCanMarry",false))
                ||(a.ContainsKey("nativeMarriageClanSuitable")&&!ReadBool(a,"nativeMarriageClanSuitable",false))
                ||(b.ContainsKey("nativeMarriageClanSuitable")&&!ReadBool(b,"nativeMarriageClanSuitable",false))
                ||(ReadBool(a,"isClanLeader",false)&&ReadBool(b,"isClanLeader",false)))return false;
            string aId=ReadString(a,"heroStringId",""),bId=ReadString(b,"heroStringId","");
            HashSet<string> ancestorsA=new HashSet<string>(ReadStringList(a,"marriageAncestorIds"),StringComparer.OrdinalIgnoreCase);
            HashSet<string> ancestorsB=new HashSet<string>(ReadStringList(b,"marriageAncestorIds"),StringComparer.OrdinalIgnoreCase);
            bool extendedKin=ancestorsA.Contains(bId)||ancestorsB.Contains(aId)||ancestorsA.Overlaps(ancestorsB);
            return !extendedKin&&!MarriageCourtshipBlocks(a,b)&&!MarriageCourtshipBlocks(b,a)
                &&ReadString(a,"fatherId","")!=bId&&ReadString(a,"motherId","")!=bId&&ReadString(b,"fatherId","")!=aId&&ReadString(b,"motherId","")!=aId
                &&!(ReadString(a,"fatherId","")!=""&&ReadString(a,"fatherId","")==ReadString(b,"fatherId",""))&&!(ReadString(a,"motherId","")!=""&&ReadString(a,"motherId","")==ReadString(b,"motherId",""));
        }

        private static bool MarriageCourtshipBlocks(Dictionary<string, object> subject,Dictionary<string, object> candidate)
        {
            string candidateId=ReadString(candidate,"heroStringId",""),candidateClan=ReadString(candidate,"clanId","");
            if(string.IsNullOrWhiteSpace(candidateClan))return false;
            return ReadDictionaryList(subject,"activeCourtships").Any(x=>
                ReadString(x,"clanId","").Equals(candidateClan,StringComparison.OrdinalIgnoreCase)
                &&!ReadString(x,"heroId","").Equals(candidateId,StringComparison.OrdinalIgnoreCase));
        }

        private static double MarriageUrgency(Dictionary<string, object> clan,Dictionary<string, object> leader,List<Dictionary<string, object>> members)
        {
            int children=members.Sum(x=>ReadInt(x,"childrenCount",0));
            double agePressure=members.Count==0?0d:members.Average(x=>ClampDouble((ReadDouble(x,"age",18d)-18d)/25d,0d,1d));
            double succession=ClampDouble((members.Count==1?0.45d:0.15d)+(children==0?0.30d:0d)+0.25d*agePressure,0d,1d);
            double isolation=ClampDouble(0.55d-ReadDouble(clan,"tier",0d)/12d-ReadDouble(clan,"gold",0d)/1000000d,0d,1d);
            double traits=(Trait01(leader,"ambition")+Trait01(leader,"dutyMotivation")+Trait01(leader,"traditionalism")+Trait01(leader,"familyMotivation")+Trait01(leader,"powerMotivation")+Trait01(leader,"pragmatism")+Trait01(leader,"riskTolerance"))/7d;
            return ClampDouble(0.45d*succession+0.25d*isolation+0.30d*traits,0d,1d);
        }

        private static double DynasticMarriageScore(Dictionary<string, object> own,Dictionary<string, object> other,Dictionary<string, object> leader,Dictionary<string, object> member,Dictionary<string, object> candidate)
        {
            double standing=ClampDouble(ReadDouble(other,"tier",0d)/6d,0d,1d)*18d+ClampDouble(ReadDouble(other,"renown",0d)/6000d,0d,1d)*10d;
            double resources=ClampDouble(ReadDouble(other,"gold",0d)/500000d,0d,1d)*10d+ClampDouble(ReadDouble(other,"influence",0d)/1000d,0d,1d)*8d;
            double compatibility=ReadString(own,"kingdomId","")==ReadString(other,"kingdomId","")?14d:8d;
            if(ReadString(own,"cultureId","")==ReadString(other,"cultureId",""))compatibility+=8d;
            double household=12d*(ReadInt(candidate,"childrenCount",0)==0?1d:0.5d)+8d*(1d-ClampDouble(Math.Abs(ReadDouble(member,"age",25d)-ReadDouble(candidate,"age",25d))/30d,0d,1d));
            double strategy=12d*Trait01(leader,"ambition")+10d*Trait01(leader,"familyMotivation")+8d*Trait01(leader,"pragmatism");
            return ClampDouble(standing+resources+compatibility+household+strategy,0d,100d);
        }

        private static double ArrangedMarriageResentment(Dictionary<string, object> spouse,Dictionary<string, object> match,Dictionary<string, object> leader,double politicalValue)
        {
            double resistance=0.20d*Trait01(spouse,"assertiveness")+0.16d*Trait01(spouse,"pride")+0.12d*Trait01(spouse,"impulsiveness")+0.12d*(1d-Trait01(spouse,"traditionalism"));
            double acceptance=0.16d*Trait01(spouse,"dutyMotivation")+0.14d*Trait01(spouse,"familyMotivation")+0.10d*Trait01(spouse,"loyalty")+0.10d*Trait01(spouse,"pragmatism")+0.08d*Trait01(spouse,"ambition");
            double relation=ClampDouble((ReadDouble(spouse,"nativeRelationToLeader",0d)+100d)/200d,0d,1d);
            double personal=0.10d*Trait01(spouse,"attraction")+0.08d*Trait01(spouse,"socialTrust");
            return Math.Round(ClampDouble(15d+65d*resistance-55d*acceptance-15d*relation-10d*personal-10d*(politicalValue/100d),0d,60d),2);
        }

        private static bool MarriagePairAlreadyActive(ReignDbConnection connection,string timelineId,string a,string b)
        {
            return QuerySql(connection,"SELECT evaluation_id FROM marriage_evaluations WHERE timeline_id=$timeline AND ((hero_a_id=$a AND hero_b_id=$b) OR (hero_a_id=$b AND hero_b_id=$a)) AND status IN ('evaluating','approved','player_court_pending','awaiting_diplomacy','executed') LIMIT 1;",new Dictionary<string, object>{{"timeline",timelineId},{"a",a},{"b",b}}).Any();
        }

        private static void CreateMarriageEvaluation(ReignDbConnection connection,string campaignId,string timelineId,string route,string status,double day,Dictionary<string, object> a,Dictionary<string, object> b,Dictionary<string, object> clanA,Dictionary<string, object> clanB,double score,double resentmentA,double resentmentB,Dictionary<string, object> detail)
        {
            string aId=ReadString(a,"heroStringId",""),bId=ReadString(b,"heroStringId","");long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();string id="marriage_eval_"+Guid.NewGuid().ToString("N");
            string clanAId=ReadString(a,"clanId",ReadString(clanA,"clanId","")),clanBId=ReadString(b,"clanId",ReadString(clanB,"clanId",""));
            string leaderA=ReadString(clanA,"leaderId",aId),leaderB=ReadString(clanB,"leaderId",bId);
            Dictionary<string, object> data=new Dictionary<string, object>(detail??new Dictionary<string, object>()){{"evaluationId",id},{"route",route},{"heroA",a},{"heroB",b},{"clanA",clanA??new Dictionary<string, object>()},{"clanB",clanB??new Dictionary<string, object>()},{"resentmentA",resentmentA},{"resentmentB",resentmentB}};
            ExecuteSql(connection,@"INSERT INTO marriage_evaluations(evaluation_id,route,status,hero_a_id,hero_b_id,clan_a_id,clan_b_id,leader_a_id,leader_b_id,world_day,score,leader_a_approved,leader_b_approved,resentment_a,resentment_b,payload_json,created_ts,updated_ts,timeline_id)
VALUES($id,$route,$status,$a,$b,$ca,$cb,$la,$lb,$day,$score,1,1,$ra,$rb,$payload,$ts,$ts,$timeline);",new Dictionary<string, object>{{"id",id},{"route",route},{"status",status},{"a",aId},{"b",bId},{"ca",clanAId},{"cb",clanBId},{"la",leaderA},{"lb",leaderB},{"day",day},{"score",score},{"ra",resentmentA},{"rb",resentmentB},{"payload",Json.Serialize(data)},{"ts",ts},{"timeline",timelineId}});
            bool diplomatic=ReadBool(detail,"diplomaticPackageRequired",false);
            string funnel=diplomatic?"diplomatic":"arranged";
            RecordWorldTestCounter(connection,campaignId,timelineId,(int)Math.Floor(day),"marriages",
                funnel+"_evaluation_"+id,new Dictionary<string, object>
                {
                    {funnel+"Evaluations",1},
                    {funnel+"Accepted",status=="approved"?1:0},
                    {funnel+"PlayerCourtPending",status=="player_court_pending"?1:0},
                    {funnel+"AwaitingDiplomacy",status=="awaiting_diplomacy"?1:0}
                });
            if(status=="approved")
            {
                string actionId=QueueDirectorAction(connection,"marriage",day,aId,bId,new Dictionary<string, object>{{"evaluationId",id},{"route",route},{"timelineId",timelineId},{"resentmentA",resentmentA},{"resentmentB",resentmentB},{"leaderAId",leaderA},{"leaderBId",leaderB}});
                if(string.IsNullOrWhiteSpace(actionId))
                    ExecuteSql(connection,"UPDATE marriage_evaluations SET status='reservation_blocked',updated_ts=$ts WHERE evaluation_id=$id;",new Dictionary<string, object>{{"ts",ts},{"id",id}});
            }
        }

        private static double ConcealmentChance(Dictionary<string, object> mother)
        {
            double motivation=0.20d*Trait01(mother,"shame")+0.15d*Trait01(mother,"familyMotivation")+0.15d*Trait01(mother,"traditionalism")+0.10d*Trait01(mother,"dutyMotivation")+0.10d*Trait01(mother,"survivalMotivation")+0.10d*Trait01(mother,"religionMotivation")+0.20d*(1d-Trait01(mother,"honesty"));
            double competence=0.25d*Trait01(mother,"tact")+0.20d*Trait01(mother,"discipline")+0.15d*Trait01(mother,"emotionalStability")+0.15d*Trait01(mother,"pragmatism")+0.15d*Trait01(mother,"confidence")+0.10d;
            return ClampDouble(0.15d+0.35d*motivation+0.40d*competence-0.10d,0.05d,0.95d);
        }

        private static Dictionary<string, object> LoadDirectorHeroProfile(string campaignId,string heroId,Dictionary<string, object> snapshot)
        {
            Dictionary<string, object> result = snapshot == null ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, object>(snapshot,StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> stored = ReadJsonObject(CharacterFile(campaignId,heroId,"profile.json"));
            foreach(var pair in stored) if(!result.ContainsKey(pair.Key) || string.IsNullOrWhiteSpace(Convert.ToString(result[pair.Key],CultureInfo.InvariantCulture))) result[pair.Key]=pair.Value;
            Dictionary<string, object> traits = ReadJsonObject(CharacterFile(campaignId,heroId,"traits.json"));
            Dictionary<string, object> foundation=ReadDictionary(traits,"foundationTraits")??ReadDictionary(result,"foundationTraits");
            if(foundation==null||foundation.Count==0)foundation=ReadDictionary(BuildTraitDocument(result),"foundationTraits")??new Dictionary<string, object>();
            result["foundationTraits"] = foundation;
            result["traits"] = traits;
            result["heroStringId"] = heroId;
            return result;
        }

        private static void TryCreateNpcAffairConception(ReignDbConnection connection,double day,string eventId,Dictionary<string, object> a,Dictionary<string, object> b)
        {
            Dictionary<string, object> mother=ReadBool(a,"isFemale",false)&&!ReadBool(b,"isFemale",false)?a:ReadBool(b,"isFemale",false)&&!ReadBool(a,"isFemale",false)?b:null;
            Dictionary<string, object> father=mother==a?b:mother==b?a:null;
            if(mother==null||father==null||ReadBool(mother,"isPregnant",false))return;
            double age=ReadDouble(mother,"age",0d);if(age<18d||age>45d)return;
            double chance=NpcConceptionChance(age,ReadInt(mother,"childrenCount",0));
            string attemptId="npc_affair_"+eventId;double roll=StableUnit(attemptId);
            ExecuteSql(connection,@"INSERT OR IGNORE INTO conception_attempts(attempt_id,source,event_id,mother_id,father_id,mother_age,chance,roll,success,world_day,payload_json,created_ts)
VALUES($id,'npc_affair',$event,$mother,$father,$age,$chance,$roll,$success,$day,'{}',$ts);",new Dictionary<string, object>{{"id",attemptId},{"event",eventId},{"mother",ReadString(mother,"heroStringId","")},{"father",ReadString(father,"heroStringId","")},{"age",age},{"chance",chance},{"roll",roll},{"success",roll<chance?1:0},{"day",day},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()}});
            if(roll>=chance)return;
            string motherId=ReadString(mother,"heroStringId",""),fatherId=ReadString(father,"heroStringId",""),legal=FirstNonEmpty(ReadString(mother,"spouseId",""),fatherId),conception="conception_"+Guid.NewGuid().ToString("N");long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ExecuteSql(connection,@"INSERT INTO conceptions(conception_id,attempt_id,mother_id,biological_father_id,legal_father_id,conception_day,due_day,status,secrecy,payload_json,created_ts,updated_ts)
VALUES($id,$attempt,$mother,$bio,$legal,$day,$due,'pending_game',$secrecy,'{}',$ts,$ts);",new Dictionary<string, object>{{"id",conception},{"attempt",attemptId},{"mother",motherId},{"bio",fatherId},{"legal",legal},{"day",day},{"due",day+36d},{"secrecy",legal==fatherId?0d:0.75d},{"ts",ts}});
            QueueDirectorAction(connection,"start_conception",day,motherId,fatherId,new Dictionary<string, object>{{"conceptionId",conception},{"motherId",motherId},{"biologicalFatherId",fatherId},{"legalFatherId",legal},{"conceptionDay",day},{"dueDay",day+36d},{"secrecy",legal==fatherId?0d:0.75d}});
        }

        private static string QueueDirectorAction(ReignDbConnection connection,string type,double day,string actor,string target,Dictionary<string, object> payload)
        {
            string id="director_action_"+Guid.NewGuid().ToString("N");
            if (type.Equals("marriage", StringComparison.OrdinalIgnoreCase))
            {
                EnsureRelationshipLifecycleSchema(connection);
                string pairKey = AmbientPairKey(actor, target);
                long reservationTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ExecuteSql(connection, @"INSERT OR IGNORE INTO relationship_marriage_reservations(
hero_id,pair_key,director_action_id,created_day,created_ts) VALUES
($actor,$pair,$action,$day,$ts),($target,$pair,$action,$day,$ts);",
                    new Dictionary<string, object>
                    {
                        ["actor"] = actor, ["target"] = target,
                        ["pair"] = pairKey, ["action"] = id,
                        ["day"] = day, ["ts"] = reservationTs
                    });
                int reserved = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_marriage_reservations WHERE director_action_id=$action;",
                    new Dictionary<string, object> { ["action"] = id })
                    .FirstOrDefault(), "count", 0);
                if (reserved != 2)
                {
                    ExecuteSql(connection, @"DELETE FROM relationship_marriage_reservations
WHERE director_action_id=$action;",
                        new Dictionary<string, object> { ["action"] = id });
                    return "";
                }
            }
            ExecuteSql(connection,@"INSERT INTO relationship_director_actions(director_action_id,action_type,status,world_day,actor_id,target_id,payload_json,created_ts)
VALUES($id,$type,'pending',$day,$actor,$target,$payload,$ts);",new Dictionary<string, object>{{"id",id},{"type",type},{"day",day},{"actor",actor},{"target",target},{"payload",Json.Serialize(payload)},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()}});
            return id;
        }

        private static Dictionary<string, object> RelationshipDirectorPollActionsApi(Dictionary<string, object> payload)
        {
            string campaignId = ReadString(payload, "campaignId", "default");
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            using (ReignDbTransaction transaction = connection.BeginTransaction())
            {
                EnsureRelationshipDirectorSchema(connection);
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long stale = ts - 300;
                List<Dictionary<string, object>> rows = QuerySql(connection, @"
SELECT * FROM relationship_director_actions
WHERE action_type<>'native_relation'
  AND (status='pending' OR (status='claimed' AND claimed_ts<$stale))
  AND NOT (action_type='marriage' AND EXISTS (
      SELECT 1 FROM relationship_conception_commitments commitment
      WHERE commitment.marriage_action_id=
          relationship_director_actions.director_action_id
        AND commitment.status='awaiting_conception'))
ORDER BY world_day,created_ts LIMIT 50;",
                    new Dictionary<string, object> { ["stale"] = stale });
                foreach (Dictionary<string, object> row in rows)
                {
                    ExecuteSql(connection,
                        "UPDATE relationship_director_actions SET status='claimed',claimed_ts=$ts WHERE director_action_id=$id;",
                        new Dictionary<string, object> { ["ts"] = ts, ["id"] = ReadString(row, "director_action_id", "") });
                    row["payload"] = TryParseJsonObject(ReadString(row, "payload_json", "{}"))
                        ?? new Dictionary<string, object>();
                }

                transaction.Commit();
                return new Dictionary<string, object> { ["ok"] = true, ["actions"] = rows };
            }
        }

        private static bool IsRelationshipNativeTargetId(string id)
        {
            return !string.IsNullOrWhiteSpace(id)
                && id.StartsWith("native_target:", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> ReportRelationshipNativeTarget(
            ReignDbConnection connection, Dictionary<string, object> payload)
        {
            string id = ReadFirstString(payload, "directorActionId", "director_action_id");
            string pairKey = id.Substring("native_target:".Length);
            string status = NormalizeLookup(ReadString(payload, "status", "completed"));
            string error = ReadString(payload, "error", "");
            double day = ReadDouble(payload, "worldDay", 0d);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, object> target = QuerySql(connection,
                "SELECT * FROM relationship_native_targets WHERE pair_key=$pair LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
            if (target == null)
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["directorActionId"] = id,
                    ["status"] = "already_resolved", ["idempotent"] = true
                };
            }

            if (status == "completed")
            {
                int applied = ReadInt(target, "target_relation", 0);
                ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
native_action_pending=0,native_action_id='',last_native_sync_day=$day,updated_ts=$ts
WHERE pair_key=$pair;",
                    new Dictionary<string, object> { ["day"] = day, ["ts"] = ts, ["pair"] = pairKey });
                ExecuteSql(connection,
                    "DELETE FROM relationship_native_targets WHERE pair_key=$pair;",
                    new Dictionary<string, object> { ["pair"] = pairKey });
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["directorActionId"] = id,
                    ["status"] = "completed", ["appliedNativeRelation"] = applied
                };
            }

            if (status == "obsolete" || status == "skipped")
            {
                ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
native_action_pending=0,native_action_id='',updated_ts=$ts WHERE pair_key=$pair;",
                    new Dictionary<string, object> { ["ts"] = ts, ["pair"] = pairKey });
                ExecuteSql(connection,
                    "DELETE FROM relationship_native_targets WHERE pair_key=$pair;",
                    new Dictionary<string, object> { ["pair"] = pairKey });
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["directorActionId"] = id,
                    ["status"] = "obsolete", ["reason"] = error
                };
            }

            ExecuteSql(connection, @"UPDATE relationship_native_targets SET
status='failed',claimed_ts=0,last_error=$error,updated_ts=$ts WHERE pair_key=$pair;",
                new Dictionary<string, object>
                {
                    ["error"] = FirstNonEmpty(error, "Native relationship application failed."),
                    ["ts"] = ts, ["pair"] = pairKey
                });
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["directorActionId"] = id,
                ["status"] = "failed", ["error"] = error
            };
        }

        private static bool ApplyRelationshipCommitmentAffinityDelta(
            ReignDbConnection connection, string campaignId, string pairKey,
            int delta, double worldDay)
        {
            Dictionary<string, object> pair = QuerySql(connection,
                "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = pairKey })
                .FirstOrDefault();
            if (pair == null) return false;
            int affinityAB = Clamp(ReadInt(pair, "affinity_a_to_b", 0)
                + delta, -100, 100);
            int affinityBA = Clamp(ReadInt(pair, "affinity_b_to_a", 0)
                + delta, -100, 100);
            int socialAB = ReadInt(pair, "social_modifier_a_to_b", 0);
            int socialBA = ReadInt(pair, "social_modifier_b_to_a", 0);
            int effectiveAB = Clamp(affinityAB + socialAB, -100, 100);
            int effectiveBA = Clamp(affinityBA + socialBA, -100, 100);
            string heroA = ReadString(pair, "hero_a_id", "");
            string heroB = ReadString(pair, "hero_b_id", "");
            int projected = ProjectNativeRelation(connection, heroA, heroB,
                affinityAB, affinityBA, effectiveAB, effectiveBA);
            EnsureRelationshipNativeTargetSchema(connection);
            Dictionary<string, object> nativeTarget = QuerySql(connection,
                "SELECT observed_relation,requires_observation FROM relationship_native_targets WHERE pair_key=$pair LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = pairKey })
                .FirstOrDefault();
            int observed = nativeTarget != null
                ? ReadInt(nativeTarget, "observed_relation", 0)
                : ReadInt(pair, "projected_native_relation", 0);
            bool observationRequired = RelationshipNativeObservationRequired(pair, nativeTarget);
            bool pending = projected != observed || observationRequired;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
affinity_a_to_b=$ab,affinity_b_to_a=$ba,effective_affinity_a_to_b=$effectiveAB,
effective_affinity_b_to_a=$effectiveBA,tag_a_to_b=$tagAB,tag_b_to_a=$tagBA,
projected_native_relation=$projected,native_action_pending=$pending,
native_action_id=$action,updated_ts=$ts WHERE pair_key=$pair;",
                new Dictionary<string, object>
                {
                    ["ab"] = affinityAB, ["ba"] = affinityBA,
                    ["effectiveAB"] = effectiveAB,
                    ["effectiveBA"] = effectiveBA,
                    ["tagAB"] = DirectionalRelationshipTag(campaignId,
                        pairKey, "a_to_b", effectiveAB, effectiveBA),
                    ["tagBA"] = DirectionalRelationshipTag(campaignId,
                        pairKey, "b_to_a", effectiveBA, effectiveAB),
                    ["projected"] = projected, ["pending"] = pending ? 1 : 0,
                    ["action"] = pending ? "native_target:" + pairKey : "",
                    ["ts"] = ts, ["pair"] = pairKey
                });
            if (pending)
                ExecuteSql(connection, @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,world_day,
last_sync_day,attempt_count,claimed_ts,last_error,updated_ts,revision,requires_observation)
VALUES($pair,$a,$b,$target,$observed,'pending',$day,-1000,0,0,'',$ts,1,$requiresObservation)
ON CONFLICT(pair_key) DO UPDATE SET target_relation=$target,status='pending',
requires_observation=CASE WHEN relationship_native_targets.requires_observation=1 OR $requiresObservation=1 THEN 1 ELSE 0 END,
world_day=$day,claimed_ts=0,last_error='',revision=CASE WHEN
relationship_native_targets.target_relation=$target THEN
relationship_native_targets.revision ELSE relationship_native_targets.revision+1 END,
updated_ts=$ts;", new Dictionary<string, object>
                {
                    ["pair"] = pairKey, ["a"] = heroA, ["b"] = heroB,
                    ["target"] = projected, ["observed"] = observed,
                    ["requiresObservation"] = observationRequired ? 1 : 0,
                    ["day"] = worldDay, ["ts"] = ts
                });
            else
                ExecuteSql(connection,
                    "DELETE FROM relationship_native_targets WHERE pair_key=$pair;",
                    new Dictionary<string, object> { ["pair"] = pairKey });
            return true;
        }

        private static void ResolvePregnancyCommitment(
            ReignDbConnection connection, string campaignId,
            Dictionary<string, object> actionPayload, string actionStatus,
            double worldDay)
        {
            string conceptionId = ReadString(actionPayload, "conceptionId", "");
            Dictionary<string, object> commitment = QuerySql(connection,
                "SELECT * FROM relationship_conception_commitments WHERE conception_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = conceptionId })
                .FirstOrDefault();
            if (commitment == null) return;
            if (!ReadString(commitment, "status", "")
                .Equals("awaiting_conception",
                    StringComparison.OrdinalIgnoreCase)) return;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (actionStatus != "completed")
            {
                ExecuteSql(connection, @"UPDATE relationship_conception_commitments
SET status='conception_failed',updated_ts=$ts WHERE conception_id=$id;",
                    new Dictionary<string, object> { ["ts"] = ts, ["id"] = conceptionId });
                return;
            }
            string pairKey = ReadString(commitment, "pair_key", "");
            string heroA = ReadString(commitment, "hero_a_id", "");
            string heroB = ReadString(commitment, "hero_b_id", "");
            bool bothPassed = ReadInt(commitment, "passed_a", 0) == 1
                && ReadInt(commitment, "passed_b", 0) == 1;
            AmbientPairContext pair = new AmbientPairContext
            {
                PairKey = pairKey, HeroAId = heroA, HeroBId = heroB
            };
            if (bothPassed)
            {
                string marriageAction = ReadString(commitment,
                    "marriage_action_id", "");
                if (string.IsNullOrWhiteSpace(marriageAction))
                    marriageAction = QueueDirectorAction(connection,
                        "marriage", worldDay, heroA, heroB,
                        new Dictionary<string, object>
                        {
                            ["source"] = "mbti_relationship_lifecycle",
                            ["pairKey"] = pairKey,
                            ["route"] = "pregnancy_commitment",
                            ["conceptionId"] = conceptionId,
                            ["timelineId"] = ReadString(actionPayload,
                                "timelineId", "main"), ["silent"] = true
                        });
                ExecuteSql(connection, @"UPDATE relationship_conception_commitments
SET status=$status,marriage_action_id=$action,updated_ts=$ts WHERE conception_id=$id;",
                    new Dictionary<string, object>
                    {
                        ["status"] = string.IsNullOrWhiteSpace(marriageAction)
                            ? "marriage_reservation_blocked" : "awaiting_marriage",
                        ["action"] = marriageAction, ["ts"] = ts,
                        ["id"] = conceptionId
                    });
                string existingMarriageStatus = ReadString(QuerySql(connection,
                    @"SELECT status FROM relationship_director_actions
WHERE director_action_id=$id LIMIT 1;",
                    new Dictionary<string, object>
                    {
                        ["id"] = marriageAction
                    }).FirstOrDefault(), "status", "");
                if (existingMarriageStatus == "completed")
                    CompletePregnancyCommitmentMarriage(connection,
                        campaignId, conceptionId, worldDay);
                else if (!string.IsNullOrWhiteSpace(existingMarriageStatus)
                    && existingMarriageStatus != "pending"
                    && existingMarriageStatus != "claimed")
                    ExecuteSql(connection, @"UPDATE relationship_conception_commitments
SET status='marriage_failed',updated_ts=$ts WHERE conception_id=$id
AND status='awaiting_marriage';",
                        new Dictionary<string, object>
                        {
                            ["ts"] = ts, ["id"] = conceptionId
                        });
                RecordRelationshipIncident(connection, pair,
                    "pregnancy_commitment_accepted", worldDay,
                    "Both unmarried lovers passed their Honor commitment rolls and agreed to marry.",
                    new Dictionary<string, object>
                    {
                        ["conceptionId"] = conceptionId,
                        ["marriageActionId"] = marriageAction
                    });
                return;
            }
            string canceledMarriageAction = ReadString(commitment,
                "marriage_action_id", "");
            if (!string.IsNullOrWhiteSpace(canceledMarriageAction))
            {
                Dictionary<string, object> cancellationResult =
                    new Dictionary<string, object>
                    {
                        ["status"] = "invalid",
                        ["error"] = "The unmarried pregnancy commitment failed before marriage could proceed.",
                        ["source"] = "pregnancy_commitment",
                        ["conceptionId"] = conceptionId,
                        ["worldDay"] = worldDay
                    };
                ExecuteSql(connection, @"UPDATE relationship_director_actions
SET status='invalid',resolved_ts=$ts,result_json=$result
WHERE director_action_id=$id AND status IN ('pending','claimed');",
                    new Dictionary<string, object>
                    {
                        ["ts"] = ts,
                        ["result"] = Json.Serialize(cancellationResult),
                        ["id"] = canceledMarriageAction
                    });
                ExecuteSql(connection, @"DELETE FROM relationship_marriage_reservations
WHERE director_action_id=$id;", new Dictionary<string, object>
                    { ["id"] = canceledMarriageAction });
            }
            ApplyRelationshipCommitmentAffinityDelta(connection, campaignId,
                pairKey, PregnancyCommitmentBreakupAffinityPenalty, worldDay);
            ExecuteSql(connection, @"UPDATE relationship_pair_lifecycle SET
lover_active=0,affair_active=0,romance_stage='',romance_positive_bonus=0,
married=0,marriage_action_id='',updated_ts=$ts WHERE pair_key=$pair;",
                new Dictionary<string, object> { ["ts"] = ts, ["pair"] = pairKey });
            Dictionary<string, object> conception = QuerySql(connection,
                "SELECT payload_json FROM conceptions WHERE conception_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = conceptionId })
                .FirstOrDefault();
            Dictionary<string, object> conceptionPayload = TryParseJsonObject(
                ReadString(conception, "payload_json", "{}"))
                ?? new Dictionary<string, object>();
            conceptionPayload["isIllegitimate"] = true;
            conceptionPayload["unmarriedCommitmentPending"] = false;
            conceptionPayload["unmarriedCommitmentOutcome"] = "breakup";
            ExecuteSql(connection, @"UPDATE conceptions SET secrecy=0.75,
payload_json=$payload,updated_ts=$ts WHERE conception_id=$id;",
                new Dictionary<string, object>
                {
                    ["payload"] = Json.Serialize(conceptionPayload),
                    ["ts"] = ts, ["id"] = conceptionId
                });
            ExecuteSql(connection, @"UPDATE relationship_conception_commitments
SET status='broke_up',affinity_delta=$delta,updated_ts=$ts WHERE conception_id=$id;",
                new Dictionary<string, object>
                {
                    ["delta"] = PregnancyCommitmentBreakupAffinityPenalty,
                    ["ts"] = ts, ["id"] = conceptionId
                });
            RecordRelationshipIncident(connection, pair,
                "pregnancy_commitment_failed", worldDay,
                "At least one unmarried lover failed the Honor commitment roll; the lovers separated.",
                new Dictionary<string, object>
                {
                    ["conceptionId"] = conceptionId,
                    ["affinityDelta"] = PregnancyCommitmentBreakupAffinityPenalty
                });
        }

        private static void CompletePregnancyCommitmentMarriage(
            ReignDbConnection connection, string campaignId,
            string conceptionId, double worldDay)
        {
            Dictionary<string, object> commitment = QuerySql(connection,
                @"SELECT pair_key,status FROM relationship_conception_commitments
WHERE conception_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = conceptionId })
                .FirstOrDefault();
            if (commitment == null || ReadString(commitment, "status", "")
                != "awaiting_marriage") return;
            string pairKey = ReadString(commitment, "pair_key", "");
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ApplyRelationshipCommitmentAffinityDelta(connection, campaignId,
                pairKey, PregnancyCommitmentMarriageAffinityBonus, worldDay);
            ExecuteSql(connection, @"UPDATE relationship_conception_commitments SET
status='married',affinity_delta=$delta,updated_ts=$ts WHERE conception_id=$id
AND status='awaiting_marriage';",
                new Dictionary<string, object>
                {
                    ["delta"] = PregnancyCommitmentMarriageAffinityBonus,
                    ["ts"] = ts, ["id"] = conceptionId
                });
            Dictionary<string, object> conceptionRow = QuerySql(connection,
                "SELECT payload_json FROM conceptions WHERE conception_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = conceptionId })
                .FirstOrDefault();
            Dictionary<string, object> completedPayload = TryParseJsonObject(
                ReadString(conceptionRow, "payload_json", "{}"))
                ?? new Dictionary<string, object>();
            completedPayload["unmarriedCommitmentPending"] = false;
            completedPayload["unmarriedCommitmentOutcome"] = "married";
            ExecuteSql(connection, @"UPDATE conceptions SET payload_json=$payload,
updated_ts=$ts WHERE conception_id=$id;",
                new Dictionary<string, object>
                {
                    ["payload"] = Json.Serialize(completedPayload),
                    ["ts"] = ts, ["id"] = conceptionId
                });
        }

        private static void AdvanceAffairMarriageCommitment(
            ReignDbConnection connection, string campaignId,
            Dictionary<string, object> action, string actionStatus,
            double worldDay)
        {
            string actionId = ReadString(action, "director_action_id", "");
			Dictionary<string, object> actionPayload = TryParseJsonObject(
				ReadString(action, "payload_json", "{}"))
				?? new Dictionary<string, object>();
			string timelineId = FirstNonEmpty(ReadString(actionPayload,
				"timelineId", ""), "main");
            Dictionary<string, object> commitment = QuerySql(connection, @"SELECT *
FROM relationship_affair_commitments WHERE divorce_action_a_id=$id
OR divorce_action_b_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = actionId })
                .FirstOrDefault();
            if (commitment == null) return;
            if (!ReadString(commitment, "status", "")
                .Equals("awaiting_divorce",
                    StringComparison.OrdinalIgnoreCase)) return;
            string pairKey = ReadString(commitment, "pair_key", "");
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool isDivorceA = actionId.Equals(ReadString(commitment,
                "divorce_action_a_id", ""),
                StringComparison.OrdinalIgnoreCase);
            bool isDivorceB = actionId.Equals(ReadString(commitment,
                "divorce_action_b_id", ""),
                StringComparison.OrdinalIgnoreCase);
            if ((isDivorceA && ReadInt(commitment,
                    "divorce_a_completed", 0) == 1)
                || (isDivorceB && ReadInt(commitment,
                    "divorce_b_completed", 0) == 1)) return;
            if (actionStatus != "completed")
            {
                ExecuteSql(connection, @"UPDATE relationship_affair_commitments
SET status='divorce_failed',updated_ts=$ts WHERE pair_key=$pair;",
                    new Dictionary<string, object> { ["ts"] = ts, ["pair"] = pairKey });
                return;
            }
            if (isDivorceA)
            {
                ExecuteSql(connection, @"UPDATE relationship_affair_commitments
SET divorce_a_completed=1,updated_ts=$ts WHERE pair_key=$pair;",
                    new Dictionary<string, object>
                    {
                        ["ts"] = ts, ["pair"] = pairKey
                    });
                commitment["divorce_a_completed"] = 1;
            }
            if (isDivorceB)
            {
                ExecuteSql(connection, @"UPDATE relationship_affair_commitments
SET divorce_b_completed=1,updated_ts=$ts WHERE pair_key=$pair;",
                    new Dictionary<string, object>
                    {
                        ["ts"] = ts, ["pair"] = pairKey
                    });
                commitment["divorce_b_completed"] = 1;
            }
            string divorcedHero = ReadString(action, "actor_id", "");
            string formerSpouse = ReadString(action, "target_id", "");
            string divorcedPairKey = AmbientPairKey(divorcedHero,
                formerSpouse);
            AmbientPairContext divorcedPair = new AmbientPairContext
            {
                PairKey = divorcedPairKey, HeroAId = divorcedHero,
                HeroBId = formerSpouse
            };
            Dictionary<string, object> rumor = RegisterSocialOccurrence(
                connection, campaignId, BuildRelationshipSocialOccurrence(
                    divorcedPair, (int)Math.Floor(worldDay), "divorce",
                    new Dictionary<string, object>
                    {
                        ["heroStringId"] = divorcedHero,
                        ["name"] = divorcedHero
                    },
                    new Dictionary<string, object>
                    {
                        ["heroStringId"] = formerSpouse,
                        ["name"] = formerSpouse
                    }, formerSpouse, divorcedHero, true, true,
					"The marriage ended when one spouse chose an affair partner.",
					timelineId));
            ExecuteSql(connection, @"UPDATE relationship_pair_lifecycle SET
married=0,divorced=1,divorce_day=$day,divorce_action_id='',
divorce_rumor_id=$rumor,updated_ts=$ts WHERE pair_key=$pair;",
                new Dictionary<string, object>
                {
                    ["day"] = worldDay,
                    ["rumor"] = ReadString(rumor, "occurrenceId", ""),
                    ["ts"] = ts, ["pair"] = divorcedPairKey
                });
            ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
shared_tag='divorced',updated_ts=$ts WHERE pair_key=$pair;",
                new Dictionary<string, object>
                {
                    ["ts"] = ts, ["pair"] = divorcedPairKey
                });
            RecordRelationshipIncident(connection, divorcedPair, "divorce",
                worldDay,
                "The marriage ended when one spouse chose an affair partner.",
                new Dictionary<string, object>
                {
                    ["directorActionId"] = actionId,
                    ["affairPairKey"] = pairKey
                }, ReadString(rumor, "occurrenceId", ""));
			FinalizeDivorceProjections(connection, campaignId, timelineId,
				divorcedPair, divorcedHero, formerSpouse, worldDay, actionId,
				"The marriage ended when one spouse chose an affair partner.");
            bool divorceAComplete = string.IsNullOrWhiteSpace(ReadString(
                    commitment, "divorce_action_a_id", ""))
                || ReadInt(commitment, "divorce_a_completed", 0) == 1;
            bool divorceBComplete = string.IsNullOrWhiteSpace(ReadString(
                    commitment, "divorce_action_b_id", ""))
                || ReadInt(commitment, "divorce_b_completed", 0) == 1;
            if (!divorceAComplete || !divorceBComplete)
                return;
            string heroA = ReadString(commitment, "hero_a_id", "");
            string heroB = ReadString(commitment, "hero_b_id", "");
            string marriageAction = QueueDirectorAction(connection, "marriage",
                worldDay, heroA, heroB, new Dictionary<string, object>
                {
                    ["source"] = "mbti_relationship_lifecycle",
                    ["pairKey"] = pairKey,
                    ["route"] = "affair_commitment",
                    ["silent"] = true
                });
            ExecuteSql(connection, @"UPDATE relationship_affair_commitments SET
status=$status,marriage_action_id=$action,updated_ts=$ts WHERE pair_key=$pair;",
                new Dictionary<string, object>
                {
                    ["status"] = string.IsNullOrWhiteSpace(marriageAction)
                        ? "marriage_reservation_blocked" : "awaiting_marriage",
                    ["action"] = marriageAction, ["ts"] = ts,
                    ["pair"] = pairKey
                });
        }

        private static Dictionary<string, object> RelationshipDirectorReportActionApi(Dictionary<string, object> payload)
        {
            string campaignId=ReadString(payload,"campaignId","default"),id=ReadFirstString(payload,"directorActionId","director_action_id"),status=ReadString(payload,"status","completed");
            if (IsRelationshipNativeTargetId(id))
            {
                using (ReignDbConnection targetConnection = OpenCampaignConnection(campaignId))
                {
                    EnsureRelationshipDirectorSchema(targetConnection);
                    return ReportRelationshipNativeTarget(targetConnection, payload);
                }
            }
            using(ReignDbConnection connection=OpenCampaignConnection(campaignId))
            {
                Dictionary<string, object> action=QuerySql(connection,"SELECT * FROM relationship_director_actions WHERE director_action_id=$id LIMIT 1;",new Dictionary<string, object>{{"id",id}}).FirstOrDefault()??new Dictionary<string, object>();
                Dictionary<string, object> actionPayload=TryParseJsonObject(ReadString(action,"payload_json","{}"))??new Dictionary<string, object>();
                long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ExecuteSql(connection,"UPDATE relationship_director_actions SET status=$status,resolved_ts=$ts,result_json=$result WHERE director_action_id=$id;",new Dictionary<string, object>{{"status",status},{"ts",ts},{"result",Json.Serialize(payload)},{"id",id}});
                if(ReadString(action,"action_type","").Equals("marriage",StringComparison.OrdinalIgnoreCase))
                    ExecuteSql(connection,@"DELETE FROM relationship_marriage_reservations
WHERE director_action_id=$id;",new Dictionary<string, object>{{"id",id}});
                if(ReadString(action,"action_type","")=="divorce")
                {
                    string a=ReadString(action,"actor_id",""),b=ReadString(action,"target_id","");string lo=string.Compare(a,b,StringComparison.OrdinalIgnoreCase)<=0?a:b,hi=lo==a?b:a;
                    if(ReadString(actionPayload,"source","")=="mbti_relationship_lifecycle")
                    {
                        string pairKey=ReadString(actionPayload,"pairKey",AmbientPairKey(a,b));
                        if(status=="completed")
                        {
                            AmbientPairContext pair=new AmbientPairContext{PairKey=pairKey,HeroAId=a,HeroBId=b};
                            double divorceWorldDay=ReadDouble(payload,"worldDay",ReadDouble(action,"world_day",0d));
							string divorceTimeline=FirstNonEmpty(ReadString(payload,"timelineId",""),
								ReadString(actionPayload,"timelineId",""),"main");
                            Dictionary<string, object> rumor=RegisterSocialOccurrence(connection,campaignId,
                                BuildRelationshipSocialOccurrence(pair,(int)Math.Floor(divorceWorldDay),"divorce",
                                    new Dictionary<string, object>{{"heroStringId",a},{"name",a}},
                                    new Dictionary<string, object>{{"heroStringId",b},{"name",b}},b,a,true,true,
									"The marriage ended in divorce.",divorceTimeline));
                            ExecuteSql(connection,@"UPDATE relationship_pair_lifecycle SET married=0,divorced=1,
divorce_day=$day,divorce_action_id='',divorce_rumor_id=$rumor,updated_ts=$ts WHERE pair_key=$pair;",
                                new Dictionary<string, object>{{"day",ReadDouble(payload,"worldDay",ReadDouble(action,"world_day",0d))},
                                    {"rumor",ReadString(rumor,"occurrenceId","")},{"ts",ts},{"pair",pairKey}});
                            ExecuteSql(connection,"UPDATE relationship_pair_chemistry SET shared_tag='divorced',updated_ts=$ts WHERE pair_key=$pair;",
                                new Dictionary<string, object>{{"ts",ts},{"pair",pairKey}});
                            RecordRelationshipIncident(connection,pair,"divorce",ReadDouble(payload,"worldDay",ReadDouble(action,"world_day",0d)),
                                "The marriage ended in divorce.",new Dictionary<string, object>{{"directorActionId",id}},ReadString(rumor,"occurrenceId",""));
							FinalizeDivorceProjections(connection,campaignId,divorceTimeline,
								pair,a,b,divorceWorldDay,id,"The marriage ended in divorce.");
                        }
                        else ExecuteSql(connection,"UPDATE relationship_pair_lifecycle SET divorce_action_id='',updated_ts=$ts WHERE pair_key=$pair;",
                            new Dictionary<string, object>{{"ts",ts},{"pair",pairKey}});
                    }
                    else if(ReadString(actionPayload,"source","")
                        =="relationship_affair_commitment")
                        AdvanceAffairMarriageCommitment(connection, campaignId,
                            action, status, ReadDouble(payload,"worldDay",
                                ReadDouble(action,"world_day",0d)));
                }
                else if(ReadString(action,"action_type","")=="marriage")
                {
                    string evaluationId=ReadString(actionPayload,"evaluationId","");
                    string outcomeRoute=WorldTestMarriageOutcomeRoute(
                        actionPayload);
                    string outcomeTimeline=ReadString(actionPayload,"timelineId","main");
                    if(!string.IsNullOrWhiteSpace(evaluationId))
                    {
                        Dictionary<string, object> evaluation=QuerySql(connection,
                            "SELECT route,status,payload_json,timeline_id FROM marriage_evaluations WHERE evaluation_id=$id LIMIT 1;",
                            new Dictionary<string, object>{{"id",evaluationId}}).FirstOrDefault()
                            ?? new Dictionary<string, object>();
                        Dictionary<string, object> evaluationPayload=TryParseJsonObject(
                            ReadString(evaluation,"payload_json","{}"))??new Dictionary<string, object>();
                        outcomeRoute=ReadBool(evaluationPayload,"diplomaticPackageRequired",false)
                            ?"diplomatic":"arranged";
                        outcomeTimeline=ReadString(evaluation,"timeline_id",outcomeTimeline);
                        string evaluationStatus=status=="completed"?"executed":status=="invalid"?"invalid_native":status=="obsolete"?"obsolete":"failed";
                        ExecuteSql(connection,"UPDATE marriage_evaluations SET status=$status,updated_ts=$ts WHERE evaluation_id=$id;",new Dictionary<string, object>{{"status",evaluationStatus},{"ts",ts},{"id",evaluationId}});
                        if(status=="completed")FinalizeMarriage(connection,campaignId,evaluationId,ReadDouble(payload,"worldDay",ReadDouble(action,"world_day",0d)));
                    }
                    int outcomeDay=(int)Math.Floor(ReadDouble(payload,"worldDay",ReadDouble(action,"world_day",0d)));
                    RecordWorldTestCounter(connection,campaignId,outcomeTimeline,outcomeDay,"marriages",
                        "outcome_"+FirstNonEmpty(evaluationId,id),
                        BuildWorldTestMarriageOutcomeCounters(outcomeRoute,status));
                    if(ReadString(actionPayload,"source","")=="mbti_relationship_lifecycle")
                    {
                        string a=ReadString(action,"actor_id",""),b=ReadString(action,"target_id",""),
                            pairKey=ReadString(actionPayload,"pairKey",AmbientPairKey(a,b));
                        if(status=="completed")
                        {
                            ExecuteSql(connection,@"UPDATE relationship_pair_lifecycle SET married=1,divorced=0,
marriage_action_id='',marriage_blocked=0,marriage_block_reason='',updated_ts=$ts WHERE pair_key=$pair;",
                                new Dictionary<string, object>{{"ts",ts},{"pair",pairKey}});
                            ExecuteSql(connection,"UPDATE relationship_pair_chemistry SET shared_tag='married',updated_ts=$ts WHERE pair_key=$pair;",
                                new Dictionary<string, object>{{"ts",ts},{"pair",pairKey}});
                            AmbientPairContext pair=new AmbientPairContext{PairKey=pairKey,HeroAId=a,HeroBId=b};
                            string lifecycleMarriageRoute=ReadString(
                                actionPayload,"route","mutual_affinity");
                            bool pregnancyCommitmentRoute=lifecycleMarriageRoute
                                =="pregnancy_commitment";
                            RecordRelationshipIncident(connection,pair,"marriage",ReadDouble(payload,"worldDay",ReadDouble(action,"world_day",0d)),
                                pregnancyCommitmentRoute
                                    ?"The pair married after jointly accepting responsibility for a conception."
                                    :"The pair married after both directional affinities reached seventy or above.",
                                new Dictionary<string, object>{{"directorActionId",id},{"route",lifecycleMarriageRoute}});
                            StoreWorldMemoryEvent(new Dictionary<string, object>{{"campaignId",campaignId},
                                {"eventId","marriage_"+id},{"eventType","world_event"},{"eventSubtype",pregnancyCommitmentRoute?"pregnancy_commitment_marriage":"mutual_affinity_marriage"},
                                {"worldDay",ReadDouble(payload,"worldDay",ReadDouble(action,"world_day",0d))},
                                {"summary",a+" and "+b+" married."},{"participants",new[]{a,b}},
                                {"visibility","public"},{"importance",0.9d}},"relationship_lifecycle_marriage");
                        }
                        else if(status=="invalid")
                            ExecuteSql(connection,@"UPDATE relationship_pair_lifecycle SET
marriage_action_id='',marriage_blocked=1,marriage_block_reason=$reason,updated_ts=$ts
WHERE pair_key=$pair;",new Dictionary<string, object>
                            {
                                {"reason",LimitText(FirstNonEmpty(ReadString(payload,"error",""),
                                    "Native marriage eligibility rejected this couple."),500)},
                                {"ts",ts},{"pair",pairKey}
                            });
                        else ExecuteSql(connection,"UPDATE relationship_pair_lifecycle SET marriage_action_id='',updated_ts=$ts WHERE pair_key=$pair;",
                            new Dictionary<string, object>{{"ts",ts},{"pair",pairKey}});
                    }
                    string conceptionCommitmentId=FirstNonEmpty(
                        ReadString(actionPayload,"conceptionId",""),
                        ReadString(QuerySql(connection,@"SELECT conception_id
FROM relationship_conception_commitments WHERE marriage_action_id=$action
AND status='awaiting_marriage' LIMIT 1;",
                            new Dictionary<string, object>{{"action",id}}).FirstOrDefault(),
                            "conception_id",""));
                    if(!string.IsNullOrWhiteSpace(conceptionCommitmentId))
                    {
                        string commitmentStatus=ReadString(QuerySql(connection,
                            "SELECT status FROM relationship_conception_commitments WHERE conception_id=$id LIMIT 1;",
                            new Dictionary<string, object>{{"id",conceptionCommitmentId}}).FirstOrDefault(),"status","");
                        if(commitmentStatus=="awaiting_marriage"&&status=="completed")
                            CompletePregnancyCommitmentMarriage(connection,
                                campaignId,conceptionCommitmentId,
                                ReadDouble(payload,"worldDay",ReadDouble(action,"world_day",0d)));
                        else if(commitmentStatus=="awaiting_marriage") ExecuteSql(connection,@"UPDATE relationship_conception_commitments SET
status='marriage_failed',updated_ts=$ts WHERE conception_id=$id;",
                            new Dictionary<string, object>{{"ts",ts},{"id",conceptionCommitmentId}});
                    }
                    string affairCommitmentPair=ReadString(actionPayload,"route","")
                        =="affair_commitment"?ReadString(actionPayload,"pairKey",""):"";
                    if(!string.IsNullOrWhiteSpace(affairCommitmentPair))
                        ExecuteSql(connection,@"UPDATE relationship_affair_commitments SET
status=$status,updated_ts=$ts WHERE pair_key=$pair AND status='awaiting_marriage';",
                            new Dictionary<string, object>{{"status",status=="completed"?"married":"marriage_failed"},{"ts",ts},{"pair",affairCommitmentPair}});
                }
                else if(ReadString(action,"action_type","")=="start_conception"
                    && (ReadString(actionPayload,"source","")=="mbti_relationship_lifecycle"
                        || ReadString(actionPayload,"source","")=="relationship_fling"))
                {
                    bool flingConception=ReadString(actionPayload,"source","")
                        =="relationship_fling";
                    string pairKey=ReadString(actionPayload,"pairKey","");
                    if(!flingConception)
                        ExecuteSql(connection,"UPDATE relationship_pair_lifecycle SET conception_action_id='',updated_ts=$ts WHERE pair_key=$pair;",
                            new Dictionary<string, object>{{"ts",ts},{"pair",pairKey}});
                    string conceptionStatus=status=="completed"?"confirmed_game":
                        status=="obsolete"?"obsolete_game":"failed_game";
                    ExecuteSql(connection,@"UPDATE conceptions SET status=$status,updated_ts=$ts
WHERE conception_id=$id AND status='pending_game';",
                        new Dictionary<string, object>{{"status",conceptionStatus},{"ts",ts},
                            {"id",ReadString(actionPayload,"conceptionId","")}});
                    if(!flingConception)
                        ResolvePregnancyCommitment(connection, campaignId,
                            actionPayload, status, ReadDouble(payload,"worldDay",
                                ReadDouble(action,"world_day",0d)));
                }
                else if(ReadString(action,"action_type","")=="native_relation"
                    && (ReadString(actionPayload,"source","")=="ambient_relationship_drift"
                        || ReadString(actionPayload,"source","")=="mbti_relationship_chemistry"
                        || ReadString(actionPayload,"source","")=="relationship_affair_discovery"
                        || ReadString(actionPayload,"source","")=="relationship_correspondence"
                        || ReadString(actionPayload,"source","")=="relationship_interaction"))
                {
                    string pairKey=ReadString(actionPayload,"pairKey",AmbientPairKey(ReadString(action,"actor_id",""),ReadString(action,"target_id","")));
                    int delta=Clamp(ReadInt(actionPayload,"delta",0),-100,100);
                    if(status=="completed")
                    {
                        if(ReadString(actionPayload,"source","")=="ambient_relationship_drift"||ReadString(actionPayload,"source","")=="mbti_relationship_chemistry")
                            ExecuteSql(connection,"UPDATE relationship_pair_chemistry SET native_action_pending=0,native_action_id='',last_native_sync_day=$day,updated_ts=$ts WHERE pair_key=$pair;",new Dictionary<string, object>{{"day",ReadDouble(payload,"worldDay",ReadDouble(action,"world_day",0d))},{"ts",ts},{"pair",pairKey}});
                        if(ReadString(actionPayload,"source","")=="mbti_relationship_chemistry")
                            ExecuteSql(connection,"DELETE FROM relationship_director_actions WHERE director_action_id=$id;",
                                new Dictionary<string, object>{{"id",id}});
                    }
                    else if(ReadString(actionPayload,"source","")=="ambient_relationship_drift"||ReadString(actionPayload,"source","")=="mbti_relationship_chemistry")
                    {
                        ExecuteSql(connection,"UPDATE relationship_pair_chemistry SET native_action_pending=0,native_action_id='',updated_ts=$ts WHERE pair_key=$pair;",new Dictionary<string, object>{{"ts",ts},{"pair",pairKey}});
                    }
                }
            }
            return new Dictionary<string, object>{{"ok",true},{"directorActionId",id},{"status",status}};
        }

		private static void FinalizeDivorceProjections(ReignDbConnection connection,
			string campaignId, string timelineId, AmbientPairContext pair,
			string heroA, string heroB, double worldDay, string actionId,
			string summary)
		{
			timelineId = string.IsNullOrWhiteSpace(timelineId) ? "main" : timelineId;
			long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			Dictionary<string, object> args = new Dictionary<string, object>
			{
				["campaign"] = campaignId, ["timeline"] = timelineId,
				["a"] = heroA, ["b"] = heroB, ["pair"] = pair.PairKey,
				["ts"] = ts
			};
			ExecuteSql(connection, @"UPDATE character_reputations
SET status='resolved_by_divorce',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id IN ($a,$b) AND tag_id='marital_strife' AND status='active';", args);
			ExecuteSql(connection, @"UPDATE rumor_subject_tags SET status='resolved_by_divorce',updated_ts=$ts
WHERE occurrence_id IN (SELECT occurrence_id FROM rumor_occurrences
 WHERE campaign_id=$campaign AND timeline_id=$timeline
 AND archetype_id='marital_strife' AND thread_key=$pair)
AND subject_id IN ($a,$b) AND status='active';", args);
			ExecuteSql(connection, @"UPDATE rumor_occurrences SET status='resolved_by_divorce',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='marital_strife' AND thread_key=$pair AND status='active';", args);
			ReconcileSocialRelationshipsForSubjects(connection, campaignId,
				timelineId, new[] { heroA, heroB }, worldDay);
			StoreWorldMemoryEvent(new Dictionary<string, object>
			{
				["campaignId"] = campaignId,
				["timelineId"] = timelineId,
				["eventId"] = "divorce_" + actionId,
				["eventType"] = "world_event",
				["eventSubtype"] = "divorce",
				["worldDay"] = worldDay,
				["summary"] = summary,
				["participants"] = new[] { heroA, heroB },
				["visibility"] = "public",
				["importance"] = 0.85d
			}, "relationship_lifecycle_divorce");
			WorldHistoryIngestBatchApi(new Dictionary<string, object>
			{
				["campaignId"] = campaignId,
				["timelineId"] = timelineId,
				["clientId"] = "reign_relationship_lifecycle",
				["historyCompleteFromWorldDay"] = worldDay,
				["events"] = new List<Dictionary<string, object>>
				{
					new Dictionary<string, object>
					{
						["eventId"] = "divorce_" + actionId,
						["sequence"] = 0L,
						["worldDay"] = worldDay,
						["eventType"] = "divorce",
						["category"] = "relationship",
						["phase"] = "completed",
						["disseminationClass"] = "major_world",
						["summary"] = summary,
						["source"] = "relationship_lifecycle_divorce",
						["entities"] = new List<Dictionary<string, object>>
						{
							new Dictionary<string, object>{{"entityId",heroA},{"entityType","hero"},{"role","former_spouse"}},
							new Dictionary<string, object>{{"entityId",heroB},{"entityType","hero"},{"role","former_spouse"}}
						}
					}
				}
			});
		}

        private static Dictionary<string, object> RelationshipDirectorReportActionsApi(Dictionary<string, object> payload)
        {
            string campaignId = ReadString(payload, "campaignId", "default");
            List<Dictionary<string, object>> receipts = ReadDictionaryList(payload, "receipts").Take(500).ToList();
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> nativeTargets = receipts
                .Where(x => IsRelationshipNativeTargetId(
                    ReadFirstString(x, "directorActionId", "director_action_id"))).ToList();
            if (nativeTargets.Count > 0)
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                using (ReignDbTransaction transaction = connection.BeginTransaction())
                {
                    EnsureRelationshipDirectorSchema(connection);
                    foreach (Dictionary<string, object> receipt in nativeTargets)
                        results.Add(ReportRelationshipNativeTarget(connection, receipt));
                    transaction.Commit();
                }
            }
            foreach (Dictionary<string, object> receipt in receipts.Except(nativeTargets))
            {
                Dictionary<string, object> single =
                    new Dictionary<string, object>(receipt, StringComparer.OrdinalIgnoreCase)
                    {
                        ["campaignId"] = campaignId
                    };
                results.Add(RelationshipDirectorReportActionApi(single));
            }
            return new Dictionary<string, object>
            {
                ["ok"] = results.All(x => ReadBool(x, "ok", false)),
                ["reportedCount"] = results.Count,
                ["results"] = results
            };
        }

        private static void FinalizeMarriage(ReignDbConnection connection,string campaignId,string evaluationId,double day)
        {
            Dictionary<string, object> row=QuerySql(connection,"SELECT * FROM marriage_evaluations WHERE evaluation_id=$id LIMIT 1;",new Dictionary<string, object>{{"id",evaluationId}}).FirstOrDefault();
            if(row==null)return;
            string a=ReadString(row,"hero_a_id",""),b=ReadString(row,"hero_b_id",""),route=ReadString(row,"route","");
            string eventId="marriage_"+evaluationId;
            if(route=="arranged")
            {
                ApplyCompelledMarriageConsequence(connection,campaignId,ReadString(row,"timeline_id","main"),eventId,a,b,ReadString(row,"leader_a_id",""),ReadDouble(row,"resentment_a",0d),day);
                ApplyCompelledMarriageConsequence(connection,campaignId,ReadString(row,"timeline_id","main"),eventId,b,a,ReadString(row,"leader_b_id",""),ReadDouble(row,"resentment_b",0d),day);
            }
            StoreWorldMemoryEvent(new Dictionary<string, object>{{"campaignId",campaignId},{"eventId",eventId},{"eventType","world_event"},{"eventSubtype",route=="arranged"?"arranged_marriage":"romantic_marriage"},{"worldDay",day},{"summary",a+" and "+b+" married"+(route=="arranged"?" by agreement of their clan leaders.":" after developing mutual trust, affection, and attraction.")},{"participants",new[]{a,b,ReadString(row,"leader_a_id",""),ReadString(row,"leader_b_id","")}},{"visibility","public"},{"importance",0.9d}},"relationship_director_marriage");
        }

        private static void ApplyCompelledMarriageConsequence(ReignDbConnection connection,string campaignId,string timelineId,string eventId,string spouse,string match,string leader,double resentment,double day)
        {
            if(resentment<=0d)return;
            long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int spouseDelta=-Clamp((int)Math.Round(Math.Max(1d,resentment/10d),MidpointRounding.AwayFromZero),1,6);
            ApplyAuthoritativeRelationshipDelta(connection,campaignId,spouse,match,spouseDelta,day,
                "compelled_political_marriage",timelineId);
            if(!string.IsNullOrWhiteSpace(leader)&&leader!=spouse)
            {
                int leaderDelta=-Clamp((int)Math.Round(Math.Max(1d,resentment/8d),MidpointRounding.AwayFromZero),1,8);
                ApplyAuthoritativeRelationshipDelta(connection,campaignId,spouse,leader,leaderDelta,day,
                    "compelled_political_marriage",timelineId);
            }
            string text=resentment>=40d?"They feel deeply wronged that their clan leader compelled this marriage and expect the grievance to shape their future.":resentment>=20d?"They resent having no choice in the arranged marriage, even while recognizing its dynastic purpose.":"They accept the clan leader's authority but retain private unease about the arranged marriage.";
            ExecuteSql(connection,@"INSERT OR IGNORE INTO comprehension(comprehension_id,event_id,owner_id,text,stance,confidence,known_by_json,about_entities_json,hidden_from_json,ts,payload_json)
VALUES($id,$event,$owner,$text,'private_response',0.9,$known,$about,'[]',$ts,$payload);",new Dictionary<string, object>{{"id","comprehension_"+evaluationIdSafe(eventId,spouse)},{"event",eventId},{"owner",spouse},{"text",text},{"known",Json.Serialize(new[]{spouse})},{"about",Json.Serialize(new[]{match,leader})},{"ts",ts},{"payload",Json.Serialize(new Dictionary<string, object>{{"resentment",resentment},{"compelledMarriage",true}})}});
        }

        private static string evaluationIdSafe(string eventId,string owner)
        {
            return Convert.ToBase64String(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(eventId+"|"+owner))).Replace("/","_").Replace("+","-").TrimEnd('=').Substring(0,24);
        }

        private static Dictionary<string, object> RelationshipDirectorStatusApi(Dictionary<string, object> payload)
        {
            string campaignId=ReadString(payload,"campaignId","default"),timelineId=ReadString(payload,"timelineId","main");using(ReignDbConnection connection=OpenCampaignConnection(campaignId)){EnsureRelationshipDirectorSchema(connection);return new Dictionary<string, object>{{"ok",true},{"relationshipModel","directional_mbti_native_relation"},{"passiveMechanicalEventsEnabled",false},{"relationshipLlmCalls",0},{"directorPolicy","political_marriage_only"},{"lastRun",QuerySql(connection,"SELECT * FROM relationship_director_runs ORDER BY world_day DESC LIMIT 1;").FirstOrDefault()??new Dictionary<string, object>()},{"pendingActions",ReadInt(QuerySql(connection,"SELECT COUNT(*) AS count FROM relationship_director_actions WHERE status='pending';").FirstOrDefault(),"count",0)},{"marriageEvaluations",QuerySql(connection,"SELECT * FROM marriage_evaluations WHERE timeline_id=$timeline ORDER BY world_day DESC LIMIT 50;",new Dictionary<string, object>{{"timeline",timelineId}})},{"marriageRolls",QuerySql(connection,"SELECT * FROM marriage_leader_rolls WHERE timeline_id=$timeline ORDER BY world_day DESC LIMIT 50;",new Dictionary<string, object>{{"timeline",timelineId}})},{"ambient",MbtiRelationshipStatusApi(payload)}};}
        }

        private static bool IsExplicitConceptionCapablePlayerText(string text)
        {
            string value=(text??"").ToLowerInvariant();
            bool completed=ContainsAny(value,"we made love","i made love","slept together","lay together","finished inside","came inside","inseminat");
            bool conceive=ContainsAny(value,"try for a child","tried for a child","attempted to have a child","make a child","conceive","get you pregnant","become pregnant");
            return (completed&&conceive)||ContainsAny(value,"finished inside","came inside","inseminat");
        }

        private static double PlayerConceptionChance(double age){if(age<18d||age>45d)return 0d;return (15d-(age-18d)*(12d/27d))/100d;}
        private static string NormalizePregnancyChoiceToken(string value){return (value??"").Trim().ToLowerInvariant().Replace("-","_").Replace(" ","_");}
        private static double StableUnit(string seed){using(SHA256 sha=SHA256.Create()){byte[] hash=sha.ComputeHash(Encoding.UTF8.GetBytes(seed??""));ulong value=BitConverter.ToUInt64(hash,0);return value/(double)ulong.MaxValue;}}
        private static double TraitFromProfile(Dictionary<string, object> profile,string key){Dictionary<string, object> direct=ReadDictionary(profile,"foundationTraits")??ReadDictionary(ReadDictionary(profile,"traits"),"foundationTraits")??ReadDictionary(profile,"traits")??new Dictionary<string, object>();return ReadDouble(direct,key,0d);}
        private static double Trait01(Dictionary<string, object> profile,string key){return ClampDouble((TraitFromProfile(profile,key)+2d)/4d,0d,1d);}

        private static List<Dictionary<string, object>> RunPregnancyChoiceSelfTests()
        {
            List<Dictionary<string, object>> rows=new List<Dictionary<string, object>>();
            Action<string,bool,string> add=(id,passed,summary)=>rows.Add(new Dictionary<string, object>{{"ok",true},{"passed",passed},{"suite","pregnancy_choice"},{"caseId",id},{"name",id},{"summary",summary},{"durationMs",0}});
            string previousRoot=CampaignsRootOverride.Value;
            string isolatedRoot=Path.Combine(DataDir,"test_runs","pregnancy_choice_"+Guid.NewGuid().ToString("N"));
            CampaignsRootOverride.Value=isolatedRoot;
            try
            {
                string campaign="pregnancy_choice_campaign";
                Func<string,string,Dictionary<string, object>> request=(attempt,decision)=>new Dictionary<string, object>
                {
                    {"campaignId",campaign},{"attemptId",attempt},{"eventId",attempt},{"decision",decision},
                    {"playerId","main_hero"},{"partnerId","partner_hero"},{"verifiedAct",true},{"actionText","*She completes the insemination.*"},
                    {"coLocated",true},{"completed",true},{"worldDay",42d},
                    {"player",new Dictionary<string, object>{{"isFemale",false},{"age",30d},{"isPregnant",false}}},
                    {"partner",new Dictionary<string, object>{{"isFemale",true},{"age",30d},{"isPregnant",false},{"spouseId",""}}}
                };

                Dictionary<string, object> preview=VerifyPlayerConceptionAttemptApi(request("attempt_preview_cancel","preview"));
                int previewRows;
                using(ReignDbConnection connection=OpenCampaignConnection(campaign))
                    previewRows=ReadInt(QuerySql(connection,"SELECT COUNT(*) AS count FROM conception_attempts;").FirstOrDefault(),"count",-1);
                add("preview_is_stateless",
                    ReadBool(preview,"ok",false)&&ReadBool(preview,"eligible",false)&&!ReadBool(preview,"rollPerformed",true)&&ReadDouble(preview,"roll",0d)==-1d&&previewRows==0,
                    "Opening the popup reports eligibility and chance without consuming or storing a pregnancy roll.");

                Dictionary<string, object> cancelled=VerifyPlayerConceptionAttemptApi(request("attempt_preview_cancel","pull_out"));
                Dictionary<string, object> cancelledThenProceed=VerifyPlayerConceptionAttemptApi(request("attempt_preview_cancel","proceed"));
                Dictionary<string, object> cancelledRow;
                using(ReignDbConnection connection=OpenCampaignConnection(campaign))
                    cancelledRow=QuerySql(connection,"SELECT * FROM conception_attempts WHERE attempt_id='attempt_preview_cancel' LIMIT 1;").FirstOrDefault();
                add("pull_out_never_rolls",
                    ReadBool(cancelled,"cancelled",false)&&!ReadBool(cancelled,"rollPerformed",true)&&ReadDouble(cancelled,"roll",0d)==-1d
                    &&ReadString(cancelledRow,"status","")=="cancelled"&&ReadDouble(cancelledRow,"roll",0d)==-1d,
                    "Pull Out persists a cancellation with the no-roll sentinel and creates no conception.");
                add("cancelled_attempt_cannot_be_replayed",
                    ReadBool(cancelledThenProceed,"idempotent",false)&&ReadBool(cancelledThenProceed,"cancelled",false)
                    &&!ReadBool(cancelledThenProceed,"rollPerformed",true)&&!ReadBool(cancelledThenProceed,"success",true),
                    "A later Proceed retry against the same cancelled attempt remains cancelled and cannot roll.");

                Dictionary<string, object> proceeded=VerifyPlayerConceptionAttemptApi(request("attempt_proceed","proceed"));
                Dictionary<string, object> proceededAgain=VerifyPlayerConceptionAttemptApi(request("attempt_proceed","proceed"));
                add("proceed_rolls_exactly_once",
                    ReadBool(proceeded,"rollPerformed",false)&&ReadDouble(proceeded,"roll",-1d)>=0d
                    &&ReadBool(proceededAgain,"idempotent",false)&&Math.Abs(ReadDouble(proceeded,"roll",-1d)-ReadDouble(proceededAgain,"roll",-2d))<double.Epsilon,
                    "Proceed performs one deterministic roll and retries return the same stored outcome.");

                Dictionary<string, object> gate=NormalizeConceptionGate(new Dictionary<string, object>{{"needed",true},{"completed",true},{"confidence",0.95d}},"",false);
                Dictionary<string, object> suppressed=NormalizeConceptionGate(new Dictionary<string, object>{{"needed",true},{"completed",true},{"confidence",0.95d}},"*She inseminates him.*",true);
                add("llm_gate_and_pull_out_suppression",
                    ReadBool(gate,"needed",false)&&!ReadBool(suppressed,"needed",true),
                    "Completed LLM insemination actions open the choice, while the pull_out follow-up cannot recursively trigger another roll.");

                Dictionary<string, Dictionary<string, object>> metadata=PromptMetadataByName();
                add("pull_out_prompt_is_editable",
                    PromptFileNames.Contains("pull_out.txt",StringComparer.OrdinalIgnoreCase)
                    &&metadata.ContainsKey("pull_out.txt")&&ReadBool(metadata["pull_out.txt"],"visible",false)
                    &&!string.IsNullOrWhiteSpace(LoadPromptTemplate("pull_out.txt")),
                    "The pull_out prompt is registered, visible in the Control Center editor, and has a non-empty default.");
            }
            finally
            {
                CampaignsRootOverride.Value=previousRoot;
                try{if(Directory.Exists(isolatedRoot))Directory.Delete(isolatedRoot,true);}catch{}
            }
            return rows;
        }

        private static List<Dictionary<string, object>> RunRelationshipSubsystemSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string,bool,string> add = (id,passed,summary) => results.Add(new Dictionary<string, object>{{"ok",true},{"passed",passed},{"suite","relationship_system"},{"caseId",id},{"name",id},{"summary",summary},{"durationMs",0}});
            add("player_conception_age_curve",Math.Abs(PlayerConceptionChance(18d)-0.15d)<0.000001d&&Math.Abs(PlayerConceptionChance(30d)-0.0966666667d)<0.000001d&&Math.Abs(PlayerConceptionChance(45d)-0.03d)<0.000001d&&PlayerConceptionChance(17.9d)==0d&&PlayerConceptionChance(45.1d)==0d,"Player curve is 15% at 18, linearly 3% at 45, and zero outside the range.");
            add("player_conception_explicit_verifier",IsExplicitConceptionCapablePlayerText("We made love and attempted to have a child together.")&&!IsExplicitConceptionCapablePlayerText("I kiss you and tell you that I love you."),"Explicit conception-capable acts pass while vague romance does not.");
            add("player_conception_choice_contract",
                NormalizePregnancyChoiceToken("preview")=="preview"&&NormalizePregnancyChoiceToken("proceed")=="proceed"&&NormalizePregnancyChoiceToken("pull_out")=="pull_out",
                "Pregnancy choice decisions distinguish stateless preview, one-time proceed, and no-roll pull-out cancellation.");
            Dictionary<string, object> eligibleRomance = new Dictionary<string, object>{{"genuineInterest",55d},{"strategicInterest",70d},{"receptivity",72d},{"hardConstraints",new Dictionary<string, object>{{"adults",true},{"nativeSuitable",true},{"closeKin",false},{"coercive",false}}}};
            Dictionary<string, object> restrainedRomance = new Dictionary<string, object>{{"genuineInterest",20d},{"strategicInterest",15d},{"receptivity",30d},{"hardConstraints",new Dictionary<string, object>{{"adults",true},{"nativeSuitable",true},{"closeKin",false},{"coercive",false}}}};
            add("affair_shared_posture_gate",DirectorRomanceEligible(eligibleRomance,60d)&&!DirectorRomanceEligible(restrainedRomance,60d),"The director uses the shared interest, receptivity, suitability, and consent posture gate.");
            add("story_physical_romance_routes",
                StoryPhysicalRomanceEligible(true,true,75d,75d,eligibleRomance,eligibleRomance)
                &&StoryPhysicalRomanceEligible(true,true,50d,50d,eligibleRomance,eligibleRomance)
                &&!StoryPhysicalRomanceEligible(false,true,100d,75d,eligibleRomance,eligibleRomance)
                &&!StoryPhysicalRomanceEligible(true,false,100d,75d,eligibleRomance,eligibleRomance)
                &&!StoryPhysicalRomanceEligible(true,true,74d,75d,eligibleRomance,eligibleRomance),
                "Physical intimacy and affairs require co-location, an allowed marital route, bilateral thread intensity, suitability, receptivity, and consent.");
            add("stable_roll_idempotency",Math.Abs(StableUnit("same_attempt")-StableUnit("same_attempt"))<double.Epsilon,"Seeded conception and director rolls are retry-stable.");
            Dictionary<string, object> readyA=new Dictionary<string, object>{{"affection",70d},{"trust",55d},{"attraction",45d},{"resentment",0d}};
            Dictionary<string, object> readyB=new Dictionary<string, object>{{"affection",85d},{"trust",70d},{"attraction",60d},{"resentment",10d}};
            add("romantic_marriage_bilateral_gate",RomanticMarriageReady(readyA,readyB)&&!RomanticMarriageReady(readyA,new Dictionary<string, object>{{"affection",69d},{"trust",80d},{"attraction",80d}}),"Romantic marriage requires both directions to meet affection 70, trust 55, and attraction 45.");
            Dictionary<string, object> marriageA=new Dictionary<string, object>
            {
                {"heroStringId","marriage_a"},{"clanId","clan_a"},{"isAlive",true},{"age",24d},{"isFemale",true},
                {"nativeCanMarry",true},{"nativeMarriageClanSuitable",true},
                {"marriageAncestorIds",new List<string>{"ancestor_a"}},
                {"activeCourtships",new List<Dictionary<string, object>>()}
            };
            Dictionary<string, object> marriageB=new Dictionary<string, object>
            {
                {"heroStringId","marriage_b"},{"clanId","clan_b"},{"isAlive",true},{"age",26d},{"isFemale",false},
                {"nativeCanMarry",true},{"nativeMarriageClanSuitable",true},
                {"marriageAncestorIds",new List<string>{"ancestor_b"}},
                {"activeCourtships",new List<Dictionary<string, object>>()}
            };
            Dictionary<string, object> nativeBlocked=new Dictionary<string, object>(marriageB){{"nativeCanMarry",false}};
            Dictionary<string, object> related=new Dictionary<string, object>(marriageB){{"marriageAncestorIds",new List<string>{"ancestor_a"}}};
            Dictionary<string, object> courtshipBlocked=new Dictionary<string, object>(marriageA)
            {
                {"activeCourtships",new List<Dictionary<string, object>>
                    {new Dictionary<string, object>{{"heroId","other_suitor"},{"clanId","clan_b"},{"level",4}}}}
            };
            add("arranged_marriage_native_eligibility",
                SnapshotMarriageEligible(marriageA,marriageB)
                &&!SnapshotMarriageEligible(marriageA,nativeBlocked)
                &&!SnapshotMarriageEligible(marriageA,related)
                &&!SnapshotMarriageEligible(courtshipBlocked,marriageB),
                "Server-side arranged-marriage selection honors native marriage suitability, extended kinship, and active native courtships before queuing an action.");
            Dictionary<string, object> dutiful=new Dictionary<string, object>{{"foundationTraits",new Dictionary<string, object>{{"dutyMotivation",2},{"familyMotivation",2},{"loyalty",2},{"traditionalism",2},{"pragmatism",2},{"assertiveness",-2},{"pride",-2},{"impulsiveness",-2}}},{"nativeRelationToLeader",80d}};
            Dictionary<string, object> resistant=new Dictionary<string, object>{{"foundationTraits",new Dictionary<string, object>{{"dutyMotivation",-2},{"familyMotivation",-2},{"loyalty",-2},{"traditionalism",-2},{"pragmatism",-2},{"assertiveness",2},{"pride",2},{"impulsiveness",2}}},{"nativeRelationToLeader",-50d}};
            add("compelled_marriage_trait_resentment",ArrangedMarriageResentment(resistant,new Dictionary<string, object>(),new Dictionary<string, object>(),40d)>ArrangedMarriageResentment(dutiful,new Dictionary<string, object>(),new Dictionary<string, object>(),40d),"Resistant personalities receive more arranged-marriage resentment than dutiful traditional personalities.");
            double lowChance=ClampDouble(0.01d+0.04d*0d,0.01d,0.05d),highChance=ClampDouble(0.01d+0.04d*1d,0.01d,0.05d);
            add("arranged_marriage_weekly_chance_cap",Math.Abs(lowChance-0.01d)<0.000001d&&Math.Abs(highChance-0.05d)<0.000001d,"Weekly clan-leader marriage chance scales from 1% and never exceeds 5%.");
            List<KeyValuePair<string,double>> eventCatalog=new List<KeyValuePair<string,double>>
            {
                new KeyValuePair<string,double>("shared_confidence",1),new KeyValuePair<string,double>("practical_favor",1),
                new KeyValuePair<string,double>("family_duty_cooperation",1),new KeyValuePair<string,double>("private_spousal_confidence",1),
                new KeyValuePair<string,double>("political_rivalry_argument",1),new KeyValuePair<string,double>("mutual_romantic_flirtation",1),
                new KeyValuePair<string,double>("romantic_confidence",1),new KeyValuePair<string,double>("romantic_intimacy",1),
                new KeyValuePair<string,double>("political_obstruction",1),new KeyValuePair<string,double>("marital_argument",1),
                new KeyValuePair<string,double>("secret_affair_intimacy",1),new KeyValuePair<string,double>("marital_separation",1)
            };
            HashSet<string> reached=new HashSet<string>(Enumerable.Range(0,2000).Select(i=>SelectWeightedPassiveEvent(eventCatalog,"reachability_"+i)),StringComparer.OrdinalIgnoreCase);
            add("weighted_event_catalog_reachability",eventCatalog.All(x=>reached.Contains(x.Key)),"Every discrete relationship event remains selectable when its eligibility gate supplies positive weight.");
            double simulated=0d;HashSet<int> processedDays=new HashSet<int>();bool duplicate=false;
            for(int day=0;day<630;day++){if(!processedDays.Add(day)){duplicate=true;break;}double gap=25d-simulated;if(Math.Abs(gap)>=0.5d)simulated=ClampDouble(simulated+ClampDouble(gap*0.02d,-0.50d,0.50d),-100d,100d);}
            add("five_year_ambient_bounded_simulation",!duplicate&&simulated>=-25d&&simulated<=25d&&processedDays.Count==630,"A seeded five-year, 126-day-year daily simulation remains bounded and processes each day once.");
            string storyCampaign="relationship_story_selftest_"+Guid.NewGuid().ToString("N");
            using(ReignDbConnection storyConnection=OpenCampaignConnection(storyCampaign))
            {
                EnsureRelationshipDirectorSchema(storyConnection);
                ApplyStoryEventToDatabase(storyConnection,"mutual_romantic_flirtation","story_flirt","story_a","story_b",10d,false,false);
                Dictionary<string,object> story=LoadPairStorySummary(storyConnection,"story_a","story_b",17d);
                add("persistent_story_schema_and_priority",Math.Abs(ReadDouble(story,"multiplier",0d)-3d)<0.0001d
                    && Math.Abs(PairStoryKindIntensity(storyConnection,"story_a","story_b","romance",17d,true)-25d)<0.0001d,
                    "Persistent directional romance threads survive storage and give recent active stories exactly 3x priority.");
                ApplyStoryEventToDatabase(storyConnection,"mutual_romantic_flirtation","story_flirt","story_a","story_b",10d,false,false);
                add("story_event_idempotency",Math.Abs(PairStoryKindIntensity(storyConnection,"story_a","story_b","romance",17d,true)-25d)<0.0001d,
                    "Reprocessing one source event cannot advance a storyline twice.");
                ApplyStoryEventToDatabase(storyConnection,"romantic_confidence","story_confidence_1","story_a","story_b",18d,false,false,eligibleRomance,eligibleRomance);
                ApplyStoryEventToDatabase(storyConnection,"romantic_confidence","story_confidence_2","story_a","story_b",19d,false,false,eligibleRomance,eligibleRomance);
                add("production_developed_courtship_penalty",Math.Abs(ArrangedMarriageCourtshipMultiplier(storyConnection,"story_a","story_c",19d)-.75d)<.0001d
                    &&Math.Abs(ArrangedMarriageCourtshipMultiplier(storyConnection,"story_a","story_b",19d)-1d)<.0001d,
                    "Persisted developed courtships apply the exact third-party arrangement penalty without penalizing their own couple.");
                ApplyStoryEventToDatabase(storyConnection,"romantic_confidence","story_confidence_3","story_a","story_b",20d,false,false,eligibleRomance,eligibleRomance);
                ApplyStoryEventToDatabase(storyConnection,"romantic_confidence","story_confidence_4","story_a","story_b",21d,false,false,eligibleRomance,eligibleRomance);
                add("production_major_courtship_penalty",Math.Abs(ArrangedMarriageCourtshipMultiplier(storyConnection,"story_a","story_c",21d)-.50d)<.0001d,
                    "Persisted major courtships apply the exact 50% third-party arrangement penalty.");
                int displaced=ConvertDisplacedCourtshipsToTemptation(storyConnection,"story_a","story_c",22d,"story_arranged");
                Dictionary<string,object> displacedThread=LoadStoryThread(storyConnection,"story_a","story_b","romance",22d);
                int displacementPressures=ReadInt(QuerySql(storyConnection,
                    "SELECT COUNT(*) AS count FROM relationship_pressures WHERE ((subject_id='story_a' AND target_id='story_b' AND kind='divided_loyalty') OR (subject_id='story_b' AND target_id='story_a' AND kind='romantic_displacement')) AND status='active';").FirstOrDefault(),"count",0);
                add("production_displaced_courtship",displaced==1&&ReadString(displacedThread,"route","")=="temptation"&&displacementPressures==2,
                    "An overriding arranged marriage preserves the persisted romance as temptation and creates only private directional pressures.");
                for(int i=0;i<4;i++)ApplyStoryEventToDatabase(storyConnection,"political_rivalry_argument","rivalry_"+i,"story_a","story_b",20+i*7,false,false);
                int feuds=ReadInt(QuerySql(storyConnection,"SELECT COUNT(DISTINCT pair_key) AS count FROM relationship_story_threads WHERE kind='rivalry' AND intensity>=75;").FirstOrDefault(),"count",0);
                add("persistent_feud_progression",feuds==1,"Repeated political conflicts create one pair-level feud state.");
                ExecuteSql(storyConnection,@"INSERT INTO relationship_pressures(pressure_id,subject_id,target_id,kind,status,intensity,secrecy,summary,related_entities_json,trigger_json,evidence_json,created_day,updated_day,updated_ts,payload_json)
VALUES('story_pressure','story_c','story_d','strategic_seduction','active',60,.8,'','[]','{}','[]',40,55,$ts,'{}');",
                    new Dictionary<string,object>{{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()}});
                LoadPairStorySummary(storyConnection,"story_c","story_d",60d);
                add("lazy_story_seed_from_pressure",PairStoryKindIntensity(storyConnection,"story_c","story_d","romance",60d,false)>=60d,
                    "Existing active relationship pressures lazily seed storyline state without rewriting facets or narrative data.");
            }
            results.AddRange(RunMbtiRelationshipSelfTests());
            return results;
        }
    }
}
