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
    internal static partial class CharactersService
    {
        internal static void EnsureIdentitySchema(ReignDbConnection connection)
        {
            CharactersSchema.CreateAcquaintances(connection);
            CharactersSchema.CreateIndexAcquaintancesSubject(connection);
            CharactersSchema.CreateIndexAcquaintancesObserver(connection);
            CharactersSchema.CreateIdentityEvidence(connection);
            CharactersSchema.CreateIndexIdentityEvidencePair(connection);
            CharactersSchema.CreateIdentityRoster(connection);
            CharactersSchema.EnsureColumnIdentityRosterIsAlive(connection);
            CharactersSchema.EnsureColumnIdentityRosterIsAdult(connection);
            CharactersSchema.EnsureColumnIdentityRosterIsPlayer(connection);
            CharactersSchema.EnsureColumnIdentityRosterSex(connection);
            CharactersSchema.EnsureColumnIdentityRosterClanTier(connection);
            CharactersSchema.EnsureColumnIdentityRosterCurrentCharm(connection);
            CharactersSchema.EnsureColumnIdentityRosterOccupation(connection);
            CharactersSchema.EnsureColumnIdentityRosterIsNotable(connection);
            CharactersSchema.EnsureColumnIdentityRosterIsWanderer(connection);
            CharactersSchema.EnsureColumnIdentityRosterIsClanLeader(connection);
            CharactersSchema.EnsureColumnIdentityRosterIsMercenaryClan(connection);
            CharactersSchema.EnsureColumnIdentityRosterGovernorSettlementId(connection);
            CharactersSchema.EnsureColumnIdentityRosterGovernorSettlementName(connection);
            CharactersSchema.EnsureColumnAcquaintancesLastRecognitionDay(connection);
            CharactersSchema.EnsureColumnAcquaintancesLastRecognitionProbability(connection);
            CharactersSchema.EnsureColumnAcquaintancesLastRecognitionRoll(connection);
            CharactersSchema.EnsureColumnAcquaintancesRecognitionRuleVersion(connection);
            CharactersSchema.CreateIndexIdentityRosterClan(connection);
            CharactersSchema.CreateIndexIdentityRosterKingdom(connection);
            EnsureCompactIdentityEvidencePayloads(connection);
        }

        private static void EnsureCompactIdentityEvidencePayloads(ReignDbConnection connection)
        {
            const int version = 2;
            int current = ReadInt(CharactersStore.SelectSchemaMeta2(connection)
                .FirstOrDefault(), "value", 0);
            if (current >= version) return;
            CharactersStore.Begin(connection);
            try
            {
                // Evidence rows are the canonical provenance store. Keeping the
                // same request envelope on the acquaintance summary row doubled
                // it without adding any authoritative state.
                CharactersStore.UpdateAcquaintances2(connection);
                foreach (Dictionary<string, object> row in CharactersStore.SelectIdentityEvidence5(connection))
                {
                    Dictionary<string, object> payload =
                        TryParseJsonObject(ReadString(row, "payload_json", ""))
                        ?? new Dictionary<string, object>();
                    CharactersStore.UpdateIdentityEvidence(connection, new Dictionary<string, object>
                        {
                            ["id"] = ReadString(row, "evidence_id", ""),
                            ["payload"] = Json.Serialize(CompactIdentityEvidencePayload(payload))
                        });
                }
                CharactersStore.UpsertSchemaMeta2(connection, new Dictionary<string, object> { ["version"] = version.ToString(CultureInfo.InvariantCulture) });
                CharactersStore.UpsertSchemaMeta3(connection);
                CharactersStore.Commit(connection);
            }
            catch
            {
                try { CharactersStore.Rollback(connection); } catch { }
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
                    compact[key] = DialogueService.LimitText(value, key.EndsWith("Text", StringComparison.OrdinalIgnoreCase)
                        || key == "text" || key == "message" ? 1200 : 160);
            }
            Dictionary<string, object> subject = ReadDictionary(payload, "subject");
            if (subject != null && !string.IsNullOrWhiteSpace(ReadString(subject, "name", "")))
                compact["subject"] = new Dictionary<string, object>
                {
                    ["name"] = DialogueService.LimitText(ReadString(subject, "name", ""), 160)
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
                            ["role"] = DialogueService.LimitText(ReadString(row, "role", ""), 32),
                            ["text"] = DialogueService.LimitText(text, 1200)
                        });
                    }
                    else
                    {
                        string text = item?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(text)) rows.Add(DialogueService.LimitText(text, 1200));
                    }
                }
                if (rows.Count > 12) rows = rows.Skip(rows.Count - 12).ToList();
                if (rows.Count > 0) compact[key] = rows;
            }
            return compact;
        }

        internal static Dictionary<string, object> IdentitySynchronizeApi(Dictionary<string, object> payload)
        {
            var syncTimer = System.Diagnostics.Stopwatch.StartNew();
            var syncPhases = new Dictionary<string, object>();
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string correlationId = OperationsService.EnsureCorrelationId(payload);
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            List<Dictionary<string, object>> heroes = ReadDictionaryList(payload, "heroes")
                .Where(x => !string.IsNullOrWhiteSpace(IdentityHeroId(x)) && ReadBool(x, "isAlive", true))
                .GroupBy(IdentityHeroId, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();
            string fingerprint = FirstNonEmpty(ReadString(payload, "rosterFingerprint", ""), ComputeIdentityRosterFingerprint(heroes));
            string expectedPortraitRosterFingerprint = ReadString(payload, "portraitRosterFingerprint", "");
            Dictionary<string, object> savedPortraitRoster = CampaignFileService.ReadJsonObject(
                CampaignFileService.CampaignFile(campaignId, "portrait_roster.json"));
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

            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                syncPhases["prepareAndConnectMs"] = syncTimer.ElapsedMilliseconds;
                var phaseTimer = System.Diagnostics.Stopwatch.StartNew();
                // Run bounded-storage migrations from the same roster synchronization that occurs
                // after a campaign is loaded. This repairs older oversized databases before the
                // next native save asks Save Sync to snapshot them.
                RelationshipsService.EnsureAmbientRelationshipSchema(connection);
                EnsureIdentitySchema(connection);
                string previousFingerprint = ReadString(CharactersStore.SelectSchemaMeta3(connection).FirstOrDefault(), "value", "");
                bool rosterChanged = !string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal);
                Dictionary<string, Dictionary<string, object>> byId = heroes.ToDictionary(IdentityHeroId, x => x, StringComparer.OrdinalIgnoreCase);
                storageRepair = ReconcileSyntheticIdentityRows(connection, heroes);
                if (rosterChanged)
                {
                    var previousRoster = CharactersStore.SelectIdentityRoster2(connection);
                    var historicalBatch = PrepareHistoricalIdentityBatch(previousRoster, heroes);
                    syncPhases["historicalPrepareMs"] = phaseTimer.ElapsedMilliseconds;
                    phaseTimer.Restart();
                    CharactersStore.Begin(connection);
                    syncPhases["transactionWaitMs"] = phaseTimer.ElapsedMilliseconds;
                    phaseTimer.Restart();
                    try
                    {
                        // Another census may have committed while preparation ran.
                        // Rebase against its roster under the existing transaction lock.
                        string lockedFingerprint = ReadString(CharactersStore.SelectSchemaMeta3(connection)
                            .FirstOrDefault(), "value", "");
                        if (!string.Equals(previousFingerprint, lockedFingerprint, StringComparison.Ordinal))
                        {
                            previousRoster = CharactersStore.SelectIdentityRoster2(connection);
                            historicalBatch = PrepareHistoricalIdentityBatch(previousRoster, heroes);
                        }
                        WriteHistoricalIdentityBatch(connection, historicalBatch, worldDay,
                            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        syncPhases["historicalRows"] = historicalBatch.Count;
                        syncPhases["historicalWriteCommands"] = (historicalBatch.Count + 999) / 1000;
                        CharactersStore.DeleteIdentityRoster(connection);
                        long rosterTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        // The roster was just cleared, so the existing batch writer
                        // persists the same rows/timestamps without thousands of
                        // command preparations and round trips under the campaign lock.
                        UpsertIdentityRosterHeroesBatch(connection, heroes, rosterTs);
                        CharactersStore.UpsertSchemaMeta4(connection, new Dictionary<string, object> { ["value"] = fingerprint });
                        CharactersStore.Commit(connection);
                        syncPhases["rosterTransactionMs"] = phaseTimer.ElapsedMilliseconds;
                        phaseTimer.Restart();
                    }
                    catch
                    {
                        try { CharactersStore.Rollback(connection); } catch { }
                        throw;
                    }
                    ReputationService.EnsureSocialReputationSchema(connection);
                    // A brand-new roster cannot have stale court-popularity
                    // projections. Those are created incrementally by relationship
                    // processing. Recomputing every lord here made first startup
                    // perform thousands of empty relationship lookups before the
                    // initial save could flush World History.
                    bool hasCourtPopularityState = ReadInt(CharactersStore.CountCourtDynamicReputationMetrics(connection)
                        .FirstOrDefault(), "count", 0) > 0;
                    var popularitySubjects = hasCourtPopularityState
                        ? SelectIdentityPopularitySubjects(connection, previousRoster,
                            CharactersStore.SelectIdentityRoster2(connection)) : new List<string>();
                    syncPhases["popularitySubjects"] = popularitySubjects.Count;
                    int courtPopularityChanges = ReputationService.RecomputeCourtPopularityReputationsBatch(connection, campaignId,
                        ReadString(payload, "timelineId", "main"), popularitySubjects, worldDay,
                        correlationId + "|court_popularity").Count;
                    if (courtPopularityChanges > 0
                        && !campaignId.StartsWith("__", StringComparison.Ordinal))
                        ReputationService.SchedulePendingReputationReasonJobs(campaignId);
                    // Expiration remains day-sensitive. The roster standing pass below
                    // reconciles the union of current and historical subjects once.
                    ReputationService.ExpireSocialRumors(connection, campaignId,
                        ReadString(payload, "timelineId", "main"), worldDay);
                    syncPhases["reputationReconciliationMs"] = phaseTimer.ElapsedMilliseconds;
                }
                phaseTimer.Restart();
                ReputationService.EnsurePublicStandingForRoster(connection, campaignId,
                    ReadString(payload, "timelineId", "main"), worldDay);
                syncPhases["publicStandingMs"] = phaseTimer.ElapsedMilliseconds;
                phaseTimer.Restart();
                Dictionary<string, object> politicalRelationships =
                    RelationshipsService.ReconcilePoliticalRelationshipNetwork(connection, campaignId,
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
                OperationsService.WriteAudit(campaignId, correlationId, "server", "identity", "identity.synchronized", "", "", "", "completed", 0, "Directional identity network synchronized.", result);
                return result;
            }
        }

        private static Dictionary<string, object> ReconcileSyntheticIdentityRows(ReignDbConnection connection, List<Dictionary<string, object>> heroes)
        {
            const int repairVersion = 3;
            int currentVersion = ReadInt(CharactersStore.SelectSchemaMeta4(connection).FirstOrDefault(), "value", 0);
            if (currentVersion >= repairVersion)
                return new Dictionary<string, object> { ["applied"] = false, ["version"] = currentVersion };

            long evidenceBefore = ReadLong(CharactersStore.CountIdentityEvidence(connection).FirstOrDefault(), "count", 0);
            long acquaintancesBefore = ReadLong(CharactersStore.CountAcquaintances2(connection).FirstOrDefault(), "count", 0);
            CharactersStore.Begin(connection);
            try
            {
                string syntheticSources = "'same_clan','immediate_family','same_kingdom_nobility','realm_sovereign','ruler_network','live_same_realm_sovereign'";
                CharactersStore.DeleteIdentityEvidence(connection, syntheticSources);
                CharactersStore.UpdateAcquaintances3(connection, syntheticSources, syntheticSources);
                CharactersStore.DeleteAcquaintances(connection, syntheticSources, syntheticSources);
                CharactersStore.UpsertSchemaMeta5(connection, new Dictionary<string, object> { ["version"] = repairVersion.ToString(CultureInfo.InvariantCulture) });
                CharactersStore.UpsertSchemaMeta3(connection);
                CharactersStore.Commit(connection);
            }
            catch
            {
                try { CharactersStore.Rollback(connection); } catch { }
                throw;
            }

            long evidenceAfter = ReadLong(CharactersStore.CountIdentityEvidence(connection).FirstOrDefault(), "count", 0);
            long acquaintancesAfter = ReadLong(CharactersStore.CountAcquaintances2(connection).FirstOrDefault(), "count", 0);
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
            if (!string.Equals(ReadString(CharactersStore.SelectSchemaMeta5(connection).FirstOrDefault(), "value", ""), "1", StringComparison.Ordinal))
                return false;
            try
            {
                // Startup synchronization is latency-sensitive and shares this
                // database with World History and relationship ingestion. A full
                // VACUUM here monopolized SQLite for minutes on a fresh campaign.
                // Keep startup maintenance bounded; retired pages can be reused by
                // SQLite and Save Sync already snapshots through the backup API.
                CharactersStore.Pragma(connection);
                CharactersStore.Pragma2(connection);
                CharactersStore.UpsertSchemaMeta6(connection);
                return true;
            }
            catch
            {
                // A concurrent reader can temporarily prevent VACUUM. Keep the flag so the
                // next campaign synchronization retries after the connection clears.
                return false;
            }
        }

        internal static void UpsertIdentityRosterHero(ReignDbConnection connection, Dictionary<string, object> hero, long ts)
        {
            string heroId = IdentityHeroId(hero);
            if (string.IsNullOrWhiteSpace(heroId)) return;
            HashSet<string> family = IdentityFamilyIds(hero);
            CharactersStore.UpsertIdentityRoster(connection, new Dictionary<string, object>
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

        internal static void UpsertIdentityRosterHeroesBatch(
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
            RelationshipsService.ExecutePostgreSqlJsonCommand(connection, CharactersStore.SelectXSql,
                RelationshipsService.PostgreSqlRelationshipRowsJson(rows));
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

        internal static void PreserveDepartingImplicitKnowledge(
            ReignDbConnection connection,
            List<Dictionary<string, object>> nextHeroes,
            double worldDay, long? fixedTimestamp = null)
        {
            List<Dictionary<string, object>> previous = CharactersStore.SelectIdentityRoster2(connection);
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
            CharactersStore.UpsertAcquaintances4(connection, new Dictionary<string, object>
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
                DialogueService.TextListFromJson(ReadString(observer, "family_ids_json", "[]")), StringComparer.OrdinalIgnoreCase);
            HashSet<string> subjectFamily = new HashSet<string>(
                DialogueService.TextListFromJson(ReadString(subject, "family_ids_json", "[]")), StringComparer.OrdinalIgnoreCase);
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

        internal static Dictionary<string, object> IdentityEncounterApi(Dictionary<string, object> payload,
            ContextualSelfIntroductionDecision contextualIntroduction = null)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string observerId = ReadFirstString(payload, "observerHeroStringId", "observerId", "npcId", "speakerHeroStringId");
            string subjectId = ReadFirstString(payload, "subjectHeroStringId", "subjectId", "playerHeroStringId");
            string encounterId = FirstNonEmpty(ReadFirstString(payload, "encounterId", "conversationSessionId", "sessionId", "eventId"), "encounter_" + Guid.NewGuid().ToString("N"));
            string correlationId = OperationsService.EnsureCorrelationId(payload);
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            if (string.IsNullOrWhiteSpace(observerId) || string.IsNullOrWhiteSpace(subjectId))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "observerHeroStringId and subjectHeroStringId are required." };

            Dictionary<string, object> observer = ReadDictionary(payload, "observer") ?? new Dictionary<string, object> { ["heroStringId"] = observerId };
            Dictionary<string, object> subject = ReadDictionary(payload, "subject") ?? new Dictionary<string, object> { ["heroStringId"] = subjectId, ["name"] = ReadString(payload, "canonicalName", "") };
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                ReputationService.EnsureSocialReputationSchema(connection);
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
                    CharactersStore.UpdateAcquaintances4(connection, IdentitySqlArgs(observerId, subjectId, worldDay, encounterId));
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
                bool contextual = string.IsNullOrWhiteSpace(claimed)
                    && !IdentityStateVerified(existing) && contextualIntroduction != null
                    && AuthoritativeIntroductionSource(contextualIntroduction.Name, subject).Length > 0;
                if (contextual) claimed = contextualIntroduction.Name;
                if (!string.IsNullOrWhiteSpace(claimed))
                {
                    string canonical = ReadString(subject, "name", "");
                    string introductionSource = AuthoritativeIntroductionSource(claimed, subject);
                    if (contextual) introductionSource += "_contextual";
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
                        if (contextual)
                            AddIdentityEvidence(connection, observerId, subjectId,
                                "contextual_self_introduction", claimed, canonical,
                                introductionSource, subjectId, encounterId,
                                contextualIntroduction.Confidence, worldDay,
                                new Dictionary<string, object>
                                {
                                    ["correlationId"] = correlationId,
                                    ["playerText"] = contextualIntroduction.EvidenceQuote
                                });
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
                    Dictionary<string, object> priorEvidence = CharactersStore.SelectIdentityEvidence6(connection, new Dictionary<string, object> { ["observer"] = observerId, ["subject"] = subjectId }).FirstOrDefault();
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
                    ReputationService.ReconcileSocialRelationshipForObserverSubject(
                        connection,
                        campaignId,
                        ReadString(payload, "timelineId", "main"),
                        observerId,
                        subjectId,
                        worldDay);
                view["socialStandingView"] = ReputationService.BuildKnownSocialStandingView(connection,
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
                        CharactersStore.CountIdentityRoster(connection)
                            .Select(rowValue =>
                                ReadLong(rowValue, "count", 0))
                            .FirstOrDefault() > 0
                };
                OperationsService.WriteAudit(campaignId, correlationId, "server", "identity", "identity.encounter", observerId, "", encounterId, "completed", 0, "Identity view resolved for encounter.", response);
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
            CharactersStore.UpdateAcquaintances5(connection, new Dictionary<string, object>
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

        internal static Dictionary<string, object> IdentityIntroductionApi(Dictionary<string, object> payload,
            ReignDbConnection existingConnection = null)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string observerId = ReadFirstString(payload, "observerHeroStringId", "observerId");
            string subjectId = ReadFirstString(payload, "subjectHeroStringId", "subjectId");
            string introducerId = ReadFirstString(payload, "introducerHeroStringId", "introducerId");
            string claimedName = DialogueService.LimitText(ReadFirstString(payload, "claimedName", "name"), 80);
            string canonicalName = DialogueService.LimitText(ReadString(payload, "canonicalName", ""), 80);
            string kind = ReadString(payload, "kind", string.IsNullOrWhiteSpace(introducerId) || string.Equals(introducerId, subjectId, StringComparison.OrdinalIgnoreCase) ? "self" : "third_party");
            string encounterId = ReadFirstString(payload, "encounterId", "conversationSessionId", "eventId");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            string correlationId = OperationsService.EnsureCorrelationId(payload);
            if (string.IsNullOrWhiteSpace(observerId) || string.IsNullOrWhiteSpace(subjectId) || string.IsNullOrWhiteSpace(claimedName))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "observer, subject, and claimedName are required." };

            using (ReignDbConnection ownedConnection = existingConnection == null ? ReignDatabase.OpenCampaignConnection(campaignId) : null)
            {
                ReignDbConnection connection = existingConnection ?? ownedConnection;
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
                // The conversation's accepted-unit receipt owns audit visibility
                // until its outer transaction commits.
                if (existingConnection == null) OperationsService.WriteAudit(campaignId, correlationId, "server", "identity", "identity.introduction", observerId, "", encounterId, "completed", 0, verified ? "Trusted introduction verified identity." : "Claimed identity recorded.", view);
                return new Dictionary<string, object> { ["ok"] = true, ["verified"] = verified, ["identityView"] = view };
            }
        }

        internal static Dictionary<string, object> IdentityQueryApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", SettingsService.LatestCampaignId());
            string observerId = ReadFirstString(payload, "observerHeroStringId", "observerId");
            string subjectId = ReadFirstString(payload, "subjectHeroStringId", "subjectId");
            int limit = Math.Max(1, Math.Min(2000, ReadInt(payload, "limit", 500)));
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
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
                    rows = CharactersStore.SelectAcquaintancesDynamic(connection, where,args);
                }
                List<Dictionary<string, object>> evidence = observerId.Length > 0 && subjectId.Length > 0
                    ? CharactersStore.SelectIdentityEvidence7(connection, new Dictionary<string, object> { ["observer"] = observerId, ["subject"] = subjectId })
                    : new List<Dictionary<string, object>>();
                return new Dictionary<string, object> { ["ok"] = true, ["campaignId"] = campaignId, ["acquaintances"] = rows, ["evidence"] = evidence };
            }
        }

        internal static Dictionary<string, object> IdentityResetApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (!ReadBool(payload, "testMode", false)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Identity reset is available only in beta test mode." };
            string campaignId = ReadString(payload, "campaignId", "default");
            string observerId = ReadFirstString(payload, "observerHeroStringId", "observerId");
            string subjectId = ReadFirstString(payload, "subjectHeroStringId", "subjectId");
            string correlationId = OperationsService.EnsureCorrelationId(payload);
            if (string.IsNullOrWhiteSpace(observerId) || string.IsNullOrWhiteSpace(subjectId)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "observer and subject are required." };
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                Dictionary<string, object> args = new Dictionary<string, object> { ["observer"] = observerId, ["subject"] = subjectId };
                CharactersStore.DeleteAcquaintances2(connection, args);
                CharactersStore.DeleteIdentityEvidence2(connection, args);
            }
            OperationsService.WriteAudit(campaignId, correlationId, "server", "identity", "identity.reset", observerId, "", "", "completed", 0, "Identity knowledge reset for beta testing.", new Dictionary<string, object> { ["observerId"] = observerId, ["subjectId"] = subjectId });
            return new Dictionary<string, object> { ["ok"] = true, ["observerId"] = observerId, ["subjectId"] = subjectId };
        }

        internal static Dictionary<string, object> IdentityDebugKnowEveryoneApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (!ReadBool(payload, "testMode", false)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Know-everyone is available only in beta test mode." };

            string campaignId = ReadString(payload, "campaignId", "default");
            string correlationId = OperationsService.EnsureCorrelationId(payload);
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

            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                CharactersStore.Begin(connection);
                try
                {
                    foreach (Dictionary<string, object> hero in heroes)
                    {
                        string result = UpsertVerifiedIdentity(connection, observer, hero, "debug_known_everyone", observerId, worldDay, correlationId);
                        if (result == "created") created++;
                        else if (result == "upgraded") upgraded++;
                        else unchanged++;
                    }
                    CharactersStore.Commit(connection);
                }
                catch
                {
                    try { CharactersStore.Rollback(connection); } catch { }
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
            OperationsService.WriteAudit(campaignId, correlationId, "server", "identity", "identity.debug.know_everyone", observerId, "", "", "completed", 0, "Player debug identity knowledge populated.", resultPayload);
            return resultPayload;
        }
    }
}
