# Platform module (server)

Process host and shared runtime. `Program` is only the host, bootstrap and routing: `Program.cs` (`Main`, startup,
the `HandleClient` router, `CampaignDataGate`), `Program.HttpIngress.cs`, `RouteTable.cs`, `Program.PlatformRoutes.cs`,
`ServerActivation.cs` and `InitializationReadiness.cs`. The rest is split into area services: `LlmProviderService`
(`Providers/`: LLM transport, provider middleware, provider profiles, the Codex app-server runtime and the
`ILlmProvider` chat providers), `SettingsService` (settings, secrets, option sets), `PromptService`,
`ActionPipelineService`, `ConversationResponseService` (response repair, parsing, diagnostics),
`CampaignFileService`, `OperationsService` (logs and audit, support bundle, observability, the priority background
scheduler) and `VerificationService` (VerificationLab, test runner and test cases), plus the `ReignDatabase` data core.

> Checked against the source on 2026-11-17. [ARCHITECTURE.md](../../../docs/ARCHITECTURE.md) describes the whole server
> and the restructuring direction.

## Main files

- `Program.cs` (about 1.3k lines): core fields, `Main`, `RunServer`, `HandleClient`, `EnterCampaignRequestAccess`,
  `RunInteractiveRequest`, `ControlCenterHtml` and `HttpRequest`.
- `Program.HttpIngress.cs` (`ReadRequest` and the ingress constants). The former `Program.<Area>.cs` partials are
  area service files without the prefix: `CampaignFiles`, `Settings`, `PromptTemplates`, `DefaultPromptTemplates`,
  `ConversationResponses`, `LogsAndAudit`, `PipelineTestRunner`, `DefaultTestCases`, and the action pipeline
  (`DialogueActions`, `ActionGates`, `ActionPlanner`, `ActionRouter`, `DirectiveTerms`, `DirectivePackages`,
  `ActionReferences`, `ActionValidation`, `ActionQueue`). See
  [ARCHITECTURE.md](../../../docs/ARCHITECTURE.md#process-composition) for the service of each file.
- Providers (`Providers/`, `LlmProviderService`): `ILlmProvider.cs`, `LlmProviders.cs` and `ChatProviders.cs` (the
  Codex subscription, OpenRouter, Ollama Cloud and OpenAI-compatible chat providers), `LlmTransport.cs`,
  `LlmProviderProfiles.cs`, `LlmProviderControlCenter.cs`, `ProviderMiddleware.cs`, `CodexAppServerProvider.cs`,
  `CodexRuntime*.cs`, `CodexFailureRecovery.cs`, `CodexPerformance*.cs`. `NativeProviderTestGuard.cs` stays in
  Platform.
- Settings and secrets: `LinuxSecretVault.cs`, `SettingsSecretVault.cs`, `GlobalKeyBridge.cs`, `RuntimeData.cs`,
  `PromptEditorPersistence.cs`, `LinuxJsonSerializer.cs`, and the client option sets (`*Options.cs`).
- Runtime services: `PriorityBackgroundScheduler.cs`, `ReignObservability.cs`, `ServerActivation.cs`,
  `InitializationReadiness.cs`, `SupportBundle.cs`, `ConversationDiagnostics*.cs`, `ConversationResponseParsing.cs`.
- Data core: `Data/ReignDatabase.cs` (`ReignDatabase`: `OpenCampaignConnection`, campaign schema caches, `ExecuteSql`,
  `QuerySql`, `EnsureDatabaseColumn`). Each module keeps its SQL in `Modules/<Module>/Data` (`<Module>Store`,
  `<Module>Schema`); see [ARCHITECTURE.md](../../../docs/ARCHITECTURE.md).
- Routing: `RouteTable.cs` (route table, dispatcher, campaign-gate declarations), `Program.PlatformRoutes.cs` (this
  module's registrations; every module has its own `<Module>Routes.cs`) and `RouteTableContractSelfTests.cs`.
- `Testing/reign-xp-options.js` and the `*-contract.mjs` files are Control Center UI contract scripts.

## Routes

Handled in other Platform files: `POST /api/activate`, `GET /api/gameplay-options`, `GET /api/global-keys`,
`POST /api/global-keys/save|unbind`, `/api/codex/*` (status, start, stop, login, logout, models), `GET
/api/telemetry/status`, `GET /api/provider/status`, `GET /api/background/status`, `/api/diagnostics/conversations*`,
`POST /api/support-bundle`, `/verification/*` (run, status, cancel, results, replay), `/initialization/readiness/*`
(seal, status, retry).

Registered in `Program.PlatformRoutes.cs` and the module route files, handled by `Program.cs` and the Platform
services here and by the Dialogue, Characters and Portraits handler files:

- Service and Control Center: `GET /health`, `GET /` and `/index.html`, logo assets, `/api/settings`,
  `/api/settings/clear-key`, `/api/shutdown`, `/api/diagnostics`, `/api/diagnostics/test-llm`, `/api/prompts`,
  `/api/logs`, `/api/logs/clear`, `/api/logs/export`, `/audit/ingest`, `/audit/query`, `POST /llm/chat`.
- Prompt test cases: `/tests/cases`, `/tests/cases/save`, `/tests/run`, `/tests/run-suite`, `/tests/runs`,
  `/tests/capture-from-correlation`.
- Core game routes: `/events/ingest`, `/memory/event`, `/memory/action_result`, `/memory/build_context`,
  `/memory/consolidate`, `/context/build`, `/context/select`, `/dialogue/respond`, `/dialogue/history`,
  `/party-chat/respond`, `/party-chat/history`, `/actions/*` (route, plan, catalog, failures, next, propose, decide,
  report), `/characters/*` (upsert, upsert-batch, construct, construct-batch, retry-failed, schema, diagnose),
  `/portrait-roster/upsert-batch`, `/identity/synchronize`, `/portraits/register|status|request|diagnose`,
  `/events/social/start|respond|resolve|history`, `/events/generated-wilderness/plan`.

## Tables

`EnsureMemorySchema` (run for every campaign on its first connection): `memories`, `events`, `summaries`, `beliefs`,
`obligations`, `comprehension`, `schema_meta`. `EnsureInitializationReadinessSchema`: `initialization_readiness_seals`.

## Self-tests and verification

- `VerificationLab.cs`: tiers, suites and the `--run-verification` / `--cancel-verification` CLI;
  `RunQuickVerificationChecks`, `RunOfflineVerificationChecks`, `RunProductionPipelineChecks`,
  `RunProviderFaultChecks`, `RunShadowWorldChecks`, `RunExtendedRebellionChecks`, `RunLongRunChecks`,
  `RunLiveLlmVerificationChecks`, `RunBuildAndContractChecks`. Split verbatim into partial files:
  `VerificationLab.Contracts.cs` (`RunBuildAndContractChecks`), `VerificationLab.Suites.cs` (the other suites,
  coverage, status and discovery helpers) and `VerificationLab.Support.cs` (UI/XML contract helpers, shadow world).
- `ArchitectureSelfTests.cs` (`RunInteractionArchitectureSelfTests`): architecture and source-text contracts.
- Other suites: `RunCodex*SelfTests`, `RunConversationDiagnosticsSelfTests`,
  `RunConversationResponseParsingSelfTests`, `RunGlobalKeyBridgeContractTests`, `RunInitializationReadinessSelfTests`,
  `RunOllamaCloudProviderContractTests`, `RunOpenRouterProviderContractTests`, `RunPromptEditorPersistenceTests`,
  `RunReignXpOptionsSelfTests`, `RunWarEconomyOptionsSelfTests`.
- `--run-tests` runs the saved prompt test cases (`RunTestsCli`).
- Test and test-support files compiled into the server: `ArchitectureSelfTests.cs`, `ConversationDiagnosticsSelfTests.cs`,
  `ConversationResponseParsingSelfTests.cs`, `GlobalKeyBridgeContractTests.cs`, `OllamaCloudProviderContractTests.cs`,
  `OpenRouterProviderContractTests.cs`, `ReignXpSelfTests.cs`, `VerificationLab.cs`, `VerificationSourceLocator.cs`,
  `WarEconomyOptionsSelfTests.cs`, `NativeProviderTestGuard.cs`.

## Dependencies

Every module depends on Platform helpers. Platform in turn references most modules, most-used first: Dialogue (520),
Characters (117), Persistence (91), WorldSimulation (91), Court (81), Portraits (77), Relationships (69),
Reputation (36), Diplomacy (17), Rebellion (12), Government (6), Spymaster (2).

Counts are approximate: distinct identifiers defined in exactly one other module and referenced per file, summed.
They show coupling, not a layering rule.
