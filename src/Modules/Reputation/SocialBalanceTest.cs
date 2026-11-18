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
    internal static partial class ReputationService
    {
        private const int SocialBalanceHarnessSchemaVersion = 2;
        private const int SocialBalanceScenarioVersion = 2;
        private static readonly object SocialBalanceLock = new object();
        private static readonly object SocialBalancePreflightSuiteLock = new object();
        private static readonly Dictionary<string, Task<List<Dictionary<string, object>>>> SocialBalancePreflightSuiteTasks
            = new Dictionary<string, Task<List<Dictionary<string, object>>>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Dictionary<string, object>> SocialBalancePreflightEvaluations
            = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        private static string SocialBalanceRoot => Path.Combine(Program.TestsDir, "social-balance");

        internal static Dictionary<string, object> SocialBalanceManifestApi(Dictionary<string, string> query)
        {
            return BuildSocialBalanceManifest();
        }

        internal static Dictionary<string, object> SocialBalancePrepareApi(Dictionary<string, object> payload)
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
                    Program.ExecuteSql(connection, @"INSERT OR REPLACE INTO social_balance_runs(
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

        internal static Dictionary<string, object> SocialBalanceCaseResultApi(Dictionary<string, object> payload)
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
                    Dictionary<string, object> enrolled = Program.QuerySql(connection,
                        "SELECT * FROM social_balance_runs WHERE run_id=$run AND campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;",
                        new Dictionary<string, object> { ["run"] = runId, ["campaign"] = campaignId, ["timeline"] = timelineId }).FirstOrDefault();
                    if (enrolled == null) return SocialBalanceError("The run is not enrolled for this campaign and timeline.");
                    Program.ExecuteSql(connection, @"INSERT INTO social_balance_case_results(
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
                    Program.ExecuteSql(connection, "UPDATE social_balance_runs SET updated_ts=$now WHERE run_id=$run;",
                        new Dictionary<string, object> { ["now"] = now, ["run"] = runId });
                }
            }
            return SocialBalanceStatus(campaignId, timelineId, runId, periodKey);
        }

        internal static void RecordSocialBalanceProfileResultIfApplicable(
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
#if !REIGN_EXCLUDE_VERIFICATION
            if (profile == "preflight")
            {
                RecordPreflightProfileResult(liveRun, command);
                return;
            }
#endif
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
                using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
                {
                    EnsureSocialReputationSchema(connection);
                    persisted = Program.QuerySql(connection, @"SELECT exchange_id,signal_id,signal_type,speaker_id,target_id,
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

#if !REIGN_EXCLUDE_VERIFICATION
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
#endif

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
            Dictionary<string, object> rebellionTests = RebellionService.RunRebellionSelfTests();
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
                    Program.ExecuteSql(connection, @"INSERT INTO social_balance_longitudinal_checkpoints(
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
                    checkpoints = Program.QuerySql(connection, @"SELECT world_day,snapshot_json,created_ts
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
                    Dictionary<string, object> row = Program.QuerySql(connection, @"SELECT snapshot_json
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
                    Dictionary<string, object> row = Program.QuerySql(connection, @"SELECT *
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
                using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
                {
                    EnsureSocialReputationSchema(connection);
                    signalEvidence = Program.QuerySql(connection, @"SELECT exchange_id,signal_id,signal_type,speaker_id,target_id,
supporting_quote,accepted,rejection_reason,evidence_json,created_ts
FROM court_social_signal_evidence
WHERE campaign_id=$campaign AND timeline_id=$timeline AND exchange_id=$exchange
ORDER BY created_ts,signal_id;", new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId, ["exchange"] = exchangeId
                    });
                    producerReceipts = Program.QuerySql(connection, @"SELECT session_id,signal_key,signal_type,speaker_id,target_id,
result_json,created_ts FROM court_conversation_signal_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND session_id=$session
AND speaker_id=$player AND target_id=$target ORDER BY created_ts,signal_key;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["session"] = sessionId,
                            ["player"] = playerId, ["target"] = targetId
                        });
                    targetRows = Program.QuerySql(connection, @"SELECT subject_id,target_id,first_session_id,first_world_day,
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
                using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
                {
                    EnsureSocialReputationSchema(connection);
                    signalEvidence = Program.QuerySql(connection, @"SELECT exchange_id,signal_id,signal_type,speaker_id,target_id,
supporting_quote,accepted,rejection_reason,evidence_json,created_ts
FROM court_social_signal_evidence
WHERE campaign_id=$campaign AND timeline_id=$timeline AND signal_id=$signal;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["signal"] = signalId
                        });
                    exposureReceipts = Program.QuerySql(connection, @"SELECT signal_id,player_id,partner_id,eligible,exposed,
probability,roll,result_json,created_ts FROM court_intimacy_exposure_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND signal_id=$signal;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["signal"] = signalId
                        });
                    occurrences = Program.QuerySql(connection, @"SELECT occurrence_id,archetype_id,thread_key,source_event_id,
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
                                Program.ExecuteSql(connection, @"UPDATE court_intimacy_exposure_receipts
SET exposed=1,result_json=$result WHERE campaign_id=$campaign AND timeline_id=$timeline AND signal_id=$signal;",
                                    new Dictionary<string, object>
                                    {
                                        ["result"] = receiptResultJson,
                                        ["campaign"] = campaignId,
                                        ["timeline"] = timelineId,
                                        ["signal"] = signalId
                                    });
                                rareExposureOverride["forcedResult"] = forcedResult;
                                exposureReceipts = Program.QuerySql(connection, @"SELECT signal_id,player_id,partner_id,eligible,exposed,
probability,roll,result_json,created_ts FROM court_intimacy_exposure_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND signal_id=$signal;",
                                    new Dictionary<string, object>
                                    {
                                        ["campaign"] = campaignId,
                                        ["timeline"] = timelineId,
                                        ["signal"] = signalId
                                    });
                                occurrences = Program.QuerySql(connection, @"SELECT occurrence_id,archetype_id,thread_key,source_event_id,
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
                : Program.ReadJsonObject(Program.CharacterFile(campaignId, targetId, "traits.json"));
            Dictionary<string, object> targetPercentages =
                DialogueService.TraitPercentageSnapshot(targetTraits);
            Dictionary<string, object> targetVirtues =
                ReadDictionary(targetTraits, "courtVirtues")
                ?? CharactersService.CalculateCourtVirtues(targetPercentages);
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
    }
}
