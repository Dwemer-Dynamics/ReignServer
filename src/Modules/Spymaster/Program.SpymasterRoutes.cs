namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the Spymaster module, in their former router order (see RouteTable.cs).
        private static void RegisterSpymasterRoutes(ReignRouteTable routes)
        {
            routes.Post("/court/spymaster/memory", request => CourtService.CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterMemoryApi));
            routes.Post("/court/spymaster/social/status", request => CourtService.CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterSocialStatusApi));
            routes.Post("/court/spymaster/social/apply", request => CourtService.CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterSocialApplyApi));
            routes.Post("/court/spymaster/exposure", request => CourtService.CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterExposureApi));
            routes.Post("/court/spymaster/agents/tick", request => CourtService.CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterAgentsTickApi));
            routes.Post("/court/spymaster/agents/status", request => CourtService.CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterAgentsStatusApi));
            routes.Post("/court/spymaster/agents/expose", request => CourtService.CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterAgentExposeApi));
        }
    }
}
