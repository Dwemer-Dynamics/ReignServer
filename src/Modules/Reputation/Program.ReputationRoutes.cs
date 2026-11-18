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
            routes.Get("/tests/social-balance/manifest", request => ReputationService.SocialBalanceManifestApi(request.Query));
            routes.Post("/tests/social-balance/prepare", request => ReputationService.SocialBalancePrepareApi(request.JsonBody));
            routes.Post("/tests/social-balance/case/result", request => ReputationService.SocialBalanceCaseResultApi(request.JsonBody));
            routes.Post("/tests/social-balance/snapshot", request => ReputationService.SocialBalanceSnapshotApi(request.JsonBody));
            routes.Post("/tests/social-balance/affinity/set", request => ReputationService.SocialBalanceSetUnderlyingAffinityApi(request.JsonBody));
            routes.Post("/tests/social-balance/affair/candidates", request => ReputationService.SocialBalanceRankAffairCandidatesApi(request.JsonBody));
            routes.Get("/tests/social-balance/status", request => ReputationService.SocialBalanceStatusApi(request.Query));
            routes.Get("/tests/social-balance/export", request => ReputationService.SocialBalanceExportApi(request.Query));
#endif
            routes.Post("/rumors/submit", request => ReputationService.RegisterSocialOccurrenceApi(request.JsonBody));
            routes.Post("/rumors/status", request => ReputationService.SocialCharacterStatusApi(request.JsonBody));
            routes.Post("/rumors/correct", request => ReputationService.CorrectSocialOccurrenceApi(request.JsonBody));
            routes.Post("/social/catalog", request => ReputationService.SocialCatalogUpdateApi(request.JsonBody));
            routes.Get("/social/catalog", request => ReputationService.SocialCatalogApi(request.Query.ToDictionary(
                pair => pair.Key, pair => (object)pair.Value,
                StringComparer.OrdinalIgnoreCase)));
            routes.Post("/character-editor/social/rumor/add", request => ReputationService.CharacterEditorSocialRumorAddApi(request.JsonBody));
            routes.Post("/character-editor/social/reputation/add", request => ReputationService.CharacterEditorSocialReputationAddApi(request.JsonBody));
            routes.Post("/character-editor/social/reputation/retry-reason", request => ReputationService.CharacterEditorSocialReasonRetryApi(request.JsonBody));
        }
    }
}
