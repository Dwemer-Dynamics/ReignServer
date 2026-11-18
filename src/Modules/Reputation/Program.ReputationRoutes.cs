using System;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the Reputation module, in their former router order (see RouteTable.cs).
        private static void RegisterReputationRoutes(ReignRouteTable routes)
        {
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Get("/tests/social-balance/manifest", request => SocialBalanceManifestApi(request.Query));
            routes.Post("/tests/social-balance/prepare", request => SocialBalancePrepareApi(request.JsonBody));
            routes.Post("/tests/social-balance/case/result", request => SocialBalanceCaseResultApi(request.JsonBody));
            routes.Post("/tests/social-balance/snapshot", request => SocialBalanceSnapshotApi(request.JsonBody));
            routes.Post("/tests/social-balance/affinity/set", request => SocialBalanceSetUnderlyingAffinityApi(request.JsonBody));
            routes.Post("/tests/social-balance/affair/candidates", request => SocialBalanceRankAffairCandidatesApi(request.JsonBody));
            routes.Get("/tests/social-balance/status", request => SocialBalanceStatusApi(request.Query));
            routes.Get("/tests/social-balance/export", request => SocialBalanceExportApi(request.Query));
#endif
            routes.Post("/rumors/submit", request => RegisterSocialOccurrenceApi(request.JsonBody));
            routes.Post("/rumors/status", request => SocialCharacterStatusApi(request.JsonBody));
            routes.Post("/rumors/correct", request => CorrectSocialOccurrenceApi(request.JsonBody));
            routes.Post("/social/catalog", request => SocialCatalogUpdateApi(request.JsonBody));
            routes.Get("/social/catalog", request => SocialCatalogApi(request.Query.ToDictionary(
                pair => pair.Key, pair => (object)pair.Value,
                StringComparer.OrdinalIgnoreCase)));
            routes.Post("/character-editor/social/rumor/add", request => CharacterEditorSocialRumorAddApi(request.JsonBody));
            routes.Post("/character-editor/social/reputation/add", request => CharacterEditorSocialReputationAddApi(request.JsonBody));
            routes.Post("/character-editor/social/reputation/retry-reason", request => CharacterEditorSocialReasonRetryApi(request.JsonBody));
        }
    }
}
