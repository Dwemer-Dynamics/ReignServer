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
        private const int SocialReputationSchemaVersion = 4;
        private const int BuiltInSocialCatalogRevision = 6;
        private static string SocialCatalogPath => Path.Combine(DataDir, "social_rumor_reputation_catalog.json");
        private static string SocialCatalogCachedJson = "";
        private static string SocialCatalogCachedPath = "";
        private static long SocialCatalogCachedWriteTicks = long.MinValue;

        private static void EnsureSocialReputationSchema(ReignDbConnection connection)
        {
            EnsureIdentitySchema(connection);
            EnsureMbtiRelationshipSchema(connection);
            MigrateLegacySocialStorage(connection);
            EnsureExpandedSocialReputationSchema(connection);
            EnsurePublicStandingSchema(connection);
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS social_catalog_revisions (
revision INTEGER PRIMARY KEY,catalog_json TEXT NOT NULL,created_ts INTEGER NOT NULL);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS rumor_occurrences (
occurrence_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
archetype_id TEXT NOT NULL,thread_key TEXT NOT NULL,source_event_id TEXT NOT NULL DEFAULT '',
world_day REAL NOT NULL,expires_day REAL NOT NULL,status TEXT NOT NULL DEFAULT 'active',
exposure_chance REAL NOT NULL DEFAULT 0,exposure_roll REAL NOT NULL DEFAULT 1,
catalog_revision INTEGER NOT NULL,participants_json TEXT NOT NULL DEFAULT '[]',
provenance_summary TEXT NOT NULL DEFAULT '',snapshot_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_rumor_occurrence_active ON rumor_occurrences(campaign_id,timeline_id,status,expires_day);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS rumor_subject_tags (
occurrence_id TEXT NOT NULL,subject_id TEXT NOT NULL,tag_id TEXT NOT NULL,
subject_role TEXT NOT NULL DEFAULT '',description TEXT NOT NULL DEFAULT '',
rumor_value INTEGER NOT NULL DEFAULT 0,reputation_value INTEGER NOT NULL DEFAULT 0,
status TEXT NOT NULL DEFAULT 'active',co_participants_json TEXT NOT NULL DEFAULT '[]',
snapshot_json TEXT NOT NULL DEFAULT '{}',updated_ts INTEGER NOT NULL,
PRIMARY KEY(occurrence_id,subject_id,tag_id));");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_rumor_subject_active ON rumor_subject_tags(subject_id,status,tag_id);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS rumor_exposure_streaks (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,archetype_id TEXT NOT NULL,
thread_key TEXT NOT NULL,streak_count INTEGER NOT NULL DEFAULT 0,last_exposure_day REAL NOT NULL DEFAULT -1,
last_occurrence_id TEXT NOT NULL DEFAULT '',updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,archetype_id,thread_key));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS shared_tag_exposure_streaks (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,subject_id TEXT NOT NULL,tag_id TEXT NOT NULL,
streak_count INTEGER NOT NULL DEFAULT 0,last_exposure_day REAL NOT NULL DEFAULT -1,
last_occurrence_id TEXT NOT NULL DEFAULT '',updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,subject_id,tag_id));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS rumor_promotion_results (
occurrence_id TEXT PRIMARY KEY,streak_count INTEGER NOT NULL,promotion_chance REAL NOT NULL DEFAULT 0,
promotion_roll REAL NOT NULL DEFAULT 1,promoted INTEGER NOT NULL DEFAULT 0,
promoted_tags_json TEXT NOT NULL DEFAULT '[]',created_ts INTEGER NOT NULL);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS character_reputations (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,subject_id TEXT NOT NULL,tag_id TEXT NOT NULL,
source_occurrence_id TEXT NOT NULL DEFAULT '',archetype_id TEXT NOT NULL DEFAULT '',
subject_role TEXT NOT NULL DEFAULT '',description TEXT NOT NULL DEFAULT '',
reputation_value INTEGER NOT NULL DEFAULT 0,acquired_day REAL NOT NULL,
catalog_revision INTEGER NOT NULL,snapshot_json TEXT NOT NULL DEFAULT '{}',
status TEXT NOT NULL DEFAULT 'active',updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,subject_id,tag_id));");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_character_reputations_subject ON character_reputations(campaign_id,timeline_id,subject_id,status);");
            // Court migrations update these base tables. A fresh installation
            // must create them before applying migrations from older campaigns.
            EnsureCourtSocialReputationSchema(connection);
            EnsureCharacterSocialProfileSchema(connection);
            EnsureWhoremongerSchema(connection);
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS directional_social_projections (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,observer_id TEXT NOT NULL,subject_id TEXT NOT NULL,
rumor_total REAL NOT NULL DEFAULT 0,reputation_total REAL NOT NULL DEFAULT 0,
charm INTEGER NOT NULL DEFAULT 0,kingdom_scale REAL NOT NULL DEFAULT 0.5,
social_modifier INTEGER NOT NULL DEFAULT 0,calculated_day REAL NOT NULL DEFAULT 0,
details_json TEXT NOT NULL DEFAULT '{}',updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,observer_id,subject_id));");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "social_modifier_a_to_b", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "social_modifier_b_to_a", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "effective_affinity_a_to_b", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "effective_affinity_b_to_a", "INTEGER NOT NULL DEFAULT 0");

            Dictionary<string, object> catalog = ReadGlobalSocialCatalog();
            int activeRevision = ReadInt(catalog, "revision", BuiltInSocialCatalogRevision);
            ExecuteSql(connection, @"INSERT INTO social_catalog_revisions(revision,catalog_json,created_ts)
VALUES($revision,$catalog,$ts)
ON CONFLICT(revision) DO UPDATE SET catalog_json=excluded.catalog_json;", new Dictionary<string, object>
            {
                ["revision"] = activeRevision, ["catalog"] = Json.Serialize(catalog),
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('social_reputation_schema_version',$version);",
                new Dictionary<string, object> { ["version"] = SocialReputationSchemaVersion.ToString(CultureInfo.InvariantCulture) });
        }

        private static void MigrateLegacySocialStorage(ReignDbConnection connection)
        {
            if (ReadString(QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='character_owned_social_cleanup_v1' LIMIT 1;").FirstOrDefault(), "value", "") == "1")
                return;
            ExecuteSql(connection, "BEGIN IMMEDIATE;");
            try
            {
                foreach (string table in new[]
                {
                    "rumor_receipts", "rumor_delivery_runs", "rumor_source_rolls", "rumors", "rumor_chains",
                    "reputation_axes", "reputation_profiles", "court_knowledge", "relationship_affair_discoveries"
                })
                    ExecuteSql(connection, "DROP TABLE IF EXISTS " + table + ";");
                if (QuerySql(connection, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='conversation_sessions' LIMIT 1;").Any())
                {
                    if (QuerySql(connection, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='summaries' LIMIT 1;").Any())
                        ExecuteSql(connection, @"DELETE FROM summaries WHERE summary_id IN (
SELECT scene_summary_id FROM conversation_sessions
WHERE channel IN ('rumor_acquisition','rumor_correction') AND scene_summary_id<>'');");
                    if (QuerySql(connection, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='conversation_turns' LIMIT 1;").Any())
                        ExecuteSql(connection, @"DELETE FROM conversation_turns WHERE session_id IN (
SELECT session_id FROM conversation_sessions WHERE channel IN ('rumor_acquisition','rumor_correction'));");
                    ExecuteSql(connection, "DELETE FROM conversation_sessions WHERE channel IN ('rumor_acquisition','rumor_correction');");
                }
                if (QuerySql(connection, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='embedding_jobs' LIMIT 1;").Any())
                    ExecuteSql(connection, "DELETE FROM embedding_jobs WHERE source_type IN ('court_knowledge','rumor_history');");
                ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('character_owned_social_cleanup_v1','1');");
                ExecuteSql(connection, "COMMIT;");
            }
            catch
            {
                try { ExecuteSql(connection, "ROLLBACK;"); } catch { }
                throw;
            }
        }

        private static Dictionary<string, object> DefaultSocialCatalog()
        {
            return new Dictionary<string, object>
            {
                ["revision"] = BuiltInSocialCatalogRevision,
                ["tags"] = new List<object>
                {
                    SocialCatalogTag("disloyal", "Disloyal", "They are said to have betrayed a spouse's trust.", -5, -15),
                    SocialCatalogTag("promiscuous", "Promiscuous", "They are said to pursue intimacy outside accepted bonds.", -5, -15),
                    SocialCatalogTag("marital_strife", "Marital strife", "Their marriage is widely understood to be deeply troubled.", -5, -10),
                    SocialCatalogTag("whoremonger", "Whoremonger", "Repeated visits to the madam's house have harmed their standing.", -5, -10),
                    new Dictionary<string, object>
                    {
                        ["id"] = "divorcee", ["label"] = "Divorcee", ["builtIn"] = true, ["enabled"] = true,
                        ["description"] = "Their marriage ended in divorce.", ["rumorValue"] = 0, ["reputationValue"] = -5,
                        ["genderVariants"] = new Dictionary<string, object>
                        {
                            ["female"] = new Dictionary<string, object> { ["reputationValue"] = -20 },
                            ["male"] = new Dictionary<string, object> { ["reputationValue"] = -5 }
                        }
                    }
                },
                ["archetypes"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["id"] = "whoremonger", ["label"] = "Whoremonger", ["builtIn"] = true, ["enabled"] = true,
                        ["allowPlayerSubject"] = true, ["baseExposureChance"] = 0d,
                        ["durationDays"] = 30d, ["promotionWindowDays"] = 30d,
                        ["description"] = "Repeated visits to the madam's house became known.",
                        ["roleMappings"] = new Dictionary<string, object>
                        { ["visitor"] = new List<object> { "whoremonger" } }
                    },
                    new Dictionary<string, object>
                    {
                        ["id"] = "affair", ["label"] = "Affair", ["builtIn"] = true, ["enabled"] = true,
                        ["allowPlayerSubject"] = true,
                        ["baseExposureChance"] = 0.05d, ["durationDays"] = 45d,
                        ["description"] = "Reports of an affair have begun circulating.",
                        ["roleMappings"] = new Dictionary<string, object>
                        {
                            ["married"] = new List<object> { "disloyal", "promiscuous" },
                            ["unmarried"] = new List<object> { "promiscuous" }
                        }
                    },
                    new Dictionary<string, object>
                    {
                        ["id"] = "marital_strife", ["label"] = "Marital strife", ["builtIn"] = true, ["enabled"] = true,
                        ["baseExposureChance"] = 0.05d, ["durationDays"] = 45d,
                        ["description"] = "Reports of serious marital strife have begun circulating.",
                        ["roleMappings"] = new Dictionary<string, object>
                        {
                            ["spouse"] = new List<object> { "marital_strife" }
                        }
                    },
                    new Dictionary<string, object>
                    {
                        ["id"] = "divorce", ["label"] = "Divorce", ["builtIn"] = true, ["enabled"] = true,
                        ["baseExposureChance"] = 1d, ["durationDays"] = 0d, ["directReputation"] = true,
                        ["description"] = "Their marriage ended in divorce.",
                        ["roleMappings"] = new Dictionary<string, object>
                        {
                            ["former_spouse"] = new List<object> { "divorcee" }
                        }
                    }
                }
            };
        }

        private static Dictionary<string, object> SocialCatalogTag(string id, string label, string description, int rumorValue, int reputationValue)
        {
            return new Dictionary<string, object>
            {
                ["id"] = id, ["label"] = label, ["builtIn"] = true, ["enabled"] = true,
                ["description"] = description, ["rumorValue"] = rumorValue, ["reputationValue"] = reputationValue,
                ["genderVariants"] = new Dictionary<string, object>()
            };
        }

        private static Dictionary<string, object> ReadActiveSocialCatalog(ReignDbConnection connection)
        {
            Dictionary<string, object> catalog = ReadGlobalSocialCatalog();
            int revision = ReadInt(catalog, "revision", BuiltInSocialCatalogRevision);
            ExecuteSql(connection, @"INSERT OR IGNORE INTO social_catalog_revisions(revision,catalog_json,created_ts)
VALUES($revision,$catalog,$ts);", new Dictionary<string, object>
            {
                ["revision"] = revision, ["catalog"] = Json.Serialize(catalog),
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            return catalog;
        }

        private static Dictionary<string, object> ReadGlobalSocialCatalog()
        {
            lock (FileLock)
            {
                string catalogPath = Path.GetFullPath(SocialCatalogPath);
                long writeTicks = File.Exists(catalogPath)
                    ? File.GetLastWriteTimeUtc(catalogPath).Ticks
                    : 0L;
                if (!string.IsNullOrWhiteSpace(SocialCatalogCachedJson)
                    && string.Equals(catalogPath, SocialCatalogCachedPath,
                        StringComparison.OrdinalIgnoreCase)
                    && writeTicks == SocialCatalogCachedWriteTicks)
                {
                    Dictionary<string, object> cached =
                        Json.Deserialize<Dictionary<string, object>>(
                            SocialCatalogCachedJson)
                        ?? new Dictionary<string, object>();
                    // Catalog edits and older persisted catalogs can leave the
                    // cache structurally valid but missing runtime-required
                    // built-in mappings/counterparts. The disk-load path has
                    // always repaired those fields; the cache fast path must
                    // enforce the same invariant rather than bypassing it.
                    if (EnsureExpandedSocialCatalog(cached))
                    {
                        WriteJsonObject(catalogPath, cached);
                        SocialCatalogCachedJson = Json.Serialize(cached);
                        SocialCatalogCachedWriteTicks = File.Exists(catalogPath)
                            ? File.GetLastWriteTimeUtc(catalogPath).Ticks
                            : 0L;
                    }
                    return cached;
                }
                Dictionary<string, object> catalog = ReadJsonObject(catalogPath);
                if (catalog.Count == 0) catalog = DefaultSocialCatalog();
                bool changed = EnsureExpandedSocialCatalog(catalog);
                if (changed || !File.Exists(catalogPath))
                    WriteJsonObject(catalogPath, catalog);
                SocialCatalogCachedJson = Json.Serialize(catalog);
                SocialCatalogCachedPath = catalogPath;
                SocialCatalogCachedWriteTicks = File.Exists(catalogPath)
                    ? File.GetLastWriteTimeUtc(catalogPath).Ticks
                    : 0L;
                return catalog;
            }
        }

        private static Dictionary<string, object> SocialCatalogApi(Dictionary<string, object> payload)
        {
            string campaignId = ReadString(payload, "campaignId", "default");
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                Dictionary<string, object> catalog = ReadActiveSocialCatalog(connection);
                catalog["ok"] = true;
                catalog["charmPreview"] = new List<object>
                {
                    SocialCharmPreview(0), SocialCharmPreview(100), SocialCharmPreview(200), SocialCharmPreview(300)
                };
                return catalog;
            }
        }

        private static Dictionary<string, object> SocialCatalogUpdateApi(Dictionary<string, object> payload)
        {
            string campaignId = ReadString(payload, "campaignId", "default");
            Dictionary<string, object> proposed = DictionaryOrDefault(payload, "catalog", payload);
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                Dictionary<string, object> current = ReadGlobalSocialCatalog();
                List<string> errors = ValidateSocialCatalog(connection, proposed, current);
                if (errors.Count > 0)
                    return new Dictionary<string, object> { ["ok"] = false, ["errors"] = errors };
                int revision = ReadInt(current, "revision", 1) + 1;
                proposed["revision"] = revision;
                lock (FileLock)
                {
                    string catalogPath = Path.GetFullPath(SocialCatalogPath);
                    WriteJsonObject(catalogPath, proposed);
                    SocialCatalogCachedJson = Json.Serialize(proposed);
                    SocialCatalogCachedPath = catalogPath;
                    SocialCatalogCachedWriteTicks =
                        File.GetLastWriteTimeUtc(catalogPath).Ticks;
                }
                ExecuteSql(connection, "INSERT INTO social_catalog_revisions(revision,catalog_json,created_ts) VALUES($revision,$json,$ts);",
                    new Dictionary<string, object>
                    {
                        ["revision"] = revision, ["json"] = Json.Serialize(proposed),
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    });
                return new Dictionary<string, object> { ["ok"] = true, ["revision"] = revision, ["catalog"] = proposed };
            }
        }

        private static List<string> ValidateSocialCatalog(ReignDbConnection connection, Dictionary<string, object> proposed, Dictionary<string, object> current)
        {
            List<string> errors = new List<string>();
            List<Dictionary<string, object>> tags = ReadDictionaryList(proposed, "tags");
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> tag in tags)
            {
                string id = ReadString(tag, "id", "").Trim();
                if (string.IsNullOrWhiteSpace(id) || !ids.Add(id)) errors.Add("Every social entry requires a unique ID.");
                int rumorValue = ReadInt(tag, "rumorValue", 0);
                int reputationValue = ReadInt(tag, "reputationValue", 0);
                if (rumorValue < -10 || rumorValue > 10) errors.Add(id + ": Rumor value must be between -10 and 10.");
                if (reputationValue < -50 || reputationValue > 50) errors.Add(id + ": Reputation value must be between -50 and 50.");
                foreach (Dictionary<string, object> variant in DictionaryOrDefault(tag, "genderVariants", new Dictionary<string, object>()).Values.OfType<Dictionary<string, object>>())
                {
                    int variantValue = ReadInt(variant, "reputationValue", reputationValue);
                    if (variantValue < -50 || variantValue > 50) errors.Add(id + ": gender reputation value must be between -50 and 50.");
                }
            }
            HashSet<string> proposedIds = new HashSet<string>(tags.Select(x => ReadString(x, "id", "")), StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> tag in tags)
                foreach (string counterpart in TextList(tag, "counterpartTagIds"))
                    if (!proposedIds.Contains(counterpart))
                        errors.Add(ReadString(tag, "id", "") + ": counterevidence entry '" + counterpart + "' does not exist.");
            foreach (Dictionary<string, object> builtIn in ReadDictionaryList(current, "tags").Where(x => ReadBool(x, "builtIn", false)))
                if (!proposedIds.Contains(ReadString(builtIn, "id", ""))) errors.Add("Built-in entry '" + ReadString(builtIn, "id", "") + "' cannot be deleted.");
            foreach (Dictionary<string, object> custom in ReadDictionaryList(current, "tags").Where(x => !ReadBool(x, "builtIn", false)))
            {
                string customId = ReadString(custom, "id", "");
                if (proposedIds.Contains(customId)) continue;
                bool used = QuerySql(connection, @"SELECT 1 FROM (
SELECT tag_id FROM rumor_subject_tags UNION ALL SELECT tag_id FROM character_reputations
) WHERE tag_id=$id LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = customId }).Any();
                if (used) errors.Add("Custom entry '" + customId + "' is already used and cannot be deleted.");
            }
            List<Dictionary<string, object>> proposedArchetypes = ReadDictionaryList(proposed, "archetypes");
            HashSet<string> archetypeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> archetype in proposedArchetypes)
            {
                string archetypeId = ReadString(archetype, "id", "").Trim();
                if (string.IsNullOrWhiteSpace(archetypeId) || !archetypeIds.Add(archetypeId))
                    errors.Add("Every social producer requires a unique ID.");
                double chance = ReadDouble(archetype, "baseExposureChance", 0d);
                double duration = ReadDouble(archetype, "durationDays", 45d);
                double promotionWindow = ReadDouble(archetype, "promotionWindowDays", 45d);
                if (chance < 0d || chance > 1d) errors.Add(archetypeId + ": exposure chance must be between 0 and 1.");
                if (duration < 0d) errors.Add(archetypeId + ": duration cannot be negative.");
                if (promotionWindow < 0d) errors.Add(archetypeId + ": promotion window cannot be negative.");
                foreach (string tagId in DictionaryOrDefault(archetype, "roleMappings", new Dictionary<string, object>())
                    .Values.SelectMany(ValueTextList))
                    if (!proposedIds.Contains(tagId)) errors.Add(archetypeId + ": mapped social entry '" + tagId + "' does not exist.");
            }
            foreach (Dictionary<string, object> builtIn in ReadDictionaryList(current, "archetypes").Where(x => ReadBool(x, "builtIn", false)))
                if (!archetypeIds.Contains(ReadString(builtIn, "id", "")))
                    errors.Add("Built-in producer '" + ReadString(builtIn, "id", "") + "' cannot be deleted.");

            Dictionary<string, object> governance = DictionaryOrDefault(proposed, "governanceSettings", new Dictionary<string, object>());
            foreach (string key in new[] { "accessionGraceDays", "sampleRetentionDays", "postSiegeRecoveryDays", "integrationFirstDay", "integrationHardExitDay" })
                if (ReadDouble(governance, key, 0d) < 0d) errors.Add("Governance setting '" + key + "' cannot be negative.");
            if (ReadDouble(governance, "sampleRetentionDays", 35d) < 35d)
                errors.Add("Governance sample retention must preserve at least thirty-five days.");
            if (ReadDouble(governance, "integrationHardExitDay", 45d) < ReadDouble(governance, "integrationFirstDay", 30d))
                errors.Add("Integration hard exit cannot precede its first evaluation.");
            if (!(ReadDouble(governance, "loyaltyDanger", 25d) < ReadDouble(governance, "loyaltyStable", 40d)
                && ReadDouble(governance, "loyaltyStable", 40d) <= ReadDouble(governance, "loyaltySecure", 50d)))
                errors.Add("Loyalty thresholds must be ordered danger < stable <= secure.");
            if (!(ReadDouble(governance, "securityCritical", 15d) < ReadDouble(governance, "securityLow", 30d)
                && ReadDouble(governance, "securityLow", 30d) < ReadDouble(governance, "securityRecovered", 50d)
                && ReadDouble(governance, "securityRecovered", 50d) <= ReadDouble(governance, "securityStrong", 60d)))
                errors.Add("Security thresholds must be ordered critical < low < recovered <= strong.");

            Dictionary<string, object> war = DictionaryOrDefault(proposed, "warScoreSettings", new Dictionary<string, object>());
            foreach (string key in new[] { "townWeight", "castleWeight", "casualtyCap", "siegeWeight", "raidWeight", "raidCap", "rulerCaptureWeight", "tributeWeight" })
                if (ReadDouble(war, key, 0d) < 0d) errors.Add("War score setting '" + key + "' cannot be negative.");
            if (ReadDouble(war, "victoryMargin", 15d) <= 0d) errors.Add("War victory margin must be greater than zero.");
            return errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static Dictionary<string, object> RegisterSocialOccurrenceApi(Dictionary<string, object> payload)
        {
            string campaignId = ReadString(payload, "campaignId", "default");
            Dictionary<string, object> result;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                result = RegisterSocialOccurrence(connection, campaignId, payload);
            }
            SchedulePendingReputationReasonJobs(campaignId);
            return result;
        }

        private static Dictionary<string, object> BuildRelationshipSocialOccurrence(AmbientPairContext pair, int day,
            string archetypeId, Dictionary<string, object> heroA, Dictionary<string, object> heroB,
            string spouseA, string spouseB, bool forceExposure, bool forcePromotion, string provenance,
            string timelineId = "main")
        {
            string roleA = archetypeId == "affair"
                ? (string.IsNullOrWhiteSpace(spouseA) ? "unmarried" : "married")
                : archetypeId == "divorce" ? "former_spouse" : "spouse";
            string roleB = archetypeId == "affair"
                ? (string.IsNullOrWhiteSpace(spouseB) ? "unmarried" : "married")
                : archetypeId == "divorce" ? "former_spouse" : "spouse";
            Func<Dictionary<string, object>, string, Dictionary<string, object>> participant = (hero, role) =>
            {
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["subjectId"] = ReadFirstString(hero, "heroStringId", "heroId", "id"),
                    ["role"] = role
                };
                if (hero.ContainsKey("clanTier")) result["clanTier"] = ReadInt(hero, "clanTier", 0);
                if (hero.ContainsKey("sex") || hero.ContainsKey("isFemale"))
                    result["sex"] = ReadString(hero, "sex", ReadBool(hero, "isFemale", false) ? "female" : "male");
                return result;
            };
            return new Dictionary<string, object>
            {
                ["timelineId"] = string.IsNullOrWhiteSpace(timelineId)
                    ? "main" : timelineId, ["archetypeId"] = archetypeId,
                ["threadKey"] = pair.PairKey, ["worldDay"] = (double)day,
                ["forceExposure"] = forceExposure, ["forcePromotion"] = forcePromotion,
                ["provenanceSummary"] = provenance,
                ["participants"] = new List<object> { participant(heroA, roleA), participant(heroB, roleB) }
            };
        }

        private static Dictionary<string, object> RegisterSocialOccurrence(
            ReignDbConnection connection, string campaignId,
            Dictionary<string, object> payload,
            Dictionary<string, object> socialCatalog = null,
            bool worldTestSchemaReady = false,
            bool useExistingTransaction = false,
            bool deferSubjectReconciliation = false,
            Action<string, Dictionary<string, object>> telemetrySink = null)
        {
            string timelineId = ReadString(payload, "timelineId", "main");
            string archetypeId = ReadString(payload, "archetypeId", "");
            string threadKey = ReadString(payload, "threadKey", "");
            double worldDay = archetypeId == "whoremonger" ? ReadDouble(payload, "worldDay", 0d)
                : Math.Floor(ReadDouble(payload, "worldDay", 0d) + 0.000001d);
            int worldDayKey = (int)worldDay;
            bool forceExposure = ReadBool(payload, "forceExposure", false);
            bool forcePromotion = ReadBool(payload, "forcePromotion", false);
            Action<string, Dictionary<string, object>> recordWorldTest =
                (chunkKey, counters) =>
                {
                    if (telemetrySink != null)
                    {
                        telemetrySink(chunkKey, counters);
                        return;
                    }
                    if (worldTestSchemaReady)
                        RecordWorldTestCounterSchemaReady(connection,
                            campaignId, timelineId, worldDayKey, "rumors",
                            chunkKey, counters);
                    else
                        RecordWorldTestCounter(connection, campaignId,
                            timelineId, worldDayKey, "rumors", chunkKey,
                            counters);
                };
            string hookKey = "hook_" + DeterministicSocialId(string.Join("|",
                campaignId, timelineId, archetypeId, threadKey,
                ReadString(payload, "sourceEventId", worldDayKey.ToString(CultureInfo.InvariantCulture))));
            if (string.IsNullOrWhiteSpace(archetypeId) || string.IsNullOrWhiteSpace(threadKey))
            {
                recordWorldTest(hookKey + "_invalid",
                    new Dictionary<string, object>
                    {
                        ["ineligibleParticipants"] = 1
                    });
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "archetypeId and threadKey are required." };
            }

            Dictionary<string, object> catalog = socialCatalog
                ?? ReadActiveSocialCatalog(connection);
            Dictionary<string, object> archetype = ReadDictionaryList(catalog, "archetypes")
                .FirstOrDefault(x => string.Equals(ReadString(x, "id", ""), archetypeId, StringComparison.OrdinalIgnoreCase));
            if (archetype == null || !ReadBool(archetype, "enabled", true))
            {
                Dictionary<string, object> disabledCounters = new Dictionary<string, object>
                {
                    ["eligibleHooks"] = 1,
                    ["disabledArchetypes"] = 1
                };
                AddSocialRumorTelemetryDimensions(disabledCounters, archetypeId,
                    new List<Dictionary<string, object>>(), payload);
                recordWorldTest(hookKey + "_disabled", disabledCounters);
                return new Dictionary<string, object> { ["ok"] = true, ["skipped"] = true, ["reason"] = "archetype_disabled_or_missing" };
            }

            bool allowPlayerSubject = ReadBool(archetype, "allowPlayerSubject", false);
            List<Dictionary<string, object>> suppliedParticipants = ReadDictionaryList(payload, "participants");
            if (archetypeId.StartsWith("ruler_favoring", StringComparison.OrdinalIgnoreCase))
            {
                ExpireRulerFavorContact(connection, campaignId, timelineId, worldDay);
                suppliedParticipants = suppliedParticipants.Where(participant => !RulerFavorRequiresFreshContact(connection,
                    campaignId, timelineId, ReadFirstString(participant, "subjectId", "heroId", "id"),
                    ReadString(participant, "linkedHeroId", ""))).ToList();
            }
            List<Dictionary<string, object>> participants = suppliedParticipants
                .Where(x => !string.IsNullOrWhiteSpace(ReadFirstString(x, "subjectId", "heroId", "id"))
                    && (allowPlayerSubject || !ReadBool(x, "isPlayer", false)) && ReadBool(x, "isAlive", true)
                    && ReadBool(x, "isAdult", !ReadBool(x, "isChild", false))).ToList();
            int ineligibleParticipants = suppliedParticipants.Count - participants.Count;
            if (participants.Count == 0)
            {
                Dictionary<string, object> emptyCounters = new Dictionary<string, object>
                {
                    ["eligibleHooks"] = 1,
                    ["ineligibleParticipants"] = Math.Max(1, ineligibleParticipants)
                };
                AddSocialRumorTelemetryDimensions(emptyCounters, archetypeId, participants, payload);
                recordWorldTest(hookKey + "_no_participants",
                    emptyCounters);
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "At least one participant is required." };
            }
            foreach (Dictionary<string, object> participant in participants)
            {
                string subjectId = ReadFirstString(participant, "subjectId", "heroId", "id");
                Dictionary<string, object> roster = QuerySql(connection,
                    "SELECT * FROM identity_roster WHERE hero_id=$id LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = subjectId }).FirstOrDefault();
                if (roster != null)
                {
                    if ((!allowPlayerSubject && ReadInt(roster, "is_player", 0) != 0) || ReadInt(roster, "is_alive", 1) == 0
                        || ReadInt(roster, "is_adult", 1) == 0)
                    {
                        participant["_excluded"] = true;
                        ineligibleParticipants++;
                        continue;
                    }
                    if (archetypeId.StartsWith("ruler_favoring", StringComparison.OrdinalIgnoreCase)
                        && ReadInt(roster, "is_ruler", 0) == 0)
                    {
                        participant["_excluded"] = true;
                        ineligibleParticipants++;
                        continue;
                    }
                    if (!participant.ContainsKey("clanTier")) participant["clanTier"] = ReadInt(roster, "clan_tier", 0);
                    if (!participant.ContainsKey("sex")) participant["sex"] = ReadString(roster, "sex", "");
                }
            }
            participants = participants.Where(x => !ReadBool(x, "_excluded", false)).ToList();
            foreach (Dictionary<string, object> participant in participants) participant.Remove("_excluded");
            if (participants.Count == 0)
            {
                Dictionary<string, object> excludedCounters = new Dictionary<string, object>
                {
                    ["eligibleHooks"] = 1,
                    ["ineligibleParticipants"] = Math.Max(1, ineligibleParticipants)
                };
                AddSocialRumorTelemetryDimensions(excludedCounters, archetypeId, participants, payload);
                recordWorldTest(hookKey + "_excluded", excludedCounters);
                return new Dictionary<string, object> { ["ok"] = true, ["skipped"] = true, ["reason"] = "no_eligible_npc_participants" };
            }
            int highestTier = Math.Max(1, Math.Min(6, participants.Max(x => Math.Max(1, ReadInt(x, "clanTier", 0)))));
            double multiplier = highestTier * 0.5d;
            double chance = payload.ContainsKey("exposureChanceOverride")
                ? ClampDouble(ReadDouble(payload, "exposureChanceOverride", 0d), 0d, 1d)
                : Math.Min(1d, Math.Max(0d,
                    ReadDouble(archetype, "baseExposureChance", 0d)
                        * multiplier));
            string sourceEventId = ReadString(payload, "sourceEventId", "");
            string exposureSeedKey = ReadString(payload, "exposureSeedKey",
                string.IsNullOrWhiteSpace(sourceEventId)
                    ? Math.Floor(worldDay).ToString(CultureInfo.InvariantCulture)
                    : sourceEventId);
            string exposureSeed = string.Join("|", campaignId, timelineId, archetypeId, threadKey,
                exposureSeedKey,
                "exposure");
            double exposureRoll = DeterministicSocialRoll(exposureSeed);
            if (!forceExposure && exposureRoll >= chance)
            {
                Dictionary<string, object> failedCounters = new Dictionary<string, object>
                {
                    ["eligibleHooks"] = 1,
                    ["ineligibleParticipants"] = ineligibleParticipants,
                    ["exposureAttempts"] = 1,
                    ["exposureFailed"] = 1
                };
                AddSocialRumorTelemetryDimensions(failedCounters, archetypeId, participants, payload);
                recordWorldTest("exposure_"
                    + DeterministicSocialId(exposureSeed) + "_failed",
                    failedCounters);
                return new Dictionary<string, object> { ["ok"] = true, ["exposed"] = false, ["chance"] = chance, ["roll"] = exposureRoll };
            }

            string occurrenceId = FirstNonEmpty(ReadString(payload, "occurrenceId", ""),
                "rumor_occurrence_" + DeterministicSocialId(exposureSeed));
            Dictionary<string, object> duplicate = QuerySql(connection,
                string.IsNullOrWhiteSpace(sourceEventId)
                    ? "SELECT occurrence_id FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id=$archetype AND thread_key=$thread AND world_day=$day LIMIT 1;"
                    : "SELECT occurrence_id FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id=$archetype AND thread_key=$thread AND source_event_id=$event LIMIT 1;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["archetype"] = archetypeId, ["thread"] = threadKey, ["day"] = worldDay, ["event"] = sourceEventId }).FirstOrDefault();
            if (duplicate != null)
            {
                Dictionary<string, object> duplicateCounters = new Dictionary<string, object>
                {
                    ["eligibleHooks"] = 1,
                    ["ineligibleParticipants"] = ineligibleParticipants,
                    ["exposureAttempts"] = 1,
                    ["exposurePassed"] = 1,
                    ["duplicateSourcesSuppressed"] = 1
                };
                AddSocialRumorTelemetryDimensions(duplicateCounters, archetypeId, participants, payload);
                recordWorldTest("exposure_"
                    + DeterministicSocialId(exposureSeed) + "_duplicate",
                    duplicateCounters);
                return new Dictionary<string, object> { ["ok"] = true,
                    ["duplicate"] = true, ["exposed"] = true,
                    ["chance"] = chance, ["roll"] = exposureRoll,
                    ["occurrenceId"] = ReadString(duplicate,
                        "occurrence_id", "") };
            }

            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int catalogRevision = ReadInt(catalog, "revision", 1);
            double expiresDay = worldDay + ReadDouble(archetype, "durationDays", 45d);
            Dictionary<string, object> streak = QuerySql(connection, @"SELECT * FROM rumor_exposure_streaks
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id=$archetype AND thread_key=$thread LIMIT 1;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["archetype"] = archetypeId, ["thread"] = threadKey }).FirstOrDefault();
            double promotionWindowDays = ReadDouble(archetype, "promotionWindowDays", 45d);
            // Whoremonger owns an inactivity deadline refreshed by every player visit,
            // including failed exposures. Its dedicated reset owns the promotion streak.
            int streakCount = streak == null || (archetypeId != "whoremonger"
                && worldDay - ReadDouble(streak, "last_exposure_day", -1000d) > promotionWindowDays)
                ? 1 : ReadInt(streak, "streak_count", 0) + 1;
            double promotionChance = streakCount <= 1 ? 0d : streakCount == 2 ? 0.30d : streakCount == 3 ? 0.60d : 0.90d;
            double promotionRoll = DeterministicSocialRoll(exposureSeed + "|promotion");
            bool promoted = forcePromotion || promotionRoll < promotionChance || ReadBool(archetype, "directReputation", false);
            string provenance = ReadString(payload, "provenanceSummary", ReadString(archetype, "description", ""));

            List<object> promotedTags = new List<object>();
            List<object> sharedPromotionResults = new List<object>();
            bool anySharedPromotionRoll = false;
            if (!useExistingTransaction)
                ExecuteSql(connection, "BEGIN IMMEDIATE;");
            try
            {
                ExecuteSql(connection, @"INSERT INTO rumor_occurrences(occurrence_id,campaign_id,timeline_id,archetype_id,thread_key,source_event_id,
world_day,expires_day,status,exposure_chance,exposure_roll,catalog_revision,participants_json,provenance_summary,snapshot_json,created_ts,updated_ts)
VALUES($id,$campaign,$timeline,$archetype,$thread,$event,$day,$expires,'active',$chance,$roll,$revision,$participants,$summary,$snapshot,$ts,$ts);",
                    new Dictionary<string, object>
                    {
                        ["id"] = occurrenceId, ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["archetype"] = archetypeId, ["thread"] = threadKey,
                        ["event"] = ReadString(payload, "sourceEventId", ""), ["day"] = worldDay, ["expires"] = expiresDay,
                        ["chance"] = chance, ["roll"] = exposureRoll, ["revision"] = catalogRevision,
                        ["participants"] = Json.Serialize(participants), ["summary"] = provenance,
                        ["snapshot"] = Json.Serialize(new Dictionary<string, object> { ["archetype"] = archetype, ["catalogRevision"] = catalogRevision }), ["ts"] = ts
                    });
                foreach (Dictionary<string, object> participant in participants)
                {
                    string subjectId = ReadFirstString(participant, "subjectId", "heroId", "id");
                    string role = ReadString(participant, "role", "");
                    foreach (string baseTagId in SocialRoleTags(archetype, role))
                    {
                        Dictionary<string, object> tag = ReadDictionaryList(catalog, "tags")
                            .FirstOrDefault(x => string.Equals(ReadString(x, "id", ""), baseTagId, StringComparison.OrdinalIgnoreCase));
                        if (tag == null || !ReadBool(tag, "enabled", true)) continue;
                        Dictionary<string, object> snap = SocialTagSnapshot(tag, participant);
                        string tagId = SocialTagStorageId(tag, participant);
                        snap["baseTagId"] = baseTagId;
                        snap["storageTagId"] = tagId;
                        if (baseTagId == "ruler_favoring")
                            ExecuteSql(connection, @"INSERT INTO court_ruler_favor_contact(campaign_id,timeline_id,ruler_id,favorite_id,grace_day)
VALUES($campaign,$timeline,$ruler,$favorite,$day) ON CONFLICT(campaign_id,timeline_id,ruler_id,favorite_id) DO NOTHING;",
                                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["ruler"] = subjectId,
                                    ["favorite"] = ReadString(snap, "linkedHeroId", ""), ["day"] = worldDay });
                        ExecuteSql(connection, @"INSERT INTO rumor_subject_tags(occurrence_id,subject_id,tag_id,subject_role,description,rumor_value,reputation_value,status,co_participants_json,snapshot_json,updated_ts)
VALUES($occurrence,$subject,$tag,$role,$description,$rumor,$reputation,'active',$others,$snapshot,$ts);",
                            new Dictionary<string, object>
                            {
                                ["occurrence"] = occurrenceId, ["subject"] = subjectId, ["tag"] = tagId, ["role"] = role,
                                ["description"] = ReadString(snap, "description", ""), ["rumor"] = ReadInt(snap, "rumorValue", 0),
                                ["reputation"] = ReadInt(snap, "reputationValue", 0),
                                ["others"] = Json.Serialize(participants.Select(x => ReadFirstString(x, "subjectId", "heroId", "id")).Where(x => !string.Equals(x, subjectId, StringComparison.OrdinalIgnoreCase)).ToList()),
                                ["snapshot"] = Json.Serialize(snap), ["ts"] = ts
                            });
                        bool sharedPromoted = false;
                        if (ReadBool(tag, "sharedPromotionStreak", false))
                        {
                            Dictionary<string, object> sharedStreak = QuerySql(connection, @"SELECT *
FROM shared_tag_exposure_streaks WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$subject AND tag_id=$tag LIMIT 1;",
                                new Dictionary<string, object>
                                {
                                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                                    ["subject"] = subjectId, ["tag"] = tagId
                                }).FirstOrDefault();
                            int sharedCount = sharedStreak == null
                                || worldDay - ReadDouble(sharedStreak, "last_exposure_day", -1000d) > promotionWindowDays
                                ? 1 : ReadInt(sharedStreak, "streak_count", 0) + 1;
                            double sharedChance = sharedCount <= 1 ? 0d
                                : sharedCount == 2 ? 0.30d : sharedCount == 3 ? 0.60d : 0.90d;
                            double sharedRoll = DeterministicSocialRoll(string.Join("|", campaignId,
                                timelineId, subjectId, tagId, occurrenceId, "shared_tag_promotion"));
                            sharedPromoted = forcePromotion || sharedRoll < sharedChance;
                            anySharedPromotionRoll |= sharedPromoted;
                            ExecuteSql(connection, @"INSERT INTO shared_tag_exposure_streaks(
campaign_id,timeline_id,subject_id,tag_id,streak_count,last_exposure_day,last_occurrence_id,updated_ts)
VALUES($campaign,$timeline,$subject,$tag,$count,$day,$occurrence,$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,tag_id) DO UPDATE SET
streak_count=$count,last_exposure_day=$day,last_occurrence_id=$occurrence,updated_ts=$ts;",
                                new Dictionary<string, object>
                                {
                                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                                    ["subject"] = subjectId, ["tag"] = tagId, ["count"] = sharedCount,
                                    ["day"] = worldDay, ["occurrence"] = occurrenceId, ["ts"] = ts
                                });
                            sharedPromotionResults.Add(new Dictionary<string, object>
                            {
                                ["subjectId"] = subjectId, ["tagId"] = tagId,
                                ["streakCount"] = sharedCount, ["promotionChance"] = sharedChance,
                                ["promotionRoll"] = sharedRoll, ["promoted"] = sharedPromoted
                            });
                        }
                        if ((promoted || sharedPromoted)
                            && PromoteSocialTag(connection, campaignId, timelineId, occurrenceId, archetypeId, subjectId, role, tagId, snap, worldDay, catalogRevision, ts))
                            promotedTags.Add(new Dictionary<string, object> { ["subjectId"] = subjectId, ["tagId"] = tagId });
                    }
                }
                ExecuteSql(connection, @"INSERT OR REPLACE INTO rumor_exposure_streaks(campaign_id,timeline_id,archetype_id,thread_key,streak_count,last_exposure_day,last_occurrence_id,updated_ts)
VALUES($campaign,$timeline,$archetype,$thread,$count,$day,$occurrence,$ts);",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["archetype"] = archetypeId, ["thread"] = threadKey, ["count"] = streakCount, ["day"] = worldDay, ["occurrence"] = occurrenceId, ["ts"] = ts });
                ExecuteSql(connection, @"INSERT INTO rumor_promotion_results(occurrence_id,streak_count,promotion_chance,promotion_roll,promoted,promoted_tags_json,created_ts)
VALUES($occurrence,$count,$chance,$roll,$promoted,$tags,$ts);",
                    new Dictionary<string, object> { ["occurrence"] = occurrenceId, ["count"] = streakCount, ["chance"] = promotionChance, ["roll"] = promotionRoll, ["promoted"] = promoted || anySharedPromotionRoll ? 1 : 0, ["tags"] = Json.Serialize(promotedTags), ["ts"] = ts });
                Dictionary<string, object> occurrenceCounters = new Dictionary<string, object>
                {
                    ["eligibleHooks"] = 1,
                    ["ineligibleParticipants"] = ineligibleParticipants,
                    ["exposureAttempts"] = 1,
                    ["exposurePassed"] = 1,
                    ["occurrencesCreated"] = 1,
                    ["subjectTagsCreated"] = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM rumor_subject_tags WHERE occurrence_id=$id;",
                        new Dictionary<string, object> { ["id"] = occurrenceId }).FirstOrDefault(), "count", 0),
                    ["streakAdvanced"] = 1,
                    ["promotionAttempts"] = 1,
                    ["promotionPassed"] = promoted || anySharedPromotionRoll ? 1 : 0,
                    ["promotionFailed"] = promoted || anySharedPromotionRoll ? 0 : 1,
                    ["durableReputationsCreated"] = promotedTags.Count
                };
                AddSocialRumorTelemetryDimensions(occurrenceCounters, archetypeId, participants, payload);
                IncrementWorldTestObjectCounter(occurrenceCounters,
                    "occurrenceLifetime:" + SocialRumorDurationBucket(expiresDay - worldDay), 1);
                if (streak != null)
                    IncrementWorldTestObjectCounter(occurrenceCounters,
                        "exposureToPromotion:" + SocialRumorDurationBucket(
                            worldDay - ReadDouble(streak, "last_exposure_day", worldDay)), 1);
                if (payload.ContainsKey("sourceEventDay"))
                    IncrementWorldTestObjectCounter(occurrenceCounters,
                        "eventToExposure:" + SocialRumorDurationBucket(
                            worldDay - ReadDouble(payload, "sourceEventDay", worldDay)), 1);
                bool immediateExpiration = expiresDay <= worldDay + 0.000001d;
                if (immediateExpiration)
                {
                    ExecuteSql(connection, @"UPDATE rumor_occurrences
SET status='expired',updated_ts=$ts WHERE occurrence_id=$id AND status='active';
UPDATE rumor_subject_tags SET status='expired',updated_ts=$ts
WHERE occurrence_id=$id AND status='active';",
                        new Dictionary<string, object>
                        {
                            ["id"] = occurrenceId, ["ts"] = ts
                        });
                    occurrenceCounters["occurrencesExpired"] = 1;
                    occurrenceCounters["immediateFinalizations"] = 1;
                }
                recordWorldTest("occurrence_" + occurrenceId,
                    occurrenceCounters);
                if (!useExistingTransaction)
                    ExecuteSql(connection, "COMMIT;");
            }
            catch
            {
                if (!useExistingTransaction)
                {
                    try { ExecuteSql(connection, "ROLLBACK;"); } catch { }
                }
                throw;
            }
            if (!deferSubjectReconciliation)
                ReconcileSocialRelationshipsForSubjects(connection, campaignId, timelineId,
                    participants.Select(x => ReadFirstString(x, "subjectId", "heroId", "id")), worldDay);
            if (promotedTags.Count > 0 && !campaignId.StartsWith("__", StringComparison.Ordinal))
                SchedulePendingReputationReasonJobs(campaignId);
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["exposed"] = true, ["occurrenceId"] = occurrenceId,
                ["occurrenceStatus"] = expiresDay <= worldDay + 0.000001d
                    ? "expired" : "active",
                ["chance"] = chance, ["roll"] = exposureRoll,
                ["exposureChance"] = chance, ["exposureRoll"] = exposureRoll,
                ["streakCount"] = streakCount, ["promotionChance"] = promotionChance,
                ["promotionRoll"] = promotionRoll, ["promoted"] = promoted || anySharedPromotionRoll,
                ["promotedTags"] = promotedTags, ["sharedPromotionResults"] = sharedPromotionResults
            };
        }

        private static bool PromoteSocialTag(ReignDbConnection connection, string campaignId, string timelineId,
            string occurrenceId, string archetypeId, string subjectId, string role, string tagId,
            Dictionary<string, object> snapshot, double worldDay, int catalogRevision, long ts)
        {
            bool isNew = QuerySql(connection, @"SELECT 1 AS found FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag AND status='active' LIMIT 1;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["tag"] = tagId }).Count == 0;
            ExecuteSql(connection, @"INSERT INTO character_reputations(campaign_id,timeline_id,subject_id,tag_id,
source_occurrence_id,archetype_id,subject_role,description,reputation_value,acquired_day,catalog_revision,snapshot_json,status,updated_ts)
VALUES($campaign,$timeline,$subject,$tag,$occurrence,$archetype,$role,$description,$value,$day,$revision,$snapshot,'active',$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,tag_id) DO UPDATE SET
source_occurrence_id=$occurrence,archetype_id=$archetype,subject_role=$role,description=$description,
reputation_value=$value,acquired_day=$day,catalog_revision=$revision,snapshot_json=$snapshot,status='active',updated_ts=$ts
WHERE character_reputations.status<>'active';",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["tag"] = tagId,
                    ["occurrence"] = occurrenceId, ["archetype"] = archetypeId, ["role"] = role,
                    ["description"] = ReadString(snapshot, "description", ""), ["value"] = ReadInt(snapshot, "reputationValue", 0),
                    ["day"] = worldDay, ["revision"] = catalogRevision, ["snapshot"] = Json.Serialize(snapshot), ["ts"] = ts
                });
            ExecuteSql(connection, @"UPDATE rumor_subject_tags SET status='superseded_by_reputation',updated_ts=$ts
WHERE subject_id=$subject AND tag_id=$tag AND status='active'
AND ($tag<>'whoremonger' OR occurrence_id IN (SELECT occurrence_id FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline));",
                new Dictionary<string, object> { ["subject"] = subjectId, ["tag"] = tagId, ["ts"] = ts,
                    ["campaign"] = campaignId, ["timeline"] = timelineId });
            if (isNew)
            {
                string incident = SocialOccurrenceIncident(connection, occurrenceId, ReadString(snapshot, "description", ""));
                RecordReputationActivation(connection, campaignId, timelineId, subjectId, tagId, occurrenceId,
                    "", worldDay, ReadString(snapshot, "description", ""), incident,
                    new Dictionary<string, object>
                    {
                        ["source"] = "rumor_promotion", ["sourceOccurrenceId"] = occurrenceId,
                        ["archetypeId"] = archetypeId, ["subjectRole"] = role, ["incidentDescription"] = incident
                    });
            }
            return isNew;
        }

        private static List<string> SocialRoleTags(Dictionary<string, object> archetype, string role)
        {
            Dictionary<string, object> mappings = DictionaryOrDefault(archetype, "roleMappings", new Dictionary<string, object>());
            if (!mappings.TryGetValue(role ?? "", out object value)) return new List<string>();
            if (value is IEnumerable<object> objects)
                return objects.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (value is IEnumerable<string> strings)
                return strings.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (value is IEnumerable sequence)
            {
                List<string> result = new List<string>();
                foreach (object item in sequence)
                {
                    string text = Convert.ToString(item, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(text)) result.Add(text);
                }
                return result;
            }
            return new List<string>();
        }

        private static Dictionary<string, object> SocialTagSnapshot(Dictionary<string, object> tag, Dictionary<string, object> participant)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(tag, StringComparer.OrdinalIgnoreCase);
            string sex = ReadString(participant, "sex", ReadBool(participant, "isFemale", false) ? "female" : "male").ToLowerInvariant();
            Dictionary<string, object> variants = DictionaryOrDefault(tag, "genderVariants", new Dictionary<string, object>());
            if (variants.TryGetValue(sex, out object variantObject) && variantObject is Dictionary<string, object> variant)
                foreach (KeyValuePair<string, object> pair in variant) result[pair.Key] = pair.Value;
            string linkedHeroId = ReadString(participant, "linkedHeroId", "");
            string linkedHeroName = FirstNonEmpty(ReadString(participant, "linkedHeroName", ""), linkedHeroId);
            if (!string.IsNullOrWhiteSpace(linkedHeroId))
            {
                result["baseTagId"] = ReadString(tag, "id", "");
                result["linkedHeroId"] = linkedHeroId;
                result["linkedHeroName"] = linkedHeroName;
                foreach (string key in new[] { "label", "description" })
                    if (result.TryGetValue(key, out object raw))
                        result[key] = Convert.ToString(raw, CultureInfo.InvariantCulture)
                            .Replace("{linkedHeroName}", linkedHeroName);
            }
            return result;
        }

        private static string SocialTagStorageId(Dictionary<string, object> tag, Dictionary<string, object> participant)
        {
            string tagId = ReadString(tag, "id", "");
            string parameter = ReadString(tag, "parameterizedBy", "");
            if (string.IsNullOrWhiteSpace(parameter)) return tagId;
            string value = ReadString(participant, parameter, "");
            return string.IsNullOrWhiteSpace(value)
                ? tagId
                : tagId + ":" + DeterministicSocialId(value).Substring(0, 16);
        }

        private static Dictionary<string, object> CorrectSocialOccurrenceApi(Dictionary<string, object> payload)
        {
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string occurrenceId = ReadString(payload, "occurrenceId", "");
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                Dictionary<string, object> occurrence = QuerySql(connection,
                    "SELECT * FROM rumor_occurrences WHERE occurrence_id=$id AND campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = occurrenceId, ["campaign"] = campaignId, ["timeline"] = timelineId }).FirstOrDefault();
                if (occurrence == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Occurrence not found." };
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ExecuteSql(connection, "BEGIN IMMEDIATE;");
                try
                {
                    foreach (Dictionary<string, object> affectedTag in QuerySql(connection,
                        "SELECT subject_id,tag_id FROM rumor_subject_tags WHERE occurrence_id=$id;",
                        new Dictionary<string, object> { ["id"] = occurrenceId }))
                    {
                        ExecuteSql(connection, @"DELETE FROM shared_tag_exposure_streaks
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag;",
                            new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId, ["timeline"] = timelineId,
                                ["subject"] = ReadString(affectedTag, "subject_id", ""),
                                ["tag"] = ReadString(affectedTag, "tag_id", "")
                            });
                    }
                    ExecuteSql(connection, "UPDATE rumor_occurrences SET status='disproven',updated_ts=$ts WHERE occurrence_id=$id;",
                        new Dictionary<string, object> { ["id"] = occurrenceId, ["ts"] = ts });
                    ExecuteSql(connection, "UPDATE rumor_subject_tags SET status='disproven',updated_ts=$ts WHERE occurrence_id=$id AND status='active';",
                        new Dictionary<string, object> { ["id"] = occurrenceId, ["ts"] = ts });
                    ExecuteSql(connection, @"DELETE FROM rumor_exposure_streaks WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id=$archetype AND thread_key=$thread AND last_occurrence_id=$id;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["archetype"] = ReadString(occurrence, "archetype_id", ""), ["thread"] = ReadString(occurrence, "thread_key", ""), ["id"] = occurrenceId
                        });
                    int occurrenceDay = (int)Math.Floor(ReadDouble(occurrence, "world_day", 0d) + 0.000001d);
                    RecordWorldTestCounter(connection, campaignId, timelineId, occurrenceDay, "rumors",
                        "correction_" + occurrenceId,
                        new Dictionary<string, object> { ["corrected"] = 1, ["disproven"] = 1 });
                ExecuteSql(connection, "COMMIT;");
                }
                catch
                {
                    try { ExecuteSql(connection, "ROLLBACK;"); } catch { }
                    throw;
                }
                List<string> affected = QuerySql(connection,
                    "SELECT DISTINCT subject_id FROM rumor_subject_tags WHERE occurrence_id=$id;",
                    new Dictionary<string, object> { ["id"] = occurrenceId })
                    .Select(x => ReadString(x, "subject_id", "")).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                ReconcileSocialRelationshipsForSubjects(connection, campaignId, timelineId, affected,
                    ReadDouble(occurrence, "world_day", 0d));
                return new Dictionary<string, object> { ["ok"] = true, ["occurrenceId"] = occurrenceId, ["reputationsRetained"] = true };
            }
        }

        private static Dictionary<string, object> SocialCharacterStatusApi(Dictionary<string, object> payload)
        {
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string subjectId = ReadFirstString(payload, "subjectId", "heroId", "npcId");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                if (ExpireSocialRumors(connection, campaignId, timelineId, worldDay) > 0)
                    ReconcileSocialRelationshipsForSubjects(connection, campaignId, timelineId, new[] { subjectId }, worldDay);
                List<Dictionary<string, object>> reputations = QuerySql(connection, @"SELECT cr.tag_id,cr.description,cr.reputation_value,cr.archetype_id,cr.subject_role,cr.acquired_day,cr.reason_text,cr.reason_status,cr.reason_activation_id,
(SELECT ra.source_event_id FROM reputation_activations ra
 WHERE ra.activation_id=cr.reason_activation_id LIMIT 1) AS activation_source_event_id,
(SELECT ra.evidence_json FROM reputation_activations ra
 WHERE ra.activation_id=cr.reason_activation_id LIMIT 1) AS activation_evidence_json,
(SELECT rle.source_event_id FROM reputation_lifecycle_events rle
 WHERE rle.campaign_id=cr.campaign_id AND rle.timeline_id=cr.timeline_id
 AND rle.subject_id=cr.subject_id AND rle.tag_id=cr.tag_id
 ORDER BY rle.created_ts DESC,rle.lifecycle_id DESC LIMIT 1) AS lifecycle_source_event_id,
(SELECT whe.correlation_id FROM reputation_lifecycle_events rle
 JOIN world_history_events whe ON whe.campaign_id=rle.campaign_id
  AND whe.timeline_id=rle.timeline_id AND whe.event_id=rle.source_event_id
 WHERE rle.campaign_id=cr.campaign_id AND rle.timeline_id=cr.timeline_id
 AND rle.subject_id=cr.subject_id AND rle.tag_id=cr.tag_id
 ORDER BY rle.created_ts DESC,rle.lifecycle_id DESC LIMIT 1) AS lifecycle_source_correlation_id,
(SELECT rle.details_json FROM reputation_lifecycle_events rle
 WHERE rle.campaign_id=cr.campaign_id AND rle.timeline_id=cr.timeline_id
 AND rle.subject_id=cr.subject_id AND rle.tag_id=cr.tag_id
 ORDER BY rle.created_ts DESC,rle.lifecycle_id DESC LIMIT 1) AS lifecycle_details_json
FROM character_reputations cr WHERE cr.campaign_id=$campaign AND cr.timeline_id=$timeline AND cr.subject_id=$subject AND cr.status='active'
ORDER BY cr.acquired_day DESC;", new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["subject"] = subjectId
                });
                foreach (Dictionary<string, object> reputation in reputations)
                {
                    Dictionary<string, object> activationEvidence = TryParseJsonObject(
                        ReadString(reputation, "activation_evidence_json", "{}"))
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> dynamicEvidence = DictionaryOrDefault(
                        activationEvidence, "dynamicEvidence", new Dictionary<string, object>());
                    reputation["activation_source_correlation_id"] = FirstNonEmpty(
                        ReadString(dynamicEvidence, "sourceCorrelationId", ""),
                        ReadString(activationEvidence, "sourceCorrelationId", ""));
                }
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["subjectId"] = subjectId,
                    ["publicStanding"] = ReadPublicStandingTrait(connection,
                        campaignId, timelineId, subjectId, worldDay),
                    ["rumors"] = QuerySql(connection, @"SELECT rst.tag_id,rst.description,rst.rumor_value,ro.archetype_id,ro.world_day,ro.expires_day,ro.provenance_summary
FROM rumor_subject_tags rst JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline AND rst.subject_id=$subject AND rst.status='active' AND ro.status='active' AND ro.expires_day>$day
AND ro.world_day=(SELECT MAX(ro2.world_day) FROM rumor_subject_tags rst2
JOIN rumor_occurrences ro2 ON ro2.occurrence_id=rst2.occurrence_id
WHERE rst2.subject_id=rst.subject_id AND rst2.tag_id=rst.tag_id AND rst2.status='active'
AND ro2.campaign_id=ro.campaign_id AND ro2.timeline_id=ro.timeline_id AND ro2.status='active' AND ro2.expires_day>$day)
ORDER BY ro.world_day DESC;", new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["day"] = worldDay }),
                    ["reputations"] = reputations
                };
            }
        }

        private static Dictionary<string, object> BuildKnownSocialStandingView(ReignDbConnection connection,
            string campaignId, string timelineId, string observerId, string subjectId, bool identityKnown, double worldDay)
        {
            if (!identityKnown || string.IsNullOrWhiteSpace(observerId) || string.IsNullOrWhiteSpace(subjectId)
                || observerId.Equals(subjectId, StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, object>
                {
                    ["known"] = false,
                    ["rumors"] = new List<object>(),
                    ["reputations"] = new List<object>()
                };
            if (ExpireSocialRumors(connection, campaignId, timelineId, worldDay) > 0)
                ReconcileSocialRelationshipsForSubjects(connection, campaignId, timelineId, new[] { subjectId }, worldDay);
            List<Dictionary<string, object>> rumors = QuerySql(connection, @"SELECT rst.description,rst.snapshot_json,
ro.archetype_id,ro.world_day,ro.expires_day,ro.provenance_summary,ro.participants_json
FROM rumor_subject_tags rst JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline AND rst.subject_id=$subject
AND rst.status='active' AND ro.status='active' AND ro.expires_day>$day
AND ro.world_day=(SELECT MAX(ro2.world_day) FROM rumor_subject_tags rst2
JOIN rumor_occurrences ro2 ON ro2.occurrence_id=rst2.occurrence_id
WHERE rst2.subject_id=rst.subject_id AND rst2.tag_id=rst.tag_id AND rst2.status='active'
AND ro2.campaign_id=ro.campaign_id AND ro2.timeline_id=ro.timeline_id AND ro2.status='active' AND ro2.expires_day>$day)
ORDER BY ro.world_day DESC;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["day"] = worldDay });
            List<Dictionary<string, object>> reputations = QuerySql(connection, @"SELECT description,snapshot_json,
archetype_id,subject_role,acquired_day,source_occurrence_id,reason_text,reason_status FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND status='active'
ORDER BY acquired_day DESC;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId });
            foreach (Dictionary<string, object> item in rumors.Concat(reputations))
            {
                Dictionary<string, object> snapshot = TryParseJsonObject(ReadString(item, "snapshot_json", "{}")) ?? new Dictionary<string, object>();
                item["label"] = ReadString(snapshot, "label", ReadString(item, "archetype_id", "Rumor"));
                item["baseTagId"] = ReadString(snapshot, "baseTagId", ReadString(snapshot, "id", ""));
                item["linkedHeroId"] = ReadString(snapshot, "linkedHeroId", "");
                item["linkedHeroName"] = ReadString(snapshot, "linkedHeroName", "");
                item.Remove("snapshot_json");
                item.Remove("source_occurrence_id");
            }
            Dictionary<string, object> observerStanding = ResolveObserverPublicStanding(connection,
                campaignId, timelineId, observerId, subjectId);
            return new Dictionary<string, object>
            {
                ["known"] = true, ["observerId"] = observerId, ["subjectId"] = subjectId,
                ["publicStanding"] = ReadPublicStandingTrait(connection,
                    campaignId, timelineId, subjectId, worldDay),
                ["observerStanding"] = observerStanding,
                ["rumors"] = rumors, ["reputations"] = reputations,
                ["familyAttentionContext"] = BuildFamilyAttentionContext(connection, campaignId, timelineId, observerId, subjectId, worldDay),
                ["sharedFavorContext"] = SharedFavorJealousyContext(connection, campaignId, timelineId, observerId, subjectId, worldDay)
            };
        }

        private static string BuildKnownSocialStandingPromptBlock(Dictionary<string, object> standing)
        {
            if (!ReadBool(standing, "known", false)) return "SOCIAL KNOWLEDGE: No verified identity, so do not use social hearsay about this person.";
            List<string> lines = new List<string>();
            string attention = ReadString(standing, "familyAttentionContext", "");
            string sharedFavor = ReadString(standing, "sharedFavorContext", "");
            if (!string.IsNullOrWhiteSpace(attention)) lines.Add(attention);
            if (!string.IsNullOrWhiteSpace(sharedFavor)) lines.Add(sharedFavor);
            foreach (Dictionary<string, object> rumor in ReadDictionaryList(standing, "rumors"))
            {
                string label = ReadString(rumor, "label", "Rumor");
                string description = ReadString(rumor, "description", "");
                string provenance = ReadString(rumor, "provenance_summary", "");
                lines.Add("- Current rumor — " + label + ": " + description
                    + (string.IsNullOrWhiteSpace(provenance) ? "" : " What is circulating: " + provenance)
                    + " Treat this as heard information, not verified fact.");
            }
            foreach (Dictionary<string, object> reputation in ReadDictionaryList(standing, "reputations"))
            {
                lines.Add("- Established reputation — " + ReadString(reputation, "label", "Reputation") + ": "
                    + ReadString(reputation, "description", "")
                    + (string.IsNullOrWhiteSpace(ReadString(reputation, "reason_text", "")) ? ""
                        : " Known basis: " + ReadString(reputation, "reason_text", ""))
                    + " This is socially established, though it need not reveal every private detail.");
            }
            Dictionary<string, object> observerStanding = ReadDictionary(standing, "observerStanding")
                ?? new Dictionary<string, object>();
            if (!ReadString(observerStanding, "scope", "none").Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                string scope = ReadString(observerStanding, "scope", "foreign");
                if (scope == "favorite")
                    lines.Add("- This observer is the ruler's currently favored person and understands that the ruler singles them out.");
                else if (scope == "kingdom_non_favorite")
                    lines.Add("- This observer belongs to the ruler's kingdom but is not among the people currently singled out for favor. Their jealousy and personality may color how strongly they resent that exclusion.");
                else
                    lines.Add("- This observer is outside the ruler's kingdom, so the ruler's favoritism does not directly affect their treatment.");
            }
            if (lines.Count == 0) return "SOCIAL KNOWLEDGE: The observer knows this person, but no current rumor or established reputation applies.";
            return "SOCIAL KNOWLEDGE ABOUT THIS KNOWN PERSON\n" + string.Join("\n", lines)
                + "\nUse this only when relevant. The recorded basis is authoritative evidence: phrase it naturally for the present conversation, but do not add events, motives, witnesses, or outcomes that are not supplied. Never mention database identifiers, mechanical values, or describe these records as tags.";
        }

        private static List<Dictionary<string, object>> LoadKnownSocialContextCandidates(ReignDbConnection connection,
            string campaignId, string timelineId, string observerId, double worldDay, string topic)
        {
            EnsureSocialReputationSchema(connection);
            ExpireSocialRumors(connection, campaignId, timelineId, worldDay);
            List<Dictionary<string, object>> candidates = new List<Dictionary<string, object>>();
            List<string> topicTokens = NormalizeLookup(topic).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<Dictionary<string, object>> rows = QuerySql(connection, @"SELECT 'rumor' AS social_type,rst.subject_id,rst.description,
rst.snapshot_json,ro.provenance_summary,ro.world_day AS lifecycle_day,ro.expires_day
FROM rumor_subject_tags rst JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline AND rst.status='active' AND ro.status='active' AND ro.expires_day>$day
AND ro.world_day=(SELECT MAX(ro2.world_day) FROM rumor_subject_tags rst2
JOIN rumor_occurrences ro2 ON ro2.occurrence_id=rst2.occurrence_id
WHERE rst2.subject_id=rst.subject_id AND rst2.tag_id=rst.tag_id AND rst2.status='active'
AND ro2.campaign_id=ro.campaign_id AND ro2.timeline_id=ro.timeline_id AND ro2.status='active' AND ro2.expires_day>$day)
UNION ALL
SELECT 'reputation' AS social_type,cr.subject_id,cr.description,cr.snapshot_json,cr.reason_text AS provenance_summary,
cr.acquired_day AS lifecycle_day,0 AS expires_day FROM character_reputations cr
WHERE cr.campaign_id=$campaign AND cr.timeline_id=$timeline AND cr.status='active'
ORDER BY lifecycle_day DESC LIMIT 500;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = worldDay });
            foreach (Dictionary<string, object> row in rows)
            {
                string subjectId = ReadString(row, "subject_id", "");
                if (string.IsNullOrWhiteSpace(subjectId) || subjectId.Equals(observerId, StringComparison.OrdinalIgnoreCase)
                    || !IdentityStateVerified(ReadAcquaintance(connection, observerId, subjectId))) continue;
                Dictionary<string, object> roster = QuerySql(connection,
                    "SELECT canonical_name FROM identity_roster WHERE hero_id=$id LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = subjectId }).FirstOrDefault();
                string name = FirstNonEmpty(ReadString(roster, "canonical_name", ""), "a known person");
                Dictionary<string, object> snapshot = TryParseJsonObject(ReadString(row, "snapshot_json", "{}")) ?? new Dictionary<string, object>();
                string label = ReadString(snapshot, "label", ReadString(row, "social_type", "rumor"));
                bool rumor = ReadString(row, "social_type", "") == "rumor";
                string claim = name + " — " + label + ": " + ReadString(row, "description", "")
                    + (rumor ? " This is an uncertain secondhand report." : " This is an established social reputation.");
                candidates.Add(new Dictionary<string, object>
                {
                    ["belief_id"] = "social_candidate_" + DeterministicSocialId(observerId + "|" + subjectId + "|" + label + "|" + ReadDouble(row, "lifecycle_day", 0d)),
                    ["claim"] = claim, ["source"] = rumor ? "current_rumor" : "established_reputation",
                    ["confidence"] = rumor ? 0.5d : 0.85d, ["ts"] = (long)(ReadDouble(row, "lifecycle_day", 0d) * 86400d),
                    ["memory_lane"] = "beliefs_and_rumors", ["visibility"] = "identity_known",
                    ["packetScore"] = topicTokens.Any(token => claim.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) ? 75d : 5d
                });
            }
            return candidates;
        }

        private static int ExpireSocialRumors(ReignDbConnection connection, string campaignId, string timelineId, double worldDay)
        {
            int forgottenCount = ExpireRulerFavorContact(connection, campaignId, timelineId, worldDay).Count
                + ExpireWhoremongerActivity(connection, campaignId, timelineId, worldDay);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<string> affectedSubjects = QuerySql(connection, @"SELECT DISTINCT rst.subject_id
FROM rumor_subject_tags rst JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline
AND ro.status='active' AND ro.expires_day<=$day;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = worldDay })
                .Select(row => ReadString(row, "subject_id", ""))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();
            List<Dictionary<string, object>> expiring = QuerySql(connection, @"SELECT occurrence_id,world_day,expires_day
FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='active' AND expires_day<=$day;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = worldDay });
            int count = expiring.Count;
            ExecuteSql(connection, @"UPDATE rumor_occurrences SET status='expired',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='active' AND expires_day<=$day;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = worldDay, ["ts"] = ts });
            ExecuteSql(connection, @"UPDATE rumor_subject_tags SET status='expired',updated_ts=$ts
WHERE status='active' AND occurrence_id IN (SELECT occurrence_id FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='expired');",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["ts"] = ts });
            int expirationDay = (int)Math.Floor(worldDay + 0.000001d);
            foreach (Dictionary<string, object> occurrence in expiring)
            {
                string occurrenceId = ReadString(occurrence, "occurrence_id", "");
                Dictionary<string, object> counters = new Dictionary<string, object> { ["expired"] = 1 };
                IncrementWorldTestObjectCounter(counters,
                    "occurrenceLifetime:" + SocialRumorDurationBucket(
                        worldDay - ReadDouble(occurrence, "world_day", worldDay)), 1);
                RecordWorldTestCounter(connection, campaignId, timelineId, expirationDay, "rumors",
                    "expiration_" + occurrenceId, counters);
            }
            if (affectedSubjects.Count > 0)
                RecomputePublicStandingForSubjects(connection, campaignId, timelineId,
                    affectedSubjects, worldDay);
            return count + forgottenCount;
        }

        private static void ReconcileSocialRelationshipsForSubjects(ReignDbConnection connection, string campaignId,
            string timelineId, IEnumerable<string> subjectIds, double worldDay,
            bool schemaReady = false, bool rumorsAlreadyExpired = false)
        {
            if (!schemaReady) EnsureSocialReputationSchema(connection);
            if (!rumorsAlreadyExpired)
                ExpireSocialRumors(connection, campaignId, timelineId,
                    worldDay);
            RecomputePublicStandingForSubjects(connection, campaignId, timelineId,
                subjectIds, worldDay);
        }

        private static void ReconcileSocialRelationshipForObserverSubject(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            string observerId,
            string subjectId,
            double worldDay)
        {
            if (string.IsNullOrWhiteSpace(observerId)
                || string.IsNullOrWhiteSpace(subjectId)
                || observerId.Equals(
                    subjectId,
                    StringComparison.OrdinalIgnoreCase))
                return;

            EnsureSocialReputationSchema(connection);
            ExpireSocialRumors(
                connection,
                campaignId,
                timelineId,
                worldDay);
            RecomputePublicStandingForSubjects(connection, campaignId, timelineId,
                new[] { subjectId }, worldDay);
        }

        private static int CalculateObserverSocialModifier(ReignDbConnection connection, string campaignId, string timelineId,
            Dictionary<string, object> observer, Dictionary<string, object> subject, double worldDay,
            out int rumorTotal, out int reputationTotal)
        {
            string observerId = ReadString(observer, "hero_id", "");
            string subjectId = ReadString(subject, "hero_id", "");
            List<int> rumorValues = new List<int>();
            foreach (Dictionary<string, object> rumor in QuerySql(connection, @"SELECT rst.rumor_value,rst.co_participants_json,ro.archetype_id
FROM rumor_subject_tags rst JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline AND rst.subject_id=$subject
AND rst.status='active' AND ro.status='active' AND ro.expires_day>$day
AND ro.world_day=(SELECT MAX(ro2.world_day) FROM rumor_subject_tags rst2
JOIN rumor_occurrences ro2 ON ro2.occurrence_id=rst2.occurrence_id
WHERE rst2.subject_id=rst.subject_id AND rst2.tag_id=rst.tag_id AND rst2.status='active'
AND ro2.campaign_id=ro.campaign_id AND ro2.timeline_id=ro.timeline_id AND ro2.status='active' AND ro2.expires_day>$day);",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["day"] = worldDay }))
            {
                bool affairCoParticipant = string.Equals(ReadString(rumor, "archetype_id", ""), "affair", StringComparison.OrdinalIgnoreCase)
                    && TextListFromJson(ReadString(rumor, "co_participants_json", "[]")).Contains(observerId, StringComparer.OrdinalIgnoreCase);
                if (!affairCoParticipant) rumorValues.Add(ReadInt(rumor, "rumor_value", 0));
            }
            List<int> reputationValues = QuerySql(connection, @"SELECT reputation_value FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND status='active';",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId })
                .Select(x => ReadInt(x, "reputation_value", 0)).ToList();
            rumorTotal = rumorValues.Sum();
            reputationTotal = reputationValues.Sum();
            string observerKingdom = ReadString(observer, "kingdom_id", "");
            string subjectKingdom = ReadString(subject, "kingdom_id", "");
            bool sameKingdom = !string.IsNullOrWhiteSpace(observerKingdom)
                && observerKingdom.Equals(subjectKingdom, StringComparison.OrdinalIgnoreCase);
            return CalculateSocialModifier(rumorValues.Concat(reputationValues), ReadInt(subject, "current_charm", 0), sameKingdom);
        }

        private static void ApplyDirectionalSocialProjection(ReignDbConnection connection, string campaignId, string timelineId,
            Dictionary<string, object> observer, Dictionary<string, object> subject, int modifier,
            int rumorTotal, int reputationTotal, double worldDay)
        {
            string observerId = ReadString(observer, "hero_id", "");
            string subjectId = ReadString(subject, "hero_id", "");
            string pairKey = AmbientPairKey(observerId, subjectId);
            bool observerIsA = pairKey.StartsWith(observerId + "|", StringComparison.OrdinalIgnoreCase);
            string heroA = observerIsA ? observerId : subjectId;
            string heroB = observerIsA ? subjectId : observerId;
            int day = (int)Math.Floor(worldDay + 0.000001d);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, object> existing = QuerySql(connection,
                "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
            // Only actual co-presence creates relationship pairs. Reputation
            // reconciliation may modify an existing pair, but it must never
            // manufacture a relationship between characters who have not met.
            if (existing == null)
                return;
            string typeA = existing == null ? ResolvePermanentMbtiTypeById(connection, campaignId, heroA, day) : ReadString(existing, "mbti_a", "XXXX");
            string typeB = existing == null ? ResolvePermanentMbtiTypeById(connection, campaignId, heroB, day) : ReadString(existing, "mbti_b", "XXXX");
            if (!MbtiDefinitions.ContainsKey(typeA) || !MbtiDefinitions.ContainsKey(typeB)) return;
            int affinityAB = existing == null ? 0 : ReadInt(existing, "affinity_a_to_b", 0);
            int affinityBA = existing == null ? 0 : ReadInt(existing, "affinity_b_to_a", 0);
            int socialAB = existing == null ? 0 : ReadInt(existing, "social_modifier_a_to_b", 0);
            int socialBA = existing == null ? 0 : ReadInt(existing, "social_modifier_b_to_a", 0);
            if (observerIsA) socialAB = modifier; else socialBA = modifier;
            int effectiveAB = Clamp(affinityAB + socialAB, -100, 100);
            int effectiveBA = Clamp(affinityBA + socialBA, -100, 100);
            int projected = Clamp(RoundAwayFromZero((effectiveAB + effectiveBA) / 2d), -100, 100);
            int baseAB = existing == null ? MbtiCompatibility(typeA, typeB) : ReadInt(existing, "base_chance_a_to_b", 50);
            int baseBA = existing == null ? MbtiCompatibility(typeB, typeA) : ReadInt(existing, "base_chance_b_to_a", 50);
            Dictionary<string, object> existingNativeTarget = QuerySql(connection,
                "SELECT observed_relation FROM relationship_native_targets WHERE pair_key=$pair LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
            int observedNative = existingNativeTarget != null
                ? ReadInt(existingNativeTarget, "observed_relation", 0)
                : existing != null && ReadInt(existing, "native_action_pending", 0) == 0
                    ? ReadInt(existing, "projected_native_relation", 0)
                    : 0;
            bool requiresNativeTarget = projected != observedNative;
            string nativeActionId = requiresNativeTarget ? "native_target:" + pairKey : "";
            ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,base_chance_a_to_b,base_chance_b_to_a,chance_a_to_b,chance_b_to_a,
affinity_a_to_b,affinity_b_to_a,social_modifier_a_to_b,social_modifier_b_to_a,effective_affinity_a_to_b,effective_affinity_b_to_a,
tag_a_to_b,tag_b_to_a,first_day,last_day,projected_native_relation,native_action_pending,native_action_id,compatibility_version,updated_ts)
VALUES($pair,$a,$b,$typeA,$typeB,$baseAB,$baseBA,$chanceAB,$chanceBA,$affinityAB,$affinityBA,$socialAB,$socialBA,$effectiveAB,$effectiveBA,
$tagAB,$tagBA,$day,$day,$projected,$pending,$action,$version,$ts)
ON CONFLICT(pair_key) DO UPDATE SET social_modifier_a_to_b=$socialAB,social_modifier_b_to_a=$socialBA,
effective_affinity_a_to_b=$effectiveAB,effective_affinity_b_to_a=$effectiveBA,tag_a_to_b=$tagAB,tag_b_to_a=$tagBA,
last_day=MAX(relationship_pair_chemistry.last_day,$day),
projected_native_relation=$projected,native_action_pending=$pending,native_action_id=$action,updated_ts=$ts;",
                new Dictionary<string, object>
                {
                    ["pair"] = pairKey, ["a"] = heroA, ["b"] = heroB, ["typeA"] = typeA, ["typeB"] = typeB,
                    ["baseAB"] = baseAB, ["baseBA"] = baseBA,
                    ["chanceAB"] = AdjustedMbtiCompatibility(baseAB), ["chanceBA"] = AdjustedMbtiCompatibility(baseBA),
                    ["affinityAB"] = affinityAB, ["affinityBA"] = affinityBA, ["socialAB"] = socialAB, ["socialBA"] = socialBA,
                    ["effectiveAB"] = effectiveAB, ["effectiveBA"] = effectiveBA,
                    ["tagAB"] = DirectionalRelationshipTag(campaignId, pairKey, "a_to_b", effectiveAB, effectiveBA),
                    ["tagBA"] = DirectionalRelationshipTag(campaignId, pairKey, "b_to_a", effectiveBA, effectiveAB),
                    ["day"] = day, ["projected"] = projected,
                    ["pending"] = requiresNativeTarget ? 1 : 0,
                    ["action"] = nativeActionId,
                    ["version"] = MbtiChemistryVersion, ["ts"] = ts
                });
            if (requiresNativeTarget)
            {
                ExecuteSql(connection, @"INSERT INTO relationship_native_targets(pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,world_day,last_sync_day,
attempt_count,claimed_ts,last_error,updated_ts,revision)
VALUES($pair,$a,$b,$target,$observed,'pending',$day,-1000,0,0,'',$ts,1)
ON CONFLICT(pair_key) DO UPDATE SET target_relation=$target,observed_relation=$observed,status='pending',world_day=$day,claimed_ts=0,last_error='',
revision=CASE WHEN relationship_native_targets.target_relation=$target
THEN relationship_native_targets.revision
ELSE relationship_native_targets.revision+1 END,updated_ts=$ts;",
                    new Dictionary<string, object>
                    {
                        ["pair"] = pairKey, ["a"] = heroA, ["b"] = heroB,
                        ["target"] = projected, ["observed"] = observedNative,
                        ["day"] = worldDay, ["ts"] = ts
                    });
            }
            else
            {
                ExecuteSql(connection,
                    "DELETE FROM relationship_native_targets WHERE pair_key=$pair;",
                    new Dictionary<string, object> { ["pair"] = pairKey });
            }
            string observerKingdom = ReadString(observer, "kingdom_id", "");
            string subjectKingdom = ReadString(subject, "kingdom_id", "");
            ExecuteSql(connection, @"INSERT OR REPLACE INTO directional_social_projections(campaign_id,timeline_id,observer_id,subject_id,
rumor_total,reputation_total,charm,kingdom_scale,social_modifier,calculated_day,details_json,updated_ts)
VALUES($campaign,$timeline,$observer,$subject,$rumorTotal,$reputationTotal,$charm,$scale,$modifier,$day,'{}',$ts);",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId, ["observer"] = observerId, ["subject"] = subjectId,
                    ["rumorTotal"] = rumorTotal, ["reputationTotal"] = reputationTotal,
                    ["charm"] = ReadInt(subject, "current_charm", 0),
                    ["scale"] = !string.IsNullOrWhiteSpace(observerKingdom) && observerKingdom.Equals(subjectKingdom, StringComparison.OrdinalIgnoreCase) ? 1d : 0.5d,
                    ["modifier"] = modifier, ["day"] = worldDay, ["ts"] = ts
                });
        }

        private static double SocialCharmMitigation(int charm)
        {
            return Math.Max(0d, Math.Min(0.60d, Math.Max(0, charm) / 300d * 0.60d));
        }

        private static int EffectiveDirectionalAffinity(Dictionary<string, object> row, bool aToB, int fallback = 0)
        {
            if (row == null) return fallback;
            string affinityKey = aToB ? "affinity_a_to_b" : "affinity_b_to_a";
            string socialKey = aToB ? "social_modifier_a_to_b" : "social_modifier_b_to_a";
            return Clamp(ReadInt(row, affinityKey, fallback) + ReadInt(row, socialKey, 0), -100, 100);
        }

        private static int CalculateSocialModifier(IEnumerable<int> values, int subjectCharm, bool sameNonEmptyKingdom)
        {
            double positive = values.Where(x => x > 0).Sum(x => (double)x);
            double negative = values.Where(x => x < 0).Sum(x => (double)x);
            double mitigated = positive + negative * (1d - SocialCharmMitigation(subjectCharm));
            double scaled = mitigated * (sameNonEmptyKingdom ? 1d : 0.5d);
            return RoundAwayFromZero(scaled);
        }

        private static Dictionary<string, object> SocialCharmPreview(int charm)
        {
            return new Dictionary<string, object>
            {
                ["charm"] = charm, ["negativeReduction"] = SocialCharmMitigation(charm),
                ["exampleRumorMinus5"] = CalculateSocialModifier(new[] { -5 }, charm, true),
                ["exampleReputationMinus15"] = CalculateSocialModifier(new[] { -15 }, charm, true)
            };
        }

        private static double DeterministicSocialRoll(string seed)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(seed ?? ""));
                ulong value = BitConverter.ToUInt64(hash, 0);
                return value / ((double)ulong.MaxValue + 1d);
            }
        }

        private static string DeterministicSocialId(string seed)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(seed ?? ""))).Replace("-", "").ToLowerInvariant().Substring(0, 24);
        }

        // Compatibility entry point for the world-test harness. It creates only
        // the character-owned social schema; no legacy propagation tables exist.
        private static void EnsureRumorSchema(ReignDbConnection connection)
        {
            EnsureSocialReputationSchema(connection);
        }

        private static List<Dictionary<string, object>> RunRumorSubsystemSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string, object> add = (id, passed, summary, data) => results.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "character_social_reputation",
                ["caseId"] = id, ["name"] = id, ["summary"] = summary,
                ["data"] = data ?? new Dictionary<string, object>(), ["durationMs"] = 0
            });
            add("social_fixture_missing_enrollment_rejected",
                !ValidateSocialBalanceLiveEnrollment(new Dictionary<string, object>(), "__missing_enrollment", out string missingEnrollmentError)
                && !string.IsNullOrWhiteSpace(missingEnrollmentError),
                "A missing run enrollment fails closed with a normal error before opening any campaign database.", null);
            var favorPreparations = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["event_id"] = "old_first", ["sequence"] = 1L, ["correlation_id"] = "first" },
                new Dictionary<string, object> { ["event_id"] = "old_second", ["sequence"] = 2L, ["correlation_id"] = "second" },
                new Dictionary<string, object> { ["event_id"] = "current_first", ["sequence"] = 3L, ["correlation_id"] = "first" },
                new Dictionary<string, object> { ["event_id"] = "current_second", ["sequence"] = 4L, ["correlation_id"] = "second" }
            };
            var currentFavorPreparation = SocialBalanceCurrentFavorEvents(favorPreparations, "first", 3, "second", 4);
            add("favor_projection_current_preparation_only",
                currentFavorPreparation.Count == 2
                && currentFavorPreparation.All(row => ReadString(row, "event_id", "").StartsWith("current_", StringComparison.Ordinal))
                && SocialBalanceHasTwoFavorSources(currentFavorPreparation, "first", "second")
                && SocialBalanceCurrentFavorEvents(favorPreparations, "first", 4, "second", 3).Count == 0
                && SocialBalanceCurrentFavorEvents(favorPreparations, "first", 0, "second", 4).Count == 0,
                "Exact native sequences identify this preparation and cannot borrow an older preparation's source events or mismatched keys.", null);
            var favorDeliveries = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["correlation_id"] = "first" },
                new Dictionary<string, object> { ["correlation_id"] = "first" },
                new Dictionary<string, object> { ["correlation_id"] = "second" }
            };
            add("favor_projection_retry_source_identity",
                SocialBalanceHasTwoFavorSources(favorDeliveries, "first", "second")
                && !SocialBalanceHasTwoFavorSources(favorDeliveries, "first", "missing")
                && !SocialBalanceHasTwoFavorSources(favorDeliveries, "first", "first"),
                "Repeated deliveries retain two canonical favor sources; missing or identical pair keys fail.", null);
            favorDeliveries.Add(new Dictionary<string, object> { ["correlation_id"] = "unexpected" });
            add("favor_projection_rejects_unexpected_source",
                !SocialBalanceHasTwoFavorSources(favorDeliveries, "first", "second"),
                "An unrelated third source must not be accepted as a delivery retry.", null);
            var favorRepresentative = new Dictionary<string, object>
            {
                ["tagId"] = "ruler_favoring:first", ["baseTagId"] = "ruler_favoring",
                ["collapsedFamily"] = "ruler_favoring", ["collapsedRepresentative"] = true,
                ["effectiveContribution"] = -5.08d
            };
            var favorSecondary = new Dictionary<string, object>
            {
                ["tagId"] = "ruler_favoring:second", ["baseTagId"] = "ruler_favoring",
                ["collapsedFamily"] = "ruler_favoring", ["effectiveContribution"] = 0d
            };
            var mixedStanding = new Dictionary<string, object>
            {
                ["standingValue"] = -3,
                ["sources"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["tagId"] = "dynasty_secure", ["effectiveContribution"] = 2d },
                    favorRepresentative, favorSecondary
                }
            };
            add("favor_projection_accepts_unrelated_standing",
                SocialBalanceFavoringCostCollapsed(mixedStanding, 246),
                "The native projection verifier isolates the favor cost while retaining the ruler's dynasty bonus and rounded total.", null);
            favorSecondary["effectiveContribution"] = -5.08d;
            mixedStanding["standingValue"] = -8;
            add("favor_projection_rejects_double_charge",
                !SocialBalanceFavoringCostCollapsed(mixedStanding, 246),
                "Two favorites must not charge the standing penalty twice even when the total matches the incorrect sources.", null);
            favorSecondary["effectiveContribution"] = 0d;
            mixedStanding["standingValue"] = -5;
            add("favor_projection_rejects_incorrect_total",
                !SocialBalanceFavoringCostCollapsed(mixedStanding, 246),
                "Isolating favor must not hide a wrong total standing value.", null);
            mixedStanding["standingValue"] = -3;
            favorRepresentative["effectiveContribution"] = -5d;
            add("favor_projection_rejects_premature_rounding",
                !SocialBalanceFavoringCostCollapsed(mixedStanding, 246),
                "The source contribution retains fractional Charm mitigation before final rounding.", null);
            add("charm_mitigation_exact",
                Math.Abs(SocialCharmMitigation(0) - 0d) < 0.000001d
                && Math.Abs(SocialCharmMitigation(100) - 0.20d) < 0.000001d
                && Math.Abs(SocialCharmMitigation(200) - 0.40d) < 0.000001d
                && Math.Abs(SocialCharmMitigation(300) - 0.60d) < 0.000001d
                && Math.Abs(SocialCharmMitigation(600) - 0.60d) < 0.000001d,
                "Current Charm reduces only negative social effects by the exact zero-to-sixty-percent curve.", null);
            add("modifier_ordering_and_rounding",
                CalculateSocialModifier(new[] { 5, -15 }, 100, true) == -7
                && CalculateSocialModifier(new[] { 5, -15 }, 100, false) == -4
                && CalculateSocialModifier(new[] { 10 }, 300, true) == 10,
                "Positive and negative values are separated, negative Charm mitigation precedes kingdom scaling, and the result rounds away from zero.", null);
            add("deterministic_roll_replay",
                DeterministicSocialRoll("same-seed") == DeterministicSocialRoll("same-seed")
                && DeterministicSocialRoll("same-seed") >= 0d && DeterministicSocialRoll("same-seed") < 1d,
                "Exposure and promotion rolls replay deterministically.", null);
            Dictionary<string, object> expandedCatalog = ReadGlobalSocialCatalog();
            HashSet<string> expandedArchetypes = new HashSet<string>(
                ReadDictionaryList(expandedCatalog, "archetypes").Select(x => ReadString(x, "id", "")),
                StringComparer.OrdinalIgnoreCase);
            add("expanded_catalog_complete",
                new[]
                {
                    "strong_captain", "weak_captain", "tactician", "failed_tactician",
                    "siege_commander", "inept_besieger", "siege_breaker", "champion_of_the_pit",
                    "duelist", "fallen_challenger", "coward", "war_crowned", "defeated_crown",
                    "realm_builder", "realm_in_decline", "provider_of_the_realm", "starving_crown",
                    "unifier_of_the_realm", "fractured_crown", "fractured_crown_rebellion",
                    "keeper_of_order", "lawless_crown", "guardian_of_the_commons",
                    "lord_of_empty_fields", "consolidator", "overextended_crown",
                    "fair_hand_of_the_crown", "hoarder_of_titles", "gatherer_of_banners",
                    "scatterer_of_banners", "ruler_favoring_presence", "ruler_favoring_dialogue", "attentive_lord", "absent_lord",
                    "infertile", "flirt", "promiscuous_flirtation", "the_unchaste"
                }.All(expandedArchetypes.Contains),
                "The revisioned catalog contains every combat, ruler-governance, and court-personality producer.", null);

            string campaignId = "__social_reputation_test_" + Guid.NewGuid().ToString("N");
            string campaignPath = CampaignDirectory(campaignId);
            try
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureSocialReputationSchema(connection);
                    EnsureWorldTestTelemetrySchema(connection);
                    long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    foreach (Dictionary<string, object> hero in new[]
                    {
                        new Dictionary<string, object> { ["heroStringId"]="married",["name"]="Married",["isAlive"]=true,["isAdult"]=true,["isPlayer"]=false,["isFemale"]=false,["clanTier"]=6,["currentCharm"]=0 },
                        new Dictionary<string, object> { ["heroStringId"]="lover",["name"]="Lover",["isAlive"]=true,["isAdult"]=true,["isPlayer"]=false,["isFemale"]=true,["clanTier"]=1,["currentCharm"]=0 },
                        new Dictionary<string, object> { ["heroStringId"]="player_subject",["name"]="Player",["isAlive"]=true,["isAdult"]=true,["isPlayer"]=true,["isFemale"]=false,["clanTier"]=6,["currentCharm"]=100 }
                    }) UpsertIdentityRosterHero(connection, hero, ts);
                    EnsurePublicStandingForRoster(connection, campaignId, "main", 1d);
                    List<Dictionary<string, object>> initialPublicStanding = QuerySql(connection, @"
SELECT subject_id,standing_value,revision,last_changed_day,sources_json
FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id='main'
ORDER BY subject_id;",
                        new Dictionary<string, object> { ["campaign"] = campaignId });
                    add("public_standing_roster_initialization",
                        initialPublicStanding.Count == 3
                        && initialPublicStanding.All(row => ReadInt(row, "standing_value", int.MinValue) == 0)
                        && initialPublicStanding.All(row => ReadInt(row, "revision", 0) == 1)
                        && initialPublicStanding.All(row => Math.Abs(ReadDouble(row, "last_changed_day", 0d) - 1d) < 0.000001d)
                        && initialPublicStanding.All(row => ReadString(row, "sources_json", "") == "[]"),
                        "Every synchronized character receives a zero-valued, revisioned Public Standing trait before acquiring social sources.",
                        initialPublicStanding);
                    Dictionary<string, object> playerSubject =
                        new Dictionary<string, object>
                        {
                            ["heroStringId"] = "player_subject",
                            ["name"] = "Player",
                            ["isAlive"] = true,
                            ["isAdult"] = true,
                            ["isPlayer"] = true,
                            ["clanTier"] = 6,
                            ["currentCharm"] = 100
                        };
                    foreach (string observerId in new[] { "married", "lover" })
                    {
                        UpsertVerifiedIdentity(
                            connection,
                            new Dictionary<string, object>
                            {
                                ["heroStringId"] = observerId,
                                ["name"] = observerId
                            },
                            playerSubject,
                            "self_test",
                            "player_subject",
                            1d,
                            "social_scope_test");
                        string pairKey = AmbientPairKey(observerId, "player_subject");
                        bool observerIsA = pairKey.StartsWith(observerId + "|",
                            StringComparison.OrdinalIgnoreCase);
                        ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,base_chance_a_to_b,base_chance_b_to_a,
chance_a_to_b,chance_b_to_a,affinity_a_to_b,affinity_b_to_a,
effective_affinity_a_to_b,effective_affinity_b_to_a,first_day,last_day,updated_ts)
VALUES($pair,$a,$b,'ENFJ','INTJ',50,50,50,50,0,0,0,0,1,1,$ts)
ON CONFLICT(pair_key) DO NOTHING;",
                            new Dictionary<string, object>
                            {
                                ["pair"] = pairKey,
                                ["a"] = observerIsA ? observerId : "player_subject",
                                ["b"] = observerIsA ? "player_subject" : observerId,
                                ["ts"] = ts
                            });
                    }
                    ExecuteSql(
                        connection,
                        @"DELETE FROM directional_social_projections
WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='player_subject';",
                        new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId
                            });
                    List<Dictionary<string, object>> pairRowsBefore =
                        QuerySql(
                            connection,
                            @"SELECT pair_key,affinity_a_to_b,affinity_b_to_a,updated_ts
FROM relationship_pair_chemistry
WHERE pair_key IN ($marriedPair,$loverPair)
ORDER BY pair_key;",
                            new Dictionary<string, object>
                            {
                                ["marriedPair"] =
                                    AmbientPairKey("married", "player_subject"),
                                ["loverPair"] =
                                    AmbientPairKey("lover", "player_subject")
                            });
                    ReconcileSocialRelationshipsForSubjects(
                        connection,
                        campaignId,
                        "main",
                        new[] { "player_subject" },
                        1d);
                    Dictionary<string, object> globalStanding =
                        ReadPublicStandingRow(
                            connection,
                            campaignId,
                            "main",
                            "player_subject");
                    Dictionary<string, object> globalMarriedAttitude =
                        ResolveEffectiveAttitude(
                            connection,
                            campaignId,
                            "main",
                            "married",
                            "player_subject",
                            "social_reputation_self_test");
                    Dictionary<string, object> globalLoverAttitude =
                        ResolveEffectiveAttitude(
                            connection,
                            campaignId,
                            "main",
                            "lover",
                            "player_subject",
                            "social_reputation_self_test");
                    ReconcileSocialRelationshipForObserverSubject(
                        connection,
                        campaignId,
                        "main",
                        "married",
                        "player_subject",
                        1d);
                    Dictionary<string, object> targetedStanding =
                        ReadPublicStandingRow(
                            connection,
                            campaignId,
                            "main",
                            "player_subject");
                    Dictionary<string, object> targetedMarriedAttitude =
                        ResolveEffectiveAttitude(
                            connection,
                            campaignId,
                            "main",
                            "married",
                            "player_subject",
                            "social_reputation_self_test");
                    List<Dictionary<string, object>> pairRowsAfter =
                        QuerySql(
                            connection,
                            @"SELECT pair_key,affinity_a_to_b,affinity_b_to_a,updated_ts
FROM relationship_pair_chemistry
WHERE pair_key IN ($marriedPair,$loverPair)
ORDER BY pair_key;",
                            new Dictionary<string, object>
                            {
                                ["marriedPair"] =
                                    AmbientPairKey("married", "player_subject"),
                                ["loverPair"] =
                                    AmbientPairKey("lover", "player_subject")
                            });
                    int copiedProjectionCount = ReadInt(
                        QuerySql(
                            connection,
                            @"SELECT COUNT(*) AS count
FROM directional_social_projections
WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='player_subject';",
                            new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId
                            }).FirstOrDefault(),
                        "count",
                        -1);
                    bool pairRowsUnchanged =
                        pairRowsBefore.Count == pairRowsAfter.Count
                        && pairRowsBefore.Zip(
                            pairRowsAfter,
                            (before, after) =>
                                ReadString(before, "pair_key", "") ==
                                    ReadString(after, "pair_key", "")
                                && ReadInt(before, "affinity_a_to_b", int.MinValue) ==
                                    ReadInt(after, "affinity_a_to_b", int.MaxValue)
                                && ReadInt(before, "affinity_b_to_a", int.MinValue) ==
                                    ReadInt(after, "affinity_b_to_a", int.MaxValue)
                                && Math.Abs(
                                    ReadDouble(before, "updated_ts", -1d) -
                                    ReadDouble(after, "updated_ts", -2d)) <
                                    0.000001d)
                            .All(value => value);
                    int standingValue =
                        ReadInt(globalStanding, "standing_value", int.MinValue);
                    add(
                        "foreground_public_standing_scope_and_parity",
                        globalStanding != null
                        && targetedStanding != null
                        && ReadInt(globalStanding, "revision", -1) ==
                            ReadInt(targetedStanding, "revision", -2)
                        && standingValue ==
                            ReadInt(targetedStanding, "standing_value", int.MaxValue)
                        && Math.Abs(
                            ReadDouble(globalStanding, "updated_ts", -1d) -
                            ReadDouble(targetedStanding, "updated_ts", -2d)) <
                            0.000001d
                        && ReadBool(globalMarriedAttitude, "hasPair", false)
                        && ReadBool(globalLoverAttitude, "hasPair", false)
                        && ReadBool(targetedMarriedAttitude, "hasPair", false)
                        && ReadInt(
                            globalMarriedAttitude,
                            "targetPublicStanding",
                            int.MaxValue) == standingValue
                        && ReadInt(
                            globalLoverAttitude,
                            "targetPublicStanding",
                            int.MaxValue) == standingValue
                        && ReadInt(
                            targetedMarriedAttitude,
                            "effectiveAttitude",
                            int.MaxValue) ==
                            ReadInt(
                                globalMarriedAttitude,
                                "effectiveAttitude",
                                int.MinValue)
                        && copiedProjectionCount == 0
                        && pairRowsUnchanged,
                        "Foreground identity reconciliation reuses one revision-stable subject Public Standing value for existing observers without copying observer projections or rewriting relationship rows.",
                        new Dictionary<string, object>
                        {
                            ["standingValue"] = standingValue,
                            ["globalRevision"] =
                                ReadInt(globalStanding, "revision", -1),
                            ["targetedRevision"] =
                                ReadInt(targetedStanding, "revision", -1),
                            ["copiedProjectionCount"] = copiedProjectionCount,
                            ["pairRowsUnchanged"] = pairRowsUnchanged,
                            ["marriedEffectiveAttitude"] =
                                ReadInt(
                                    targetedMarriedAttitude,
                                    "effectiveAttitude",
                                    int.MinValue),
                            ["loverEffectiveAttitude"] =
                                ReadInt(
                                    globalLoverAttitude,
                                    "effectiveAttitude",
                                    int.MinValue)
                        });
                    AmbientPairContext pair = new AmbientPairContext { PairKey = AmbientPairKey("married", "lover"), HeroAId = "married", HeroBId = "lover" };
                    Dictionary<string, object> first = RegisterSocialOccurrence(connection, campaignId,
                        BuildRelationshipSocialOccurrence(pair, 10, "affair",
                            new Dictionary<string, object> { ["heroStringId"]="married",["spouseId"]="spouse",["clanTier"]=6,["isFemale"]=false },
                            new Dictionary<string, object> { ["heroStringId"]="lover",["spouseId"]="",["clanTier"]=1,["isFemale"]=true },
                            "spouse", "", true, false, "An affair was exposed."));
                    Dictionary<string, object> replay = RegisterSocialOccurrence(connection, campaignId,
                        BuildRelationshipSocialOccurrence(pair, 10, "affair",
                            new Dictionary<string, object> { ["heroStringId"]="married",["spouseId"]="spouse",["clanTier"]=6,["isFemale"]=false },
                            new Dictionary<string, object> { ["heroStringId"]="lover",["spouseId"]="",["clanTier"]=1,["isFemale"]=true },
                            "spouse", "", true, false, "An affair was exposed."));
                    Dictionary<string, object> second = RegisterSocialOccurrence(connection, campaignId,
                        BuildRelationshipSocialOccurrence(pair, 11, "affair",
                            new Dictionary<string, object> { ["heroStringId"]="married",["spouseId"]="spouse",["clanTier"]=6,["isFemale"]=false },
                            new Dictionary<string, object> { ["heroStringId"]="lover",["spouseId"]="",["clanTier"]=1,["isFemale"]=true },
                            "spouse", "", true, true, "The affair was exposed again."));
                    Dictionary<string, object> failedExposurePayload =
                        BuildRelationshipSocialOccurrence(pair, 12, "affair",
                            new Dictionary<string, object> { ["heroStringId"]="married",["spouseId"]="spouse",["clanTier"]=6,["isFemale"]=false },
                            new Dictionary<string, object> { ["heroStringId"]="lover",["spouseId"]="",["clanTier"]=1,["isFemale"]=true },
                            "spouse", "", false, false, "An affair report failed to circulate.");
                    string failedSource = "failed_exposure_0";
                    for (int seedIndex = 0; seedIndex < 100; seedIndex++)
                    {
                        string candidate = "failed_exposure_" + seedIndex.ToString(CultureInfo.InvariantCulture);
                        string seed = string.Join("|", campaignId, "main", "affair", pair.PairKey,
                            candidate, "exposure");
                        if (DeterministicSocialRoll(seed) >= 0.15d)
                        {
                            failedSource = candidate;
                            break;
                        }
                    }
                    failedExposurePayload["sourceEventId"] = failedSource;
                    Dictionary<string, object> failedExposure =
                        RegisterSocialOccurrence(connection, campaignId, failedExposurePayload);
                    add("world_test_rumor_funnel_counts",
                        WorldTestCounterValue(connection, campaignId, "main", 10, "rumors", "eligibleHooks") == 2
                        && WorldTestCounterValue(connection, campaignId, "main", 10, "rumors", "exposureAttempts") == 2
                        && WorldTestCounterValue(connection, campaignId, "main", 10, "rumors", "exposurePassed") == 2
                        && WorldTestCounterValue(connection, campaignId, "main", 10, "rumors", "duplicateSourcesSuppressed") == 1
                        && WorldTestCounterValue(connection, campaignId, "main", 10, "rumors", "occurrencesCreated") == 1
                        && WorldTestCounterValue(connection, campaignId, "main", 11, "rumors", "promotionPassed") == 1
                        && WorldTestCounterValue(connection, campaignId, "main", 11, "rumors", "durableReputationsCreated") == 3
                        && WorldTestCounterValue(connection, campaignId, "main", 12, "rumors", "exposureFailed") == 1
                        && !ReadBool(failedExposure, "exposed", true),
                        "World Test counts eligible hooks, attempts, passed and failed exposure, duplicate suppression, occurrences, promotion, and durable reputations.",
                        failedExposure);
                    add("clan_tier_multiplier_and_cap",
                        Math.Abs(ReadDouble(first, "exposureChance", 0d) - 0.15d) < 0.000001d,
                        "Tier six applies the highest participant multiplier of three to the five-percent base chance.", first);
                    add("one_daily_roll_per_thread",
                        ReadBool(replay, "duplicate", false),
                        "A relationship thread and archetype can produce only one deterministic occurrence per day.", replay);
                    Dictionary<string, object> firstOccurrence = QuerySql(connection,
                        "SELECT world_day,expires_day FROM rumor_occurrences WHERE occurrence_id=$id LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = ReadString(first, "occurrenceId", "") }).FirstOrDefault();
                    add("rumor_duration_45_days",
                        Math.Abs(ReadDouble(firstOccurrence, "expires_day", 0d)
                            - ReadDouble(firstOccurrence, "world_day", 0d) - 45d) < 0.000001d,
                        "Built-in Rumors expire exactly forty-five campaign days after occurrence.", firstOccurrence);
                    add("promotion_streak_and_complete_bundle",
                        ReadInt(first, "streakCount", 0) == 1 && !ReadBool(first, "promoted", true)
                        && ReadInt(second, "streakCount", 0) == 2
                        && Math.Abs(ReadDouble(second, "promotionChance", 0d) - 0.30d) < 0.000001d
                        && QuerySql(connection, "SELECT tag_id FROM character_reputations WHERE subject_id='married';").Count == 2
                        && QuerySql(connection, "SELECT tag_id FROM character_reputations WHERE subject_id='lover';").Count == 1,
                        "The first exposure cannot promote; one later incident roll promotes the married and unmarried role bundles atomically.", second);
                    add("promotion_suppresses_matching_rumors",
                        QuerySql(connection, "SELECT 1 FROM rumor_subject_tags WHERE status='active';").Count == 0,
                        "An established reputation suppresses every matching active rumor without deleting occurrence history.", null);
                    Dictionary<string, object> marriedStanding = ReadPublicStandingTrait(
                        connection, campaignId, "main", "married", 11d);
                    List<Dictionary<string, object>> marriedStandingSources =
                        ReadDictionaryList(marriedStanding, "sources");
                    int stableRevision = ReadInt(marriedStanding, "revision", 0);
                    Dictionary<string, object> stableStanding = ReadPublicStandingTrait(
                        connection, campaignId, "main", "married", 11d);
                    add("public_standing_active_source_summary",
                        ReadString(marriedStanding, "subjectId", "") == "married"
                        && ReadInt(marriedStanding, "standingValue", 0) == -30
                        && marriedStandingSources.Count == 2
                        && marriedStandingSources.All(source =>
                            ReadString(source, "sourceType", "") == "reputation")
                        && marriedStandingSources.Sum(source =>
                            ReadInt(source, "effectiveContribution", 0)) == -30
                        && ReadInt(stableStanding, "revision", 0) == stableRevision
                        && Math.Abs(ReadDouble(stableStanding, "lastChangedCampaignDay", 0d)
                            - ReadDouble(marriedStanding, "lastChangedCampaignDay", 0d)) < 0.000001d,
                        "Public Standing combines active sources after Charm mitigation and remains revision-stable when the projection is unchanged.",
                        marriedStanding);
                    ExecuteSql(connection,
                        "UPDATE identity_roster SET current_charm=100 WHERE hero_id='married';");
                    ReconcileSocialRelationshipsForSubjects(connection, campaignId, "main",
                        new[] { "married" }, 12d);
                    Dictionary<string, object> mitigatedStanding = ReadPublicStandingTrait(
                        connection, campaignId, "main", "married", 12d);
                    add("public_standing_charm_revision",
                        ReadInt(mitigatedStanding, "standingValue", 0) == -24
                        && ReadInt(mitigatedStanding, "revision", 0) == stableRevision + 1
                        && Math.Abs(ReadDouble(mitigatedStanding,
                            "lastChangedCampaignDay", 0d) - 12d) < 0.000001d,
                        "A subject Charm change recalculates negative Public Standing, advances its revision, and records the campaign day.",
                        mitigatedStanding);

                    AmbientPairContext divorcePair = new AmbientPairContext { PairKey = AmbientPairKey("married", "lover"), HeroAId = "married", HeroBId = "lover" };
                    RegisterSocialOccurrence(connection, campaignId, BuildRelationshipSocialOccurrence(divorcePair, 20, "divorce",
                        new Dictionary<string, object> { ["heroStringId"]="married",["isFemale"]=false },
                        new Dictionary<string, object> { ["heroStringId"]="lover",["isFemale"]=true }, "lover", "married", true, true, "They divorced."));
                    int maleDivorce = ReadInt(QuerySql(connection, "SELECT reputation_value FROM character_reputations WHERE subject_id='married' AND tag_id='divorcee';").FirstOrDefault(), "reputation_value", 0);
                    int femaleDivorce = ReadInt(QuerySql(connection, "SELECT reputation_value FROM character_reputations WHERE subject_id='lover' AND tag_id='divorcee';").FirstOrDefault(), "reputation_value", 0);
                    add("gendered_divorce_reputation", maleDivorce == -5 && femaleDivorce == -20,
                        "Divorce directly establishes the snapshotted male and female reputation values for both former spouses.",
                        new Dictionary<string, object> { ["male"] = maleDivorce, ["female"] = femaleDivorce });

                    string occurrenceId = ReadString(first, "occurrenceId", "");
                    CorrectSocialOccurrenceApi(new Dictionary<string, object> { ["campaignId"]=campaignId,["timelineId"]="main",["occurrenceId"]=occurrenceId });
                    add("correction_preserves_reputation_history",
                        QuerySql(connection, "SELECT 1 FROM rumor_occurrences WHERE occurrence_id=$id AND status='disproven';",
                            new Dictionary<string, object> { ["id"] = occurrenceId }).Any()
                        && QuerySql(connection, "SELECT 1 FROM character_reputations WHERE status='active';").Any(),
                        "Correction deactivates the occurrence and resets its active streak while retaining already-established reputations.", null);
                    add("world_test_rumor_correction_counts",
                        WorldTestCounterValue(connection, campaignId, "main", 10, "rumors", "corrected") == 1
                        && WorldTestCounterValue(connection, campaignId, "main", 10, "rumors", "disproven") == 1,
                        "Correction records bounded funnel counters without changing retained reputation history.", null);

                    Func<string, string, string, string, double, bool, Dictionary<string, object>> social =
                        (archetype, subject, role, source, day, forcePromotion) =>
                            RegisterSocialOccurrence(connection, campaignId, new Dictionary<string, object>
                            {
                                ["timelineId"] = "main", ["archetypeId"] = archetype,
                                ["threadKey"] = subject + "|" + archetype, ["sourceEventId"] = source,
                                ["worldDay"] = day, ["forceExposure"] = true, ["forcePromotion"] = forcePromotion,
                                ["provenanceSummary"] = "Expanded social self-test.",
                                ["participants"] = new List<object>
                                {
                                    new Dictionary<string, object>
                                    {
                                        ["subjectId"] = subject, ["role"] = role, ["clanTier"] = 6,
                                        ["isAlive"] = true, ["isAdult"] = true, ["isPlayer"] = subject == "player_subject"
                                    }
                                }
                            });

                    Dictionary<string, object> courtSubject = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "court_subject", ["name"] = "Court Subject",
                        ["isAlive"] = true, ["isAdult"] = true, ["isPlayer"] = false,
                        ["isLord"] = true, ["isRuler"] = true, ["occupation"] = "Lord", ["kingdomId"] = "kingdom_court",
                        ["clanId"] = "court_subject_clan", ["clanTier"] = 3, ["currentCharm"] = 0
                    };
                    UpsertIdentityRosterHero(connection, courtSubject, ts);
                    for (int index = 0; index < 50; index++)
                    {
                        string observerId = "court_observer_" + index.ToString("00", CultureInfo.InvariantCulture);
                        UpsertIdentityRosterHero(connection, new Dictionary<string, object>
                        {
                            ["heroStringId"] = observerId, ["name"] = "Court Observer " + index,
                            ["isAlive"] = true, ["isAdult"] = true, ["isPlayer"] = false,
                            ["isLord"] = true, ["occupation"] = "Lord", ["kingdomId"] = "kingdom_court",
                            ["clanId"] = "court_observer_clan_" + index, ["clanTier"] = 2,
                            ["isMercenaryClan"] = false, ["currentCharm"] = 0
                        }, ts);
                        string pairKey = AmbientPairKey(observerId, "court_subject");
                        bool observerIsA = pairKey.StartsWith(observerId + "|", StringComparison.OrdinalIgnoreCase);
                        int incoming = index < 30 ? 35 : -35;
                        ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,
effective_affinity_a_to_b,effective_affinity_b_to_a,first_day,last_day,updated_ts)
VALUES($pair,$a,$b,$ab,$ba,$ab,$ba,1,1,$ts)
ON CONFLICT(pair_key) DO UPDATE SET affinity_a_to_b=$ab,affinity_b_to_a=$ba,
effective_affinity_a_to_b=$ab,effective_affinity_b_to_a=$ba,updated_ts=$ts;",
                            new Dictionary<string, object>
                            {
                                ["pair"] = pairKey,
                                ["a"] = observerIsA ? observerId : "court_subject",
                                ["b"] = observerIsA ? "court_subject" : observerId,
                                ["ab"] = observerIsA ? incoming : 0,
                                ["ba"] = observerIsA ? 0 : incoming,
                                ["ts"] = ts
                            });
                    }
                    bool popularityChanged = RecomputeCourtPopularityReputations(connection,
                        campaignId, "main", "court_subject", 20d, "court_popularity_self_test");
                    Dictionary<string, object> popularityMetric = QuerySql(connection, @"SELECT *
FROM court_dynamic_reputation_metrics WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='court_subject' AND domain='popularity' LIMIT 1;",
                        new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();
                    List<Dictionary<string, object>> activePopularity = QuerySql(connection, @"SELECT tag_id,reputation_value
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='court_subject' AND status='active'
AND tag_id IN ('well_regarded','court_favorite','beloved_of_the_court',
'ill_regarded','shunned_at_court','court_pariah') ORDER BY tag_id;",
                        new Dictionary<string, object> { ["campaign"] = campaignId });
                    add("court_popularity_underlying_affinity_thresholds",
                        popularityChanged
                        && ReadInt(popularityMetric, "positive_count", 0) == 30
                        && ReadInt(popularityMetric, "negative_count", 0) == 20
                        && activePopularity.Any(x => ReadString(x, "tag_id", "") == "beloved_of_the_court"
                            && ReadInt(x, "reputation_value", 0) == 15)
                        && activePopularity.Any(x => ReadString(x, "tag_id", "") == "shunned_at_court"
                            && ReadInt(x, "reputation_value", 0) == -10)
                        && activePopularity.Count == 2,
                        "Only the highest dynamic tier per polarity is active, and positive and negative court standing may coexist.",
                        new Dictionary<string, object>
                        {
                            ["metric"] = popularityMetric, ["active"] = activePopularity
                        });
                    ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET social_modifier_a_to_b=50,social_modifier_b_to_a=-50,
effective_affinity_a_to_b=100,effective_affinity_b_to_a=-100
WHERE hero_a_id='court_subject' OR hero_b_id='court_subject';");
                    bool feedbackChanged = RecomputeCourtPopularityReputations(connection,
                        campaignId, "main", "court_subject", 21d, "court_popularity_no_feedback");
                    Dictionary<string, object> feedbackMetric = QuerySql(connection, @"SELECT *
FROM court_dynamic_reputation_metrics WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='court_subject' AND domain='popularity' LIMIT 1;",
                        new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();
                    add("court_popularity_ignores_social_feedback",
                        !feedbackChanged
                        && ReadInt(feedbackMetric, "positive_count", 0) == 30
                        && ReadInt(feedbackMetric, "negative_count", 0) == 20,
                        "Popularity derives from directional underlying affinity and never feeds its own effective social modifiers back into the count.",
                        feedbackMetric);

                    foreach (Dictionary<string, object> hero in new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["heroStringId"]="court_male",["name"]="Court Male",["isAlive"]=true,
                            ["isAdult"]=true,["isLord"]=true,["occupation"]="Lord",["isFemale"]=false,
                            ["kingdomId"]="kingdom_court",["clanTier"]=3
                        },
                        new Dictionary<string, object>
                        {
                            ["heroStringId"]="court_female",["name"]="Court Female",["isAlive"]=true,
                            ["isAdult"]=true,["isLord"]=true,["occupation"]="Lord",["isFemale"]=true,
                            ["kingdomId"]="kingdom_court",["clanTier"]=3
                        }
                    }) UpsertIdentityRosterHero(connection, hero, ts);

                    Func<string, string, string, double, bool, Dictionary<string, object>> favored =
                        (subjectId, linkedId, linkedName, day, promote) =>
                            RegisterSocialOccurrence(connection, campaignId, new Dictionary<string, object>
                            {
                                ["timelineId"] = "main", ["archetypeId"] = "ruler_favoring_presence",
                                ["threadKey"] = subjectId + "|ruler_favoring|" + linkedId,
                                ["sourceEventId"] = subjectId + "|" + linkedId + "|" + day.ToString(CultureInfo.InvariantCulture),
                                ["worldDay"] = day, ["forceExposure"] = true, ["forcePromotion"] = promote,
                                ["provenanceSummary"] = linkedName + " showed recorded favor toward the subject.",
                                ["participants"] = new List<object>
                                {
                                    new Dictionary<string, object>
                                    {
                                        ["subjectId"] = subjectId, ["role"] = "ruler", ["clanTier"] = 3,
                                        ["isAlive"] = true, ["isAdult"] = true,
                                        ["linkedHeroId"] = linkedId, ["linkedHeroName"] = linkedName
                                    }
                                }
                            });
                    favored("court_subject", "court_male", "Court Male", 22d, false);
                    favored("court_subject", "court_male", "Court Male", 23d, true);
                    favored("court_subject", "court_female", "Court Female", 22d, false);
                    favored("court_subject", "court_female", "Court Female", 23d, true);
                    List<Dictionary<string, object>> favoredReputations = QuerySql(connection, @"SELECT tag_id,
reputation_value,snapshot_json FROM character_reputations WHERE campaign_id=$campaign
AND timeline_id='main' AND subject_id='court_subject' AND status='active'
AND tag_id LIKE 'ruler_favoring:%' ORDER BY tag_id;",
                        new Dictionary<string, object> { ["campaign"] = campaignId });
                    add("court_favored_by_parameterized",
                        favoredReputations.Count == 2
                        && favoredReputations.All(x => ReadInt(x, "reputation_value", 0) == -10)
                        && favoredReputations.Any(x => ReadString(x, "snapshot_json", "").Contains("Court Male"))
                        && favoredReputations.Any(x => ReadString(x, "snapshot_json", "").Contains("Court Female")),
                        "Ruler Favoring promotion is independently streaked and stored for each favorite while Public Standing collapses its global cost.",
                        favoredReputations);

                    SetCourtDynamicReputation(connection, campaignId, "main", "court_subject",
                        "dynasty_secure", true, 3, 24d, "dynasty_self_test", "",
                        "Three publicly acknowledged living children secure the subject's dynasty.",
                        new Dictionary<string, object> { ["livingAcknowledgedChildCount"] = 3 });
                    Dictionary<string, object> dynastyRow = QuerySql(connection, @"SELECT reputation_value,status
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='court_subject' AND tag_id='dynasty_secure' LIMIT 1;",
                        new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();
                    SetCourtDynamicReputation(connection, campaignId, "main", "court_subject",
                        "dynasty_secure", false, 0, 25d, "dynasty_self_test_lost", "",
                        "No publicly acknowledged living child currently secures the subject's dynasty.",
                        new Dictionary<string, object> { ["livingAcknowledgedChildCount"] = 0 });
                    Dictionary<string, object> dynastyLost = QuerySql(connection, @"SELECT reputation_value,status
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='court_subject' AND tag_id='dynasty_secure' LIMIT 1;",
                        new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();
                    add("court_dynasty_dynamic_value",
                        ReadInt(dynastyRow, "reputation_value", 0) == 3
                        && ReadString(dynastyRow, "status", "") == "active"
                        && ReadString(dynastyLost, "status", "") == "derived_requirement_lost",
                        "Dynasty Secure is one dynamic relationship point per living acknowledged child and disappears immediately when the requirement is lost.",
                        new Dictionary<string, object> { ["active"] = dynastyRow, ["lost"] = dynastyLost });

                    Dictionary<string, object> judgmentCatalog = ReadActiveSocialCatalog(connection);
                    List<object> judgmentResults = new List<object>();
                    int judgmentIndex = 0;
                    foreach (string judgmentTagId in NobleJudgmentDirectTagIds)
                    {
                        string judgmentEventId = "noble_judgment_event_"
                            + (judgmentIndex++).ToString(CultureInfo.InvariantCulture);
                        bool handled = ApplyCourtDynamicSocialOutcome(connection,
                            campaignId, "main", "court_subject",
                            new Dictionary<string, object>
                            {
                                ["dynamicReputationTagId"] = judgmentTagId,
                                ["dynamicActive"] = true,
                                ["dynamicValue"] = 4,
                                ["dynamicDescription"] = "A formal noble judgment established this reputation.",
                                ["directReputationProducer"] = "noble_judgment",
                                ["provenanceSummary"] = "The ruler entered a formal judgment.",
                                ["dynamicEvidence"] = new Dictionary<string, object>
                                {
                                    ["matterId"] = "noble_judgment_self_test",
                                    ["sourceCorrelationId"] = "noble_judgment_self_test|judgment|court_subject|"
                                        + judgmentTagId
                                }
                            }, judgmentEventId, 25.5d,
                            out Dictionary<string, object> judgmentResult,
                            judgmentCatalog);
                        judgmentResults.Add(new Dictionary<string, object>
                        {
                            ["tagId"] = judgmentTagId,
                            ["handled"] = handled,
                            ["result"] = judgmentResult
                        });
                    }
                    bool reaffirmed = ApplyCourtDynamicSocialOutcome(connection,
                        campaignId, "main", "court_subject",
                        new Dictionary<string, object>
                        {
                            ["dynamicReputationTagId"] = "incompetent",
                            ["dynamicActive"] = true,
                            ["dynamicValue"] = 4,
                            ["dynamicDescription"] = "A formal noble judgment established this reputation.",
                            ["directReputationProducer"] = "noble_judgment",
                            ["provenanceSummary"] = "The ruler reaffirmed the formal judgment.",
                            ["dynamicEvidence"] = new Dictionary<string, object>
                            {
                                ["matterId"] = "noble_judgment_self_test_reaffirmed",
                                ["sourceCorrelationId"] = "noble_judgment_self_test_reaffirmed|judgment|court_subject|incompetent"
                            }
                        }, "noble_judgment_event_reaffirmed", 25.6d,
                        out Dictionary<string, object> reaffirmedResult,
                        judgmentCatalog);
                    List<Dictionary<string, object>> judgmentRows = QuerySql(connection, @"SELECT tag_id,status
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='court_subject' AND tag_id IN ('incompetent','cruel','dishonorable','divorcee','disloyal',
'promiscuous','the_unchaste','corrupt','coward','traitor','murderous','murderer','convicted_murderer');",
                        new Dictionary<string, object> { ["campaign"] = campaignId });
                    Dictionary<string, object> reaffirmedActivation = QuerySql(connection, @"SELECT ra.source_event_id,ra.evidence_json
FROM character_reputations cr JOIN reputation_activations ra
ON ra.activation_id=cr.reason_activation_id
WHERE cr.campaign_id=$campaign AND cr.timeline_id='main' AND cr.subject_id='court_subject'
AND cr.tag_id='incompetent' LIMIT 1;",
                        new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();
                    Dictionary<string, object> reaffirmedEvidence = TryParseJsonObject(
                        ReadString(reaffirmedActivation, "evidence_json", "{}"))
                        ?? new Dictionary<string, object>();
                    string reaffirmedCorrelation = ReadString(DictionaryOrDefault(
                        reaffirmedEvidence, "dynamicEvidence", new Dictionary<string, object>()),
                        "sourceCorrelationId", "");
                    bool rejectedUnauthorizedProducer = ApplyCourtDynamicSocialOutcome(connection,
                        campaignId, "main", "unauthorized_judgment_subject",
                        new Dictionary<string, object>
                        {
                            ["dynamicReputationTagId"] = "flirt",
                            ["dynamicActive"] = true,
                            ["dynamicValue"] = -5,
                            ["dynamicDescription"] = "This must not be created by a noble judgment.",
                            ["directReputationProducer"] = "noble_judgment",
                            ["provenanceSummary"] = "Unauthorized direct producer guard self-test."
                        }, "noble_judgment_event_unauthorized", 25.7d,
                        out Dictionary<string, object> unauthorizedResult,
                        judgmentCatalog)
                        && !ReadBool(unauthorizedResult, "changed", false);
                    Dictionary<string, object> unauthorizedRow = QuerySql(connection, @"SELECT tag_id,status
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='unauthorized_judgment_subject' AND tag_id='flirt' LIMIT 1;",
                        new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();
                    add("court_noble_judgment_direct_reputation_tags",
                        judgmentResults.OfType<Dictionary<string, object>>().All(x =>
                            ReadBool(x, "handled", false)
                            && ReadBool(DictionaryOrDefault(x, "result",
                                new Dictionary<string, object>()), "changed", false))
                        && judgmentRows.Count == NobleJudgmentDirectTagIds.Length
                        && judgmentRows.All(x => ReadString(x, "status", "") == "active")
                        && reaffirmed
                        && ReadBool(reaffirmedResult, "changed", false)
                        && ReadString(reaffirmedActivation, "source_event_id", "")
                            == "noble_judgment_event_reaffirmed"
                        && reaffirmedCorrelation
                            == "noble_judgment_self_test_reaffirmed|judgment|court_subject|incompetent"
                        && rejectedUnauthorizedProducer
                        && unauthorizedRow == null,
                        "Every docket-applicable reputation accepts only the explicit noble-judgment producer, and a repeated judgment refreshes exact activation provenance.",
                        new Dictionary<string, object>
                        {
                            ["initial"] = judgmentResults,
                            ["rows"] = judgmentRows,
                            ["reaffirmed"] = reaffirmedResult,
                            ["activation"] = reaffirmedActivation,
                            ["activationSourceCorrelationId"] = reaffirmedCorrelation,
                            ["unauthorizedResult"] = unauthorizedResult,
                            ["unauthorizedRow"] = unauthorizedRow
                        });

                    const string ingestJudgmentEventId =
                        "noble_judgment_world_history_ingest_self_test";
                    const string ingestJudgmentCorrelation =
                        "noble_judgment_ingest_self_test|judgment|court_ingest_subject|incompetent";
                    Dictionary<string, object> ingestJudgment =
                        WorldHistoryIngestBatchApi(new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["timelineId"] = "main",
                            ["clientId"] = "rumor-self-test",
                            ["historyCompleteFromWorldDay"] = 0d,
                            ["events"] = new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    ["eventId"] = ingestJudgmentEventId,
                                    ["sequence"] = 26001L,
                                    ["worldDay"] = 25.8d,
                                    ["eventType"] = "social_outcome",
                                    ["phase"] = "completed",
                                    ["category"] = "social",
                                    ["correlationId"] = ingestJudgmentCorrelation,
                                    ["summary"] = "The ruler entered a formal judgment through World History.",
                                    ["source"] = "reign_social",
                                    ["isComplete"] = true,
                                    ["entities"] = new List<object>(),
                                    ["payload"] = new Dictionary<string, object>
                                    {
                                        ["subjectId"] = "court_ingest_subject",
                                        ["role"] = "derived",
                                        ["dynamicReputationTagId"] = "incompetent",
                                        ["dynamicActive"] = true,
                                        ["dynamicValue"] = 3,
                                        ["dynamicDescription"] = "A formal noble judgment established this reputation.",
                                        ["directReputationProducer"] = "noble_judgment",
                                        ["sourceEventId"] = ingestJudgmentCorrelation,
                                        ["provenanceSummary"] = "The ruler entered a formal judgment through World History.",
                                        ["dynamicEvidence"] = new Dictionary<string, object>
                                        {
                                            ["matterId"] = "noble_judgment_ingest_self_test",
                                            ["sourceCorrelationId"] = ingestJudgmentCorrelation
                                        }
                                    }
                                }
                            }
                        });
                    Dictionary<string, object> ingestJudgmentProfile =
                        SocialCharacterStatusApi(new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["timelineId"] = "main",
                            ["subjectId"] = "court_ingest_subject",
                            ["worldDay"] = 25.8d
                        });
                    Dictionary<string, object> ingestJudgmentReputation =
                        ReadDictionaryList(ingestJudgmentProfile, "reputations")
                            .FirstOrDefault(row => string.Equals(ReadString(row,
                                    "tag_id", ""), "incompetent",
                                StringComparison.OrdinalIgnoreCase));
                    add("court_direct_judgment_acknowledged_with_world_history_ingest",
                        ReadBool(ingestJudgment, "ok", false)
                        && ReadInt(ingestJudgment,
                            "directSocialOutcomesAcknowledged", 0) == 1
                        && ingestJudgmentReputation != null
                        && ReadString(ingestJudgmentReputation,
                            "activation_source_event_id", "")
                            == ingestJudgmentEventId
                        && ReadString(ingestJudgmentReputation,
                            "activation_source_correlation_id", "")
                            == ingestJudgmentCorrelation,
                        "World History acknowledges a direct noble-judgment outcome only after its exact authoritative reputation activation is queryable.",
                        new Dictionary<string, object>
                        {
                            ["ingest"] = ingestJudgment,
                            ["reputation"] = ingestJudgmentReputation
                        });

                    social("the_unchaste", "court_male", "parent", "unchaste_male", 26d, true);
                    social("the_unchaste", "court_female", "parent", "unchaste_female", 26d, true);
                    int maleUnchaste = ReadInt(QuerySql(connection, @"SELECT reputation_value
FROM character_reputations WHERE campaign_id=$campaign AND subject_id='court_male'
AND tag_id='the_unchaste' AND status='active' LIMIT 1;",
                        new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault(),
                        "reputation_value", 0);
                    int femaleUnchaste = ReadInt(QuerySql(connection, @"SELECT reputation_value
FROM character_reputations WHERE campaign_id=$campaign AND subject_id='court_female'
AND tag_id='the_unchaste' AND status='active' LIMIT 1;",
                        new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault(),
                        "reputation_value", 0);
                    add("court_unchaste_gender_values",
                        maleUnchaste == -5 && femaleUnchaste == -50,
                        "Public illegitimacy snapshots the configured male and female court penalties.",
                        new Dictionary<string, object> { ["male"] = maleUnchaste, ["female"] = femaleUnchaste });

                    social("affair", "court_male", "unmarried", "shared_promiscuous_affair", 26.5d, false);
                    Dictionary<string, object> sharedFlirtResult = social("promiscuous_flirtation",
                        "court_male", "speaker", "shared_promiscuous_flirt", 27.5d, false);
                    Dictionary<string, object> sharedPromiscuousStreak = QuerySql(connection, @"SELECT
streak_count,last_exposure_day FROM shared_tag_exposure_streaks
WHERE campaign_id=$campaign AND timeline_id='main' AND subject_id='court_male'
AND tag_id='promiscuous' LIMIT 1;",
                        new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();
                    add("court_promiscuous_shared_cross_producer_streak",
                        ReadInt(sharedPromiscuousStreak, "streak_count", 0) == 2
                        && ReadDictionaryList(sharedFlirtResult, "sharedPromotionResults").Any(x =>
                            ReadString(x, "tagId", "") == "promiscuous"
                            && ReadInt(x, "streakCount", 0) == 2
                            && Math.Abs(ReadDouble(x, "promotionChance", 0d) - 0.30d) < 0.000001d),
                        "Exposed affairs and validated multi-target flirtation advance one shared character/Promiscuous promotion streak.",
                        new Dictionary<string, object>
                        {
                            ["streak"] = sharedPromiscuousStreak,
                            ["flirtResult"] = sharedFlirtResult
                        });

                    Dictionary<string, object> signalSession = new Dictionary<string, object>
                    {
                        ["session_id"] = "court_signal_unvalidated", ["channel"] = "in_person",
                        ["npc_id"] = "court_female", ["player_id"] = "player_subject",
                        ["payload_json"] = "{\"timelineId\":\"main\"}"
                    };
                    List<Dictionary<string, object>> signalTurns = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["turn_id"]="signal_player",["exchange_id"]="signal_exchange",["role"]="player",
                            ["payload_json"]="{\"socialSignals\":[{\"signalId\":\"flirt_test\",\"type\":\"flirtation\",\"validated\":true,\"confidence\":\"explicit\",\"validationSource\":\"untrusted_text_parser\",\"speakerHeroId\":\"player_subject\",\"targetHeroId\":\"court_female\"}]}"
                        },
                        new Dictionary<string, object>
                        {
                            ["turn_id"]="signal_npc",["exchange_id"]="signal_exchange",["role"]="npc",
                            ["payload_json"]="{}"
                        }
                    };
                    ProcessCompletedConversationCourtStanding(campaignId, signalSession, signalTurns, 27d, false);
                    int rejectedSignals = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM court_conversation_signal_receipts WHERE campaign_id=$campaign AND session_id='court_signal_unvalidated';",
                        new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault(), "count", -1);
                    signalSession["session_id"] = "court_signal_validated";
                    signalTurns[0]["turn_id"] = "signal_player_validated";
                    signalTurns[0]["payload_json"] = "{\"socialSignals\":[{\"signalId\":\"flirt_test_validated\",\"type\":\"flirtation\",\"validated\":true,\"confidence\":\"explicit\",\"validationSource\":\"reign_conversation_engine\",\"speakerHeroId\":\"player_subject\",\"targetHeroId\":\"court_female\"}]}";
                    ProcessCompletedConversationCourtStanding(campaignId, signalSession, signalTurns, 28d, false);
                    int acceptedSignals = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM court_conversation_signal_receipts WHERE campaign_id=$campaign AND session_id='court_signal_validated';",
                        new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault(), "count", -1);
                    add("court_flirt_requires_validated_signal",
                        rejectedSignals == 0 && acceptedSignals == 1,
                        "Raw text or untrusted parser output cannot create Flirt evidence; only the explicit structured Reign conversation-engine contract is accepted.",
                        new Dictionary<string, object>
                        {
                            ["rejectedReceiptCount"] = rejectedSignals,
                            ["acceptedReceiptCount"] = acceptedSignals
                        });

                    var singleSpeakerTurns = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["exchange_id"] = "family_one", ["role"] = "player", ["text"] = "Tell me what you found." },
                        new Dictionary<string, object> { ["exchange_id"] = "family_one", ["role"] = "npc", ["speaker_id"] = "lover", ["text"] = "I will." }
                    };
                    var groupSpeakerTurns = new List<Dictionary<string, object>>(singleSpeakerTurns)
                    {
                        new Dictionary<string, object> { ["exchange_id"] = "family_one", ["role"] = "npc", ["speaker_id"] = "married", ["text"] = "I heard it too." }
                    };
                    add("court_family_chambers_party_chat_social_boundary",
                        IsCourtStandingConversationChannel("in_person")
                        && IsCourtStandingConversationChannel("party_chat")
                        && !IsCourtStandingConversationChannel("correspondence")
                        && SingleQualifyingFavorNpcId(singleSpeakerTurns, new[] { "family_one" }) == "lover"
                        && string.IsNullOrWhiteSpace(SingleQualifyingFavorNpcId(groupSpeakerTurns, new[] { "family_one" })),
                        "Family Chambers party chat participates in court-social processing, while only a one-NPC conversation can advance that NPC's ruler-favor counter.",
                        new Dictionary<string, object>
                        {
                            ["singleSpeaker"] = SingleQualifyingFavorNpcId(singleSpeakerTurns, new[] { "family_one" }),
                            ["groupSpeaker"] = SingleQualifyingFavorNpcId(groupSpeakerTurns, new[] { "family_one" })
                        });

                    foreach (Dictionary<string, object> hero in new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["heroStringId"]="group_intro_alpha",["name"]="Lady Alpha",["isAlive"]=true,
                            ["isAdult"]=true,["isLord"]=true,["occupation"]="Lord",["isFemale"]=true,
                            ["kingdomId"]="kingdom_court",["clanTier"]=3
                        },
                        new Dictionary<string, object>
                        {
                            ["heroStringId"]="group_intro_beta",["name"]="Lord Beta",["isAlive"]=true,
                            ["isAdult"]=true,["isLord"]=true,["occupation"]="Lord",["isFemale"]=false,
                            ["kingdomId"]="kingdom_court",["clanTier"]=3
                        }
                    }) UpsertIdentityRosterHero(connection, hero, ts);
                    ExecuteSql(connection, @"DELETE FROM acquaintances
WHERE (observer_id='group_intro_alpha' AND subject_id='group_intro_beta')
   OR (observer_id='group_intro_beta' AND subject_id='group_intro_alpha');");
                    var explicitIntroductionTurns = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["exchange_id"]="family_intro",["role"]="player",["speaker_id"]="player_subject",["text"]="Lady Alpha, meet Lord Beta." },
                        new Dictionary<string, object> { ["exchange_id"]="family_intro",["role"]="npc",["speaker_id"]="group_intro_alpha",["text"]="I receive the introduction." },
                        new Dictionary<string, object> { ["exchange_id"]="family_intro",["role"]="npc",["speaker_id"]="group_intro_beta",["text"]="And I return the courtesy." }
                    };
                    List<Dictionary<string, object>> explicitIntroductions =
                        ProcessExplicitGroupConversationIntroductions(connection, "party_chat",
                            "family_group_intro", explicitIntroductionTurns, 29d);
                    Dictionary<string, object> alphaKnowsBeta =
                        ReadStoredAcquaintance(connection, "group_intro_alpha", "group_intro_beta");
                    Dictionary<string, object> betaKnowsAlpha =
                        ReadStoredAcquaintance(connection, "group_intro_beta", "group_intro_alpha");
                    add("court_family_chambers_explicit_group_introduction",
                        explicitIntroductions.Count == 2
                        && IdentityStateVerified(alphaKnowsBeta)
                        && IdentityStateVerified(betaKnowsAlpha)
                        && ReadString(alphaKnowsBeta, "verification_source", "") == "explicit_group_introduction"
                        && ReadString(betaKnowsAlpha, "verification_source", "") == "explicit_group_introduction"
                        && ProcessExplicitGroupConversationIntroductions(connection, "in_person",
                            "ordinary_audience", explicitIntroductionTurns, 29d).Count == 0,
                        "An explicit, named Family Chambers introduction verifies both adult participants, while ordinary one-to-one audiences do not use the group rule.",
                        new Dictionary<string, object>
                        {
                            ["writes"] = explicitIntroductions,
                            ["alphaKnowsBeta"] = alphaKnowsBeta,
                            ["betaKnowsAlpha"] = betaKnowsAlpha
                        });

                    Dictionary<string, object> sameDayA = social("strong_captain", "player_subject", "commander", "same_day_a", 30d, true);
                    Dictionary<string, object> sameDayB = social("strong_captain", "player_subject", "commander", "same_day_b", 30d, false);
                    add("player_subject_and_distinct_same_day_outcomes",
                        ReadBool(sameDayA, "exposed", false) && ReadBool(sameDayB, "exposed", false)
                        && QuerySql(connection, @"SELECT 1 FROM rumor_occurrences
WHERE campaign_id=$campaign AND archetype_id='strong_captain' AND thread_key='player_subject|strong_captain' AND world_day=30;",
                            new Dictionary<string, object> { ["campaign"] = campaignId }).Count == 2,
                        "Living adult players are eligible, and independently sourced combat outcomes on one day remain distinct.",
                        new Dictionary<string, object> { ["first"] = sameDayA, ["second"] = sameDayB });

                    // Counterevidence only applies to an established active
                    // Reputation.  The same-day outcomes above deliberately
                    // test rumor-source deduplication and do not promise that
                    // the promotion roll succeeds, so establish the fixture's
                    // precondition explicitly instead of depending on a
                    // coincidental earlier catalog/test state.
                    ExecuteSql(connection, @"INSERT INTO character_reputations(
campaign_id,timeline_id,subject_id,tag_id,source_occurrence_id,archetype_id,
subject_role,description,reputation_value,acquired_day,catalog_revision,
snapshot_json,status,updated_ts)
VALUES($campaign,'main','player_subject','strong_captain','counter_fixture',
'strong_captain','commander','Counterevidence fixture reputation.',10,30.5,
$revision,'{}','active',$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,tag_id) DO UPDATE SET
status='active',updated_ts=excluded.updated_ts;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId,
                            ["revision"] = BuiltInSocialCatalogRevision,
                            ["ts"] = ts
                        });
                    Dictionary<string, object> counterFixtureState = QuerySql(connection, @"SELECT campaign_id,timeline_id,subject_id,tag_id,status
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='player_subject' AND tag_id='strong_captain' LIMIT 1;",
                        new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();

                    for (int count = 1; count <= 3; count++)
                    {
                        double requiredRoll = count * 0.05d;
                        string counterEvent = Enumerable.Range(0, 10000)
                            .Select(index => "counter_" + count + "_" + index)
                            .First(candidate => DeterministicSocialRoll(string.Join("|", campaignId, "main",
                                "player_subject", "strong_captain", candidate, "counter")) >= requiredRoll);
                        ApplySocialCounterevidence(connection, campaignId, "main", "player_subject",
                            "weak_captain", new List<string>(), counterEvent, 30d + count);
                    }
                    Dictionary<string, object> counterState = QuerySql(connection, @"SELECT consecutive_count,last_chance
FROM reputation_counterevidence WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='player_subject' AND tag_id='strong_captain' LIMIT 1;",
                        new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();
                    ApplySocialCounterevidence(connection, campaignId, "main", "player_subject",
                        "strong_captain", new List<string>(), "matching_reset", 34d);
                    Dictionary<string, object> resetState = QuerySql(connection, @"SELECT consecutive_count
FROM reputation_counterevidence WHERE campaign_id=$campaign AND timeline_id='main'
AND subject_id='player_subject' AND tag_id='strong_captain' LIMIT 1;",
                        new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();
                    add("counterevidence_stack_and_reset",
                        ReadInt(counterState, "consecutive_count", 0) == 3
                        && Math.Abs(ReadDouble(counterState, "last_chance", 0d) - 0.15d) < 0.000001d
                        && ReadInt(resetState, "consecutive_count", -1) == 0,
                        "Opposite outcomes advance removal chances by five percentage points and matching outcomes reset the stack. "
                        + "Fixture=" + Json.Serialize(counterFixtureState) + ". "
                        + "Observed before=" + Json.Serialize(counterState)
                        + ", after=" + Json.Serialize(resetState) + ".",
                        new Dictionary<string, object> { ["beforeReset"] = counterState, ["afterReset"] = resetState });

                    social("tactician", "player_subject", "commander", "derived_tactician", 35d, true);
                    Dictionary<string, object> directReputationOccurrence =
                        social("war_crowned", "player_subject", "ruler", "derived_war", 36d, true);
                    string directReputationOccurrenceId = ReadString(
                        directReputationOccurrence, "occurrenceId", "");
                    Dictionary<string, object> finalizedDirectOccurrence =
                        QuerySql(connection, @"SELECT status,expires_day FROM rumor_occurrences
WHERE occurrence_id=$id LIMIT 1;", new Dictionary<string, object>
                        {
                            ["id"] = directReputationOccurrenceId
                        }).FirstOrDefault();
                    add("zero_duration_direct_reputation_finalizes_occurrence",
                        ReadString(directReputationOccurrence, "occurrenceStatus", "") == "expired"
                        && ReadString(finalizedDirectOccurrence, "status", "") == "expired"
                        && QuerySql(connection, @"SELECT 1 FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id='main' AND subject_id='player_subject'
AND tag_id='war_crowned' AND status='active' LIMIT 1;",
                            new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId
                            }).Any(),
                        "Zero-duration direct-reputation outcomes retain their durable reputation but do not remain as phantom active rumors.",
                        new Dictionary<string, object>
                        {
                            ["result"] = directReputationOccurrence,
                            ["occurrence"] = finalizedDirectOccurrence
                        });
                    social("realm_builder", "player_subject", "ruler", "derived_realm", 37d, true);
                    social("unifier_of_the_realm", "player_subject", "ruler", "derived_unifier", 38d, true);
                    social("fair_hand_of_the_crown", "player_subject", "ruler", "derived_fair", 39d, true);
                    RecomputeDerivedSocialReputations(connection, campaignId, "main", "player_subject", 39d, "derived_check");
                    add("derived_combat_and_governance_reputations",
                        QuerySql(connection, @"SELECT 1 FROM character_reputations
WHERE campaign_id=$campaign AND subject_id='player_subject' AND tag_id='battlemaster' AND status='active';",
                            new Dictionary<string, object> { ["campaign"] = campaignId }).Any()
                        && QuerySql(connection, @"SELECT 1 FROM character_reputations
WHERE campaign_id=$campaign AND subject_id='player_subject' AND tag_id='steward_of_the_realm' AND status='active';",
                            new Dictionary<string, object> { ["campaign"] = campaignId }).Any(),
                        "Three base combat Reputations derive Battlemaster, while three governance pillars derive Steward of the Realm.", null);

                    Dictionary<string, object> manualRumor = CharacterEditorSocialRumorAddApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId, ["timelineId"] = "main", ["heroStringId"] = "player_subject",
                        ["archetypeId"] = "weak_captain", ["role"] = "commander",
                        ["incidentDescription"] = "Witnesses blamed the commander for a poorly handled defeat.",
                        ["idempotencyKey"] = "manual_rumor_fixture"
                    });
                    Dictionary<string, object> manualRumorReplay = CharacterEditorSocialRumorAddApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId, ["timelineId"] = "main", ["heroStringId"] = "player_subject",
                        ["archetypeId"] = "weak_captain", ["role"] = "commander",
                        ["incidentDescription"] = "Witnesses blamed the commander for a poorly handled defeat.",
                        ["idempotencyKey"] = "manual_rumor_fixture"
                    });
                    Dictionary<string, object> manualReputation = CharacterEditorSocialReputationAddApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId, ["timelineId"] = "main", ["heroStringId"] = "player_subject",
                        ["tagId"] = "fallen_challenger",
                        ["incidentDescription"] = "The challenger was decisively defeated before assembled witnesses.",
                        ["idempotencyKey"] = "manual_reputation_fixture"
                    });
                    Dictionary<string, object> socialProjection = CharacterSocialStandingProjection(connection, campaignId, "player_subject");
                    List<Dictionary<string, object>> projectedRumors = ReadDictionaryList(socialProjection, "activeRumors");
                    List<Dictionary<string, object>> projectedReputations = ReadDictionaryList(socialProjection, "activeReputations");
                    add("character_profile_social_projection",
                        ReadBool(manualRumor, "ok", false) && ReadBool(manualRumorReplay, "duplicate", false)
                        && ReadBool(manualReputation, "ok", false)
                        && projectedRumors.Any(row => ReadString(row, "label", "") == "Weak Captain"
                            && ReadString(row, "incidentDescription", "").Contains("poorly handled defeat"))
                        && projectedReputations.Any(row => ReadString(row, "label", "") == "The Fallen Challenger"
                            && ReadString(row, "reason_status", "") == "recorded"),
                        "Character profiles list every active social occurrence, manual Rumors are idempotent, and direct Reputations retain their factual establishing event.",
                        new Dictionary<string, object> { ["rumors"] = projectedRumors.Count, ["reputations"] = projectedReputations.Count });
                    add("reputation_incident_is_authoritative",
                        !QuerySql(connection, @"SELECT 1 FROM reputation_reason_jobs j
JOIN reputation_activations a ON a.activation_id=j.activation_id
WHERE a.subject_id='player_subject' AND a.tag_id='fallen_challenger';").Any()
                        && QuerySql(connection, @"SELECT 1 FROM character_reputations
WHERE subject_id='player_subject' AND tag_id='fallen_challenger'
AND reason_text='The challenger was decisively defeated before assembled witnesses.'
AND reason_status='recorded';").Any(),
                        "A newly established Reputation stores its triggering event verbatim and creates no background LLM reason job.", null);
                    Dictionary<string, object> choices = ReadDictionary(socialProjection, "catalogChoices") ?? new Dictionary<string, object>();
                    add("manual_catalog_excludes_derived",
                        !ReadDictionaryList(choices, "reputations").Any(row =>
                            new[] { "Battlemaster", "The Frail", "Steward of the Realm", "The Ruinous Crown" }
                                .Contains(ReadString(row, "label", ""), StringComparer.OrdinalIgnoreCase)),
                        "The profile add dialog offers enabled base Reputations but never derived Reputations.", null);

                    ExecuteSql(connection, "BEGIN IMMEDIATE;");
                    ExecuteSql(connection, @"INSERT INTO world_history_events(
event_id,campaign_id,timeline_id,sequence,world_day,event_type,summary,payload_json,created_utc)
VALUES('social_batch_poison',$campaign,'main',9000,40,'social_outcome','Malformed social outcome','{}',$created);",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId,
                            ["created"] = DateTime.UtcNow.ToString("o")
                        });
                    for (int index = 0; index < 205; index++)
                    {
                        string eventId = "social_batch_" + index.ToString(CultureInfo.InvariantCulture);
                        ExecuteSql(connection, @"INSERT INTO world_history_events(
event_id,campaign_id,timeline_id,sequence,world_day,event_type,summary,payload_json,created_utc)
VALUES($event,$campaign,'main',$sequence,40,'social_outcome','Counter-only batch fixture',$payload,$created);",
                            new Dictionary<string, object>
                            {
                                ["event"] = eventId, ["campaign"] = campaignId,
                                ["sequence"] = 9001 + index,
                                ["payload"] = "{\"subjectId\":\"player_subject\",\"archetypeId\":\"\",\"counterOnlyTagIds\":[]}",
                                ["created"] = DateTime.UtcNow.ToString("o")
                            });
                    }
                    ExecuteSql(connection, "COMMIT;");
                    ProcessPendingSocialWorldHistoryOutcomeBatch(campaignId, "main", campaignId + "|main|batch_1");
                    ProcessPendingSocialWorldHistoryOutcomeBatch(campaignId, "main", campaignId + "|main|batch_2");
                    ProcessPendingSocialWorldHistoryOutcomeBatch(campaignId, "main", campaignId + "|main|batch_3");
                    int completedBatchOutcomes = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM social_outcome_receipts WHERE event_id LIKE 'social_batch_%' AND completed=1;").FirstOrDefault(), "count", 0);
                    Dictionary<string, object> poisonReceipt = QuerySql(connection, @"SELECT completed,attempt_count,last_error
FROM social_outcome_receipts WHERE event_id='social_batch_poison' LIMIT 1;").FirstOrDefault();
                    add("social_outcome_batched_drain_and_poison_isolation",
                        completedBatchOutcomes == 205
                        && ReadInt(poisonReceipt, "completed", 0) == -1
                        && ReadInt(poisonReceipt, "attempt_count", 0) == 3
                        && !HasPendingSocialWorldHistoryOutcomes(campaignId, "main"),
                        "Social outcomes drain in bounded batches, a malformed event cannot block later hooks, and exhausted failures terminate visibly after three attempts.",
                        new Dictionary<string, object>
                        {
                            ["completed"] = completedBatchOutcomes,
                            ["poisonAttempts"] = ReadInt(poisonReceipt, "attempt_count", 0)
                        });

                    ExecuteSql(connection, "BEGIN IMMEDIATE;");
                    for (int index = 0; index < 1000; index++)
                    {
                        string bulkId = "bulk_" + index.ToString(CultureInfo.InvariantCulture);
                        ExecuteSql(connection, @"INSERT INTO rumor_occurrences(occurrence_id,campaign_id,timeline_id,archetype_id,thread_key,
world_day,expires_day,status,catalog_revision,participants_json,created_ts,updated_ts)
VALUES($id,$campaign,'main','bulk_fixture',$id,25,55,'active',1,'[]',$ts,$ts);",
                            new Dictionary<string, object> { ["id"] = bulkId, ["campaign"] = campaignId, ["ts"] = ts });
                        ExecuteSql(connection, @"INSERT INTO rumor_subject_tags(occurrence_id,subject_id,tag_id,description,rumor_value,
reputation_value,status,updated_ts) VALUES($id,'bulk_subject',$tag,'Bulk performance fixture',-1,-1,'active',$ts);",
                            new Dictionary<string, object> { ["id"] = bulkId, ["tag"] = "bulk_tag_" + index.ToString(CultureInfo.InvariantCulture), ["ts"] = ts });
                    }
                    ExecuteSql(connection, "COMMIT;");
                    Dictionary<string, object> bulkObserver = new Dictionary<string, object>
                        { ["hero_id"]="bulk_observer",["kingdom_id"]="kingdom_a" };
                    Dictionary<string, object> bulkSubject = new Dictionary<string, object>
                        { ["hero_id"]="bulk_subject",["kingdom_id"]="kingdom_a",["current_charm"]=0 };
                    System.Diagnostics.Stopwatch bulkTimer = System.Diagnostics.Stopwatch.StartNew();
                    int bulkModifier = CalculateObserverSocialModifier(connection, campaignId, "main",
                        bulkObserver, bulkSubject, 25d, out int bulkRumorTotal, out int bulkReputationTotal);
                    bulkTimer.Stop();
                    add("thousand_rumor_indexed_projection",
                        bulkRumorTotal == -1000 && bulkReputationTotal == 0 && bulkModifier == -1000
                        && bulkTimer.ElapsedMilliseconds < 5000,
                        "One indexed active-Rumor query evaluates 1,000 distinct character-owned entries without LLM work; the social sum remains unclamped until effective affinity.",
                        new Dictionary<string, object> { ["rumorCount"]=1000,["rumorTotal"]=bulkRumorTotal,["modifier"]=bulkModifier,["elapsedMs"]=bulkTimer.ElapsedMilliseconds });

                    HashSet<string> legacy = new HashSet<string>(QuerySql(connection,
                        "SELECT name FROM sqlite_master WHERE type='table';").Select(x => ReadString(x, "name", "")), StringComparer.OrdinalIgnoreCase);
                    add("legacy_propagation_storage_absent",
                        new[] { "rumor_chains", "rumor_receipts", "rumor_delivery_runs", "rumor_source_rolls", "reputation_profiles", "reputation_axes" }.All(x => !legacy.Contains(x)),
                        "Fresh schema creates no propagation, receipt, acquisition-run, or five-axis reputation tables.", null);
                    RunWhoremongerSelfTests(connection, campaignId, add);
                }
            }
            finally
            {
                try { if (System.IO.Directory.Exists(campaignPath)) System.IO.Directory.Delete(campaignPath, true); } catch { }
            }
            return results;
        }
    }
}
