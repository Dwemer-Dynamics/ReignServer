using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void EnsureCharacterSocialProfileSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS reputation_activations (
activation_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
subject_id TEXT NOT NULL,tag_id TEXT NOT NULL,source_occurrence_id TEXT NOT NULL DEFAULT '',
source_event_id TEXT NOT NULL DEFAULT '',activation_day REAL NOT NULL DEFAULT 0,
description TEXT NOT NULL DEFAULT '',incident_description TEXT NOT NULL DEFAULT '',
evidence_json TEXT NOT NULL DEFAULT '{}',reason_text TEXT NOT NULL DEFAULT '',
            reason_status TEXT NOT NULL DEFAULT 'recorded',provider TEXT NOT NULL DEFAULT '',
model TEXT NOT NULL DEFAULT '',generation_attempts INTEGER NOT NULL DEFAULT 0,
last_error TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);");
            ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_reputation_activations_subject
ON reputation_activations(campaign_id,timeline_id,subject_id,tag_id,activation_day DESC);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS reputation_reason_jobs (
job_id TEXT PRIMARY KEY,activation_id TEXT NOT NULL UNIQUE,campaign_id TEXT NOT NULL,
            status TEXT NOT NULL DEFAULT 'retired',attempt_count INTEGER NOT NULL DEFAULT 0,
last_error TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_reputation_reason_jobs_status ON reputation_reason_jobs(status,updated_ts);");
            EnsureDatabaseColumn(connection, "character_reputations", "reason_activation_id", "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "character_reputations", "reason_text", "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "character_reputations", "reason_status", "TEXT NOT NULL DEFAULT ''");
            string recordedMigration = ReadString(QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='reputation_reasons_are_recorded_v1' LIMIT 1;")
                .FirstOrDefault(), "value", "");
            if (recordedMigration != "1")
            {
                NormalizeRecordedReputationReasons(connection);
                ExecuteSql(connection, @"INSERT INTO schema_meta(key,value)
VALUES('reputation_reasons_are_recorded_v1','1')
ON CONFLICT(key) DO UPDATE SET value='1';");
            }
        }

        private static string RecordReputationActivation(ReignDbConnection connection, string campaignId, string timelineId,
            string subjectId, string tagId, string sourceOccurrenceId, string sourceEventId, double worldDay,
            string description, string incidentDescription, Dictionary<string, object> evidence)
        {
            EnsureCharacterSocialProfileSchema(connection);
            string fallback = FirstNonEmpty(incidentDescription, description, "This reputation became established through recorded events.");
            string activationId = "reputation_activation_" + Guid.NewGuid().ToString("N");
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ExecuteSql(connection, @"INSERT INTO reputation_activations(activation_id,campaign_id,timeline_id,subject_id,tag_id,
source_occurrence_id,source_event_id,activation_day,description,incident_description,evidence_json,reason_text,reason_status,
created_ts,updated_ts)
VALUES($activation,$campaign,$timeline,$subject,$tag,$occurrence,$event,$day,$description,$incident,$evidence,$fallback,'recorded',$ts,$ts);",
                new Dictionary<string, object>
                {
                    ["activation"] = activationId, ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["subject"] = subjectId, ["tag"] = tagId, ["occurrence"] = sourceOccurrenceId ?? "",
                    ["event"] = sourceEventId ?? "", ["day"] = worldDay, ["description"] = description ?? "",
                    ["incident"] = fallback, ["evidence"] = Json.Serialize(evidence ?? new Dictionary<string, object>()),
                    ["fallback"] = fallback, ["ts"] = ts
                });
            ExecuteSql(connection, @"UPDATE character_reputations
SET reason_activation_id=$activation,reason_text=$fallback,reason_status='recorded',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag;",
                new Dictionary<string, object>
                {
                    ["activation"] = activationId, ["fallback"] = fallback, ["ts"] = ts,
                    ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["tag"] = tagId
                });
            return activationId;
        }

        private static string SocialOccurrenceIncident(ReignDbConnection connection, string occurrenceId, string fallback)
        {
            if (string.IsNullOrWhiteSpace(occurrenceId)) return fallback ?? "";
            Dictionary<string, object> row = QuerySql(connection,
                "SELECT provenance_summary FROM rumor_occurrences WHERE occurrence_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = occurrenceId }).FirstOrDefault();
            return FirstNonEmpty(ReadString(row, "provenance_summary", ""), fallback);
        }

        private static void SchedulePendingReputationReasonJobs(string campaignId)
        {
            // Reputation reasons are authoritative producer evidence. Richer
            // phrasing belongs to the interaction prompt, never a background
            // LLM write that can delay or alter social state.
        }

        private static void NormalizeRecordedReputationReasons(ReignDbConnection connection)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ExecuteSql(connection, @"UPDATE reputation_activations
SET reason_text=COALESCE(NULLIF(incident_description,''),NULLIF(description,''),'A recorded event established this reputation.'),
reason_status='recorded',provider='',model='',last_error='',updated_ts=$ts;",
                new Dictionary<string, object> { ["ts"] = ts });
            ExecuteSql(connection, @"UPDATE character_reputations
SET reason_text=COALESCE(NULLIF((SELECT a.incident_description FROM reputation_activations a
    WHERE a.activation_id=character_reputations.reason_activation_id),''),NULLIF(reason_text,''),NULLIF(description,''),'A recorded event established this reputation.'),
reason_status='recorded',updated_ts=$ts
WHERE reason_activation_id<>'';", new Dictionary<string, object> { ["ts"] = ts });
            ExecuteSql(connection, @"UPDATE reputation_reason_jobs
SET status='retired',last_error='Background LLM reason generation was retired; the recorded incident is authoritative.',updated_ts=$ts
WHERE status<>'retired';", new Dictionary<string, object> { ["ts"] = ts });
        }

        private static void ProcessReputationReasonJob(string campaignId, string jobId)
        {
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                NormalizeRecordedReputationReasons(connection);
            }
        }

        private static void ResumePendingReputationReasonJobs()
        {
            EnqueuePriorityBackgroundWork("reputation.reason.resume", "maintenance", () =>
            {
                string root = CampaignsRoot();
                if (!Directory.Exists(root)) return;
                foreach (string directory in Directory.GetDirectories(root))
                {
                    if (PriorityBackgroundWorkShouldYield()) return;
                    string campaignId = Path.GetFileName(directory);
                    using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                    {
                        EnsureSocialReputationSchema(connection);
                        NormalizeRecordedReputationReasons(connection);
                    }
                }
            });
        }

        private static Dictionary<string, object> CharacterSocialStandingProjection(
            ReignDbConnection connection, string campaignId, string subjectId)
        {
            EnsureSocialReputationSchema(connection);
            string timelineId = ResolveCharacterSocialTimeline(connection);
            double worldDay = ResolveCharacterSocialWorldDay(connection, campaignId, timelineId);
            if (worldDay > 0d) ExpireSocialRumors(connection, campaignId, timelineId, worldDay);
            bool popularityChanged = RecomputeCourtPopularityReputations(connection, campaignId, timelineId,
                subjectId, worldDay, "character_profile|" + subjectId + "|"
                    + Math.Floor(worldDay).ToString(CultureInfo.InvariantCulture));
            if (popularityChanged)
            {
                ReconcileSocialRelationshipsForSubjects(connection, campaignId, timelineId, new[] { subjectId }, worldDay);
                if (!campaignId.StartsWith("__", StringComparison.Ordinal))
                    SchedulePendingReputationReasonJobs(campaignId);
            }
            Dictionary<string, object> catalog = ReadActiveSocialCatalog(connection);
            Dictionary<string, Dictionary<string, object>> tags = ReadDictionaryList(catalog, "tags")
                .Where(tag => !string.IsNullOrWhiteSpace(ReadString(tag, "id", "")))
                .ToDictionary(tag => ReadString(tag, "id", ""), tag => tag, StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> rumors = QuerySql(connection, @"SELECT rst.tag_id,rst.subject_role,rst.description,
rst.rumor_value,rst.snapshot_json,ro.occurrence_id,ro.archetype_id,ro.source_event_id,ro.world_day,ro.expires_day,
ro.provenance_summary,ro.snapshot_json AS occurrence_snapshot_json
FROM rumor_subject_tags rst JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline AND rst.subject_id=$subject
AND rst.status='active' AND ro.status='active' AND ro.expires_day>$day
ORDER BY ro.world_day DESC,ro.created_ts DESC;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["day"] = worldDay });
            foreach (Dictionary<string, object> row in rumors)
                EnrichSocialProfileRow(row, tags, false);
            List<Dictionary<string, object>> reputations = QuerySql(connection, @"SELECT tag_id,description,reputation_value,
acquired_day,reason_text,reason_status,reason_activation_id,snapshot_json,archetype_id,subject_role
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND status='active'
ORDER BY acquired_day DESC,updated_ts DESC;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId });
            foreach (Dictionary<string, object> row in reputations)
                EnrichSocialProfileRow(row, tags, true);
            Dictionary<string, object> publicStanding = ReadPublicStandingTrait(
                connection, campaignId, timelineId, subjectId, worldDay);
            return new Dictionary<string, object>
            {
                ["timelineId"] = timelineId, ["worldDay"] = worldDay,
                ["activeRumors"] = rumors, ["activeReputations"] = reputations,
                ["publicStanding"] = publicStanding,
                ["catalogChoices"] = BuildCharacterSocialCatalogChoices(catalog)
            };
        }

        private static void EnrichSocialProfileRow(Dictionary<string, object> row,
            Dictionary<string, Dictionary<string, object>> tags, bool reputation)
        {
            string tagId = ReadString(row, "tag_id", "");
            Dictionary<string, object> definition;
            if (!tags.TryGetValue(tagId, out definition))
                definition = TryParseJsonObject(ReadString(row, "snapshot_json", "{}")) ?? new Dictionary<string, object>();
            int value = reputation ? ReadInt(row, "reputation_value", 0) : ReadInt(row, "rumor_value", 0);
            row["label"] = ReadString(definition, "label", tagId);
            row["classification"] = value > 0 ? "positive" : value < 0 ? "negative" : "neutral";
            if (reputation) row["activationKey"] = ReadString(row, "reason_activation_id", "");
            row["incidentDescription"] = reputation
                ? FirstNonEmpty(ReadString(row, "reason_text", ""), ReadString(row, "description", ""))
                : FirstNonEmpty(ReadString(row, "provenance_summary", ""), ReadString(row, "description", ""));
            row.Remove("tag_id");
            row.Remove("rumor_value");
            row.Remove("reputation_value");
            row.Remove("snapshot_json");
            row.Remove("occurrence_snapshot_json");
            row.Remove("reason_activation_id");
            row.Remove("occurrence_id");
            row.Remove("source_event_id");
        }

        private static Dictionary<string, object> BuildCharacterSocialCatalogChoices(Dictionary<string, object> catalog)
        {
            List<Dictionary<string, object>> tags = ReadDictionaryList(catalog, "tags");
            Dictionary<string, Dictionary<string, object>> byId = tags
                .Where(tag => !string.IsNullOrWhiteSpace(ReadString(tag, "id", "")))
                .ToDictionary(tag => ReadString(tag, "id", ""), tag => tag, StringComparer.OrdinalIgnoreCase);
            List<object> rumorChoices = new List<object>();
            foreach (Dictionary<string, object> archetype in ReadDictionaryList(catalog, "archetypes")
                .Where(item => ReadBool(item, "enabled", true) && !ReadBool(item, "directReputation", false)))
            {
                foreach (KeyValuePair<string, object> role in DictionaryOrDefault(archetype, "roleMappings", new Dictionary<string, object>()))
                {
                    List<Dictionary<string, object>> mapped = ValueTextList(role.Value)
                        .Where(byId.ContainsKey).Select(id => byId[id])
                        .Where(tag => ReadBool(tag, "enabled", true) && !ReadBool(tag, "derived", false)
                            && ReadBool(tag, "manualEligible", true)).ToList();
                    if (mapped.Count == 0) continue;
                    rumorChoices.Add(new Dictionary<string, object>
                    {
                        ["archetypeId"] = ReadString(archetype, "id", ""), ["role"] = role.Key,
                        ["label"] = string.Join(" / ", mapped.Select(tag => ReadString(tag, "label", ReadString(tag, "id", "")))),
                        ["description"] = ReadString(archetype, "description", ""),
                        ["allowPlayerSubject"] = ReadBool(archetype, "allowPlayerSubject", false)
                    });
                }
            }
            List<object> reputationChoices = tags
                .Where(tag => ReadBool(tag, "enabled", true) && !ReadBool(tag, "derived", false)
                    && ReadBool(tag, "manualEligible", true)
                    && ReadInt(tag, "reputationValue", 0) != 0)
                .OrderBy(tag => ReadString(tag, "label", ""))
                .Select(tag => (object)new Dictionary<string, object>
                {
                    ["tagId"] = ReadString(tag, "id", ""), ["label"] = ReadString(tag, "label", ""),
                    ["description"] = ReadString(tag, "description", ""),
                    ["classification"] = ReadInt(tag, "reputationValue", 0) > 0 ? "positive" : "negative",
                    ["allowPlayerSubject"] = ReadBool(tag, "allowPlayerSubject", true)
                }).ToList();
            return new Dictionary<string, object> { ["rumors"] = rumorChoices, ["reputations"] = reputationChoices };
        }

        private static string ResolveCharacterSocialTimeline(ReignDbConnection connection)
        {
            if (QuerySql(connection, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='world_test_native_heartbeats';").Any())
            {
                string timeline = ReadString(QuerySql(connection,
                    "SELECT timeline_id FROM world_test_native_heartbeats ORDER BY world_day DESC,updated_ts DESC LIMIT 1;").FirstOrDefault(), "timeline_id", "");
                if (!string.IsNullOrWhiteSpace(timeline)) return timeline;
            }
            string rumorTimeline = ReadString(QuerySql(connection,
                "SELECT timeline_id FROM rumor_occurrences ORDER BY world_day DESC,updated_ts DESC LIMIT 1;").FirstOrDefault(), "timeline_id", "");
            return FirstNonEmpty(rumorTimeline, "main");
        }

        private static double ResolveCharacterSocialWorldDay(ReignDbConnection connection, string campaignId, string timelineId)
        {
            double day = LatestKnownWorldDay(connection, campaignId);
            if (QuerySql(connection, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='world_test_native_heartbeats';").Any())
                day = Math.Max(day, ReadDouble(QuerySql(connection,
                    "SELECT MAX(world_day) AS day FROM world_test_native_heartbeats WHERE timeline_id=$timeline;",
                    new Dictionary<string, object> { ["timeline"] = timelineId }).FirstOrDefault(), "day", 0d));
            return day;
        }

        private static Dictionary<string, object> CharacterEditorSocialRumorAddApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = FirstNonEmpty(ReadString(payload, "campaignId", ""), LatestCampaignId());
            string subjectId = ReadFirstString(payload, "heroStringId", "heroId", "subjectId");
            string archetypeId = ReadString(payload, "archetypeId", "");
            string role = ReadString(payload, "role", "");
            string incident = (ReadString(payload, "incidentDescription", "") ?? "").Trim();
            string idempotencyKey = ReadString(payload, "idempotencyKey", "");
            if (string.IsNullOrWhiteSpace(subjectId) || string.IsNullOrWhiteSpace(archetypeId) || string.IsNullOrWhiteSpace(role))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Character, Rumor, and role are required." };
            if (incident.Length < 10 || incident.Length > 500)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Incident description must be between 10 and 500 characters." };
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "idempotencyKey is required." };
            Dictionary<string, object> result;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                string timelineId = FirstNonEmpty(ReadString(payload, "timelineId", ""), ResolveCharacterSocialTimeline(connection));
                double worldDay = ResolveCharacterSocialWorldDay(connection, campaignId, timelineId);
                if (worldDay <= 0d) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Current campaign day is unavailable. Load the campaign before adding social standing." };
                Dictionary<string, object> participant = CharacterSocialParticipant(connection, campaignId, subjectId, role);
                if (participant == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Only living adult characters can receive active Rumors." };
                result = RegisterSocialOccurrence(connection, campaignId, new Dictionary<string, object>
                {
                    ["timelineId"] = timelineId, ["archetypeId"] = archetypeId,
                    ["threadKey"] = subjectId + "|" + archetypeId,
                    ["sourceEventId"] = "control_center_rumor_" + DeterministicSocialId(idempotencyKey),
                    ["worldDay"] = worldDay, ["forceExposure"] = true,
                    ["provenanceSummary"] = incident, ["participants"] = new List<object> { participant }
                });
                result["socialStanding"] = CharacterSocialStandingProjection(connection, campaignId, subjectId);
            }
            if (!campaignId.StartsWith("__", StringComparison.Ordinal))
                SchedulePendingReputationReasonJobs(campaignId);
            return result;
        }

        private static Dictionary<string, object> CharacterEditorSocialReputationAddApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = FirstNonEmpty(ReadString(payload, "campaignId", ""), LatestCampaignId());
            string subjectId = ReadFirstString(payload, "heroStringId", "heroId", "subjectId");
            string tagId = ReadString(payload, "tagId", "");
            string incident = (ReadString(payload, "incidentDescription", "") ?? "").Trim();
            string idempotencyKey = ReadString(payload, "idempotencyKey", "");
            if (string.IsNullOrWhiteSpace(subjectId) || string.IsNullOrWhiteSpace(tagId))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Character and Reputation are required." };
            if (incident.Length < 10 || incident.Length > 500)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Incident description must be between 10 and 500 characters." };
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "idempotencyKey is required." };
            Dictionary<string, object> response;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                string timelineId = FirstNonEmpty(ReadString(payload, "timelineId", ""), ResolveCharacterSocialTimeline(connection));
                double worldDay = ResolveCharacterSocialWorldDay(connection, campaignId, timelineId);
                if (worldDay <= 0d) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Current campaign day is unavailable. Load the campaign before adding social standing." };
                Dictionary<string, object> participant = CharacterSocialParticipant(connection, campaignId, subjectId, "manual");
                if (participant == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Only living adult characters can receive active Reputations." };
                Dictionary<string, object> catalog = ReadActiveSocialCatalog(connection);
                Dictionary<string, object> tag = ReadDictionaryList(catalog, "tags").FirstOrDefault(item =>
                    string.Equals(ReadString(item, "id", ""), tagId, StringComparison.OrdinalIgnoreCase));
                if (tag == null || !ReadBool(tag, "enabled", true) || ReadBool(tag, "derived", false)
                    || !ReadBool(tag, "manualEligible", true))
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "That Reputation is unavailable for manual addition." };
                string sourceEventId = "control_center_reputation_" + DeterministicSocialId(idempotencyKey);
                Dictionary<string, object> duplicateActivation = QuerySql(connection,
                    "SELECT activation_id FROM reputation_activations WHERE campaign_id=$campaign AND source_event_id=$event LIMIT 1;",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["event"] = sourceEventId }).FirstOrDefault();
                if (duplicateActivation != null)
                    return new Dictionary<string, object> { ["ok"] = true, ["duplicate"] = true, ["socialStanding"] = CharacterSocialStandingProjection(connection, campaignId, subjectId) };
                if (QuerySql(connection, @"SELECT 1 FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$subject AND tag_id=$tag AND status='active' LIMIT 1;",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["tag"] = tagId }).Any())
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "This character already has that active Reputation." };
                Dictionary<string, object> snapshot = SocialTagSnapshot(tag, participant);
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                int revision = ReadInt(catalog, "revision", BuiltInSocialCatalogRevision);
                ExecuteSql(connection, @"INSERT INTO character_reputations(campaign_id,timeline_id,subject_id,tag_id,
source_occurrence_id,archetype_id,subject_role,description,reputation_value,acquired_day,catalog_revision,snapshot_json,status,updated_ts)
VALUES($campaign,$timeline,$subject,$tag,'','manual_control_center','manual',$description,$value,$day,$revision,$snapshot,'active',$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,tag_id) DO UPDATE SET source_occurrence_id='',archetype_id='manual_control_center',
subject_role='manual',description=$description,reputation_value=$value,acquired_day=$day,catalog_revision=$revision,
snapshot_json=$snapshot,status='active',updated_ts=$ts;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["tag"] = tagId,
                        ["description"] = ReadString(snapshot, "description", ""), ["value"] = ReadInt(snapshot, "reputationValue", 0),
                        ["day"] = worldDay, ["revision"] = revision, ["snapshot"] = Json.Serialize(snapshot), ["ts"] = ts
                    });
                string activationId = RecordReputationActivation(connection, campaignId, timelineId, subjectId, tagId, "",
                    sourceEventId, worldDay, ReadString(snapshot, "description", ""), incident,
                    new Dictionary<string, object> { ["source"] = "manual_control_center", ["incidentDescription"] = incident });
                RecomputeDerivedSocialReputations(connection, campaignId, timelineId, subjectId, worldDay, sourceEventId);
                ReconcileSocialRelationshipsForSubjects(connection, campaignId, timelineId, new[] { subjectId }, worldDay);
                response = new Dictionary<string, object>
                {
                    ["ok"] = true, ["activationId"] = activationId,
                    ["socialStanding"] = CharacterSocialStandingProjection(connection, campaignId, subjectId)
                };
            }
            if (!campaignId.StartsWith("__", StringComparison.Ordinal))
                SchedulePendingReputationReasonJobs(campaignId);
            return response;
        }

        private static Dictionary<string, object> CharacterEditorSocialReasonRetryApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = FirstNonEmpty(ReadString(payload, "campaignId", ""), LatestCampaignId());
            string activationId = ReadString(payload, "activationId", "");
            if (string.IsNullOrWhiteSpace(activationId))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "activationId is required." };
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                Dictionary<string, object> activation = QuerySql(connection,
                    "SELECT incident_description,description FROM reputation_activations WHERE activation_id=$activation LIMIT 1;",
                    new Dictionary<string, object> { ["activation"] = activationId }).FirstOrDefault();
                if (activation == null)
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "The recorded reputation event was not found." };
                string reason = FirstNonEmpty(ReadString(activation, "incident_description", ""),
                    ReadString(activation, "description", ""), "A recorded event established this reputation.");
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ExecuteSql(connection, @"UPDATE reputation_activations
SET reason_text=$reason,reason_status='recorded',provider='',model='',last_error='',updated_ts=$ts
WHERE activation_id=$activation;",
                    new Dictionary<string, object> { ["activation"] = activationId, ["reason"] = reason, ["ts"] = ts });
                ExecuteSql(connection, @"UPDATE character_reputations
SET reason_text=$reason,reason_status='recorded',updated_ts=$ts WHERE reason_activation_id=$activation;",
                    new Dictionary<string, object> { ["activation"] = activationId, ["reason"] = reason, ["ts"] = ts });
                ExecuteSql(connection, @"UPDATE reputation_reason_jobs
SET status='retired',last_error='Background LLM reason generation was retired.',updated_ts=$ts
WHERE activation_id=$activation;",
                    new Dictionary<string, object> { ["activation"] = activationId, ["ts"] = ts });
                return new Dictionary<string, object> { ["ok"] = true, ["recorded"] = true, ["reason"] = reason };
            }
        }

        private static Dictionary<string, object> CharacterSocialParticipant(
            ReignDbConnection connection, string campaignId, string subjectId, string role)
        {
            Dictionary<string, object> roster = QuerySql(connection, "SELECT * FROM identity_roster WHERE hero_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = subjectId }).FirstOrDefault();
            Dictionary<string, object> profile = ReadJsonObject(CharacterFile(campaignId, subjectId, "profile.json"));
            bool alive = roster != null ? ReadInt(roster, "is_alive", 1) != 0 : ReadBool(profile, "isAlive", true);
            bool adult = roster != null ? ReadInt(roster, "is_adult", 1) != 0 : !ReadBool(profile, "isChild", false);
            if (!alive || !adult) return null;
            return new Dictionary<string, object>
            {
                ["subjectId"] = subjectId, ["role"] = role, ["isAlive"] = true, ["isAdult"] = true,
                ["isPlayer"] = roster != null ? ReadInt(roster, "is_player", 0) != 0 : ReadBool(profile, "isPlayer", false),
                ["clanTier"] = roster != null ? ReadInt(roster, "clan_tier", 0) : ReadInt(profile, "clanTier", 0),
                ["sex"] = roster != null ? ReadString(roster, "sex", "") : (ReadBool(profile, "isFemale", false) ? "female" : "male")
            };
        }
    }
}
