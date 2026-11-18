# Court module (server)

Castle court life: court sessions, agendas and matters, offices and regents, the royal council, ruler petitions,
court-life scenes and dialogue, noble visitors, family visits and family chambers, patronage history, ambassadors
(own and foreign), international court referrals, intelligence operations, obligations and plots, castle chat
prompts and scene generation. `ExcludeCourt=true` compiles this folder (and Spymaster) out.

> Checked against the source on 2026-11-17. [ARCHITECTURE.md](../../../docs/ARCHITECTURE.md) describes the whole server
> and the restructuring direction. Testing notes:
> [CapitalAmbassadorTesting](../../../docs/server/CapitalAmbassadorTesting.md).

## Main files

- `CourtSystem.cs`: sessions, agenda, matters, offices, regents, ambassadors, intelligence, obligations, plots,
  `CapitalCourtApi` (the wrapper most court routes, including Spymaster's, go through) and `RunCourtSelfTests`.
- Dialogue and scenes: `CourtLifeDialogue.cs`, `CourtLifeScenes.cs`, `CourtLifeResolutionContinuity.cs`,
  `RoyalCouncil.cs`, `RulerDocketDialogue.cs`, `NobleVisitorDialogue.cs`, `CourtCharacter.cs`.
- Family: `ReignFamilyVisits.cs`, `FamilyChambers.cs`.
- Ambassadors and international: `ForeignAmbassadorSystem.cs`, `InternationalCourtLife.cs`,
  `CapitalAmbassadorTestSystem.cs` (test harness).
- Castle chat: `CastleChatPrompts.cs`, `CastleChatOpeningGuard.cs`, `CastleSceneGeneration.cs`; shared rules in
  `shared/Reign.Shared.Source/Core/ReignCastleChatCore.cs`.
- `PatronageHistory.cs`.

## Service class

Every file except `Program.CourtRoutes.cs` holds `internal static partial class CourtService` (not a `Program`
partial), apart from the test classes that were already their own types (`CastleChatSelfTests`,
`CastleSceneImageContractSelfTests`, `FamilyChambersSelfTests`, `WarCouncilScrollSelfTests`). It calls `Program`
members with an explicit `Program.` qualifier, and `Program` and the other modules call it as `CourtService.X`; the
members used across that boundary are `internal`. `Program.CourtRoutes.cs` stays part of `Program` and registers the
service handlers, and Spymaster's route file passes its handlers to `CourtService.CapitalCourtApi`. Characters,
Dialogue, Relationships and Platform call into it for court character data (`EnsureCourtCharacterData`,
`LoadCourtCharacterScheme`) and court context in prompts.

## Routes

`/court/session/open|tick|close`, `/court/agenda/build`, `/court/home`, `/court/view`,
`/court/matter/start|respond|resolve`, `/court/offices/assign|dismiss`, `/court/regent/assign|dismiss`,
`/court/royal-council/respond`, `/court/ruler-petition/respond`, `/court/life/scene|respond|interpret|family`,
`/court/life/international/pending|resolve|refer`, `GET /court/patronage/public-history`,
`/court/ambassadors/assign|mission|recall`, `/court/foreign-ambassadors/establish|dismiss|end|location|authority|tick`,
`/court/foreign-ambassadors/referrals/respond`, `/court/intelligence/start|cancel`,
`/court/obligations/query|upsert|resolve`, `/court/plots/query|upsert`, `/court/world-action/validate`,
`/court/diplomacy/propose`, `/court/supply/validate`, `/court/native/validate`, `/api/castle-chat/prompts`,
`/castle-chat/scene/generate`, `/family-chambers/scene/generate`, `/family-chambers/childhood/mature`.

Test routes: `/court/foreign-ambassadors/test`, `GET /court/tests`.

## Tables

- `EnsureCourtSchema`: `court_sessions`, `court_agenda`, `court_matters`, `court_commands`, `court_audit`,
  `court_office_history`, `court_regent_history`, `court_plots`, `ambassador_postings`, `intelligence_operations`.
- `EnsureForeignAmbassadorSchema`: `foreign_ambassador_postings`, `ambassador_decisions`,
  `ambassador_official_archive`, `ambassador_pressure_audit`, `ambassador_pressure_daily`, `ambassador_referrals`.
- `EnsureInternationalCourtLifeSchema`: `international_court_queue`, `international_court_referrals`.
- `EnsureFamilyVisitSchema`: `court_family_visits`, `court_family_visit_turns`, `court_family_attention`,
  `court_family_reconciliations`. `EnsureFamilyChambersSchema`: `childhood_experiences`,
  `childhood_maturity_receipts`.
- `court_life_turns`, `royal_council_turns` (`EnsureRoyalCouncilSchema`), `ruler_petition_turns`
  (`EnsureRulerPetitionDialogueSchema`).

## Self-tests

`CourtService.RunCourtSelfTests` (CLI `--run-court-tests`, route `/court/tests`), `CourtService.RunCourtCharacterSelfTests`. Test files compiled
into the server: `CastleChatSelfTests.cs`, `CastleSceneImageContractSelfTests.cs`,
`CourtLifeCreatorProfileSelfTests.cs`, `FamilyChambersSelfTests.cs`, `FamilyVisitSelfTests.cs`,
`WarCouncilScrollSelfTests.cs`, and the `CapitalAmbassadorTestSystem.cs` harness.

## Dependencies

Other modules whose members this module references, most-used first: Platform (186), Dialogue (94), Diplomacy (25),
Persistence (12), Relationships (12), Reputation (12), Portraits (8), Characters (4), WorldSimulation (2).

Counts are approximate: distinct identifiers defined in exactly one other module and referenced per file, summed.
They show coupling, not a layering rule.
