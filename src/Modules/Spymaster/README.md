# Spymaster module (server)

The spymaster office of a capital court: mission memory, social intelligence, exposure, and foreign agents. Compiled
out together with Court when `ExcludeCourt=true`.

> Checked against the source on 2026-11-17. [ARCHITECTURE.md](../../../docs/ARCHITECTURE.md) describes the whole server
> and the restructuring direction. Testing notes: [SpymasterTesting](../../../docs/server/SpymasterTesting.md).

## Main files

- `SpymasterRoutes.cs`: this module's route registrations (see `Platform/RouteTable.cs`). It stays part of
  `Program` and calls the `SpymasterService` handlers.
- `SpymasterSystem.cs`: the handlers and schema, in `internal static partial class SpymasterService` (not a `Program`
  partial). It calls `Program` members with an explicit `Program.` qualifier. Shared game-free rules are in
  `shared/Reign.Shared.Source/Core/ReignSpymasterCore.cs`.

## Routes

All `POST`, each wrapped in Court's `CapitalCourtApi`: `/court/spymaster/memory`, `/court/spymaster/social/status`,
`/court/spymaster/social/apply`, `/court/spymaster/exposure`, `/court/spymaster/agents/tick`,
`/court/spymaster/agents/status`, `/court/spymaster/agents/expose`.

## Tables

`EnsureSpymasterSchema`: `spymaster_mission_memory`, `foreign_agent_assignments`.

## Self-tests

`SpymasterService.RunSpymasterSystemSelfTests`, VerificationLab suite `spymaster`. Test file compiled into the server:
`SpymasterSystemSelfTests.cs` (also part of `SpymasterService`).

## Dependencies

Other modules whose members this module references, most-used first: Platform (14), Dialogue (4),
Relationships (3), Reputation (3), Court (1), Diplomacy (1), WorldSimulation (1), Persistence (1). It also relies
on Court's `CapitalCourtApi` for every route.

Counts are approximate: distinct identifiers defined in exactly one other module and referenced per file, summed.
They show coupling, not a layering rule.
