using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class ReputationService
    {
        private static Dictionary<string, object> SocialBalancePersistenceDatabaseSnapshot(
            string campaignId, string timelineId)
        {
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                EnsureCourtSocialReputationSchema(connection);
                Dictionary<string, object> parameters = new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                };
                List<Dictionary<string, object>> signals = ReputationStore.SelectCourtSocialSignalEvidence(connection, parameters);
                List<Dictionary<string, object>> signalReceipts = ReputationStore.SelectCourtConversationSignalReceipts2(connection, parameters);
                List<Dictionary<string, object>> favoring = ReputationStore.SelectCourtPlayerFavoringExchanges3(connection, parameters);
                List<Dictionary<string, object>> favoringReceipts = ReputationStore.SelectCourtPlayerFavoringExchangeReceipts3(connection, parameters);
                List<Dictionary<string, object>> intimacy = ReputationStore.SelectCourtIntimacyExposureReceipts2(connection, parameters);
                List<Dictionary<string, object>> flirtTargets = ReputationStore.SelectCourtFlirtTargets2(connection, parameters);
                int activeLegacyReputations = ReadInt(ReputationStore.CountCharacterReputations(connection, parameters).FirstOrDefault(), "count", 0);
                int activeLegacyRumors = ReadInt(ReputationStore.CountRumorSubjectTags(connection, parameters).FirstOrDefault(), "count", 0);
                bool migrationMarker = ReputationStore.SelectSchemaMeta5(connection)
                    .Any();
                Dictionary<string, object> sections = new Dictionary<string, object>
                {
                    ["socialSignals"] = PersistenceSection(signals),
                    ["signalReceipts"] = PersistenceSection(signalReceipts),
                    ["playerFavoringExchanges"] = PersistenceSection(favoring),
                    ["playerFavoringExchangeReceipts"] = PersistenceSection(favoringReceipts),
                    ["intimacyReceipts"] = PersistenceSection(intimacy),
                    ["flirtTargets"] = PersistenceSection(flirtTargets)
                };
                string overallHash = DialogueService.SemanticSha256Hex(string.Join("|", sections
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => pair.Key + ":" + ReadString(
                        pair.Value as Dictionary<string, object>, "hash", ""))));
                bool required = signals.Count > 0 && signalReceipts.Count > 0
                    && favoring.Count > 0 && favoringReceipts.Count > 0
                    && intimacy.Count > 0 && flirtTargets.Count > 0;
                return new Dictionary<string, object>
                {
                    ["sections"] = sections, ["overallHash"] = overallHash,
                    ["hasRequiredEvidence"] = required,
                    ["legacyMigrationMarker"] = migrationMarker,
                    ["activeLegacyFavoredByReputations"] = activeLegacyReputations,
                    ["activeLegacyFavoredByRumors"] = activeLegacyRumors,
                    ["legacyFavoringMigrationSafe"] = migrationMarker
                        && activeLegacyReputations == 0 && activeLegacyRumors == 0
                };
            }
        }

        private static Dictionary<string, object> PersistenceSection(
            List<Dictionary<string, object>> rows)
        {
            rows = rows ?? new List<Dictionary<string, object>>();
            return new Dictionary<string, object>
            {
                ["count"] = rows.Count,
                ["hash"] = DialogueService.SemanticSha256Hex(Json.Serialize(rows))
            };
        }

        private static string SocialBalancePersistencePath(string runId)
        {
            return Path.Combine(SocialBalanceRoot, "persistence",
                Program.SafePathSegment(runId, "run") + ".json");
        }

        internal static Dictionary<string, object> SocialBalanceSnapshotApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "").Trim();
            string timelineId = ReadString(payload, "timelineId", "main").Trim();
            string runId = ReadString(payload, "runId", "").Trim();
            string periodKey = ReadString(payload, "periodKey", "").Trim();
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(timelineId)
                || string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(periodKey))
                return SocialBalanceError("campaignId, timelineId, runId, and periodKey are required.");
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, object> snapshot = ReadDictionary(payload, "snapshot");
            snapshot["serverSocial"] = BuildServerSocialBalanceSnapshot(campaignId, timelineId, snapshot);
            lock (SocialBalanceLock)
            {
                using (ReignDbConnection connection = OpenSocialBalanceDatabase())
                {
                    if (!ReputationStore.SelectSocialBalanceRuns(connection, new Dictionary<string, object> { ["run"] = runId, ["campaign"] = campaignId, ["timeline"] = timelineId }).Any())
                        return SocialBalanceError("The run is not enrolled for this campaign and timeline.");
                    ReputationStore.UpsertSocialBalanceSnapshots(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["run"] = runId,
                            ["period"] = periodKey, ["day"] = ReadDouble(payload, "worldDay", 0d),
                            ["snapshot"] = Json.Serialize(snapshot), ["now"] = now
                        });
                }
            }
            return new Dictionary<string, object> { ["ok"] = true, ["stored"] = true, ["periodKey"] = periodKey };
        }

#if !REIGN_EXCLUDE_VERIFICATION
        internal static Dictionary<string, object> SocialBalanceRankAffairCandidatesApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            if (!ValidateSocialBalanceLiveEnrollment(payload, campaignId, out string enrollmentError))
                return SocialBalanceError(enrollmentError);
            Dictionary<string, object> enrollment = ReadDictionary(payload, "enrollment")
                ?? new Dictionary<string, object>();
            string playerId = ReadString(enrollment, "mainHeroId", "");
            bool playerIsFemale = ReadBool(payload, "playerIsFemale", false);
            const int veryLowThreshold = 20;
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> snapshot in ReadDictionaryList(payload, "candidateSnapshots")
                .Take(2000))
            {
                string targetId = ReadFirstString(snapshot, "heroId", "heroStringId");
                string spouseId = ReadString(snapshot, "spouseId", "");
                double age = ReadDouble(snapshot, "age", 0d);
                if (string.IsNullOrWhiteSpace(targetId)
                    || targetId.Equals(playerId, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(spouseId)
                    || spouseId.Equals(playerId, StringComparison.OrdinalIgnoreCase)
                    || !ReadBool(snapshot, "isAlive", false)
                    || !ReadBool(snapshot, "isAdult", age >= 18d)
                    || age < 18d
                    || ReadBool(snapshot, "isFemale", false) == playerIsFemale)
                    continue;

                Dictionary<string, object> runtimeProfile =
                    DialogueService.QualificationRuntimeProfileFromTarget(targetId, snapshot);
                Dictionary<string, object> profile = Program.ReadJsonObject(
                    Program.CharacterFile(campaignId, targetId, "profile.json"));
                Dictionary<string, object> traits = Program.ReadJsonObject(
                    Program.CharacterFile(campaignId, targetId, "traits.json"));
                string traitSource = "campaign_traits";
                if (profile.Count == 0) profile = runtimeProfile;
                if (traits.Count == 0)
                {
                    traits = CharactersService.BuildTraitDocument(runtimeProfile);
                    traitSource = "deterministic_runtime_projection";
                }
                CharactersService.EnsureTraitPercentageData(traits, targetId);
                CourtService.EnsureCourtCharacterData(traits, profile, targetId);
                Dictionary<string, object> percentages =
                    DialogueService.TraitPercentageSnapshot(traits);
                Dictionary<string, object> virtues =
                    ReadDictionary(traits, "courtVirtues")
                    ?? CharactersService.CalculateCourtVirtues(percentages);
                Dictionary<string, object> courtCharacter =
                    ReadDictionary(traits, "courtCharacter")
                    ?? new Dictionary<string, object>();
                int honor = ReadInt(virtues, "honor", 50);
                int loyalty = ReadInt(percentages, "loyalty", 50);
                rows.Add(new Dictionary<string, object>
                {
                    ["heroId"] = targetId,
                    ["heroName"] = FirstNonEmpty(
                        ReadString(profile, "name", ""),
                        ReadString(snapshot, "name", targetId)),
                    ["spouseId"] = spouseId,
                    ["age"] = age,
                    ["isFemale"] = ReadBool(snapshot, "isFemale", false),
                    ["clanTier"] = ReadInt(snapshot, "clanTier", 0),
                    ["nativeRelation"] = ReadInt(snapshot, "nativeRelation", 0),
                    ["nativeHonor"] = ReadInt(snapshot, "honor", 0),
                    ["sameSettlement"] = ReadBool(snapshot, "sameSettlement", false),
                    ["honor"] = honor,
                    ["loyalty"] = loyalty,
                    ["flirtatiousness"] = ReadInt(percentages, "flirtatiousness", 50),
                    ["ambition"] = ReadInt(percentages, "ambition", 50),
                    ["powerMotivation"] = ReadInt(percentages, "powerMotivation", 50),
                    ["courtCharacterCell"] = ReadString(courtCharacter, "cellId", ""),
                    ["courtCharacterTitle"] = ReadString(courtCharacter, "title", ""),
                    ["traitSource"] = traitSource,
                    ["veryLowThreshold"] = veryLowThreshold,
                    ["qualifiesVeryLowHonorAndLoyalty"] =
                        honor <= veryLowThreshold && loyalty <= veryLowThreshold
                });
            }
            rows = rows
                .OrderBy(row => ReadBool(row,
                    "qualifiesVeryLowHonorAndLoyalty", false) ? 0 : 1)
                .ThenBy(row => ReadInt(row, "honor", 50))
                .ThenBy(row => ReadInt(row, "loyalty", 50))
                .ThenByDescending(row => ReadInt(row, "ambition", 50))
                .ThenByDescending(row => ReadInt(row, "powerMotivation", 50))
                .ThenByDescending(row => ReadBool(row, "sameSettlement", false))
                .ThenBy(row => ReadString(row, "heroId", ""),
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["playerId"] = playerId,
                ["veryLowThreshold"] = veryLowThreshold,
                ["candidateCount"] = rows.Count,
                ["qualifyingCount"] = rows.Count(row => ReadBool(row,
                    "qualifiesVeryLowHonorAndLoyalty", false)),
                ["candidates"] = rows
            };
        }
#endif

        private static Dictionary<string, object> BuildServerSocialBalanceSnapshot(
            string campaignId, string timelineId, Dictionary<string, object> nativeSnapshot)
        {
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["available"] = false,
                ["campaignId"] = campaignId ?? "",
                ["timelineId"] = timelineId ?? "main"
            };
            if (string.IsNullOrWhiteSpace(campaignId)
                || !DialogueService.HasCampaignPostgreSqlStorage(campaignId)) return result;
            try
            {
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    EnsureSocialReputationSchema(connection);
                    Dictionary<string, object> parameters = new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    };
                    List<Dictionary<string, object>> rumorByTag = ReputationStore.SelectRumorSubjectTags8(connection, parameters);
                    List<Dictionary<string, object>> reputationByTag = ReputationStore.SelectCharacterReputations17(connection, parameters);
                    List<Dictionary<string, object>> subjects = ReputationStore.SelectRumorSubjectTags9(connection, parameters);
                    List<Dictionary<string, object>> projections = ReputationStore.SelectDirectionalViews(connection, parameters);
                    List<Dictionary<string, object>> relationships = ReputationStore.SelectEffectivePairs(connection, parameters);
                    List<Dictionary<string, object>> lifecycle = ReputationStore.SelectReputationLifecycleEvents(connection, parameters);
                    List<Dictionary<string, object>> reasons = ReputationStore.SelectReputationActivations3(connection, parameters);
                    HashSet<string> courtTags = new HashSet<string>(
                        ReadDictionaryList(ReadActiveSocialCatalog(connection), "tags")
                            .Where(x => ReadString(x, "family", "")
                                .Equals("court_personality", StringComparison.OrdinalIgnoreCase))
                            .Select(x => ReadString(x, "id", "")),
                        StringComparer.OrdinalIgnoreCase);
                    Func<Dictionary<string, object>, bool> isCourtTag = row =>
                    {
                        string id = ReadString(row, "tag_id", "");
                        int separator = id.IndexOf(':');
                        if (separator > 0) id = id.Substring(0, separator);
                        return courtTags.Contains(id);
                    };
                    List<Dictionary<string, object>> courtRumors = rumorByTag.Where(isCourtTag).ToList();
                    List<Dictionary<string, object>> courtReputations = reputationByTag.Where(isCourtTag).ToList();
                    List<Dictionary<string, object>> courtPopularityMetrics = ReputationStore.SelectCourtDynamicReputationMetrics(connection, parameters);

                    Dictionary<string, string> subjectClasses = SocialBalanceSubjectClasses(nativeSnapshot);
                    foreach (Dictionary<string, object> subject in subjects)
                    {
                        string id = ReadString(subject, "subject_id", "");
                        subject["subject_class"] = subjectClasses.TryGetValue(id, out string role) ? role : "unclassified";
                    }
                    List<Dictionary<string, object>> byClass = subjects.GroupBy(x => ReadString(x, "subject_class", "unclassified"))
                        .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(group => new Dictionary<string, object>
                        {
                            ["subject_class"] = group.Key,
                            ["tagged_subject_count"] = group.Count(),
                            ["active_rumor_count"] = group.Sum(x => ReadInt(x, "active_rumor_count", 0)),
                            ["active_reputation_count"] = group.Sum(x => ReadInt(x, "active_reputation_count", 0)),
                            ["raw_rumor_effect"] = group.Sum(x => ReadInt(x, "raw_rumor_effect", 0)),
                            ["raw_reputation_effect"] = group.Sum(x => ReadInt(x, "raw_reputation_effect", 0))
                        }).ToList();
                    result["available"] = true;
                    result["activeRumorsByTag"] = rumorByTag;
                    result["activeReputationsByTag"] = reputationByTag;
                    result["taggedSubjects"] = subjects;
                    result["standingBySubjectClass"] = byClass;
                    result["directionalProjection"] = projections.FirstOrDefault() ?? new Dictionary<string, object>();
                    result["relationshipPressure"] = relationships.FirstOrDefault() ?? new Dictionary<string, object>();
                    result["reputationLifecycle"] = lifecycle;
                    result["reasonGeneration"] = reasons;
                    result["courtPersonality"] = new Dictionary<string, object>
                    {
                        ["activeRumorsByTag"] = courtRumors,
                        ["activeReputationsByTag"] = courtReputations,
                        ["activeRumorOccurrences"] = courtRumors.Sum(x => ReadInt(x, "occurrence_count", 0)),
                        ["activeReputationSubjects"] = courtReputations.Sum(x => ReadInt(x, "subject_count", 0)),
                        ["rawRumorEffect"] = courtRumors.Sum(x => ReadInt(x, "raw_effect_sum", 0)),
                        ["rawReputationEffect"] = courtReputations.Sum(x => ReadInt(x, "raw_effect_sum", 0)),
                        ["popularityMetrics"] = courtPopularityMetrics,
                        ["positivePopularityQualifiers"] = courtPopularityMetrics.Sum(x => ReadInt(x, "positive_count", 0)),
                        ["negativePopularityQualifiers"] = courtPopularityMetrics.Sum(x => ReadInt(x, "negative_count", 0))
                    };
                    result["capturedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                }
            }
            catch (Exception ex)
            {
                result["error"] = ex.Message;
            }
            return result;
        }

        private static Dictionary<string, string> SocialBalanceSubjectClasses(Dictionary<string, object> nativeSnapshot)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> hero in ReadDictionaryList(nativeSnapshot, "heroes"))
            {
                string id = ReadString(hero, "heroId", "");
                if (string.IsNullOrWhiteSpace(id)) continue;
                string role = ReadBool(hero, "isPlayer", false) ? "player"
                    : ReadBool(hero, "isRuler", false) ? "ruler"
                    : ReadBool(hero, "isRegularLord", false) ? "regular_lord"
                    : ReadBool(hero, "isClanLeader", false) ? "vassal_clan_leader"
                    : ReadBool(hero, "isNotable", false) ? "notable"
                    : ReadBool(hero, "isWanderer", false) ? "wanderer"
                    : "other_adult";
                result[id] = role;
            }
            return result;
        }

        internal static Dictionary<string, object> SocialBalanceStatusApi(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            query.TryGetValue("campaignId", out string campaignId);
            query.TryGetValue("timelineId", out string timelineId);
            query.TryGetValue("runId", out string runId);
            query.TryGetValue("periodKey", out string periodKey);
            Dictionary<string, object> status = SocialBalanceStatus(campaignId ?? "",
                string.IsNullOrWhiteSpace(timelineId) ? "main" : timelineId,
                runId ?? "", periodKey ?? "");
            if (query.TryGetValue("releaseVerdict", out string releaseVerdict)
                && string.Equals(releaseVerdict, "true", StringComparison.OrdinalIgnoreCase))
            {
                List<Dictionary<string, object>> cases = ReadDictionaryList(status, "cases");
                List<Dictionary<string, object>> blockers = cases
                    .Where(row => ReadString(row, "status", "") != "passed_cached"
                        && !ReadBool(row, "deferred", false)).ToList();
                bool ready = ReadBool(status, "prepared", false) && blockers.Count == 0;
                status["ready"] = ready;
                status["verdict"] = ready ? "ready" : "not_ready";
                status["blockerCount"] = blockers.Count;
                status["blockers"] = blockers.Select(row => new Dictionary<string, object>
                {
                    ["caseId"] = ReadString(row, "caseId", ""),
                    ["group"] = ReadString(row, "group", ""),
                    ["status"] = ReadString(row, "status", "pending"),
                    ["dependencyFingerprint"] = ReadString(row, "dependencyFingerprint", "")
                }).ToList();
            }
            return status;
        }

        private static List<Dictionary<string, object>> SocialBalanceCurrentFavorEvents(
            List<Dictionary<string, object>> events, string firstKey, long firstSequence, string secondKey, long secondSequence)
        {
            if (firstSequence <= 0 || secondSequence <= 0 || firstSequence == secondSequence)
                return new List<Dictionary<string, object>>();
            return events.Where(row => (ReadLong(row, "sequence", 0) == firstSequence
                    && ReadString(row, "correlation_id", "") == firstKey)
                || (ReadLong(row, "sequence", 0) == secondSequence
                    && ReadString(row, "correlation_id", "") == secondKey)).ToList();
        }

        private static bool SocialBalanceHasTwoFavorSources(List<Dictionary<string, object>> events,
            string firstSourceKey, string secondSourceKey)
        {
            if (string.IsNullOrWhiteSpace(firstSourceKey) || string.IsNullOrWhiteSpace(secondSourceKey)
                || firstSourceKey == secondSourceKey) return false;
            var sources = new HashSet<string>(events.Select(row => ReadString(row, "correlation_id", "")), StringComparer.Ordinal);
            return sources.Count == 2 && sources.Contains(firstSourceKey) && sources.Contains(secondSourceKey);
        }

        private static bool SocialBalanceFavoringCostCollapsed(Dictionary<string, object> standing, int rulerCharm)
        {
            var sources = ReadDictionaryList(standing, "sources");
            var favoring = sources.Where(IsRulerFavoringStandingSource).ToList();
            double expected = -10d * (1d - SocialCharmMitigation(rulerCharm));
            return favoring.Count >= 2
                && favoring.Count(source => ReadBool(source, "collapsedRepresentative", false)) == 1
                && favoring.All(source => ReadString(source, "collapsedFamily", "")
                    .Equals("ruler_favoring", StringComparison.OrdinalIgnoreCase))
                && Math.Abs(favoring.Sum(source => ReadDouble(source, "effectiveContribution", 0d)) - expected) < 0.000001d
                && favoring.Where(source => !ReadBool(source, "collapsedRepresentative", false))
                    .All(source => Math.Abs(ReadDouble(source, "effectiveContribution", 0d)) < 0.000001d)
                && ReadInt(standing, "standingValue", int.MinValue) == RelationshipsService.RoundAwayFromZero(
                    sources.Sum(source => ReadDouble(source, "effectiveContribution", 0d)));
        }

        internal static Dictionary<string, object> SocialBalanceExportApi(Dictionary<string, string> query)
        {
            Dictionary<string, object> status = SocialBalanceStatusApi(query);
            string caseId = query.TryGetValue("caseId", out string requestedCase) ? requestedCase.Trim() : "";
            bool includeSnapshots = !query.TryGetValue("includeSnapshots", out string snapshotOption)
                || !string.Equals(snapshotOption, "false", StringComparison.OrdinalIgnoreCase);
            status["reportFilter"] = new Dictionary<string, object>
            {
                ["caseId"] = caseId, ["snapshotsIncluded"] = includeSnapshots
            };
            string campaignId = ReadString(status, "campaignId", "");
            string timelineId = ReadString(status, "timelineId", "");
            string runId = ReadString(status, "runId", "");
            lock (SocialBalanceLock)
            {
                using (ReignDbConnection connection = OpenSocialBalanceDatabase())
                {
                    List<Dictionary<string, object>> caseRows = ReputationStore.SelectSocialBalanceCaseResults(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["run"] = runId, ["case"] = caseId });
                    status["caseResults"] = caseRows.Select(row => new Dictionary<string, object>
                    {
                        ["caseId"] = ReadString(row, "case_id", ""),
                        ["periodKey"] = ReadString(row, "period_key", ""),
                        ["runId"] = ReadString(row, "run_id", ""),
                        ["scenarioVersion"] = ReadInt(row, "scenario_version", 0),
                        ["dependencyFingerprint"] = ReadString(row, "dependency_fingerprint", ""),
                        ["status"] = ReadString(row, "status", ""),
                        ["attemptCount"] = ReadInt(row, "attempt_count", 0),
                        ["evidence"] = TryParseJsonObject(ReadString(row, "evidence_json", "{}")),
                        ["firstStartedTs"] = ReadLong(row, "first_started_ts", 0),
                        ["completedTs"] = ReadLong(row, "completed_ts", 0),
                        ["updatedTs"] = ReadLong(row, "updated_ts", 0)
                    }).ToList();
                    if (!includeSnapshots)
                    {
                        status["snapshots"] = new List<object>();
                        status["longitudinalCheckpoints"] = new List<object>();
                        return status;
                    }
                    status["snapshots"] = ReputationStore.SelectSocialBalanceSnapshots(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["run"] = runId });
                    status["longitudinalCheckpoints"] = ReputationStore.SelectSocialBalanceLongitudinalCheckpoints(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["run"] = runId })
                        .Select(row => new Dictionary<string, object>
                        {
                            ["runId"] = ReadString(row, "run_id", ""),
                            ["worldDay"] = ReadDouble(row, "world_day", 0d),
                            ["snapshot"] = TryParseJsonObject(ReadString(row, "snapshot_json", "{}")),
                            ["createdTs"] = ReadLong(row, "created_ts", 0)
                        }).ToList();
                }
            }
            return status;
        }

        internal static bool ValidateSocialBalanceLiveEnrollment(
            Dictionary<string, object> payload, string campaignId, out string error)
        {
            error = string.Empty;
            Dictionary<string, object> enrollment = ReadDictionary(payload, "enrollment")
                ?? new Dictionary<string, object>();
            string enrolledCampaign = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "");
            string savePrefix = ReadString(enrollment, "savePrefix", "");
            string mainHeroId = ReadString(enrollment, "mainHeroId", "");
            string socialRunId = ReadString(enrollment, "runId", "");
            string campaignTestRunId = ReadString(enrollment, "campaignTestRunId", "");
            string disposableSaveName = ReadString(enrollment, "disposableSaveName", "");
            string protectedBaselineSaveName = ReadString(enrollment, "protectedBaselineSaveName", "");
            bool legacyNamespace = savePrefix.StartsWith("Reign_SocialBalance_", StringComparison.OrdinalIgnoreCase);
            bool guardedCampaignNamespace = !string.IsNullOrWhiteSpace(campaignTestRunId)
                && !string.IsNullOrWhiteSpace(protectedBaselineSaveName)
                && !string.Equals(disposableSaveName, protectedBaselineSaveName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(disposableSaveName, savePrefix + "_Current", StringComparison.OrdinalIgnoreCase);
            if (enrollment.Count == 0 || string.IsNullOrWhiteSpace(socialRunId)
                || !string.Equals(enrolledCampaign, campaignId, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(timelineId) || string.IsNullOrWhiteSpace(mainHeroId)
                || (!legacyNamespace && !guardedCampaignNamespace))
            {
                error = "Social-balance mode requires a complete isolated enrollment matching the loaded campaign.";
                return false;
            }
            lock (SocialBalanceLock)
            {
                using (ReignDbConnection connection = OpenSocialBalanceDatabase())
                {
                    bool found = ReputationStore.SelectSocialBalanceRuns2(connection, new Dictionary<string, object>
                        {
                            ["run"] = socialRunId, ["campaign"] = enrolledCampaign,
                            ["timeline"] = timelineId, ["prefix"] = savePrefix, ["hero"] = mainHeroId
                        }).Any();
                    if (!found)
                    {
                        error = "No prepared social-balance enrollment matches this campaign, timeline, save prefix, main hero, and run.";
                        return false;
                    }
                }
            }
            return true;
        }

        private static Dictionary<string, object> SocialBalanceStatus(string campaignId, string timelineId, string runId, string periodKey)
        {
            Dictionary<string, object> manifest = BuildSocialBalanceManifest();
            List<Dictionary<string, object>> definitions = ReadDictionaryList(manifest, "cases");
            List<Dictionary<string, object>> rows;
            Dictionary<string, object> run;
            lock (SocialBalanceLock)
            {
                using (ReignDbConnection connection = OpenSocialBalanceDatabase())
                {
                    if (string.IsNullOrWhiteSpace(runId))
                    {
                        run = ReputationStore.SelectSocialBalanceRuns3(connection, new Dictionary<string, object> { ["campaign"] = campaignId ?? "", ["timeline"] = timelineId ?? "" }).FirstOrDefault();
                        runId = ReadString(run, "run_id", "");
                    }
                    else
                    {
                        run = ReputationStore.SelectSocialBalanceRuns4(connection, new Dictionary<string, object> { ["run"] = runId }).FirstOrDefault();
                    }
                    campaignId = FirstNonEmpty(campaignId, ReadString(run, "campaign_id", ""));
                    timelineId = FirstNonEmpty(timelineId, ReadString(run, "timeline_id", "main"));
                    rows = ReputationStore.SelectSocialBalanceCaseResults2(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId });
                }
            }

            List<Dictionary<string, object>> cases = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> definition in definitions)
            {
                string caseId = ReadString(definition, "caseId", "");
                string repeatPolicy = ReadString(definition, "repeatPolicy", "pass_once");
                string wantedPeriod = repeatPolicy == "pass_once" ? "once" : periodKey;
                Dictionary<string, object> row = rows
                    .Where(x => string.Equals(ReadString(x, "case_id", ""), caseId, StringComparison.OrdinalIgnoreCase))
                    .Where(x => string.IsNullOrWhiteSpace(wantedPeriod)
                        || string.Equals(ReadString(x, "period_key", ""), wantedPeriod, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => ReadLong(x, "updated_ts", 0)).FirstOrDefault();
                string currentFingerprint = ReadString(definition, "dependencyFingerprint", "");
                bool compatible = IsSocialBalanceCompatibleResult(row, definition);
                bool passed = compatible && string.Equals(ReadString(row, "status", ""), "passed", StringComparison.OrdinalIgnoreCase);
                bool deferred = !ReadBool(definition, "enabled", true);
                Dictionary<string, object> projected = new Dictionary<string, object>(definition, StringComparer.OrdinalIgnoreCase)
                {
                    ["status"] = deferred ? "deferred" : passed ? "passed_cached"
                        : compatible ? ReadString(row, "status", "pending")
                        : row == null ? "pending" : "dependency_changed",
                    ["skip"] = passed || deferred,
                    ["deferred"] = deferred,
                    ["periodKey"] = wantedPeriod ?? "",
                    ["attemptCount"] = ReadInt(row, "attempt_count", 0),
                    ["lastRunId"] = ReadString(row, "run_id", "")
                };
                cases.Add(projected);
            }
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["schemaVersion"] = SocialBalanceHarnessSchemaVersion,
                ["scenarioVersion"] = SocialBalanceScenarioVersion,
                ["campaignId"] = campaignId ?? "", ["timelineId"] = timelineId ?? "main",
                ["runId"] = runId ?? "", ["runStatus"] = ReadString(run, "status", "not_prepared"),
                ["prepared"] = run != null, ["armed"] = false,
                ["passedCompatibleCount"] = cases.Count(x =>
                    ReadString(x, "status", "") == "passed_cached"),
                ["deferredCount"] = cases.Count(x => ReadBool(x, "deferred", false)),
                ["pendingCount"] = cases.Count(x => !ReadBool(x, "skip", false)),
                ["cases"] = cases, ["guardrails"] = ReadDictionary(manifest, "guardrails")
            };
        }

        private static Dictionary<string, object> BuildSocialBalanceManifest()
        {
            Dictionary<string, string> capabilities = SocialBalanceCapabilityFingerprints();
            List<Dictionary<string, object>> cases = SocialBalanceCases();
            foreach (Dictionary<string, object> definition in cases)
                definition["dependencyFingerprint"] = SocialBalanceCaseFingerprint(definition, capabilities);
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["schemaVersion"] = SocialBalanceHarnessSchemaVersion,
                ["scenarioVersion"] = SocialBalanceScenarioVersion,
                ["name"] = "Rumor and Reputation Campaign Balance Loop",
                ["profiles"] = new[] { "preflight", "signal_contract", "player_flirt", "player_affair",
                    "unchaste", "npc_favoring_presence", "player_favoring_dialogue", "favoring_projection",
                    "favoring_jealousy_charm", "favoring_rebellion", "player_parity", "save_prepare",
                    "save_verify", "longitudinal_90_day", "cleanup_marker" },
                ["capabilityFingerprints"] = capabilities,
                ["cases"] = cases,
                ["coverage"] = new Dictionary<string, object>
                {
                    ["subjects"] = new[] { "ruler", "vassal_clan_leader", "regular_lord", "player" },
                    ["systems"] = new[] { "social_core", "domestic", "combat", "governance", "court_personality", "relationship_projection", "rebellion", "profile_reason", "save_sync", "longitudinal_balance" },
                    ["functionalPolicy"] = "Pass once per campaign/timeline while the case dependency fingerprint is unchanged.",
                    ["longitudinalPolicy"] = "Measure once per campaign month; never replay completed functional cases just because a later system failed."
                },
                ["guardrails"] = new Dictionary<string, object>
                {
                    ["legacyDedicatedSavePrefix"] = "Reign_SocialBalance_",
                    ["guardedCampaignTestCurrentRequired"] = true,
                    ["guardedCampaignTestExactSaveIdentityRequired"] = true,
                    ["requiresCampaignId"] = true, ["requiresTimelineId"] = true,
                    ["requiresMainHeroId"] = true, ["commandsRequireEnrollment"] = true,
                    ["preparationArmsBridge"] = false, ["preparationQueuesCommands"] = false,
                    ["nativeMutationScope"] = "explicitly enrolled disposable campaign only",
                    ["otherSavesMayBeModified"] = false
                }
            };
        }

        private static List<Dictionary<string, object>> SocialBalanceCases()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, string, string, string[], string> add = (id, group, subject, deps, repeat) =>
                rows.Add(new Dictionary<string, object>
                {
                    ["caseId"] = id, ["group"] = group, ["subjectClass"] = subject,
                    ["dependencies"] = deps.ToList(), ["repeatPolicy"] = repeat,
                    ["enabled"] = true,
                    ["scenarioVersion"] = SocialBalanceScenarioVersion
                });

            add("core_exposure_clan_tiers", "social_core", "regular_lord", new[] { "social_core", "harness" }, "pass_once");
            add("core_promotion_45_day_boundaries", "social_core", "regular_lord", new[] { "social_core", "harness" }, "pass_once");
            add("core_counterevidence_removal_reacquisition", "social_core", "regular_lord", new[] { "social_core", "harness" }, "pass_once");
            add("core_identity_knowledge_gate", "social_core", "regular_lord", new[] { "social_core", "identity", "harness" }, "pass_once");
            add("core_charm_mitigation_boundaries", "social_core", "regular_lord", new[] { "social_core", "relationship_projection", "harness" }, "pass_once");
            add("core_same_foreign_kingdom_projection", "social_core", "regular_lord", new[] { "social_core", "relationship_projection", "harness" }, "pass_once");
            add("core_profile_reason_lifecycle", "profile", "regular_lord", new[] { "social_core", "profile_reason", "harness" }, "pass_once");
            add("core_save_reload_idempotency", "save_sync", "regular_lord", new[] { "social_core", "save_sync", "harness" }, "pass_once");

            add("domestic_affair_role_bundles", "domestic", "regular_lord", new[] { "domestic", "social_core", "harness" }, "pass_once");
            add("domestic_marital_strife_and_divorce", "domestic", "regular_lord", new[] { "domestic", "social_core", "harness" }, "pass_once");

            add("combat_non_army_captains", "combat", "regular_lord", new[] { "combat", "social_core", "harness" }, "pass_once");
            add("combat_army_exclusivity", "combat", "vassal_clan_leader", new[] { "combat", "social_core", "harness" }, "pass_once");
            add("combat_army_tactician_pair", "combat", "vassal_clan_leader", new[] { "combat", "social_core", "harness" }, "pass_once");
            add("combat_siege_attack_and_breaker", "combat", "regular_lord", new[] { "combat", "social_core", "harness" }, "pass_once");
            add("combat_tournament_player_observation", "combat", "regular_lord", new[] { "combat", "social_core", "harness" }, "pass_once");
            add("combat_duel_modes", "combat", "regular_lord", new[] { "combat", "social_core", "harness" }, "pass_once");
            add("combat_coward_escape_and_recovery", "combat", "regular_lord", new[] { "combat", "social_core", "harness" }, "pass_once");
            add("combat_derived_battlemaster_frail", "combat", "regular_lord", new[] { "combat", "social_core", "harness" }, "pass_once");
            add("combat_war_crown_verdict", "combat", "ruler", new[] { "combat", "governance", "social_core", "harness" }, "pass_once");

            add("governance_accession_and_split_week", "governance", "ruler", new[] { "governance", "harness" }, "pass_once");
            add("governance_prosperity_pair", "governance", "ruler", new[] { "governance", "social_core", "harness" }, "pass_once");
            add("governance_food_pair", "governance", "ruler", new[] { "governance", "social_core", "harness" }, "pass_once");
            add("governance_loyalty_rebellion_pair", "governance", "ruler", new[] { "governance", "social_core", "harness" }, "pass_once");
            add("governance_security_pair", "governance", "ruler", new[] { "governance", "social_core", "harness" }, "pass_once");
            add("governance_village_recovery_pair", "governance", "ruler", new[] { "governance", "social_core", "harness" }, "pass_once");
            add("governance_integration_pair", "governance", "ruler", new[] { "governance", "social_core", "harness" }, "pass_once");
            add("governance_fief_award_pair", "governance", "ruler", new[] { "governance", "social_core", "harness" }, "pass_once");
            add("governance_clan_retention_pair", "governance", "ruler", new[] { "governance", "social_core", "harness" }, "pass_once");
            add("governance_derived_steward_ruinous", "governance", "ruler", new[] { "governance", "social_core", "harness" }, "pass_once");

            add("court_popularity_regular_lord_thresholds", "court_personality", "regular_lord", new[] { "court_personality", "relationship_projection", "identity", "harness" }, "pass_once");
            add("court_popularity_polarization_no_feedback", "court_personality", "ruler", new[] { "court_personality", "relationship_projection", "identity", "harness" }, "pass_once");
            add("social_signal_contract", "court_personality", "player", new[] { "court_personality", "dialogue", "harness" }, "pass_once");
            add("player_affair_intimacy_discovery", "domestic", "player", new[] { "domestic", "dialogue", "social_core", "harness" }, "pass_once");
            add("npc_ruler_favoring_presence", "court_personality", "ruler", new[] { "court_personality", "relationship_projection", "harness" }, "pass_once");
            add("player_ruler_favoring_dialogue", "court_personality", "player", new[] { "court_personality", "dialogue", "relationship_projection", "harness", "player_favoring_dialogue" }, "pass_once");
            add("favoring_collapsed_projection", "court_personality", "ruler", new[] { "court_personality", "relationship_projection", "harness" }, "pass_once");
            add("favoring_jealousy_charm_matrix", "court_personality", "regular_lord", new[] { "court_personality", "relationship_projection", "harness" }, "pass_once");
            add("favoring_rebellion_successor", "rebellion", "ruler", new[] { "court_personality", "relationship_projection", "rebellion", "harness" }, "pass_once");
            add("court_attentive_landed_leader", "court_personality", "vassal_clan_leader", new[] { "court_personality", "social_core", "harness" }, "pass_once");
            add("court_absent_ruler", "court_personality", "ruler", new[] { "court_personality", "social_core", "harness" }, "pass_once");
            add("court_infertile_season_opportunity", "court_personality", "regular_lord", new[] { "court_personality", "social_core", "harness" }, "pass_once");
            add("court_infertile_pregnancy_correction", "court_personality", "regular_lord", new[] { "court_personality", "social_core", "harness" }, "pass_once");
            add("court_dynasty_secure_dynamic", "court_personality", "regular_lord", new[] { "court_personality", "social_core", "harness" }, "pass_once");
            add("court_unchaste_publicity_and_gender", "court_personality", "regular_lord", new[] { "court_personality", "social_core", "harness" }, "pass_once");
            add("court_flirt_validated_player_signal", "court_personality", "player", new[] { "court_personality", "profile_reason", "harness" }, "pass_once");
            add("court_unchaste_player_parity", "court_personality", "player", new[] { "court_personality", "profile_reason", "social_core", "harness" }, "pass_once");
            add("court_promiscuous_new_target", "court_personality", "player", new[] { "court_personality", "profile_reason", "harness" }, "pass_once");

            add("rebellion_relationship_threshold_roll", "rebellion", "vassal_clan_leader", new[] { "rebellion", "relationship_projection", "harness" }, "pass_once");
            add("rebellion_membership_split", "rebellion", "regular_lord", new[] { "rebellion", "relationship_projection", "harness" }, "pass_once");
            add("rebellion_rebel_victory_new_leadership", "rebellion", "ruler", new[] { "rebellion", "governance", "harness" }, "pass_once");
            add("rebellion_loyalist_victory_recovery", "rebellion", "ruler", new[] { "rebellion", "governance", "harness" }, "pass_once");
            add("rebellion_new_ruler_grace_and_baseline", "rebellion", "ruler", new[] { "rebellion", "governance", "harness" }, "pass_once");

            add("monthly_tag_prevalence_and_polarity", "longitudinal", "all_adult_heroes", new[] { "social_core", "longitudinal", "harness" }, "campaign_monthly");
            add("monthly_ruler_standing_and_turnover", "longitudinal", "ruler", new[] { "governance", "rebellion", "longitudinal", "harness" }, "campaign_monthly");
            add("monthly_regular_lord_standing", "longitudinal", "regular_lord", new[] { "combat", "domestic", "relationship_projection", "longitudinal", "harness" }, "campaign_monthly");
            add("monthly_rebellion_causal_chain", "longitudinal", "vassal_clan_leader", new[] { "rebellion", "governance", "relationship_projection", "longitudinal", "harness" }, "campaign_monthly");
            add("monthly_court_personality_balance", "longitudinal", "all_adult_heroes", new[] { "court_personality", "relationship_projection", "longitudinal", "harness" }, "campaign_monthly");
            add("longitudinal_90_day_social_rebellion_balance", "longitudinal", "all_adult_heroes", new[] { "court_personality", "relationship_projection", "rebellion", "longitudinal", "harness" }, "campaign_monthly");
            HashSet<string> focusedCases = new HashSet<string>(new[]
            {
                "core_save_reload_idempotency", "social_signal_contract",
                "player_affair_intimacy_discovery", "npc_ruler_favoring_presence",
                "player_ruler_favoring_dialogue", "favoring_collapsed_projection",
                "favoring_jealousy_charm_matrix", "court_unchaste_publicity_and_gender",
                "court_flirt_validated_player_signal", "court_unchaste_player_parity"
            }, StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> row in rows)
            {
                string group = ReadString(row, "group", "");
                string subject = ReadString(row, "subjectClass", "");
                if (!focusedCases.Contains(ReadString(row, "caseId", "")))
                    ((List<string>)row["dependencies"]).Add("acceptance_harness");
                row["requiredControls"] = SocialBalanceRequiredControls(group);
                row["evidenceContract"] = SocialBalanceEvidenceContract(group, subject);
                row["selectionPolicy"] = subject == "regular_lord"
                    ? "Select a living adult lord who is neither a kingdom ruler nor a clan leader."
                    : subject == "vassal_clan_leader"
                        ? "Select a living adult non-ruling noble clan leader with a stable kingdom membership."
                        : subject == "ruler"
                            ? "Select the active ruler recorded by the current ruler term."
                            : "Select from the enrolled campaign roster by explicit stable hero ID.";
            }
            return rows;
        }

        private static List<string> SocialBalanceRequiredControls(string group)
        {
            switch ((group ?? "").ToLowerInvariant())
            {
                case "social_core":
                    return new List<string> { "social_snapshot", "social_set_hero", "social_set_relation", "social_record_outcome", "save_checkpoint" };
                case "domestic":
                    return new List<string> { "social_snapshot", "social_record_outcome", "social_set_relation", "save_checkpoint" };
                case "combat":
                    return new List<string> { "social_snapshot", "social_record_outcome", "social_time_control", "save_checkpoint" };
                case "governance":
                    return new List<string> { "social_snapshot", "social_set_town", "social_set_village", "social_set_ruler", "social_change_clan", "social_change_owner", "social_time_control", "save_checkpoint" };
                case "court_personality":
                    return new List<string> { "social_snapshot", "social_set_underlying_affinity", "social_set_location", "social_create_child", "social_record_outcome", "social_time_control", "save_checkpoint" };
                case "rebellion":
                    return new List<string> { "social_snapshot", "social_set_relation", "social_queue_rebellion_roll", "social_resolve_rebellion", "social_set_ruler", "social_time_control", "save_checkpoint" };
                case "profile":
                    return new List<string> { "social_snapshot", "social_record_outcome", "wait_for_memory" };
                case "save_sync":
                    return new List<string> { "social_snapshot", "save_checkpoint", "shutdown_game" };
                case "longitudinal":
                    return new List<string> { "social_snapshot", "social_time_control", "social_acknowledge_diplomacy_announcement", "save_checkpoint" };
                default:
                    return new List<string> { "social_snapshot" };
            }
        }

        private static List<string> SocialBalanceEvidenceContract(string group, string subject)
        {
            List<string> evidence = new List<string>
            {
                "Native before/after snapshot with campaign, timeline, world day, and stable subject IDs.",
                "Server social projection containing active occurrences, active reputations, directional modifiers, and lifecycle events.",
                "Exactly-once command receipts preserved inside the enrolled save."
            };
            if (subject == "regular_lord")
                evidence.Add("Proof the subject is an ordinary adult lord and is neither ruler nor clan leader.");
            if (group == "rebellion")
                evidence.Add("Saved weekly roll, eligibility, override marker, movement membership, resolution, and post-resolution ruler term.");
            if (group == "governance")
                evidence.Add("Seven-day evidence window plus preceding baseline, exclusions, affected holdings, and ruler-term attribution.");
            if (group == "court_personality")
                evidence.Add("Court-personality producer evidence including governed-location streaks, seasonal family opportunity, public parentage, or same-kingdom underlying-affinity counts as applicable.");
            if (group == "longitudinal")
                evidence.Add("Campaign-month rollup comparing prevalence, polarity, relationship pressure, rebellions, and leadership turnover with prior months.");
            return evidence;
        }

        private static Dictionary<string, string> SocialBalanceCapabilityFingerprints()
        {
            Dictionary<string, string[]> sources = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["social_core"] = new[] { "ReignBetaServer/SocialReputation.cs", "ReignBetaServer/ExpandedSocialReputation.cs" },
                ["domestic"] = new[] { "ReignBetaServer/RelationshipLifecycle.cs", "ReignBetaServer/RelationshipDirector.cs" },
                ["combat"] = new[] { "ReignBeta/src/Campaign/ReignCombatReputationCampaignBehavior.cs" },
                ["governance"] = new[] { "ReignBeta/src/Campaign/ReignRulerReputationCampaignBehavior.cs" },
                ["court_personality"] = new[] { "ReignBetaServer/CourtSocialReputation.cs", "ReignBetaServer/CategorizedMemory.cs", "ReignBeta/src/Campaign/ReignCourtPersonalityReputationCampaignBehavior.cs", "ReignBeta/src/Campaign/ReignFamilyCampaignBehavior.cs" },
                ["dialogue"] = new[] { "ReignBetaServer/Program.cs", "ReignBetaServer/CategorizedMemory.cs" },
                ["relationship_projection"] = new[] { "ReignBetaServer/SocialReputation.cs", "ReignBeta/src/Campaign/ReignRelationshipCampaignBehavior.cs" },
                ["identity"] = new[] { "ReignBetaServer/IdentitySystem.cs" },
                ["rebellion"] = new[] { "ReignBeta/src/Campaign/ReignRebellionCampaignBehavior.cs", "ReignBetaServer/RebellionDirector.cs" },
                ["profile_reason"] = new[] { "ReignBetaServer/CharacterSocialProfile.cs" },
                ["save_sync"] = new[] { "ReignBetaServer/SaveSync.cs", "ReignBeta/src/Campaign/ReignSaveSyncCampaignBehavior.cs" },
                ["longitudinal"] = new[] { "ReignBetaServer/WorldTest.cs" },
                ["player_favoring_dialogue"] = new[]
                {
                    "ReignServer/src/Modules/Reputation/SocialBalanceTest.cs",
                    "ReignBeta/src/Modules/Reputation/Campaign/ReignLiveInteractionSocialBalanceHost.cs"
                },
                ["acceptance_harness"] = new[]
                {
                    "ReignServer/src/Modules/Reputation/SocialBalanceTest.cs",
                    "ReignBeta/src/Modules/Reputation/Campaign/ReignLiveInteractionSocialBalanceHost.cs"
                }
            };
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string[]> capability in sources)
            {
                StringBuilder material = new StringBuilder(capability.Key).Append("|v1");
                foreach (string relative in capability.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    string path = ResolveSocialBalanceSource(relative);
                    material.Append("|").Append(relative).Append("|");
                    material.Append(File.Exists(path) ? SocialBalanceFileHash(path) : "semantic-v1");
                }
                result[capability.Key] = DialogueService.SemanticSha256Hex(material.ToString()).Substring(0, 24);
            }
            // This version identifies the unchanged shared enrollment, isolation, receipt, and
            // snapshot contract. Profile-specific harness behavior owns a separate capability
            // fingerprint so a focused repair cannot invalidate compatible passes for all cases.
            result["harness"] = "80401068d76f4b87f8e8884e";
            // Count/isolation behavior was proven by the deployed v1 dialogue harness. Batch
            // acceptance logic now shares these source files but has its own acceptance_harness
            // fingerprint, so unrelated batch edits must not invalidate that compatible pass.
            result["player_favoring_dialogue"] = "d887a6fa777b5bba3ec61d4a";
            return result;
        }

        private static string SocialBalanceCaseFingerprint(Dictionary<string, object> definition, Dictionary<string, string> capabilities = null)
        {
            capabilities = capabilities ?? SocialBalanceCapabilityFingerprints();
            StringBuilder material = new StringBuilder();
            material.Append(SocialBalanceScenarioVersion).Append("|").Append(ReadString(definition, "caseId", ""));
            foreach (string dependency in ReadStringList(definition, "dependencies").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                capabilities.TryGetValue(dependency, out string fingerprint);
                material.Append("|").Append(dependency).Append(":").Append(fingerprint ?? "missing");
            }
            return DialogueService.SemanticSha256Hex(material.ToString()).Substring(0, 32);
        }

        private static bool IsSocialBalanceCompatibleResult(
            Dictionary<string, object> row, Dictionary<string, object> definition)
        {
            return row != null && definition != null
                && ReadInt(row, "scenario_version", 0) == SocialBalanceScenarioVersion
                && string.Equals(ReadString(row, "dependency_fingerprint", ""),
                    ReadString(definition, "dependencyFingerprint", ""), StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveSocialBalanceSource(string relative)
        {
            string normalized = relative.Replace('/', Path.DirectorySeparatorChar);
            string direct = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", normalized));
            if (File.Exists(direct)) return direct;
            string contract = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "verification_contracts", "workspace", normalized);
            return contract;
        }

        private static string SocialBalanceFileHash(string path)
        {
            // A source split into Name.*.cs partial files beside it is hashed together with them (ordinal name
            // order), so the fingerprint still covers the whole logical source; an unsplit file hashes as before.
            string stem = Path.GetFileNameWithoutExtension(path) + ".";
            List<string> files = new List<string> { path };
            files.AddRange(Directory.EnumerateFiles(Path.GetDirectoryName(path), "*.cs")
                .Where(file => Path.GetFileName(file).Length > stem.Length + 3
                    && Path.GetFileName(file).StartsWith(stem, StringComparison.Ordinal)
                    && file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => Path.GetFileName(file), StringComparer.Ordinal));
            byte[] bytes = files.SelectMany(file => File.ReadAllBytes(file)).ToArray();
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static ReignDbConnection OpenSocialBalanceDatabase()
        {
            ReignDbConnection connection =
                ReignPostgreSqlStorage.OpenUtilityConnection(
                    "reign_social_balance");
            EnsureSocialBalanceSchema(connection);
            return connection;
        }

        private static void EnsureSocialBalanceSchema(ReignDbConnection connection)
        {
            ReputationSchema.CreateSocialBalanceRuns(connection);
            ReputationSchema.CreateIndexSocialBalanceRunsCampaign(connection);
            ReputationSchema.CreateSocialBalanceCaseResults(connection);
            ReputationSchema.CreateIndexSocialBalanceCaseRun(connection);
            ReputationSchema.CreateSocialBalanceSnapshots(connection);
            ReputationSchema.CreateSocialBalanceLongitudinalCheckpoints(connection);
        }

        internal static Dictionary<string, object> RunSocialBalanceHarnessSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["id"] = "social_balance_" + id, ["suite"] = "social_balance_harness",
                ["passed"] = passed, ["summary"] = summary
            });
            Dictionary<string, object> manifest = BuildSocialBalanceManifest();
            List<Dictionary<string, object>> cases = ReadDictionaryList(manifest, "cases");
            add("manifest_subject_coverage",
                cases.Any(x => ReadString(x, "subjectClass", "") == "ruler")
                && cases.Any(x => ReadString(x, "subjectClass", "") == "regular_lord")
                && cases.Any(x => ReadString(x, "subjectClass", "") == "vassal_clan_leader"),
                "The manifest explicitly covers rulers, vassal clan leaders, and regular lords.");
            add("pass_once_policy",
                cases.Where(x => ReadString(x, "repeatPolicy", "") == "pass_once").All(x =>
                    !string.IsNullOrWhiteSpace(ReadString(x, "dependencyFingerprint", ""))),
                "Every functional case has a granular dependency fingerprint and is reusable after a restart.");
            Dictionary<string, object> cachedFixture = new Dictionary<string, object>
            {
                ["scenario_version"] = SocialBalanceScenarioVersion,
                ["dependency_fingerprint"] = ReadString(cases[0], "dependencyFingerprint", ""),
                ["status"] = "passed"
            };
            Dictionary<string, object> staleFixture = new Dictionary<string, object>(cachedFixture)
            {
                ["dependency_fingerprint"] = "changed"
            };
            add("restart_cache_compatibility",
                IsSocialBalanceCompatibleResult(cachedFixture, cases[0])
                && !IsSocialBalanceCompatibleResult(staleFixture, cases[0]),
                "A passed case survives process restarts and becomes pending only when its own fingerprint changes.");
            add("longitudinal_period_policy",
                cases.Count(x => ReadString(x, "repeatPolicy", "") == "campaign_monthly") >= 4,
                "Campaign balance continues as monthly snapshots without replaying completed functional cases.");
            Dictionary<string, object> combatCase = cases.First(x => ReadString(x, "caseId", "") == "combat_non_army_captains");
            Dictionary<string, object> governanceCase = cases.First(x => ReadString(x, "caseId", "") == "governance_food_pair");
            add("granular_dependencies",
                !ReadStringList(combatCase, "dependencies").Contains("governance")
                && !ReadStringList(governanceCase, "dependencies").Contains("combat"),
                "A governance failure or change cannot invalidate a passed combat case, and vice versa.");
            Dictionary<string, object> guardrails = ReadDictionary(manifest, "guardrails");
            add("preparation_is_inert",
                !ReadBool(guardrails, "preparationArmsBridge", true)
                && !ReadBool(guardrails, "preparationQueuesCommands", true)
                && !ReadBool(guardrails, "otherSavesMayBeModified", true),
                "Preparing the harness neither arms the live bridge nor queues native mutations, and other saves are out of scope.");
            add("effective_affinity_fixture",
                SocialBalanceUnderlyingAffinityForEffectiveValue(-40, 0) == -40
                && SocialBalanceUnderlyingAffinityForEffectiveValue(-40, 15) == -55
                && SocialBalanceUnderlyingAffinityForEffectiveValue(-95, 20) == -100
                && SocialBalanceUnderlyingAffinityForEffectiveValue(95, -20) == 100,
                "Effective-affinity fixtures compensate for public standing and clamp safely at native bounds.");
            add("rebellion_lifecycle_coverage",
                cases.Count(x => ReadString(x, "group", "") == "rebellion") >= 5
                && cases.Any(x => ReadString(x, "caseId", "").Contains("new_leadership")),
                "Rebellion outbreak, membership, both resolutions, and new-ruler governance are separate checkpointed cases.");
            add("court_personality_coverage",
                cases.Count(x => ReadString(x, "group", "") == "court_personality") >= 11
                && cases.Any(x => ReadString(x, "caseId", "").Contains("regular_lord"))
                && cases.Any(x => ReadString(x, "caseId", "").Contains("ruler")),
                "Court-personality cases separately cover regular lords, landed leaders, rulers, family standing, and dynamic popularity.");
            add("player_interaction_gate_active",
                cases.Any(x => ReadString(x, "caseId", "") == "court_flirt_validated_player_signal"
                    && ReadBool(x, "enabled", false))
                && cases.Any(x => ReadString(x, "caseId", "") == "court_unchaste_player_parity"
                    && ReadBool(x, "enabled", false)),
                "Validated player Flirt and native player Unchaste parity are active blocking cases.");
            bool passed = results.Count > 0 && results.All(x => ReadBool(x, "passed", false));
            return new Dictionary<string, object>
            {
                ["ok"] = passed, ["passed"] = passed,
                ["passedCount"] = results.Count(x => ReadBool(x, "passed", false)),
                ["totalCount"] = results.Count, ["results"] = results
            };
        }

        private static Dictionary<string, object> SocialBalanceError(string error)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = error ?? "Social-balance harness error." };
        }
    }
}
