using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the Platform module, in their former router order (see RouteTable.cs).
        // Legacy(...) routes are still served by the if/else chain in HandleClient.
        private static void RegisterPlatformRoutes(ReignRouteTable routes)
        {
            // Test and verification tooling isolates its own campaigns, so it runs without the campaign lease;
            // live game tests (/tests/live and below) act on the active campaign and stay gated.
            routes.CampaignGateExemptArea("/tests/", "/tests/live");
            routes.CampaignGateExemptArea("/verification/");
            // The Control Center shell, its assets and /health read no campaign state. A Save Sync replacement must
            // not hide service health or the static shell from systemd and the launcher while campaign requests
            // remain gated.
            {
                RouteHandler handler = (request, route) =>
                {
                    WriteHtml(route.Stream, ControlCenterHtml());
                    LogRequest(request, 200);
                    return RouteResponseWritten;
                };
                routes.Get("/", handler).WithoutCampaignGate();
                routes.Get("/index.html", handler).WithoutCampaignGate();
            }
            routes.Get("/assets/reign-icon.webp", (request, route) =>
            {
                WriteFile(route.Stream, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "assets", "reign-icon.webp"), "image/webp");
                LogRequest(request, 200);
                return RouteResponseWritten;
            }).WithoutCampaignGate();
            routes.Get("/assets/ReignLogo.png", (request, route) =>
            {
                WriteFile(route.Stream, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "assets", "ReignLogo.png"), "image/png");
                LogRequest(request, 200);
                return RouteResponseWritten;
            }).WithoutCampaignGate();
            routes.Legacy("GET", "/health").WithoutCampaignGate();
            routes.Post("/api/activate", request => ActivateRunningServer(request.JsonBody));
            routes.Get("/api/settings", request => SettingsForUi(LoadSettings()));
            routes.Get("/api/gameplay-options", request => ReignXpOptionsForClient());
            routes.Get("/api/global-keys", request => GlobalKeysStatus(request.Query.TryGetValue("slot", out string onlySlot) && GlobalKeySlots.Contains(onlySlot) ? onlySlot : null));
            {
                Func<HttpRequest, object> handler = request => !GlobalKeyRequestAllowed(request) ? GlobalKeyFail("forbidden")
                    : request.Path.EndsWith("/save", StringComparison.Ordinal) ? SaveGlobalKey(request.JsonBody) : UnbindGlobalKey(request.JsonBody);
                routes.Post("/api/global-keys/save", handler);
                routes.Post("/api/global-keys/unbind", handler);
            }
            routes.Post("/api/settings", request => SaveSettings(request.JsonBody));
            // Stop reads no campaign state and may synchronously settle never-started background releases, so it
            // must run without campaign ownership.
            routes.Post("/api/shutdown", request => RequestServerShutdown()).WithoutCampaignGate();
            routes.Post("/api/settings/clear-key", request => ClearSettingsKey(request.JsonBody, request));
            routes.Legacy("GET", "/api/diagnostics");
            routes.Post("/api/diagnostics/test-llm", request => TestLlmKey(request.JsonBody));
            routes.Get("/api/codex/status", request => CodexStatusApi());
            routes.Post("/api/codex/start", request => StartCodexAppServerApi());
            routes.Post("/api/codex/stop", request => StopCodexAppServerApi());
            routes.Post("/api/codex/login/start", request => StartCodexLoginApi(request.JsonBody));
            routes.Post("/api/codex/logout", request => CodexLogoutApi());
            routes.Post("/api/codex/models/refresh", request => RefreshCodexModelsApi());
            routes.Post("/api/codex/models/verify", request => CodexVerifyModelApi(request.JsonBody));
            routes.Legacy("GET", "/api/telemetry/status");
            routes.Legacy("GET", "/api/provider/status");
            routes.Legacy("GET", "/api/background/status");
            routes.Legacy("GET", "/api/prompts");
            routes.Legacy("POST", "/api/prompts");
            routes.Post("/api/castle-chat/prompts", request => CastleChatPromptsApi(request.JsonBody));
            routes.Legacy("GET", "/api/logs");
            routes.Get("/api/diagnostics/conversations", request => ConversationDiagnosticsListApi(request.Query));
            routes.Get("/api/diagnostics/conversations/trace", request => ConversationDiagnosticsTraceApi(request.Query));
            routes.Get("/api/diagnostics/conversations/payload", request => ConversationDiagnosticsPayloadApi(request.Query));
            routes.Get("/api/diagnostics/conversations/capture", request => ConversationDiagnosticsCaptureApi());
            routes.Post("/api/diagnostics/conversations/capture", request => ConversationDiagnosticsCaptureApi(request.JsonBody));
            routes.Post("/api/diagnostics/conversations/clear", request => ConversationDiagnosticsClearApi(request.JsonBody));
            routes.Get("/api/diagnostics/conversations/export", (request, route) =>
            {
                WriteConversationDiagnosticsExport(route.Stream, request.Query);
                LogRequest(request, 200);
                return RouteResponseWritten;
            });
            routes.Post("/api/logs/clear", request => ClearLogs(request.JsonBody));
            routes.Get("/api/logs/export", (request, route) =>
            {
                WriteLogExport(route.Stream, request.Query);
                LogRequest(request, 200);
                return RouteResponseWritten;
            });
            routes.Post("/api/support-bundle", (request, route) =>
            {
                WriteSupportBundle(route.Stream, request.JsonBody);
                LogRequest(request, 200);
                return RouteResponseWritten;
            });
            routes.Post("/audit/ingest", request => AuditIngest(request.JsonBody));
            routes.Legacy("GET", "/audit/query");
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Legacy("GET", "/tests/cases");
            routes.Legacy("POST", "/tests/cases/save");
            routes.Legacy("POST", "/tests/run");
            routes.Legacy("POST", "/tests/run-suite");
            routes.Legacy("POST", "/tests/capture-from-correlation");
            routes.Legacy("GET", "/tests/runs");
            routes.Legacy("POST", "/verification/run");
            routes.Legacy("GET", "/verification/status");
            routes.Legacy("POST", "/verification/cancel");
            routes.Legacy("GET", "/verification/results");
            routes.Legacy("POST", "/verification/replay");
#endif
            routes.Post("/events/ingest", request => IngestEvent(request.JsonBody));
            routes.Post("/initialization/readiness/seal", request => InitializationReadinessSealApi(request.JsonBody));
            routes.Post("/initialization/readiness/status", request => InitializationReadinessStatusApi(request.JsonBody));
            routes.Post("/initialization/readiness/retry", request => InitializationReadinessRetryApi(request.JsonBody));
            routes.Post("/memory/event", request => MemoryEventApi(request.JsonBody));
            routes.Post("/memory/conversation_finished", request => string.IsNullOrWhiteSpace(ReadFirstString(request.JsonBody, "sessionId", "conversationSessionId", "conversationId"))
                ? MemoryConversationFinishedApi(request.JsonBody)
                : ConversationFinishApi(request.JsonBody));
            routes.Post("/memory/action_result", request => MemoryActionResultApi(request.JsonBody));
            routes.Post("/memory/build_context", request => MemoryBuildContextApi(request.JsonBody));
            routes.Post("/memory/consolidate", request => MemoryConsolidateApi(request.JsonBody));
            routes.Legacy("POST", "/identity/synchronize");
            routes.Post("/context/build", request => BuildContext(request.JsonBody));
            routes.Post("/context/select", request => SelectContextPulls(request.JsonBody));
            routes.Post("/actions/route", request => RouteActionsApi(request.JsonBody));
            routes.Post("/actions/plan", request => PlanActionsApi(request.JsonBody));
            routes.Post("/characters/upsert", request => UpsertCharacter(request.JsonBody));
            routes.Post("/characters/upsert-batch", request => UpsertCharacterBatch(request.JsonBody));
            routes.Post("/portrait-roster/upsert-batch", request => UpsertPortraitRoster(request.JsonBody));
            routes.Post("/characters/construct", request => ConstructCharacter(request.JsonBody));
            routes.Post("/characters/construct-batch", request => ConstructCharacterBatch(request.JsonBody));
            routes.Post("/characters/retry-failed", request => RetryFailedCharacters(request.JsonBody));
            routes.Get("/characters/schema", request => CharacterSchemaApi());
            routes.Post("/characters/diagnose", request => CharacterStructureDiagnostics(request.JsonBody));
            routes.Post("/dialogue/history", request => DialogueHistory(request.JsonBody));
            routes.Post("/dialogue/respond", request => RunInteractiveRequest(() => DialogueRespond(request.JsonBody)));
            routes.Post("/party-chat/respond", request => RunInteractiveRequest(() => PartyChatRespond(request.JsonBody)));
            routes.Post("/party-chat/history", request => PartyChatHistory(request.JsonBody));
            routes.Post("/events/social/start", request => SocialEventStart(request.JsonBody));
            routes.Post("/events/social/respond", request => RunInteractiveRequest(() => SocialEventRespond(request.JsonBody)));
            routes.Post("/events/social/resolve", request => SocialEventResolve(request.JsonBody));
            routes.Post("/events/social/history", request => SocialEventHistory(request.JsonBody));
            routes.Post("/events/generated-wilderness/plan", request => GeneratedWildernessPlan(request.JsonBody));
            routes.Post("/portraits/register", request => PortraitRegister(request.JsonBody));
            routes.Get("/portraits/status", request => PortraitStatus(request.JsonBody, request.Query));
            routes.Post("/portraits/status", request => PortraitStatus(request.JsonBody, request.Query));
            routes.Post("/portraits/request", request => PortraitRequest(request.JsonBody));
            routes.Post("/castle-chat/scene/generate", request => RunInteractiveRequest(() => CastleSceneGenerate(request.JsonBody)));
            routes.Post("/family-chambers/scene/generate", request => RunInteractiveRequest(() => FamilyChambersSceneGenerate(request.JsonBody)));
            routes.Post("/family-chambers/childhood/mature", request => FamilyChambersMatureChildhood(request.JsonBody));
            routes.Post("/portraits/diagnose", request => PortraitDiagnose(request.JsonBody));
            routes.Legacy("GET", "/actions/catalog");
            routes.Legacy("GET", "/actions/failures");
            routes.Get("/actions/next", request => NextActions(request.JsonBody, request.Query));
            routes.Post("/actions/next", request => NextActions(request.JsonBody, request.Query));
            routes.Post("/actions/propose", request => ProposeAction(request.JsonBody));
            routes.Post("/actions/decide", request => DecideActions(request.JsonBody));
            routes.Post("/actions/report", request => ReportAction(request.JsonBody));
            routes.Post("/llm/chat", request => ChatWithLlm(request.JsonBody));
        }
    }
}
