# Reign Portrait Product Pipeline

## Shared portrait rebuilds (2026-09-05)

An existing shared `portrait.png` is the authoritative image-edit reference, resolved from its confined cache folder on the server. `portrait_input.json` supplies the saved native physique. The reference hash is bound to the AI image separately from the retained historical native-source hash. Rebuilds preserve face, outfit, coverage and scene while applying the physique layer; they skip the additional clothing redesign stage. Provider routing, single-face composition validation and derivative rules remain unchanged. Missing AI masters still use the native background generator. Shared generation no longer persists `source.png`. Catalog availability and Generate Selected do not depend on that obsolete file. Failed output writes/derivatives restore the previous product; a concurrently changed master blocks replacement.

Source cleanup is an offline, exact-file data operation only after validated deployment and a complete master/metadata inventory. Keep all AI masters, derivatives, prompts and historical receipts; never rewrite history to claim the old generation used physique guidance. Tests run through existing `physique_shared_*` contracts and manifest-selected Release validation. Paid regeneration and human visual acceptance remain separate cost-gated checks.

Portrait Images and Scenery are separate generation products. Only the Portrait Images profile uses this pipeline.

## Native identity source

Every campaign portrait request defaults to `portraitNativeSourceMode=required`. Reign invokes the packaged `native-portrait-generator\Bannerlord.NativeCharacterImageGenerator.App.exe` background command with the exact campaign ID and character identifiers. The helper reads the persisted Reign roster, launches the TaleWorlds render host on its private desktop, and creates an opaque 768x1024 PNG governed by `ai_source_wan_portrait_v5`. It does not launch the visible game, load a save, or mutate campaign state.

The normal server build publishes the self-contained generator, its native render host, and configuration into the server output. The build fails if either executable is absent. The host loads the matching TaleWorlds starter assembly from the user's installed Bannerlord copy; Reign does not redistribute or pin a local game binary. A developer may configure `portraitNativeSourceGeneratorPath`, but shipped installations resolve the bundled location first when no override is set.

## Cross-model output contract

Reign appends the same measurable composition contract regardless of provider or model and requests the portrait profile's canonical size. Provider output is accepted only when it is a portrait-oriented image with exactly one detected, horizontally centered face whose height and upper-frame placement match the original shared Grok portrait population. Supported extra-tall canvases are cropped to 2:3 around the detected face. The normalized PNG is the only image stored or returned.

If the identity source cannot be produced or the provider output fails validation, generation fails closed and the existing portrait remains active. Scenery requests bypass the native portrait source, portrait prompt contract, face gate, and normalization.

## Versioned product handoff

Successful portrait responses include a `reign-portrait-product-v1` receipt. It carries the normalized face box, detector and model, confidence and candidate count, canonical source and master dimensions and SHA-256 hashes, effective-prompt hash, provider provenance, and derivative contract. The response also returns the exact canonical source and expanded effective prompt used for the provider call.

The game commits `portrait.png`, `source.png`, `prompt.txt`, `portrait_input.json`, `.ai_generation.json`, all four display derivatives, and the derivative manifest as one rollback-safe transaction. New AI portraits require exactly one server-validated face and can never use `legacy_fallback`. If validation or any file replacement fails, the complete previous portrait product remains active.

The local `POST /portraits/derivatives/rebuild-fallback` maintenance route is idempotent and targets only campaign folders whose current manifest is marked `legacy_fallback`. It never changes a portrait master or any `_shared` portrait. Each repaired derivative set records the detector focus in its manifest; a repeated call finds no repaired entries eligible.
