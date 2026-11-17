# Campaign schema migrations

Each campaign lives in its own PostgreSQL schema (`reign_campaign_<hash>`). Save Sync
snapshots (`reign_save_<hash>`) are full clones of it and imported archives restore one.
Historically the campaign schema was shaped by about 80 `Ensure*Schema` methods that run
lazily on hot paths. This document describes the versioned runner that replaces them
step by step. The shared `reign_meta` schema keeps its own versioned migrations
(`ApplyInfrastructureMigrations` and `database/reign_meta.sql`).

## How it works

Code: `src/Modules/Persistence/CampaignMigrations.cs`.

- The version is the `campaign_migration_version` row in the campaign's own `schema_meta`
  table. A schema without the row is version 0.
- `OpenCampaignConnection` readies a schema on its cold path (the first open in a process,
  and again after `InvalidateCampaignSchemaCaches`). The cold path runs, in order:
  1. `ApplyCampaignBaselineSchema`: migration 1, the core baseline (`EnsureMemorySchema` and
     the methods it calls, categorized memory, world history, semantic memory, the
     categorized-memory backfill and the legacy scalar-memory repair).
  2. `ApplyCampaignSchemaMigrations`: every numbered migration above the recorded version.
  3. Index deduplication, then the in-memory ready markers.
- Migration 1 is re-run on every cold open, as before this runner existed. It is idempotent
  and also reconciles the semantic provider generation from settings, so it cannot be a
  run-once step. Its list entry only records the version.
- Migrations 2 and later run once. Each runs in its own transaction, takes the campaign
  schema-mutation advisory lock (`hashtextextended(current_schema(), 9137)`, the same lock
  every schema DDL statement already takes), re-reads the version, applies its DDL and
  writes the new version before committing. Concurrent openers in one process are also
  serialized by `CampaignSchemaLock`; openers in other processes are serialized by the
  advisory lock. Transient contention retries the whole migration transaction.
- A schema recorded at a newer version than the server knows (after a code rollback) is
  left alone; migrations never downgrade.
- The warm-path check (`IsMemorySchemaRevisionCurrent`) also requires the recorded version
  to be at least the latest. If Save Sync or an import replaces a schema with an older one
  beneath a cached campaign, the next open sees the lower version, clears the caches and
  runs the cold path.
- Restoring a Save Sync snapshot (`RestoreCampaignSnapshot`) or replacing a campaign from an
  archive (`DropCampaign` before `pg_restore`) calls `InvalidateCampaignSchemaCaches`, so the
  next open upgrades the restored schema whatever version it was saved at.

## Migration-owned Ensure methods

When a module's tables move into a numbered migration, keep its `Ensure*Schema` method
callable but make it skip the DDL once the runner has reached that version for the
connection's campaign in this process:

```csharp
private static void EnsurePlayerApproachSchema(ReignDbConnection c)
{
    if (IsCampaignMigrationReady(c, PlayerApproachSchemaMigrationVersion)) return;
    ApplyPlayerApproachSchemaDdl(c);
}
```

`IsCampaignMigrationReady` reads only an in-memory marker (no database query), set by the
cold path and cleared by `InvalidateCampaignSchemaCaches`. If the marker is missing (for
example a connection that did not come from `OpenCampaignConnection`), the method still
applies its idempotent DDL as before.

## Adding a migration

1. Put the DDL in a method of the owning module, for example `ApplyXyzSchemaDdl(ReignDbConnection)`.
   Use `ExecuteSql` without a transaction argument; the statements enlist in the migration
   transaction. Use `CREATE TABLE IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS` and similar
   idempotent forms, because the tables may already exist in schemas created by the old
   `Ensure*` path.
2. Append one entry to `CampaignSchemaMigrations` with the next number. Add a named version
   constant if code outside the list refers to it.
3. Turn the module's `Ensure*Schema` method into the guarded form above.
4. Extend `RunCampaignMigrationSelfTests` in `PostgreSqlStorageSelfTests.cs` (run by the `save_sync` suite, so `--run-save-sync-tests` covers it) if the new
   migration needs a specific check (the generic checks cover every version).

Ordering rules:

- The list is append-only. Never renumber, reorder, edit or delete a shipped migration;
  fix a mistake with a new migration.
- Numbers are contiguous from 1. The self-test fails on gaps.
- A migration may depend only on the baseline and on lower-numbered migrations.
- Create tables and columns before any statement that backfills or seeds them, inside the
  same migration.
- Keep each migration database-only: no settings reads, file I/O, HTTP, LLM or other
  external work inside the transaction, and no second connection (it would wait on the
  locks the migration holds). The one accepted, pre-existing exception: every `ExecuteSql`
  call marks the campaign's Save Sync active state dirty (`MarkSaveSyncActiveStateDirty`,
  a state file written under `SaveSyncActiveStateLock`). A rolled-back migration attempt
  therefore still marks the campaign dirty, which is harmless.
- A failing migration makes `OpenCampaignConnection` throw, which blocks opening the
  campaign until it succeeds. Keep every migration idempotent and short, and keep data
  backfills out of the runner.
- Do not add new DDL to the baseline. Everything new is a numbered migration.
- Large data backfills that cannot run in one short transaction stay outside the runner,
  guarded by their own `schema_meta` markers as today.

## Remaining slices

Slice 1 (this change) adds the runner, records the baseline as migration 1 and moves the
player-approach tables into migration 2.

Later slices each move one group of `Ensure*Schema` methods into new migrations, smallest and
most self-contained first, so a failure points at one module:

| Slice | Group | Methods |
|---|---|---|
| 2 | Small dialogue leaf tables | `EnsureBanterSchema`, `EnsureAcceptedConversationUnitSchema`, `EnsureRouteConversationAcceptanceSchema`, `EnsureLateInteractionMemorySchema`, `EnsureChatViewSchema`, `EnsureChatViewWardrobeSchema`, `EnsureConversationReferenceGraphSchema`, `EnsurePlayerPerformanceSchema` |
| 3 | Characters and portraits | `EnsureNpcLivesSchema`, `EnsurePregnancyContextSchema`, `EnsureCharacterSocialProfileSchema`, `EnsureMemorySnapshotSourceSchema` |
| 4 | Court, Spymaster, Diplomacy | `EnsureFamilyVisitSchema`, `EnsureFamilyChambersSchema`, `EnsureRoyalCouncilSchema`, `EnsureRulerPetitionDialogueSchema`, `EnsureInternationalCourtLifeSchema`, `EnsureForeignAmbassadorSchema`, `EnsureSpymasterSchema`, `EnsureClanAccordsSchema`, `EnsureKingdomLeaderDiplomacySchema`, `EnsurePoliticalPressureSchema` (Court and Spymaster are compiled out with `ExcludeCourt`; keep the DDL for their migrations in a file compiled in both builds so the numbering is identical) |
| 5 | Reputation | `EnsurePublicStandingSchema`, `EnsureRulerFavorContactSchema`, `EnsureWhoremongerSchema`, `EnsureCourtSocialReputationSchema`, `EnsureExpandedSocialReputationSchema`, `EnsureSocialSubjectWorkSchema`, `EnsureRumorSchema` |
| 6 | Relationships | `EnsureAmbientRelationshipSchema`, `EnsureArrangedMarriageSchema`, `EnsureRelationshipStorylineSchema`, `EnsureCorrespondenceDeliverySchema`, `EnsureAutonomousCorrespondenceSchema`, `EnsureSharedRelationshipHistorySchema`, `EnsureNotableMbtiSchema`, `EnsureConversationRelationshipSchema`, `EnsureClanConflictSchema`, `EnsureWorldRelationshipSchema`, `EnsureRelationshipLifecycleSchema`, `EnsureRelationshipDirectorSchema`, `EnsureRelationshipNativeTargetSchema` |
| 7 | Dialogue memory and continuity | `EnsureConversationContinuitySchema`, `EnsureConversationSceneStateSchema`, `EnsureConversationInputSchema`, `EnsureConversationMotiveSchema`, `EnsureConversationAgencySchema`, `EnsureIntoxicationSchema`, `EnsureCommunicationSchema`, `EnsureCorrespondenceMemorySchema`, `EnsureSharedActivityMemorySchema`, `EnsurePersonalGossipSchema`, `EnsureMemorySpecialistSchema`, `EnsureWorldHistoryLieDetectionSchema`, `EnsureWorldHistoryRetentionSchema` |
| 8 | Hot shared schemas with in-memory shape caches | `EnsureMbtiRelationshipSchema` (73 call sites), `EnsureSocialReputationSchema` (60), and the methods already guarded by `IsPostgreSqlComponentSchemaReady` markers. Fold their marker revisions into migrations last, once the earlier slices have proven the pattern. |
| 9 | Clean-up | Drop hot-path `Ensure*` calls whose tables are migration-owned and whose callers only obtain connections from `OpenCampaignConnection` (see below). Retire per-component `schema_meta` revision markers that a migration now supersedes. |

Test-only schemas (`EnsureWorldTestSchema`, `EnsureWorldTestTelemetrySchema`,
`EnsureGossipTestSchema`, `EnsureSocialBalanceSchema`, the Final Conversation Gauntlet
scheduler) stay outside the runner. They belong to the verification flavour and must not
create tables in player campaigns.

The baseline methods (`EnsureMemorySchema` and the methods it calls, categorized memory,
world history and semantic memory) stay in migration 1. A later slice may split the
run-once parts of the baseline into numbered migrations, but the semantic provider
reconciliation must stay on the cold path.

### When a hot-path Ensure call can be dropped

A call to a migration-owned `Ensure*Schema` method can be removed from a hot path when:

- its connection always comes from `OpenCampaignConnection` (the cold path has run the
  runner, so the tables exist), not from `ReignPostgreSqlStorage.OpenCampaignConnection`,
  a snapshot schema or a utility schema;
- no self-test drops the tables and relies on the call to recreate them; and
- the method is not also responsible for a data backfill or an in-memory cache refresh.

Until then the guarded method costs one dictionary lookup, so removal is a clean-up rather
than a performance requirement. Slice 1 removes no call sites.
