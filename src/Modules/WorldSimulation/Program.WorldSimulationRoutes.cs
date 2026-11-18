using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the WorldSimulation module, in their former router order (see RouteTable.cs).
        private static void RegisterWorldSimulationRoutes(ReignRouteTable routes)
        {
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Post("/tests/live/arm", request => LiveTestArmApi(request.JsonBody));
            routes.Get("/tests/live/runtime", request => LiveTestRuntimeApi(request.Query));
            routes.Post("/tests/live/targets/search", request => LiveTestTargetSearchApi(request.JsonBody));
            routes.Post("/tests/live/run/start", request => LiveTestRunStartApi(request.JsonBody));
            routes.Post("/tests/live/command/enqueue", request => LiveTestCommandEnqueueApi(request.JsonBody));
            routes.Post("/tests/live/run/pause", request => LiveTestRunPauseApi(request.JsonBody, false));
            routes.Post("/tests/live/run/resume", request => LiveTestRunPauseApi(request.JsonBody, true));
            routes.Post("/tests/live/run/cancel", request => LiveTestRunCancelApi(request.JsonBody));
            routes.Get("/tests/live/run/status", request => LiveTestRunStatusApi(request.Query, false));
            routes.Get("/tests/live/run/report", request => LiveTestRunStatusApi(request.Query, true));
            routes.Post("/tests/live/game/heartbeat", request => LiveTestHeartbeatApi(request.JsonBody));
            routes.Post("/tests/live/game/poll", request => LiveTestGamePollApi(request.JsonBody));
            routes.Post("/tests/live/game/ack", request => LiveTestGameAckApi(request.JsonBody));
            routes.Post("/tests/live/game/result", request => LiveTestGameResultApi(request.JsonBody));
            routes.Get("/tests/campaign-command/manifest", request => CampaignCommandManifestApi(request.Query));
            routes.Post("/tests/campaign-command/prepare", request => CampaignCommandPrepareApi(request.JsonBody));
            routes.Post("/tests/campaign-command/save/verify", request => CampaignCommandSaveVerifyApi(request.JsonBody));
            routes.Post("/tests/campaign-command/profile/result", request => CampaignCommandProfileResultApi(request.JsonBody));
            routes.Get("/tests/campaign-command/status", request => CampaignCommandStatusApi(request.Query, false));
            routes.Get("/tests/campaign-command/report", request => CampaignCommandStatusApi(request.Query, true));
            routes.Get("/tests/campaign-command/replay", request => CampaignCommandReplayApi(request.Query));
            routes.Post("/tests/campaign-command/resume", request => CampaignCommandResumeApi(request.JsonBody));
            routes.Post("/tests/campaign-command/cancel", request => CampaignCommandCancelApi(request.JsonBody));
            routes.Post("/tests/campaign-command/execution", request => CampaignCommandExecutionApi(request.JsonBody));
            routes.Get("/tests/campaign-command/readiness", request => CampaignCommandReadinessApi(request.Query));
#endif
            routes.Post("/world-history/ingest-batch", request => WorldHistoryIngestBatchApi(request.JsonBody));
            routes.Post("/world-history/timeline/open", request => WorldHistoryOpenTimelineApi(request.JsonBody));
            routes.Get("/world-history/query", request => WorldHistoryQueryApi(request.Query));
            routes.GetPrefix("/world-history/event/", StringComparison.OrdinalIgnoreCase, request => WorldHistoryEventApi(request.Query, Uri.UnescapeDataString(request.Path.Substring("/world-history/event/".Length))));
            routes.GetPrefix("/world-history/correlation/", StringComparison.OrdinalIgnoreCase, request => WorldHistoryCorrelationApi(request.Query, Uri.UnescapeDataString(request.Path.Substring("/world-history/correlation/".Length))));
            routes.Post("/world-history/verify", request => WorldHistoryVerifyApi(request.JsonBody));
            routes.Get("/world-history/export", request =>
            {
                Dictionary<string, string> exportQuery = new Dictionary<string, string>(request.Query, StringComparer.OrdinalIgnoreCase) { ["limit"] = "500" };
                return WorldHistoryQueryApi(exportQuery);
            });
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Post("/world-history/tests", request => RunWorldHistorySelfTests());
#endif
            routes.Post("/world-test/heartbeat", request => WorldTestHeartbeatApi(request.JsonBody));
            routes.Get("/world-test/campaigns", request => WorldTestCampaignsApi(request.Query));
            {
                Func<HttpRequest, object> handler = request => WorldTestOverviewApi(request.Query);
                routes.Get("/world-test/overview", handler);
                routes.Get("/world-test/export", handler);
            }
            routes.Get("/world-test/details", request => WorldTestDetailsApi(request.Query));
            routes.Get("/world-test/checkpoints", request => WorldTestCheckpointsApi(request.Query));
            routes.Get("/world-test/comparison", request => WorldTestComparisonApi(request.Query));
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Get("/world-test/tests", request => RunWorldTestSelfTests());
#endif
#if !REIGN_EXCLUDE_COURT
            routes.Post("/arrests/case/apply", request => ArrestCaseApplyApi(request.JsonBody));
            routes.Post("/arrests/evidence/evaluate", request => ArrestEvidenceEvaluateApi(request.JsonBody));
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Post("/arrests/test/contracts", request => ArrestContractReportApi());
#endif
#endif
            routes.Post("/events/social/approaches/roll", request => SocialEventApproachRollApi(request.JsonBody));
            routes.Post("/events/social/turn", request => RunInteractiveRequest(() => SocialEventTurnApi(request.JsonBody)));
            routes.Post("/events/social/turn-plan", request => RunInteractiveRequest(() => SocialEventTurnPlanApi(request.JsonBody)));
            routes.Post("/campaign-commands/report", request => CampaignCommandReportApi(request.JsonBody));
            routes.Post("/campaign-commands/judge", request => CampaignCommandJudgeApi(request.JsonBody));
        }
    }
}
