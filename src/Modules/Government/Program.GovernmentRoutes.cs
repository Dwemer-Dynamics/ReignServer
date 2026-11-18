using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the Government module, in their former router order (see RouteTable.cs).
        // Legacy(...) routes are still served by the if/else chain in HandleClient.
        private static void RegisterGovernmentRoutes(ReignRouteTable routes)
        {
#if !REIGN_EXCLUDE_COURT
            routes.Post("/government/meeting/speaker-statement", request => RunInteractiveRequest(() => GovernmentService.GovernmentSpeakerStatement(request.JsonBody)));
            routes.Post("/government/hearing/discussion", request => RunInteractiveRequest(() => GovernmentService.GovernmentHearingDiscussion(request.JsonBody)));
            routes.Post("/government/crown/decision", request => RunInteractiveRequest(() => GovernmentService.GovernmentCrownDecision(request.JsonBody)));
#endif
        }
    }
}
