# Rebellion module (server)

The rebellion director: rebellion movements, memberships, backing requests and recognition, and the negotiated
action drafts and approvals used by rebellion negotiations. One file, `RebellionDirector.cs`.

> Checked against the source on 2026-11-17. [ARCHITECTURE.md](../../../ARCHITECTURE.md) describes the whole server
> and the restructuring direction. Testing notes: [RebellionTesting](../../../docs/server/RebellionTesting.md).

## Routes

`/rebellions/evaluate`, `GET /rebellions/query`, `/negotiations/draft`, `/negotiations/respond`,
`GET /negotiations/query`, and the test route `GET /rebellions/tests`.

## Tables

`EnsureRebellionSchema`: `rebellion_movements`, `rebellion_memberships`, `rebellion_backing_requests`,
`rebellion_recognition_attempts`, `rebellion_transitions`, `negotiated_action_drafts`, `negotiated_action_approvals`.

## Self-tests

`RunRebellionSelfTests` (CLI `--run-rebellion-tests`, route `/rebellions/tests`). The VerificationLab `rebellion`
suite (`RunExtendedRebellionChecks`) lives in `Platform/VerificationLab.cs`.

## Dependencies

Other modules whose members this module references, most-used first: Platform (28), Diplomacy (6), Dialogue (6),
Relationships (2), Characters (2), Persistence (1), Portraits (1).

Counts are approximate: distinct identifiers defined in exactly one other module and referenced per file, summed.
They show coupling, not a layering rule.
