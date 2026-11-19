using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class ReputationService
    {
        private static void EnsureExpandedSocialReputationSchema(ReignDbConnection connection)
        {
            Dictionary<string, object> occurrenceTable = ReignDatabase.QuerySql(connection,
                "SELECT sql FROM sqlite_master WHERE type='table' AND name='rumor_occurrences' LIMIT 1;").FirstOrDefault();
            string occurrenceSql = ReadString(occurrenceTable, "sql", "");
            if (!string.IsNullOrWhiteSpace(occurrenceSql)
                && occurrenceSql.IndexOf("UNIQUE(campaign_id,timeline_id,archetype_id,thread_key,world_day)", StringComparison.OrdinalIgnoreCase) >= 0
                && ReadString(ReignDatabase.QuerySql(connection,
                    "SELECT value FROM schema_meta WHERE key='expanded_social_occurrence_keys_v2' LIMIT 1;").FirstOrDefault(), "value", "") != "1")
            {
                ReignDatabase.ExecuteSql(connection, "BEGIN IMMEDIATE;");
                try
                {
                    ReignDatabase.ExecuteSql(connection, @"CREATE TABLE rumor_occurrences_v2 (
occurrence_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
archetype_id TEXT NOT NULL,thread_key TEXT NOT NULL,source_event_id TEXT NOT NULL DEFAULT '',
world_day REAL NOT NULL,expires_day REAL NOT NULL,status TEXT NOT NULL DEFAULT 'active',
exposure_chance REAL NOT NULL DEFAULT 0,exposure_roll REAL NOT NULL DEFAULT 1,
catalog_revision INTEGER NOT NULL,participants_json TEXT NOT NULL DEFAULT '[]',
provenance_summary TEXT NOT NULL DEFAULT '',snapshot_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);");
                    ReignDatabase.ExecuteSql(connection, @"INSERT INTO rumor_occurrences_v2
SELECT occurrence_id,campaign_id,timeline_id,archetype_id,thread_key,source_event_id,
world_day,expires_day,status,exposure_chance,exposure_roll,catalog_revision,participants_json,
provenance_summary,snapshot_json,created_ts,updated_ts FROM rumor_occurrences;");
                    ReignDatabase.ExecuteSql(connection, "DROP TABLE rumor_occurrences;");
                    ReignDatabase.ExecuteSql(connection, "ALTER TABLE rumor_occurrences_v2 RENAME TO rumor_occurrences;");
                    ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('expanded_social_occurrence_keys_v2','1');");
                    ReignDatabase.ExecuteSql(connection, "COMMIT;");
                }
                catch
                {
                    try { ReignDatabase.ExecuteSql(connection, "ROLLBACK;"); } catch { }
                    throw;
                }
            }

            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS social_outcome_receipts (
event_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
subject_id TEXT NOT NULL DEFAULT '',archetype_id TEXT NOT NULL DEFAULT '',
counter_applied INTEGER NOT NULL DEFAULT 0,completed INTEGER NOT NULL DEFAULT 0,
result_json TEXT NOT NULL DEFAULT '{}',attempt_count INTEGER NOT NULL DEFAULT 0,
last_error TEXT NOT NULL DEFAULT '',updated_ts INTEGER NOT NULL);");
            ReignDatabase.EnsureDatabaseColumn(connection, "social_outcome_receipts", "attempt_count", "INTEGER NOT NULL DEFAULT 0");
            ReignDatabase.EnsureDatabaseColumn(connection, "social_outcome_receipts", "last_error", "TEXT NOT NULL DEFAULT ''");
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS reputation_counterevidence (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,subject_id TEXT NOT NULL,tag_id TEXT NOT NULL,
consecutive_count INTEGER NOT NULL DEFAULT 0,last_event_id TEXT NOT NULL DEFAULT '',
last_outcome_day REAL NOT NULL DEFAULT 0,last_chance REAL NOT NULL DEFAULT 0,last_roll REAL NOT NULL DEFAULT 1,
updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,subject_id,tag_id));");
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS reputation_lifecycle_events (
lifecycle_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
subject_id TEXT NOT NULL,tag_id TEXT NOT NULL,event_type TEXT NOT NULL,
source_event_id TEXT NOT NULL DEFAULT '',world_day REAL NOT NULL DEFAULT 0,
details_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL);");
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_reputation_lifecycle_subject ON reputation_lifecycle_events(campaign_id,timeline_id,subject_id,tag_id,world_day);");

            if (ReignDatabase.QuerySql(connection, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='rumor_occurrences' LIMIT 1;").Any()
                && ReadString(ReignDatabase.QuerySql(connection,
                    "SELECT value FROM schema_meta WHERE key='builtin_rumor_duration_45_v2' LIMIT 1;").FirstOrDefault(), "value", "") != "1")
            {
                ReignDatabase.ExecuteSql(connection, @"UPDATE rumor_occurrences
SET expires_day=MAX(expires_day,world_day+45),updated_ts=$ts
WHERE status='active' AND archetype_id IN ('affair','marital_strife');",
                    new Dictionary<string, object> { ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('builtin_rumor_duration_45_v2','1');");
            }
        }

        private static bool EnsureExpandedSocialCatalog(Dictionary<string, object> catalog)
        {
            string catalogBefore = Json.Serialize(catalog ?? new Dictionary<string, object>());
            bool changed = false;
            List<object> tags = CatalogObjectList(catalog, "tags");
            List<object> archetypes = CatalogObjectList(catalog, "archetypes");
            Dictionary<string, object> coreCatalog = DefaultSocialCatalog();
            foreach (Dictionary<string, object> coreTag in ReadDictionaryList(coreCatalog, "tags"))
            {
                string coreId = ReadString(coreTag, "id", "");
                if (tags.OfType<Dictionary<string, object>>().Any(x =>
                    string.Equals(ReadString(x, "id", ""), coreId, StringComparison.OrdinalIgnoreCase))) continue;
                tags.Add(coreTag);
                changed = true;
            }
            foreach (Dictionary<string, object> coreArchetype in ReadDictionaryList(coreCatalog, "archetypes"))
            {
                string coreId = ReadString(coreArchetype, "id", "");
                if (archetypes.OfType<Dictionary<string, object>>().Any(x =>
                    string.Equals(ReadString(x, "id", ""), coreId, StringComparison.OrdinalIgnoreCase))) continue;
                archetypes.Add(coreArchetype);
                changed = true;
            }

            foreach (Dictionary<string, object> archetype in archetypes.OfType<Dictionary<string, object>>()
                .Where(x => ReadBool(x, "builtIn", false) && !ReadBool(x, "directReputation", false)
                    && ReadString(x, "id", "") != "whoremonger"))
            {
                if (ReadDouble(archetype, "durationDays", 30d) == 30d)
                {
                    archetype["durationDays"] = 45d;
                    changed = true;
                }
                if (!archetype.ContainsKey("promotionWindowDays")
                    || Math.Abs(ReadDouble(archetype, "promotionWindowDays", 30d) - 30d) < 0.000001d)
                {
                    archetype["promotionWindowDays"] = 45d;
                    changed = true;
                }
            }

            Action<string, string, string, int, int, string, string, string, string[], bool> tag =
                (id, label, description, rumor, reputation, family, discipline, polarity, counterparts, derived) =>
                {
                    Dictionary<string, object> existing = tags
                        .OfType<Dictionary<string, object>>()
                        .FirstOrDefault(x => string.Equals(
                            ReadString(x, "id", ""), id,
                            StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        // Catalogs created by older builds may already contain
                        // the built-in tag but not newer rule fields.  Preserve
                        // every explicit/customized value while filling only
                        // absent fields required by current runtime behavior.
                        Action<string, object> addMissing = (key, value) =>
                        {
                            if (existing.ContainsKey(key)) return;
                            existing[key] = value;
                            changed = true;
                        };
                        addMissing("family", family);
                        addMissing("discipline", discipline);
                        addMissing("polarity", polarity);
                        List<string> existingCounterparts = TextList(existing,
                            "counterpartTagIds");
                        List<string> requiredCounterparts = counterparts
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        if (requiredCounterparts.Any(required =>
                            !existingCounterparts.Contains(required,
                                StringComparer.OrdinalIgnoreCase)))
                        {
                            existing["counterpartTagIds"] = existingCounterparts
                                .Concat(requiredCounterparts)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .Cast<object>().ToList();
                            changed = true;
                        }
                        else
                        {
                            addMissing("counterpartTagIds",
                                requiredCounterparts.Cast<object>().ToList());
                        }
                        addMissing("removable", !derived);
                        addMissing("derived", derived);
                        return;
                    }
                    Dictionary<string, object> item = SocialCatalogTag(id, label, description, rumor, reputation);
                    item["family"] = family;
                    item["discipline"] = discipline;
                    item["polarity"] = polarity;
                    item["counterpartTagIds"] = counterparts.Cast<object>().ToList();
                    item["removable"] = !derived;
                    item["derived"] = derived;
                    tags.Add(item);
                    changed = true;
                };
            Action<string, string, string, double, string, string, string, bool> addArchetype =
                (id, label, description, chance, tagId, family, role, direct) =>
                {
                    Dictionary<string, object> existing = archetypes
                        .OfType<Dictionary<string, object>>()
                        .FirstOrDefault(x => string.Equals(
                            ReadString(x, "id", ""), id,
                            StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        Dictionary<string, object> mappings =
                            Program.DictionaryOrDefault(existing, "roleMappings",
                                new Dictionary<string, object>());
                        List<string> mappedTags = mappings.TryGetValue(role,
                                out object mappedValue)
                            ? ValueTextList(mappedValue)
                            : new List<string>();
                        if (!mappedTags.Contains(tagId,
                            StringComparer.OrdinalIgnoreCase))
                        {
                            mappings[role] = mappedTags.Concat(new[] { tagId })
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .Cast<object>().ToList();
                            existing["roleMappings"] = mappings;
                            changed = true;
                        }
                        return;
                    }
                    archetypes.Add(new Dictionary<string, object>
                    {
                        ["id"] = id, ["label"] = label, ["builtIn"] = true, ["enabled"] = true,
                        ["baseExposureChance"] = chance, ["durationDays"] = direct ? 0d : 45d,
                        ["promotionWindowDays"] = 45d, ["description"] = description,
                        ["family"] = family, ["allowPlayerSubject"] = true, ["directReputation"] = direct,
                        ["roleMappings"] = new Dictionary<string, object> { [role] = new List<object> { tagId } }
                    });
                    changed = true;
                };

            tag("strong_captain", "Strong Captain", "They are said to command a war party with strength and judgment.", 5, 10, "combat", "captain", "positive", new[] { "weak_captain", "coward" }, false);
            tag("weak_captain", "Weak Captain", "Their failures in command have become a subject of open doubt.", -5, -10, "combat", "captain", "negative", new[] { "strong_captain" }, false);
            tag("tactician", "Tactician", "They are credited with directing armies effectively in the field.", 5, 10, "combat", "army_command", "positive", new[] { "failed_tactician", "coward" }, false);
            tag("failed_tactician", "Failed Tactician", "Their direction of an army ended in conspicuous failure.", -5, -10, "combat", "army_command", "negative", new[] { "tactician" }, false);
            tag("siege_commander", "Siege Commander", "They are credited with bringing a difficult siege to victory.", 5, 10, "combat", "siege_attack", "positive", new[] { "inept_besieger", "coward" }, false);
            tag("inept_besieger", "Inept Besieger", "Their conduct of a failed siege is remembered with contempt.", -10, -20, "combat", "siege_attack", "negative", new[] { "siege_commander" }, false);
            tag("siege_breaker", "Siege Breaker", "They stood among the commanders who broke an enemy siege.", 5, 10, "combat", "siege_defense", "positive", new[] { "coward" }, false);
            tag("champion_of_the_pit", "Champion of the Pit", "Their tournament victories have made them celebrated in the arena.", 5, 15, "combat", "tournament", "positive", Array.Empty<string>(), false);
            tag("duelist", "Duelist", "They are known for prevailing in personal challenges.", 5, 10, "combat", "duel", "positive", new[] { "fallen_challenger" }, false);
            tag("fallen_challenger", "The Fallen Challenger", "Their defeats in personal challenges have become part of their name.", -5, -10, "combat", "duel", "negative", new[] { "duelist" }, false);
            tag("coward", "Coward", "They are accused of fleeing defeat rather than facing capture.", -10, -20, "combat", "coward", "negative", Array.Empty<string>(), false);
            tag("war_crowned", "War Crowned", "They are remembered as the ruler who brought a kingdom through a victorious war.", 0, 15, "combat", "war", "positive", new[] { "defeated_crown" }, false);
            tag("defeated_crown", "The Defeated Crown", "They are remembered as the ruler beneath a kingdom's defeat.", 0, -15, "combat", "war", "negative", new[] { "war_crowned" }, false);
            tag("battlemaster", "Battlemaster", "Their victories across several forms of warfare have established uncommon martial prestige.", 0, 10, "combat", "derived_combat", "positive", Array.Empty<string>(), true);
            tag("the_frail", "The Frail", "Repeated failures across several forms of warfare have defined their martial standing.", 0, -10, "combat", "derived_combat", "negative", Array.Empty<string>(), true);

            addArchetype("strong_captain", "Strong Captain", "Reports credit this commander with victory in a field battle.", 0.05d, "strong_captain", "combat", "commander", false);
            addArchetype("weak_captain", "Weak Captain", "Reports blame this commander for defeat in a field battle.", 0.05d, "weak_captain", "combat", "commander", false);
            addArchetype("tactician", "Tactician", "Reports credit this army commander with victory.", 0.10d, "tactician", "combat", "commander", false);
            addArchetype("failed_tactician", "Failed Tactician", "Reports blame this army commander for defeat.", 0.10d, "failed_tactician", "combat", "commander", false);
            addArchetype("siege_commander", "Siege Commander", "Reports credit this commander with a successful siege.", 0.20d, "siege_commander", "combat", "commander", false);
            addArchetype("inept_besieger", "Inept Besieger", "Reports blame this commander for a failed or abandoned siege.", 0.20d, "inept_besieger", "combat", "commander", false);
            addArchetype("siege_breaker", "Siege Breaker", "Reports credit this defender with helping to break a siege.", 0.10d, "siege_breaker", "combat", "defender", false);
            addArchetype("champion_of_the_pit", "Champion of the Pit", "Reports celebrate this tournament victory.", 0.15d, "champion_of_the_pit", "combat", "winner", false);
            addArchetype("duelist", "Duelist", "Reports celebrate victory in a personal challenge.", 0.10d, "duelist", "combat", "winner", false);
            addArchetype("fallen_challenger", "The Fallen Challenger", "Reports dwell on defeat in a personal challenge.", 0.10d, "fallen_challenger", "combat", "loser", false);
            addArchetype("coward", "Coward", "Reports accuse this defeated commander of fleeing capture.", 0.20d, "coward", "combat", "commander", false);
            addArchetype("war_crowned", "War Crowned", "A decisive war ended in this ruler's favor.", 1d, "war_crowned", "combat", "ruler", true);
            addArchetype("defeated_crown", "The Defeated Crown", "A decisive war ended in this ruler's defeat.", 1d, "defeated_crown", "combat", "ruler", true);

            AddGovernanceCatalogEntries(tags, archetypes, tag, addArchetype);
            AddCourtSocialCatalogEntries(tags, archetypes, tag, addArchetype);

            if (ReadInt(catalog, "revision", 1) < BuiltInSocialCatalogRevision)
            {
                catalog["revision"] = BuiltInSocialCatalogRevision;
                changed = true;
            }
            if (!catalog.ContainsKey("governanceSettings"))
            {
                catalog["governanceSettings"] = new Dictionary<string, object>
                {
                    ["accessionGraceDays"] = 30d, ["sampleRetentionDays"] = 35d,
                    ["postSiegeRecoveryDays"] = 7d, ["integrationFirstDay"] = 30d,
                    ["integrationHardExitDay"] = 45d,
                    ["loyaltyDanger"] = 25d, ["loyaltyStable"] = 40d, ["loyaltySecure"] = 50d,
                    ["securityCritical"] = 15d, ["securityLow"] = 30d,
                    ["securityRecovered"] = 50d, ["securityStrong"] = 60d
                };
                changed = true;
            }
            if (!catalog.ContainsKey("warScoreSettings"))
            {
                catalog["warScoreSettings"] = new Dictionary<string, object>
                {
                    ["townWeight"] = 40d, ["castleWeight"] = 20d, ["casualtyCap"] = 25d,
                    ["siegeWeight"] = 5d, ["raidWeight"] = 1d, ["raidCap"] = 10d,
                    ["rulerCaptureWeight"] = 15d, ["tributeWeight"] = 15d, ["victoryMargin"] = 15d
                };
                changed = true;
            }
            return changed || !string.Equals(catalogBefore, Json.Serialize(catalog),
                StringComparison.Ordinal);
        }

        private static void AddGovernanceCatalogEntries(List<object> tags, List<object> archetypes,
            Action<string, string, string, int, int, string, string, string, string[], bool> addTag,
            Action<string, string, string, double, string, string, string, bool> addArchetype)
        {
            Action<string, string, string, int, int, string, string, string, string[], string> governanceTag =
                (id, label, description, rumor, reputation, discipline, polarity, pillar, counterparts, unused) =>
                {
                    int before = tags.Count;
                    addTag(id, label, description, rumor, reputation, "governance", discipline, polarity, counterparts, false);
                    if (tags.Count > before)
                    {
                        Dictionary<string, object> item = tags.OfType<Dictionary<string, object>>().Last();
                        item["governancePillar"] = pillar;
                    }
                };
            governanceTag("realm_builder", "Realm Builder", "Their reign is associated with broad and sustained prosperity.", 5, 10, "prosperity", "positive", "economy_subjects", new[] { "realm_in_decline" }, "");
            governanceTag("realm_in_decline", "Realm in Decline", "Their reign is associated with sustained economic decline.", -5, -15, "prosperity", "negative", "economy_subjects", new[] { "realm_builder" }, "");
            governanceTag("provider_of_the_realm", "Provider of the Realm", "Their realm is known for dependable stores and recovery from hunger.", 5, 15, "food", "positive", "economy_subjects", new[] { "starving_crown" }, "");
            governanceTag("starving_crown", "The Starving Crown", "Settlements were left hungry and without adequate relief under their crown.", -10, -20, "food", "negative", "economy_subjects", new[] { "provider_of_the_realm" }, "");
            governanceTag("unifier_of_the_realm", "Unifier of the Realm", "They restored or maintained loyalty across the kingdom.", 5, 15, "loyalty", "positive", "stability_integration", new[] { "fractured_crown" }, "");
            governanceTag("fractured_crown", "The Fractured Crown", "Dangerous disloyalty and rebellion marked their rule.", -10, -20, "loyalty", "negative", "stability_integration", new[] { "unifier_of_the_realm" }, "");
            governanceTag("keeper_of_order", "Keeper of Order", "Their rule is associated with security and restored public order.", 5, 10, "security", "positive", "stability_integration", new[] { "lawless_crown" }, "");
            governanceTag("lawless_crown", "The Lawless Crown", "Widespread insecurity was allowed to persist under their rule.", -5, -15, "security", "negative", "stability_integration", new[] { "keeper_of_order" }, "");
            governanceTag("guardian_of_the_commons", "Guardian of the Commons", "Ruined villages and their people recovered under their protection.", 5, 10, "villages", "positive", "economy_subjects", new[] { "lord_of_empty_fields" }, "");
            governanceTag("lord_of_empty_fields", "Lord of Empty Fields", "Ruined and deserted villages were left without recovery.", -5, -15, "villages", "negative", "economy_subjects", new[] { "guardian_of_the_commons" }, "");
            governanceTag("consolidator", "Consolidator", "Newly won territory became stable under their rule.", 5, 15, "integration", "positive", "stability_integration", new[] { "overextended_crown" }, "");
            governanceTag("overextended_crown", "The Overextended Crown", "New conquests remained unstable and poorly governed.", -10, -20, "integration", "negative", "stability_integration", new[] { "consolidator" }, "");
            governanceTag("fair_hand_of_the_crown", "Fair Hand of the Crown", "They distributed land to clans with the strongest objective need.", 5, 15, "fief_award", "positive", "political_management", new[] { "hoarder_of_titles" }, "");
            governanceTag("hoarder_of_titles", "Hoarder of Titles", "They accumulated fiefs while deserving clans remained without land.", -10, -20, "fief_award", "negative", "political_management", new[] { "fair_hand_of_the_crown" }, "");
            governanceTag("gatherer_of_banners", "Gatherer of Banners", "Established noble houses gathered and remained beneath their banner.", 5, 15, "clan_retention", "positive", "political_management", new[] { "scatterer_of_banners" }, "");
            governanceTag("scatterer_of_banners", "Scatterer of Banners", "Established noble houses abandoned their kingdom under this ruler.", -10, -20, "clan_retention", "negative", "political_management", new[] { "gatherer_of_banners" }, "");
            addTag("steward_of_the_realm", "Steward of the Realm", "Their legacy joins prosperity, stability, and political stewardship.", 0, 10, "governance", "derived_governance", "positive", Array.Empty<string>(), true);
            addTag("ruinous_crown", "The Ruinous Crown", "Their legacy joins economic, institutional, and political failure.", 0, -15, "governance", "derived_governance", "negative", Array.Empty<string>(), true);

            addArchetype("realm_builder", "Realm Builder", "Sustained prosperity growth is being credited to the ruler.", 0.05d, "realm_builder", "governance", "ruler", false);
            addArchetype("realm_in_decline", "Realm in Decline", "Sustained prosperity decline is being blamed on the ruler.", 0.10d, "realm_in_decline", "governance", "ruler", false);
            addArchetype("provider_of_the_realm", "Provider of the Realm", "Reliable food stores or recovery are being credited to the ruler.", 0.10d, "provider_of_the_realm", "governance", "ruler", false);
            addArchetype("starving_crown", "The Starving Crown", "Persistent starvation is being blamed on the ruler.", 0.20d, "starving_crown", "governance", "ruler", false);
            addArchetype("unifier_of_the_realm", "Unifier of the Realm", "Broad loyalty or recovery is being credited to the ruler.", 0.10d, "unifier_of_the_realm", "governance", "ruler", false);
            addArchetype("fractured_crown", "The Fractured Crown", "Persistent dangerous loyalty is being blamed on the ruler.", 0.20d, "fractured_crown", "governance", "ruler", false);
            addArchetype("fractured_crown_rebellion", "The Fractured Crown", "An actual rebellion has broken out under this ruler.", 0.30d, "fractured_crown", "governance", "ruler", false);
            addArchetype("keeper_of_order", "Keeper of Order", "High security or recovery is being credited to the ruler.", 0.05d, "keeper_of_order", "governance", "ruler", false);
            addArchetype("lawless_crown", "The Lawless Crown", "Persistent insecurity is being blamed on the ruler.", 0.10d, "lawless_crown", "governance", "ruler", false);
            addArchetype("guardian_of_the_commons", "Guardian of the Commons", "Village recovery is being credited to the ruler.", 0.10d, "guardian_of_the_commons", "governance", "ruler", false);
            addArchetype("lord_of_empty_fields", "Lord of Empty Fields", "Ruined villages remain neglected under this ruler.", 0.15d, "lord_of_empty_fields", "governance", "ruler", false);
            addArchetype("consolidator", "Consolidator", "A conquest became stable under this ruler.", 0.10d, "consolidator", "governance", "ruler", false);
            addArchetype("overextended_crown", "The Overextended Crown", "A conquest remained dangerously unstable.", 0.20d, "overextended_crown", "governance", "ruler", false);
            addArchetype("fair_hand_of_the_crown", "Fair Hand of the Crown", "A fief was awarded to an objectively under-landed clan.", 0.15d, "fair_hand_of_the_crown", "governance", "ruler", false);
            addArchetype("hoarder_of_titles", "Hoarder of Titles", "The ruling clan took land while eligible clans remained under-landed.", 0.20d, "hoarder_of_titles", "governance", "ruler", false);
            addArchetype("gatherer_of_banners", "Gatherer of Banners", "A noble clan joined and remained under this ruler.", 0.10d, "gatherer_of_banners", "governance", "ruler", false);
            addArchetype("scatterer_of_banners", "Scatterer of Banners", "An established noble clan abandoned this ruler.", 0.20d, "scatterer_of_banners", "governance", "ruler", false);
        }

        private static List<object> CatalogObjectList(Dictionary<string, object> catalog, string key)
        {
            if (catalog.TryGetValue(key, out object existing) && existing is List<object> list) return list;
            List<object> result = existing is IEnumerable<object> sequence ? sequence.ToList() : new List<object>();
            catalog[key] = result;
            return result;
        }

        internal static void ProcessSocialWorldHistoryEvent(string campaignId, string timelineId,
            Dictionary<string, object> item, bool scheduleReasonJobs = true,
            ReignDbConnection sharedConnection = null,
            bool deferSubjectReconciliation = false,
            Dictionary<string, object> socialCatalog = null,
            Action<string, Dictionary<string, object>> telemetrySink = null)
        {
            if (!string.Equals(ReadFirstString(item, "eventType", "event_type", "type"), "social_outcome", StringComparison.OrdinalIgnoreCase))
                return;
            Dictionary<string, object> payload = Program.DictionaryOrDefault(item, "payload", item);
            string eventId = FirstNonEmpty(ReadFirstString(item, "eventId", "event_id", "id"), ReadString(payload, "sourceEventId", ""));
            string subjectId = ReadFirstString(payload, "subjectId", "heroId");
            string archetypeId = ReadString(payload, "archetypeId", "");
            double worldDay = ReadDouble(item, "worldDay", ReadDouble(payload, "worldDay", 0d));
            if (string.IsNullOrWhiteSpace(eventId))
                throw new InvalidDataException("A social outcome is missing its durable event ID.");
            if (string.IsNullOrWhiteSpace(subjectId))
                throw new InvalidDataException("Social outcome " + eventId + " is missing its subject ID.");

            bool ownsConnection = sharedConnection == null;
            ReignDbConnection connection = sharedConnection
                ?? ReignDatabase.OpenCampaignConnection(campaignId);
            try
            {
                if (ownsConnection) EnsureSocialReputationSchema(connection);
                Dictionary<string, object> receipt = ReignDatabase.QuerySql(connection,
                    "SELECT * FROM social_outcome_receipts WHERE event_id=$id LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = eventId }).FirstOrDefault();
                if (ReadInt(receipt, "completed", 0) != 0) return;
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (receipt == null)
                    ReignDatabase.ExecuteSql(connection, @"INSERT INTO social_outcome_receipts(event_id,campaign_id,timeline_id,subject_id,archetype_id,updated_ts)
VALUES($event,$campaign,$timeline,$subject,$archetype,$ts);",
                        new Dictionary<string, object> { ["event"] = eventId, ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["archetype"] = archetypeId, ["ts"] = ts });

                Dictionary<string, object> result;
                if (archetypeId == "whoremonger")
                {
                    result = RegisterWhoremongerNpcEntry(connection, campaignId, timelineId,
                        eventId, subjectId, worldDay, payload, sharedConnection != null, socialCatalog);
                    ReignDatabase.ExecuteSql(connection, "UPDATE social_outcome_receipts SET counter_applied=1,completed=1,result_json=$result,updated_ts=$ts WHERE event_id=$event;",
                        new Dictionary<string, object> { ["event"] = eventId,
                            ["result"] = Json.Serialize(result), ["ts"] = ts });
                    return;
                }
                if (ApplyCourtDynamicSocialOutcome(connection, campaignId, timelineId, subjectId,
                    payload, eventId, worldDay, out result, socialCatalog))
                {
                    if (!deferSubjectReconciliation)
                    {
                        RecomputeDerivedSocialReputations(connection, campaignId,
                            timelineId, subjectId, worldDay, eventId,
                            socialCatalog);
                        ReconcileSocialRelationshipsForSubjects(connection,
                            campaignId, timelineId, new[] { subjectId }, worldDay);
                    }
                    ReignDatabase.ExecuteSql(connection, "UPDATE social_outcome_receipts SET counter_applied=1,completed=1,result_json=$result,updated_ts=$ts WHERE event_id=$event;",
                        new Dictionary<string, object>
                        {
                            ["event"] = eventId, ["result"] = Json.Serialize(result),
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });
                    if (scheduleReasonJobs && !campaignId.StartsWith("__", StringComparison.Ordinal))
                        SchedulePendingReputationReasonJobs(campaignId);
                    return;
                }

                if (ReadInt(receipt, "counter_applied", 0) == 0)
                {
                    List<string> explicitCounterTags = TextList(payload, "counterOnlyTagIds");
                    ApplySocialCounterevidence(connection, campaignId, timelineId, subjectId, archetypeId,
                        explicitCounterTags, eventId, worldDay,
                        ReadBool(payload, "counterRumorOnly", false),
                        socialCatalog);
                    ReignDatabase.ExecuteSql(connection, "UPDATE social_outcome_receipts SET counter_applied=1,updated_ts=$ts WHERE event_id=$event;",
                        new Dictionary<string, object> { ["event"] = eventId, ["ts"] = ts });
                }

                result = new Dictionary<string, object> { ["ok"] = true, ["counterOnly"] = string.IsNullOrWhiteSpace(archetypeId) };
                if (!string.IsNullOrWhiteSpace(archetypeId))
                {
                    Dictionary<string, object> occurrence = new Dictionary<string, object>
                    {
                        ["timelineId"] = timelineId, ["archetypeId"] = archetypeId,
                        ["threadKey"] = subjectId + "|" + archetypeId,
                        ["sourceEventId"] = eventId, ["worldDay"] = worldDay,
                        ["provenanceSummary"] = ReadString(payload, "provenanceSummary", ReadString(item, "summary", "")),
                        ["forceExposure"] = ReadBool(payload, "forceExposure", false),
                        ["forcePromotion"] = ReadBool(payload, "forcePromotion", false),
                        ["participants"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["subjectId"] = subjectId,
                                ["role"] = ReadString(payload, "role", "ruler"),
                                ["clanTier"] = ReadInt(payload, "clanTier", 1),
                                ["isAlive"] = ReadBool(payload, "isAlive", true),
                                ["isAdult"] = ReadBool(payload, "isAdult", true),
                                ["isPlayer"] = ReadBool(payload, "isPlayer", false),
                                ["sex"] = ReadString(payload, "sex", ""),
                                ["linkedHeroId"] = ReadString(payload, "linkedHeroId", ""),
                                ["linkedHeroName"] = ReadString(payload, "linkedHeroName", "")
                            }
                        }
                    };
                    result = RegisterSocialOccurrence(connection, campaignId,
                        occurrence, socialCatalog, true,
                        sharedConnection != null, deferSubjectReconciliation,
                        telemetrySink);
                }
                if (!deferSubjectReconciliation)
                {
                    RecomputeDerivedSocialReputations(connection, campaignId,
                        timelineId, subjectId, worldDay, eventId,
                        socialCatalog);
                    ReconcileSocialRelationshipsForSubjects(connection,
                        campaignId, timelineId, new[] { subjectId }, worldDay);
                }
                ReignDatabase.ExecuteSql(connection, "UPDATE social_outcome_receipts SET completed=1,result_json=$result,updated_ts=$ts WHERE event_id=$event;",
                    new Dictionary<string, object> { ["event"] = eventId, ["result"] = Json.Serialize(result), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            }
            finally
            {
                if (ownsConnection) connection.Dispose();
            }
            if (scheduleReasonJobs && !campaignId.StartsWith("__", StringComparison.Ordinal))
                SchedulePendingReputationReasonJobs(campaignId);
        }

        internal static void RecordSocialWorldHistoryFailure(string campaignId, string timelineId,
            string eventId, Exception error,
            ReignDbConnection sharedConnection = null)
        {
            if (string.IsNullOrWhiteSpace(eventId)) return;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string message = Program.LimitText(error == null ? "Unknown social outcome failure." : error.Message, 1000);
            bool ownsConnection = sharedConnection == null;
            ReignDbConnection connection = sharedConnection
                ?? ReignDatabase.OpenCampaignConnection(campaignId);
            try
            {
                EnsureSocialReputationSchema(connection);
                ReignDatabase.ExecuteSql(connection, @"INSERT INTO social_outcome_receipts(
event_id,campaign_id,timeline_id,attempt_count,last_error,completed,result_json,updated_ts)
VALUES($event,$campaign,$timeline,1,$error,0,$result,$ts)
ON CONFLICT(event_id) DO UPDATE SET
attempt_count=social_outcome_receipts.attempt_count+1,
last_error=excluded.last_error,
completed=CASE WHEN social_outcome_receipts.attempt_count+1>=3 THEN -1 ELSE 0 END,
result_json=excluded.result_json,updated_ts=excluded.updated_ts;",
                    new Dictionary<string, object>
                    {
                        ["event"] = eventId, ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["error"] = message,
                        ["result"] = Json.Serialize(new Dictionary<string, object>
                        {
                            ["ok"] = false, ["error"] = message
                        }),
                        ["ts"] = ts
                    });
            }
            finally
            {
                if (ownsConnection) connection.Dispose();
            }
        }

        private static void ApplySocialCounterevidence(ReignDbConnection connection, string campaignId, string timelineId,
            string subjectId, string archetypeId, List<string> explicitCounterTags, string eventId, double worldDay,
            bool rumorOnly = false,
            Dictionary<string, object> socialCatalog = null)
        {
            Dictionary<string, object> catalog = socialCatalog
                ?? ReadActiveSocialCatalog(connection);
            List<Dictionary<string, object>> tags = ReadDictionaryList(catalog, "tags");
            List<Dictionary<string, object>> archetypes = ReadDictionaryList(catalog, "archetypes");
            List<string> outcomeTags = new List<string>();
            Dictionary<string, object> archetype = archetypes.FirstOrDefault(x =>
                string.Equals(ReadString(x, "id", ""), archetypeId, StringComparison.OrdinalIgnoreCase));
            if (archetype != null)
                outcomeTags.AddRange(Program.DictionaryOrDefault(archetype, "roleMappings", new Dictionary<string, object>())
                    .Values.SelectMany(ValueTextList));
            // An archetype whose ID is also a tag ID has an unambiguous
            // canonical outcome tag. Always include it: legacy or customized
            // catalogs can retain a partial role mapping from an older build,
            // and treating that incomplete mapping as authoritative would
            // silently disable counterpart/counterevidence processing.
            if (tags.Any(x => string.Equals(ReadString(x, "id", ""),
                    archetypeId, StringComparison.OrdinalIgnoreCase)))
                outcomeTags.Add(archetypeId);
            outcomeTags = outcomeTags.Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            List<string> counterTags = explicitCounterTags.ToList();
            foreach (string outcomeTag in outcomeTags)
            {
                Dictionary<string, object> definition = tags.FirstOrDefault(x =>
                    string.Equals(ReadString(x, "id", ""), outcomeTag, StringComparison.OrdinalIgnoreCase));
                counterTags.AddRange(TextList(definition, "counterpartTagIds"));
                // Counterpart relationships are symmetric even when a legacy
                // catalog recorded the link on only the opposite tag.
                counterTags.AddRange(tags.Where(candidate =>
                        TextList(candidate, "counterpartTagIds").Contains(
                            outcomeTag, StringComparer.OrdinalIgnoreCase))
                    .Select(candidate => ReadString(candidate, "id", "")));
                ReignDatabase.ExecuteSql(connection, @"UPDATE reputation_counterevidence SET consecutive_count=0,last_event_id=$event,
last_outcome_day=$day,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId,
                        ["tag"] = outcomeTag, ["event"] = eventId, ["day"] = worldDay,
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    });
            }
            counterTags = counterTags.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string counterTag in counterTags)
            {
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ReignDatabase.ExecuteSql(connection, @"UPDATE rumor_subject_tags SET status='countered',updated_ts=$ts
WHERE subject_id=$subject AND tag_id=$tag AND status='active';",
                    new Dictionary<string, object> { ["subject"] = subjectId, ["tag"] = counterTag, ["ts"] = ts });
                ReignDatabase.ExecuteSql(connection, @"DELETE FROM shared_tag_exposure_streaks
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["subject"] = subjectId, ["tag"] = counterTag
                    });
                foreach (Dictionary<string, object> counterArchetype in archetypes.Where(x =>
                    Program.DictionaryOrDefault(x, "roleMappings", new Dictionary<string, object>()).Values
                        .SelectMany(ValueTextList).Contains(counterTag, StringComparer.OrdinalIgnoreCase)))
                {
                    ReignDatabase.ExecuteSql(connection, @"DELETE FROM rumor_exposure_streaks
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id=$archetype AND thread_key=$thread;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["archetype"] = ReadString(counterArchetype, "id", ""),
                            ["thread"] = subjectId + "|" + ReadString(counterArchetype, "id", "")
                        });
                }

                if (rumorOnly) continue;
                bool activeReputation = ReignDatabase.QuerySql(connection, @"SELECT 1 FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag AND status='active' LIMIT 1;",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["tag"] = counterTag }).Any();
                if (!activeReputation) continue;
                Dictionary<string, object> prior = ReignDatabase.QuerySql(connection, @"SELECT * FROM reputation_counterevidence
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag LIMIT 1;",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["tag"] = counterTag }).FirstOrDefault();
                int count = ReadInt(prior, "consecutive_count", 0) + 1;
                double chance = Math.Min(1d, count * 0.05d);
                double roll = DeterministicSocialRoll(string.Join("|", campaignId, timelineId, subjectId, counterTag, eventId, "counter"));
                ReignDatabase.ExecuteSql(connection, @"INSERT INTO reputation_counterevidence(campaign_id,timeline_id,subject_id,tag_id,
consecutive_count,last_event_id,last_outcome_day,last_chance,last_roll,updated_ts)
VALUES($campaign,$timeline,$subject,$tag,$count,$event,$day,$chance,$roll,$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,tag_id) DO UPDATE SET
consecutive_count=$count,last_event_id=$event,last_outcome_day=$day,last_chance=$chance,last_roll=$roll,updated_ts=$ts;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId,
                        ["tag"] = counterTag, ["count"] = count, ["event"] = eventId, ["day"] = worldDay,
                        ["chance"] = chance, ["roll"] = roll, ["ts"] = ts
                    });
                if (roll >= chance) continue;
                ReignDatabase.ExecuteSql(connection, @"UPDATE character_reputations SET status='removed_by_counterevidence',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag AND status='active';",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["tag"] = counterTag, ["ts"] = ts });
                ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO reputation_lifecycle_events(lifecycle_id,campaign_id,timeline_id,
subject_id,tag_id,event_type,source_event_id,world_day,details_json,created_ts)
VALUES($id,$campaign,$timeline,$subject,$tag,'removed_by_counterevidence',$event,$day,$details,$ts);",
                    new Dictionary<string, object>
                    {
                        ["id"] = "reputation_lifecycle_" + DeterministicSocialId(eventId + "|" + subjectId + "|" + counterTag),
                        ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId,
                        ["tag"] = counterTag, ["event"] = eventId, ["day"] = worldDay,
                        ["details"] = Json.Serialize(new Dictionary<string, object> { ["count"] = count, ["chance"] = chance, ["roll"] = roll }), ["ts"] = ts
                    });
                ReignDatabase.ExecuteSql(connection, @"UPDATE reputation_counterevidence SET consecutive_count=0,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag;",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["tag"] = counterTag, ["ts"] = ts });
            }
        }

        private static void RecomputeDerivedSocialReputations(ReignDbConnection connection, string campaignId,
            string timelineId, string subjectId, double worldDay,
            string sourceEventId,
            Dictionary<string, object> socialCatalog = null)
        {
            Dictionary<string, object> catalog = socialCatalog
                ?? ReadActiveSocialCatalog(connection);
            Dictionary<string, Dictionary<string, object>> definitions = ReadDictionaryList(catalog, "tags")
                .ToDictionary(x => ReadString(x, "id", ""), x => x, StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> active = ReignDatabase.QuerySql(connection, @"SELECT tag_id,snapshot_json FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND status='active';",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId });
            List<Dictionary<string, object>> activeDefinitions = active.Select(row =>
            {
                string id = ReadString(row, "tag_id", "");
                if (definitions.TryGetValue(id, out Dictionary<string, object> value)) return value;
                return TryParseJsonObject(ReadString(row, "snapshot_json", "{}")) ?? new Dictionary<string, object>();
            }).Where(x => x != null).ToList();

            int combatPositive = activeDefinitions.Count(x => ReadString(x, "family", "") == "combat"
                && ReadString(x, "polarity", "") == "positive" && !ReadBool(x, "derived", false));
            int combatNegative = activeDefinitions.Count(x => ReadString(x, "family", "") == "combat"
                && ReadString(x, "polarity", "") == "negative" && !ReadBool(x, "derived", false));
            SetDerivedReputation(connection, campaignId, timelineId, subjectId, "battlemaster", combatPositive >= 3, definitions, worldDay, sourceEventId);
            SetDerivedReputation(connection, campaignId, timelineId, subjectId, "the_frail", combatNegative >= 3, definitions, worldDay, sourceEventId);

            HashSet<string> positivePillars = new HashSet<string>(activeDefinitions.Where(x =>
                    ReadString(x, "family", "") == "governance" && ReadString(x, "polarity", "") == "positive"
                    && !ReadBool(x, "derived", false))
                .Select(x => ReadString(x, "governancePillar", "")).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
            HashSet<string> negativePillars = new HashSet<string>(activeDefinitions.Where(x =>
                    ReadString(x, "family", "") == "governance" && ReadString(x, "polarity", "") == "negative"
                    && !ReadBool(x, "derived", false))
                .Select(x => ReadString(x, "governancePillar", "")).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
            int governancePositive = activeDefinitions.Count(x => ReadString(x, "family", "") == "governance"
                && ReadString(x, "polarity", "") == "positive" && !ReadBool(x, "derived", false));
            int governanceNegative = activeDefinitions.Count(x => ReadString(x, "family", "") == "governance"
                && ReadString(x, "polarity", "") == "negative" && !ReadBool(x, "derived", false));
            bool steward = governancePositive >= 3 && positivePillars.Count >= 3;
            bool ruinous = governanceNegative >= 3 && negativePillars.Count >= 3;
            SetDerivedReputation(connection, campaignId, timelineId, subjectId, "steward_of_the_realm", steward && !ruinous, definitions, worldDay, sourceEventId);
            SetDerivedReputation(connection, campaignId, timelineId, subjectId, "ruinous_crown", ruinous && !steward, definitions, worldDay, sourceEventId);
        }

        private static void SetDerivedReputation(ReignDbConnection connection, string campaignId, string timelineId,
            string subjectId, string tagId, bool active, Dictionary<string, Dictionary<string, object>> definitions,
            double worldDay, string sourceEventId)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool currentlyActive = ReignDatabase.QuerySql(connection, @"SELECT 1 FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag AND status='active' LIMIT 1;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["tag"] = tagId }).Any();
            if (active == currentlyActive) return;
            if (!active)
            {
                ReignDatabase.ExecuteSql(connection, @"UPDATE character_reputations SET status='derived_requirement_lost',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag AND status='active';",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId, ["tag"] = tagId, ["ts"] = ts });
                return;
            }
            if (!definitions.TryGetValue(tagId, out Dictionary<string, object> definition)) return;
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_reputations(campaign_id,timeline_id,subject_id,tag_id,
source_occurrence_id,archetype_id,subject_role,description,reputation_value,acquired_day,catalog_revision,snapshot_json,status,updated_ts)
VALUES($campaign,$timeline,$subject,$tag,$source,'derived','derived',$description,$value,$day,$revision,$snapshot,'active',$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,tag_id) DO UPDATE SET
source_occurrence_id=$source,archetype_id='derived',subject_role='derived',description=$description,
reputation_value=$value,acquired_day=$day,catalog_revision=$revision,snapshot_json=$snapshot,status='active',updated_ts=$ts;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId,
                    ["tag"] = tagId, ["source"] = sourceEventId, ["description"] = ReadString(definition, "description", ""),
                    ["value"] = ReadInt(definition, "reputationValue", 0), ["day"] = worldDay,
                    ["revision"] = BuiltInSocialCatalogRevision, ["snapshot"] = Json.Serialize(definition), ["ts"] = ts
                });
            string incident = "This reputation became established because the character currently meets its recorded social requirements.";
            RecordReputationActivation(connection, campaignId, timelineId, subjectId, tagId, "", sourceEventId,
                worldDay, ReadString(definition, "description", ""), incident,
                new Dictionary<string, object>
                {
                    ["source"] = "derived_reputation", ["sourceEventId"] = sourceEventId,
                    ["qualifyingReputations"] = activeReputationLabelsForReason(connection, campaignId, timelineId, subjectId)
                });
        }

        private static List<string> activeReputationLabelsForReason(ReignDbConnection connection, string campaignId,
            string timelineId, string subjectId)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT description FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND status='active'
AND subject_role<>'derived' ORDER BY acquired_day DESC LIMIT 20;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId })
                .Select(row => ReadString(row, "description", "")).Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        }

        private static List<string> TextList(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value)) return new List<string>();
            return ValueTextList(value);
        }

        private static List<string> ValueTextList(object value)
        {
            if (value is IEnumerable<object> objects)
                return objects.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture))
                    .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (value is string text && !string.IsNullOrWhiteSpace(text)) return new List<string> { text };
            return new List<string>();
        }
    }
}
