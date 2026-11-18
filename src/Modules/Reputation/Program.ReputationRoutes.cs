using System;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the Reputation module, in their former router order (see RouteTable.cs).
        // Legacy(...) routes are still served by the if/else chain in HandleClient.
        private static void RegisterReputationRoutes(ReignRouteTable routes)
        {
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Legacy("GET", "/tests/social-balance/manifest");
            routes.Legacy("POST", "/tests/social-balance/prepare");
            routes.Legacy("POST", "/tests/social-balance/case/result");
            routes.Legacy("POST", "/tests/social-balance/snapshot");
            routes.Legacy("POST", "/tests/social-balance/affinity/set");
            routes.Legacy("POST", "/tests/social-balance/affair/candidates");
            routes.Legacy("GET", "/tests/social-balance/status");
            routes.Legacy("GET", "/tests/social-balance/export");
#endif
            routes.Legacy("POST", "/rumors/submit");
            routes.Legacy("POST", "/rumors/status");
            routes.Legacy("POST", "/rumors/correct");
            routes.Legacy("POST", "/social/catalog");
            routes.Legacy("GET", "/social/catalog");
            routes.Post("/character-editor/social/rumor/add", request => CharacterEditorSocialRumorAddApi(request.JsonBody));
            routes.Post("/character-editor/social/reputation/add", request => CharacterEditorSocialReputationAddApi(request.JsonBody));
            routes.Post("/character-editor/social/reputation/retry-reason", request => CharacterEditorSocialReasonRetryApi(request.JsonBody));
        }

        private static bool TryHandleReputationRoute(HttpRequest request, out object response)
        {
            response = null;
            if (request.Method == "POST")
            {
                switch (request.Path)
                {
                    case "/rumors/submit": response = RegisterSocialOccurrenceApi(request.JsonBody); return true;
                    case "/rumors/status": response = SocialCharacterStatusApi(request.JsonBody); return true;
                    case "/rumors/correct": response = CorrectSocialOccurrenceApi(request.JsonBody); return true;
                    case "/social/catalog": response = SocialCatalogUpdateApi(request.JsonBody); return true;
                }
            }
            if (request.Method == "GET" && request.Path == "/social/catalog")
            {
                response = SocialCatalogApi(request.Query.ToDictionary(
                    pair => pair.Key, pair => (object)pair.Value,
                    StringComparer.OrdinalIgnoreCase));
                return true;
            }
            return false;
        }
    }
}
