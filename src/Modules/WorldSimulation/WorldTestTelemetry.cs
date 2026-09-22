using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int PostgreSqlWorldTestTelemetrySchemaRevision = 1;

        private static void EnsureWorldTestTelemetrySchema(ReignDbConnection connection)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            const string marker =
                "postgresql_world_test_telemetry_schema_revision";
            if (IsPostgreSqlComponentSchemaReady(connection, marker,
                PostgreSqlWorldTestTelemetrySchemaRevision))
                return;

            EnsureWorldTestTelemetrySchemaCore(connection);
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                ExecuteSql(connection, @"
INSERT INTO schema_meta(key,value)
VALUES('postgresql_world_test_telemetry_schema_revision',$revision)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                    new Dictionary<string, object>
                    {
                        ["revision"] =
                            PostgreSqlWorldTestTelemetrySchemaRevision.ToString()
                    });
                MarkPostgreSqlComponentSchemaReady(connection, marker,
                    PostgreSqlWorldTestTelemetrySchemaRevision);
            }
        }

        private static void EnsureWorldTestTelemetrySchemaCore(
            ReignDbConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_manifests (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,manifest_json TEXT NOT NULL DEFAULT '{}',
first_observed_day REAL NOT NULL DEFAULT 0,latest_observed_day REAL NOT NULL DEFAULT 0,
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_chunk_counters (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,
subsystem TEXT NOT NULL,chunk_key TEXT NOT NULL,counters_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,day_key,subsystem,chunk_key));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_current_metrics (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,metric_scope TEXT NOT NULL,
metric_key TEXT NOT NULL,metric_value INTEGER NOT NULL DEFAULT 0,
updated_day INTEGER NOT NULL DEFAULT 0,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,metric_scope,metric_key));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_relationship_memberships (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,category_type TEXT NOT NULL,
category_key TEXT NOT NULL,hero_id TEXT NOT NULL,edge_count INTEGER NOT NULL DEFAULT 0,
directional_edge_count INTEGER NOT NULL DEFAULT 0,updated_day INTEGER NOT NULL DEFAULT 0,
updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,category_type,category_key,hero_id));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_relationship_pair_memberships (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,category_type TEXT NOT NULL,
category_key TEXT NOT NULL,pair_key TEXT NOT NULL,edge_count INTEGER NOT NULL DEFAULT 0,
directional_edge_count INTEGER NOT NULL DEFAULT 0,updated_day INTEGER NOT NULL DEFAULT 0,
updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,category_type,category_key,pair_key));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_relationship_pairs (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,pair_key TEXT NOT NULL,
hero_a_id TEXT NOT NULL DEFAULT '',hero_b_id TEXT NOT NULL DEFAULT '',
affinity_a_to_b INTEGER NOT NULL DEFAULT 0,affinity_b_to_a INTEGER NOT NULL DEFAULT 0,
tag_a_to_b TEXT NOT NULL DEFAULT '',tag_b_to_a TEXT NOT NULL DEFAULT '',
shared_tag TEXT NOT NULL DEFAULT '',mbti_a TEXT NOT NULL DEFAULT '',
mbti_b TEXT NOT NULL DEFAULT '',notable_a INTEGER NOT NULL DEFAULT 0,
notable_b INTEGER NOT NULL DEFAULT 0,last_delta_a_to_b INTEGER NOT NULL DEFAULT 0,
last_delta_b_to_a INTEGER NOT NULL DEFAULT 0,last_context_kind TEXT NOT NULL DEFAULT '',
first_day INTEGER NOT NULL DEFAULT 0,last_day INTEGER NOT NULL DEFAULT 0,
updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,pair_key));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_diagnostic_evidence (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,
subsystem TEXT NOT NULL,evidence_key TEXT NOT NULL,severity TEXT NOT NULL DEFAULT 'info',
payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,subsystem,evidence_key));");
            ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_world_test_evidence_day
ON world_test_diagnostic_evidence(campaign_id,timeline_id,subsystem,day_key);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_rollup_queue (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,
status TEXT NOT NULL DEFAULT 'pending',revision INTEGER NOT NULL DEFAULT 1,
reason TEXT NOT NULL DEFAULT '',attempt_count INTEGER NOT NULL DEFAULT 0,
last_error TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,day_key));");
            ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_world_test_rollup_queue_status
ON world_test_rollup_queue(status,updated_ts);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_checkpoint_reports (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,checkpoint_day REAL NOT NULL,
report_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,checkpoint_day));");
        }

        private static void RecordWorldTestCounter(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            int dayKey,
            string subsystem,
            string chunkKey,
            Dictionary<string, object> counters)
        {
            EnsureWorldTestTelemetrySchema(connection);
            RecordWorldTestCounterSchemaReady(connection, campaignId,
                timelineId, dayKey, subsystem, chunkKey, counters);
        }

        private static void RecordWorldTestCounterSchemaReady(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            int dayKey,
            string subsystem,
            string chunkKey,
            Dictionary<string, object> counters)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ExecuteSql(connection, @"INSERT INTO world_test_chunk_counters(
campaign_id,timeline_id,day_key,subsystem,chunk_key,counters_json,created_ts,updated_ts)
VALUES($campaign,$timeline,$day,$subsystem,$chunk,$counters,$ts,$ts)
ON CONFLICT(campaign_id,timeline_id,day_key,subsystem,chunk_key) DO UPDATE SET
counters_json=$counters,updated_ts=$ts;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId ?? string.Empty,
                    ["timeline"] = string.IsNullOrWhiteSpace(timelineId) ? "main" : timelineId,
                    ["day"] = dayKey,
                    ["subsystem"] = subsystem ?? string.Empty,
                    ["chunk"] = chunkKey ?? string.Empty,
                    ["counters"] = Json.Serialize(counters ?? new Dictionary<string, object>()),
                    ["ts"] = now
                });
            SignalWorldTestRollupWorker();
        }

        private static void RecordWorldTestEvidence(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            int dayKey,
            string subsystem,
            string evidenceKey,
            string severity,
            Dictionary<string, object> payload)
        {
            EnsureWorldTestTelemetrySchema(connection);
            campaignId = campaignId ?? string.Empty;
            timelineId = string.IsNullOrWhiteSpace(timelineId) ? "main" : timelineId;
            subsystem = subsystem ?? string.Empty;
            evidenceKey = evidenceKey ?? string.Empty;
            severity = (severity ?? "info").Trim().ToLowerInvariant();
            bool important = severity == "warning" || severity == "error";
            Dictionary<string, object> parameters = new Dictionary<string, object>
            {
                ["campaign"] = campaignId,
                ["timeline"] = timelineId,
                ["day"] = dayKey,
                ["subsystem"] = subsystem,
                ["key"] = evidenceKey
            };
            bool exists = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM world_test_diagnostic_evidence
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subsystem=$subsystem
AND evidence_key=$key;", parameters).FirstOrDefault(), "count", 0) > 0;
            if (!important && !exists)
            {
                int ordinaryCount = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM world_test_diagnostic_evidence
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subsystem=$subsystem
AND day_key=$day AND severity NOT IN ('warning','error');", parameters).FirstOrDefault(), "count", 0);
                if (ordinaryCount >= 100) return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            parameters["severity"] = severity;
            parameters["payload"] = Json.Serialize(payload ?? new Dictionary<string, object>());
            parameters["ts"] = now;
            ExecuteSql(connection, @"INSERT INTO world_test_diagnostic_evidence(
campaign_id,timeline_id,day_key,subsystem,evidence_key,severity,payload_json,created_ts,updated_ts)
VALUES($campaign,$timeline,$day,$subsystem,$key,$severity,$payload,$ts,$ts)
ON CONFLICT(campaign_id,timeline_id,subsystem,evidence_key) DO UPDATE SET
day_key=$day,severity=$severity,payload_json=$payload,updated_ts=$ts;", parameters);
        }

        private static void ApplyWorldTestRelationshipPairDelta(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            int dayKey,
            Dictionary<string, object> before,
            Dictionary<string, object> after,
            Dictionary<string, object> chunkCounters)
        {
            EnsureWorldTestTelemetrySchema(connection);
            campaignId = campaignId ?? string.Empty;
            timelineId = string.IsNullOrWhiteSpace(timelineId) ? "main" : timelineId;
            string materializedPairKey = ReadString(after, "pairKey",
                ReadString(after, "pair_key",
                    ReadString(before, "pairKey", ReadString(before, "pair_key", ""))));
            bool pairAlreadyMaterialized = !string.IsNullOrWhiteSpace(materializedPairKey)
                && ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM world_test_relationship_pairs
WHERE campaign_id=$campaign AND timeline_id=$timeline AND pair_key=$pair;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId,
                        ["timeline"] = timelineId,
                        ["pair"] = materializedPairKey
                    }).FirstOrDefault(), "count", 0) > 0;
            Dictionary<string, WorldTestRelationshipMembership> beforeMemberships =
                pairAlreadyMaterialized
                    ? BuildWorldTestRelationshipMemberships(before)
                    : new Dictionary<string, WorldTestRelationshipMembership>(
                        StringComparer.OrdinalIgnoreCase);
            Dictionary<string, WorldTestRelationshipMembership> afterMemberships =
                BuildWorldTestRelationshipMemberships(after);
            HashSet<string> keys = new HashSet<string>(
                beforeMemberships.Keys.Concat(afterMemberships.Keys), StringComparer.OrdinalIgnoreCase);
            foreach (string key in keys)
            {
                WorldTestRelationshipMembership oldMembership =
                    beforeMemberships.TryGetValue(key, out WorldTestRelationshipMembership oldValue)
                        ? oldValue : null;
                WorldTestRelationshipMembership newMembership =
                    afterMemberships.TryGetValue(key, out WorldTestRelationshipMembership newValue)
                        ? newValue : null;
                WorldTestRelationshipMembership descriptor = newMembership ?? oldMembership;
                int oldCount = oldMembership?.Count ?? 0;
                int newCount = newMembership?.Count ?? 0;
                int oldDirectional = oldMembership?.DirectionalCount ?? 0;
                int newDirectional = newMembership?.DirectionalCount ?? 0;
                int countDelta = newCount - oldCount;
                int directionalDelta = newDirectional - oldDirectional;
                if (countDelta == 0 && directionalDelta == 0) continue;

                ApplyWorldTestHeroMembershipDelta(
                    connection, campaignId, timelineId, dayKey, descriptor,
                    countDelta, directionalDelta);
                ApplyWorldTestPairMembershipDelta(
                    connection, campaignId, timelineId, dayKey, descriptor,
                    countDelta, directionalDelta);
                if (oldCount == 0 && newCount > 0)
                    IncrementWorldTestObjectCounter(
                        chunkCounters, descriptor.CategoryType + "Entries:" + descriptor.CategoryKey, 1);
                else if (oldCount > 0 && newCount == 0)
                    IncrementWorldTestObjectCounter(
                        chunkCounters, descriptor.CategoryType + "Exits:" + descriptor.CategoryKey, 1);
            }
            ReplaceWorldTestRelationshipPair(
                connection, campaignId, timelineId, dayKey, before, after);
        }

        private static void ReplaceWorldTestRelationshipPair(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            int dayKey,
            Dictionary<string, object> before,
            Dictionary<string, object> after)
        {
            Dictionary<string, object> state = after ?? new Dictionary<string, object>();
            string pairKey = ReadString(state, "pairKey", ReadString(state, "pair_key", ""));
            if (string.IsNullOrWhiteSpace(pairKey))
            {
                pairKey = ReadString(before, "pairKey", ReadString(before, "pair_key", ""));
                if (!string.IsNullOrWhiteSpace(pairKey))
                {
                    ExecuteSql(connection, @"DELETE FROM world_test_relationship_pairs
WHERE campaign_id=$campaign AND timeline_id=$timeline AND pair_key=$pair;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId,
                            ["timeline"] = timelineId,
                            ["pair"] = pairKey
                        });
                }
                return;
            }
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ExecuteSql(connection, @"INSERT INTO world_test_relationship_pairs(
campaign_id,timeline_id,pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,
tag_a_to_b,tag_b_to_a,shared_tag,mbti_a,mbti_b,notable_a,notable_b,
last_delta_a_to_b,last_delta_b_to_a,last_context_kind,first_day,last_day,updated_ts)
VALUES($campaign,$timeline,$pair,$a,$b,$affinityAB,$affinityBA,$tagAB,$tagBA,$shared,
$mbtiA,$mbtiB,$notableA,$notableB,$deltaAB,$deltaBA,$context,$first,$last,$ts)
ON CONFLICT(campaign_id,timeline_id,pair_key) DO UPDATE SET
hero_a_id=$a,hero_b_id=$b,affinity_a_to_b=$affinityAB,affinity_b_to_a=$affinityBA,
tag_a_to_b=$tagAB,tag_b_to_a=$tagBA,shared_tag=$shared,mbti_a=$mbtiA,mbti_b=$mbtiB,
notable_a=$notableA,notable_b=$notableB,last_delta_a_to_b=$deltaAB,
last_delta_b_to_a=$deltaBA,last_context_kind=$context,last_day=$last,updated_ts=$ts;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId,
                    ["pair"] = pairKey,
                    ["a"] = ReadString(state, "heroAId", ReadString(state, "hero_a_id", "")),
                    ["b"] = ReadString(state, "heroBId", ReadString(state, "hero_b_id", "")),
                    ["affinityAB"] = ReadInt(state, "affinityAToB", ReadInt(state, "affinity_a_to_b", 0)),
                    ["affinityBA"] = ReadInt(state, "affinityBToA", ReadInt(state, "affinity_b_to_a", 0)),
                    ["tagAB"] = ReadString(state, "tagAToB", ReadString(state, "tag_a_to_b", "")),
                    ["tagBA"] = ReadString(state, "tagBToA", ReadString(state, "tag_b_to_a", "")),
                    ["shared"] = ReadString(state, "sharedTag", ReadString(state, "shared_tag", "")),
                    ["mbtiA"] = ReadString(state, "mbtiA", ReadString(state, "mbti_a", "")),
                    ["mbtiB"] = ReadString(state, "mbtiB", ReadString(state, "mbti_b", "")),
                    ["notableA"] = ReadBool(state, "notableA", false) ? 1 : 0,
                    ["notableB"] = ReadBool(state, "notableB", false) ? 1 : 0,
                    ["deltaAB"] = ReadInt(state, "deltaAToB", ReadInt(state, "last_delta_a_to_b", 0)),
                    ["deltaBA"] = ReadInt(state, "deltaBToA", ReadInt(state, "last_delta_b_to_a", 0)),
                    ["context"] = ReadString(state, "contextKind", ReadString(state, "last_context_kind", "")),
                    ["first"] = ReadInt(state, "firstDay", ReadInt(state, "first_day", dayKey)),
                    ["last"] = dayKey,
                    ["ts"] = now
                });
        }

        private static void EnqueueWorldTestRollup(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            int dayKey,
            string reason)
        {
            EnsureWorldTestTelemetrySchema(connection);
            InvalidateWorldTestOverviewCache(campaignId,
                string.IsNullOrWhiteSpace(timelineId) ? "main" : timelineId);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ExecuteSql(connection, @"INSERT INTO world_test_rollup_queue(
campaign_id,timeline_id,day_key,status,revision,reason,attempt_count,last_error,created_ts,updated_ts)
VALUES($campaign,$timeline,$day,'pending',1,$reason,0,'',$ts,$ts)
ON CONFLICT(campaign_id,timeline_id,day_key) DO UPDATE SET
status='pending',revision=world_test_rollup_queue.revision+1,
reason=$reason,last_error='',updated_ts=$ts;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId ?? string.Empty,
                    ["timeline"] = string.IsNullOrWhiteSpace(timelineId) ? "main" : timelineId,
                    ["day"] = dayKey,
                    ["reason"] = reason ?? string.Empty,
                    ["ts"] = now
                });
        }

        private sealed class WorldTestRelationshipMembership
        {
            internal string CategoryType;
            internal string CategoryKey;
            internal string HeroId;
            internal string PairKey;
            internal int Count;
            internal int DirectionalCount;
        }

        private static Dictionary<string, WorldTestRelationshipMembership>
            BuildWorldTestRelationshipMemberships(Dictionary<string, object> state)
        {
            Dictionary<string, WorldTestRelationshipMembership> memberships =
                new Dictionary<string, WorldTestRelationshipMembership>(StringComparer.OrdinalIgnoreCase);
            if (state == null || state.Count == 0) return memberships;
            string heroA = ReadString(state, "heroAId", ReadString(state, "hero_a_id", ""));
            string heroB = ReadString(state, "heroBId", ReadString(state, "hero_b_id", ""));
            string pairKey = ReadString(state, "pairKey", ReadString(state, "pair_key", ""));
            if (string.IsNullOrWhiteSpace(pairKey))
                pairKey = string.Compare(heroA, heroB, StringComparison.OrdinalIgnoreCase) <= 0
                    ? heroA + "|" + heroB : heroB + "|" + heroA;
            if (!string.IsNullOrWhiteSpace(heroA))
            {
                AddWorldTestRelationshipMembership(
                    memberships, "band",
                    RelationshipBand(ReadInt(state, "affinityAToB", ReadInt(state, "affinity_a_to_b", 0))),
                    heroA, pairKey, true);
                AddWorldTestRelationshipMembership(
                    memberships, "affinity",
                    ReadInt(state, "affinityAToB", ReadInt(state, "affinity_a_to_b", 0))
                        .ToString(System.Globalization.CultureInfo.InvariantCulture),
                    heroA, pairKey, true);
                foreach (string tag in ReadWorldTestTags(state, "tagAToB", "tag_a_to_b", "tagsAToB"))
                    AddWorldTestRelationshipMembership(memberships, "tag", tag, heroA, pairKey, true);
            }
            if (!string.IsNullOrWhiteSpace(heroB))
            {
                AddWorldTestRelationshipMembership(
                    memberships, "band",
                    RelationshipBand(ReadInt(state, "affinityBToA", ReadInt(state, "affinity_b_to_a", 0))),
                    heroB, pairKey, true);
                AddWorldTestRelationshipMembership(
                    memberships, "affinity",
                    ReadInt(state, "affinityBToA", ReadInt(state, "affinity_b_to_a", 0))
                        .ToString(System.Globalization.CultureInfo.InvariantCulture),
                    heroB, pairKey, true);
                foreach (string tag in ReadWorldTestTags(state, "tagBToA", "tag_b_to_a", "tagsBToA"))
                    AddWorldTestRelationshipMembership(memberships, "tag", tag, heroB, pairKey, true);
            }
            string sharedTag = ReadString(state, "sharedTag", ReadString(state, "shared_tag", ""));
            if (!string.IsNullOrWhiteSpace(sharedTag))
            {
                if (!string.IsNullOrWhiteSpace(heroA))
                    AddWorldTestRelationshipMembership(memberships, "tag", sharedTag, heroA, pairKey, false);
                if (!string.IsNullOrWhiteSpace(heroB))
                    AddWorldTestRelationshipMembership(memberships, "tag", sharedTag, heroB, pairKey, false);
            }
            return memberships;
        }

        private static IEnumerable<string> ReadWorldTestTags(
            Dictionary<string, object> state,
            string primaryKey,
            string legacyKey,
            string listKey)
        {
            HashSet<string> tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string primary = ReadString(state, primaryKey, ReadString(state, legacyKey, ""));
            if (!string.IsNullOrWhiteSpace(primary)) tags.Add(primary);
            if (state.TryGetValue(listKey, out object raw) && raw is IEnumerable values && !(raw is string))
            {
                foreach (object value in values)
                {
                    string tag = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(tag)) tags.Add(tag);
                }
            }
            return tags;
        }

        private static void AddWorldTestRelationshipMembership(
            Dictionary<string, WorldTestRelationshipMembership> memberships,
            string categoryType,
            string categoryKey,
            string heroId,
            string pairKey,
            bool directional)
        {
            if (string.IsNullOrWhiteSpace(categoryKey)
                || string.IsNullOrWhiteSpace(heroId)
                || string.IsNullOrWhiteSpace(pairKey)) return;
            string key = categoryType + "\u001f" + categoryKey + "\u001f" + heroId + "\u001f" + pairKey;
            if (!memberships.TryGetValue(key, out WorldTestRelationshipMembership membership))
            {
                membership = new WorldTestRelationshipMembership
                {
                    CategoryType = categoryType,
                    CategoryKey = categoryKey,
                    HeroId = heroId,
                    PairKey = pairKey
                };
                memberships[key] = membership;
            }
            membership.Count++;
            if (directional) membership.DirectionalCount++;
        }

        private static void ApplyWorldTestHeroMembershipDelta(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            int dayKey,
            WorldTestRelationshipMembership membership,
            int countDelta,
            int directionalDelta)
        {
            Dictionary<string, object> parameters = WorldTestMembershipParameters(
                campaignId, timelineId, dayKey, membership);
            Dictionary<string, object> current = QuerySql(connection, @"SELECT edge_count,directional_edge_count
FROM world_test_relationship_memberships
WHERE campaign_id=$campaign AND timeline_id=$timeline AND category_type=$type
AND category_key=$category AND hero_id=$hero;", parameters).FirstOrDefault();
            int oldCount = ReadInt(current, "edge_count", 0);
            int oldDirectional = ReadInt(current, "directional_edge_count", 0);
            int newCount = Math.Max(0, oldCount + countDelta);
            int newDirectional = Math.Max(0, oldDirectional + directionalDelta);
            parameters["count"] = newCount;
            parameters["directional"] = newDirectional;
            if (newCount == 0)
            {
                ExecuteSql(connection, @"DELETE FROM world_test_relationship_memberships
WHERE campaign_id=$campaign AND timeline_id=$timeline AND category_type=$type
AND category_key=$category AND hero_id=$hero;", parameters);
            }
            else
            {
                ExecuteSql(connection, @"INSERT INTO world_test_relationship_memberships(
campaign_id,timeline_id,category_type,category_key,hero_id,edge_count,
directional_edge_count,updated_day,updated_ts)
VALUES($campaign,$timeline,$type,$category,$hero,$count,$directional,$day,$ts)
ON CONFLICT(campaign_id,timeline_id,category_type,category_key,hero_id) DO UPDATE SET
edge_count=$count,directional_edge_count=$directional,updated_day=$day,updated_ts=$ts;", parameters);
            }
            if (oldCount == 0 && newCount > 0)
                AdjustWorldTestCurrentMetric(connection, campaignId, timelineId, dayKey, "relationships",
                    membership.CategoryType + ":" + membership.CategoryKey + ":uniqueNpcCount", 1);
            else if (oldCount > 0 && newCount == 0)
                AdjustWorldTestCurrentMetric(connection, campaignId, timelineId, dayKey, "relationships",
                    membership.CategoryType + ":" + membership.CategoryKey + ":uniqueNpcCount", -1);
            if (newDirectional != oldDirectional)
                AdjustWorldTestCurrentMetric(connection, campaignId, timelineId, dayKey, "relationships",
                    membership.CategoryType + ":" + membership.CategoryKey + ":directionalEdgeCount",
                    newDirectional - oldDirectional);
        }

        private static void ApplyWorldTestPairMembershipDelta(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            int dayKey,
            WorldTestRelationshipMembership membership,
            int countDelta,
            int directionalDelta)
        {
            Dictionary<string, object> parameters = WorldTestMembershipParameters(
                campaignId, timelineId, dayKey, membership);
            Dictionary<string, object> current = QuerySql(connection, @"SELECT edge_count,directional_edge_count
FROM world_test_relationship_pair_memberships
WHERE campaign_id=$campaign AND timeline_id=$timeline AND category_type=$type
AND category_key=$category AND pair_key=$pair;", parameters).FirstOrDefault();
            int oldCount = ReadInt(current, "edge_count", 0);
            int oldDirectional = ReadInt(current, "directional_edge_count", 0);
            int newCount = Math.Max(0, oldCount + countDelta);
            int newDirectional = Math.Max(0, oldDirectional + directionalDelta);
            parameters["count"] = newCount;
            parameters["directional"] = newDirectional;
            if (newCount == 0)
            {
                ExecuteSql(connection, @"DELETE FROM world_test_relationship_pair_memberships
WHERE campaign_id=$campaign AND timeline_id=$timeline AND category_type=$type
AND category_key=$category AND pair_key=$pair;", parameters);
            }
            else
            {
                ExecuteSql(connection, @"INSERT INTO world_test_relationship_pair_memberships(
campaign_id,timeline_id,category_type,category_key,pair_key,edge_count,
directional_edge_count,updated_day,updated_ts)
VALUES($campaign,$timeline,$type,$category,$pair,$count,$directional,$day,$ts)
ON CONFLICT(campaign_id,timeline_id,category_type,category_key,pair_key) DO UPDATE SET
edge_count=$count,directional_edge_count=$directional,updated_day=$day,updated_ts=$ts;", parameters);
            }
            if (oldCount == 0 && newCount > 0)
                AdjustWorldTestCurrentMetric(connection, campaignId, timelineId, dayKey, "relationships",
                    membership.CategoryType + ":" + membership.CategoryKey + ":pairCount", 1);
            else if (oldCount > 0 && newCount == 0)
                AdjustWorldTestCurrentMetric(connection, campaignId, timelineId, dayKey, "relationships",
                    membership.CategoryType + ":" + membership.CategoryKey + ":pairCount", -1);
        }

        private static Dictionary<string, object> WorldTestMembershipParameters(
            string campaignId,
            string timelineId,
            int dayKey,
            WorldTestRelationshipMembership membership)
        {
            return new Dictionary<string, object>
            {
                ["campaign"] = campaignId,
                ["timeline"] = timelineId,
                ["day"] = dayKey,
                ["type"] = membership.CategoryType,
                ["category"] = membership.CategoryKey,
                ["hero"] = membership.HeroId,
                ["pair"] = membership.PairKey,
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
        }

        private static void AdjustWorldTestCurrentMetric(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            int dayKey,
            string scope,
            string key,
            int delta)
        {
            if (delta == 0) return;
            ExecuteSql(connection, @"INSERT INTO world_test_current_metrics(
campaign_id,timeline_id,metric_scope,metric_key,metric_value,updated_day,updated_ts)
VALUES($campaign,$timeline,$scope,$key,MAX(0,$delta),$day,$ts)
ON CONFLICT(campaign_id,timeline_id,metric_scope,metric_key) DO UPDATE SET
metric_value=MAX(0,world_test_current_metrics.metric_value+$delta),
updated_day=$day,updated_ts=$ts;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId,
                    ["scope"] = scope,
                    ["key"] = key,
                    ["delta"] = delta,
                    ["day"] = dayKey,
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
        }

        private static void IncrementWorldTestObjectCounter(
            Dictionary<string, object> counters,
            string key,
            int amount)
        {
            if (counters == null || string.IsNullOrWhiteSpace(key) || amount == 0) return;
            counters[key] = ReadInt(counters, key, 0) + amount;
        }

        private static void AddSocialRumorTelemetryDimensions(
            Dictionary<string, object> counters,
            string archetypeId,
            IEnumerable<Dictionary<string, object>> participants,
            Dictionary<string, object> payload)
        {
            if (counters == null) return;
            IncrementWorldTestObjectCounter(counters,
                "archetype:" + FirstNonEmpty(archetypeId, "unknown"), 1);
            string provenance = FirstNonEmpty(
                ReadString(payload, "provenanceType", ""),
                ReadString(payload, "sourceType", ""),
                ReadString(payload, "sourceSystem", ""),
                "unspecified");
            IncrementWorldTestObjectCounter(counters, "provenance:" + provenance, 1);
            foreach (Dictionary<string, object> participant in participants
                ?? Enumerable.Empty<Dictionary<string, object>>())
            {
                string role = FirstNonEmpty(ReadString(participant, "role", ""), "unknown");
                string kingdom = FirstNonEmpty(
                    ReadFirstString(participant, "kingdomId", "kingdomStringId"), "none");
                string culture = FirstNonEmpty(
                    ReadFirstString(participant, "cultureId", "cultureStringId"), "unknown");
                IncrementWorldTestObjectCounter(counters, "subjectRole:" + role, 1);
                IncrementWorldTestObjectCounter(counters, "kingdom:" + kingdom, 1);
                IncrementWorldTestObjectCounter(counters, "culture:" + culture, 1);
            }
        }

        private static string SocialRumorDurationBucket(double days)
        {
            if (days <= 0.000001d) return "same_day";
            if (days <= 7d) return "1-7";
            if (days <= 31d) return "8-31";
            if (days <= 45d) return "32-45";
            return "46+";
        }

        private static void RecordWorldTestDiplomacyEvaluation(
            string campaignId,
            string timelineId,
            int dayKey,
            string status,
            int initiativeAttempts,
            bool initiativeSucceeded,
            bool failed,
            Dictionary<string, object> dimensions = null)
        {
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                Dictionary<string, object> counters =
                    BuildWorldTestDiplomacyEvaluationCounters(
                        status, initiativeAttempts, initiativeSucceeded, failed, dimensions);
                RecordWorldTestCounter(connection, campaignId, timelineId, dayKey, "diplomacy",
                    "evaluation_" + dayKey.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    counters);
            }
        }

        private static Dictionary<string, object> BuildWorldTestDiplomacyEvaluationCounters(
            string status,
            int initiativeAttempts,
            bool initiativeSucceeded,
            bool failed,
            Dictionary<string, object> dimensions = null)
        {
            Dictionary<string, object> counters = new Dictionary<string, object>
            {
                ["due"] = 1,
                ["completed"] = failed ? 0 : 1,
                ["noAction"] = status == "no_ruler_eligible"
                    || status == "no_initiative"
                    || status == "ruler_chose_no_action" ? 1 : 0,
                ["initiativeAttempted"] = initiativeAttempts,
                ["initiativeSucceeded"] = initiativeSucceeded ? 1 : 0,
                ["failures"] = failed ? 1 : 0
            };
            foreach (KeyValuePair<string, object> item in dimensions
                ?? new Dictionary<string, object>())
            {
                string value = Convert.ToString(item.Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(value))
                    IncrementWorldTestObjectCounter(counters, item.Key + ":" + value, 1);
            }
            IncrementWorldTestObjectCounter(counters,
                "status:" + FirstNonEmpty(status, "unknown"), 1);
            return counters;
        }

        private static Dictionary<string, object> BuildWorldTestMarriageOutcomeCounters(
            string route,
            string status)
        {
            string normalizedRoute = NormalizeLookup(route).Replace(' ', '_');
            if (normalizedRoute != "organic"
                && normalizedRoute != "arranged"
                && normalizedRoute != "diplomatic"
                && normalizedRoute != "pregnancy_commitment"
                && normalizedRoute != "affair_commitment")
                normalizedRoute = "unknown";
            string normalizedStatus = NormalizeLookup(status);
            Dictionary<string, object> counters = new Dictionary<string, object>();
            string prefix = normalizedRoute == "pregnancy_commitment"
                ? "pregnancyCommitment"
                : normalizedRoute == "affair_commitment"
                    ? "affairCommitment" : normalizedRoute;
            if (normalizedStatus == "completed" || normalizedStatus == "executed")
                counters[prefix + "Completed"] = 1;
            else if (normalizedStatus == "refused")
                counters[prefix + "Refused"] = 1;
            else if (normalizedStatus == "invalid" || normalizedStatus == "invalid_native")
                counters[prefix + "Invalid"] = 1;
            else if (normalizedStatus == "obsolete" || normalizedStatus == "abandoned")
                counters[prefix + "Abandoned"] = 1;
            else
                counters[prefix + "Failed"] = 1;
            counters["route:" + normalizedRoute] = 1;
            counters["outcome:" + FirstNonEmpty(normalizedStatus, "unknown")] = 1;
            return counters;
        }

        private static string WorldTestMarriageOutcomeRoute(
            Dictionary<string, object> actionPayload)
        {
            string route = NormalizeLookup(ReadString(actionPayload,
                "route", "")).Replace(' ', '_');
            if (route == "pregnancy_commitment")
                return "pregnancy_commitment";
            if (route == "affair_commitment") return "affair_commitment";
            return "organic";
        }

        private static Dictionary<string, object> BuildWorldTestRebellionRollCounters(
            bool wasEligible,
            bool triggered,
            int rulerRelation,
            bool isPlayerClan,
            string exclusionReason)
        {
            string exclusion = NormalizeLookup(exclusionReason);
            Dictionary<string, object> counters = new Dictionary<string, object>
            {
                ["weeklyRolls"] = 1,
                ["eligible"] = wasEligible ? 1 : 0,
                ["excludedRelationshipThreshold"] = !wasEligible
                    && string.IsNullOrWhiteSpace(exclusion)
                    && rulerRelation >= -20 ? 1 : 0,
                ["excludedCooldown"] = exclusion == "cooldown" ? 1 : 0,
                ["excludedActiveWar"] = exclusion == "active_war" ? 1 : 0,
                ["excludedIneligibleLeader"] = exclusion == "ineligible_leader" ? 1 : 0,
                ["excludedMissingRuler"] = exclusion == "missing_ruler" ? 1 : 0,
                ["triggered"] = triggered ? 1 : 0,
                ["playerInclusionViolation"] = isPlayerClan
                    && (wasEligible || triggered) ? 1 : 0
            };
            return counters;
        }

        private static List<Dictionary<string, object>> RunWorldTestTelemetrySelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["ok"] = true,
                ["passed"] = passed,
                ["suite"] = "world_test_telemetry",
                ["caseId"] = id,
                ["name"] = id,
                ["summary"] = summary,
                ["durationMs"] = 0
            });

            Dictionary<string, object> noActionCounters =
                BuildWorldTestDiplomacyEvaluationCounters(
                    "ruler_chose_no_action", 1, true, false,
                    new Dictionary<string, object> { ["intent"] = "peace" });
            add("world_test_no_action_is_healthy_evaluation",
                ReadInt(noActionCounters, "due", 0) == 1
                    && ReadInt(noActionCounters, "completed", 0) == 1
                    && ReadInt(noActionCounters, "noAction", 0) == 1
                    && ReadInt(noActionCounters, "failures", 0) == 0,
                "A legitimate diplomacy no-action result satisfies cadence without recording a failure.");

            Dictionary<string, object> organicMarriage =
                BuildWorldTestMarriageOutcomeCounters("organic", "completed");
            Dictionary<string, object> arrangedMarriage =
                BuildWorldTestMarriageOutcomeCounters("arranged", "completed");
            Dictionary<string, object> diplomaticMarriage =
                BuildWorldTestMarriageOutcomeCounters("diplomatic", "completed");
            Dictionary<string, object> pregnancyCommitmentMarriage =
                BuildWorldTestMarriageOutcomeCounters("pregnancy_commitment",
                    "completed");
            add("world_test_marriage_routes_separate",
                ReadInt(organicMarriage, "organicCompleted", 0) == 1
                    && ReadInt(arrangedMarriage, "arrangedCompleted", 0) == 1
                    && ReadInt(diplomaticMarriage, "diplomaticCompleted", 0) == 1
                    && ReadInt(pregnancyCommitmentMarriage,
                        "pregnancyCommitmentCompleted", 0) == 1
                    && !organicMarriage.ContainsKey("arrangedCompleted")
                    && !arrangedMarriage.ContainsKey("diplomaticCompleted")
                    && !pregnancyCommitmentMarriage.ContainsKey(
                        "organicCompleted")
                    && WorldTestMarriageOutcomeRoute(
                        new Dictionary<string, object>
                        {
                            ["route"] = "mutual_affinity"
                        }) == "organic"
                    && WorldTestMarriageOutcomeRoute(
                        new Dictionary<string, object>
                        {
                            ["route"] = "pregnancy_commitment"
                        }) == "pregnancy_commitment",
                "Organic, pregnancy-commitment, arranged, and diplomatic marriages have separate outcome funnels.");

            Dictionary<string, object> eligibleRebellion =
                BuildWorldTestRebellionRollCounters(true, true, -30, false, "");
            Dictionary<string, object> cooldownRebellion =
                BuildWorldTestRebellionRollCounters(false, false, -30, false, "cooldown");
            add("world_test_rebellion_denominators",
                ReadInt(eligibleRebellion, "eligible", 0) == 1
                    && ReadInt(eligibleRebellion, "triggered", 0) == 1
                    && ReadInt(cooldownRebellion, "excludedCooldown", 0) == 1,
                "Rebellion rates retain eligible and cooldown-exclusion denominators.");

            List<int> missingRebellionWeeks = MissingRebellionWeekIndexes(
                new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["weekIndex"] = 13011 },
                    new Dictionary<string, object> { ["weekIndex"] = 13013 },
                    new Dictionary<string, object> { ["weekIndex"] = 13014 }
                });
            add("world_test_detects_internal_rebellion_cadence_gap",
                missingRebellionWeeks.SequenceEqual(new[] { 13012 }),
                "World Test warns about an internal missing weekly roll set even when a newer rebellion heartbeat exists.");

            try
            {
                string storageCampaign = "__world_test_telemetry_"
                    + Guid.NewGuid().ToString("N").Substring(0, 12);
                using (ReignDbConnection connection =
                    OpenCampaignConnection(storageCampaign))
                {
                    const string campaign = "telemetry_fixture";
                    EnsureWorldTestTelemetrySchema(connection);

                    Dictionary<string, object> mainCounters = new Dictionary<string, object>
                    {
                        ["evaluatedPairs"] = 500
                    };
                    RecordWorldTestCounter(
                        connection, campaign, "main", 1, "relationships", "chunk-000", mainCounters);
                    RecordWorldTestCounter(
                        connection, campaign, "main", 1, "relationships", "chunk-000", mainCounters);
                    RecordWorldTestCounter(
                        connection, campaign, "branch", 1, "relationships", "chunk-000",
                        new Dictionary<string, object> { ["evaluatedPairs"] = 37 });
                    RecordWorldTestCounter(
                        connection, campaign, "main", 5, "diplomacy", "evaluation_5",
                        BuildWorldTestDiplomacyEvaluationCounters(
                            "ruler_chose_no_action", 1, true, false));
                    RecordWorldTestCounter(
                        connection, campaign, "main", 6, "diplomacy", "evaluation_6",
                        BuildWorldTestDiplomacyEvaluationCounters(
                            "model_failure", 1, true, true));

                    RecordWorldTestEvidence(
                        connection, campaign, "main", 1, "relationships", "pair-a-b", "warning",
                        new Dictionary<string, object> { ["message"] = "first observation" });
                    RecordWorldTestEvidence(
                        connection, campaign, "main", 1, "relationships", "pair-a-b", "warning",
                        new Dictionary<string, object> { ["message"] = "replacement observation" });

                    Dictionary<string, object> initialPair = new Dictionary<string, object>
                    {
                        ["pairKey"] = "a|b",
                        ["heroAId"] = "a",
                        ["heroBId"] = "b",
                        ["affinityAToB"] = 0,
                        ["affinityBToA"] = 15,
                        ["tagAToB"] = "neutral",
                        ["tagBToA"] = "acquaintance"
                    };
                    ApplyWorldTestRelationshipPairDelta(
                        connection, campaign, "main", 1,
                        new Dictionary<string, object>(), initialPair,
                        new Dictionary<string, object>());
                    Dictionary<string, object> friendPair =
                        new Dictionary<string, object>(initialPair, StringComparer.OrdinalIgnoreCase)
                        {
                            ["affinityAToB"] = 35,
                            ["affinityBToA"] = 35,
                            ["tagAToB"] = "friend",
                            ["tagBToA"] = "friend"
                        };
                    ApplyWorldTestRelationshipPairDelta(
                        connection, campaign, "main", 2, initialPair, friendPair,
                        new Dictionary<string, object>());

                    EnqueueWorldTestRollup(connection, campaign, "main", 2, "relationships_completed");
                    EnqueueWorldTestRollup(connection, campaign, "main", 2, "heartbeat");

                    int mainEvaluated = WorldTestCounterValue(
                        connection, campaign, "main", 1, "relationships", "evaluatedPairs");
                    int branchEvaluated = WorldTestCounterValue(
                        connection, campaign, "branch", 1, "relationships", "evaluatedPairs");
                    int evidenceCount = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM world_test_diagnostic_evidence
WHERE campaign_id=$campaign AND timeline_id='main' AND subsystem='relationships'
AND evidence_key='pair-a-b';",
                        new Dictionary<string, object> { ["campaign"] = campaign }).FirstOrDefault(), "count", 0);

                    add("counter_chunk_idempotent", mainEvaluated == 500,
                        "Replaying one chunk key does not double-count its 500 evaluations.");
                    add("timeline_counter_isolation", branchEvaluated == 37,
                        "A branch retains counters independent of main.");
                    add("diplomacy_cadence_uses_successful_evaluation_heartbeat",
                        Math.Abs(LatestSuccessfulDiplomacyEvaluationDay(
                            connection, campaign, "main") - 5d) < 0.001d,
                        "The diplomacy card advances on a successful no-action evaluation while ignoring a newer failed attempt.");
                    add("bounded_evidence_replaces_same_key", evidenceCount == 1,
                        "Repeated anomaly evidence replaces the same bounded record.");
                    add("relationship_membership_zero_to_one",
                        WorldTestMetricValue(connection, campaign, "main", "relationships", "band:neutral:directionalEdgeCount") == 0
                        && WorldTestMetricValue(connection, campaign, "main", "relationships", "band:acquaintance:pairCount") == 0
                        && WorldTestMetricValue(connection, campaign, "main", "relationships", "band:friend:uniqueNpcCount") == 2
                        && WorldTestMetricValue(connection, campaign, "main", "relationships", "band:friend:directionalEdgeCount") == 2
                        && WorldTestMetricValue(connection, campaign, "main", "relationships", "band:friend:pairCount") == 1,
                        "Relationship category populations change only on edge, NPC, and pair membership transitions.");
                    Dictionary<string, object> materializedPair = QuerySql(connection, @"
SELECT hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,tag_a_to_b,tag_b_to_a
FROM world_test_relationship_pairs
WHERE campaign_id=$campaign AND timeline_id='main' AND pair_key='a|b' LIMIT 1;",
                        new Dictionary<string, object> { ["campaign"] = campaign }).FirstOrDefault();
                    add("relationship_overview_materialized_pair_state",
                        materializedPair != null
                        && ReadInt(materializedPair, "affinity_a_to_b", 0) == 35
                        && ReadInt(materializedPair, "affinity_b_to_a", 0) == 35
                        && ReadString(materializedPair, "tag_a_to_b", "") == "friend"
                        && ReadString(materializedPair, "tag_b_to_a", "") == "friend",
                        "Current relationship state is materialized for overview and rollups without scanning the authoritative pair ledger.");
                    ApplyWorldTestRelationshipPairDelta(
                        connection, campaign, "branch", 2, friendPair, friendPair,
                        new Dictionary<string, object>());
                    add("relationship_branch_materializes_inherited_state",
                        WorldTestMetricValue(connection, campaign, "branch", "relationships",
                            "band:friend:directionalEdgeCount") == 2
                        && WorldTestMetricValue(connection, campaign, "branch", "relationships",
                            "band:friend:pairCount") == 1,
                        "A new timeline materializes inherited pair state even when its first processed values do not change.");
                    int queuedRollups = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM world_test_rollup_queue WHERE campaign_id=$campaign AND timeline_id='main' AND day_key=2;",
                        new Dictionary<string, object> { ["campaign"] = campaign }).FirstOrDefault(), "count", 0);
                    add("rollup_queue_idempotent", queuedRollups == 1,
                        "Repeated completion signals keep one durable rollup queue row per campaign timeline day.");
                }
                ReignPostgreSqlStorage.DropCampaign(storageCampaign);
            }
            catch (Exception ex)
            {
                string failure = "Telemetry foundation is unavailable: " + ex.Message;
                add("counter_chunk_idempotent", false, failure);
                add("timeline_counter_isolation", false, failure);
                add("bounded_evidence_replaces_same_key", false, failure);
                add("relationship_membership_zero_to_one", false, failure);
                add("relationship_overview_materialized_pair_state", false, failure);
                add("relationship_branch_materializes_inherited_state", false, failure);
                add("rollup_queue_idempotent", false, failure);
            }

            return results;
        }

        private static int WorldTestCounterValue(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            int dayKey,
            string subsystem,
            string metricKey)
        {
            List<Dictionary<string, object>> rows = QuerySql(connection, @"SELECT counters_json
FROM world_test_chunk_counters
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day
AND subsystem=$subsystem;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId,
                    ["day"] = dayKey,
                    ["subsystem"] = subsystem
                });
            int total = 0;
            foreach (Dictionary<string, object> row in rows)
            {
                Dictionary<string, object> counters =
                    TryParseJsonObject(ReadString(row, "counters_json", "{}"))
                    ?? new Dictionary<string, object>();
                total += ReadInt(counters, metricKey, 0);
            }
            return total;
        }

        private static Dictionary<string, int> WorldTestCounterTotals(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            string subsystem)
        {
            EnsureWorldTestTelemetrySchema(connection);
            Dictionary<string, int> totals =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> row in QuerySql(connection, @"SELECT counters_json
FROM world_test_chunk_counters
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subsystem=$subsystem;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId,
                    ["subsystem"] = subsystem
                }))
            {
                Dictionary<string, object> counters =
                    TryParseJsonObject(ReadString(row, "counters_json", "{}"))
                    ?? new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> item in counters)
                {
                    if (!totals.ContainsKey(item.Key)) totals[item.Key] = 0;
                    totals[item.Key] += ReadInt(counters, item.Key, 0);
                }
            }
            return totals;
        }

        private static long WorldTestMetricValue(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            string metricScope,
            string metricKey)
        {
            Dictionary<string, object> row = QuerySql(connection, @"SELECT metric_value
FROM world_test_current_metrics
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND metric_scope=$scope AND metric_key=$key LIMIT 1;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId,
                    ["scope"] = metricScope,
                    ["key"] = metricKey
                }).FirstOrDefault();
            return ReadLong(row, "metric_value", 0L);
        }
    }
}
