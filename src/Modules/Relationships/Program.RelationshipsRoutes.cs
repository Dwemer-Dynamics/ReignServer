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
            routes.Post("/political-pressure/evaluate", request => RelationshipsService.EvaluatePoliticalIncidentsCombined(request.JsonBody));
            routes.Get("/clan-conflicts/notices", request => RelationshipsService.NextClanConflictNotices(request.Query));
            routes.Post("/clan-conflicts/acknowledge", request => RelationshipsService.AcknowledgeClanConflictNotice(request.JsonBody));
            routes.Post("/relationships/evaluate", request => RelationshipsService.RelationshipEvaluateApi(request.JsonBody));
            routes.Post("/relationships/conversation/evaluate", request => RelationshipsService.ConversationRelationshipEvaluateApi(request.JsonBody));
            routes.Post("/relationships/conversation/native-receipt", request => RelationshipsService.ConversationRelationshipNativeReceiptApi(request.JsonBody));
            routes.Get("/relationships/conversation/recent", request => RelationshipsService.ConversationRelationshipRecentApi(request.Query));
            routes.Post("/relationships/context", request => RelationshipsService.RelationshipContextApi(request.JsonBody));
            routes.Post("/relationships/effective/batch", request => RelationshipsService.EffectiveAttitudeBatchApi(request.JsonBody));
            routes.Post("/relationships/history/ensure", request => RelationshipsService.SharedRelationshipHistoryEnsureApi(request.JsonBody));
            {
                Func<HttpRequest, object> handler = request => RelationshipsService.SharedRelationshipHistoryQueryApi(
                    request.Method == "POST"
                        ? request.JsonBody
                        : request.Query.ToDictionary(
                            pair => pair.Key, pair => (object)pair.Value,
                            StringComparer.OrdinalIgnoreCase));
                routes.Get("/relationships/history/query", handler);
                routes.Post("/relationships/history/query", handler);
            }
            routes.Post("/relationships/developments/poll", request => RelationshipsService.RetiredRelationshipMechanicResult("relationship_developments"));
            routes.Post("/relationships/developments/resolve", request => RelationshipsService.RetiredRelationshipMechanicResult("relationship_developments"));
            routes.Post("/relationships/director/snapshot", request => RelationshipsService.RelationshipDirectorSnapshotApi(request.JsonBody));
            routes.Post("/relationships/director/run", request => RelationshipsService.RelationshipDirectorRunApi(request.JsonBody));
            routes.Post("/relationships/director/poll_actions", request => RelationshipsService.RelationshipDirectorPollActionsApi(request.JsonBody));
            routes.Post("/relationships/director/report_action", request => RelationshipsService.RelationshipDirectorReportActionApi(request.JsonBody));
            routes.Post("/relationships/director/report_actions", request => RelationshipsService.RelationshipDirectorReportActionsApi(request.JsonBody));
            routes.Post("/relationships/director/status", request => RelationshipsService.RelationshipDirectorStatusApi(request.JsonBody));
            routes.Post("/relationships/ambient/snapshot", request => RelationshipsService.MbtiRelationshipSnapshotApi(request.JsonBody));
            routes.Post("/relationships/initialization/seed", request => RelationshipsService.CampaignOpeningRelationshipSeedApi(request.JsonBody));
            routes.Post("/relationships/ambient/native_sync/report", request => RelationshipsService.RelationshipNativeSyncReportApi(request.JsonBody));
            routes.Post("/relationships/ambient/native_sync/pull", request => RelationshipsService.RelationshipNativeTargetPullApi(request.JsonBody));
            routes.Post("/relationships/ambient/native_sync/receipts", request => RelationshipsService.RelationshipNativeTargetReceiptsApi(request.JsonBody));
            {
                Func<HttpRequest, object> handler = request => RelationshipsService.MbtiRelationshipStatusApi(RelationshipStatusPayload(request));
                routes.Get("/relationships/ambient/status", handler);
                routes.Post("/relationships/ambient/status", handler);
            }
            routes.Post("/relationships/simulation/census/status", request => RelationshipsService.RetiredRelationshipMechanicResult("legacy_relationship_simulation"));
            routes.Post("/relationships/simulation/start", request => RelationshipsService.RetiredRelationshipMechanicResult("legacy_relationship_simulation"));
            routes.Post("/relationships/simulation/stop", request => RelationshipsService.RetiredRelationshipMechanicResult("legacy_relationship_simulation"));
            routes.Post("/relationships/simulation/status", request => RelationshipsService.RetiredRelationshipMechanicResult("legacy_relationship_simulation"));
            routes.Post("/family/verify_conception_attempt", request => RelationshipsService.VerifyPlayerConceptionAttemptApi(request.JsonBody));
            routes.Post("/family/pregnancy/sync", request => RelationshipsService.FamilyPregnancySyncApi(request.JsonBody));
            routes.Post("/family/conception/report", request => RelationshipsService.FamilyConceptionReportApi(request.JsonBody));
            routes.Post("/family/parentage/reveal", request => RelationshipsService.FamilyParentageRevealApi(request.JsonBody));
            routes.Post("/correspondence/threads", request => RelationshipsService.CorrespondenceThreadsApi(request.JsonBody));
            routes.Post("/correspondence/send", request => RelationshipsService.CorrespondenceSendApi(request.JsonBody));
            routes.Post("/correspondence/tick", request => RelationshipsService.CorrespondenceTickApi(request.JsonBody));
            routes.Post("/approaches/evaluate", request => RelationshipsService.PlayerApproachEvaluateApi(request.JsonBody));
            routes.Post("/approaches/decision", request => RelationshipsService.PlayerApproachDecisionApi(request.JsonBody));
            routes.Post("/correspondence/autonomous/candidates", request => RelationshipsService.AutonomousCorrespondenceCandidatesApi(request.JsonBody));
            routes.Post("/correspondence/autonomous/evaluate", request => RelationshipsService.AutonomousCorrespondenceEvaluateApi(request.JsonBody));
            routes.Post("/correspondence/read", request => RelationshipsService.CorrespondenceReadApi(request.JsonBody));
            routes.Post("/notables/mbti/initialize", request => RelationshipsService.NotableMbtiInitializeApi(request.JsonBody));
            routes.Post("/notables/mbti/confirm-batch", request => RelationshipsService.NotableMbtiConfirmBatchApi(request.JsonBody));
            routes.PostPrefix("/rulers/", StringComparison.Ordinal, request => RelationshipsService.RulerRelationsApi(request.Path.Substring("/rulers/".Length), request.JsonBody));
        }
    }
}
