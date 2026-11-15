# Reign Offline-First Verification Lab

The optional `memory_evidence_replay` `denseWorkerReplay` fixture uses an explicitly
started nonce-scoped loopback test worker, never the installed worker. It requires
high port40000-65000,32hex nonce,pinned bge/minilm model/tree and matching window;
the handshake requires isolated/no-external-network/model-loaded identity.
Only non-listening ReignValidation can activate it. It indexes source-linked
verbatim memory projections and runs production semantic HTTP, SQL fusion and
final writer packing. It cannot combine with supplied writer responses/continuations
or other captured model helpers. No live campaign/provider/native effect or release
certificate is granted. The paired `DIALOGUE_AND_PROVIDERS.md` full-pipeline section
defines source grading and separate first/repeated latency/RSS evidence.

Private drinking actions (2026-10-08): `conversation_intoxication` includes `RunConversationDrinkActionSelfTests` (`private_drink_*`) on the live reducer/final acceptance path. Coverage includes expressive prose, typed consumption/ordered cup operations, malformed data without reply blocking, missing cup identity, local rebasing, replay/concurrency, recovery and isolated PostgreSQL snapshot restore/cleanup. Historical parser contracts remain separate. Run `prompt_efficiency` for output schemas, repair retention, accepted-unit individual/group rollback/replay and the independent 6,144-token allowance. Use the exact successful Release artifact through `reign_run_offline_verification`; no provider, live campaign or native advancement. See [ConversationIntoxication](ConversationIntoxication.md) for current authority and remaining native/provider acceptance.

## Trait-driven group participation (2026-09-28)

Artifact-bound `interaction_architecture` runs `group_floor_*` against the production response-order helper and isolated PostgreSQL campaigns. The 2026-10-08 contextual-participation change replaces probability/zero-writer assertions with full-audience writer dispatch, narrated castle-arrival preservation, contextual initiative/quiet prompting, no invented prior speech after silence, model-chosen all-quiet completion, named/action/wilderness priority, privacy/absent-speaker controls, receipt-scoped tavern preflight and exact replay. Injected writers prove orchestration, not model judgment. Run-owned schemas/files are removed in finally; no model or native control is used. `prompt_efficiency` retains accepted-source/observer/quiet-response contracts. Native persistence and human naturalness require separate acceptance after deployment.

The same group suite covers open questions/greetings without a mandatory lead for factual remarks, inverse silence instructions, and event/tavern preflight before context selection. Tavern fixtures verify receipt-derived private membership, unchanged dialogue/terms during preflight, zero-call quiet responses and completed replay. Existing native clients and the server guard must agree on the frozen order while retaining the full memory audience.

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

2026-10-12: `contracts.codex_runtime` also exercises the production persistent roleplay adapter and scoped Direct lookup host. Tests retain a large character foundation, update current context on a warm turn, force a fresh current prompt after compaction, and check nested usage receipts, original-record visibility, tool argument/budget boundaries and save-load invalidation. The shared browser fixture checks Auto/Direct placement, defaults, provider-bank preservation, automatic model discovery, identifier variants, selection retention and discovery failure recovery. Use the artifact-bound offline Codex suite; never inject its transport into the listening game server. See [provider safety and configuration](CODEX_APP_SERVER_PROVIDER.md).

`reign-codex-performance-report-v1` is cataloged under `codexPerformance` in `reign.testing.json`. Run `reign_run_offline_verification` with the exact successful Release validation run ID and suite `codex_performance` for injected runtime, conversation, settings migration, dispatch-cap, and report contracts. Run `interaction_architecture` for the shared Control Center fixture, then its three browser contracts at 1672, 1024, and 600 pixels with all traffic intercepted. This is a web control change: native artwork raster/fidelity checks do not apply.

The optional provider comparison is explicit-only: `reign_start_verification`, tier `live-llm`, suite `codex_performance`, the usual `start Reign verification` confirmation, and `codexPerformanceJson`. The JSON must name the exact model, caseIds, full baselineOptions and variantOptions, the single changedOption, a mandatory totalProviderCallCap from 1 through 1000, and confirmation `run bounded Codex performance comparison`. The run-wide cap covers every physical turn/start across primary generation, repair, tier retries, probes, judges and repeat passes. Discovery and thread creation are not generation calls. Cancellation/closed budgets prevent later dispatch. The suite uses isolated Verification Lab campaign data, preserves the selected live provider/settings, and is never included implicitly in aggregate live suites.

Model checking is separate: POST `/api/codex/models/verify` requires `maxRequests:1`, `bounded:true`, and confirmation `verify one Codex model request`. The Control Center explains usage before confirmation. Discovery remains free. A timeout remains inconclusive; request-model echoes do not establish actual served model identity. No user entitlement or shared Codex configuration is modified.

Reports separate baseline/variant latency and quality outcomes, retain unknown usage/fingerprints as unknown, and keep clean attempts, successful repairs, accepted repair overrides, and unusable results separate. Runtime/conversation diagnostics contain no reasoning or rejected draft text. GPT-5.4 access, actual Fast-mode service, and live roleplay quality require later user-authorized tests. See `docs/agent/CODEX_PERFORMANCE.md` for controls and limitations.

The provider comparison plan also requires `validationRunId`: the server checks its own assembly SHA-256 against that successful Release run's exact artifact before dispatch. Source and validation fingerprints come from that report; they are never inferred from model settings. Supply this ID inside codexPerformanceJson (or the CLI `--codex-performance-plan` JSON file). Ordinary quick/offline contracts do not require or authorize a provider plan.

Injected Codex contracts are CLI-only: use artifact-bound `reign_run_offline_verification`. The listening server refuses this injected suite so its test transport cannot intercept gameplay requests. The dedicated Codex browser script wraps the shared provider script; run that wrapper once, then the separate full Control Center matrix.

The MCP supplies its trusted workspace as `validationWorkspaceRoot` so an installed server can locate the exact report. CLI comparison plans may supply that exact Reign workspace explicitly. The report verifies the running assembly hash and records the actual runtime executable hash when its path is known; accepted replies are retained only for later human roleplay evaluation.

Codex comparison fixtures must identify a shipped character or include a completed character snapshot (constructed, traits, and a complete narrative when using narrative v5). Each isolated arm preserves the supplied character stack; fixture personality authoring is not part of the latency comparison. Both arms must keep the same reasoning mode and schema version. Served-model consistency uses authoritative runtime evidence; a request-model echo leaves verification unknown.


## Captured writer continuations (2026-09-30)

2026-10-13 Codex capacity regressions: the artifact-bound `codex_performance` suite injects exact-model runtime metadata for 272k/500k context windows at 95% usable capacity. Check complete 279,768/450,000/600,000-character requests, final dispatch without truncation, malformed/expired/duplicate/wrong-model metadata, route isolation and oversized pre-dispatch rejection. The production two-turn fixture reports 150k input tokens and must still reuse its thread under the verified model allowance. Offline fixtures never read a signed-in user's model cache, discover models or generate provider responses. `capacityAccounting`, `runtimeManagedContext`, `runtimeReserve` and `contextInputLimitTokens` disclose the budget basis; actual tokenization and compaction remain runtime-enforced. Provider and native acceptance remain separate.

The existing `memory_evidence_replay` supports up to three optional captured writer continuations after its first accepted turn. All use the same run-owned campaign, production accepted-unit publisher, original prepared writer requests and fresh source-bounded reads; no provider or game is invoked. `writer_continuation_*` in `prompt_efficiency` covers bounds, forbidden overrides, fresh checker judgments, pending publication, immutable inputs, conditional memory, actual later prompt content and retry deduplication without future-source leakage. Inspect nested results and separate execution completeness from memory-update and semantic accuracy. Procedure: the paired workspace `docs/agent/testing/DIALOGUE_AND_PROVIDERS.md`.

An optional `writerReplay.peerDisplayName` supplies the established display label for a synthetic peer when eligible authored history supplies no name. The fixture label is trimmed, single-line and bounded to 160 UTF-16 code units. It precedes the legacy `Player` default. Eligible authored names take precedence. The initial fixture supplies the label for all continuations.

New response captures include `response.requestSha256` from the exact exported `providerWireCapture`. Each continuation uses the hash of its own prepared request. Replay checks the hash before accepting speech or writing memory. A mismatched response records `writer_response_request_mismatch`. Older fixtures retain compatibility and report `responseRequestBinding.status` as `legacy_unbound`. Use `verified` bindings for paired writer evidence.

An isolated case can supply `reviewedFactReplay` to compare a compact initial request with the stock precision request. Pass a source-only `reviewReceiptSha256` and one to eight bounded `facts`, each with `id`, `sourceId`, `sourceSequence`, `sourceTextSha256`, `speakerId`, `quote` and `text`. The exact source digest covers UTF-8 observer-projected text. The replay checks active source, shared audience, cutoff, speaker, sequence and the quoted span in its disposable campaign. It removes only uniquely matched stock `reported_exchange` carriers whose source turns are all outside the selected recent window. The final serialized messages must preserve the latest 29 prior authored bodies and current contribution in role and order, contain each reviewed fact and omit its internal ID and known older originals. A failed check blocks publication. Read `reviewedFactProjection` for source pins and removed aliases. The review receipt identifies independent semantic review. The replay tests the source join and delivery. A supplied response requires the exact candidate request hash. Continuation fixtures are separate from this treatment.

After a bound response commits in an ordinary reviewed-fact case, `reviewedFactFollowUp` captures a compact comparison beside `nextWriterWireCapture`. It rechecks the same reviewed sources at the new cutoff, drops superseded facts and omits reported exchange and recent recap carriers from the reference packet. It checks the newest 29 prior authored roles, IDs and exact bodies plus the new contribution, and detects known older original bodies and exposed note IDs. Inspect the diagnostic status separately. The comparison captures a request only. Reviewed recovery, detached party and durable later-scene fixtures retain their separate next-read paths.

An ordinary reviewed-fact free-prose `dialogue` or `event` fixture can set `writerReplay.repairResponses` to `[]` after binding the exact initial response. This runs the same native format and continuity repair logic through deterministic cleanup. Inspect `initialCapturedProse`, `conversationRepairEvidence` and the accepted text. A requested provider repair stops with `writer_reviewed_deterministic_repair_requires_provider` before any repair request export or publication. Reviewed writer plans, detached party views, durable reads and the bound reviewed recovery experiment use their separate fixture paths.

The isolated publisher advances its campaign's accepted-turn sequence to the imported writer cutoff before committing the captured reply. Both new turns can then use the ordinary allocator, and the next cutoff includes the imported accepted history.

The optional `reviewedFactReplay.writerPlan` treatment accepts `reviewed_basis` or `reviewed_basis_with_player_report` for isolated dialogue and event cases. It places the exact reviewed recollection immediately before the final contribution with plan instructions. The captured `response_format` binds the recalled speaker and derived meaning in `recallBasis` before a free `reply`. The mixed-turn treatment adds `currentReport` and requires an exact `current_player` memory write for the supplied contribution. `reviewedWriterPlan` records schema identity and plan conformance. A mismatched or reordered plan blocks publication. The ordinary grounding and memory checks still govern an accepted response. Detached party, reviewed recovery, durable later-scene and continuation inputs remain separate. Count exact input and decoder cost with the selected serving tokenizer before local generation.

Use `reviewed_bound_delivery` or `reviewed_bound_delivery_with_player_report` to compare a short delivery contract. Supply independently reviewed `deliveryChoices` with exactly two bounded strings named `direct` and `conversational`. Verify that both lines preserve the admitted speaker, status and terms. Only the style identifiers reach the decoder. The replay returns `reviewedWriterPlan.deliveryChoicesSha256` for the exact lines and source review identity. Include it as `writerReplay.response.reviewedDeliverySha256` alongside the exact request hash on publication. A changed receipt or unrecognized response blocks publication. The engine renders the selected line locally, strips the style identifier, and runs ordinary grounding and publication. The mixed variant also requires the same exact player report write. This isolated treatment measures delivery of checked wording and leaves automated source admission and live provider behavior open.

For a provider-free recovery comparison on a reviewed-fact `dialogue` or `event` case, add `reviewedRecoveryReplay` with `mode` of `derived_retry` or `eligible_repair` and a 32-hex `trialId`. Bind the first draft to the initial wire and supply `writerReplay.repairResponses` with zero or one capture. The current exact player contribution must expressly correct a prior account and request earlier evidence to permit lookup. A malformed draft alone uses a derived retry. The eligible arm searches the disposable snapshot with the existing lexical ranker and requires one unambiguous exchange tied to a verified first-pass fact. Every row must predate the recent window and pass cutoff, revision, attribution and full current audience checks. Up to four complete attributed turns fit within 4,000 UTF-8 bytes. The derived, denied and eligible requests add the same guidance before the authored history. Only the eligible request adds the original exchange. The server verifies the final wire outside these insertions and exposes `reviewedRecovery` with source hashes, lookup timing, insertion position and arm binding. Count finalized serving-template tokens independently before running a writer. Replaying its reply requires the exact stage request hash and `response.recoveryBindingSha256`. A pending or mismatched response leaves the supplied draft unpublished. This experiment uses the same isolated publisher and stock next-read diagnostic. It excludes detached party publications and writer continuations.

To measure native scene formation without a model, stage `reign-scene-formation-replay-input-v1` as `memory-accuracy/evidence-input.json` in the successful Release artifact. The `memory_evidence_replay` suite accepts 1–16 cases and at most 128 rows per case and 512 total. Each case supplies `caseId`, `observerId`, `peerId`, `worldDay`, `asOfSequence` and `sources`. Each source uses the existing fields for ID, session, exchange, attributed speaker, role, text, accepted sequence, world day and audience. This two-party fixture requires the source to be shared with the observer and peer. The manifest excludes queries, labels, settings, responses and workers. Seed only eligible older originals whose archived scope was verified independently.

The native closed-scene summarizer runs with generation disabled at 1,800 and 5,000 characters. It commits and reopens the summary, exact turn links and lexical row in the disposable world. `sceneFormation` records the same attributed source options in both arms, native selection overhead and diagnostic reasons. The extractive fallback may exceed the recap budget while preserving full source coverage in storage. Score required occurrence coverage and whole-record cost in a separate grader. Keep the stored originals separate from first-pass writer evidence. Distinguish verified closed scenes from synthetic closures of earlier excerpts. A scene still open at the recall cutoff needs a separate source-formation path.

For a later provider-free read, add `durableReadReplay` to a single precision `dialogue` or `event` case with a bound captured response. Supply a later `worldDay`, a `query`, 29 `authored` objects with `role` and `text`, and `reviewedFact` with `text`, `sourceBodySha256` and `reviewReceiptSha256`. The lab raises only its run-owned accepted-turn sequence before publication. In a validation-only transaction it locks the canonical accepted source and the matching memory row, validates the full attributed source, and commits the reviewed paraphrase and its digest in the row's payload. The accepted speech and searchable record keep their original bodies. The lab closes the earlier session, seeds the later authored window and uses fresh database connections. The compact reader receives the stored projection only after the normal ranker selects that memory. It checks the original occurrence through a metadata-only query with its actor, event, recipients, cutoff, PostgreSQL row version and restore generation. Ordinary ranking retains its existing eligibility queries. The compact policy suppresses raw exchange and recap candidates.

Inspect `persistedReviewedProjection`, `source`, `controls` and the stock and compact wires in `durableRead`. A separate run with the same speech and explicit write omitted tests causality. In the positive run, two sequential `revocations` also capture compact requests after a revision-only update and an audience change to the same source. The source body and occurrence remain exact. Each must reach the gate through the same ranked stored projection and unchanged lexical index. The first records `source_revision`, and the second records `source_revision` and `source_audience`. Both must keep the old body, paraphrase and stored record ID out of the final request. Count exact serving-template tokens and compare authority and authored history independently. This test uses a single isolated process and provides no concurrent publication, cross-restore or generated accuracy proof. It excludes detached party publications, continuations and worker replays.

An archived shared `party_chat` case can pin `publishedPartyViewSha256` to the exact UTF-8 bytes of `evidence-party-publication.json` beside `evidence-input.json`. Produce the detached receipt independently of the writer arms. Use schema `reign-evidence-party-publication-v1` with `caseId`, `observerId`, `peerId`, `sessionId`, `asOfSequence`, `asOfUtcSeconds`, `worldDay`, `querySha256`, `archiveReceiptSha256`, the shared `audience`, the latest 29 `recentSourceIds` and the chronological `rows`. Each row binds `sourceId`, `publicationId`, `exchangeId`, `speakerId`, `speakerName`, `role`, `sequence`, `utcSeconds`, `worldDay` and `bodySha256`. All seeded source audiences must match the verified party audience. Only one initial precision case is permitted, with a receipt of at most 256 KiB and no specialist, late interaction, selection or dense replay. The isolated reader checks the pinned bytes, cutoff, party and stored rows. Only the initial bound precision read and envelope can use that public view. Both arms check the final wire for exact authored history. Provider-added authority can follow the current contribution. A supplied reply must bind its exact request hash. The separate stock next-read diagnostic has its own scope. See the paired workspace procedure in `docs/agent/testing/DIALOGUE_AND_PROVIDERS.md`.
