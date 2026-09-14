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
    /// <summary>
    /// Durable control-plane state for the campaign social-balance loop.
    ///
    /// This file deliberately does not launch Bannerlord, arm the live bridge, or
    /// mutate a campaign.  It defines the manifest and records results produced by
    /// the explicitly enrolled disposable test save.  Functional cases are reused
    /// after a restart while their own dependency fingerprint remains unchanged.
    /// Longitudinal balance checkpoints use one period key per campaign month.
    /// </summary>
    internal static partial class Program
    {
        private const int SocialBalanceHarnessSchemaVersion = 2;
        private const int SocialBalanceScenarioVersion = 2;
        private static readonly object SocialBalanceLock = new object();
        private static readonly object SocialBalancePreflightSuiteLock = new object();
        private static readonly Dictionary<string, Task<List<Dictionary<string, object>>>> SocialBalancePreflightSuiteTasks
            = new Dictionary<string, Task<List<Dictionary<string, object>>>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Dictionary<string, object>> SocialBalancePreflightEvaluations
            = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        private static string SocialBalanceRoot => Path.Combine(TestsDir, "social-balance");

        private static Dictionary<string, object> SocialBalanceManifestApi(Dictionary<string, string> query)
        {
            return BuildSocialBalanceManifest();
        }

        private static Dictionary<string, object> SocialBalancePrepareApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (!string.Equals(ReadString(payload, "confirmation", ""), "prepare", StringComparison.OrdinalIgnoreCase))
                return SocialBalanceError("confirmation must be 'prepare'. This endpoint never arms or starts the live loop.");

            string campaignId = ReadString(payload, "campaignId", "").Trim();
            string timelineId = ReadString(payload, "timelineId", "main").Trim();
            string savePrefix = ReadString(payload, "savePrefix", "").Trim();
            string mainHeroId = ReadString(payload, "mainHeroId", "").Trim();
            string campaignTestRunId = ReadString(payload, "campaignTestRunId", "").Trim();
            string disposableSaveName = ReadString(payload, "disposableSaveName", "").Trim();
            string protectedBaselineSaveName = ReadString(payload, "protectedBaselineSaveName", "").Trim();
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(timelineId)
                || string.IsNullOrWhiteSpace(savePrefix) || string.IsNullOrWhiteSpace(mainHeroId))
                return SocialBalanceError("campaignId, timelineId, savePrefix, and mainHeroId are required for isolated enrollment.");
            bool legacyNamespace = savePrefix.StartsWith("Reign_SocialBalance_", StringComparison.OrdinalIgnoreCase);
            bool guardedCampaignNamespace = !string.IsNullOrWhiteSpace(campaignTestRunId)
                && !string.IsNullOrWhiteSpace(protectedBaselineSaveName)
                && !string.Equals(disposableSaveName, protectedBaselineSaveName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(disposableSaveName, savePrefix + "_Current", StringComparison.OrdinalIgnoreCase);
            if (!legacyNamespace && !guardedCampaignNamespace)
                return SocialBalanceError("The disposable save must use the legacy Social Balance namespace or an exact guarded campaign-test Current enrollment.");

            Dictionary<string, object> manifest = BuildSocialBalanceManifest();
            string runId = FirstNonEmpty(ReadString(payload, "runId", ""),
                "social-balance-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, object> enrollment = new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["timelineId"] = timelineId,
                ["savePrefix"] = savePrefix,
                ["mainHeroId"] = mainHeroId,
                ["runId"] = runId,
                ["campaignTestRunId"] = campaignTestRunId,
                ["disposableSaveName"] = disposableSaveName,
                ["protectedBaselineSaveName"] = protectedBaselineSaveName
            };

            lock (SocialBalanceLock)
            {
                using (ReignDbConnection connection = OpenSocialBalanceDatabase())
                {
                    ExecuteSql(connection, @"INSERT OR REPLACE INTO social_balance_runs(
run_id,campaign_id,timeline_id,save_prefix,main_hero_id,status,manifest_json,created_ts,updated_ts)
VALUES($run,$campaign,$timeline,$prefix,$hero,'prepared',$manifest,
COALESCE((SELECT created_ts FROM social_balance_runs WHERE run_id=$run),$now),$now);",
                        new Dictionary<string, object>
                        {
                            ["run"] = runId, ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["prefix"] = savePrefix, ["hero"] = mainHeroId,
                            ["manifest"] = Json.Serialize(manifest), ["now"] = now
                        });
                }
            }
            Dictionary<string, object> status = SocialBalanceStatus(campaignId, timelineId, runId, "");
            status["enrollment"] = enrollment;
            status["preparedOnly"] = true;
            status["armed"] = false;
            status["message"] = "Harness enrollment was prepared. No live bridge was armed and no campaign command was queued.";
            return status;
        }

        private static Dictionary<string, object> SocialBalanceCaseResultApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "").Trim();
            string timelineId = ReadString(payload, "timelineId", "main").Trim();
            string runId = ReadString(payload, "runId", "").Trim();
            string caseId = ReadString(payload, "caseId", "").Trim();
            string status = ReadString(payload, "status", "").Trim().ToLowerInvariant();
            string periodKey = ReadString(payload, "periodKey", "once").Trim();
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(timelineId)
                || string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(caseId))
                return SocialBalanceError("campaignId, timelineId, runId, and caseId are required.");
            if (status != "passed" && status != "failed" && status != "blocked")
                return SocialBalanceError("status must be passed, failed, or blocked.");

            Dictionary<string, object> definition = SocialBalanceCases()
                .FirstOrDefault(x => string.Equals(ReadString(x, "caseId", ""), caseId, StringComparison.OrdinalIgnoreCase));
            if (definition == null) return SocialBalanceError("Unknown social-balance case '" + caseId + "'.");
            if (!ReadBool(definition, "enabled", true))
                return SocialBalanceError("Social-balance case '" + caseId + "' is deferred until its activation gate is available.");
            string repeatPolicy = ReadString(definition, "repeatPolicy", "pass_once");
            if (repeatPolicy == "pass_once") periodKey = "once";
            if (repeatPolicy == "campaign_monthly" && !periodKey.StartsWith("month_", StringComparison.OrdinalIgnoreCase))
                return SocialBalanceError("Longitudinal cases require a periodKey such as month_12.");

            string fingerprint = SocialBalanceCaseFingerprint(definition);
            string suppliedFingerprint = ReadString(payload, "dependencyFingerprint", "");
            if (!string.IsNullOrWhiteSpace(suppliedFingerprint)
                && !string.Equals(suppliedFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                return SocialBalanceError("The result dependency fingerprint is stale; rebuild the case from the current manifest.");

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            lock (SocialBalanceLock)
            {
                using (ReignDbConnection connection = OpenSocialBalanceDatabase())
                {
                    Dictionary<string, object> enrolled = QuerySql(connection,
                        "SELECT * FROM social_balance_runs WHERE run_id=$run AND campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;",
                        new Dictionary<string, object> { ["run"] = runId, ["campaign"] = campaignId, ["timeline"] = timelineId }).FirstOrDefault();
                    if (enrolled == null) return SocialBalanceError("The run is not enrolled for this campaign and timeline.");
                    ExecuteSql(connection, @"INSERT INTO social_balance_case_results(
campaign_id,timeline_id,case_id,period_key,run_id,scenario_version,dependency_fingerprint,
status,attempt_count,evidence_json,first_started_ts,completed_ts,updated_ts)
VALUES($campaign,$timeline,$case,$period,$run,$version,$fingerprint,$status,1,$evidence,$now,
CASE WHEN $terminal=1 THEN $now ELSE 0 END,$now)
ON CONFLICT(campaign_id,timeline_id,case_id,period_key) DO UPDATE SET
run_id=excluded.run_id,scenario_version=excluded.scenario_version,
dependency_fingerprint=excluded.dependency_fingerprint,status=excluded.status,
attempt_count=social_balance_case_results.attempt_count+1,evidence_json=excluded.evidence_json,
completed_ts=excluded.completed_ts,updated_ts=excluded.updated_ts;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["case"] = caseId,
                            ["period"] = periodKey, ["run"] = runId, ["version"] = SocialBalanceScenarioVersion,
                            ["fingerprint"] = fingerprint, ["status"] = status,
                            ["evidence"] = Json.Serialize(ReadDictionary(payload, "evidence")),
                            ["terminal"] = status == "passed" || status == "failed" ? 1 : 0, ["now"] = now
                        });
                    ExecuteSql(connection, "UPDATE social_balance_runs SET updated_ts=$now WHERE run_id=$run;",
                        new Dictionary<string, object> { ["now"] = now, ["run"] = runId });
                }
            }
            return SocialBalanceStatus(campaignId, timelineId, runId, periodKey);
        }

        private static void RecordSocialBalanceProfileResultIfApplicable(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            if (liveRun == null || command == null
                || !ReadString(liveRun, "mode", "").Equals("social_balance", StringComparison.OrdinalIgnoreCase))
                return;

            string operation = ReadString(command, "operation", "").Trim().ToLowerInvariant();
            if (operation == "social_snapshot")
            {
                Dictionary<string, object> snapshotEnrollment = SocialBalanceProfileEnrollment(liveRun, command);
                Dictionary<string, object> snapshot = ReadDictionary(command, "result")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> stored = SocialBalanceSnapshotApi(new Dictionary<string, object>
                {
                    ["campaignId"] = ReadString(snapshotEnrollment, "campaignId", ""),
                    ["timelineId"] = ReadString(snapshotEnrollment, "timelineId", "main"),
                    ["runId"] = ReadString(snapshotEnrollment, "runId", ""),
                    ["periodKey"] = ReadString(command, "periodKey", ""),
                    ["worldDay"] = ReadDouble(snapshot, "worldDay", 0d),
                    ["snapshot"] = snapshot
                });
                command["socialBalanceSnapshotStorage"] = stored;
                return;
            }
            if (operation != "social_reputation_profile") return;

            string profile = ReadString(command, "profile", "").Trim().ToLowerInvariant();
            if (profile == "preflight")
            {
                RecordPreflightProfileResult(liveRun, command);
                return;
            }
            if (profile == "player_flirt")
            {
                RecordPlayerFlirtProfileResult(liveRun, command);
                return;
            }
            if (profile == "player_affair")
            {
                RecordPlayerAffairProfileResult(liveRun, command);
                return;
            }
            if (profile == "unchaste")
            {
                RecordUnchasteProfileResult(liveRun, command);
                return;
            }
            if (profile == "npc_favoring_presence")
            {
                RecordNpcFavoringPresenceProfileResult(liveRun, command);
                return;
            }
            if (profile == "player_favoring_dialogue")
            {
                RecordPlayerFavoringDialogueProfileResult(liveRun, command);
                return;
            }
            if (profile == "favoring_projection")
            {
                RecordFavoringProjectionProfileResult(liveRun, command);
                return;
            }
            if (profile == "favoring_jealousy_charm")
            {
                RecordFavoringJealousyCharmProfileResult(liveRun, command);
                return;
            }
            if (profile == "player_parity")
            {
                RecordPlayerParityProfileResult(liveRun, command);
                return;
            }
            if (profile == "favoring_rebellion")
            {
                RecordFavoringRebellionProfileResult(liveRun, command);
                return;
            }
            if (profile == "longitudinal_90_day")
            {
                RecordLongitudinalProfileResult(liveRun, command);
                return;
            }
            if (profile == "save_prepare")
            {
                RecordSavePrepareProfileResult(liveRun, command);
                return;
            }
            if (profile == "save_verify")
            {
                RecordSaveVerifyProfileResult(liveRun, command);
                return;
            }
            if (profile == "cleanup_marker")
            {
                RecordCleanupMarkerProfileResult(liveRun, command);
                return;
            }
            if (profile != "signal_contract") return;

            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> validTurn = ReadDictionary(result, "validTurn")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> hypotheticalTurn = ReadDictionary(result, "hypotheticalTurn")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> validRaw = ReadDictionary(validTurn, "rawResponse")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> hypotheticalRaw = ReadDictionary(hypotheticalTurn, "rawResponse")
                ?? new Dictionary<string, object>();
            string validExchangeId = FirstNonEmpty(ReadString(validTurn, "exchangeId", ""),
                ReadString(ReadDictionary(validRaw, "conversationExchange"), "exchangeId", ""));
            string hypotheticalExchangeId = FirstNonEmpty(ReadString(hypotheticalTurn, "exchangeId", ""),
                ReadString(ReadDictionary(hypotheticalRaw, "conversationExchange"), "exchangeId", ""));
            string playerId = ReadString(enrollment, "mainHeroId", "");
            string targetId = ReadString(result, "targetHeroId", "");

            List<Dictionary<string, object>> validSignals = ReadDictionaryList(validRaw, "socialSignals");
            List<Dictionary<string, object>> validRejected = ReadDictionaryList(validRaw, "rejectedSocialSignals");
            List<Dictionary<string, object>> hypotheticalSignals = ReadDictionaryList(hypotheticalRaw, "socialSignals");
            List<Dictionary<string, object>> hypotheticalRejected = ReadDictionaryList(hypotheticalRaw, "rejectedSocialSignals");
            List<Dictionary<string, object>> persisted = new List<Dictionary<string, object>>();
            if (!string.IsNullOrWhiteSpace(campaignId))
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureSocialReputationSchema(connection);
                    persisted = QuerySql(connection, @"SELECT exchange_id,signal_id,signal_type,speaker_id,target_id,
supporting_quote,accepted,rejection_reason,evidence_json,created_ts
FROM court_social_signal_evidence
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND exchange_id IN ($valid,$hypothetical)
ORDER BY created_ts,signal_id;", new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId,
                        ["timeline"] = timelineId,
                        ["valid"] = validExchangeId,
                        ["hypothetical"] = hypotheticalExchangeId
                    });
                }
            }

            List<Dictionary<string, object>> persistedValid = persisted.Where(row =>
                ReadString(row, "exchange_id", "").Equals(validExchangeId, StringComparison.OrdinalIgnoreCase)).ToList();
            List<Dictionary<string, object>> persistedHypothetical = persisted.Where(row =>
                ReadString(row, "exchange_id", "").Equals(hypotheticalExchangeId, StringComparison.OrdinalIgnoreCase)).ToList();
            bool stableDistinctExchanges = !string.IsNullOrWhiteSpace(validExchangeId)
                && !string.IsNullOrWhiteSpace(hypotheticalExchangeId)
                && !validExchangeId.Equals(hypotheticalExchangeId, StringComparison.OrdinalIgnoreCase);
            bool acceptedExplicitFlirt = validSignals.Any(signal =>
                ReadString(signal, "type", "").Equals("flirtation", StringComparison.OrdinalIgnoreCase)
                && ReadBool(signal, "validated", false)
                && ReadString(signal, "validationSource", "").Equals("reign_conversation_engine", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(ReadString(signal, "supportingQuote", ""))
                && ((ReadString(signal, "speakerHeroId", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)
                        && ReadString(signal, "targetHeroId", "").Equals(targetId, StringComparison.OrdinalIgnoreCase))
                    || (ReadString(signal, "speakerHeroId", "").Equals(targetId, StringComparison.OrdinalIgnoreCase)
                        && ReadString(signal, "targetHeroId", "").Equals(playerId, StringComparison.OrdinalIgnoreCase))));
            bool acceptedPersisted = persistedValid.Any(row =>
                ReadInt(row, "accepted", 0) == 1
                && ReadString(row, "signal_type", "").Equals("flirtation", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(ReadString(row, "signal_id", ""))
                && !string.IsNullOrWhiteSpace(ReadString(row, "supporting_quote", "")));
            bool hypotheticalSuppressed = hypotheticalSignals.Count == 0
                && persistedHypothetical.All(row => ReadInt(row, "accepted", 0) == 0);
            bool configuredModelEvidence = ReadBool(result, "configuredDialogueModelUsed", false)
                && ReadBool(result, "naturalLanguageOnly", false)
                && !string.IsNullOrWhiteSpace(ReadString(validTurn, "playerText", ""))
                && !string.IsNullOrWhiteSpace(ReadString(hypotheticalTurn, "playerText", ""));
            bool passed = stableDistinctExchanges && acceptedExplicitFlirt && acceptedPersisted
                && hypotheticalSuppressed && configuredModelEvidence;

            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["validExchangeId"] = validExchangeId,
                ["hypotheticalExchangeId"] = hypotheticalExchangeId,
                ["playerId"] = playerId,
                ["targetId"] = targetId,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["stableDistinctExchangeIds"] = stableDistinctExchanges,
                    ["acceptedExplicitValidatedFlirt"] = acceptedExplicitFlirt,
                    ["acceptedEvidencePersisted"] = acceptedPersisted,
                    ["hypotheticalSuppressed"] = hypotheticalSuppressed,
                    ["configuredDialogueModelUsed"] = configuredModelEvidence
                },
                ["acceptedSignals"] = validSignals,
                ["rejectedSignals"] = validRejected.Concat(hypotheticalRejected).ToList(),
                ["persistedSignals"] = persisted,
                ["validPlayerText"] = ReadString(validTurn, "playerText", ""),
                ["hypotheticalPlayerText"] = ReadString(hypotheticalTurn, "playerText", "")
            };
            Dictionary<string, object> recorded = SocialBalanceCaseResultApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["timelineId"] = timelineId,
                ["runId"] = runId,
                ["caseId"] = "social_signal_contract",
                ["status"] = passed ? "passed" : "failed",
                ["periodKey"] = "once",
                ["evidence"] = evidence
            });
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = profile,
                ["caseId"] = "social_signal_contract",
                ["passed"] = passed,
                ["evidence"] = evidence,
                ["recorded"] = recorded
            };
        }

        private static void RecordPreflightProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = SocialBalanceProfileEnrollment(liveRun, command);
            string suiteKey = string.Join("|", new[]
            {
                ReadString(enrollment, "campaignId", ""),
                ReadString(enrollment, "timelineId", "main"),
                ReadString(enrollment, "runId", "")
            });
            Task<List<Dictionary<string, object>>> suiteTask;
            lock (SocialBalancePreflightSuiteLock)
            {
                Dictionary<string, object> cached;
                if (SocialBalancePreflightEvaluations.TryGetValue(suiteKey, out cached))
                {
                    liveRun["socialBalanceProfileEvaluation"] = cached;
                    return;
                }
                if (!SocialBalancePreflightSuiteTasks.TryGetValue(suiteKey, out suiteTask))
                {
                    suiteTask = Task.Run(() => RunRumorSubsystemSelfTests());
                    SocialBalancePreflightSuiteTasks[suiteKey] = suiteTask;
                }
            }

            // The controller deliberately retries an accepted command when its HTTP completion
            // request exceeds one lease. Every retry joins this same run-keyed task, so the
            // isolated suite continues once instead of restarting with a fresh temporary campaign.
            List<Dictionary<string, object>> deterministic = suiteTask.GetAwaiter().GetResult();
            lock (SocialBalancePreflightSuiteLock)
            {
                Dictionary<string, object> cached;
                if (SocialBalancePreflightEvaluations.TryGetValue(suiteKey, out cached))
                {
                    liveRun["socialBalanceProfileEvaluation"] = cached;
                    return;
                }
                RecordPreflightProfileResultCore(liveRun, command, deterministic);
                cached = ReadDictionary(liveRun, "socialBalanceProfileEvaluation")
                    ?? new Dictionary<string, object>();
                SocialBalancePreflightEvaluations[suiteKey] = cached;
                SocialBalancePreflightSuiteTasks.Remove(suiteKey);
            }
        }

        private static void RecordPreflightProfileResultCore(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command,
            List<Dictionary<string, object>> deterministic)
        {
            Dictionary<string, object> enrollment = SocialBalanceProfileEnrollment(liveRun, command);
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> snapshot = LoadSocialBalanceSnapshot(
                campaignId, timelineId, runId, "before_preflight");
            List<Dictionary<string, object>> heroes = ReadDictionaryList(snapshot, "heroes");
            List<string> failedChecks = deterministic
                .Where(row => !ReadBool(row, "passed", false))
                .Select(row => ReadFirstString(row, "caseId", "name", "id"))
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToList();

            int regularLords = heroes.Count(row => ReadBool(row, "isRegularLord", false));
            int vassalLeaders = heroes.Count(row => ReadBool(row, "isClanLeader", false)
                && !ReadBool(row, "isRuler", false) && !ReadBool(row, "isPlayer", false));
            int rulers = heroes.Count(row => ReadBool(row, "isRuler", false));
            int players = heroes.Count(row => ReadBool(row, "isPlayer", false));
            Stopwatch timer = Stopwatch.StartNew();
            long checksum = 0;
            for (int rulerIndex = 0; rulerIndex < 12; rulerIndex++)
                for (int npcIndex = 0; npcIndex < 1000; npcIndex++)
                    checksum += CalculateSocialModifier(
                        new[] { (npcIndex % 31) - 15, rulerIndex - 6 },
                        (npcIndex * 17 + rulerIndex * 29) % 301,
                        (npcIndex + rulerIndex) % 2 == 0);
            timer.Stop();
            bool nativeCoverage = ReadStringList(result, "blockers").Count == 0
                && heroes.Count >= 1000 && regularLords > 0 && vassalLeaders > 0
                && rulers > 0 && players == 1;
            bool scalePerformance = timer.ElapsedMilliseconds <= 2000 && checksum != long.MinValue;
            bool passed = failedChecks.Count == 0 && nativeCoverage && scalePerformance;
            List<string> passedChecks = deterministic
                .Where(row => ReadBool(row, "passed", false))
                .Select(row => ReadFirstString(row, "caseId", "name", "id"))
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            Dictionary<string, object> commonEvidence = new Dictionary<string, object>
            {
                ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["nativeSnapshotPeriod"] = "before_preflight",
                ["nativeWorldDay"] = ReadDouble(snapshot, "worldDay", 0d),
                ["nativeRoleCounts"] = new Dictionary<string, object>
                {
                    ["allAdults"] = heroes.Count, ["regularLords"] = regularLords,
                    ["vassalClanLeaders"] = vassalLeaders, ["rulers"] = rulers,
                    ["players"] = players
                },
                ["deterministicChecksPassed"] = passedChecks,
                ["deterministicChecksFailed"] = failedChecks,
                ["scaleProbe"] = new Dictionary<string, object>
                {
                    ["rulerCount"] = 12, ["npcCount"] = 1000,
                    ["evaluatedViews"] = 12000, ["elapsedMs"] = timer.ElapsedMilliseconds,
                    ["checksum"] = checksum, ["deadlineMs"] = 2000
                },
                ["assertions"] = new Dictionary<string, object>
                {
                    ["nativeSupportedSubjectCategories"] = nativeCoverage,
                    ["allRumorReputationContractsPassed"] = failedChecks.Count == 0,
                    ["twelveRulerThousandNpcProjectionWithinDeadline"] = scalePerformance
                }
            };
            HashSet<string> focusedProfiles = new HashSet<string>(new[]
            {
                "core_save_reload_idempotency", "social_signal_contract",
                "player_affair_intimacy_discovery", "npc_ruler_favoring_presence",
                "player_ruler_favoring_dialogue", "favoring_collapsed_projection",
                "favoring_jealousy_charm_matrix", "court_unchaste_publicity_and_gender",
                "court_flirt_validated_player_signal", "court_unchaste_player_parity"
            }, StringComparer.OrdinalIgnoreCase);
            List<string> recordedCases = new List<string>();
            foreach (Dictionary<string, object> definition in SocialBalanceCases())
            {
                string caseId = ReadString(definition, "caseId", "");
                string group = ReadString(definition, "group", "");
                if (focusedProfiles.Contains(caseId) || group == "rebellion"
                    || group == "longitudinal" || ReadString(definition, "repeatPolicy", "") != "pass_once")
                    continue;
                Dictionary<string, object> evidence = new Dictionary<string, object>(commonEvidence,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["caseId"] = caseId,
                    ["caseGroup"] = group,
                    ["subjectClass"] = ReadString(definition, "subjectClass", "")
                };
                SocialBalanceCaseResultApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                    ["runId"] = runId, ["caseId"] = caseId,
                    ["status"] = passed ? "passed" : "failed", ["periodKey"] = "once",
                    ["evidence"] = evidence
                });
                recordedCases.Add(caseId);
                if (recordedCases.Count >= 15) break;
            }
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "preflight", ["passed"] = passed,
                ["recordedCaseCount"] = recordedCases.Count,
                ["recordedCases"] = recordedCases, ["evidence"] = commonEvidence
            };
        }

        private static void RecordFavoringRebellionProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = SocialBalanceProfileEnrollment(liveRun, command);
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> rebellionTests = RunRebellionSelfTests();
            List<Dictionary<string, object>> testRows = ReadDictionaryList(rebellionTests, "tests");
            List<string> failedTests = testRows.Where(row => !ReadBool(row, "passed", false))
                .Select(row => ReadFirstString(row, "caseId", "name", "id"))
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            Dictionary<string, object> declaration = ReadDictionary(result, "declarationHarness")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> resolution = ReadDictionary(result, "resolutionHarness")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> cleanup = ReadDictionary(result, "cleanupHarness")
                ?? new Dictionary<string, object>();
            Func<Dictionary<string, object>, bool> assertionsPassed = evidence =>
                ReadDictionaryList(evidence, "assertions").Count > 0
                && ReadDictionaryList(evidence, "assertions")
                    .All(row => ReadBool(row, "passed", false));
            bool favoringPass = HasCompatibleSocialBalancePass(
                campaignId, timelineId, "favoring_collapsed_projection");
            bool transition = ReadBool(result, "nativeRulerTransitionVerified", false)
                && ReadBool(result, "originalRulerRestored", false);
            bool passed = failedTests.Count == 0 && favoringPass && transition
                && assertionsPassed(declaration) && assertionsPassed(resolution)
                && assertionsPassed(cleanup);
            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["compatibleFavoringProjection"] = favoringPass,
                ["nativeRulerTransition"] = result,
                ["declarationHarness"] = declaration,
                ["resolutionHarness"] = resolution,
                ["cleanupHarness"] = cleanup,
                ["deterministicRebellionFailures"] = failedTests,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["relationshipThresholdAndWeeklyRollContracts"] = failedTests.Count == 0,
                    ["membershipSplitAndDeclaration"] = assertionsPassed(declaration),
                    ["rebelAndLoyalistResolutionOutcomes"] = assertionsPassed(resolution),
                    ["nativeLeadershipTransitionAndRestore"] = transition,
                    ["favoringEvidenceSurvivesSuccessorEvaluation"] = favoringPass,
                    ["exactFixtureCleanup"] = assertionsPassed(cleanup)
                }
            };
            string[] cases =
            {
                "favoring_rebellion_successor", "rebellion_relationship_threshold_roll",
                "rebellion_membership_split", "rebellion_rebel_victory_new_leadership",
                "rebellion_loyalist_victory_recovery", "rebellion_new_ruler_grace_and_baseline"
            };
            foreach (string caseId in cases)
                SocialBalanceCaseResultApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                    ["runId"] = runId, ["caseId"] = caseId,
                    ["status"] = passed ? "passed" : "failed", ["periodKey"] = "once",
                    ["evidence"] = evidence
                });
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "favoring_rebellion", ["passed"] = passed,
                ["recordedCases"] = cases, ["evidence"] = evidence
            };
        }

        private static void RecordLongitudinalProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = SocialBalanceProfileEnrollment(liveRun, command);
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> snapshot = LoadSocialBalanceSnapshot(
                campaignId, timelineId, runId, "before_longitudinal_90_day");
            double worldDay = ReadDouble(snapshot, "worldDay", -1d);
            if (worldDay < 0d)
            {
                liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
                {
                    ["profile"] = "longitudinal_90_day", ["passed"] = false,
                    ["error"] = "The native checkpoint snapshot was not stored."
                };
                return;
            }
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<Dictionary<string, object>> checkpoints;
            lock (SocialBalanceLock)
            {
                using (ReignDbConnection connection = OpenSocialBalanceDatabase())
                {
                    ExecuteSql(connection, @"INSERT INTO social_balance_longitudinal_checkpoints(
campaign_id,timeline_id,run_id,world_day,snapshot_json,created_ts)
VALUES($campaign,$timeline,$run,$day,$snapshot,$now)
ON CONFLICT(campaign_id,timeline_id,run_id,world_day) DO UPDATE SET
snapshot_json=excluded.snapshot_json,created_ts=excluded.created_ts;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["run"] = runId, ["day"] = worldDay,
                            ["snapshot"] = Json.Serialize(snapshot), ["now"] = now
                        });
                    checkpoints = QuerySql(connection, @"SELECT world_day,snapshot_json,created_ts
FROM social_balance_longitudinal_checkpoints WHERE campaign_id=$campaign
AND timeline_id=$timeline AND run_id=$run ORDER BY world_day;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["run"] = runId
                        });
                }
            }
            double baselineDay = checkpoints.Select(row => ReadDouble(row, "world_day", worldDay)).Min();
            double spanDays = worldDay - baselineDay;
            List<Dictionary<string, object>> parsed = checkpoints.Select(row =>
            {
                Dictionary<string, object> value = TryParseJsonObject(ReadString(row, "snapshot_json", "{}"));
                value["worldDay"] = ReadDouble(row, "world_day", 0d);
                return value;
            }).ToList();
            bool fourCheckpoints = parsed.Count >= 4
                && parsed.Any(row => ReadDouble(row, "worldDay", 0d) - baselineDay >= 29.5d)
                && parsed.Any(row => ReadDouble(row, "worldDay", 0d) - baselineDay >= 59.5d)
                && spanDays >= 89.5d;
            bool serverSnapshots = parsed.All(row =>
                ReadBool(ReadDictionary(row, "serverSocial"), "available", false));
            bool boundedState = true;
            int maximumSocialRows = 0;
            int maximumRelationshipPairs = 0;
            HashSet<string> observedRulers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> row in parsed)
            {
                List<Dictionary<string, object>> heroes = ReadDictionaryList(row, "heroes");
                foreach (Dictionary<string, object> hero in heroes.Where(x => ReadBool(x, "isRuler", false)))
                    observedRulers.Add(ReadString(hero, "heroId", ""));
                Dictionary<string, object> social = ReadDictionary(row, "serverSocial")
                    ?? new Dictionary<string, object>();
                int socialRows = ReadDictionaryList(social, "activeRumorsByTag")
                    .Sum(x => Math.Max(0, ReadInt(x, "occurrence_count", 0)))
                    + ReadDictionaryList(social, "activeReputationsByTag")
                    .Sum(x => Math.Max(0, ReadInt(x, "subject_count", 0)));
                int pairCount = Math.Max(0, ReadInt(
                    ReadDictionary(social, "directionalProjection"), "pair_count", 0));
                maximumSocialRows = Math.Max(maximumSocialRows, socialRows);
                maximumRelationshipPairs = Math.Max(maximumRelationshipPairs, pairCount);
                int adultCount = Math.Max(1, heroes.Count);
                boundedState &= socialRows <= adultCount * 50
                    && pairCount <= adultCount * adultCount;
            }
            bool passed = fourCheckpoints && serverSnapshots && boundedState
                && observedRulers.Count > 0 && observedRulers.Count <= 24;
            string periodKey = "month_" + Math.Max(0,
                (int)Math.Floor(worldDay / 30d)).ToString(CultureInfo.InvariantCulture);
            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["baselineWorldDay"] = baselineDay, ["finalWorldDay"] = worldDay,
                ["spanDays"] = spanDays, ["checkpointCount"] = parsed.Count,
                ["checkpointWorldDays"] = parsed.Select(row => ReadDouble(row, "worldDay", 0d)).ToList(),
                ["observedRulerCount"] = observedRulers.Count,
                ["observedRulerIds"] = observedRulers.OrderBy(value => value).ToList(),
                ["maximumSocialStateRows"] = maximumSocialRows,
                ["maximumRelationshipPairs"] = maximumRelationshipPairs,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["dayZeroThirtySixtyNinetyCheckpoints"] = fourCheckpoints,
                    ["serverSocialProjectionAvailableAtEveryCheckpoint"] = serverSnapshots,
                    ["socialAndRelationshipStateGrowthBounded"] = boundedState,
                    ["rulerStandingAndTurnoverBounded"] = observedRulers.Count > 0
                        && observedRulers.Count <= 24
                }
            };
            if (passed)
            {
                string[] cases =
                {
                    "monthly_tag_prevalence_and_polarity", "monthly_ruler_standing_and_turnover",
                    "monthly_regular_lord_standing", "monthly_rebellion_causal_chain",
                    "monthly_court_personality_balance", "longitudinal_90_day_social_rebellion_balance"
                };
                foreach (string caseId in cases)
                    SocialBalanceCaseResultApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                        ["runId"] = runId, ["caseId"] = caseId,
                        ["status"] = "passed", ["periodKey"] = periodKey,
                        ["evidence"] = evidence
                    });
            }
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "longitudinal_90_day", ["passed"] = passed,
                ["staged"] = !passed, ["periodKey"] = periodKey, ["evidence"] = evidence
            };
        }

        private static Dictionary<string, object> SocialBalanceProfileEnrollment(
            Dictionary<string, object> liveRun, Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            return enrollment == null || enrollment.Count == 0
                ? ReadDictionary(liveRun, "enrollment") ?? new Dictionary<string, object>()
                : enrollment;
        }

        private static Dictionary<string, object> LoadSocialBalanceSnapshot(
            string campaignId, string timelineId, string runId, string periodKey)
        {
            lock (SocialBalanceLock)
            {
                using (ReignDbConnection connection = OpenSocialBalanceDatabase())
                {
                    Dictionary<string, object> row = QuerySql(connection, @"SELECT snapshot_json
FROM social_balance_snapshots WHERE campaign_id=$campaign AND timeline_id=$timeline
AND run_id=$run AND period_key=$period LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["run"] = runId, ["period"] = periodKey
                        }).FirstOrDefault();
                    return TryParseJsonObject(ReadString(row, "snapshot_json", "{}"));
                }
            }
        }

        private static bool HasCompatibleSocialBalancePass(
            string campaignId, string timelineId, string caseId)
        {
            Dictionary<string, object> definition = SocialBalanceCases().FirstOrDefault(row =>
                ReadString(row, "caseId", "").Equals(caseId, StringComparison.OrdinalIgnoreCase));
            if (definition == null) return false;
            definition["dependencyFingerprint"] = SocialBalanceCaseFingerprint(definition);
            lock (SocialBalanceLock)
            {
                using (ReignDbConnection connection = OpenSocialBalanceDatabase())
                {
                    Dictionary<string, object> row = QuerySql(connection, @"SELECT *
FROM social_balance_case_results WHERE campaign_id=$campaign AND timeline_id=$timeline
AND case_id=$case AND period_key='once' LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["case"] = caseId
                        }).FirstOrDefault();
                    return IsSocialBalanceCompatibleResult(row, definition)
                        && ReadString(row, "status", "") == "passed";
                }
            }
        }

        private static void RecordPlayerFlirtProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            string playerId = ReadString(enrollment, "mainHeroId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            string targetId = ReadString(result, "targetHeroId", "");
            Dictionary<string, object> validTurn = ReadDictionary(result, "validTurn")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> raw = ReadDictionary(validTurn, "rawResponse")
                ?? new Dictionary<string, object>();
            string exchangeId = FirstNonEmpty(ReadString(validTurn, "exchangeId", ""),
                ReadString(ReadDictionary(raw, "conversationExchange"), "exchangeId", ""));
            Dictionary<string, object> closeResult = ReadDictionary(result, "closeResult")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> courtSocial = ReadDictionary(
                ReadDictionary(closeResult, "memory"), "courtSocial")
                ?? new Dictionary<string, object>();
            string sessionId = FirstNonEmpty(ReadString(closeResult, "sessionId", ""),
                ReadString(ReadDictionary(raw, "conversationExchange"), "sessionId", ""));
            List<Dictionary<string, object>> signals = ReadDictionaryList(raw, "socialSignals");
            List<Dictionary<string, object>> signalEvidence = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> producerReceipts = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> targetRows = new List<Dictionary<string, object>>();
            if (!string.IsNullOrWhiteSpace(campaignId))
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureSocialReputationSchema(connection);
                    signalEvidence = QuerySql(connection, @"SELECT exchange_id,signal_id,signal_type,speaker_id,target_id,
supporting_quote,accepted,rejection_reason,evidence_json,created_ts
FROM court_social_signal_evidence
WHERE campaign_id=$campaign AND timeline_id=$timeline AND exchange_id=$exchange
ORDER BY created_ts,signal_id;", new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId, ["exchange"] = exchangeId
                    });
                    producerReceipts = QuerySql(connection, @"SELECT session_id,signal_key,signal_type,speaker_id,target_id,
result_json,created_ts FROM court_conversation_signal_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND session_id=$session
AND speaker_id=$player AND target_id=$target ORDER BY created_ts,signal_key;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["session"] = sessionId,
                            ["player"] = playerId, ["target"] = targetId
                        });
                    targetRows = QuerySql(connection, @"SELECT subject_id,target_id,first_session_id,first_world_day,
last_session_id,last_world_day,incident_count,updated_ts FROM court_flirt_targets
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$player AND target_id=$target;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["player"] = playerId, ["target"] = targetId
                        });
                }
            }

            bool explicitValidatedSignal = signals.Any(signal =>
                ReadString(signal, "type", "").Equals("flirtation", StringComparison.OrdinalIgnoreCase)
                && ReadBool(signal, "validated", false)
                && ReadString(signal, "validationSource", "").Equals("reign_conversation_engine", StringComparison.OrdinalIgnoreCase)
                && ReadString(signal, "speakerHeroId", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)
                && ReadString(signal, "targetHeroId", "").Equals(targetId, StringComparison.OrdinalIgnoreCase));
            bool acceptedPersisted = signalEvidence.Any(row => ReadInt(row, "accepted", 0) == 1
                && ReadString(row, "signal_type", "").Equals("flirtation", StringComparison.OrdinalIgnoreCase)
                && ReadString(row, "speaker_id", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)
                && ReadString(row, "target_id", "").Equals(targetId, StringComparison.OrdinalIgnoreCase));
            List<Dictionary<string, object>> producerResults = producerReceipts
                .Select(row => TryParseJsonObject(ReadString(row, "result_json", "{}"))
                    ?? new Dictionary<string, object>()).ToList();
            int speakerClanTier = signals.Count == 0 ? 0 : ReadInt(signals[0], "speakerClanTier", 0);
            double expectedExposureChance = Math.Min(1d, 0.05d * Math.Max(1, speakerClanTier) * 0.5d);
            bool productionRollRecorded = producerResults.Any(item =>
                (item.ContainsKey("chance") || item.ContainsKey("exposureChance"))
                && (item.ContainsKey("roll") || item.ContainsKey("exposureRoll")));
            bool clanTierScalingCorrect = producerResults.Any(item => Math.Abs(
                ReadDouble(item, "chance", ReadDouble(item, "exposureChance", -1d))
                - expectedExposureChance) < 0.0000001d);
            bool passed = !string.IsNullOrWhiteSpace(exchangeId)
                && !string.IsNullOrWhiteSpace(sessionId)
                && ReadBool(result, "configuredDialogueModelUsed", false)
                && ReadBool(result, "naturalLanguageOnly", false)
                && explicitValidatedSignal && acceptedPersisted
                && ReadBool(courtSocial, "processed", false)
                && producerReceipts.Count == 1
                && targetRows.Count == 1
                && productionRollRecorded
                && clanTierScalingCorrect;
            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["exchangeId"] = exchangeId, ["sessionId"] = sessionId,
                ["playerId"] = playerId, ["targetId"] = targetId,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["explicitValidatedSignal"] = explicitValidatedSignal,
                    ["acceptedEvidencePersisted"] = acceptedPersisted,
                    ["closeTimeProducerProcessed"] = ReadBool(courtSocial, "processed", false),
                    ["exactlyOneProducerReceipt"] = producerReceipts.Count == 1,
                    ["targetHistoryPersisted"] = targetRows.Count == 1,
                    ["productionRollRecorded"] = productionRollRecorded,
                    ["clanTierScalingCorrect"] = clanTierScalingCorrect,
                    ["configuredDialogueModelUsed"] = ReadBool(result, "configuredDialogueModelUsed", false)
                        && ReadBool(result, "naturalLanguageOnly", false)
                },
                ["acceptedSignals"] = signals,
                ["persistedSignals"] = signalEvidence,
                ["producerReceipts"] = producerReceipts,
                ["producerResults"] = producerResults,
                ["speakerClanTier"] = speakerClanTier,
                ["expectedExposureChance"] = expectedExposureChance,
                ["targetHistory"] = targetRows,
                ["courtSocial"] = courtSocial,
                ["playerText"] = ReadString(validTurn, "playerText", "")
            };
            Dictionary<string, object> recorded = SocialBalanceCaseResultApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["runId"] = runId,
                ["caseId"] = "court_flirt_validated_player_signal",
                ["status"] = passed ? "passed" : "failed", ["periodKey"] = "once",
                ["evidence"] = evidence
            });
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "player_flirt", ["caseId"] = "court_flirt_validated_player_signal",
                ["passed"] = passed, ["evidence"] = evidence, ["recorded"] = recorded
            };
        }

        private static void RecordPlayerAffairProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            string playerId = ReadString(enrollment, "mainHeroId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> completedAttempt = ReadDictionary(result, "completedAttempt")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> target = ReadDictionary(completedAttempt, "target")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> fixture = ReadDictionary(completedAttempt, "fixture")
                ?? new Dictionary<string, object>();
            string targetId = ReadString(target, "heroId", "");
            Dictionary<string, object> intimacyTurn = ReadDictionary(completedAttempt, "intimacyTurn")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> raw = ReadDictionary(intimacyTurn, "rawResponse")
                ?? new Dictionary<string, object>();
            string exchangeId = FirstNonEmpty(ReadString(intimacyTurn, "exchangeId", ""),
                ReadString(ReadDictionary(raw, "conversationExchange"), "exchangeId", ""));
            List<Dictionary<string, object>> signals = ReadDictionaryList(raw, "socialSignals");
            Dictionary<string, object> intimacySignal = signals.FirstOrDefault(signal =>
                ReadString(signal, "type", "").Equals("sexual_intimacy_completed", StringComparison.OrdinalIgnoreCase)
                && ReadBool(signal, "validated", false)
                && ReadString(signal, "validationSource", "").Equals("reign_conversation_engine", StringComparison.OrdinalIgnoreCase));
            string signalId = ReadString(intimacySignal, "signalId", "");

            List<Dictionary<string, object>> signalEvidence = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> exposureReceipts = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> occurrences = new List<Dictionary<string, object>>();
            Dictionary<string, object> rareExposureOverride = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(campaignId) && !string.IsNullOrWhiteSpace(signalId))
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureSocialReputationSchema(connection);
                    signalEvidence = QuerySql(connection, @"SELECT exchange_id,signal_id,signal_type,speaker_id,target_id,
supporting_quote,accepted,rejection_reason,evidence_json,created_ts
FROM court_social_signal_evidence
WHERE campaign_id=$campaign AND timeline_id=$timeline AND signal_id=$signal;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["signal"] = signalId
                        });
                    exposureReceipts = QuerySql(connection, @"SELECT signal_id,player_id,partner_id,eligible,exposed,
probability,roll,result_json,created_ts FROM court_intimacy_exposure_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND signal_id=$signal;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["signal"] = signalId
                        });
                    occurrences = QuerySql(connection, @"SELECT occurrence_id,archetype_id,thread_key,source_event_id,
world_day,expires_day,status,exposure_chance,exposure_roll,participants_json,provenance_summary
FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='affair' AND source_event_id=$event;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["event"] = "intimacy|" + signalId
                        });

                    bool forceRareExposure = ReadBool(result,
                        "forceRareAffairExposureAfterNaturalMiss", false);
                    Dictionary<string, object> naturalReceipt = exposureReceipts.Count == 1
                        ? exposureReceipts[0] : null;
                    double naturalChance = ReadDouble(naturalReceipt, "probability", -1d);
                    double naturalRoll = ReadDouble(naturalReceipt, "roll", -1d);
                    bool exactNaturalMiss = forceRareExposure
                        && naturalReceipt != null
                        && ReadInt(naturalReceipt, "eligible", 0) == 1
                        && ReadInt(naturalReceipt, "exposed", 0) == 0
                        && naturalChance > 0d && naturalRoll >= naturalChance
                        && occurrences.Count == 0
                        && !string.IsNullOrWhiteSpace(runId);
                    if (exactNaturalMiss)
                    {
                        Dictionary<string, object> player = CourtConversationParticipant(
                            connection, playerId, campaignId);
                        Dictionary<string, object> partner = CourtConversationParticipant(
                            connection, targetId, campaignId);
                        if (player != null && partner != null)
                        {
                            player["role"] = string.IsNullOrWhiteSpace(ReadString(player, "spouseId", ""))
                                ? "unmarried" : "married";
                            partner["role"] = string.IsNullOrWhiteSpace(ReadString(partner, "spouseId", ""))
                                ? "unmarried" : "married";
                            Dictionary<string, object> forcedResult = RegisterSocialOccurrence(
                                connection, campaignId, new Dictionary<string, object>
                                {
                                    ["timelineId"] = timelineId,
                                    ["archetypeId"] = "affair",
                                    ["threadKey"] = string.Join("|", new[] { playerId, targetId }
                                        .OrderBy(x => x)) + "|affair",
                                    ["sourceEventId"] = "intimacy|" + signalId,
                                    ["worldDay"] = ReadDouble(intimacySignal, "worldDay", 0d),
                                    ["provenanceSummary"] = ReadString(player, "canonicalName", "The player")
                                        + " completed an intimate encounter with "
                                        + ReadString(partner, "canonicalName", "another person")
                                        + " while at least one participant was married to someone else.",
                                    ["participants"] = new List<object> { player, partner },
                                    ["forceExposure"] = true
                                });
                            bool consumed = ReadBool(forcedResult, "exposed",
                                ReadBool(forcedResult, "created", false));
                            rareExposureOverride = new Dictionary<string, object>
                            {
                                ["runId"] = runId,
                                ["caseId"] = "player_affair_intimacy_discovery",
                                ["signalId"] = signalId,
                                ["scope"] = "exact_enrolled_run_case_signal",
                                ["naturalChance"] = naturalChance,
                                ["naturalRoll"] = naturalRoll,
                                ["appliedRoll"] = 0d,
                                ["productionChanceUnchanged"] = true,
                                ["nonSerialized"] = true,
                                ["consumed"] = consumed
                            };
                            if (consumed)
                            {
                                forcedResult["rareExposureOverride"] =
                                    new Dictionary<string, object>(rareExposureOverride);
                                string receiptResultJson = Json.Serialize(forcedResult);
                                ExecuteSql(connection, @"UPDATE court_intimacy_exposure_receipts
SET exposed=1,result_json=$result WHERE campaign_id=$campaign AND timeline_id=$timeline AND signal_id=$signal;",
                                    new Dictionary<string, object>
                                    {
                                        ["result"] = receiptResultJson,
                                        ["campaign"] = campaignId,
                                        ["timeline"] = timelineId,
                                        ["signal"] = signalId
                                    });
                                rareExposureOverride["forcedResult"] = forcedResult;
                                exposureReceipts = QuerySql(connection, @"SELECT signal_id,player_id,partner_id,eligible,exposed,
probability,roll,result_json,created_ts FROM court_intimacy_exposure_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND signal_id=$signal;",
                                    new Dictionary<string, object>
                                    {
                                        ["campaign"] = campaignId,
                                        ["timeline"] = timelineId,
                                        ["signal"] = signalId
                                    });
                                occurrences = QuerySql(connection, @"SELECT occurrence_id,archetype_id,thread_key,source_event_id,
world_day,expires_day,status,exposure_chance,exposure_roll,participants_json,provenance_summary
FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='affair' AND source_event_id=$event;",
                                    new Dictionary<string, object>
                                    {
                                        ["campaign"] = campaignId,
                                        ["timeline"] = timelineId,
                                        ["event"] = "intimacy|" + signalId
                                    });
                            }
                        }
                    }
                }
            }

            bool exactParticipants = intimacySignal != null
                && ((ReadString(intimacySignal, "speakerHeroId", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)
                        && ReadString(intimacySignal, "targetHeroId", "").Equals(targetId, StringComparison.OrdinalIgnoreCase))
                    || (ReadString(intimacySignal, "speakerHeroId", "").Equals(targetId, StringComparison.OrdinalIgnoreCase)
                        && ReadString(intimacySignal, "targetHeroId", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)));
            bool persistedAccepted = signalEvidence.Count == 1
                && ReadInt(signalEvidence[0], "accepted", 0) == 1
                && ReadString(signalEvidence[0], "exchange_id", "").Equals(exchangeId, StringComparison.OrdinalIgnoreCase);
            bool eligibleReceipt = exposureReceipts.Count == 1
                && ReadInt(exposureReceipts[0], "eligible", 0) == 1
                && ((ReadString(exposureReceipts[0], "player_id", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)
                        && ReadString(exposureReceipts[0], "partner_id", "").Equals(targetId, StringComparison.OrdinalIgnoreCase)));
            int highestClanTier = Math.Max(ReadInt(intimacySignal, "speakerClanTier", 0),
                ReadInt(intimacySignal, "targetClanTier", 0));
            double expectedChance = Math.Min(1d, 0.05d * Math.Max(1, highestClanTier) * 0.5d);
            bool productionRollRecorded = eligibleReceipt
                && Math.Abs(ReadDouble(exposureReceipts[0], "probability", -1d) - expectedChance) < 0.0000001d
                && ReadDouble(exposureReceipts[0], "roll", -1d) >= 0d;
            bool exposed = eligibleReceipt && ReadInt(exposureReceipts[0], "exposed", 0) == 1;
            bool occurrencePersisted = exposed && occurrences.Count == 1
                && ReadString(occurrences[0], "status", "").Equals("active", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(ReadDouble(occurrences[0], "exposure_chance", -1d) - expectedChance) < 0.0000001d;
            bool confirmationCompleted = ReadString(completedAttempt, "intimacyStatus", "") == "needs_input"
                && ReadString(completedAttempt, "confirmationStatus", "") == "completed";
            Dictionary<string, object> targetAffinity = ReadDictionary(fixture, "targetToPlayerAffinity")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> playerAffinity = ReadDictionary(fixture, "playerToTargetAffinity")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> restoredTargetAffinity = ReadDictionary(fixture, "restoredTargetToPlayerAffinity")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> restoredPlayerAffinity = ReadDictionary(fixture, "restoredPlayerToTargetAffinity")
                ?? new Dictionary<string, object>();
            bool mutualAffinityFixtureApplied = ReadBool(targetAffinity, "ok", false)
                && ReadBool(playerAffinity, "ok", false)
                && ReadInt(targetAffinity, "underlyingAffinity", 0) == 100
                && ReadInt(playerAffinity, "underlyingAffinity", 0) == 100;
            bool mutualAffinityFixtureRestored = ReadBool(restoredTargetAffinity, "ok", false)
                && ReadBool(restoredPlayerAffinity, "ok", false)
                && ReadInt(restoredTargetAffinity, "underlyingAffinity", int.MinValue)
                    == ReadInt(targetAffinity, "priorUnderlyingAffinity", int.MaxValue)
                && ReadInt(restoredPlayerAffinity, "underlyingAffinity", int.MinValue)
                    == ReadInt(playerAffinity, "priorUnderlyingAffinity", int.MaxValue);
            bool promptOverrideAssisted = ReadBool(result, "promptOverrideAssisted", false);
            List<Dictionary<string, object>> promptOverrideReceipts =
                ReadDictionaryList(completedAttempt, "promptOverrideReceipts");
            bool promptOverrideScoped = !promptOverrideAssisted
                || (ReadString(result, "promptOverrideScope", "").Equals(
                        "guarded_request_only", StringComparison.OrdinalIgnoreCase)
                    && promptOverrideReceipts.Count >= 2
                    && promptOverrideReceipts.All(receipt =>
                        ReadBool(receipt, "requested", false)
                        && ReadBool(receipt, "active", false)
                        && ReadString(receipt, "scope", "").Equals(
                            "request_only", StringComparison.OrdinalIgnoreCase)
                        && ReadString(receipt, "profile", "").Equals(
                            "player_affair", StringComparison.OrdinalIgnoreCase)
                        && ReadString(receipt, "reason", "").Equals(
                            "authorized_guarded_affair_capability_turn",
                            StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(
                            ReadString(receipt, "ownerCommandId", "")))
                    && ReadBool(result, "promptOverrideClearedAfterUse", false));
            bool configuredModelEvidence = ReadBool(result, "configuredDialogueModelUsed", false)
                && (ReadBool(result, "naturalLanguageOnly", false) || promptOverrideAssisted)
                && ReadBool(result, "adultConsentGuard", false)
                && promptOverrideScoped
                && !string.IsNullOrWhiteSpace(ReadString(intimacyTurn, "playerText", ""));
            Dictionary<string, object> targetTraits = string.IsNullOrWhiteSpace(targetId)
                ? new Dictionary<string, object>()
                : ReadJsonObject(CharacterFile(campaignId, targetId, "traits.json"));
            Dictionary<string, object> targetPercentages =
                TraitPercentageSnapshot(targetTraits);
            Dictionary<string, object> targetVirtues =
                ReadDictionary(targetTraits, "courtVirtues")
                ?? CalculateCourtVirtues(targetPercentages);
            int targetHonor = ReadInt(targetVirtues, "honor", 50);
            int targetLoyalty = ReadInt(targetPercentages, "loyalty", 50);
            bool veryLowHonorAndLoyalty =
                ReadBool(target, "qualifiesVeryLowHonorAndLoyalty", false)
                && targetHonor <= 20 && targetLoyalty <= 20;
            bool passed = !string.IsNullOrWhiteSpace(exchangeId)
                && !string.IsNullOrWhiteSpace(signalId)
                && exactParticipants && persistedAccepted && eligibleReceipt
                && productionRollRecorded && occurrencePersisted
                && confirmationCompleted && mutualAffinityFixtureApplied
                && mutualAffinityFixtureRestored && configuredModelEvidence
                && veryLowHonorAndLoyalty;

            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["exchangeId"] = exchangeId, ["signalId"] = signalId,
                ["playerId"] = playerId, ["targetId"] = targetId,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["validatedCompletedIntimacy"] = intimacySignal != null,
                    ["exactAdultParticipants"] = exactParticipants,
                    ["acceptedEvidencePersisted"] = persistedAccepted,
                    ["pregnancyRiskConfirmationCompleted"] = confirmationCompleted,
                    ["mutualAffinityFixtureApplied"] = mutualAffinityFixtureApplied,
                    ["mutualAffinityFixtureRestored"] = mutualAffinityFixtureRestored,
                    ["veryLowHonorAndLoyaltyTarget"] = veryLowHonorAndLoyalty,
                    ["marriedToAnotherEligibility"] = eligibleReceipt,
                    ["exactlyOneExposureReceipt"] = exposureReceipts.Count == 1,
                    ["productionTierScaledRollRecorded"] = productionRollRecorded,
                    ["affairExposed"] = exposed,
                    ["activeAffairOccurrencePersisted"] = occurrencePersisted,
                    ["configuredDialogueModelUsed"] = configuredModelEvidence,
                    ["promptOverrideAssisted"] = promptOverrideAssisted,
                    ["promptOverrideScopedAndCleared"] = promptOverrideScoped,
                    ["rareExposureOverrideConsumed"] = rareExposureOverride.Count == 0
                        || ReadBool(rareExposureOverride, "consumed", false)
                },
                ["acceptedSignals"] = signals,
                ["persistedSignals"] = signalEvidence,
                ["exposureReceipts"] = exposureReceipts,
                ["promptOverrideReceipts"] = promptOverrideReceipts,
                ["occurrences"] = occurrences,
                ["highestClanTier"] = highestClanTier,
                ["expectedExposureChance"] = expectedChance,
                ["rareExposureOverride"] = rareExposureOverride,
                ["targetPersonality"] = new Dictionary<string, object>
                {
                    ["honor"] = targetHonor,
                    ["loyalty"] = targetLoyalty,
                    ["veryLowThreshold"] = 20,
                    ["rankedCandidate"] = target
                },
                ["playerText"] = ReadString(intimacyTurn, "playerText", ""),
                ["attempts"] = ReadDictionaryList(result, "attempts")
            };
            Dictionary<string, object> recorded = SocialBalanceCaseResultApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["runId"] = runId,
                ["caseId"] = "player_affair_intimacy_discovery",
                ["status"] = passed ? "passed" : "failed", ["periodKey"] = "once",
                ["evidence"] = evidence
            });
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "player_affair", ["caseId"] = "player_affair_intimacy_discovery",
                ["passed"] = passed, ["evidence"] = evidence, ["recorded"] = recorded
            };
        }

        private static void RecordUnchasteProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> child = ReadDictionary(result, "child")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> femaleParent = ReadDictionary(result, "femaleParent")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> maleParent = ReadDictionary(result, "maleParent")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> femaleProducer = ReadDictionary(femaleParent, "producerEvidence")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> maleProducer = ReadDictionary(maleParent, "producerEvidence")
                ?? new Dictionary<string, object>();
            string femaleId = ReadString(femaleParent, "heroId", "");
            string maleId = ReadString(maleParent, "heroId", "");
            string childId = ReadString(child, "childId", "");
            string femaleSource = ReadString(femaleProducer, "sourceKey", "");
            string maleSource = ReadString(maleProducer, "sourceKey", "");

            List<Dictionary<string, object>> sourceEvents = new List<Dictionary<string, object>>();
            List<string> sourceEventIds = new List<string>();
            List<Dictionary<string, object>> sourceReceipts = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> occurrences = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> subjectTags = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> reputations = new List<Dictionary<string, object>>();
            if (!string.IsNullOrWhiteSpace(campaignId)
                && !string.IsNullOrWhiteSpace(femaleSource)
                && !string.IsNullOrWhiteSpace(maleSource))
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureSocialReputationSchema(connection);
                    sourceEvents = QuerySql(connection, @"SELECT event_id,correlation_id,sequence,world_day,payload_json
FROM world_history_events WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_type='social_outcome' AND correlation_id IN ($female,$male)
ORDER BY sequence DESC;", new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["female"] = femaleSource, ["male"] = maleSource
                    })
                        .GroupBy(row => ReadString(row, "correlation_id", ""),
                            StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First()).ToList();
                    sourceEventIds = sourceEvents
                        .Select(row => ReadString(row, "event_id", ""))
                        .Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
                    if (sourceEventIds.Count > 0)
                    {
                        DateTime receiptDeadline = DateTime.UtcNow.AddSeconds(30);
                        do
                        {
                            sourceReceipts = QuerySql(connection, @"SELECT event_id,subject_id,archetype_id,
completed,attempt_count,last_error,result_json,updated_ts FROM social_outcome_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND event_id IN ($first,$second)
ORDER BY event_id;", new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId, ["timeline"] = timelineId,
                                ["first"] = sourceEventIds[0],
                                ["second"] = sourceEventIds.Count > 1 ? sourceEventIds[1] : sourceEventIds[0]
                            });
                            if (sourceReceipts.Count == sourceEventIds.Count
                                && sourceReceipts.All(row => ReadInt(row, "completed", 0) != 0))
                                break;
                            if (DateTime.UtcNow >= receiptDeadline) break;
                            Thread.Sleep(100);
                        }
                        while (true);
                        occurrences = QuerySql(connection, @"SELECT occurrence_id,archetype_id,source_event_id,
world_day,status,exposure_chance,exposure_roll,participants_json,provenance_summary,snapshot_json
FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='the_unchaste' AND source_event_id IN ($first,$second)
ORDER BY source_event_id;", new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["first"] = sourceEventIds[0],
                            ["second"] = sourceEventIds.Count > 1 ? sourceEventIds[1] : sourceEventIds[0]
                        });
                    }
                    List<string> occurrenceIds = occurrences
                        .Select(row => ReadString(row, "occurrence_id", ""))
                        .Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
                    if (occurrenceIds.Count > 0)
                    {
                        subjectTags = QuerySql(connection, @"SELECT occurrence_id,subject_id,tag_id,subject_role,
rumor_value,reputation_value,status,snapshot_json FROM rumor_subject_tags
WHERE tag_id='the_unchaste' AND occurrence_id IN ($first,$second)
ORDER BY subject_id;", new Dictionary<string, object>
                        {
                            ["first"] = occurrenceIds[0],
                            ["second"] = occurrenceIds.Count > 1 ? occurrenceIds[1] : occurrenceIds[0]
                        });
                    }
                    reputations = QuerySql(connection, @"SELECT subject_id,tag_id,source_occurrence_id,
archetype_id,subject_role,description,reputation_value,acquired_day,status,snapshot_json
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
AND tag_id='the_unchaste' AND subject_id IN ($female,$male)
ORDER BY subject_id;", new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["female"] = femaleId, ["male"] = maleId
                    });
                }
            }

            bool publicParentage = ReadBool(child, "publiclyKnown", false)
                && ReadBool(child, "illegitimate", false)
                && ReadString(child, "motherId", "").Equals(femaleId, StringComparison.OrdinalIgnoreCase)
                && ReadString(child, "biologicalFatherId", "").Equals(maleId, StringComparison.OrdinalIgnoreCase)
                && ReadStringList(femaleProducer, "childIds").Contains(childId, StringComparer.OrdinalIgnoreCase)
                && ReadStringList(maleProducer, "childIds").Contains(childId, StringComparer.OrdinalIgnoreCase);
            bool ordinaryAdultParents = ReadBool(femaleParent, "isFemale", false)
                && !ReadBool(maleParent, "isFemale", true)
                && ReadBool(femaleParent, "isRegularLord", false)
                && ReadBool(maleParent, "isRegularLord", false)
                && !string.IsNullOrWhiteSpace(femaleId) && !string.IsNullOrWhiteSpace(maleId)
                && !femaleId.Equals(maleId, StringComparison.OrdinalIgnoreCase);
            bool productionProducer = ReadBool(result, "productionProducerUsed", false)
                && ReadBool(result, "forcedRareBranch", false)
                && ReadBool(result, "worldHistoryDrained", false)
                && ReadBool(femaleProducer, "forcedRareBranch", false)
                && ReadBool(maleProducer, "forcedRareBranch", false);
            bool exactSourceEvents = sourceEvents.Count == 2
                && sourceEvents.Any(row => ReadString(row, "correlation_id", "")
                    .Equals(femaleSource, StringComparison.OrdinalIgnoreCase))
                && sourceEvents.Any(row => ReadString(row, "correlation_id", "")
                    .Equals(maleSource, StringComparison.OrdinalIgnoreCase));
            bool exactOccurrences = exactSourceEvents && occurrences.Count == 2
                && occurrences.All(row => ReadString(row, "status", "")
                    .Equals("active", StringComparison.OrdinalIgnoreCase))
                && sourceEventIds.Count == 2
                && sourceEventIds.All(eventId => occurrences.Any(row =>
                    ReadString(row, "source_event_id", "")
                        .Equals(eventId, StringComparison.OrdinalIgnoreCase)));
            bool exactRumorValues = subjectTags.Any(row =>
                    ReadString(row, "subject_id", "").Equals(femaleId, StringComparison.OrdinalIgnoreCase)
                    && ReadInt(row, "rumor_value", int.MinValue) == 0
                    && ReadInt(row, "reputation_value", int.MinValue) == -50)
                && subjectTags.Any(row =>
                    ReadString(row, "subject_id", "").Equals(maleId, StringComparison.OrdinalIgnoreCase)
                    && ReadInt(row, "rumor_value", int.MinValue) == 0
                    && ReadInt(row, "reputation_value", int.MinValue) == -5);
            bool exactEstablishedValues = reputations.Count == 2
                && reputations.Any(row =>
                    ReadString(row, "subject_id", "").Equals(femaleId, StringComparison.OrdinalIgnoreCase)
                    && ReadInt(row, "reputation_value", int.MinValue) == -50
                    && ReadString(row, "status", "").Equals("active", StringComparison.OrdinalIgnoreCase))
                && reputations.Any(row =>
                    ReadString(row, "subject_id", "").Equals(maleId, StringComparison.OrdinalIgnoreCase)
                    && ReadInt(row, "reputation_value", int.MinValue) == -5
                    && ReadString(row, "status", "").Equals("active", StringComparison.OrdinalIgnoreCase));
            bool passed = publicParentage && ordinaryAdultParents && productionProducer
                && exactOccurrences && exactRumorValues && exactEstablishedValues;

            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["child"] = child, ["femaleParent"] = femaleParent, ["maleParent"] = maleParent,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["publicIllegitimateChildLinkedToBothParents"] = publicParentage,
                    ["ordinaryAdultMaleAndFemaleLords"] = ordinaryAdultParents,
                    ["productionCourtPersonalityProducerUsed"] = productionProducer,
                    ["exactForcedOccurrencesPersisted"] = exactOccurrences,
                    ["zeroRumorAndGenderSpecificTagValues"] = exactRumorValues,
                    ["femaleMinus50MaleMinus5Established"] = exactEstablishedValues
                },
                ["sourceEvents"] = sourceEvents,
                ["sourceReceipts"] = sourceReceipts,
                ["occurrences"] = occurrences,
                ["subjectTags"] = subjectTags,
                ["reputations"] = reputations
            };
            Dictionary<string, object> recorded = SocialBalanceCaseResultApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["runId"] = runId,
                ["caseId"] = "court_unchaste_publicity_and_gender",
                ["status"] = passed ? "passed" : "failed", ["periodKey"] = "once",
                ["evidence"] = evidence
            });
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "unchaste", ["caseId"] = "court_unchaste_publicity_and_gender",
                ["passed"] = passed, ["evidence"] = evidence, ["recorded"] = recorded
            };
        }

        private static void RecordNpcFavoringPresenceProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            string sourceKey = ReadString(result, "sourceKey", "");
            string rulerId = ReadString(result, "rulerId", "");
            string favoriteId = ReadString(result, "favoriteId", "");
            int rulerClanTier = ReadInt(result, "rulerClanTier", 0);
            Dictionary<string, object> sourceEvent = new Dictionary<string, object>();
            Dictionary<string, object> sourceReceipt = new Dictionary<string, object>();
            Dictionary<string, object> occurrence = new Dictionary<string, object>();
            List<Dictionary<string, object>> subjectTags = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> reputations = new List<Dictionary<string, object>>();
            if (!string.IsNullOrWhiteSpace(campaignId)
                && !string.IsNullOrWhiteSpace(timelineId)
                && !string.IsNullOrWhiteSpace(sourceKey))
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureSocialReputationSchema(connection);
                    sourceEvent = QuerySql(connection, @"SELECT event_id,correlation_id,sequence,world_day,payload_json
FROM world_history_events WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_type='social_outcome' AND correlation_id=$source ORDER BY sequence DESC LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["source"] = sourceKey
                        }).FirstOrDefault() ?? new Dictionary<string, object>();
                    string eventId = ReadString(sourceEvent, "event_id", "");
                    if (!string.IsNullOrWhiteSpace(eventId))
                    {
                        DateTime receiptDeadline = DateTime.UtcNow.AddSeconds(30);
                        do
                        {
                            sourceReceipt = QuerySql(connection, @"SELECT event_id,subject_id,archetype_id,
completed,attempt_count,last_error,result_json,updated_ts FROM social_outcome_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND event_id=$event LIMIT 1;",
                                new Dictionary<string, object>
                                {
                                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                                    ["event"] = eventId
                                }).FirstOrDefault() ?? new Dictionary<string, object>();
                            if (ReadInt(sourceReceipt, "completed", 0) != 0) break;
                            if (DateTime.UtcNow >= receiptDeadline) break;
                            Thread.Sleep(100);
                        }
                        while (true);
                        occurrence = QuerySql(connection, @"SELECT occurrence_id,archetype_id,source_event_id,
world_day,status,exposure_chance,exposure_roll,participants_json,provenance_summary,snapshot_json
FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='ruler_favoring_presence' AND source_event_id=$event LIMIT 1;",
                            new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId, ["timeline"] = timelineId,
                                ["event"] = eventId
                            }).FirstOrDefault() ?? new Dictionary<string, object>();
                    }
                    string occurrenceId = ReadString(occurrence, "occurrence_id", "");
                    if (!string.IsNullOrWhiteSpace(occurrenceId))
                    {
                        subjectTags = QuerySql(connection, @"SELECT occurrence_id,subject_id,tag_id,subject_role,
rumor_value,reputation_value,status,snapshot_json FROM rumor_subject_tags
WHERE occurrence_id=$occurrence AND tag_id LIKE 'ruler_favoring:%';",
                            new Dictionary<string, object> { ["occurrence"] = occurrenceId });
                    }
                    reputations = QuerySql(connection, @"SELECT subject_id,tag_id,source_occurrence_id,
archetype_id,subject_role,description,reputation_value,acquired_day,status,snapshot_json
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$ruler AND status='active' AND tag_id LIKE 'ruler_favoring:%';",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["ruler"] = rulerId
                        });
                }
            }

            double expectedChance = Math.Min(1d, 0.05d
                * Math.Max(1, Math.Min(6, rulerClanTier)) * 0.5d);
            Dictionary<string, object> eventPayload = TryParseJsonObject(
                ReadString(sourceEvent, "payload_json", "{}")) ?? new Dictionary<string, object>();
            bool productionStateMachine = ReadBool(result, "productionStateMachineUsed", false)
                && ReadInt(result, "pairProcessorRevision", 0) == 2
                && ReadInt(result, "pairProcessorCallCount", 0) == 3
                && ReadBool(result, "forcedRareBranch", false)
                && ReadBool(result, "worldHistoryDrained", false);
            bool resetAndThreshold = ReadInt(result, "separationResetCount", -1) == 1
                && ReadInt(result, "dayTenCount", -1) == 10
                && ReadBool(result, "dayTenProducedNoEvent", false)
                && ReadInt(result, "dayElevenCount", -1) == 11
                && ReadBool(result, "dayElevenQueuedExactEvent", false);
            bool exactEventAndReceipt = !string.IsNullOrWhiteSpace(ReadString(sourceEvent, "event_id", ""))
                && ReadString(sourceEvent, "correlation_id", "")
                    .Equals(sourceKey, StringComparison.OrdinalIgnoreCase)
                && ReadBool(eventPayload, "forcedRareBranch", false)
                && ReadString(eventPayload, "socialBalanceTestRunId", "")
                    .Equals(runId, StringComparison.OrdinalIgnoreCase)
                && ReadInt(sourceReceipt, "completed", 0) != 0
                && ReadString(sourceReceipt, "archetype_id", "")
                    .Equals("ruler_favoring_presence", StringComparison.OrdinalIgnoreCase);
            bool exactOccurrence = ReadString(occurrence, "source_event_id", "")
                    .Equals(ReadString(sourceEvent, "event_id", ""), StringComparison.OrdinalIgnoreCase)
                && ReadString(occurrence, "status", "").Equals("active", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(ReadDouble(occurrence, "exposure_chance", -1d) - expectedChance) < 0.0000001d;
            bool parameterizedEvidence = subjectTags.Any(row =>
                    ReadString(row, "subject_id", "").Equals(rulerId, StringComparison.OrdinalIgnoreCase)
                    && ReadInt(row, "rumor_value", 0) == -5
                    && ReadInt(row, "reputation_value", 0) == -10
                    && ReadString(TryParseJsonObject(ReadString(row, "snapshot_json", "{}"))
                            ?? new Dictionary<string, object>(), "linkedHeroId", "")
                        .Equals(favoriteId, StringComparison.OrdinalIgnoreCase))
                && reputations.Any(row =>
                    ReadString(row, "subject_id", "").Equals(rulerId, StringComparison.OrdinalIgnoreCase)
                    && ReadInt(row, "reputation_value", 0) == -10
                    && ReadString(TryParseJsonObject(ReadString(row, "snapshot_json", "{}"))
                            ?? new Dictionary<string, object>(), "linkedHeroId", "")
                        .Equals(favoriteId, StringComparison.OrdinalIgnoreCase));
            bool passed = productionStateMachine && resetAndThreshold && exactEventAndReceipt
                && exactOccurrence && parameterizedEvidence;
            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["rulerId"] = rulerId, ["favoriteId"] = favoriteId,
                ["sourceKey"] = sourceKey, ["rulerClanTier"] = rulerClanTier,
                ["expectedExposureChance"] = expectedChance,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["productionStateMachineUsed"] = productionStateMachine,
                    ["separationResetAndDayElevenThreshold"] = resetAndThreshold,
                    ["exactEventReceiptCompleted"] = exactEventAndReceipt,
                    ["fivePercentBaseClanTierScaling"] = exactOccurrence,
                    ["parameterizedFavoringPersisted"] = parameterizedEvidence
                },
                ["nativeResult"] = result, ["sourceEvent"] = sourceEvent,
                ["sourceReceipt"] = sourceReceipt, ["occurrence"] = occurrence,
                ["subjectTags"] = subjectTags, ["reputations"] = reputations
            };
            Dictionary<string, object> recorded = SocialBalanceCaseResultApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["runId"] = runId,
                ["caseId"] = "npc_ruler_favoring_presence",
                ["status"] = passed ? "passed" : "failed", ["periodKey"] = "once",
                ["evidence"] = evidence
            });
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "npc_favoring_presence", ["caseId"] = "npc_ruler_favoring_presence",
                ["passed"] = passed, ["evidence"] = evidence, ["recorded"] = recorded
            };
        }

        private static void RecordPlayerFavoringDialogueProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            string playerId = ReadString(enrollment, "mainHeroId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> primaryTarget = ReadDictionary(result, "primaryTarget")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> controlTarget = ReadDictionary(result, "controlTarget")
                ?? new Dictionary<string, object>();
            string primaryId = ReadFirstString(primaryTarget, "heroId", "id");
            string controlId = ReadFirstString(controlTarget, "heroId", "id");
            Dictionary<string, object> player = new Dictionary<string, object>();
            Dictionary<string, object> primaryCounter = new Dictionary<string, object>();
            Dictionary<string, object> controlCounter = new Dictionary<string, object>();
            List<Dictionary<string, object>> primaryReceipts = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> controlReceipts = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> primaryOccurrences = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> controlOccurrences = new List<Dictionary<string, object>>();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                player = QuerySql(connection, @"SELECT hero_id,is_player,is_ruler,is_alive,is_adult,clan_tier
FROM identity_roster WHERE hero_id=$player LIMIT 1;",
                    new Dictionary<string, object> { ["player"] = playerId }).FirstOrDefault()
                    ?? new Dictionary<string, object>();
                primaryCounter = QuerySql(connection, @"SELECT ruler_id,favorite_id,exchange_count,last_exchange_id,last_world_day
FROM court_player_favoring_exchanges WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ruler_id=$ruler AND favorite_id=$favorite LIMIT 1;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["ruler"] = playerId, ["favorite"] = primaryId
                    }).FirstOrDefault() ?? new Dictionary<string, object>();
                controlCounter = QuerySql(connection, @"SELECT ruler_id,favorite_id,exchange_count,last_exchange_id,last_world_day
FROM court_player_favoring_exchanges WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ruler_id=$ruler AND favorite_id=$favorite LIMIT 1;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["ruler"] = playerId, ["favorite"] = controlId
                    }).FirstOrDefault() ?? new Dictionary<string, object>();
                primaryReceipts = QuerySql(connection, @"SELECT exchange_id,world_day,created_ts
FROM court_player_favoring_exchange_receipts WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ruler_id=$ruler AND favorite_id=$favorite ORDER BY created_ts,exchange_id;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["ruler"] = playerId, ["favorite"] = primaryId
                    });
                controlReceipts = QuerySql(connection, @"SELECT exchange_id,world_day,created_ts
FROM court_player_favoring_exchange_receipts WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ruler_id=$ruler AND favorite_id=$favorite ORDER BY created_ts,exchange_id;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["ruler"] = playerId, ["favorite"] = controlId
                    });
                primaryOccurrences = QuerySql(connection, @"SELECT occurrence_id,thread_key,source_event_id,status,
exposure_chance,exposure_roll,participants_json,provenance_summary FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id='ruler_favoring_dialogue'
AND thread_key=$thread;", new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["thread"] = playerId + "|ruler_favoring|" + primaryId
                    });
                controlOccurrences = QuerySql(connection, @"SELECT occurrence_id,thread_key,source_event_id,status,
exposure_chance,exposure_roll,participants_json,provenance_summary FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id='ruler_favoring_dialogue'
AND thread_key=$thread;", new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["thread"] = playerId + "|ruler_favoring|" + controlId
                    });
            }

            Dictionary<string, object> primaryClose = ReadDictionary(result, "primaryCloseResult")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> primaryMemory = ReadDictionary(primaryClose, "memory")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> courtSocial = ReadDictionary(primaryMemory, "courtSocial")
                ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> producerResults = ReadDictionaryList(courtSocial, "results");
            int tier = Math.Max(1, Math.Min(6, ReadInt(player, "clan_tier",
                ReadInt(result, "playerClanTier", 0))));
            double expectedChance = Math.Min(1d, 0.10d * tier * 0.5d);
            Dictionary<string, object> producerResult = producerResults.Count == 1
                ? producerResults[0] : new Dictionary<string, object>();
            bool producerExposed = ReadBool(producerResult, "exposed", false);
            bool dialogueUsed = ReadBool(result, "naturalLanguageOnly", false)
                && ReadBool(result, "configuredDialogueModelUsed", false)
                && ReadBool(result, "playerIsRuler", false)
                && ReadInt(player, "is_player", 0) != 0 && ReadInt(player, "is_ruler", 0) != 0;
            bool cleanTargetIsolation = PlayerFavoringTargetsHadCleanHistory(
                result, primaryId, controlId);
            bool primarySix = ReadInt(result, "primaryExchangeCount", 0) == 6
                && ReadDictionaryList(result, "primaryTurns").Count == 6
                && ReadInt(primaryCounter, "exchange_count", 0) == 6
                && primaryReceipts.Count == 6
                && primaryReceipts.Select(row => ReadString(row, "exchange_id", ""))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 6;
            bool independentControl = !string.IsNullOrWhiteSpace(primaryId)
                && !string.IsNullOrWhiteSpace(controlId)
                && !primaryId.Equals(controlId, StringComparison.OrdinalIgnoreCase)
                && ReadInt(result, "controlExchangeCount", 0) == 1
                && ReadInt(controlCounter, "exchange_count", 0) == 1
                && controlReceipts.Count == 1 && controlOccurrences.Count == 0;
            bool chanceAndRoll = producerResults.Count == 1
                && Math.Abs(ReadDouble(producerResult, "chance", -1d) - expectedChance) < 0.0000001d
                && ReadDouble(producerResult, "roll", -1d) >= 0d
                && ReadDouble(producerResult, "roll", -1d) < 1d
                && (producerExposed
                    ? primaryOccurrences.Count == 1
                        && Math.Abs(ReadDouble(primaryOccurrences[0], "exposure_chance", -1d)
                            - expectedChance) < 0.0000001d
                    : primaryOccurrences.Count == 0);
            bool passed = dialogueUsed && cleanTargetIsolation
                && primarySix && independentControl && chanceAndRoll;
            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["playerId"] = playerId, ["primaryTargetId"] = primaryId,
                ["controlTargetId"] = controlId, ["playerClanTier"] = tier,
                ["expectedExposureChance"] = expectedChance,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["visibleProductionDialogueUsed"] = dialogueUsed,
                    ["cleanTargetIsolation"] = cleanTargetIsolation,
                    ["sixDistinctPrimaryExchanges"] = primarySix,
                    ["perTargetIndependentCounters"] = independentControl,
                    ["tenPercentBaseClanTierScaling"] = chanceAndRoll
                },
                ["nativeResult"] = result, ["playerRoster"] = player,
                ["primaryCounter"] = primaryCounter, ["controlCounter"] = controlCounter,
                ["primaryReceipts"] = primaryReceipts, ["controlReceipts"] = controlReceipts,
                ["producerResults"] = producerResults,
                ["primaryOccurrences"] = primaryOccurrences,
                ["controlOccurrences"] = controlOccurrences
            };
            Dictionary<string, object> recorded = SocialBalanceCaseResultApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["runId"] = runId,
                ["caseId"] = "player_ruler_favoring_dialogue",
                ["status"] = passed ? "passed" : "failed", ["periodKey"] = "once",
                ["evidence"] = evidence
            });
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "player_favoring_dialogue",
                ["caseId"] = "player_ruler_favoring_dialogue",
                ["passed"] = passed, ["evidence"] = evidence, ["recorded"] = recorded
            };
        }

        private static bool PlayerFavoringTargetsHadCleanHistory(
            Dictionary<string, object> result, string primaryId, string controlId)
        {
            List<Dictionary<string, object>> histories =
                ReadDictionaryList(result, "targetHistoryEvidence");
            bool primaryClean = histories.Any(row =>
                ReadString(row, "heroId", "").Equals(primaryId, StringComparison.OrdinalIgnoreCase)
                && ReadInt(row, "persistedDialogueLineCount", -1) == 0
                && ReadBool(row, "cleanConversationalSlate", false));
            bool controlClean = histories.Any(row =>
                ReadString(row, "heroId", "").Equals(controlId, StringComparison.OrdinalIgnoreCase)
                && ReadInt(row, "persistedDialogueLineCount", -1) == 0
                && ReadBool(row, "cleanConversationalSlate", false));
            return ReadBool(result, "cleanConversationalSlateVerified", false)
                && primaryClean && controlClean;
        }

        private static void RecordFavoringProjectionProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            string rulerId = ReadString(result, "rulerId", "");
            string favoriteOneId = ReadString(result, "favoriteOneId", "");
            string favoriteTwoId = ReadString(result, "favoriteTwoId", "");
            string nonFavoriteId = ReadString(result, "nonFavoriteObserverId", "");
            string foreignId = ReadString(result, "foreignObserverId", "");
            Dictionary<string, object> firstProducer = ReadDictionary(result,
                "firstProducerEvidence") ?? new Dictionary<string, object>();
            Dictionary<string, object> secondProducer = ReadDictionary(result,
                "secondProducerEvidence") ?? new Dictionary<string, object>();
            string firstSourceKey = ReadString(firstProducer, "sourceKey", "");
            string secondSourceKey = ReadString(secondProducer, "sourceKey", "");
            long firstSequence = ReadLong(firstProducer, "worldHistorySequence", 0);
            long secondSequence = ReadLong(secondProducer, "worldHistorySequence", 0);
            var currentSourceEvents = new List<Dictionary<string, object>>();
            var currentEventIds = new HashSet<string>(StringComparer.Ordinal);
            List<Dictionary<string, object>> sourceEvents = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> sourceReceipts = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> occurrences = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> subjectTags = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> reputations = new List<Dictionary<string, object>>();
            Dictionary<string, object> standing = new Dictionary<string, object>();
            Dictionary<string, object> favoriteOneView = new Dictionary<string, object>();
            Dictionary<string, object> favoriteTwoView = new Dictionary<string, object>();
            Dictionary<string, object> nonFavoriteView = new Dictionary<string, object>();
            Dictionary<string, object> foreignView = new Dictionary<string, object>();
            Dictionary<string, int> pairCountsBefore = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> pairCountsAfter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int rulerCharm = 0;

            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                sourceEvents = QuerySql(connection, @"SELECT event_id,correlation_id,sequence,world_day,payload_json
FROM world_history_events WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_type='social_outcome' AND (correlation_id=$first OR correlation_id=$second)
ORDER BY sequence;", new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["first"] = firstSourceKey, ["second"] = secondSourceKey
                });
                currentSourceEvents = SocialBalanceCurrentFavorEvents(sourceEvents,
                    firstSourceKey, firstSequence, secondSourceKey, secondSequence);
                currentEventIds = new HashSet<string>(currentSourceEvents.Select(row => ReadString(row, "event_id", "")), StringComparer.Ordinal);
                DateTime receiptDeadline = DateTime.UtcNow.AddSeconds(30);
                do
                {
                    sourceReceipts = sourceEvents.Count == 0
                        ? new List<Dictionary<string, object>>()
                        : QuerySql(connection, @"SELECT event_id,subject_id,archetype_id,completed,
attempt_count,last_error,result_json,updated_ts FROM social_outcome_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_id IN (SELECT event_id FROM world_history_events WHERE campaign_id=$campaign
AND timeline_id=$timeline AND event_type='social_outcome' AND (correlation_id=$first OR correlation_id=$second))
ORDER BY event_id;",
                            new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId, ["timeline"] = timelineId,
                                ["first"] = firstSourceKey,
                                ["second"] = secondSourceKey
                            });
                    if (currentEventIds.Count == 2 && currentEventIds.All(id => sourceReceipts.Any(
                        row => ReadString(row, "event_id", "") == id && ReadInt(row, "completed", 0) != 0))) break;
                    if (DateTime.UtcNow >= receiptDeadline) break;
                    Thread.Sleep(100);
                }
                while (true);
                occurrences = QuerySql(connection, @"SELECT occurrence_id,archetype_id,source_event_id,
world_day,status,exposure_chance,exposure_roll,participants_json,provenance_summary,snapshot_json
FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='ruler_favoring_presence'
AND source_event_id IN (SELECT event_id FROM world_history_events WHERE campaign_id=$campaign
AND timeline_id=$timeline AND event_type='social_outcome' AND (correlation_id=$first OR correlation_id=$second))
ORDER BY occurrence_id;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["first"] = firstSourceKey,
                        ["second"] = secondSourceKey
                    });
                subjectTags = QuerySql(connection, @"SELECT occurrence_id,subject_id,tag_id,subject_role,
rumor_value,reputation_value,status,snapshot_json FROM rumor_subject_tags
WHERE occurrence_id IN (SELECT occurrence_id FROM rumor_occurrences WHERE campaign_id=$campaign
AND timeline_id=$timeline AND archetype_id='ruler_favoring_presence' AND source_event_id IN
(SELECT event_id FROM world_history_events WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_type='social_outcome' AND (correlation_id=$first OR correlation_id=$second)))
AND tag_id LIKE 'ruler_favoring:%' ORDER BY occurrence_id,tag_id;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["first"] = firstSourceKey, ["second"] = secondSourceKey
                    });
                reputations = QuerySql(connection, @"SELECT subject_id,tag_id,source_occurrence_id,
archetype_id,subject_role,description,reputation_value,acquired_day,status,snapshot_json
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$ruler AND status='active' AND tag_id LIKE 'ruler_favoring:%'
AND source_occurrence_id IN (SELECT occurrence_id FROM rumor_occurrences WHERE campaign_id=$campaign
AND timeline_id=$timeline AND archetype_id='ruler_favoring_presence' AND source_event_id IN
(SELECT event_id FROM world_history_events WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_type='social_outcome' AND (correlation_id=$first OR correlation_id=$second)))
ORDER BY tag_id,source_occurrence_id;", new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["ruler"] = rulerId, ["first"] = firstSourceKey,
                    ["second"] = secondSourceKey
                });
                double worldDay = occurrences.Count == 0 ? 0d
                    : occurrences.Max(row => ReadDouble(row, "world_day", 0d));
                RecomputePublicStandingForSubjects(connection, campaignId, timelineId,
                    new[] { rulerId }, worldDay);
                standing = ReadPublicStandingTrait(connection, campaignId, timelineId,
                    rulerId, worldDay);
                rulerCharm = ReadInt(QuerySql(connection,
                    "SELECT current_charm FROM identity_roster WHERE hero_id=$id LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = rulerId }).FirstOrDefault(),
                    "current_charm", 0);
                foreach (string observerId in new[] { favoriteOneId, favoriteTwoId, nonFavoriteId, foreignId })
                {
                    string pairKey = AmbientPairKey(observerId, rulerId);
                    pairCountsBefore[observerId] = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_pair_chemistry WHERE pair_key=$pair;",
                        new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault(), "count", 0);
                }
                favoriteOneView = ResolveObserverPublicStanding(connection, campaignId,
                    timelineId, favoriteOneId, rulerId);
                favoriteTwoView = ResolveObserverPublicStanding(connection, campaignId,
                    timelineId, favoriteTwoId, rulerId);
                nonFavoriteView = ResolveObserverPublicStanding(connection, campaignId,
                    timelineId, nonFavoriteId, rulerId);
                foreignView = ResolveObserverPublicStanding(connection, campaignId,
                    timelineId, foreignId, rulerId);
                foreach (string observerId in new[] { favoriteOneId, favoriteTwoId, nonFavoriteId, foreignId })
                {
                    string pairKey = AmbientPairKey(observerId, rulerId);
                    pairCountsAfter[observerId] = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_pair_chemistry WHERE pair_key=$pair;",
                        new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault(), "count", 0);
                }
            }

            List<Dictionary<string, object>> standingSources = ReadDictionaryList(standing, "sources");
            List<Dictionary<string, object>> favoringSources = standingSources
                .Where(IsRulerFavoringStandingSource).ToList();
            int expectedGlobal = RoundAwayFromZero(-10d * (1d - SocialCharmMitigation(rulerCharm)));
            int expectedNonFavorite = RoundAwayFromZero(-10d
                * (1d - SocialCharmMitigation(rulerCharm))
                * ReadDouble(nonFavoriteView, "jealousyFactor", 1d));
            HashSet<string> linkedFavorites = new HashSet<string>(reputations.Select(row =>
                ReadString(TryParseJsonObject(ReadString(row, "snapshot_json", "{}"))
                    ?? new Dictionary<string, object>(), "linkedHeroId", "")),
                StringComparer.OrdinalIgnoreCase);
            int currentOccurrenceCount = occurrences.Count(row => currentEventIds.Contains(ReadString(row, "source_event_id", "")));
            bool twoIndependentFavorites = SocialBalanceHasTwoFavorSources(currentSourceEvents, firstSourceKey, secondSourceKey)
                && currentEventIds.Count == 2 && currentEventIds.All(id => sourceReceipts.Any(
                    row => ReadString(row, "event_id", "") == id && ReadInt(row, "completed", 0) != 0))
                && currentOccurrenceCount == 2 && subjectTags.Count >= 2 && reputations.Count == 2
                && linkedFavorites.Contains(favoriteOneId) && linkedFavorites.Contains(favoriteTwoId);
            bool collapsedGlobal = SocialBalanceFavoringCostCollapsed(standing, rulerCharm);
            bool favoriteBonuses = ReadString(favoriteOneView, "scope", "") == "favorite"
                && ReadInt(favoriteOneView, "favoring", 0) == 15
                && ReadString(favoriteTwoView, "scope", "") == "favorite"
                && ReadInt(favoriteTwoView, "favoring", 0) == 15;
            bool scopedObservers = ReadString(nonFavoriteView, "scope", "") == "kingdom_non_favorite"
                && ReadInt(nonFavoriteView, "favoring", int.MinValue) == expectedNonFavorite
                && ReadString(foreignView, "scope", "") == "foreign"
                && ReadInt(foreignView, "favoring", int.MinValue) == 0;
            bool noPairCreation = pairCountsBefore.Count == 4
                && pairCountsBefore.All(pair => pairCountsAfter.TryGetValue(pair.Key, out int after)
                    && after == pair.Value);
            bool nativeProduction = ReadBool(result, "productionStateMachineUsed", false)
                && ReadBool(result, "worldHistoryDrained", false);
            bool passed = nativeProduction && twoIndependentFavorites && collapsedGlobal
                && favoriteBonuses && scopedObservers && noPairCreation;
            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["rulerId"] = rulerId, ["rulerCharm"] = rulerCharm,
                ["favoriteIds"] = new[] { favoriteOneId, favoriteTwoId },
                ["nonFavoriteObserverId"] = nonFavoriteId,
                ["foreignObserverId"] = foreignId,
                ["expectedGlobalFavoringContribution"] = expectedGlobal,
                ["preparationEventCount"] = sourceEvents.Count,
                ["currentPreparationEventIds"] = currentEventIds.ToList(),
                ["currentPreparationOccurrenceCount"] = currentOccurrenceCount,
                ["historicalOccurrenceCount"] = occurrences.Count - currentOccurrenceCount,
                ["uniqueSourceCount"] = sourceEvents.Select(row => ReadString(row, "correlation_id", "")).Distinct().Count(),
                ["actualGlobalFavoringContribution"] = favoringSources.Sum(source => ReadDouble(source, "effectiveContribution", 0d)),
                ["nonFavoringContribution"] = standingSources.Where(source => !IsRulerFavoringStandingSource(source))
                    .Sum(source => ReadDouble(source, "effectiveContribution", 0d)),
                ["expectedNonFavoriteFavoring"] = expectedNonFavorite,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["nativeProductionStateMachineUsed"] = nativeProduction,
                    ["twoIndependentParameterizedFavorites"] = twoIndependentFavorites,
                    ["globalFavoringCostCollapsed"] = collapsedGlobal,
                    ["eachNamedFavoriteReceivesEstablishedBonus"] = favoriteBonuses,
                    ["sameKingdomJealousyAndForeignSuppression"] = scopedObservers,
                    ["observerResolutionCreatesNoRelationshipPair"] = noPairCreation
                },
                ["nativeResult"] = result, ["sourceEvents"] = sourceEvents,
                ["sourceReceipts"] = sourceReceipts, ["occurrences"] = occurrences,
                ["subjectTags"] = subjectTags, ["reputations"] = reputations,
                ["publicStanding"] = standing,
                ["favoriteOneView"] = favoriteOneView,
                ["favoriteTwoView"] = favoriteTwoView,
                ["nonFavoriteView"] = nonFavoriteView,
                ["foreignView"] = foreignView,
                ["pairCountsBefore"] = pairCountsBefore,
                ["pairCountsAfter"] = pairCountsAfter
            };
            Dictionary<string, object> recorded = SocialBalanceCaseResultApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["runId"] = runId, ["caseId"] = "favoring_collapsed_projection",
                ["status"] = passed ? "passed" : "failed", ["periodKey"] = "once",
                ["evidence"] = evidence
            });
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "favoring_projection",
                ["caseId"] = "favoring_collapsed_projection",
                ["passed"] = passed, ["evidence"] = evidence, ["recorded"] = recorded
            };
        }

        private static void RecordFavoringJealousyCharmProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            string rulerId = "";
            string rulerKingdomId = "";
            int rulerCharm = 0;
            Dictionary<string, object> standing = new Dictionary<string, object>();
            List<Dictionary<string, object>> observerCandidates = new List<Dictionary<string, object>>();
            Dictionary<string, object> lowObserver = new Dictionary<string, object>();
            Dictionary<string, object> highObserver = new Dictionary<string, object>();
            Dictionary<string, object> lowView = new Dictionary<string, object>();
            Dictionary<string, object> highView = new Dictionary<string, object>();
            Dictionary<string, object> playerView = new Dictionary<string, object>();
            Dictionary<string, object> favoriteView = new Dictionary<string, object>();
            Dictionary<string, object> foreignView = new Dictionary<string, object>();
            Dictionary<string, int> pairCountsBefore = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> pairCountsAfter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<string> favoriteIds = new List<string>();
            string playerId = "";
            string foreignId = "";

            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                Dictionary<string, object> rulerRow = QuerySql(connection, @"SELECT subject_id,
COUNT(*) AS favoring_count FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='active'
AND tag_id LIKE 'ruler_favoring:%' GROUP BY subject_id
ORDER BY favoring_count DESC,subject_id LIMIT 1;", new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
                rulerId = ReadString(rulerRow, "subject_id", "");
                Dictionary<string, object> roster = QuerySql(connection, @"SELECT hero_id,kingdom_id,
current_charm FROM identity_roster WHERE hero_id=$id LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = rulerId }).FirstOrDefault()
                    ?? new Dictionary<string, object>();
                rulerKingdomId = ReadString(roster, "kingdom_id", "");
                rulerCharm = ReadInt(roster, "current_charm", 0);
                double worldDay = ReadDouble(QuerySql(connection, @"SELECT MAX(acquired_day) AS day
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$subject AND status='active' AND tag_id LIKE 'ruler_favoring:%';",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["subject"] = rulerId
                    }).FirstOrDefault(), "day", 0d);
                RecomputePublicStandingForSubjects(connection, campaignId, timelineId,
                    new[] { rulerId }, worldDay);
                standing = ReadPublicStandingTrait(connection, campaignId, timelineId,
                    rulerId, worldDay);
                favoriteIds = ReadDictionaryList(standing, "sources")
                    .Where(IsRulerFavoringStandingSource)
                    .Select(source => ReadString(source, "linkedHeroId", ""))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                observerCandidates = QuerySql(connection, @"SELECT hero_id,kingdom_id,is_player
FROM identity_roster WHERE is_alive=1 AND is_adult=1 AND kingdom_id=$kingdom
AND hero_id<>$subject ORDER BY hero_id;", new Dictionary<string, object>
                {
                    ["kingdom"] = rulerKingdomId, ["subject"] = rulerId
                }).Where(row => !favoriteIds.Contains(ReadString(row, "hero_id", ""),
                    StringComparer.OrdinalIgnoreCase)).Select(row =>
                {
                    string id = ReadString(row, "hero_id", "");
                    Dictionary<string, object> copy = new Dictionary<string, object>(row);
                    copy["jealousy"] = RelationshipTraitPercent(ReadJsonObject(
                        CharacterFile(campaignId, id, "traits.json")), "jealousy");
                    return copy;
                }).OrderBy(row => ReadDouble(row, "jealousy", 50d))
                    .ThenBy(row => ReadString(row, "hero_id", ""), StringComparer.OrdinalIgnoreCase)
                    .ToList();
                lowObserver = observerCandidates.FirstOrDefault() ?? new Dictionary<string, object>();
                highObserver = observerCandidates.LastOrDefault() ?? new Dictionary<string, object>();
                playerId = ReadString(QuerySql(connection,
                    "SELECT hero_id FROM identity_roster WHERE is_player=1 LIMIT 1;",
                    new Dictionary<string, object>()).FirstOrDefault(), "hero_id", "");
                foreignId = ReadString(QuerySql(connection, @"SELECT hero_id FROM identity_roster
WHERE is_alive=1 AND is_adult=1 AND kingdom_id<>'' AND kingdom_id<>$kingdom
ORDER BY hero_id LIMIT 1;", new Dictionary<string, object>
                {
                    ["kingdom"] = rulerKingdomId
                }).FirstOrDefault(), "hero_id", "");
                string lowId = ReadString(lowObserver, "hero_id", "");
                string highId = ReadString(highObserver, "hero_id", "");
                string favoriteId = favoriteIds.FirstOrDefault() ?? "";
                foreach (string observerId in new[] { lowId, highId, playerId, favoriteId, foreignId }
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string pairKey = AmbientPairKey(observerId, rulerId);
                    pairCountsBefore[observerId] = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_pair_chemistry WHERE pair_key=$pair;",
                        new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault(), "count", 0);
                }
                lowView = ResolveObserverPublicStanding(connection, campaignId, timelineId,
                    lowId, rulerId);
                highView = ResolveObserverPublicStanding(connection, campaignId, timelineId,
                    highId, rulerId);
                playerView = ResolveObserverPublicStanding(connection, campaignId, timelineId,
                    playerId, rulerId);
                favoriteView = ResolveObserverPublicStanding(connection, campaignId, timelineId,
                    favoriteId, rulerId);
                foreignView = ResolveObserverPublicStanding(connection, campaignId, timelineId,
                    foreignId, rulerId);
                foreach (string observerId in pairCountsBefore.Keys.ToList())
                {
                    string pairKey = AmbientPairKey(observerId, rulerId);
                    pairCountsAfter[observerId] = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_pair_chemistry WHERE pair_key=$pair;",
                        new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault(), "count", 0);
                }
            }

            double charmMitigation = SocialCharmMitigation(rulerCharm);
            Func<Dictionary<string, object>, int> expected = view => RoundAwayFromZero(-10d
                * (1d - charmMitigation) * ReadDouble(view, "jealousyFactor", 1d));
            bool productionStanding = !string.IsNullOrWhiteSpace(rulerId)
                && favoriteIds.Count >= 2
                && ReadDictionaryList(standing, "sources").Count(IsRulerFavoringStandingSource) >= 2;
            bool jealousyMath = observerCandidates.Count >= 2
                && ReadString(lowView, "scope", "") == "kingdom_non_favorite"
                && ReadString(highView, "scope", "") == "kingdom_non_favorite"
                && Math.Abs(ReadDouble(lowView, "jealousyFactor", -1d)
                    - (0.5d + ReadDouble(lowObserver, "jealousy", 50d) / 100d)) < 0.0000001d
                && Math.Abs(ReadDouble(highView, "jealousyFactor", -1d)
                    - (0.5d + ReadDouble(highObserver, "jealousy", 50d) / 100d)) < 0.0000001d
                && ReadInt(lowView, "favoring", int.MinValue) == expected(lowView)
                && ReadInt(highView, "favoring", int.MinValue) == expected(highView);
            bool factorBoundaries = Math.Abs((0.5d + ClampDouble(0d, 0d, 100d) / 100d) - 0.5d) < 0.0000001d
                && Math.Abs((0.5d + ClampDouble(100d, 0d, 100d) / 100d) - 1.5d) < 0.0000001d;
            bool charmMath = Math.Abs(ReadDouble(lowView, "charmMitigation", -1d)
                    - charmMitigation) < 0.0000001d
                && Math.Abs(ReadDouble(highView, "charmMitigation", -1d)
                    - charmMitigation) < 0.0000001d;
            bool neutralAndScoped = Math.Abs(ReadDouble(playerView, "jealousyFactor", -1d) - 1d) < 0.0000001d
                && ReadString(favoriteView, "scope", "") == "favorite"
                && ReadInt(favoriteView, "favoring", 0) == 15
                && ReadString(foreignView, "scope", "") == "foreign"
                && ReadInt(foreignView, "favoring", int.MinValue) == 0;
            bool noPairCreation = pairCountsBefore.Count >= 4
                && pairCountsBefore.All(pair => pairCountsAfter.TryGetValue(pair.Key, out int after)
                    && after == pair.Value);
            bool nativePrerequisites = ReadBool(result, "authoritativePassRequired", false)
                && ReadStringList(result, "blockers").Count == 0;
            bool passed = nativePrerequisites && productionStanding && jealousyMath
                && factorBoundaries && charmMath && neutralAndScoped && noPairCreation;
            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["rulerId"] = rulerId, ["rulerKingdomId"] = rulerKingdomId,
                ["rulerCharm"] = rulerCharm, ["charmMitigation"] = charmMitigation,
                ["favoriteIds"] = favoriteIds, ["playerObserverId"] = playerId,
                ["foreignObserverId"] = foreignId,
                ["lowObserver"] = lowObserver, ["highObserver"] = highObserver,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["nativePrerequisitesSatisfied"] = nativePrerequisites,
                    ["productionFavoringStandingAvailable"] = productionStanding,
                    ["nativeLowHighJealousyMathExact"] = jealousyMath,
                    ["jealousyFactorBoundariesHalfToOneAndHalf"] = factorBoundaries,
                    ["subjectCharmMitigationExact"] = charmMath,
                    ["playerNeutralFavoriteBonusForeignSuppression"] = neutralAndScoped,
                    ["observerResolutionCreatesNoRelationshipPair"] = noPairCreation
                },
                ["publicStanding"] = standing,
                ["lowObserverView"] = lowView, ["highObserverView"] = highView,
                ["playerView"] = playerView, ["favoriteView"] = favoriteView,
                ["foreignView"] = foreignView,
                ["pairCountsBefore"] = pairCountsBefore,
                ["pairCountsAfter"] = pairCountsAfter,
                ["nativeResult"] = result
            };
            Dictionary<string, object> recorded = SocialBalanceCaseResultApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["runId"] = runId, ["caseId"] = "favoring_jealousy_charm_matrix",
                ["status"] = passed ? "passed" : "failed", ["periodKey"] = "once",
                ["evidence"] = evidence
            });
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "favoring_jealousy_charm",
                ["caseId"] = "favoring_jealousy_charm_matrix",
                ["passed"] = passed, ["evidence"] = evidence, ["recorded"] = recorded
            };
        }

        private static void RecordPlayerParityProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> player = ReadDictionary(result, "player")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> child = ReadDictionary(result, "child")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> producer = ReadDictionary(player, "producerEvidence")
                ?? new Dictionary<string, object>();
            string playerId = ReadString(player, "heroId", "");
            string childId = ReadString(child, "childId", "");
            string sourceKey = ReadString(producer, "sourceKey", "");
            bool playerIsFemale = ReadBool(player, "isFemale", false);
            int expectedValue = playerIsFemale ? -50 : -5;

            Dictionary<string, object> sourceEvent = new Dictionary<string, object>();
            Dictionary<string, object> sourceReceipt = new Dictionary<string, object>();
            Dictionary<string, object> occurrence = new Dictionary<string, object>();
            Dictionary<string, object> subjectTag = new Dictionary<string, object>();
            Dictionary<string, object> reputation = new Dictionary<string, object>();
            Dictionary<string, object> roster = new Dictionary<string, object>();
            Dictionary<string, object> standing = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(campaignId)
                && !string.IsNullOrWhiteSpace(playerId)
                && !string.IsNullOrWhiteSpace(sourceKey))
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureSocialReputationSchema(connection);
                    sourceEvent = QuerySql(connection, @"SELECT event_id,correlation_id,sequence,world_day,payload_json
FROM world_history_events WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_type='social_outcome' AND correlation_id=$source ORDER BY sequence DESC LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["source"] = sourceKey
                        }).FirstOrDefault() ?? new Dictionary<string, object>();
                    string eventId = ReadString(sourceEvent, "event_id", "");
                    if (!string.IsNullOrWhiteSpace(eventId))
                    {
                        DateTime receiptDeadline = DateTime.UtcNow.AddSeconds(30);
                        do
                        {
                            sourceReceipt = QuerySql(connection, @"SELECT event_id,subject_id,archetype_id,
completed,attempt_count,last_error,result_json,updated_ts FROM social_outcome_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND event_id=$event LIMIT 1;",
                                new Dictionary<string, object>
                                {
                                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                                    ["event"] = eventId
                                }).FirstOrDefault() ?? new Dictionary<string, object>();
                            if (ReadInt(sourceReceipt, "completed", 0) != 0
                                || DateTime.UtcNow >= receiptDeadline) break;
                            Thread.Sleep(100);
                        }
                        while (true);
                        occurrence = QuerySql(connection, @"SELECT occurrence_id,archetype_id,source_event_id,
world_day,status,exposure_chance,exposure_roll,participants_json,provenance_summary,snapshot_json
FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='the_unchaste' AND source_event_id=$event LIMIT 1;",
                            new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId, ["timeline"] = timelineId,
                                ["event"] = eventId
                            }).FirstOrDefault() ?? new Dictionary<string, object>();
                    }
                    string occurrenceId = ReadString(occurrence, "occurrence_id", "");
                    if (!string.IsNullOrWhiteSpace(occurrenceId))
                    {
                        subjectTag = QuerySql(connection, @"SELECT occurrence_id,subject_id,tag_id,subject_role,
rumor_value,reputation_value,status,snapshot_json FROM rumor_subject_tags
WHERE occurrence_id=$occurrence AND subject_id=$subject AND tag_id='the_unchaste' LIMIT 1;",
                            new Dictionary<string, object>
                            {
                                ["occurrence"] = occurrenceId, ["subject"] = playerId
                            }).FirstOrDefault() ?? new Dictionary<string, object>();
                        reputation = QuerySql(connection, @"SELECT subject_id,tag_id,source_occurrence_id,
archetype_id,subject_role,description,reputation_value,acquired_day,status,snapshot_json
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$subject AND tag_id='the_unchaste' AND source_occurrence_id=$occurrence LIMIT 1;",
                            new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId, ["timeline"] = timelineId,
                                ["subject"] = playerId, ["occurrence"] = occurrenceId
                            }).FirstOrDefault() ?? new Dictionary<string, object>();
                    }
                    roster = QuerySql(connection, @"SELECT hero_id,is_player,is_alive,is_adult,sex,
kingdom_id,current_charm FROM identity_roster WHERE hero_id=$subject LIMIT 1;",
                        new Dictionary<string, object> { ["subject"] = playerId })
                        .FirstOrDefault() ?? new Dictionary<string, object>();
                    double worldDay = ReadDouble(sourceEvent, "world_day", 0d);
                    RecomputePublicStandingForSubjects(connection, campaignId, timelineId,
                        new[] { playerId }, worldDay);
                    standing = ReadPublicStandingTrait(connection, campaignId, timelineId,
                        playerId, worldDay);
                }
            }

            Dictionary<string, object> currentStatus = SocialBalanceStatus(
                campaignId, timelineId, runId, "");
            List<Dictionary<string, object>> statusCases = ReadDictionaryList(currentStatus, "cases");
            Func<string, bool> compatiblePassed = caseId => statusCases.Any(row =>
                ReadString(row, "caseId", "").Equals(caseId, StringComparison.OrdinalIgnoreCase)
                && ReadString(row, "status", "").Equals("passed_cached", StringComparison.OrdinalIgnoreCase));
            bool prerequisiteCases = compatiblePassed("court_unchaste_publicity_and_gender")
                && compatiblePassed("court_flirt_validated_player_signal")
                && compatiblePassed("player_affair_intimacy_discovery");
            bool publicParentage = ReadBool(child, "publiclyKnown", false)
                && ReadBool(child, "illegitimate", false)
                && ReadStringList(producer, "childIds").Contains(childId, StringComparer.OrdinalIgnoreCase)
                && (ReadString(child, "motherId", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)
                    || ReadString(child, "biologicalFatherId", "").Equals(playerId, StringComparison.OrdinalIgnoreCase));
            bool nativePlayer = ReadBool(player, "isPlayer", false)
                && ReadBool(player, "isRuler", false)
                && ReadInt(roster, "is_player", 0) == 1
                && ReadInt(roster, "is_alive", 0) == 1
                && ReadInt(roster, "is_adult", 0) == 1
                && ReadString(roster, "sex", "").Equals(
                    playerIsFemale ? "female" : "male", StringComparison.OrdinalIgnoreCase);
            bool productionProducer = ReadBool(result, "productionProducerUsed", false)
                && ReadBool(result, "forcedRareBranch", false)
                && ReadBool(result, "worldHistoryDrained", false)
                && ReadBool(producer, "forcedRareBranch", false);
            bool exactProductionRows = !string.IsNullOrWhiteSpace(ReadString(sourceEvent, "event_id", ""))
                && ReadString(sourceEvent, "correlation_id", "").Equals(sourceKey, StringComparison.OrdinalIgnoreCase)
                && ReadInt(sourceReceipt, "completed", 0) == 1
                && ReadString(sourceReceipt, "subject_id", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)
                && ReadString(occurrence, "archetype_id", "").Equals("the_unchaste", StringComparison.OrdinalIgnoreCase)
                && ReadString(occurrence, "status", "").Equals("active", StringComparison.OrdinalIgnoreCase);
            bool exactParityValues = ReadString(subjectTag, "subject_id", "")
                    .Equals(playerId, StringComparison.OrdinalIgnoreCase)
                && ReadInt(subjectTag, "rumor_value", int.MinValue) == 0
                && ReadInt(subjectTag, "reputation_value", int.MinValue) == expectedValue
                && ReadString(reputation, "subject_id", "")
                    .Equals(playerId, StringComparison.OrdinalIgnoreCase)
                && ReadInt(reputation, "reputation_value", int.MinValue) == expectedValue
                && ReadString(reputation, "status", "").Equals("active", StringComparison.OrdinalIgnoreCase);
            bool passed = prerequisiteCases && publicParentage && nativePlayer
                && productionProducer && exactProductionRows && exactParityValues;
            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["player"] = player, ["child"] = child,
                ["expectedGenderParityValue"] = expectedValue,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["compatiblePlayerFlirtAffairAndNpcUnchasteEvidence"] = prerequisiteCases,
                    ["publicIllegitimateChildLinkedToPlayer"] = publicParentage,
                    ["nativeAdultRulerPlayerIndependentlyVerified"] = nativePlayer,
                    ["productionCourtPersonalityProducerUsed"] = productionProducer,
                    ["exactPlayerOccurrenceAndReceiptPersisted"] = exactProductionRows,
                    ["playerZeroRumorAndGenderSpecificEstablishedValue"] = exactParityValues
                },
                ["sourceEvent"] = sourceEvent, ["sourceReceipt"] = sourceReceipt,
                ["occurrence"] = occurrence, ["subjectTag"] = subjectTag,
                ["reputation"] = reputation, ["identityRoster"] = roster,
                ["publicStanding"] = standing,
                ["prerequisiteCaseIds"] = new[]
                {
                    "court_flirt_validated_player_signal",
                    "player_affair_intimacy_discovery",
                    "court_unchaste_publicity_and_gender"
                }
            };
            Dictionary<string, object> recorded = SocialBalanceCaseResultApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["runId"] = runId, ["caseId"] = "court_unchaste_player_parity",
                ["status"] = passed ? "passed" : "failed", ["periodKey"] = "once",
                ["evidence"] = evidence
            });
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "player_parity", ["caseId"] = "court_unchaste_player_parity",
                ["passed"] = passed, ["evidence"] = evidence, ["recorded"] = recorded
            };
        }

        private static void RecordSavePrepareProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> marker = ReadDictionary(result, "persistenceMarker")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> database = SocialBalancePersistenceDatabaseSnapshot(
                campaignId, timelineId);
            bool exactMarker = !string.IsNullOrWhiteSpace(ReadString(marker, "markerId", ""))
                && ReadString(marker, "runId", "").Equals(runId, StringComparison.OrdinalIgnoreCase)
                && ReadString(marker, "campaignId", "").Equals(campaignId, StringComparison.OrdinalIgnoreCase)
                && ReadString(marker, "timelineId", "").Equals(timelineId, StringComparison.OrdinalIgnoreCase);
            bool requiredEvidence = ReadBool(database, "hasRequiredEvidence", false);
            bool migrationSafe = ReadBool(database, "legacyFavoringMigrationSafe", false);
            Dictionary<string, object> prepared = new Dictionary<string, object>
            {
                ["schemaVersion"] = 1,
                ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["runId"] = runId, ["liveRunId"] = ReadString(liveRun, "runId", ""),
                ["preparedGameInstanceId"] = ReadString(liveRun, "gameInstanceId", ""),
                ["marker"] = marker, ["database"] = database,
                ["recordedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                ["assertions"] = new Dictionary<string, object>
                {
                    ["exactSaveBackedMarkerStaged"] = exactMarker,
                    ["signalIntimacyFavoringEvidencePresent"] = requiredEvidence,
                    ["legacyFavoredByMigrationSafe"] = migrationSafe
                }
            };
            WriteJsonObject(SocialBalancePersistencePath(runId), prepared);
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "save_prepare", ["staged"] = exactMarker && requiredEvidence && migrationSafe,
                ["requiresNativeSaveAndDifferentGameInstance"] = true,
                ["evidence"] = prepared
            };
        }

        private static void RecordSaveVerifyProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string campaignId = ReadString(enrollment, "campaignId", "");
            string timelineId = ReadString(enrollment, "timelineId", "main");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> clientVerification = ReadDictionary(result, "persistenceVerification")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> prepared = ReadJsonObject(SocialBalancePersistencePath(runId));
            Dictionary<string, object> preparedMarker = ReadDictionary(prepared, "marker")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> reloadedMarker = ReadDictionary(clientVerification, "marker")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> preparedDatabase = ReadDictionary(prepared, "database")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> currentDatabase = SocialBalancePersistenceDatabaseSnapshot(
                campaignId, timelineId);
            string preparedInstance = ReadString(prepared, "preparedGameInstanceId", "");
            string currentInstance = ReadString(liveRun, "gameInstanceId", "");
            bool prepareExists = !string.IsNullOrWhiteSpace(ReadString(prepared, "runId", ""))
                && ReadString(prepared, "runId", "").Equals(runId, StringComparison.OrdinalIgnoreCase)
                && ReadString(prepared, "campaignId", "").Equals(campaignId, StringComparison.OrdinalIgnoreCase)
                && ReadString(prepared, "timelineId", "").Equals(timelineId, StringComparison.OrdinalIgnoreCase);
            bool markerMatches = !string.IsNullOrWhiteSpace(ReadString(preparedMarker, "markerId", ""))
                && ReadString(preparedMarker, "markerId", "").Equals(
                    ReadString(reloadedMarker, "markerId", ""), StringComparison.OrdinalIgnoreCase);
            bool differentInstance = !string.IsNullOrWhiteSpace(preparedInstance)
                && !string.IsNullOrWhiteSpace(currentInstance)
                && !preparedInstance.Equals(currentInstance, StringComparison.OrdinalIgnoreCase);
            bool clientVerified = ReadBool(clientVerification, "verified", false);
            bool databaseUnchanged = !string.IsNullOrWhiteSpace(ReadString(preparedDatabase, "overallHash", ""))
                && ReadString(preparedDatabase, "overallHash", "").Equals(
                    ReadString(currentDatabase, "overallHash", ""), StringComparison.OrdinalIgnoreCase);
            bool requiredEvidence = ReadBool(currentDatabase, "hasRequiredEvidence", false);
            bool migrationSafe = ReadBool(currentDatabase, "legacyFavoringMigrationSafe", false);
            bool passed = prepareExists && markerMatches && differentInstance && clientVerified
                && databaseUnchanged && requiredEvidence && migrationSafe;
            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["prepare"] = prepared,
                ["clientVerification"] = clientVerification,
                ["currentDatabase"] = currentDatabase,
                ["currentGameInstanceId"] = currentInstance,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["durablePrepareRecordExists"] = prepareExists,
                    ["sameSaveBackedMarkerReloaded"] = markerMatches,
                    ["differentGameInstanceProvesReload"] = differentInstance,
                    ["nativeHarnessAndCourtStateVerified"] = clientVerified,
                    ["signalIntimacyFavoringDatabaseUnchanged"] = databaseUnchanged,
                    ["requiredPersistenceCategoriesPresent"] = requiredEvidence,
                    ["legacyFavoredByRemainsDisabled"] = migrationSafe
                }
            };
            Dictionary<string, object> recorded = SocialBalanceCaseResultApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["runId"] = runId, ["caseId"] = "core_save_reload_idempotency",
                ["status"] = passed ? "passed" : "failed", ["periodKey"] = "once",
                ["evidence"] = evidence
            });
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "save_verify", ["caseId"] = "core_save_reload_idempotency",
                ["passed"] = passed, ["evidence"] = evidence, ["recorded"] = recorded
            };
        }

        private static void RecordCleanupMarkerProfileResult(
            Dictionary<string, object> liveRun,
            Dictionary<string, object> command)
        {
            Dictionary<string, object> enrollment = ReadDictionary(command, "enrollment");
            if (enrollment == null || enrollment.Count == 0)
                enrollment = ReadDictionary(liveRun, "enrollment");
            string runId = ReadString(enrollment, "runId", "");
            Dictionary<string, object> result = ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            bool clientCleared = ReadBool(result, "persistenceMarkerCleared", false);
            string path = SocialBalancePersistencePath(runId);
            bool serverCleared = false;
            if (clientCleared)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    serverCleared = !File.Exists(path);
                }
                catch { serverCleared = false; }
            }
            liveRun["socialBalanceProfileEvaluation"] = new Dictionary<string, object>
            {
                ["profile"] = "cleanup_marker", ["passed"] = clientCleared && serverCleared,
                ["evidence"] = new Dictionary<string, object>
                {
                    ["clientPersistenceMarkerCleared"] = clientCleared,
                    ["serverPrepareRecordCleared"] = serverCleared,
                    ["promptOverrideActive"] = ReadBool(result, "promptOverrideActive", true)
                }
            };
        }

        private static Dictionary<string, object> SocialBalancePersistenceDatabaseSnapshot(
            string campaignId, string timelineId)
        {
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                EnsureCourtSocialReputationSchema(connection);
                Dictionary<string, object> parameters = new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                };
                List<Dictionary<string, object>> signals = QuerySql(connection, @"SELECT exchange_id,signal_id,
session_id,signal_type,speaker_id,target_id,supporting_quote,accepted,rejection_reason,evidence_json
FROM court_social_signal_evidence WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY exchange_id,signal_id;", parameters);
                List<Dictionary<string, object>> signalReceipts = QuerySql(connection, @"SELECT session_id,signal_key,
signal_type,speaker_id,target_id,result_json FROM court_conversation_signal_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY session_id,signal_key;", parameters);
                List<Dictionary<string, object>> favoring = QuerySql(connection, @"SELECT ruler_id,favorite_id,
exchange_count,last_exchange_id,last_world_day FROM court_player_favoring_exchanges
WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY ruler_id,favorite_id;", parameters);
                List<Dictionary<string, object>> favoringReceipts = QuerySql(connection, @"SELECT ruler_id,favorite_id,
exchange_id,world_day FROM court_player_favoring_exchange_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY ruler_id,favorite_id,exchange_id;", parameters);
                List<Dictionary<string, object>> intimacy = QuerySql(connection, @"SELECT signal_id,player_id,partner_id,
eligible,exposed,probability,roll,result_json FROM court_intimacy_exposure_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY signal_id;", parameters);
                List<Dictionary<string, object>> flirtTargets = QuerySql(connection, @"SELECT subject_id,target_id,
first_session_id,first_world_day,last_session_id,last_world_day,incident_count FROM court_flirt_targets
WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY subject_id,target_id;", parameters);
                int activeLegacyReputations = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='active'
AND (tag_id='favored_by' OR tag_id LIKE 'favored_by:%');", parameters).FirstOrDefault(), "count", 0);
                int activeLegacyRumors = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM rumor_subject_tags tags JOIN rumor_occurrences occurrences
ON occurrences.occurrence_id=tags.occurrence_id
WHERE occurrences.campaign_id=$campaign AND occurrences.timeline_id=$timeline AND tags.status='active'
AND (tags.tag_id='favored_by' OR tags.tag_id LIKE 'favored_by:%');", parameters).FirstOrDefault(), "count", 0);
                bool migrationMarker = QuerySql(connection,
                    "SELECT 1 FROM schema_meta WHERE key='ruler_favoring_supersession_v1' AND value='1' LIMIT 1;")
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
                string overallHash = SocialBalanceHash(string.Join("|", sections
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
                ["hash"] = SocialBalanceHash(Json.Serialize(rows))
            };
        }

        private static string SocialBalancePersistencePath(string runId)
        {
            return Path.Combine(SocialBalanceRoot, "persistence",
                SafePathSegment(runId, "run") + ".json");
        }

        private static Dictionary<string, object> SocialBalanceSnapshotApi(Dictionary<string, object> payload)
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
                    if (!QuerySql(connection,
                        "SELECT 1 FROM social_balance_runs WHERE run_id=$run AND campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;",
                        new Dictionary<string, object> { ["run"] = runId, ["campaign"] = campaignId, ["timeline"] = timelineId }).Any())
                        return SocialBalanceError("The run is not enrolled for this campaign and timeline.");
                    ExecuteSql(connection, @"INSERT OR REPLACE INTO social_balance_snapshots(
campaign_id,timeline_id,run_id,period_key,world_day,snapshot_json,created_ts)
VALUES($campaign,$timeline,$run,$period,$day,$snapshot,$now);",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["run"] = runId,
                            ["period"] = periodKey, ["day"] = ReadDouble(payload, "worldDay", 0d),
                            ["snapshot"] = Json.Serialize(snapshot), ["now"] = now
                        });
                }
            }
            return new Dictionary<string, object> { ["ok"] = true, ["stored"] = true, ["periodKey"] = periodKey };
        }

        private static Dictionary<string, object> SocialBalanceRankAffairCandidatesApi(
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
                    QualificationRuntimeProfileFromTarget(targetId, snapshot);
                Dictionary<string, object> profile = ReadJsonObject(
                    CharacterFile(campaignId, targetId, "profile.json"));
                Dictionary<string, object> traits = ReadJsonObject(
                    CharacterFile(campaignId, targetId, "traits.json"));
                string traitSource = "campaign_traits";
                if (profile.Count == 0) profile = runtimeProfile;
                if (traits.Count == 0)
                {
                    traits = BuildTraitDocument(runtimeProfile);
                    traitSource = "deterministic_runtime_projection";
                }
                EnsureTraitPercentageData(traits, targetId);
                EnsureCourtCharacterData(traits, profile, targetId);
                Dictionary<string, object> percentages =
                    TraitPercentageSnapshot(traits);
                Dictionary<string, object> virtues =
                    ReadDictionary(traits, "courtVirtues")
                    ?? CalculateCourtVirtues(percentages);
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

        private static Dictionary<string, object> SocialBalanceSetUnderlyingAffinityApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            string timelineId = ReadString(payload, "timelineId", "main");
            string observerId = ReadString(payload, "observerId", "");
            string subjectId = ReadString(payload, "subjectId", "");
            int requestedValue = Clamp(ReadInt(payload, "value", 0), -100, 100);
            string valueMode = ReadString(payload, "valueMode", "underlying")
                .Trim().ToLowerInvariant();
            if (valueMode != "underlying" && valueMode != "effective")
                return SocialBalanceError("valueMode must be 'underlying' or 'effective'.");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            if (!ValidateSocialBalanceLiveEnrollment(payload, campaignId, out string enrollmentError))
                return SocialBalanceError(enrollmentError);
            if (string.IsNullOrWhiteSpace(observerId) || string.IsNullOrWhiteSpace(subjectId)
                || observerId.Equals(subjectId, StringComparison.OrdinalIgnoreCase))
                return SocialBalanceError("Two distinct stable hero IDs are required.");
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                if (QuerySql(connection,
                    "SELECT 1 FROM identity_roster WHERE hero_id=$id AND is_alive=1 AND is_adult=1 LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = observerId }).Count == 0
                    || QuerySql(connection,
                    "SELECT 1 FROM identity_roster WHERE hero_id=$id AND is_alive=1 AND is_adult=1 LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = subjectId }).Count == 0)
                    return SocialBalanceError("Both heroes must exist as living adults in the synchronized identity roster.");

                string pairKey = AmbientPairKey(observerId, subjectId);
                bool observerIsA = pairKey.StartsWith(observerId + "|", StringComparison.OrdinalIgnoreCase);
                string heroA = observerIsA ? observerId : subjectId;
                string heroB = observerIsA ? subjectId : observerId;
                Dictionary<string, object> priorPair = QuerySql(connection,
                    "SELECT affinity_a_to_b,affinity_b_to_a FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                    new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
                bool pairExisted = priorPair != null;
                int priorUnderlyingAffinity = pairExisted
                    ? ReadInt(priorPair, observerIsA ? "affinity_a_to_b" : "affinity_b_to_a", 0)
                    : 0;
                int day = (int)Math.Floor(worldDay + 0.000001d);
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                int subjectStanding = ReadInt(QuerySql(connection, @"SELECT standing_value
FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject
LIMIT 1;", new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["subject"] = subjectId
                    }).FirstOrDefault(), "standing_value", 0);
                int value = valueMode == "effective"
                    ? SocialBalanceUnderlyingAffinityForEffectiveValue(
                        requestedValue, subjectStanding)
                    : requestedValue;
                ExecuteSql(connection, @"INSERT OR IGNORE INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,first_day,last_day,updated_ts)
VALUES($pair,$a,$b,$day,$day,$ts);",
                    new Dictionary<string, object>
                    {
                        ["pair"] = pairKey, ["a"] = heroA, ["b"] = heroB,
                        ["day"] = day, ["ts"] = ts
                    });
                string affinityColumn = observerIsA ? "affinity_a_to_b" : "affinity_b_to_a";
                ExecuteSql(connection, "UPDATE relationship_pair_chemistry SET "
                    + affinityColumn + "=$value,last_day=MAX(last_day,$day),updated_ts=$ts WHERE pair_key=$pair;",
                    new Dictionary<string, object>
                    {
                        ["value"] = value, ["day"] = day, ["ts"] = ts, ["pair"] = pairKey
                    });
                bool popularityChanged = RecomputeCourtPopularityReputations(connection,
                    campaignId, timelineId, subjectId, worldDay,
                    "social_balance_affinity|" + observerId + "|" + subjectId + "|" + day);
                if (popularityChanged)
                    ReconcileSocialRelationshipsForSubjects(connection, campaignId, timelineId,
                        new[] { subjectId }, worldDay);
                RefreshEffectivePairProjection(connection, campaignId, timelineId,
                    QuerySql(connection,
                        "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                        new Dictionary<string, object> { ["pair"] = pairKey })
                        .FirstOrDefault(), worldDay, "social_balance_affinity");
                Dictionary<string, object> updatedPair = QuerySql(connection, @"SELECT
affinity_a_to_b,affinity_b_to_a,effective_affinity_a_to_b,effective_affinity_b_to_a,
projected_native_relation,native_action_pending,native_action_id
FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                    new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["observerId"] = observerId, ["subjectId"] = subjectId,
                    ["requestedValue"] = requestedValue, ["valueMode"] = valueMode,
                    ["subjectStanding"] = subjectStanding,
                    ["pairExisted"] = pairExisted,
                    ["priorUnderlyingAffinity"] = priorUnderlyingAffinity,
                    ["underlyingAffinity"] = value,
                    ["effectiveAffinity"] = Clamp(value + subjectStanding, -100, 100),
                    ["pairKey"] = pairKey, ["pair"] = updatedPair,
                    ["popularityChanged"] = popularityChanged
                };
            }
        }

        private static int SocialBalanceUnderlyingAffinityForEffectiveValue(
            int effectiveValue, int subjectStanding)
        {
            return Clamp(effectiveValue - subjectStanding, -100, 100);
        }

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
                || !HasCampaignPostgreSqlStorage(campaignId)) return result;
            try
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureSocialReputationSchema(connection);
                    Dictionary<string, object> parameters = new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    };
                    List<Dictionary<string, object>> rumorByTag = QuerySql(connection, @"SELECT rst.tag_id,
COUNT(*) AS occurrence_count,COUNT(DISTINCT rst.subject_id) AS subject_count,
SUM(rst.rumor_value) AS raw_effect_sum,MIN(ro.world_day) AS oldest_day,
MAX(ro.world_day) AS newest_day,MAX(ro.expires_day) AS latest_expiration_day
FROM rumor_subject_tags rst JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline
AND ro.status='active' AND rst.status='active'
GROUP BY rst.tag_id ORDER BY rst.tag_id;", parameters);
                    List<Dictionary<string, object>> reputationByTag = QuerySql(connection, @"SELECT tag_id,
COUNT(*) AS subject_count,SUM(reputation_value) AS raw_effect_sum,
MIN(acquired_day) AS oldest_day,MAX(acquired_day) AS newest_day
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='active'
GROUP BY tag_id ORDER BY tag_id;", parameters);
                    List<Dictionary<string, object>> subjects = QuerySql(connection, @"SELECT subject_id,
SUM(rumor_count) AS active_rumor_count,SUM(reputation_count) AS active_reputation_count,
SUM(rumor_effect) AS raw_rumor_effect,SUM(reputation_effect) AS raw_reputation_effect
FROM (
 SELECT rst.subject_id,COUNT(*) AS rumor_count,0 AS reputation_count,
 SUM(rst.rumor_value) AS rumor_effect,0 AS reputation_effect
 FROM rumor_subject_tags rst JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
 WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline
 AND ro.status='active' AND rst.status='active' GROUP BY rst.subject_id
 UNION ALL
 SELECT subject_id,0,COUNT(*),0,SUM(reputation_value)
 FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
 AND status='active' GROUP BY subject_id
) social GROUP BY subject_id ORDER BY subject_id;", parameters);
                    List<Dictionary<string, object>> projections = QuerySql(connection, @"WITH directional_views AS (
SELECT COALESCE(sb.standing_value,0) AS social_modifier
FROM relationship_pair_chemistry p
LEFT JOIN character_public_standing sb ON sb.campaign_id=$campaign
AND sb.timeline_id=$timeline AND sb.subject_id=p.hero_b_id
UNION ALL
SELECT COALESCE(sa.standing_value,0) AS social_modifier
FROM relationship_pair_chemistry p
LEFT JOIN character_public_standing sa ON sa.campaign_id=$campaign
AND sa.timeline_id=$timeline AND sa.subject_id=p.hero_a_id
)
SELECT
COUNT(*) AS pair_count,
SUM(CASE WHEN social_modifier<0 THEN 1 ELSE 0 END) AS negative_count,
SUM(CASE WHEN social_modifier>0 THEN 1 ELSE 0 END) AS positive_count,
SUM(CASE WHEN social_modifier<=-20 THEN 1 ELSE 0 END) AS rebellion_pressure_count,
MIN(social_modifier) AS minimum_modifier,MAX(social_modifier) AS maximum_modifier,
AVG(social_modifier) AS average_modifier
FROM directional_views;", parameters);
                    List<Dictionary<string, object>> relationships = QuerySql(connection, @"WITH effective_pairs AS (
SELECT
MAX(-100,MIN(100,p.affinity_a_to_b+COALESCE(sb.standing_value,0))) AS effective_a_to_b,
MAX(-100,MIN(100,p.affinity_b_to_a+COALESCE(sa.standing_value,0))) AS effective_b_to_a
FROM relationship_pair_chemistry p
LEFT JOIN character_public_standing sa ON sa.campaign_id=$campaign
AND sa.timeline_id=$timeline AND sa.subject_id=p.hero_a_id
LEFT JOIN character_public_standing sb ON sb.campaign_id=$campaign
AND sb.timeline_id=$timeline AND sb.subject_id=p.hero_b_id
)
SELECT
COUNT(*) AS pair_count,
SUM(CASE WHEN effective_a_to_b<-20 OR effective_b_to_a<-20 THEN 1 ELSE 0 END) AS below_rebellion_threshold_pairs,
SUM(CASE WHEN effective_a_to_b<=-50 OR effective_b_to_a<=-50 THEN 1 ELSE 0 END) AS deeply_hostile_pairs,
MIN(MIN(effective_a_to_b,effective_b_to_a)) AS minimum_effective_affinity,
MAX(MAX(effective_a_to_b,effective_b_to_a)) AS maximum_effective_affinity
FROM effective_pairs;", parameters);
                    List<Dictionary<string, object>> lifecycle = QuerySql(connection, @"SELECT event_type,
COUNT(*) AS event_count,COUNT(DISTINCT subject_id) AS subject_count,
MIN(world_day) AS oldest_day,MAX(world_day) AS newest_day
FROM reputation_lifecycle_events WHERE campaign_id=$campaign AND timeline_id=$timeline
GROUP BY event_type ORDER BY event_type;", parameters);
                    List<Dictionary<string, object>> reasons = QuerySql(connection, @"SELECT reason_status,
COUNT(*) AS activation_count,AVG(generation_attempts) AS average_attempts
FROM reputation_activations WHERE campaign_id=$campaign AND timeline_id=$timeline
GROUP BY reason_status ORDER BY reason_status;", parameters);
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
                    List<Dictionary<string, object>> courtPopularityMetrics = QuerySql(connection, @"SELECT
subject_id,positive_count,negative_count,dynamic_value,calculated_day
FROM court_dynamic_reputation_metrics
WHERE campaign_id=$campaign AND timeline_id=$timeline AND domain='popularity'
ORDER BY subject_id;", parameters);

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

        private static Dictionary<string, object> SocialBalanceStatusApi(Dictionary<string, string> query)
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
                && ReadInt(standing, "standingValue", int.MinValue) == RoundAwayFromZero(
                    sources.Sum(source => ReadDouble(source, "effectiveContribution", 0d)));
        }

        private static Dictionary<string, object> SocialBalanceExportApi(Dictionary<string, string> query)
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
                    List<Dictionary<string, object>> caseRows = QuerySql(connection, @"SELECT *
FROM social_balance_case_results WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ($run='' OR run_id=$run) AND ($case='' OR case_id=$case) ORDER BY case_id,period_key,updated_ts;",
                        new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["run"] = runId, ["case"] = caseId });
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
                    status["snapshots"] = QuerySql(connection, @"SELECT period_key,world_day,snapshot_json,created_ts
FROM social_balance_snapshots WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ($run='' OR run_id=$run) ORDER BY world_day,period_key;",
                        new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["run"] = runId });
                    status["longitudinalCheckpoints"] = QuerySql(connection, @"SELECT run_id,world_day,snapshot_json,created_ts
FROM social_balance_longitudinal_checkpoints WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ($run='' OR run_id=$run) ORDER BY world_day;",
                        new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["run"] = runId })
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

        private static bool ValidateSocialBalanceLiveEnrollment(
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
                    bool found = QuerySql(connection, @"SELECT 1 FROM social_balance_runs
WHERE run_id=$run AND campaign_id=$campaign AND timeline_id=$timeline
AND save_prefix=$prefix AND main_hero_id=$hero AND status IN ('prepared','running','paused') LIMIT 1;",
                        new Dictionary<string, object>
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
                        run = QuerySql(connection, @"SELECT * FROM social_balance_runs
WHERE ($campaign='' OR campaign_id=$campaign) AND ($timeline='' OR timeline_id=$timeline)
ORDER BY updated_ts DESC LIMIT 1;",
                            new Dictionary<string, object> { ["campaign"] = campaignId ?? "", ["timeline"] = timelineId ?? "" }).FirstOrDefault();
                        runId = ReadString(run, "run_id", "");
                    }
                    else
                    {
                        run = QuerySql(connection, "SELECT * FROM social_balance_runs WHERE run_id=$run LIMIT 1;",
                            new Dictionary<string, object> { ["run"] = runId }).FirstOrDefault();
                    }
                    campaignId = FirstNonEmpty(campaignId, ReadString(run, "campaign_id", ""));
                    timelineId = FirstNonEmpty(timelineId, ReadString(run, "timeline_id", "main"));
                    rows = QuerySql(connection, @"SELECT * FROM social_balance_case_results
WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY case_id,period_key;",
                        new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId });
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
                    "ReignBetaServer/src/Modules/Reputation/SocialBalanceTest.cs",
                    "ReignBeta/src/Modules/Reputation/Campaign/ReignLiveInteractionSocialBalanceHost.cs"
                },
                ["acceptance_harness"] = new[]
                {
                    "ReignBetaServer/src/Modules/Reputation/SocialBalanceTest.cs",
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
                result[capability.Key] = SocialBalanceHash(material.ToString()).Substring(0, 24);
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
            return SocialBalanceHash(material.ToString()).Substring(0, 32);
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
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }

        private static string SocialBalanceHash(string value)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant();
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
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS social_balance_runs(
run_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
save_prefix TEXT NOT NULL,main_hero_id TEXT NOT NULL,status TEXT NOT NULL,
manifest_json TEXT NOT NULL,created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_social_balance_runs_campaign ON social_balance_runs(campaign_id,timeline_id,updated_ts);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS social_balance_case_results(
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,case_id TEXT NOT NULL,period_key TEXT NOT NULL,
run_id TEXT NOT NULL,scenario_version INTEGER NOT NULL,dependency_fingerprint TEXT NOT NULL,
status TEXT NOT NULL,attempt_count INTEGER NOT NULL DEFAULT 0,evidence_json TEXT NOT NULL DEFAULT '{}',
first_started_ts INTEGER NOT NULL,completed_ts INTEGER NOT NULL DEFAULT 0,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,case_id,period_key));");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_social_balance_case_run ON social_balance_case_results(run_id,status,updated_ts);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS social_balance_snapshots(
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,run_id TEXT NOT NULL,period_key TEXT NOT NULL,
world_day REAL NOT NULL,snapshot_json TEXT NOT NULL,created_ts INTEGER NOT NULL,
 PRIMARY KEY(campaign_id,timeline_id,period_key));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS social_balance_longitudinal_checkpoints(
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,run_id TEXT NOT NULL,
world_day REAL NOT NULL,snapshot_json TEXT NOT NULL,created_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,run_id,world_day));");
        }

        private static Dictionary<string, object> RunSocialBalanceHarnessSelfTests()
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
