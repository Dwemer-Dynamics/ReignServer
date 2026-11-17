# WorldSimulation module (server)

The simulated world around the player: world history (ingest, timelines, queries, verification, retention and
compaction), the World Test passive campaign observatory and its telemetry rollups, campaign commands (the
LLM-judged world actions and their test harness), social events and approaches, generated wilderness variety,
arrests and dialogue marriage actions. It also hosts the live interaction test control plane.

> Checked against the source on 2026-11-17. [ARCHITECTURE.md](../../../ARCHITECTURE.md) describes the whole server
> and the restructuring direction. The world-history contract is documented in the client repository
> (`ReignBeta/docs/WorldHistory.md`).

## Main files

- World history: `WorldHistory.cs`, `WorldHistoryClaimEvidence.cs`, `WorldHistoryRetention.cs`,
  `WorldHistoryCompactionWorker.cs`.
- World Test: `WorldTest.cs`, `WorldTestReadiness.cs`, `WorldTestReports.cs`, `WorldTestTelemetry.cs`,
  `WorldTestRollupWorker.cs` (started by `RunServer`).
- Campaign commands and actions: `CampaignCommandSystem.cs`, `DialogueActionAuthority.cs`,
  `DialogueMarriageActions.cs`, `ArrestSystem.cs` (`TryHandleArrestRoute`).
- Social events: `SocialEventTurns.cs`, `SocialEventApproaches.cs`, `GeneratedWildernessVariety.cs`. The start,
  respond, resolve and history routes for social events are still in `Platform/Program.cs`.
- `LiveInteractionTest.cs`: the server side of the live in-game test harness (`/tests/live/*`), driven by
  `tests/ReignLiveTest`.

## Routes

`/world-history/ingest-batch`, `/world-history/timeline/open`, `GET /world-history/query`,
`GET /world-history/event/*`, `GET /world-history/correlation/*`, `/world-history/verify`,
`GET /world-history/export`, `/world-test/heartbeat`, `GET /world-test/campaigns|overview|export|details|checkpoints|comparison`,
`/events/social/approaches/roll`, `/events/social/turn`, `/events/social/turn-plan`, `/campaign-commands/report`,
`/campaign-commands/judge`, `/arrests/case/apply`, `/arrests/evidence/evaluate`.

Test routes: `/world-history/tests`, `GET /world-test/tests`, `/arrests/test/contracts`, `/tests/live/*` (arm,
runtime, target search, run start/pause/resume/cancel/status/report, command enqueue, game heartbeat/poll/ack/result),
`/tests/campaign-command/*` (manifest, prepare, save verify, profile result, status, report, replay, resume, cancel,
execution, readiness).

## Tables

- `EnsureWorldHistorySchema` (run for every campaign on its first connection): `world_history_timelines`,
  `world_history_events`, `world_history_entities`, `world_history_ingest_state`, `world_history_knowledge_rules`,
  `world_history_claim_checks`. `EnsureWorldHistoryRetentionSchema`: `world_history_daily_aggregates`,
  `world_history_seasonal_aggregates`.
- `EnsureWorldTestSchema` and `EnsureWorldTestTelemetrySchema`: `world_test_daily_rollups`,
  `world_test_native_heartbeats`, `world_test_rebellion_memberships`, `world_test_rebellion_movements`,
  `world_test_rebellion_rolls`, `world_test_checkpoint_reports`, `world_test_chunk_counters`,
  `world_test_current_metrics`, `world_test_diagnostic_evidence`, `world_test_manifests`,
  `world_test_relationship_memberships`, `world_test_relationship_pair_memberships`, `world_test_relationship_pairs`,
  `world_test_rollup_queue`.

## Self-tests

`RunWorldHistorySelfTests` (CLI `--run-world-history-tests`), `RunWorldHistoryStressTests`
(`--run-world-history-stress-tests`), `RunWorldTestSelfTests` (`--run-world-test-tests`),
`RunWorldTestRollupSelfTests`, `RunWorldTestTelemetrySelfTests`, `RunCampaignCommandSystemSelfTests`,
`RunCampaignCommandOfflineSimulationChecks`, `RunCampaignCommandLlmMatrixChecks`, `RunArrestSystemSelfTests`,
`RunSocialEventTurnProgressSelfTests`, `RunWildernessVarietySelfTests`, `RunLiveInteractionTestSelfTests`.
VerificationLab suite `campaign_command`. Test and harness files compiled into the server:
`DialogueMarriageActionTests.cs`, `GeneratedWildernessVarietySelfTests.cs`, `SocialEventTurnProgressSelfTests.cs`,
`LiveInteractionTest.cs`.

## Dependencies

Other modules whose members this module references, most-used first: Platform (261), Dialogue (122),
Persistence (60), Relationships (47), Characters (15), Diplomacy (15), Reputation (14), Portraits (4), Court (2),
Rebellion (1).

Counts are approximate: distinct identifiers defined in exactly one other module and referenced per file, summed.
They show coupling, not a layering rule.
