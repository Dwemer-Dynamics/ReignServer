using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the Dialogue module, in their former router order (see RouteTable.cs).
        // Legacy(...) routes are still served by the if/else chain in HandleClient.
        private static void RegisterDialogueRoutes(ReignRouteTable routes)
        {
            routes.Legacy("GET", "/memory/background/status");
            routes.Legacy("GET", "/llm/prompt-cache/status");
            routes.Post("/llm/prompt-cache/probe", request => PromptCacheProbeApi(request.JsonBody));
            routes.Get("/audit/prompt", request => ReadPromptEvidenceApi(request.Query));
            routes.Legacy("POST", "/tests/npc-dialogue/request");
            routes.Legacy("POST", "/tests/npc-dialogue/request/poll");
            routes.Legacy("POST", "/tests/npc-dialogue/request/ack");
            routes.Legacy("POST", "/tests/npc-dialogue/start");
            routes.Legacy("POST", "/tests/npc-dialogue/turn");
            routes.Legacy("POST", "/tests/npc-dialogue/scene");
            routes.Legacy("POST", "/tests/npc-dialogue/finish");
            routes.Legacy("POST", "/tests/npc-dialogue/cancel");
            routes.Legacy("GET", "/tests/npc-dialogue/status");
            routes.Legacy("POST", "/tests/party-dialogue/request");
            routes.Legacy("POST", "/tests/party-dialogue/request/poll");
            routes.Legacy("POST", "/tests/party-dialogue/request/ack");
            routes.Legacy("POST", "/tests/party-dialogue/start");
            routes.Legacy("POST", "/tests/party-dialogue/turn");
            routes.Legacy("POST", "/tests/party-dialogue/scene");
            routes.Legacy("POST", "/tests/party-dialogue/finish");
            routes.Legacy("POST", "/tests/party-dialogue/cancel");
            routes.Legacy("GET", "/tests/party-dialogue/status");
            routes.Legacy("POST", "/tests/final-gauntlet/catalog/build");
            routes.Legacy("POST", "/tests/final-gauntlet/state/fingerprint");
            routes.Legacy("GET", "/tests/final-gauntlet/catalog");
            routes.Legacy("POST", "/tests/final-gauntlet/run/start");
            routes.Legacy("POST", "/tests/final-gauntlet/run/promote");
            routes.Legacy("POST", "/tests/final-gauntlet/run/resume");
            routes.Legacy("POST", "/tests/final-gauntlet/run/pause");
            routes.Legacy("POST", "/tests/final-gauntlet/case/lease");
            routes.Legacy("POST", "/tests/final-gauntlet/case/result");
            routes.Legacy("POST", "/tests/final-gauntlet/action/evidence");
            routes.Legacy("POST", "/tests/final-gauntlet/fault/arm");
            routes.Legacy("POST", "/tests/final-gauntlet/run/cancel");
            routes.Legacy("GET", "/tests/final-gauntlet/run/status");
            routes.Legacy("GET", "/tests/final-gauntlet/run/report");
            routes.Legacy("GET", "/tests/final-gauntlet/review-pack");
            routes.Legacy("POST", "/tests/live/readiness/reset");
            routes.Legacy("POST", "/tests/live/readiness/marker");
            routes.Legacy("POST", "/tests/live/readiness/migrate-build");
            routes.Legacy("POST", "/tests/live/readiness/roster");
            routes.Legacy("GET", "/tests/live/readiness");
            routes.Legacy("POST", "/tests/live/readiness/evaluate-run");
            routes.Legacy("POST", "/tests/live/fixtures/relationship-history");
            routes.Legacy("POST", "/tests/live/fixtures/manipulation");
            routes.Legacy("POST", "/tests/live/fixtures/conversation-benefit");
            routes.Legacy("POST", "/tests/replay-corpus/capture");
            routes.Legacy("GET", "/tests/replay-corpus");
            routes.Legacy("POST", "/tests/replay-corpus/evaluate");
            routes.Post("/world-history/lie-check", request => WorldHistoryLieCheckApi(request.JsonBody));
            routes.Get("/world-history/lie-checks", request => WorldHistoryLieChecksApi(request.Query));
            routes.Post("/memory/conversation/start", request => ConversationStartApi(request.JsonBody));
            routes.Post("/memory/conversation/finish", request => ConversationFinishApi(request.JsonBody));
            routes.Post("/memory/conversation/recover", request => ConversationRecoverApi(request.JsonBody));
            routes.Get("/memory/embeddings/status", request => SemanticMemoryStatusApi(request.Query));
            routes.Post("/memory/embeddings/reindex", request => SemanticMemoryReindexApi(request.JsonBody));
            routes.Post("/memory/embeddings/retry-failed", request => SemanticMemoryRetryFailedApi(request.JsonBody));
            routes.Post("/conversation-scene/hourly-tick", request => ConversationSceneHourlyTickApi(request.JsonBody));
            routes.Post("/gossip/test", request => PersonalGossipTestApi(request.JsonBody));
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
