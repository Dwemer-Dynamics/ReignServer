# Reign Server

Reign Server is the local web/API server for Bannerlord Reign. It hosts the browser control center, LLM routing, prompt management, character storage, portrait generation settings, action planning, diagnostics, audit logs, and Test Lab APIs.

This repo contains the server source. Runtime data, API keys, generated campaign folders, audit logs, and packaged app builds are intentionally ignored.

How the server is composed (entry point, router, campaign gate, storage, verification, deployment) is described in
[ARCHITECTURE.md](../ARCHITECTURE.md); each folder under `src/Modules` has a README for its routes, tables and
self-tests.

The native world-history database and claim-verification contract are documented in the client repository at
[ReignBeta/docs/WorldHistory.md](../../../Reign/ReignBeta/docs/WorldHistory.md).
The offline, live-LLM, and Bannerlord verification tiers are documented in [VerificationLab.md](VerificationLab.md).
The canonical agent-facing testing catalog, autonomous campaign procedure, save
safety rules, and restart recovery contract are documented in
[docs/agent/TESTING_TOOL_GUIDE.md](../../../Reign/docs/agent/TESTING_TOOL_GUIDE.md) in the client repository.

## Documents in this folder

- Platform and operations: [ControlCenter](ControlCenter.md), [GlobalApiKeys](GlobalApiKeys.md),
  [CODEX_APP_SERVER_PROVIDER](CODEX_APP_SERVER_PROVIDER.md), [ConversationDiagnostics](ConversationDiagnostics.md),
  [HighMemoryMode](HighMemoryMode.md), [SAVE_SYNC](SAVE_SYNC.md).
- Verification: [VerificationLab](VerificationLab.md), [FinalConversationGauntlet](FinalConversationGauntlet.md),
  [CapitalAmbassadorTesting](CapitalAmbassadorTesting.md), [KingdomEventTesting](KingdomEventTesting.md),
  [RebellionTesting](RebellionTesting.md), [SpymasterTesting](SpymasterTesting.md),
  [CodexRoleplayEvaluation](CodexRoleplayEvaluation.md).
- Dialogue and memory: [StructuredActions](StructuredActions.md), [ConversationGrounding](ConversationGrounding.md),
  [ConversationIntoxication](ConversationIntoxication.md),
  [ConversationResponseRecovery](ConversationResponseRecovery.md),
  [DialogueClaimValidation](DialogueClaimValidation.md), [MemoryPrecision](MemoryPrecision.md),
  [SpecialistMemory](SpecialistMemory.md), [MemoryContextExperiment](MemoryContextExperiment.md),
  [NpcMemoryRetrievalBenchmark](NpcMemoryRetrievalBenchmark.md), [Correspondence](Correspondence.md),
  [PregnancyContext](PregnancyContext.md).
- Characters and portraits: [CharacterConstructionPolicy](CharacterConstructionPolicy.md), [NpcLives](NpcLives.md),
  [TraitPercentages](TraitPercentages.md), [PORTRAIT_PRODUCT_PIPELINE](PORTRAIT_PRODUCT_PIPELINE.md),
  [PetitionerPortraitPromptLibrary](PetitionerPortraitPromptLibrary.md).

## Build

Use the workspace Reign MCP `reign_get_validation_plan` and `reign_validate`
tools. Do not invoke the project build commands directly. Deploy only artifacts
listed by the successful authoritative validation report.

The default local server address during development is:

```text
http://127.0.0.1:5101
```

Run deterministic verification without starting the web server:

```powershell
..\ReignBeta\server\app\ReignVerification.exe --tier offline --json
```

Drive a loaded campaign without MCM or screen automation:

```powershell
..\ReignBeta\server\app\ReignLiveTest.exe arm --minutes 30
..\ReignBeta\server\app\ReignLiveTest.exe status --json
```

Run a reusable versioned scenario (the installed controller defaults to headless presentation and guarded world effects):

```powershell
..\ReignBeta\server\app\ReignLiveTest.exe run .\ReignLiveTest\scenarios\memory-cross-mode.json --timeout 1800
```

A `checkpoint` step pauses before the next command. After saving or reloading the same campaign, use `ReignLiveTest.exe resume --run <runId>`; the fresh game instance is adopted without replaying completed turns.

## Safety

The provider-free, non-listening `--import-tavern-native-sources <absolute-manifest> --pack-campaign tavern_pack_<task-id>` command imports a fully completed authored native portrait batch into a confined local source store. It validates all 284 identities, native body/outfit/framing results and source hashes before atomic publication; identical retries are idempotent. `--import-tavern-native-sources help` prints CLI help. Use the [tavern source-pack procedure](../../../Reign/docs/agent/TAVERN_PORTRAIT_SOURCE_PACK.md) for validation, native-operation gates, process-local `REIGN_DATA_ROOT`, stopped installed-server import or isolated staging, and the separate provider/deployment gates. The ordinary portrait endpoint accepts only the resulting opaque source IDs in a dedicated task campaign. It does not accept arbitrary prepared image paths or write shared portraits.

- API keys should be saved through the server web UI only.
- Do not commit `data/settings.json`, campaign folders, logs, generated images, or packaged runtime builds.
