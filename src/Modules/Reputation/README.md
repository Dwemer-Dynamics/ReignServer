# Reputation module (server)

Social reputation: rumors and their promotion into reputations, the social catalog, court social reputation and
ruler favour contact, expanded and directional social projections, public standing, the whoremonger reputation and
the social-balance test harness.

> Checked against the source on 2026-11-17. [ARCHITECTURE.md](../../../docs/ARCHITECTURE.md) describes the whole server
> and the restructuring direction.

## Main files

- `ReputationRoutes.cs`: this module's route registrations (see `Platform/RouteTable.cs`).
- `SocialReputation.cs`, `ExpandedSocialReputation.cs`, `CharacterSocialProfile.cs`, `PublicStanding.cs`.
- `CourtSocialReputation.cs`, `RulerFavorContact.cs`, `WhoremongerReputation.cs`.
- `SocialBalanceTest.cs`: the social-balance harness and its routes (with its `SocialBalanceTest.Profiles.cs` and
  `SocialBalanceTest.Status.cs` partials).

## Service class

Every file except `ReputationRoutes.cs` holds `internal static partial class ReputationService` (not a
`Program` partial). It calls `Program` members with an explicit `Program.` qualifier, and `Program` and the other
modules call it as `ReputationService.X`; the members used across that boundary are `internal`.
`ReputationRoutes.cs` (an `internal static` route class called from `Platform/RouteTable.cs`) registers the service handlers. Relationships, Dialogue,
Characters, WorldSimulation and Platform call into it (social reconciliation, public standing, reputation context in
prompts). The social outcome worker counters, the pending world-history worker set and the social catalog cache are
static fields of `ReputationService`.

## Routes

Registered in `ReputationRoutes.cs`: `/rumors/submit`, `/rumors/status`, `/rumors/correct`, `/social/catalog` (GET and
POST), `/character-editor/social/rumor/add`, `/character-editor/social/reputation/add`,
`/character-editor/social/reputation/retry-reason`, and the harness routes `/tests/social-balance/*` (manifest,
prepare, case result, snapshot, affinity set, affair candidates, status, export). Reputation reason jobs resume at
startup (`ResumePendingReputationReasonJobs`).

## Tables

Created by `EnsureSocialReputationSchema`, `EnsureRumorSchema`, `EnsureExpandedSocialReputationSchema`,
`EnsureCourtSocialReputationSchema`, `EnsureRulerFavorContactSchema`, `EnsurePublicStandingSchema`,
`EnsureCharacterSocialProfileSchema`, `EnsureSocialSubjectWorkSchema`, `EnsureWhoremongerSchema` and
`EnsureSocialBalanceSchema`:

- Rumors and reputation: `rumor_occurrences`, `rumor_occurrences_v2`, `rumor_promotion_results`,
  `rumor_subject_tags`, `rumor_exposure_streaks`, `shared_tag_exposure_streaks`, `character_reputations`,
  `reputation_activations`, `reputation_counterevidence`, `reputation_lifecycle_events`, `reputation_reason_jobs`,
  `social_catalog_revisions`, `social_outcome_receipts`, `social_subject_work_queue`,
  `directional_social_projections`, `character_public_standing`.
- Court: `court_conversation_signal_receipts`, `court_dynamic_reputation_metrics`, `court_flirt_targets`,
  `court_intimacy_exposure_receipts`, `court_player_favoring_exchanges`, `court_player_favoring_exchange_receipts`,
  `court_ruler_favor_contact`, `court_ruler_favor_contact_receipts`, `court_social_signal_evidence`.
- Whoremonger: `whoremonger_activity`, `whoremonger_event_receipts`.
- Social-balance harness: `social_balance_runs`, `social_balance_case_results`, `social_balance_snapshots`,
  `social_balance_longitudinal_checkpoints`.

## Self-tests

`RunRumorSubsystemSelfTests` (CLI `--run-rumor-tests`), `RunSocialBalanceHarnessSelfTests`
(`--run-social-balance-harness-tests`), `RunRulerFavorContactBatchSelfTests`, `RunWhoremongerSelfTests`,
`RunWhoremongerDelayedConfirmationSelfTests`, all `ReputationService` members. VerificationLab suite `social_reputation`. Test files compiled into
the server: `RulerFavorContactBatchSelfTests.cs`, `SocialBalanceTest.cs`, `WhoremongerReputationSelfTests.cs`.

## Dependencies

Other modules whose members this module references, most-used first: Platform (116), Dialogue (26),
Relationships (25), Persistence (17), WorldSimulation (16), Characters (14), Court (3), Government (1),
Rebellion (1), Portraits (1).

Counts are approximate: distinct identifiers defined in exactly one other module and referenced per file, summed.
They show coupling, not a layering rule.
