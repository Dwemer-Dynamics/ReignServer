# Kingdom catastrophe and boon launch acceptance

This suite forces rare events, but every forced case still uses the production catalog, eligibility filters, target kingdom, native effects, announcement, World History write, persistence record, and save/load behavior. Forcing a case does not consume or alter the normal saved 1% daily roll.

The canonical schema-v2 matrix is `tests/ReignLiveTest/scenarios/kingdom-event-acceptance-manifest.json`. It proves every one of the 15 archetypes once, then adds middle and last cases for one harmful timed, beneficial timed, and immediate representative. The first case is already present in the base set, so this is 21 forced cases rather than a redundant 45-case cross-product. Expand only when an archetype uses a distinct unproven adapter or a failure indicates target-position sensitivity. The read-only CLI preflight is `kingdom-event-preflight.json`. The durable parameterized entry point is MCP `reign_start_kingdom_event_test`; its exact confirmation is `start Reign kingdom event test on disposable save`.

## Profiles

- Every profile accepts the guarded `campaignTestRunId`. The MCP proves the exact armed, run-owned `Current`, and the client independently matches `MBSaveLoad.ActiveSaveSlotName` before executing.
- `preflight` returns all 15 production definitions, polarity, timing, magnitude, stable ordered eligible kingdoms, active conflicts, and player-target status.
- `force` invokes the production `ForceEvent` path for one archetype and target variant, persists an evidence ledger, proves an exactly-once forced record, and proves the daily-roll marker did not move.
- `observe` verifies the saved record and exact current economic contribution or the native war/peace/succession outcome.
- `advance_observe` observes after the caller advances only through `reign_advance_campaign_test`; it never owns time or checkpoint state itself.
- `expire_observe` proves the 30-day record is inactive and its model contribution is zero after guarded campaign advancement.
- `no_target` calls the production force path with an exact absent target and proves refusal, zero records, and an unchanged daily-roll marker.
- `organic_prepare` first requires campaign initialization, autonomous-world preparation, native-save quiescence, and Save Sync alignment; it fails closed without creating a ledger or arming an override while those production prerequisites are pending. It then scans the unchanged stable production roll for the next bounded 1% trigger with a currently eligible polarity. For bounded acceptance, its optional `accelerateOrganicTrigger=true` arms only the exact run's next disposable-save day; the non-serialized override leaves the 1% constant and original stable roll unchanged, is consumed before selection/effects, and is also cleared by same-run cleanup if unused. Advance to the returned target day through `reign_advance_campaign_test`, then `organic_verify` proves one new non-forced event with its original basis-point roll and proves the override was consumed.
- `save_prepare` records only a feature fingerprint and native game-instance ID. Stop/drain, overwrite the guarded `Current`, and restart through the campaign-test service; `save_verify` requires a different game instance and the identical fingerprint.
- `cleanup_marker` removes only the inert test ledger. World mutations are undone by restoring the verified baseline, never by synthetic inverse diplomacy or ruler changes.

Every mutating call requires a unique `fixtureRunId`, the archetype, and `first`, `middle`, or `last`. After `force`, capture the production announcement before acknowledgement. Query World History with the returned `worldHistoryCorrelationId` and verify the event title, kingdom, secondary kingdom or heir, polarity, phase, exact effect/duration, and forced origin.

## Launch matrix

Enroll an exact immutable baseline through the guarded campaign-test service, arm its task-owned `Current`, checkpoint it once, then restore that exact `Current` without saving transient state between destructive cases. Run:

1. Force every one of the 15 archetypes once on the first stable eligible target. For all 12 timed adapters, verify the exact contribution on every current applicable town/castle/village, direction, magnitude, unrelated dimensions, announcement, and World History.
2. Add middle and last target cases for `famine`, `bountiful_harvest`, and `border_crisis`; with their base first cases this proves stable ordering for harmful timed, beneficial timed, and immediate categories.
3. For `border_crisis`, verify a distinct active non-enemy partner, no protected treaty/truce, native war, and the event-linked Reign war origin. For `grand_reconciliation`, verify a prior ordinary enemy, native peace, and rebellion/civil-war exclusion. For `orderly_succession`, verify a living old ruler, valid adult NPC ruling-clan heir, unchanged ruling clan/fiefs, and player exclusion.
4. Run `no_target` for the war, peace, and succession adapters and require fail-closed zero-mutation evidence.
5. Perform native save/restart/load on `famine`, `bountiful_harvest`, and `orderly_succession`. Advance one `famine` representative through the day-30 boundary and prove expiry plus zero contribution.
6. Run one prepared natural 1% observation through ordinary campaign advancement and prove the non-forced record, natural roll, announcement/history correlation, and marker movement.

This is a destructive disposable-campaign matrix. War, peace, succession, daily hearth/security/prosperity/loyalty changes, and event history must never be applied to the preserved user campaign. Keep the visible server at `127.0.0.1:5101`, never run a second hidden lifetime group, collect bounded live-run and crash evidence, restore the guarded `Current` between cases, and delete only exact run-owned campaign-test checkpoints after their identities are verified.

Launch readiness requires 21/21 forced cases, three distinct no-target adapters, three representative native reloads, one expiry boundary, one organic event, announcement/UI and World History evidence, zero crashes or duplicate records, exact deployed hashes from the final canonical validation artifact, and confirmed run-owned save/server cleanup. Offline validation or preparation alone is not live acceptance.
