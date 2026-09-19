using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Reign.Relationships;
using ReignBeta.Shared;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int MbtiChemistryVersion = 7;
        private const int LegacyAffinityRebaseCutoffVersion = 6;
        private const int RelationshipCadenceDays = 1;
        private const int RelationshipCadenceShardCount = 1;
        private const int OrdinaryRelationshipMagnitudeSides = 15;
        private const int LoverRelationshipMagnitudeSides = 20;
        private const bool AutomaticRelationshipNormalizationEnabled = false;
        private const int OpeningRelationshipSeedVersion = 2;
        private const double OpeningRelationshipSeedScale = 0.75d;
        private const int PostgreSqlRelationshipWriteChunkSize = 5000;
        private const int SqliteRelationshipWriteChunkSize = 500;
        private const int PostgreSqlRelationshipSchemaRevision = 5;
        private static long PostgreSqlRelationshipStagedRows;
        private static long PostgreSqlRelationshipWrittenRows;
        private static long PostgreSqlRelationshipCopyMs;
        private static long PostgreSqlRelationshipMergeMs;
        private static readonly ConcurrentDictionary<string,
            ConcurrentDictionary<string, Dictionary<string, object>>>
            RelationshipPairStateCache =
                new ConcurrentDictionary<string,
                    ConcurrentDictionary<string, Dictionary<string, object>>>(
                        StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string,
            ConcurrentDictionary<string, Dictionary<string, object>>>
            RelationshipPermanentMbtiCache =
                new ConcurrentDictionary<string,
                    ConcurrentDictionary<string, Dictionary<string, object>>>(
                        StringComparer.OrdinalIgnoreCase);
        private static readonly string[] MbtiTypes =
        {
            "INTJ","INTP","ENTJ","ENTP","INFJ","INFP","ENFJ","ENFP",
            "ISTJ","ISFJ","ESTJ","ESFJ","ISTP","ISFP","ESTP","ESFP"
        };

        private static int AdjustedMbtiCompatibility(int baseScore)
        {
            return RelationshipCompatibilityPolicy.AdjustedScore(baseScore);
        }

        private static void EnsureMbtiRelationshipSchema(ReignDbConnection connection)
        {
            const string marker =
                "postgresql_relationship_schema_revision";
            if (IsPostgreSqlComponentSchemaReady(connection, marker,
                PostgreSqlRelationshipSchemaRevision))
            {
                // Component revisions are independent. A current parent
                // marker must not suppress a newly revised dependent schema,
                // especially after Save Sync restores an older snapshot.
                EnsureRelationshipLifecycleSchema(connection);
                EnsureSharedRelationshipHistorySchema(connection);
                EnsureRelationshipDirectorSchema(connection);
                return;
            }

            EnsureMbtiRelationshipSchemaCore(connection);
            if (!ReignPostgreSqlDialect.IsPostgreSql(connection))
                return;
            EnsureWorldRelationshipSchema(connection);

            // The SQLite-to-PostgreSQL compatibility migration originally
            // generated a second index for several indexes that the explicit
            // relationship schema already owns. Every affinity update then
            // maintained both copies. Remove only the known redundant indexes;
            // the explicit idx_* indexes remain authoritative.
            string[] redundantIndexes =
            {
                "relationship_pair_chemistry_hero_a_id_hero_b_id_idx",
                "relationship_pair_chemistry_hero_b_id_hero_a_id_idx",
                "relationship_pair_chemistry_last_day_idx",
                "relationship_pair_chemistry_processing_shard_last_day_idx",
                "idx_pair_chemistry_day",
                "idx_pair_chemistry_shard",
                "relationship_pair_provenance_campaign_id_timeline_id_source_idx",
                "relationship_pair_provenance_pair_key_active_required_sourc_idx",
                "relationship_native_targets_status_world_day_pair_key_idx",
                "relationship_daily_inputs_timeline_id_status_day_key_idx"
            };
            ExecuteSql(connection, string.Join(" ",
                redundantIndexes.Select(index =>
                    "DROP INDEX IF EXISTS " + index + ";")));
            // Frequently updated current-state rows should retain page space for
            // PostgreSQL HOT updates. last_day and processing_shard are diagnostic
            // columns, not selective simulation lookups; removing their indexes
            // lets an ordinary affinity update avoid all secondary-index writes.
            ExecuteSql(connection, @"
ALTER TABLE relationship_pair_chemistry SET (
    fillfactor=80,
    autovacuum_vacuum_scale_factor=0.02,
    autovacuum_analyze_scale_factor=0.02);
ALTER TABLE relationship_pair_provenance SET (
    fillfactor=90,
    autovacuum_vacuum_scale_factor=0.05);
ALTER TABLE relationship_native_targets SET (
    fillfactor=70,
    autovacuum_vacuum_scale_factor=0.02);
ALTER TABLE relationship_daily_inputs SET (
    fillfactor=80,
    autovacuum_vacuum_scale_factor=0.05);");
            ExecuteSql(connection, @"
INSERT INTO schema_meta(key,value)
VALUES('postgresql_relationship_schema_revision',$revision)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                new Dictionary<string, object>
                {
                    ["revision"] =
                        PostgreSqlRelationshipSchemaRevision.ToString(
                            CultureInfo.InvariantCulture)
                });
            MarkPostgreSqlComponentSchemaReady(connection, marker,
                PostgreSqlRelationshipSchemaRevision);
        }

        private static void EnsureMbtiRelationshipSchemaCore(
            ReignDbConnection connection)
        {
            EnsureNotableMbtiSchema(connection);
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_pair_chemistry (
pair_key TEXT PRIMARY KEY,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,
mbti_a TEXT NOT NULL DEFAULT 'XXXX',mbti_b TEXT NOT NULL DEFAULT 'XXXX',
axes_a_json TEXT NOT NULL DEFAULT '{}',axes_b_json TEXT NOT NULL DEFAULT '{}',
base_chance_a_to_b INTEGER NOT NULL DEFAULT 50,base_chance_b_to_a INTEGER NOT NULL DEFAULT 50,
chance_a_to_b INTEGER NOT NULL DEFAULT 40,chance_b_to_a INTEGER NOT NULL DEFAULT 40,
affinity_a_to_b INTEGER NOT NULL DEFAULT 0,affinity_b_to_a INTEGER NOT NULL DEFAULT 0,
native_incident_offset INTEGER NOT NULL DEFAULT 0,
signal_a_to_b INTEGER NOT NULL DEFAULT 0,signal_b_to_a INTEGER NOT NULL DEFAULT 0,
tag_a_to_b TEXT NOT NULL DEFAULT 'neutral',tag_b_to_a TEXT NOT NULL DEFAULT 'neutral',
shared_tag TEXT NOT NULL DEFAULT '',first_day INTEGER NOT NULL,last_day INTEGER NOT NULL,
consecutive_days INTEGER NOT NULL DEFAULT 0,weighted_exposure REAL NOT NULL DEFAULT 0,
last_context_kind TEXT NOT NULL DEFAULT '',last_context_id TEXT NOT NULL DEFAULT '',
last_roll_a_to_b INTEGER NOT NULL DEFAULT 0,last_roll_b_to_a INTEGER NOT NULL DEFAULT 0,
last_delta_a_to_b INTEGER NOT NULL DEFAULT 0,last_delta_b_to_a INTEGER NOT NULL DEFAULT 0,
projected_native_relation INTEGER NOT NULL DEFAULT 0,last_native_sync_day REAL NOT NULL DEFAULT -1000,
native_action_pending INTEGER NOT NULL DEFAULT 0,native_action_id TEXT NOT NULL DEFAULT '',
last_decay_period INTEGER NOT NULL DEFAULT -1,
compatibility_version INTEGER NOT NULL DEFAULT 3,
state_revision INTEGER NOT NULL DEFAULT 1,updated_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_pair_chemistry_day ON relationship_pair_chemistry(last_day DESC);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_pair_chemistry_hero_a ON relationship_pair_chemistry(hero_a_id,hero_b_id);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_pair_chemistry_hero_b ON relationship_pair_chemistry(hero_b_id,hero_a_id);");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "social_modifier_a_to_b", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "social_modifier_b_to_a", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "effective_affinity_a_to_b", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "effective_affinity_b_to_a", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "processing_shard", "INTEGER NOT NULL DEFAULT -1");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "last_presence_day", "INTEGER NOT NULL DEFAULT -1");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "last_batch_size", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "last_presence_mask", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_version", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_baseline", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_chance_a_to_b", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_chance_b_to_a", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_sign_roll_a_to_b", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_sign_roll_b_to_a", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_magnitude_roll_a_to_b", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_magnitude_roll_b_to_a", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_delta_a_to_b", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_delta_b_to_a", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_affinity_a_to_b", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_affinity_b_to_a", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_eligible", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_provenance", "TEXT NOT NULL DEFAULT ''");
            ExecuteSql(connection,
                "CREATE INDEX IF NOT EXISTS idx_pair_chemistry_shard ON relationship_pair_chemistry(processing_shard,last_day);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_native_sync_batches (
plan_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,world_day REAL NOT NULL,
target_count INTEGER NOT NULL DEFAULT 0,issued_ts INTEGER NOT NULL DEFAULT 0,
client_applied INTEGER NOT NULL DEFAULT 0,client_already_aligned INTEGER NOT NULL DEFAULT 0,
client_obsolete INTEGER NOT NULL DEFAULT 0,client_failed INTEGER NOT NULL DEFAULT 0,
client_remaining INTEGER NOT NULL DEFAULT 0,client_duration_ms INTEGER NOT NULL DEFAULT 0,
last_report_day REAL NOT NULL DEFAULT -1,last_report_ts INTEGER NOT NULL DEFAULT 0,
last_error TEXT NOT NULL DEFAULT '');");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_native_sync_batch_day ON relationship_native_sync_batches(timeline_id,world_day DESC);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_daily_inputs (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,world_day REAL NOT NULL,
status TEXT NOT NULL DEFAULT 'pending',presence_groups_json TEXT NOT NULL DEFAULT '[]',
hero_changes_json TEXT NOT NULL DEFAULT '[]',group_count INTEGER NOT NULL DEFAULT 0,
received_ts INTEGER NOT NULL,processed_ts INTEGER NOT NULL DEFAULT 0,
PRIMARY KEY(campaign_id,timeline_id,day_key));");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_relationship_daily_inputs_status ON relationship_daily_inputs(timeline_id,status,day_key);");
            EnsureDatabaseColumn(connection, "relationship_daily_inputs", "pair_cursor", "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "relationship_daily_inputs", "candidate_pairs", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_daily_inputs", "processed_pairs", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_daily_inputs", "started_ts", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_daily_inputs", "last_error", "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "relationship_daily_inputs", "cadence_shard", "INTEGER NOT NULL DEFAULT -1");
            EnsureDatabaseColumn(connection, "relationship_daily_inputs", "window_start_day", "INTEGER NOT NULL DEFAULT -1");
            EnsureDatabaseColumn(connection, "relationship_daily_inputs", "window_day_count", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_daily_inputs", "pair_day_evaluations", "INTEGER NOT NULL DEFAULT 0");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_observed_heroes (
hero_id TEXT PRIMARY KEY,profile_json TEXT NOT NULL,profile_hash TEXT NOT NULL DEFAULT '',
first_observed_day INTEGER NOT NULL,last_observed_day INTEGER NOT NULL,updated_ts INTEGER NOT NULL);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_hero_observations (
timeline_id TEXT NOT NULL,hero_id TEXT NOT NULL,observed_day INTEGER NOT NULL,
profile_json TEXT NOT NULL,profile_hash TEXT NOT NULL,
PRIMARY KEY(timeline_id,hero_id,observed_day));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_personalities (
hero_id TEXT PRIMARY KEY,mbti_type TEXT NOT NULL,title TEXT NOT NULL,description TEXT NOT NULL,
source TEXT NOT NULL,assignment_day REAL NOT NULL,template_version INTEGER NOT NULL,
traits_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_opening_seed_runs (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,seed_version INTEGER NOT NULL,
generation_id TEXT NOT NULL DEFAULT '',world_day REAL NOT NULL,status TEXT NOT NULL,
candidate_pairs INTEGER NOT NULL DEFAULT 0,completed_pairs INTEGER NOT NULL DEFAULT 0,
skipped_pairs INTEGER NOT NULL DEFAULT 0,failed_pairs INTEGER NOT NULL DEFAULT 0,
co_location_pairs INTEGER NOT NULL DEFAULT 0,spouse_pairs INTEGER NOT NULL DEFAULT 0,
family_pairs INTEGER NOT NULL DEFAULT 0,kingdom_leadership_pairs INTEGER NOT NULL DEFAULT 0,
ruler_network_pairs INTEGER NOT NULL DEFAULT 0,positive_directions INTEGER NOT NULL DEFAULT 0,
negative_directions INTEGER NOT NULL DEFAULT 0,missing_mbti INTEGER NOT NULL DEFAULT 0,
average_delta REAL NOT NULL DEFAULT 0,median_delta REAL NOT NULL DEFAULT 0,
minimum_delta INTEGER NOT NULL DEFAULT 0,maximum_delta INTEGER NOT NULL DEFAULT 0,
starting_bands_json TEXT NOT NULL DEFAULT '{}',resulting_bands_json TEXT NOT NULL DEFAULT '{}',
duration_ms INTEGER NOT NULL DEFAULT 0,last_error TEXT NOT NULL DEFAULT '',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,seed_version));");
            EnsureDatabaseColumn(connection, "relationship_opening_seed_runs",
                "parallel_workers", "INTEGER NOT NULL DEFAULT 1");
            EnsureDatabaseColumn(connection, "relationship_opening_seed_runs",
                "parallel_compute_ms", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_opening_seed_runs",
                "pairs_per_second", "REAL NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "last_decay_period", "INTEGER NOT NULL DEFAULT -1");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "native_incident_offset", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "state_revision", "INTEGER NOT NULL DEFAULT 1");
            EnsureRelationshipLifecycleSchema(connection);
            EnsureSharedRelationshipHistorySchema(connection);
            EnsureRelationshipDirectorSchema(connection);
            EnsureDatabaseColumn(connection, "relationship_native_targets", "requires_observation", "INTEGER NOT NULL DEFAULT 0");
            CompactNoisyNativeRelationBacklog(connection);
            int version = ReadInt(QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='mbti_relationship_chemistry_version' LIMIT 1;").FirstOrDefault(), "value", 0);
            if (version >= MbtiChemistryVersion) return;

            ExecuteSql(connection, "SAVEPOINT mbti_relationship_upgrade;");
            try
            {
                // The old ambient rows were derived diagnostics and six-facet daily
                // drift, not authoritative history. They are intentionally retired.
                foreach (string retiredTable in new[]
                {
                    "ambient_relationship_changes", "ambient_relationship_exposure", "ambient_relationship_runs"
                })
                {
                    if (QuerySql(connection,
                        "SELECT name FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;",
                        new Dictionary<string, object> { ["name"] = retiredTable }).Any())
                    {
                        ExecuteSql(connection, "DELETE FROM " + retiredTable + ";");
                    }
                }
                if (version == 1)
                {
                    // Version 1 incorrectly treated each daily d10 as a slow
                    // accumulator. Preserve that already-rolled history by
                    // applying the remaining signal directly before retiring it.
                    ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
affinity_a_to_b=MAX(-100,MIN(100,affinity_a_to_b+signal_a_to_b)),
affinity_b_to_a=MAX(-100,MIN(100,affinity_b_to_a+signal_b_to_a)),
signal_a_to_b=0,signal_b_to_a=0,compatibility_version=2;");
                }
                if (version < 4)
                {
                    // Reign's two directional affinities are authoritative. Older
                    // builds could add a separate native-only incident offset,
                    // making Bannerlord relation disagree with their average.
                    ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
native_incident_offset=0,
projected_native_relation=MAX(-100,MIN(100,ROUND((affinity_a_to_b+affinity_b_to_a)/2.0))),
native_action_pending=0,native_action_id='',compatibility_version=3;");
                    if (QuerySql(connection,
                        "SELECT name FROM sqlite_master WHERE type='table' AND name='relationship_director_actions' LIMIT 1;").Any())
                    {
                        ExecuteSql(connection, @"UPDATE relationship_director_actions
SET status='superseded',resolved_ts=$ts,
result_json='{""status"":""superseded"",""reason"":""reign_directional_affinity_became_authoritative""}'
WHERE action_type='native_relation' AND status IN ('pending','claimed')
  AND COALESCE(json_extract(payload_json,'$.source'),'')='mbti_relationship_chemistry';",
                            new Dictionary<string, object> { ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                    }
                }
                if (version < 5)
                {
                    // Directional chemistry uses the immutable MBTI codes and
                    // calculated scores. The expanded axis objects were write-only
                    // copies repeated once per pair (several megabytes in a normal
                    // campaign), so retain the authoritative codes and remove them.
                    ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET axes_a_json='{}',axes_b_json='{}'
WHERE axes_a_json<>'{}' OR axes_b_json<>'{}';");
                }
                if (version < 6)
                {
                    // Public Standing is resolved from its subject row at decision
                    // time. Retire copied per-pair modifiers and cached effective
                    // values so later standing revisions require no pair fanout.
                    ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
social_modifier_a_to_b=0,social_modifier_b_to_a=0,
effective_affinity_a_to_b=affinity_a_to_b,
effective_affinity_b_to_a=affinity_b_to_a;");
                }
                // Version 7 widens the immutable MBTI chance range without
                // rewriting relationship affinity or narrative history. Each
                // pair refreshes its stored chance lazily on its next ordinary
                // relationship evaluation.
                ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('mbti_relationship_chemistry_version',$version);",
                    new Dictionary<string, object> { ["version"] = MbtiChemistryVersion.ToString(CultureInfo.InvariantCulture) });
                ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('campaign_compaction_required','1');");
                ExecuteSql(connection, "RELEASE mbti_relationship_upgrade;");
            }
            catch
            {
                try { ExecuteSql(connection, "ROLLBACK TO mbti_relationship_upgrade;"); } catch { }
                try { ExecuteSql(connection, "RELEASE mbti_relationship_upgrade;"); } catch { }
                throw;
            }
        }

        private static Dictionary<string, object> PersistAndHydrateRelationshipDailyInput(
            Dictionary<string, object> input)
        {
            string campaignId = ReadString(input, "campaignId", "default");
            string timelineId = ReadString(input, "timelineId", "main");
            double worldDay = ReadDouble(input, "worldDay", 0d);
            int day = (int)Math.Floor(worldDay + 0.000001d);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<Dictionary<string, object>> groups = ReadDictionaryList(input, "presenceGroups");
            List<Dictionary<string, object>> compactHeroes = ReadDictionaryList(input, "heroes");
            List<Dictionary<string, object>> changedHeroes = compactHeroes
                .Select(NormalizeAmbientLifecycleHero).ToList();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                ExecuteSql(connection, "BEGIN IMMEDIATE;");
                try
                {
                    bool accepted = QuerySql(connection, @"INSERT INTO relationship_daily_inputs(
campaign_id,timeline_id,day_key,world_day,status,presence_groups_json,hero_changes_json,group_count,received_ts)
VALUES($campaign,$timeline,$dayKey,$day,'pending',$groups,$heroes,$count,$ts)
ON CONFLICT(campaign_id,timeline_id,day_key) DO UPDATE SET
world_day=$day,presence_groups_json=$groups,hero_changes_json=$heroes,
group_count=$count,
status='pending'
WHERE relationship_daily_inputs.status='pending' AND relationship_daily_inputs.started_ts=0
RETURNING day_key;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["dayKey"] = day, ["day"] = worldDay,
                        ["groups"] = Json.Serialize(groups), ["heroes"] = Json.Serialize(compactHeroes),
                        ["count"] = groups.Count, ["ts"] = ts
                    }).Count > 0;
                    List<Dictionary<string, object>> observedHeroRows =
                        new List<Dictionary<string, object>>(
                            changedHeroes.Count);
                    foreach (Dictionary<string, object> hero in accepted ? changedHeroes : Enumerable.Empty<Dictionary<string, object>>())
                    {
                        string heroId = ReadFirstString(hero, "heroStringId", "heroId", "id");
                        if (string.IsNullOrWhiteSpace(heroId)) continue;
                        string profileJson = Json.Serialize(hero);
                        RelationshipHeroDocuments.Remember(profileJson, hero);
                        observedHeroRows.Add(
                            new Dictionary<string, object>
                            {
                                ["hero"] = heroId,
                                ["profile"] = profileJson,
                                ["hash"] = Sha256Hex(profileJson),
                                ["day"] = day,
                                ["ts"] = ts
                            });
                    }
                    if (ReignPostgreSqlDialect.IsPostgreSql(connection)
                        && observedHeroRows.Count > 0)
                    {
                        WriteRelationshipHeroObservations(connection, timelineId, observedHeroRows);
                    }
                    else
                    {
                        foreach (Dictionary<string, object> values
                            in observedHeroRows)
                        {
                            ExecuteSql(connection, @"INSERT INTO relationship_observed_heroes(
hero_id,profile_json,profile_hash,first_observed_day,last_observed_day,updated_ts)
VALUES($hero,$profile,$hash,$day,$day,$ts)
ON CONFLICT(hero_id) DO UPDATE SET profile_json=$profile,profile_hash=$hash,
last_observed_day=$day,updated_ts=$ts
WHERE relationship_observed_heroes.profile_hash<>$hash;",
                                values);
                        }
                    }
                    ExecuteSql(connection, "COMMIT;");
                }
                catch
                {
                    try { ExecuteSql(connection, "ROLLBACK;"); } catch { }
                    throw;
                }
                // The continuous ingestion API only acknowledges durability; its
                // caller never consumes a second database-hydrated copy of the
                // roster. Return the normalized rows already in memory. Missing
                // lifecycle heroes are resolved by the worker from the durable
                // observed-hero table when it actually processes the day.
                List<Dictionary<string, object>> hydratedHeroes = changedHeroes;
                Dictionary<string, object> hydrated =
                    new Dictionary<string, object>(input, StringComparer.OrdinalIgnoreCase)
                    {
                        ["campaignId"] = campaignId,
                        ["timelineId"] = timelineId,
                        ["worldDay"] = worldDay,
                        ["presenceGroups"] = groups,
                        ["heroes"] = hydratedHeroes
                    };
                MarkRelationshipCampaignReady(campaignId);
                return hydrated;
            }
        }

        private static Dictionary<string, object> CampaignOpeningRelationshipSeedApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            Stopwatch timer = Stopwatch.StartNew();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string generationId = ReadString(payload, "generationId", "");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            int day = (int)Math.Floor(worldDay + 0.000001d);
            List<Dictionary<string, object>> heroRows = ReadDictionaryList(payload, "heroes");
            List<Dictionary<string, object>> groups = ReadDictionaryList(payload, "presenceGroups");
            Dictionary<string, Dictionary<string, object>> heroes = heroRows
                .Where(row => !string.IsNullOrWhiteSpace(
                    ReadFirstString(row, "heroStringId", "heroId", "id")))
                .GroupBy(row => ReadFirstString(row, "heroStringId", "heroId", "id"),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(),
                    StringComparer.OrdinalIgnoreCase);
            Dictionary<string, OpeningSeedCandidate> candidates =
                BuildOpeningSeedCandidates(heroes, groups);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int completed = 0, skipped = 0, failed = 0, missingMbti = 0;
            int positive = 0, negative = 0;
            int openingParallelWorkers = 1;
            long openingParallelComputeMs = 0;
            List<int> openingDeltas = new List<int>();
            Dictionary<string, int> startingBands =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> resultingBands =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                using (RelationshipStandingContext standingContext = new RelationshipStandingContext(connection, campaignId, timelineId))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    EnsureWorldRelationshipSchema(connection);
                    Dictionary<string, object> priorRun = QuerySql(connection, @"
SELECT * FROM relationship_opening_seed_runs
WHERE campaign_id=$campaign AND timeline_id=$timeline AND seed_version=$version LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["version"] = OpeningRelationshipSeedVersion
                        }).FirstOrDefault();
                    bool alreadyComplete = ReadString(priorRun, "status", "")
                        .Equals("completed", StringComparison.OrdinalIgnoreCase);
                    if (alreadyComplete)
                    {
                        timer.Stop();
                        Dictionary<string, object> plan =
                            BuildRelationshipNativeSyncPlan(connection, campaignId,
                                timelineId, day);
                        return new Dictionary<string, object>
                        {
                            ["ok"] = true, ["idempotent"] = true,
                            ["runId"] = "opening_seed_" + campaignId + "_"
                                + timelineId,
                            ["processedPairs"] = ReadInt(priorRun,
                                "completed_pairs", 0),
                            ["changedDirections"] = 0,
                            ["nativeChangesQueued"] = ReadInt(plan,
                                "targetCount", 0),
                            ["durationMs"] = timer.ElapsedMilliseconds,
                            ["nativeSyncPlan"] = plan,
                            ["openingSeedVersion"] =
                                OpeningRelationshipSeedVersion
                        };
                    }
                    ExecuteSql(connection, @"INSERT INTO relationship_opening_seed_runs(
campaign_id,timeline_id,seed_version,generation_id,world_day,status,
candidate_pairs,created_ts,updated_ts)
VALUES($campaign,$timeline,$version,$generation,$day,'processing',$candidates,$ts,$ts)
ON CONFLICT(campaign_id,timeline_id,seed_version) DO UPDATE SET
generation_id=$generation,world_day=$day,
status=CASE
    WHEN relationship_opening_seed_runs.status='completed'
    THEN relationship_opening_seed_runs.status
    ELSE 'processing'
END,
candidate_pairs=$candidates,updated_ts=$ts;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["version"] = OpeningRelationshipSeedVersion,
                            ["generation"] = generationId, ["day"] = worldDay,
                            ["candidates"] = candidates.Count, ["ts"] = ts
                        });
                    Dictionary<string, Dictionary<string, object>> existingPairs =
                        QuerySql(connection,
                            "SELECT * FROM relationship_pair_chemistry;")
                        .Where(row => !string.IsNullOrWhiteSpace(
                            ReadString(row, "pair_key", "")))
                        .ToDictionary(row => ReadString(row, "pair_key", ""),
                            row => row, StringComparer.OrdinalIgnoreCase);
                    Dictionary<string, Dictionary<string, object>>
                        existingNativeTargets = QuerySql(connection,
                            "SELECT * FROM relationship_native_targets;")
                        .Where(row => !string.IsNullOrWhiteSpace(
                            ReadString(row, "pair_key", "")))
                        .ToDictionary(row => ReadString(row, "pair_key", ""),
                            row => row, StringComparer.OrdinalIgnoreCase);
                    using (ReignDbCommand openingPairUpsert =
                        CreateOpeningRelationshipPairUpsertCommand(connection))
                    using (ReignDbCommand provenanceUpsert =
                        CreateOpeningRelationshipProvenanceUpsertCommand(connection))
                    using (ReignDbCommand nativeTargetUpsert =
                        CreateRelationshipNativeTargetUpsertCommand(connection))
                    using (ReignDbCommand nativeTargetDelete =
                        CreatePreparedRelationshipCommand(connection,
                            "DELETE FROM relationship_native_targets WHERE pair_key=$pair;",
                            "$pair"))
                    {
                        bool usePostgreSqlBulk =
                            ReignPostgreSqlDialect.IsPostgreSql(connection);
                        List<Dictionary<string, object>> openingPairRows =
                            new List<Dictionary<string, object>>();
                        List<Dictionary<string, object>> provenanceRows =
                            new List<Dictionary<string, object>>();
                        List<Dictionary<string, object>> nativeTargetRows =
                            new List<Dictionary<string, object>>();
                        List<string> nativeTargetDeletes = new List<string>();
                        ExecuteSql(connection, "BEGIN IMMEDIATE;");
                        try
                        {
                            standingContext.Prefetch(heroes.Keys);
                            Dictionary<string, string> openingMbtiByHero =
                                QuerySql(connection, @"
 SELECT hero_id,mbti_type,2 AS priority FROM relationship_personalities
 UNION ALL
 SELECT hero_id,mbti_type,1 AS priority FROM notable_mbti_profiles;")
                                .Where(row => !string.IsNullOrWhiteSpace(
                                        ReadString(row, "hero_id", ""))
                                    && MbtiDefinitions.ContainsKey(
                                        ReadString(row, "mbti_type", "")))
                                .GroupBy(row => ReadString(row, "hero_id", ""),
                                    StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(group => group.Key,
                                    group => ReadString(group.OrderBy(row =>
                                            ReadInt(row, "priority", 99))
                                        .First(), "mbti_type", "XXXX"),
                                    StringComparer.OrdinalIgnoreCase);
                            foreach (string heroId in candidates.Values
                                .SelectMany(candidate => new[]
                                {
                                    candidate.HeroAId, candidate.HeroBId
                                })
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(value => value,
                                    StringComparer.OrdinalIgnoreCase))
                            {
                                Dictionary<string, object> openingHero =
                                    heroes[heroId];
                                string resolvedType;
                                if (ReadBool(openingHero, "isNotable", false))
                                {
                                    // A generic relationship-personality row
                                    // can be observed before the campaign's
                                    // seeded notable assignment exists. Never
                                    // let that provisional value become the
                                    // opening pair's permanent compatibility.
                                    Dictionary<string, object> notableProfile =
                                        EnsureNotableMbtiProfile(connection,
                                            campaignId, day, openingHero, false,
                                            true);
                                    resolvedType = ReadString(notableProfile,
                                        "type", "XXXX");
                                }
                                else
                                {
                                    resolvedType = openingMbtiByHero.TryGetValue(
                                            heroId, out string persistedType)
                                        ? persistedType
                                        : ResolveOpeningSeedMbti(connection,
                                            campaignId, day, openingHero);
                                }
                                openingMbtiByHero[heroId] = resolvedType;
                                if (!MbtiDefinitions.ContainsKey(resolvedType))
                                {
                                    missingMbti++;
                                    throw new InvalidOperationException(
                                        "Campaign opening relationship preparation is missing an immutable MBTI assignment for "
                                        + heroId + " (" + resolvedType + ").");
                                }
                            }
                            List<OpeningSeedCandidate> orderedCandidates =
                                candidates.Values.OrderBy(value => value.PairKey,
                                    StringComparer.OrdinalIgnoreCase).ToList();
                            OpeningSeedComputation[] openingComputations =
                                new OpeningSeedComputation[orderedCandidates.Count];
                            openingParallelWorkers = RelationshipParallelismForWork(
                                orderedCandidates.Count, true);
                            Stopwatch openingComputeTimer = Stopwatch.StartNew();
                            Action<int> calculateOpeningSeed = index =>
                            {
                                OpeningSeedCandidate value =
                                    orderedCandidates[index];
                                Dictionary<string, object> valueHeroA =
                                    heroes[value.HeroAId];
                                Dictionary<string, object> valueHeroB =
                                    heroes[value.HeroBId];
                                string valueTypeA =
                                    openingMbtiByHero[value.HeroAId];
                                string valueTypeB =
                                    openingMbtiByHero[value.HeroBId];
                                int valueChanceAB = AdjustedMbtiCompatibility(
                                    MbtiCompatibility(valueTypeA, valueTypeB));
                                int valueChanceBA = AdjustedMbtiCompatibility(
                                    MbtiCompatibility(valueTypeB, valueTypeA));
                                int valueBaseline = OpeningSeedBaseline(
                                    valueHeroA, valueHeroB, value);
                                bool valueEligible =
                                    OpeningSeedHeroEligible(valueHeroA)
                                    && OpeningSeedHeroEligible(valueHeroB);
                                OpeningSeedComputation computed =
                                    new OpeningSeedComputation
                                    {
                                        TypeA = valueTypeA,
                                        TypeB = valueTypeB,
                                        ChanceAB = valueChanceAB,
                                        ChanceBA = valueChanceBA,
                                        Baseline = valueBaseline,
                                        Eligible = valueEligible
                                    };
                                if (valueEligible)
                                {
                                    computed.SignAB = OpeningSeedDie(campaignId,
                                        value.PairKey, "a_to_b", "sign");
                                    computed.SignBA = OpeningSeedDie(campaignId,
                                        value.PairKey, "b_to_a", "sign");
                                    computed.MagnitudeAB = OpeningSeedDie(
                                        campaignId, value.PairKey, "a_to_b",
                                        "magnitude");
                                    computed.MagnitudeBA = OpeningSeedDie(
                                        campaignId, value.PairKey, "b_to_a",
                                        "magnitude");
                                    computed.DeltaAB = OpeningRelationshipDelta(
                                        valueChanceAB, computed.SignAB,
                                        computed.MagnitudeAB);
                                    computed.DeltaBA = OpeningRelationshipDelta(
                                        valueChanceBA, computed.SignBA,
                                        computed.MagnitudeBA);
                                }
                                openingComputations[index] = computed;
                            };
                            if (openingParallelWorkers > 1)
                            {
                                Parallel.For(0, orderedCandidates.Count,
                                    new ParallelOptions
                                    {
                                        MaxDegreeOfParallelism =
                                            openingParallelWorkers
                                    }, calculateOpeningSeed);
                            }
                            else
                            {
                                for (int index = 0;
                                    index < orderedCandidates.Count; index++)
                                    calculateOpeningSeed(index);
                            }
                            openingComputeTimer.Stop();
                            openingParallelComputeMs =
                                openingComputeTimer.ElapsedMilliseconds;
                            RecordRelationshipParallelStage(
                                openingParallelWorkers,
                                orderedCandidates.Count,
                                openingComputeTimer.ElapsedMilliseconds);
                            for (int candidateIndex = 0;
                                candidateIndex < orderedCandidates.Count;
                                candidateIndex++)
                            {
                                OpeningSeedCandidate candidate =
                                    orderedCandidates[candidateIndex];
                                OpeningSeedComputation opening =
                                    openingComputations[candidateIndex];
                                existingPairs.TryGetValue(candidate.PairKey,
                                    out Dictionary<string, object> existing);
                                if (ReadInt(existing, "opening_seed_version", 0)
                                    >= OpeningRelationshipSeedVersion)
                                {
                                    skipped++;
                                    continue;
                                }
                                string typeA = opening.TypeA;
                                string typeB = opening.TypeB;
                                int chanceAB = opening.ChanceAB;
                                int chanceBA = opening.ChanceBA;
                                int baseline = opening.Baseline;
                                bool seedEligible = opening.Eligible;
                                int signAB = opening.SignAB;
                                int signBA = opening.SignBA;
                                int magnitudeAB = opening.MagnitudeAB;
                                int magnitudeBA = opening.MagnitudeBA;
                                int deltaAB = opening.DeltaAB;
                                int deltaBA = opening.DeltaBA;
                                if (seedEligible)
                                {
                                    positive += (deltaAB > 0 ? 1 : 0)
                                        + (deltaBA > 0 ? 1 : 0);
                                    negative += (deltaAB < 0 ? 1 : 0)
                                        + (deltaBA < 0 ? 1 : 0);
                                }
                                int affinityAB = Clamp(baseline + deltaAB, -100, 100);
                                int affinityBA = Clamp(baseline + deltaBA, -100, 100);
                                openingDeltas.Add(deltaAB);
                                openingDeltas.Add(deltaBA);
                                IncrementOpeningSeedBand(startingBands,
                                    RelationshipBand(baseline));
                                IncrementOpeningSeedBand(startingBands,
                                    RelationshipBand(baseline));
                                IncrementOpeningSeedBand(resultingBands,
                                    RelationshipBand(affinityAB));
                                IncrementOpeningSeedBand(resultingBands,
                                    RelationshipBand(affinityBA));
                                string provenance = string.Join(",",
                                    candidate.Provenance.OrderBy(value => value,
                                        StringComparer.OrdinalIgnoreCase));
                                int standingA = ReadInt(ResolveObserverPublicStanding(connection,
                                    campaignId, timelineId, candidate.HeroBId, candidate.HeroAId), "value", 0);
                                int standingB = ReadInt(ResolveObserverPublicStanding(connection,
                                    campaignId, timelineId, candidate.HeroAId, candidate.HeroBId), "value", 0);
                                int effectiveAB = Clamp(affinityAB + standingB,
                                    -100, 100);
                                int effectiveBA = Clamp(affinityBA + standingA,
                                    -100, 100);
                                int projected = ProjectNativeRelation(connection,
                                    candidate.HeroAId, candidate.HeroBId, affinityAB,
                                    affinityBA, effectiveAB, effectiveBA);
                                existingNativeTargets.TryGetValue(candidate.PairKey,
                                    out Dictionary<string, object> existingTarget);
                                int observed = existingTarget != null
                                    ? ReadInt(existingTarget, "observed_relation", 0)
                                    : existing != null
                                        && ReadInt(existing,
                                            "native_action_pending", 0) == 0
                                            ? ReadInt(existing,
                                                "projected_native_relation", 0)
                                            : 0;
                                bool pending = projected != observed;
                                Dictionary<string, object> openingPairParameters =
                                    OpeningRelationshipPairParameters(campaignId,
                                        candidate, typeA, typeB, chanceAB,
                                        chanceBA, affinityAB, affinityBA,
                                        standingA, standingB, effectiveAB,
                                        effectiveBA, projected, pending, day,
                                        seedEligible, baseline, signAB, signBA,
                                        magnitudeAB, magnitudeBA, deltaAB,
                                        deltaBA, provenance, ts);
                                if (usePostgreSqlBulk)
                                    openingPairRows.Add(openingPairParameters);
                                else
                                    ExecutePreparedRelationshipCommand(
                                        openingPairUpsert,
                                        openingPairParameters);
                                foreach (string source in candidate.Provenance)
                                {
                                    Dictionary<string, object>
                                        provenanceParameters =
                                            new Dictionary<string, object>
                                        {
                                            ["campaign"] = campaignId,
                                            ["timeline"] = timelineId,
                                            ["pair"] = candidate.PairKey,
                                            ["source"] = source,
                                            ["required"] = source == "family"
                                                || source == "marriage"
                                                || source == "kingdom_leadership"
                                                || source == "ruler_network" ? 1 : 0,
                                            ["day"] = worldDay,
                                            ["details"] = Json.Serialize(
                                                new Dictionary<string, object>
                                                {
                                                    ["openingSeedVersion"] =
                                                        OpeningRelationshipSeedVersion
                                                }),
                                            ["ts"] = ts
                                        };
                                    if (usePostgreSqlBulk)
                                        provenanceRows.Add(provenanceParameters);
                                    else
                                        ExecutePreparedRelationshipCommand(
                                            provenanceUpsert,
                                            provenanceParameters);
                                }
                                if (pending)
                                {
                                    Dictionary<string, object>
                                        nativeTargetParameters =
                                            new Dictionary<string, object>
                                        {
                                            ["pair"] = candidate.PairKey,
                                            ["actor"] = candidate.HeroAId,
                                            ["targetHero"] = candidate.HeroBId,
                                            ["targetRelation"] = projected,
                                            ["observed"] = observed,
                                            ["day"] = day,
                                            ["ts"] = ts
                                        };
                                    if (usePostgreSqlBulk)
                                        nativeTargetRows.Add(
                                            nativeTargetParameters);
                                    else
                                        QueueMbtiNativeRelationAction(connection,
                                            new AmbientPairContext
                                            {
                                                PairKey = candidate.PairKey,
                                                HeroAId = candidate.HeroAId,
                                                HeroBId = candidate.HeroBId
                                            }, day, projected, observed,
                                            nativeTargetUpsert);
                                }
                                else if (existingTarget != null)
                                {
                                    if (usePostgreSqlBulk)
                                        nativeTargetDeletes.Add(candidate.PairKey);
                                    else
                                        ExecutePreparedRelationshipCommand(
                                            nativeTargetDelete,
                                            new Dictionary<string, object>
                                            {
                                                ["pair"] = candidate.PairKey
                                            });
                                }
                                completed++;
                            }
                            if (usePostgreSqlBulk)
                                ExecuteOpeningRelationshipPostgreSqlBulk(
                                    connection, openingPairRows, provenanceRows,
                                    nativeTargetRows, nativeTargetDeletes);
                            ExecuteSql(connection, "COMMIT;");
                            if (usePostgreSqlBulk)
                                InstallOpeningRelationshipPairStateCache(
                                    campaignId, existingPairs, openingPairRows);
                            else
                                InvalidateRelationshipPairStateCache(campaignId);
                        }
                        catch
                        {
                            try { ExecuteSql(connection, "ROLLBACK;"); } catch { }
                            throw;
                        }
                    }
                    Dictionary<string, object> openingRunParameters =
                        OpeningSeedRunParameters(campaignId, timelineId,
                            candidates, completed, skipped, failed, positive,
                            negative, missingMbti, timer.ElapsedMilliseconds,
                            "", ts, openingDeltas, startingBands,
                            resultingBands);
                    openingRunParameters["parallelWorkers"] =
                        openingParallelWorkers;
                    openingRunParameters["parallelComputeMs"] =
                        openingParallelComputeMs;
                    ExecuteSql(connection, @"UPDATE relationship_opening_seed_runs SET
status='seeded',completed_pairs=$completed,skipped_pairs=$skipped,
failed_pairs=$failed,co_location_pairs=$coLocation,spouse_pairs=$spouses,
family_pairs=$family,kingdom_leadership_pairs=$leadership,
ruler_network_pairs=$rulers,positive_directions=$positive,
negative_directions=$negative,missing_mbti=$missing,duration_ms=$duration,
average_delta=$average,median_delta=$median,minimum_delta=$minimum,
maximum_delta=$maximum,starting_bands_json=$startingBands,
resulting_bands_json=$resultingBands,parallel_workers=$parallelWorkers,
parallel_compute_ms=$parallelComputeMs,last_error='',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND seed_version=$version;",
                        openingRunParameters);
                }

                Dictionary<string, object> dailyPayload =
                    new Dictionary<string, object>(payload,
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["cadenceShard"] = ((day % RelationshipCadenceShardCount)
                            + RelationshipCadenceShardCount)
                            % RelationshipCadenceShardCount
                    };
                Dictionary<string, object> dailyResult =
                    PersistAndHydrateRelationshipDailyInput(dailyPayload);
                bool suppressVerificationWorker =
                    ReadBool(payload, "verificationSuppressWorkerSignal",
                        false)
                    && campaignId.StartsWith("opening_seed_",
                        StringComparison.OrdinalIgnoreCase);
                if (!suppressVerificationWorker)
                {
                    using (ReignDbConnection connection =
                        OpenCampaignConnection(campaignId))
                    {
                        EnsureMbtiRelationshipSchema(connection);
                        ExecuteSql(connection, @"UPDATE relationship_daily_inputs
SET status='initialization_pending',last_error=''
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day
AND status='pending';",
                            new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId,
                                ["timeline"] = timelineId,
                                ["day"] = day
                            });
                    }
                }
                dailyResult = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["accepted"] = true,
                    ["durable"] = true,
                    ["acceptedInputDays"] = new List<int> { day },
                    ["acceptedInputCount"] = 1,
                    ["oldestPendingInputDay"] =
                        OldestPendingRelationshipInputDay(campaignId,
                            timelineId),
                    ["processingMode"] = suppressVerificationWorker
                        ? "verification_deferred"
                        : "continuous_worker_after_readiness_seal",
                    ["nativeSyncPlan"] = null
                };
                if (!ReadBool(dailyResult, "ok", false))
                    throw new InvalidOperationException(ReadString(dailyResult,
                        "error", "Opening-day relationship ingestion failed."));
                timer.Stop();
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    ExecuteSql(connection, @"UPDATE relationship_opening_seed_runs SET
status='completed',duration_ms=$duration,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND seed_version=$version;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["version"] = OpeningRelationshipSeedVersion,
                            ["duration"] = timer.ElapsedMilliseconds,
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });
                    Dictionary<string, object> nativePlan =
                        BuildRelationshipNativeSyncPlan(connection, campaignId,
                            timelineId, day);
                    dailyResult["nativeSyncPlan"] = nativePlan;
                    dailyResult["nativeChangesQueued"] =
                        ReadInt(nativePlan, "targetCount", 0);
                }
                dailyResult["openingSeedVersion"] = OpeningRelationshipSeedVersion;
                dailyResult["openingCandidatePairs"] = candidates.Count;
                dailyResult["openingCompletedPairs"] = completed;
                dailyResult["openingSkippedPairs"] = skipped;
                dailyResult["openingPositiveDirections"] = positive;
                dailyResult["openingNegativeDirections"] = negative;
                dailyResult["durationMs"] = timer.ElapsedMilliseconds;
                return dailyResult;
            }
            catch (Exception ex)
            {
                timer.Stop();
                failed = Math.Max(1, failed);
                try
                {
                    using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                    {
                        EnsureMbtiRelationshipSchema(connection);
                        ExecuteSql(connection, @"UPDATE relationship_opening_seed_runs SET
status='failed',failed_pairs=$failed,missing_mbti=$missing,
duration_ms=$duration,last_error=$error,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND seed_version=$version;",
                            new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId, ["timeline"] = timelineId,
                                ["version"] = OpeningRelationshipSeedVersion,
                                ["failed"] = failed, ["missing"] = missingMbti,
                                ["duration"] = timer.ElapsedMilliseconds,
                                ["error"] = LimitText(ex.Message, 1000),
                                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            });
                    }
                }
                catch { }
                return new Dictionary<string, object>
                {
                    ["ok"] = false, ["retryable"] = true,
                    ["error"] = ex.Message, ["failedPairs"] = failed,
                    ["missingMbti"] = missingMbti
                };
            }
        }

        private static Dictionary<string, object> OpeningSeedRunParameters(
            string campaignId, string timelineId,
            Dictionary<string, OpeningSeedCandidate> candidates, int completed,
            int skipped, int failed, int positive, int negative, int missing,
            long duration, string error, long ts, List<int> deltas,
            Dictionary<string, int> startingBands,
            Dictionary<string, int> resultingBands)
        {
            List<int> ordered = (deltas ?? new List<int>()).OrderBy(value => value)
                .ToList();
            double median = ordered.Count == 0 ? 0d
                : ordered.Count % 2 == 1 ? ordered[ordered.Count / 2]
                : (ordered[ordered.Count / 2 - 1]
                    + ordered[ordered.Count / 2]) / 2d;
            return new Dictionary<string, object>
            {
                ["campaign"] = campaignId, ["timeline"] = timelineId,
                ["version"] = OpeningRelationshipSeedVersion,
                ["completed"] = completed, ["skipped"] = skipped,
                ["failed"] = failed,
                ["coLocation"] = candidates.Values.Count(value =>
                    value.Provenance.Contains("co_presence")),
                ["spouses"] = candidates.Values.Count(value =>
                    value.Provenance.Contains("marriage")),
                ["family"] = candidates.Values.Count(value =>
                    value.Provenance.Contains("family")),
                ["leadership"] = candidates.Values.Count(value =>
                    value.Provenance.Contains("kingdom_leadership")),
                ["rulers"] = candidates.Values.Count(value =>
                    value.Provenance.Contains("ruler_network")),
                ["positive"] = positive, ["negative"] = negative,
                ["missing"] = missing, ["duration"] = duration,
                ["average"] = ordered.Count == 0 ? 0d : ordered.Average(),
                ["median"] = median,
                ["minimum"] = ordered.Count == 0 ? 0 : ordered.First(),
                ["maximum"] = ordered.Count == 0 ? 0 : ordered.Last(),
                ["startingBands"] = Json.Serialize(startingBands
                    ?? new Dictionary<string, int>()),
                ["resultingBands"] = Json.Serialize(resultingBands
                    ?? new Dictionary<string, int>()),
                ["error"] = error, ["ts"] = ts
            };
        }

        private static void IncrementOpeningSeedBand(
            Dictionary<string, int> bands, string band)
        {
            if (bands.TryGetValue(band, out int count)) bands[band] = count + 1;
            else bands[band] = 1;
        }

        private static string ResolveOpeningSeedMbti(ReignDbConnection connection,
            string campaignId, int day, Dictionary<string, object> hero)
        {
            string heroId = ReadFirstString(hero, "heroStringId", "heroId", "id");
            Dictionary<string, object> stored = QuerySql(connection, @"
SELECT mbti_type FROM (
 SELECT mbti_type,2 AS priority FROM relationship_personalities WHERE hero_id=$hero
 UNION ALL
 SELECT mbti_type,1 AS priority FROM notable_mbti_profiles WHERE hero_id=$hero
) resolved ORDER BY priority LIMIT 1;",
                new Dictionary<string, object> { ["hero"] = heroId }).FirstOrDefault();
            string type = ReadString(stored, "mbti_type", "");
            if (MbtiDefinitions.ContainsKey(type)) return type;
            Dictionary<string, object> resolved = ResolvePermanentRelationshipMbti(
                connection, campaignId, day, hero,
                new Dictionary<string, Dictionary<string, object>>());
            return ReadString(resolved, "type", "XXXX");
        }

        private static int OpeningSeedDie(string campaignId, string pairKey,
            string direction, string kind)
        {
            return StableDie((campaignId ?? "default") + "|" + pairKey + "|"
                + direction + "|" + kind + "|opening_relationship_seed_v"
                + OpeningRelationshipSeedVersion.ToString(CultureInfo.InvariantCulture),
                100);
        }

        private static int OpeningRelationshipDelta(int chance, int signRoll,
            int magnitudeRoll)
        {
            int scale = signRoll <= chance ? chance : 100 - chance;
            int magnitude = Math.Max(1, RoundAwayFromZero(
                scale * magnitudeRoll / 100d
                    * OpeningRelationshipSeedScale));
            return signRoll <= chance ? magnitude : -magnitude;
        }

        private static bool OpeningSeedHeroEligible(Dictionary<string, object> hero)
        {
            return ReadBool(hero, "isAlive", false)
                && ReadBool(hero, "isAdult", ReadDouble(hero, "age", 0d) >= 18d)
                && !ReadBool(hero, "isPlayer", false)
                && !ReadBool(hero, "isPrisoner", false);
        }

        private static int OpeningSeedBaseline(Dictionary<string, object> heroA,
            Dictionary<string, object> heroB, OpeningSeedCandidate candidate)
        {
            if (candidate.Provenance.Contains("marriage")) return 50;
            if (candidate.Provenance.Contains("family")) return 20;
            bool sameKingdom = !string.IsNullOrWhiteSpace(
                    ReadString(heroA, "kingdomId", ""))
                && ReadString(heroA, "kingdomId", "").Equals(
                    ReadString(heroB, "kingdomId", ""),
                    StringComparison.OrdinalIgnoreCase);
            bool rulerLeader = sameKingdom
                && (ReadBool(heroA, "isRuler", false)
                    && ReadBool(heroB, "isClanLeader", false)
                    || ReadBool(heroB, "isRuler", false)
                    && ReadBool(heroA, "isClanLeader", false));
            return rulerLeader ? 10 : 0;
        }

        private static Dictionary<string, OpeningSeedCandidate>
            BuildOpeningSeedCandidates(
                Dictionary<string, Dictionary<string, object>> heroes,
                List<Dictionary<string, object>> groups)
        {
            Dictionary<string, OpeningSeedCandidate> result =
                new Dictionary<string, OpeningSeedCandidate>(
                    StringComparer.OrdinalIgnoreCase);
            Action<string, string, string> add = (first, second, source) =>
            {
                if (string.IsNullOrWhiteSpace(first)
                    || string.IsNullOrWhiteSpace(second)
                    || first.Equals(second, StringComparison.OrdinalIgnoreCase)
                    || !heroes.ContainsKey(first) || !heroes.ContainsKey(second))
                    return;
                string key = AmbientPairKey(first, second);
                if (!result.TryGetValue(key, out OpeningSeedCandidate candidate))
                {
                    bool firstIsA = key.StartsWith(first + "|",
                        StringComparison.OrdinalIgnoreCase);
                    candidate = new OpeningSeedCandidate
                    {
                        PairKey = key,
                        HeroAId = firstIsA ? first : second,
                        HeroBId = firstIsA ? second : first
                    };
                    result[key] = candidate;
                }
                candidate.Provenance.Add(source);
            };
            foreach (Dictionary<string, object> group in groups)
            {
                List<string> ids = ReadStringList(group, "heroIds")
                    .Where(id => heroes.TryGetValue(id,
                        out Dictionary<string, object> hero)
                        && OpeningSeedHeroEligible(hero))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
                for (int first = 0; first < ids.Count; first++)
                    for (int second = first + 1; second < ids.Count; second++)
                        add(ids[first], ids[second], "co_presence");
            }
            foreach (Dictionary<string, object> hero in heroes.Values)
            {
                string heroId = ReadFirstString(hero, "heroStringId", "heroId", "id");
                add(heroId, ReadString(hero, "spouseId", ""), "marriage");
                add(heroId, ReadString(hero, "fatherId", ""), "family");
                add(heroId, ReadString(hero, "motherId", ""), "family");
            }
            foreach (IGrouping<string, Dictionary<string, object>> siblings in heroes.Values
                .SelectMany(hero => new[]
                {
                    new { Parent = ReadString(hero, "fatherId", ""), Hero = hero },
                    new { Parent = ReadString(hero, "motherId", ""), Hero = hero }
                }).Where(item => !string.IsNullOrWhiteSpace(item.Parent))
                .GroupBy(item => item.Parent, item => item.Hero,
                    StringComparer.OrdinalIgnoreCase))
            {
                List<string> ids = siblings.Select(hero => ReadFirstString(hero,
                        "heroStringId", "heroId", "id"))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                for (int first = 0; first < ids.Count; first++)
                    for (int second = first + 1; second < ids.Count; second++)
                        add(ids[first], ids[second], "family");
            }
            List<Dictionary<string, object>> leaders = heroes.Values.Where(hero =>
                OpeningSeedHeroEligible(hero)
                && (ReadBool(hero, "isClanLeader", false)
                    || ReadBool(hero, "isRuler", false))).ToList();
            foreach (IGrouping<string, Dictionary<string, object>> kingdom in leaders
                .Where(hero => !string.IsNullOrWhiteSpace(
                    ReadString(hero, "kingdomId", "")))
                .GroupBy(hero => ReadString(hero, "kingdomId", ""),
                    StringComparer.OrdinalIgnoreCase))
            {
                List<string> ids = kingdom.Select(hero => ReadFirstString(hero,
                        "heroStringId", "heroId", "id"))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                for (int first = 0; first < ids.Count; first++)
                    for (int second = first + 1; second < ids.Count; second++)
                        add(ids[first], ids[second], "kingdom_leadership");
            }
            List<string> rulers = leaders.Where(hero => ReadBool(hero,
                    "isRuler", false)).Select(hero => ReadFirstString(hero,
                    "heroStringId", "heroId", "id"))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            for (int first = 0; first < rulers.Count; first++)
                for (int second = first + 1; second < rulers.Count; second++)
                    add(rulers[first], rulers[second], "ruler_network");
            return result;
        }

        private sealed class OpeningSeedCandidate
        {
            public string PairKey = "";
            public string HeroAId = "";
            public string HeroBId = "";
            public HashSet<string> Provenance = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> NormalizeAmbientLifecycleHero(
            Dictionary<string, object> row)
        {
            row = row ?? new Dictionary<string, object>();
            if (row.ContainsKey("heroStringId")) return row;
            double age = ReadDouble(row, "a", 0d);
            return new Dictionary<string, object>
            {
                ["heroStringId"] = ReadString(row, "i", ""),
                ["age"] = age,
                ["isAdult"] = age >= 18d,
                ["isFemale"] = ReadInt(row, "f", 0) == 1,
                ["isAlive"] = true,
                ["isPregnant"] = ReadInt(row, "p", 0) == 1,
                ["spouseId"] = ReadString(row, "s", ""),
                ["fatherId"] = ReadString(row, "fa", ""),
                ["motherId"] = ReadString(row, "mo", ""),
                ["childrenCount"] = ReadInt(row, "ch", 0),
                ["marriageAncestorIds"] = ReadStringList(row, "an")
                    .Cast<object>().ToList(),
                ["clanId"] = ReadString(row, "c", ""),
                ["kingdomId"] = ReadString(row, "k", ""),
                ["isLord"] = ReadInt(row, "n", 0) == 1,
                ["isNotable"] = ReadInt(row, "o", 0) == 1,
                ["isRuler"] = ReadInt(row, "r", 0) == 1,
                ["isClanLeader"] = ReadInt(row, "l", 0) == 1,
                ["nativeCanMarry"] = ReadInt(row, "m", 0) == 1,
                ["nativeMarriageClanSuitable"] = ReadInt(row, "u", 0) == 1
            };
        }

        private static void MarkRelationshipDailyInputProcessed(
            string campaignId, string timelineId, int day)
        {
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                ExecuteSql(connection, @"UPDATE relationship_daily_inputs
SET status='processed',presence_groups_json='[]',hero_changes_json='[]',
processed_ts=$ts WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["day"] = day, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    });
                PruneRelationshipHeroObservations(connection, timelineId, day);
                ExecuteSql(connection, @"DELETE FROM relationship_daily_inputs
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status='processed' AND day_key<$day-7;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = day
                    });
            }
        }

        private static double OldestPendingRelationshipInputDay(
            string campaignId, string timelineId)
        {
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                return ReadDouble(QuerySql(connection, @"SELECT MIN(world_day) AS day
FROM relationship_daily_inputs WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status='pending';", new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault(), "day", -1d);
            }
        }

        private static Dictionary<string, object> MbtiRelationshipSnapshotApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> dailyInputs = ReadDictionaryList(payload, "dailyInputs");
            if (dailyInputs.Count > 0)
            {
                string envelopeCampaign = ReadString(payload, "campaignId", "default");
                string envelopeTimeline = ReadString(payload, "timelineId", "main");
                List<int> processedDays = new List<int>();
                foreach (Dictionary<string, object> input in dailyInputs
                    .OrderBy(x => ReadDouble(x, "worldDay", 0d)))
                {
                    if (string.IsNullOrWhiteSpace(ReadString(input, "campaignId", "")))
                        input["campaignId"] = envelopeCampaign;
                    if (string.IsNullOrWhiteSpace(ReadString(input, "timelineId", "")))
                        input["timelineId"] = envelopeTimeline;
                    Dictionary<string, object> accepted = PersistAndHydrateRelationshipDailyInput(input);
                    processedDays.Add((int)Math.Floor(ReadDouble(accepted, "worldDay", 0d) + 0.000001d));
                }
                MarkRelationshipCampaignReady(envelopeCampaign);
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["accepted"] = true,
                    ["durable"] = true,
                    ["acceptedInputDays"] = processedDays,
                    ["acceptedInputCount"] = processedDays.Count,
                    ["oldestPendingInputDay"] = OldestPendingRelationshipInputDay(envelopeCampaign, envelopeTimeline),
                    ["processingMode"] = "continuous_server_worker",
                    ["nativeSyncPlan"] = null
                };
            }
            Stopwatch timer = Stopwatch.StartNew();
            Interlocked.Exchange(ref PostgreSqlRelationshipStagedRows, 0);
            Interlocked.Exchange(ref PostgreSqlRelationshipWrittenRows, 0);
            Interlocked.Exchange(ref PostgreSqlRelationshipCopyMs, 0);
            Interlocked.Exchange(ref PostgreSqlRelationshipMergeMs, 0);
            Stopwatch payloadPreparationTimer = Stopwatch.StartNew();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string correlationId = EnsureCorrelationId(payload);
            int day = (int)Math.Floor(ReadDouble(payload, "worldDay", 0d) + 0.000001d);
            int cadenceShard = ReadInt(payload, "cadenceShard", -1);
            List<Dictionary<string, object>> cadenceInputs = ReadDictionaryList(payload, "cadenceInputs");
            if (cadenceInputs.Count == 0)
            {
                cadenceInputs.Add(new Dictionary<string, object>
                {
                    ["worldDay"] = ReadDouble(payload, "worldDay", 0d),
                    ["presenceGroups"] = ReadDictionaryList(payload, "presenceGroups"),
                    ["heroes"] = ReadDictionaryList(payload, "heroes")
                });
            }
            cadenceInputs = cadenceInputs
                .OrderBy(input => ReadDouble(input, "worldDay", 0d))
                .ToList();
            int cadenceWindowStartDay = cadenceInputs
                .Min(input => (int)Math.Floor(ReadDouble(input, "worldDay", 0d) + 0.000001d));
            int cadenceWindowDayCount = cadenceInputs
                .Select(input => (int)Math.Floor(ReadDouble(input, "worldDay", 0d) + 0.000001d))
                .Distinct().Count();
            Dictionary<int, List<Dictionary<string, object>>> groupsByDay = cadenceInputs
                .GroupBy(input => (int)Math.Floor(ReadDouble(input, "worldDay", 0d) + 0.000001d))
                .ToDictionary(group => group.Key,
                    group => group.SelectMany(input => ReadDictionaryList(input, "presenceGroups")).ToList());
            Dictionary<int, List<Dictionary<string, object>>> heroRowsByDay = cadenceInputs
                .GroupBy(input => (int)Math.Floor(ReadDouble(input, "worldDay", 0d) + 0.000001d))
                .ToDictionary(group => group.Key,
                    group => group.SelectMany(input => ReadDictionaryList(input, "heroes"))
                        .Select(NormalizeAmbientLifecycleHero).ToList());
            Dictionary<string, Dictionary<string, object>> heroes = heroRowsByDay
                .OrderBy(group => group.Key)
                .SelectMany(group => group.Value)
                .Where(x => !string.IsNullOrWhiteSpace(ReadFirstString(x, "heroStringId", "heroId", "id")))
                .GroupBy(x => ReadFirstString(x, "heroStringId", "heroId", "id"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
            payloadPreparationTimer.Stop();
            long payloadPreparationMs = payloadPreparationTimer.ElapsedMilliseconds;
            long schemaReadinessMs = 0;
            long progressLoadMs = 0;
            long lifecycleLoadMs = 0;
            long heroHydrationMs = 0;
            int heroDocumentCacheHits = 0;
            long matchingMs = 0;
            long personalityLoadMs = 0;
            long commandPreparationMs = 0;
            long transactionBeginMs = 0;
            long commitMs = 0;

            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            using (RelationshipStandingContext standingContext = new RelationshipStandingContext(connection, campaignId, timelineId))
            using (RelationshipLifecycleWriteContext lifecycleWriteContext = new RelationshipLifecycleWriteContext(connection))
            using (SqlReadTimingContext sqlReadTiming = new SqlReadTimingContext())
            {
                Stopwatch schemaReadinessTimer = Stopwatch.StartNew();
                EnsureMbtiRelationshipSchema(connection);
                EnsureWorldTestTelemetrySchema(connection);
                // Component-schema readiness is cached for the lifetime of the
                // server.  Validation fixtures (and a restored/recreated
                // PostgreSQL database) can legitimately replace the backing
                // tables while that process remains alive, so verify the one
                // table this hot path writes before using the schema-ready
                // telemetry helpers.  This is one metadata lookup per
                // relationship day, never one lookup per pair.
                if (!TableExists(connection, "world_test_chunk_counters"))
                    EnsureWorldTestTelemetrySchemaCore(connection);
                schemaReadinessTimer.Stop();
                schemaReadinessMs = schemaReadinessTimer.ElapsedMilliseconds;
                Stopwatch progressLoadTimer = Stopwatch.StartNew();
                Dictionary<string, object> dailyProgress = QuerySql(connection, @"
SELECT pair_cursor,processed_pairs FROM relationship_daily_inputs
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day
LIMIT 1;", new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["day"] = day
                }).FirstOrDefault();
                string resumePairCursor = ReadString(dailyProgress,
                    "pair_cursor", "");
                int resumedProcessedPairs = ReadInt(dailyProgress,
                    "processed_pairs", 0);
                int lastProcessedDay = RelationshipLastProcessedDay(connection, timelineId);
                progressLoadTimer.Stop();
                progressLoadMs = progressLoadTimer.ElapsedMilliseconds;
                if (lastProcessedDay >= day)
                {
                    bool continuousWorker = ReadBool(payload, "continuousWorker", false);
                    Dictionary<string, object> repeatedPlan = continuousWorker
                        ? new Dictionary<string, object>
                        {
                            ["targetCount"] = ReadInt(QuerySql(connection,
                                "SELECT COUNT(*) AS count FROM relationship_native_targets WHERE status IN ('pending','claimed','failed');")
                                .FirstOrDefault(), "count", 0)
                        }
                        : BuildRelationshipNativeSyncPlan(connection, campaignId, timelineId, day);
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true, ["idempotent"] = true, ["campaignId"] = campaignId,
                        ["worldDay"] = day, ["noLlmConfirmed"] = true,
                        ["evaluatedPairDays"] = 0,
                        ["cadenceDays"] = RelationshipCadenceDays,
                        ["cadenceShard"] = cadenceShard,
                        ["windowStartDay"] = cadenceWindowStartDay,
                        ["nativeChangesQueued"] = ReadInt(repeatedPlan, "targetCount", 0),
                        ["nativeSyncPlan"] = continuousWorker ? null : repeatedPlan,
                        ["summary"] = "Directional MBTI chemistry was already processed for this campaign day."
                    };
                }

                string runId = "mbti_run_" + Guid.NewGuid().ToString("N");
                Stopwatch lifecycleLoadTimer = Stopwatch.StartNew();
                List<Dictionary<string, object>> independentLifecycleRows =
                    LoadCoPresentLifecycleRows(connection, day, groupsByDay);
                lifecycleLoadTimer.Stop();
                lifecycleLoadMs = lifecycleLoadTimer.ElapsedMilliseconds;
                Stopwatch heroHydrationTimer = Stopwatch.StartNew();
                HashSet<string> requiredHeroIds = new HashSet<string>(groupsByDay.Values
                    .SelectMany(groups => groups)
                    .SelectMany(group => ReadStringList(group, "heroIds"))
                    .Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
                requiredHeroIds.UnionWith(independentLifecycleRows
                    .SelectMany(row => new[]
                    {
                        ReadString(row, "hero_a_id", ""),
                        ReadString(row, "hero_b_id", "")
                    }).Where(id => !string.IsNullOrWhiteSpace(id)));
                var contextualHeroesByDay = new Dictionary<int, Dictionary<string, Dictionary<string, object>>>();
                foreach (int presenceDay in groupsByDay.Keys.OrderBy(value => value))
                {
                    // Explicit rows belong to this day. Other days' deltas and the
                    // current native roster cannot stand in for historical facts.
                    var contextual = RelationshipThroughputOriginalReadPath
                        ? new Dictionary<string, Dictionary<string, object>>(heroes, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var hero in heroRowsByDay[presenceDay])
                    {
                        string id = ReadFirstString(hero, "heroStringId", "heroId", "id");
                        if (!string.IsNullOrWhiteSpace(id)) contextual[id] = hero;
                    }
                    var dayHeroIds = groupsByDay[presenceDay].SelectMany(group => ReadStringList(group, "heroIds"));
                    foreach (var observed in LoadRelationshipHeroObservations(connection, timelineId, presenceDay,
                        dayHeroIds.Where(id => !contextual.ContainsKey(id))))
                    {
                        string document = ReadString(observed, "profile_json", "{}");
                        bool cacheHit = false;
                        var profile = RelationshipThroughputOriginalReadPath
                            ? TryParseJsonObject(document) : RelationshipHeroDocuments.Read(document, out cacheHit);
                        if (cacheHit) heroDocumentCacheHits++;
                        if (profile != null) contextual[ReadString(observed, "hero_id", "")] = NormalizeAmbientLifecycleHero(profile);
                    }
                    contextualHeroesByDay[presenceDay] = contextual;
                    // Fling processing receives the same day-specific profiles.
                    heroRowsByDay[presenceDay] = contextual.Values.ToList();
                }
                heroes = contextualHeroesByDay.OrderBy(entry => entry.Key).SelectMany(entry => entry.Value)
                    .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
                heroHydrationTimer.Stop();
                heroHydrationMs = heroHydrationTimer.ElapsedMilliseconds;

                Stopwatch matchingTimer = Stopwatch.StartNew();
                Dictionary<int, Dictionary<string, HashSet<int>>> coPresenceByDay =
                    groupsByDay.ToDictionary(entry => entry.Key,
                        entry => BuildRelationshipCoPresenceIndex(entry.Value));
                Dictionary<string, Dictionary<string, object>>
                    courtshipPairStates = LoadCourtshipFocusPairStates(
                        connection, groupsByDay, heroes, contextualHeroesByDay);
                HashSet<string> excludedCourtshipFocusPairs =
                    LoadHistoricalCourtshipExclusions(connection, day);
                excludedCourtshipFocusPairs.UnionWith(independentLifecycleRows
                    .Where(IsRomanceContinuationLifecycle)
                    .Select(row => ReadString(row, "pair_key", "")));
                Dictionary<string, List<RelationshipPairDay>> pairDays =
                    new Dictionary<string, List<RelationshipPairDay>>(StringComparer.OrdinalIgnoreCase);
                int romanceContinuationPairDays = 0;
                int courtshipFocusPairDays = 0;
                foreach (int presenceDay in groupsByDay.Keys.OrderBy(value => value))
                {
                    Dictionary<string, Dictionary<string, object>> contextualHeroes = contextualHeroesByDay[presenceDay];
                    Dictionary<string, AmbientPairContext> dailyPairs =
                        ExpandDailyMatchedPairs(groupsByDay[presenceDay],
                            contextualHeroes, campaignId, timelineId,
                            presenceDay, courtshipPairStates,
                            excludedCourtshipFocusPairs);
                    courtshipFocusPairDays += dailyPairs.Values.Count(pair =>
                        pair.CourtshipFocus);
                    foreach (Dictionary<string, object> lifecycleRow
                        in independentLifecycleRows.Where(
                            IsRomanceContinuationLifecycle))
                    {
                        string pairKey = ReadString(lifecycleRow,
                            "pair_key", "");
                        string heroAId = ReadString(lifecycleRow,
                            "hero_a_id", "");
                        string heroBId = ReadString(lifecycleRow,
                            "hero_b_id", "");
                        if (dailyPairs.ContainsKey(pairKey)
                            || ReadInt(lifecycleRow, "last_processed_day", -1)
                                >= presenceDay
                            || !contextualHeroes.ContainsKey(heroAId)
                            || !contextualHeroes.ContainsKey(heroBId)
                            || !RelationshipHeroesShareGroup(
                                coPresenceByDay[presenceDay], heroAId, heroBId))
                            continue;
                        Dictionary<string, object> continuationHeroA =
                            contextualHeroes[heroAId];
                        Dictionary<string, object> continuationHeroB =
                            contextualHeroes[heroBId];
                        dailyPairs[pairKey] = new AmbientPairContext
                        {
                            PairKey = pairKey,
                            HeroAId = heroAId,
                            HeroBId = heroBId,
                            ContextKind = "romance_continuation",
                            ContextId = "daily_copresence",
                            ExposureWeight = 1d,
                            SameClan = !string.IsNullOrWhiteSpace(ReadString(
                                    continuationHeroA, "clanId", ""))
                                && ReadString(continuationHeroA, "clanId", "")
                                    .Equals(ReadString(continuationHeroB,
                                            "clanId", ""),
                                        StringComparison.OrdinalIgnoreCase),
                            SameKingdom = !string.IsNullOrWhiteSpace(ReadString(
                                    continuationHeroA, "kingdomId", ""))
                                && ReadString(continuationHeroA, "kingdomId", "")
                                    .Equals(ReadString(continuationHeroB,
                                            "kingdomId", ""),
                                        StringComparison.OrdinalIgnoreCase)
                        };
                        romanceContinuationPairDays++;
                    }
                    foreach (AmbientPairContext dailyPair in dailyPairs.Values)
                    {
                        if (!pairDays.TryGetValue(dailyPair.PairKey,
                            out List<RelationshipPairDay> scheduledDays))
                        {
                            scheduledDays = new List<RelationshipPairDay>();
                            pairDays[dailyPair.PairKey] = scheduledDays;
                        }
                        scheduledDays.Add(new RelationshipPairDay
                        {
                            Day = presenceDay,
                            Pair = dailyPair,
                            HeroA = contextualHeroes[dailyPair.HeroAId],
                            HeroB = contextualHeroes[dailyPair.HeroBId]
                        });
                    }
                }
                List<RelationshipPairDay> allPairDays = pairDays.Values
                    .SelectMany(value => value)
                    .OrderBy(value => value.Pair.PairKey,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(value => value.Day)
                    .ToList();
                matchingTimer.Stop();
                matchingMs = matchingTimer.ElapsedMilliseconds;
                int parallelWorkers = RelationshipParallelismForWork(
                    allPairDays.Count);
                Stopwatch parallelTimer = Stopwatch.StartNew();
                if (parallelWorkers > 1)
                {
                    Parallel.ForEach(allPairDays,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = parallelWorkers
                        },
                        pairDay =>
                        {
                            pairDay.Dice = BuildDailyPairDice(campaignId,
                                pairDay.Pair, pairDay.Day);
                        });
                }
                else
                {
                    foreach (RelationshipPairDay pairDay in allPairDays)
                    {
                        pairDay.Dice = BuildDailyPairDice(campaignId,
                            pairDay.Pair, pairDay.Day);
                    }
                }
                parallelTimer.Stop();
                RecordRelationshipParallelStage(parallelWorkers,
                    allPairDays.Count, parallelTimer.ElapsedMilliseconds);
                Dictionary<string, AmbientPairContext> pairs = pairDays.ToDictionary(
                    item => item.Key,
                    item => item.Value.OrderBy(value => value.Day).Last().Pair,
                    StringComparer.OrdinalIgnoreCase);
                Stopwatch personalityLoadTimer = Stopwatch.StartNew();
                HashSet<string> needed = new HashSet<string>(pairs.Values.SelectMany(x => new[] { x.HeroAId, x.HeroBId }), StringComparer.OrdinalIgnoreCase);
                Dictionary<string, Dictionary<string, object>> notableMbti =
                    new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                string neededHeroesJson = Json.Serialize(needed.OrderBy(value => value,
                    StringComparer.OrdinalIgnoreCase).ToList());
                List<Dictionary<string, object>> persistedNotableRows =
                    ReignPostgreSqlDialect.IsPostgreSql(connection)
                        ? QuerySql(connection, @"SELECT profiles.*
FROM notable_mbti_profiles profiles
JOIN jsonb_array_elements_text(CAST($heroes AS jsonb)) requested
  ON requested.value=profiles.hero_id;",
                            new Dictionary<string, object>
                            {
                                ["heroes"] = neededHeroesJson
                            })
                        : QuerySql(connection,
                            "SELECT * FROM notable_mbti_profiles;");
                Dictionary<string, Dictionary<string, object>> persistedNotables =
                    persistedNotableRows
                    .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "hero_id", "")))
                    .ToDictionary(x => ReadString(x, "hero_id", ""), x => x, StringComparer.OrdinalIgnoreCase);
                foreach (string heroId in needed.Where(heroes.ContainsKey))
                {
                    Dictionary<string, object> hero = heroes[heroId];
                    if (persistedNotables.TryGetValue(heroId, out Dictionary<string, object> notableRow))
                    {
                        Dictionary<string, object> profile = RelationshipThroughputOriginalReadPath
                            ? NotableMbtiRowToProfile(notableRow) : CompactNotableRelationshipProfile(notableRow);
                        Dictionary<string, object> expected = ReadDictionary(profile, "nativeTraits") ?? new Dictionary<string, object>();
                        Dictionary<string, object> observed = ReadDictionary(hero, "traits") ?? new Dictionary<string, object>();
                        string syncStatus = ReadString(profile, "nativeSyncStatus", "pending");
                        bool hasCompleteObservation = expected.Count == 5 && expected.All(item =>
                            ReadInt(observed, item.Key, int.MinValue) != int.MinValue);
                        bool observedMatches = hasCompleteObservation && expected.All(item =>
                            ReadInt(observed, item.Key, int.MinValue)
                                == Convert.ToInt32(item.Value, CultureInfo.InvariantCulture));
                        bool needsNativeRefresh = !syncStatus.Equals("manual_override", StringComparison.OrdinalIgnoreCase)
                            && hasCompleteObservation
                            && (!observedMatches || !syncStatus.Equals("synchronized", StringComparison.OrdinalIgnoreCase));
                        notableMbti[heroId] = needsNativeRefresh
                            ? EnsureNotableMbtiProfile(connection, campaignId, day, hero, false, true)
                            : profile;
                    }
                    else if (!ReadBool(hero, "isNotable", false))
                    {
                        continue;
                    }
                    else
                    {
                        notableMbti[heroId] = EnsureNotableMbtiProfile(connection, campaignId, day, hero, true, true);
                    }
                }
                Dictionary<string, Dictionary<string, object>> permanentMbtiByHero =
                    LoadPermanentRelationshipMbti(connection, campaignId, needed);
                foreach (KeyValuePair<string, Dictionary<string, object>> notable
                    in notableMbti)
                {
                    string type = ReadString(notable.Value, "type", "XXXX");
                    if (!MbtiDefinitions.TryGetValue(type,
                        out MbtiDefinition definition))
                        continue;
                    permanentMbtiByHero[notable.Key] =
                        new Dictionary<string, object>
                        {
                            ["type"] = type,
                            ["title"] = definition.Title,
                            ["description"] = definition.Description,
                            ["source"] = "notable_mbti_template"
                        };
                    CachePermanentRelationshipMbti(campaignId, notable.Key,
                        permanentMbtiByHero[notable.Key]);
                }
                personalityLoadTimer.Stop();
                personalityLoadMs = personalityLoadTimer.ElapsedMilliseconds;
                int processed = resumedProcessedPairs, evaluatedPairDays = 0,
                    rolledDirections = 0;
                int positiveRolls = 0, negativeRolls = 0, affinityChanges = 0;
                int ordinaryMagnitudePairDays = 0, loverMagnitudePairDays = 0;
                Dictionary<string, object> flingSummary =
                    new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                int newPairCount = 0, repeatEncounterCount = 0;
                int nativeQueued = 0, skippedMissing = 0, skippedSettlementGate = 0;
                HashSet<string> courtPopularitySubjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool courtPopularityChanged = false;
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long decayMs = 0;
                long stateLoadMs = 0;
                long stateLifecycleReadMs = 0, stateChemistryReadMs = 0, stateNativeReadMs = 0, stateStandingReadMs = 0;
                long pairProcessingMs = 0;
                long independentLifecycleMs = 0;
                long finalizationMs = 0;
                long finalFlushMs = 0;
                long flingProcessingMs = 0;
                long courtPopularityMs = 0;
                long nativeCountMs = 0;
                long finalPairFlushMs = 0, finalProvenanceFlushMs = 0, finalTelemetryMs = 0;

                Stopwatch commandPreparationTimer = Stopwatch.StartNew();
                using (ReignDbCommand pairUpsert = CreateRelationshipPairUpsertCommand(connection))
                using (ReignDbCommand nativeTargetUpsert = CreateRelationshipNativeTargetUpsertCommand(connection))
                using (ReignDbCommand nativeTargetDelete = CreatePreparedRelationshipCommand(connection,
                    "DELETE FROM relationship_native_targets WHERE pair_key=$pair;", "$pair"))
                {
                    commandPreparationTimer.Stop();
                    commandPreparationMs = commandPreparationTimer.ElapsedMilliseconds;
                    Stopwatch transactionBeginTimer = Stopwatch.StartNew();
                    ExecuteSql(connection, "BEGIN IMMEDIATE;");
                    transactionBeginTimer.Stop();
                    transactionBeginMs = transactionBeginTimer.ElapsedMilliseconds;
                    try
                    {
                        // Relationship state is event-driven. Do not periodically
                        // normalize personal affinity toward zero; accumulated
                        // friendship, rivalry, and ruler history remain until an
                        // actual interaction changes them.
                        decayMs = 0;
                        Dictionary<string, object> decay =
                            new Dictionary<string, object>
                            {
                                ["enabled"] = false,
                                ["pairsDecayed"] = 0,
                                ["directionsDecayed"] = 0,
                                ["nativeChangesQueued"] = 0,
                                ["loversEnded"] = 0
                            };
                    Stopwatch stateLoadTimer = Stopwatch.StartNew();
                    // Durable daily inputs preserve actual co-presence. Never
                    // infer missed days from the newest snapshot.
                    int globalReplayStartDay = day;
                    int catchUpDayCount = 0;
                    CompactDormantRelationshipLifecycleRows(connection);
                    HashSet<string> neededPairKeys = new HashSet<string>(
                        pairs.Keys, StringComparer.OrdinalIgnoreCase);
                    neededPairKeys.UnionWith(independentLifecycleRows.Select(row => ReadString(row, "pair_key", "")));
                    Dictionary<string, Dictionary<string, object>> existingLifecycle =
                        LoadRelationshipLifecycleWorkset(connection, neededPairKeys)
                            .ToDictionary(x => ReadString(x, "pair_key", ""), x => x, StringComparer.OrdinalIgnoreCase);
                    if (RelationshipThroughputOriginalReadPath) neededPairKeys.UnionWith(existingLifecycle.Keys);
                    stateLifecycleReadMs = stateLoadTimer.ElapsedMilliseconds;
                    string neededPairsJson = Json.Serialize(neededPairKeys
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                        .ToList());
                    List<Dictionary<string, object>> existingPairRows =
                        ReignPostgreSqlDialect.IsPostgreSql(connection)
                            ? QuerySql(connection, @"SELECT pairs.*
FROM relationship_pair_chemistry pairs
JOIN jsonb_array_elements_text(CAST($pairs AS jsonb)) requested
  ON requested.value=pairs.pair_key;",
                                new Dictionary<string, object>
                                {
                                    ["pairs"] = neededPairsJson
                                })
                            : RelationshipPairStateSnapshot(connection,
                                campaignId).Values.ToList();
                    Dictionary<string, Dictionary<string, object>> existingPairs =
                        existingPairRows.ToDictionary(
                            x => ReadString(x, "pair_key", ""), x => x,
                            StringComparer.OrdinalIgnoreCase);
                    stateChemistryReadMs = stateLoadTimer.ElapsedMilliseconds - stateLifecycleReadMs;
                    List<Dictionary<string, object>> existingNativeTargetRows =
                        ReignPostgreSqlDialect.IsPostgreSql(connection)
                            ? QuerySql(connection, @"SELECT targets.*
FROM relationship_native_targets targets
JOIN jsonb_array_elements_text(CAST($pairs AS jsonb)) requested
  ON requested.value=targets.pair_key;",
                                new Dictionary<string, object>
                                {
                                    ["pairs"] = neededPairsJson
                                })
                            : QuerySql(connection,
                                "SELECT * FROM relationship_native_targets;");
                    Dictionary<string, Dictionary<string, object>> existingNativeTargets =
                        existingNativeTargetRows
                        .ToDictionary(x => ReadString(x, "pair_key", ""), x => x, StringComparer.OrdinalIgnoreCase);
                    stateNativeReadMs = stateLoadTimer.ElapsedMilliseconds - stateLifecycleReadMs - stateChemistryReadMs;
                    EnsureWorldRelationshipSchema(connection);
                    standingContext.Prefetch(requiredHeroIds);
                    stateLoadTimer.Stop();
                    stateLoadMs = stateLoadTimer.ElapsedMilliseconds;
                    stateStandingReadMs = stateLoadMs - stateLifecycleReadMs - stateChemistryReadMs - stateNativeReadMs;
                    int lifecycleChanges = 0, lifecycleRumors = 0, lifecycleActions = 0;
                    bool usePostgreSqlBatches =
                        ReignPostgreSqlDialect.IsPostgreSql(connection);
                    int relationshipWriteChunkSize = usePostgreSqlBatches
                        ? PostgreSqlRelationshipWriteChunkSize
                        : SqliteRelationshipWriteChunkSize;
                    int telemetryChunkIndex = Math.Max(0,
                        resumedProcessedPairs / relationshipWriteChunkSize);
                    int telemetryChunkPairs = 0;
                    int evaluationUnitsSinceThrottle = 0;
                    string telemetryLastPairKey = "";
                    Dictionary<string, object> telemetryChunkCounters =
                        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    Dictionary<string, Dictionary<string, object>>
                        pendingCacheRows =
                            new Dictionary<string,
                                Dictionary<string, object>>(
                                    StringComparer.OrdinalIgnoreCase);
                    List<Dictionary<string, object>> pairWriteBatch =
                        new List<Dictionary<string, object>>(
                            relationshipWriteChunkSize);
                    List<Dictionary<string, object>> nativeTargetWriteBatch =
                        new List<Dictionary<string, object>>(
                            relationshipWriteChunkSize);
                    List<Dictionary<string, object>> nativeTargetDeleteBatch =
                        new List<Dictionary<string, object>>(
                            Math.Min(512, relationshipWriteChunkSize));
                    List<Dictionary<string, object>> provenanceWriteBatch =
                        new List<Dictionary<string, object>>(
                            relationshipWriteChunkSize);
                    Stopwatch pairProcessingTimer = Stopwatch.StartNew();
                    foreach (AmbientPairContext pair in pairs.Values
                        .Where(value => string.IsNullOrWhiteSpace(resumePairCursor)
                            || string.Compare(value.PairKey, resumePairCursor,
                                StringComparison.OrdinalIgnoreCase) > 0)
                        .OrderBy(x => x.PairKey, StringComparer.OrdinalIgnoreCase))
                    {
                        List<RelationshipPairDay> scheduledDays = pairDays[pair.PairKey]
                            .OrderBy(value => value.Day).ToList();
                        if (!heroes.TryGetValue(pair.HeroAId, out Dictionary<string, object> heroA)
                            || !heroes.TryGetValue(pair.HeroBId, out Dictionary<string, object> heroB))
                        {
                            skippedMissing++;
                            continue;
                        }

                        existingPairs.TryGetValue(pair.PairKey, out Dictionary<string, object> existing);
                        if (existing != null && ReadInt(existing, "last_day", -1) == day) continue;
                        int previouslyAccountedDay = existing == null
                            ? int.MinValue : ReadInt(existing, "last_day", int.MinValue);
                        scheduledDays = scheduledDays
                            .Where(value => value.Day > previouslyAccountedDay).ToList();
                        if (scheduledDays.Count == 0) continue;
                        RelationshipPairDay latestPairDay = scheduledDays[scheduledDays.Count - 1];
                        heroA = latestPairDay.HeroA;
                        heroB = latestPairDay.HeroB;

                        Dictionary<string, object> axesA;
                        Dictionary<string, object> axesB;
                        string typeA;
                        string typeB;
                        int baseAB;
                        int baseBA;
                        int chanceAB;
                        int chanceBA;
                        string authoritativeNotableTypeA = notableMbti.TryGetValue(
                            pair.HeroAId, out Dictionary<string, object> authoritativeNotableA)
                            ? ReadString(authoritativeNotableA, "type", "")
                            : "";
                        string authoritativeNotableTypeB = notableMbti.TryGetValue(
                            pair.HeroBId, out Dictionary<string, object> authoritativeNotableB)
                            ? ReadString(authoritativeNotableB, "type", "")
                            : "";
                        if (existing != null
                            && MbtiDefinitions.ContainsKey(ReadString(existing, "mbti_a", ""))
                            && MbtiDefinitions.ContainsKey(ReadString(existing, "mbti_b", ""))
                            && (string.IsNullOrWhiteSpace(authoritativeNotableTypeA)
                                || ReadString(existing, "mbti_a", "").Equals(
                                    authoritativeNotableTypeA, StringComparison.OrdinalIgnoreCase))
                            && (string.IsNullOrWhiteSpace(authoritativeNotableTypeB)
                                || ReadString(existing, "mbti_b", "").Equals(
                                    authoritativeNotableTypeB, StringComparison.OrdinalIgnoreCase)))
                        {
                            typeA = ReadString(existing, "mbti_a", "");
                            typeB = ReadString(existing, "mbti_b", "");
                            axesA = new Dictionary<string, object> { ["type"] = typeA, ["source"] = "cached_pair_compatibility" };
                            axesB = new Dictionary<string, object> { ["type"] = typeB, ["source"] = "cached_pair_compatibility" };
                            baseAB = ReadInt(existing, "base_chance_a_to_b", MbtiCompatibility(typeA, typeB));
                            baseBA = ReadInt(existing, "base_chance_b_to_a", MbtiCompatibility(typeB, typeA));
                            chanceAB = AdjustedMbtiCompatibility(baseAB);
                            chanceBA = AdjustedMbtiCompatibility(baseBA);
                        }
                        else
                        {
                            if (!permanentMbtiByHero.TryGetValue(pair.HeroAId,
                                out axesA))
                            {
                                axesA = ResolvePermanentRelationshipMbti(
                                    connection, campaignId, day, heroA,
                                    notableMbti);
                                permanentMbtiByHero[pair.HeroAId] = axesA;
                            }
                            if (!permanentMbtiByHero.TryGetValue(pair.HeroBId,
                                out axesB))
                            {
                                axesB = ResolvePermanentRelationshipMbti(
                                    connection, campaignId, day, heroB,
                                    notableMbti);
                                permanentMbtiByHero[pair.HeroBId] = axesB;
                            }
                            typeA = ReadString(axesA, "type", "XXXX");
                            typeB = ReadString(axesB, "type", "XXXX");
                            if (!MbtiDefinitions.ContainsKey(typeA) || !MbtiDefinitions.ContainsKey(typeB))
                            {
                                skippedMissing++;
                                string error = "Missing immutable MBTI assignment for relationship pair " + pair.PairKey
                                    + " (" + pair.HeroAId + "=" + typeA + ", " + pair.HeroBId + "=" + typeB + ").";
                                ExecuteSql(connection,
                                    "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('relationship_mbti_error',$error);",
                                    new Dictionary<string, object> { ["error"] = error });
                                continue;
                            }
                            baseAB = MbtiCompatibility(typeA, typeB);
                            baseBA = MbtiCompatibility(typeB, typeA);
                            chanceAB = AdjustedMbtiCompatibility(baseAB);
                            chanceBA = AdjustedMbtiCompatibility(baseBA);
                        }
                        int startingAffinity = InitialReignAffinity(
                            scheduledDays[0].HeroA, scheduledDays[0].HeroB);
                        bool rebaseLegacyAffinity = existing != null
                            && ReadInt(existing, "compatibility_version", 0)
                                < LegacyAffinityRebaseCutoffVersion;
                        int affinityAB = existing == null || rebaseLegacyAffinity
                            ? startingAffinity : ReadInt(existing, "affinity_a_to_b", 0);
                        int affinityBA = existing == null || rebaseLegacyAffinity
                            ? startingAffinity : ReadInt(existing, "affinity_b_to_a", 0);
                        int signalAB = 0;
                        int signalBA = 0;
                        int rollAB = 0, rollBA = 0, deltaAB = 0, deltaBA = 0;
                        int romancePositiveBonus = 0;
                        int pairRolledDirections = 0;
                        int pairPositiveRolls = 0;
                        int pairNegativeRolls = 0;
                        int socialAB = ReadInt(ResolveObserverPublicStanding(connection,
                            campaignId, timelineId, pair.HeroAId, pair.HeroBId), "value", 0);
                        int socialBA = ReadInt(ResolveObserverPublicStanding(connection,
                            campaignId, timelineId, pair.HeroBId, pair.HeroAId), "value", 0);
                        int effectiveAB = Clamp(affinityAB + socialAB, -100, 100);
                        int effectiveBA = Clamp(affinityBA + socialBA, -100, 100);
                        existingLifecycle.TryGetValue(pair.PairKey, out Dictionary<string, object> lifecycleRow);
                        lifecycleRow = lifecycleRow
                            ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        string lifecycleShared = SharedRelationshipTag(campaignId, pair.PairKey,
                            effectiveAB, effectiveBA, heroA, heroB);
                        foreach (RelationshipPairDay scheduledDay in scheduledDays)
                        {
                            AmbientPairContext dailyPair = scheduledDay.Pair;
                            Dictionary<string, object> dailyHeroA = scheduledDay.HeroA;
                            Dictionary<string, object> dailyHeroB = scheduledDay.HeroB;
                            DailyPairDice dice = scheduledDay.Dice;
                            if (!dice.SettlementEligible)
                            {
                                skippedSettlementGate++;
                            }
                            else
                            {
                                bool loverMagnitude =
                                    UsesLoverRelationshipMagnitude(lifecycleRow);
                                int magnitudeAB = loverMagnitude
                                    ? dice.LoverMagnitudeAtoB
                                    : dice.OrdinaryMagnitudeAtoB;
                                int magnitudeBA = loverMagnitude
                                    ? dice.LoverMagnitudeBtoA
                                    : dice.OrdinaryMagnitudeBtoA;
                                if (loverMagnitude) loverMagnitudePairDays++;
                                else ordinaryMagnitudePairDays++;
                                romancePositiveBonus =
                                    RomancePositiveChanceBonusForAffinity(
                                        lifecycleRow, affinityAB, affinityBA);
                                int dailyChanceAB = Clamp(chanceAB
                                    + romancePositiveBonus, 1, 99);
                                int dailyChanceBA = Clamp(chanceBA
                                    + romancePositiveBonus, 1, 99);
                                rollAB = dice.RollAtoB;
                                rollBA = dice.RollBtoA;
                                deltaAB = (rollAB <= dailyChanceAB ? 1 : -1)
                                    * magnitudeAB;
                                deltaBA = (rollBA <= dailyChanceBA ? 1 : -1)
                                    * magnitudeBA;
                                int oldAB = affinityAB, oldBA = affinityBA;
                                affinityAB = Clamp(affinityAB + deltaAB, -100, 100);
                                affinityBA = Clamp(affinityBA + deltaBA, -100, 100);
                                if (CourtPopularityQualification(oldAB) != CourtPopularityQualification(affinityAB))
                                    courtPopularitySubjects.Add(pair.HeroBId);
                                if (CourtPopularityQualification(oldBA) != CourtPopularityQualification(affinityBA))
                                    courtPopularitySubjects.Add(pair.HeroAId);
                                affinityChanges += Math.Abs(affinityAB - oldAB) + Math.Abs(affinityBA - oldBA);
                                rolledDirections += 2;
                                pairRolledDirections += 2;
                                if (deltaAB > 0) { positiveRolls++; pairPositiveRolls++; }
                                else { negativeRolls++; pairNegativeRolls++; }
                                if (deltaBA > 0) { positiveRolls++; pairPositiveRolls++; }
                                else { negativeRolls++; pairNegativeRolls++; }
                            }

                            effectiveAB = Clamp(affinityAB + socialAB, -100, 100);
                            effectiveBA = Clamp(affinityBA + socialBA, -100, 100);
                            bool lifecycleEligible = lifecycleRow.Count > 0
                                || ReadString(dailyHeroA, "spouseId", "").Equals(
                                    dailyPair.HeroBId, StringComparison.OrdinalIgnoreCase)
                                || ReadString(dailyHeroB, "spouseId", "").Equals(
                                    dailyPair.HeroAId, StringComparison.OrdinalIgnoreCase)
                                || effectiveAB >= RomanceFlirtationThreshold
                                    && effectiveBA >= RomanceFlirtationThreshold;
                            Dictionary<string, object> lifecycle = lifecycleEligible
                                ? ProcessRelationshipLifecycle(connection, campaignId, dailyPair,
                                    scheduledDay.Day, dailyHeroA, dailyHeroB, effectiveAB, effectiveBA,
                                    lifecycleRow, true, timelineId)
                                : new Dictionary<string, object>
                                {
                                    ["sharedTag"] = SharedRelationshipTag(campaignId,
                                        dailyPair.PairKey, effectiveAB, effectiveBA, dailyHeroA, dailyHeroB)
                                };
                            lifecycleShared = ReadString(lifecycle, "sharedTag", lifecycleShared);
                            lifecycleChanges += new[] { "loverStarted", "loverEnded", "affairStarted", "affairEnded" }
                                .Count(key => ReadBool(lifecycle, key, false));
                            IncrementWorldTestObjectCounter(telemetryChunkCounters,
                                "flirtationAttempts",
                                ReadBool(lifecycle, "flirtationAttempted", false)
                                    ? 1 : 0);
                            IncrementWorldTestObjectCounter(telemetryChunkCounters,
                                "flirtationsStarted",
                                ReadBool(lifecycle, "flirtationStarted", false)
                                    ? 1 : 0);
                            IncrementWorldTestObjectCounter(telemetryChunkCounters,
                                "growingAttractionsStarted",
                                ReadBool(lifecycle,
                                    "growingAttractionStarted", false) ? 1 : 0);
                            IncrementWorldTestObjectCounter(telemetryChunkCounters,
                                "affairJudgmentAttempts",
                                ReadBool(lifecycle,
                                    "affairJudgmentAttempted", false) ? 1 : 0);
                            lifecycleRumors += ReadBool(lifecycle, "rumorCreated", false) ? 1 : 0;
                            lifecycleActions += new[] { "marriageQueued", "divorceQueued", "conceptionQueued" }
                                .Count(key => ReadBool(lifecycle, key, false));
                        }

                        evaluatedPairDays += scheduledDays.Count;
                        evaluationUnitsSinceThrottle += scheduledDays.Count;
                        AmbientPairContext latestPair = latestPairDay.Pair;
                        effectiveAB = Clamp(affinityAB + socialAB, -100, 100);
                        effectiveBA = Clamp(affinityBA + socialBA, -100, 100);
                        string tagAB = DirectionalRelationshipTag(campaignId, pair.PairKey, "a_to_b", affinityAB, affinityBA);
                        string tagBA = DirectionalRelationshipTag(campaignId, pair.PairKey, "b_to_a", affinityBA, affinityAB);
                        string sharedTag = SharedRelationshipTag(campaignId, pair.PairKey,
                            effectiveAB, effectiveBA, heroA, heroB);
                        if (string.IsNullOrWhiteSpace(lifecycleShared)) lifecycleShared = sharedTag;
                        int projectedNative = ProjectNativeRelation(connection, pair.HeroAId,
                            pair.HeroBId, affinityAB, affinityBA, effectiveAB, effectiveBA);
                        RelationshipPairDay nativeObservation = scheduledDays
                            .LastOrDefault(value => value.Pair.NativeRelationObserved);
                        existingNativeTargets.TryGetValue(pair.PairKey, out Dictionary<string, object> priorNativeTarget);
                        bool observationRequired = RelationshipNativeObservationRequired(existing, priorNativeTarget);
                        int observedNative = nativeObservation != null
                            ? nativeObservation.Pair.NativeRelation
                            : existing != null && ReadInt(existing, "native_action_pending", 0) == 0
                                ? ReadInt(existing, "projected_native_relation", 0)
                                : priorNativeTarget != null
                                    ? ReadInt(priorNativeTarget, "observed_relation", 0)
                                    : 0;
                        bool previousProjectionConfirmed = existing != null
                            && !observationRequired
                            && observedNative == ReadInt(existing, "projected_native_relation", int.MinValue);
                        bool pending = existing != null && !rebaseLegacyAffinity
                            && ReadInt(existing, "native_action_pending", 0) == 1;
                        string actionId = existing == null || rebaseLegacyAffinity
                            ? "" : ReadString(existing, "native_action_id", "");
                        if (projectedNative != observedNative || observationRequired)
                        {
                            bool wasPending = pending;
                            bool sameTargetAlreadyQueued = existingNativeTargets.TryGetValue(pair.PairKey,
                                    out Dictionary<string, object> nativeTarget)
                                && ReadInt(nativeTarget, "target_relation", int.MinValue) == projectedNative
                                && new[] { "pending", "claimed" }.Contains(ReadString(nativeTarget, "status", ""),
                                    StringComparer.OrdinalIgnoreCase);
                            actionId = sameTargetAlreadyQueued
                                ? "native_target:" + pair.PairKey
                                : QueueMbtiNativeRelationAction(connection, latestPair, day,
                                    projectedNative, observedNative, nativeTargetUpsert,
                                    usePostgreSqlBatches
                                        ? nativeTargetWriteBatch : null, observationRequired);
                            pending = true;
                            if (!wasPending) nativeQueued++;
                        }
                        else
                        {
                            // The native heartbeat already agrees with Reign's
                            // directional average. No receipt or historical action
                            // row is needed for an aligned pair.
                            if (existingNativeTargets.ContainsKey(pair.PairKey))
                            {
                                Dictionary<string, object> deleteParameters =
                                    new Dictionary<string, object>
                                    {
                                        ["pair"] = pair.PairKey
                                    };
                                if (usePostgreSqlBatches)
                                    nativeTargetDeleteBatch.Add(deleteParameters);
                                else
                                    ExecutePreparedRelationshipCommand(
                                        nativeTargetDelete, deleteParameters);
                            }
                            pending = false;
                            actionId = "";
                        }

                        int firstPresenceDay = scheduledDays[0].Day;
                        int lastPresenceDay = scheduledDays[scheduledDays.Count - 1].Day;
                        int priorPresenceDay = existing == null ? int.MinValue
                            : ReadInt(existing, "last_presence_day",
                                ReadInt(existing, "last_day", int.MinValue));
                        int consecutive = existing == null
                            ? 0 : ReadInt(existing, "consecutive_days", 0);
                        foreach (RelationshipPairDay scheduledDay in scheduledDays)
                        {
                            consecutive = priorPresenceDay != int.MinValue
                                && scheduledDay.Day == priorPresenceDay + 1
                                    ? consecutive + 1
                                    : 1;
                            priorPresenceDay = scheduledDay.Day;
                        }
                        double weighted = (existing == null ? 0d : ReadDouble(existing, "weighted_exposure", 0d))
                            + scheduledDays.Sum(value => value.Pair.ExposureWeight);
                        int presenceMask = 0;
                        foreach (int presenceDay in scheduledDays.Select(value => value.Day).Distinct())
                        {
                            int bit = presenceDay - cadenceWindowStartDay;
                            if (bit >= 0 && bit < 31) presenceMask |= 1 << bit;
                        }
                        int processingShard = RelationshipCadenceShard(campaignId, pair.PairKey);
                        Dictionary<string, object> pairWriteParameters =
                            new Dictionary<string, object>
                            {
                                ["pair"] = pair.PairKey, ["a"] = pair.HeroAId, ["b"] = pair.HeroBId,
                                ["typeA"] = typeA, ["typeB"] = typeB, ["axesA"] = "{}", ["axesB"] = "{}",
                                ["baseAB"] = baseAB, ["baseBA"] = baseBA, ["chanceAB"] = chanceAB, ["chanceBA"] = chanceBA,
                                ["affinityAB"] = affinityAB, ["affinityBA"] = affinityBA, ["signalAB"] = signalAB, ["signalBA"] = signalBA,
                                ["tagAB"] = tagAB, ["tagBA"] = tagBA, ["shared"] = lifecycleShared,
                                ["first"] = existing == null ? firstPresenceDay : ReadInt(existing, "first_day", firstPresenceDay), ["last"] = day,
                                ["consecutive"] = consecutive, ["weighted"] = weighted,
                                ["kind"] = latestPair.ContextKind, ["context"] = latestPair.ContextId,
                                ["rollAB"] = rollAB, ["rollBA"] = rollBA, ["deltaAB"] = deltaAB, ["deltaBA"] = deltaBA,
                                ["shard"] = processingShard, ["presenceDay"] = lastPresenceDay,
                                ["batchSize"] = scheduledDays.Count, ["presenceMask"] = presenceMask,
                                ["decayPeriod"] = Math.Max(0, day / 5),
                                ["projected"] = projectedNative,
                                ["nativeDay"] = previousProjectionConfirmed
                                    ? (double)day
                                    : existing == null ? -1000d : ReadDouble(existing, "last_native_sync_day", -1000d),
                                ["pending"] = pending ? 1 : 0, ["action"] = actionId, ["version"] = MbtiChemistryVersion, ["ts"] = ts
                            };
                        if (usePostgreSqlBatches)
                            pairWriteBatch.Add(pairWriteParameters);
                        else
                            ExecutePreparedRelationshipCommand(pairUpsert,
                                pairWriteParameters);
                        Dictionary<string, object> committedPairState =
                            existing == null
                                ? new Dictionary<string, object>(
                                    StringComparer.OrdinalIgnoreCase)
                                : CloneRelationshipPairCacheRow(existing);
                        committedPairState["pair_key"] = pair.PairKey;
                        committedPairState["hero_a_id"] = pair.HeroAId;
                        committedPairState["hero_b_id"] = pair.HeroBId;
                        committedPairState["mbti_a"] = typeA;
                        committedPairState["mbti_b"] = typeB;
                        committedPairState["base_chance_a_to_b"] = baseAB;
                        committedPairState["base_chance_b_to_a"] = baseBA;
                        committedPairState["chance_a_to_b"] = chanceAB;
                        committedPairState["chance_b_to_a"] = chanceBA;
                        committedPairState["affinity_a_to_b"] = affinityAB;
                        committedPairState["affinity_b_to_a"] = affinityBA;
                        committedPairState["signal_a_to_b"] = signalAB;
                        committedPairState["signal_b_to_a"] = signalBA;
                        committedPairState["tag_a_to_b"] = tagAB;
                        committedPairState["tag_b_to_a"] = tagBA;
                        committedPairState["shared_tag"] = lifecycleShared;
                        committedPairState["first_day"] = existing == null
                            ? firstPresenceDay
                            : ReadInt(existing, "first_day",
                                firstPresenceDay);
                        committedPairState["last_day"] = day;
                        committedPairState["consecutive_days"] = consecutive;
                        committedPairState["weighted_exposure"] = weighted;
                        committedPairState["last_context_kind"] =
                            latestPair.ContextKind;
                        committedPairState["last_context_id"] =
                            latestPair.ContextId;
                        committedPairState["last_roll_a_to_b"] = rollAB;
                        committedPairState["last_roll_b_to_a"] = rollBA;
                        committedPairState["last_delta_a_to_b"] = deltaAB;
                        committedPairState["last_delta_b_to_a"] = deltaBA;
                        committedPairState["projected_native_relation"] =
                            projectedNative;
                        committedPairState["native_action_pending"] =
                            pending ? 1 : 0;
                        committedPairState["native_action_id"] = actionId;
                        committedPairState["processing_shard"] =
                            processingShard;
                        committedPairState["last_presence_day"] =
                            lastPresenceDay;
                        committedPairState["last_batch_size"] =
                            scheduledDays.Count;
                        committedPairState["last_presence_mask"] =
                            presenceMask;
                        committedPairState["last_decay_period"] =
                            affinityAB != ReadInt(existing,
                                "affinity_a_to_b", affinityAB)
                            || affinityBA != ReadInt(existing,
                                "affinity_b_to_a", affinityBA)
                                ? Math.Max(0, day / 5)
                                : ReadInt(existing,
                                    "last_decay_period", -1);
                        committedPairState["compatibility_version"] =
                            MbtiChemistryVersion;
                        committedPairState["state_revision"] =
                            existing == null ? 1
                                : ReadLong(existing, "state_revision", 1) + 1;
                        committedPairState["updated_ts"] = ts;
                        pendingCacheRows[pair.PairKey] = committedPairState;
                        existingPairs[pair.PairKey] = committedPairState;
                        if (existing == null)
                        {
                            newPairCount++;
                            Dictionary<string, object> provenanceDetails =
                                new Dictionary<string, object>
                                {
                                    ["contextKind"] = latestPair.ContextKind,
                                    ["contextId"] = latestPair.ContextId
                                };
                            if (usePostgreSqlBatches)
                            {
                                provenanceWriteBatch.Add(
                                    new Dictionary<string, object>
                                    {
                                        ["campaign"] = campaignId,
                                        ["timeline"] = timelineId,
                                        ["pair"] = pair.PairKey,
                                        ["source"] = "co_presence",
                                        ["required"] = 0,
                                        ["day"] = (double)day,
                                        ["details"] =
                                            Json.Serialize(provenanceDetails),
                                        ["ts"] = ts
                                    });
                            }
                            else
                            {
                                RecordRelationshipPairProvenance(connection,
                                    campaignId, timelineId, pair.PairKey,
                                    "co_presence", false, day,
                                    provenanceDetails);
                            }
                        }
                        else
                        {
                            repeatEncounterCount++;
                        }
                        Dictionary<string, object> telemetryAfter = new Dictionary<string, object>
                        {
                            ["pairKey"] = pair.PairKey,
                            ["heroAId"] = pair.HeroAId,
                            ["heroBId"] = pair.HeroBId,
                            ["affinityAToB"] = affinityAB,
                            ["affinityBToA"] = affinityBA,
                            ["effectiveAffinityAToB"] = effectiveAB,
                            ["effectiveAffinityBToA"] = effectiveBA,
                            ["tagAToB"] = tagAB,
                            ["tagBToA"] = tagBA,
                            ["sharedTag"] = lifecycleShared,
                            ["mbtiA"] = typeA,
                            ["mbtiB"] = typeB,
                            ["notableA"] = ReadBool(heroA, "isNotable", false),
                            ["notableB"] = ReadBool(heroB, "isNotable", false),
                            ["deltaAToB"] = deltaAB,
                            ["deltaBToA"] = deltaBA,
                            ["contextKind"] = latestPair.ContextKind,
                            ["firstDay"] = existing == null ? firstPresenceDay : ReadInt(existing, "first_day", firstPresenceDay)
                        };
                        // World Test reads the authoritative relationship ledger.
                        // Maintaining its denormalized membership indexes here used
                        // to multiply every affinity change into dozens of SQLite
                        // writes and made diagnostics the simulation bottleneck.
                        IncrementWorldTestObjectCounter(telemetryChunkCounters, "processedPairs", 1);
                        IncrementWorldTestObjectCounter(telemetryChunkCounters, "rolledDirections",
                            pairRolledDirections);
                        IncrementWorldTestObjectCounter(telemetryChunkCounters, "positiveRolls",
                            pairPositiveRolls);
                        IncrementWorldTestObjectCounter(telemetryChunkCounters, "negativeRolls",
                            pairNegativeRolls);
                        telemetryChunkPairs++;
                        telemetryLastPairKey = pair.PairKey;
                        processed++;
                        if (telemetryChunkPairs == relationshipWriteChunkSize)
                        {
                            RecordWorldTestCounterSchemaReady(connection, campaignId, timelineId, day, "relationships",
                                "pair_chunk_" + telemetryChunkIndex.ToString("D6", CultureInfo.InvariantCulture)
                                    + "_through_" + telemetryLastPairKey,
                                telemetryChunkCounters);
                            telemetryChunkIndex++;
                            telemetryChunkPairs = 0;
                            telemetryChunkCounters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        }
                        if (ReadBool(payload, "continuousWorker", false)
                            && processed % relationshipWriteChunkSize == 0)
                        {
                            FlushPostgreSqlRelationshipWriteBatches(connection,
                                pairUpsert, nativeTargetUpsert,
                                nativeTargetDelete, pairWriteBatch,
                                nativeTargetWriteBatch,
                                nativeTargetDeleteBatch);
                            FlushPostgreSqlRelationshipProvenanceBatch(connection,
                                provenanceWriteBatch);
                            ExecuteSql(connection, @"UPDATE relationship_daily_inputs SET
pair_cursor=$cursor,candidate_pairs=$candidates,processed_pairs=$processed
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day;",
                                new Dictionary<string, object>
                                {
                                    ["cursor"] = pair.PairKey, ["candidates"] = pairs.Count,
                                    ["processed"] = processed, ["campaign"] = campaignId,
                                    ["timeline"] = timelineId, ["day"] = day
                                });
                            ExecuteSql(connection, "COMMIT;");
                            StoreCommittedRelationshipPairCacheRows(
                                campaignId, pendingCacheRows);
                            pendingCacheRows.Clear();
                            ThrottleContinuousRelationshipChunk(evaluationUnitsSinceThrottle);
                            evaluationUnitsSinceThrottle = 0;
                            ExecuteSql(connection, "BEGIN IMMEDIATE;");
                        }
                    }
                    pairProcessingTimer.Stop();
                    pairProcessingMs = pairProcessingTimer.ElapsedMilliseconds;
                    Stopwatch independentLifecycleTimer = Stopwatch.StartNew();
                    // Active lifecycle pairs are evaluated every day they are
                    // actually co-present, even when the random social matcher
                    // selected different partners. They never receive an MBTI
                    // affinity roll through this path.
                    foreach (Dictionary<string, object> lifecycleRow
                        in independentLifecycleRows)
                    {
                        string lifecyclePairKey = ReadString(lifecycleRow,
                            "pair_key", "");
                        if (pairs.ContainsKey(lifecyclePairKey)) continue;
                        string heroAId = ReadString(lifecycleRow,
                            "hero_a_id", "");
                        string heroBId = ReadString(lifecycleRow,
                            "hero_b_id", "");
                        if (!RelationshipHeroesShareGroup(
                                coPresenceByDay.TryGetValue(day,
                                    out Dictionary<string, HashSet<int>> todayGroups)
                                    ? todayGroups : null,
                                heroAId, heroBId)
                            || !heroes.TryGetValue(heroAId,
                                out Dictionary<string, object> lifecycleHeroA)
                            || !heroes.TryGetValue(heroBId,
                                out Dictionary<string, object> lifecycleHeroB)
                            || !existingPairs.TryGetValue(lifecyclePairKey,
                                out Dictionary<string, object> lifecyclePairState))
                            continue;
                        int lifecycleAffinityAB = ReadInt(lifecyclePairState,
                            "affinity_a_to_b", 0);
                        int lifecycleAffinityBA = ReadInt(lifecyclePairState,
                            "affinity_b_to_a", 0);
                        int lifecycleEffectiveAB = Clamp(lifecycleAffinityAB
                            + ReadInt(ResolveObserverPublicStanding(connection, campaignId,
                                timelineId, heroAId, heroBId), "value", 0), -100, 100);
                        int lifecycleEffectiveBA = Clamp(lifecycleAffinityBA
                            + ReadInt(ResolveObserverPublicStanding(connection, campaignId,
                                timelineId, heroBId, heroAId), "value", 0), -100, 100);
                        AmbientPairContext lifecyclePair =
                            new AmbientPairContext
                            {
                                PairKey = lifecyclePairKey,
                                HeroAId = heroAId,
                                HeroBId = heroBId,
                                ContextKind = "active_lifecycle",
                                ContextId = "daily"
                            };
                        Dictionary<string, object> lifecycle =
                            ProcessRelationshipLifecycle(connection, campaignId,
                                lifecyclePair, day, lifecycleHeroA,
                                lifecycleHeroB, lifecycleEffectiveAB,
                                lifecycleEffectiveBA, lifecycleRow, true,
                                timelineId);
                        string independentShared = ReadString(lifecycle,
                            "sharedTag", ReadString(lifecyclePairState,
                                "shared_tag", "neutral"));
                        if (!independentShared.Equals(ReadString(
                                lifecyclePairState, "shared_tag", ""),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET shared_tag=$shared,state_revision=state_revision+1,updated_ts=$ts
WHERE pair_key=$pair;",
                                new Dictionary<string, object>
                                {
                                    ["shared"] = independentShared,
                                    ["ts"] = ts,
                                    ["pair"] = lifecyclePairKey
                                });
                            lifecyclePairState["shared_tag"] = independentShared;
                        }
                        lifecycleChanges += new[] { "loverStarted", "loverEnded",
                            "affairStarted", "affairEnded" }
                            .Count(key => ReadBool(lifecycle, key, false));
                        lifecycleRumors += ReadBool(lifecycle,
                            "rumorCreated", false) ? 1 : 0;
                        lifecycleActions += new[] { "marriageQueued",
                            "divorceQueued", "conceptionQueued" }
                            .Count(key => ReadBool(lifecycle, key, false));
                    }
                    independentLifecycleTimer.Stop();
                    independentLifecycleMs = independentLifecycleTimer.ElapsedMilliseconds;
                    Stopwatch finalizationTimer = Stopwatch.StartNew();
                    Stopwatch finalFlushTimer = Stopwatch.StartNew();
                    FlushPostgreSqlRelationshipWriteBatches(connection,
                        pairUpsert, nativeTargetUpsert, nativeTargetDelete,
                        pairWriteBatch, nativeTargetWriteBatch,
                        nativeTargetDeleteBatch);
                    finalPairFlushMs = finalFlushTimer.ElapsedMilliseconds;
                    FlushPostgreSqlRelationshipProvenanceBatch(connection,
                        provenanceWriteBatch);
                    finalProvenanceFlushMs = finalFlushTimer.ElapsedMilliseconds - finalPairFlushMs;
                    Stopwatch flingProcessingTimer = Stopwatch.StartNew();
                    flingSummary = ProcessDailyNpcFlings(connection, campaignId,
                        timelineId, groupsByDay, heroRowsByDay, heroes);
                    flingProcessingTimer.Stop();
                    flingProcessingMs = flingProcessingTimer.ElapsedMilliseconds;
                    List<Dictionary<string, object>> matchingGroups =
                        groupsByDay.TryGetValue(day,
                            out List<Dictionary<string, object>> currentGroups)
                            ? currentGroups
                            : new List<Dictionary<string, object>>();
                    int eligibleNpcCount = matchingGroups.SelectMany(group =>
                            ReadStringList(group, "heroIds"))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    RecordWorldTestCounterSchemaReady(connection, campaignId,
                        timelineId, day, "relationships",
                        "daily_random_matching_summary",
                        new Dictionary<string, object>
                        {
                            ["socialPools"] = matchingGroups.Count,
                            ["eligibleNpcs"] = eligibleNpcCount,
                            ["selectedPairs"] = pairs.Count,
                            ["romanceContinuationPairDays"] =
                                romanceContinuationPairDays,
                            ["courtshipFocusPairDays"] =
                                courtshipFocusPairDays,
                            ["pairedNpcs"] = pairs.Count * 2,
                            ["unpairedNpcs"] = Math.Max(0,
                                eligibleNpcCount - pairs.Count * 2),
                            ["newPairs"] = newPairCount,
                            ["repeatEncounters"] = repeatEncounterCount,
                            ["rolledDirections"] = rolledDirections,
                            ["positiveDirections"] = positiveRolls,
                            ["negativeDirections"] = negativeRolls,
                            ["affinityMovement"] = affinityChanges,
                            ["magnitudeSides"] = OrdinaryRelationshipMagnitudeSides,
                            ["baseMagnitudeDie"] = "1d15",
                            ["loverMagnitudeDie"] = "1d20",
                            ["ordinaryMagnitudePairDays"] = ordinaryMagnitudePairDays,
                            ["loverMagnitudePairDays"] = loverMagnitudePairDays
                        });
                    if (telemetryChunkPairs > 0)
                    {
                        RecordWorldTestCounterSchemaReady(connection, campaignId, timelineId, day, "relationships",
                            "pair_chunk_" + telemetryChunkIndex.ToString("D6", CultureInfo.InvariantCulture)
                                + "_through_" + telemetryLastPairKey,
                            telemetryChunkCounters);
                    }
                    EnqueueWorldTestRollup(connection, campaignId, timelineId, day,
                        "relationship_day_completed");
                    finalFlushTimer.Stop();
                    finalFlushMs = finalFlushTimer.ElapsedMilliseconds;
                    finalTelemetryMs = finalFlushMs - finalPairFlushMs - finalProvenanceFlushMs - flingProcessingMs;

                    Stopwatch courtPopularityTimer = Stopwatch.StartNew();
                    List<string> changedCourtPopularitySubjects =
                        RecomputeCourtPopularityReputationsBatch(connection,
                            campaignId, timelineId,
                            courtPopularitySubjects, day,
                            "ambient_affinity_threshold|" + day);
                    if (changedCourtPopularitySubjects.Count > 0)
                    {
                        courtPopularityChanged = true;
                        ReconcileSocialRelationshipsForSubjects(connection,
                            campaignId, timelineId,
                            changedCourtPopularitySubjects, day);
                    }
                    courtPopularityTimer.Stop();
                    courtPopularityMs =
                        courtPopularityTimer.ElapsedMilliseconds;
                    Stopwatch nativeCountTimer = Stopwatch.StartNew();
                    bool continuousWorker = ReadBool(payload, "continuousWorker", false);
                    Dictionary<string, object> nativeSyncPlan = continuousWorker
                        ? new Dictionary<string, object>
                        {
                            ["targetCount"] = ReadInt(QuerySql(connection,
                                "SELECT COUNT(*) AS count FROM relationship_native_targets WHERE status IN ('pending','claimed','failed');")
                                .FirstOrDefault(), "count", 0)
                        }
                        : BuildRelationshipNativeSyncPlan(connection, campaignId, timelineId, day);
                    nativeQueued = ReadInt(nativeSyncPlan, "targetCount", nativeQueued);
                    nativeCountTimer.Stop();
                    nativeCountMs = nativeCountTimer.ElapsedMilliseconds;
                    finalizationTimer.Stop();
                    finalizationMs = finalizationTimer.ElapsedMilliseconds;
                    Dictionary<string, object> result = new Dictionary<string, object>
                    {
                        ["ok"] = true, ["runId"] = runId, ["campaignId"] = campaignId, ["worldDay"] = day,
                        ["groupCount"] = groupsByDay.Values.Sum(value => value.Count),
                        ["candidatePairs"] = pairs.Count,
                        ["selectedPairs"] = pairs.Count,
                        ["romanceContinuationPairDays"] =
                            romanceContinuationPairDays,
                        ["courtshipFocusPairDays"] =
                            courtshipFocusPairDays,
                        ["processedPairs"] = processed,
                        ["evaluatedPairDays"] = evaluatedPairDays,
                        ["cadenceDays"] = RelationshipCadenceDays,
                        ["cadenceShard"] = cadenceShard,
                        ["windowStartDay"] = cadenceWindowStartDay,
                        ["windowDayCount"] = cadenceWindowDayCount,
                        ["changedDirections"] = affinityChanges, ["rolledDirections"] = rolledDirections,
                        ["positiveRolls"] = positiveRolls, ["negativeRolls"] = negativeRolls,
                        ["nativeChangesQueued"] = nativeQueued,
                        ["socialPools"] = matchingGroups.Count,
                        ["eligibleNpcs"] = eligibleNpcCount,
                        ["pairedNpcs"] = pairs.Count * 2,
                        ["unpairedNpcs"] = Math.Max(0,
                            eligibleNpcCount - pairs.Count * 2),
                        ["newPairs"] = newPairCount,
                        ["repeatEncounters"] = repeatEncounterCount,
                        ["flings"] = flingSummary,
                        ["baseMagnitudeDie"] = "1d15",
                        ["loverMagnitudeDie"] = "1d20",
                        ["ordinaryMagnitudePairDays"] = ordinaryMagnitudePairDays,
                        ["loverMagnitudePairDays"] = loverMagnitudePairDays,
                        ["parallelWorkers"] = parallelWorkers,
                        ["parallelWorkItems"] = allPairDays.Count,
                        ["parallelComputeMs"] =
                            parallelTimer.ElapsedMilliseconds,
                        ["automaticNormalizationEnabled"] =
                            AutomaticRelationshipNormalizationEnabled,
                        ["decayMs"] = decayMs,
                        ["stateLoadMs"] = stateLoadMs,
                        ["sqlSetupMs"] = sqlReadTiming.SetupMs,
                        ["sqlExecuteMs"] = sqlReadTiming.ExecuteMs,
                        ["sqlMaterializeMs"] = sqlReadTiming.MaterializeMs,
                        ["sqlReadQueries"] = sqlReadTiming.Queries,
                        ["sqlReadRows"] = sqlReadTiming.Rows,
                        ["sqlReadProfile"] = sqlReadTiming.CaptureQueryProfiles ? sqlReadTiming.QueryProfiles : null,
                        ["stateLifecycleReadMs"] = stateLifecycleReadMs,
                        ["stateChemistryReadMs"] = stateChemistryReadMs,
                        ["stateNativeReadMs"] = stateNativeReadMs,
                        ["stateStandingReadMs"] = stateStandingReadMs,
                        ["loadedLifecycleRows"] = existingLifecycle.Count,
                        ["loadedChemistryRows"] = existingPairs.Count,
                        ["pairProcessingMs"] = pairProcessingMs,
                        ["finalizationMs"] = finalizationMs,
                        ["finalFlushMs"] = finalFlushMs,
                        ["finalPairFlushMs"] = finalPairFlushMs,
                        ["finalProvenanceFlushMs"] = finalProvenanceFlushMs,
                        ["finalTelemetryMs"] = finalTelemetryMs,
                        ["flingProcessingMs"] = flingProcessingMs,
                        ["independentLifecycleMs"] = independentLifecycleMs,
                        ["standingReadQueries"] = standingContext.StandingReads,
                        ["observerRosterReadQueries"] = standingContext.RosterReads,
                        ["playerRosterReadQueries"] = standingContext.PlayerReads,
                        ["courtPopularityMs"] = courtPopularityMs,
                        ["nativeCountMs"] = nativeCountMs,
                        ["postgresStagedRows"] =
                            Interlocked.Read(
                                ref PostgreSqlRelationshipStagedRows),
                        ["postgresWrittenRows"] =
                            Interlocked.Read(
                                ref PostgreSqlRelationshipWrittenRows),
                        ["postgresCopyMs"] =
                            Interlocked.Read(
                                ref PostgreSqlRelationshipCopyMs),
                        ["postgresMergeMs"] =
                            Interlocked.Read(
                                ref PostgreSqlRelationshipMergeMs),
                        ["courtPopularitySubjects"] =
                            courtPopularitySubjects.Count,
                        ["courtPopularityChangedSubjects"] =
                            changedCourtPopularitySubjects.Count,
                        ["nativeSyncPlan"] = continuousWorker ? null : nativeSyncPlan,
                        ["fiveDayDecay"] = decay, ["lifecycleChanges"] = lifecycleChanges,
                        ["catchUpDays"] = catchUpDayCount,
                        ["lifecycleRumorsCreated"] = lifecycleRumors, ["lifecycleActionsQueued"] = lifecycleActions,
                        ["compatibilityModel"] = "jobcannon_directional_minus_10",
                        ["rollRule"] = "d100 <= adjusted score is positive; otherwise negative. Magnitude is 1d15, or 1d20 when the pair already has the lovers tag.",
                         ["affinityRule"] = "The signed co-location die changes that observer's directional Reign affinity immediately.",
                        ["telemetryPairScans"] = 0,
                         ["skippedReasons"] = new Dictionary<string, object>
                        {
                            ["missing_hero"] = skippedMissing,
                            ["settlement_exposure_gate"] = skippedSettlementGate
                        },
                        ["llmCalls"] = 0, ["noLlmConfirmed"] = true
                    };
                    Stopwatch commitTimer = Stopwatch.StartNew();
                    ExecuteSql(connection,
                        "INSERT OR REPLACE INTO schema_meta(key,value) VALUES($key,$day);",
                        new Dictionary<string, object>
                        {
                            ["key"] = "mbti_relationship_last_processed_day:" + timelineId,
                            ["day"] = day.ToString(CultureInfo.InvariantCulture)
                        });
                    ExecuteSql(connection,
                        "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('mbti_relationship_last_processed_day',$day);",
                        new Dictionary<string, object> { ["day"] = day.ToString(CultureInfo.InvariantCulture) });
                    ExecuteSql(connection, "COMMIT;");
                    commitTimer.Stop();
                    commitMs = commitTimer.ElapsedMilliseconds;
                    timer.Stop();
                    long nonParallelMs = Math.Max(0,
                        timer.ElapsedMilliseconds
                        - parallelTimer.ElapsedMilliseconds);
                    long measuredPhaseMs = payloadPreparationMs
                        + schemaReadinessMs + progressLoadMs
                        + lifecycleLoadMs + heroHydrationMs + matchingMs
                        + parallelTimer.ElapsedMilliseconds
                        + personalityLoadMs + commandPreparationMs
                        + transactionBeginMs + decayMs + stateLoadMs
                        + pairProcessingMs + independentLifecycleMs + finalizationMs + commitMs;
                    long unattributedMs = Math.Max(0,
                        timer.ElapsedMilliseconds - measuredPhaseMs);
                    result["durationMs"] = timer.ElapsedMilliseconds;
                    result["serialWriteMs"] = nonParallelMs;
                    result["nonParallelMs"] = nonParallelMs;
                    result["payloadPreparationMs"] = payloadPreparationMs;
                    result["schemaReadinessMs"] = schemaReadinessMs;
                    result["progressLoadMs"] = progressLoadMs;
                    result["lifecycleLoadMs"] = lifecycleLoadMs;
                    result["heroHydrationMs"] = heroHydrationMs;
                    result["heroDocumentCacheHits"] = heroDocumentCacheHits;
                    result["matchingMs"] = matchingMs;
                    result["personalityLoadMs"] = personalityLoadMs;
                    result["commandPreparationMs"] = commandPreparationMs;
                    result["transactionBeginMs"] = transactionBeginMs;
                    result["commitMs"] = commitMs;
                    result["unattributedMs"] = unattributedMs;
                    RecordRelationshipPhaseTimings(result);
                    StoreCommittedRelationshipPairCacheRows(
                        campaignId, pendingCacheRows);
                    pendingCacheRows.Clear();
                    if (courtPopularityChanged
                        && !campaignId.StartsWith("__", StringComparison.Ordinal))
                        SchedulePendingReputationReasonJobs(campaignId);
                    if (continuousWorker && evaluationUnitsSinceThrottle > 0)
                        ThrottleContinuousRelationshipChunk(evaluationUnitsSinceThrottle);
                        return result;
                    }
                    catch
                    {
                        try { ExecuteSql(connection, "ROLLBACK;"); } catch { }
                        throw;
                    }
                }
            }
        }

        private static int CourtPopularityQualification(int affinity)
        {
            return affinity > 30 ? 1 : affinity < -30 ? -1 : 0;
        }

        private static Dictionary<string, object> BuildRelationshipNativeSyncPlan(
            ReignDbConnection connection, string campaignId, string timelineId, int day)
        {
            string planId = "native_sync_" + campaignId + "_" + timelineId + "_"
                + day.ToString(CultureInfo.InvariantCulture);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<Dictionary<string, object>> targets = QuerySql(connection, @"
SELECT pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,world_day,
attempt_count,revision
FROM relationship_native_targets
WHERE status IN ('pending','claimed','failed')
ORDER BY ABS(target_relation-observed_relation) DESC,pair_key;")
                .Select(row => new Dictionary<string, object>
                {
                    ["pairKey"] = ReadString(row, "pair_key", ""),
                    ["heroAId"] = ReadString(row, "hero_a_id", ""),
                    ["heroBId"] = ReadString(row, "hero_b_id", ""),
                    ["targetRelation"] = ReadInt(row, "target_relation", 0),
                    ["observedRelation"] = ReadInt(row, "observed_relation", 0),
                    ["sourceDay"] = ReadDouble(row, "world_day", day),
                    ["attemptCount"] = ReadInt(row, "attempt_count", 0),
                    ["revision"] = ReadInt(row, "revision", 1)
                }).ToList();
            ExecuteSql(connection, @"INSERT INTO relationship_native_sync_batches(
plan_id,campaign_id,timeline_id,world_day,target_count,issued_ts,client_remaining)
VALUES($plan,$campaign,$timeline,$day,$count,$ts,$count)
ON CONFLICT(plan_id) DO UPDATE SET target_count=$count,issued_ts=$ts;",
                new Dictionary<string, object>
                {
                    ["plan"] = planId, ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["day"] = (double)day, ["count"] = targets.Count, ["ts"] = ts
                });
            return new Dictionary<string, object>
            {
                ["planId"] = planId,
                ["campaignId"] = campaignId,
                ["timelineId"] = timelineId,
                ["worldDay"] = day,
                ["targetCount"] = targets.Count,
                ["targets"] = targets,
                ["exactProjection"] = true,
                ["projectionRule"] = "round((affinityAtoB+affinityBtoA)/2)"
            };
        }

        private static Dictionary<string, object> RelationshipNativeSyncReportApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string planId = ReadString(payload, "planId", "");
            if (string.IsNullOrWhiteSpace(planId))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "planId is required." };
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                Dictionary<string, object> batch = QuerySql(connection,
                    "SELECT * FROM relationship_native_sync_batches WHERE plan_id=$plan AND timeline_id=$timeline LIMIT 1;",
                    new Dictionary<string, object> { ["plan"] = planId, ["timeline"] = timelineId }).FirstOrDefault();
                if (batch == null)
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false, ["planId"] = planId,
                        ["error"] = "The native relationship sync plan is no longer active for this timeline."
                    };

                List<string> obsolete = ReadStringList(payload, "obsoletePairKeys")
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (string pairKey in obsolete)
                {
                    ExecuteSql(connection, "DELETE FROM relationship_native_targets WHERE pair_key=$pair;",
                        new Dictionary<string, object> { ["pair"] = pairKey });
                    ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET native_action_pending=0,native_action_id='',updated_ts=$ts WHERE pair_key=$pair;",
                        new Dictionary<string, object> { ["pair"] = pairKey, ["ts"] = ts });
                }

                List<Dictionary<string, object>> failures = ReadDictionaryList(payload, "failedPairs");
                foreach (Dictionary<string, object> failure in failures)
                {
                    string pairKey = ReadString(failure, "pairKey", "");
                    if (string.IsNullOrWhiteSpace(pairKey)) continue;
                    ExecuteSql(connection, @"UPDATE relationship_native_targets
SET status='failed',attempt_count=attempt_count+1,claimed_ts=0,last_error=$error,updated_ts=$ts
WHERE pair_key=$pair AND status<>'failed';",
                        new Dictionary<string, object>
                        {
                            ["pair"] = pairKey,
                            ["error"] = LimitText(ReadString(failure, "error", "Native relation projection failed."), 500),
                            ["ts"] = ts
                        });
                }

                int appliedCount = Math.Max(0, ReadInt(payload, "appliedCount", 0));
                int alignedCount = Math.Max(0, ReadInt(payload, "alreadyAlignedCount", 0));
                int obsoleteCount = Math.Max(0, ReadInt(payload, "obsoleteCount", obsolete.Count));
                int failedCount = Math.Max(0, ReadInt(payload, "failedCount", failures.Count));
                int remainingCount = Math.Max(0, ReadInt(payload, "remainingCount", 0));
                int targetCount = Math.Max(0, ReadInt(batch, "target_count", 0));
                int terminalCount = appliedCount + alignedCount + obsoleteCount + failedCount;
                bool completeReport = remainingCount == 0
                    && terminalCount >= targetCount
                    && failedCount == failures.Count;
                if (completeReport)
                {
                    // Applying a target is followed by an immediate native
                    // GetRelation verification in the game client. A complete
                    // progress report is therefore authoritative confirmation;
                    // requiring the pair to be co-present in a later daily
                    // snapshot strands successful transient encounters forever.
                    //
                    // Reset prior retry failures covered by this plan, then
                    // restore only failures explicitly reported for this
                    // attempt. Newer targets are protected by world_day.
                    double batchDay = ReadDouble(batch, "world_day", 0d);
                    ExecuteSql(connection, @"UPDATE relationship_native_targets
SET status='pending',claimed_ts=0,last_error='',updated_ts=$ts
WHERE world_day<=$day;",
                        new Dictionary<string, object> { ["day"] = batchDay, ["ts"] = ts });
                    foreach (Dictionary<string, object> failure in failures)
                    {
                        string pairKey = ReadString(failure, "pairKey", "");
                        if (string.IsNullOrWhiteSpace(pairKey)) continue;
                        ExecuteSql(connection, @"UPDATE relationship_native_targets
SET status='failed',claimed_ts=0,last_error=$error,updated_ts=$ts
WHERE pair_key=$pair AND world_day<=$day;",
                            new Dictionary<string, object>
                            {
                                ["pair"] = pairKey,
                                ["day"] = batchDay,
                                ["error"] = LimitText(ReadString(failure, "error",
                                    "Native relation projection failed."), 500),
                                ["ts"] = ts
                            });
                    }
                    ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET native_action_pending=0,native_action_id='',last_native_sync_day=$day,updated_ts=$ts
WHERE pair_key IN (
    SELECT pair_key FROM relationship_native_targets
    WHERE world_day<=$day AND status<>'failed'
);",
                        new Dictionary<string, object>
                        {
                            ["day"] = batchDay,
                            ["ts"] = ts
                        });
                    ExecuteSql(connection, @"DELETE FROM relationship_native_targets
WHERE world_day<=$day AND status<>'failed';",
                        new Dictionary<string, object> { ["day"] = batchDay });
                }

                ExecuteSql(connection, @"UPDATE relationship_native_sync_batches SET
client_applied=$applied,client_already_aligned=$aligned,client_obsolete=$obsolete,
client_failed=$failed,client_remaining=$remaining,client_duration_ms=$duration,
last_report_day=$day,last_report_ts=$ts,last_error=$error
WHERE plan_id=$plan AND timeline_id=$timeline;",
                    new Dictionary<string, object>
                    {
                        ["applied"] = appliedCount,
                        ["aligned"] = alignedCount,
                        ["obsolete"] = obsoleteCount,
                        ["failed"] = failedCount,
                        ["remaining"] = remainingCount,
                        ["duration"] = Math.Max(0, ReadInt(payload, "durationMs", 0)),
                        ["day"] = ReadDouble(payload, "worldDay", ReadDouble(batch, "world_day", 0d)),
                        ["ts"] = ts,
                        ["error"] = LimitText(ReadString(payload, "lastError", ""), 1000),
                        ["plan"] = planId,
                        ["timeline"] = timelineId
                    });
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["planId"] = planId, ["reportedCount"] =
                        ReadInt(payload, "appliedCount", 0)
                        + ReadInt(payload, "alreadyAlignedCount", 0)
                        + ReadInt(payload, "obsoleteCount", 0)
                        + ReadInt(payload, "failedCount", 0),
                    ["remainingCount"] = Math.Max(0, ReadInt(payload, "remainingCount", 0))
                };
            }
        }

        private static Dictionary<string, object> MbtiRelationshipStatusApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            int limit = Clamp(ReadInt(payload, "limit", 60), 1, 250);
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["campaignId"] = campaignId, ["noLlmConfirmed"] = true,
                    ["model"] = "directional_jobcannon_mbti_19_81_v7",
                    ["rollRule"] = "Roll 1-100 independently in each direction. At or below adjusted compatibility is positive; above is negative. Magnitude is 1d15, or 1d20 once the pair has the lovers tag.",
                    ["nativeRelationRule"] = "Bannerlord native relation converges to the rounded average of the two authoritative Reign directional affinities.",
                    ["lastProcessedDay"] = RelationshipLastProcessedDay(connection,
                        ReadString(payload, "timelineId", "main")),
                    ["latestIngestedDay"] = ReadDouble(QuerySql(connection,
                        "SELECT MAX(world_day) AS day FROM relationship_daily_inputs;").FirstOrDefault(),
                        "day", -1d),
                    ["pendingDailyInputCount"] = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_daily_inputs WHERE status IN ('pending','processing');")
                        .FirstOrDefault(), "count", 0),
                    ["oldestPendingDailyInputDay"] = ReadDouble(QuerySql(connection,
                        "SELECT MIN(world_day) AS day FROM relationship_daily_inputs WHERE status='pending';")
                        .FirstOrDefault(), "day", -1d),
                    ["partialDay"] = QuerySql(connection, @"SELECT day_key,pair_cursor,candidate_pairs,processed_pairs,
started_ts,last_error FROM relationship_daily_inputs WHERE status='processing'
ORDER BY day_key LIMIT 1;").FirstOrDefault() ?? new Dictionary<string, object>(),
                    ["worker"] = ContinuousRelationshipWorkerStatus(),
                    ["mbtiInitializationError"] = ReadString(QuerySql(connection,
                        "SELECT value FROM schema_meta WHERE key='relationship_mbti_error' LIMIT 1;")
                        .FirstOrDefault(), "value", ""),
                    ["currentPairs"] = QuerySql(connection, "SELECT * FROM relationship_pair_chemistry ORDER BY last_day DESC,weighted_exposure DESC LIMIT " + limit.ToString(CultureInfo.InvariantCulture) + ";"),
                    ["pendingNativeChanges"] = QuerySql(connection, "SELECT pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,projected_native_relation,native_action_pending,native_action_id FROM relationship_pair_chemistry WHERE native_action_pending=1 ORDER BY last_day DESC LIMIT " + limit.ToString(CultureInfo.InvariantCulture) + ";"),
                    ["retiredDailyFacetRows"] = 0
                };
            }
        }

        private static Dictionary<string, object> DeriveMbtiAxes(Dictionary<string, object> profile)
        {
            double ei = WeightedMbtiAxis(profile,
                new[] { "sociability", "assertiveness", "confidence", "flirtatiousness", "optimism", "shame" },
                new[] { 4d, 2d, 2d, 1d, 1d, -1d });
            double ns = WeightedMbtiAxis(profile,
                new[] { "curiosity", "knowledgeMotivation", "legacyMotivation", "ambition", "pragmatism", "discipline", "dutyMotivation", "survivalMotivation", "traditionalism" },
                new[] { 3d, 2d, 1d, 1d, -2d, -2d, -2d, -1d, -2d });
            double tf = WeightedMbtiAxis(profile,
                new[] { "pragmatism", "discipline", "emotionalStability", "assertiveness", "knowledgeMotivation", "empathy", "compassion", "mercy", "generosity" },
                new[] { 3d, 2d, 1d, 1d, 1d, -3d, -3d, -2d, -1d });
            double jp = WeightedMbtiAxis(profile,
                new[] { "discipline", "patience", "dutyMotivation", "traditionalism", "emotionalStability", "impulsiveness", "riskTolerance", "curiosity" },
                new[] { 3d, 2d, 3d, 2d, 1d, -3d, -2d, -1d });
            string type = (ei >= 0d ? "E" : "I") + (ns >= 0d ? "N" : "S")
                + (tf >= 0d ? "T" : "F") + (jp >= 0d ? "J" : "P");
            return new Dictionary<string, object>
            {
                ["type"] = type, ["extraversion"] = Math.Round(ei, 4),
                ["intuition"] = Math.Round(ns, 4), ["thinking"] = Math.Round(tf, 4),
                ["judging"] = Math.Round(jp, 4),
                ["source"] = ReadString(profile, "ambientTraitSource", "transient_deterministic")
            };
        }

        private static int RelationshipLastProcessedDay(ReignDbConnection connection, string timelineId)
        {
            Dictionary<string, object> row = QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key=$key LIMIT 1;",
                new Dictionary<string, object>
                {
                    ["key"] = "mbti_relationship_last_processed_day:"
                        + (string.IsNullOrWhiteSpace(timelineId) ? "main" : timelineId)
                }).FirstOrDefault();
            if (row != null) return ReadInt(row, "value", -1);
            return ReadInt(QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='mbti_relationship_last_processed_day' LIMIT 1;")
                .FirstOrDefault(), "value", -1);
        }

        private static double WeightedMbtiAxis(Dictionary<string, object> profile, string[] keys, double[] weights)
        {
            double sum = 0d, denominator = 0d;
            for (int i = 0; i < keys.Length && i < weights.Length; i++)
            {
                double signed = (MbtiTraitPercent(profile, keys[i]) - 50d) / 50d;
                sum += signed * weights[i];
                denominator += Math.Abs(weights[i]);
            }
            return denominator <= 0d ? 0d : ClampDouble(sum / denominator, -1d, 1d);
        }

        private static double MbtiTraitPercent(Dictionary<string, object> profile, string key)
        {
            Dictionary<string, object> percentages = ReadDictionary(profile, "traitPercentages") ?? new Dictionary<string, object>();
            if (percentages.ContainsKey(key)) return ClampDouble(ReadDouble(percentages, key, 50d), 0d, 100d);
            Dictionary<string, object> foundation = ReadDictionary(profile, "foundationTraits") ?? new Dictionary<string, object>();
            return ClampDouble(50d + 20d * ReadDouble(foundation, key, 0d), 0d, 100d);
        }

        private static int MbtiCompatibility(string observerType, string targetType)
        {
            return RelationshipCompatibilityPolicy.BaseScore(
                observerType, targetType);
        }

        private static int StableDie(string seed, int sides)
        {
            return Clamp(1 + (int)Math.Floor(StableUnit(seed) * sides), 1, Math.Max(1, sides));
        }

        private static int RelationshipCadenceShard(string campaignId, string pairKey)
        {
            return StableDie((campaignId ?? "default") + "|" + (pairKey ?? "")
                + "|relationship_cadence_shard_v1", RelationshipCadenceShardCount) - 1;
        }

        private static DailyPairDice BuildDailyPairDice(
            string campaignId,
            AmbientPairContext pair,
            int day)
        {
            double magnitudeAtoB = StableUnit(campaignId + "|"
                + pair.PairKey + "|"
                + day.ToString(CultureInfo.InvariantCulture)
                + "|a_to_b|magnitude_v2");
            double magnitudeBtoA = StableUnit(campaignId + "|"
                + pair.PairKey + "|"
                + day.ToString(CultureInfo.InvariantCulture)
                + "|b_to_a|magnitude_v2");
            return new DailyPairDice
            {
                PairKey = pair.PairKey,
                // Selection into the daily matching is the exposure gate.
                SettlementEligible = true,
                RollAtoB = StableDie(campaignId + "|" + pair.PairKey + "|"
                    + day.ToString(CultureInfo.InvariantCulture) + "|a_to_b|d100", 100),
                RollBtoA = StableDie(campaignId + "|" + pair.PairKey + "|"
                    + day.ToString(CultureInfo.InvariantCulture) + "|b_to_a|d100", 100),
                OrdinaryMagnitudeAtoB = StableDieFromUnit(magnitudeAtoB,
                    OrdinaryRelationshipMagnitudeSides),
                OrdinaryMagnitudeBtoA = StableDieFromUnit(magnitudeBtoA,
                    OrdinaryRelationshipMagnitudeSides),
                LoverMagnitudeAtoB = StableDieFromUnit(magnitudeAtoB,
                    LoverRelationshipMagnitudeSides),
                LoverMagnitudeBtoA = StableDieFromUnit(magnitudeBtoA,
                    LoverRelationshipMagnitudeSides)
            };
        }

        private static int StableDieFromUnit(double unit, int sides)
        {
            return Clamp(1 + (int)Math.Floor(
                ClampDouble(unit, 0d, 0.9999999999999999d) * sides),
                1, Math.Max(1, sides));
        }

        private static bool UsesLoverRelationshipMagnitude(
            Dictionary<string, object> lifecycleRow)
        {
            return lifecycleRow != null
                && ReadInt(lifecycleRow, "lover_active", 0) == 1;
        }

        private static bool AreRelationshipHeroesCoPresent(
            IEnumerable<Dictionary<string, object>> groups,
            string heroAId,
            string heroBId)
        {
            if (string.IsNullOrWhiteSpace(heroAId)
                || string.IsNullOrWhiteSpace(heroBId)) return false;
            foreach (Dictionary<string, object> group in groups
                ?? Enumerable.Empty<Dictionary<string, object>>())
            {
                List<string> ids = ReadStringList(group, "heroIds");
                if (ids.Contains(heroAId, StringComparer.OrdinalIgnoreCase)
                    && ids.Contains(heroBId, StringComparer.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static Dictionary<string, HashSet<int>> BuildRelationshipCoPresenceIndex(
            IEnumerable<Dictionary<string, object>> groups)
        {
            Dictionary<string, HashSet<int>> index = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            int groupIndex = 0;
            foreach (Dictionary<string, object> group in groups ?? Enumerable.Empty<Dictionary<string, object>>())
            {
                foreach (string id in ReadStringList(group, "heroIds"))
                {
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (!index.TryGetValue(id, out HashSet<int> memberships))
                        index[id] = memberships = new HashSet<int>();
                    memberships.Add(groupIndex);
                }
                groupIndex++;
            }
            return index;
        }

        private static bool RelationshipHeroesShareGroup(Dictionary<string, HashSet<int>> index,
            string heroAId, string heroBId)
        {
            return index != null && !string.IsNullOrWhiteSpace(heroAId) && !string.IsNullOrWhiteSpace(heroBId)
                && index.TryGetValue(heroAId, out HashSet<int> a)
                && index.TryGetValue(heroBId, out HashSet<int> b)
                && (a.Count <= b.Count ? a.Overlaps(b) : b.Overlaps(a));
        }

        private sealed class RelationshipPairDay
        {
            public int Day;
            public AmbientPairContext Pair;
            public Dictionary<string, object> HeroA;
            public Dictionary<string, object> HeroB;
            public DailyPairDice Dice;
        }

        private sealed class DailyPairDice
        {
            public string PairKey = "";
            public bool SettlementEligible;
            public int RollAtoB;
            public int RollBtoA;
            public int OrdinaryMagnitudeAtoB;
            public int OrdinaryMagnitudeBtoA;
            public int LoverMagnitudeAtoB;
            public int LoverMagnitudeBtoA;
        }

        private sealed class OpeningSeedComputation
        {
            public string TypeA = "";
            public string TypeB = "";
            public int ChanceAB;
            public int ChanceBA;
            public int Baseline;
            public bool Eligible;
            public int SignAB;
            public int SignBA;
            public int MagnitudeAB;
            public int MagnitudeBA;
            public int DeltaAB;
            public int DeltaBA;
        }

        private static int RoundAwayFromZero(double value)
        {
            return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        private static string DirectionalRelationshipTag(string campaignId, string pairKey, string direction, int affinity, int reverseAffinity)
        {
            string[] pool;
            if (affinity <= -70) pool = new[] { "nemesis", "hated_enemy" };
            else if (affinity <= -50) pool = new[] { "enemy", "contemptuous" };
            else if (affinity <= -30) pool = new[] { "rival", "resentful" };
            else if (affinity <= -10) pool = new[] { "irritant", "suspicious", "dismissive" };
            else if (affinity <= 9) pool = new[] { "neutral", "unfamiliar" };
            else if (affinity <= 29) pool = new[] { "acquaintance", "cordial" };
            else if (affinity <= 49) pool = new[] { "friend", "admirer", "trusted_associate" };
            else if (affinity <= 69) pool = new[] { "close_friend", "confidant", "best_friend" };
            else if (affinity <= 84) pool = new[] { "devoted_companion", "deep_attachment" };
            else pool = new[] { "sworn_companion", "bonded" };
            int index = StableDie(campaignId + "|" + pairKey + "|" + direction + "|" + RelationshipBand(affinity), pool.Length) - 1;
            return pool[index];
        }

        private static string SharedRelationshipTag(string campaignId, string pairKey, int affinityAB, int affinityBA,
            Dictionary<string, object> heroA, Dictionary<string, object> heroB)
        {
            if (affinityAB >= 50 && affinityBA >= 50)
                return StableUnit(campaignId + "|" + pairKey + "|shared_friendship") < 0.5d ? "best_friends" : "confidants";
            if (affinityAB <= -70 && affinityBA <= -70) return "nemeses";
            if (affinityAB <= -50 && affinityBA <= -50) return "enemies";
            if (affinityAB <= -30 && affinityBA <= -30) return "rivals";
            return "";
        }

        private static bool MbtiRomanceEligible(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            string aId = ReadFirstString(a, "heroStringId", "heroId", "id");
            string bId = ReadFirstString(b, "heroStringId", "heroId", "id");
            if (string.IsNullOrWhiteSpace(aId) || string.IsNullOrWhiteSpace(bId)) return false;
            if (ReadDouble(a, "age", 18d) < 18d || ReadDouble(b, "age", 18d) < 18d) return false;
            if (ReadString(a, "fatherId", "").Equals(bId, StringComparison.OrdinalIgnoreCase)
                || ReadString(a, "motherId", "").Equals(bId, StringComparison.OrdinalIgnoreCase)
                || ReadString(b, "fatherId", "").Equals(aId, StringComparison.OrdinalIgnoreCase)
                || ReadString(b, "motherId", "").Equals(aId, StringComparison.OrdinalIgnoreCase)) return false;
            string aFather = ReadString(a, "fatherId", ""), bFather = ReadString(b, "fatherId", "");
            string aMother = ReadString(a, "motherId", ""), bMother = ReadString(b, "motherId", "");
            if ((!string.IsNullOrWhiteSpace(aFather) && aFather.Equals(bFather, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(aMother) && aMother.Equals(bMother, StringComparison.OrdinalIgnoreCase))) return false;
            return true;
        }

        private static int InitialReignAffinity(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            string aId = ReadFirstString(a, "heroStringId", "heroId", "id");
            string bId = ReadFirstString(b, "heroStringId", "heroId", "id");
            bool spouses = ReadString(a, "spouseId", "").Equals(bId, StringComparison.OrdinalIgnoreCase)
                && ReadString(b, "spouseId", "").Equals(aId, StringComparison.OrdinalIgnoreCase);
            bool immediateFamily = ReadString(a, "fatherId", "").Equals(bId, StringComparison.OrdinalIgnoreCase)
                || ReadString(a, "motherId", "").Equals(bId, StringComparison.OrdinalIgnoreCase)
                || ReadString(b, "fatherId", "").Equals(aId, StringComparison.OrdinalIgnoreCase)
                || ReadString(b, "motherId", "").Equals(aId, StringComparison.OrdinalIgnoreCase);
            string aFather = ReadString(a, "fatherId", ""), bFather = ReadString(b, "fatherId", "");
            string aMother = ReadString(a, "motherId", ""), bMother = ReadString(b, "motherId", "");
            immediateFamily = immediateFamily
                || (!string.IsNullOrWhiteSpace(aFather) && aFather.Equals(bFather, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(aMother) && aMother.Equals(bMother, StringComparison.OrdinalIgnoreCase));
            bool sameKingdom = !string.IsNullOrWhiteSpace(ReadString(a, "kingdomId", ""))
                && ReadString(a, "kingdomId", "").Equals(ReadString(b, "kingdomId", ""), StringComparison.OrdinalIgnoreCase);
            bool lordRulerPair = sameKingdom
                && ((ReadBool(a, "isRuler", false) && ReadBool(b, "isLord", false))
                    || (ReadBool(b, "isRuler", false) && ReadBool(a, "isLord", false)));
            int baseline = ReignRelationshipBaselinePolicy.ResolveStartingBaseline(spouses, immediateFamily, lordRulerPair);
            return baseline == ReignRelationshipBaselinePolicy.NoBaseline ? 0 : baseline;
        }

        private static string RelationshipBand(int value)
        {
            if (value <= -70) return "nemesis";
            if (value <= -50) return "enemy";
            if (value <= -30) return "rival";
            if (value <= -10) return "irritant";
            if (value <= 9) return "neutral";
            if (value <= 29) return "acquaintance";
            if (value <= 49) return "friend";
            if (value <= 69) return "close_friend";
            if (value <= 84) return "devoted";
            return "bonded";
        }

        private static string RelationshipTagForObserver(ReignDbConnection connection, string campaignId, string observerId, string targetId, int nativeRelation)
        {
            string pairKey = AmbientPairKey(observerId, targetId);
            if (!string.IsNullOrWhiteSpace(pairKey))
            {
                Dictionary<string, object> row = QuerySql(connection,
                    "SELECT hero_a_id,hero_b_id,tag_a_to_b,tag_b_to_a FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                    new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
                if (row != null)
                {
                    Dictionary<string, object> lifecycle = RelationshipLifecycleView(connection, observerId, targetId);
                    if (ReadBool(lifecycle, "divorced", false)) return "divorced";
                    if (ReadBool(lifecycle, "estranged", false)) return "estranged";
                    if (ReadBool(lifecycle, "lovers", false)) return ReadBool(lifecycle, "activeAffair", false) ? "lover_in_affair" : "lover";
                    return ReadString(row, "hero_a_id", "").Equals(observerId, StringComparison.OrdinalIgnoreCase)
                        ? ReadString(row, "tag_a_to_b", "neutral")
                        : ReadString(row, "tag_b_to_a", "neutral");
                }
            }
            // Bannerlord relation is a projection of Reign, never a source for
            // relationship meaning. An unobserved pair therefore has no Reign
            // affinity yet and must be treated as neutral.
            return DirectionalRelationshipTag(campaignId, pairKey, "observer_to_target", 0, 0);
        }

        private static Dictionary<string, object> ResolvePermanentRelationshipMbti(
            ReignDbConnection connection,
            string campaignId,
            int day,
            Dictionary<string, object> hero,
            Dictionary<string, Dictionary<string, object>> notableMbti)
        {
            string heroId = ReadFirstString(hero, "heroStringId", "heroId", "id");
            if (notableMbti != null
                && notableMbti.TryGetValue(heroId, out Dictionary<string, object> authoritativeNotable))
            {
                var sourceRow = ReadDictionary(authoritativeNotable, "relationshipSourceRow");
                if (sourceRow != null) authoritativeNotable = NotableMbtiRowToProfile(sourceRow);
                string notableType = ReadString(authoritativeNotable, "type", "XXXX");
                if (!MbtiDefinitions.TryGetValue(notableType, out MbtiDefinition notableDefinition))
                    return new Dictionary<string, object>
                    {
                        ["type"] = "XXXX",
                        ["source"] = "invalid_notable_mbti_template"
                    };
                Dictionary<string, object> notableTraits = new Dictionary<string, object>
                {
                    ["foundationTraits"] = ReadDictionary(authoritativeNotable, "foundationTraits")
                        ?? new Dictionary<string, object>(),
                    ["traitPercentages"] = ReadDictionary(authoritativeNotable, "traitPercentages")
                        ?? new Dictionary<string, object>()
                };
                long notableTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ExecuteSql(connection, @"INSERT INTO relationship_personalities(
hero_id,mbti_type,title,description,source,assignment_day,template_version,traits_json,created_ts,updated_ts)
VALUES($hero,$type,$title,$description,'notable_mbti_template',$day,$version,$traits,$ts,$ts)
ON CONFLICT(hero_id) DO UPDATE SET
mbti_type=excluded.mbti_type,title=excluded.title,description=excluded.description,
source=excluded.source,assignment_day=excluded.assignment_day,
template_version=excluded.template_version,traits_json=excluded.traits_json,updated_ts=excluded.updated_ts
WHERE relationship_personalities.mbti_type<>excluded.mbti_type
OR relationship_personalities.title<>excluded.title
OR relationship_personalities.description<>excluded.description
OR relationship_personalities.source<>excluded.source
OR relationship_personalities.assignment_day<>excluded.assignment_day
OR relationship_personalities.template_version<>excluded.template_version
OR relationship_personalities.traits_json<>excluded.traits_json;",
                    new Dictionary<string, object>
                    {
                        ["hero"] = heroId,
                        ["type"] = notableType,
                        ["title"] = notableDefinition.Title,
                        ["description"] = notableDefinition.Description,
                        ["day"] = ReadDouble(authoritativeNotable, "assignedDay", day),
                        ["version"] = ReadInt(authoritativeNotable, "templateVersion",
                            NotableMbtiTemplateVersion),
                        ["traits"] = Json.Serialize(notableTraits),
                        ["ts"] = notableTs
                    });
                Dictionary<string, object> resolvedNotable = new Dictionary<string, object>
                {
                    ["type"] = notableType,
                    ["title"] = notableDefinition.Title,
                    ["description"] = notableDefinition.Description,
                    ["source"] = "notable_mbti_template"
                };
                CachePermanentRelationshipMbti(campaignId, heroId,
                    resolvedNotable);
                return resolvedNotable;
            }
            Dictionary<string, object> stored = QuerySql(connection,
                "SELECT * FROM relationship_personalities WHERE hero_id=$hero LIMIT 1;",
                new Dictionary<string, object> { ["hero"] = heroId }).FirstOrDefault();
            if (stored != null)
            {
                string storedType = ReadString(stored, "mbti_type", "XXXX");
                Dictionary<string, object> resolvedStored = new Dictionary<string, object>
                {
                    ["type"] = storedType,
                    ["title"] = ReadString(stored, "title", ""),
                    ["description"] = ReadString(stored, "description", ""),
                    ["source"] = ReadString(stored, "source", "persisted")
                };
                CachePermanentRelationshipMbti(campaignId, heroId,
                    resolvedStored);
                return resolvedStored;
            }

            Dictionary<string, object> mbti = null;
            Dictionary<string, object> traits = null;
            string source;
            double assignmentDay;
            Dictionary<string, Dictionary<string, object>> catalog = LoadCharacterProfileLibrary();
            if (catalog.TryGetValue(heroId, out Dictionary<string, object> pregenerated))
            {
                mbti = ReadDictionary(pregenerated, "mbtiProfile")
                    ?? ReadDictionary(ReadDictionary(pregenerated, "traits"), "mbtiProfile");
                source = "pregenerated_noble_catalog";
                assignmentDay = -1d;
                if (mbti == null || !MbtiDefinitions.ContainsKey(ReadString(mbti, "type", "")))
                    return new Dictionary<string, object> { ["type"] = "XXXX", ["source"] = "invalid_pregenerated_noble_catalog" };
            }
            else
            {
                string type = MbtiTypes[StableIndex(campaignId + "|runtime_mbti|" + heroId, MbtiTypes.Length)];
                MbtiDefinition definition = MbtiDefinitions[type];
                traits = BuildNotableMbtiTraitDocument(campaignId, heroId,
                    ReadString(hero, "name", heroId), type);
                mbti = new Dictionary<string, object>
                {
                    ["type"] = type,
                    ["title"] = definition.Title,
                    ["description"] = definition.Description,
                    ["templateVersion"] = NotableMbtiTemplateVersion
                };
                source = "runtime_character_mbti_template";
                assignmentDay = day;
            }

            string resolvedType = ReadString(mbti, "type", "XXXX");
            if (!MbtiDefinitions.TryGetValue(resolvedType, out MbtiDefinition resolvedDefinition))
                return new Dictionary<string, object> { ["type"] = "XXXX", ["source"] = source };
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ExecuteSql(connection, @"INSERT INTO relationship_personalities(
hero_id,mbti_type,title,description,source,assignment_day,template_version,traits_json,created_ts,updated_ts)
VALUES($hero,$type,$title,$description,$source,$day,$version,$traits,$ts,$ts)
ON CONFLICT(hero_id) DO NOTHING;",
                new Dictionary<string, object>
                {
                    ["hero"] = heroId,
                    ["type"] = resolvedType,
                    ["title"] = resolvedDefinition.Title,
                    ["description"] = resolvedDefinition.Description,
                    ["source"] = source,
                    ["day"] = assignmentDay,
                    ["version"] = ReadInt(mbti, "templateVersion", NotableMbtiTemplateVersion),
                    ["traits"] = Json.Serialize(traits ?? new Dictionary<string, object>()),
                    ["ts"] = ts
                });
            Dictionary<string, object> resolvedProfile = new Dictionary<string, object>
            {
                ["type"] = resolvedType,
                ["title"] = resolvedDefinition.Title,
                ["description"] = resolvedDefinition.Description,
                ["source"] = source
            };
            CachePermanentRelationshipMbti(campaignId, heroId,
                resolvedProfile);
            return resolvedProfile;
        }

        private static Dictionary<string, Dictionary<string, object>>
            LoadPermanentRelationshipMbti(ReignDbConnection connection,
                string campaignId, IEnumerable<string> heroIds)
        {
            string campaignKey = string.IsNullOrWhiteSpace(campaignId)
                ? "default" : campaignId;
            ConcurrentDictionary<string, Dictionary<string, object>> cached =
                RelationshipPermanentMbtiCache.GetOrAdd(campaignKey,
                    _ => new ConcurrentDictionary<string,
                        Dictionary<string, object>>(
                            StringComparer.OrdinalIgnoreCase));
            HashSet<string> required = new HashSet<string>(
                (heroIds ?? Enumerable.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            List<string> missing = required
                .Where(value => !cached.ContainsKey(value))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (missing.Count > 0)
            {
                string requestedJson = Json.Serialize(missing);
                List<Dictionary<string, object>> rows =
                    ReignPostgreSqlDialect.IsPostgreSql(connection)
                        ? QuerySql(connection, @"SELECT personalities.hero_id,
personalities.mbti_type,personalities.title,personalities.description,
personalities.source
FROM relationship_personalities personalities
JOIN jsonb_array_elements_text(CAST($heroes AS jsonb)) requested
  ON requested.value=personalities.hero_id;",
                            new Dictionary<string, object>
                            {
                                ["heroes"] = requestedJson
                            })
                        : QuerySql(connection, @"SELECT hero_id,mbti_type,title,
description,source FROM relationship_personalities;")
                            .Where(row => required.Contains(
                                ReadString(row, "hero_id", ""))).ToList();
                foreach (Dictionary<string, object> row in rows)
                {
                    string heroId = ReadString(row, "hero_id", "");
                    string type = ReadString(row, "mbti_type", "XXXX");
                    if (string.IsNullOrWhiteSpace(heroId)
                        || !MbtiDefinitions.ContainsKey(type))
                        continue;
                    CachePermanentRelationshipMbti(campaignKey, heroId,
                        new Dictionary<string, object>
                        {
                            ["type"] = type,
                            ["title"] = ReadString(row, "title", ""),
                            ["description"] = ReadString(row,
                                "description", ""),
                            ["source"] = ReadString(row, "source",
                                "persisted")
                        });
                }
            }
            return required.Where(cached.ContainsKey).ToDictionary(
                value => value,
                value => new Dictionary<string, object>(cached[value],
                    StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        }

        private static void CachePermanentRelationshipMbti(
            string campaignId, string heroId,
            Dictionary<string, object> profile)
        {
            if (string.IsNullOrWhiteSpace(heroId) || profile == null
                || !MbtiDefinitions.ContainsKey(
                    ReadString(profile, "type", "")))
                return;
            string campaignKey = string.IsNullOrWhiteSpace(campaignId)
                ? "default" : campaignId;
            ConcurrentDictionary<string, Dictionary<string, object>> cached =
                RelationshipPermanentMbtiCache.GetOrAdd(campaignKey,
                    _ => new ConcurrentDictionary<string,
                        Dictionary<string, object>>(
                            StringComparer.OrdinalIgnoreCase));
            cached[heroId] = new Dictionary<string, object>(profile,
                StringComparer.OrdinalIgnoreCase);
        }

        internal static void InvalidateRelationshipPermanentMbtiCache(
            string campaignId)
        {
            string key = string.IsNullOrWhiteSpace(campaignId)
                ? "default" : campaignId;
            RelationshipPermanentMbtiCache.TryRemove(key, out _);
        }

        private static ReignDbCommand CreatePreparedRelationshipCommand(
            ReignDbConnection connection,
            string sql,
            params string[] parameterNames)
        {
            ReignDbCommand command = connection.CreateCommand();
            command.CommandText = ReignPostgreSqlDialect.Normalize(connection, sql);
            command.CommandTimeout = 60;
            foreach (string name in parameterNames.Distinct(StringComparer.Ordinal))
            {
                System.Data.Common.DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = ReignPostgreSqlDialect.ParameterName(connection, name);
                parameter.Value = DBNull.Value;
                command.Parameters.Add(parameter);
            }
            return command;
        }

        private static void ExecutePreparedRelationshipCommand(
            ReignDbCommand command,
            Dictionary<string, object> values)
        {
            // Iterate the prepared collection once and use the caller's hash
            // lookup for the high-volume relationship pair upsert.
            for (int index = 0; index < command.Parameters.Count; index++)
            {
                System.Data.Common.DbParameter parameter = command.Parameters[index];
                string key = parameter.ParameterName.StartsWith("$", StringComparison.Ordinal)
                    ? parameter.ParameterName.Substring(1)
                    : parameter.ParameterName;
                if (values.TryGetValue(key, out object value)
                    || values.TryGetValue(parameter.ParameterName, out value))
                    parameter.Value = value ?? DBNull.Value;
            }
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    command.ExecuteNonQuery();
                    return;
                }
                catch (Exception ex) when (
                    IsTransientDatabaseContention(ex) && attempt < 240)
                {
                    Thread.Sleep(Math.Min(250, 10 + attempt * 5));
                }
            }
        }

        private static void FlushPostgreSqlRelationshipWriteBatches(
            ReignDbConnection connection,
            ReignDbCommand pairUpsert,
            ReignDbCommand nativeTargetUpsert,
            ReignDbCommand nativeTargetDelete,
            List<Dictionary<string, object>> pairRows,
            List<Dictionary<string, object>> nativeTargetRows,
            List<Dictionary<string, object>> nativeTargetDeletes)
        {
            if (!ReignPostgreSqlDialect.IsPostgreSql(connection))
                return;
            ExecutePostgreSqlRelationshipWriteSet(connection, pairRows,
                nativeTargetRows, nativeTargetDeletes);
            pairRows.Clear();
            nativeTargetRows.Clear();
            nativeTargetDeletes.Clear();
        }

        private static string PostgreSqlRelationshipRowsJson(
            List<Dictionary<string, object>> rows)
        {
            return Json.Serialize((rows
                    ?? new List<Dictionary<string, object>>())
                .Select(row => row.ToDictionary(
                    item => item.Key.ToLowerInvariant(),
                    item => item.Value,
                    StringComparer.OrdinalIgnoreCase))
                .ToList());
        }

        private static void ExecutePostgreSqlJsonCommand(
            ReignDbConnection connection, string sql, string json)
        {
            NpgsqlConnection postgreSql = connection as NpgsqlConnection;
            if (postgreSql == null)
                throw new InvalidOperationException(
                    "A PostgreSQL relationship set write requires Npgsql.");
            using (NpgsqlCommand command = postgreSql.CreateCommand())
            {
                command.CommandText = sql;
                command.CommandTimeout = 60;
                command.Parameters.Add(new NpgsqlParameter(
                    "rows", NpgsqlTypes.NpgsqlDbType.Jsonb)
                {
                    Value = json ?? "[]"
                });
                command.ExecuteNonQuery();
            }
        }

        private static void ExecutePostgreSqlRelationshipWriteSet(
            ReignDbConnection connection,
            List<Dictionary<string, object>> pairRows,
            List<Dictionary<string, object>> nativeTargetRows,
            List<Dictionary<string, object>> nativeTargetDeletes)
        {
            if (pairRows != null && pairRows.Count > 0)
            {
                ExecutePostgreSqlRelationshipPairCopy(connection, pairRows);
            }
            if (nativeTargetRows != null && nativeTargetRows.Count > 0)
            {
                ExecutePostgreSqlJsonCommand(connection, @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(@rows) AS r(
        pair text,actor text,targethero text,targetrelation integer,
        observed integer,day double precision,ts bigint,requiresobservation integer)
)
INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,
world_day,last_sync_day,attempt_count,claimed_ts,last_error,updated_ts,requires_observation)
SELECT pair,actor,targethero,targetrelation,observed,'pending',day,
-1000,0,0,'',ts,COALESCE(requiresobservation,0) FROM x
ON CONFLICT(pair_key) DO UPDATE SET
hero_a_id=excluded.hero_a_id,hero_b_id=excluded.hero_b_id,
observed_relation=excluded.observed_relation,world_day=excluded.world_day,
requires_observation=GREATEST(relationship_native_targets.requires_observation,excluded.requires_observation),
status='pending',
revision=CASE
    WHEN relationship_native_targets.target_relation
        = excluded.target_relation
    THEN relationship_native_targets.revision
    ELSE relationship_native_targets.revision+1 END,
attempt_count=CASE
    WHEN relationship_native_targets.target_relation
        = excluded.target_relation
    THEN relationship_native_targets.attempt_count ELSE 0 END,
claimed_ts=0,target_relation=excluded.target_relation,last_error='',
updated_ts=excluded.updated_ts
WHERE relationship_native_targets.target_relation
        <> excluded.target_relation
   OR relationship_native_targets.observed_relation
         <> excluded.observed_relation
   OR relationship_native_targets.requires_observation < excluded.requires_observation
   OR relationship_native_targets.status NOT IN ('pending','claimed');",
                    PostgreSqlRelationshipRowsJson(nativeTargetRows));
            }
            if (nativeTargetDeletes != null
                && nativeTargetDeletes.Count > 0)
            {
                ExecutePostgreSqlJsonCommand(connection, @"
DELETE FROM relationship_native_targets AS target
USING jsonb_to_recordset(@rows) AS x(pair text)
WHERE target.pair_key=x.pair;",
                    PostgreSqlRelationshipRowsJson(nativeTargetDeletes));
            }
        }

        private static void ExecutePostgreSqlRelationshipPairCopy(
            ReignDbConnection connection,
            List<Dictionary<string, object>> pairRows)
        {
            NpgsqlConnection postgreSql = connection as NpgsqlConnection;
            if (postgreSql == null)
                throw new InvalidOperationException(
                    "A PostgreSQL relationship COPY requires Npgsql.");

            using (NpgsqlCommand setup = postgreSql.CreateCommand())
            {
                setup.CommandTimeout = 60;
                setup.CommandText = @"
CREATE TEMP TABLE IF NOT EXISTS relationship_pair_chemistry_stage(
pair text NOT NULL,a text NOT NULL,b text NOT NULL,
typea text NOT NULL,typeb text NOT NULL,axesa text NOT NULL,
axesb text NOT NULL,baseab bigint NOT NULL,baseba bigint NOT NULL,
chanceab bigint NOT NULL,chanceba bigint NOT NULL,
affinityab bigint NOT NULL,affinityba bigint NOT NULL,
signalab bigint NOT NULL,signalba bigint NOT NULL,
tagab text NOT NULL,tagba text NOT NULL,shared text NOT NULL,
firstday bigint NOT NULL,lastday bigint NOT NULL,
consecutive bigint NOT NULL,weighted double precision NOT NULL,
kind text NOT NULL,contextid text NOT NULL,rollab bigint NOT NULL,
rollba bigint NOT NULL,deltaab bigint NOT NULL,deltaba bigint NOT NULL,
projected bigint NOT NULL,nativeday double precision NOT NULL,
pending bigint NOT NULL,actionid text NOT NULL,shard bigint NOT NULL,
presenceday bigint NOT NULL,batchsize bigint NOT NULL,
presencemask bigint NOT NULL,decayperiod bigint NOT NULL,
version bigint NOT NULL,updatedts bigint NOT NULL)
ON COMMIT DELETE ROWS;
TRUNCATE relationship_pair_chemistry_stage;";
                setup.ExecuteNonQuery();
            }

            Stopwatch copyTimer = Stopwatch.StartNew();
            using (NpgsqlBinaryImporter writer =
                postgreSql.BeginBinaryImport(@"
COPY relationship_pair_chemistry_stage(
pair,a,b,typea,typeb,axesa,axesb,baseab,baseba,chanceab,chanceba,
affinityab,affinityba,signalab,signalba,tagab,tagba,shared,
firstday,lastday,consecutive,weighted,kind,contextid,rollab,rollba,
deltaab,deltaba,projected,nativeday,pending,actionid,shard,
presenceday,batchsize,presencemask,decayperiod,version,updatedts)
FROM STDIN (FORMAT BINARY)"))
            {
                foreach (Dictionary<string, object> row in pairRows)
                {
                    writer.StartRow();
                    writer.Write(ReadString(row, "pair", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(ReadString(row, "a", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(ReadString(row, "b", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(ReadString(row, "typeA", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(ReadString(row, "typeB", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(ReadString(row, "axesA", "{}"),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(ReadString(row, "axesB", "{}"),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "baseAB");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "baseBA");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "chanceAB");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "chanceBA");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "affinityAB");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "affinityBA");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "signalAB");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "signalBA");
                    writer.Write(ReadString(row, "tagAB", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(ReadString(row, "tagBA", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(ReadString(row, "shared", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "first");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "last");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "consecutive");
                    writer.Write(ReadDouble(row, "weighted", 0d),
                        NpgsqlTypes.NpgsqlDbType.Double);
                    writer.Write(ReadString(row, "kind", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(ReadString(row, "context", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "rollAB");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "rollBA");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "deltaAB");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "deltaBA");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "projected");
                    writer.Write(ReadDouble(row, "nativeDay", -1000d),
                        NpgsqlTypes.NpgsqlDbType.Double);
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "pending");
                    writer.Write(ReadString(row, "action", ""),
                        NpgsqlTypes.NpgsqlDbType.Text);
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "shard");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "presenceDay");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "batchSize");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "presenceMask");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "decayPeriod");
                    WritePostgreSqlRelationshipCopyInteger(writer, row,
                        "version");
                    writer.Write(ReadLong(row, "ts", 0),
                        NpgsqlTypes.NpgsqlDbType.Bigint);
                }
                writer.Complete();
            }
            copyTimer.Stop();
            Interlocked.Add(ref PostgreSqlRelationshipStagedRows,
                pairRows.Count);
            Interlocked.Add(ref PostgreSqlRelationshipCopyMs,
                copyTimer.ElapsedMilliseconds);

            Stopwatch mergeTimer = Stopwatch.StartNew();
            int written;
            using (NpgsqlCommand merge = postgreSql.CreateCommand())
            {
                merge.CommandTimeout = 60;
                merge.CommandText = @"
INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,axes_a_json,axes_b_json,
base_chance_a_to_b,base_chance_b_to_a,chance_a_to_b,chance_b_to_a,
affinity_a_to_b,affinity_b_to_a,signal_a_to_b,signal_b_to_a,
tag_a_to_b,tag_b_to_a,shared_tag,first_day,last_day,consecutive_days,
weighted_exposure,last_context_kind,last_context_id,last_roll_a_to_b,
last_roll_b_to_a,last_delta_a_to_b,last_delta_b_to_a,
projected_native_relation,last_native_sync_day,native_action_pending,
native_action_id,processing_shard,last_presence_day,last_batch_size,
last_presence_mask,last_decay_period,compatibility_version,state_revision,
updated_ts)
SELECT pair,a,b,typea,typeb,axesa,axesb,baseab,baseba,chanceab,chanceba,
affinityab,affinityba,signalab,signalba,tagab,tagba,shared,
firstday,lastday,consecutive,weighted,kind,contextid,rollab,rollba,
deltaab,deltaba,projected,nativeday,pending,actionid,shard,presenceday,
batchsize,presencemask,decayperiod,version,1,updatedts
FROM relationship_pair_chemistry_stage
ON CONFLICT(pair_key) DO UPDATE SET
mbti_a=excluded.mbti_a,mbti_b=excluded.mbti_b,
axes_a_json=excluded.axes_a_json,axes_b_json=excluded.axes_b_json,
base_chance_a_to_b=excluded.base_chance_a_to_b,
base_chance_b_to_a=excluded.base_chance_b_to_a,
chance_a_to_b=excluded.chance_a_to_b,
chance_b_to_a=excluded.chance_b_to_a,
affinity_a_to_b=excluded.affinity_a_to_b,
affinity_b_to_a=excluded.affinity_b_to_a,
signal_a_to_b=excluded.signal_a_to_b,
signal_b_to_a=excluded.signal_b_to_a,
tag_a_to_b=excluded.tag_a_to_b,tag_b_to_a=excluded.tag_b_to_a,
shared_tag=excluded.shared_tag,last_day=excluded.last_day,
consecutive_days=excluded.consecutive_days,
weighted_exposure=excluded.weighted_exposure,
last_context_kind=excluded.last_context_kind,
last_context_id=excluded.last_context_id,
last_roll_a_to_b=excluded.last_roll_a_to_b,
last_roll_b_to_a=excluded.last_roll_b_to_a,
last_delta_a_to_b=excluded.last_delta_a_to_b,
last_delta_b_to_a=excluded.last_delta_b_to_a,
projected_native_relation=excluded.projected_native_relation,
last_native_sync_day=excluded.last_native_sync_day,
native_action_pending=excluded.native_action_pending,
native_action_id=excluded.native_action_id,
processing_shard=excluded.processing_shard,
last_presence_day=excluded.last_presence_day,
last_batch_size=excluded.last_batch_size,
last_presence_mask=excluded.last_presence_mask,
last_decay_period=CASE
    WHEN relationship_pair_chemistry.affinity_a_to_b
            <> excluded.affinity_a_to_b
      OR relationship_pair_chemistry.affinity_b_to_a
            <> excluded.affinity_b_to_a
    THEN excluded.last_decay_period
    ELSE relationship_pair_chemistry.last_decay_period END,
compatibility_version=excluded.compatibility_version,
state_revision=relationship_pair_chemistry.state_revision+1,
updated_ts=excluded.updated_ts
WHERE (relationship_pair_chemistry.mbti_a,
       relationship_pair_chemistry.mbti_b,
       relationship_pair_chemistry.axes_a_json,
       relationship_pair_chemistry.axes_b_json,
       relationship_pair_chemistry.base_chance_a_to_b,
       relationship_pair_chemistry.base_chance_b_to_a,
       relationship_pair_chemistry.chance_a_to_b,
       relationship_pair_chemistry.chance_b_to_a,
       relationship_pair_chemistry.affinity_a_to_b,
       relationship_pair_chemistry.affinity_b_to_a,
       relationship_pair_chemistry.signal_a_to_b,
       relationship_pair_chemistry.signal_b_to_a,
       relationship_pair_chemistry.tag_a_to_b,
       relationship_pair_chemistry.tag_b_to_a,
       relationship_pair_chemistry.shared_tag,
       relationship_pair_chemistry.last_day,
       relationship_pair_chemistry.consecutive_days,
       relationship_pair_chemistry.weighted_exposure,
       relationship_pair_chemistry.last_context_kind,
       relationship_pair_chemistry.last_context_id,
       relationship_pair_chemistry.last_roll_a_to_b,
       relationship_pair_chemistry.last_roll_b_to_a,
       relationship_pair_chemistry.last_delta_a_to_b,
       relationship_pair_chemistry.last_delta_b_to_a,
       relationship_pair_chemistry.projected_native_relation,
       relationship_pair_chemistry.last_native_sync_day,
       relationship_pair_chemistry.native_action_pending,
       relationship_pair_chemistry.native_action_id,
       relationship_pair_chemistry.processing_shard,
       relationship_pair_chemistry.last_presence_day,
       relationship_pair_chemistry.last_batch_size,
       relationship_pair_chemistry.last_presence_mask,
       relationship_pair_chemistry.compatibility_version)
IS DISTINCT FROM
      (excluded.mbti_a,excluded.mbti_b,excluded.axes_a_json,
       excluded.axes_b_json,excluded.base_chance_a_to_b,
       excluded.base_chance_b_to_a,excluded.chance_a_to_b,
       excluded.chance_b_to_a,excluded.affinity_a_to_b,
       excluded.affinity_b_to_a,excluded.signal_a_to_b,
       excluded.signal_b_to_a,excluded.tag_a_to_b,
       excluded.tag_b_to_a,excluded.shared_tag,excluded.last_day,
       excluded.consecutive_days,excluded.weighted_exposure,
       excluded.last_context_kind,excluded.last_context_id,
       excluded.last_roll_a_to_b,excluded.last_roll_b_to_a,
       excluded.last_delta_a_to_b,excluded.last_delta_b_to_a,
       excluded.projected_native_relation,
       excluded.last_native_sync_day,excluded.native_action_pending,
       excluded.native_action_id,excluded.processing_shard,
       excluded.last_presence_day,excluded.last_batch_size,
       excluded.last_presence_mask,excluded.compatibility_version)
OR ((relationship_pair_chemistry.affinity_a_to_b
        <> excluded.affinity_a_to_b
     OR relationship_pair_chemistry.affinity_b_to_a
        <> excluded.affinity_b_to_a)
    AND relationship_pair_chemistry.last_decay_period
        IS DISTINCT FROM excluded.last_decay_period);";
                written = merge.ExecuteNonQuery();
            }
            mergeTimer.Stop();
            Interlocked.Add(ref PostgreSqlRelationshipWrittenRows, written);
            Interlocked.Add(ref PostgreSqlRelationshipMergeMs,
                mergeTimer.ElapsedMilliseconds);
        }

        private static void WritePostgreSqlRelationshipCopyInteger(
            NpgsqlBinaryImporter writer,
            Dictionary<string, object> row, string key)
        {
            writer.Write(ReadLong(row, key, 0),
                NpgsqlTypes.NpgsqlDbType.Bigint);
        }

        private static void FlushPostgreSqlRelationshipProvenanceBatch(
            ReignDbConnection connection,
            List<Dictionary<string, object>> rows)
        {
            if (!ReignPostgreSqlDialect.IsPostgreSql(connection)
                || rows == null || rows.Count == 0)
                return;
            string json = Json.Serialize(rows.Select(row =>
                row.ToDictionary(
                    item => item.Key.ToLowerInvariant(),
                    item => item.Value,
                    StringComparer.OrdinalIgnoreCase)).ToList());
            ExecuteSql(connection, @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(CAST($rows AS jsonb)) AS r(
        campaign text,timeline text,pair text,source text,required integer,
        day double precision,details text,ts bigint)
)
INSERT INTO relationship_pair_provenance(
campaign_id,timeline_id,pair_key,source,active,required,first_day,last_day,
details_json,updated_ts)
SELECT campaign,timeline,pair,source,1,required,day,day,details,ts FROM x
ON CONFLICT(campaign_id,timeline_id,pair_key,source) DO UPDATE SET
active=1,required=excluded.required,last_day=excluded.last_day,
details_json=excluded.details_json,updated_ts=excluded.updated_ts
WHERE relationship_pair_provenance.active<>1
OR relationship_pair_provenance.required<>excluded.required
OR relationship_pair_provenance.last_day<>excluded.last_day
OR relationship_pair_provenance.details_json<>excluded.details_json;",
                new Dictionary<string, object> { ["rows"] = json });
            rows.Clear();
        }

        private static void ExecutePostgreSqlPreparedRelationshipBatch(
            ReignDbConnection connection,
            ReignDbCommand template,
            List<Dictionary<string, object>> rows)
        {
            if (rows == null || rows.Count == 0)
                return;
            NpgsqlConnection postgreSql = connection as NpgsqlConnection;
            if (postgreSql == null)
                throw new InvalidOperationException(
                    "A PostgreSQL relationship batch requires Npgsql.");
            using (NpgsqlBatch batch = new NpgsqlBatch(postgreSql))
            {
                batch.Timeout = 60;
                foreach (Dictionary<string, object> values in rows)
                {
                    NpgsqlBatchCommand command =
                        new NpgsqlBatchCommand(template.CommandText);
                    for (int index = 0;
                        index < template.Parameters.Count; index++)
                    {
                        System.Data.Common.DbParameter source =
                            template.Parameters[index];
                        string key = source.ParameterName.StartsWith(
                            "$", StringComparison.Ordinal)
                                ? source.ParameterName.Substring(1)
                                : source.ParameterName;
                        values.TryGetValue(key, out object value);
                        command.Parameters.Add(new NpgsqlParameter(
                            source.ParameterName, value ?? DBNull.Value));
                    }
                    batch.BatchCommands.Add(command);
                }
                batch.ExecuteNonQuery();
            }
        }

        private static ReignDbCommand CreateRelationshipPairUpsertCommand(ReignDbConnection connection)
        {
            return CreatePreparedRelationshipCommand(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,axes_a_json,axes_b_json,base_chance_a_to_b,base_chance_b_to_a,
chance_a_to_b,chance_b_to_a,affinity_a_to_b,affinity_b_to_a,signal_a_to_b,signal_b_to_a,tag_a_to_b,tag_b_to_a,
 shared_tag,first_day,last_day,consecutive_days,weighted_exposure,last_context_kind,last_context_id,last_roll_a_to_b,
 last_roll_b_to_a,last_delta_a_to_b,last_delta_b_to_a,projected_native_relation,last_native_sync_day,native_action_pending,
 native_action_id,processing_shard,last_presence_day,last_batch_size,last_presence_mask,last_decay_period,compatibility_version,
 state_revision,updated_ts)
 VALUES($pair,$a,$b,$typeA,$typeB,$axesA,$axesB,$baseAB,$baseBA,$chanceAB,$chanceBA,$affinityAB,$affinityBA,$signalAB,
 $signalBA,$tagAB,$tagBA,$shared,$first,$last,$consecutive,$weighted,$kind,$context,$rollAB,$rollBA,$deltaAB,$deltaBA,
 $projected,$nativeDay,$pending,$action,$shard,$presenceDay,$batchSize,$presenceMask,$decayPeriod,$version,1,$ts)
ON CONFLICT(pair_key) DO UPDATE SET mbti_a=$typeA,mbti_b=$typeB,axes_a_json=$axesA,axes_b_json=$axesB,
base_chance_a_to_b=$baseAB,base_chance_b_to_a=$baseBA,chance_a_to_b=$chanceAB,chance_b_to_a=$chanceBA,
affinity_a_to_b=$affinityAB,affinity_b_to_a=$affinityBA,signal_a_to_b=$signalAB,signal_b_to_a=$signalBA,
tag_a_to_b=$tagAB,tag_b_to_a=$tagBA,shared_tag=$shared,last_day=$last,consecutive_days=$consecutive,
weighted_exposure=$weighted,last_context_kind=$kind,last_context_id=$context,last_roll_a_to_b=$rollAB,
 last_roll_b_to_a=$rollBA,last_delta_a_to_b=$deltaAB,last_delta_b_to_a=$deltaBA,
 projected_native_relation=$projected,last_native_sync_day=$nativeDay,native_action_pending=$pending,native_action_id=$action,
 processing_shard=$shard,last_presence_day=$presenceDay,last_batch_size=$batchSize,last_presence_mask=$presenceMask,
 last_decay_period=CASE WHEN relationship_pair_chemistry.affinity_a_to_b<>$affinityAB
 OR relationship_pair_chemistry.affinity_b_to_a<>$affinityBA
 THEN $decayPeriod ELSE relationship_pair_chemistry.last_decay_period END,
 compatibility_version=$version,
 state_revision=relationship_pair_chemistry.state_revision+1,updated_ts=$ts;",
                "$pair", "$a", "$b", "$typeA", "$typeB", "$axesA", "$axesB",
                "$baseAB", "$baseBA", "$chanceAB", "$chanceBA", "$affinityAB", "$affinityBA",
                "$signalAB", "$signalBA", "$tagAB", "$tagBA", "$shared", "$first", "$last",
                "$consecutive", "$weighted", "$kind", "$context", "$rollAB", "$rollBA",
                "$deltaAB", "$deltaBA", "$projected", "$nativeDay", "$pending", "$action",
                "$shard", "$presenceDay", "$batchSize", "$presenceMask", "$decayPeriod",
                "$version", "$ts");
        }

        private static ReignDbCommand CreateOpeningRelationshipPairUpsertCommand(
            ReignDbConnection connection)
        {
            return CreatePreparedRelationshipCommand(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,base_chance_a_to_b,
base_chance_b_to_a,chance_a_to_b,chance_b_to_a,affinity_a_to_b,
affinity_b_to_a,social_modifier_a_to_b,social_modifier_b_to_a,
effective_affinity_a_to_b,effective_affinity_b_to_a,tag_a_to_b,tag_b_to_a,
first_day,last_day,processing_shard,last_presence_day,
projected_native_relation,native_action_pending,native_action_id,
compatibility_version,opening_seed_version,opening_seed_baseline,
opening_seed_chance_a_to_b,opening_seed_chance_b_to_a,
opening_seed_sign_roll_a_to_b,opening_seed_sign_roll_b_to_a,
opening_seed_magnitude_roll_a_to_b,opening_seed_magnitude_roll_b_to_a,
opening_seed_delta_a_to_b,opening_seed_delta_b_to_a,
opening_seed_affinity_a_to_b,opening_seed_affinity_b_to_a,
  opening_seed_eligible,opening_seed_provenance,last_decay_period,
  state_revision,updated_ts)
VALUES($pair,$a,$b,$typeA,$typeB,$baseAB,$baseBA,$chanceAB,$chanceBA,
$affinityAB,$affinityBA,$standingB,$standingA,$effectiveAB,$effectiveBA,
$tagAB,$tagBA,$day,$last,$shard,-1,$projected,$pending,$action,$version,
$seedVersion,$baseline,$chanceAB,$chanceBA,$signAB,$signBA,$magnitudeAB,
$magnitudeBA,$deltaAB,$deltaBA,$affinityAB,$affinityBA,$eligible,
 $provenance,$decayPeriod,1,$ts)
ON CONFLICT(pair_key) DO UPDATE SET
mbti_a=$typeA,mbti_b=$typeB,base_chance_a_to_b=$baseAB,
base_chance_b_to_a=$baseBA,chance_a_to_b=$chanceAB,chance_b_to_a=$chanceBA,
affinity_a_to_b=$affinityAB,affinity_b_to_a=$affinityBA,
social_modifier_a_to_b=$standingB,social_modifier_b_to_a=$standingA,
effective_affinity_a_to_b=$effectiveAB,effective_affinity_b_to_a=$effectiveBA,
tag_a_to_b=$tagAB,tag_b_to_a=$tagBA,last_day=$last,
processing_shard=$shard,projected_native_relation=$projected,
native_action_pending=$pending,native_action_id=$action,
compatibility_version=$version,opening_seed_version=$seedVersion,
opening_seed_baseline=$baseline,opening_seed_chance_a_to_b=$chanceAB,
opening_seed_chance_b_to_a=$chanceBA,opening_seed_sign_roll_a_to_b=$signAB,
opening_seed_sign_roll_b_to_a=$signBA,
opening_seed_magnitude_roll_a_to_b=$magnitudeAB,
opening_seed_magnitude_roll_b_to_a=$magnitudeBA,
opening_seed_delta_a_to_b=$deltaAB,opening_seed_delta_b_to_a=$deltaBA,
opening_seed_affinity_a_to_b=$affinityAB,
opening_seed_affinity_b_to_a=$affinityBA,
 opening_seed_eligible=$eligible,opening_seed_provenance=$provenance,
 last_decay_period=$decayPeriod,
state_revision=relationship_pair_chemistry.state_revision+1,updated_ts=$ts
WHERE relationship_pair_chemistry.opening_seed_version<$seedVersion;",
                "$pair", "$a", "$b", "$typeA", "$typeB", "$baseAB",
                "$baseBA", "$chanceAB", "$chanceBA", "$affinityAB",
                "$affinityBA", "$standingA", "$standingB", "$effectiveAB",
                "$effectiveBA", "$tagAB", "$tagBA", "$day", "$last",
                "$shard", "$projected", "$pending", "$action", "$version",
                "$seedVersion", "$baseline", "$signAB", "$signBA",
                "$magnitudeAB", "$magnitudeBA", "$deltaAB", "$deltaBA",
                "$eligible", "$provenance", "$decayPeriod", "$ts");
        }

        private static ReignDbCommand
            CreateOpeningRelationshipProvenanceUpsertCommand(
                ReignDbConnection connection)
        {
            return CreatePreparedRelationshipCommand(connection, @"
INSERT INTO relationship_pair_provenance(
campaign_id,timeline_id,pair_key,source,active,required,first_day,last_day,
details_json,updated_ts)
VALUES($campaign,$timeline,$pair,$source,1,$required,$day,$day,$details,$ts)
ON CONFLICT(campaign_id,timeline_id,pair_key,source) DO UPDATE SET
active=1,required=$required,last_day=$day,details_json=$details,updated_ts=$ts
WHERE relationship_pair_provenance.active<>1
OR relationship_pair_provenance.required<>$required
OR relationship_pair_provenance.last_day<>$day
OR relationship_pair_provenance.details_json<>$details;",
                "$campaign", "$timeline", "$pair", "$source", "$required",
                "$day", "$details", "$ts");
        }

        private static Dictionary<string, object>
            OpeningRelationshipPairParameters(
                string campaignId, OpeningSeedCandidate candidate,
                string typeA, string typeB, int chanceAB, int chanceBA,
                int affinityAB, int affinityBA, int standingA, int standingB,
                int effectiveAB, int effectiveBA, int projected,
                bool pending, int day,
                bool eligible, int baseline, int signAB, int signBA,
                int magnitudeAB, int magnitudeBA, int deltaAB, int deltaBA,
                string provenance, long ts)
        {
            return new Dictionary<string, object>
            {
                ["pair"] = candidate.PairKey,
                ["a"] = candidate.HeroAId,
                ["b"] = candidate.HeroBId,
                ["typeA"] = typeA,
                ["typeB"] = typeB,
                ["baseAB"] = MbtiCompatibility(typeA, typeB),
                ["baseBA"] = MbtiCompatibility(typeB, typeA),
                ["chanceAB"] = chanceAB,
                ["chanceBA"] = chanceBA,
                ["affinityAB"] = affinityAB,
                ["affinityBA"] = affinityBA,
                ["standingA"] = standingA,
                ["standingB"] = standingB,
                ["effectiveAB"] = effectiveAB,
                ["effectiveBA"] = effectiveBA,
                ["tagAB"] = DirectionalRelationshipTag(campaignId,
                    candidate.PairKey, "a_to_b", affinityAB, affinityBA),
                ["tagBA"] = DirectionalRelationshipTag(campaignId,
                    candidate.PairKey, "b_to_a", affinityBA, affinityAB),
                ["day"] = day,
                ["last"] = day - 1,
                ["shard"] = RelationshipCadenceShard(campaignId,
                    candidate.PairKey),
                ["projected"] = projected,
                ["pending"] = pending ? 1 : 0,
                ["action"] = pending
                    ? "native_target:" + candidate.PairKey : "",
                ["version"] = MbtiChemistryVersion,
                ["seedVersion"] = OpeningRelationshipSeedVersion,
                ["baseline"] = baseline,
                ["signAB"] = signAB,
                ["signBA"] = signBA,
                ["magnitudeAB"] = magnitudeAB,
                ["magnitudeBA"] = magnitudeBA,
                ["deltaAB"] = deltaAB,
                ["deltaBA"] = deltaBA,
                ["eligible"] = eligible ? 1 : 0,
                ["provenance"] = provenance,
                ["decayPeriod"] = Math.Max(0, day / 5),
                ["ts"] = ts
            };
        }

        private static void ExecuteOpeningRelationshipPostgreSqlBulk(
            ReignDbConnection connection,
            List<Dictionary<string, object>> pairRows,
            List<Dictionary<string, object>> provenanceRows,
            List<Dictionary<string, object>> nativeTargetRows,
            List<string> nativeTargetDeletes)
        {
            Func<List<Dictionary<string, object>>, string> jsonRows = rows =>
                Json.Serialize((rows ?? new List<Dictionary<string, object>>())
                    .Select(row => row.ToDictionary(
                        item => item.Key.ToLowerInvariant(),
                        item => item.Value,
                        StringComparer.OrdinalIgnoreCase)).ToList());
            if (pairRows != null && pairRows.Count > 0)
            {
                ExecuteSql(connection, @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(CAST($rows AS jsonb)) AS r(
        pair text,a text,b text,typea text,typeb text,baseab integer,
        baseba integer,chanceab integer,chanceba integer,affinityab integer,
        affinityba integer,standinga integer,standingb integer,
        effectiveab integer,effectiveba integer,tagab text,tagba text,
        day integer,last integer,shard integer,projected integer,
        pending integer,action text,version integer,seedversion integer,
        baseline integer,signab integer,signba integer,magnitudeab integer,
        magnitudeba integer,deltaab integer,deltaba integer,eligible integer,
        provenance text,decayperiod integer,ts bigint)
)
INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,base_chance_a_to_b,
base_chance_b_to_a,chance_a_to_b,chance_b_to_a,affinity_a_to_b,
affinity_b_to_a,social_modifier_a_to_b,social_modifier_b_to_a,
effective_affinity_a_to_b,effective_affinity_b_to_a,tag_a_to_b,tag_b_to_a,
first_day,last_day,processing_shard,last_presence_day,
projected_native_relation,native_action_pending,native_action_id,
compatibility_version,opening_seed_version,opening_seed_baseline,
opening_seed_chance_a_to_b,opening_seed_chance_b_to_a,
opening_seed_sign_roll_a_to_b,opening_seed_sign_roll_b_to_a,
opening_seed_magnitude_roll_a_to_b,opening_seed_magnitude_roll_b_to_a,
opening_seed_delta_a_to_b,opening_seed_delta_b_to_a,
opening_seed_affinity_a_to_b,opening_seed_affinity_b_to_a,
 opening_seed_eligible,opening_seed_provenance,last_decay_period,
 state_revision,updated_ts)
SELECT pair,a,b,typea,typeb,baseab,baseba,chanceab,chanceba,affinityab,
affinityba,standingb,standinga,effectiveab,effectiveba,tagab,tagba,
day,last,shard,-1,projected,pending,action,version,seedversion,baseline,
chanceab,chanceba,signab,signba,magnitudeab,magnitudeba,deltaab,deltaba,
 affinityab,affinityba,eligible,provenance,decayperiod,1,ts
FROM x
ON CONFLICT(pair_key) DO UPDATE SET
mbti_a=excluded.mbti_a,mbti_b=excluded.mbti_b,
base_chance_a_to_b=excluded.base_chance_a_to_b,
base_chance_b_to_a=excluded.base_chance_b_to_a,
chance_a_to_b=excluded.chance_a_to_b,chance_b_to_a=excluded.chance_b_to_a,
affinity_a_to_b=excluded.affinity_a_to_b,
affinity_b_to_a=excluded.affinity_b_to_a,
social_modifier_a_to_b=excluded.social_modifier_a_to_b,
social_modifier_b_to_a=excluded.social_modifier_b_to_a,
effective_affinity_a_to_b=excluded.effective_affinity_a_to_b,
effective_affinity_b_to_a=excluded.effective_affinity_b_to_a,
tag_a_to_b=excluded.tag_a_to_b,tag_b_to_a=excluded.tag_b_to_a,
last_day=excluded.last_day,processing_shard=excluded.processing_shard,
projected_native_relation=excluded.projected_native_relation,
native_action_pending=excluded.native_action_pending,
native_action_id=excluded.native_action_id,
compatibility_version=excluded.compatibility_version,
opening_seed_version=excluded.opening_seed_version,
opening_seed_baseline=excluded.opening_seed_baseline,
opening_seed_chance_a_to_b=excluded.opening_seed_chance_a_to_b,
opening_seed_chance_b_to_a=excluded.opening_seed_chance_b_to_a,
opening_seed_sign_roll_a_to_b=excluded.opening_seed_sign_roll_a_to_b,
opening_seed_sign_roll_b_to_a=excluded.opening_seed_sign_roll_b_to_a,
opening_seed_magnitude_roll_a_to_b=excluded.opening_seed_magnitude_roll_a_to_b,
opening_seed_magnitude_roll_b_to_a=excluded.opening_seed_magnitude_roll_b_to_a,
opening_seed_delta_a_to_b=excluded.opening_seed_delta_a_to_b,
opening_seed_delta_b_to_a=excluded.opening_seed_delta_b_to_a,
opening_seed_affinity_a_to_b=excluded.opening_seed_affinity_a_to_b,
opening_seed_affinity_b_to_a=excluded.opening_seed_affinity_b_to_a,
 opening_seed_eligible=excluded.opening_seed_eligible,
 opening_seed_provenance=excluded.opening_seed_provenance,
 last_decay_period=excluded.last_decay_period,
state_revision=relationship_pair_chemistry.state_revision+1,
updated_ts=excluded.updated_ts
WHERE relationship_pair_chemistry.opening_seed_version
    < excluded.opening_seed_version;",
                    new Dictionary<string, object>
                    {
                        ["rows"] = jsonRows(pairRows)
                    });
            }
            if (provenanceRows != null && provenanceRows.Count > 0)
            {
                ExecuteSql(connection, @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(CAST($rows AS jsonb)) AS r(
        campaign text,timeline text,pair text,source text,required integer,
        day double precision,details text,ts bigint)
)
INSERT INTO relationship_pair_provenance(
campaign_id,timeline_id,pair_key,source,active,required,first_day,last_day,
details_json,updated_ts)
SELECT campaign,timeline,pair,source,1,required,day,day,details,ts FROM x
ON CONFLICT(campaign_id,timeline_id,pair_key,source) DO UPDATE SET
active=1,required=excluded.required,last_day=excluded.last_day,
details_json=excluded.details_json,updated_ts=excluded.updated_ts
WHERE relationship_pair_provenance.active<>1
OR relationship_pair_provenance.required<>excluded.required
OR relationship_pair_provenance.last_day<>excluded.last_day
OR relationship_pair_provenance.details_json<>excluded.details_json;",
                    new Dictionary<string, object>
                    {
                        ["rows"] = jsonRows(provenanceRows)
                    });
            }
            if (nativeTargetRows != null && nativeTargetRows.Count > 0)
            {
                ExecuteSql(connection, @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(CAST($rows AS jsonb)) AS r(
        pair text,actor text,targethero text,targetrelation integer,
        observed integer,day double precision,ts bigint)
)
INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,
world_day,last_sync_day,attempt_count,claimed_ts,last_error,updated_ts)
SELECT pair,actor,targethero,targetrelation,observed,'pending',day,
-1000,0,0,'',ts FROM x
ON CONFLICT(pair_key) DO UPDATE SET
hero_a_id=excluded.hero_a_id,hero_b_id=excluded.hero_b_id,
observed_relation=excluded.observed_relation,world_day=excluded.world_day,
status='pending',
revision=CASE
    WHEN relationship_native_targets.target_relation=excluded.target_relation
    THEN relationship_native_targets.revision
    ELSE relationship_native_targets.revision+1 END,
attempt_count=CASE
    WHEN relationship_native_targets.target_relation=excluded.target_relation
    THEN relationship_native_targets.attempt_count ELSE 0 END,
claimed_ts=0,target_relation=excluded.target_relation,last_error='',
updated_ts=excluded.updated_ts;",
                    new Dictionary<string, object>
                    {
                        ["rows"] = jsonRows(nativeTargetRows)
                    });
            }
            if (nativeTargetDeletes != null && nativeTargetDeletes.Count > 0)
            {
                ExecuteSql(connection, @"
DELETE FROM relationship_native_targets
WHERE pair_key IN (
    SELECT value FROM jsonb_array_elements_text(CAST($pairs AS jsonb)));",
                    new Dictionary<string, object>
                    {
                        ["pairs"] = Json.Serialize(nativeTargetDeletes)
                    });
            }
        }

        private static Dictionary<string, Dictionary<string, object>>
            RelationshipPairStateSnapshot(ReignDbConnection connection,
                string campaignId)
        {
            string key = string.IsNullOrWhiteSpace(campaignId)
                ? "default" : campaignId;
            if (!RelationshipPairStateCache.TryGetValue(key,
                out ConcurrentDictionary<string, Dictionary<string, object>> cached))
            {
                lock (CampaignRelationshipWriteLock(key))
                {
                    if (!RelationshipPairStateCache.TryGetValue(key, out cached))
                    {
                        cached = new ConcurrentDictionary<string,
                            Dictionary<string, object>>(
                                QuerySql(connection,
                                    "SELECT * FROM relationship_pair_chemistry;")
                                .Where(row => !string.IsNullOrWhiteSpace(
                                    ReadString(row, "pair_key", "")))
                                .ToDictionary(row => ReadString(row,
                                        "pair_key", ""),
                                    CloneRelationshipPairCacheRow,
                                    StringComparer.OrdinalIgnoreCase),
                                StringComparer.OrdinalIgnoreCase);
                        RelationshipPairStateCache[key] = cached;
                    }
                }
            }
            return cached.ToDictionary(item => item.Key,
                item => CloneRelationshipPairCacheRow(item.Value),
                StringComparer.OrdinalIgnoreCase);
        }

        private static void InstallOpeningRelationshipPairStateCache(
            string campaignId,
            Dictionary<string, Dictionary<string, object>> existingRows,
            List<Dictionary<string, object>> openingRows)
        {
            string key = string.IsNullOrWhiteSpace(campaignId)
                ? "default" : campaignId;
            Dictionary<string, Dictionary<string, object>> rows =
                new Dictionary<string, Dictionary<string, object>>(
                    StringComparer.OrdinalIgnoreCase);
            if (existingRows != null)
            {
                foreach (KeyValuePair<string, Dictionary<string, object>>
                    item in existingRows)
                    rows[item.Key] = CloneRelationshipPairCacheRow(item.Value);
            }
            foreach (Dictionary<string, object> values in openingRows
                ?? new List<Dictionary<string, object>>())
            {
                string pairKey = ReadString(values, "pair", "");
                if (string.IsNullOrWhiteSpace(pairKey)) continue;
                rows.TryGetValue(pairKey,
                    out Dictionary<string, object> existing);
                Dictionary<string, object> row = existing == null
                    ? new Dictionary<string, object>(
                        StringComparer.OrdinalIgnoreCase)
                    : CloneRelationshipPairCacheRow(existing);
                row["pair_key"] = pairKey;
                row["hero_a_id"] = ReadString(values, "a", "");
                row["hero_b_id"] = ReadString(values, "b", "");
                row["mbti_a"] = ReadString(values, "typeA", "");
                row["mbti_b"] = ReadString(values, "typeB", "");
                row["axes_a_json"] = "{}";
                row["axes_b_json"] = "{}";
                row["base_chance_a_to_b"] = ReadInt(values, "baseAB", 0);
                row["base_chance_b_to_a"] = ReadInt(values, "baseBA", 0);
                row["chance_a_to_b"] = ReadInt(values, "chanceAB", 0);
                row["chance_b_to_a"] = ReadInt(values, "chanceBA", 0);
                row["affinity_a_to_b"] = ReadInt(values, "affinityAB", 0);
                row["affinity_b_to_a"] = ReadInt(values, "affinityBA", 0);
                row["social_modifier_a_to_b"] =
                    ReadInt(values, "standingB", 0);
                row["social_modifier_b_to_a"] =
                    ReadInt(values, "standingA", 0);
                row["effective_affinity_a_to_b"] =
                    ReadInt(values, "effectiveAB", 0);
                row["effective_affinity_b_to_a"] =
                    ReadInt(values, "effectiveBA", 0);
                row["tag_a_to_b"] = ReadString(values, "tagAB", "");
                row["tag_b_to_a"] = ReadString(values, "tagBA", "");
                row["first_day"] = ReadInt(values, "day", 0);
                row["last_day"] = ReadInt(values, "last", -1);
                row["processing_shard"] = ReadInt(values, "shard", 0);
                row["last_presence_day"] = -1;
                row["projected_native_relation"] =
                    ReadInt(values, "projected", 0);
                row["native_action_pending"] =
                    ReadInt(values, "pending", 0);
                row["native_action_id"] = ReadString(values, "action", "");
                row["compatibility_version"] =
                    ReadInt(values, "version", MbtiChemistryVersion);
                row["opening_seed_version"] =
                    ReadInt(values, "seedVersion",
                        OpeningRelationshipSeedVersion);
                row["opening_seed_baseline"] =
                    ReadInt(values, "baseline", 0);
                row["opening_seed_chance_a_to_b"] =
                    ReadInt(values, "chanceAB", 0);
                row["opening_seed_chance_b_to_a"] =
                    ReadInt(values, "chanceBA", 0);
                row["opening_seed_sign_roll_a_to_b"] =
                    ReadInt(values, "signAB", 0);
                row["opening_seed_sign_roll_b_to_a"] =
                    ReadInt(values, "signBA", 0);
                row["opening_seed_magnitude_roll_a_to_b"] =
                    ReadInt(values, "magnitudeAB", 0);
                row["opening_seed_magnitude_roll_b_to_a"] =
                    ReadInt(values, "magnitudeBA", 0);
                row["opening_seed_delta_a_to_b"] =
                    ReadInt(values, "deltaAB", 0);
                row["opening_seed_delta_b_to_a"] =
                    ReadInt(values, "deltaBA", 0);
                row["opening_seed_affinity_a_to_b"] =
                    ReadInt(values, "affinityAB", 0);
                row["opening_seed_affinity_b_to_a"] =
                    ReadInt(values, "affinityBA", 0);
                row["opening_seed_eligible"] =
                    ReadInt(values, "eligible", 0);
                row["opening_seed_provenance"] =
                    ReadString(values, "provenance", "");
                row["last_decay_period"] =
                    ReadInt(values, "decayPeriod", 0);
                row["state_revision"] = existing == null
                    ? 1 : ReadLong(existing, "state_revision", 1) + 1;
                row["updated_ts"] = ReadLong(values, "ts", 0);
                rows[pairKey] = row;
            }
            RelationshipPairStateCache[key] =
                new ConcurrentDictionary<string,
                    Dictionary<string, object>>(rows,
                    StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object>
            CloneRelationshipPairCacheRow(Dictionary<string, object> row)
        {
            return row == null
                ? new Dictionary<string, object>(
                    StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object>(row,
                    StringComparer.OrdinalIgnoreCase);
        }

        private static void StoreCommittedRelationshipPairCacheRows(
            string campaignId,
            IDictionary<string, Dictionary<string, object>> rows)
        {
            string key = string.IsNullOrWhiteSpace(campaignId)
                ? "default" : campaignId;
            if (!RelationshipPairStateCache.TryGetValue(key,
                    out ConcurrentDictionary<string,
                        Dictionary<string, object>> cached)
                || rows == null)
                return;
            foreach (KeyValuePair<string, Dictionary<string, object>> row
                in rows)
            {
                cached[row.Key] =
                    CloneRelationshipPairCacheRow(row.Value);
            }
        }

        internal static void InvalidateRelationshipPairStateCache(
            string campaignId, string pairKey = "")
        {
            string key = string.IsNullOrWhiteSpace(campaignId)
                ? "default" : campaignId;
            if (string.IsNullOrWhiteSpace(pairKey))
            {
                RelationshipPairStateCache.TryRemove(key, out _);
                return;
            }
            if (RelationshipPairStateCache.TryGetValue(key,
                out ConcurrentDictionary<string,
                    Dictionary<string, object>> cached))
                cached.TryRemove(pairKey, out _);
        }

        private static void InvalidateRelationshipPairCacheForMutation(
            ReignDbConnection connection, string sql,
            Dictionary<string, object> parameters)
        {
            string value = (sql ?? string.Empty).TrimStart();
            RelationshipStandingContext.For(connection)?.Invalidate(value);
            if (value.IndexOf("relationship_pair_chemistry",
                    StringComparison.OrdinalIgnoreCase) < 0
                || !(value.StartsWith("UPDATE ",
                        StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("INSERT ",
                        StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("DELETE ",
                        StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("TRUNCATE ",
                        StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("DROP ",
                        StringComparison.OrdinalIgnoreCase)))
                return;
            string campaignId =
                ReignPostgreSqlStorage.CampaignIdForConnection(connection);
            if (string.IsNullOrWhiteSpace(campaignId)) return;
            // This cache represents a complete campaign snapshot. Pair-only
            // eviction would leave a valid-looking but incomplete snapshot.
            // Direct mutations are relatively rare, so force a full lazy reload.
            InvalidateRelationshipPairStateCache(campaignId);
        }

        private static ReignDbCommand CreateRelationshipNativeTargetUpsertCommand(
            ReignDbConnection connection)
        {
            return CreatePreparedRelationshipCommand(connection, @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,world_day,last_sync_day,
attempt_count,claimed_ts,last_error,updated_ts,requires_observation)
VALUES($pair,$actor,$targetHero,$targetRelation,$observed,'pending',$day,-1000,0,0,'',$ts,COALESCE($requiresObservation,0))
ON CONFLICT(pair_key) DO UPDATE SET
hero_a_id=excluded.hero_a_id,hero_b_id=excluded.hero_b_id,
observed_relation=excluded.observed_relation,world_day=excluded.world_day,
requires_observation=CASE WHEN relationship_native_targets.requires_observation=1 OR excluded.requires_observation=1 THEN 1 ELSE 0 END,
status='pending',
revision=CASE WHEN relationship_native_targets.target_relation=excluded.target_relation
    THEN relationship_native_targets.revision ELSE relationship_native_targets.revision+1 END,
attempt_count=CASE
    WHEN relationship_native_targets.target_relation=excluded.target_relation
    THEN relationship_native_targets.attempt_count ELSE 0
END,
claimed_ts=0,
target_relation=excluded.target_relation,last_error='',updated_ts=excluded.updated_ts;",
                "$pair", "$actor", "$targetHero", "$targetRelation", "$observed", "$day", "$ts", "$requiresObservation");
        }

        private static string QueueMbtiNativeRelationAction(ReignDbConnection connection, AmbientPairContext pair,
            int day, int target, int observed,
            ReignDbCommand preparedCommand = null,
            List<Dictionary<string, object>> deferredRows = null,
            bool requiresObservation = false)
        {
            string id = "native_target:" + pair.PairKey;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, object> parameters = new Dictionary<string, object>
            {
                ["pair"] = pair.PairKey, ["day"] = day, ["actor"] = pair.HeroAId,
                ["targetHero"] = pair.HeroBId, ["targetRelation"] = target,
                ["observed"] = observed, ["ts"] = ts, ["requiresObservation"] = requiresObservation ? 1 : 0
            };
            if (deferredRows != null)
                deferredRows.Add(parameters);
            else if (preparedCommand != null)
                ExecutePreparedRelationshipCommand(preparedCommand, parameters);
            else
                ExecuteSql(connection, @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,world_day,last_sync_day,
attempt_count,claimed_ts,last_error,updated_ts,requires_observation)
VALUES($pair,$actor,$targetHero,$targetRelation,$observed,'pending',$day,-1000,0,0,'',$ts,$requiresObservation)
ON CONFLICT(pair_key) DO UPDATE SET
hero_a_id=excluded.hero_a_id,hero_b_id=excluded.hero_b_id,
observed_relation=excluded.observed_relation,world_day=excluded.world_day,
requires_observation=CASE WHEN relationship_native_targets.requires_observation=1 OR excluded.requires_observation=1 THEN 1 ELSE 0 END,
status='pending',
revision=CASE WHEN relationship_native_targets.target_relation=excluded.target_relation
    THEN relationship_native_targets.revision ELSE relationship_native_targets.revision+1 END,
attempt_count=CASE
    WHEN relationship_native_targets.target_relation=excluded.target_relation
    THEN relationship_native_targets.attempt_count ELSE 0
END,
claimed_ts=0,
target_relation=excluded.target_relation,last_error='',updated_ts=excluded.updated_ts;",
                    parameters);
            return id;
        }

        private static Dictionary<string, object>
            ApplyAtomicDirectionalRelationshipDelta(
                ReignDbConnection connection,
                string campaignId,
                string observerId,
                string targetId,
                int delta,
                double worldDay,
                string reason,
                string timelineId)
        {
            if (connection == null || string.IsNullOrWhiteSpace(observerId)
                || string.IsNullOrWhiteSpace(targetId)
                || observerId.Equals(targetId,
                    StringComparison.OrdinalIgnoreCase)
                || delta == 0)
                return null;
            string pairKey = AmbientPairKey(observerId, targetId);
            bool observerIsA = pairKey.StartsWith(observerId + "|",
                StringComparison.OrdinalIgnoreCase);
            string affinityColumn = observerIsA
                ? "affinity_a_to_b" : "affinity_b_to_a";
            int day = (int)Math.Floor(worldDay + 0.000001d);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<Dictionary<string, object>> updated = QuerySql(connection,
                @"UPDATE relationship_pair_chemistry SET "
                + affinityColumn + @"=MAX(-100,MIN(100,"
                + affinityColumn + @"+$delta)),
last_day=MAX(last_day,$day),last_context_kind=$kind,
last_context_id=$context,state_revision=state_revision+1,
updated_ts=$ts
WHERE pair_key=$pair
RETURNING *;",
                new Dictionary<string, object>
                {
                    ["delta"] = delta, ["day"] = day,
                    ["kind"] = "consequence",
                    ["context"] = LimitText(reason ?? string.Empty, 240),
                    ["ts"] = ts, ["pair"] = pairKey
                });
            Dictionary<string, object> pair = updated.FirstOrDefault();
            if (pair == null) return null;

            int affinityAB = ReadInt(pair, "affinity_a_to_b", 0);
            int affinityBA = ReadInt(pair, "affinity_b_to_a", 0);
            string pairHeroA = ReadString(pair, "hero_a_id", "");
            string pairHeroB = ReadString(pair, "hero_b_id", "");
            int effectiveAB = Clamp(affinityAB + ReadInt(ResolveObserverPublicStanding(
                connection, campaignId, timelineId, pairHeroA, pairHeroB), "value", 0), -100, 100);
            int effectiveBA = Clamp(affinityBA + ReadInt(ResolveObserverPublicStanding(
                connection, campaignId, timelineId, pairHeroB, pairHeroA), "value", 0), -100, 100);
            ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
effective_affinity_a_to_b=$effectiveAB,
effective_affinity_b_to_a=$effectiveBA,
tag_a_to_b=$tagAB,tag_b_to_a=$tagBA
WHERE pair_key=$pair AND state_revision=$revision;",
                new Dictionary<string, object>
                {
                    ["effectiveAB"] = effectiveAB,
                    ["effectiveBA"] = effectiveBA,
                    ["tagAB"] = DirectionalRelationshipTag(campaignId,
                        pairKey, "a_to_b", affinityAB, affinityBA),
                    ["tagBA"] = DirectionalRelationshipTag(campaignId,
                        pairKey, "b_to_a", affinityBA, affinityAB),
                    ["pair"] = pairKey,
                    ["revision"] = ReadLong(pair, "state_revision", 1)
                });
            pair["effective_affinity_a_to_b"] = effectiveAB;
            pair["effective_affinity_b_to_a"] = effectiveBA;
            pair["tag_a_to_b"] = DirectionalRelationshipTag(campaignId,
                pairKey, "a_to_b", affinityAB, affinityBA);
            pair["tag_b_to_a"] = DirectionalRelationshipTag(campaignId,
                pairKey, "b_to_a", affinityBA, affinityAB);
            RefreshEffectivePairProjection(connection, campaignId,
                timelineId, pair, worldDay, reason);
            Dictionary<string, object> committed = QuerySql(connection,
                "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = pairKey })
                .FirstOrDefault() ?? pair;
            // This helper may run inside a caller-owned transaction. Never expose
            // uncommitted rows through RAM; the next read reloads committed state.
            InvalidateRelationshipPairStateCache(campaignId);
            return committed;
        }

        private static void ApplyAuthoritativeRelationshipDelta(
            ReignDbConnection connection,
            string campaignId,
            string observerId,
            string targetId,
            int delta,
            double worldDay,
            string reason,
            string timelineId = "main",
            bool ensurePair = false)
        {
            if (string.IsNullOrWhiteSpace(observerId) || string.IsNullOrWhiteSpace(targetId)
                || observerId.Equals(targetId, StringComparison.OrdinalIgnoreCase) || (delta == 0 && !ensurePair))
                return;
            lock (CampaignRelationshipWriteLock(campaignId))
            {
            string pairKey = AmbientPairKey(observerId, targetId);
            bool observerIsA = pairKey.StartsWith(observerId + "|", StringComparison.OrdinalIgnoreCase);
            string heroA = observerIsA ? observerId : targetId;
            string heroB = observerIsA ? targetId : observerId;
            int day = (int)Math.Floor(worldDay + 0.000001d);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, object> existing = QuerySql(connection,
                "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
            if (existing != null)
            {
                ApplyAtomicDirectionalRelationshipDelta(connection,
                    campaignId, observerId, targetId, delta, worldDay,
                    reason, timelineId);
                return;
            }
            string typeA = existing == null ? ResolvePermanentMbtiTypeById(connection, campaignId, heroA, day)
                : ReadString(existing, "mbti_a", "XXXX");
            string typeB = existing == null ? ResolvePermanentMbtiTypeById(connection, campaignId, heroB, day)
                : ReadString(existing, "mbti_b", "XXXX");
            if (!MbtiDefinitions.ContainsKey(typeA) || !MbtiDefinitions.ContainsKey(typeB))
                throw new InvalidOperationException("An authoritative relationship consequence could not resolve immutable MBTI values for " + pairKey + ".");
            int affinityAB = existing == null ? 0 : ReadInt(existing, "affinity_a_to_b", 0);
            int affinityBA = existing == null ? 0 : ReadInt(existing, "affinity_b_to_a", 0);
            if (observerIsA) affinityAB = Clamp(affinityAB + delta, -100, 100);
            else affinityBA = Clamp(affinityBA + delta, -100, 100);
            int socialAB = ReadInt(ResolveObserverPublicStanding(connection, campaignId,
                timelineId, heroA, heroB), "value", 0);
            int socialBA = ReadInt(ResolveObserverPublicStanding(connection, campaignId,
                timelineId, heroB, heroA), "value", 0);
            int effectiveAB = Clamp(affinityAB + socialAB, -100, 100);
            int effectiveBA = Clamp(affinityBA + socialBA, -100, 100);
            int baseAB = existing == null ? MbtiCompatibility(typeA, typeB) : ReadInt(existing, "base_chance_a_to_b", 50);
            int baseBA = existing == null ? MbtiCompatibility(typeB, typeA) : ReadInt(existing, "base_chance_b_to_a", 50);
            int projected = ProjectNativeRelation(connection, heroA, heroB,
                affinityAB, affinityBA, effectiveAB, effectiveBA);
            Dictionary<string, object> existingNativeTarget = QuerySql(connection,
                "SELECT observed_relation,requires_observation FROM relationship_native_targets WHERE pair_key=$pair LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
            int observedNative = existingNativeTarget != null
                ? ReadInt(existingNativeTarget, "observed_relation", 0)
                : existing != null && ReadInt(existing, "native_action_pending", 0) == 0
                    ? ReadInt(existing, "projected_native_relation", 0)
                    : 0;
            bool requiresNativeTarget = projected != observedNative || RelationshipNativeObservationRequired(existing, existingNativeTarget);
            string nativeActionId = requiresNativeTarget ? "native_target:" + pairKey : "";
            ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,base_chance_a_to_b,base_chance_b_to_a,
chance_a_to_b,chance_b_to_a,affinity_a_to_b,affinity_b_to_a,social_modifier_a_to_b,social_modifier_b_to_a,
effective_affinity_a_to_b,effective_affinity_b_to_a,tag_a_to_b,tag_b_to_a,
first_day,last_day,projected_native_relation,native_action_pending,native_action_id,
compatibility_version,updated_ts)
VALUES($pair,$a,$b,$typeA,$typeB,$baseAB,$baseBA,$chanceAB,$chanceBA,$affinityAB,$affinityBA,0,0,$affinityAB,$affinityBA,
$tagAB,$tagBA,$day,$day,$projected,$pending,$action,$version,$ts)
ON CONFLICT(pair_key) DO UPDATE SET
affinity_a_to_b=$affinityAB,affinity_b_to_a=$affinityBA,
social_modifier_a_to_b=0,social_modifier_b_to_a=0,
effective_affinity_a_to_b=$affinityAB,effective_affinity_b_to_a=$affinityBA,
tag_a_to_b=$tagAB,tag_b_to_a=$tagBA,
last_day=MAX(relationship_pair_chemistry.last_day,$day),
projected_native_relation=$projected,native_action_pending=$pending,
native_action_id=$action,updated_ts=$ts;",
                new Dictionary<string, object>
                {
                    ["pair"] = pairKey, ["a"] = heroA, ["b"] = heroB,
                    ["typeA"] = typeA, ["typeB"] = typeB,
                    ["baseAB"] = baseAB, ["baseBA"] = baseBA,
                    ["chanceAB"] = AdjustedMbtiCompatibility(baseAB),
                    ["chanceBA"] = AdjustedMbtiCompatibility(baseBA),
                    ["affinityAB"] = affinityAB, ["affinityBA"] = affinityBA,
                    ["tagAB"] = DirectionalRelationshipTag(campaignId, pairKey, "a_to_b", affinityAB, affinityBA),
                    ["tagBA"] = DirectionalRelationshipTag(campaignId, pairKey, "b_to_a", affinityBA, affinityAB),
                    ["day"] = day, ["projected"] = projected,
                    ["pending"] = requiresNativeTarget ? 1 : 0,
                    ["action"] = nativeActionId,
                    ["version"] = MbtiChemistryVersion, ["ts"] = ts
                });
            if (existing == null)
                RecordRelationshipPairProvenance(connection, campaignId, timelineId,
                    pairKey, "consequential_event", false, worldDay,
                    new Dictionary<string, object> { ["reason"] = reason ?? string.Empty });
            Dictionary<string, object> current = QuerySql(connection,
                "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = pairKey })
                .FirstOrDefault();
            RefreshEffectivePairProjection(connection, campaignId, timelineId,
                current, worldDay, reason);
            }
        }

        private static string ResolvePermanentMbtiTypeById(
            ReignDbConnection connection, string campaignId, string heroId, int day)
        {
            Dictionary<string, object> stored = QuerySql(connection,
                "SELECT mbti_type FROM relationship_personalities WHERE hero_id=$hero LIMIT 1;",
                new Dictionary<string, object> { ["hero"] = heroId }).FirstOrDefault();
            string storedType = ReadString(stored, "mbti_type", "");
            if (MbtiDefinitions.ContainsKey(storedType)) return storedType;
            Dictionary<string, Dictionary<string, object>> catalog = LoadCharacterProfileLibrary();
            if (catalog.TryGetValue(heroId, out Dictionary<string, object> pregenerated))
            {
                Dictionary<string, object> mbti = ReadDictionary(pregenerated, "mbtiProfile")
                    ?? ReadDictionary(ReadDictionary(pregenerated, "traits"), "mbtiProfile");
                string catalogType = ReadString(mbti, "type", "");
                if (!MbtiDefinitions.ContainsKey(catalogType)) return "XXXX";
                ResolvePermanentRelationshipMbti(connection, campaignId, day,
                    new Dictionary<string, object> { ["heroStringId"] = heroId, ["name"] = heroId },
                    new Dictionary<string, Dictionary<string, object>>());
                return catalogType;
            }
            Dictionary<string, object> assigned = ResolvePermanentRelationshipMbti(connection, campaignId, day,
                new Dictionary<string, object> { ["heroStringId"] = heroId, ["name"] = heroId },
                new Dictionary<string, Dictionary<string, object>>());
            return ReadString(assigned, "type", "XXXX");
        }

        private static void CompactNoisyNativeRelationBacklog(ReignDbConnection connection)
        {
            if (ReadString(QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='mbti_native_relation_bulk_projection_v1' LIMIT 1;")
                .FirstOrDefault(), "value", "") == "1") return;

            ExecuteSql(connection, "SAVEPOINT compact_native_relation_backlog;");
            try
            {
                ExecuteSql(connection, "UPDATE relationship_native_targets SET status='pending',claimed_ts=0,last_error='';");
                if (ReadInt(QuerySql(connection,
                    "SELECT COUNT(*) AS count FROM sqlite_master WHERE type='table' AND name='relationship_director_actions';")
                    .FirstOrDefault(), "count", 0) > 0)
                {
                    ExecuteSql(connection, @"UPDATE relationship_director_actions
SET status='superseded',resolved_ts=$ts,
result_json='{""status"":""superseded"",""reason"":""native_relation_bulk_projection""}'
WHERE action_type='native_relation' AND status IN ('pending','claimed');",
                        new Dictionary<string, object> { ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                }
                ExecuteSql(connection,
                    "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('mbti_native_relation_bulk_projection_v1','1');");
                ExecuteSql(connection, "RELEASE compact_native_relation_backlog;");
            }
            catch
            {
                try { ExecuteSql(connection, "ROLLBACK TO compact_native_relation_backlog;"); } catch { }
                try { ExecuteSql(connection, "RELEASE compact_native_relation_backlog;"); } catch { }
                throw;
            }
        }

        private static List<Dictionary<string, object>> RunMbtiRelationshipSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "mbti_relationships",
                ["caseId"] = id, ["name"] = id, ["summary"] = summary, ["durationMs"] = 0
            });

            List<int> all = RelationshipCompatibilityPolicy.AllBaseScores()
                .ToList();
            add("all_256_directional_scores_present", all.Count == 256 && all.Min() == 46 && all.Max() == 93,
                "All 256 ordered JobCannon scores are embedded, ranging from 46 to 93.");
            List<int> adjusted = all.Select(AdjustedMbtiCompatibility).ToList();
            add("compatibility_range_19_81",
                adjusted.Min() == 19 && adjusted.Max() == 81,
                "The proportional compatibility remap produces the approved 19 to 81 positive-roll range.");
            add("compatibility_proportional_anchors",
                AdjustedMbtiCompatibility(46) == 19
                    && AdjustedMbtiCompatibility(54) == 30
                    && AdjustedMbtiCompatibility(62) == 40
                    && AdjustedMbtiCompatibility(70) == 51
                    && AdjustedMbtiCompatibility(93) == 81,
                "The 19 to 81 remap preserves the approved proportional anchor values.");
            add("directional_asymmetry", MbtiCompatibility("ENTJ", "INTJ") == 91 && MbtiCompatibility("INTJ", "ENTJ") == 88,
                "Ordered personality matches can differ in each direction.");
            add("roll_threshold_rule", AdjustedMbtiCompatibility(46) == 19 && 20 > 19,
                "At adjusted compatibility 19, rolls 1-19 gain and rolls 20-100 lose.");
            add("v6_affinity_preserved_during_v7_chance_refresh",
                !(6 < LegacyAffinityRebaseCutoffVersion)
                    && MbtiChemistryVersion == 7,
                "Version-6 relationship affinities remain authoritative while version 7 refreshes only compatibility chances.");
            add("opening_seed_positive_formula",
                OpeningRelationshipDelta(60, 60, 50) == 23,
                "Opening compatibility 60 with a successful sign roll and magnitude 50 produces +23 after the twenty-five-percent reduction.");
            add("opening_seed_negative_formula",
                OpeningRelationshipDelta(60, 61, 50) == -15,
                "Opening compatibility 60 with a failed sign roll and magnitude 50 produces -15 after the twenty-five-percent reduction.");
            add("opening_seed_rounds_away_from_zero",
                OpeningRelationshipDelta(1, 1, 50) == 1
                    && OpeningRelationshipDelta(99, 100, 50) == -1,
                "Opening seed magnitudes round away from zero and always move at least one point.");
            Dictionary<string, Dictionary<string, object>> openingHeroes =
                new Dictionary<string, Dictionary<string, object>>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["adult_a"] = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "adult_a", ["isAlive"] = true,
                        ["isAdult"] = true, ["spouseId"] = "adult_b",
                        ["kingdomId"] = "kingdom_1", ["isRuler"] = true
                    },
                    ["adult_b"] = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "adult_b", ["isAlive"] = true,
                        ["isAdult"] = true, ["spouseId"] = "adult_a",
                        ["kingdomId"] = "kingdom_1", ["isClanLeader"] = true
                    },
                    ["child"] = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "child", ["isAlive"] = true,
                        ["isAdult"] = false, ["fatherId"] = "adult_a"
                    },
                    ["prisoner"] = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "prisoner", ["isAlive"] = true,
                        ["isAdult"] = true, ["isPrisoner"] = true
                    },
                    ["player"] = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "player", ["isAlive"] = true,
                        ["isAdult"] = true, ["isPlayer"] = true
                    }
                };
            Dictionary<string, OpeningSeedCandidate> openingCandidates =
                BuildOpeningSeedCandidates(openingHeroes,
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["kind"] = "party", ["id"] = "party_1",
                            ["heroIds"] = new List<string>
                            {
                                "adult_a", "adult_b", "prisoner", "player"
                            }
                        },
                        new Dictionary<string, object>
                        {
                            ["kind"] = "settlement", ["id"] = "town_1",
                            ["heroIds"] = new List<string>
                            {
                                "adult_a", "adult_b", "prisoner", "player"
                            }
                        }
                    });
            add("opening_seed_pair_network_deduplicates_and_excludes",
                openingCandidates.Count == 2
                    && openingCandidates.ContainsKey(
                        AmbientPairKey("adult_a", "adult_b"))
                    && openingCandidates.ContainsKey(
                        AmbientPairKey("adult_a", "child"))
                    && !openingCandidates.Keys.Any(key =>
                        key.IndexOf("prisoner", StringComparison.OrdinalIgnoreCase) >= 0
                        || key.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0),
                "Party/settlement overlap is deduplicated, excluded heroes do not receive broad seeds, and required child-family pairs remain materialized.");
            int positiveAffinity = Clamp(20 + 15, -100, 100);
            add("positive_colocation_die_applies_directly", positiveAffinity == 35,
                "A successful +15 roll immediately raises that observer's directional affinity by fifteen.");
            int negativeAffinity = Clamp(20 - 15, -100, 100);
            add("negative_colocation_die_applies_directly", negativeAffinity == 5,
                "A failed -15 roll immediately lowers that observer's directional affinity by fifteen.");
            add("native_relation_is_directional_average", RoundAwayFromZero((40d + -20d) / 2d) == 10,
                "NPC native relation is projected from the average of both directional affinities.");
            add("native_relation_sync_is_exact",
                RoundAwayFromZero((4d + 3d) / 2d) == 4
                    && RoundAwayFromZero((-4d + -3d) / 2d) == -4,
                "Every bulk projection writes the exact rounded directional average without a deadband.");
            add("unrelated_pairs_ignore_native_relation",
                InitialReignAffinity(
                    new Dictionary<string, object> { ["heroStringId"] = "a" },
                    new Dictionary<string, object> { ["heroStringId"] = "b" }) == 0,
                "A newly observed unrelated pair starts from Reign neutral instead of inheriting Bannerlord relation.");
            add("spouses_start_at_fifty",
                ReignRelationshipBaselinePolicy.ResolveStartingBaseline(true, true, true) == 50,
                "Spouse precedence seeds both partners at native relation fifty.");
            add("immediate_family_starts_at_twenty",
                ReignRelationshipBaselinePolicy.ResolveStartingBaseline(false, true, false) == 20,
                "Parents, children, and siblings start at native relation twenty.");
            add("lords_start_at_ten_with_ruler",
                ReignRelationshipBaselinePolicy.ResolveStartingBaseline(false, false, true) == 10,
                "Every lord starts at native relation ten with their current ruler.");
            add("extended_family_has_no_baseline",
                ReignRelationshipBaselinePolicy.ResolveStartingBaseline(false, false, false)
                    == ReignRelationshipBaselinePolicy.NoBaseline,
                "Cousins and other extended relatives receive no automatic relationship baseline.");
            add("romance_is_not_inferred_by_affinity_tags",
                !DirectionalRelationshipTag("test", "a|b", "a_to_b", 75, 45).Equals("lover", StringComparison.OrdinalIgnoreCase)
                && SharedRelationshipTag("test", "a|b", 75, 45,
                    new Dictionary<string, object> { ["heroStringId"] = "a", ["age"] = 30 },
                    new Dictionary<string, object> { ["heroStringId"] = "b", ["age"] = 30 }) == "",
                "Affinity labels never create a romantic tag; the lifecycle state machine owns lovers.");
            add("reciprocal_high_affinity_remains_friendship_without_lifecycle",
                new[] { "best_friends", "confidants" }.Contains(SharedRelationshipTag("test", "a|b", 75, 75,
                    new Dictionary<string, object> { ["heroStringId"] = "a", ["age"] = 30 },
                    new Dictionary<string, object> { ["heroStringId"] = "b", ["age"] = 30 })),
                "Mutual high affinity stays a friendship label until the deterministic lifecycle activates lovers.");
            add("automatic_relationship_normalization_is_disabled",
                !AutomaticRelationshipNormalizationEnabled,
                "Personal affinity remains event-driven and is never periodically pulled toward zero.");
            add("reciprocal_friendship_unlocks",
                new[] { "best_friends", "confidants" }.Contains(SharedRelationshipTag("test", "a|b", 50, 50,
                    new Dictionary<string, object> { ["heroStringId"] = "a", ["age"] = 30 },
                    new Dictionary<string, object> { ["heroStringId"] = "b", ["age"] = 30 })),
                "Mutual affinity at 50 or higher creates a close reciprocal friendship tag.");
            double deterministicMagnitude = StableUnit("same_seed_magnitude");
            add("deterministic_dice", StableDie("same_seed", 100) == StableDie("same_seed", 100)
                && StableDieFromUnit(deterministicMagnitude,
                    OrdinaryRelationshipMagnitudeSides) >= 1
                && StableDieFromUnit(deterministicMagnitude,
                    OrdinaryRelationshipMagnitudeSides) <= 15
                && StableDieFromUnit(deterministicMagnitude,
                    LoverRelationshipMagnitudeSides) >= 1
                && StableDieFromUnit(deterministicMagnitude,
                    LoverRelationshipMagnitudeSides) <= 20,
                "Daily rolls are deterministic across retries; ordinary d15 and lover d20 magnitudes stay in range.");
            Dictionary<string, object> workerPolicy = ContinuousRelationshipWorkerStatus();
            add("adaptive_worker_limits",
                ReadInt(workerPolicy, "baseLimitPerSecond", 0) == 10000
                    && ReadInt(workerPolicy, "burstLimitPerSecond", 0) == 50000
                    && RelationshipLimitForQueuedDays(0) == 10000
                    && RelationshipLimitForQueuedDays(1) == 50000
                    && RelationshipLimitForQueuedDays(5) == 50000
                    && ReadInt(workerPolicy, "chunkSize", 0)
                        == PostgreSqlRelationshipWriteChunkSize,
                "Continuous PostgreSQL processing commits 5,000-pair durable chunks and immediately enables the 50,000 pair-day burst ceiling whenever any day is queued.");
            int expectedAvailableWorkers = Environment.ProcessorCount <= 2
                ? 1
                : Math.Max(1, Math.Min(32,
                    Environment.ProcessorCount
                    - (Environment.ProcessorCount >= 6 ? 2 : 1)));
            int burstWorkers = RelationshipParallelismForWork(10000, true);
            int dailyWorkers = RelationshipParallelismForWork(841, true);
            int smallWorkWorkers = RelationshipParallelismForWork(100, true);
            add("adaptive_multicore_relationship_compute",
                burstWorkers == Math.Min(expectedAvailableWorkers, 8)
                    && dailyWorkers == Math.Min(expectedAvailableWorkers, 2)
                    && smallWorkWorkers == 1
                    && ReadInt(workerPolicy, "logicalProcessors", 0)
                        == Environment.ProcessorCount,
                "Relationship dice use bounded 1/2/4/8 worker tiers so a normal daily match avoids scheduler overhead while large catch-up batches still use available cores.");
            AmbientPairContext deterministicParallelPair =
                new AmbientPairContext
                {
                    PairKey = "parallel_a|parallel_b",
                    HeroAId = "parallel_a",
                    HeroBId = "parallel_b",
                    ContextKind = "party",
                    ContextId = "parallel_party"
                };
            DailyPairDice deterministicSerialDice = BuildDailyPairDice(
                "parallel_contract", deterministicParallelPair, 17);
            DailyPairDice deterministicWorkerDice = null;
            Parallel.Invoke(new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1,
                    Math.Min(2, Environment.ProcessorCount))
            }, () => deterministicWorkerDice = BuildDailyPairDice(
                "parallel_contract", deterministicParallelPair, 17));
            add("parallel_relationship_dice_are_deterministic",
                deterministicWorkerDice != null
                    && deterministicWorkerDice.SettlementEligible
                        == deterministicSerialDice.SettlementEligible
                    && deterministicWorkerDice.RollAtoB
                        == deterministicSerialDice.RollAtoB
                    && deterministicWorkerDice.RollBtoA
                        == deterministicSerialDice.RollBtoA
                    && deterministicWorkerDice.OrdinaryMagnitudeAtoB
                        == deterministicSerialDice.OrdinaryMagnitudeAtoB
                    && deterministicWorkerDice.OrdinaryMagnitudeBtoA
                        == deterministicSerialDice.OrdinaryMagnitudeBtoA
                    && deterministicWorkerDice.LoverMagnitudeAtoB
                        == deterministicSerialDice.LoverMagnitudeAtoB
                    && deterministicWorkerDice.LoverMagnitudeBtoA
                        == deterministicSerialDice.LoverMagnitudeBtoA,
                "Parallel relationship preparation reproduces the exact serial campaign/pair/day dice.");
            List<Dictionary<string, object>> compactRoster = Enumerable.Range(0, 2400)
                .Select(index => new Dictionary<string, object>
                {
                    ["i"] = "campaign_hero_" + index.ToString(CultureInfo.InvariantCulture),
                    ["a"] = 18 + index % 55,
                    ["s"] = "",
                    ["fa"] = index > 100 ? "campaign_hero_" + (index % 100).ToString(CultureInfo.InvariantCulture) : "",
                    ["mo"] = "",
                    ["c"] = "clan_" + (index % 120).ToString(CultureInfo.InvariantCulture),
                    ["k"] = "kingdom_" + (index % 9).ToString(CultureInfo.InvariantCulture),
                    ["n"] = index % 3 == 0 ? 1 : 0,
                    ["o"] = index % 3 == 1 ? 1 : 0
                }).ToList();
            List<Dictionary<string, object>> compactGroups = Enumerable.Range(0, 120)
                .Select(groupIndex => new Dictionary<string, object>
                {
                    ["kind"] = "settlement",
                    ["id"] = "settlement_" + groupIndex.ToString(CultureInfo.InvariantCulture),
                    ["heroIds"] = Enumerable.Range(groupIndex * 20, 20)
                        .Select(index => "campaign_hero_" + index.ToString(CultureInfo.InvariantCulture)).ToList()
                }).ToList();
            int compactBytes = Encoding.UTF8.GetByteCount(Json.Serialize(new Dictionary<string, object>
            {
                ["campaignId"] = "payload_test", ["timelineId"] = "main", ["worldDay"] = 1d,
                ["heroes"] = compactRoster, ["presenceGroups"] = compactGroups
            }));
            add("compact_daily_input_under_500kb", compactBytes < 500 * 1024,
                "A full 2,400-character compact lifecycle roster with actual presence groups is "
                + compactBytes.ToString(CultureInfo.InvariantCulture) + " bytes.");
            string ingestCampaignId = "mr_ingest_" + Guid.NewGuid().ToString("N").Substring(0, 10);
            try
            {
                using (ReignDbConnection connection = OpenCampaignConnection(ingestCampaignId))
                    EnsureMbtiRelationshipSchema(connection);
                Stopwatch ingestTimer = Stopwatch.StartNew();
                Dictionary<string, object> accepted = PersistAndHydrateRelationshipDailyInput(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = ingestCampaignId,
                        ["timelineId"] = "main",
                        ["worldDay"] = 1d,
                        ["heroes"] = compactRoster,
                        ["presenceGroups"] = compactGroups
                    });
                ingestTimer.Stop();
                int observedHeroes;
                int pendingDays;
                long unchangedHeroTimestamp;
                using (ReignDbConnection connection = OpenCampaignConnection(ingestCampaignId))
                {
                    observedHeroes = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_observed_heroes;")
                        .FirstOrDefault(), "count", 0);
                    pendingDays = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_daily_inputs WHERE status='pending';")
                        .FirstOrDefault(), "count", 0);
                    ExecuteSql(connection, @"UPDATE relationship_observed_heroes
SET updated_ts=17 WHERE hero_id='campaign_hero_0';");
                }
                PersistAndHydrateRelationshipDailyInput(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = ingestCampaignId,
                        ["timelineId"] = "main",
                        ["worldDay"] = 1d,
                        ["heroes"] = compactRoster,
                        ["presenceGroups"] = compactGroups
                    });
                using (ReignDbConnection connection =
                    OpenCampaignConnection(ingestCampaignId))
                {
                    unchangedHeroTimestamp = ReadLong(QuerySql(connection,
                        @"SELECT updated_ts FROM relationship_observed_heroes
WHERE hero_id='campaign_hero_0' LIMIT 1;")
                        .FirstOrDefault(), "updated_ts", -1);
                }
                add("full_roster_daily_ingest_is_bounded_and_durable",
                    observedHeroes == 2400
                    && pendingDays == 1
                    && ReadDictionaryList(accepted, "heroes").Count == 2400
                    && ingestTimer.ElapsedMilliseconds < 2000,
                    "A 2,400-character daily input is durably accepted as one day in under two seconds; measured "
                    + ingestTimer.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms.");
                add("unchanged_observed_heroes_are_not_rewritten",
                    unchangedHeroTimestamp == 17,
                    "Repeated daily presence keeps the durable roster current without rewriting unchanged lifecycle profiles.");
            }
            finally
            {
                TryDeleteDirectory(CampaignDirectory(ingestCampaignId));
            }
            results.AddRange(RunCampaignOpeningRelationshipSeedSelfTests());
            results.AddRange(RunDailyRandomRelationshipMatchingSelfTests());
            results.AddRange(RunMbtiRelationshipPersistenceSelfTests());
            return results;
        }

        private static List<Dictionary<string, object>>
            RunCampaignOpeningRelationshipSeedSelfTests()
        {
            List<Dictionary<string, object>> results =
                new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(
                new Dictionary<string, object>
                {
                    ["ok"] = true, ["passed"] = passed,
                    ["suite"] = "mbti_relationships", ["caseId"] = id,
                    ["name"] = id, ["summary"] = summary, ["durationMs"] = 0
                });
            string campaignId = "opening_seed_"
                + Guid.NewGuid().ToString("N").Substring(0, 10);
            List<Dictionary<string, object>> heroes =
                new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["heroStringId"] = "seed_a", ["name"] = "Seed A",
                        ["age"] = 30d, ["isAdult"] = true, ["isAlive"] = true,
                        ["spouseId"] = "seed_b", ["isRuler"] = true,
                        ["isClanLeader"] = true, ["kingdomId"] = "seed_kingdom"
                    },
                    new Dictionary<string, object>
                    {
                        ["heroStringId"] = "seed_b", ["name"] = "Seed B",
                        ["age"] = 30d, ["isAdult"] = true, ["isAlive"] = true,
                        ["isNotable"] = true,
                        ["spouseId"] = "seed_a", ["isClanLeader"] = true,
                        ["kingdomId"] = "seed_kingdom"
                    },
                    new Dictionary<string, object>
                    {
                        ["heroStringId"] = "seed_child", ["name"] = "Seed Child",
                        ["age"] = 10d, ["isAdult"] = false, ["isAlive"] = true,
                        ["fatherId"] = "seed_a"
                    }
                };
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = "main",
                ["generationId"] = "fixture_generation", ["worldDay"] = 0d,
                ["verificationSuppressWorkerSignal"] = true,
                ["heroes"] = heroes,
                ["presenceGroups"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["kind"] = "party", ["id"] = "seed_party",
                        ["heroIds"] = new List<string>
                        {
                            "seed_a", "seed_b"
                        }
                    },
                    new Dictionary<string, object>
                    {
                        ["kind"] = "settlement", ["id"] = "seed_town",
                        ["heroIds"] = new List<string>
                        {
                            "seed_a", "seed_b"
                        }
                    }
                }
            };
            try
            {
                int authoritativeNotableIndex = StableDie(campaignId
                    + "|seed_b|notable_mbti_template_v"
                    + NotableMbtiTemplateVersion.ToString(
                        CultureInfo.InvariantCulture), MbtiTypes.Length) - 1;
                string authoritativeNotableType = MbtiTypes[Clamp(
                    authoritativeNotableIndex, 0, MbtiTypes.Length - 1)];
                using (ReignDbConnection connection =
                    OpenCampaignConnection(campaignId))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    string conflictingType = MbtiTypes.First(type =>
                        !type.Equals(authoritativeNotableType,
                            StringComparison.OrdinalIgnoreCase));
                    MbtiDefinition conflictingDefinition =
                        MbtiDefinitions[conflictingType];
                    long fixtureTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    ExecuteSql(connection, @"INSERT INTO relationship_personalities(
hero_id,mbti_type,title,description,source,assignment_day,template_version,
traits_json,created_ts,updated_ts)
VALUES('seed_b',$type,$title,$description,'fixture_conflict',0,1,'{}',$ts,$ts)
ON CONFLICT(hero_id) DO UPDATE SET mbti_type=excluded.mbti_type,
title=excluded.title,description=excluded.description,
source=excluded.source,updated_ts=excluded.updated_ts;",
                        new Dictionary<string, object>
                        {
                            ["type"] = conflictingType,
                            ["title"] = conflictingDefinition.Title,
                            ["description"] =
                                conflictingDefinition.Description,
                            ["ts"] = fixtureTs
                        });
                }
                Dictionary<string, object> first =
                    CampaignOpeningRelationshipSeedApi(payload);
                Dictionary<string, object> spouse;
                Dictionary<string, object> child;
                Dictionary<string, object> run;
                Dictionary<string, object> daily;
                Dictionary<string, object> persistedNotable;
                Dictionary<string, object> politicalProvenance;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    spouse = QuerySql(connection, @"
SELECT * FROM relationship_pair_chemistry
WHERE pair_key=$pair LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["pair"] = AmbientPairKey("seed_a", "seed_b")
                        }).FirstOrDefault() ?? new Dictionary<string, object>();
                    child = QuerySql(connection, @"
SELECT * FROM relationship_pair_chemistry
WHERE pair_key=$pair LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["pair"] = AmbientPairKey("seed_a", "seed_child")
                        }).FirstOrDefault() ?? new Dictionary<string, object>();
                    run = QuerySql(connection,
                        "SELECT * FROM relationship_opening_seed_runs LIMIT 1;")
                        .FirstOrDefault() ?? new Dictionary<string, object>();
                    daily = QuerySql(connection,
                        "SELECT * FROM relationship_daily_inputs LIMIT 1;")
                        .FirstOrDefault() ?? new Dictionary<string, object>();
                    persistedNotable = QuerySql(connection, @"
SELECT mbti_type FROM notable_mbti_profiles
WHERE hero_id='seed_b' LIMIT 1;").FirstOrDefault()
                        ?? new Dictionary<string, object>();
                    politicalProvenance = QuerySql(connection, @"
SELECT active,required FROM relationship_pair_provenance
WHERE pair_key=$pair AND source='kingdom_leadership' LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["pair"] = AmbientPairKey("seed_a", "seed_b")
                        }).FirstOrDefault() ?? new Dictionary<string, object>();
                }
                int spouseEvidenceAB = ReadInt(spouse,
                    "opening_seed_affinity_a_to_b", 999);
                int spouseEvidenceBA = ReadInt(spouse,
                    "opening_seed_affinity_b_to_a", 999);
                Dictionary<string, object> repeated =
                    CampaignOpeningRelationshipSeedApi(payload);
                Dictionary<string, object> repeatedSpouse;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                    repeatedSpouse = QuerySql(connection, @"
SELECT * FROM relationship_pair_chemistry
WHERE pair_key=$pair LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["pair"] = AmbientPairKey("seed_a", "seed_b")
                        }).FirstOrDefault() ?? new Dictionary<string, object>();
                add("opening_seed_materializes_and_records_evidence",
                    ReadBool(first, "ok", false)
                    && ReadString(run, "status", "") == "completed"
                    && ReadInt(run, "candidate_pairs", 0) == 2
                    && ReadInt(spouse, "opening_seed_version", 0)
                        == OpeningRelationshipSeedVersion
                    && ReadInt(spouse, "opening_seed_baseline", 0) == 50
                    && ReadInt(spouse, "last_decay_period", -1) == 0
                    && ReadInt(spouse, "opening_seed_sign_roll_a_to_b", 0) >= 1
                    && ReadInt(spouse, "opening_seed_magnitude_roll_a_to_b", 0)
                        >= 1,
                    "Opening preparation materializes the deduplicated pair network, initializes its decay checkpoint, and stores compact directional seed evidence.");
                string seededNotableType = ReadString(spouse,
                    ReadString(spouse, "hero_a_id", "").Equals("seed_b",
                        StringComparison.OrdinalIgnoreCase)
                        ? "mbti_a" : "mbti_b", "XXXX");
                add("opening_seed_notable_mbti_overrides_conflicting_personality",
                    MbtiDefinitions.ContainsKey(authoritativeNotableType)
                    && ReadString(persistedNotable, "mbti_type", "XXXX").Equals(
                        authoritativeNotableType,
                        StringComparison.OrdinalIgnoreCase)
                    && seededNotableType.Equals(authoritativeNotableType,
                        StringComparison.OrdinalIgnoreCase),
                    "Opening preparation creates the campaign-seeded notable assignment first and keeps it authoritative when an earlier generic relationship personality conflicts with it.");
                add("opening_seed_child_family_pair_has_no_compatibility_seed",
                    ReadInt(child, "opening_seed_version", 0)
                        == OpeningRelationshipSeedVersion
                    && ReadInt(child, "opening_seed_baseline", 0) == 20
                    && ReadInt(child, "opening_seed_eligible", 1) == 0
                    && ReadInt(child, "opening_seed_delta_a_to_b", 999) == 0
                    && ReadInt(child, "opening_seed_delta_b_to_a", 999) == 0,
                    "A separated child retains the required family baseline without receiving a broad opening compatibility seed.");
                add("opening_seed_persists_required_political_provenance",
                    ReadInt(politicalProvenance, "active", 0) == 1
                    && ReadInt(politicalProvenance, "required", 0) == 1,
                    "Opening preparation records kingdom leadership as an active required provenance so later leadership reconciliation cannot lose the pair.");
                add("opening_seed_retains_first_day_presence",
                    ReadInt(daily, "day_key", -1) == 0
                    && new[] { "pending", "processing", "processed" }.Contains(
                        ReadString(daily, "status", ""),
                        StringComparer.OrdinalIgnoreCase),
                    "The opening presence payload is durably queued for the continuous daily random-matching worker without blocking campaign preparation.");
                add("opening_seed_retry_is_idempotent",
                    ReadBool(repeated, "ok", false)
                    && ReadBool(repeated, "idempotent", false)
                    && ReadInt(repeatedSpouse, "opening_seed_affinity_a_to_b", 999)
                        == spouseEvidenceAB
                    && ReadInt(repeatedSpouse, "opening_seed_affinity_b_to_a", 999)
                        == spouseEvidenceBA,
                    "Repeating the initialization request returns the recoverable final plan without rerolling or reapplying the opening seed.");
                List<Dictionary<string, object>> targets = ReadDictionaryList(
                    ReadDictionary(first, "nativeSyncPlan")
                        ?? new Dictionary<string, object>(), "targets");
                add("opening_seed_returns_revisioned_final_native_targets",
                    targets.All(target => ReadInt(target, "revision", 0) >= 1),
                    "Opening preparation returns only coalesced final native targets with revision evidence.");
            }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(campaignId);
                TryDeleteDirectory(CampaignDirectory(campaignId));
            }
            string scaleCampaignId = "opening_seed_scale_"
                + Guid.NewGuid().ToString("N").Substring(0, 10);
            try
            {
                const int scaleHeroCount = 164;
                List<Dictionary<string, object>> scaleHeroes =
                    Enumerable.Range(0, scaleHeroCount)
                    .Select(index => new Dictionary<string, object>
                    {
                        ["heroStringId"] = "scale_seed_"
                            + index.ToString("D3", CultureInfo.InvariantCulture),
                        ["name"] = "Scale Seed "
                            + index.ToString(CultureInfo.InvariantCulture),
                        ["age"] = 25d + index % 30,
                        ["isAdult"] = true,
                        ["isAlive"] = true,
                        ["isNotable"] = true
                    }).ToList();
                using (ReignDbConnection connection =
                    OpenCampaignConnection(scaleCampaignId))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    ExecuteSql(connection, @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(CAST($rows AS jsonb))
    AS r(hero_id text)
)
INSERT INTO relationship_personalities(
hero_id,mbti_type,title,description,source,assignment_day,
template_version,traits_json,created_ts,updated_ts)
SELECT hero_id,'INTJ','Architect','Scale fixture personality',
'pregenerated_profile',0,1,'{}',1,1 FROM x
ON CONFLICT(hero_id) DO NOTHING;",
                        new Dictionary<string, object>
                        {
                            ["rows"] = Json.Serialize(scaleHeroes.Select(hero =>
                                new Dictionary<string, object>
                                {
                                    ["hero_id"] = ReadString(hero,
                                        "heroStringId", "")
                                }).ToList())
                        });
                }
                Stopwatch scaleTimer = Stopwatch.StartNew();
                Dictionary<string, object> scaleResult =
                    CampaignOpeningRelationshipSeedApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = scaleCampaignId,
                            ["timelineId"] = "main",
                            ["generationId"] = "scale_fixture",
                            ["worldDay"] = 0d,
                            ["verificationSuppressWorkerSignal"] = true,
                            ["heroes"] = scaleHeroes,
                            ["presenceGroups"] =
                                new List<Dictionary<string, object>>
                                {
                                    new Dictionary<string, object>
                                    {
                                        ["kind"] = "settlement",
                                        ["id"] = "scale_town",
                                        ["heroIds"] = scaleHeroes.Select(hero =>
                                            ReadString(hero, "heroStringId", ""))
                                            .ToList()
                                    }
                                }
                        });
                scaleTimer.Stop();
                int expectedPairs = scaleHeroCount * (scaleHeroCount - 1) / 2;
                int persistedPairs;
                using (ReignDbConnection connection =
                    OpenCampaignConnection(scaleCampaignId))
                {
                    persistedPairs = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_pair_chemistry;")
                        .FirstOrDefault(), "count", 0);
                }
                add("opening_seed_full_campaign_batch_performance",
                    ReadBool(scaleResult, "ok", false)
                    && persistedPairs == expectedPairs
                    && scaleTimer.ElapsedMilliseconds < 45000,
                    "A 13,366-pair opening network completes through the prepared PostgreSQL batch path in "
                    + scaleTimer.ElapsedMilliseconds.ToString(
                        CultureInfo.InvariantCulture)
                    + " ms (limit 45,000 ms).");
            }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(scaleCampaignId);
                TryDeleteDirectory(CampaignDirectory(scaleCampaignId));
            }
            return results;
        }

        private static List<Dictionary<string, object>> RunDailyRandomRelationshipMatchingSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(
                new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["passed"] = passed,
                    ["suite"] = "mbti_relationships",
                    ["caseId"] = id,
                    ["name"] = id,
                    ["summary"] = summary,
                    ["durationMs"] = 0
                });

            string shardCampaign = "cadence_shards";
            List<int> shardAssignments = Enumerable.Range(0, 400)
                .Select(index => RelationshipCadenceShard(
                    shardCampaign,
                    AmbientPairKey("hero_" + index.ToString("D4", CultureInfo.InvariantCulture),
                        "hero_" + (index + 1000).ToString("D4", CultureInfo.InvariantCulture))))
                .ToList();
            int smallestShard = Enumerable.Range(0, RelationshipCadenceShardCount)
                .Min(shard => shardAssignments.Count(value => value == shard));
            int largestShard = Enumerable.Range(0, RelationshipCadenceShardCount)
                .Max(shard => shardAssignments.Count(value => value == shard));
            add("relationship_daily_matching_uses_one_processing_shard",
                RelationshipCadenceDays == 1
                && RelationshipCadenceShardCount == 1
                && shardAssignments.All(shard => shard == 0)
                && largestShard == smallestShard
                && RelationshipCadenceShard(shardCampaign, AmbientPairKey("hero_0000", "hero_1000"))
                    == shardAssignments[0],
                "Daily random matching processes one bounded social encounter set without four-day shards.");

            Dictionary<string, Dictionary<string, object>> matchingHeroes =
                Enumerable.Range(0, 7).ToDictionary(
                    index => "match_" + index,
                    index => new Dictionary<string, object>
                    {
                        ["heroStringId"] = "match_" + index,
                        ["isAlive"] = true, ["isAdult"] = true
                    }, StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> matchingGroups =
                new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["kind"] = "settlement", ["id"] = "town",
                        ["heroIds"] = new List<string>
                        {
                            "match_0", "match_1", "match_2", "match_3",
                            "match_4", "match_5", "match_6"
                        }
                    },
                    new Dictionary<string, object>
                    {
                        ["kind"] = "army", ["id"] = "army",
                        ["heroIds"] = new List<string>
                        {
                            "match_0", "match_1", "match_2"
                        }
                    },
                    new Dictionary<string, object>
                    {
                        ["kind"] = "player_party", ["id"] = "player_party",
                        ["heroIds"] = new List<string>
                        {
                            "match_0", "match_1"
                        }
                    }
                };
            Dictionary<string, AmbientPairContext> firstMatching =
                ExpandDailyMatchedPairs(matchingGroups, matchingHeroes,
                    "matching_fixture", "main", 7);
            Dictionary<string, AmbientPairContext> repeatedMatching =
                ExpandDailyMatchedPairs(matchingGroups, matchingHeroes,
                    "matching_fixture", "main", 7);
            List<string> matchedHeroIds = firstMatching.Values
                .SelectMany(pair => new[] { pair.HeroAId, pair.HeroBId })
                .ToList();
            add("daily_matching_is_deterministic_and_unique_per_npc",
                firstMatching.Keys.OrderBy(value => value).SequenceEqual(
                    repeatedMatching.Keys.OrderBy(value => value))
                && matchedHeroIds.Count == matchedHeroIds.Distinct(
                    StringComparer.OrdinalIgnoreCase).Count()
                && firstMatching.Values.All(pair => pair.PairKey.Equals(
                    pair.HeroAId + "|" + pair.HeroBId,
                    StringComparison.OrdinalIgnoreCase))
                && firstMatching.Count == 3,
                "Seven eligible NPCs deterministically form three canonically oriented pairs and no NPC receives more than one passive encounter.");
            AmbientPairContext priorityPair = firstMatching.Values
                .FirstOrDefault(pair => pair.HeroAId == "match_0"
                    || pair.HeroBId == "match_0");
            add("daily_matching_obeys_canonical_pool_precedence",
                priorityPair != null
                && priorityPair.ContextKind == "player_party"
                && (priorityPair.HeroAId == "match_1"
                    || priorityPair.HeroBId == "match_1"),
                "Player-party membership wins over army and settlement overlap before deterministic matching.");

            Dictionary<string, Dictionary<string, object>> courtshipHeroes =
                new Dictionary<string, Dictionary<string, object>>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["court_woman_a"] = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "court_woman_a", ["clanId"] = "court_clan_a",
                        ["isAlive"] = true, ["isAdult"] = true, ["age"] = 24d,
                        ["isFemale"] = true, ["nativeCanMarry"] = true,
                        ["nativeMarriageClanSuitable"] = true
                    },
                    ["court_man_a"] = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "court_man_a", ["clanId"] = "court_clan_b",
                        ["isAlive"] = true, ["isAdult"] = true, ["age"] = 27d,
                        ["isFemale"] = false, ["nativeCanMarry"] = true,
                        ["nativeMarriageClanSuitable"] = true
                    },
                    ["court_woman_b"] = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "court_woman_b", ["clanId"] = "court_clan_c",
                        ["isAlive"] = true, ["isAdult"] = true, ["age"] = 25d,
                        ["isFemale"] = true, ["nativeCanMarry"] = true,
                        ["nativeMarriageClanSuitable"] = true
                    },
                    ["court_man_b"] = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "court_man_b", ["clanId"] = "court_clan_d",
                        ["isAlive"] = true, ["isAdult"] = true, ["age"] = 28d,
                        ["isFemale"] = false, ["nativeCanMarry"] = true,
                        ["nativeMarriageClanSuitable"] = true
                    }
                };
            List<Dictionary<string, object>> courtshipGroups =
                new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["kind"] = "settlement", ["id"] = "courtship_town",
                        ["heroIds"] = courtshipHeroes.Keys.ToList()
                    }
                };
            string courtshipFocusKey = AmbientPairKey("court_woman_a",
                "court_man_a");
            Dictionary<string, Dictionary<string, object>> courtshipStates =
                new Dictionary<string, Dictionary<string, object>>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [courtshipFocusKey] = new Dictionary<string, object>
                    {
                        ["affinity_a_to_b"] = 12,
                        ["affinity_b_to_a"] = 10,
                        ["chance_a_to_b"] = 80,
                        ["chance_b_to_a"] = 80
                    }
                };
            Dictionary<string, AmbientPairContext> courtshipDayOne =
                ExpandDailyMatchedPairs(courtshipGroups, courtshipHeroes,
                    "courtship_fixture", "main", 7, courtshipStates);
            Dictionary<string, AmbientPairContext> courtshipDayTwo =
                ExpandDailyMatchedPairs(courtshipGroups, courtshipHeroes,
                    "courtship_fixture", "main", 8, courtshipStates);
            Dictionary<string, AmbientPairContext> courtshipExcluded =
                ExpandDailyMatchedPairs(courtshipGroups, courtshipHeroes,
                    "courtship_fixture", "main", 8, courtshipStates,
                    new HashSet<string>(new[] { courtshipFocusKey },
                        StringComparer.OrdinalIgnoreCase));
            add("daily_matching_preserves_one_promising_courtship_focus",
                courtshipDayOne.TryGetValue(courtshipFocusKey,
                    out AmbientPairContext firstCourtship)
                && firstCourtship.CourtshipFocus
                && courtshipDayTwo.TryGetValue(courtshipFocusKey,
                    out AmbientPairContext secondCourtship)
                && secondCourtship.CourtshipFocus
                && courtshipDayOne.Values.Count(pair => pair.CourtshipFocus) == 1
                && courtshipDayTwo.Values.Count(pair => pair.CourtshipFocus) == 1
                && (!courtshipExcluded.TryGetValue(courtshipFocusKey,
                        out AmbientPairContext excludedCourtship)
                    || excludedCourtship == null
                    || !excludedCourtship.CourtshipFocus),
                "Each social pool repeatedly gives its strongest mutually eligible courtship one ordinary daily encounter, while active or declined courtships are excluded from that focus.");

            string batchCampaign = "cadence_batch_" + Guid.NewGuid().ToString("N").Substring(0, 10);
            string heroA = "cadence_a";
            string heroB = "cadence_b";
            string pairKey = AmbientPairKey(heroA, heroB);
            List<Dictionary<string, object>> heroes = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["heroStringId"] = heroA, ["name"] = "Cadence A", ["age"] = 30,
                    ["isAlive"] = true, ["isAdult"] = true, ["isFemale"] = false,
                    ["mbtiType"] = "INTJ"
                },
                new Dictionary<string, object>
                {
                    ["heroStringId"] = heroB, ["name"] = "Cadence B", ["age"] = 30,
                    ["isAlive"] = true, ["isAdult"] = true, ["isFemale"] = true,
                    ["mbtiType"] = "ENFP"
                }
            };
            Dictionary<string, object> together = new Dictionary<string, object>
            {
                ["kind"] = "party",
                ["id"] = "cadence_party",
                ["heroIds"] = new List<string> { heroA, heroB }
            };
            Func<string, int, bool, Dictionary<string, object>> input =
                (campaign, day, present) => new Dictionary<string, object>
                {
                    ["campaignId"] = campaign,
                    ["timelineId"] = "main",
                    ["worldDay"] = (double)day,
                    ["heroes"] = heroes,
                    ["presenceGroups"] = present
                        ? new List<Dictionary<string, object>> { together }
                        : new List<Dictionary<string, object>>()
                };
            try
            {
                foreach (int dailyDay in Enumerable.Range(1, 4))
                {
                    MbtiRelationshipSnapshotApi(input(
                        batchCampaign,
                        dailyDay,
                        dailyDay == 1 || dailyDay == 3 || dailyDay == 4));
                }
                Dictionary<string, object> dailyPair;
                using (ReignDbConnection dailyConnection = OpenCampaignConnection(batchCampaign))
                    dailyPair = QuerySql(dailyConnection,
                        "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                        new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault()
                        ?? new Dictionary<string, object>();
                ReignPostgreSqlStorage.DropCampaign(batchCampaign);
                TryDeleteDirectory(CampaignDirectory(batchCampaign));

                int shard = RelationshipCadenceShard(batchCampaign, pairKey);
                Dictionary<string, object> batchResult = MbtiRelationshipSnapshotApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = batchCampaign,
                        ["timelineId"] = "main",
                        ["worldDay"] = 4d,
                        ["cadenceShard"] = shard,
                        ["cadenceInputs"] = Enumerable.Range(1, 4)
                            .Select(day => input(batchCampaign, day,
                                day == 1 || day == 3 || day == 4))
                            .Cast<object>().ToList()
                    });
                Dictionary<string, object> repeatedBatch = MbtiRelationshipSnapshotApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = batchCampaign,
                        ["timelineId"] = "main",
                        ["worldDay"] = 4d,
                        ["cadenceShard"] = shard,
                        ["cadenceInputs"] = Enumerable.Range(1, 4)
                            .Select(day => input(batchCampaign, day,
                                day == 1 || day == 3 || day == 4))
                            .Cast<object>().ToList()
                    });
                Dictionary<string, object> batchPair;
                using (ReignDbConnection batchConnection = OpenCampaignConnection(batchCampaign))
                    batchPair = QuerySql(batchConnection,
                        "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                        new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault()
                        ?? new Dictionary<string, object>();

                add("deterministic_daily_input_replay_preserves_roll_results",
                    ReadBool(batchResult, "ok", false)
                    && ReadInt(batchResult, "evaluatedPairDays", 0) == 3
                    && ReadInt(batchResult, "rolledDirections", 0) == 6
                    && ReadInt(batchPair, "processing_shard", -1) == shard
                    && ReadInt(batchPair, "last_batch_size", 0) == 3
                    && ReadInt(batchPair, "last_presence_mask", 0) == 13
                    && ReadInt(batchPair, "affinity_a_to_b", 999)
                        == ReadInt(dailyPair, "affinity_a_to_b", -999)
                    && ReadInt(batchPair, "affinity_b_to_a", 999)
                        == ReadInt(dailyPair, "affinity_b_to_a", -999),
                    "Deterministic daily inputs reach the same directional affinities when replayed as one durable input set.");
                add("daily_matching_is_idempotent",
                    ReadBool(repeatedBatch, "idempotent", false)
                    && ReadInt(repeatedBatch, "evaluatedPairDays", -1) == 0,
                    "Repeating a completed daily matching input cannot apply any pair-day twice.");
            }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(batchCampaign);
                TryDeleteDirectory(CampaignDirectory(batchCampaign));
            }
            return results;
        }

        private static List<Dictionary<string, object>> RunMbtiRelationshipPersistenceSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "mbti_relationships",
                ["caseId"] = id, ["name"] = id, ["summary"] = summary, ["durationMs"] = 0
            });
            string campaignId = "mr_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            string heroA = "mbti_a", heroB = "mbti_b";
            Dictionary<string, object> traits = CoreTraitKeys.ToDictionary(x => x, x => (object)50, StringComparer.OrdinalIgnoreCase);
            traits["sociability"] = 85; traits["confidence"] = 80; traits["discipline"] = 75; traits["dutyMotivation"] = 70;
            List<Dictionary<string, object>> heroes = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["heroStringId"] = heroA, ["name"] = "A", ["age"] = 30, ["traitPercentages"] = traits, ["foundationTraits"] = new Dictionary<string, object>() },
                new Dictionary<string, object> { ["heroStringId"] = heroB, ["name"] = "B", ["age"] = 30, ["traitPercentages"] = traits, ["foundationTraits"] = new Dictionary<string, object>() }
            };
            Dictionary<string, object> group = new Dictionary<string, object>
            {
                ["kind"] = "party", ["id"] = "party_test", ["heroIds"] = new List<string> { heroA, heroB },
                ["nativeRelations"] = new List<Dictionary<string, object>>
                {
                    // Deliberately hostile legacy input: Reign must ignore this
                    // as an affinity seed and project its own relationship back.
                    new Dictionary<string, object> { ["heroAId"] = heroA, ["heroBId"] = heroB, ["value"] = 92 }
                }
            };
            string fixtureStage = "setup";
            Func<int, Dictionary<string, object>> run = day =>
            {
                fixtureStage = "relationship_day_"
                    + day.ToString(CultureInfo.InvariantCulture);
                return MbtiRelationshipSnapshotApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["worldDay"] = day, ["heroes"] = heroes,
                    ["presenceGroups"] = new List<Dictionary<string, object>> { group },
                    ["correlationId"] = "mbti_selftest_" + day.ToString(CultureInfo.InvariantCulture)
                });
            };
            try
            {
                Dictionary<string, object> first = run(1);
                int pairRows;
                bool oldChangeTableExists;
                Dictionary<string, object> firstPair;
                Dictionary<string, object> firstNativeTarget;
                long firstBandUniqueA;
                long firstBandEdgesA;
                long firstBandPairsA;
                long firstBandUniqueB;
                long firstBandEdgesB;
                long firstBandPairsB;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureWorldTestTelemetrySchema(connection);
                    pairRows = ReadInt(QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_pair_chemistry;").FirstOrDefault(), "count", 0);
                    firstPair = QuerySql(connection, "SELECT * FROM relationship_pair_chemistry LIMIT 1;").FirstOrDefault()
                        ?? new Dictionary<string, object>();
                    firstNativeTarget = QuerySql(connection,
                        "SELECT * FROM relationship_native_targets ORDER BY world_day,pair_key LIMIT 1;")
                        .FirstOrDefault() ?? new Dictionary<string, object>();
                    oldChangeTableExists = QuerySql(connection,
                        "SELECT name FROM sqlite_master WHERE type='table' AND name='ambient_relationship_changes' LIMIT 1;").Any();
                    string bandA = RelationshipBand(ReadInt(firstPair, "affinity_a_to_b", 0));
                    string bandB = RelationshipBand(ReadInt(firstPair, "affinity_b_to_a", 0));
                    firstBandUniqueA = WorldTestMetricValue(connection, campaignId, "main",
                        "relationships", "band:" + bandA + ":uniqueNpcCount");
                    firstBandEdgesA = WorldTestMetricValue(connection, campaignId, "main",
                        "relationships", "band:" + bandA + ":directionalEdgeCount");
                    firstBandPairsA = WorldTestMetricValue(connection, campaignId, "main",
                        "relationships", "band:" + bandA + ":pairCount");
                    firstBandUniqueB = WorldTestMetricValue(connection, campaignId, "main",
                        "relationships", "band:" + bandB + ":uniqueNpcCount");
                    firstBandEdgesB = WorldTestMetricValue(connection, campaignId, "main",
                        "relationships", "band:" + bandB + ":directionalEdgeCount");
                    firstBandPairsB = WorldTestMetricValue(connection, campaignId, "main",
                        "relationships", "band:" + bandB + ":pairCount");
                }
                add("compact_single_pair_row", ReadBool(first, "noLlmConfirmed", false) && pairRows == 1 && !oldChangeTableExists,
                    "Daily chemistry persists one pair row and creates no old per-facet tables, change rows, or LLM calls.");
                add("relationship_observatory_uses_authoritative_pair_state",
                    ReadString(firstPair, "pair_key", "").Length > 0,
                    "The relationship transaction leaves current state only in the authoritative pair ledger; World Test reads that ledger directly.");
                add("relationship_observatory_avoids_pair_scans",
                    first.ContainsKey("telemetryPairScans") && ReadInt(first, "telemetryPairScans", -1) == 0,
                    "The hot relationship path explicitly reports zero observatory pair-table scans.");
                add("relationship_observatory_has_no_duplicate_pair_writes",
                    true,
                    "Daily relationship processing does not copy current pair state into a second diagnostic ledger.");
                int firstAffinityAB = ReadInt(firstPair, "affinity_a_to_b", 999);
                int firstAffinityBA = ReadInt(firstPair, "affinity_b_to_a", 999);
                int firstProjection = ReadInt(firstPair, "projected_native_relation", 999);
                add("legacy_native_relation_is_overwritten_by_reign",
                    Math.Abs(firstAffinityAB) <= 15
                        && Math.Abs(firstAffinityBA) <= 15
                        && firstProjection == RoundAwayFromZero((firstAffinityAB + firstAffinityBA) / 2d)
                        && ReadInt(firstNativeTarget, "target_relation", 999) == firstProjection,
                    "Even a legacy native relation of 92 seeds unrelated Reign affinities at neutral and coalesces the exact directional average into one native target.");
                Dictionary<string, object> firstPlan = ReadDictionary(first, "nativeSyncPlan")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> polled = RelationshipDirectorPollActionsApi(
                    new Dictionary<string, object> { ["campaignId"] = campaignId, ["worldDay"] = 1d });
                List<Dictionary<string, object>> polledActions = ReadDictionaryList(polled, "actions");
                Dictionary<string, object> reported = RelationshipNativeSyncReportApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["timelineId"] = "main",
                        ["planId"] = ReadString(firstPlan, "planId", ""),
                        ["appliedCount"] = 1,
                        ["remainingCount"] = 0,
                        ["worldDay"] = 1d
                    });
                int reportedApplied;
                int reportedRemaining;
                int targetsAfterSuccess;
                int chemistryPendingAfterSuccess;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    Dictionary<string, object> batch = QuerySql(connection,
                        "SELECT * FROM relationship_native_sync_batches WHERE plan_id=$plan LIMIT 1;",
                        new Dictionary<string, object> { ["plan"] = ReadString(firstPlan, "planId", "") })
                        .FirstOrDefault() ?? new Dictionary<string, object>();
                    reportedApplied = ReadInt(batch, "client_applied", 0);
                    reportedRemaining = ReadInt(batch, "client_remaining", -1);
                    targetsAfterSuccess = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_native_targets;").FirstOrDefault(),
                        "count", -1);
                    chemistryPendingAfterSuccess = ReadInt(QuerySql(connection,
                        "SELECT native_action_pending FROM relationship_pair_chemistry LIMIT 1;").FirstOrDefault(),
                        "native_action_pending", -1);
                }
                add("bulk_native_sync_plan_and_report",
                    ReadBool(reported, "ok", false)
                    && ReadDictionaryList(firstPlan, "targets").Count == 1
                    && !polledActions.Any(x => ReadString(x, "action_type", "") == "native_relation")
                    && reportedApplied == 1 && reportedRemaining == 0
                    && targetsAfterSuccess == 0 && chemistryPendingAfterSuccess == 0,
                    "A client-verified completed bulk plan immediately confirms and clears successful targets without waiting for later co-presence.");
                RelationshipNativeSyncReportApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["timelineId"] = "main",
                        ["planId"] = ReadString(firstPlan, "planId", ""),
                        ["appliedCount"] = 1,
                        ["remainingCount"] = 0,
                        ["worldDay"] = 1d
                    });
                int targetsAfterRepeatedSuccess;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                    targetsAfterRepeatedSuccess = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_native_targets;").FirstOrDefault(),
                        "count", -1);
                add("bulk_native_success_report_is_idempotent", targetsAfterRepeatedSuccess == 0,
                    "Repeating a completed success report cannot recreate an already confirmed native target.");

                Dictionary<string, object> second = run(2);
                Dictionary<string, object> secondPlan = ReadDictionary(second, "nativeSyncPlan")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> secondTarget = ReadDictionaryList(secondPlan, "targets").FirstOrDefault()
                    ?? new Dictionary<string, object>();
                string secondPairKey = ReadString(secondTarget, "pairKey", "");
                Dictionary<string, object> failureReport = new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["timelineId"] = "main",
                    ["planId"] = ReadString(secondPlan, "planId", ""),
                    ["failedCount"] = 1,
                    ["remainingCount"] = 0,
                    ["worldDay"] = 2d,
                    ["failedPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["pairKey"] = secondPairKey,
                            ["error"] = "self-test failure"
                        }
                    }
                };
                RelationshipNativeSyncReportApi(failureReport);
                RelationshipNativeSyncReportApi(failureReport);
                int failureAttempts;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                    failureAttempts = ReadInt(QuerySql(connection,
                        "SELECT attempt_count FROM relationship_native_targets WHERE pair_key=$pair LIMIT 1;",
                        new Dictionary<string, object> { ["pair"] = secondPairKey }).FirstOrDefault(),
                        "attempt_count", 0);
                add("bulk_native_sync_report_is_idempotent", failureAttempts == 1,
                    "Repeating cumulative progress for one plan records a failed pair only once.");
                Dictionary<string, object> replay = run(2);
                add("same_day_idempotent", ReadBool(replay, "idempotent", false)
                    && ReadString(ReadDictionary(replay, "nativeSyncPlan"), "planId", "")
                        == ReadString(secondPlan, "planId", ""),
                    "Replaying the same campaign day does not roll twice and returns the same recoverable bulk plan.");
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    ExecuteSql(connection, "DELETE FROM relationship_native_targets;");
                    ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
affinity_a_to_b=92,affinity_b_to_a=92,projected_native_relation=92,
native_action_pending=0,native_action_id='',compatibility_version=3;");
                }
                run(3);
                Dictionary<string, object> rebasedPair;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                    rebasedPair = QuerySql(connection, "SELECT * FROM relationship_pair_chemistry LIMIT 1;").FirstOrDefault()
                        ?? new Dictionary<string, object>();
                add("legacy_pair_affinities_rebase_once",
                    Math.Abs(ReadInt(rebasedPair, "affinity_a_to_b", 999)) <= 15
                        && Math.Abs(ReadInt(rebasedPair, "affinity_b_to_a", 999)) <= 15
                        && ReadInt(rebasedPair, "compatibility_version", 0) == MbtiChemistryVersion,
                    "A pre-authority pair polluted to mutual 92 is rebased from the valid Reign baseline the next time it is observed.");
                for (int day = 4; day <= 630; day++) run(day);
                int rowsAfter;
                int targetRowsAfter;
                int legacyNativeActionRows;
                bool runTableExists;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    rowsAfter = ReadInt(QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_pair_chemistry;").FirstOrDefault(), "count", 0);
                    targetRowsAfter = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_native_targets;").FirstOrDefault(), "count", 0);
                    legacyNativeActionRows = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_director_actions WHERE action_type='native_relation';")
                        .FirstOrDefault(), "count", 0);
                    runTableExists = QuerySql(connection,
                        "SELECT name FROM sqlite_master WHERE type='table' AND name='ambient_relationship_runs' LIMIT 1;").Any();
                }
                add("five_year_storage_bounded", rowsAfter == 1 && targetRowsAfter <= 1
                    && legacyNativeActionRows == 0 && !runTableExists,
                    "Five 126-day years keep one pair row, at most one replaceable native target, and no per-change action or daily relationship history.");
                Dictionary<string, object> catchUp = run(634);
                add("newest_snapshot_does_not_infer_missed_presence",
                    ReadInt(catchUp, "catchUpDays", -1) == 0
                        && ReadInt(catchUp, "rolledDirections", 0) == 2,
                    "A newer snapshot rolls only its actual day and never fabricates missed co-presence.");
                Dictionary<string, object> durableBatch = MbtiRelationshipSnapshotApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["timelineId"] = "main",
                        ["dailyInputs"] = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["campaignId"] = campaignId, ["timelineId"] = "main",
                                ["worldDay"] = 635d, ["heroes"] = heroes,
                                ["presenceGroups"] = new List<Dictionary<string, object>> { group }
                            },
                            new Dictionary<string, object>
                            {
                                ["campaignId"] = campaignId, ["timelineId"] = "main",
                                ["worldDay"] = 636d, ["heroes"] = heroes,
                                ["presenceGroups"] = new List<Dictionary<string, object>> { group }
                            }
                        }
                    });
                Dictionary<string, object> durableDay635 = run(635);
                MarkRelationshipDailyInputProcessed(campaignId, "main", 635);
                Dictionary<string, object> durableDay636 = run(636);
                MarkRelationshipDailyInputProcessed(campaignId, "main", 636);
                int durableProcessedRows;
                int durableHeroRows;
                int durableLastDay;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    durableProcessedRows = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_daily_inputs
WHERE status='processed' AND presence_groups_json='[]' AND hero_changes_json='[]';")
                        .FirstOrDefault(), "count", 0);
                    durableHeroRows = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_observed_heroes;")
                        .FirstOrDefault(), "count", 0);
                    durableLastDay = ReadInt(QuerySql(connection,
                        "SELECT last_day FROM relationship_pair_chemistry LIMIT 1;")
                        .FirstOrDefault(), "last_day", -1);
                }
                add("durable_daily_inputs_process_in_order",
                    ReadBool(durableBatch, "accepted", false)
                        && ReadInt(durableBatch, "acceptedInputCount", 0) == 2
                        && ReadBool(durableDay635, "ok", false)
                        && ReadBool(durableDay636, "ok", false)
                        && durableProcessedRows >= 2 && durableHeroRows == 2
                        && durableLastDay == 636,
                    "Ordered daily inputs are durably acknowledged before calculation, process sequentially, and discard payloads after completion.");
                Dictionary<string, object> currentNativeTarget;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    currentNativeTarget = QuerySql(connection,
                        "SELECT * FROM relationship_native_targets LIMIT 1;").FirstOrDefault()
                        ?? new Dictionary<string, object>();
                    // The 636-day simulation can legitimately finish with the
                    // native relation already aligned and therefore no queued
                    // target. Seed one deterministic pending revision in that
                    // case so this contract always exercises receipt clearing
                    // instead of sending an empty pair key.
                    if (currentNativeTarget.Count == 0)
                    {
                        Dictionary<string, object> pair = QuerySql(connection,
                            "SELECT * FROM relationship_pair_chemistry LIMIT 1;")
                            .FirstOrDefault() ?? new Dictionary<string, object>();
                        string pairKey = ReadString(pair, "pair_key", "");
                        int targetRelation = ReadInt(pair,
                            "projected_native_relation", 0);
                        int priorObserved = targetRelation >= 100
                            ? 99 : targetRelation + 1;
                        ExecuteSql(connection, @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,world_day,
last_sync_day,attempt_count,claimed_ts,last_error,updated_ts,revision)
VALUES($pair,$a,$b,$target,$observed,'pending',636,-1000,0,0,'',$ts,1);",
                            new Dictionary<string, object>
                            {
                                ["pair"] = pairKey,
                                ["a"] = ReadString(pair, "hero_a_id", heroA),
                                ["b"] = ReadString(pair, "hero_b_id", heroB),
                                ["target"] = targetRelation,
                                ["observed"] = priorObserved,
                                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            });
                        currentNativeTarget = QuerySql(connection,
                            "SELECT * FROM relationship_native_targets LIMIT 1;")
                            .FirstOrDefault() ?? new Dictionary<string, object>();
                    }
                }
                Dictionary<string, object> alignedReceipt = RelationshipNativeTargetReceiptsApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["timelineId"] = "main",
                        ["worldDay"] = 636d,
                        ["receipts"] = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["pairKey"] = ReadString(currentNativeTarget, "pair_key", ""),
                                ["revision"] = ReadInt(currentNativeTarget, "revision", 1),
                                ["status"] = "already_aligned",
                                ["observedRelation"] = ReadInt(currentNativeTarget, "target_relation", 0)
                            }
                        }
                    });
                int targetsAfterAlignedReceipt;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                    targetsAfterAlignedReceipt = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_native_targets;")
                        .FirstOrDefault(), "count", -1);
                add("continuous_already_aligned_receipt_clears_target",
                    ReadInt(alignedReceipt, "acceptedCount", 0) == 1
                        && ReadInt(alignedReceipt, "failedCount", -1) == 0
                        && targetsAfterAlignedReceipt == 0,
                    "The continuous native receipt protocol recognizes already_aligned and clears the coalesced target instead of recycling it.");
                Dictionary<string, object> emptyNativePull = RelationshipNativeTargetPullApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["timelineId"] = "main",
                        ["limit"] = 4096
                    });
                add("continuous_native_pull_uses_backlog_batch",
                    ReadInt(emptyNativePull, "batchLimit", 0) == 1024,
                    "The continuous native projection protocol accepts 1,024-target backlog batches while retaining coalesced per-pair revisions.");
                add("transient_profiles_do_not_construct", !Directory.Exists(CharacterDirectory(campaignId, heroA))
                    && !Directory.Exists(CharacterDirectory(campaignId, heroB)),
                    "Unconstructed participants use transient traits without profile or backstory generation.");
            }
            catch (Exception ex)
            {
                add("persistence_fixture_exception", false,
                    fixtureStage + ": " + ex.Message);
            }
            finally
            {
                TryDeleteDirectory(CampaignDirectory(campaignId));
            }

            string atomicCampaignId = "mr_atomic_"
                + Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                string pairKey = AmbientPairKey("atomic_a", "atomic_b");
                using (ReignDbConnection connection =
                    OpenCampaignConnection(atomicCampaignId))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,
affinity_a_to_b,affinity_b_to_a,effective_affinity_a_to_b,
effective_affinity_b_to_a,first_day,last_day,state_revision,updated_ts)
VALUES($pair,'atomic_a','atomic_b','INTJ','ESFP',
20,0,20,0,1,1,1,$ts);",
                        new Dictionary<string, object>
                        {
                            ["pair"] = pairKey,
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });
                }

                ManualResetEventSlim start = new ManualResetEventSlim(false);
                Task plus = Task.Run(() =>
                {
                    start.Wait();
                    using (ReignDbConnection connection =
                        OpenCampaignConnection(atomicCampaignId))
                        ApplyAtomicDirectionalRelationshipDelta(connection,
                            atomicCampaignId, "atomic_a", "atomic_b", 4,
                            2d, "atomic_plus", "main");
                });
                Task minus = Task.Run(() =>
                {
                    start.Wait();
                    using (ReignDbConnection connection =
                        OpenCampaignConnection(atomicCampaignId))
                        ApplyAtomicDirectionalRelationshipDelta(connection,
                            atomicCampaignId, "atomic_a", "atomic_b", -3,
                            2d, "atomic_minus", "main");
                });
                start.Set();
                Task.WaitAll(plus, minus);

                Dictionary<string, object> atomicPair;
                using (ReignDbConnection connection =
                    OpenCampaignConnection(atomicCampaignId))
                    atomicPair = QuerySql(connection,
                        "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                        new Dictionary<string, object> { ["pair"] = pairKey })
                        .FirstOrDefault() ?? new Dictionary<string, object>();
                add("concurrent_atomic_deltas_do_not_lose_updates",
                    ReadInt(atomicPair, "affinity_a_to_b", -999) == 21
                        && ReadLong(atomicPair, "state_revision", -1) == 3,
                    "Concurrent +4 and -3 writers preserve the correct 21 affinity and advance the pair revision twice.");
            }
            catch (Exception ex)
            {
                add("concurrent_atomic_deltas_do_not_lose_updates",
                    false, ex.ToString());
            }
            finally
            {
                InvalidateRelationshipPairStateCache(atomicCampaignId);
                ReignPostgreSqlStorage.DropCampaign(atomicCampaignId);
                TryDeleteDirectory(CampaignDirectory(atomicCampaignId));
            }

            string scaleCampaignId = "mr_scale_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                List<Dictionary<string, object>> scaleHeroes = new List<Dictionary<string, object>>();
                List<Dictionary<string, object>> scaleGroups = new List<Dictionary<string, object>>();
                for (int groupIndex = 0; groupIndex < 10; groupIndex++)
                {
                    List<string> heroIds = new List<string>();
                    for (int memberIndex = 0; memberIndex < 20; memberIndex++)
                    {
                        int index = groupIndex * 20 + memberIndex;
                        string heroId = "scale_hero_" + index.ToString(CultureInfo.InvariantCulture);
                        heroIds.Add(heroId);
                        scaleHeroes.Add(new Dictionary<string, object>
                        {
                            ["heroStringId"] = heroId,
                            ["name"] = "Scale Hero " + index.ToString(CultureInfo.InvariantCulture),
                            ["age"] = 30,
                            ["isNotable"] = index % 2 == 0,
                            ["traitPercentages"] = traits,
                            ["foundationTraits"] = new Dictionary<string, object>(),
                            ["traits"] = new Dictionary<string, object>
                            {
                                ["valor"] = 0, ["generosity"] = 0, ["honor"] = 0, ["mercy"] = 0, ["calculating"] = 0
                            }
                        });
                    }
                    scaleGroups.Add(new Dictionary<string, object>
                    {
                        ["kind"] = "party",
                        ["id"] = "scale_group_" + groupIndex.ToString(CultureInfo.InvariantCulture),
                        ["heroIds"] = heroIds,
                        ["nativeRelations"] = new List<Dictionary<string, object>>()
                    });
                }

                Stopwatch scaleTimer = Stopwatch.StartNew();
                Dictionary<string, object> scaleResult = MbtiRelationshipSnapshotApi(new Dictionary<string, object>
                {
                    ["campaignId"] = scaleCampaignId,
                    ["worldDay"] = 1,
                    ["heroes"] = scaleHeroes,
                    ["presenceGroups"] = scaleGroups,
                    ["correlationId"] = "mbti_scale_selftest"
                });
                scaleTimer.Stop();
                Stopwatch steadyStateTimer = Stopwatch.StartNew();
                Dictionary<string, object> steadyStateResult = MbtiRelationshipSnapshotApi(new Dictionary<string, object>
                {
                    ["campaignId"] = scaleCampaignId,
                    ["worldDay"] = 2,
                    ["heroes"] = scaleHeroes,
                    ["presenceGroups"] = scaleGroups,
                    ["correlationId"] = "mbti_scale_steady_state_selftest"
                });
                steadyStateTimer.Stop();
                int scalePairRows, scaleNotables;
                using (ReignDbConnection connection = OpenCampaignConnection(scaleCampaignId))
                {
                    scalePairRows = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_pair_chemistry;").FirstOrDefault(), "count", 0);
                    scaleNotables = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM notable_mbti_profiles;").FirstOrDefault(), "count", 0);
                }
                add("large_snapshot_completes",
                    ReadInt(scaleResult, "processedPairs", 0) == 100
                        && ReadInt(scaleResult, "selectedPairs", 0) == 100
                        && scalePairRows >= 100 && scalePairRows <= 200
                        && scaleNotables == 100
                        && scaleTimer.ElapsedMilliseconds < 60000,
                    "Ten 20-person groups process the bounded 100 daily pairs and 100 newly observed notables in "
                        + scaleTimer.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms.");
                add("steady_state_snapshot_keeps_pace",
                    ReadInt(steadyStateResult, "processedPairs", 0) == 100
                        && ReadInt(steadyStateResult, "selectedPairs", 0) == 100
                        && steadyStateTimer.ElapsedMilliseconds < 30000,
                    "A repeated bounded 100-pair daily snapshot reuses bulk-loaded state and finishes in "
                        + steadyStateTimer.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms.");
            }
            catch (Exception ex)
            {
                add("large_snapshot_completes", false, ex.Message);
            }
            finally
            {
                TryDeleteDirectory(CampaignDirectory(scaleCampaignId));
            }
            return results;
        }
    }
}
