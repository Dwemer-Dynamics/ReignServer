namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the Spymaster module, in their former router order (see RouteTable.cs).
        private static void RegisterSpymasterRoutes(ReignRouteTable routes)
        {
            routes.Post("/court/spymaster/memory", request => CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterMemoryApi));
            routes.Post("/court/spymaster/social/status", request => CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterSocialStatusApi));
            routes.Post("/court/spymaster/social/apply", request => CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterSocialApplyApi));
            routes.Post("/court/spymaster/exposure", request => CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterExposureApi));
            routes.Post("/court/spymaster/agents/tick", request => CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterAgentsTickApi));
            routes.Post("/court/spymaster/agents/status", request => CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterAgentsStatusApi));
            routes.Post("/court/spymaster/agents/expose", request => CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterAgentExposeApi));
        }
    }
}
