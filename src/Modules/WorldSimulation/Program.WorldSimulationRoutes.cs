using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the WorldSimulation module, in their former router order (see RouteTable.cs).
        // Legacy(...) routes are still served by the if/else chain in HandleClient.
        private static void RegisterWorldSimulationRoutes(ReignRouteTable routes)
        {
            routes.Legacy("POST", "/tests/live/arm");
            routes.Legacy("GET", "/tests/live/runtime");
            routes.Legacy("POST", "/tests/live/targets/search");
            routes.Legacy("POST", "/tests/live/run/start");
            routes.Legacy("POST", "/tests/live/command/enqueue");
            routes.Legacy("POST", "/tests/live/run/pause");
            routes.Legacy("POST", "/tests/live/run/resume");
            routes.Legacy("POST", "/tests/live/run/cancel");
            routes.Legacy("GET", "/tests/live/run/status");
            routes.Legacy("GET", "/tests/live/run/report");
            routes.Legacy("POST", "/tests/live/game/heartbeat");
            routes.Legacy("POST", "/tests/live/game/poll");
            routes.Legacy("POST", "/tests/live/game/ack");
            routes.Legacy("POST", "/tests/live/game/result");
            routes.Legacy("GET", "/tests/campaign-command/manifest");
            routes.Legacy("POST", "/tests/campaign-command/prepare");
            routes.Legacy("POST", "/tests/campaign-command/save/verify");
            routes.Legacy("POST", "/tests/campaign-command/profile/result");
            routes.Legacy("GET", "/tests/campaign-command/status");
            routes.Legacy("GET", "/tests/campaign-command/report");
            routes.Legacy("GET", "/tests/campaign-command/replay");
            routes.Legacy("POST", "/tests/campaign-command/resume");
            routes.Legacy("POST", "/tests/campaign-command/cancel");
            routes.Legacy("POST", "/tests/campaign-command/execution");
            routes.Legacy("GET", "/tests/campaign-command/readiness");
            routes.Post("/world-history/ingest-batch", request => WorldHistoryIngestBatchApi(request.JsonBody));
            routes.Post("/world-history/timeline/open", request => WorldHistoryOpenTimelineApi(request.JsonBody));
            routes.Legacy("GET", "/world-history/query");
            routes.LegacyPrefix("GET", "/world-history/event/", StringComparison.OrdinalIgnoreCase);
            routes.LegacyPrefix("GET", "/world-history/correlation/", StringComparison.OrdinalIgnoreCase);
            routes.Post("/world-history/verify", request => WorldHistoryVerifyApi(request.JsonBody));
            routes.Get("/world-history/export", request =>
            {
                Dictionary<string, string> exportQuery = new Dictionary<string, string>(request.Query, StringComparer.OrdinalIgnoreCase) { ["limit"] = "500" };
                return WorldHistoryQueryApi(exportQuery);
            });
            routes.Post("/world-history/tests", request => RunWorldHistorySelfTests());
            routes.Post("/world-test/heartbeat", request => WorldTestHeartbeatApi(request.JsonBody));
            routes.Legacy("GET", "/world-test/campaigns");
            routes.Legacy("GET", "/world-test/overview");
            routes.Legacy("GET", "/world-test/export");
            routes.Legacy("GET", "/world-test/details");
            routes.Get("/world-test/checkpoints", request => WorldTestCheckpointsApi(request.Query));
            routes.Get("/world-test/comparison", request => WorldTestComparisonApi(request.Query));
            routes.Get("/world-test/tests", request => RunWorldTestSelfTests());
#if !REIGN_EXCLUDE_COURT
            routes.Legacy("POST", "/arrests/case/apply");
            routes.Legacy("POST", "/arrests/evidence/evaluate");
            routes.Legacy("POST", "/arrests/test/contracts");
#endif
            routes.Post("/events/social/approaches/roll", request => SocialEventApproachRollApi(request.JsonBody));
            routes.Legacy("POST", "/events/social/turn");
            routes.Post("/events/social/turn-plan", request => RunInteractiveRequest(() => SocialEventTurnPlanApi(request.JsonBody)));
            routes.Post("/campaign-commands/report", request => CampaignCommandReportApi(request.JsonBody));
            routes.Post("/campaign-commands/judge", request => CampaignCommandJudgeApi(request.JsonBody));
        }
    }
}
