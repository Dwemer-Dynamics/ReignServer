namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the Spymaster module, in their former router order (see RouteTable.cs).
        // Legacy(...) routes are still served by the if/else chain in HandleClient.
        private static void RegisterSpymasterRoutes(ReignRouteTable routes)
        {
            routes.Legacy("POST", "/court/spymaster/memory");
            routes.Legacy("POST", "/court/spymaster/social/status");
            routes.Legacy("POST", "/court/spymaster/social/apply");
            routes.Legacy("POST", "/court/spymaster/exposure");
            routes.Legacy("POST", "/court/spymaster/agents/tick");
            routes.Legacy("POST", "/court/spymaster/agents/status");
            routes.Legacy("POST", "/court/spymaster/agents/expose");
        }

        private static bool TryHandleSpymasterRoute(HttpRequest request, out object response)
        {
            response = null;
            if (request.Method != "POST") return false;
            switch (request.Path)
            {
                case "/court/spymaster/memory":
                    response = CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterMemoryApi); return true;
                case "/court/spymaster/social/status":
                    response = CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterSocialStatusApi); return true;
                case "/court/spymaster/social/apply":
                    response = CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterSocialApplyApi); return true;
                case "/court/spymaster/exposure":
                    response = CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterExposureApi); return true;
                case "/court/spymaster/agents/tick":
                    response = CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterAgentsTickApi); return true;
                case "/court/spymaster/agents/status":
                    response = CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterAgentsStatusApi); return true;
                case "/court/spymaster/agents/expose":
                    response = CapitalCourtApi(request.JsonBody, SpymasterService.SpymasterAgentExposeApi); return true;
                default:
                    return false;
            }
        }
    }
}
