using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the Court module, in their former router order (see RouteTable.cs).
        private static void RegisterCourtRoutes(ReignRouteTable routes)
        {
            routes.Post("/court/session/open", request => CourtService.CourtSessionOpenApi(request.JsonBody));
            routes.Post("/court/session/tick", request => CourtService.CourtSessionTickApi(request.JsonBody));
            routes.Post("/court/session/close", request => CourtService.CourtSessionCloseApi(request.JsonBody));
            routes.Post("/court/agenda/build", request => CourtService.CourtAgendaBuildApi(request.JsonBody));
            routes.Post("/court/home", request => CourtService.CourtHomeApi(request.JsonBody));
            routes.Post("/court/view", request => CourtService.CourtViewApi(request.JsonBody));
            routes.Post("/court/matter/start", request => CourtService.CourtMatterStartApi(request.JsonBody));
            routes.Post("/court/matter/respond", request => CourtService.CourtMatterRespondApi(request.JsonBody));
            routes.Post("/court/matter/resolve", request => CourtService.CourtMatterResolveApi(request.JsonBody));
            routes.Post("/court/offices/assign", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.CourtOfficeAssignApi));
            routes.Post("/court/offices/dismiss", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.CourtOfficeDismissApi));
            routes.Post("/court/royal-council/respond", request => CourtService.CapitalCourtApi(request.JsonBody, body => RunInteractiveRequest(() => CourtService.RoyalCouncilRespond(body))));
            routes.Post("/court/ruler-petition/respond", request => CourtService.CapitalCourtApi(request.JsonBody, body => RunInteractiveRequest(() => CourtService.RulerPetitionRespond(body))));
            routes.Post("/court/life/scene", request => CourtService.CapitalCourtApi(request.JsonBody, body => RunInteractiveRequest(() => CourtService.CourtLifeScene(body))));
            routes.Post("/court/life/respond", request => CourtService.CapitalCourtApi(request.JsonBody, body => RunInteractiveRequest(() => CourtService.CourtLifeRespond(body))));
            routes.Get("/court/patronage/public-history", request => CourtService.PatronagePublicHistoryApi(request.Query));
            routes.Post("/court/life/interpret", request => CourtService.CapitalCourtApi(request.JsonBody, body => RunInteractiveRequest(() => CourtService.CourtLifeInterpret(body))));
            routes.Post("/court/life/family", request => CourtService.FamilyVisitDispatch(request.JsonBody));
            routes.Post("/court/life/international/pending", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.InternationalCourtPendingApi));
            routes.Post("/court/life/international/resolve", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.InternationalCourtResolutionApi));
            routes.Post("/court/life/international/refer", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.InternationalCourtReferralApi));
            routes.Post("/court/regent/assign", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.CourtRegentAssignApi));
            routes.Post("/court/regent/dismiss", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.CourtRegentDismissApi));
            routes.Post("/court/ambassadors/assign", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.CourtAmbassadorAssignApi));
            routes.Post("/court/ambassadors/mission", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.CourtAmbassadorMissionApi));
            routes.Post("/court/ambassadors/recall", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.CourtAmbassadorRecallApi));
            routes.Post("/court/foreign-ambassadors/establish", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.ForeignAmbassadorEstablishApi));
            routes.Post("/court/foreign-ambassadors/dismiss", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.ForeignAmbassadorDismissApi));
            routes.Post("/court/foreign-ambassadors/end", request => CourtService.ForeignAmbassadorEndApi(request.JsonBody));
            routes.Post("/court/foreign-ambassadors/location", request => CourtService.ForeignAmbassadorLocationApi(request.JsonBody));
            routes.Post("/court/foreign-ambassadors/authority", request => CourtService.ForeignAmbassadorAuthorityApi(request.JsonBody));
            routes.Post("/court/foreign-ambassadors/tick", request => CourtService.ForeignAmbassadorTickApi(request.JsonBody));
            routes.Post("/court/foreign-ambassadors/referrals/respond", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.ForeignAmbassadorReferralRespondApi));
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Post("/court/foreign-ambassadors/test", request => CourtService.CapitalAmbassadorTestApi(request.JsonBody));
#endif
            routes.Post("/court/intelligence/start", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.CourtIntelligenceStartApi));
            routes.Post("/court/intelligence/cancel", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.CourtIntelligenceCancelApi));
            routes.Post("/court/obligations/query", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.CourtObligationsQueryApi));
            routes.Post("/court/obligations/upsert", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.CourtObligationUpsertApi));
            routes.Post("/court/obligations/resolve", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.CourtObligationResolveApi));
            routes.Post("/court/plots/query", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.CourtPlotsQueryApi));
            routes.Post("/court/plots/upsert", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.CourtPlotUpsertApi));
            routes.Post("/court/world-action/validate", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.CourtWorldActionValidateApi));
            routes.Post("/court/diplomacy/propose", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.CourtDiplomacyProposeApi));
            routes.Post("/court/supply/validate", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.CourtSupplyValidateApi));
            routes.Post("/court/native/validate", request => CourtService.CapitalCourtApi(request.JsonBody, CourtService.CourtNativeActionValidateApi));
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Get("/court/tests", request => CourtService.RunCourtSelfTests());
#endif
        }
    }
}
