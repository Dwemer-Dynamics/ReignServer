using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the Rebellion module, in their former router order (see RouteTable.cs).
        // Legacy(...) routes are still served by the if/else chain in HandleClient.
        private static void RegisterRebellionRoutes(ReignRouteTable routes)
        {
            routes.Post("/rebellions/evaluate", request => EvaluateRebellions(request.JsonBody));
            routes.Legacy("GET", "/rebellions/query");
            routes.Post("/negotiations/draft", request => NegotiationDraftApi(request.JsonBody));
            routes.Post("/negotiations/respond", request => NegotiationRespondApi(request.JsonBody));
            routes.Get("/negotiations/query", request => NegotiationQueryApi(request.Query));
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Get("/rebellions/tests", request => RunRebellionSelfTests());
#endif
        }
    }
}
