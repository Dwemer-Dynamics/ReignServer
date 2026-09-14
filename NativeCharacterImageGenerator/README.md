# Bannerlord Native Character Image Generator

Resident snapshots marked `reign-encountered-resident-v1` use the complete native tableau (`ai_source_resident_full_outfit_v1`, 768x1024) so headwear, clothing and footwear are all visible to the image provider. Ordinary portraits retain the close WAN preset described below. Resident equipment, including intentionally empty slots, is preserved through final rendering; it never receives stock clothing. The background result records the selected framing hash and actual headgear-suppression policy.

Standalone character viewing and source-image rendering from a user's locally installed *Mount & Blade II: Bannerlord* assets and Bannerlord Reign roster data. Fresh sources are rendered by a native TaleWorlds worker; `Bannerlord.exe`, a campaign, a save, and the visible game UI are not started. The Reign server is not required.

Tavern house snapshots explicitly select `portraitSourceProfile=ai_source_resident_full_outfit_v1` to preserve the complete current outfit and footwear without claiming an encountered-resident identity. The existing background source command accepts this selector and the ordinary `ai_source_wan_portrait_v5` profile; unknown or non-string selectors fail before rendering. The complete-outfit profile renders at 1536x2048 and outputs 768x1024 with crop scale 1, center 0.5 and pitch 0. `PortraitSourceCharacterTests` verifies this route, exact native body/physique/outfit retention, and invalid-profile rejection.

For a prepared multi-character source pack, the existing render-only host also accepts the version-3 batch manifest produced by `NativeEngineRenderService.RenderBatchAsync` through `REIGN_NATIVE_PORTRAIT_JOB`. Each version-4 request supplies an exact native body, equipment, output and status path. One host retains the native engine for all requests; `continueOnError` preserves per-character failures in the batch status. Use only validated host/module artifacts and task-owned staging paths, with `BANNERLORD_GAME_PATH` bound to the local installation. This is source-image authoring, never a campaign or provider test. The background App command remains a single-character command.

This repository contains code only. It does not contain or redistribute TaleWorlds meshes, textures, packages, or other game content.

## Character Studio

Open `publish\app\Bannerlord.NativeCharacterImageGenerator.App.exe` or use the desktop shortcut named **Bannerlord Native Character Image Generator**.

The Windows interface:

- includes an **Appearance Lab** that creates several approximate native Bannerlord candidates from a written physical description, a reference portrait, or both;
- extracts a bounded semantic appearance intent instead of asking a model to invent raw Bannerlord sliders, then deterministically maps that intent onto culture/sex-valid native templates and constrained body-key controls;
- renders every candidate through the existing render-only TaleWorlds worker, supports stable seeds and rerolls, and exports the selected body key and materialization fields without creating a campaign hero;
- loads concrete hero definitions from Native, the War Sails `NavalDLC`, and Reign;
- merges Bannerlord Reign's last saved campaign portrait roster, including dynamically created heroes;
- provides a scrollable roster and name, culture, or native-ID search;
- retains Reign's cached native `source.png` as a separate reference view;
- generates a brand-new PNG from body and equipment data with Bannerlord's native `CharacterTableau` renderer, without reading the cached portrait;
- always suppresses the Head equipment slot in fresh sources so native hair and the complete face remain visible;
- resolves native civilian equipment, body properties, meshes, materials, and textures;
- keeps the rotatable standalone 3D renderer as a secondary preview;
- opens at a head-to-waist portrait zoom, allows full-body through head-close-up zoom, and saves the visible crop as PNG.
- builds a complete source set in one persistent engine session, with per-character `source.png` folders, progress/cancel controls, and a `manifest.json` that records every success or failure.
- builds or resumes a separate AI-ready 1,260-character `_shared` source package without contacting the Reign server or changing Reign's installed portrait cache.

The first startup indexes the relevant native asset packages and may take a few seconds.

### Appearance Lab analysis providers

Written descriptions always work through the Studio's built-in deterministic analyzer and require no network or Reign server. Optional model-assisted text and portrait analysis uses a provider-neutral OpenAI-compatible adapter. Configure it through process or user environment variables:

```text
NATIVE_CHARACTER_AI_API_KEY=...
NATIVE_CHARACTER_AI_MODEL=gpt-4o-mini
NATIVE_CHARACTER_AI_ENDPOINT=https://nano-gpt.com/api/v1/chat/completions   # optional
```

`NANO_GPT_API_KEY` is also recognized as a key fallback for compatibility with an existing NanoGPT setup. Provider-specific HTTP code is isolated from the mapper. API keys and reference-image base64 are never written to status text or logs.

The same endpoint, model, and API key can be entered under **Optional helper LLM settings** in the Appearance Lab. A key entered there is held in memory for the current Studio session only; it is not saved to disk. This makes the complete prompt-to-native-preview workflow testable from the Studio without coupling it to Reign settings.

If an upstream LLM already produces consistent physical details, prompt it to return `bannerlord_appearance_intent_v1` JSON and paste that response directly into the Studio. The Studio detects and validates this contract before considering any analyzer; it does not send the structured response through a second LLM. This is the preferred future Reign integration path because it avoids reinterpretation and keeps the semantic-to-native mapping deterministic.

If vision is unavailable, an image-only request fails with a clear configuration message. If a description was also supplied, the Studio explicitly falls back to that description rather than claiming the image was analyzed.

The mapper is deterministic for the same intent, seed, candidate index, and reroll number. A reroll preserves the semantic intent and root seed while sampling another bounded set of native candidates. All previews are render-only; rejected candidates do not write Bannerlord campaign state.

### Export contract

**Export Selected Appearance JSON** writes `bannerlord_native_character_v1`. The contract is independent of WPF and contains:

- mapper version, stable seed, candidate index, and the native character used as a culture-valid basis;
- the complete `bannerlord_appearance_intent_v1` semantic intent and per-field confidence data;
- sex, culture, age, weight, build, explicit native body key, and civilian equipment assignments.

The contract deliberately contains no Reign court record, petitioner ID, campaign hero ID, or server/UI dependency. A future Reign materializer can consume the native appearance fields and add its own idempotent petitioner-to-hero mapping without importing Studio UI code.

## Reign integration and fidelity

All Reign character portrait requests now use this background pipeline without first capturing an in-game widget. The optional `--character-snapshot <path>` argument accepts `reign-native-portrait-snapshot-v1` JSON captured on the game's main thread. Its campaign/hero identity must match the command; its explicit facial body key, age, gender, weight/build and current civilian clothing override saved-catalog appearance. Missing equipment alone never blocks a portrait: the renderer supplies stock clothing while the AI prompt controls the final outfit. Without a snapshot, offline Control Center requests resolve the persisted roster; shared requests resolve the authored catalog. Scenery remains separate. Legacy source-mode settings no longer bypass required native generation.

Reign persists an offline portrait roster during its normal campaign identity synchronization. Each row contains the live hero ID, character-object ID, body key, weight/build, current civilian equipment, native character code, faction colors, and portrait cache key. The Studio reads that file later, so Bannerlord can be closed.

The shipped Reign server package includes the self-contained Studio runtime under `server\app\native-portrait-generator`. For Portrait Images requests, Reign calls its `--reign-render-portrait-source` background command with the exact campaign and character identity. The command opens no Studio or Bannerlord window, starts no campaign, and writes a fresh opaque 768x1024 `ai_source_wan_portrait_v5` source for that one request. Reign defaults to required mode and fails closed if this bundled runtime or its native render host is unavailable; an optional development path override does not replace the packaged default. TaleWorlds assemblies are not redistributed: the host resolves the matching assemblies from the user's installed game.

After any configured image provider returns, Reign applies the same provider-independent product gate: one centered face, original full-body framing, and an approved 2:3 through 3:4 portrait canvas. Supported extra-tall provider canvases are face-anchored and normalized before storage. Rejected output never replaces the existing portrait. Scenery requests stay on their separate source, prompt, size, and model profile and do not invoke this portrait-only pipeline.

The **Cached game capture (reference)** view displays Reign's locally cached `source.png`. It is retained for comparison and never supplies pixels to fresh generation.

The **Generate new native source** view starts the installed render host, loads TaleWorlds' native engine and asset modules, creates a `CharacterTableau` from the selected character's body and civilian-equipment data, writes a new PNG under `%LOCALAPPDATA%\Bannerlord Native Character Studio\GeneratedSources`, and exits. The worker runs on a private Windows desktop, so Bannerlord's loading surface is not shown. Its job contract deliberately omits the Head slot as well as weapons, horses, and harnesses.

**Build ALL Source Images** sends the complete Native + War Sails + Reign catalog to one render worker. The TaleWorlds object registry, meshes, textures, and tableau renderer remain loaded while every requested character is captured, which avoids paying engine startup cost for each image. Each final image uses maximum source zoom with extra upward headroom and is saved as `<Name> (<native-id>)\source.png` below the selected build folder. The manifest includes module counts and preserves failed rows so missing coverage is explicit.

### AI-ready shared source cache

**Build Full AI Source Cache** uses the canonical installed XML and Reign's shipped profile library to build exactly 397 Native lords, 78 Native story/special characters, 65 War Sails heroes, and 720 Reign court characters. **Resume Source Cache Build** revalidates source, metadata, catalog, and render hashes and renders only missing or stale entries. **Open Cache Folder** opens the most recent package.

The standalone package has this contract:

```text
Reign Shared Portrait Source Cache - <timestamp>\
  build_manifest.json
  build_manifest.tsv
  _shared\
    Rhagaea (lord_1_14)\
      source.png
      portrait_input.json
      lord_export_info.txt
```

The default WAN portrait preset renders at 1536×2048 through one persistent TaleWorlds worker and performs a high-quality 2× downsample to an opaque RGB 768×1024 PNG on black. Its eye-level camera framing and direct native gaze control keep the character looking into the lens in a close head-and-upper-torso composition with extra headroom. A soft camera-aligned key and fill light illuminate both eyes and facial planes, with a restrained rim light separating dark hair from the black background. A one-time first-frame warm-up prevents asset-streaming captures before clothing textures are ready. The previous 3072×3840 to 1129×1075 quality preset remains selectable as a legacy fallback. Both presets validate below 10 MB and always omit headgear, weapons, horses, and harnesses while retaining the canonical civilian body, leg, glove, and cape slots.

Before publication, the worker safely preloads scene resources and requires two independently clean captures. An 8/16-pixel periodic-edge validator rejects Bannerlord's incomplete tiled material placeholders and retries up to 20 times. The persistent tableau scene also removes the preceding renderer-owned light rig before installing the next one, preventing exposure from increasing across a long batch. These checks are part of the versioned render-contract hash, so a cache built by an older contract is never silently accepted by Resume.

`portrait_input.json` records canonical identity, age/gender/culture, occupation, clan/station and provenance, body and face data, exact visible equipment, profile and physical-confidence data, render framing, and hashes needed by the later AI pass. The 1,117 characters covered by Reign's profile library use those shipped profiles; 65 War Sails and 78 special Native records explicitly use the neutral physical-confidence score of 50.

Reign and the offline worker share `reign_court_shared_v1`: parents are generated first, children inherit from their generated parents, and both paths use the same stable seeds, face constraints, culture-valid non-bald hair selection, and 720-face uniqueness check.

This workflow makes no AI or network request, creates no prompt or derived portrait files, and never writes into the installed `PortraitCache\_shared` directory. Cache installation and AI generation remain separate future operations.

Base-game characters can be generated directly from installed XML. Reign campaign characters and their exact current civilian assignment become available from Reign's saved portrait roster after its normal in-game identity sync has run at least once with this version installed. The Studio then consumes that saved data later with the game closed.

The **Rotatable offline 3D** view remains useful for turning a character or inspecting uncached assets. It reads geometry, clothing assignments, body-key controls, UVs, and textures from the installation but uses its own CPU lighting and pose.

## Requirements

- Windows with a locally installed, legally obtained copy of Bannerlord.

The installation is auto-detected in common Steam locations. Otherwise pass `--game` or set `BANNERLORD_GAME_PATH`.

## Command-line renderer

The lower-level CPU renderer remains available for development and asset inspection.

```powershell
dotnet run --project src\NativeCharacterImageGenerator -- render `
  --character lord_1_14 `
  --zoom 3.3 `
  --output artifacts\Rhagaea.png
```

Useful commands:

```powershell
dotnet run --project src\NativeCharacterImageGenerator -- --help
dotnet run --project src\NativeCharacterImageGenerator -- --list-morphs
dotnet run --project src\NativeCharacterImageGenerator -- --verify-ai-cache-catalog
```

The native-engine smoke/resume verifier is available for development:

```powershell
dotnet run --project src\NativeCharacterImageGenerator.SourceCacheVerifier -c Release -- `
  --package artifacts\ai-cache-smoke
```

## Publish the Windows app

```powershell
dotnet publish src\NativeCharacterImageGenerator.App\NativeCharacterImageGenerator.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -o publish\app
```

## Design boundary

```text
Installed Bannerlord XML/TPAC + Reign saved roster/cache (read-only)
        |
        +--> fresh native CharacterTableau worker -> new PNG -> zoom/crop/save
        |
        +--> cached Bannerlord source capture -> reference/zoom/save
        |
        +--> native mesh/material parser -> rotatable CPU preview
```

The Studio does not modify game assets. Its installed native helper/module load the user's existing TaleWorlds files read-only; generated images and job status files are written only under the user's local application-data directory. Reign writes its roster and portrait cache while Bannerlord is running, and the Studio reads those persisted local files afterward.

## Third-party code

The TPAC parsing layer is a locally maintained fork of the MIT-licensed TpacTool library. BC7 texture decoding uses BCnEncoder.NET. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and `third_party/TpacTool/LICENSE`.
