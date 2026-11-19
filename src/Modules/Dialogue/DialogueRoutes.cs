using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static class DialogueRoutes
    {
        // HTTP routes owned by the Dialogue module, in their former router order (see RouteTable.cs).
        internal static void RegisterDialogueRoutes(Program.ReignRouteTable routes)
        {
            routes.Get("/memory/background/status", request => DialogueService.MemoryBackgroundStatusApi(request.Query));
            routes.Get("/llm/prompt-cache/status", request => DialogueService.PromptCacheStatusApi());
            routes.Post("/llm/prompt-cache/probe", request => DialogueService.PromptCacheProbeApi(request.JsonBody));
            routes.Get("/audit/prompt", request => DialogueService.ReadPromptEvidenceApi(request.Query));
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Post("/tests/npc-dialogue/request", request => DialogueService.NpcDialogueAuditRequestApi(request.JsonBody));
            routes.Post("/tests/npc-dialogue/request/poll", request => DialogueService.NpcDialogueAuditRequestPollApi(request.JsonBody));
            routes.Post("/tests/npc-dialogue/request/ack", request => DialogueService.NpcDialogueAuditRequestAckApi(request.JsonBody));
            routes.Post("/tests/npc-dialogue/start", request => DialogueService.NpcDialogueAuditStartApi(request.JsonBody));
            routes.Post("/tests/npc-dialogue/turn", request => DialogueService.NpcDialogueAuditTurnApi(request.JsonBody));
            routes.Post("/tests/npc-dialogue/scene", request => DialogueService.NpcDialogueAuditSceneApi(request.JsonBody));
            routes.Post("/tests/npc-dialogue/finish", request => DialogueService.NpcDialogueAuditFinishApi(request.JsonBody));
            routes.Post("/tests/npc-dialogue/cancel", request => DialogueService.NpcDialogueAuditCancelApi(request.JsonBody));
            routes.Get("/tests/npc-dialogue/status", request => DialogueService.NpcDialogueAuditStatusApi(request.Query));
            routes.Post("/tests/party-dialogue/request", request => DialogueService.PartyDialogueAuditRequestApi(request.JsonBody));
            routes.Post("/tests/party-dialogue/request/poll", request => DialogueService.PartyDialogueAuditRequestPollApi(request.JsonBody));
            routes.Post("/tests/party-dialogue/request/ack", request => DialogueService.PartyDialogueAuditRequestAckApi(request.JsonBody));
            routes.Post("/tests/party-dialogue/start", request => DialogueService.PartyDialogueAuditStartApi(request.JsonBody));
            routes.Post("/tests/party-dialogue/turn", request => DialogueService.PartyDialogueAuditTurnApi(request.JsonBody));
            routes.Post("/tests/party-dialogue/scene", request => DialogueService.PartyDialogueAuditSceneApi(request.JsonBody));
            routes.Post("/tests/party-dialogue/finish", request => DialogueService.PartyDialogueAuditFinishApi(request.JsonBody));
            routes.Post("/tests/party-dialogue/cancel", request => DialogueService.PartyDialogueAuditCancelApi(request.JsonBody));
            routes.Get("/tests/party-dialogue/status", request => DialogueService.PartyDialogueAuditStatusApi(request.Query));
            routes.Post("/tests/final-gauntlet/catalog/build", request => DialogueService.FinalGauntletCatalogBuildApi(request.JsonBody));
            routes.Post("/tests/final-gauntlet/state/fingerprint", request => DialogueService.FinalGauntletStateFingerprintApi(request.JsonBody));
            routes.Get("/tests/final-gauntlet/catalog", request => DialogueService.FinalGauntletCatalogApi(request.Query));
            routes.Post("/tests/final-gauntlet/run/start", request => DialogueService.FinalGauntletRunStartApi(request.JsonBody));
            routes.Post("/tests/final-gauntlet/run/promote", request => DialogueService.FinalGauntletPromoteApi(request.JsonBody));
            routes.Post("/tests/final-gauntlet/run/resume", request => DialogueService.FinalGauntletResumeApi(request.JsonBody));
            routes.Post("/tests/final-gauntlet/run/pause", request => DialogueService.FinalGauntletPauseApi(request.JsonBody));
            routes.Post("/tests/final-gauntlet/case/lease", request => DialogueService.FinalGauntletLeaseApi(request.JsonBody));
            routes.Post("/tests/final-gauntlet/case/result", request => DialogueService.FinalGauntletResultApi(request.JsonBody));
            routes.Post("/tests/final-gauntlet/action/evidence", request => DialogueService.FinalGauntletActionEvidenceApi(request.JsonBody));
            routes.Post("/tests/final-gauntlet/fault/arm", request => DialogueService.FinalGauntletFaultArmApi(request.JsonBody));
            routes.Post("/tests/final-gauntlet/run/cancel", request => DialogueService.FinalGauntletCancelApi(request.JsonBody));
            routes.Get("/tests/final-gauntlet/run/status", request => DialogueService.FinalGauntletRunStatusApi(request.Query, false));
            routes.Get("/tests/final-gauntlet/run/report", request => DialogueService.FinalGauntletRunStatusApi(request.Query, true));
            routes.Get("/tests/final-gauntlet/review-pack", request => DialogueService.FinalGauntletReviewPackApi(request.Query));
            routes.Post("/tests/live/readiness/reset", request => DialogueService.ConversationReadinessResetApi(request.JsonBody));
            routes.Post("/tests/live/readiness/marker", request => DialogueService.ConversationReadinessMarkerApi(request.JsonBody));
            routes.Post("/tests/live/readiness/migrate-build", request => DialogueService.ConversationReadinessMigrateBuildApi(request.JsonBody));
            routes.Post("/tests/live/readiness/roster", request => DialogueService.ConversationReadinessRosterApi(request.JsonBody));
            routes.Get("/tests/live/readiness", request => DialogueService.ConversationReadinessStatusApi(request.Query));
            routes.Post("/tests/live/readiness/evaluate-run", request => DialogueService.ConversationReadinessEvaluateRunApi(request.JsonBody));
            routes.Post("/tests/live/fixtures/relationship-history", request =>
                DialogueService.ConversationQualificationRelationshipFixtureApi(
                    request.JsonBody));
            routes.Post("/tests/live/fixtures/manipulation", request =>
                DialogueService.ConversationQualificationManipulationFixtureApi(
                    request.JsonBody));
            routes.Post("/tests/live/fixtures/conversation-benefit", request =>
                DialogueService.ConversationQualificationBenefitFixtureApi(
                    request.JsonBody));
            routes.Post("/tests/replay-corpus/capture", request => DialogueService.ReplayCorpusCaptureApi(request.JsonBody));
            routes.Get("/tests/replay-corpus", request => DialogueService.ReplayCorpusListApi(request.Query));
            routes.Post("/tests/replay-corpus/evaluate", request => DialogueService.ReplayCorpusEvaluateApi(request.JsonBody));
#endif
            routes.Post("/world-history/lie-check", request => DialogueService.WorldHistoryLieCheckApi(request.JsonBody));
            routes.Get("/world-history/lie-checks", request => DialogueService.WorldHistoryLieChecksApi(request.Query));
            routes.Post("/memory/conversation/start", request => DialogueService.ConversationStartApi(request.JsonBody));
            routes.Post("/memory/conversation/finish", request => DialogueService.ConversationFinishApi(request.JsonBody));
            routes.Post("/memory/conversation/recover", request => DialogueService.ConversationRecoverApi(request.JsonBody));
            routes.Get("/memory/embeddings/status", request => DialogueService.SemanticMemoryStatusApi(request.Query));
            routes.Post("/memory/embeddings/reindex", request => DialogueService.SemanticMemoryReindexApi(request.JsonBody));
            routes.Post("/memory/embeddings/retry-failed", request => DialogueService.SemanticMemoryRetryFailedApi(request.JsonBody));
            routes.Post("/conversation-scene/hourly-tick", request => DialogueService.ConversationSceneHourlyTickApi(request.JsonBody));
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Post("/gossip/test", request => DialogueService.PersonalGossipTestApi(request.JsonBody));
#endif
            routes.Post("/gossip/tick", request => DialogueService.PersonalGossipTickApi(request.JsonBody));
            routes.PostPrefix("/communications/", StringComparison.Ordinal, request => DialogueService.CommunicationApi(request.Path.Substring("/communications/".Length), request.JsonBody));
            routes.Get("/communications/status", request => DialogueService.CommunicationApi("status", request.Query.ToDictionary(pair => pair.Key, pair => (object)pair.Value)));
            routes.Get("/gossip/status", request => DialogueService.PersonalGossipStatusApi(request.Query));
            routes.Post("/conversation-scene/formal-outfits/scan", request => Program.RunInteractiveRequest(() => DialogueService.ScanFormalOutfitsApi(request.JsonBody)));
            routes.PostPrefix("/party-banter/", StringComparison.Ordinal, request => DialogueService.PartyBanterApi(request.Path.Substring("/party-banter/".Length), request.JsonBody));
            routes.Post("/party-chat/turn-plan", request => Program.RunInteractiveRequest(() => DialogueService.GroupConversationFloorApi(request.JsonBody)));
            routes.Get(Reign.Core.Contracts.Actions.TerminalActionReportProtocol.ProposedRoute, request => DialogueService.TerminalActionReportPreflightApi(request.Query));
            routes.Post(Reign.Core.Contracts.Actions.TerminalActionReportProtocol.ProposedRoute, request => DialogueService.TerminalActionReportApi(request.JsonBody));
            routes.Post(DialogueService.IndividualViewInsertionRoute, request => DialogueService.IndividualViewInsertionApi(request.JsonBody));
        }
    }
}
