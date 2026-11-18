using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the Dialogue module, in their former router order (see RouteTable.cs).
        private static void RegisterDialogueRoutes(ReignRouteTable routes)
        {
            routes.Get("/memory/background/status", request => MemoryBackgroundStatusApi(request.Query));
            routes.Get("/llm/prompt-cache/status", request => PromptCacheStatusApi());
            routes.Post("/llm/prompt-cache/probe", request => PromptCacheProbeApi(request.JsonBody));
            routes.Get("/audit/prompt", request => ReadPromptEvidenceApi(request.Query));
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Post("/tests/npc-dialogue/request", request => NpcDialogueAuditRequestApi(request.JsonBody));
            routes.Post("/tests/npc-dialogue/request/poll", request => NpcDialogueAuditRequestPollApi(request.JsonBody));
            routes.Post("/tests/npc-dialogue/request/ack", request => NpcDialogueAuditRequestAckApi(request.JsonBody));
            routes.Post("/tests/npc-dialogue/start", request => NpcDialogueAuditStartApi(request.JsonBody));
            routes.Post("/tests/npc-dialogue/turn", request => NpcDialogueAuditTurnApi(request.JsonBody));
            routes.Post("/tests/npc-dialogue/scene", request => NpcDialogueAuditSceneApi(request.JsonBody));
            routes.Post("/tests/npc-dialogue/finish", request => NpcDialogueAuditFinishApi(request.JsonBody));
            routes.Post("/tests/npc-dialogue/cancel", request => NpcDialogueAuditCancelApi(request.JsonBody));
            routes.Get("/tests/npc-dialogue/status", request => NpcDialogueAuditStatusApi(request.Query));
            routes.Post("/tests/party-dialogue/request", request => PartyDialogueAuditRequestApi(request.JsonBody));
            routes.Post("/tests/party-dialogue/request/poll", request => PartyDialogueAuditRequestPollApi(request.JsonBody));
            routes.Post("/tests/party-dialogue/request/ack", request => PartyDialogueAuditRequestAckApi(request.JsonBody));
            routes.Post("/tests/party-dialogue/start", request => PartyDialogueAuditStartApi(request.JsonBody));
            routes.Post("/tests/party-dialogue/turn", request => PartyDialogueAuditTurnApi(request.JsonBody));
            routes.Post("/tests/party-dialogue/scene", request => PartyDialogueAuditSceneApi(request.JsonBody));
            routes.Post("/tests/party-dialogue/finish", request => PartyDialogueAuditFinishApi(request.JsonBody));
            routes.Post("/tests/party-dialogue/cancel", request => PartyDialogueAuditCancelApi(request.JsonBody));
            routes.Get("/tests/party-dialogue/status", request => PartyDialogueAuditStatusApi(request.Query));
            routes.Post("/tests/final-gauntlet/catalog/build", request => FinalGauntletCatalogBuildApi(request.JsonBody));
            routes.Post("/tests/final-gauntlet/state/fingerprint", request => FinalGauntletStateFingerprintApi(request.JsonBody));
            routes.Get("/tests/final-gauntlet/catalog", request => FinalGauntletCatalogApi(request.Query));
            routes.Post("/tests/final-gauntlet/run/start", request => FinalGauntletRunStartApi(request.JsonBody));
            routes.Post("/tests/final-gauntlet/run/promote", request => FinalGauntletPromoteApi(request.JsonBody));
            routes.Post("/tests/final-gauntlet/run/resume", request => FinalGauntletResumeApi(request.JsonBody));
            routes.Post("/tests/final-gauntlet/run/pause", request => FinalGauntletPauseApi(request.JsonBody));
            routes.Post("/tests/final-gauntlet/case/lease", request => FinalGauntletLeaseApi(request.JsonBody));
            routes.Post("/tests/final-gauntlet/case/result", request => FinalGauntletResultApi(request.JsonBody));
            routes.Post("/tests/final-gauntlet/action/evidence", request => FinalGauntletActionEvidenceApi(request.JsonBody));
            routes.Post("/tests/final-gauntlet/fault/arm", request => FinalGauntletFaultArmApi(request.JsonBody));
            routes.Post("/tests/final-gauntlet/run/cancel", request => FinalGauntletCancelApi(request.JsonBody));
            routes.Get("/tests/final-gauntlet/run/status", request => FinalGauntletRunStatusApi(request.Query, false));
            routes.Get("/tests/final-gauntlet/run/report", request => FinalGauntletRunStatusApi(request.Query, true));
            routes.Get("/tests/final-gauntlet/review-pack", request => FinalGauntletReviewPackApi(request.Query));
            routes.Post("/tests/live/readiness/reset", request => ConversationReadinessResetApi(request.JsonBody));
            routes.Post("/tests/live/readiness/marker", request => ConversationReadinessMarkerApi(request.JsonBody));
            routes.Post("/tests/live/readiness/migrate-build", request => ConversationReadinessMigrateBuildApi(request.JsonBody));
            routes.Post("/tests/live/readiness/roster", request => ConversationReadinessRosterApi(request.JsonBody));
            routes.Get("/tests/live/readiness", request => ConversationReadinessStatusApi(request.Query));
            routes.Post("/tests/live/readiness/evaluate-run", request => ConversationReadinessEvaluateRunApi(request.JsonBody));
            routes.Post("/tests/live/fixtures/relationship-history", request =>
                ConversationQualificationRelationshipFixtureApi(
                    request.JsonBody));
            routes.Post("/tests/live/fixtures/manipulation", request =>
                ConversationQualificationManipulationFixtureApi(
                    request.JsonBody));
            routes.Post("/tests/live/fixtures/conversation-benefit", request =>
                ConversationQualificationBenefitFixtureApi(
                    request.JsonBody));
            routes.Post("/tests/replay-corpus/capture", request => ReplayCorpusCaptureApi(request.JsonBody));
            routes.Get("/tests/replay-corpus", request => ReplayCorpusListApi(request.Query));
            routes.Post("/tests/replay-corpus/evaluate", request => ReplayCorpusEvaluateApi(request.JsonBody));
#endif
            routes.Post("/world-history/lie-check", request => WorldHistoryLieCheckApi(request.JsonBody));
            routes.Get("/world-history/lie-checks", request => WorldHistoryLieChecksApi(request.Query));
            routes.Post("/memory/conversation/start", request => ConversationStartApi(request.JsonBody));
            routes.Post("/memory/conversation/finish", request => ConversationFinishApi(request.JsonBody));
            routes.Post("/memory/conversation/recover", request => ConversationRecoverApi(request.JsonBody));
            routes.Get("/memory/embeddings/status", request => SemanticMemoryStatusApi(request.Query));
            routes.Post("/memory/embeddings/reindex", request => SemanticMemoryReindexApi(request.JsonBody));
            routes.Post("/memory/embeddings/retry-failed", request => SemanticMemoryRetryFailedApi(request.JsonBody));
            routes.Post("/conversation-scene/hourly-tick", request => ConversationSceneHourlyTickApi(request.JsonBody));
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Post("/gossip/test", request => PersonalGossipTestApi(request.JsonBody));
#endif
            routes.Post("/gossip/tick", request => PersonalGossipTickApi(request.JsonBody));
            routes.PostPrefix("/communications/", StringComparison.Ordinal, request => CommunicationApi(request.Path.Substring("/communications/".Length), request.JsonBody));
            routes.Get("/communications/status", request => CommunicationApi("status", request.Query.ToDictionary(pair => pair.Key, pair => (object)pair.Value)));
            routes.Get("/gossip/status", request => PersonalGossipStatusApi(request.Query));
            routes.Post("/conversation-scene/formal-outfits/scan", request => RunInteractiveRequest(() => ScanFormalOutfitsApi(request.JsonBody)));
            routes.PostPrefix("/party-banter/", StringComparison.Ordinal, request => PartyBanterApi(request.Path.Substring("/party-banter/".Length), request.JsonBody));
            routes.Post("/party-chat/turn-plan", request => RunInteractiveRequest(() => GroupConversationFloorApi(request.JsonBody)));
            routes.Get(Reign.Core.Contracts.Actions.TerminalActionReportProtocol.ProposedRoute, request => TerminalActionReportPreflightApi(request.Query));
            routes.Post(Reign.Core.Contracts.Actions.TerminalActionReportProtocol.ProposedRoute, request => TerminalActionReportApi(request.JsonBody));
            routes.Post(IndividualViewInsertionRoute, request => IndividualViewInsertionApi(request.JsonBody));
        }
    }
}
