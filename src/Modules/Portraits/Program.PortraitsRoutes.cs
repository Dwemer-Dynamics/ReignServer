using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the Portraits module, in their former router order (see RouteTable.cs).
        private static void RegisterPortraitsRoutes(ReignRouteTable routes)
        {
            routes.PostPrefix("/chat-view/", StringComparison.Ordinal, request => PortraitsService.ChatViewApi(request.JsonBody, request.Path.Substring("/chat-view/".Length)));
            routes.Post("/tavern-house/scene", request => RunInteractiveRequest(() => PortraitsService.TavernHouseGenerateScene(request.JsonBody)));
            routes.Get("/portraits/vision/models", request => PortraitsService.PortraitVisionModelCatalog(request.Query.TryGetValue("provider", out string visionProvider) ? visionProvider : "Codex"));
            routes.PostPrefix("/portraits/outfit/", StringComparison.Ordinal, request => PortraitsService.OutfitChangeApi(request.JsonBody, request.Path.Substring("/portraits/outfit/".Length)));
            routes.Post("/portraits/generate", request => PortraitsService.PortraitGenerate(request.JsonBody));
            routes.Post("/portraits/generate/status", request => PortraitsService.PortraitGenerationStatus(request.JsonBody));
            routes.Get("/portraits/shared-generation/catalog", request => PortraitsService.SharedPortraitCatalogApi());
            routes.Post("/portraits/shared-generation/open-folder", request => PortraitsService.OpenSharedPortraitCacheFolderApi());
            routes.Post("/portraits/shared-generation/generate-one", request => PortraitsService.GenerateOneSharedPortraitApi(request.JsonBody));
            routes.Post("/portraits/shared-generation/delete-one", request => PortraitsService.DeleteOneSharedPortraitApi(request.JsonBody));
            routes.Post("/portraits/shared-generation/build-missing", request => PortraitsService.StartMissingSharedPortraitBuildApi());
            routes.Get("/portraits/shared-generation/status", request => PortraitsService.SharedPortraitGenerationStatusApi());
            routes.Post("/portraits/shared-generation/cancel", request => PortraitsService.CancelSharedPortraitGenerationApi());
            routes.Post("/portraits/derivatives/build-missing", request => PortraitsService.StartPortraitDerivativeBuildApi());
            routes.Post("/portraits/derivatives/rebuild-fallback", request => PortraitsService.StartPortraitDerivativeFallbackRepairApi());
            routes.Get("/portraits/derivatives/status", request => PortraitsService.PortraitDerivativeBuildStatusApi());
        }
    }
}
