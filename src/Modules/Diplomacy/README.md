# Diplomacy module (server)

Kingdom-level politics: the world diplomacy director and its event queue, kingdom-leader diplomacy (agreements,
treaties, incidents, war origins), political pressure, and clan accords.

> Checked against the source on 2026-11-17. [ARCHITECTURE.md](../../../ARCHITECTURE.md) describes the whole server
> and the restructuring direction.

## Main files

`WorldDiplomacyDirector.cs`, `WorldDiplomacyDebug.cs`, `KingdomLeaderDiplomacy.cs`, `PoliticalPressureSystem.cs`,
`ClanAccords.cs`; tests in `ClanAccordTests.cs` and `ClanAccordServerTests.cs`.

## Routes

`/diplomacy/director/evaluate`, `/diplomacy/debug/random-event`, `GET /diplomacy/events/next`,
`/diplomacy/events/acknowledge`, `GET /political-pressure/notices`, `/political-pressure/acknowledge`,
`/clan-accords/sync`. (`/political-pressure/evaluate` is handled by `EvaluatePoliticalIncidentsCombined` in
Relationships.)

## Tables

- `EnsureKingdomLeaderDiplomacySchema`: `ruler_agreement_history`, `ruler_diplomacy_activity`,
  `ruler_diplomacy_opportunities`, `ruler_diplomatic_incidents`, `ruler_relationship_crossings`,
  `ruler_relationship_rolls`, `ruler_treaty_obligations`, `ruler_war_origins`.
- `EnsurePoliticalPressureSchema`: `political_pressure_state`, `political_pressure_incidents`,
  `political_pressure_activity`, `political_pressure_action_rolls`, `political_pressure_daily_rolls`,
  `political_pressure_delta_receipts`, `political_pressure_native_effects`.
- `EnsureClanAccordsSchema`: `clan_accord_snapshots`, `clan_accord_event_receipts`, `clan_accord_relation_receipts`.

## Self-tests

`RunWorldDiplomacySelfTests` (CLI `--run-diplomacy-tests`), `RunPoliticalPressureSelfTests`,
`RunClanAccordSelfTests`, `RunClanAccordServerSelfTests`. Test files compiled into the server: `ClanAccordTests.cs`,
`ClanAccordServerTests.cs`; `WorldDiplomacyDebug.cs` backs the debug route.

## Dependencies

Other modules whose members this module references, most-used first: Platform (96), Dialogue (23),
Relationships (16), Persistence (11), Court (5), WorldSimulation (4), Characters (3), Reputation (2), Rebellion (1).

Counts are approximate: distinct identifiers defined in exactly one other module and referenced per file, summed.
They show coupling, not a layering rule.
