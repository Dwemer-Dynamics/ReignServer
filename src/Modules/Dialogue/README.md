# Dialogue module (server)

The conversation pipeline and memory: conversation sessions and continuity, prompt composition and caching,
structured actions in dialogue, grounding and claim validation, categorized, semantic and specialist memory, group
and party conversation, drinking and intoxication, personal gossip, communications, temporary guests, and the final
conversation gauntlet qualification system. The main turn handlers (`/dialogue/respond`, `/party-chat/respond`,
`/memory/build_context`, `/context/*`) are `Program` partial files at this folder's root (`Program.<Area>.cs`) and call into this module.

> Checked against the source on 2026-11-17. [ARCHITECTURE.md](../../../docs/ARCHITECTURE.md) describes the whole server
> and the restructuring direction. Feature docs in [docs/server](../../../docs/server/README.md), for example
> StructuredActions, ConversationGrounding, DialogueClaimValidation, MemoryPrecision, SpecialistMemory,
> FinalConversationGauntlet.

## Service class

Every file except `Program.DialogueRoutes.cs` and the `Program.<Area>.cs` handler files that used to be a `Program` partial is now
`internal static partial class DialogueService`, in its existing file. Types that were already separate
(`CodexConversationContracts`, the `FinalConversationGauntlet*` classes and others) are unchanged apart from
qualifiers. `DialogueService` calls `Program` members with an explicit `Program.` qualifier, and `Program` and the
other modules call it as `DialogueService.X`; the members used across that boundary are `internal`.
`Program.DialogueRoutes.cs` stays part of `Program` and registers the service handlers, and the main turn handlers
in the `Program.<Area>.cs` partial files here (split from `Platform/Program.cs`) call the service the same way. The module's static state (memory and semantic queues, prompt
caches, the conversation turn dispatch gate, the capacity catalog and late-interaction recovery) lives in
`DialogueService`.

## Folders

The module's files are grouped into feature folders (namespaces are unchanged; the handler files
`Program.<Area>.cs` and `Program.DialogueRoutes.cs` stay at the module root):

- `Conversation/`: conversation state and continuity, `Conversation*.cs`, `LogicalConversation.cs`,
  `SinglePassConversation.cs`, `RoleplayContinuity.cs`, `GroupConversation*.cs`, `TemporaryGuestDialogue.cs`,
  `MotiveAwareConversation*.cs`, `CharacterCommitments.cs`, `PlayerInputAudience.cs`, `PlayerPerformanceContext.cs`,
  `PoliticalDangerPosture.cs`.
- `Drinking/`: `ConversationBeverages.cs`, `ConversationDrink*.cs`, `ConversationIntoxication*.cs`.
- `Prompts/`: `Prompt*.cs`, `ProviderResponseRecovery.cs`, `RetiredPromptMigration.cs`, `Codex*.cs`, `CultureVoice.cs`.
- `Actions/`: `StructuredActions.cs`, `PlayerActionIntents.cs`, `DialogueAction*.cs`, `DialogueAgencyEvidence.cs`,
  `ConversationEffectAcceptance.cs`, `AcceptedItemGiftRouting.cs`.
- `Grounding/`: `DialogueClaim*.cs`, `DialogueAccuracy.cs`, `DialogueAnswerSupport.cs`, `DialogueIntegrity.cs`,
  `DialogueQuestionCoverage.cs`, `ConversationGrounding*.cs`, `ConversationInputGrounding.cs`, `ConversationEvidence.cs`.
- `Memory/`: `CategorizedMemory*.cs`, `SemanticMemory*.cs` (also starts and supervises the vector worker),
  `Memory*.cs`, `LateInteractionMemory.cs`, `TemporalKnowledgeGraph.cs`, `SharedActivityMemory.cs`,
  `WorldHistoryDialogueMemory.cs`, `ConversationRouteMemory.cs`, `LieDetection.cs`.
- `Gossip/`: `PersonalGossip*.cs`.
- `Communications/`: `Communication*.cs`, `CorrespondenceMemory.cs`, `PartyBanter.cs`, `ChatViewSources.cs`.
- `Qualification/`: `NpcDialogueAudit.cs`, `PartyDialogueAudit.cs`, `ConversationReadiness*.cs`,
  `ConversationQualificationFixtures.cs`, `InteractionReplayCorpus.cs`, the `*Replay*.cs` files and
  `SovereignDemeanorQualification.cs`.
- `FinalGauntlet/`: `FinalConversationGauntlet*.cs` (the final conversation gauntlet controller, catalog and evaluation).

Self-test files sit beside the code they test, and split partial files (`Name.*.cs`) stay in the same folder as `Name.cs`.

## Routes

`/memory/conversation/start|finish|recover`, `/memory/conversation_finished`, `GET /memory/background/status`,
`GET /memory/embeddings/status`, `/memory/embeddings/reindex`, `/memory/embeddings/retry-failed`,
`GET /llm/prompt-cache/status`, `/llm/prompt-cache/probe`, `GET /audit/prompt`, `/world-history/lie-check`,
`GET /world-history/lie-checks`, `/conversation-scene/hourly-tick`, `/conversation-scene/formal-outfits/scan`,
`/party-chat/turn-plan`, `/party-banter/*`, `/communications/*` (and `GET /communications/status`), `/gossip/test`,
`/gossip/tick`, `GET /gossip/status`.

Test and qualification routes: `/tests/npc-dialogue/*`, `/tests/party-dialogue/*`, `/tests/final-gauntlet/*`
(catalog, state fingerprint, run start/promote/resume/pause/cancel/status/report, case lease/result, action
evidence, fault arm, review pack), `/tests/live/readiness*`, `/tests/live/fixtures/*`, `/tests/replay-corpus*`.

CLI: `--run-motive-conversation-tests`, `--run-sovereign-demeanor-provider-tests`,
`--reclassify-sovereign-demeanor-report`, `--run-prompt-caching-tests`, `--run-conversation-scene-state-tests`,
`--continuity-repair` (see also `scripts/Repair-ConversationContinuity.ps1`).

## Tables

Created lazily by about 28 `Ensure*Schema` methods (for example `EnsureCategorizedMemorySchema`,
`EnsureSemanticMemorySchema`, `EnsureConversationContinuitySchema`, `EnsurePersonalGossipSchema`,
`EnsureTemporalKnowledgeGraphSchema`). `EnsureCategorizedMemorySchema` and `EnsureSemanticMemorySchema` run for
every campaign on its first connection.

- Conversations: `conversation_sessions`, `conversation_turns`, `conversation_accepted_units`,
  `conversation_action_outbox`, `conversation_continuity`, `conversation_continuity_pending`,
  `conversation_continuity_repairs`, `conversation_continuity_versions`, `conversation_input_receipts`,
  `conversation_reference_edges`, `conversation_route_acceptance`, `conversation_scene_state`,
  `conversation_scene_handoffs`, `conversation_scene_proposals`, `conversation_motive_cooldowns`,
  `conversation_negotiations`, `conversation_negotiation_turns`, `conversation_banter_episodes`,
  `conversation_drink_scene`, `conversation_drinking_turns`, `conversation_intoxication`,
  `group_conversation_state`, `group_conversation_contributions`, `character_performance_events`,
  `knowledge_receipts`.
- Memory: `memory_sources`, `memory_projections`, `memory_background_jobs`, `memory_continuity_branches`,
  `memory_precision_state`, `memory_specialist_sources`, `memory_late_interaction_sources`,
  `memory_late_interaction_attempts`, `memory_restored_sources`, `memory_restored_source_context`,
  `memory_restored_letters`, `memory_restored_letter_context`, `embedding_documents`, `embedding_jobs`,
  `temporal_knowledge_nodes`, `temporal_knowledge_edges`, `temporal_knowledge_assertions`,
  `shared_activity_events`, `shared_activity_observers`, `shared_activity_deliveries`, `shared_travel_contact`,
  `world_history_claim_beliefs`, `world_history_lie_checks`.
- Social: `personal_gossip_clock`, `personal_gossip_history`, `personal_gossip_messages`, `personal_gossip_topics`,
  `personal_gossip_test_fixtures`, `communication_history`, `communication_messages`, `chat_view_frames`,
  `chat_view_jobs`, `chat_view_requests`.
- `reign_meta.final_gauntlet_*` controller tables (`FinalGauntletStore`, metadata connection, outside campaign
  schemas).
- Self-test only: `sentinel`, `source_probe`, `route_atomic_fixture`, `gauntlet_rollback_probe`.

## Self-tests

About 87 suites, for example `RunStructuredActionSelfTests`, `RunCategorizedMemorySubsystemSelfTests`,
`RunSemanticMemorySelfTests`, `RunMemoryPrecisionSelfTests`, `RunPromptCachingSelfTests`,
`RunConversationContinuitySelfTests`, `RunPersonalGossipSelfTests`, `RunCommunicationSelfTests`,
`RunCodexConversationSelfTests` and the `RunFinalConversationGauntlet*SelfTests` family. Most are reached from
`Platform/VerificationLab.cs` (suites `pipeline`, `memory_precision`, `contracts`) or from aggregate suites inside
this module (for example `PromptCaching.cs`).

Test, fixture and replay files compiled into the server: every `*SelfTests.cs` and `*Tests.cs` file here (66 files),
plus `ConversationQualificationFixtures.cs`, `FinalConversationGauntletFixtures.cs`, `DenseMemoryReplay.cs`,
`DialogueAttributionReplay.cs`, `MemoryAccuracyReplay.cs`, `MemoryEvidenceReplay*.cs`, `NpcDialogueAudit.cs`,
`PartyDialogueAudit.cs`, `InteractionReplayCorpus.cs` and the final gauntlet controller files.

## Dependencies

Other modules whose members this module references, most-used first: Platform (2318), Persistence (303),
Characters (118), WorldSimulation (93), Relationships (91), Court (55), Portraits (46), Reputation (12),
Rebellion (3), Diplomacy (1).

Counts are approximate: distinct identifiers defined in exactly one other module and referenced per file, summed.
They show coupling, not a layering rule.
