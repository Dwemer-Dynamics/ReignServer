# Characters module (server)

Character identity and construction: who knows whom (identity and acquaintances), the character editor, character
narratives and the profile library, traits and political beliefs, NPC lives, town notables, encountered residents,
wanderers and the tavern house. The basic character upsert/construct routes are handled in the
`Program` partials `Program.CharacterConstruction.cs` and `Program.CharacterProfiles.cs` in this folder.

> Checked against the source on 2026-11-17. [ARCHITECTURE.md](../../../docs/ARCHITECTURE.md) describes the whole server
> and the restructuring direction. Feature docs: [NpcLives](../../../docs/server/NpcLives.md),
> [CharacterConstructionPolicy](../../../docs/server/CharacterConstructionPolicy.md),
> [TraitPercentages](../../../docs/server/TraitPercentages.md).

## Main files

- Identity: `IdentitySystem.cs`, `IdentitySynchronizationPerformance.cs`, `ContextualSelfIntroduction.cs`,
  `CourtAudienceIdentity.cs`.
- Editor and narratives: `CharacterEditor.cs`, `CharacterNarrative*.cs`, `CharacterProfileLibrary.cs`,
  `CharacterTraits.cs`, `TraitPercentages.cs`, `FoundationTraitRangeVNext.cs`, `DynamicCharacteristics.cs`,
  `SkillAwareness.cs`, `PoliticalBeliefs.cs`, `PoliticalEvidence.cs`, `CharacterLifeState.cs`.
- NPC lives and approaches: `NpcLives*.cs`, `NpcApproachConversation.cs`, `NpcLifeInvitationOpening.cs`.
- Populations: `TownNotableProfiles.cs`, `EncounteredResidents.cs`, `EncounteredResidentActions.cs`,
  `WandererPopulation.cs`, `CommonerHousehold.cs`, `CommonerRolePrompt.cs`.
- Tavern house: `TavernHouseConversation.cs`, `TavernHouseActions.cs`.
- `narrative-editor-ui-contract.mjs`: Control Center UI contract script.

## Service class

The module's `Program` partials are now `internal static partial class CharactersService` in their existing files. It
calls `Program` members with an explicit `Program.` qualifier, and `Program` and the other modules call it as
`CharactersService.X`; the members used across that boundary are `internal`. `Program.CharactersRoutes.cs` stays part
of `Program` and registers the service handlers; `Program.CharacterConstruction.cs` and `Program.CharacterProfiles.cs`
are `Program` partials split from `Platform/Program.cs`. Dialogue, Relationships, WorldSimulation, Reputation, Court and
Platform call into it for identity, traits, profiles and NPC lives. Its static state (narrative and political-profile
locks, the character editor authority cache, the town profile queue) lives in `CharactersService`. The self-tests
listed below are `CharactersService` members.

## Routes

`/identity/encounter`, `/identity/introduction`, `/identity/query`, `/identity/reset`,
`/identity/debug/know-everyone`, `GET /characters/profile-library/status`, `/characters/profile-library/action`,
`GET /characters/narrative`, `/character-editor/*` (list, load, construct, save, rollback, portrait upload/select,
native poll/report; `GET /character-editor/portrait`), `/government/political-profiles/sync`,
`/notables/profiles/prewarm`, `/npc-lives/*` (dispatched by `NpcLivesApi`), `/tavern-house/respond`,
`/tavern-house/turn-plan`, `/tavern-house/confirm`, `/tavern-house/close`.

CLI commands: `--author-character-narratives`, `--generate-profile-library`, `--validate-profile-library`,
`--migrate-character-profiles`, `--regenerate-character-personalities`, `--audit-foundation-traits-vnext`.

## Tables

`EnsureIdentitySchema`: `acquaintances`, `identity_evidence`, `identity_roster`. `EnsureCharacterEditorSchema`:
`character_editor_heads`, `character_editor_native_commands`, `character_editor_reconciliation`,
`character_editor_revisions`. `EnsureNpcLivesSchema`: `character_npc_lives_state`.
`EnsureDynamicCharacteristicsSchema`: `dynamic_characteristics`. Much character state is also kept as campaign JSON
files.

## Self-tests

`RunCharacterEditorSubsystemSelfTests` (CLI `--run-character-editor-tests`), `RunTraitPercentageSelfTests`
(`--run-trait-percentage-tests`), `RunCharacterNarrativeTests`, `RunNarrativePersistenceTests`,
`RunCharacterProfileLibrarySelfTests`, `RunCharacterLifeStateSelfTests`, `RunDynamicCharacteristicsSelfTests`,
`RunFoundationTraitRangeVNextSelfTests`, `RunIdentitySubsystemSelfTests`, `RunSkillAwarenessSelfTests`,
`RunNpcLivesSelfTests`, `RunNpcApproachConversationSelfTests`, `RunNpcLifeInvitationOpeningSelfTests`,
`RunTownNotableProfileSelfTests`, `RunTownNotableNarrativePublicationTests`, `RunEncounteredResidentTests`,
`RunWandererPopulationTests`, `RunStartingChildrenTests`, `RunTavernHouseContractTests`, `RunTavernHouseRosterTests`.

Test files compiled into the server: `CharacterLifeStateSelfTests.cs`, `CharacterNarrativePersistenceTests.cs`,
`CharacterNarrativeProviderTests.cs`, `CharacterNarrativeSelfTests.cs`, `ContextualSelfIntroductionSelfTests.cs`,
`EncounteredResidentsTests.cs`, `NpcApproachConversationSelfTests.cs`, `NpcLifeInvitationOpeningSelfTests.cs`,
`NpcLivesSelfTests.cs`, `NpcLivesTestFixtures.cs`, `StartingChildrenTests.cs`, `TavernHouseContractTests.cs`,
`TavernHouseRosterTests.cs`, `TownNotableProfileSelfTests.cs`, `WandererPopulationTests.cs`.

## Dependencies

Other modules whose members this module references, most-used first: Platform (588), Dialogue (294),
Persistence (74), Relationships (43), Portraits (27), WorldSimulation (17), Court (15), Reputation (13),
Government (2).

Counts are approximate: distinct identifiers defined in exactly one other module and referenced per file, summed.
They show coupling, not a layering rule.
