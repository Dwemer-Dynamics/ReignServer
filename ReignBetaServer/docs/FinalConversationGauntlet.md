# Reign Final Conversation Gauntlet

The Final Conversation Gauntlet is Reign's permanent, generated conversation
release gate. It uses the production prompt builder, retrieval, resolver,
validator, executor, persistence, relationship, rumor, and memory paths.

## Execution policy

1. Run the `exhaustive-final-review` verification prerequisite.
2. Arm the loopback Live Interaction Bridge against a disposable derivative
   save with Save Sync aligned. Before any readiness evidence is selected, the
   game exports the complete living-adult NPC census. The server validates
   stable hero IDs, adult/living eligibility, noble clan registration, clan
   names, clan leaders, and clan tiers, then produces the authoritative
   campaign/clan baseline fingerprint.
3. Run Stage A. It selects only readiness categories that still lack compatible
   same-build proof and is hard-bounded to 40 physical provider calls.
4. Stage B is scheduled only when Stage A passes.
5. The exhaustive catalog remains the traceability source, while deterministic
   rows run provider-free and represented rows map to a 90-call live covering
   set. It contains 25 two-turn individual fixtures and 10 two-turn,
   two-NPC group fixtures. Each dense fixture represents one coherent test
   family and carries the real behavioral requirements and prohibitions for
   every mapped catalog ID. Every Stage B case—including representative rows, the twenty final
   scenes, and both long-horizon cases—has one logical execution. A behavioral
   or assertion failure is recorded and the runner continues.
6. A transient provider delivery failure receives at most three physical
   attempts inside the same request correlation. The scheduler never recreates
   the scenario, player turn, memory, or action to perform that retry. Only
   exhaustion after those attempts is classified `ProviderExhausted`.
7. Export the report and review pack. The user and Codex review results before
   deciding on fixes or another run.

Stage A, Stage B promotion, and every resume require the same authoritative
baseline fingerprint. Target enumeration order does not affect it, while a
changed hero/clan registration, clan name, leader, tier, kingdom, or role does.
A mismatch fails closed and requires a new run; evidence from the previous
baseline is never silently reused.

The physical-call partitions are 40 for Stage A, 90 for representative live
coverage, 60 for the twenty final scenes, 110 for `LNG-001`, 44 for `LNG-002`,
and 156 shared auxiliary/retry reserve. Long-horizon partitions include their
expected production scene-summary calls. The shared reserve covers blinded
semantic judges, other production memory summaries, format/schema repair,
action-planner calls, and transient provider retries. Every child correlation
inherits the case correlation and every physical dispatch is written to the
central ledger. Request 501 is refused before network dispatch.

Visible player lines are ordinary in-world role-play. Fixture objectives,
represented requirement IDs, seeds, and scoring instructions remain private
metadata and never become visible player speech. The first turn establishes a
plausible situation; the final turn asks for a consequential response and is
the semantic scoring point. Deterministic families rely on hard evidence
instead of spending a model judge call merely to restate parser, resolver,
idempotency, or schema facts.

Every response is checked for production-schema integrity, correlation
lineage, prompt/context evidence, action grounding, and memory ownership.
Group scenes additionally require distinct agency: later speakers must use the
shared conversation while retaining their own motives, qualifications, and
conclusions instead of cloning the first response. Dialogue may consent to a
proposed gift, payment, release, appointment, or other action, but may not
narrate that native state change as completed until the production
validator/executor returns a successful receipt.

`LNG-001` carries one focal NPC through ten closed scenes and one hundred
natural exchanges. Its facts develop instead of merely repeating: a promise is
made and fulfilled, an old account is corrected without merging both versions,
a keepsake decision develops, trivial details compete with important history,
and later probes ask for knowledge with explicit uncertainty. `LNG-002` rotates
twenty adults through four five-person social-event circles, then runs ten
private follow-ups split evenly between genuine witnesses and non-witness
controls. The latter must show that another circle's exact fact was absent from
retrieval, so a fluent denial cannot conceal private-memory leakage.

## Commands

```powershell
ReignLiveTest.exe gauntlet plan --json
ReignLiveTest.exe gauntlet start --campaign <campaign-id> --json
ReignLiveTest.exe gauntlet status --campaign <campaign-id> --run <run-id> --json
ReignLiveTest.exe gauntlet pause --campaign <campaign-id> --run <run-id> --json
ReignLiveTest.exe gauntlet resume --campaign <campaign-id> --run <run-id> --json
ReignLiveTest.exe gauntlet cancel --campaign <campaign-id> --run <run-id> --json
ReignLiveTest.exe gauntlet report --campaign <campaign-id> --run <run-id> --json
ReignLiveTest.exe gauntlet review-pack --campaign <campaign-id> --run <run-id> --json
```

The same catalog, progress, cancellation, report, and review-pack controls are
available in Control Center under **Live Test Bridge**.

## Evidence

Each run persists:

- `manifest.json`
- `cases.ndjson`
- `assertions.ndjson`
- `evidence/`
- `replays/`
- `scorecard.json`
- `report.json`
- `review-pack.json`
- `review-answer-key.private.json`

The manifest retains the full run and campaign identities even though the
physical directory uses a short hash to stay below Windows path limits.
Evidence contains prompt sections, retrieval sources, model responses,
resolver and validation audits, state before/after, persistence writes,
probability rolls, timings, retries, and correlations. Secrets are removed.

## Safety and lifecycle

- The bridge is loopback-only and requires an explicit expiring arm.
- Fixture mutations require the exact campaign and game instance, a derivative
  save, and the gauntlet confirmation token.
- Native world effects are guarded by default.
- Fault injection is scoped to one run/case/campaign/game, expires, and fires
  once. It is inert in normal gameplay.
- Save/reload and restart operations reconcile the active command and Save Sync
  before lifecycle changes.
- `ConvTest_Gauntlet_A` and `ConvTest_Gauntlet_B` are the only rotating
  derivatives. `ConvTest`, `BaseOne`, `BaseTwo`, and `BaseThree` are protected.
- Start and resume require a registered Save Sync snapshot for `ConvTest`.
  Before starting, the controller reserves enough of the 15 unique-state slots
  for whichever rotating derivatives do not already exist. Insufficient
  capacity fails closed with manual cleanup instructions; automation never
  deletes a save to make room.
- The server is started only through Reign's visible unified launch path.

## Release decision

Hard assertions and state validation are authoritative. Semantic rubric scores
are indicators. A release cannot pass with a zero-tolerance failure, provider
exhaustion, incomplete denominator, or missing review.

The blinded pack contains 10–12 diverse, risk-weighted scenes. Reviewer-facing
items omit fixture labels, expected outcomes, trait numbers, and automated
scores. The private answer key is reviewed only after scoring.
