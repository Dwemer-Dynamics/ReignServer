# Rebellion preparation autonomous testing

The durable controller entry point is:

`ReignLiveTest.exe rebellion --profile <profile>`

The release authority is
`ReignLiveTest/Features/WorldSimulation/rebellion-certification-manifest.json`.
The MCP surface provides `reign_get_rebellion_test_manifest`,
`reign_run_rebellion_contract_tests`, `reign_prepare_rebellion_certification`,
`reign_run_rebellion_certification_contract`,
`reign_carry_forward_rebellion_passes`,
`reign_start_rebellion_certification_case`, and
`reign_evaluate_rebellion_release_readiness`. All native work routes through
the existing visible armed live-game bridge and never starts a second server.

Profiles are `smoke`, `feature`, `pledges`, `reporting`, `summons`, `verdicts`,
`declaration`, `save_prepare`, `save_verify`, `resolution`, and `cleanup`.
Every profile beyond observational `smoke` and `feature` requires the exact
active loaded save name in either the dedicated `Reign_` namespace or the
`ReignTest_` Current namespace created by guarded campaign-test enrollment.
The client independently compares that argument with Bannerlord's active save
slot before creating state. Each profile emits distinct, fail-closed
assertions plus exact pre/post fixture counts. LiveTest never substitutes for
the guarded campaign-test checkpoint route. `travel_prepare` stages the
production plot before `reign_checkpoint_campaign_test`; `travel_verify`
proves the same identities after guarded restart. `cleanup` removes only the
exact run-owned fixture namespace.

Secret recruitment closes when a report is dispatched or a ruler summons is
in flight. The reporting and summons profiles explicitly prove that production
gate, named informer/ruler ids, travel timing, idempotence, and the seven-day
deadline measured from delivery.

Release acceptance uses one proof per materially distinct transition or
failure mode. The server-only contract credits only the five bounded
ambiguity/negation/correction cases. The six direct/mail
pledge/refusal/reporting cases and all six deterministic client state-machine
profiles require their own live reports. Organic evidence
must originate in visible Individual Chat or Correspondence; direct decision
commands are a bypass failure. Each organic case uses a bounded two-turn
exchange: the opening request samples the noble's concerns, then a natural
follow-up answers identity, plan, risk, and terms and asks for a final decision.
Every decisive follow-up repeats that the request concerns rebellion against the
ruler so the production secret-preparation adapter does not depend on unstated
prior-turn context.
Text classification is subordinate to the structured `actionGate`: pledge
requires an actionable accepted commitment. An unconditional final refusal
requires `needed=true` with `commitment=refused`. Negotiation or deferral
requires `needed=false` with `commitment=conditional` and persists nothing; a
refused label without an actionable gate also fails closed. An eligible
concealed report uses either `commitment=final_private_report` or a visible
`commitment=refused` plus explicit non-negated private report intent, while the
visible reply remains a refusal or guarded neutrality. Natural final
refusal therefore does not depend on a second brittle phrase match. Natural final
future-tense and nominal forms such as `I'll pledge`, `we will pledge`, or
`that's my pledge` route to secret preparation, never immediate declaration.
Normalized contraction forms such as `cant`/`can t`, `wont`/`won t`, and
`dont`/`don t` remain explicit negations.
A reply beginning with a standalone `No` is a refusal only after report and
positive-pledge recognition have failed. Explicit final phrases such as `will
not throw my clan` and `stands aside` also resolve refusal after scene-setting
prose, while negotiation-only `no land`/`no gold` does not.
Persisted provenance maps production-normalized `in_person`/`in person` to `individual_chat`
and preserves `correspondence` distinctly.
Mail certification enrolls the exact known recipient clan leader under the
disposable fixture sovereign. It dispatches the concern-sampling letter,
advances six native days and waits for correspondence quiescence, then
dispatches the decisive follow-up, advances another six native days, and waits
again before the decision oracle. During guarded passive-world advancement
only, the standard Reign letter-arrival inquiry is dismissed as `Not Now`; the
harness never opens the reply or treats the notice as evidence. Dispatch alone
is never reply or decision evidence.
Delivered replies about a planned, not-yet-declared rebellion use the same
`actionGate` contract as Individual Chat and route pledge, refusal, or concealed
report through `resolve_rebellion_pledge` with `requestChannel=correspondence`.
The separate `rebellionDecision=accept_recruitment` value remains reserved for
joining an already-declared rebellion.
Pledge follow-ups name the allied leaders and exact promised holding so an
otherwise willing noble is not left negotiating an unspecified condition.
Report cases ask only whether the clan will support or refuse the rebellion; they
never tell, invite, or coach the NPC to report. Production supplies authoritative
sovereign identity, NPC-to-sovereign and NPC-to-player relations, Reign Loyalty,
and native Honor. The server accepts a private report only for the NPC's own
sovereign, relation at least +10, and either Loyalty at least 61 or positive
Honor. Ineligible report intent fails closed. The immediate result remains
indistinguishable from refusal; only the delayed ruler summons reveals the report.
For `RB-LANG-003`, `RB-LANG-006`, and `RB-NATIVE-001`, `native_setup` enrolls living, free
leaders of active non-ruling vassal clans that the production pledge action can
accept. It records exact clan-leader identity plus prior native relation and Honor
values, then temporarily gives each leader at most -10 relation to the player, at
least +35 to the current sovereign, and Honor at least +1. The guarded checkpoint
reload restores those native values. These moderate values satisfy the production
plausibility gate without defining the final choice. The NPC still decides from
natural manifest turns through the configured model; the fixture never injects a
decision action or prompt directive. The native travel sequence also asks only
for support or refusal and never tells, invites, or coaches the NPC to report.
Clarification or negotiation is not a decision. Release acceptance must also cover the named informer's
traveling report; summons delivery and its seven-day deadline; attendance,
renunciation, defiance, and ignored summons; pardon, prison, confiscatory exile,
execution, and death-disabled fallback; activation of pledged clans with their
  holdings; one representative save/reload sequence; both leader-bound civil-war outcomes;
mercy judgments; one mixed town/castle/bound-village transfer; one foreign clan
and living-family reintegration; and exact disposable-save/server cleanup.

Prepared certification binds campaign-test enrollment plus final source,
client, server, provider, catalog, Bannerlord, campaign, timeline, baseline,
and disposable-save fingerprints. Only manifest language/deterministic cases
marked `passOnce` can be carried between runs by preserving their original
durable report references, and only when every immutable fingerprint is
identical. Native, save/load, transfer, reintegration, and
cleanup evidence is never carried. The travel case requires separate prepare
and verify stages around the guarded rolling checkpoint, visible Bannerlord
restart, and native campaign advancement.

Offline validation and a successful smoke profile prepare a deployment but do
not constitute live acceptance. Deployment must use the exact artifacts from
the authoritative successful validation report, and the live matrix runs only
after the user separately authorizes deployment.
