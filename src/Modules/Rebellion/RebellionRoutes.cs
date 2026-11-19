using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static class RebellionRoutes
    {
        // HTTP routes owned by the Rebellion module, in their former router order (see RouteTable.cs).
        internal static void RegisterRebellionRoutes(Program.ReignRouteTable routes)
        {
            routes.Post("/rebellions/evaluate", request => RebellionService.EvaluateRebellions(request.JsonBody));
            routes.Get("/rebellions/query", request => RebellionService.RebellionQueryApi(request.Query));
            routes.Post("/negotiations/draft", request => RebellionService.NegotiationDraftApi(request.JsonBody));
            routes.Post("/negotiations/respond", request => RebellionService.NegotiationRespondApi(request.JsonBody));
            routes.Get("/negotiations/query", request => RebellionService.NegotiationQueryApi(request.Query));
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Get("/rebellions/tests", request => RebellionService.RunRebellionSelfTests());
#endif
        }
    }
}
