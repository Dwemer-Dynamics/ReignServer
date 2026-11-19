using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static class CharactersRoutes
    {
        // HTTP routes owned by the Characters module, in their former router order (see RouteTable.cs).
        internal static void RegisterCharactersRoutes(Program.ReignRouteTable routes)
        {
            routes.Get("/character-editor/portrait", (request, route) =>
            {
                string campaignId = request.Query.ContainsKey("campaignId") ? request.Query["campaignId"] : Program.LatestCampaignId();
                string heroId = request.Query.ContainsKey("heroStringId") ? request.Query["heroStringId"] : "";
                string variant = request.Query.ContainsKey("variant") ? Program.SafePathSegment(request.Query["variant"], "active") : "active";
                Program.WriteFile(route.Stream, CharactersService.ResolveCharacterEditorPortraitPath(campaignId, heroId, variant), "image/png");
                Program.LogRequest(request, 200);
                return Program.RouteResponseWritten;
            });
#if !REIGN_EXCLUDE_COURT
            routes.Post("/government/political-profiles/sync", request => CharactersService.PoliticalProfilesSync(request.JsonBody));
#endif
            routes.Post("/identity/encounter", request => CharactersService.IdentityEncounterApi(request.JsonBody));
            routes.Post("/identity/introduction", request => CharactersService.IdentityIntroductionApi(request.JsonBody));
            routes.Post("/identity/query", request => CharactersService.IdentityQueryApi(request.JsonBody));
            routes.Post("/identity/reset", request => CharactersService.IdentityResetApi(request.JsonBody));
            routes.Post("/identity/debug/know-everyone", request => CharactersService.IdentityDebugKnowEveryoneApi(request.JsonBody));
            routes.Get("/characters/profile-library/status", request => CharactersService.CharacterProfileLibraryStatusApi());
            routes.Post("/characters/profile-library/action", request => CharactersService.CharacterProfileLibraryActionApi(request.JsonBody));
            routes.Post("/character-editor/list", request => CharactersService.CharacterEditorListApi(request.JsonBody));
            routes.Post("/character-editor/load", request => CharactersService.CharacterEditorLoadApi(request.JsonBody));
            routes.Get("/characters/narrative", request => CharactersService.InspectCharacterNarrative(request.Query));
            routes.Post("/character-editor/construct", request => CharactersService.CharacterEditorConstructApi(request.JsonBody));
            routes.Post("/character-editor/save", request => CharactersService.CharacterEditorSaveApi(request.JsonBody));
            routes.Post("/character-editor/rollback", request => CharactersService.CharacterEditorRollbackApi(request.JsonBody));
            routes.Post("/character-editor/portrait/upload", request => CharactersService.CharacterEditorPortraitUploadApi(request.JsonBody));
            routes.Post("/character-editor/portrait/select", request => CharactersService.CharacterEditorPortraitSelectApi(request.JsonBody));
            routes.Post("/character-editor/native/poll", request => CharactersService.CharacterEditorNativePollApi(request.JsonBody));
            routes.Post("/character-editor/native/report", request => CharactersService.CharacterEditorNativeReportApi(request.JsonBody));
            routes.Post("/notables/profiles/prewarm", request => CharactersService.PrewarmTownNotableProfiles(request.JsonBody));
            routes.PostPrefix("/npc-lives/", StringComparison.Ordinal, request => CharactersService.NpcLivesApi(request.Path.Substring("/npc-lives/".Length), request.JsonBody));
            routes.Post("/tavern-house/respond", request => Program.RunInteractiveRequest(() => CharactersService.TavernHouseRespond(request.JsonBody)));
            routes.Post("/tavern-house/turn-plan", request => Program.RunInteractiveRequest(() => CharactersService.TavernHouseTurnPlanApi(request.JsonBody)));
            routes.Post("/tavern-house/confirm", request => CharactersService.TavernHouseConfirmVisit(request.JsonBody));
            routes.Post("/tavern-house/close", request => CharactersService.TavernHouseCloseVisit(request.JsonBody));
        }
    }
}
