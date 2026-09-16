# Spymaster autonomous testing

The Spymaster harness has two layers that share `ReignSpymasterCore.cs`:

- `reign_run_offline_verification` with suite `spymaster` runs the non-listening deterministic formula, price, effect, recruitment, social-mitigation, assassination, breakout, and coverage contracts. It never starts Bannerlord or a server listener.
- The installed `ReignLiveTest.exe run <scenario>` entry point drives the loaded production campaign through the armed exactly-once live bridge. Its reversible `spymaster_test` command runs the production mission state machine on the Bannerlord campaign thread, records irreversible native actions as intents, restores gold and state, and emits structured assertions in the normal live-run artifact.

Normal campaigns cannot activate the hooks. `ReignSpymasterTestRuntime` is process-local, inactive by default, and begins only inside an armed live-test command. Feature runs restore the original serialized Spymaster state and player gold in a `finally` block. The save/load fixture is namespaced by the run ID and the verification command removes it after checking it.

## Profiles

From the installed server application directory, with the visible Reign server and a loaded ruler campaign already running:

```text
ReignLiveTest.exe arm --minutes 120
ReignLiveTest.exe run verification_contracts\workspace\tests\ReignLiveTest\scenarios\spymaster-smoke.json --timeout 900
ReignLiveTest.exe run verification_contracts\workspace\tests\ReignLiveTest\scenarios\spymaster-feature.json --timeout 1800
```

The smoke profile opens the real Spymaster screen, exports native geometry, traverses representative tabs and scopes, runs the deterministic campaign checks, and closes the screen. The feature profile executes the full reversible backend matrix plus all target groups and archive UI paths.

Native save/load is a deliberate two-phase lifecycle so the controller can verify that a new game process loaded the checkpoint:

```text
ReignLiveTest.exe run verification_contracts\workspace\tests\ReignLiveTest\scenarios\spymaster-save-prepare.json --timeout 900
ReignLiveTest.exe game stop
ReignLiveTest.exe game start --save Reign_Spymaster_Automation_A
ReignLiveTest.exe wait-game --timeout 600
ReignLiveTest.exe arm --minutes 120
ReignLiveTest.exe run verification_contracts\workspace\tests\ReignLiveTest\scenarios\spymaster-save-verify.json --timeout 900
```

Use the same run ID for prepare and verify when invoking `spymaster_test` directly through the live API. Scenario run artifacts contain scenario names, command pre/post data, assertion IDs, timing, campaign/game instance IDs, native-intent evidence, and UI snapshot paths. The controller's existing report command and server diagnostics collect the associated Reign logs; RGL and Windows crash artifacts are collected by the existing game-lifecycle failure collector.

## Coverage and cleanup

The runner covers appointment identity and locks, Roguery boundaries, displayed/resolved rolls, the intended price table, capacity and payment, invalidation/no-refund, complete immutable land reports, settlement disruptions and clamping, relationship/rumor person-report separation, assassination success/failure intents, breakout boundaries, social mitigation, enemy-agent recruitment/action idempotence, full mission memory serialization, old-state migration, and native save/load. NPC-stat discovery through `person_skills` is intentionally excluded from launch acceptance because NPC stats are no longer hidden and that operation is slated for removal.

After an interrupted save/load run, issue `spymaster_test` with profile `cleanup` and the original run ID, then delete only the namespaced `Reign_Spymaster_Automation_A.sav` checkpoint after confirming the user's original save is intact. Never run two game or server lifetime groups in parallel.

## Organic launch acceptance suite

Prepared on 2026-08-10 and first exercised in an actual Bannerlord campaign on 2026-08-11. The canonical ordered manifest is `tests/ReignLiveTest/scenarios/spymaster-organic-manifest.json`. This suite complements the deterministic matrix; it does not replace it. A completed run is established only by its machine-readable run artifacts and final acceptance report, not by this document.

Organic scenarios deliberately use:

- the real Spymaster view model and its normal mission-submission method;
- ordinary `spy_` mission IDs, Bannerlord random rolls, displayed probability snapshots, payment, capacity, and campaign-time completion;
- production five-day foreign-agent recruitment ticks, with no direct agent insertion;
- production server persistence for mission memory, social effects, hidden affiliations, and exposed networks;
- a native save, process restart, and load boundary.

Every organic run persists a small `spymaster_organic_launch` evidence ledger in the disposable save. It records the campaign ID, selected targets, starting gold/day, mission IDs, pre/post snapshots, agent/action/effect IDs, probability rolls, results, reports, and observation stages. The ordinary live-run artifact adds command timing, game-instance and run IDs, logs, UI snapshots, crash evidence, and cleanup state. Exposed agents remain durable historical records with their sponsor and exposure day; they are removed from the active pool without deleting the evidence.

### Safety and campaign preparation

Do all of the following before the first mutating profile:

1. Keep the user's current save untouched. Create a native recovery checkpoint and an isolated disposable campaign/save branch, and confirm Save Sync is aligned.
2. Use the one visible Reign lifetime group at `http://127.0.0.1:5101`. Never start a second or hidden server.
3. Load a player-ruled kingdom with an active, living, free Spymaster; sufficient gold; at least one player town/castle; foreign towns/nobles; and no pre-existing active Spymaster missions.
4. For the natural agent chain, use a realm actually at war. At least one local noble or notable must have loyalty/relationship `<= 40`. A sponsor ruler relationship above `-30` must remain dormant; an actual operation requires a sponsor ruler relationship `<= -30`. The read-only agent-status projection reports these preconditions and the exact loyalty/chance inputs but does not recruit or activate anyone.
   If ordinary relationship simulation would move the ruler across that boundary during a long acceptance window, use `reign_set_spymaster_agent_activation_fixture` with the save's exact existing `Reign_SocialBalance_` enrollment prefix. Check `-29`, exactly `-30`, and `-31`. The fixture sets the native Bannerlord relation first and then both authoritative underlying directional affinities. Those exact underlying directions drive foreign-agent activation; effective-value conversion includes unrelated social modifiers and would move the tested boundary. Setting only the native relation is temporary because Reign correctly converges it back toward the two authoritative directional affinities.
5. Run `preflight` first. Do not continue if it reports missing targets, missing candidates, a forced runtime, unavailable server projection, or an unsuitable campaign. Appointment automation waits up to 60 seconds for production court-session alignment before selecting a candidate; timeout is a failed readiness condition, not a reason to bypass the server office path.
6. Run `prepare` only with the exact confirmation embedded in the scenario. It establishes the next genuine five-day observation boundary but does not roll, insert, or activate an agent, and it does not create a fixed native save. The guarded campaign-test Current checkpoint owns save and rollback isolation.

The general MCP entry point is `reign_start_spymaster_organic_test`, with the exact confirmation `start Reign organic Spymaster test on disposable save`. Its production-random profiles are `preflight`, `prepare`, `intelligence`, `people_scopes`, `sabotage`, `social_fabrication`, `social_mitigation`, `self_mitigation`, `agent_watch`, `counterintelligence`, `save_prepare`, `save_verify`, `evaluate`, and `cleanup_marker`. Controlled profiles are `controlled_social_success`, `controlled_rumor_mitigation`, `controlled_self_mitigation`, `controlled_agent_action`, and `controlled_counterintelligence`. Mission controls bind one-shot rolls only to exact captured production GUIDs after visible UI submission and require `force Reign Spymaster outcomes on disposable save`. The agent-action control binds one nonserialized roll to one exact active hidden agent's next due opportunity and requires `force one Reign Spymaster foreign-agent action on disposable save`; its receipt preserves the real 58% threshold, production stable roll, selected effect, action ID, and cooldown. All controls self-clear and cannot affect unrelated missions, agents, or normal campaigns.

Social mitigation uses the guarded `select-social-item` production UI action. That action awaits the server-backed rumor/reputation refresh before selecting the requested record; a fixed delay is not a substitute for refresh completion, especially while native campaign processing is busy.

The assassination/captivity/breakout branches are intentionally separate: `reign_start_spymaster_organic_destructive_test` accepts `controlled_assassination_success` or `controlled_capture_breakout_success` and requires the exact confirmation `run irreversible Reign Spymaster test on disposable save`. Run each profile on its own restore-isolated guarded Current branch. The first binds a success roll to one exact UI-submitted person assassination and proves the native target death. The second submits a governor assassination against a governed foreign settlement, whose settlement party supplies a durable native prisoner holder; it binds failure and capture rolls to that exact mission, verifies native Spymaster captivity, then binds the breakout roll to that same mission and proves native escape without sponsor exposure. Production also searches the target party, target settlement, home, clan fiefs, and foreign ruler in order and must never report capture or set attribution pending unless native captivity actually occurs. Never invoke either profile on the preserved user campaign.

### Ordered run for launch night

Arm the already-visible server bridge, run each JSON from the installed `verification_contracts/workspace/tests/ReignLiveTest/scenarios` directory, and retain every resulting run ID. The intended order is:

Every hero-target dropdown uses the same living-and-active eligibility boundary as production mission resolution. If a selected hero becomes unavailable after submission, retain the cancelled mission as natural lifecycle evidence; a later cycle must refresh the UI and select another currently eligible target rather than repeatedly submitting against the stale hero. Repeated social-fabrication cycles select a fresh currently eligible foreign noble before reusing a prior mission target; this is semantic harness selection only and does not change player UI ordering or production mission rules. Social mitigation independently selects an active foreign noble with a live rumor and an active foreign noble with a live established reputation. Each selector starts from a naturally succeeded fabrication mission and confirms that the corresponding record still exists through the production social-status endpoint before using the normal UI, so the acceptance suite never assumes both independent random outcomes landed on one target.

1. `spymaster-organic-preflight.json`
2. `spymaster-organic-prepare.json`
3. `spymaster-organic-intelligence.json`
4. `spymaster-organic-people-scopes.json`
5. `spymaster-organic-sabotage.json`
6. `spymaster-organic-social-fabrication.json`
7. If representative natural runs have not produced both rare records, run `spymaster-controlled-social-success.json` once to prove those exact success branches without unbounded probabilistic waiting
8. `spymaster-organic-social-mitigation.json` after a harmful rumor and a harmful reputation have each succeeded on any still-active eligible foreign targets; if the native rumor-mitigation roll fails, use `spymaster-controlled-rumor-mitigation.json` once while that live rumor remains eligible
9. `spymaster-organic-self-mitigation.json` only if the player organically has both an applicable rumor and reputation
10. `spymaster-organic-agent-watch.json` for twelve real five-day cycles. The supplementary `spymaster-organic-war-setup.json`, `spymaster-organic-agent-cycle.json`, and `spymaster-organic-agent-operation-window.json` scenarios establish the disposable production-war precondition, add one natural recruitment boundary, and observe a twenty-day natural attack window respectively; none inserts or forces an agent. The war setup and cycle explicitly acknowledge Bannerlord's native `WarMapNotification`/`PeaceMapNotification` through the armed disposable-save bridge, and passive advancement removes only those two native notice types if they appear while campaign time is running. This prevents a declaration-of-war notice from silently freezing an unattended test without granting the harness permission to dismiss unrelated inquiries.
11. `spymaster-organic-counterintelligence.json`, repeated only while a hidden active agent remains
12. `spymaster-organic-save-prepare.json` to capture the combined pre-reload evidence snapshot; this profile never creates a native save
13. Stop and drain the enrolled campaign test, overwrite its exact run-owned `Current` checkpoint with `reign_checkpoint_campaign_test`, restart that checkpoint with `reign_restart_campaign_test`, then run `spymaster-organic-save-verify.json`. A historical interrupted `save_prepare` may have left exactly `Reign_Spymaster_Organic_Midrun_A`; the guarded checkpoint-deletion host accepts only that exact legacy name with exact prefix `Reign_Spymaster_Organic_` as a cleanup recovery exception. It does not authorize any other `Reign_` save.
14. Repeat probabilistic profiles named by `recommendedNext` only for natural distribution evidence; use the controlled exact-mission profile for a rare correctness branch rather than waiting indefinitely
15. On one disposable restore branch, run `spymaster-controlled-assassination-success.json`; restore Current, then run `spymaster-controlled-capture-breakout-success.json` on a second branch and restore Current again
16. Run `spymaster-organic-evaluate.json`, then the manual UI/dialogue pass below
16. Run `spymaster-organic-cleanup-marker.json`, restore the original recovery checkpoint, and delete only the exact disposable native saves and Save Sync points after their paths are verified

Example CLI invocation (do not use it while another controller owns the game):

```text
ReignLiveTest.exe arm --minutes 240
ReignLiveTest.exe run verification_contracts\workspace\tests\ReignLiveTest\scenarios\spymaster-organic-preflight.json --timeout 1800
```

Long campaign-time profiles should use an overall controller timeout of at least `21600` seconds. Every `world_advance` step is explicitly classified as `passive_world`, so its declared 7200-second command timeout is preserved even inside the mixed Spymaster scenario.

### Manual, organic UI and dialogue pass

Computer control is still required for interactions where genuine mouse behavior matters. Capture screenshots and structured notes for each:

- open the Spymaster screen from Court; verify artwork/text/assets, portrait and appointed name, every tab, dropdown, scroll/archive/report view, Escape/back behavior, and closing while missions continue;
- click the portrait/appointment picker, inspect eligible candidates, verify a character holding another office cannot be selected, replace/dismiss on the disposable branch, and verify active missions block replacement/dismissal; also observe dead, prisoner, and freed states when naturally available;
- compare displayed price, duration, success, detection, and target detail immediately before submission with the captured mission snapshot immediately after submission;
- inspect land and people reports in the UI and compare them to the contemporaneous world/economy/social state;
- after natural foreign-agent action and successful counterintelligence, verify the archive identifies the agent, sponsor, target, effect, detection, and attribution without exposing information early;
- talk to the appointed Spymaster and ask about several current and historical missions, an enemy action, the identified agent, and any rescue attempt before and after reload; compare answers with the machine-readable ledger;
- verify no duplicate completion/toast/history appears after reopening screens, waiting, saving, or loading.

### Pass gate and cleanup

The organic gate is not a fixed single-roll expectation. Individual success/failure/detection outcomes must match their stored rolls and displayed snapshots; aggregate outcomes must stay inside the recorded three-sigma envelope. The exhaustive evaluation additionally requires successful intelligence, all four sabotage effects, the three people scopes, a synchronized social success, natural foreign-agent recruitment, a natural operation against a player holding, successful counterintelligence identification, both assassination attempt paths, exact-once IDs, valid locality, and production-only IDs.

Do not mark launch acceptance complete merely because preparation or offline validation passes. Completion requires the actual Bannerlord runs, manual UI/dialogue evidence, native save/reload evidence, deployment hashes from the canonical validation artifacts, a clean crash/log review, restoration of the user's checkpoint, removal of the exact disposable saves, and confirmation that the visible server is returned to its expected state.
