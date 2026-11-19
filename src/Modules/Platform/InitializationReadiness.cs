using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int InitializationReadinessSealVersion = 3;

        internal static Dictionary<string, object> InitializationReadinessStatusApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            string timelineId = ReadString(payload, "timelineId", "main");
            if (string.IsNullOrWhiteSpace(campaignId))
                return InitializationReadinessFailure(campaignId, timelineId,
                    InitializationReadinessSealVersion, "campaignId is required.");
            if (ReadBool(payload, "resumeWorkers", true))
            {
                ReputationService.ResumePendingSocialWorldHistoryOutcomes(campaignId, timelineId);
                RelationshipsService.SignalContinuousRelationshipWorker();
            }
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                WorldSimulationService.EnsureWorldHistorySchema(connection);
                ReputationService.EnsureSocialReputationSchema(connection);
                ReputationService.EnsureSocialSubjectWorkSchema(connection);
                RelationshipsService.EnsureWorldRelationshipSchema(connection);
                RelationshipsService.EnsureMbtiRelationshipSchema(connection);
                int pending = ReadInt(PlatformStore.CountWorldHistoryEvents(connection, new Dictionary<string, object> { ["timeline"] = timelineId })
                    .FirstOrDefault(), "count", 0);
                int completed = ReadInt(PlatformStore.CountSocialOutcomeReceipts(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    }).FirstOrDefault(), "count", 0);
                int failed = ReadInt(PlatformStore.CountSocialOutcomeReceipts2(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    }).FirstOrDefault(), "count", 0);
                Dictionary<string, object> lanes = PlatformStore.CountSocialSubjectWorkQueue(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                        , ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    }).FirstOrDefault() ?? new Dictionary<string, object>();
                int standingInvalidations = ReadInt(PlatformStore.CountPublicStandingInvalidations(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    }).FirstOrDefault(), "count", 0);
                int standingFailures = ReadInt(PlatformStore.CountCharacterPublicStanding(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    }).FirstOrDefault(), "count", 0);
                int nativeTargets = ReadInt(PlatformStore.CountRelationshipNativeTargets(connection).FirstOrDefault(),
                    "count", 0);
                int nativeFailures = ReadInt(PlatformStore.CountRelationshipNativeTargets2(connection).FirstOrDefault(), "count", 0);
                int rumors = ReadInt(PlatformStore.CountRumorOccurrences(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    }).FirstOrDefault(), "count", 0);
                int reputations = ReadInt(PlatformStore.CountCharacterReputations(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    }).FirstOrDefault(), "count", 0);
                bool ready = pending == 0 && failed == 0
                    && ReadInt(lanes, "processing", 0) == 0
                    && ReadInt(lanes, "pending", 0) == 0
                    && standingInvalidations == 0 && standingFailures == 0
                    && nativeTargets == 0 && nativeFailures == 0;
                long lastBatchMs = Interlocked.Read(ref ReputationService.SocialOutcomeLastBatchMs);
                long lastBatchCompleted = Interlocked.Read(
                    ref ReputationService.SocialOutcomeLastBatchCompleted);
                double outcomesPerSecond = lastBatchMs > 0
                    ? lastBatchCompleted * 1000d / lastBatchMs : 0d;
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["ready"] = ready,
                    ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                    ["pendingSocialOutcomes"] = pending,
                    ["completedSocialOutcomes"] = completed,
                    ["failedSocialOutcomes"] = failed,
                    ["pendingSubjectLanes"] = ReadInt(lanes, "pending", 0),
                    ["activeSubjectLanes"] = ReadInt(lanes, "processing", 0),
                    ["expiredSubjectClaims"] = ReadInt(lanes, "expired", 0),
                    ["activeWorkers"] = Volatile.Read(ref ReputationService.SocialOutcomeActiveWorkers),
                    ["configuredWorkers"] = ReputationService.SocialOutcomeConfiguredWorkers,
                    ["completedTotal"] = Interlocked.Read(ref ReputationService.SocialOutcomeCompletedTotal),
                    ["lastBatchMs"] = Interlocked.Read(ref ReputationService.SocialOutcomeLastBatchMs),
                    ["lastBatchCompleted"] = lastBatchCompleted,
                    ["outcomesPerSecond"] = outcomesPerSecond,
                    ["estimatedSecondsRemaining"] = outcomesPerSecond > 0d
                        ? Math.Ceiling(pending / outcomesPerSecond) : 0d,
                    ["lastTransactionMs"] = Interlocked.Read(
                        ref ReputationService.SocialOutcomeLastTransactionMs),
                    ["lastTransactionPerEventMs"] = Interlocked.Read(
                        ref ReputationService.SocialOutcomeLastTransactionPerEventMs),
                    ["transactionConflicts"] = Interlocked.Read(
                        ref ReputationService.SocialOutcomeTransactionConflicts),
                    ["transactionAttempts"] = Interlocked.Read(
                        ref ReputationService.SocialOutcomeTransactionAttempts),
                    ["transactionConflictRate"] =
                        Interlocked.Read(ref ReputationService.SocialOutcomeTransactionAttempts) > 0
                            ? Interlocked.Read(ref ReputationService.SocialOutcomeTransactionConflicts)
                                / (double)Interlocked.Read(
                                    ref ReputationService.SocialOutcomeTransactionAttempts)
                            : 0d,
                    ["lastWorkerError"] = ReputationService.SocialOutcomeLastError,
                    ["standingInvalidations"] = standingInvalidations,
                    ["standingFailures"] = standingFailures,
                    ["nativeTargets"] = nativeTargets,
                    ["nativeFailures"] = nativeFailures,
                    ["rumorsCreated"] = rumors,
                    ["reputationsCreated"] = reputations
                };
            }
        }

        private static Dictionary<string, object> InitializationReadinessRetryApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            string timelineId = ReadString(payload, "timelineId", "main");
            if (string.IsNullOrWhiteSpace(campaignId))
                return InitializationReadinessFailure(campaignId, timelineId,
                    InitializationReadinessSealVersion, "campaignId is required.");
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                ReputationService.EnsureSocialReputationSchema(connection);
                ReputationService.EnsureSocialSubjectWorkSchema(connection);
                PlatformStore.UpdateSocialOutcomeReceipts(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    });
                PlatformStore.UpdateSocialSubjectWorkQueue(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    });
            }
            ReputationService.ResumePendingSocialWorldHistoryOutcomes(campaignId, timelineId);
            RelationshipsService.SignalContinuousRelationshipWorker();
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["campaignId"] = campaignId,
                ["timelineId"] = timelineId
            };
        }

        private static Dictionary<string, object> InitializationReadinessSealApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            string timelineId = ReadString(payload, "timelineId", "");
            string generationId = ReadString(payload, "generationId", "");
            string relationshipPlanId = ReadString(payload, "relationshipPlanId", "");
            int sealVersion = ReadInt(payload, "sealVersion", 0);
            long expectedHistorySequence =
                Math.Max(0L, ReadLong(payload, "expectedHistorySequence", 0L));
            if (string.IsNullOrWhiteSpace(campaignId)
                || string.IsNullOrWhiteSpace(timelineId)
                || string.IsNullOrWhiteSpace(generationId))
            {
                return InitializationReadinessFailure(
                    campaignId,
                    timelineId,
                    sealVersion,
                    "campaignId, timelineId, and generationId are required.");
            }
            if (sealVersion != InitializationReadinessSealVersion)
            {
                return InitializationReadinessFailure(
                    campaignId,
                    timelineId,
                    sealVersion,
                    "The client readiness seal version is not supported.");
            }

            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                WorldSimulationService.EnsureWorldHistorySchema(connection);
                RelationshipsService.EnsureMbtiRelationshipSchema(connection);
                EnsureInitializationReadinessSchema(connection);

                Dictionary<string, object> timeline = PlatformStore.SelectWorldHistoryTimelines(
                    connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId,
                        ["timeline"] = timelineId
                    }).FirstOrDefault();
                long durableHistorySequence = ReadLong(timeline, "head_sequence", -1L);
                if (timeline == null || durableHistorySequence < expectedHistorySequence)
                {
                    Dictionary<string, object> failure = InitializationReadinessFailure(
                        campaignId,
                        timelineId,
                        sealVersion,
                        "The requested world-history watermark is not durable.");
                    failure["acknowledgedHistorySequence"] =
                        Math.Max(0L, durableHistorySequence);
                    return failure;
                }

                int pendingNativeTargets = ReadInt(PlatformStore.CountRelationshipNativeTargets3(
                    connection, null).FirstOrDefault(), "count", 0);
                Dictionary<string, object> socialStatus =
                    InitializationReadinessStatusApi(payload);
                if (!ReadBool(socialStatus, "ready", false))
                {
                    Dictionary<string, object> failure =
                        InitializationReadinessFailure(campaignId, timelineId,
                            sealVersion,
                            "Initial social-world processing is not yet quiescent.");
                    foreach (KeyValuePair<string, object> item in socialStatus)
                        failure[item.Key] = item.Value;
                    failure["ok"] = false;
                    return failure;
                }
                bool relationshipPlanComplete = true;
                if (!string.IsNullOrWhiteSpace(relationshipPlanId))
                {
                    Dictionary<string, object> plan = PlatformStore.SelectRelationshipNativeSyncBatches(
                        connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId,
                            ["timeline"] = timelineId,
                            ["plan"] = relationshipPlanId
                        }).FirstOrDefault();
                    relationshipPlanComplete = plan != null
                        && ReadInt(plan, "client_remaining", 1) == 0
                        && ReadInt(plan, "client_failed", 1) == 0;
                }

                // A named opening plan is a stable initialization boundary.
                // Continuous relationship and Public Standing work can produce
                // newer coalesced targets after that plan has been applied. Those
                // targets must drain normally, but they must not invalidate an
                // already completed opening plan and strand the loading screen.
                bool nativeInitializationComplete = relationshipPlanComplete
                    && pendingNativeTargets == 0;
                if (!nativeInitializationComplete)
                {
                    Dictionary<string, object> failure = InitializationReadinessFailure(
                        campaignId,
                        timelineId,
                        sealVersion,
                        !relationshipPlanComplete
                            ? "The initial relationship projection plan is incomplete."
                            : "Native relationship targets are still pending.");
                    failure["acknowledgedHistorySequence"] = durableHistorySequence;
                    failure["relationshipPlanComplete"] = relationshipPlanComplete;
                    failure["pendingNativeTargets"] = pendingNativeTargets;
                    return failure;
                }

                PlatformStore.UpsertInitializationReadinessSeals(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId,
                        ["timeline"] = timelineId,
                        ["version"] = sealVersion,
                        ["generation"] = generationId,
                        ["sequence"] = expectedHistorySequence,
                        ["plan"] = relationshipPlanId,
                        ["utc"] = DateTime.UtcNow.ToString("o")
                    });

                // The opening presence input is durable before the game is
                // released, but relationship processing is intentionally held
                // behind this seal. This prevents the day-one worker from
                // competing with native projection receipts and creating a
                // second generation of targets inside the loading gate.
                PlatformStore.UpdateRelationshipDailyInputs(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId,
                        ["timeline"] = timelineId
                    });
                RelationshipsService.SignalContinuousRelationshipWorker();

                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["campaignId"] = campaignId,
                    ["timelineId"] = timelineId,
                    ["sealVersion"] = sealVersion,
                    ["acknowledgedHistorySequence"] = durableHistorySequence,
                    ["relationshipPlanComplete"] = true,
                    ["pendingNativeTargets"] = pendingNativeTargets,
                    ["error"] = ""
                };
            }
        }

        private static void EnsureInitializationReadinessSchema(ReignDbConnection connection)
        {
            PlatformSchema.CreateInitializationReadinessSeals(connection);
        }

        private static Dictionary<string, object> InitializationReadinessFailure(
            string campaignId,
            string timelineId,
            int sealVersion,
            string error)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["campaignId"] = campaignId ?? "",
                ["timelineId"] = timelineId ?? "",
                ["sealVersion"] = sealVersion,
                ["acknowledgedHistorySequence"] = 0L,
                ["relationshipPlanComplete"] = false,
                ["pendingNativeTargets"] = 0,
                ["error"] = error ?? "Campaign initialization is not ready."
            };
        }

        private static Dictionary<string, object> RunInitializationReadinessSelfTests()
        {
            string campaignId = "initialization_"
                + Guid.NewGuid().ToString("N").Substring(0, 12);
            string timelineId = "main_initialization";
            try
            {
                Dictionary<string, object> opened = WorldSimulationService.WorldHistoryOpenTimelineApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["timelineId"] = timelineId,
                    ["savedSequence"] = 3L,
                    ["savedHeadEventId"] = "initialization_head",
                    ["historyCompleteFromWorldDay"] = 1d,
                    ["currentWorldDay"] = 1d
                });
                timelineId = ReadString(opened, "timelineId", timelineId);
                Dictionary<string, object> missingHistory =
                    InitializationReadinessSealApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["timelineId"] = timelineId,
                        ["generationId"] = "generation-a",
                        ["sealVersion"] = InitializationReadinessSealVersion,
                        ["expectedHistorySequence"] = 4L
                    });
                Dictionary<string, object> accepted =
                    InitializationReadinessSealApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["timelineId"] = timelineId,
                        ["generationId"] = "generation-a",
                        ["sealVersion"] = InitializationReadinessSealVersion,
                        ["expectedHistorySequence"] = 3L
                    });
                const string completedPlanId =
                    "native_sync_initialization_regression";
                using (ReignDbConnection connection =
                    ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    RelationshipsService.EnsureMbtiRelationshipSchema(connection);
                    PlatformStore.InsertRelationshipNativeSyncBatches(connection, new Dictionary<string, object>
                        {
                            ["plan"] = completedPlanId,
                            ["campaign"] = campaignId,
                            ["timeline"] = timelineId
                        });
                    PlatformStore.InsertRelationshipNativeTargets(connection);
                    PlatformStore.InsertRelationshipDailyInputs(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId,
                            ["timeline"] = timelineId
                        });
                }
                Dictionary<string, object> rejectedCompletedPlanWithLaterWork =
                    InitializationReadinessSealApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["timelineId"] = timelineId,
                            ["generationId"] = "generation-b",
                            ["relationshipPlanId"] = completedPlanId,
                            ["sealVersion"] =
                                InitializationReadinessSealVersion,
                            ["expectedHistorySequence"] = 3L
                        });
                string heldDailyInputStatus;
                using (ReignDbConnection connection =
                    ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    heldDailyInputStatus = ReadString(PlatformStore.SelectRelationshipDailyInputs(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId,
                            ["timeline"] = timelineId
                        }).FirstOrDefault(), "status", "");
                    PlatformStore.DeleteRelationshipNativeTargets(connection);
                }
                Dictionary<string, object> acceptedAfterQuiescence =
                    InitializationReadinessSealApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["timelineId"] = timelineId,
                            ["generationId"] = "generation-b",
                            ["relationshipPlanId"] = completedPlanId,
                            ["sealVersion"] = InitializationReadinessSealVersion,
                            ["expectedHistorySequence"] = 3L
                        });
                string releasedDailyInputStatus;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                    releasedDailyInputStatus = ReadString(PlatformStore.SelectRelationshipDailyInputs(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId,
                            ["timeline"] = timelineId
                        }).FirstOrDefault(), "status", "");
                List<Dictionary<string, object>> assertions =
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = "rejects_missing_history_watermark",
                            ["passed"] = !ReadBool(missingHistory, "ok", true)
                        },
                        new Dictionary<string, object>
                        {
                            ["name"] = "acknowledges_durable_quiescent_campaign",
                            ["passed"] = ReadBool(accepted, "ok", false)
                                && ReadLong(
                                    accepted,
                                    "acknowledgedHistorySequence",
                                    0L) >= 3L
                        },
                        new Dictionary<string, object>
                        {
                            ["name"] =
                                "later_native_targets_block_readiness_until_quiescent",
                            ["passed"] = !ReadBool(
                                rejectedCompletedPlanWithLaterWork,
                                "ok", true)
                                && heldDailyInputStatus.Equals(
                                    "initialization_pending",
                                    StringComparison.OrdinalIgnoreCase)
                                && ReadBool(acceptedAfterQuiescence,
                                    "ok", false)
                        },
                        new Dictionary<string, object>
                        {
                            ["name"] =
                                "day_one_worker_input_releases_only_after_readiness_seal",
                            ["passed"] = string.Equals(
                                releasedDailyInputStatus, "pending",
                                StringComparison.OrdinalIgnoreCase)
                        }
                    };
                bool passed = assertions.All(row =>
                    ReadBool(row, "passed", false));
                return new Dictionary<string, object>
                {
                    ["ok"] = passed,
                    ["passed"] = passed,
                    ["assertions"] = assertions
                };
            }
            finally
            {
                try { Directory.Delete(CampaignDirectory(campaignId), true); }
                catch { }
            }
        }
    }
}
