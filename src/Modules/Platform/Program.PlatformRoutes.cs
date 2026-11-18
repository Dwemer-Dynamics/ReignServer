using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the Platform module, in their former router order (see RouteTable.cs).
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
            routes.Get("/health", request =>
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["service"] = "BannerlordReignServer",
                    ["databaseName"] = ReignPostgreSqlOptions.FromEnvironment().Database,
                    ["databaseSchemaVersion"] = ReignPostgreSqlStorage.DatabaseSchemaVersion,
                    ["requiredDatabaseSchemaVersion"] = ReignPostgreSqlStorage.RequiredDatabaseSchemaVersion,
                    ["databaseSchemaUpToDate"] = ReignPostgreSqlStorage.DatabaseSchemaVersion == ReignPostgreSqlStorage.RequiredDatabaseSchemaVersion,
                    ["protocolVersion"] = Reign.Core.Contracts.Platform.ReignInstallation.SupportedProtocol,
                    ["serverVersion"] = Reign.Core.Contracts.Platform.ReignInstallation.TryLoadCurrent()?.Version ?? typeof(Program).Assembly.GetName().Version.ToString(3),
                    ["contentVersion"] = Reign.Core.Contracts.Platform.ReignInstallation.TryLoadCurrent()?.ContentVersion ?? "development",
                    ["dataDir"] = DataDir,
                    ["processId"] = Process.GetCurrentProcess().Id,
                    ["port"] = ActiveServerPort,
                    ["managedBy"] = "ReignServer",
                    // Exact additive capability: the route, preflight and legacy guards are wired.
                    [Reign.Core.Contracts.Actions.TerminalActionReportProtocol.HealthCapabilityField] =
                        Reign.Core.Contracts.Actions.TerminalActionReportProtocol.RequestSchema,
                    // Additive: verified-load recovery, load proofs and admission-bound acks are wired.
                    [Reign.Core.Contracts.Actions.TerminalActionReportProtocol.RecoveryHealthCapabilityField] =
                        Reign.Core.Contracts.Actions.TerminalActionReportProtocol.RecoverySchema
                };
            }).WithoutCampaignGate();
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
            routes.Get("/api/diagnostics", request => ServerDiagnostics());
            routes.Post("/api/diagnostics/test-llm", request => TestLlmKey(request.JsonBody));
            routes.Get("/api/codex/status", request => CodexStatusApi());
            routes.Post("/api/codex/start", request => StartCodexAppServerApi());
            routes.Post("/api/codex/stop", request => StopCodexAppServerApi());
            routes.Post("/api/codex/login/start", request => StartCodexLoginApi(request.JsonBody));
            routes.Post("/api/codex/logout", request => CodexLogoutApi());
            routes.Post("/api/codex/models/refresh", request => RefreshCodexModelsApi());
            routes.Post("/api/codex/models/verify", request => CodexVerifyModelApi(request.JsonBody));
            routes.Get("/api/telemetry/status", request => ReignTelemetryStatus());
            routes.Get("/api/provider/status", request => ProviderMiddlewareStatus());
            routes.Get("/api/background/status", request => PriorityBackgroundStatus());
            routes.Get("/api/prompts", request => ReadPromptTemplatesApi());
            routes.Post("/api/prompts", request => SavePromptTemplatesApi(request.JsonBody));
            routes.Post("/api/castle-chat/prompts", request => CastleChatPromptsApi(request.JsonBody));
            routes.Get("/api/logs", request => ReadLogApi(request.Query));
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
            routes.Get("/audit/query", request => AuditQuery(request.Query));
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Get("/tests/cases", request => TestCasesApi(request.Query));
            routes.Post("/tests/cases/save", request => SaveTestCaseApi(request.JsonBody));
            routes.Post("/tests/run", request => RunTestApi(request.JsonBody));
            routes.Post("/tests/run-suite", request => RunTestSuiteApi(request.JsonBody));
            routes.Post("/tests/capture-from-correlation", request => CaptureTestFromCorrelationApi(request.JsonBody));
            routes.Get("/tests/runs", request => TestRunsApi(request.Query));
            routes.Post("/verification/run", request => StartVerificationApi(request.JsonBody));
            routes.Get("/verification/status", request => VerificationStatusApi());
            routes.Post("/verification/cancel", request => CancelVerificationApi());
            routes.Get("/verification/results", request => VerificationResultsApi(request.Query));
            routes.Post("/verification/replay", request => ReplayVerificationApi(request.JsonBody));
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
            routes.Post("/identity/synchronize", request => IdentitySynchronizeWithPortraitRosterUpgradeApi(request.JsonBody));
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
            routes.Get("/actions/catalog", request => ActionCatalog());
            routes.Get("/actions/failures", request => ActionFailures(request.JsonBody, request.Query));
            routes.Get("/actions/next", request => NextActions(request.JsonBody, request.Query));
            routes.Post("/actions/next", request => NextActions(request.JsonBody, request.Query));
            routes.Post("/actions/propose", request => ProposeAction(request.JsonBody));
            routes.Post("/actions/decide", request => DecideActions(request.JsonBody));
            routes.Post("/actions/report", request => ReportAction(request.JsonBody));
            routes.Post("/llm/chat", request => ChatWithLlm(request.JsonBody));
        }
    }
}
