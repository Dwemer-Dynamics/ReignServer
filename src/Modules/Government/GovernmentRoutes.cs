using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static class GovernmentRoutes
    {
        // HTTP routes owned by the Government module, in their former router order (see RouteTable.cs).
        internal static void RegisterGovernmentRoutes(Program.ReignRouteTable routes)
        {
#if !REIGN_EXCLUDE_COURT
            routes.Post("/government/meeting/speaker-statement", request => Program.RunInteractiveRequest(() => GovernmentService.GovernmentSpeakerStatement(request.JsonBody)));
            routes.Post("/government/hearing/discussion", request => Program.RunInteractiveRequest(() => GovernmentService.GovernmentHearingDiscussion(request.JsonBody)));
            routes.Post("/government/crown/decision", request => Program.RunInteractiveRequest(() => GovernmentService.GovernmentCrownDecision(request.JsonBody)));
#endif
        }
    }
}
