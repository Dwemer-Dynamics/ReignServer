using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the Portraits module, in their former router order (see RouteTable.cs).
        // Legacy(...) routes are still served by the if/else chain in HandleClient.
        private static void RegisterPortraitsRoutes(ReignRouteTable routes)
        {
            routes.PostPrefix("/chat-view/", StringComparison.Ordinal, request => ChatViewApi(request.JsonBody, request.Path.Substring("/chat-view/".Length)));
            routes.Post("/tavern-house/scene", request => RunInteractiveRequest(() => TavernHouseGenerateScene(request.JsonBody)));
            routes.Get("/portraits/vision/models", request => PortraitVisionModelCatalog(request.Query.TryGetValue("provider", out string visionProvider) ? visionProvider : "Codex"));
            routes.PostPrefix("/portraits/outfit/", StringComparison.Ordinal, request => OutfitChangeApi(request.JsonBody, request.Path.Substring("/portraits/outfit/".Length)));
            routes.Post("/portraits/generate", request => PortraitGenerate(request.JsonBody));
            routes.Post("/portraits/generate/status", request => PortraitGenerationStatus(request.JsonBody));
            routes.Get("/portraits/shared-generation/catalog", request => SharedPortraitCatalogApi());
            routes.Post("/portraits/shared-generation/open-folder", request => OpenSharedPortraitCacheFolderApi());
            routes.Post("/portraits/shared-generation/generate-one", request => GenerateOneSharedPortraitApi(request.JsonBody));
            routes.Post("/portraits/shared-generation/delete-one", request => DeleteOneSharedPortraitApi(request.JsonBody));
            routes.Post("/portraits/shared-generation/build-missing", request => StartMissingSharedPortraitBuildApi());
            routes.Get("/portraits/shared-generation/status", request => SharedPortraitGenerationStatusApi());
            routes.Post("/portraits/shared-generation/cancel", request => CancelSharedPortraitGenerationApi());
            routes.Post("/portraits/derivatives/build-missing", request => StartPortraitDerivativeBuildApi());
            routes.Post("/portraits/derivatives/rebuild-fallback", request => StartPortraitDerivativeFallbackRepairApi());
            routes.Get("/portraits/derivatives/status", request => PortraitDerivativeBuildStatusApi());
        }
    }
}
