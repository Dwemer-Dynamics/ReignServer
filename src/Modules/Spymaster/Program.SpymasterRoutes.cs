namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static bool TryHandleSpymasterRoute(HttpRequest request, out object response)
        {
            response = null;
            if (request.Method != "POST") return false;
            switch (request.Path)
            {
                case "/court/spymaster/memory":
                    response = CapitalCourtApi(request.JsonBody, SpymasterMemoryApi); return true;
                case "/court/spymaster/social/status":
                    response = CapitalCourtApi(request.JsonBody, SpymasterSocialStatusApi); return true;
                case "/court/spymaster/social/apply":
                    response = CapitalCourtApi(request.JsonBody, SpymasterSocialApplyApi); return true;
                case "/court/spymaster/exposure":
                    response = CapitalCourtApi(request.JsonBody, SpymasterExposureApi); return true;
                case "/court/spymaster/agents/tick":
                    response = CapitalCourtApi(request.JsonBody, SpymasterAgentsTickApi); return true;
                case "/court/spymaster/agents/status":
                    response = CapitalCourtApi(request.JsonBody, SpymasterAgentsStatusApi); return true;
                case "/court/spymaster/agents/expose":
                    response = CapitalCourtApi(request.JsonBody, SpymasterAgentExposeApi); return true;
                default:
                    return false;
            }
        }
    }
}
