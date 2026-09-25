# Reign Offline-First Verification Lab

## Conversation Diagnostics (2026-09-24)

The architecture memory fixtures now use complete retained records within the source budget and persist/reload their exact conversation rows before compaction, matching the existing source-provenance guard. This repairs stale fixture assumptions without changing production memory behavior. Diagnostic cases also cover background work after HTTP delivery, fixed visible-response timing, damaged-tail recovery and Codex generation timing through completion. Run the browser matrices from the paired Reign workspace (or set `REIGN_WORKSPACE`) so the sibling server checkout resolves the authoritative palette.

The existing artifact-bound `interaction_architecture` suite runs `diagnostic_*` cases in a unique store below isolated `VerificationDir`, with injected responses and no listening server/provider calls. It emits `browser-fixture.json` alongside the shared exact Control Center HTML. Use the two intercepted browser matrices described in [ConversationDiagnostics.md](ConversationDiagnostics.md). Fixtures cover empty and reasoning-only output, actual negotiation rejection/revalidation, physical retries, immutable paging, persistence, concurrency, failed storage, export and confined clearing. `reign.testing.json` owns the catalog; existing artifact retention owns test cleanup. Native delivery and paid provider acceptance are not established by these tests.

## Conversation continuity maintenance and isolated contracts

The `prompt_efficiency` suite's `pipeline.prompt_caching` check runs `RunConversationContinuitySelfTests` against isolated storage, without providers or a listener. It exercises accepted agenda/relationship history, source privacy, scene handoffs, whole-record selection, final prompt coverage/capacity, letters and the repair transaction. Run it only through artifact-bound `reign_run_offline_verification` and retain the successful canonical Release validation/hygiene report. See `conversationContinuity` in `reign.testing.json` and `docs/agent/testing/DIALOGUE_AND_PROVIDERS.md` for the procedure and coverage limits.

Maintenance entry point: `ReignServer --continuity-repair --manifest <absolute-json> --report <absolute-json>`. The default is a rolled-back preview. Use the checked-in `scripts/Repair-ConversationContinuity.ps1` wrapper with the validated executable hash; it handles exact campaign selection, stop-state checks and verified backup evidence for an explicitly authorized apply. Direct application additionally needs maintenance mode, matching reviewed manifest and backup hashes, an exact current timeline and unchanged source provenance. This is a non-listening transaction, not a provider request or historical event replay. Private manifests/reports/backups remain outside version control. Deployment, shutdown, game advancement and paid testing are separate authorizations.

For selecting among contract, Verification Lab, feature harness, disposable
campaign, and human acceptance layers, use the canonical
[testing tool guide](../../docs/agent/TESTING_TOOL_GUIDE.md) and the generated
`reign_get_testing_catalog` inventory.

The Verification Lab is the release-safety harness for Bannerlord Reign. It runs deterministic simulations before spending LLM tokens or opening Bannerlord, writes only to isolated test folders, and never mutates a normal campaign.

## Tiers

- `quick`: contracts, coverage inventory, XML, prompt budget, 126-day calendar, and existing diplomacy/history/rebellion baselines.
- `offline`: quick checks plus every canned pipeline case, subsystem self-tests, provider faults, all action types in the shadow world, atomic trades, rebellion lifecycle checks, persistence, and a seeded five-year simulation.
- `live-llm`: requires deterministic checks to pass, then runs a capped natural-language matrix through the production action gate and hidden planner. It does not use `@` or the old compliance path.
- `game`: writes a command for the Bannerlord module and waits for the disposable verification campaign to run the native action gauntlets.
- `all`: runs deterministic, live-LLM, and game tiers in order.
- `commit`: permanent alias for the fast `quick` gate.
- `full-regression`: permanent alias for `offline`.
- `release-candidate`: permanent alias for `all`.
- `exhaustive-final-review`: runs the offline prerequisite, then hands off to the one-pass Final Conversation Gauntlet.
- `human-immersion`: validates review tooling before a reviewer scores the blinded pack.

The offline aggregate checks contain many assertions. For example, `pipeline.canned_cases` currently runs every registered canned case and `rebellion.lifecycle_matrix` expands into its full lifecycle matrix.

## Running It

Temporary guest renewal regressions run through the existing artifact-bound `reign_run_offline_verification` tool with `tier=offline`, `suite=prompt_efficiency`, and a successful Release `validationRunId`. The `pipeline.prompt_caching` assertions exercise the captured Hulara twenty-more-day exchange, current agreement routing, native revision binding, consent/stale-context controls and receipt-informed prompts. The paired MCP tests also execute the client review-session policy. Neither suite invokes providers or changes a live campaign. Native lock cleanup, town entry and save/load require the separately enrolled disposable-campaign procedure in the client checkout's `docs/agent/testing/DIALOGUE_AND_PROVIDERS.md`.

For starting-population work, use `reign_run_offline_verification` with the successful Release `validationRunId`, `tier=quick` (or `offline`), and `suite=starting_children`. This focused, provider-free suite executes `contracts.starting_children`; the complete `contracts` suite also includes it. The suite proves planning and receipt replay, while native child aging and save acceptance require a separately authorized disposable new campaign. Runtime observations are available through `reign_get_live_test_status`, under `characterInitialization.startingChildren`; there is no command to seed an existing save.

From the packaged server folder:

```powershell
.\ReignVerification.exe --tier quick --json
.\ReignVerification.exe --tier offline --seed 1337 --json
.\ReignVerification.exe --tier offline --suite rebellion --repeat 10 --fail-fast --json
.\ReignVerification.exe --tier live-llm --live-case-cap 20 --json
.\ReignVerification.exe --tier game --game-timeout-seconds 900 --json
.\ReignVerification.exe --cancel
.\ReignVerification.exe --tier full-regression --json
.\ReignVerification.exe --tier release-candidate --json
```

The exhaustive conversation program is controlled by `ReignLiveTest.exe gauntlet`.
Its provider-consuming stages are never started by ordinary verification.
See [FinalConversationGauntlet.md](FinalConversationGauntlet.md).

The same controls are available on the server's **Verification Lab** tab. HTTP integrations can use:

- `POST /verification/run`
- `GET /verification/status`
- `POST /verification/cancel`
- `GET /verification/results`
- `POST /verification/replay`

## Storage And Safety

Mutable verification data lives under:

```text
server/app/data/tests/verification/
```

Each run receives a separate sandbox. Failed results and replay bundles are retained; successful routine results are pruned to the newest 20. Real campaign folders are read-only to the verifier.

The server package includes a small `verification_contracts` snapshot containing only the source contracts needed for inventory checks. It does not contain runtime campaign data or secrets. API keys, authorization headers, and image payloads are redacted or omitted from verification results.

## Game Tier

Use a disposable save restored from a clean snapshot. Start the game and load that campaign before running the `game` tier. The client polls for the command, prepares the existing action arena, runs one suite at a time, and reports the native results back to the server. The tier stops on failure or timeout.

The game bridge currently proves the native action gauntlets. Mission-only flows, screenshot comparison, full rebellion lifecycle play, portraits, encyclopedia capture, and every conversation surface remain separate live acceptance work and must not be inferred from an offline pass.

## Acceptance

- Deterministic checks must pass at 100%.
- Live LLM intent quality must be at least 90%, with 100% privacy, schema safety, refusal behavior, and false-positive prevention.
- Critical game-on smoke checks must pass at 100%.
- A completed offline run does not mark a feature complete when its native Bannerlord lifecycle is still untested.
# Codex SDK image compatibility

Catalog schema `reign-codex-image-compatibility-v1` adds explicit suite `codex_images`. Quick/offline runs are provider-free and reuse image adapter contracts. The separately authorized live-llm route uses only the signed-in Codex subscription and generates 1..3 bounded samples (`liveCaseCap`), with no campaign changes. Start through `reign_start_verification` after deployment and explicit subscription-usage approval; use `repeat=1`. Standard cancellation stops only the owned image helper. Inspect `codex-images/report.json` and the images beneath the run sandbox. Image validity is separate from human likeness/scene acceptance. See `docs/agent/TESTING_TOOL_GUIDE.md` for exact MCP routing and gates.


## OpenRouter providers (2026-09-09)

`reign-openrouter-providers-v1` adds independently saved NanoGPT and OpenRouter chat credentials and model selections while retaining Codex and custom APIs. `openRouterApiKey` is shared by OpenRouter chat and all four image profiles; blank/masked/null submissions preserve it, explicit clearing never falls back to another key. Legacy API keys migrate only to their endpoint owner once. `llmModelProfiles` stores only the twelve allowlisted model fields per provider; active flat model fields remain the runtime contract. OpenRouter uses its fixed HTTPS endpoints, prefixed chat model IDs, native reasoning controls including direct-selector exclusion, and reference-image requests to `/api/v1/images`.

Use the existing artifact-bound `interaction_architecture` offline suite and `contracts.image_generation_profiles` (included in quick `contracts`; the focused `codex_images` quick suite also includes all image adapter contracts). Injected image transport proves all five catalog models across four profiles, exact source reference bytes, endpoint/key selection, empty/error/echo rejection and timeout/missing-key behavior. `OpenRouterCatalogTests` checks catalog/help drift. No new tool, live profile, confirmation phrase or campaign mutation route is introduced. The existing GLM cache probe is enabled only for NanoGPT in the UI; its backend rejects non-NanoGPT endpoints before making a provider request.

The architecture suite emits `control-center.html`. Run `chat-provider-ui-contract.mjs` with that HTML path and an output directory; run `portrait-profile-ui-contract.mjs` on the same fixture. Both live under `src/Modules/` (Platform and Portraits respectively) and intercept all requests. The chat matrix covers four providers at 1672/1024/600, draft switching, all model roles, masked-key preservation and save/reload; the image matrix covers NanoGPT/OpenRouter/AtlasCloud and normal-only Codex. Reuse `control-center-ui-contract.mjs` for full-page palette/navigation regression. MCP has no browser-preview tool; these documented shared fixture harnesses are the fallback. Native Bannerlord, portrait-layering and approved-raster fidelity are not applicable to these web form changes. Provider-backed output still requires a configured OpenRouter key and separately authorized paid smoke testing.

Image presets are exact matches verified from `GET https://openrouter.ai/api/v1/images/models` on 2026-09-09: `openai/gpt-image-1`, `google/gemini-3-pro-image-preview`, `bytedance-seed/seedream-4.5`, `bytedance-seed/seedream-5-0-pro`, `x-ai/grok-imagine-image-quality`. GPT Image 1.5, WAN and Flux Kontext exact variants were absent and are not silently mapped to another model. Existing AtlasCloud/NanoGPT catalogs remain intact. See https://openrouter.ai/docs/guides/overview/multimodal/image-generation for request capabilities.

## Codex performance controls and isolated comparisons

`reign-codex-performance-report-v1` is cataloged under `codexPerformance` in `reign.testing.json`. Run `reign_run_offline_verification` with the exact successful Release validation run ID and suite `codex_performance` for injected runtime, conversation, settings migration, dispatch-cap, and report contracts. Run `interaction_architecture` for the shared Control Center fixture, then its three browser contracts at 1672, 1024, and 600 pixels with all traffic intercepted. This is a web control change: native artwork raster/fidelity checks do not apply.

The optional provider comparison is explicit-only: `reign_start_verification`, tier `live-llm`, suite `codex_performance`, the usual `start Reign verification` confirmation, and `codexPerformanceJson`. The JSON must name the exact model, caseIds, full baselineOptions and variantOptions, the single changedOption, a mandatory totalProviderCallCap from 1 through 1000, and confirmation `run bounded Codex performance comparison`. The run-wide cap covers every physical turn/start across primary generation, repair, tier retries, probes, judges and repeat passes. Discovery and thread creation are not generation calls. Cancellation/closed budgets prevent later dispatch. The suite uses isolated Verification Lab campaign data, preserves the selected live provider/settings, and is never included implicitly in aggregate live suites.

Model checking is separate: POST `/api/codex/models/verify` requires `maxRequests:1`, `bounded:true`, and confirmation `verify one Codex model request`. The Control Center explains usage before confirmation. Discovery remains free. A timeout remains inconclusive; request-model echoes do not establish actual served model identity. No user entitlement or shared Codex configuration is modified.

Reports separate baseline/variant latency and quality outcomes, retain unknown usage/fingerprints as unknown, and keep clean attempts, successful repairs, accepted repair overrides, and unusable results separate. Runtime/conversation diagnostics contain no reasoning or rejected draft text. GPT-5.4 access, actual Fast-mode service, and live roleplay quality require later user-authorized tests. See `docs/agent/CODEX_PERFORMANCE.md` for controls and limitations.

The provider comparison plan also requires `validationRunId`: the server checks its own assembly SHA-256 against that successful Release run's exact artifact before dispatch. Source and validation fingerprints come from that report; they are never inferred from model settings. Supply this ID inside codexPerformanceJson (or the CLI `--codex-performance-plan` JSON file). Ordinary quick/offline contracts do not require or authorize a provider plan.

Injected Codex contracts are CLI-only: use artifact-bound `reign_run_offline_verification`. The listening server refuses this injected suite so its test transport cannot intercept gameplay requests. The dedicated Codex browser script wraps the shared provider script; run that wrapper once, then the separate full Control Center matrix.

The MCP supplies its trusted workspace as `validationWorkspaceRoot` so an installed server can locate the exact report. CLI comparison plans may supply that exact Reign workspace explicitly. The report verifies the running assembly hash and records the actual runtime executable hash when its path is known; accepted replies are retained only for later human roleplay evaluation.

Codex comparison fixtures must identify a shipped character or include a completed character snapshot (constructed, traits, and a complete narrative when using narrative v5). Each isolated arm preserves the supplied character stack; fixture personality authoring is not part of the latency comparison. Both arms must keep the same reasoning mode and schema version. Served-model consistency uses authoritative runtime evidence; a request-model echo leaves verification unknown.
