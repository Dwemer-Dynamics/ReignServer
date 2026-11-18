using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the Diplomacy module, in their former router order (see RouteTable.cs).
        private static void RegisterDiplomacyRoutes(ReignRouteTable routes)
        {
            routes.Post("/diplomacy/director/evaluate", request => EvaluateWorldDiplomacy(request.JsonBody));
            routes.Post("/diplomacy/debug/random-event", request => QueueRandomDiplomacyDebugEvent(request.JsonBody));
            routes.Get("/political-pressure/notices", request => NextPoliticalPressureNotices(request.Query));
            routes.Post("/political-pressure/acknowledge", request => AcknowledgePoliticalPressureNotice(request.JsonBody));
#if !REIGN_EXCLUDE_COURT
            routes.Post("/clan-accords/sync", request => ClanAccordsSyncApi(request.JsonBody));
#endif
            routes.Get("/diplomacy/events/next", request => NextDiplomaticEvents(request.JsonBody, request.Query));
            routes.Post("/diplomacy/events/acknowledge", request => AcknowledgeDiplomaticEvent(request.JsonBody));
        }
    }
}
