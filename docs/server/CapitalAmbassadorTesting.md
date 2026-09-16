# Reign Capital and Ambassador Acceptance Testing

The lifecycle certification was revised on 2026-08-21 from a branch-per-state harness to two compact end-to-end native lifecycles backed by deterministic contracts. Earlier 2026-08-11/13 reports remain historical evidence only; machine-readable current-build reports determine readiness. The canonical manifest is `tests/ReignLiveTest/scenarios/capital-ambassador-manifest.json`; its MCP entry point is `reign_start_capital_ambassador_test`, protected by the exact confirmation `start Reign capital ambassador test on disposable save`.

## Proof model

Run `deterministic` and `server_matrix` once per compatible final fingerprint. They prove capital eligibility and scope, foreign-ruler and envoy eligibility, Charm 200 and relation -30 boundaries, deterministic selection, duplicate isolation, travel timing, authority-charter immutability, five direct commercial permissions, nine referral-only categories, exact-term hashes, counteroffers, official archives, successor inheritance, tone pressure, war cancellation, and one representative provider, transport, and retry-exhaustion failure class each. The synthetic server campaign and schema must be removed in `finally`.

Native lifecycle A runs as one restore-isolated `lifecycle_a` profile:

1. Designate the prepared safe owned town through the production capital path.
2. Relocate to the alternate safe owned town.
3. Transfer that active capital to the deterministic foreign ruler through Bannerlord's native `ChangeOwnerOfSettlementAction.ApplyByGift` path so the real ownership callback clears the capital and suspends royal scope.
4. Verify any resident envoy shelters or ends safely.
5. Designate the surviving original town and verify capital authority and resident routing recover.

Native lifecycle B uses the same prepared resident posting:

1. `prepare_resident` establishes a real eligible foreign posting through the production server route and drives its production arrival tick.
   If every otherwise eligible foreign origin is already occupied by a posting inherited from the disposable baseline, this guarded prepare phase selects exactly one deterministic living, peaceful origin with an unassigned eligible replacement candidate, establishes the production `-30` ruler-relation boundary only when needed, and retires that origin's blocking posting before making the fresh appointment. It normally requires the authoritative server end route plus native return-home. If and only if the server returns the exact `Foreign ambassador posting was not found.` response, the report records `serverPostingAbsent=true` and the fixture retires the proven native-only orphan; every other server error remains fail-closed. Native retirement uses terminal status `ended` and retains `test_fixture_replaced` as the end reason. A stale old envoy activity flag cannot prevent slot recovery. Preflight emits `occupiedOriginDiagnostics`; the prepare report must contain `fixture_preexisting_posting_replaced` and `fixture_origin_relation_boundary`. This recovery is never allowed on the enrolled baseline and requires rollback.
2. `official_dialogue_corpus` opens visible production `ambassador_official` dialogue and sends eight base player utterances, producing sixteen player/NPC turns. The compact base covers request, refusal, ambiguity, clarification, referral, and counteroffer. If its fixed commercial request is outside the deterministic envoy charter, send one bounded ninth natural exchange using an actually granted permission to prove the grant, for eighteen total turns.
3. `lifecycle_b_transition` moves the resident with the capital, exercises production shelter and recovery, then invokes the production war callback and verifies recall/removal plus return to friendly territory.

The live report is authoritative for every actual player utterance, NPC reply, `ambassadorDecision`, official exchange/archive identifiers, provider timing, and close-time ambassador report. Direct actions cannot replace the natural-language corpus.

## Disposable campaign and save boundaries

Use only an exact guarded campaign-test `Current` created from a user-authorized immutable baseline. The player must rule an active kingdom while inside a safe owned town, own at least one additional safe town and one safe castle, and have a peaceful foreign kingdom with ruler relation -30 or better and an eligible free adult lord or lady with at least 200 Charm.

Only two native save/load boundaries are required:

- Resident boundary: run `prepare_resident`, then `save_prepare` (marker only), stop/drain, overwrite the campaign-test `Current`, restart that exact checkpoint, and run `save_verify`.
- Transition/recall boundary: after `official_dialogue_corpus` and `lifecycle_b_transition`, run `save_prepare`, stop/drain, overwrite `Current`, restart it, and run `save_verify`.

The feature harness never creates a fixed `Reign_CapitalAmbassador_Roundtrip_A` save. The guarded campaign-test controller owns checkpoint identity, Save Sync alignment, visible restart, and reload receipts.

## Canonical run order

1. Prepare/start/arm the general campaign-test enrollment and capture its status.
2. Run `preflight`. Use `prepare_ownership` only when a second safe owned town is missing. A no-origin result caused solely by pre-existing active postings is recovered inside guarded `prepare_resident`; other missing foreign eligibility remains a failed prerequisite. Both recovery branches require rollback.
3. Run `deterministic` and `server_matrix` once on the final compatible fingerprint and preserve their reports as pass-once evidence.
4. Run `prepare_resident`, `save_prepare`, guarded checkpoint/restart, and `save_verify` for the resident boundary. Run `ui` for the visible Ambassador Gauntlet snapshot.
5. Restore the resident `Current`, run `lifecycle_a`, retain its report, then restore the same `Current` without saving.
6. Run `official_dialogue_corpus` and inspect all eight exchange results. If no grant completed because the fixed request was outside the charter, send one bounded natural grant follow-up against an actually granted permission. Then run `lifecycle_b_transition`.
7. Run `save_prepare`, guarded checkpoint/restart, and `save_verify` for the transition/recall boundary.
8. Capture the live reports, validation fingerprint, deployed hashes, UI snapshot, official decision/archive evidence, campaign-test report, and cleanup receipt.
9. Run `cleanup_marker`, close Bannerlord without saving, and delete only the exact run-owned disposable checkpoint. Never delete or overwrite the enrolled baseline.

## Release gate

Readiness requires both native lifecycles, both real save/load boundaries, the complete compact natural corpus (sixteen base turns, or eighteen when the bounded charter-permitted grant follow-up is required), visible UI evidence, deterministic/server matrices, one-per-class fault coverage, final manifest-selected validation, exact validated deployment, and clean rollback. Offline or historical success cannot substitute for final-build native evidence. Do not add permutations unless they exercise a distinct adapter or a discovered defect.
