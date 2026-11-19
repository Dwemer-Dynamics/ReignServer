# ReignServer architecture

ReignServer is the local Linux server for the Bannerlord mod Reign. It is one C# net10 executable (`ReignServer`,
`ReignServer.csproj`, linux-x64 only) backed by PostgreSQL, with a managed Python vector worker beside it. The
Bannerlord client (`ReignBeta`, sibling `Reign` repository) talks to it over loopback HTTP; the Control Center UI in
`ui/` is served by the same process. This document describes how the server is composed today, verified against the
code on 2026-11-17, and points at the direction of the current restructuring programme. The client side is described in
the Reign repository's `docs/agent/ARCHITECTURE.md` (in a paired checkout, `../../Reign/docs/agent/ARCHITECTURE.md`).

## Repositories and assemblies

```mermaid
flowchart LR
    subgraph Server["ReignServer (this repository)"]
        RS["ReignServer.csproj<br/>net10 linux-x64 exe, namespace ReignBetaServer"]
        CC["shared/Reign.Core.Contracts<br/>netstandard2.0 DTOs and policies"]
        RK["shared/Reign.Relationships<br/>netstandard2.0 relationship kernel"]
        SS["shared/Reign.Shared.Source<br/>source compiled into both sides"]
        VW["services/vector-worker<br/>Python worker.py"]
        LT["tests/ReignLiveTest, tests/ReignVerification<br/>CLI controllers"]
    end
    subgraph Client["Reign (sibling checkout)"]
        RB["ReignBeta.dll<br/>Bannerlord mod"]
        MCP["ReignMcp<br/>canonical validator"]
    end
    RS --> CC
    RS --> RK
    RK --> CC
    RS -- "compiles" --> SS
    RB -- "references" --> CC
    RB -- "compiles linked source" --> SS
    RB -- "HTTP via Apache :8089 or :5101" --> RS
    RS -- "spawns, HTTP :5102" --> VW
    LT -- "HTTP / runs --run-verification" --> RS
    MCP -- "builds, validates" --> RS
```

- `ReignServer.csproj` sets `EnableDefaultCompileItems=false` and compiles `src/**/*.cs` plus
  `shared/Reign.Shared.Source/**/*.cs` (linked under `Shared/`). It references the `Reign.Core.Contracts` and
  `Reign.Relationships` projects. `ExcludeCourt=true` removes `src/Modules/Court` and `src/Modules/Spymaster` and
  defines `REIGN_EXCLUDE_COURT`. `ReignExcludeVerification=true` builds the opt-in test-free flavour described under
  [Verification](#verification-compiled-into-the-server).
- `database/reign_meta.sql`, the default prompts in `prompts/` and a few assets are embedded resources; `ui/`,
  `profiles/`, `services/vector-worker/*.py` and the portrait face-detection model are copied to the output.
- `ReignIncludePrivateVerificationContracts=true` (paired workspace only) runs the `CopyVerificationContracts` target,
  which copies server and client source, UI, docs and GUI files into `verification_contracts/workspace` so the
  source-text contract checks can read them at run time. A public build leaves it disabled.
- `shared/Reign.Core.Contracts` (netstandard2.0, nullable, warnings as errors) holds wire DTOs, protocol constants
  (`Platform/ReignInstallation.SupportedProtocol`, `ReignProtocolHandler.HeaderName`, the terminal action report
  schemas) and game-free policies used by both client and server.
- `shared/Reign.Relationships` is the relationship kernel: deterministic, dependency-free policies
  (`RelationshipCompatibilityPolicy` MBTI compatibility scores, `ArrangedMarriagePolicy`) used by the server's
  Relationships, WorldSimulation and related code. `shared/Reign.Relationships.Tests` holds its xUnit tests.
- `shared/Reign.Shared.Source` is a source package (see its README): a few helpers both sides compile with their
  existing namespaces, so no new runtime assembly or save identity is introduced.
- The server repository owns everything under `shared/`. The client consumes a contract change only after the server
  change has merged.

## Process composition

`internal static partial class Program` (namespace `ReignBetaServer`) is now only the host: `Platform/Program.cs`
(the core fields, `Main`, `RunServer`, the startup helpers, `HandleClient`, the campaign request gate,
`RunInteractiveRequest`, `ControlCenterHtml` and `HttpRequest`), `Platform/Program.HttpIngress.cs` (ingress limits,
`ReadRequest`, response writers), the router (`Platform/RouteTable.cs`, `Platform/Program.PlatformRoutes.cs`) and the
bootstrap files `Platform/ServerActivation.cs` and `Platform/InitializationReadiness.cs`. No module file is a
`Program` partial (P3-3). Every domain module is its own `internal static partial class <Module>Service`:
`DialogueService`, `CharactersService`, `WorldSimulationService`, `RelationshipsService`, `PersistenceService`,
`ReputationService`, `DiplomacyService`, `CourtService`, `PortraitsService`, `GovernmentService`, `RebellionService`
and `SpymasterService`, and registers its routes from an `internal static class <Module>Routes` in
`<Module>Routes.cs`, which `RouteTable.cs` calls. The rest of Platform is split by area into service classes:

| Service | Files (in `src/Modules/Platform`) |
|---|---|
| `LlmProviderService` | `Providers/`: `LlmTransport` (`ChatWithLlm`, provider requests), `ProviderMiddleware`, `CodexAppServerProvider`, `CodexRuntime*`, `CodexFailureRecovery`, `CodexPerformance*`, `LlmProviderProfiles`, `LlmProviderControlCenter`, and the provider contract tests |
| `SettingsService` | `Settings`, `SettingsSecretVault`, `LinuxSecretVault`, `GlobalKeyBridge`, `RuntimeData` and the `*Options` files |
| `PromptService` | `PromptTemplates`, `DefaultPromptTemplates`, `PromptEditorPersistence` |
| `ActionPipelineService` | `DialogueActions`, `ActionGates`, `ActionPlanner`, `ActionRouter`, `DirectiveTerms`, `DirectivePackages`, `ActionReferences`, `ActionValidation`, `ActionQueue`, `CampaignCommandDialogueDrafts` |
| `ConversationResponseService` | `ConversationResponses` (structured-response repair, visible-reply sanitising), `ConversationResponseParsing`, `ConversationDiagnostics*` |
| `CampaignFileService` | `CampaignFiles` (campaign directories and JSON file IO) |
| `OperationsService` | `LogsAndAudit`, `SupportBundle`, `ReignObservability`, `PriorityBackgroundScheduler` |
| `VerificationService` | `VerificationLab*`, `ArchitectureSelfTests`, `RouteTableContractSelfTests`, `PipelineTestRunner`, `DefaultTestCases` |

The main turn handlers that were `Program.<Area>.cs` files are module service files: Dialogue's
`Memory/MemoryStore.cs`, `Memory/MemoryPacket.cs`, `Memory/MemoryRetrieval.cs`, `Memory/ContextPulls.cs`,
`Conversation/DialogueTurns.cs` (`/dialogue/respond`, `/dialogue/history`), `Conversation/EventConversations.cs`
and `Prompts/PromptContext.cs` (`DialogueService`); Characters' `CharacterConstruction.cs` and
`CharacterProfiles.cs` (`CharactersService`); Portraits' `PortraitRequests.cs` (`PortraitsService`). Member names
did not change and no overload group was split. Static fields that moved out of `Program` have side-effect-free
initializers, so initializing them with their new class instead of with `Program` does not change behaviour.

LLM chat providers implement `ILlmProvider` (`Providers/ILlmProvider.cs`): the Codex subscription, OpenRouter,
Ollama Cloud and an OpenAI-compatible fallback (NanoGPT and custom endpoints) in `Providers/ChatProviders.cs`.
`LlmProviders.Select` picks one in that order, and `ChatWithLlm` dispatches through it. The HTTP providers share
the campaign provider wait and `ProviderMiddleware` (concurrency slots, response cache, streaming).

Source-text contracts that used to read the `Program` type read the former Program area instead:
`VerificationSourceLocator.ReadServerProgramAreaSourceText` on the server and `TestSourceLocator.ServerProgramText`
in the client tests.

`Main` (`Platform/Program.cs`) runs in this order:

1. Commands that run before normal startup. `--import-tavern-native-sources`
   (`Portraits/PreparedTavernPortraitSources.cs`) is checked first. Then a `--run-verification` process is forced
   onto the isolated `ReignValidation` database (`REIGN_VALIDATION_MODE=1`). Then `--activate-only` (which must not
   touch campaign storage) and `--continuity-repair` (which repairs a campaign schema) run and exit.
2. `RunServer`: more one-shot commands (`--author-character-narratives`, `--complete-delete-all`,
   `--generate-profile-library`), then startup housekeeping (data directories, log compaction, Save Sync recovery
   cleanup, prompt templates, default test cases).
3. Self-test and verification commands (see [Verification](#verification-compiled-into-the-server)); each prints
   JSON and exits.
4. Normal start: `ReignPostgreSqlStorage.EnsureInfrastructure()` applies `reign_meta` migrations, the character
   profile library loads, a `TcpListener` binds loopback (port from `--port`, else settings `port`, default 5101),
   then background services start: the priority background scheduler, reputation reason jobs, the continuous
   relationship worker, the world-test rollup worker, memory background jobs, the semantic memory subsystem (vector
   worker), campaign retirement processing and Save Sync optimisation. SIGTERM/SIGINT request a graceful shutdown.

The accept loop hands each connection to `Task.Run(() => HandleClient(client))`, up to
`IngressMaxConcurrentConnections` (256); beyond that the connection is answered busy and closed, never queued.

### Request handling

`HandleClient` (`Platform/Program.cs`) dispatches through the route table in `Platform/RouteTable.cs`. Each module
registers its routes in `src/Modules/<Module>/<Module>Routes.cs`, giving the method, the exact path or prefix
(with its comparison), the handler and the campaign-gate policy, and the first match in registration order wins; an
unmatched request gets the "Unknown route." 404. `RouteTableContractSelfTests.cs` (interaction architecture suite)
checks the table against the pre-table route inventory, gate decisions and precedence. Per request `HandleClient`:

1. Reads and frames the request in `ReadRequest`, enforcing the ingress limits and access policy before any body is
   buffered, any campaign lock is taken or any route runs.
2. Refuses a client whose `X-Reign-Protocol` header names a different protocol than
   `ReignInstallation.SupportedProtocol`.
3. Calls `EnterCampaignRequestAccess(path)`, then dispatches. Prefix routes forward to module dispatchers such as `ChatViewApi`, `NpcLivesApi`,
   `CommunicationApi`, `PartyBanterApi`, `OutfitChangeApi` and `RulerRelationsApi`.
4. Writes the JSON response, logs it, releases the campaign access, deletes any unconsumed upload and returns the
   ingress reservations in `finally`.

Interactive LLM routes are wrapped in `RunInteractiveRequest(...)`, which counts and traces in-flight interactive
requests; background work checks `InteractiveRequestActiveOrRecent` and defers while one is active or recent.

### Ingress limits

Defined beside `ReadRequest` in `Platform/Program.HttpIngress.cs`: request line 8 KiB, headers 64 KiB and 128 fields, identity
headers 256 chars, JSON body 64 MiB, support bundle 8 MiB, view-insertion ack 16 KiB, campaign upload up to the
backup expanded limit, 30 s idle and header budgets, a body budget of 60 s plus 1 MiB/s, 256 concurrent connections,
and 128 MiB of buffered JSON shared across all connections. Repeated framing or trust headers are refused.
`PublicIngressRoutes` (`/health`, `/`, `/index.html` and the two logo assets) stay public; other routes pass
`IngressAccessRefusal`. Apache on 8089 and direct local tools on 5101 both arrive at this one listener.

### CampaignDataGate and lock order

`CampaignDataGate` is a process-wide `ReaderWriterLockSlim` (no recursion):

- Ordinary requests take a read lease in `EnterCampaignRequestAccess` and record a `CampaignRequestLease` with the
  current `CampaignReplacementGeneration`, so work started before a campaign replacement can detect it.
- Exempt (no lease): `/health`, the Control Center shell and assets, `/api/shutdown`, `/save-sync/*`,
  `/api/save-sync/*`, the campaign export/import/delete/delete-all/cleanup routes, `/tests/*` except `/tests/live*`,
  and `/verification/*`. The exempt routes that change campaigns take the write lock themselves: Save Sync register,
  load and finalize (`Persistence/SaveSync.cs`) and the campaign backup operations (`Persistence/CampaignBackups.cs`).
- The exemptions are declared on the routes (`.WithoutCampaignGate()`) and as exempt areas (`CampaignGateExemptArea`) in
  the module route files; `EnterCampaignRequestAccess` asks the route table. They apply to the path for every method,
  registered or not.

Documented lock order, outermost first: `CampaignDataGate`, then `CampaignBackupMutationLock`, then
`DeleteAllScheduleLock` (innermost; never held across I/O or waits). Cold per-campaign schema initialisation takes
`FileLock` (also exposed as `SettingsSaveGate`) before `CampaignSchemaLock`; `OpenCampaignConnection` throws if
the reverse order is attempted. Other module-local lock orders are documented in comments at their call sites (for
example `Dialogue/Actions/PlayerActionIntents.cs`, `Relationships/WorldRelationshipModel.cs`, `Platform/VerificationLab.cs`).

## Storage

PostgreSQL is the only database (Npgsql). `ReignPostgreSqlOptions.FromEnvironment()` reads `REIGN_DB_*`; the product
database is `reign` and validation runs use `ReignValidation`.

- `reign_meta` schema (`database/reign_meta.sql`, embedded): `storage_version`, `campaign_registry`,
  `save_sync_snapshots`, `migration_runs`, the `final_gauntlet_*` controller tables (kept outside campaign schemas so
  a Save Sync rollback does not erase them) and helper functions such as schema cloning. Startup applies it in one
  transaction under `pg_advisory_xact_lock`; `RequiredDatabaseSchemaVersion` (currently 2) must match, and a newer
  database is refused. `/health` reports the applied and required versions.
- One schema per campaign: `reign_campaign_<hash>` (`CampaignSchemaName`).
  `ReignPostgreSqlStorage.OpenCampaignConnection` creates and registers it in `campaign_registry` on first use and
  sets `search_path` to the campaign schema, `reign_meta`, `public`, so module SQL uses unqualified table names.
- Save Sync snapshots: `reign_save_<hash>` schemas recorded in `reign_meta.save_sync_snapshots`, created by
  `CloneCampaignToSnapshot` and restored transactionally by `RestoreCampaignSnapshot`. Campaign JSON files, portraits
  and vectors follow the policy in [docs/server/SAVE_SYNC.md](server/SAVE_SYNC.md).
- Per-campaign tables are created lazily. The first `Program.OpenCampaignConnection` for a campaign runs the core
  `EnsureMemorySchema`, `EnsureCategorizedMemorySchema`, `EnsureWorldHistorySchema` and `EnsureSemanticMemorySchema`
  under the schema locks and caches the result (`CampaignSchemaReady`, checked against `schema_meta`). Every other
  module calls its own `Ensure<Feature>Schema(connection)` when a route first needs it, with
  `CREATE TABLE IF NOT EXISTS` DDL and `schema_meta` revision markers (`IsPostgreSqlComponentSchemaReady`). There are
  about 255 `CREATE TABLE` statements in 88 source files. Each module README lists the tables it creates.
- Files under the data root (`REIGN_DATA_ROOT`, else `data/` beside the executable): `settings.json`, prompts,
  logs, campaign folders, Save Sync stores and vectors. Secrets go through `LinuxSecretVault` /
  `SettingsSecretVault`; shared provider keys come from the global key bridge (`Platform/GlobalKeyBridge.cs`).

## Verification compiled into the server

The shipped server contains its own test system. It is not a separate test project:

- Self-test suites are static `Run*SelfTests` / `Run*Tests` / `Run*Checks` methods in `*SelfTests.cs`,
  `*Tests.cs`, `*ContractTests.cs`, fixture and replay files inside each module folder. Some product types expose
  `*ForTests` hooks used only by these suites.
- `Platform/VerificationLab.cs` aggregates them into tiers (`quick`, `offline`, `live-llm`, `game`, `all`) and named
  suites (`--suite`, for example `contracts`, `pipeline`, `memory_precision`, `government`, `spymaster`,
  `social_reputation`, `campaign_command`, `rebellion`, `shadow_world`, `long_run`, `provider_faults`,
  `war_economy_options`, `interaction_architecture`, `baselines`). `Platform/ArchitectureSelfTests.cs` holds
  architecture and source-text contract checks.
- Command-line entry points in `Main`/`RunServer`: `--run-verification` (with `--tier`, `--suite`, `--seed`,
  `--repeat`, `--fail-fast`, `--json`), `--cancel-verification`, `--run-tests`, and about 25 targeted flags such as
  `--run-diplomacy-tests`, `--run-world-history-tests`, `--run-world-history-stress-tests`,
  `--run-rebellion-tests`, `--run-world-test-tests`, `--run-social-balance-harness-tests`, `--run-court-tests`,
  `--run-trait-percentage-tests`, `--run-motive-conversation-tests`, `--run-sovereign-demeanor-provider-tests`,
  `--run-prompt-caching-tests`, `--run-conversation-scene-state-tests`, `--run-rumor-tests`,
  `--run-relationship-tests`, `--run-pregnancy-choice-tests`, `--run-notable-mbti-tests`,
  `--run-relationship-simulation-tests`, `--run-save-sync-tests`, `--run-character-editor-tests`,
  `--run-portrait-derivative-tests`, `--run-shared-portrait-generation-tests` and
  `--run-portrait-provider-adapter-tests`.
- HTTP test routes are part of the router: `/verification/*`, `/tests/*` (test cases, NPC and party dialogue audits,
  the final conversation gauntlet, live interaction tests, campaign-command and social-balance harnesses, replay
  corpus) and a few module routes such as `/court/tests`, `/rebellions/tests`, `/world-history/tests`,
  `/world-test/tests` and `/arrests/test/contracts`.
- `tests/ReignVerification` is a thin CLI that refuses to run outside the isolated validation environment and
  forwards to `ReignServer.dll --run-verification`. `tests/ReignLiveTest` is the live-test controller used against a
  running game; its feature folders mirror server modules. See [docs/server/VerificationLab.md](server/VerificationLab.md).
- Never run these directly. All builds and tests go through the canonical Reign MCP validator in the paired client
  checkout (see `AGENTS.md`).

### Test-free build flavour (opt-in)

The default build is unchanged and still ships everything above. Building with
`-p:ReignExcludeVerification=true` defines `REIGN_EXCLUDE_VERIFICATION` and removes the compiled-in test system:

- Sources left out: `src/**/*SelfTests*.cs`, `src/**/*Tests.cs`, `src/**/*Fixtures.cs`, `Platform/VerificationLab.cs`
  with its `VerificationLab.Contracts.cs`, `VerificationLab.Suites.cs` and `VerificationLab.Support.cs` partials,
  `Platform/VerificationSourceLocator.cs`, `Platform/CodexPerformanceVerification.cs`,
  `Court/CapitalAmbassadorTestSystem.cs`, `Portraits/BackgroundPortraitContracts.cs` and
  `Dialogue/Gossip/PersonalGossipCompositionProbe.cs`.
- `#if !REIGN_EXCLUDE_VERIFICATION` guards the product call sites that reach them: the `Run*SelfTests` aggregators
  inside product files, the `--run-*` / `--cancel-verification` dispatch in `RunServer` (the flavour refuses those
  flags with exit code 1 instead of starting the server), the `/tests/*` and `/verification/*` route
  registrations, and the module self-test routes (`/court/tests`,
  `/court/foreign-ambassadors/test`, `/rebellions/tests`, `/world-history/tests`, `/world-test/tests`,
  `/arrests/test/contracts`, `/gossip/test`). Those routes take the normal "Unknown route." 404 path.
- The live-test bridge (`WorldSimulation/LiveInteractionTest.cs`), the final conversation gauntlet store and provider
  budget, and the social-balance harness stay compiled because product paths consult them (prompt-override
  authorization, relationship-worker pauses, the LLM retry budget); only their verification-only methods are guarded.
- The validator still builds and tests the default flavour. Nothing ships the test-free flavour yet.

## Vector worker

`services/vector-worker/worker.py` is a local HTTP service (default loopback port 5102) for semantic memory:
FastEmbed ONNX `BAAI/bge-small-en-v1.5` embeddings with local Qdrant, ColBERT late-interaction discovery, memory
selection/reranking and dialogue claim validation helpers. The server owns its lifetime: the semantic memory
subsystem (`Dialogue/Memory/SemanticMemory.cs`) starts it with `REIGN_VECTOR_PYTHON` against the data directory, monitors
`/health`, and stops it on shutdown. When semantic retrieval is unavailable, memory retrieval falls back to
PostgreSQL full-text search. See
`services/vector-worker/README.md`.

## Linux / WSL deployment

The server runs in its own `ReignServer` WSL distro (Debian):

- `scripts/reignctl` (installed to `/usr/local/bin/reignctl`) owns the lifecycle: `start`, `stop`, `status`,
  `prepare` (pinned dependencies and models), `activate` / `rollback`, `rollback-list`, `repair-permissions`,
  `configure-web` and `global-keys-enroll`. Public `start`/`stop` delegate to `reignserver.service`
  (`scripts/reignserver.service`), whose ExecStart/ExecStop call back into `reignctl`.
- `scripts/reign-control.py` is the Apache CGI behind the Control Center's service controls; `scripts/reign_versions.py`
  manages retained runtime versions; `scripts/install-reign-control.sh` installs these with recoverable begin, restore
  and commit steps.
- Apache exposes port 8089 and forwards to the server on loopback 5101; PostgreSQL uses 5432.
- Source lives in `/var/www/html/ReignServer`, retained executables in `runtime/` (`runtime/current` is the active copy),
  persistent data in `data/`.
- `scripts/Build-Linux.py` builds the public Linux artifact; `scripts/Deploy-LocalWsl.ps1` deploys a validated
  artifact locally. The full procedure is in `AGENTS.md` and `scripts/skills/reign-server-wsl-deploy`.

## Modules

Each folder under `src/Modules` has a README listing its routes, the tables it creates, its self-test suites and its
dependencies on other modules.

| Module | Role |
|---|---|
| [Platform](../src/Modules/Platform/README.md) | `Program.cs` host and router, the area services (`LlmProviderService` under `Providers/`, `SettingsService`, `PromptService`, `ActionPipelineService` and others), settings, providers, Codex runtime, diagnostics, VerificationLab |
| [Persistence](../src/Modules/Persistence/README.md) | PostgreSQL storage, `reign_meta`, campaign backups, Save Sync |
| [Characters](../src/Modules/Characters/README.md) | identity, character editor, narratives, NPC lives, notables, tavern house |
| [Dialogue](../src/Modules/Dialogue/README.md) | conversation pipeline, memory, prompts, gossip, communications, final gauntlet |
| [Relationships](../src/Modules/Relationships/README.md) | relationship director and lifecycle, MBTI, correspondence, family, rulers |
| [Reputation](../src/Modules/Reputation/README.md) | rumors, social and court reputation, public standing |
| [Diplomacy](../src/Modules/Diplomacy/README.md) | world diplomacy director, political pressure, clan accords |
| [Government](../src/Modules/Government/README.md) | government speaker, hearing and crown-decision dialogue |
| [Court](../src/Modules/Court/README.md) | court sessions, dockets, ambassadors, castle chat, family chambers |
| [Spymaster](../src/Modules/Spymaster/README.md) | spymaster office routes |
| [Rebellion](../src/Modules/Rebellion/README.md) | rebellion director and negotiations |
| [WorldSimulation](../src/Modules/WorldSimulation/README.md) | world history, world test, campaign commands, social events, arrests, live tests |
| [Portraits](../src/Modules/Portraits/README.md) | portrait generation and providers, chat view, outfits, shared portraits |

## Rules every change keeps

- The wire is a contract: route, method, headers and JSON shape stay compatible unless a compatible server change
  lands first. New capability fields are additive.
- Hold the documented lock order; never take `CampaignDataGate` inside another campaign or backup lock.
- Every new campaign table needs its `Ensure*Schema` call on the paths that use it, and must be covered by Save Sync
  (it lives in the campaign schema unless it is controller state).
- Optional providers, the vector worker and other services fail cleanly when absent.
- Build and test only through the canonical validator.

## Direction (2026-11 restructuring programme)

These are planned, one reviewed PR at a time and without behaviour change. Step 1 has landed as an opt-in, step 2 has
landed, steps 3 and 4 have partly landed, and step 7 is partial (see each step); update this document in the same PR as any step that
lands.

1. A verification build flavour: `*SelfTests*`, VerificationLab, test routes and the `--run-*` flags compiled only
   into a verification build (using a csproj condition like `ExcludeCourt`), with the validator building that flavour.
   Landed as an opt-in: `ReignExcludeVerification=true` builds the test-free flavour (see
   [Test-free build flavour](#test-free-build-flavour-opt-in)); the default build keeps everything.
2. A route table: per-module route registrations (method, path, handler, gate policy) replace the `HandleClient`
   chain, and gate exemptions are declared on the routes instead of in a separate list. Landed: every route is
   registered in the table and the chain is gone.
3. Shared helpers (`ReadString`, `TestDict`, `QuerySql`, `ExecuteSql` and similar) move to a static `ServerCore`.
   Partly landed: `Platform/ServerCore.cs` holds the stateless helpers (the `Read*`/`TestDict`/`TestList` family,
   `FirstNonEmpty`, the shared `Json` serializer, `UnwrapJsonResponse`, `TryParseStrictJsonObject`,
   `TryParseJsonObject`, `CanonicalJson`, `CanonicalizeJsonValue`, and the stateless PostgreSQL helpers
   `AddSqlParameters`, `IsTransientDatabaseContention`, `IsStalePostgreSqlPreparedPlan`, `ResetPostgreSqlPreparedPlans`,
   `IsPostgreSqlSchemaMutation`, `AcquirePostgreSqlTransactionMutationLock`, and every overload of `Clamp`,
   `Sha256Hex`, `ParseInt` and `WriteText`, whose overloads used to sit in different modules, plus the pure
   `HttpStatusText` that `WriteText` uses; the `ParseInt(string, int)` overload stays verification-build only). A `global using static` makes them
   callable unqualified from every type. Helpers that touch `Program` state (`QuerySql`, `ExecuteSql`, campaign
   connections, settings) stay in `Program`. Every overload of a moved name moves with it, so no method group is split.
4. The `partial class Program` is split per module into `<Module>Service` and `<Module>Store` classes, leaf modules
   first. Landed for every domain module: Spymaster (`SpymasterService`), Rebellion (`RebellionService`), Government
   (`GovernmentService`), Diplomacy (`DiplomacyService`), Court (`CourtService`), Portraits (`PortraitsService`),
   Reputation (`ReputationService`), Characters (`CharactersService`), WorldSimulation (`WorldSimulationService`),
   Relationships (`RelationshipsService`), Persistence (`PersistenceService`) and Dialogue (`DialogueService`) are
   their own
   `internal static partial class`es, in their existing files. They
   call back into `Program` with explicit `Program.` qualifiers; `Program` and the other modules call them as
   `<Module>Service.X`, and the members used across that boundary are `internal` (every overload of a name together).
   Each module's static state (locks, caches, job state, counters) moved with it and is still a single static
   instance; its field initializers are side-effect free, so initializing them lazily with the service class instead
   of with `Program` does not change behaviour. The module route files (`<Module>Routes.cs`) stay part of
   `Program` and name the service handlers. Test classes that were already separate types (for example
   `CastleChatSelfTests`) are unchanged apart from qualifiers. None of these modules has SQL-only code that separates
   cleanly without moving code between files, so there is no `<Module>Store` yet. One method group stays split on
   purpose: the private `V` factories in `CharactersService` (`string, int, bool`) and `RelationshipsService`
   (`int, int, int, int`) each build a nested type of their own module, are called only from their own file, and
   their parameter lists cannot convert into each other. P3-3 finished the split: the module route files became
   `<Module>Routes` classes, the domain handler files became service files, and the rest of Platform moved into area
   services (`LlmProviderService`, `SettingsService`, `PromptService`, `ActionPipelineService`,
   `ConversationResponseService`, `CampaignFileService`, `OperationsService`, `VerificationService`), so `Program`
   keeps only the host, bootstrap and routing (see [Process composition](#process-composition)).
4a. Data layer (P3-1): `Platform/Data/ReignDatabase.cs` is the data core (`OpenCampaignConnection`, the campaign
   schema caches, `ExecuteSql`, `QuerySql`, `EnsureDatabaseColumn`; formerly `Program.CampaignSql.cs`), and the
   low-level PostgreSQL types live in `Persistence/Data`. Every module owns its SQL under `Modules/<Module>/Data`:
   `<Module>Store` holds its statements, `<Module>Schema` its `CREATE`/`ALTER`/`DROP` and column DDL, and the
   `<Module>SelfTestStore`/`<Module>SelfTestSchema` classes (files named `*SelfTests*`, compiled only into the
   verification build) hold the statements used only by self-tests. Module code calls named store methods
   (for example `CourtStore.SelectCourtPlots(connection, parameters)`); a statement's text is kept verbatim, and
   the non-literal pieces of a composed statement are passed as arguments in their original order. Statements a
   module composes at run time use the store's `QuerySql`/`ExecuteSql` pass-throughs with text kept in the store.
   The `Ensure*Schema` methods still run the same DDL in the same order, so the fresh-install schema is unchanged.
5. Per-campaign versioned migrations: each module's `Ensure*` DDL becomes ordered migrations under one runner,
   following the `reign_meta` pattern, and runtime `Ensure*` calls leave hot paths.
6. Typed request and response DTOs in `Reign.Core.Contracts` for client-facing routes, module by module.
7. CI: extend the existing `.github/workflows/reign-channels.yml` (today it verifies the promotion path and builds
   the public Linux server with `scripts/Build-Linux.py`) to run the server tests and MCP tests.
