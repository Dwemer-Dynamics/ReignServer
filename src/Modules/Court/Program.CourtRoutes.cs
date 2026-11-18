using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the Court module, in their former router order (see RouteTable.cs).
        // Legacy(...) routes are still served by the if/else chain in HandleClient.
        private static void RegisterCourtRoutes(ReignRouteTable routes)
        {
            routes.Post("/court/session/open", request => CourtSessionOpenApi(request.JsonBody));
            routes.Post("/court/session/tick", request => CourtSessionTickApi(request.JsonBody));
            routes.Post("/court/session/close", request => CourtSessionCloseApi(request.JsonBody));
            routes.Post("/court/agenda/build", request => CourtAgendaBuildApi(request.JsonBody));
            routes.Post("/court/home", request => CourtHomeApi(request.JsonBody));
            routes.Post("/court/view", request => CourtViewApi(request.JsonBody));
            routes.Post("/court/matter/start", request => CourtMatterStartApi(request.JsonBody));
            routes.Post("/court/matter/respond", request => CourtMatterRespondApi(request.JsonBody));
            routes.Post("/court/matter/resolve", request => CourtMatterResolveApi(request.JsonBody));
            routes.Post("/court/offices/assign", request => CapitalCourtApi(request.JsonBody, CourtOfficeAssignApi));
            routes.Post("/court/offices/dismiss", request => CapitalCourtApi(request.JsonBody, CourtOfficeDismissApi));
            routes.Post("/court/royal-council/respond", request => CapitalCourtApi(request.JsonBody, body => RunInteractiveRequest(() => RoyalCouncilRespond(body))));
            routes.Legacy("POST", "/court/ruler-petition/respond");
            routes.Post("/court/life/scene", request => CapitalCourtApi(request.JsonBody, body => RunInteractiveRequest(() => CourtLifeScene(body))));
            routes.Post("/court/life/respond", request => CapitalCourtApi(request.JsonBody, body => RunInteractiveRequest(() => CourtLifeRespond(body))));
            routes.Get("/court/patronage/public-history", request => PatronagePublicHistoryApi(request.Query));
            routes.Post("/court/life/interpret", request => CapitalCourtApi(request.JsonBody, body => RunInteractiveRequest(() => CourtLifeInterpret(body))));
            routes.Post("/court/life/family", request => FamilyVisitDispatch(request.JsonBody));
            routes.Post("/court/life/international/pending", request => CapitalCourtApi(request.JsonBody, InternationalCourtPendingApi));
            routes.Post("/court/life/international/resolve", request => CapitalCourtApi(request.JsonBody, InternationalCourtResolutionApi));
            routes.Post("/court/life/international/refer", request => CapitalCourtApi(request.JsonBody, InternationalCourtReferralApi));
            routes.Post("/court/regent/assign", request => CapitalCourtApi(request.JsonBody, CourtRegentAssignApi));
            routes.Post("/court/regent/dismiss", request => CapitalCourtApi(request.JsonBody, CourtRegentDismissApi));
            routes.Post("/court/ambassadors/assign", request => CapitalCourtApi(request.JsonBody, CourtAmbassadorAssignApi));
            routes.Post("/court/ambassadors/mission", request => CapitalCourtApi(request.JsonBody, CourtAmbassadorMissionApi));
            routes.Post("/court/ambassadors/recall", request => CapitalCourtApi(request.JsonBody, CourtAmbassadorRecallApi));
            routes.Post("/court/foreign-ambassadors/establish", request => CapitalCourtApi(request.JsonBody, ForeignAmbassadorEstablishApi));
            routes.Post("/court/foreign-ambassadors/dismiss", request => CapitalCourtApi(request.JsonBody, ForeignAmbassadorDismissApi));
            routes.Post("/court/foreign-ambassadors/end", request => ForeignAmbassadorEndApi(request.JsonBody));
            routes.Post("/court/foreign-ambassadors/location", request => ForeignAmbassadorLocationApi(request.JsonBody));
            routes.Post("/court/foreign-ambassadors/authority", request => ForeignAmbassadorAuthorityApi(request.JsonBody));
            routes.Post("/court/foreign-ambassadors/tick", request => ForeignAmbassadorTickApi(request.JsonBody));
            routes.Post("/court/foreign-ambassadors/referrals/respond", request => CapitalCourtApi(request.JsonBody, ForeignAmbassadorReferralRespondApi));
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Legacy("POST", "/court/foreign-ambassadors/test");
#endif
            routes.Post("/court/intelligence/start", request => CapitalCourtApi(request.JsonBody, CourtIntelligenceStartApi));
            routes.Post("/court/intelligence/cancel", request => CapitalCourtApi(request.JsonBody, CourtIntelligenceCancelApi));
            routes.Post("/court/obligations/query", request => CapitalCourtApi(request.JsonBody, CourtObligationsQueryApi));
            routes.Post("/court/obligations/upsert", request => CapitalCourtApi(request.JsonBody, CourtObligationUpsertApi));
            routes.Post("/court/obligations/resolve", request => CapitalCourtApi(request.JsonBody, CourtObligationResolveApi));
            routes.Post("/court/plots/query", request => CapitalCourtApi(request.JsonBody, CourtPlotsQueryApi));
            routes.Post("/court/plots/upsert", request => CapitalCourtApi(request.JsonBody, CourtPlotUpsertApi));
            routes.Post("/court/world-action/validate", request => CapitalCourtApi(request.JsonBody, CourtWorldActionValidateApi));
            routes.Post("/court/diplomacy/propose", request => CapitalCourtApi(request.JsonBody, CourtDiplomacyProposeApi));
            routes.Post("/court/supply/validate", request => CapitalCourtApi(request.JsonBody, CourtSupplyValidateApi));
            routes.Post("/court/native/validate", request => CapitalCourtApi(request.JsonBody, CourtNativeActionValidateApi));
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Get("/court/tests", request => RunCourtSelfTests());
#endif
        }
    }
}
