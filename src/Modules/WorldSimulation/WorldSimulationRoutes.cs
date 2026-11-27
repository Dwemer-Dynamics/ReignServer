using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static class WorldSimulationRoutes
    {
        // HTTP routes owned by the WorldSimulation module, in their former router order (see RouteTable.cs).
        internal static void RegisterWorldSimulationRoutes(Program.ReignRouteTable routes)
        {
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Post("/tests/live/arm", request => WorldSimulationService.LiveTestArmApi(request.JsonBody));
            routes.Get("/tests/live/runtime", request => WorldSimulationService.LiveTestRuntimeApi(request.Query));
            routes.Post("/tests/live/targets/search", request => WorldSimulationService.LiveTestTargetSearchApi(request.JsonBody));
            routes.Post("/tests/live/run/start", request => WorldSimulationService.LiveTestRunStartApi(request.JsonBody));
            routes.Post("/tests/live/command/enqueue", request => WorldSimulationService.LiveTestCommandEnqueueApi(request.JsonBody));
            routes.Post("/tests/live/run/pause", request => WorldSimulationService.LiveTestRunPauseApi(request.JsonBody, false));
            routes.Post("/tests/live/run/resume", request => WorldSimulationService.LiveTestRunPauseApi(request.JsonBody, true));
            routes.Post("/tests/live/run/cancel", request => WorldSimulationService.LiveTestRunCancelApi(request.JsonBody));
            routes.Get("/tests/live/run/status", request => WorldSimulationService.LiveTestRunStatusApi(request.Query, false));
            routes.Get("/tests/live/run/report", request => WorldSimulationService.LiveTestRunStatusApi(request.Query, true));
            routes.Post("/tests/live/game/heartbeat", request => WorldSimulationService.LiveTestHeartbeatApi(request.JsonBody));
            routes.Post("/tests/live/game/poll", request => WorldSimulationService.LiveTestGamePollApi(request.JsonBody));
            routes.Post("/tests/live/game/ack", request => WorldSimulationService.LiveTestGameAckApi(request.JsonBody));
            routes.Post("/tests/live/game/result", request => WorldSimulationService.LiveTestGameResultApi(request.JsonBody));
            routes.Post("/tests/live/game/notable-startup-fixture", request => WorldSimulationService.LiveTestNotableStartupFixtureApi(request.JsonBody));
            routes.Get("/tests/campaign-command/manifest", request => WorldSimulationService.CampaignCommandManifestApi(request.Query));
            routes.Post("/tests/campaign-command/prepare", request => WorldSimulationService.CampaignCommandPrepareApi(request.JsonBody));
            routes.Post("/tests/campaign-command/save/verify", request => WorldSimulationService.CampaignCommandSaveVerifyApi(request.JsonBody));
            routes.Post("/tests/campaign-command/profile/result", request => WorldSimulationService.CampaignCommandProfileResultApi(request.JsonBody));
            routes.Get("/tests/campaign-command/status", request => WorldSimulationService.CampaignCommandStatusApi(request.Query, false));
            routes.Get("/tests/campaign-command/report", request => WorldSimulationService.CampaignCommandStatusApi(request.Query, true));
            routes.Get("/tests/campaign-command/replay", request => WorldSimulationService.CampaignCommandReplayApi(request.Query));
            routes.Post("/tests/campaign-command/resume", request => WorldSimulationService.CampaignCommandResumeApi(request.JsonBody));
            routes.Post("/tests/campaign-command/cancel", request => WorldSimulationService.CampaignCommandCancelApi(request.JsonBody));
            routes.Post("/tests/campaign-command/execution", request => WorldSimulationService.CampaignCommandExecutionApi(request.JsonBody));
            routes.Get("/tests/campaign-command/readiness", request => WorldSimulationService.CampaignCommandReadinessApi(request.Query));
#endif
            routes.Post("/world-history/ingest-batch", request => WorldSimulationService.WorldHistoryIngestBatchApi(request.JsonBody));
            routes.Post(WorldSimulationService.OwnedJusticeHistoryAppendRoute, request => WorldSimulationService.WorldHistoryOwnedJusticeAppendApi(request.JsonBody));
            routes.Post("/world-history/timeline/open", request => WorldSimulationService.WorldHistoryOpenTimelineApi(request.JsonBody));
            routes.Get("/world-history/query", request => WorldSimulationService.WorldHistoryQueryApi(request.Query));
            routes.Get("/world-history/head", request => WorldSimulationService.WorldHistoryHeadApi(request.Query));
            routes.GetPrefix("/world-history/event/", StringComparison.OrdinalIgnoreCase, request => WorldSimulationService.WorldHistoryEventApi(request.Query, Uri.UnescapeDataString(request.Path.Substring("/world-history/event/".Length))));
            routes.GetPrefix("/world-history/correlation/", StringComparison.OrdinalIgnoreCase, request => WorldSimulationService.WorldHistoryCorrelationApi(request.Query, Uri.UnescapeDataString(request.Path.Substring("/world-history/correlation/".Length))));
            routes.Post("/world-history/verify", request => WorldSimulationService.WorldHistoryVerifyApi(request.JsonBody));
            routes.Get("/world-history/export", request =>
            {
                Dictionary<string, string> exportQuery = new Dictionary<string, string>(request.Query, StringComparer.OrdinalIgnoreCase) { ["limit"] = "500" };
                return WorldSimulationService.WorldHistoryQueryApi(exportQuery);
            });
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Post("/world-history/tests", request => WorldSimulationService.RunWorldHistorySelfTests());
#endif
            routes.Post("/world-test/heartbeat", request => WorldSimulationService.WorldTestHeartbeatApi(request.JsonBody));
            routes.Get("/world-test/campaigns", request => WorldSimulationService.WorldTestCampaignsApi(request.Query));
            {
                Func<Program.HttpRequest, object> handler = request => WorldSimulationService.WorldTestOverviewApi(request.Query);
                routes.Get("/world-test/overview", handler);
                routes.Get("/world-test/export", handler);
            }
            routes.Get("/world-test/details", request => WorldSimulationService.WorldTestDetailsApi(request.Query));
            routes.Get("/world-test/checkpoints", request => WorldSimulationService.WorldTestCheckpointsApi(request.Query));
            routes.Get("/world-test/comparison", request => WorldSimulationService.WorldTestComparisonApi(request.Query));
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Get("/world-test/tests", request => WorldSimulationService.RunWorldTestSelfTests());
#endif
#if !REIGN_EXCLUDE_COURT
            routes.Post("/arrests/case/apply", request => WorldSimulationService.ArrestCaseApplyApi(request.JsonBody));
            routes.Post(WorldSimulationService.OwnedArrestReclassificationRoute, request => WorldSimulationService.ArrestCaseReclassifyApi(request.JsonBody));
            routes.Post("/arrests/evidence/evaluate", request => WorldSimulationService.ArrestEvidenceEvaluateApi(request.JsonBody));
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Post("/arrests/test/contracts", request => WorldSimulationService.ArrestContractReportApi());
#endif
#endif
            routes.Post("/events/social/approaches/roll", request => WorldSimulationService.SocialEventApproachRollApi(request.JsonBody));
            routes.Post("/events/social/turn", request => Program.RunInteractiveRequest(() => WorldSimulationService.SocialEventTurnApi(request.JsonBody)));
            routes.Post("/events/social/turn-plan", request => Program.RunInteractiveRequest(() => WorldSimulationService.SocialEventTurnPlanApi(request.JsonBody)));
            routes.Post("/campaign-commands/report", request => WorldSimulationService.CampaignCommandReportApi(request.JsonBody));
            routes.Post("/campaign-commands/judge", request => WorldSimulationService.CampaignCommandJudgeApi(request.JsonBody));
        }
    }
}
