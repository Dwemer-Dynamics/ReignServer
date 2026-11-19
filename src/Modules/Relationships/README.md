# Relationships module (server)

NPC relationships and their consequences: the relationship director and its action outbox, the continuous
relationship worker, pair lifecycle, chemistry and MBTI compatibility, flings, arranged marriage and spouse
separation, storylines and shared relationship history, native relation projection and sync, clan conflicts,
correspondence (letters, delivery, autonomous letters), player approaches, family (conception, pregnancy,
parentage) and ruler judgment relations.

> Checked against the source on 2026-11-17. [ARCHITECTURE.md](../../../docs/ARCHITECTURE.md) describes the whole server
> and the restructuring direction. Feature docs: [Correspondence](../../../docs/server/Correspondence.md),
> [PregnancyContext](../../../docs/server/PregnancyContext.md).

## Main files

- Director and worker: `RelationshipDirector.cs`, `ContinuousRelationshipWorker.cs` (started by `RunServer`),
  `RelationshipWorkScheduling.cs`, `RelationshipLifecycle.cs`, `RelationshipSimulation.cs`,
  `WorldRelationshipModel.cs`, `AmbientRelationships.cs`, `MbtiRelationships.cs`, `NotableMbtiProfiles.cs`.
- Pair features: `RelationshipFlings.cs`, `ArrangedMarriage.cs`, `SpouseSeparation.cs`, `RelationshipStorylines.cs`,
  `SharedRelationshipHistory*.cs`, `RelationshipHeroObservations.cs`, `ConversationRelationships.cs`,
  `RelationshipPrompts.cs`.
- Native game sync: `NativeRelationProjection.cs`, `RelationshipNativeTargetApi.cs`.
- Wider effects: `ClanConflictSystem.cs`, `RulerJudgmentRelations.cs`, `PlayerApproaches.cs`, `PregnancyContext.cs`.
- Letters: `Relationships.cs`, `CorrespondenceDelivery.cs`, `AutonomousCorrespondence.cs`,
  `CampaignCommandCorrespondence.cs`.

The deterministic policies `RelationshipCompatibilityPolicy` and `ArrangedMarriagePolicy` live in the shared
relationship kernel `shared/Reign.Relationships`; wire types and policies such as `PlayerApproachPolicy`,
`NativeRelationEffectLedger` and `CorrespondenceTransitPolicy` live in `shared/Reign.Core.Contracts`.
The clan-conflict notice routes use the typed contracts in
`shared/Reign.Core.Contracts/Relationships/ClanConflictNoticeContracts.cs` (see
[TypedContracts](../../../docs/server/TypedContracts.md)).

## Service class

The module's `Program` partials are now `internal static partial class RelationshipsService` in their existing files.
It calls `Program` members with an explicit `Program.` qualifier, and `Program` and the other modules call it as
`RelationshipsService.X`; the members used across that boundary are `internal`. `RelationshipsRoutes.cs` (an `internal static` route class called
from `Platform/RouteTable.cs`) registers the service handlers. Dialogue, Reputation, Characters, WorldSimulation and Platform
call into it for relationship state, MBTI profiles, correspondence and the relationship worker. Its static state (the
relationship hero document cache, worker and scheduling state) lives in `RelationshipsService`. The self-tests listed
below are `RelationshipsService` members.

## Routes

`/relationships/evaluate`, `/relationships/context`, `/relationships/effective/batch`,
`/relationships/conversation/evaluate|native-receipt`, `GET /relationships/conversation/recent`,
`/relationships/history/ensure`, `/relationships/history/query` (GET and POST),
`/relationships/director/snapshot|run|poll_actions|report_action|report_actions|status`,
`/relationships/ambient/snapshot`, `/relationships/ambient/status` (GET and POST),
`/relationships/ambient/native_sync/report|pull|receipts`, `/relationships/initialization/seed`,
`/political-pressure/evaluate`, `GET /clan-conflicts/notices`, `/clan-conflicts/acknowledge`,
`/family/verify_conception_attempt`, `/family/pregnancy/sync`, `/family/conception/report`,
`/family/parentage/reveal`, `/correspondence/threads|send|tick|read`,
`/correspondence/autonomous/candidates|evaluate`, `/approaches/evaluate|decision`,
`/notables/mbti/initialize|confirm-batch`, `/rulers/*` (dispatched by `RulerRelationsApi`).

Retired routes kept for compatibility (they return `RetiredRelationshipMechanicResult`):
`/relationships/developments/poll|resolve`, `/relationships/simulation/census/status`, `/relationships/simulation/start|stop|status`.

## Tables

Created by the `Ensure*Schema` methods in this folder (for example `EnsureRelationshipDirectorSchema`,
`EnsureRelationshipLifecycleSchema`, `EnsureWorldRelationshipSchema`, `EnsureMbtiRelationshipSchema`,
`EnsureClanConflictSchema`, `EnsureCorrespondenceDeliverySchema`, `EnsurePregnancyContextSchema`):

- Director and pairs: `relationship_director_runs`, `relationship_director_actions`, `director_outbox`,
  `relationship_daily_inputs`, `relationship_incidents`, `relationship_interaction_receipts`,
  `relationship_pair_chemistry`, `relationship_pair_lifecycle`, `relationship_pair_provenance`,
  `relationship_personalities`, `relationship_observed_heroes`, `relationship_hero_observations`,
  `relationship_opening_seed_runs`, `relationship_story_threads`, `relationship_story_seeded_pairs`,
  `relationship_marriage_reservations`, `relationship_affair_commitments`, `relationship_conception_commitments`,
  `relationship_ruler_receipts`, `relationship_native_sync_batches`, `relationship_native_targets`,
  `relationship_fling_encounters`, `relationship_fling_profiles`, `relationship_fling_rolls`,
  `relationship_fling_sparks`, `shared_relationship_history_chapters`, `notable_mbti_profiles`,
  `conversation_relationship_benefits`, `conversation_relationship_receipts`, `public_standing_invalidations`.
- Marriage and family: `arranged_marriage_effects`, `arranged_marriage_periods`, `arranged_marriage_refusals`,
  `marriage_evaluations`, `marriage_leader_rolls`, `conception_attempts`, `conception_contexts`,
  `conception_knowledge`, `conceptions`, `parentage`.
- Clan conflicts: `clan_conflict_daily_rolls`, `clan_conflict_day_sweeps`, `clan_conflict_incidents`,
  `clan_conflict_relationship_effects`.
- Letters and approaches: `letters`, `correspondence_threads`, `correspondence_delivery_jobs`,
  `autonomous_correspondence_checks`, `autonomous_correspondence_cooldowns`, `player_approach_state`,
  `player_attention_events`.

## Self-tests

`RunCurrentRelationshipSelfTests` (CLI `--run-relationship-tests` and `--run-relationship-simulation-tests`;
it includes the `clan_conflicts` suite and its notice wire-contract checks),
`RunPregnancyChoiceSelfTests` (`--run-pregnancy-choice-tests`), `RunNotableMbtiSelfTests`
(`--run-notable-mbti-tests`); `--run-relationship-simulation-audit` is retired and only returns a message. About 30
suites in total, many aggregated by `RelationshipLifecycle.cs`, for example `RunRelationshipLifecycleSelfTests`,
`RunWorldRelationshipModelSelfTests`, `RunRelationshipThroughputSelfTests`, `RunRulerJudgmentSelfTests`,
`RunPlayerApproachSelfTests`, `RunSharedRelationshipHistorySelfTests`.

Test and replay files compiled into the server: `CorrespondenceDeliverySelfTests.cs`,
`CorrespondenceTransitSelfTests.cs`, `NativeRelationEffectSelfTests.cs`, `PlayerApproachSelfTests.cs`,
`PregnancyContextSelfTests.cs`, `RelationshipThroughputReplay.cs`, `RelationshipThroughputSelfTests.cs`,
`RulerJudgmentSelfTests.cs`, `SharedRelationshipHistoryEvidenceSelfTests.cs`.

## Dependencies

Other modules whose members this module references, most-used first: Platform (504), Dialogue (226),
Persistence (123), WorldSimulation (53), Characters (49), Reputation (43), Portraits (9), Court (7), Diplomacy (6),
Rebellion (2), Government (1).

Counts are approximate: distinct identifiers defined in exactly one other module and referenced per file, summed.
They show coupling, not a layering rule.
