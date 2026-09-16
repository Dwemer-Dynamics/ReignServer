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

        private static Dictionary<string, object> InitializationReadinessStatusApi(
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
                ResumePendingSocialWorldHistoryOutcomes(campaignId, timelineId);
                SignalContinuousRelationshipWorker();
            }
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureWorldHistorySchema(connection);
                EnsureSocialReputationSchema(connection);
                EnsureSocialSubjectWorkSchema(connection);
                EnsureWorldRelationshipSchema(connection);
                EnsureMbtiRelationshipSchema(connection);
                int pending = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM world_history_events e
WHERE e.timeline_id=$timeline AND e.event_type='social_outcome'
AND NOT EXISTS (SELECT 1 FROM social_outcome_receipts r
 WHERE r.event_id=e.event_id AND (r.completed<>0 OR r.attempt_count>=3));",
                    new Dictionary<string, object> { ["timeline"] = timelineId })
                    .FirstOrDefault(), "count", 0);
                int completed = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM social_outcome_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND completed=1;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    }).FirstOrDefault(), "count", 0);
                int failed = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM social_outcome_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND (completed=-1 OR attempt_count>=3);",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    }).FirstOrDefault(), "count", 0);
                Dictionary<string, object> lanes = QuerySql(connection, @"
SELECT COUNT(*) AS total,
SUM(CASE WHEN status='processing' THEN 1 ELSE 0 END) AS processing,
SUM(CASE WHEN status='pending' THEN 1 ELSE 0 END) AS pending,
SUM(CASE WHEN status='processing' AND claim_expires_ts<$ts THEN 1 ELSE 0 END) AS expired
FROM social_subject_work_queue
WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                        , ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    }).FirstOrDefault() ?? new Dictionary<string, object>();
                int standingInvalidations = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM public_standing_invalidations
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status IN ('pending','processing');",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    }).FirstOrDefault(), "count", 0);
                int standingFailures = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND calculation_status='error';",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    }).FirstOrDefault(), "count", 0);
                int nativeTargets = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_native_targets
WHERE status IN ('pending','claimed','failed');").FirstOrDefault(),
                    "count", 0);
                int nativeFailures = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_native_targets
WHERE status='failed';").FirstOrDefault(), "count", 0);
                int rumors = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    }).FirstOrDefault(), "count", 0);
                int reputations = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    }).FirstOrDefault(), "count", 0);
                bool ready = pending == 0 && failed == 0
                    && ReadInt(lanes, "processing", 0) == 0
                    && ReadInt(lanes, "pending", 0) == 0
                    && standingInvalidations == 0 && standingFailures == 0
                    && nativeTargets == 0 && nativeFailures == 0;
                long lastBatchMs = Interlocked.Read(ref SocialOutcomeLastBatchMs);
                long lastBatchCompleted = Interlocked.Read(
                    ref SocialOutcomeLastBatchCompleted);
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
                    ["activeWorkers"] = Volatile.Read(ref SocialOutcomeActiveWorkers),
                    ["configuredWorkers"] = SocialOutcomeConfiguredWorkers,
                    ["completedTotal"] = Interlocked.Read(ref SocialOutcomeCompletedTotal),
                    ["lastBatchMs"] = Interlocked.Read(ref SocialOutcomeLastBatchMs),
                    ["lastBatchCompleted"] = lastBatchCompleted,
                    ["outcomesPerSecond"] = outcomesPerSecond,
                    ["estimatedSecondsRemaining"] = outcomesPerSecond > 0d
                        ? Math.Ceiling(pending / outcomesPerSecond) : 0d,
                    ["lastTransactionMs"] = Interlocked.Read(
                        ref SocialOutcomeLastTransactionMs),
                    ["lastTransactionPerEventMs"] = Interlocked.Read(
                        ref SocialOutcomeLastTransactionPerEventMs),
                    ["transactionConflicts"] = Interlocked.Read(
                        ref SocialOutcomeTransactionConflicts),
                    ["transactionAttempts"] = Interlocked.Read(
                        ref SocialOutcomeTransactionAttempts),
                    ["transactionConflictRate"] =
                        Interlocked.Read(ref SocialOutcomeTransactionAttempts) > 0
                            ? Interlocked.Read(ref SocialOutcomeTransactionConflicts)
                                / (double)Interlocked.Read(
                                    ref SocialOutcomeTransactionAttempts)
                            : 0d,
                    ["lastWorkerError"] = SocialOutcomeLastError,
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
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                EnsureSocialSubjectWorkSchema(connection);
                ExecuteSql(connection, @"UPDATE social_outcome_receipts
SET completed=0,attempt_count=0,last_error='',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND (completed=-1 OR attempt_count>=3);",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    });
                ExecuteSql(connection, @"UPDATE social_subject_work_queue
SET status='pending',claim_owner='',claim_expires_ts=0,last_error='',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status<>'completed';",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    });
            }
            ResumePendingSocialWorldHistoryOutcomes(campaignId, timelineId);
            SignalContinuousRelationshipWorker();
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

            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureWorldHistorySchema(connection);
                EnsureMbtiRelationshipSchema(connection);
                EnsureInitializationReadinessSchema(connection);

                Dictionary<string, object> timeline = QuerySql(
                    connection,
                    @"SELECT campaign_id,timeline_id,head_sequence
FROM world_history_timelines
WHERE campaign_id=$campaign AND timeline_id=$timeline
LIMIT 1;",
                    new Dictionary<string, object>
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

                int pendingNativeTargets = ReadInt(QuerySql(
                    connection,
                    "SELECT COUNT(*) AS count FROM relationship_native_targets;",
                    null).FirstOrDefault(), "count", 0);
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
                    Dictionary<string, object> plan = QuerySql(
                        connection,
                        @"SELECT target_count,client_failed,client_remaining
FROM relationship_native_sync_batches
WHERE campaign_id=$campaign AND timeline_id=$timeline AND plan_id=$plan
LIMIT 1;",
                        new Dictionary<string, object>
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

                ExecuteSql(connection, @"INSERT INTO initialization_readiness_seals(
campaign_id,timeline_id,seal_version,generation_id,history_sequence,
relationship_plan_id,acknowledged_utc)
VALUES($campaign,$timeline,$version,$generation,$sequence,$plan,$utc)
ON CONFLICT(campaign_id,timeline_id,seal_version) DO UPDATE SET
generation_id=$generation,history_sequence=MAX(initialization_readiness_seals.history_sequence,$sequence),
relationship_plan_id=$plan,acknowledged_utc=$utc;",
                    new Dictionary<string, object>
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
                ExecuteSql(connection, @"UPDATE relationship_daily_inputs
SET status='pending',last_error=''
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status='initialization_pending';",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId,
                        ["timeline"] = timelineId
                    });
                SignalContinuousRelationshipWorker();

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
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS initialization_readiness_seals (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,seal_version INTEGER NOT NULL,
generation_id TEXT NOT NULL,history_sequence INTEGER NOT NULL DEFAULT 0,
relationship_plan_id TEXT NOT NULL DEFAULT '',acknowledged_utc TEXT NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,seal_version));");
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
                Dictionary<string, object> opened = WorldHistoryOpenTimelineApi(new Dictionary<string, object>
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
                    OpenCampaignConnection(campaignId))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    ExecuteSql(connection, @"INSERT INTO relationship_native_sync_batches(
plan_id,campaign_id,timeline_id,world_day,target_count,issued_ts,
client_applied,client_remaining,client_failed)
VALUES($plan,$campaign,$timeline,1,1,1,1,0,0);",
                        new Dictionary<string, object>
                        {
                            ["plan"] = completedPlanId,
                            ["campaign"] = campaignId,
                            ["timeline"] = timelineId
                        });
                    ExecuteSql(connection, @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,
world_day,last_sync_day,attempt_count,claimed_ts,last_error,updated_ts)
VALUES('later-a|later-b','later-a','later-b',3,0,'pending',
1,-1000,0,0,'',1);");
                    ExecuteSql(connection, @"INSERT INTO relationship_daily_inputs(
campaign_id,timeline_id,day_key,world_day,status,presence_groups_json,
hero_changes_json,group_count,received_ts)
VALUES($campaign,$timeline,1,1,'initialization_pending','[]','[]',0,1);",
                        new Dictionary<string, object>
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
                    OpenCampaignConnection(campaignId))
                {
                    heldDailyInputStatus = ReadString(QuerySql(connection,
                        @"SELECT status FROM relationship_daily_inputs
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=1;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId,
                            ["timeline"] = timelineId
                        }).FirstOrDefault(), "status", "");
                    ExecuteSql(connection,
                        "DELETE FROM relationship_native_targets WHERE pair_key='later-a|later-b';");
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
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                    releasedDailyInputStatus = ReadString(QuerySql(connection,
                        @"SELECT status FROM relationship_daily_inputs
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=1;",
                        new Dictionary<string, object>
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
