# Portraits module (server)

AI portrait and scene images: portrait generation and refresh, image providers (OpenRouter, Codex), vision checks,
face detection and physique, clothing and outfit changes, shared first-party portraits, portrait derivatives, the
chat view (conversation scene images and wardrobe), encountered-resident portraits, adult portrait rules, tavern
house scenes and native portrait source import. Portrait registration, status, request and diagnosis routes are
in `Portraits/Program.PortraitRequests.cs` (part of `Program`).

> Checked against the source on 2026-11-17. [ARCHITECTURE.md](../../../docs/ARCHITECTURE.md) describes the whole server
> and the restructuring direction. Feature docs:
> [PORTRAIT_PRODUCT_PIPELINE](../../../docs/server/PORTRAIT_PRODUCT_PIPELINE.md),
> [PetitionerPortraitPromptLibrary](../../../docs/server/PetitionerPortraitPromptLibrary.md).

## Main files

- Generation: `PortraitGeneration.cs`, `PortraitRefresh.cs`, `PortraitProviderFailure.cs`,
  `OpenRouterImageProvider.cs`, `CodexImageProvider.cs`, `ImageProfileControlCenter.cs`, `AdultPortraitGeneration.cs`,
  `EncounteredResidentPortraits.cs`, `NativePortraitSourceGeneration.cs`.
- Image analysis and codecs: `PortraitVision.cs`, `PortraitFaceDetection.cs` (loads the
  `version-RFB-320.onnx` face model from `portrait_models/` beside the executable; the source copy is in
  `resources/portrait-models`), `PortraitPhysique.cs`, `PortraitPhysicalConfidenceCatalog.cs`,
  `LinuxImageCodec.cs`.
- Clothing: `PortraitClothingCatalog.cs`, `OutfitChanges.cs`.
- Shared portraits and derivatives: `SharedPortraitGeneration.cs`, `SharedPortraitRebuild.cs`,
  `PortraitDerivativeJobs.cs`.
- Chat view: `ChatView.cs`, `ChatViewAppearance.cs`, `ChatViewImageCapabilities.cs`, `ChatViewWardrobe*.cs`.
- Tavern: `TavernHouseSceneGeneration.cs`, `PreparedTavernPortraitSources.cs` (the
  `--import-tavern-native-sources` command, handled before normal startup).
- `portrait-profile-ui-contract.mjs`: Control Center UI contract script.

## Service class

Every file except `Program.PortraitsRoutes.cs`, `Program.PortraitRequests.cs` and `LinuxImageCodec.cs` (its own `LinuxImageCodec` class) holds
`internal static partial class PortraitsService` (not a `Program` partial). It calls `Program` members with an
explicit `Program.` qualifier, and `Program` and the other modules call it as `PortraitsService.X`; the members used
across that boundary are `internal`. `Program.PortraitsRoutes.cs` stays part of `Program` and registers the service
handlers; the portrait registration, status, request and diagnosis routes in `Portraits/Program.PortraitRequests.cs` call the service
the same way. The module's static state (generation and derivative job state, the chat view and Codex image gates,
outfit change operations, the face-detection session and the vision model catalog) lives in `PortraitsService`.

## Routes

`/portraits/generate`, `/portraits/generate/status`, `GET /portraits/vision/models`, `/portraits/outfit/*`
(dispatched by `OutfitChangeApi`), `GET /portraits/shared-generation/catalog|status`,
`/portraits/shared-generation/open-folder|generate-one|delete-one|build-missing|cancel`,
`/portraits/derivatives/build-missing|rebuild-fallback`, `GET /portraits/derivatives/status`, `/chat-view/*`
(dispatched by `ChatViewApi`), `/tavern-house/scene`.

## Tables

`EnsureChatViewWardrobeSchema`: `chat_view_wardrobe_evidence`. The chat view's `chat_view_frames`, `chat_view_jobs`
and `chat_view_requests` are created in Dialogue (`ChatViewSources.cs`). Generated images live in campaign and
`PortraitCache` folders, not in PostgreSQL.

## Self-tests

`RunPortraitDerivativeSelfTests` (CLI `--run-portrait-derivative-tests`), `RunSharedPortraitGenerationSelfTests`
(`--run-shared-portrait-generation-tests`), `RunPortraitProviderAdapterContractTests`
(`--run-portrait-provider-adapter-tests`), `RunPortraitRefreshContractTests`, `RunPortraitVisionContractTests`,
`RunPortraitPhysiqueContractTests`, `RunOutfitChangeContractTests`, `RunAdultPortraitContractTests`,
`RunCodexImageProviderContractTests`, `RunOpenRouterImageProviderContractTests`, `RunPreparedTavernPortraitSourceTests`,
`RunChatViewContractTests` and the chat view checks, all `PortraitsService` members.

Test files compiled into the server: `AdultPortraitContractTests.cs`, `BackgroundPortraitContracts.cs`,
`ChatViewSelfTests.cs`, `ChatViewWardrobeCoverageSelfTests.cs`, `ChatViewWardrobeSelfTests.cs`,
`CodexImageProviderContractTests.cs`, `OpenRouterImageProviderContractTests.cs`, `OutfitChangeContractTests.cs`,
`PortraitPhysiqueContractTests.cs`, `PortraitRefreshContractTests.cs`, `PortraitVisionContractTests.cs`,
`PreparedTavernPortraitSourceTests.cs`.

## Dependencies

Other modules whose members this module references, most-used first: Platform (328), Dialogue (120),
Persistence (28), Characters (15), Relationships (4), Court (2).

Counts are approximate: distinct identifiers defined in exactly one other module and referenced per file, summed.
They show coupling, not a layering rule.
