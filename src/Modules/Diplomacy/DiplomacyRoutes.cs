using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static class DiplomacyRoutes
    {
        // HTTP routes owned by the Diplomacy module, in their former router order (see RouteTable.cs).
        internal static void RegisterDiplomacyRoutes(Program.ReignRouteTable routes)
        {
            routes.Post("/diplomacy/director/evaluate", request => DiplomacyService.EvaluateWorldDiplomacy(request.JsonBody));
            routes.Post("/diplomacy/debug/random-event", request => DiplomacyService.QueueRandomDiplomacyDebugEvent(request.JsonBody));
            routes.Get("/political-pressure/notices", request => DiplomacyService.NextPoliticalPressureNotices(request.Query));
            routes.Post("/political-pressure/acknowledge", request => DiplomacyService.AcknowledgePoliticalPressureNotice(request.JsonBody));
#if !REIGN_EXCLUDE_COURT
            routes.Post("/clan-accords/sync", request => DiplomacyService.ClanAccordsSyncApi(request.JsonBody));
#endif
            routes.Get("/diplomacy/events/next", request => DiplomacyService.NextDiplomaticEvents(request.JsonBody, request.Query));
            routes.Post("/diplomacy/events/acknowledge", request => DiplomacyService.AcknowledgeDiplomaticEvent(request.JsonBody));
        }
    }
}
