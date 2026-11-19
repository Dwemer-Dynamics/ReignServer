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
    internal static partial class ReputationService
    {
        private const int SocialReputationSchemaVersion = 4;
        private const int BuiltInSocialCatalogRevision = 6;
        private static string SocialCatalogPath => Path.Combine(Program.DataDir, "social_rumor_reputation_catalog.json");
        // Schema initialization reads this catalog while holding CampaignSchemaLock.
        // Keep its file access off FileLock: action delivery takes FileLock before
        // opening a campaign connection, and the opposite order deadlocks.
        private static readonly object SocialCatalogLock = new object();
        private static string SocialCatalogCachedJson = "";
        private static string SocialCatalogCachedPath = "";
        private static long SocialCatalogCachedWriteTicks = long.MinValue;

        internal static void EnsureSocialReputationSchema(ReignDbConnection connection)
        {
            CharactersService.EnsureIdentitySchema(connection);
            RelationshipsService.EnsureMbtiRelationshipSchema(connection);
            MigrateLegacySocialStorage(connection);
            EnsureExpandedSocialReputationSchema(connection);
            EnsurePublicStandingSchema(connection);
            ReputationSchema.CreateSocialCatalogRevisions(connection);
            ReputationSchema.CreateRumorOccurrences(connection);
            ReputationSchema.CreateIndexRumorOccurrenceActive(connection);
            ReputationSchema.CreateRumorSubjectTags(connection);
            ReputationSchema.CreateIndexRumorSubjectActive(connection);
            ReputationSchema.CreateRumorExposureStreaks(connection);
            ReputationSchema.CreateSharedTagExposureStreaks(connection);
            ReputationSchema.CreateRumorPromotionResults(connection);
            ReputationSchema.CreateCharacterReputations(connection);
            ReputationSchema.CreateIndexCharacterReputationsSubject(connection);
            // Court migrations update these base tables. A fresh installation
            // must create them before applying migrations from older campaigns.
            EnsureCourtSocialReputationSchema(connection);
            EnsureCharacterSocialProfileSchema(connection);
            EnsureWhoremongerSchema(connection);
            ReputationSchema.CreateDirectionalSocialProjections(connection);
            ReputationSchema.EnsureColumnRelationshipPairChemistrySocialModifierAToB(connection);
            ReputationSchema.EnsureColumnRelationshipPairChemistrySocialModifierBToA(connection);
            ReputationSchema.EnsureColumnRelationshipPairChemistryEffectiveAffinityAToB(connection);
            ReputationSchema.EnsureColumnRelationshipPairChemistryEffectiveAffinityBToA(connection);

            Dictionary<string, object> catalog = ReadGlobalSocialCatalog();
            int activeRevision = ReadInt(catalog, "revision", BuiltInSocialCatalogRevision);
            ReputationStore.UpsertSocialCatalogRevisions(connection, new Dictionary<string, object>
            {
                ["revision"] = activeRevision, ["catalog"] = Json.Serialize(catalog),
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            ReputationStore.UpsertSchemaMeta6(connection, new Dictionary<string, object> { ["version"] = SocialReputationSchemaVersion.ToString(CultureInfo.InvariantCulture) });
        }

        private static void MigrateLegacySocialStorage(ReignDbConnection connection)
        {
            if (ReadString(ReputationStore.SelectSchemaMeta6(connection).FirstOrDefault(), "value", "") == "1")
                return;
            ReputationStore.Begin(connection);
            try
            {
                foreach (string table in new[]
                {
                    "rumor_receipts", "rumor_delivery_runs", "rumor_source_rolls", "rumors", "rumor_chains",
                    "reputation_axes", "reputation_profiles", "court_knowledge", "relationship_affair_discoveries"
                })
                    ReputationSchema.DropDynamic(connection, table);
                if (ReputationStore.SelectSqliteMaster5(connection).Any())
                {
                    if (ReputationStore.SelectSqliteMaster6(connection).Any())
                        ReputationStore.DeleteSummaries(connection);
                    if (ReputationStore.SelectSqliteMaster7(connection).Any())
                        ReputationStore.DeleteConversationTurns(connection);
                    ReputationStore.DeleteConversationSessions(connection);
                }
                if (ReputationStore.SelectSqliteMaster8(connection).Any())
                    ReputationStore.DeleteEmbeddingJobs(connection);
                ReputationStore.UpsertSchemaMeta7(connection);
                ReputationStore.Commit(connection);
            }
            catch
            {
                try { ReputationStore.Rollback(connection); } catch { }
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

        internal static Dictionary<string, object> ReadActiveSocialCatalog(ReignDbConnection connection)
        {
            Dictionary<string, object> catalog = ReadGlobalSocialCatalog();
            int revision = ReadInt(catalog, "revision", BuiltInSocialCatalogRevision);
            ReputationStore.InsertOrIgnoreSocialCatalogRevisions(connection, new Dictionary<string, object>
            {
                ["revision"] = revision, ["catalog"] = Json.Serialize(catalog),
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            return catalog;
        }

        private static Dictionary<string, object> ReadGlobalSocialCatalog()
        {
            lock (SocialCatalogLock)
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
                        WriteSocialCatalog(catalogPath, cached);
                        SocialCatalogCachedJson = Json.Serialize(cached);
                        SocialCatalogCachedWriteTicks = File.Exists(catalogPath)
                            ? File.GetLastWriteTimeUtc(catalogPath).Ticks
                            : 0L;
                    }
                    return cached;
                }
                Dictionary<string, object> catalog = ReadSocialCatalog(catalogPath);
                if (catalog.Count == 0) catalog = DefaultSocialCatalog();
                bool changed = EnsureExpandedSocialCatalog(catalog);
                if (changed || !File.Exists(catalogPath))
                    WriteSocialCatalog(catalogPath, catalog);
                SocialCatalogCachedJson = Json.Serialize(catalog);
                SocialCatalogCachedPath = catalogPath;
                SocialCatalogCachedWriteTicks = File.Exists(catalogPath)
                    ? File.GetLastWriteTimeUtc(catalogPath).Ticks
                    : 0L;
                return catalog;
            }
        }

        private static Dictionary<string, object> ReadSocialCatalog(string path)
        {
            try
            {
                if (!File.Exists(path)) return new Dictionary<string, object>();
                string contents = File.ReadAllText(path, Encoding.UTF8);
                return string.IsNullOrWhiteSpace(contents)
                    ? new Dictionary<string, object>()
                    : Json.Deserialize<Dictionary<string, object>>(contents)
                        ?? new Dictionary<string, object>();
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }

        private static void WriteSocialCatalog(string path, Dictionary<string, object> catalog)
        {
            PersistenceService.ThrowIfCampaignRequestReplaced();
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "~" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                File.WriteAllText(temporary, Json.Serialize(catalog), new UTF8Encoding(false));
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        internal static Dictionary<string, object> SocialCatalogApi(Dictionary<string, object> payload)
        {
            string campaignId = ReadString(payload, "campaignId", "default");
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
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

        internal static Dictionary<string, object> SocialCatalogUpdateApi(Dictionary<string, object> payload)
        {
            string campaignId = ReadString(payload, "campaignId", "default");
            Dictionary<string, object> proposed = CharactersService.DictionaryOrDefault(payload, "catalog", payload);
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                Dictionary<string, object> current = ReadGlobalSocialCatalog();
                List<string> errors = ValidateSocialCatalog(connection, proposed, current);
                if (errors.Count > 0)
                    return new Dictionary<string, object> { ["ok"] = false, ["errors"] = errors };
                int revision = ReadInt(current, "revision", 1) + 1;
                proposed["revision"] = revision;
                lock (SocialCatalogLock)
                {
                    string catalogPath = Path.GetFullPath(SocialCatalogPath);
                    WriteSocialCatalog(catalogPath, proposed);
                    SocialCatalogCachedJson = Json.Serialize(proposed);
                    SocialCatalogCachedPath = catalogPath;
                    SocialCatalogCachedWriteTicks =
                        File.GetLastWriteTimeUtc(catalogPath).Ticks;
                }
                ReputationStore.InsertSocialCatalogRevisions(connection, new Dictionary<string, object>
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
                foreach (Dictionary<string, object> variant in CharactersService.DictionaryOrDefault(tag, "genderVariants", new Dictionary<string, object>()).Values.OfType<Dictionary<string, object>>())
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
                bool used = ReputationStore.SelectRumorSubjectTags11(connection, new Dictionary<string, object> { ["id"] = customId }).Any();
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
                foreach (string tagId in CharactersService.DictionaryOrDefault(archetype, "roleMappings", new Dictionary<string, object>())
                    .Values.SelectMany(ValueTextList))
                    if (!proposedIds.Contains(tagId)) errors.Add(archetypeId + ": mapped social entry '" + tagId + "' does not exist.");
            }
            foreach (Dictionary<string, object> builtIn in ReadDictionaryList(current, "archetypes").Where(x => ReadBool(x, "builtIn", false)))
                if (!archetypeIds.Contains(ReadString(builtIn, "id", "")))
                    errors.Add("Built-in producer '" + ReadString(builtIn, "id", "") + "' cannot be deleted.");

            Dictionary<string, object> governance = CharactersService.DictionaryOrDefault(proposed, "governanceSettings", new Dictionary<string, object>());
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

            Dictionary<string, object> war = CharactersService.DictionaryOrDefault(proposed, "warScoreSettings", new Dictionary<string, object>());
            foreach (string key in new[] { "townWeight", "castleWeight", "casualtyCap", "siegeWeight", "raidWeight", "raidCap", "rulerCaptureWeight", "tributeWeight" })
                if (ReadDouble(war, key, 0d) < 0d) errors.Add("War score setting '" + key + "' cannot be negative.");
            if (ReadDouble(war, "victoryMargin", 15d) <= 0d) errors.Add("War victory margin must be greater than zero.");
            return errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static Dictionary<string, object> RegisterSocialOccurrenceApi(Dictionary<string, object> payload)
        {
            string campaignId = ReadString(payload, "campaignId", "default");
            Dictionary<string, object> result;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                result = RegisterSocialOccurrence(connection, campaignId, payload);
            }
            SchedulePendingReputationReasonJobs(campaignId);
            return result;
        }

        internal static Dictionary<string, object> BuildRelationshipSocialOccurrence(RelationshipsService.AmbientPairContext pair, int day,
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

        internal static Dictionary<string, object> RegisterSocialOccurrence(
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
                        WorldSimulationService.RecordWorldTestCounterSchemaReady(connection,
                            campaignId, timelineId, worldDayKey, "rumors",
                            chunkKey, counters);
                    else
                        WorldSimulationService.RecordWorldTestCounter(connection, campaignId,
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
                WorldSimulationService.AddSocialRumorTelemetryDimensions(disabledCounters, archetypeId,
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
                WorldSimulationService.AddSocialRumorTelemetryDimensions(emptyCounters, archetypeId, participants, payload);
                recordWorldTest(hookKey + "_no_participants",
                    emptyCounters);
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "At least one participant is required." };
            }
            foreach (Dictionary<string, object> participant in participants)
            {
                string subjectId = ReadFirstString(participant, "subjectId", "heroId", "id");
                Dictionary<string, object> roster = ReputationStore.SelectIdentityRoster(connection, new Dictionary<string, object> { ["id"] = subjectId }).FirstOrDefault();
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
                WorldSimulationService.AddSocialRumorTelemetryDimensions(excludedCounters, archetypeId, participants, payload);
                recordWorldTest(hookKey + "_excluded", excludedCounters);
                return new Dictionary<string, object> { ["ok"] = true, ["skipped"] = true, ["reason"] = "no_eligible_npc_participants" };
            }
            int highestTier = Math.Max(1, Math.Min(6, participants.Max(x => Math.Max(1, ReadInt(x, "clanTier", 0)))));
            double multiplier = highestTier * 0.5d;
            double chance = payload.ContainsKey("exposureChanceOverride")
                ? DialogueService.ClampDouble(ReadDouble(payload, "exposureChanceOverride", 0d), 0d, 1d)
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
                WorldSimulationService.AddSocialRumorTelemetryDimensions(failedCounters, archetypeId, participants, payload);
                recordWorldTest("exposure_"
                    + DeterministicSocialId(exposureSeed) + "_failed",
                    failedCounters);
                return new Dictionary<string, object> { ["ok"] = true, ["exposed"] = false, ["chance"] = chance, ["roll"] = exposureRoll };
            }

            string occurrenceId = FirstNonEmpty(ReadString(payload, "occurrenceId", ""),
                "rumor_occurrence_" + DeterministicSocialId(exposureSeed));
            Dictionary<string, object> duplicate = ReputationStore.QuerySql(connection,
                string.IsNullOrWhiteSpace(sourceEventId)
                    ? ReputationStore.SelectRumorOccurrencesSql
                    : ReputationStore.SelectRumorOccurrencesSql2,
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
                WorldSimulationService.AddSocialRumorTelemetryDimensions(duplicateCounters, archetypeId, participants, payload);
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
            Dictionary<string, object> streak = ReputationStore.SelectRumorExposureStreaks(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["archetype"] = archetypeId, ["thread"] = threadKey }).FirstOrDefault();
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
                ReputationStore.Begin(connection);
            try
            {
                ReputationStore.InsertRumorOccurrences2(connection, new Dictionary<string, object>
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
                            ReputationStore.InsertOrIgnoreCourtRulerFavorContact2(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["ruler"] = subjectId,
                                    ["favorite"] = ReadString(snap, "linkedHeroId", ""), ["day"] = worldDay });
                        ReputationStore.InsertRumorSubjectTags2(connection, new Dictionary<string, object>
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
                            Dictionary<string, object> sharedStreak = ReputationStore.SelectSharedTagExposureStreaks2(connection, new Dictionary<string, object>
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
                            ReputationStore.UpsertSharedTagExposureStreaks(connection, new Dictionary<string, object>
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
                ReputationStore.UpsertRumorExposureStreaks(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["archetype"] = archetypeId, ["thread"] = threadKey, ["count"] = streakCount, ["day"] = worldDay, ["occurrence"] = occurrenceId, ["ts"] = ts });
                ReputationStore.InsertRumorPromotionResults(connection, new Dictionary<string, object> { ["occurrence"] = occurrenceId, ["count"] = streakCount, ["chance"] = promotionChance, ["roll"] = promotionRoll, ["promoted"] = promoted || anySharedPromotionRoll ? 1 : 0, ["tags"] = Json.Serialize(promotedTags), ["ts"] = ts });
                Dictionary<string, object> occurrenceCounters = new Dictionary<string, object>
                {
                    ["eligibleHooks"] = 1,
                    ["ineligibleParticipants"] = ineligibleParticipants,
                    ["exposureAttempts"] = 1,
                    ["exposurePassed"] = 1,
                    ["occurrencesCreated"] = 1,
                    ["subjectTagsCreated"] = ReadInt(ReputationStore.CountRumorSubjectTags2(connection, new Dictionary<string, object> { ["id"] = occurrenceId }).FirstOrDefault(), "count", 0),
                    ["streakAdvanced"] = 1,
                    ["promotionAttempts"] = 1,
                    ["promotionPassed"] = promoted || anySharedPromotionRoll ? 1 : 0,
                    ["promotionFailed"] = promoted || anySharedPromotionRoll ? 0 : 1,
                    ["durableReputationsCreated"] = promotedTags.Count
                };
                WorldSimulationService.AddSocialRumorTelemetryDimensions(occurrenceCounters, archetypeId, participants, payload);
                WorldSimulationService.IncrementWorldTestObjectCounter(occurrenceCounters,
                    "occurrenceLifetime:" + WorldSimulationService.SocialRumorDurationBucket(expiresDay - worldDay), 1);
                if (streak != null)
                    WorldSimulationService.IncrementWorldTestObjectCounter(occurrenceCounters,
                        "exposureToPromotion:" + WorldSimulationService.SocialRumorDurationBucket(
                            worldDay - ReadDouble(streak, "last_exposure_day", worldDay)), 1);
                if (payload.ContainsKey("sourceEventDay"))
                    WorldSimulationService.IncrementWorldTestObjectCounter(occurrenceCounters,
                        "eventToExposure:" + WorldSimulationService.SocialRumorDurationBucket(
                            worldDay - ReadDouble(payload, "sourceEventDay", worldDay)), 1);
                bool immediateExpiration = expiresDay <= worldDay + 0.000001d;
                if (immediateExpiration)
                {
                    ReputationStore.UpdateRumorOccurrences3(connection, new Dictionary<string, object>
                        {
                            ["id"] = occurrenceId, ["ts"] = ts
                        });
                    occurrenceCounters["occurrencesExpired"] = 1;
                    occurrenceCounters["immediateFinalizations"] = 1;
                }
                recordWorldTest("occurrence_" + occurrenceId,
                    occurrenceCounters);
                if (!useExistingTransaction)
                    ReputationStore.Commit(connection);
            }
            catch
            {
                if (!useExistingTransaction)
                {
                    try { ReputationStore.Rollback(connection); } catch { }
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
            bool isNew = ReputationStore.SelectCharacterReputations36(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["tag"] = tagId }).Count == 0;
            ReputationStore.UpsertCharacterReputations5(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["tag"] = tagId,
                    ["occurrence"] = occurrenceId, ["archetype"] = archetypeId, ["role"] = role,
                    ["description"] = ReadString(snapshot, "description", ""), ["value"] = ReadInt(snapshot, "reputationValue", 0),
                    ["day"] = worldDay, ["revision"] = catalogRevision, ["snapshot"] = Json.Serialize(snapshot), ["ts"] = ts
                });
            ReputationStore.UpdateRumorSubjectTags4(connection, new Dictionary<string, object> { ["subject"] = subjectId, ["tag"] = tagId, ["ts"] = ts,
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
            Dictionary<string, object> mappings = CharactersService.DictionaryOrDefault(archetype, "roleMappings", new Dictionary<string, object>());
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
            Dictionary<string, object> variants = CharactersService.DictionaryOrDefault(tag, "genderVariants", new Dictionary<string, object>());
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

        internal static Dictionary<string, object> CorrectSocialOccurrenceApi(Dictionary<string, object> payload)
        {
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string occurrenceId = ReadString(payload, "occurrenceId", "");
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                Dictionary<string, object> occurrence = ReputationStore.SelectRumorOccurrences13(connection, new Dictionary<string, object> { ["id"] = occurrenceId, ["campaign"] = campaignId, ["timeline"] = timelineId }).FirstOrDefault();
                if (occurrence == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Occurrence not found." };
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ReputationStore.Begin(connection);
                try
                {
                    foreach (Dictionary<string, object> affectedTag in ReputationStore.SelectRumorSubjectTags12(connection, new Dictionary<string, object> { ["id"] = occurrenceId }))
                    {
                        ReputationStore.DeleteSharedTagExposureStreaks(connection, new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId, ["timeline"] = timelineId,
                                ["subject"] = ReadString(affectedTag, "subject_id", ""),
                                ["tag"] = ReadString(affectedTag, "tag_id", "")
                            });
                    }
                    ReputationStore.UpdateRumorOccurrences4(connection, new Dictionary<string, object> { ["id"] = occurrenceId, ["ts"] = ts });
                    ReputationStore.UpdateRumorSubjectTags5(connection, new Dictionary<string, object> { ["id"] = occurrenceId, ["ts"] = ts });
                    ReputationStore.DeleteRumorExposureStreaks2(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["archetype"] = ReadString(occurrence, "archetype_id", ""), ["thread"] = ReadString(occurrence, "thread_key", ""), ["id"] = occurrenceId
                        });
                    int occurrenceDay = (int)Math.Floor(ReadDouble(occurrence, "world_day", 0d) + 0.000001d);
                    WorldSimulationService.RecordWorldTestCounter(connection, campaignId, timelineId, occurrenceDay, "rumors",
                        "correction_" + occurrenceId,
                        new Dictionary<string, object> { ["corrected"] = 1, ["disproven"] = 1 });
                ReputationStore.Commit(connection);
                }
                catch
                {
                    try { ReputationStore.Rollback(connection); } catch { }
                    throw;
                }
                List<string> affected = ReputationStore.SelectRumorSubjectTags13(connection, new Dictionary<string, object> { ["id"] = occurrenceId })
                    .Select(x => ReadString(x, "subject_id", "")).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                ReconcileSocialRelationshipsForSubjects(connection, campaignId, timelineId, affected,
                    ReadDouble(occurrence, "world_day", 0d));
                return new Dictionary<string, object> { ["ok"] = true, ["occurrenceId"] = occurrenceId, ["reputationsRetained"] = true };
            }
        }

        internal static Dictionary<string, object> SocialCharacterStatusApi(Dictionary<string, object> payload)
        {
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string subjectId = ReadFirstString(payload, "subjectId", "heroId", "npcId");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                if (ExpireSocialRumors(connection, campaignId, timelineId, worldDay) > 0)
                    ReconcileSocialRelationshipsForSubjects(connection, campaignId, timelineId, new[] { subjectId }, worldDay);
                List<Dictionary<string, object>> reputations = ReputationStore.SelectReputationActivations4(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["subject"] = subjectId
                });
                foreach (Dictionary<string, object> reputation in reputations)
                {
                    Dictionary<string, object> activationEvidence = TryParseJsonObject(
                        ReadString(reputation, "activation_evidence_json", "{}"))
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> dynamicEvidence = CharactersService.DictionaryOrDefault(
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
                    ["rumors"] = ReputationStore.SelectRumorSubjectTags14(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["day"] = worldDay }),
                    ["reputations"] = reputations
                };
            }
        }

        internal static Dictionary<string, object> BuildKnownSocialStandingView(ReignDbConnection connection,
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
            List<Dictionary<string, object>> rumors = ReputationStore.SelectRumorSubjectTags15(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["day"] = worldDay });
            List<Dictionary<string, object>> reputations = ReputationStore.SelectCharacterReputations37(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId });
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
            Dictionary<string, object> observerStanding = RelationshipsService.ResolveObserverPublicStanding(connection,
                campaignId, timelineId, observerId, subjectId);
            return new Dictionary<string, object>
            {
                ["known"] = true, ["observerId"] = observerId, ["subjectId"] = subjectId,
                ["publicStanding"] = ReadPublicStandingTrait(connection,
                    campaignId, timelineId, subjectId, worldDay),
                ["observerStanding"] = observerStanding,
                ["rumors"] = rumors, ["reputations"] = reputations,
                ["familyAttentionContext"] = CourtService.BuildFamilyAttentionContext(connection, campaignId, timelineId, observerId, subjectId, worldDay),
                ["sharedFavorContext"] = SharedFavorJealousyContext(connection, campaignId, timelineId, observerId, subjectId, worldDay)
            };
        }

        internal static string BuildKnownSocialStandingPromptBlock(Dictionary<string, object> standing)
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

        internal static List<Dictionary<string, object>> LoadKnownSocialContextCandidates(ReignDbConnection connection,
            string campaignId, string timelineId, string observerId, double worldDay, string topic)
        {
            EnsureSocialReputationSchema(connection);
            ExpireSocialRumors(connection, campaignId, timelineId, worldDay);
            List<Dictionary<string, object>> candidates = new List<Dictionary<string, object>>();
            List<string> topicTokens = ActionPipelineService.NormalizeLookup(topic).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<Dictionary<string, object>> rows = ReputationStore.SelectRumorSubjectTags16(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = worldDay });
            foreach (Dictionary<string, object> row in rows)
            {
                string subjectId = ReadString(row, "subject_id", "");
                if (string.IsNullOrWhiteSpace(subjectId) || subjectId.Equals(observerId, StringComparison.OrdinalIgnoreCase)
                    || !CharactersService.IdentityStateVerified(CharactersService.ReadAcquaintance(connection, observerId, subjectId))) continue;
                Dictionary<string, object> roster = ReputationStore.SelectIdentityRoster13(connection, new Dictionary<string, object> { ["id"] = subjectId }).FirstOrDefault();
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

        internal static int ExpireSocialRumors(ReignDbConnection connection, string campaignId, string timelineId, double worldDay)
        {
            int forgottenCount = ExpireRulerFavorContact(connection, campaignId, timelineId, worldDay).Count
                + ExpireWhoremongerActivity(connection, campaignId, timelineId, worldDay);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<string> affectedSubjects = ReputationStore.SelectRumorSubjectTags17(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = worldDay })
                .Select(row => ReadString(row, "subject_id", ""))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();
            List<Dictionary<string, object>> expiring = ReputationStore.SelectRumorOccurrences14(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = worldDay });
            int count = expiring.Count;
            ReputationStore.UpdateRumorOccurrences5(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = worldDay, ["ts"] = ts });
            ReputationStore.UpdateRumorSubjectTags6(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["ts"] = ts });
            int expirationDay = (int)Math.Floor(worldDay + 0.000001d);
            foreach (Dictionary<string, object> occurrence in expiring)
            {
                string occurrenceId = ReadString(occurrence, "occurrence_id", "");
                Dictionary<string, object> counters = new Dictionary<string, object> { ["expired"] = 1 };
                WorldSimulationService.IncrementWorldTestObjectCounter(counters,
                    "occurrenceLifetime:" + WorldSimulationService.SocialRumorDurationBucket(
                        worldDay - ReadDouble(occurrence, "world_day", worldDay)), 1);
                WorldSimulationService.RecordWorldTestCounter(connection, campaignId, timelineId, expirationDay, "rumors",
                    "expiration_" + occurrenceId, counters);
            }
            if (affectedSubjects.Count > 0)
                RecomputePublicStandingForSubjects(connection, campaignId, timelineId,
                    affectedSubjects, worldDay);
            return count + forgottenCount;
        }

        internal static void ReconcileSocialRelationshipsForSubjects(ReignDbConnection connection, string campaignId,
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

        internal static void ReconcileSocialRelationshipForObserverSubject(
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
            foreach (Dictionary<string, object> rumor in ReputationStore.SelectRumorSubjectTags18(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["day"] = worldDay }))
            {
                bool affairCoParticipant = string.Equals(ReadString(rumor, "archetype_id", ""), "affair", StringComparison.OrdinalIgnoreCase)
                    && DialogueService.TextListFromJson(ReadString(rumor, "co_participants_json", "[]")).Contains(observerId, StringComparer.OrdinalIgnoreCase);
                if (!affairCoParticipant) rumorValues.Add(ReadInt(rumor, "rumor_value", 0));
            }
            List<int> reputationValues = ReputationStore.SelectCharacterReputations38(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId })
                .Select(x => ReadInt(x, "reputation_value", 0)).ToList();
            rumorTotal = rumorValues.Sum();
            reputationTotal = reputationValues.Sum();
            string observerKingdom = ReadString(observer, "kingdom_id", "");
            string subjectKingdom = ReadString(subject, "kingdom_id", "");
            bool sameKingdom = !string.IsNullOrWhiteSpace(observerKingdom)
                && observerKingdom.Equals(subjectKingdom, StringComparison.OrdinalIgnoreCase);
            return CalculateSocialModifier(rumorValues.Concat(reputationValues), ReadInt(subject, "current_charm", 0), sameKingdom);
        }

        internal static double SocialCharmMitigation(int charm)
        {
            return Math.Max(0d, Math.Min(0.60d, Math.Max(0, charm) / 300d * 0.60d));
        }

        internal static int EffectiveDirectionalAffinity(Dictionary<string, object> row, bool aToB, int fallback = 0)
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
            return RelationshipsService.RoundAwayFromZero(scaled);
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

        internal static double DeterministicSocialRoll(string seed)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(seed ?? ""));
                ulong value = BitConverter.ToUInt64(hash, 0);
                return value / ((double)ulong.MaxValue + 1d);
            }
        }

        internal static string DeterministicSocialId(string seed)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(seed ?? ""))).Replace("-", "").ToLowerInvariant().Substring(0, 24);
        }

        // Compatibility entry point for the world-test harness. It creates only
        // the character-owned social schema; no legacy propagation tables exist.
        internal static void EnsureRumorSchema(ReignDbConnection connection)
        {
            EnsureSocialReputationSchema(connection);
        }
    }
}
