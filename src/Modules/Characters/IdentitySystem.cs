using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void EnsureIdentitySchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS acquaintances (
observer_id TEXT NOT NULL,
subject_id TEXT NOT NULL,
identity_state TEXT NOT NULL DEFAULT 'encountered_unknown',
canonical_name TEXT NOT NULL DEFAULT '',
claimed_name TEXT NOT NULL DEFAULT '',
aliases_json TEXT NOT NULL DEFAULT '[]',
verification_source TEXT NOT NULL DEFAULT '',
source_entity_id TEXT NOT NULL DEFAULT '',
confidence REAL NOT NULL DEFAULT 0,
first_met_day REAL NOT NULL DEFAULT 0,
last_met_day REAL NOT NULL DEFAULT 0,
encounter_count INTEGER NOT NULL DEFAULT 0,
last_encounter_id TEXT NOT NULL DEFAULT '',
recognition_attempts INTEGER NOT NULL DEFAULT 0,
last_recognition_result TEXT NOT NULL DEFAULT '',
last_recognition_encounter_id TEXT NOT NULL DEFAULT '',
payload_json TEXT NOT NULL DEFAULT '{}',
updated_ts INTEGER NOT NULL DEFAULT 0,
PRIMARY KEY(observer_id,subject_id));");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_acquaintances_subject ON acquaintances(subject_id,identity_state);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_acquaintances_observer ON acquaintances(observer_id,identity_state);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS identity_evidence (
evidence_id TEXT PRIMARY KEY,
observer_id TEXT NOT NULL,
subject_id TEXT NOT NULL,
evidence_type TEXT NOT NULL,
claimed_name TEXT NOT NULL DEFAULT '',
canonical_name TEXT NOT NULL DEFAULT '',
source TEXT NOT NULL DEFAULT '',
source_entity_id TEXT NOT NULL DEFAULT '',
encounter_id TEXT NOT NULL DEFAULT '',
confidence REAL NOT NULL DEFAULT 0,
world_day REAL NOT NULL DEFAULT 0,
payload_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL DEFAULT 0);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_identity_evidence_pair ON identity_evidence(observer_id,subject_id,created_ts DESC);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS identity_roster (
hero_id TEXT PRIMARY KEY,canonical_name TEXT NOT NULL DEFAULT '',clan_id TEXT NOT NULL DEFAULT '',
kingdom_id TEXT NOT NULL DEFAULT '',is_lord INTEGER NOT NULL DEFAULT 0,is_ruler INTEGER NOT NULL DEFAULT 0,
family_ids_json TEXT NOT NULL DEFAULT '[]',updated_ts INTEGER NOT NULL DEFAULT 0);");
            EnsureDatabaseColumn(connection, "identity_roster", "is_alive", "INTEGER NOT NULL DEFAULT 1");
            EnsureDatabaseColumn(connection, "identity_roster", "is_adult", "INTEGER NOT NULL DEFAULT 1");
            EnsureDatabaseColumn(connection, "identity_roster", "is_player", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "identity_roster", "sex", "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "identity_roster", "clan_tier", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "identity_roster", "current_charm", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "identity_roster", "occupation", "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "identity_roster", "is_notable", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "identity_roster", "is_wanderer", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "identity_roster", "is_clan_leader", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "identity_roster", "is_mercenary_clan", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "identity_roster", "governor_settlement_id", "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "identity_roster", "governor_settlement_name", "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "acquaintances", "last_recognition_day", "REAL NOT NULL DEFAULT -1");
            EnsureDatabaseColumn(connection, "acquaintances", "last_recognition_probability", "REAL NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "acquaintances", "last_recognition_roll", "REAL NOT NULL DEFAULT -1");
            EnsureDatabaseColumn(connection, "acquaintances", "recognition_rule_version", "INTEGER NOT NULL DEFAULT 0");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_identity_roster_clan ON identity_roster(clan_id,hero_id);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_identity_roster_kingdom ON identity_roster(kingdom_id,is_lord,hero_id);");
            EnsureCompactIdentityEvidencePayloads(connection);
        }

        private static void EnsureCompactIdentityEvidencePayloads(ReignDbConnection connection)
        {
            const int version = 2;
            int current = ReadInt(QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='identity_payload_compaction_version' LIMIT 1;")
                .FirstOrDefault(), "value", 0);
            if (current >= version) return;
            ExecuteSql(connection, "BEGIN IMMEDIATE;");
            try
            {
                // Evidence rows are the canonical provenance store. Keeping the
                // same request envelope on the acquaintance summary row doubled
                // it without adding any authoritative state.
                ExecuteSql(connection,
                    "UPDATE acquaintances SET payload_json='{}' WHERE payload_json<>'{}';");
                foreach (Dictionary<string, object> row in QuerySql(connection,
                    "SELECT evidence_id,payload_json FROM identity_evidence WHERE length(payload_json)>2048;"))
                {
                    Dictionary<string, object> payload =
                        TryParseJsonObject(ReadString(row, "payload_json", ""))
                        ?? new Dictionary<string, object>();
                    ExecuteSql(connection,
                        "UPDATE identity_evidence SET payload_json=$payload WHERE evidence_id=$id;",
                        new Dictionary<string, object>
                        {
                            ["id"] = ReadString(row, "evidence_id", ""),
                            ["payload"] = Json.Serialize(CompactIdentityEvidencePayload(payload))
                        });
                }
                ExecuteSql(connection,
                    "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('identity_payload_compaction_version',$version);",
                    new Dictionary<string, object> { ["version"] = version.ToString(CultureInfo.InvariantCulture) });
                ExecuteSql(connection,
                    "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('campaign_compaction_required','1');");
                ExecuteSql(connection, "COMMIT;");
            }
            catch
            {
                try { ExecuteSql(connection, "ROLLBACK;"); } catch { }
                throw;
            }
        }

        private static Dictionary<string, object> CompactIdentityEvidencePayload(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            Dictionary<string, object> compact = new Dictionary<string, object>();
            foreach (string key in new[]
            {
                "playerText", "text", "message", "playerName", "mainHeroName",
                "mode", "encounterId", "conversationSessionId", "sessionId", "eventId"
            })
            {
                string value = ReadString(payload, key, "");
                if (!string.IsNullOrWhiteSpace(value))
                    compact[key] = LimitText(value, key.EndsWith("Text", StringComparison.OrdinalIgnoreCase)
                        || key == "text" || key == "message" ? 1200 : 160);
            }
            Dictionary<string, object> subject = ReadDictionary(payload, "subject");
            if (subject != null && !string.IsNullOrWhiteSpace(ReadString(subject, "name", "")))
                compact["subject"] = new Dictionary<string, object>
                {
                    ["name"] = LimitText(ReadString(subject, "name", ""), 160)
                };
            foreach (string key in new[] { "groupTranscript", "transcript" })
            {
                if (!payload.TryGetValue(key, out object raw)
                    || raw == null || raw is string || !(raw is IEnumerable enumerable))
                    continue;
                List<object> rows = new List<object>();
                foreach (object item in enumerable)
                {
                    if (item is Dictionary<string, object> row)
                    {
                        string text = ReadFirstString(row, "text", "content", "message");
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        rows.Add(new Dictionary<string, object>
                        {
                            ["role"] = LimitText(ReadString(row, "role", ""), 32),
                            ["text"] = LimitText(text, 1200)
                        });
                    }
                    else
                    {
                        string text = item?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(text)) rows.Add(LimitText(text, 1200));
                    }
                }
                if (rows.Count > 12) rows = rows.Skip(rows.Count - 12).ToList();
                if (rows.Count > 0) compact[key] = rows;
            }
            return compact;
        }

        private static Dictionary<string, object> IdentitySynchronizeApi(Dictionary<string, object> payload)
        {
            var syncTimer = System.Diagnostics.Stopwatch.StartNew();
            var syncPhases = new Dictionary<string, object>();
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string correlationId = EnsureCorrelationId(payload);
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            List<Dictionary<string, object>> heroes = ReadDictionaryList(payload, "heroes")
                .Where(x => !string.IsNullOrWhiteSpace(IdentityHeroId(x)) && ReadBool(x, "isAlive", true))
                .GroupBy(IdentityHeroId, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();
            string fingerprint = FirstNonEmpty(ReadString(payload, "rosterFingerprint", ""), ComputeIdentityRosterFingerprint(heroes));
            string expectedPortraitRosterFingerprint = ReadString(payload, "portraitRosterFingerprint", "");
            Dictionary<string, object> savedPortraitRoster = ReadJsonObject(
                CampaignFile(campaignId, "portrait_roster.json"));
            string savedPortraitRosterFingerprint = ReadString(savedPortraitRoster, "rosterFingerprint", "");
            bool portraitRosterSyncRequired = !string.IsNullOrWhiteSpace(expectedPortraitRosterFingerprint)
                && !string.Equals(
                    expectedPortraitRosterFingerprint,
                    savedPortraitRosterFingerprint,
                    StringComparison.Ordinal);
            Dictionary<string, object> financeCensus = PersistFinanceSnapshotIndex(
                campaignId,
                ReadDictionaryList(payload, "financeSnapshots"),
                worldDay);
            int seeded = 0;
            int upgraded = 0;
            int unchanged = 0;
            int migrated = 0;
            Dictionary<string, object> storageRepair = new Dictionary<string, object>();

            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                syncPhases["prepareAndConnectMs"] = syncTimer.ElapsedMilliseconds;
                var phaseTimer = System.Diagnostics.Stopwatch.StartNew();
                // Run bounded-storage migrations from the same roster synchronization that occurs
                // after a campaign is loaded. This repairs older oversized databases before the
                // next native save asks Save Sync to snapshot them.
                EnsureAmbientRelationshipSchema(connection);
                EnsureIdentitySchema(connection);
                string previousFingerprint = ReadString(QuerySql(connection, "SELECT value FROM schema_meta WHERE key='identity_roster_fingerprint' LIMIT 1;").FirstOrDefault(), "value", "");
                bool rosterChanged = !string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal);
                Dictionary<string, Dictionary<string, object>> byId = heroes.ToDictionary(IdentityHeroId, x => x, StringComparer.OrdinalIgnoreCase);
                storageRepair = ReconcileSyntheticIdentityRows(connection, heroes);
                if (rosterChanged)
                {
                    var previousRoster = QuerySql(connection, "SELECT * FROM identity_roster;");
                    var historicalBatch = PrepareHistoricalIdentityBatch(previousRoster, heroes);
                    syncPhases["historicalPrepareMs"] = phaseTimer.ElapsedMilliseconds;
                    phaseTimer.Restart();
                    ExecuteSql(connection, "BEGIN IMMEDIATE;");
                    syncPhases["transactionWaitMs"] = phaseTimer.ElapsedMilliseconds;
                    phaseTimer.Restart();
                    try
                    {
                        // Another census may have committed while preparation ran.
                        // Rebase against its roster under the existing transaction lock.
                        string lockedFingerprint = ReadString(QuerySql(connection,
                            "SELECT value FROM schema_meta WHERE key='identity_roster_fingerprint' LIMIT 1;")
                            .FirstOrDefault(), "value", "");
                        if (!string.Equals(previousFingerprint, lockedFingerprint, StringComparison.Ordinal))
                        {
                            previousRoster = QuerySql(connection, "SELECT * FROM identity_roster;");
                            historicalBatch = PrepareHistoricalIdentityBatch(previousRoster, heroes);
                        }
                        WriteHistoricalIdentityBatch(connection, historicalBatch, worldDay,
                            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        syncPhases["historicalRows"] = historicalBatch.Count;
                        syncPhases["historicalWriteCommands"] = (historicalBatch.Count + 999) / 1000;
                        ExecuteSql(connection, "DELETE FROM identity_roster;");
                        long rosterTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        // The roster was just cleared, so the existing batch writer
                        // persists the same rows/timestamps without thousands of
                        // command preparations and round trips under the campaign lock.
                        UpsertIdentityRosterHeroesBatch(connection, heroes, rosterTs);
                        ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('identity_roster_fingerprint',$value);", new Dictionary<string, object> { ["value"] = fingerprint });
                        ExecuteSql(connection, "COMMIT;");
                        syncPhases["rosterTransactionMs"] = phaseTimer.ElapsedMilliseconds;
                        phaseTimer.Restart();
                    }
                    catch
                    {
                        try { ExecuteSql(connection, "ROLLBACK;"); } catch { }
                        throw;
                    }
                    EnsureSocialReputationSchema(connection);
                    // A brand-new roster cannot have stale court-popularity
                    // projections. Those are created incrementally by relationship
                    // processing. Recomputing every lord here made first startup
                    // perform thousands of empty relationship lookups before the
                    // initial save could flush World History.
                    bool hasCourtPopularityState = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM court_dynamic_reputation_metrics WHERE domain='popularity' LIMIT 1;")
                        .FirstOrDefault(), "count", 0) > 0;
                    var popularitySubjects = hasCourtPopularityState
                        ? SelectIdentityPopularitySubjects(connection, previousRoster,
                            QuerySql(connection, "SELECT * FROM identity_roster;")) : new List<string>();
                    syncPhases["popularitySubjects"] = popularitySubjects.Count;
                    int courtPopularityChanges = RecomputeCourtPopularityReputationsBatch(connection, campaignId,
                        ReadString(payload, "timelineId", "main"), popularitySubjects, worldDay,
                        correlationId + "|court_popularity").Count;
                    if (courtPopularityChanges > 0
                        && !campaignId.StartsWith("__", StringComparison.Ordinal))
                        SchedulePendingReputationReasonJobs(campaignId);
                    // Expiration remains day-sensitive. The roster standing pass below
                    // reconciles the union of current and historical subjects once.
                    ExpireSocialRumors(connection, campaignId,
                        ReadString(payload, "timelineId", "main"), worldDay);
                    syncPhases["reputationReconciliationMs"] = phaseTimer.ElapsedMilliseconds;
                }
                phaseTimer.Restart();
                EnsurePublicStandingForRoster(connection, campaignId,
                    ReadString(payload, "timelineId", "main"), worldDay);
                syncPhases["publicStandingMs"] = phaseTimer.ElapsedMilliseconds;
                phaseTimer.Restart();
                Dictionary<string, object> politicalRelationships =
                    ReconcilePoliticalRelationshipNetwork(connection, campaignId,
                        ReadString(payload, "timelineId", "main"), worldDay,
                        rosterChanged ? "identity_roster_changed" : "identity_roster_verified",
                        ReadBool(payload, "deferRelationshipProjection", false));
                syncPhases["politicalRelationshipsMs"] = phaseTimer.ElapsedMilliseconds;
                phaseTimer.Restart();

                // Legacy dialogue continuity is a separate one-time migration. Run it
                // even when an earlier build already wrote the same roster fingerprint.
                migrated += MigrateLegacyIdentityKnowledge(connection, byId, worldDay, correlationId);
                bool compacted = TryCompactCampaignDatabase(connection);
                syncPhases["migrationAndCompactionMs"] = phaseTimer.ElapsedMilliseconds;
                syncPhases["totalMs"] = syncTimer.ElapsedMilliseconds;

                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["campaignId"] = campaignId,
                    ["rosterFingerprint"] = fingerprint,
                    ["rosterChanged"] = rosterChanged,
                    ["heroCount"] = heroes.Count,
                    ["seeded"] = seeded,
                    ["upgraded"] = upgraded,
                    ["unchanged"] = unchanged,
                    ["legacyMigrated"] = migrated,
                    ["storageRepair"] = storageRepair,
                    ["politicalRelationships"] = politicalRelationships,
                    ["databaseCompacted"] = compacted
                };
                result["portraitRosterSyncRequired"] = portraitRosterSyncRequired;
                result["portraitRosterFingerprint"] = savedPortraitRosterFingerprint;
                result["financeCensus"] = financeCensus;
                result["performance"] = syncPhases;
                WriteAudit(campaignId, correlationId, "server", "identity", "identity.synchronized", "", "", "", "completed", 0, "Directional identity network synchronized.", result);
                return result;
            }
        }

        private static Dictionary<string, object> ReconcileSyntheticIdentityRows(ReignDbConnection connection, List<Dictionary<string, object>> heroes)
        {
            const int repairVersion = 3;
            int currentVersion = ReadInt(QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='identity_synthetic_repair_version' LIMIT 1;").FirstOrDefault(), "value", 0);
            if (currentVersion >= repairVersion)
                return new Dictionary<string, object> { ["applied"] = false, ["version"] = currentVersion };

            long evidenceBefore = ReadLong(QuerySql(connection, "SELECT COUNT(*) AS count FROM identity_evidence;").FirstOrDefault(), "count", 0);
            long acquaintancesBefore = ReadLong(QuerySql(connection, "SELECT COUNT(*) AS count FROM acquaintances;").FirstOrDefault(), "count", 0);
            ExecuteSql(connection, "BEGIN IMMEDIATE;");
            try
            {
                string syntheticSources = "'same_clan','immediate_family','same_kingdom_nobility','realm_sovereign','ruler_network','live_same_realm_sovereign'";
                ExecuteSql(connection, @"DELETE FROM identity_evidence
WHERE source IN (" + syntheticSources + @");");
                ExecuteSql(connection, @"UPDATE acquaintances SET
identity_state=CASE WHEN claimed_name<>'' THEN 'claimed' ELSE 'encountered_unknown' END,
verification_source=CASE WHEN claimed_name<>'' THEN 'preserved_explicit_claim' ELSE 'recognition_required_after_rule_v3' END,
source_entity_id='',confidence=CASE WHEN claimed_name<>'' THEN 0.6 ELSE 0 END,
last_recognition_result='',last_recognition_encounter_id='',last_recognition_day=-1,
last_recognition_probability=0,last_recognition_roll=-1,recognition_rule_version=3
WHERE verification_source IN (" + syntheticSources + @")
AND NOT EXISTS (
    SELECT 1 FROM identity_evidence e
    WHERE e.observer_id=acquaintances.observer_id AND e.subject_id=acquaintances.subject_id
    AND e.source NOT IN (" + syntheticSources + @")
    AND e.evidence_type='verified'
);");
                ExecuteSql(connection, @"DELETE FROM acquaintances
WHERE verification_source IN (" + syntheticSources + @")
AND encounter_count=0
AND claimed_name=''
AND NOT EXISTS (
    SELECT 1 FROM identity_evidence e
    WHERE e.observer_id=acquaintances.observer_id AND e.subject_id=acquaintances.subject_id
    AND e.source NOT IN (" + syntheticSources + @")
);");
                ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('identity_synthetic_repair_version',$version);",
                    new Dictionary<string, object> { ["version"] = repairVersion.ToString(CultureInfo.InvariantCulture) });
                ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('campaign_compaction_required','1');");
                ExecuteSql(connection, "COMMIT;");
            }
            catch
            {
                try { ExecuteSql(connection, "ROLLBACK;"); } catch { }
                throw;
            }

            long evidenceAfter = ReadLong(QuerySql(connection, "SELECT COUNT(*) AS count FROM identity_evidence;").FirstOrDefault(), "count", 0);
            long acquaintancesAfter = ReadLong(QuerySql(connection, "SELECT COUNT(*) AS count FROM acquaintances;").FirstOrDefault(), "count", 0);
            return new Dictionary<string, object>
            {
                ["applied"] = true,
                ["version"] = repairVersion,
                ["storageModel"] = "compact_identity_roster",
                ["removedEvidence"] = Math.Max(0L, evidenceBefore - evidenceAfter),
                ["removedAcquaintances"] = Math.Max(0L, acquaintancesBefore - acquaintancesAfter)
            };
        }

        private static bool TryCompactCampaignDatabase(ReignDbConnection connection)
        {
            if (!string.Equals(ReadString(QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='campaign_compaction_required' LIMIT 1;").FirstOrDefault(), "value", ""), "1", StringComparison.Ordinal))
                return false;
            try
            {
                // Startup synchronization is latency-sensitive and shares this
                // database with World History and relationship ingestion. A full
                // VACUUM here monopolized SQLite for minutes on a fresh campaign.
                // Keep startup maintenance bounded; retired pages can be reused by
                // SQLite and Save Sync already snapshots through the backup API.
                ExecuteSql(connection, "PRAGMA wal_checkpoint(PASSIVE);");
                ExecuteSql(connection, "PRAGMA optimize;");
                ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('campaign_compaction_required','0');");
                return true;
            }
            catch
            {
                // A concurrent reader can temporarily prevent VACUUM. Keep the flag so the
                // next campaign synchronization retries after the connection clears.
                return false;
            }
        }

        private static void UpsertIdentityRosterHero(ReignDbConnection connection, Dictionary<string, object> hero, long ts)
        {
            string heroId = IdentityHeroId(hero);
            if (string.IsNullOrWhiteSpace(heroId)) return;
            HashSet<string> family = IdentityFamilyIds(hero);
            ExecuteSql(connection, @"INSERT INTO identity_roster(
hero_id,canonical_name,clan_id,kingdom_id,is_lord,is_ruler,family_ids_json,is_alive,is_adult,is_player,sex,clan_tier,current_charm,
occupation,is_notable,is_wanderer,is_clan_leader,is_mercenary_clan,governor_settlement_id,governor_settlement_name,updated_ts)
VALUES($hero,$name,$clan,$kingdom,$lord,$ruler,$family,$alive,$adult,$player,$sex,$tier,$charm,
$occupation,$notable,$wanderer,$clanLeader,$mercenary,$governorId,$governorName,$ts)
ON CONFLICT(hero_id) DO UPDATE SET canonical_name=$name,clan_id=$clan,kingdom_id=$kingdom,
is_lord=$lord,is_ruler=$ruler,family_ids_json=$family,is_alive=$alive,is_adult=$adult,is_player=$player,
sex=$sex,clan_tier=$tier,current_charm=$charm,occupation=$occupation,is_notable=$notable,is_wanderer=$wanderer,
is_clan_leader=$clanLeader,is_mercenary_clan=$mercenary,
governor_settlement_id=$governorId,governor_settlement_name=$governorName,updated_ts=$ts;",
                new Dictionary<string, object>
                {
                    ["hero"] = heroId, ["name"] = ReadString(hero, "name", ""),
                    ["clan"] = ReadString(hero, "clanId", ""), ["kingdom"] = ReadString(hero, "kingdomId", ""),
                    ["lord"] = IdentityIsLord(hero) ? 1 : 0, ["ruler"] = ReadBool(hero, "isRuler", false) ? 1 : 0,
                    ["family"] = Json.Serialize(family.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()),
                    ["alive"] = ReadBool(hero, "isAlive", true) ? 1 : 0,
                    ["adult"] = ReadBool(hero, "isAdult", !ReadBool(hero, "isChild", false)) ? 1 : 0,
                    ["player"] = ReadBool(hero, "isPlayer", false) ? 1 : 0,
                    ["sex"] = ReadString(hero, "sex", ReadBool(hero, "isFemale", false) ? "female" : "male"),
                    ["tier"] = Math.Max(0, Math.Min(6, ReadInt(hero, "clanTier", 0))),
                    ["charm"] = Math.Max(0, ReadInt(hero, "currentCharm", ReadInt(hero, "charm", 0))),
                    ["occupation"] = ReadString(hero, "occupation", ""),
                    ["notable"] = ReadBool(hero, "isNotable", false) ? 1 : 0,
                    ["wanderer"] = ReadBool(hero, "isWanderer", false) ? 1 : 0,
                    ["clanLeader"] = ReadBool(hero, "isClanLeader", false) ? 1 : 0,
                    ["mercenary"] = ReadBool(hero, "isMercenaryClan", false) ? 1 : 0,
                    ["governorId"] = ReadString(hero, "governorOfSettlementId", ""),
                    ["governorName"] = ReadString(hero, "governorOfSettlementName", ""),
                    ["ts"] = ts
                });
        }

        private static void UpsertIdentityRosterHeroesBatch(
            ReignDbConnection connection,
            IEnumerable<Dictionary<string, object>> heroes,
            long ts)
        {
            List<Dictionary<string, object>> source = (heroes
                    ?? Enumerable.Empty<Dictionary<string, object>>())
                .Where(hero => !string.IsNullOrWhiteSpace(IdentityHeroId(hero)))
                .ToList();
            if (!ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                foreach (Dictionary<string, object> hero in source)
                    UpsertIdentityRosterHero(connection, hero, ts);
                return;
            }
            if (source.Count == 0)
                return;

            List<Dictionary<string, object>> rows = source.Select(hero =>
            {
                HashSet<string> family = IdentityFamilyIds(hero);
                return new Dictionary<string, object>
                {
                    ["hero"] = IdentityHeroId(hero),
                    ["name"] = ReadString(hero, "name", ""),
                    ["clan"] = ReadString(hero, "clanId", ""),
                    ["kingdom"] = ReadString(hero, "kingdomId", ""),
                    ["lord"] = IdentityIsLord(hero) ? 1 : 0,
                    ["ruler"] = ReadBool(hero, "isRuler", false) ? 1 : 0,
                    ["family"] = Json.Serialize(family.OrderBy(value => value,
                        StringComparer.OrdinalIgnoreCase).ToList()),
                    ["alive"] = ReadBool(hero, "isAlive", true) ? 1 : 0,
                    ["adult"] = ReadBool(hero, "isAdult",
                        !ReadBool(hero, "isChild", false)) ? 1 : 0,
                    ["player"] = ReadBool(hero, "isPlayer", false) ? 1 : 0,
                    ["sex"] = ReadString(hero, "sex",
                        ReadBool(hero, "isFemale", false) ? "female" : "male"),
                    ["tier"] = Math.Max(0, Math.Min(6,
                        ReadInt(hero, "clanTier", 0))),
                    ["charm"] = Math.Max(0, ReadInt(hero, "currentCharm",
                        ReadInt(hero, "charm", 0))),
                    ["occupation"] = ReadString(hero, "occupation", ""),
                    ["notable"] = ReadBool(hero, "isNotable", false) ? 1 : 0,
                    ["wanderer"] = ReadBool(hero, "isWanderer", false) ? 1 : 0,
                    ["clanleader"] = ReadBool(hero, "isClanLeader", false) ? 1 : 0,
                    ["mercenary"] = ReadBool(hero, "isMercenaryClan", false) ? 1 : 0,
                    ["governorid"] = ReadString(hero,
                        "governorOfSettlementId", ""),
                    ["governorname"] = ReadString(hero,
                        "governorOfSettlementName", ""),
                    ["ts"] = ts
                };
            }).ToList();
            ExecutePostgreSqlJsonCommand(connection, @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(@rows) AS r(
        hero text,name text,clan text,kingdom text,lord integer,
        ruler integer,family text,alive integer,adult integer,
        player integer,sex text,tier integer,charm integer,
        occupation text,notable integer,wanderer integer,
        clanleader integer,mercenary integer,governorid text,
        governorname text,ts bigint)
)
INSERT INTO identity_roster(
    hero_id,canonical_name,clan_id,kingdom_id,is_lord,is_ruler,
    family_ids_json,is_alive,is_adult,is_player,sex,clan_tier,
    current_charm,occupation,is_notable,is_wanderer,is_clan_leader,
    is_mercenary_clan,governor_settlement_id,governor_settlement_name,
    updated_ts)
SELECT hero,name,clan,kingdom,lord,ruler,family,alive,adult,player,sex,
       tier,charm,occupation,notable,wanderer,clanleader,mercenary,
       governorid,governorname,ts
FROM x
ON CONFLICT(hero_id) DO UPDATE SET
    canonical_name=excluded.canonical_name,
    clan_id=excluded.clan_id,
    kingdom_id=excluded.kingdom_id,
    is_lord=excluded.is_lord,
    is_ruler=excluded.is_ruler,
    family_ids_json=excluded.family_ids_json,
    is_alive=excluded.is_alive,
    is_adult=excluded.is_adult,
    is_player=excluded.is_player,
    sex=excluded.sex,
    clan_tier=excluded.clan_tier,
    current_charm=excluded.current_charm,
    occupation=excluded.occupation,
    is_notable=excluded.is_notable,
    is_wanderer=excluded.is_wanderer,
    is_clan_leader=excluded.is_clan_leader,
    is_mercenary_clan=excluded.is_mercenary_clan,
    governor_settlement_id=excluded.governor_settlement_id,
    governor_settlement_name=excluded.governor_settlement_name,
    updated_ts=excluded.updated_ts
WHERE (identity_roster.canonical_name,identity_roster.clan_id,
       identity_roster.kingdom_id,identity_roster.is_lord,
       identity_roster.is_ruler,identity_roster.family_ids_json,
       identity_roster.is_alive,identity_roster.is_adult,
       identity_roster.is_player,identity_roster.sex,
       identity_roster.clan_tier,identity_roster.current_charm,
       identity_roster.occupation,identity_roster.is_notable,
       identity_roster.is_wanderer,identity_roster.is_clan_leader,
       identity_roster.is_mercenary_clan,
       identity_roster.governor_settlement_id,
       identity_roster.governor_settlement_name)
IS DISTINCT FROM
      (excluded.canonical_name,excluded.clan_id,excluded.kingdom_id,
       excluded.is_lord,excluded.is_ruler,excluded.family_ids_json,
       excluded.is_alive,excluded.is_adult,excluded.is_player,
       excluded.sex,excluded.clan_tier,excluded.current_charm,
       excluded.occupation,excluded.is_notable,excluded.is_wanderer,
       excluded.is_clan_leader,excluded.is_mercenary_clan,
       excluded.governor_settlement_id,
       excluded.governor_settlement_name);",
                PostgreSqlRelationshipRowsJson(rows));
        }

        private static HashSet<string> IdentityFamilyIds(Dictionary<string, object> hero)
        {
            HashSet<string> family = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            family.Add(ReadString(hero, "spouseId", ""));
            family.Add(ReadString(hero, "fatherId", ""));
            family.Add(ReadString(hero, "motherId", ""));
            foreach (string childId in ReadStringList(hero, "childrenIds")) family.Add(childId);
            family.RemoveWhere(string.IsNullOrWhiteSpace);
            return family;
        }

        private static void PreserveDepartingImplicitKnowledge(
            ReignDbConnection connection,
            List<Dictionary<string, object>> nextHeroes,
            double worldDay, long? fixedTimestamp = null)
        {
            List<Dictionary<string, object>> previous = QuerySql(connection, "SELECT * FROM identity_roster;");
            if (previous.Count == 0) return;
            Dictionary<string, Dictionary<string, object>> next = (nextHeroes ?? new List<Dictionary<string, object>>())
                .Where(x => !string.IsNullOrWhiteSpace(IdentityHeroId(x)))
                .ToDictionary(IdentityHeroId, x => x, StringComparer.OrdinalIgnoreCase);
            long ts = fixedTimestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (Dictionary<string, object> oldHero in previous)
            {
                string heroId = ReadString(oldHero, "hero_id", "");
                if (!next.TryGetValue(heroId, out Dictionary<string, object> newHero)) continue;
                bool changed = !ReadString(oldHero, "clan_id", "").Equals(ReadString(newHero, "clanId", ""), StringComparison.OrdinalIgnoreCase)
                    || !ReadString(oldHero, "kingdom_id", "").Equals(ReadString(newHero, "kingdomId", ""), StringComparison.OrdinalIgnoreCase)
                    || ReadInt(oldHero, "is_lord", 0) != (IdentityIsLord(newHero) ? 1 : 0)
                    || ReadInt(oldHero, "is_ruler", 0) != (ReadBool(newHero, "isRuler", false) ? 1 : 0)
                    || !ReadString(oldHero, "family_ids_json", "[]").Equals(
                        Json.Serialize(IdentityFamilyIds(newHero).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()),
                        StringComparison.Ordinal);
                if (!changed) continue;
                foreach (Dictionary<string, object> other in previous)
                {
                    if (heroId.Equals(ReadString(other, "hero_id", ""), StringComparison.OrdinalIgnoreCase)) continue;
                    PreserveImplicitIdentityDirection(connection, oldHero, other, worldDay, ts);
                    PreserveImplicitIdentityDirection(connection, other, oldHero, worldDay, ts);
                }
            }
        }

        private static void PreserveImplicitIdentityDirection(
            ReignDbConnection connection,
            Dictionary<string, object> observer,
            Dictionary<string, object> subject,
            double worldDay,
            long ts)
        {
            string source = ImplicitIdentitySource(observer, subject);
            if (string.IsNullOrWhiteSpace(source)) return;
            ExecuteSql(connection, @"INSERT INTO acquaintances(
observer_id,subject_id,identity_state,canonical_name,verification_source,confidence,first_met_day,last_met_day,updated_ts)
VALUES($observer,$subject,'verified',$name,'historical_group_identity',1,$day,$day,$ts)
ON CONFLICT(observer_id,subject_id) DO UPDATE SET
identity_state='verified',
canonical_name=CASE WHEN excluded.canonical_name<>'' THEN excluded.canonical_name ELSE acquaintances.canonical_name END,
verification_source='historical_group_identity',
confidence=1,
last_met_day=$day,
updated_ts=$ts
WHERE acquaintances.identity_state<>'verified';",
                new Dictionary<string, object>
                {
                    ["observer"] = ReadString(observer, "hero_id", ""),
                    ["subject"] = ReadString(subject, "hero_id", ""),
                    ["name"] = ReadString(subject, "canonical_name", ""),
                    ["day"] = worldDay, ["ts"] = ts
                });
        }

        private static string ImplicitIdentitySource(
            Dictionary<string, object> observer,
            Dictionary<string, object> subject)
        {
            if (observer == null || subject == null) return "";
            string observerId = ReadString(observer, "hero_id", "");
            string subjectId = ReadString(subject, "hero_id", "");
            if (string.IsNullOrWhiteSpace(observerId) || string.IsNullOrWhiteSpace(subjectId)
                || observerId.Equals(subjectId, StringComparison.OrdinalIgnoreCase)) return "";
            HashSet<string> observerFamily = new HashSet<string>(
                TextListFromJson(ReadString(observer, "family_ids_json", "[]")), StringComparer.OrdinalIgnoreCase);
            HashSet<string> subjectFamily = new HashSet<string>(
                TextListFromJson(ReadString(subject, "family_ids_json", "[]")), StringComparer.OrdinalIgnoreCase);
            if (observerFamily.Contains(subjectId) || subjectFamily.Contains(observerId)) return "immediate_family";
            string observerClan = ReadString(observer, "clan_id", "");
            if (!string.IsNullOrWhiteSpace(observerClan)
                && observerClan.Equals(ReadString(subject, "clan_id", ""), StringComparison.OrdinalIgnoreCase)) return "same_clan";
            string observerKingdom = ReadString(observer, "kingdom_id", "");
            if (ReadInt(observer, "is_lord", 0) != 0
                && ReadInt(subject, "is_ruler", 0) != 0
                && !string.IsNullOrWhiteSpace(observerKingdom)
                && observerKingdom.Equals(
                    ReadString(subject, "kingdom_id", ""),
                    StringComparison.OrdinalIgnoreCase))
                return "own_sovereign";
            return "";
        }

        private static string LiveImplicitIdentitySource(
            Dictionary<string, object> observer,
            Dictionary<string, object> subject)
        {
            if (observer == null || subject == null) return "";
            string observerId = IdentityHeroId(observer);
            string subjectId = IdentityHeroId(subject);
            if (string.IsNullOrWhiteSpace(observerId)
                || string.IsNullOrWhiteSpace(subjectId)
                || observerId.Equals(subjectId,
                    StringComparison.OrdinalIgnoreCase))
                return "";

            HashSet<string> observerFamily = IdentityFamilyIds(observer);
            HashSet<string> subjectFamily = IdentityFamilyIds(subject);
            if (observerFamily.Contains(subjectId)
                || subjectFamily.Contains(observerId))
                return "immediate_family";

            string observerClan = ReadString(observer, "clanId", "");
            string subjectClan = ReadString(subject, "clanId", "");
            if (!string.IsNullOrWhiteSpace(observerClan)
                && observerClan.Equals(subjectClan,
                    StringComparison.OrdinalIgnoreCase))
                return "same_clan";

            string observerKingdom = ReadString(observer, "kingdomId", "");
            if (IdentityIsLord(observer)
                && ReadBool(subject, "isRuler", false)
                && !string.IsNullOrWhiteSpace(observerKingdom)
                && observerKingdom.Equals(
                    ReadString(subject, "kingdomId", ""),
                    StringComparison.OrdinalIgnoreCase))
                return "own_sovereign";

            return "";
        }

        private static Dictionary<string, object> IdentityEncounterApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string observerId = ReadFirstString(payload, "observerHeroStringId", "observerId", "npcId", "speakerHeroStringId");
            string subjectId = ReadFirstString(payload, "subjectHeroStringId", "subjectId", "playerHeroStringId");
            string encounterId = FirstNonEmpty(ReadFirstString(payload, "encounterId", "conversationSessionId", "sessionId", "eventId"), "encounter_" + Guid.NewGuid().ToString("N"));
            string correlationId = EnsureCorrelationId(payload);
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            if (string.IsNullOrWhiteSpace(observerId) || string.IsNullOrWhiteSpace(subjectId))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "observerHeroStringId and subjectHeroStringId are required." };

            Dictionary<string, object> observer = ReadDictionary(payload, "observer") ?? new Dictionary<string, object> { ["heroStringId"] = observerId };
            Dictionary<string, object> subject = ReadDictionary(payload, "subject") ?? new Dictionary<string, object> { ["heroStringId"] = subjectId, ["name"] = ReadString(payload, "canonicalName", "") };
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                Dictionary<string, object> stored =
                    ReadStoredAcquaintance(
                        connection, observerId, subjectId);
                string liveImplicitSource =
                    LiveImplicitIdentitySource(observer, subject);
                if (HasQualifiedCourtAudienceIdentity(payload, observer, subject))
                    liveImplicitSource = "formal_court_audience";
                if (!string.IsNullOrWhiteSpace(liveImplicitSource)
                    && !IdentityStateVerified(stored)
                    && (stored == null
                        || !liveImplicitSource.Equals(
                            "own_sovereign",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    // Only encountered pairs become durable rows. The complete
                    // native network remains one compact roster row per hero.
                    // Family and clan transitions may upgrade stale rows, but
                    // becoming a sovereign cannot retroactively reveal an
                    // acquaintance whose identity was already unknown.
                    UpsertVerifiedIdentity(
                        connection, observer, subject,
                        liveImplicitSource, subjectId,
                        worldDay, correlationId);
                }
                Dictionary<string, object> existing =
                    ReadAcquaintance(
                        connection, observerId, subjectId);
                bool newEncounter = existing == null || !string.Equals(ReadString(existing, "last_encounter_id", ""), encounterId, StringComparison.OrdinalIgnoreCase);
                if (existing == null)
                {
                    InsertUnknownAcquaintance(connection, observerId, subjectId, ReadString(subject, "name", ""), encounterId, worldDay, payload);
                    AddIdentityEvidence(connection, observerId, subjectId, "encountered_unknown", "", ReadString(subject, "name", ""), "direct_encounter", "", encounterId, 0d, worldDay, payload);
                    existing = ReadAcquaintance(connection, observerId, subjectId);
                }
                else if (newEncounter)
                {
                    ExecuteSql(connection, @"UPDATE acquaintances SET last_met_day=$day,encounter_count=encounter_count+1,last_encounter_id=$encounter,updated_ts=$ts WHERE observer_id=$observer AND subject_id=$subject;",
                        IdentitySqlArgs(observerId, subjectId, worldDay, encounterId));
                    existing = ReadAcquaintance(connection, observerId, subjectId);
                }

                Dictionary<string, object> recognition = new Dictionary<string, object>
                {
                    ["status"] = IdentityStateVerified(existing) ? "not_needed" : "identity_required",
                    ["recognized"] = false
                };

                string playerText = ReadFirstString(payload, "playerText", "text", "message");
                string claimed = ExtractAuthoritativeSelfIntroduction(playerText, subject);
                if (string.IsNullOrWhiteSpace(claimed) && !IdentityStateVerified(existing))
                    claimed = ExtractNameFirstSelfIntroduction(playerText, subject, payload);
                if (string.IsNullOrWhiteSpace(claimed)) claimed = ExtractSelfIntroducedName(
                    playerText,
                    IdentityIntroductionAnswerExpected(payload),
                    ReadString(subject, "name", ""));
                if (!string.IsNullOrWhiteSpace(claimed))
                {
                    string canonical = ReadString(subject, "name", "");
                    string introductionSource = AuthoritativeIntroductionSource(claimed, subject);
                    if (!string.IsNullOrWhiteSpace(introductionSource))
                    {
                        QuarantineAmbiguousStoredIdentityClaim(
                            connection,
                            ReadStoredAcquaintance(
                                connection,
                                observerId,
                                subjectId),
                            observerId,
                            subjectId,
                            worldDay);
                        UpsertVerifiedIdentity(
                            connection, observer, subject,
                            introductionSource, subjectId,
                            worldDay, correlationId);
                        recognition = new Dictionary<string, object>
                        {
                            ["status"] = "not_needed_after_introduction",
                            ["recognized"] = true,
                            ["source"] = introductionSource
                        };
                    }
                    else
                    {
                        UpsertClaimedIdentity(connection, observerId, subjectId,
                            canonical, claimed, "self_introduction", subjectId,
                            encounterId, worldDay, payload);
                    }
                }

                Dictionary<string, object> row = ReadAcquaintance(connection, observerId, subjectId);
                if (!IdentityStateVerified(row) && string.IsNullOrWhiteSpace(claimed)
                    && ReadString(row, "identity_state", "") == "claimed"
                    && ReadString(row, "verification_source", "") == "self_introduction"
                    && AuthoritativeIntroductionSource(ReadString(row, "claimed_name", ""), subject).Length > 0)
                {
                    Dictionary<string, object> priorEvidence = QuerySql(connection,
                        "SELECT payload_json FROM identity_evidence WHERE observer_id=$observer AND subject_id=$subject AND source='self_introduction' AND evidence_type='claimed_identity' ORDER BY created_ts DESC LIMIT 1;",
                        new Dictionary<string, object> { ["observer"] = observerId, ["subject"] = subjectId }).FirstOrDefault();
                    Dictionary<string, object> priorPayload = TryParseJsonObject(ReadString(priorEvidence, "payload_json", ""));
                    string priorIntroduction = ExtractAuthoritativeSelfIntroduction(ReadFirstString(priorPayload, "playerText", "text", "message"), subject);
                    if (priorIntroduction.Length > 0 && NormalizeIdentityName(priorIntroduction) == NormalizeIdentityName(ReadString(row, "claimed_name", "")))
                    {
                        UpsertVerifiedIdentity(connection, observer, subject,
                            AuthoritativeIntroductionSource(priorIntroduction, subject) + "_reconciled", subjectId, worldDay, correlationId);
                        row = ReadAcquaintance(connection, observerId, subjectId);
                    }
                }
                if (!IdentityStateVerified(row))
                {
                    recognition = ResolveIdentityRecognitionAttempt(
                        connection, campaignId, observer, subject,
                        observerId, subjectId, encounterId,
                        worldDay, correlationId, row);
                    row = ReadAcquaintance(
                        connection, observerId, subjectId);
                }
                if (QuarantineAmbiguousStoredIdentityClaim(connection, row, observerId, subjectId, worldDay))
                {
                    row = ReadAcquaintance(connection, observerId, subjectId);
                }
                Dictionary<string, object> view = BuildIdentityView(row, payload);
                if (IdentityStateVerified(row))
                    ReconcileSocialRelationshipForObserverSubject(
                        connection,
                        campaignId,
                        ReadString(payload, "timelineId", "main"),
                        observerId,
                        subjectId,
                        worldDay);
                view["socialStandingView"] = BuildKnownSocialStandingView(connection,
                    campaignId, ReadString(payload, "timelineId", "main"), observerId, subjectId,
                    IdentityStateVerified(row), worldDay);
                Dictionary<string, object> response = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["campaignId"] = campaignId,
                    ["encounterId"] = encounterId,
                    ["newEncounter"] = newEncounter,
                    ["identityView"] = view,
                    ["recognition"] = recognition,
                    ["introductionDetected"] = claimed,
                    ["liveImplicitRecognitionSource"] =
                        liveImplicitSource,
                    ["identityRosterAvailable"] =
                        QuerySql(connection,
                            "SELECT COUNT(*) AS count FROM identity_roster;")
                            .Select(rowValue =>
                                ReadLong(rowValue, "count", 0))
                            .FirstOrDefault() > 0
                };
                WriteAudit(campaignId, correlationId, "server", "identity", "identity.encounter", observerId, "", encounterId, "completed", 0, "Identity view resolved for encounter.", response);
                return response;
            }
        }

        private const int IdentityRecognitionRuleVersion = 3;

        private static Dictionary<string, object> ResolveIdentityRecognitionAttempt(
            ReignDbConnection connection,
            string campaignId,
            Dictionary<string, object> observer,
            Dictionary<string, object> subject,
            string observerId,
            string subjectId,
            string encounterId,
            double worldDay,
            string correlationId,
            Dictionary<string, object> row)
        {
            row = row ?? new Dictionary<string, object>();
            string lastEncounter = ReadString(
                row, "last_recognition_encounter_id", "");
            if (!string.IsNullOrWhiteSpace(encounterId)
                && lastEncounter.Equals(
                    encounterId, StringComparison.OrdinalIgnoreCase))
            {
                return IdentityRecognitionReceipt(
                    row, "replayed",
                    ReadString(row, "last_recognition_result", "")
                        .Equals("recognized",
                            StringComparison.OrdinalIgnoreCase));
            }

            double lastDay = ReadDouble(
                row, "last_recognition_day", -1d);
            if (lastDay >= 0d && worldDay >= lastDay
                && worldDay - lastDay < 1d)
            {
                Dictionary<string, object> cooldown =
                    IdentityRecognitionReceipt(
                        row, "cooldown", false);
                cooldown["nextEligibleWorldDay"] = lastDay + 1d;
                return cooldown;
            }

            int charm = IdentityRecognitionCharm(observer);
            int tier = Math.Max(
                1, Math.Min(6,
                    ReadInt(subject, "clanTier",
                        ReadInt(subject, "clan_tier", 0))));
            double baseChance = Math.Min(
                0.80d, Math.Max(0, charm) / 250d);
            double tierMultiplier =
                IdentityRecognitionTierMultiplier(tier);
            double probability = Math.Min(
                0.80d, baseChance * tierMultiplier);
            bool residentLocalAuthority = IsResidentLocalAuthority(observer, subjectId);
            if (residentLocalAuthority)
                probability = ReignBeta.Shared.Characters.EncounteredResidentRules.LocalAuthorityRecognitionProbability;
            double roll = StableIdentityRecognitionRoll(
                campaignId, observerId, subjectId, encounterId);
            bool recognized = roll < probability;
            string result = recognized
                ? "recognized"
                : "not_recognized";
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ExecuteSql(connection, @"UPDATE acquaintances SET
recognition_attempts=recognition_attempts+1,
last_recognition_result=$result,
last_recognition_encounter_id=$encounter,
last_recognition_day=$day,
last_recognition_probability=$probability,
last_recognition_roll=$roll,
recognition_rule_version=$version,
updated_ts=$ts
WHERE observer_id=$observer AND subject_id=$subject
AND COALESCE(last_recognition_encounter_id,'')<>$encounter;",
                new Dictionary<string, object>
                {
                    ["result"] = result,
                    ["encounter"] = encounterId ?? "",
                    ["day"] = worldDay,
                    ["probability"] = probability,
                    ["roll"] = roll,
                    ["version"] = IdentityRecognitionRuleVersion,
                    ["ts"] = ts,
                    ["observer"] = observerId,
                    ["subject"] = subjectId
                });
            Dictionary<string, object> updated =
                ReadStoredAcquaintance(
                    connection, observerId, subjectId)
                ?? row;
            if (!ReadString(
                    updated, "last_recognition_encounter_id", "")
                .Equals(encounterId ?? "",
                    StringComparison.OrdinalIgnoreCase))
            {
                return IdentityRecognitionReceipt(
                    updated, "replayed",
                    ReadString(updated,
                        "last_recognition_result", "")
                        .Equals("recognized",
                            StringComparison.OrdinalIgnoreCase));
            }

            Dictionary<string, object> receipt =
                IdentityRecognitionReceipt(
                    updated, "completed", recognized);
            receipt["observerCharm"] = charm;
            receipt["residentLocalAuthority"] = residentLocalAuthority;
            receipt["subjectClanTier"] = tier;
            receipt["baseChance"] = baseChance;
            receipt["clanTierMultiplier"] = tierMultiplier;
            receipt["correlationId"] = correlationId ?? "";
            AddIdentityEvidence(
                connection, observerId, subjectId,
                recognized
                    ? "recognition_succeeded"
                    : "recognition_failed",
                "", ReadString(subject, "name", ""),
                "charm_clan_recognition_roll_v3",
                observerId, encounterId,
                recognized ? 1d : 0d, worldDay,
                receipt);
            if (recognized)
            {
                UpsertVerifiedIdentity(
                    connection, observer, subject,
                    "charm_clan_recognition_roll_v3",
                    observerId, worldDay, correlationId);
            }
            return receipt;
        }

        private static Dictionary<string, object>
            IdentityRecognitionReceipt(
                Dictionary<string, object> row,
                string status,
                bool recognized)
        {
            row = row ?? new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["status"] = status ?? "",
                ["recognized"] = recognized,
                ["result"] = ReadString(
                    row, "last_recognition_result", ""),
                ["encounterId"] = ReadString(
                    row, "last_recognition_encounter_id", ""),
                ["worldDay"] = ReadDouble(
                    row, "last_recognition_day", -1d),
                ["probability"] = ReadDouble(
                    row, "last_recognition_probability", 0d),
                ["roll"] = ReadDouble(
                    row, "last_recognition_roll", -1d),
                ["attempts"] = ReadInt(
                    row, "recognition_attempts", 0),
                ["ruleVersion"] = ReadInt(
                    row, "recognition_rule_version",
                    IdentityRecognitionRuleVersion)
            };
        }

        private static int IdentityRecognitionCharm(
            Dictionary<string, object> observer)
        {
            observer = observer
                ?? new Dictionary<string, object>();
            Dictionary<string, object> skills =
                ReadDictionary(observer, "skills")
                ?? new Dictionary<string, object>();
            return Math.Max(0, ReadInt(
                observer, "currentCharm",
                ReadInt(observer, "current_charm",
                    ReadInt(observer, "charm",
                        ReadInt(skills, "charm", 0)))));
        }

        private static double IdentityRecognitionTierMultiplier(
            int clanTier)
        {
            int tier = Math.Max(1, Math.Min(6, clanTier));
            if (tier <= 3)
                return 0.50d + 0.25d * (tier - 1);
            return 1d + (tier - 3) / 6d;
        }

        private static double StableIdentityRecognitionRoll(
            string campaignId,
            string observerId,
            string subjectId,
            string encounterId)
        {
            string seed = string.Join("|", new[]
            {
                "identity_recognition",
                IdentityRecognitionRuleVersion.ToString(
                    CultureInfo.InvariantCulture),
                campaignId ?? "default",
                observerId ?? "",
                subjectId ?? "",
                encounterId ?? ""
            });
            byte[] digest;
            using (SHA256 sha = SHA256.Create())
                digest = sha.ComputeHash(
                    Encoding.UTF8.GetBytes(seed));
            ulong value = 0UL;
            for (int i = 0; i < 8; i++)
                value = (value << 8) | digest[i];
            ulong mantissa = value >> 11;
            return mantissa / (double)(1UL << 53);
        }

        private static Dictionary<string, object> IdentityIntroductionApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string observerId = ReadFirstString(payload, "observerHeroStringId", "observerId");
            string subjectId = ReadFirstString(payload, "subjectHeroStringId", "subjectId");
            string introducerId = ReadFirstString(payload, "introducerHeroStringId", "introducerId");
            string claimedName = LimitText(ReadFirstString(payload, "claimedName", "name"), 80);
            string canonicalName = LimitText(ReadString(payload, "canonicalName", ""), 80);
            string kind = ReadString(payload, "kind", string.IsNullOrWhiteSpace(introducerId) || string.Equals(introducerId, subjectId, StringComparison.OrdinalIgnoreCase) ? "self" : "third_party");
            string encounterId = ReadFirstString(payload, "encounterId", "conversationSessionId", "eventId");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            string correlationId = EnsureCorrelationId(payload);
            if (string.IsNullOrWhiteSpace(observerId) || string.IsNullOrWhiteSpace(subjectId) || string.IsNullOrWhiteSpace(claimedName))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "observer, subject, and claimedName are required." };

            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                bool verified = false;
                string source = "self_introduction";
                if (string.Equals(kind, "third_party", StringComparison.OrdinalIgnoreCase))
                {
                    Dictionary<string, object> knowsObserver = ReadAcquaintance(connection, introducerId, observerId);
                    Dictionary<string, object> knowsSubject = ReadAcquaintance(connection, introducerId, subjectId);
                    if (!IdentityStateVerified(knowsObserver) || !IdentityStateVerified(knowsSubject))
                        return new Dictionary<string, object> { ["ok"] = false, ["error"] = "The introducer must have verified knowledge of both people." };
                    verified = true;
                    source = "trusted_third_party_introduction";
                }

                if (verified)
                {
                    Dictionary<string, object> observer = new Dictionary<string, object> { ["heroStringId"] = observerId };
                    Dictionary<string, object> subject = new Dictionary<string, object> { ["heroStringId"] = subjectId, ["name"] = FirstNonEmpty(canonicalName, claimedName) };
                    UpsertVerifiedIdentity(connection, observer, subject, source, introducerId, worldDay, correlationId);
                }
                else
                {
                    UpsertClaimedIdentity(connection, observerId, subjectId, canonicalName, claimedName, source, FirstNonEmpty(introducerId, subjectId), encounterId, worldDay, payload);
                }

                Dictionary<string, object> view = BuildIdentityView(ReadAcquaintance(connection, observerId, subjectId), payload);
                WriteAudit(campaignId, correlationId, "server", "identity", "identity.introduction", observerId, "", encounterId, "completed", 0, verified ? "Trusted introduction verified identity." : "Claimed identity recorded.", view);
                return new Dictionary<string, object> { ["ok"] = true, ["verified"] = verified, ["identityView"] = view };
            }
        }

        private static Dictionary<string, object> IdentityQueryApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", LatestCampaignId());
            string observerId = ReadFirstString(payload, "observerHeroStringId", "observerId");
            string subjectId = ReadFirstString(payload, "subjectHeroStringId", "subjectId");
            int limit = Math.Max(1, Math.Min(2000, ReadInt(payload, "limit", 500)));
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                List<string> clauses = new List<string>();
                Dictionary<string, object> args = new Dictionary<string, object> { ["limit"] = limit };
                if (!string.IsNullOrWhiteSpace(observerId)) { clauses.Add("observer_id=$observer"); args["observer"] = observerId; }
                if (!string.IsNullOrWhiteSpace(subjectId)) { clauses.Add("subject_id=$subject"); args["subject"] = subjectId; }
                string where = clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses);
                List<Dictionary<string, object>> rows;
                if (!string.IsNullOrWhiteSpace(observerId) && !string.IsNullOrWhiteSpace(subjectId))
                {
                    Dictionary<string, object> pair = ReadAcquaintance(connection, observerId, subjectId);
                    rows = pair == null
                        ? new List<Dictionary<string, object>>()
                        : new List<Dictionary<string, object>> { pair };
                }
                else
                {
                    rows = QuerySql(connection, "SELECT * FROM acquaintances" + where + " ORDER BY updated_ts DESC LIMIT $limit;", args);
                }
                List<Dictionary<string, object>> evidence = observerId.Length > 0 && subjectId.Length > 0
                    ? QuerySql(connection, "SELECT * FROM identity_evidence WHERE observer_id=$observer AND subject_id=$subject ORDER BY created_ts DESC LIMIT 200;", new Dictionary<string, object> { ["observer"] = observerId, ["subject"] = subjectId })
                    : new List<Dictionary<string, object>>();
                return new Dictionary<string, object> { ["ok"] = true, ["campaignId"] = campaignId, ["acquaintances"] = rows, ["evidence"] = evidence };
            }
        }

        private static Dictionary<string, object> IdentityResetApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (!ReadBool(payload, "testMode", false)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Identity reset is available only in beta test mode." };
            string campaignId = ReadString(payload, "campaignId", "default");
            string observerId = ReadFirstString(payload, "observerHeroStringId", "observerId");
            string subjectId = ReadFirstString(payload, "subjectHeroStringId", "subjectId");
            string correlationId = EnsureCorrelationId(payload);
            if (string.IsNullOrWhiteSpace(observerId) || string.IsNullOrWhiteSpace(subjectId)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "observer and subject are required." };
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                Dictionary<string, object> args = new Dictionary<string, object> { ["observer"] = observerId, ["subject"] = subjectId };
                ExecuteSql(connection, "DELETE FROM acquaintances WHERE observer_id=$observer AND subject_id=$subject;", args);
                ExecuteSql(connection, "DELETE FROM identity_evidence WHERE observer_id=$observer AND subject_id=$subject;", args);
            }
            WriteAudit(campaignId, correlationId, "server", "identity", "identity.reset", observerId, "", "", "completed", 0, "Identity knowledge reset for beta testing.", new Dictionary<string, object> { ["observerId"] = observerId, ["subjectId"] = subjectId });
            return new Dictionary<string, object> { ["ok"] = true, ["observerId"] = observerId, ["subjectId"] = subjectId };
        }

        private static Dictionary<string, object> IdentityDebugKnowEveryoneApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (!ReadBool(payload, "testMode", false)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Know-everyone is available only in beta test mode." };

            string campaignId = ReadString(payload, "campaignId", "default");
            string correlationId = EnsureCorrelationId(payload);
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            Dictionary<string, object> observer = ReadDictionary(payload, "observer") ?? new Dictionary<string, object>
            {
                ["heroStringId"] = ReadFirstString(payload, "observerHeroStringId", "observerId"),
                ["name"] = ReadString(payload, "observerName", "")
            };
            string observerId = IdentityHeroId(observer);
            if (string.IsNullOrWhiteSpace(observerId)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "A player observer is required." };

            List<Dictionary<string, object>> heroes = ReadDictionaryList(payload, "heroes")
                .Where(x => !string.IsNullOrWhiteSpace(IdentityHeroId(x))
                    && !string.Equals(IdentityHeroId(x), observerId, StringComparison.OrdinalIgnoreCase)
                    && ReadBool(x, "isAlive", true))
                .GroupBy(IdentityHeroId, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();
            int created = 0;
            int upgraded = 0;
            int unchanged = 0;

            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                ExecuteSql(connection, "BEGIN IMMEDIATE;");
                try
                {
                    foreach (Dictionary<string, object> hero in heroes)
                    {
                        string result = UpsertVerifiedIdentity(connection, observer, hero, "debug_known_everyone", observerId, worldDay, correlationId);
                        if (result == "created") created++;
                        else if (result == "upgraded") upgraded++;
                        else unchanged++;
                    }
                    ExecuteSql(connection, "COMMIT;");
                }
                catch
                {
                    try { ExecuteSql(connection, "ROLLBACK;"); } catch { }
                    throw;
                }
            }

            Dictionary<string, object> resultPayload = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["observerId"] = observerId,
                ["requested"] = heroes.Count,
                ["known"] = created + upgraded + unchanged,
                ["complete"] = created + upgraded + unchanged == heroes.Count,
                ["created"] = created,
                ["upgraded"] = upgraded,
                ["unchanged"] = unchanged
            };
            WriteAudit(campaignId, correlationId, "server", "identity", "identity.debug.know_everyone", observerId, "", "", "completed", 0, "Player debug identity knowledge populated.", resultPayload);
            return resultPayload;
        }

        private static Dictionary<string, object> ResolvePromptIdentity(Dictionary<string, object> payload, string observerId, string mode, string fallbackEncounterId)
        {
            payload = payload ?? new Dictionary<string, object>();
            Dictionary<string, object> suppliedSubject =
                ReadDictionary(payload, "playerIdentity");
            Dictionary<string, object> subject = suppliedSubject == null
                ? new Dictionary<string, object>
                {
                    ["heroStringId"] = ReadFirstString(payload,
                        "playerHeroStringId", "mainHeroStringId", "playerId"),
                    ["name"] = ReadString(payload, "playerName", "Player")
                }
                : new Dictionary<string, object>(suppliedSubject,
                    StringComparer.OrdinalIgnoreCase);
            if (!subject.ContainsKey("isFemale")
                && payload.ContainsKey("playerIsFemale"))
                subject["isFemale"] = ReadBool(
                    payload, "playerIsFemale", false);
            if (!subject.ContainsKey("sex")
                && subject.ContainsKey("isFemale"))
                subject["sex"] = ReadBool(
                    subject, "isFemale", false)
                    ? "female"
                    : "male";
            string subjectId = IdentityHeroId(subject);
            if (string.IsNullOrWhiteSpace(observerId) || string.IsNullOrWhiteSpace(subjectId))
            {
                return BuildIdentityView(null,
                    new Dictionary<string, object>
                    {
                        ["mode"] = mode ?? "",
                        ["canonicalName"] = ReadString(
                            subject, "name", ""),
                        ["subject"] = subject
                    });
            }

            string encounterId = FirstNonEmpty(
                ReadFirstString(payload, "conversationSessionId", "sessionId", "eventId", "socialEventId"),
                fallbackEncounterId,
                "encounter_" + observerId + "_" + subjectId);
            Dictionary<string, object> encounter = new Dictionary<string, object>
            {
                ["campaignId"] = ReadString(payload, "campaignId", "default"),
                ["correlationId"] = EnsureCorrelationId(payload),
                ["observerHeroStringId"] = observerId,
                ["subjectHeroStringId"] = subjectId,
                ["encounterId"] = encounterId,
                ["mode"] = mode ?? "dialogue",
                ["worldDay"] = ReadDouble(payload, "worldDay", 0d),
                ["playerText"] = ReadFirstString(payload, "playerText", "text", "message"),
                ["canonicalName"] = ReadString(subject, "name", ReadString(payload, "playerName", "Player")),
                ["isPrisoner"] = ReadBool(subject, "isPrisoner", false),
                ["appearance"] = ReadDictionary(subject, "appearance") ?? new Dictionary<string, object>(),
                ["observer"] = ReadDictionary(payload, "speaker") ?? ReadDictionary(payload, "hero") ?? new Dictionary<string, object> { ["heroStringId"] = observerId },
                ["subject"] = subject
            };
            foreach (string key in new[]
            {
                "transcript", "groupTranscript", "attendees", "participantProfiles", "sceneParticipants",
                "conversationSceneState", "playerName", "mainHeroName",
                "sceneContext", "selectedContextPulls", "contextBundles",
                "nativePoliticalContext", "courtLifeContext", "conversationMode", "timelineId"
            })
            {
                if (payload.ContainsKey(key)) encounter[key] = payload[key];
            }
            Dictionary<string, object> response = IdentityEncounterApi(encounter);
            return ReadDictionary(response, "identityView") ?? BuildIdentityView(null, encounter);
        }

        private static List<Dictionary<string, object>>
            EnsureObserverParticipantIdentityViews(
                string campaignId,
                Dictionary<string, object> payload,
                string observerId)
        {
            payload = payload
                ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> existing =
                ReadDictionaryList(
                    payload,
                    "observerParticipantIdentityViews");
            if (existing.Count > 0
                && existing.All(row =>
                    ReadFirstString(
                            row,
                            "observerHeroStringId",
                            "observerId")
                        .Equals(
                            observerId ?? "",
                            StringComparison.OrdinalIgnoreCase)))
                return existing;

            List<Dictionary<string, object>> profiles =
                MergedInteractionParticipantProfiles(payload);
            string playerId = ReadFirstString(
                payload,
                "playerHeroStringId",
                "mainHeroStringId",
                "playerId");
            Dictionary<string, object> observer =
                profiles.FirstOrDefault(profile =>
                    CharacterIdFrom(profile).Equals(
                        observerId ?? "",
                        StringComparison.OrdinalIgnoreCase))
                ?? ReadDictionary(payload, "speaker")
                ?? ReadDictionary(payload, "hero")
                ?? new Dictionary<string, object>
                {
                    ["heroStringId"] = observerId ?? ""
                };
            string encounterId = FirstNonEmpty(
                GroupConversationSessionId(payload),
                ReadFirstString(
                    payload,
                    "conversationSessionId",
                    "sessionId",
                    "eventId"),
                "group_identity_"
                    + EnsureCorrelationId(payload));
            Dictionary<string, object> nativeBase =
                ReadDictionary(
                    payload,
                    "nativePoliticalContext")
                ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> views =
                new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> subject in profiles)
            {
                string subjectId = CharacterIdFrom(subject);
                if (string.IsNullOrWhiteSpace(subjectId)
                    || subjectId.Equals(
                        observerId ?? "",
                        StringComparison.OrdinalIgnoreCase)
                    || subjectId.Equals(
                        playerId,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                Dictionary<string, object> pairNative =
                    new Dictionary<string, object>(
                        nativeBase,
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["observer"] = observer,
                        ["subject"] = subject,
                        // The always-on diplomacy packet is player-kingdom
                        // state. Do not misapply it to another NPC subject.
                        ["diplomacy"] =
                            new Dictionary<string, object>()
                    };
                Dictionary<string, object> response =
                    IdentityEncounterApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] =
                                campaignId ?? "default",
                            ["correlationId"] =
                                EnsureCorrelationId(payload),
                            ["observerHeroStringId"] =
                                observerId ?? "",
                            ["subjectHeroStringId"] =
                                subjectId,
                            ["observer"] = observer,
                            ["subject"] = subject,
                            ["canonicalName"] =
                                ReadString(subject, "name", ""),
                            ["encounterId"] = encounterId,
                            ["worldDay"] = ReadDouble(
                                payload, "worldDay", 0d),
                            ["mode"] = ReadString(
                                payload,
                                "mode",
                                "group_conversation"),
                            ["nativePoliticalContext"] =
                                pairNative,
                            ["appearance"] =
                                ReadDictionary(
                                    subject,
                                    "appearance")
                                ?? new Dictionary<string, object>()
                        });
                views.Add(
                    new Dictionary<string, object>
                    {
                        ["observerHeroStringId"] =
                            observerId ?? "",
                        ["subjectHeroStringId"] =
                            subjectId,
                        ["identityView"] =
                            ReadDictionary(
                                response,
                                "identityView")
                            ?? new Dictionary<string, object>(),
                        ["recognition"] =
                            ReadDictionary(
                                response,
                                "recognition")
                            ?? new Dictionary<string, object>()
                    });
            }
            payload["observerParticipantIdentityViews"] = views;
            return views;
        }

        private static Dictionary<string, object>
            ObserverParticipantIdentityView(
                Dictionary<string, object> payload,
                string subjectId)
        {
            return ReadDictionaryList(
                    payload
                        ?? new Dictionary<string, object>(),
                    "observerParticipantIdentityViews")
                .Where(row => ReadFirstString(
                        row,
                        "subjectHeroStringId",
                        "subjectId")
                    .Equals(
                        subjectId ?? "",
                        StringComparison.OrdinalIgnoreCase))
                .Select(row => ReadDictionary(
                        row,
                        "identityView")
                    ?? new Dictionary<string, object>())
                .FirstOrDefault()
                ?? new Dictionary<string, object>();
        }

        private static string ObserverSafeParticipantName(
            Dictionary<string, object> payload,
            string observerId,
            string subjectId,
            string canonicalFallback)
        {
            if (string.IsNullOrWhiteSpace(subjectId)
                || subjectId.Equals(
                    observerId ?? "",
                    StringComparison.OrdinalIgnoreCase))
                return FirstNonEmpty(
                    canonicalFallback,
                    subjectId,
                    "the current speaker");
            Dictionary<string, object> view =
                ObserverParticipantIdentityView(payload, subjectId);
            return FirstNonEmpty(
                ReadString(view, "usableName", ""),
                ReadString(view, "safeLabel", ""),
                "an unidentified participant");
        }

        private static string FormatEventLinesForObserver(
            List<Dictionary<string, object>> eventLines,
            Dictionary<string, object> payload,
            string observerId,
            Dictionary<string, object> playerIdentityView)
        {
            if (eventLines == null || eventLines.Count == 0)
                return "none";
            string playerId = ReadFirstString(
                payload
                    ?? new Dictionary<string, object>(),
                "playerHeroStringId",
                "mainHeroStringId",
                "playerId");
            StringBuilder builder = new StringBuilder();
            foreach (Dictionary<string, object> line in eventLines)
            {
                string text = ReadString(line, "text", "");
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                string speakerId = ReadFirstString(
                    line,
                    "speakerHeroStringId",
                    "speaker_id",
                    "heroStringId");
                string role = ReadString(line, "role", "");
                string canonical = ReadString(
                    line,
                    "speaker",
                    "Event");
                string attribution = canonical;
                bool identified = true;
                if (role.Equals(
                        "player",
                        StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(playerId)
                        && speakerId.Equals(
                            playerId,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    attribution = FirstNonEmpty(
                        ReadString(
                            playerIdentityView
                                ?? new Dictionary<string, object>(),
                            "usableName",
                            ""),
                        ReadString(
                            playerIdentityView
                                ?? new Dictionary<string, object>(),
                            "safeLabel",
                            ""),
                        "the player");
                    identified = ReadBool(
                        playerIdentityView
                            ?? new Dictionary<string, object>(),
                        "canonicalNameAllowed",
                        false);
                }
                else if (!string.IsNullOrWhiteSpace(speakerId)
                    && !speakerId.Equals(
                        observerId ?? "",
                        StringComparison.OrdinalIgnoreCase))
                {
                    Dictionary<string, object> pairView =
                        ObserverParticipantIdentityView(
                            payload,
                            speakerId);
                    attribution = ObserverSafeParticipantName(
                        payload,
                        observerId,
                        speakerId,
                        canonical);
                    identified = ReadBool(
                        pairView,
                        "canonicalNameAllowed",
                        false);
                }
                if (!string.IsNullOrWhiteSpace(speakerId)
                    && (identified
                        || speakerId.Equals(
                            observerId ?? "",
                            StringComparison.OrdinalIgnoreCase)))
                    attribution += " [id=" + speakerId + "]";
                if (!string.IsNullOrWhiteSpace(role))
                    attribution += " [role=" + role + "]";
                builder.AppendLine(
                    "- " + attribution + ": " + text);
            }
            return builder.Length == 0
                ? "none"
                : builder.ToString().TrimEnd();
        }

        private static void RecordPresentNpcSelfIntroduction(
            string campaignId,
            Dictionary<string, object> payload,
            string speakerId,
            string speakerName,
            string reply,
            string encounterId)
        {
            string introduced = ExtractSelfIntroducedName(
                reply ?? "",
                false);
            if (string.IsNullOrWhiteSpace(introduced)
                || !NormalizeIdentityName(introduced).Equals(
                    NormalizeIdentityName(speakerName),
                    StringComparison.OrdinalIgnoreCase))
                return;
            List<Dictionary<string, object>> profiles =
                MergedInteractionParticipantProfiles(payload);
            string playerId = ReadFirstString(
                payload,
                "playerHeroStringId",
                "mainHeroStringId",
                "playerId");
            List<string> observers = profiles
                .Select(CharacterIdFrom)
                .Concat(new[] { playerId })
                .Where(id => !string.IsNullOrWhiteSpace(id)
                    && !id.Equals(
                        speakerId ?? "",
                        StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Dictionary<string, object> subject =
                profiles.FirstOrDefault(profile =>
                    CharacterIdFrom(profile).Equals(
                        speakerId ?? "",
                        StringComparison.OrdinalIgnoreCase))
                ?? new Dictionary<string, object>
                {
                    ["heroStringId"] = speakerId ?? "",
                    ["name"] = speakerName ?? ""
                };
            foreach (string observerId in observers)
            {
                IdentityEncounterApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId ?? "default",
                        ["correlationId"] =
                            EnsureCorrelationId(payload),
                        ["observerHeroStringId"] = observerId,
                        ["subjectHeroStringId"] =
                            speakerId ?? "",
                        ["observer"] =
                            profiles.FirstOrDefault(profile =>
                                CharacterIdFrom(profile).Equals(
                                    observerId,
                                    StringComparison.OrdinalIgnoreCase))
                            ?? new Dictionary<string, object>
                            {
                                ["heroStringId"] = observerId
                            },
                        ["subject"] = subject,
                        ["canonicalName"] =
                            speakerName ?? "",
                        ["encounterId"] =
                            encounterId ?? "",
                        ["worldDay"] = ReadDouble(
                            payload,
                            "worldDay",
                            0d),
                        ["mode"] = ReadString(
                            payload,
                            "mode",
                            "group_conversation"),
                        ["playerText"] = reply ?? ""
                    });
            }
        }

        private static string BuildIdentityPromptBlock(Dictionary<string, object> identityView)
        {
            identityView = identityView ?? new Dictionary<string, object>();
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("IDENTITY KNOWLEDGE - OBSERVER-SPECIFIC AND AUTHORITATIVE");
            builder.AppendLine("Identity state: " + ReadString(identityView, "identityState", "encountered_unknown"));
            builder.AppendLine("Usable name or label: " + ReadString(identityView, "usableName", "the stranger"));
            if (ReadBool(identityView, "subjectSexKnown", false))
            {
                string subjectSex = ReadString(
                    identityView, "subjectSex", "unknown");
                builder.AppendLine(
                    "Subject visible sex: " + subjectSex
                    + " (authoritative current native fact, independent of whether the observer knows the subject's name or rank)");
                builder.AppendLine(
                    "VISIBLE-SEX ADDRESS RULE: never describe or directly address this person as the opposite sex. "
                    + "This fact establishes sex only; it does not reveal or prove a name, noble title, clan, occupation, or political office.");
            }
            if (!ReadBool(identityView, "knowsIdentity", false)
                && !ReadBool(identityView, "canonicalNameAllowed", false))
            {
                builder.AppendLine(
                    "UNKNOWN-IDENTITY ADDRESS STYLE: speak to this person as 'you' by default. "
                    + "The usable descriptive label is optional, may appear at most once in a reply, "
                    + "is not a personal name, and must not become a repeated epithet or a prerequisite for ordinary conversation.");
            }
            string claimed = ReadString(identityView, "claimedName", "");
            if (!string.IsNullOrWhiteSpace(claimed))
            {
                string usable = ReadString(identityView, "usableName", "");
                bool canonicalVerified = ReadBool(
                    identityView, "canonicalNameAllowed", false);
                bool conflictsWithVerifiedIdentity = canonicalVerified
                    && !NormalizeIdentityName(claimed).Equals(
                        NormalizeIdentityName(usable),
                        StringComparison.OrdinalIgnoreCase);
                if (conflictsWithVerifiedIdentity)
                {
                    builder.AppendLine(
                        "Earlier conflicting claimed name: " + claimed
                        + ". This does not replace the verified identity "
                        + usable + ". Never address the person as the earlier "
                        + "claim or deny their verified identity because of it; "
                        + "mention it only when explicitly discussing that old claim.");
                }
                else
                {
                    builder.AppendLine(
                        "Claimed name: " + claimed
                        + " (not independently verified unless this exact name "
                        + "is also the verified usable identity)");
                    builder.AppendLine(
                        "The preceding Claimed name field is the exact text of "
                        + "the name they supplied. The usable stranger label is "
                        + "only a description of uncertainty and is not part of "
                        + "that name. Never say they claimed the whole "
                        + "descriptive label as their name.");
                }
            }
            builder.AppendLine(ReadString(identityView, "instruction", "Do not use a canonical name that the observer does not know."));
            Dictionary<string, object> authority =
                ReadDictionary(identityView, "authorityView")
                ?? new Dictionary<string, object>();
            builder.AppendLine(
                "PUBLIC IDENTITY AND POLITICAL AUTHORITY - AUTHORITATIVE");
            builder.AppendLine(
                "Authority source: "
                + ReadString(authority, "source", "none"));
            builder.AppendLine(
                "Recognized current roles: "
                + (ReadStringList(authority, "recognizedRoles").Count == 0
                    ? "none"
                    : string.Join(", ",
                        ReadStringList(
                            authority, "recognizedRoles"))));
            builder.AppendLine(
                "PUBLIC ROLES ARE CUMULATIVE, NOT MUTUALLY EXCLUSIVE. Every "
                + "role in the recognized-current-roles list is true at the "
                + "same time. The primary role is only the most specific title. "
                + "For example, a king who is also listed as lord remains both "
                + "king and lord; never say that becoming ruler makes the person "
                + "'not a lord' or erases any other listed office.");
            builder.AppendLine(
                "Subject primary current role: "
                + ReadString(
                    authority,
                    "subjectPrimaryRole",
                    "identity_unverified"));
            builder.AppendLine(
                "Observer primary current role: "
                + ReadString(
                    authority,
                    "observerPrimaryRole",
                    "commoner_or_unaffiliated"));
            builder.AppendLine(
                "Observer-relative authority relationship: "
                + ReadString(
                    authority,
                    "authorityRelationship",
                    "identity_unverified"));
            builder.AppendLine(
                "DIRECT AUTHORITY-QUESTION RULE: when the latest player line "
                + "asks who they are politically, what office or role they "
                + "hold, whether they are this observer's sovereign, whose "
                + "sovereign they are, who owns or governs the current "
                + "settlement, or what the current diplomacy is, answer every "
                + "requested point directly from this authoritative block. "
                + "A terse or hostile character may answer bluntly, but must "
                + "not evade, redirect, or omit the answer. State 'none', "
                + "'not your sovereign', 'no verified governorship', or "
                + "equivalent grounded wording when the authoritative value "
                + "is absent or false; never invent an office to fill the gap.");
            if (ReadBool(
                    authority, "realmSovereignKnown", false))
            {
                builder.AppendLine(
                    "The observer knows that "
                    + ReadString(identityView, "usableName", "this person")
                    + " is the current sovereign of "
                    + FirstNonEmpty(
                        ReadString(
                            authority,
                            "subjectKingdomName", ""),
                        "their current realm")
                    + ". This is a verified public office, not a claim. "
                    + "Use the public realm name in dialogue; never speak a "
                    + "raw native StringId such as `new_kingdom`.");
                builder.AppendLine(
                    "Valid formal addresses: "
                    + string.Join(", ", ReadStringList(
                        authority, "validFormalAddresses"))
                    + ". Personal identity knowledge is "
                    + (ReadBool(authority, "personalIdentityKnown", false)
                        ? "known; supplied names may be used subject to etiquette."
                        : "not known; do not reveal or use the canonical name."));
            }
            if (ReadBool(
                    authority,
                    "subjectIsObserverSovereign", false))
            {
                builder.AppendLine(
                    "ALLEGIANCE RELATION: this person is the observer's own "
                    + "current sovereign, and the observer is their subject. "
                    + "The observer may be loyal, ambitious, manipulative, "
                    + "resentful, oppositional, or deliberately defiant, but "
                    + "must not accidentally frame their own sovereign as a "
                    + "foreign equal, ordinary visitor, or unaccountable stranger.");
            }
            else if (ReadBool(
                    authority,
                    "observerIsSubjectSovereign", false))
            {
                builder.AppendLine(
                    "ALLEGIANCE RELATION: the observer is this known person's "
                    + "own current sovereign. The observer knows the person is "
                    + "their subject.");
            }
            if (ReadBool(
                    authority,
                    "currentSettlementOwnerKnown", false))
            {
                builder.AppendLine(
                    "The observer knows that "
                    + ReadString(identityView, "usableName", "this person")
                    + "'s clan currently owns "
                    + ReadString(
                        authority,
                        "currentSettlementName",
                        "the current settlement")
                    + " (`"
                    + ReadString(
                        authority,
                        "currentSettlementId", "")
                    + "`). This is current native ownership, not a claim.");
            }
            if (!string.IsNullOrWhiteSpace(
                    ReadString(
                        authority,
                        "currentSettlementId", "")))
            {
                builder.AppendLine(
                    "CURRENT CONVERSATION SETTLEMENT: "
                    + ReadString(
                        authority,
                        "currentSettlementName",
                        "the current settlement")
                    + " (`"
                    + ReadString(
                        authority,
                        "currentSettlementId", "")
                    + "`). This is the active local venue for statements such "
                    + "as 'this city', 'here', 'this hall', and scene headings. "
                    + "An observer's home, clan seat, or native map position "
                    + "elsewhere is background only and must not replace the "
                    + "current conversation settlement.");
                builder.AppendLine(
                    "CURRENT LOCAL AUTHORITY: "
                    + ReadString(
                        authority,
                        "currentSettlementName",
                        "the current settlement")
                    + " is held by clan "
                    + FirstNonEmpty(
                        ReadString(
                            authority,
                            "currentSettlementOwnerClanName", ""),
                        ReadString(
                            authority,
                            "currentSettlementOwnerClanId", ""),
                        "unknown")
                    + "; governor hero id is "
                    + FirstNonEmpty(
                        ReadString(
                            authority,
                            "currentSettlementGovernorHeroId", ""),
                        "none")
                    + ". The observer may say 'my city', 'my castle', or "
                    + "'my hall' as a personal authority claim only when the "
                    + "native role facts establish owner-clan membership, "
                    + "governorship, or genuine hosting authority. Civic wording "
                    + "such as 'our city' must not be presented as personal ownership.");
            }
            if (ReadBool(
                    authority,
                    "currentDiplomacyKnown", false))
            {
                List<string> enemyNames = ReadStringList(
                    authority,
                    "currentEnemyKingdomNames");
                builder.AppendLine(
                    "VOLATILE CURRENT DIPLOMACY: the subject's kingdom is at "
                    + "war with "
                    + ReadInt(
                        authority,
                        "currentEnemyKingdomCount", 0)
                    + " distinct enemy kingdom(s)"
                    + (enemyNames.Count == 0
                        ? ""
                        : " (" + string.Join(", ", enemyNames) + ")")
                    + ". This current native value supersedes older snapshots. "
                    + "Do not volunteer war counts during unrelated small talk, "
                    + "and never reinterpret hostile clans or minor factions as nations.");
            }
            builder.AppendLine(
                ReadString(
                    authority, "instruction",
                    "Do not infer unsupported political office."));
            builder.AppendLine("Mechanical hero IDs and resolver names are server plumbing, not facts this NPC knows. Never reveal an unknown canonical identity from them.");
            builder.AppendLine(BuildKnownSocialStandingPromptBlock(
                ReadDictionary(identityView, "socialStandingView") ?? new Dictionary<string, object>()));
            return builder.ToString().TrimEnd();
        }

        private static string SanitizePromptForIdentity(string prompt, string latestPlayerText, string canonicalName, Dictionary<string, object> identityView)
        {
            prompt = prompt ?? string.Empty;
            identityView = identityView ?? new Dictionary<string, object>();
            bool canonicalNameAllowed =
                ReadBool(identityView, "canonicalNameAllowed", false);
            string usableName = ReadString(
                identityView, "usableName", canonicalName ?? "");
            string conflictingClaim = ReadString(
                identityView, "claimedName", "");
            if (canonicalNameAllowed
                && !string.IsNullOrWhiteSpace(usableName)
                && !string.IsNullOrWhiteSpace(conflictingClaim)
                && !NormalizeIdentityName(conflictingClaim).Equals(
                    NormalizeIdentityName(usableName),
                    StringComparison.OrdinalIgnoreCase))
            {
                // Keep one authoritative, attributed record of the old claim in
                // the identity block, but stop stale state, summaries, and
                // transcripts from repeating it as the person's current name.
                // The newest player line remains verbatim evidence.
                string verifiedNonce = Guid.NewGuid().ToString("N");
                string identityMarker =
                    "__REIGN_VERIFIED_IDENTITY_BLOCK_" + verifiedNonce + "__";
                string latestMarker =
                    "__REIGN_LATEST_PLAYER_TEXT_" + verifiedNonce + "__";
                string identityBlock =
                    BuildIdentityPromptBlock(identityView);
                bool protectedIdentity =
                    !string.IsNullOrWhiteSpace(identityBlock)
                    && prompt.Contains(identityBlock);
                bool protectedVerifiedLatest =
                    !string.IsNullOrWhiteSpace(latestPlayerText)
                    && prompt.Contains(latestPlayerText);
                if (protectedIdentity)
                    prompt = prompt.Replace(identityBlock, identityMarker);
                if (protectedVerifiedLatest)
                    prompt = prompt.Replace(latestPlayerText, latestMarker);
                prompt = Regex.Replace(
                    prompt,
                    @"(?<![\p{L}\p{N}])"
                        + Regex.Escape(conflictingClaim)
                        + @"(?![\p{L}\p{N}])",
                    usableName,
                    RegexOptions.IgnoreCase
                        | RegexOptions.CultureInvariant);
                if (protectedVerifiedLatest)
                    prompt = prompt.Replace(latestMarker, latestPlayerText);
                if (protectedIdentity)
                    prompt = prompt.Replace(identityMarker, identityBlock);
                return ReplaceVerifiedIdentityLabel(
                    prompt, latestPlayerText,
                    identityView, usableName);
            }
            if (canonicalNameAllowed)
                return ReplaceVerifiedIdentityLabel(
                    prompt, latestPlayerText,
                    identityView, usableName);
            if (string.IsNullOrWhiteSpace(canonicalName))
                return prompt;
            string replacement = ReadString(identityView, "usableName", ReadString(identityView, "safeLabel", "the stranger"));
            if (string.Equals(NormalizeIdentityName(replacement), NormalizeIdentityName(canonicalName), StringComparison.OrdinalIgnoreCase)) return prompt;
            string claimedName = ReadString(identityView, "claimedName", "");
            // A name the observer actually heard is safe to repeat as an unverified
            // claim. Replacing it in the final reply changed correct model wording
            // such as "your name was Rhovarion" into the false assertion that the
            // player had spoken the entire descriptive stranger label as a name.
            if (!string.IsNullOrWhiteSpace(claimedName)
                && NormalizeIdentityName(claimedName).Equals(NormalizeIdentityName(canonicalName), StringComparison.OrdinalIgnoreCase))
            {
                return prompt;
            }
            string nonce = Guid.NewGuid().ToString("N");
            string marker = "__REIGN_LATEST_PLAYER_TEXT_" + nonce + "__";
            string replacementMarker = "__REIGN_IDENTITY_LABEL_" + nonce + "__";
            string claimMarker = "__REIGN_IDENTITY_CLAIM_" + nonce + "__";
            bool protectedLatest = !string.IsNullOrWhiteSpace(latestPlayerText) && prompt.Contains(latestPlayerText);
            if (protectedLatest) prompt = prompt.Replace(latestPlayerText, marker);
            // The observer-safe label can itself contain the unverified claimed name
            // (for example, "the stranger claiming to be Rhovarion"). Protect that
            // already-sanitized label before replacing otherwise leaked canonical-name
            // occurrences, or each pass recursively wraps it as "the stranger claiming
            // to be the stranger claiming to be Rhovarion".
            bool protectedReplacement = !string.IsNullOrWhiteSpace(replacement)
                && prompt.IndexOf(replacement, StringComparison.OrdinalIgnoreCase) >= 0;
            if (protectedReplacement)
            {
                prompt = Regex.Replace(prompt, Regex.Escape(replacement), replacementMarker,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            string claimPrefix = string.IsNullOrWhiteSpace(claimedName) ? "" : "Claimed name: " + claimedName;
            bool protectedClaim = !string.IsNullOrWhiteSpace(claimPrefix) && prompt.Contains(claimPrefix);
            if (protectedClaim) prompt = prompt.Replace(claimPrefix, claimMarker);
            prompt = Regex.Replace(prompt, @"(?<![\p{L}\p{N}])" + Regex.Escape(canonicalName) + @"(?![\p{L}\p{N}])", replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (protectedClaim) prompt = prompt.Replace(claimMarker, claimPrefix);
            if (protectedReplacement) prompt = prompt.Replace(replacementMarker, replacement);
            if (protectedLatest) prompt = prompt.Replace(marker, latestPlayerText);
            return prompt;
        }

        private static string ReplaceVerifiedIdentityLabel(
            string prompt,
            string latestPlayerText,
            Dictionary<string, object> identityView,
            string usableName)
        {
            string safeLabel = ReadString(
                identityView, "safeLabel", "");
            if (string.IsNullOrWhiteSpace(prompt)
                || string.IsNullOrWhiteSpace(safeLabel)
                || string.IsNullOrWhiteSpace(usableName)
                || NormalizeIdentityName(safeLabel).Equals(
                    NormalizeIdentityName(usableName),
                    StringComparison.OrdinalIgnoreCase))
                return prompt ?? string.Empty;

            string nonce = Guid.NewGuid().ToString("N");
            string identityBlock =
                BuildIdentityPromptBlock(identityView);
            string identityMarker =
                "__REIGN_VERIFIED_IDENTITY_" + nonce + "__";
            string latestMarker =
                "__REIGN_VERIFIED_LATEST_" + nonce + "__";
            bool protectedIdentity =
                !string.IsNullOrWhiteSpace(identityBlock)
                && prompt.Contains(identityBlock);
            bool protectedLatest =
                !string.IsNullOrWhiteSpace(latestPlayerText)
                && prompt.Contains(latestPlayerText);
            if (protectedIdentity)
                prompt = prompt.Replace(
                    identityBlock, identityMarker);
            if (protectedLatest)
                prompt = prompt.Replace(
                    latestPlayerText, latestMarker);
            prompt = Regex.Replace(
                prompt,
                @"(?<![\p{L}\p{N}])"
                    + Regex.Escape(safeLabel)
                    + @"(?![\p{L}\p{N}])",
                usableName,
                RegexOptions.IgnoreCase
                    | RegexOptions.CultureInvariant);
            if (protectedLatest)
                prompt = prompt.Replace(
                    latestMarker, latestPlayerText);
            if (protectedIdentity)
                prompt = prompt.Replace(
                    identityMarker, identityBlock);
            return prompt;
        }

        private static List<Dictionary<string, object>> ApplyIdentityIntroductionWrites(string campaignId, Dictionary<string, object> payload, Dictionary<string, object> parsed, string encounterId)
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            if (parsed == null) return results;
            List<Dictionary<string, object>> writes = ReadDictionaryList(parsed, "identityIntroductions").Concat(ReadDictionaryList(parsed, "identity_introductions")).Take(4).ToList();
            if (writes.Count == 0) return results;
            HashSet<string> present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in ReadStringList(payload, "activeHeroIds")) present.Add(id);
            foreach (Dictionary<string, object> attendee in ReadDictionaryList(payload, "attendees")) present.Add(CharacterIdFrom(attendee));
            present.Add(ReadFirstString(payload, "speakerHeroStringId", "heroStringId"));
            present.Add(ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId"));
            present.RemoveWhere(string.IsNullOrWhiteSpace);
            foreach (Dictionary<string, object> write in writes)
            {
                string observer = ReadFirstString(write, "observerHeroStringId", "observerId");
                string subject = ReadFirstString(write, "subjectHeroStringId", "subjectId");
                string introducer = ReadFirstString(write, "introducerHeroStringId", "introducerId");
                string name = ReadFirstString(write, "claimedName", "name");
                if (!present.Contains(observer) || !present.Contains(subject) || !present.Contains(introducer) || string.IsNullOrWhiteSpace(name))
                {
                    results.Add(new Dictionary<string, object> { ["ok"] = false, ["error"] = "Introduction rejected because observer, subject, introducer, or name was not present in the scene.", ["write"] = write });
                    continue;
                }
                Dictionary<string, object> request = new Dictionary<string, object>(write, StringComparer.OrdinalIgnoreCase)
                {
                    ["campaignId"] = campaignId,
                    ["observerHeroStringId"] = observer,
                    ["subjectHeroStringId"] = subject,
                    ["introducerHeroStringId"] = introducer,
                    ["claimedName"] = name,
                    ["kind"] = "third_party",
                    ["encounterId"] = encounterId,
                    ["worldDay"] = ReadDouble(payload, "worldDay", 0d),
                    ["correlationId"] = EnsureCorrelationId(payload)
                };
                results.Add(IdentityIntroductionApi(request));
            }
            return results;
        }

        private static string UpsertVerifiedIdentity(ReignDbConnection connection, Dictionary<string, object> observer, Dictionary<string, object> subject, string source, string sourceEntityId, double worldDay, string correlationId)
        {
            string observerId = IdentityHeroId(observer), subjectId = IdentityHeroId(subject), canonicalName = ReadString(subject, "name", "");
            if (string.IsNullOrWhiteSpace(observerId) || string.IsNullOrWhiteSpace(subjectId) || string.Equals(observerId, subjectId, StringComparison.OrdinalIgnoreCase)) return "unchanged";
            Dictionary<string, object> existing = ReadStoredAcquaintance(connection, observerId, subjectId);
            string result = existing == null ? "created" : IdentityStateVerified(existing) ? "unchanged" : "upgraded";
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<string> aliases = existing == null ? new List<string>() : TextListFromJson(ReadString(existing, "aliases_json", "[]"));
            string claimed = existing == null ? "" : ReadString(existing, "claimed_name", "");
            if (!string.IsNullOrWhiteSpace(claimed) && !aliases.Contains(claimed, StringComparer.OrdinalIgnoreCase)) aliases.Add(claimed);
            ExecuteSql(connection, @"INSERT INTO acquaintances(observer_id,subject_id,identity_state,canonical_name,claimed_name,aliases_json,verification_source,source_entity_id,confidence,first_met_day,last_met_day,updated_ts)
VALUES($observer,$subject,'verified',$canonical,$claimed,$aliases,$source,$sourceEntity,1,$day,$day,$ts)
ON CONFLICT(observer_id,subject_id) DO UPDATE SET identity_state='verified',canonical_name=CASE WHEN excluded.canonical_name<>'' THEN excluded.canonical_name ELSE acquaintances.canonical_name END,aliases_json=excluded.aliases_json,verification_source=excluded.verification_source,source_entity_id=excluded.source_entity_id,confidence=1,updated_ts=excluded.updated_ts;",
                new Dictionary<string, object> { ["observer"] = observerId, ["subject"] = subjectId, ["canonical"] = canonicalName, ["claimed"] = claimed, ["aliases"] = Json.Serialize(aliases), ["source"] = source ?? "", ["sourceEntity"] = sourceEntityId ?? "", ["day"] = worldDay, ["ts"] = ts });
            if (result != "unchanged") AddIdentityEvidence(connection, observerId, subjectId, "verified", claimed, canonicalName, source, sourceEntityId, "", 1d, worldDay, new Dictionary<string, object> { ["correlationId"] = correlationId ?? "" });
            return result;
        }

        private static void UpsertClaimedIdentity(ReignDbConnection connection, string observerId, string subjectId, string canonicalName, string claimedName, string source, string sourceEntityId, string encounterId, double worldDay, Dictionary<string, object> payload)
        {
            Dictionary<string, object> existing = ReadAcquaintance(connection, observerId, subjectId);
            if (IdentityStateVerified(existing))
            {
                string knownCanonical = ReadString(existing, "canonical_name", canonicalName);
                if (!string.Equals(NormalizeIdentityName(knownCanonical), NormalizeIdentityName(claimedName), StringComparison.OrdinalIgnoreCase))
                {
                    AddIdentityEvidence(connection, observerId, subjectId, "conflicting_claim", claimedName, knownCanonical, source, sourceEntityId, encounterId, 0.25d, worldDay, payload);
                }
                return;
            }
            List<string> aliases = existing == null ? new List<string>() : TextListFromJson(ReadString(existing, "aliases_json", "[]"));
            string prior = existing == null ? "" : ReadString(existing, "claimed_name", "");
            if (!string.IsNullOrWhiteSpace(prior) && !aliases.Contains(prior, StringComparer.OrdinalIgnoreCase)) aliases.Add(prior);
            if (!aliases.Contains(claimedName, StringComparer.OrdinalIgnoreCase)) aliases.Add(claimedName);
            aliases = PruneAmbiguousAddressAliases(connection, observerId, subjectId, aliases, claimedName);
            string state = aliases.Any(alias => !NormalizeIdentityName(alias).Equals(NormalizeIdentityName(claimedName), StringComparison.OrdinalIgnoreCase)) ? "disputed" : "claimed";
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ExecuteSql(connection, @"INSERT INTO acquaintances(observer_id,subject_id,identity_state,canonical_name,claimed_name,aliases_json,verification_source,source_entity_id,confidence,first_met_day,last_met_day,last_encounter_id,payload_json,updated_ts)
VALUES($observer,$subject,$state,$canonical,$claimed,$aliases,$source,$sourceEntity,0.6,$day,$day,$encounter,$payload,$ts)
ON CONFLICT(observer_id,subject_id) DO UPDATE SET identity_state=$state,canonical_name=CASE WHEN acquaintances.canonical_name='' THEN excluded.canonical_name ELSE acquaintances.canonical_name END,claimed_name=$claimed,aliases_json=$aliases,verification_source=$source,source_entity_id=$sourceEntity,confidence=0.6,last_met_day=$day,last_encounter_id=CASE WHEN $encounter<>'' THEN $encounter ELSE acquaintances.last_encounter_id END,payload_json=$payload,updated_ts=$ts;",
                new Dictionary<string, object> { ["observer"] = observerId, ["subject"] = subjectId, ["state"] = state, ["canonical"] = canonicalName ?? "", ["claimed"] = claimedName, ["aliases"] = Json.Serialize(aliases), ["source"] = source ?? "", ["sourceEntity"] = sourceEntityId ?? "", ["day"] = worldDay, ["encounter"] = encounterId ?? "", ["payload"] = "{}", ["ts"] = ts });
            AddIdentityEvidence(connection, observerId, subjectId, state == "disputed" ? "disputed_claim" : "claimed_identity", claimedName, canonicalName, source, sourceEntityId, encounterId, 0.6d, worldDay, payload);
        }

        private static List<string> PruneAmbiguousAddressAliases(ReignDbConnection connection, string observerId, string subjectId,
            List<string> aliases, string currentClaim)
        {
            List<string> result = new List<string>();
            foreach (string alias in (aliases ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (NormalizeIdentityName(alias).Equals(NormalizeIdentityName(currentClaim), StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(alias);
                    continue;
                }
                List<Dictionary<string, object>> evidence = QuerySql(connection, @"SELECT evidence_type,claimed_name,source,payload_json FROM identity_evidence
WHERE observer_id=$observer AND subject_id=$subject AND claimed_name=$alias ORDER BY created_ts;",
                    new Dictionary<string, object> { ["observer"] = observerId, ["subject"] = subjectId, ["alias"] = alias });
                bool supported = evidence.Any(row =>
                {
                    string type = ReadString(row, "evidence_type", "");
                    if (type.Equals("verified", StringComparison.OrdinalIgnoreCase) || type.Equals("trusted_introduction", StringComparison.OrdinalIgnoreCase)) return true;
                    Dictionary<string, object> sourcePayload = TryParseJsonObject(ReadString(row, "payload_json", "")) ?? new Dictionary<string, object>();
                    string sourceText = ReadFirstString(sourcePayload, "playerText", "text", "message");
                    string explicitName = ExtractSelfIntroducedName(sourceText, false);
                    if (NormalizeIdentityName(explicitName).Equals(NormalizeIdentityName(alias), StringComparison.OrdinalIgnoreCase)) return true;
                    string permissiveName = ExtractSelfIntroducedName(sourceText, true);
                    return IdentityIntroductionAnswerExpected(sourcePayload)
                        && NormalizeIdentityName(permissiveName).Equals(NormalizeIdentityName(alias), StringComparison.OrdinalIgnoreCase);
                });
                bool ambiguousAddressOnly = evidence.Count > 0 && !supported && evidence.All(row =>
                {
                    Dictionary<string, object> sourcePayload = TryParseJsonObject(ReadString(row, "payload_json", "")) ?? new Dictionary<string, object>();
                    string sourceText = ReadFirstString(sourcePayload, "playerText", "text", "message");
                    return NormalizeIdentityName(ExtractSelfIntroducedName(sourceText, true)).Equals(NormalizeIdentityName(alias), StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrWhiteSpace(ExtractSelfIntroducedName(sourceText, false))
                        && !IdentityIntroductionAnswerExpected(sourcePayload);
                });
                if (!ambiguousAddressOnly) result.Add(alias);
            }
            if (!result.Contains(currentClaim, StringComparer.OrdinalIgnoreCase)) result.Add(currentClaim);
            return result;
        }

        private static Dictionary<string, object> BuildIdentityView(Dictionary<string, object> row, Dictionary<string, object> context)
        {
            string state = row == null ? "encountered_unknown" : ReadString(row, "identity_state", "encountered_unknown");
            string claimed = row == null ? "" : ReadString(row, "claimed_name", "");
            string canonical = row == null ? ReadString(context, "canonicalName", "") : ReadString(row, "canonical_name", ReadString(context, "canonicalName", ""));
            bool verified = string.Equals(state, "verified", StringComparison.OrdinalIgnoreCase);
            string safeLabel = DeriveSceneIdentityLabel(context);
            string usable = verified ? FirstNonEmpty(canonical, claimed, safeLabel)
                : string.Equals(state, "claimed", StringComparison.OrdinalIgnoreCase) ? FirstNonEmpty(claimed, safeLabel)
                : string.Equals(state, "disputed", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(claimed) ? "the stranger claiming to be " + claimed
                : safeLabel;
            bool subjectSexKnown = TryResolveIdentitySubjectSex(
                context, out string subjectSex);
            bool verifiedQualifiedIntroduction = verified && ReadString(row, "verification_source", "")
                .StartsWith("authoritative_qualified_self_introduction", StringComparison.Ordinal);
            Dictionary<string, object> result =
                new Dictionary<string, object>
            {
                ["identityState"] = state,
                ["usableName"] = usable,
                ["safeLabel"] = safeLabel,
                ["claimedName"] = verifiedQualifiedIntroduction ? usable : claimed,
                ["authoritativeIntroducedName"] = verifiedQualifiedIntroduction ? claimed : "",
                ["canonicalNameAllowed"] = verified,
                ["knowsIdentity"] = verified || string.Equals(state, "claimed", StringComparison.OrdinalIgnoreCase),
                ["knowledgeSource"] = row == null ? "none" : ReadString(row, "verification_source", ""),
                ["confidence"] = row == null ? 0d : ReadDouble(row, "confidence", 0d),
                ["encounterCount"] = row == null ? 0 : ReadInt(row, "encounter_count", 0),
                ["recognitionAttempts"] = row == null ? 0 : ReadInt(row, "recognition_attempts", 0),
                ["subjectSexKnown"] = subjectSexKnown,
                ["subjectSex"] = subjectSexKnown
                    ? subjectSex
                    : "unknown",
                ["instruction"] = verified
                    ? "The observer knows this person's canonical identity and may use their name and established titles."
                    : string.Equals(state, "claimed", StringComparison.OrdinalIgnoreCase)
                        ? "The observer knows only the name this person claimed. Use that claimed name without treating it as independently verified."
                        : string.Equals(state, "disputed", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(claimed)
                            ? "The observer has conflicting or otherwise disputed identity claims. They may repeat the exact latest claimed name as an unverified claim, use second person, or use the scene label, but must not present the canonical identity or titles as verified."
                            : "The observer does not know this person's name. Use only the scene-based label or second-person address; never reveal the canonical name or titles."
            };
            Dictionary<string, object> authorityView =
                BuildCurrentPoliticalAuthorityView(result, context);
            result["authorityView"] = authorityView;
            if (!verified && ReadBool(
                    authorityView, "publicOfficeKnown", false))
            {
                result["instruction"] =
                    "The observer does not know or recognize this person's canonical personal identity, so the canonical name remains forbidden. The observer does know the person's current public office from authoritative institutional context and may use only the supplied valid formal office addresses.";
            }
            return result;
        }

        private static bool TryResolveIdentitySubjectSex(
            Dictionary<string, object> context,
            out string sex)
        {
            sex = "unknown";
            context = context
                ?? new Dictionary<string, object>();
            Dictionary<string, object> native =
                ReadDictionary(context, "nativePoliticalContext")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> subject =
                ReadDictionary(native, "subject")
                ?? ReadDictionary(context, "subject")
                ?? new Dictionary<string, object>();
            string suppliedSex = ReadString(subject, "sex", "")
                .Trim().ToLowerInvariant();
            if (suppliedSex == "female" || suppliedSex == "male")
            {
                sex = suppliedSex;
                return true;
            }
            if (!subject.ContainsKey("isFemale")) return false;
            sex = ReadBool(subject, "isFemale", false)
                ? "female"
                : "male";
            return true;
        }

        private static Dictionary<string, object>
            BuildCurrentPoliticalAuthorityView(
                Dictionary<string, object> identityView,
                Dictionary<string, object> context)
        {
            identityView = identityView
                ?? new Dictionary<string, object>();
            context = context
                ?? new Dictionary<string, object>();
            Dictionary<string, object> native =
                ReadDictionary(context, "nativePoliticalContext")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> observer =
                ReadDictionary(native, "observer")
                ?? ReadDictionary(context, "observer")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> subject =
                ReadDictionary(native, "subject")
                ?? ReadDictionary(context, "subject")
                ?? new Dictionary<string, object>();
            bool identityVerified = ReadBool(
                identityView, "canonicalNameAllowed", false);
            string observerKingdomId = ReadString(
                observer, "kingdomId", "");
            string subjectKingdomId = ReadString(
                subject, "kingdomId", "");
            string observerKingdomName = ReadString(
                observer, "kingdomName", "");
            string subjectKingdomName = ReadString(
                subject, "kingdomName", "");
            string observerClanId = ReadString(
                observer, "clanId", "");
            string subjectClanId = ReadString(
                subject, "clanId", "");
            bool sameClan =
                !string.IsNullOrWhiteSpace(observerClanId)
                && observerClanId.Equals(
                    subjectClanId,
                    StringComparison.OrdinalIgnoreCase);
            bool sameKingdom =
                !string.IsNullOrWhiteSpace(observerKingdomId)
                && observerKingdomId.Equals(
                    subjectKingdomId,
                    StringComparison.OrdinalIgnoreCase);
            bool subjectIsRuler = ReadBool(
                subject, "isRuler", false);
            bool observerIsRuler = ReadBool(
                observer, "isRuler", false);

            Dictionary<string, object> settlement =
                ReadDictionary(native, "settlement")
                ?? new Dictionary<string, object>();
            if (settlement.Count == 0)
            {
                foreach (Dictionary<string, object> bundle in
                    ReadDictionaryList(context, "contextBundles"))
                {
                    if (!ReadString(bundle, "id", "").Equals(
                            "current_settlement_facts",
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    settlement = ReadDictionary(
                        ReadDictionary(bundle, "data")
                            ?? new Dictionary<string, object>(),
                        "settlement")
                        ?? new Dictionary<string, object>();
                    break;
                }
            }
            string settlementId = ReadFirstString(
                settlement, "settlementId", "id");
            string settlementName = ReadString(
                settlement, "name", "");
            string ownerClanId = ReadString(
                settlement, "ownerClanId", "");
            string ownerClanName = ReadString(
                settlement, "ownerClanName", "");
            string settlementKingdomId = ReadFirstString(
                settlement, "kingdomId", "factionId");
            string settlementKingdomName = ReadFirstString(
                settlement, "kingdomName", "factionName");
            string governorHeroId = ReadFirstString(
                settlement, "governorHeroId", "governorId");
            Dictionary<string, object> diplomacy =
                ReadDictionary(native, "diplomacy")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> subjectKingdomState =
                ReadDictionary(diplomacy, "playerKingdom")
                ?? new Dictionary<string, object>();
            subjectKingdomName = FirstNonEmpty(
                subjectKingdomName,
                ReadFirstString(
                    subjectKingdomState,
                    "kingdomName",
                    "name"),
                !string.IsNullOrWhiteSpace(subjectKingdomId)
                    && subjectKingdomId.Equals(
                        settlementKingdomId,
                        StringComparison.OrdinalIgnoreCase)
                    ? settlementKingdomName
                    : "");
            List<Dictionary<string, object>> enemyKingdoms =
                ReadDictionaryList(
                    diplomacy, "playerEnemyKingdoms");
            string subjectId = IdentityHeroId(subject);
            string observerId = IdentityHeroId(observer);
            string encounterMode = NormalizeLookup(
                ReadString(context, "mode", ""));
            bool institutionalEncounter =
                ReadBool(context, "officialAudience", false)
                || ReadBool(context, "officialEvent", false)
                || encounterMode.Contains("social_event")
                || encounterMode.Contains("party_chat")
                || encounterMode.Contains("group")
                || encounterMode.Contains("court")
                || encounterMode.Contains("castle");
            bool subjectRealmOwnsCurrentSettlement =
                !string.IsNullOrWhiteSpace(subjectKingdomId)
                && subjectKingdomId.Equals(
                    settlementKingdomId,
                    StringComparison.OrdinalIgnoreCase);
            bool subjectClanOwnsCurrentSettlement =
                !string.IsNullOrWhiteSpace(subjectClanId)
                && subjectClanId.Equals(
                    ownerClanId,
                    StringComparison.OrdinalIgnoreCase);
            Dictionary<string, object> civic = ReadDictionary(native, "observerCivicAffiliation");
            bool localCivicSovereign = ReadBool(native, "authoritative", false)
                && ReadBool(civic, "authoritative", false) && ReadBool(civic, "available", false)
                && ReadBool(civic, "localEncounter", false)
                && !string.IsNullOrWhiteSpace(observerId) && !string.IsNullOrWhiteSpace(subjectId)
                && ReadString(civic, "heroStringId", "").Equals(observerId, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(settlementId)
                && ReadString(civic, "settlementId", "").Equals(settlementId, StringComparison.OrdinalIgnoreCase)
                && subjectRealmOwnsCurrentSettlement
                && ReadString(civic, "kingdomId", "").Equals(subjectKingdomId, StringComparison.OrdinalIgnoreCase)
                && ReadString(civic, "sovereignHeroStringId", "").Equals(subjectId, StringComparison.OrdinalIgnoreCase)
                && (ReadString(civic, "source", "") == "native_home_settlement"
                    || ReadString(civic, "source", "") == "native_notable_roster");
            // Native political summaries omit resident metadata. Use the matching
            // full observer profile, while current native ownership remains authoritative.
            var residentObserver = IsEncounteredResidentProfile(observer)
                ? observer : ReadDictionary(context, "observer");
            var residentHome = ReadDictionary(residentObserver, "encounteredResident");
            bool residentCivicSovereign = identityVerified && subjectIsRuler
                && ReadBool(native, "authoritative", false)
                && IsEncounteredResidentProfile(residentObserver)
                && !string.IsNullOrWhiteSpace(observerId)
                && observerId.Equals(IdentityHeroId(residentObserver), StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(subjectId) && !string.IsNullOrWhiteSpace(settlementId)
                && ReadString(residentHome, "homeSettlementId", "").Equals(settlementId, StringComparison.OrdinalIgnoreCase)
                && ReadString(residentHome, "homeRulerId", "").Equals(subjectId, StringComparison.OrdinalIgnoreCase)
                && ReadString(residentHome, "homeKingdomId", "").Equals(subjectKingdomId, StringComparison.OrdinalIgnoreCase)
                && subjectRealmOwnsCurrentSettlement;
            localCivicSovereign = localCivicSovereign || residentCivicSovereign;
            bool publicOfficeKnown = subjectIsRuler
                && !string.IsNullOrWhiteSpace(subjectKingdomId)
                && (identityVerified || localCivicSovereign
                    || sameKingdom && institutionalEncounter
                    || sameKingdom && subjectRealmOwnsCurrentSettlement
                        && subjectClanOwnsCurrentSettlement);
            bool realmSovereignKnown = publicOfficeKnown;
            bool subjectIsObserverSovereign =
                realmSovereignKnown && (sameKingdom || localCivicSovereign);
            bool observerIsSubjectSovereign =
                identityVerified && observerIsRuler
                && sameKingdom;
            bool currentSettlementOwnerKnown =
                identityVerified
                && !string.IsNullOrWhiteSpace(subjectClanId)
                && subjectClanId.Equals(
                    ownerClanId,
                    StringComparison.OrdinalIgnoreCase);
            bool observerClanOwnsCurrentSettlement =
                !string.IsNullOrWhiteSpace(observerClanId)
                && observerClanId.Equals(
                    ownerClanId,
                    StringComparison.OrdinalIgnoreCase);
            bool subjectIsCurrentGovernor =
                identityVerified
                && !string.IsNullOrWhiteSpace(subjectId)
                && subjectId.Equals(
                    governorHeroId,
                    StringComparison.OrdinalIgnoreCase);
            bool observerIsCurrentGovernor =
                !string.IsNullOrWhiteSpace(observerId)
                && observerId.Equals(
                    governorHeroId,
                    StringComparison.OrdinalIgnoreCase);
            List<string> recognizedRoles = identityVerified
                ? BuildAuthoritativePublicRoles(subject)
                : new List<string>();
            List<string> observerRoles =
                BuildAuthoritativePublicRoles(observer);
            if (realmSovereignKnown)
                recognizedRoles.Add("realm_sovereign");
            if (subjectIsObserverSovereign)
                recognizedRoles.Add(
                    "subject_is_observer_sovereign");
            if (observerIsSubjectSovereign)
                recognizedRoles.Add(
                    "observer_is_subject_sovereign");
            if (sameClan && identityVerified)
                recognizedRoles.Add("same_clan");
            if (currentSettlementOwnerKnown)
                recognizedRoles.Add(
                    "current_settlement_owner_clan_member");
            if (subjectIsCurrentGovernor)
                recognizedRoles.Add("current_settlement_governor");
            recognizedRoles = recognizedRoles
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string authorityRelationship =
                subjectIsObserverSovereign
                    ? "subject_is_observer_sovereign"
                    : observerIsSubjectSovereign
                        ? "observer_is_subject_sovereign"
                        : sameClan && identityVerified
                            ? "same_clan"
                            : sameKingdom && identityVerified
                                ? "same_kingdom_peer_or_subject"
                                : identityVerified
                                    ? "known_foreign_or_independent_person"
                                    : "identity_unverified";
            string source = subjectIsObserverSovereign
                ? identityVerified
                    ? "verified_identity_live_allegiance"
                    : institutionalEncounter
                        ? "institutional_encounter_live_allegiance"
                        : "sovereign_seat_live_allegiance"
                : currentSettlementOwnerKnown
                    ? "live_current_settlement_ownership"
                    : identityVerified
                        ? "verified_identity_current_native_office"
                        : "identity_not_verified";
            if (localCivicSovereign) source = "local_notable_live_civic_authority";
            if (residentCivicSovereign) source = "recognized_resident_live_civic_authority";
            return new Dictionary<string, object>
            {
                ["localCivicSovereign"] = localCivicSovereign,
                ["identityVerified"] = identityVerified,
                ["personalIdentityKnown"] = identityVerified,
                ["publicOfficeKnown"] = publicOfficeKnown,
                ["officeKnowledgeSource"] = publicOfficeKnown
                    ? source
                    : "office_not_established",
                ["source"] = source,
                ["observerKingdomId"] = observerKingdomId,
                ["subjectKingdomId"] = subjectKingdomId,
                ["observerKingdomName"] = observerKingdomName,
                ["subjectKingdomName"] = subjectKingdomName,
                ["observerClanId"] = observerClanId,
                ["subjectClanId"] = subjectClanId,
                ["sameClan"] = sameClan,
                ["sameKingdom"] = sameKingdom,
                ["observerIsRuler"] = observerIsRuler,
                ["subjectIsRuler"] = subjectIsRuler,
                ["realmSovereignKnown"] =
                    realmSovereignKnown,
                ["subjectIsObserverSovereign"] =
                    subjectIsObserverSovereign,
                ["observerIsSubjectSovereign"] =
                    observerIsSubjectSovereign,
                ["authorityRelationship"] =
                    authorityRelationship,
                ["currentSettlementId"] = settlementId,
                ["currentSettlementName"] = settlementName,
                ["currentSettlementKingdomId"] =
                    settlementKingdomId,
                ["currentSettlementKingdomName"] =
                    settlementKingdomName,
                ["currentSettlementOwnerClanId"] =
                    ownerClanId,
                ["currentSettlementOwnerClanName"] =
                    ownerClanName,
                ["currentSettlementGovernorHeroId"] =
                    governorHeroId,
                ["currentSettlementOwnerKnown"] =
                    currentSettlementOwnerKnown,
                ["observerClanOwnsCurrentSettlement"] =
                    observerClanOwnsCurrentSettlement,
                ["subjectIsCurrentGovernor"] =
                    subjectIsCurrentGovernor,
                ["observerIsCurrentGovernor"] =
                    observerIsCurrentGovernor,
                ["subjectPrimaryRole"] =
                    identityVerified
                        ? PrimaryAuthoritativePublicRole(subject)
                        : publicOfficeKnown
                            ? "realm_sovereign"
                            : "identity_unverified",
                ["observerPrimaryRole"] =
                    PrimaryAuthoritativePublicRole(observer),
                ["observerRoles"] = observerRoles,
                ["currentDiplomacyKnown"] =
                    ReadBool(native, "authoritative", false)
                    && ReadBool(
                        subjectKingdomState,
                        "available", false),
                ["currentEnemyKingdomCount"] =
                    enemyKingdoms.Count > 0
                        ? enemyKingdoms.Count
                        : ReadInt(
                            subjectKingdomState,
                            "warCount", 0),
                ["currentEnemyKingdomIds"] =
                    enemyKingdoms.Select(x =>
                            ReadFirstString(
                                x, "kingdomId", "factionId"))
                        .Where(x =>
                            !string.IsNullOrWhiteSpace(x))
                        .Distinct(
                            StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                ["currentEnemyKingdomNames"] =
                    enemyKingdoms.Select(x =>
                            ReadString(x, "name", ""))
                        .Where(x =>
                            !string.IsNullOrWhiteSpace(x))
                        .Distinct(
                            StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                ["nativePoliticalObservedWorldDay"] =
                    ReadDouble(
                        native, "observedWorldDay", -1d),
                ["recognizedRoles"] = recognizedRoles,
                ["validFormalAddresses"] = publicOfficeKnown
                    ? SovereignFormalAddresses(subject)
                    : new List<string>(),
                ["instruction"] = subjectIsObserverSovereign
                    ? identityVerified
                        ? "The observer knows this person is their own current sovereign and knows their personal identity. Personality may produce loyalty, strategic flattery, manipulation, resentment, candid opposition, or deliberate defiance, but the observer must understand the unequal political relationship and its risks."
                        : "The observer knows from the institutional encounter and current allegiance that this person is their sovereign, but does not know or recognize the sovereign's personal identity. Use a valid sovereign title without revealing the unknown name."
                    : realmSovereignKnown
                        ? "The observer recognizes this person as a current foreign or independent sovereign. Clothing, lack of retinue, or an understated presentation cannot make that verified public office uncertain."
                    : currentSettlementOwnerKnown
                        ? "The observer recognizes this person's clan as the current owner of the settlement. Appearance cannot override current native ownership."
                        : "Do not infer a political office that is not established by current native facts and verified identity."
            };
        }

        private static List<string> SovereignFormalAddresses(
            Dictionary<string, object> subject)
        {
            bool female = ReadBool(subject, "isFemale", false)
                || ReadString(subject, "sex", "").Equals(
                    "female", StringComparison.OrdinalIgnoreCase);
            return female
                ? new List<string>
                {
                    "Your Grace", "Your Majesty", "my queen"
                }
                : new List<string>
                {
                    "Your Grace", "Your Majesty", "Sire", "my king"
                };
        }

        private static List<string> BuildAuthoritativePublicRoles(
            Dictionary<string, object> hero)
        {
            hero = hero ?? new Dictionary<string, object>();
            List<string> roles = new List<string>();
            bool female = ReadBool(
                hero, "isFemale",
                ReadString(hero, "sex", "")
                    .Equals("female",
                        StringComparison.OrdinalIgnoreCase));
            if (ReadBool(hero, "isRuler", false))
                roles.Add(female ? "queen" : "king");
            if (ReadBool(hero, "isLord", false))
                roles.Add(female ? "lady" : "lord");
            if (!string.IsNullOrWhiteSpace(
                    ReadFirstString(
                        hero, "governorOfSettlementId",
                        "governor_settlement_id")))
                roles.Add("governor");
            if (ReadBool(hero, "isNotable",
                    ReadInt(hero, "is_notable", 0) != 0))
                roles.Add("notable");
            if (ReadBool(hero, "isWanderer",
                    ReadInt(hero, "is_wanderer", 0) != 0))
                roles.Add("wanderer");
            string occupation = NormalizeLookup(
                ReadString(hero, "occupation", ""));
            if (roles.Count == 0)
            {
                if (occupation.Contains("merchant"))
                    roles.Add("merchant");
                else if (occupation.Contains("artisan"))
                    roles.Add("artisan");
                else if (occupation.Contains("gang"))
                    roles.Add("gang_leader");
                else if (occupation.Contains("rural"))
                    roles.Add("rural_notable");
                else
                    roles.Add("commoner_or_unaffiliated");
            }
            return roles.Distinct(
                StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string PrimaryAuthoritativePublicRole(
            Dictionary<string, object> hero)
        {
            List<string> roles =
                BuildAuthoritativePublicRoles(hero);
            return roles.FirstOrDefault() ?? "unknown";
        }

        private static string DeriveSceneIdentityLabel(Dictionary<string, object> context)
        {
            string supplied = ReadFirstString(context, "visibleLabel", "sceneLabel", "unknownLabel");
            if (!string.IsNullOrWhiteSpace(supplied)) return LimitText(supplied, 80);
            Dictionary<string, object> appearance = ReadDictionary(context, "appearance") ?? ReadDictionary(ReadDictionary(context, "subject"), "appearance") ?? new Dictionary<string, object>();
            string status = ReadString(appearance, "visibleStatusLabel", "").ToLowerInvariant();
            string mode = ReadString(context, "mode", "").ToLowerInvariant();
            if (ReadBool(context, "isPrisoner", false)) return "the prisoner";
            if (mode.Contains("court")) return status.Contains("noble") ? "the unknown noble" : "the petitioner";
            if (status.Contains("noble") || status.Contains("wealth")) return "the well-dressed stranger";
            if (status.Contains("poor") || status.Contains("destitute")) return "the poorly dressed traveler";
            List<Dictionary<string, object>> equipment = ReadDictionaryList(appearance, "civilianEquipment").Concat(ReadDictionaryList(appearance, "battleEquipment")).ToList();
            if (equipment.Any(x => ReadBool(x, "isWeapon", false))) return "the armed stranger";
            return "the stranger";
        }

        private static bool IdentityIntroductionAnswerExpected(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            List<string> candidateNpcLines = ReadDictionaryList(payload, "groupTranscript")
                .Concat(ReadDictionaryList(payload, "transcript"))
                .Where(row => ReadString(row, "role", "").Equals("npc", StringComparison.OrdinalIgnoreCase))
                .Select(row => ReadFirstString(row, "text", "content", "message"))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            string playerName = FirstNonEmpty(ReadFirstString(payload, "playerName", "mainHeroName"),
                ReadString(ReadDictionary(payload, "subject"), "name", ""));
            foreach (string line in ReadStringList(payload, "transcript").AsEnumerable().Reverse())
            {
                string trimmed = (line ?? "").Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                if (!string.IsNullOrWhiteSpace(playerName)
                    && trimmed.StartsWith(playerName + ":", StringComparison.OrdinalIgnoreCase)) continue;
                candidateNpcLines.Add(trimmed);
                break;
            }

            string latestNpcLine = candidateNpcLines.LastOrDefault() ?? "";
            return ContainsAny(latestNpcLine.ToLowerInvariant(),
                "what is your name", "what's your name", "who are you", "what are you called",
                "tell me your name", "give me your name", "state your name", "introduce yourself",
                "not introduced yourself", "not yet introduced yourself", "not yet introduced themselves",
                "have not introduced yourself", "haven't introduced yourself");
        }

        private static string ExtractSelfIntroducedName(
            string text,
            bool allowNameFirstAnswer = false,
            string canonicalName = "")
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string clean = Regex.Replace(text, @"\*[^*]*\*", " ");
            clean = Regex.Replace(clean, @"\s+", " ").Trim();
            const string namePattern = @"([A-Z][\p{L}'\-]*(?:\s+(?:of|the|de|von|fen|Banu|[A-Z][\p{L}'\-]*)){0,3})";
            Match match = Regex.Match(clean, @"(?i:\b(?:my\s+(?:real\s+)?name\s+is|call\s+me|you\s+may\s+call\s+me|people\s+call\s+me))\s+" + namePattern + @"(?=\s*[,.;!?]|\s*$)", RegexOptions.CultureInvariant);
            if (!match.Success)
                match = Regex.Match(clean, @"(?i:\bwhen\s+i\s+said)\s+" + namePattern + @"\s*,?\s*(?i:i\s+mean(?:t)?\s+(?:that\s+)?(?:it\s+)?is\s+my\s+(?:real\s+)?name)\b", RegexOptions.CultureInvariant);
            if (!match.Success)
                match = Regex.Match(clean, @"\b" + namePattern + @"\s+(?i:is\s+my\s+(?:real\s+)?name)\b", RegexOptions.CultureInvariant);
            // A direct answer to "Who are you?" commonly begins with a name
            // followed by the speaker continuing with "I...".  Keeping this
            // shape anchored to the start avoids treating an arbitrary proper
            // noun later in the sentence as a self-introduction. This ambiguous
            // form is valid only when the preceding NPC line actually requested
            // an introduction; otherwise "Menor, I am giving you..." is address.
            if (!match.Success && allowNameFirstAnswer)
                match = Regex.Match(clean, @"^\s*" + namePattern + @"\s*,\s*(?i:i(?:\s+am|'m|’m|m)\b)", RegexOptions.CultureInvariant);
            if (!match.Success)
                match = Regex.Match(clean, @"(?i:\b(?:i\s+am|i'm|i’m|im))\s+" + namePattern + @"(?=\s*[,.;!?]|\s*$)", RegexOptions.CultureInvariant);
            bool nameAndTitleAnswer = false;
            if (!match.Success
                && (allowNameFirstAnswer
                    || !string.IsNullOrWhiteSpace(canonicalName)))
            {
                match = Regex.Match(
                    clean,
                    @"^\s*" + namePattern
                        + @"\s*[,;:\-\u2014]\s*"
                        + @"(?i:(?:the|a|an)\s+)?"
                        + @"(?:lord|lady|king|queen|ruler|sovereign|"
                        + @"owner|governor|vassal|mercenary|wanderer|"
                        + @"merchant|soldier|captain|commander)\b",
                    RegexOptions.CultureInvariant);
                nameAndTitleAnswer = match.Success;
            }
            if (!match.Success) return string.Empty;
            string candidate = match.Groups[1].Value.Trim();
            if (nameAndTitleAnswer
                && !string.IsNullOrWhiteSpace(canonicalName)
                && !NormalizeIdentityName(candidate).Equals(
                    NormalizeIdentityName(canonicalName),
                    StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            string first = candidate.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            string[] rejected = { "a", "an", "the", "yes", "no", "well", "actually", "honestly", "hello", "hi", "greetings", "thanks", "thank", "here", "ready", "sorry", "asking", "looking", "going", "trying", "standing", "kneeling", "traveling", "travelling", "pleased", "glad", "afraid", "lord", "lady", "king", "queen", "ruler", "merchant", "soldier", "prisoner", "traveler", "traveller" };
            if (rejected.Contains(first, StringComparer.OrdinalIgnoreCase)) return string.Empty;
            return candidate.Length <= 80 ? candidate : string.Empty;
        }

        private static bool QuarantineAmbiguousStoredIdentityClaim(ReignDbConnection connection,
            Dictionary<string, object> row, string observerId, string subjectId, double worldDay)
        {
            if (row == null || IdentityStateVerified(row)) return false;
            string state = ReadString(row, "identity_state", "");
            string claimed = ReadString(row, "claimed_name", "");
            if (string.IsNullOrWhiteSpace(claimed)
                || !(state.Equals("claimed", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("disputed", StringComparison.OrdinalIgnoreCase))) return false;

            Dictionary<string, object> sourcePayload = TryParseJsonObject(ReadString(row, "payload_json", ""))
                ?? new Dictionary<string, object>();
            if (sourcePayload.Count == 0)
            {
                Dictionary<string, object> evidence = QuerySql(connection, @"SELECT payload_json
FROM identity_evidence
WHERE observer_id=$observer AND subject_id=$subject
ORDER BY created_ts DESC LIMIT 1;",
                    new Dictionary<string, object>
                    {
                        ["observer"] = observerId,
                        ["subject"] = subjectId
                    }).FirstOrDefault();
                sourcePayload = TryParseJsonObject(ReadString(evidence, "payload_json", ""))
                    ?? new Dictionary<string, object>();
            }
            string sourceText = ReadFirstString(sourcePayload, "playerText", "text", "message");
            string explicitName = ExtractSelfIntroducedName(
                sourceText,
                false,
                ReadString(row, "canonical_name", ""));
            string permissiveName = ExtractSelfIntroducedName(sourceText, true);
            bool supported = NormalizeIdentityName(explicitName).Equals(NormalizeIdentityName(claimed), StringComparison.OrdinalIgnoreCase)
                || IdentityIntroductionAnswerExpected(sourcePayload)
                    && NormalizeIdentityName(permissiveName).Equals(NormalizeIdentityName(claimed), StringComparison.OrdinalIgnoreCase);
            if (supported || !NormalizeIdentityName(permissiveName).Equals(NormalizeIdentityName(claimed), StringComparison.OrdinalIgnoreCase))
                return false;

            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ExecuteSql(connection, @"UPDATE acquaintances SET identity_state='encountered_unknown',claimed_name='',aliases_json='[]',
verification_source='quarantined_ambiguous_name_first_address',source_entity_id='',confidence=0,payload_json=$payload,updated_ts=$ts
WHERE observer_id=$observer AND subject_id=$subject AND identity_state<>'verified';",
                new Dictionary<string, object>
                {
                    ["observer"] = observerId, ["subject"] = subjectId, ["payload"] = Json.Serialize(sourcePayload), ["ts"] = ts
                });
            AddIdentityEvidence(connection, observerId, subjectId, "invalid_claim_quarantined", claimed,
                ReadString(row, "canonical_name", ""), "ambiguous_name_first_address", subjectId,
                ReadString(row, "last_encounter_id", ""), 0d, worldDay, sourcePayload);
            return true;
        }

        private static string UpsertIdentityStateRank(string state)
        {
            if (string.Equals(state, "verified", StringComparison.OrdinalIgnoreCase)) return "3";
            if (string.Equals(state, "disputed", StringComparison.OrdinalIgnoreCase)) return "2";
            if (string.Equals(state, "claimed", StringComparison.OrdinalIgnoreCase)) return "1";
            return "0";
        }

        private static bool IdentityStateVerified(Dictionary<string, object> row)
        {
            return row != null && string.Equals(ReadString(row, "identity_state", ""), "verified", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> ReadAcquaintance(ReignDbConnection connection, string observerId, string subjectId)
        {
            Dictionary<string, object> stored = ReadStoredAcquaintance(connection, observerId, subjectId);
            List<Dictionary<string, object>> roster = QuerySql(connection,
                "SELECT * FROM identity_roster WHERE hero_id=$observer OR hero_id=$subject;",
                new Dictionary<string, object> { ["observer"] = observerId, ["subject"] = subjectId });
            Dictionary<string, object> observer = roster.FirstOrDefault(x =>
                ReadString(x, "hero_id", "").Equals(observerId, StringComparison.OrdinalIgnoreCase));
            Dictionary<string, object> subject = roster.FirstOrDefault(x =>
                ReadString(x, "hero_id", "").Equals(subjectId, StringComparison.OrdinalIgnoreCase));
            string source = ImplicitIdentitySource(observer, subject);
            if (string.IsNullOrWhiteSpace(source)) return stored;
            if (IdentityStateVerified(stored)) return stored;
            return new Dictionary<string, object>
            {
                ["observer_id"] = observerId, ["subject_id"] = subjectId, ["identity_state"] = "verified",
                ["canonical_name"] = ReadString(subject, "canonical_name",
                    ReadString(stored, "canonical_name", "")),
                ["claimed_name"] = ReadString(stored, "claimed_name", ""),
                ["aliases_json"] = ReadString(stored, "aliases_json", "[]"),
                ["verification_source"] = source, ["source_entity_id"] = "",
                ["confidence"] = 1d, ["first_met_day"] = 0d, ["last_met_day"] = 0d,
                ["encounter_count"] = ReadInt(stored, "encounter_count", 0),
                ["last_encounter_id"] = ReadString(stored, "last_encounter_id", ""),
                ["recognition_attempts"] = ReadInt(stored, "recognition_attempts", 0),
                ["last_recognition_result"] = "", ["last_recognition_encounter_id"] = "",
                ["payload_json"] = "{}", ["updated_ts"] = 0L, ["implicit"] = true
            };
        }

        private static Dictionary<string, object> ReadStoredAcquaintance(
            ReignDbConnection connection,
            string observerId,
            string subjectId)
        {
            return QuerySql(connection,
                "SELECT * FROM acquaintances WHERE observer_id=$observer AND subject_id=$subject LIMIT 1;",
                new Dictionary<string, object> { ["observer"] = observerId, ["subject"] = subjectId }).FirstOrDefault();
        }

        private static void InsertUnknownAcquaintance(ReignDbConnection connection, string observerId, string subjectId, string canonicalName, string encounterId, double worldDay, Dictionary<string, object> payload)
        {
            ExecuteSql(connection, @"INSERT OR IGNORE INTO acquaintances(observer_id,subject_id,identity_state,canonical_name,first_met_day,last_met_day,encounter_count,last_encounter_id,payload_json,updated_ts)
VALUES($observer,$subject,'encountered_unknown',$canonical,$day,$day,1,$encounter,$payload,$ts);", new Dictionary<string, object>
            {
                ["observer"] = observerId, ["subject"] = subjectId, ["canonical"] = canonicalName ?? "", ["day"] = worldDay,
                ["encounter"] = encounterId ?? "", ["payload"] = "{}", ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }

        private static void AddIdentityEvidence(ReignDbConnection connection, string observerId, string subjectId, string evidenceType, string claimedName, string canonicalName, string source, string sourceEntityId, string encounterId, double confidence, double worldDay, Dictionary<string, object> payload)
        {
            ExecuteSql(connection, @"INSERT INTO identity_evidence(evidence_id,observer_id,subject_id,evidence_type,claimed_name,canonical_name,source,source_entity_id,encounter_id,confidence,world_day,payload_json,created_ts)
VALUES($id,$observer,$subject,$type,$claimed,$canonical,$source,$sourceEntity,$encounter,$confidence,$day,$payload,$ts);", new Dictionary<string, object>
            {
                ["id"] = "identity_" + Guid.NewGuid().ToString("N"), ["observer"] = observerId, ["subject"] = subjectId, ["type"] = evidenceType,
                ["claimed"] = claimedName ?? "", ["canonical"] = canonicalName ?? "", ["source"] = source ?? "", ["sourceEntity"] = sourceEntityId ?? "",
                ["encounter"] = encounterId ?? "", ["confidence"] = confidence, ["day"] = worldDay,
                ["payload"] = Json.Serialize(CompactIdentityEvidencePayload(payload)), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }

        private static int MigrateLegacyIdentityKnowledge(ReignDbConnection connection, Dictionary<string, Dictionary<string, object>> heroes, double worldDay, string correlationId)
        {
            if (string.Equals(ReadString(QuerySql(connection, "SELECT value FROM schema_meta WHERE key='identity_legacy_dialogue_migrated' LIMIT 1;").FirstOrDefault(), "value", ""), "1", StringComparison.Ordinal)) return 0;
            int count = 0;
            foreach (Dictionary<string, object> session in QuerySql(connection, "SELECT DISTINCT npc_id,player_id FROM conversation_sessions WHERE status<>'deleted' AND npc_id<>'' AND player_id<>'';"))
            {
                string observerId = ReadString(session, "npc_id", ""), subjectId = ReadString(session, "player_id", "");
                if (!heroes.TryGetValue(subjectId, out Dictionary<string, object> subject)) continue;
                Dictionary<string, object> existing = ReadAcquaintance(connection, observerId, subjectId);
                if (existing != null) continue;
                UpsertClaimedIdentity(connection, observerId, subjectId, ReadString(subject, "name", ""), ReadString(subject, "name", ""), "legacy_dialogue_migration", subjectId, "", worldDay, new Dictionary<string, object> { ["correlationId"] = correlationId });
                count++;
            }
            ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('identity_legacy_dialogue_migrated','1');");
            return count;
        }

        private static Dictionary<string, object> IdentitySqlArgs(string observerId, string subjectId, double worldDay, string encounterId)
        {
            return new Dictionary<string, object> { ["observer"] = observerId, ["subject"] = subjectId, ["day"] = worldDay, ["encounter"] = encounterId ?? "", ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        }

        private static string IdentityHeroId(Dictionary<string, object> hero)
        {
            return ReadFirstString(hero, "heroStringId", "heroId", "id");
        }

        private static bool IdentityIsLord(Dictionary<string, object> hero)
        {
            return ReadBool(hero, "isLord", false) || string.Equals(ReadString(hero, "occupation", ""), "Lord", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractAuthoritativeSelfIntroduction(string text, Dictionary<string, object> subject)
        {
            string clean = Regex.Replace(text ?? "", @"\*[^*]*\*", " ");
            Match match = Regex.Match(clean,
                @"\b(?:my\s+(?:real\s+)?name\s+is|i\s+am|i['’]m|call\s+me|you\s+may\s+call\s+me)\s+([\p{L}\p{M}'’\-]+(?:\s+[\p{L}\p{M}'’\-]+){0,6})(?=\s*[,.;!?]|\s*$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success && AuthoritativeIntroductionSource(match.Groups[1].Value, subject).Length > 0
                ? match.Groups[1].Value : "";
        }

        private static string ExtractNameFirstSelfIntroduction(
            string text, Dictionary<string, object> subject,
            Dictionary<string, object> payload)
        {
            string subjectId = IdentityHeroId(subject);
            string playerId = ReadFirstString(payload,
                "playerHeroStringId", "mainHeroStringId", "playerId");
            if (!ReadBool(subject, "isPlayer", false)
                && !ReadString(subject, "role", "").Equals("player", StringComparison.OrdinalIgnoreCase)
                && !subjectId.Equals(playerId, StringComparison.OrdinalIgnoreCase)
                && !subjectId.Equals("main_hero", StringComparison.OrdinalIgnoreCase))
                return "";

            string canonicalName = ReadString(subject, "name", "").Trim();
            if (canonicalName.Length == 0) return "";
            string clean = Regex.Replace(text ?? "", @"\*[^*]*\*", " ");
            clean = Regex.Replace(clean, @"\s+", " ").Trim();
            if (clean.Length == 0) return "";

            List<string> candidates = new List<string> { canonicalName };
            foreach (string key in new[] { "clanName", "kingdomName" })
            {
                string qualifier = ReadString(subject, key, "").Trim();
                if (qualifier.Length == 0) continue;
                candidates.Add(canonicalName + " of " + qualifier);
                candidates.Add(canonicalName + " " + qualifier);
            }
            List<Dictionary<string, object>> others =
                MergedInteractionParticipantProfiles(payload)
                .Concat(new[] { ReadDictionary(payload, "observer") })
                .Where(profile => profile != null
                    && !IdentityHeroId(profile).Equals(subjectId,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (string candidate in candidates
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(value => value.Length))
            {
                string pattern = string.Join(@"\s+", candidate.Split(
                    new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(Regex.Escape));
                if (!Regex.IsMatch(clean, @"^" + pattern + @"(?=\s*[,.;:!?]|\s*$)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    continue;
                string normalized = NormalizeIdentityName(candidate);
                if (others.Any(profile =>
                    NormalizeIdentityName(ReadString(profile, "name", "")) == normalized))
                    continue;
                if (AuthoritativeIntroductionSource(candidate, subject).Length > 0)
                    return candidate;
            }
            return "";
        }

        private static string AuthoritativeIntroductionSource(string claimed, Dictionary<string, object> subject)
        {
            string name = NormalizeIdentityName(ReadString(subject, "name", ""));
            string normalized = NormalizeIdentityName(claimed);
            if (name.Length == 0 || normalized.Length == 0) return "";
            if (normalized == name) return "exact_self_introduction";
            foreach (string key in new[] { "clanName", "kingdomName" })
            {
                string qualifier = NormalizeIdentityName(ReadString(subject, key, ""));
                if (qualifier.Length != 0 && (normalized == name + " " + qualifier
                    || normalized == name + " of " + qualifier))
                    return "authoritative_qualified_self_introduction";
            }
            return "";
        }

        private static string NormalizeIdentityName(string value)
        {
            return Regex.Replace((value ?? "").Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD), @"[^\p{L}\p{N}]+", " ").Trim();
        }

        private static string ComputeIdentityRosterFingerprint(List<Dictionary<string, object>> heroes)
        {
            string canonical = string.Join("\n", (heroes ?? new List<Dictionary<string, object>>()).OrderBy(IdentityHeroId, StringComparer.OrdinalIgnoreCase).Select(x => string.Join("|", new[]
            {
                IdentityHeroId(x), ReadString(x,"clanId",""), ReadString(x,"kingdomId",""), IdentityIsLord(x)?"1":"0", ReadBool(x,"isRuler",false)?"1":"0", ReadBool(x,"isAlive",true)?"1":"0",
                ReadBool(x,"isAdult",!ReadBool(x,"isChild",false))?"1":"0", ReadBool(x,"isPlayer",false)?"1":"0",
                ReadString(x,"sex",ReadBool(x,"isFemale",false)?"female":"male"), ReadInt(x,"clanTier",0).ToString(CultureInfo.InvariantCulture),
                ReadInt(x,"currentCharm",ReadInt(x,"charm",0)).ToString(CultureInfo.InvariantCulture),
                ReadString(x,"occupation",""), ReadBool(x,"isNotable",false)?"1":"0", ReadBool(x,"isWanderer",false)?"1":"0",
                ReadBool(x,"isClanLeader",false)?"1":"0", ReadBool(x,"isMercenaryClan",false)?"1":"0",
                ReadString(x,"governorOfSettlementId",""), ReadString(x,"governorOfSettlementName",""),
                ReadString(x,"spouseId",""), ReadString(x,"fatherId",""), ReadString(x,"motherId",""), string.Join(",",ReadStringList(x,"childrenIds").OrderBy(v=>v,StringComparer.OrdinalIgnoreCase))
            })));
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "").ToLowerInvariant();
        }

        private static List<Dictionary<string, object>> RunIdentitySubsystemSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string, object> add = (id, passed, summary, data) => results.Add(new Dictionary<string, object>
            {
                ["ok"] = true,
                ["passed"] = passed,
                ["suite"] = "identity_system",
                ["caseId"] = id,
                ["name"] = id,
                ["summary"] = summary,
                ["data"] = data ?? new Dictionary<string, object>(),
                ["durationMs"] = 0
            });

            string campaignId = "__identity_self_test_" + Guid.NewGuid().ToString("N");
            string campaignPath = CampaignDirectory(campaignId);
            string courtCampaignId = campaignId + "_court";
            string courtCampaignPath = CampaignDirectory(courtCampaignId);
            try
            {
                Dictionary<string, object> player = IdentityTestHero("player", "Aeric", "player_clan", "south", true, false);
                // The prompt adapter observes a court at day 50 and persists its own
                // live roster context. Keep it separate from the day-10 roster fixture.
                AddCourtAudienceIdentityTests(courtCampaignId, add);
                using (ReignDbConnection isolationConnection = OpenCampaignConnection(campaignId))
                {
                    EnsureIdentitySchema(isolationConnection);
                    int priorRosterRows = ReadInt(QuerySql(isolationConnection,
                        "SELECT COUNT(*) AS count FROM identity_roster;").FirstOrDefault(), "count", -1);
                    add("court_audience_fixture_isolated", priorRosterRows == 0,
                        "The persisted court prompt fixture leaves the separate roster campaign empty.",
                        new Dictionary<string, object> { ["priorRosterRows"] = priorRosterRows });
                }
                player["childrenIds"] = new List<object> { "child" };
                Dictionary<string, object> sameKingdom = IdentityTestHero("same_kingdom", "Ira", "ira_clan", "south", true, false);
                Dictionary<string, object> clanmate = IdentityTestHero("clanmate", "Alynneth", "player_clan", "", false, false);
                Dictionary<string, object> child = IdentityTestHero("child", "Child", "ward_clan", "", false, false);
                child["fatherId"] = "player";
                Dictionary<string, object> ruler = IdentityTestHero("ruler", "Rhagaea", "ruler_clan", "empire", true, true);
                Dictionary<string, object> foreign = IdentityTestHero("foreign", "Derthert", "foreign_clan", "vlandia", true, false);
                List<Dictionary<string, object>> roster = new List<Dictionary<string, object>> { player, sameKingdom, clanmate, child, ruler, foreign };
                Dictionary<string, object> syncPayload = new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["correlationId"] = "identity_self_test_sync",
                    ["worldDay"] = 10d,
                    ["portraitRosterFingerprint"] = "portrait_roster_self_test",
                    ["heroes"] = roster
                };
                Dictionary<string, object> sync = IdentitySynchronizeApi(syncPayload);
                WriteJsonObject(Path.Combine(campaignPath, "portrait_roster.json"), new Dictionary<string, object>
                {
                    ["version"] = 2,
                    ["campaignId"] = campaignId,
                    ["rosterFingerprint"] = "portrait_roster_self_test",
                    ["characters"] = new List<object>()
                });
                Dictionary<string, object> secondSync = IdentitySynchronizeApi(syncPayload);

                add("portrait_roster_resynchronizes_when_server_copy_is_missing",
                    ReadBool(sync, "portraitRosterSyncRequired", false)
                    && !ReadBool(secondSync, "portraitRosterSyncRequired", true),
                    "Identity synchronization requests a portrait roster upload when the server copy is missing and accepts the matching persisted copy.",
                    null);

                add("same_kingdom_requires_identification", IdentityTestState(campaignId, "player", "same_kingdom") == "" && IdentityTestState(campaignId, "same_kingdom", "player") == "", "Kingdom membership alone grants no identity knowledge in either direction.", null);
                Dictionary<string, object> swornLadyRosterRow =
                    new Dictionary<string, object>
                    {
                        ["hero_id"] = "sworn_lady",
                        ["clan_id"] = "sworn_house",
                        ["kingdom_id"] = "realm",
                        ["is_lord"] = 1,
                        ["is_ruler"] = 0,
                        ["family_ids_json"] = "[]"
                    };
                Dictionary<string, object> ownSovereignRosterRow =
                    new Dictionary<string, object>
                    {
                        ["hero_id"] = "own_sovereign",
                        ["clan_id"] = "ruling_house",
                        ["kingdom_id"] = "realm",
                        ["is_lord"] = 1,
                        ["is_ruler"] = 1,
                        ["family_ids_json"] = "[]"
                    };
                Dictionary<string, object> sameRealmPeerRosterRow =
                    new Dictionary<string, object>
                    {
                        ["hero_id"] = "same_realm_peer",
                        ["clan_id"] = "peer_house",
                        ["kingdom_id"] = "realm",
                        ["is_lord"] = 1,
                        ["is_ruler"] = 0,
                        ["family_ids_json"] = "[]"
                    };
                add("roster_own_sovereign_is_implicit_identity",
                    ImplicitIdentitySource(
                        swornLadyRosterRow,
                        ownSovereignRosterRow) == "own_sovereign"
                    && string.IsNullOrWhiteSpace(
                        ImplicitIdentitySource(
                            swornLadyRosterRow,
                            sameRealmPeerRosterRow)),
                    "A sworn noble's own sovereign is implicit identity knowledge in the compact roster, while another same-realm peer remains unidentified.",
                    null);
                add("foreign_nobles_unknown", IdentityTestState(campaignId, "foreign", "player") == "" && IdentityTestState(campaignId, "player", "foreign") == "", "Foreign nobles receive no automatic identity knowledge.", null);
                add("ruler_network_requires_identification", IdentityTestState(campaignId, "ruler", "foreign") == "" && IdentityTestState(campaignId, "foreign", "ruler") == "", "Ruler status does not create omniscient identity knowledge in either direction.", null);
                add("same_clan_and_family", IdentityTestState(campaignId, "player", "clanmate") == "verified" && IdentityTestState(campaignId, "player", "child") == "verified" && IdentityTestState(campaignId, "child", "player") == "verified", "Same-clan and immediate-family safety rules verify identity.", null);
                add("synchronization_idempotent", ReadBool(sync, "rosterChanged", false) && !ReadBool(secondSync, "rosterChanged", true), "An unchanged roster fingerprint does not rebuild the network.", secondSync);
                using (ReignDbConnection storageConnection = OpenCampaignConnection(campaignId))
                {
                    int storedSyntheticPairs = ReadInt(QuerySql(storageConnection,
                        "SELECT COUNT(*) AS count FROM acquaintances WHERE verification_source IN ('same_clan','immediate_family','same_kingdom_nobility','ruler_network');").FirstOrDefault(), "count", -1);
                    int rosterRows = ReadInt(QuerySql(storageConnection,
                        "SELECT COUNT(*) AS count FROM identity_roster;").FirstOrDefault(), "count", -1);
                    add("compact_roster_storage", storedSyntheticPairs == 0 && rosterRows == roster.Count,
                        "Automatic clan and immediate-family knowledge is derived from one roster row per hero instead of an O(N-squared) pair matrix.",
                        new Dictionary<string, object> { ["storedSyntheticPairs"] = storedSyntheticPairs, ["rosterRows"] = rosterRows });
                    HashSet<string> chemistryIndexes = new HashSet<string>(
                        QuerySql(storageConnection,
                            "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='relationship_pair_chemistry';")
                            .Select(row => ReadString(row, "name", "")),
                        StringComparer.OrdinalIgnoreCase);
                    add("court_popularity_uses_directional_pair_indexes",
                        chemistryIndexes.Contains("idx_pair_chemistry_hero_a")
                        && chemistryIndexes.Contains("idx_pair_chemistry_hero_b"),
                        "Court-popularity refreshes can seek relationship rows by either hero direction instead of scanning the full chemistry table per noble.",
                        new Dictionary<string, object> { ["indexes"] = chemistryIndexes.OrderBy(x => x).ToList() });
                }
                Dictionary<string, object> liveRealmObserver =
                    IdentityTestHero(
                        "live_realm_observer",
                        "Marvonara",
                        "observer_clan",
                        "new_kingdom",
                        true,
                        false);
                Dictionary<string, object> liveRealmSovereign =
                    IdentityTestHero(
                        "live_realm_sovereign",
                        "Gorigos",
                        "player_faction",
                        "new_kingdom",
                        true,
                        true);
                liveRealmObserver["currentCharm"] = 200;
                liveRealmSovereign["clanTier"] = 6;
                string recognizedSovereignEncounterId =
                    IdentityRecognitionEncounterForOutcome(
                        campaignId,
                        "live_realm_observer",
                        "live_realm_sovereign",
                        true,
                        0.80d);
                Dictionary<string, object> preRosterRealmEncounter =
                    IdentityEncounterApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["observerHeroStringId"] =
                                "live_realm_observer",
                            ["subjectHeroStringId"] =
                                "live_realm_sovereign",
                            ["canonicalName"] = "Gorigos",
                            ["observer"] = liveRealmObserver,
                            ["subject"] = liveRealmSovereign,
                            ["encounterId"] =
                                recognizedSovereignEncounterId,
                            ["worldDay"] = 1d,
                            ["playerText"] = "Good evening.",
                            ["contextBundles"] =
                                new List<Dictionary<string, object>>
                                {
                                    new Dictionary<string, object>
                                    {
                                        ["id"] =
                                            "current_settlement_facts",
                                        ["data"] =
                                            new Dictionary<string, object>
                                            {
                                                ["settlement"] =
                                                    new Dictionary<string, object>
                                                    {
                                                        ["settlementId"] =
                                                            "town_EW2",
                                                        ["name"] =
                                                            "Zeonica",
                                                        ["ownerClanId"] =
                                                            "player_faction",
                                                        ["ownerClanName"] =
                                                            "Bustamys",
                                                        ["kingdomId"] =
                                                            "new_kingdom"
                                                    }
                                            }
                                    }
                                }
                        });
                Dictionary<string, object> preRosterRealmView =
                    ReadDictionary(
                        preRosterRealmEncounter, "identityView")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> preRosterAuthority =
                    ReadDictionary(
                        preRosterRealmView, "authorityView")
                    ?? new Dictionary<string, object>();
                string sovereignPrompt =
                    BuildIdentityPromptBlock(
                        preRosterRealmView);
                add("sworn_lord_knows_current_sovereign",
                    ReadBool(
                        preRosterRealmView,
                        "canonicalNameAllowed", false)
                    && ReadString(
                        preRosterRealmView,
                        "usableName", "") == "Gorigos"
                    && ReadString(
                        preRosterRealmView,
                        "knowledgeSource", "")
                        == "own_sovereign"
                    && ReadInt(
                        preRosterRealmView,
                        "recognitionAttempts", -1) == 0
                    && ReadBool(
                        preRosterAuthority,
                        "realmSovereignKnown", false)
                    && ReadBool(
                        preRosterAuthority,
                        "subjectIsObserverSovereign", false)
                    && ReadBool(
                        preRosterAuthority,
                        "currentSettlementOwnerKnown", false)
                    && sovereignPrompt.Contains(
                        "observer's own current sovereign"),
                    "A sworn lord knows their own current sovereign without a recognition roll; the rule remains one-way and does not grant same-kingdom peer omniscience.",
                    preRosterRealmEncounter);

                add("recognition_probability_curve",
                    Math.Abs(IdentityRecognitionTierMultiplier(1) - 0.50d) < 0.000001d
                    && Math.Abs(IdentityRecognitionTierMultiplier(2) - 0.75d) < 0.000001d
                    && Math.Abs(IdentityRecognitionTierMultiplier(3) - 1d) < 0.000001d
                    && Math.Abs(IdentityRecognitionTierMultiplier(4) - 7d / 6d) < 0.000001d
                    && Math.Abs(IdentityRecognitionTierMultiplier(5) - 4d / 3d) < 0.000001d
                    && Math.Abs(IdentityRecognitionTierMultiplier(6) - 1.50d) < 0.000001d
                    && Math.Abs(Math.Min(0.80d, 100d / 250d * IdentityRecognitionTierMultiplier(1)) - 0.20d) < 0.000001d
                    && Math.Abs(Math.Min(0.80d, 100d / 250d * IdentityRecognitionTierMultiplier(3)) - 0.40d) < 0.000001d
                    && Math.Abs(Math.Min(0.80d, 100d / 250d * IdentityRecognitionTierMultiplier(6)) - 0.60d) < 0.000001d
                    && Math.Abs(Math.Min(0.80d, 200d / 250d * IdentityRecognitionTierMultiplier(6)) - 0.80d) < 0.000001d,
                    "Charm 100/200 and clan tiers 1/3/6 produce the configured 20/40/60 and capped 40/80/80 percent chances.",
                    null);
                double stableRollA = StableIdentityRecognitionRoll(
                    campaignId, "observer", "subject", "encounter");
                double stableRollB = StableIdentityRecognitionRoll(
                    campaignId, "observer", "subject", "encounter");
                add("recognition_roll_is_stable",
                    stableRollA >= 0d && stableRollA < 1d
                    && Math.Abs(stableRollA - stableRollB) < 0.000000000001d,
                    "The recognition roll is stable for the same campaign, pair, encounter, and rules version.",
                    stableRollA);

                Dictionary<string, object> marvonara =
                    IdentityTestHero(
                        "marvonara",
                        "Marvonara Damarorides",
                        "clan_empire_west_2",
                        "new_kingdom",
                        true,
                        false);
                marvonara["isFemale"] = true;
                marvonara["sex"] = "female";
                Dictionary<string, object> gorigos =
                    IdentityTestHero(
                        "main_hero",
                        "Gorigos",
                        "player_faction",
                        "new_kingdom",
                        true,
                        true);
                gorigos["clanTier"] = 6;
                gorigos["kingdomName"] = "Paltos";
                marvonara["kingdomName"] = "Paltos";
                Dictionary<string, object> marvonaraNative =
                    new Dictionary<string, object>
                    {
                        ["authoritative"] = true,
                        ["observedWorldDay"] = 50d,
                        ["observer"] = marvonara,
                        ["subject"] = gorigos,
                        ["settlement"] =
                            new Dictionary<string, object>
                            {
                                ["settlementId"] = "town_EW2",
                                ["name"] = "Zeonica",
                                ["ownerClanId"] = "player_faction",
                                ["ownerClanName"] = "Bustamys",
                                ["kingdomId"] = "new_kingdom",
                                ["kingdomName"] = "Paltos",
                                ["governorHeroId"] = "governor_other"
                            },
                        ["diplomacy"] =
                            new Dictionary<string, object>
                            {
                                ["playerKingdom"] =
                                    new Dictionary<string, object>
                                    {
                                        ["available"] = true,
                                        ["name"] = "Paltos",
                                        ["warCount"] = 2
                                    },
                                ["playerEnemyKingdoms"] =
                                    new List<Dictionary<string, object>>
                                    {
                                        new Dictionary<string, object>
                                        {
                                            ["kingdomId"] = "enemy_a",
                                            ["name"] = "Enemy A"
                                        },
                                        new Dictionary<string, object>
                                        {
                                            ["kingdomId"] = "enemy_b",
                                            ["name"] = "Enemy B"
                                        }
                                    }
                            }
                    };
                Dictionary<string, object> marvonaraIdentity =
                    BuildIdentityView(
                        new Dictionary<string, object>
                        {
                            ["identity_state"] = "verified",
                            ["canonical_name"] = "Gorigos",
                            ["verification_source"] =
                                "recognition_fixture",
                            ["confidence"] = 1d
                        },
                        new Dictionary<string, object>
                        {
                            ["observer"] = marvonara,
                            ["subject"] = gorigos,
                            ["nativePoliticalContext"] =
                                marvonaraNative
                        });
                Dictionary<string, object> marvonaraAuthority =
                    ReadDictionary(
                        marvonaraIdentity, "authorityView")
                    ?? new Dictionary<string, object>();
                string marvonaraPrompt =
                    BuildIdentityPromptBlock(
                        marvonaraIdentity);
                add("marvonara_authority_invariant",
                    ReadBool(
                        marvonaraAuthority,
                        "subjectIsObserverSovereign", false)
                    && ReadBool(
                        marvonaraAuthority,
                        "currentSettlementOwnerKnown", false)
                    && !ReadBool(
                        marvonaraAuthority,
                        "observerClanOwnsCurrentSettlement", true)
                    && ReadInt(
                        marvonaraAuthority,
                        "currentEnemyKingdomCount", -1) == 2
                    && marvonaraPrompt.Contains(
                        "observer's own current sovereign")
                    && marvonaraPrompt.Contains(
                        "Subject primary current role: king")
                    && marvonaraPrompt.Contains(
                        "Observer-relative authority relationship: subject_is_observer_sovereign")
                    && marvonaraPrompt.Contains(
                        "current sovereign of Paltos")
                    && !marvonaraPrompt.Contains(
                        "current sovereign of kingdom `new_kingdom`")
                    && marvonaraPrompt.Contains(
                        "must not evade, redirect, or omit the answer")
                    && marvonaraPrompt.Contains(
                        "Do not volunteer war counts"),
                    "The exact live failure now has observer-relative sovereignty, player-clan ownership, and distinct current enemy kingdoms in an always-on prompt invariant.",
                    marvonaraIdentity);
                Dictionary<string, object> institutionalUnknownIdentity =
                    BuildIdentityView(
                        null,
                        new Dictionary<string, object>
                        {
                            ["mode"] = "social_event",
                            ["canonicalName"] = "Gorigos",
                            ["observer"] = marvonara,
                            ["subject"] = gorigos,
                            ["nativePoliticalContext"] = marvonaraNative
                        });
                Dictionary<string, object> institutionalUnknownAuthority =
                    ReadDictionary(institutionalUnknownIdentity,
                        "authorityView")
                    ?? new Dictionary<string, object>();
                string institutionalUnknownPrompt =
                    BuildIdentityPromptBlock(
                        institutionalUnknownIdentity);
                add("institutional_office_survives_unknown_personal_identity",
                    !ReadBool(institutionalUnknownIdentity,
                        "canonicalNameAllowed", true)
                    && ReadBool(institutionalUnknownAuthority,
                        "publicOfficeKnown", false)
                    && ReadBool(institutionalUnknownAuthority,
                        "subjectIsObserverSovereign", false)
                    && ReadString(institutionalUnknownAuthority,
                        "subjectPrimaryRole", "")
                        == "realm_sovereign"
                    && ReadStringList(institutionalUnknownAuthority,
                        "validFormalAddresses").Contains("Your Grace")
                    && institutionalUnknownPrompt.Contains(
                        "canonical name remains forbidden")
                    && institutionalUnknownPrompt.Contains(
                        "Valid formal addresses:"),
                    "A failed face/name recognition state cannot erase a sovereign office established by an official same-realm event; the NPC uses a title without leaking the name.",
                    institutionalUnknownIdentity);

                var michael = new Dictionary<string, object>(gorigos) { ["name"] = "Michael", ["clanName"] = "Howarton", ["kingdomName"] = "Howarton" };
                foreach (string valid in new[] { "Michael", "Michael Howarton", "Michael of Howarton", "  michael   howarton  " })
                    add("qualified_introduction_" + valid.Trim().Replace(" ", "_"),
                        AuthoritativeIntroductionSource(valid, michael).Length > 0, "Authoritative names accept case and spacing.", valid);
                foreach (string invalid in new[] { "Howarton", "Michael Nobody", "Michael Howarton the Great", "Mich", "Howarton Michael" })
                    add("invalid_introduction_" + invalid.Replace(" ", "_"),
                        AuthoritativeIntroductionSource(invalid, michael).Length == 0, "No arbitrary suffix, partial name or clan-only introduction.", invalid);
                add("ochivos_full_name_introduction",
                    AuthoritativeIntroductionSource(ExtractAuthoritativeSelfIntroduction("*I glance over at him* Nice to meet you Ochivos, I am Michael Howarton.", michael), michael)
                        == "authoritative_qualified_self_introduction", "The original full-name introduction is authoritative.", null);
                var nameFirstObserver = IdentityTestHero(
                    "name_first_observer", "Hulara", "hulara_clan", "khuzait", true, false);
                var nameFirstPayload = new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["observerHeroStringId"] = "name_first_observer",
                    ["subjectHeroStringId"] = "main_hero",
                    ["observer"] = nameFirstObserver,
                    ["subject"] = michael,
                    ["attendees"] = new List<Dictionary<string, object>> { nameFirstObserver, michael },
                    ["mode"] = "social_event",
                    ["encounterId"] = "name_first_social_event",
                    ["worldDay"] = 51d,
                    ["playerText"] = "*I take a seat* Michael of Howarton, Im sorry I am a traveller, I never know where I will be welcome."
                };
                Dictionary<string, object> nameFirstEncounter = IdentityEncounterApi(nameFirstPayload);
                Dictionary<string, object> nameFirstView = ReadDictionary(
                    nameFirstEncounter, "identityView") ?? new Dictionary<string, object>();
                add("name_first_social_event_introduction",
                    ReadString(nameFirstEncounter, "introductionDetected", "") == "Michael of Howarton"
                    && ReadString(nameFirstView, "identityState", "") == "verified"
                    && ReadString(nameFirstView, "usableName", "") == "Michael"
                    && ReadString(nameFirstView, "knowledgeSource", "")
                        == "authoritative_qualified_self_introduction",
                    "The exact name-first feast introduction verifies identity before the first NPC response.",
                    nameFirstEncounter);
                using (ReignDbConnection nameFirstConnection = OpenCampaignConnection(campaignId))
                    add("name_first_social_event_persists",
                        IdentityStateVerified(ReadAcquaintance(
                            nameFirstConnection, "name_first_observer", "main_hero")),
                        "The introduced identity survives a fresh database connection.", null);
                var sameNameAttendee = IdentityTestHero(
                    "same_name_attendee", "Michael", "other_clan", "khuzait", false, false);
                var ambiguousNamePayload = new Dictionary<string, object>(nameFirstPayload)
                {
                    ["attendees"] = new List<Dictionary<string, object>>
                        { nameFirstObserver, michael, sameNameAttendee }
                };
                add("name_first_introduction_false_positive_guards",
                    ExtractNameFirstSelfIntroduction(
                        "Hulara, I am here for the feast.", michael, nameFirstPayload) == ""
                    && ExtractNameFirstSelfIntroduction(
                        "I heard Michael won the tournament.", michael, nameFirstPayload) == ""
                    && ExtractNameFirstSelfIntroduction(
                        "Michael, good day.", michael, ambiguousNamePayload) == ""
                    && ExtractNameFirstSelfIntroduction(
                        "Hulara, good day.", nameFirstObserver, nameFirstPayload) == ""
                    && ExtractNameFirstSelfIntroduction(
                        "Michael, good day.", michael, nameFirstPayload) == "Michael",
                    "Name-first identification excludes third-person mentions, addresses to another present person and non-player subjects.", null);
                var ochivos = IdentityTestHero("ochivos", "Ochivos the Wheeler", "", "", false, false);
                var civic = new Dictionary<string, object> { ["authoritative"] = true, ["available"] = true,
                    ["heroStringId"] = "ochivos", ["settlementId"] = "town_EW2", ["kingdomId"] = "new_kingdom",
                    ["sovereignHeroStringId"] = "main_hero", ["localEncounter"] = true, ["source"] = "native_home_settlement" };
                var civicNative = new Dictionary<string, object>(marvonaraNative) { ["observer"] = ochivos, ["subject"] = michael, ["observerCivicAffiliation"] = civic };
                var civicContext = new Dictionary<string, object> { ["mode"] = "social_event", ["observer"] = ochivos, ["subject"] = michael, ["nativePoliticalContext"] = civicNative };
                var civicView = BuildIdentityView(null, civicContext);
                var civicAuthority = ReadDictionary(civicView, "authorityView");
                add("ochivos_local_office_not_personal_identity",
                    !ReadBool(civicView, "canonicalNameAllowed", true) && ReadBool(civicAuthority, "subjectIsObserverSovereign", false)
                    && !ReadBool(civicAuthority, "sameKingdom", true), "Civic office never grants formal membership or personal name knowledge.", civicView);
                civic["localEncounter"] = false;
                add("visiting_notable_no_false_local_office", !ReadBool(ReadDictionary(BuildIdentityView(null, civicContext), "authorityView"),
                    "publicOfficeKnown", true), "Visiting presence alone grants no civic office recognition.", null);
                civic["localEncounter"] = true;
                civic["sovereignHeroStringId"] = "successor";
                add("civic_succession_recalculates", !ReadBool(ReadDictionary(BuildIdentityView(null, civicContext), "authorityView"),
                    "publicOfficeKnown", true), "A former sovereign does not retain the current title through civic evidence.", null);
                civic["sovereignHeroStringId"] = "main_hero";
                civic["kingdomId"] = "conqueror";
                add("civic_conquest_recalculates", !ReadBool(ReadDictionary(BuildIdentityView(null, civicContext), "authorityView"),
                    "publicOfficeKnown", true), "A realm mismatch cannot confer civic authority.", null);
                using (ReignDbConnection introductionConnection = OpenCampaignConnection(campaignId))
                {
                    EnsureIdentitySchema(introductionConnection);
                    UpsertClaimedIdentity(introductionConnection, "ochivos", "main_hero", "Michael", "Michael Howarton", "self_introduction", "main_hero", "legacy_intro", 49d,
                        new Dictionary<string, object> { ["playerText"] = "I am Michael Howarton." });
                }
                var reconciledEncounter = IdentityEncounterApi(new Dictionary<string, object> {
                    ["campaignId"] = campaignId, ["observer"] = ochivos, ["subject"] = michael,
                    ["observerHeroStringId"] = "ochivos", ["subjectHeroStringId"] = "main_hero",
                    ["encounterId"] = "qualified_reconciliation", ["worldDay"] = 50d });
                using (ReignDbConnection reopenedIntroductionConnection = OpenCampaignConnection(campaignId))
                {
                    var savedIntroduction = ReadAcquaintance(reopenedIntroductionConnection, "ochivos", "main_hero");
                    add("qualified_claim_reconciliation_persists", IdentityStateVerified(savedIntroduction)
                        && ReadString(savedIntroduction, "verification_source", "") == "authoritative_qualified_self_introduction_reconciled",
                        "An evidence-backed legacy introduction is verified on encounter and survives a fresh database connection.", reconciledEncounter);
                    add("qualified_claim_keeps_original_evidence", QuerySql(reopenedIntroductionConnection,
                        "SELECT evidence_id FROM identity_evidence WHERE observer_id='ochivos' AND subject_id='main_hero' AND source='self_introduction';").Count > 0,
                        "Reconciliation appends verification without deleting the original claim evidence.", null);
                }
                add("marvonara_false_claims_are_rejected",
                    VerifiedPoliticalAuthorityContradiction(
                        new Dictionary<string, object>
                        {
                            ["reply"] =
                                "A sovereign at war on twelve fronts walks into my hall."
                        },
                        marvonaraIdentity)
                    && !VerifiedPoliticalAuthorityContradiction(
                        new Dictionary<string, object>
                        {
                            ["reply"] =
                                "Gorigos, I know you are my sovereign. Welcome to Zeonica."
                        },
                        marvonaraIdentity),
                    "False local ownership and an inflated current-war count trigger repair, while a grounded observer-relative acknowledgment passes.",
                    null);
                add("cumulative_roles_and_conversation_venue_are_enforced",
                    VerifiedPoliticalAuthorityContradiction(
                        new Dictionary<string, object>
                        {
                            ["reply"] =
                                "You are a king. That makes you a ruler, not a lord."
                        },
                        marvonaraIdentity)
                    && VerifiedPoliticalAuthorityContradiction(
                        new Dictionary<string, object>
                        {
                            ["reply"] =
                                "1084 Winter — Amitatys\n\nThis is Amitatys, not your clan's city."
                        },
                        marvonaraIdentity)
                    && !VerifiedPoliticalAuthorityContradiction(
                        new Dictionary<string, object>
                        {
                            ["reply"] =
                                "1084 Winter — Zeonica\n\nYou are both king and lord, and you are my sovereign here in Zeonica."
                        },
                        marvonaraIdentity),
                    "A ruler cannot lose another simultaneously listed public role, and a remote observer's home cannot replace the authoritative conversation settlement.",
                    null);

                Dictionary<string, object> independentLord =
                    IdentityTestHero(
                        "independent_lord",
                        "Acthon",
                        "independent_clan",
                        "",
                        true,
                        false);
                Dictionary<string, object> foreignObserver =
                    IdentityTestHero(
                        "foreign_observer",
                        "Aziman Damasaqani",
                        "banu_sarmal",
                        "aserai",
                        true,
                        false);
                Dictionary<string, object> independentLordIdentity =
                    BuildIdentityView(
                        new Dictionary<string, object>
                        {
                            ["identity_state"] = "verified",
                            ["canonical_name"] = "Acthon",
                            ["verification_source"] =
                                "exact_self_introduction",
                            ["confidence"] = 1d
                        },
                        new Dictionary<string, object>
                        {
                            ["observer"] = foreignObserver,
                            ["subject"] = independentLord,
                            ["nativePoliticalContext"] =
                                new Dictionary<string, object>
                                {
                                    ["authoritative"] = true,
                                    ["observer"] = foreignObserver,
                                    ["subject"] = independentLord,
                                    ["settlement"] =
                                        new Dictionary<string, object>
                                        {
                                            ["settlementId"] =
                                                "castle_A3",
                                            ["name"] =
                                                "Ain Baliq Castle",
                                            ["ownerClanId"] =
                                                "banu_sarmal",
                                            ["ownerClanName"] =
                                                "Banu Sarmal",
                                            ["kingdomId"] = "aserai",
                                            ["governorHeroId"] = ""
                                        }
                                }
                        });
                string independentLordPrompt =
                    BuildIdentityPromptBlock(
                        independentLordIdentity);
                add("independent_lord_direct_authority_answer_contract",
                    independentLordPrompt.Contains(
                        "Subject primary current role: lord")
                    && independentLordPrompt.Contains(
                        "Observer primary current role: lord")
                    && independentLordPrompt.Contains(
                        "Observer-relative authority relationship: known_foreign_or_independent_person")
                    && independentLordPrompt.Contains(
                        "must not evade, redirect, or omit the answer")
                    && independentLordPrompt.Contains(
                        "not your sovereign")
                    && independentLordPrompt.Contains(
                        "Ain Baliq Castle is held by clan Banu Sarmal"),
                    "A verified independent lord receives explicit role, observer-relative authority, settlement ownership, and a direct-answer instruction matching the live Aziman failure.",
                    independentLordIdentity);

                List<Dictionary<string, object>> roleFixtures =
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = "Queen",
                            ["isFemale"] = true,
                            ["isRuler"] = true,
                            ["isLord"] = true
                        },
                        new Dictionary<string, object>
                        {
                            ["name"] = "Governor",
                            ["isFemale"] = true,
                            ["isLord"] = true,
                            ["governorOfSettlementId"] = "town"
                        },
                        new Dictionary<string, object>
                        {
                            ["name"] = "Notable",
                            ["isNotable"] = true,
                            ["occupation"] = "Merchant"
                        },
                        new Dictionary<string, object>
                        {
                            ["name"] = "Wanderer",
                            ["isWanderer"] = true
                        },
                        new Dictionary<string, object>
                        {
                            ["name"] = "Commoner",
                            ["occupation"] = "Commoner"
                        }
                    };
                List<string> roleProjection = roleFixtures
                    .Select(fixture => string.Join(
                        ",",
                        BuildAuthoritativePublicRoles(
                            fixture)))
                    .ToList();
                add("authoritative_role_projection",
                    roleProjection[0] == "queen,lady"
                    && roleProjection[1] == "lady,governor"
                    && roleProjection[2] == "notable"
                    && roleProjection[3] == "wanderer"
                    && roleProjection[4] ==
                        "commoner_or_unaffiliated",
                    "King/queen, lord/lady, governor, notable, wanderer, and commoner roles are additive and derived from current native facts.",
                    roleProjection);

                Dictionary<string, object> transitionObserver =
                    IdentityTestHero(
                        "transition_observer",
                        "Witness",
                        "witness_clan",
                        "realm_a",
                        true,
                        false);
                Dictionary<string, object> transitionSubject =
                    IdentityTestHero(
                        "transition_subject",
                        "Later King",
                        "later_king_clan",
                        "realm_b",
                        true,
                        false);
                IdentityEncounterApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["observerHeroStringId"] =
                            "transition_observer",
                        ["subjectHeroStringId"] =
                            "transition_subject",
                        ["canonicalName"] = "Later King",
                        ["observer"] = transitionObserver,
                        ["subject"] = transitionSubject,
                        ["encounterId"] =
                            "transition_unknown",
                        ["worldDay"] = 2d,
                        ["playerText"] = "Good day."
                    });
                transitionSubject["kingdomId"] = "realm_a";
                transitionSubject["isRuler"] = true;
                Dictionary<string, object> transitioned =
                    IdentityEncounterApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["observerHeroStringId"] =
                                "transition_observer",
                            ["subjectHeroStringId"] =
                                "transition_subject",
                            ["canonicalName"] = "Later King",
                            ["observer"] = transitionObserver,
                            ["subject"] = transitionSubject,
                            ["encounterId"] =
                                "transition_sovereign",
                            ["worldDay"] = 3d,
                            ["playerText"] = "We meet again."
                        });
                Dictionary<string, object> transitionedView =
                    ReadDictionary(transitioned, "identityView")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> transitionedAuthority =
                    ReadDictionary(
                        transitionedView, "authorityView")
                    ?? new Dictionary<string, object>();
                add("sovereign_transition_still_requires_identification",
                    ReadString(
                        transitionedView,
                        "identityState", "")
                        == "encountered_unknown"
                    && !ReadBool(
                        transitionedAuthority,
                        "subjectIsObserverSovereign", true)
                    && ReadStringList(
                        transitionedAuthority,
                        "recognizedRoles").Count == 0,
                    "Becoming a same-realm sovereign does not bypass the observer's required introduction or recognition roll.",
                    transitioned);

                Dictionary<string, object> titleIntroduction =
                    IdentityEncounterApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["observerHeroStringId"] =
                                "title_intro_observer",
                            ["subjectHeroStringId"] =
                                "title_intro_subject",
                            ["canonicalName"] = "Gorigos",
                            ["observer"] =
                                IdentityTestHero(
                                    "title_intro_observer",
                                    "Marvonara",
                                    "observer_clan",
                                    "realm_a",
                                    true,
                                    false),
                            ["subject"] =
                                IdentityTestHero(
                                    "title_intro_subject",
                                    "Gorigos",
                                    "player_clan",
                                    "realm_b",
                                    true,
                                    false),
                            ["encounterId"] =
                                "natural_title_introduction",
                            ["worldDay"] = 4d,
                            ["playerText"] =
                                "Gorigos, the lord of this city.",
                            ["transcript"] =
                                new List<Dictionary<string, object>>
                                {
                                    new Dictionary<string, object>
                                    {
                                        ["role"] = "npc",
                                        ["text"] =
                                            "Who are you?"
                                    }
                                }
                        });
                Dictionary<string, object> titleIntroductionView =
                    ReadDictionary(
                        titleIntroduction, "identityView")
                    ?? new Dictionary<string, object>();
                add("natural_name_and_title_answer_detected",
                    ReadString(
                        titleIntroduction,
                        "introductionDetected", "")
                        == "Gorigos"
                    && ReadString(
                        titleIntroductionView,
                        "identityState", "") == "verified"
                    && ReadString(
                        titleIntroductionView,
                        "usableName", "") == "Gorigos",
                    "An exact canonical name followed by an appositive title verifies identity when answering an identity question without treating arbitrary NPC address as a player name."
                        + " Observed introduction='"
                        + ReadString(titleIntroduction, "introductionDetected", "")
                        + "', state='"
                        + ReadString(titleIntroductionView, "identityState", "")
                        + "', usable='"
                        + ReadString(titleIntroductionView, "usableName", "")
                        + "'.",
                    titleIntroduction);

                Dictionary<string, object> groupIdentityPayload =
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["correlationId"] =
                            "identity_group_privacy",
                        ["mode"] = "party_chat",
                        ["conversationSessionId"] =
                            "identity_group_session",
                        ["playerHeroStringId"] = "player",
                        ["worldDay"] = 9d,
                        ["participantProfiles"] =
                            new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object>(
                                    player,
                                    StringComparer.OrdinalIgnoreCase)
                                {
                                    ["role"] = "player"
                                },
                                clanmate,
                                foreign
                            },
                        ["activeHeroIds"] =
                            new List<object>
                            {
                                "clanmate",
                                "foreign"
                            }
                    };
                List<Dictionary<string, object>> groupViews =
                    EnsureObserverParticipantIdentityViews(
                        campaignId,
                        groupIdentityPayload,
                        "player");
                List<Dictionary<string, object>>
                    secondObserverViews =
                        EnsureObserverParticipantIdentityViews(
                            campaignId,
                            groupIdentityPayload,
                            "clanmate");
                List<Dictionary<string, object>>
                    restoredPlayerViews =
                        EnsureObserverParticipantIdentityViews(
                            campaignId,
                            groupIdentityPayload,
                            "player");
                string observerSafeTranscript =
                    FormatEventLinesForObserver(
                        new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["speakerHeroStringId"] =
                                    "clanmate",
                                ["speaker"] = "Alynneth",
                                ["role"] = "npc",
                                ["text"] =
                                    "The eastern road is safer."
                            },
                            new Dictionary<string, object>
                            {
                                ["speakerHeroStringId"] =
                                    "foreign",
                                ["speaker"] = "Derthert",
                                ["role"] = "npc",
                                ["text"] =
                                    "I disagree about the road."
                            }
                        },
                        groupIdentityPayload,
                        "player",
                        new Dictionary<string, object>
                        {
                            ["usableName"] = "Aeric",
                            ["canonicalNameAllowed"] = true
                        });
                add("group_identity_is_observer_relative",
                    groupViews.Count == 2
                    && secondObserverViews.All(row =>
                        ReadString(
                            row,
                            "observerHeroStringId",
                            "").Equals(
                                "clanmate",
                                StringComparison.OrdinalIgnoreCase))
                    && restoredPlayerViews.All(row =>
                        ReadString(
                            row,
                            "observerHeroStringId",
                            "").Equals(
                                "player",
                                StringComparison.OrdinalIgnoreCase))
                    && observerSafeTranscript.Contains(
                        "Alynneth [id=clanmate]")
                    && !observerSafeTranscript.Contains(
                        "Derthert")
                    && observerSafeTranscript.Contains(
                        "the stranger")
                    && IdentityTestState(
                        campaignId,
                        "foreign",
                        "player") == "",
                    "A shared transcript reveals a same-clan participant but withholds an unidentified NPC's canonical name without creating reverse-direction knowledge.",
                    observerSafeTranscript);
                UpdateSharedGroupConversationState(
                    campaignId,
                    groupIdentityPayload,
                    "foreign",
                    "Derthert",
                    "I am Derthert.",
                    "identity_group_session",
                    100);
                string safeCachedSpeaker =
                    BuildSharedGroupConversationPrompt(
                        campaignId,
                        groupIdentityPayload,
                        "player",
                        new List<Dictionary<string, object>>(),
                        "Continue.");
                add("group_self_introduction_is_witnessed_and_directional",
                    IdentityTestState(
                        campaignId,
                        "player",
                        "foreign") == "verified"
                    && IdentityTestState(
                        campaignId,
                        "clanmate",
                        "foreign") == "verified"
                    && IdentityTestState(
                        campaignId,
                        "foreign",
                        "player") == ""
                    && safeCachedSpeaker.Contains(
                        "Last contributing speaker: the stranger")
                    && !safeCachedSpeaker.Contains(
                        "Last contributing speaker: Derthert"),
                    "An exact NPC self-introduction is persisted for every present observer, remains directional, and cannot retroactively leak through a stale cached observer view in the same prompt.",
                    safeCachedSpeaker);
                Dictionary<string, object> debugKnown = IdentityDebugKnowEveryoneApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["correlationId"] = "identity_self_test_know_everyone",
                    ["worldDay"] = 10d,
                    ["testMode"] = true,
                    ["observer"] = player,
                    ["heroes"] = roster
                });
                add("debug_know_everyone_player_only", ReadBool(debugKnown, "ok", false)
                    && ReadInt(debugKnown, "known", 0) == roster.Count - 1
                    && ReadInt(debugKnown, "requested", 0) == roster.Count - 1
                    && ReadBool(debugKnown, "complete", false)
                    && IdentityTestState(campaignId, "player", "foreign") == "verified"
                    && IdentityTestState(campaignId, "foreign", "player") == "", "The guarded debug helper makes only the player know every living roster hero.", debugKnown);

                Dictionary<string, object> encounterBase = new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["observerHeroStringId"] = "foreign",
                    ["subjectHeroStringId"] = "player",
                    ["canonicalName"] = "Aeric",
                    ["observer"] = foreign,
                    ["subject"] = player,
                    ["mode"] = "dialogue",
                    ["worldDay"] = 11d
                };
                Dictionary<string, object> firstEncounter = IdentityEncounterApi(new Dictionary<string, object>(encounterBase) { ["encounterId"] = "encounter_one", ["playerText"] = "Good day." });
                Dictionary<string, object> repeatedEncounter = IdentityEncounterApi(new Dictionary<string, object>(encounterBase) { ["encounterId"] = "encounter_one", ["playerText"] = "Still here." });
                Dictionary<string, object> nextEncounter = IdentityEncounterApi(new Dictionary<string, object>(encounterBase) { ["encounterId"] = "encounter_two", ["playerText"] = "We meet again." });
                Dictionary<string, object> unknownView = ReadDictionary(firstEncounter, "identityView") ?? new Dictionary<string, object>();
                Dictionary<string, object> nextView = ReadDictionary(nextEncounter, "identityView") ?? new Dictionary<string, object>();
                Dictionary<string, object> unknownAuthority =
                    ReadDictionary(unknownView, "authorityView")
                    ?? new Dictionary<string, object>();
                string unknownPrompt =
                    BuildIdentityPromptBlock(unknownView);
                add("unknown_prompt_view",
                    !ReadBool(
                        unknownView, "canonicalNameAllowed", true)
                    && ReadString(
                        unknownView, "usableName", "")
                        .IndexOf(
                            "Aeric",
                            StringComparison.OrdinalIgnoreCase) < 0
                    && ReadString(
                        unknownAuthority,
                        "subjectPrimaryRole", "")
                        == "identity_unverified"
                    && ReadBool(
                        unknownView,
                        "subjectSexKnown", false)
                    && ReadString(
                        unknownView,
                        "subjectSex", "") == "male"
                    && unknownPrompt.Contains(
                        "Subject visible sex: male")
                    && unknownPrompt.Contains(
                        "does not reveal or prove a name, noble title")
                    && !unknownPrompt.Contains(
                        "Subject primary current role: lord"),
                    "Unknown observers receive a scene-safe label and authoritative visible sex, but no hidden native identity or role before identification.",
                    unknownView);
                Dictionary<string, object> unknownFemaleView =
                    BuildIdentityView(
                        null,
                        new Dictionary<string, object>
                        {
                            ["mode"] = "party_chat",
                            ["canonicalName"] = "Hidden Woman",
                            ["subject"] =
                                new Dictionary<string, object>
                                {
                                    ["heroStringId"] =
                                        "unknown_female_player",
                                    ["name"] = "Hidden Woman",
                                    ["isFemale"] = true,
                                    ["isLord"] = true
                                }
                        });
                string unknownFemalePrompt =
                    BuildIdentityPromptBlock(unknownFemaleView);
                add("unknown_female_sex_is_visible_without_role_leak",
                    ReadString(
                        unknownFemaleView,
                        "subjectSex", "") == "female"
                    && unknownFemalePrompt.Contains(
                        "Subject visible sex: female")
                    && unknownFemalePrompt.Contains(
                        "Subject primary current role: identity_unverified")
                    && !unknownFemalePrompt.Contains(
                        "Recognized current roles: lady"),
                    "Party and event prompts can preserve a woman's visible sex without leaking her unknown name or inferring the title of lady.",
                    unknownFemaleView);
                add("recognition_is_once_per_encounter_with_cooldown",
                    ReadInt(ReadDictionary(repeatedEncounter, "identityView"), "recognitionAttempts", -1) == 1
                    && ReadInt(nextView, "recognitionAttempts", -1) == 1
                    && ReadString(
                        ReadDictionary(
                            nextEncounter,
                            "recognition"),
                        "status", "") == "cooldown"
                    && !ReadBool(ReadDictionary(nextEncounter, "recognition"), "recognized", true)
                    && !ReadBool(nextView, "canonicalNameAllowed", true),
                    "A failed deterministic recognition roll is not duplicated by transport retries or rapid session reopening.", nextEncounter);

                Dictionary<string, object> claim = IdentityEncounterApi(new Dictionary<string, object>(encounterBase) { ["encounterId"] = "encounter_three", ["playerText"] = "My name is Rowan." });
                Dictionary<string, object> claimView = ReadDictionary(claim, "identityView") ?? new Dictionary<string, object>();
                add("false_alias_claimed", ReadString(claimView, "identityState", "") == "claimed" && ReadString(claimView, "usableName", "") == "Rowan" && !ReadBool(claimView, "canonicalNameAllowed", true), "A self-introduced alias remains usable but unverified.", claimView);
                string verifiedAliasPrompt = BuildIdentityPromptBlock(
                    new Dictionary<string, object>
                    {
                        ["identityState"] = "verified",
                        ["usableName"] = "Aeric",
                        ["claimedName"] = "Rowan",
                        ["canonicalNameAllowed"] = true,
                        ["instruction"] =
                            "The observer knows the verified identity Aeric."
                    });
                add("verified_identity_dominates_earlier_alias",
                    verifiedAliasPrompt.Contains(
                        "Earlier conflicting claimed name: Rowan")
                    && verifiedAliasPrompt.Contains(
                        "does not replace the verified identity Aeric")
                    && verifiedAliasPrompt.Contains(
                        "Never address the person as the earlier claim"),
                    "A stale or false earlier alias remains auditable but cannot replace the later verified canonical identity in the prompt.",
                    verifiedAliasPrompt);
                string sanitizedVerifiedPrompt =
                    SanitizePromptForIdentity(
                        verifiedAliasPrompt
                            + "\nCURRENT PLAN: Refuse Rowan until he explains himself."
                            + "\nPLAYER TEXT: I claim my rank entitles me to your time.",
                        "I claim my rank entitles me to your time.",
                        "Aeric",
                        new Dictionary<string, object>
                        {
                            ["identityState"] = "verified",
                            ["usableName"] = "Aeric",
                            ["claimedName"] = "Rowan",
                            ["canonicalNameAllowed"] = true,
                            ["instruction"] =
                                "The observer knows the verified identity Aeric."
                        });
                add("verified_identity_sanitizes_stale_alias_context",
                    sanitizedVerifiedPrompt.Contains(
                        "CURRENT PLAN: Refuse Aeric")
                    && Regex.Matches(
                        sanitizedVerifiedPrompt,
                        @"(?<![\p{L}\p{N}])Rowan(?![\p{L}\p{N}])",
                        RegexOptions.IgnoreCase).Count == 1,
                    "A verified canonical identity rewrites stale alias-bearing state while preserving the one authoritative attributed identity record.",
                    sanitizedVerifiedPrompt);
                add("introduction_false_positive_guard", string.IsNullOrWhiteSpace(ExtractSelfIntroducedName("I am a lord.")) && string.IsNullOrWhiteSpace(ExtractSelfIntroducedName("I am ready.")) && string.IsNullOrWhiteSpace(ExtractSelfIntroducedName("Yes, I am here for the tournament.")) && ExtractSelfIntroducedName("I am Aeric.") == "Aeric", "Occupations, ordinary states, and conversational openers are not mistaken for names, while an explicit capitalized identity remains valid.", null);
                add("direct_address_is_not_self_introduction",
                    string.IsNullOrWhiteSpace(ExtractSelfIntroducedName("Menor, I am giving you this silver cup as a sincere gift."))
                    && ExtractSelfIntroducedName("Osarios, Im actually here for the tournament.", true) == "Osarios",
                    "A name-first address to an NPC cannot rename the player; name-first answers remain available only when an NPC actually requested the player's identity.", null);
                using (ReignDbConnection identityConnection = OpenCampaignConnection(campaignId))
                {
                    UpsertClaimedIdentity(identityConnection, "alias_cleanup_observer", "player", "Aeric", "Menor", "self_introduction", "player",
                        "legacy_address_encounter", 11d, new Dictionary<string, object>
                        {
                            ["playerText"] = "Menor, I am giving you this silver cup as a sincere gift.",
                            ["transcript"] = new List<Dictionary<string, object>>()
                        });
                }
                Dictionary<string, object> repairedAlias = IdentityEncounterApi(new Dictionary<string, object>(encounterBase)
                {
                    ["observerHeroStringId"] = "alias_cleanup_observer", ["observer"] = new Dictionary<string, object> { ["heroStringId"] = "alias_cleanup_observer", ["name"] = "Witness" },
                    ["encounterId"] = "explicit_repair_encounter", ["playerText"] = "My name is Aeric."
                });
                Dictionary<string, object> repairedAliasView = ReadDictionary(repairedAlias, "identityView") ?? new Dictionary<string, object>();
                List<string> repairedAliases;
                using (ReignDbConnection identityConnection = OpenCampaignConnection(campaignId))
                {
                    repairedAliases = TextListFromJson(ReadString(ReadStoredAcquaintance(identityConnection, "alias_cleanup_observer", "player"), "aliases_json", "[]"));
                }
                add("explicit_name_repairs_legacy_address_alias", ReadString(repairedAliasView, "identityState", "") == "verified"
                    && ReadString(repairedAliasView, "usableName", "") == "Aeric"
                    && !repairedAliases.Contains("Menor", StringComparer.OrdinalIgnoreCase),
                    "A direct self-introduction removes a legacy alias whose only evidence was a name-first NPC address.", repairedAliasView);
                add("real_name_introduction_detected",
                    ExtractSelfIntroducedName("For clarity, I am introducing myself to you now: my real name is Rhovarion.") == "Rhovarion",
                    "Natural explicit 'my real name is' wording records the claimed identity before the response is generated.", null);
                add("natural_name_answer_detected",
                    ExtractSelfIntroducedName("*I glance over at her and give her a friendly smile* Osarios, Im actually here for the tournament, just waiting for it to start.", true) == "Osarios",
                    "A natural name-first answer to an identity question records the claimed name.", null);
                add("name_correction_detected",
                    ExtractSelfIntroducedName("*I stop to face her* Im sorry, when I said Osarios, I mean that is my name. Ill be using my own.") == "Osarios",
                    "An explicit correction that identifies an earlier word as the player's name records the claim.", null);
                string sanitized = SanitizePromptForIdentity("PROFILE: Aeric\nMEMORY: Aeric visited Onira.\nLATEST: My name is Aeric.", "My name is Aeric.", "Aeric", claimView);
                add("prompt_privacy_sanitization", !sanitized.Contains("PROFILE: Aeric") && !sanitized.Contains("MEMORY: Aeric") && sanitized.Contains("LATEST: My name is Aeric."), "Canonical names are removed from prompt context while the latest explicit introduction remains visible.", sanitized);

                using (ReignDbConnection identityConnection =
                    OpenCampaignConnection(campaignId))
                {
                    UpsertVerifiedIdentity(
                        identityConnection, ruler, foreign,
                        "self_test_prior_knowledge", "ruler",
                        12d, "trusted_intro_setup");
                    UpsertVerifiedIdentity(
                        identityConnection, ruler, player,
                        "self_test_prior_knowledge", "ruler",
                        12d, "trusted_intro_setup");
                }
                Dictionary<string, object> trusted = IdentityIntroductionApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["observerHeroStringId"] = "foreign",
                    ["subjectHeroStringId"] = "player",
                    ["introducerHeroStringId"] = "ruler",
                    ["claimedName"] = "Aeric",
                    ["canonicalName"] = "Aeric",
                    ["kind"] = "third_party",
                    ["encounterId"] = "court_intro",
                    ["worldDay"] = 12d
                });
                add("trusted_introduction_verifies", ReadBool(trusted, "ok", false) && ReadBool(trusted, "verified", false) && IdentityTestState(campaignId, "foreign", "player") == "verified", "A trusted introducer who knows both people verifies the identity.", trusted);
                Dictionary<string, object> untrusted = IdentityIntroductionApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["observerHeroStringId"] = "same_kingdom",
                    ["subjectHeroStringId"] = "foreign",
                    ["introducerHeroStringId"] = "clanmate",
                    ["claimedName"] = "Derthert",
                    ["canonicalName"] = "Derthert",
                    ["kind"] = "third_party"
                });
                add("untrusted_introduction_rejected", !ReadBool(untrusted, "ok", true), "An introducer without verified knowledge of both people cannot verify identity.", untrusted);

                sameKingdom["kingdomId"] = "new_kingdom";
                IdentitySynchronizeApi(new Dictionary<string, object>(syncPayload) { ["heroes"] = roster, ["worldDay"] = 13d });
                add("knowledge_survives_defection", IdentityTestState(campaignId, "player", "same_kingdom") == "verified", "Synchronization never removes learned identity after a kingdom change.", null);
            }
            catch (Exception ex)
            {
                add("identity_self_test_exception", false, "Identity subsystem self-test threw an exception: " + ex.Message, ex.ToString());
            }
            finally
            {
                TryDeleteIdentitySelfTestCampaign(courtCampaignPath);
                TryDeleteIdentitySelfTestCampaign(campaignPath);
            }
            return results;
        }

        private static Dictionary<string, object> IdentityTestHero(string id, string name, string clanId, string kingdomId, bool isLord, bool isRuler)
        {
            return new Dictionary<string, object>
            {
                ["heroStringId"] = id,
                ["name"] = name,
                ["clanId"] = clanId,
                ["kingdomId"] = kingdomId,
                ["isLord"] = isLord,
                ["isRuler"] = isRuler,
                ["isAlive"] = true,
                ["isAdult"] = true,
                ["isFemale"] = false,
                ["sex"] = "male",
                ["occupation"] = isLord ? "Lord" : "Commoner",
                ["isNotable"] = false,
                ["isWanderer"] = false,
                ["clanTier"] = 3,
                ["currentCharm"] = 0,
                ["childrenIds"] = new List<object>()
            };
        }

        private static string IdentityRecognitionEncounterForOutcome(
            string campaignId,
            string observerId,
            string subjectId,
            bool recognized,
            double probability)
        {
            for (int i = 0; i < 10000; i++)
            {
                string encounterId =
                    "recognition_fixture_"
                    + i.ToString(
                        CultureInfo.InvariantCulture);
                bool outcome = StableIdentityRecognitionRoll(
                    campaignId,
                    observerId,
                    subjectId,
                    encounterId) < probability;
                if (outcome == recognized)
                    return encounterId;
            }
            throw new InvalidOperationException(
                "Unable to find a deterministic recognition fixture.");
        }

        private static string IdentityTestState(string campaignId, string observerId, string subjectId)
        {
            Dictionary<string, object> query = IdentityQueryApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["observerHeroStringId"] = observerId,
                ["subjectHeroStringId"] = subjectId,
                ["limit"] = 1
            });
            return ReadString(ReadDictionaryList(query, "acquaintances").FirstOrDefault(), "identity_state", "");
        }

        private static void TryDeleteIdentitySelfTestCampaign(string campaignPath)
        {
            try
            {
                string root = Path.GetFullPath(CampaignsRoot()) + Path.DirectorySeparatorChar;
                string target = Path.GetFullPath(campaignPath ?? "");
                if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(target).StartsWith("__identity_self_test_", StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
                {
                    ReignPostgreSqlStorage.ClearAllPools();
                    Directory.Delete(target, true);
                }
            }
            catch { }
        }
    }
}
