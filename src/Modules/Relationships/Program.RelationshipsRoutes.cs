using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the Relationships module, in their former router order (see RouteTable.cs).
        private static void RegisterRelationshipsRoutes(ReignRouteTable routes)
        {
            routes.Post("/political-pressure/evaluate", request => EvaluatePoliticalIncidentsCombined(request.JsonBody));
            routes.Get("/clan-conflicts/notices", request => NextClanConflictNotices(request.Query));
            routes.Post("/clan-conflicts/acknowledge", request => AcknowledgeClanConflictNotice(request.JsonBody));
            routes.Post("/relationships/evaluate", request => RelationshipEvaluateApi(request.JsonBody));
            routes.Post("/relationships/conversation/evaluate", request => ConversationRelationshipEvaluateApi(request.JsonBody));
            routes.Post("/relationships/conversation/native-receipt", request => ConversationRelationshipNativeReceiptApi(request.JsonBody));
            routes.Get("/relationships/conversation/recent", request => ConversationRelationshipRecentApi(request.Query));
            routes.Post("/relationships/context", request => RelationshipContextApi(request.JsonBody));
            routes.Post("/relationships/effective/batch", request => EffectiveAttitudeBatchApi(request.JsonBody));
            routes.Post("/relationships/history/ensure", request => SharedRelationshipHistoryEnsureApi(request.JsonBody));
            {
                Func<HttpRequest, object> handler = request => SharedRelationshipHistoryQueryApi(
                    request.Method == "POST"
                        ? request.JsonBody
                        : request.Query.ToDictionary(
                            pair => pair.Key, pair => (object)pair.Value,
                            StringComparer.OrdinalIgnoreCase));
                routes.Get("/relationships/history/query", handler);
                routes.Post("/relationships/history/query", handler);
            }
            routes.Post("/relationships/developments/poll", request => RetiredRelationshipMechanicResult("relationship_developments"));
            routes.Post("/relationships/developments/resolve", request => RetiredRelationshipMechanicResult("relationship_developments"));
            routes.Post("/relationships/director/snapshot", request => RelationshipDirectorSnapshotApi(request.JsonBody));
            routes.Post("/relationships/director/run", request => RelationshipDirectorRunApi(request.JsonBody));
            routes.Post("/relationships/director/poll_actions", request => RelationshipDirectorPollActionsApi(request.JsonBody));
            routes.Post("/relationships/director/report_action", request => RelationshipDirectorReportActionApi(request.JsonBody));
            routes.Post("/relationships/director/report_actions", request => RelationshipDirectorReportActionsApi(request.JsonBody));
            routes.Post("/relationships/director/status", request => RelationshipDirectorStatusApi(request.JsonBody));
            routes.Post("/relationships/ambient/snapshot", request => MbtiRelationshipSnapshotApi(request.JsonBody));
            routes.Post("/relationships/initialization/seed", request => CampaignOpeningRelationshipSeedApi(request.JsonBody));
            routes.Post("/relationships/ambient/native_sync/report", request => RelationshipNativeSyncReportApi(request.JsonBody));
            routes.Post("/relationships/ambient/native_sync/pull", request => RelationshipNativeTargetPullApi(request.JsonBody));
            routes.Post("/relationships/ambient/native_sync/receipts", request => RelationshipNativeTargetReceiptsApi(request.JsonBody));
            {
                Func<HttpRequest, object> handler = request => MbtiRelationshipStatusApi(RelationshipStatusPayload(request));
                routes.Get("/relationships/ambient/status", handler);
                routes.Post("/relationships/ambient/status", handler);
            }
            routes.Post("/relationships/simulation/census/status", request => RetiredRelationshipMechanicResult("legacy_relationship_simulation"));
            routes.Post("/relationships/simulation/start", request => RetiredRelationshipMechanicResult("legacy_relationship_simulation"));
            routes.Post("/relationships/simulation/stop", request => RetiredRelationshipMechanicResult("legacy_relationship_simulation"));
            routes.Post("/relationships/simulation/status", request => RetiredRelationshipMechanicResult("legacy_relationship_simulation"));
            routes.Post("/family/verify_conception_attempt", request => VerifyPlayerConceptionAttemptApi(request.JsonBody));
            routes.Post("/family/pregnancy/sync", request => FamilyPregnancySyncApi(request.JsonBody));
            routes.Post("/family/conception/report", request => FamilyConceptionReportApi(request.JsonBody));
            routes.Post("/family/parentage/reveal", request => FamilyParentageRevealApi(request.JsonBody));
            routes.Post("/correspondence/threads", request => CorrespondenceThreadsApi(request.JsonBody));
            routes.Post("/correspondence/send", request => CorrespondenceSendApi(request.JsonBody));
            routes.Post("/correspondence/tick", request => CorrespondenceTickApi(request.JsonBody));
            routes.Post("/approaches/evaluate", request => PlayerApproachEvaluateApi(request.JsonBody));
            routes.Post("/approaches/decision", request => PlayerApproachDecisionApi(request.JsonBody));
            routes.Post("/correspondence/autonomous/candidates", request => AutonomousCorrespondenceCandidatesApi(request.JsonBody));
            routes.Post("/correspondence/autonomous/evaluate", request => AutonomousCorrespondenceEvaluateApi(request.JsonBody));
            routes.Post("/correspondence/read", request => CorrespondenceReadApi(request.JsonBody));
            routes.Post("/notables/mbti/initialize", request => NotableMbtiInitializeApi(request.JsonBody));
            routes.Post("/notables/mbti/confirm-batch", request => NotableMbtiConfirmBatchApi(request.JsonBody));
            routes.PostPrefix("/rulers/", StringComparison.Ordinal, request => RulerRelationsApi(request.Path.Substring("/rulers/".Length), request.JsonBody));
        }
    }
}
