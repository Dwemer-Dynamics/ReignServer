using System;
using System.Collections.Generic;
using System.Diagnostics;
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
                    OperationsService.LogRequest(request, 200);
                    return RouteResponseWritten;
                };
                routes.Get("/", handler).WithoutCampaignGate();
                routes.Get("/index.html", handler).WithoutCampaignGate();
            }
            routes.Get("/assets/reign-icon.webp", (request, route) =>
            {
                WriteFile(route.Stream, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "assets", "reign-icon.webp"), "image/webp");
                OperationsService.LogRequest(request, 200);
                return RouteResponseWritten;
            }).WithoutCampaignGate();
            routes.Get("/assets/ReignLogo.png", (request, route) =>
            {
                WriteFile(route.Stream, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "assets", "ReignLogo.png"), "image/png");
                OperationsService.LogRequest(request, 200);
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
            routes.Get("/api/settings", request => SettingsService.SettingsForUi(SettingsService.LoadSettings()));
            routes.Get("/api/gameplay-options", request => SettingsService.ReignXpOptionsForClient());
            routes.Get("/api/global-keys", request => SettingsService.GlobalKeysStatus(request.Query.TryGetValue("slot", out string onlySlot) && SettingsService.GlobalKeySlots.Contains(onlySlot) ? onlySlot : null));
            {
                Func<HttpRequest, object> handler = request => !SettingsService.GlobalKeyRequestAllowed(request) ? SettingsService.GlobalKeyFail("forbidden")
                    : request.Path.EndsWith("/save", StringComparison.Ordinal) ? SettingsService.SaveGlobalKey(request.JsonBody) : SettingsService.UnbindGlobalKey(request.JsonBody);
                routes.Post("/api/global-keys/save", handler);
                routes.Post("/api/global-keys/unbind", handler);
            }
            routes.Post("/api/settings", request => SettingsService.SaveSettings(request.JsonBody));
            // Stop reads no campaign state and may synchronously settle never-started background releases, so it
            // must run without campaign ownership.
            routes.Post("/api/shutdown", request => RequestServerShutdown()).WithoutCampaignGate();
            routes.Post("/api/settings/clear-key", request => SettingsService.ClearSettingsKey(request.JsonBody, request));
            routes.Get("/api/diagnostics", request => SettingsService.ServerDiagnostics());
            routes.Post("/api/diagnostics/test-llm", request => SettingsService.TestLlmKey(request.JsonBody));
            routes.Get("/api/codex/status", request => LlmProviderService.CodexStatusApi());
            routes.Post("/api/codex/start", request => LlmProviderService.StartCodexAppServerApi());
            routes.Post("/api/codex/stop", request => LlmProviderService.StopCodexAppServerApi());
            routes.Post("/api/codex/login/start", request => LlmProviderService.StartCodexLoginApi(request.JsonBody));
            routes.Post("/api/codex/logout", request => LlmProviderService.CodexLogoutApi());
            routes.Post("/api/codex/models/refresh", request => LlmProviderService.RefreshCodexModelsApi());
            routes.Post("/api/codex/models/verify", request => LlmProviderService.CodexVerifyModelApi(request.JsonBody));
            routes.Get("/api/telemetry/status", request => OperationsService.ReignTelemetryStatus());
            routes.Get("/api/provider/status", request => LlmProviderService.ProviderMiddlewareStatus());
            routes.Get("/api/background/status", request => OperationsService.PriorityBackgroundStatus());
            routes.Get("/api/prompts", request => PromptService.ReadPromptTemplatesApi());
            routes.Post("/api/prompts", request => PromptService.SavePromptTemplatesApi(request.JsonBody));
            routes.Post("/api/castle-chat/prompts", request => CourtService.CastleChatPromptsApi(request.JsonBody));
            routes.Get("/api/logs", request => OperationsService.ReadLogApi(request.Query));
            routes.Get("/api/diagnostics/conversations", request => ConversationResponseService.ConversationDiagnosticsListApi(request.Query));
            routes.Get("/api/diagnostics/conversations/trace", request => ConversationResponseService.ConversationDiagnosticsTraceApi(request.Query));
            routes.Get("/api/diagnostics/conversations/payload", request => ConversationResponseService.ConversationDiagnosticsPayloadApi(request.Query));
            routes.Get("/api/diagnostics/conversations/capture", request => ConversationResponseService.ConversationDiagnosticsCaptureApi());
            routes.Post("/api/diagnostics/conversations/capture", request => ConversationResponseService.ConversationDiagnosticsCaptureApi(request.JsonBody));
            routes.Post("/api/diagnostics/conversations/clear", request => ConversationResponseService.ConversationDiagnosticsClearApi(request.JsonBody));
            routes.Get("/api/diagnostics/conversations/export", (request, route) =>
            {
                ConversationResponseService.WriteConversationDiagnosticsExport(route.Stream, request.Query);
                OperationsService.LogRequest(request, 200);
                return RouteResponseWritten;
            });
            routes.Post("/api/logs/clear", request => OperationsService.ClearLogs(request.JsonBody));
            routes.Get("/api/logs/export", (request, route) =>
            {
                OperationsService.WriteLogExport(route.Stream, request.Query);
                OperationsService.LogRequest(request, 200);
                return RouteResponseWritten;
            });
            routes.Post("/api/support-bundle", (request, route) =>
            {
                OperationsService.WriteSupportBundle(route.Stream, request.JsonBody);
                OperationsService.LogRequest(request, 200);
                return RouteResponseWritten;
            });
            routes.Post("/audit/ingest", request => OperationsService.AuditIngest(request.JsonBody));
            routes.Get("/audit/query", request => OperationsService.AuditQuery(request.Query));
#if !REIGN_EXCLUDE_VERIFICATION
            routes.Get("/tests/cases", request => VerificationService.TestCasesApi(request.Query));
            routes.Post("/tests/cases/save", request => VerificationService.SaveTestCaseApi(request.JsonBody));
            routes.Post("/tests/run", request => VerificationService.RunTestApi(request.JsonBody));
            routes.Post("/tests/run-suite", request => VerificationService.RunTestSuiteApi(request.JsonBody));
            routes.Post("/tests/capture-from-correlation", request => VerificationService.CaptureTestFromCorrelationApi(request.JsonBody));
            routes.Get("/tests/runs", request => VerificationService.TestRunsApi(request.Query));
            routes.Post("/verification/run", request => VerificationService.StartVerificationApi(request.JsonBody));
            routes.Get("/verification/status", request => VerificationService.VerificationStatusApi());
            routes.Post("/verification/cancel", request => VerificationService.CancelVerificationApi());
            routes.Get("/verification/results", request => VerificationService.VerificationResultsApi(request.Query));
            routes.Post("/verification/replay", request => VerificationService.ReplayVerificationApi(request.JsonBody));
#endif
            routes.Post("/events/ingest", request => DialogueService.IngestEvent(request.JsonBody));
            routes.Post("/initialization/readiness/seal", request => InitializationReadinessSealApi(request.JsonBody));
            routes.Post("/initialization/readiness/status", request => InitializationReadinessStatusApi(request.JsonBody));
            routes.Post("/initialization/readiness/retry", request => InitializationReadinessRetryApi(request.JsonBody));
            routes.Post("/memory/event", request => DialogueService.MemoryEventApi(request.JsonBody));
            routes.Post("/memory/conversation_finished", request => string.IsNullOrWhiteSpace(ReadFirstString(request.JsonBody, "sessionId", "conversationSessionId", "conversationId"))
                ? DialogueService.MemoryConversationFinishedApi(request.JsonBody)
                : DialogueService.ConversationFinishApi(request.JsonBody));
            routes.Post("/memory/action_result", request => DialogueService.MemoryActionResultApi(request.JsonBody));
            routes.Post("/memory/build_context", request => DialogueService.MemoryBuildContextApi(request.JsonBody));
            routes.Post("/memory/consolidate", request => DialogueService.MemoryConsolidateApi(request.JsonBody));
            routes.Post("/identity/synchronize", request => CharactersService.IdentitySynchronizeWithPortraitRosterUpgradeApi(request.JsonBody));
            routes.Post("/context/build", request => DialogueService.BuildContext(request.JsonBody));
            routes.Post("/context/select", request => DialogueService.SelectContextPulls(request.JsonBody));
            routes.Post("/actions/route", request => ActionPipelineService.RouteActionsApi(request.JsonBody));
            routes.Post("/actions/plan", request => ActionPipelineService.PlanActionsApi(request.JsonBody));
            routes.Post("/characters/upsert", request => CharactersService.UpsertCharacter(request.JsonBody));
            routes.Post("/characters/upsert-batch", request => CharactersService.UpsertCharacterBatch(request.JsonBody));
            routes.Post("/portrait-roster/upsert-batch", request => CharactersService.UpsertPortraitRoster(request.JsonBody));
            routes.Post("/characters/construct", request => CharactersService.ConstructCharacter(request.JsonBody));
            routes.Post("/characters/construct-batch", request => CharactersService.ConstructCharacterBatch(request.JsonBody));
            routes.Post("/characters/retry-failed", request => CharactersService.RetryFailedCharacters(request.JsonBody));
            routes.Get("/characters/schema", request => CharactersService.CharacterSchemaApi());
            routes.Post("/characters/diagnose", request => CharactersService.CharacterStructureDiagnostics(request.JsonBody));
            routes.Post("/dialogue/history", request => DialogueService.DialogueHistory(request.JsonBody));
            routes.Post("/dialogue/respond", request => RunInteractiveRequest(() => DialogueService.DialogueRespond(request.JsonBody)));
            routes.Post("/party-chat/respond", request => RunInteractiveRequest(() => DialogueService.PartyChatRespond(request.JsonBody)));
            routes.Post("/party-chat/history", request => DialogueService.PartyChatHistory(request.JsonBody));
            routes.Post("/events/social/start", request => DialogueService.SocialEventStart(request.JsonBody));
            routes.Post("/events/social/respond", request => RunInteractiveRequest(() => DialogueService.SocialEventRespond(request.JsonBody)));
            routes.Post("/events/social/resolve", request => DialogueService.SocialEventResolve(request.JsonBody));
            routes.Post("/events/social/history", request => DialogueService.SocialEventHistory(request.JsonBody));
            routes.Post("/events/generated-wilderness/plan", request => DialogueService.GeneratedWildernessPlan(request.JsonBody));
            routes.Post("/portraits/register", request => PortraitsService.PortraitRegister(request.JsonBody));
            routes.Get("/portraits/status", request => PortraitsService.PortraitStatus(request.JsonBody, request.Query));
            routes.Post("/portraits/status", request => PortraitsService.PortraitStatus(request.JsonBody, request.Query));
            routes.Post("/portraits/request", request => PortraitsService.PortraitRequest(request.JsonBody));
            routes.Post("/castle-chat/scene/generate", request => RunInteractiveRequest(() => CourtService.CastleSceneGenerate(request.JsonBody)));
            routes.Post("/family-chambers/scene/generate", request => RunInteractiveRequest(() => CourtService.FamilyChambersSceneGenerate(request.JsonBody)));
            routes.Post("/family-chambers/childhood/mature", request => CourtService.FamilyChambersMatureChildhood(request.JsonBody));
            routes.Post("/portraits/diagnose", request => PortraitsService.PortraitDiagnose(request.JsonBody));
            routes.Get("/actions/catalog", request => ActionPipelineService.ActionCatalog());
            routes.Get("/actions/failures", request => ActionPipelineService.ActionFailures(request.JsonBody, request.Query));
            routes.Get("/actions/next", request => ActionPipelineService.NextActions(request.JsonBody, request.Query));
            routes.Post("/actions/next", request => ActionPipelineService.NextActions(request.JsonBody, request.Query));
            routes.Post("/actions/propose", request => ActionPipelineService.ProposeAction(request.JsonBody));
            routes.Post("/actions/decide", request => ActionPipelineService.DecideActions(request.JsonBody));
            routes.Post("/actions/report", request => ActionPipelineService.ReportAction(request.JsonBody));
            routes.Post("/llm/chat", request => LlmProviderService.ChatWithLlm(request.JsonBody));
        }
    }
}
