using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the Characters module, in their former router order (see RouteTable.cs).
        // Legacy(...) routes are still served by the if/else chain in HandleClient.
        private static void RegisterCharactersRoutes(ReignRouteTable routes)
        {
            routes.Get("/character-editor/portrait", (request, route) =>
            {
                string campaignId = request.Query.ContainsKey("campaignId") ? request.Query["campaignId"] : LatestCampaignId();
                string heroId = request.Query.ContainsKey("heroStringId") ? request.Query["heroStringId"] : "";
                string variant = request.Query.ContainsKey("variant") ? SafePathSegment(request.Query["variant"], "active") : "active";
                WriteFile(route.Stream, ResolveCharacterEditorPortraitPath(campaignId, heroId, variant), "image/png");
                LogRequest(request, 200);
                return RouteResponseWritten;
            });
#if !REIGN_EXCLUDE_COURT
            routes.Post("/government/political-profiles/sync", request => PoliticalProfilesSync(request.JsonBody));
#endif
            routes.Post("/identity/encounter", request => IdentityEncounterApi(request.JsonBody));
            routes.Post("/identity/introduction", request => IdentityIntroductionApi(request.JsonBody));
            routes.Post("/identity/query", request => IdentityQueryApi(request.JsonBody));
            routes.Post("/identity/reset", request => IdentityResetApi(request.JsonBody));
            routes.Post("/identity/debug/know-everyone", request => IdentityDebugKnowEveryoneApi(request.JsonBody));
            routes.Get("/characters/profile-library/status", request => CharacterProfileLibraryStatusApi());
            routes.Post("/characters/profile-library/action", request => CharacterProfileLibraryActionApi(request.JsonBody));
            routes.Legacy("POST", "/character-editor/list");
            routes.Post("/character-editor/load", request => CharacterEditorLoadApi(request.JsonBody));
            routes.Get("/characters/narrative", request => InspectCharacterNarrative(request.Query));
            routes.Post("/character-editor/construct", request => CharacterEditorConstructApi(request.JsonBody));
            routes.Post("/character-editor/save", request => CharacterEditorSaveApi(request.JsonBody));
            routes.Post("/character-editor/rollback", request => CharacterEditorRollbackApi(request.JsonBody));
            routes.Post("/character-editor/portrait/upload", request => CharacterEditorPortraitUploadApi(request.JsonBody));
            routes.Post("/character-editor/portrait/select", request => CharacterEditorPortraitSelectApi(request.JsonBody));
            routes.Post("/character-editor/native/poll", request => CharacterEditorNativePollApi(request.JsonBody));
            routes.Post("/character-editor/native/report", request => CharacterEditorNativeReportApi(request.JsonBody));
            routes.Post("/notables/profiles/prewarm", request => PrewarmTownNotableProfiles(request.JsonBody));
            routes.PostPrefix("/npc-lives/", StringComparison.Ordinal, request => NpcLivesApi(request.Path.Substring("/npc-lives/".Length), request.JsonBody));
            routes.Post("/tavern-house/respond", request => RunInteractiveRequest(() => TavernHouseRespond(request.JsonBody)));
            routes.Post("/tavern-house/turn-plan", request => RunInteractiveRequest(() => TavernHouseTurnPlanApi(request.JsonBody)));
            routes.Post("/tavern-house/confirm", request => TavernHouseConfirmVisit(request.JsonBody));
            routes.Post("/tavern-house/close", request => TavernHouseCloseVisit(request.JsonBody));
        }
    }
}
