# Persistence module (server)

PostgreSQL storage and campaign lifecycle: database options and connections, the `reign_meta` infrastructure
migrations, per-campaign schemas, Save Sync snapshots and restore, campaign backup export/import/delete, campaign
retirement, and the validation archive.

> Checked against the source on 2026-11-17. [ARCHITECTURE.md](../../../ARCHITECTURE.md) describes the whole server
> and the restructuring direction; [SAVE_SYNC.md](../../../docs/server/SAVE_SYNC.md) describes Save Sync behaviour.

## Main files

- `PostgreSqlStorage.cs`: `ReignPostgreSqlOptions` (`REIGN_DB_*`, databases `reign` and `ReignValidation`) and
  `ReignPostgreSqlStorage` (`EnsureInfrastructure`, `ApplyInfrastructureMigrations`, `RequiredDatabaseSchemaVersion`,
  `OpenCampaignConnection`, `CampaignSchemaName`, `SnapshotSchemaName`, `CloneCampaignToSnapshot`,
  `RestoreCampaignSnapshot`, campaign archive export/import, drop and diagnostics).
- `PostgreSqlSaveSync.cs`, `SaveSync.cs`: Save Sync register, finalize, load, status and the optimiser.
- `CampaignBackups.cs`: campaign export/import/delete/delete-all, zero-save cleanup, storage audit, retirement, and
  `CampaignBackupMutationLock`.
- `CampaignProviderWait.cs`: `CampaignRequestLease` and `CampaignReplacementGeneration`; lets a synchronous HTTP
  request suspend its `CampaignDataGate` read lease during a provider wait.
- `PostgreSqlDialect.cs`, `DatabaseParameterExtensions.cs`, `DatabaseAvailability.cs`, `GlobalDatabaseAliases.cs`,
  `WorldHistoryPostgreSqlBulk.cs`, `PostgreSqlValidationArchive.cs`: SQL helpers, the shared outage probe, bulk
  writes and the validation-run campaign archive.

## Routes

`GET /api/campaigns`, `GET /api/campaigns/storage-audit`, `GET /api/campaigns/export`, `POST /api/campaigns/import`,
`POST /api/campaigns/delete`, `POST /api/campaigns/delete-all`, `POST /api/campaigns/cleanup-zero-save`,
`POST /save-sync/register`, `POST /save-sync/finalize`, `POST /save-sync/delete-native-save`,
`POST /save-sync/campaign-unloaded`, `POST /save-sync/load`, `POST /api/save-sync/preview`, `GET /api/save-sync/status`.

All of these are exempt from the per-request `CampaignDataGate` read lease; the ones that change campaigns take the
write lock themselves.

## Tables

The `reign_meta` schema from `database/reign_meta.sql`: `storage_version`, `campaign_registry`, `save_sync_snapshots`,
`migration_runs`, `final_gauntlet_*` controller tables, plus schema helper functions. This module creates the
`reign_campaign_*` and `reign_save_*` schemas but no per-campaign feature tables (`postgresql_probe` and
`provider_wait_contract` are self-test tables).

## Self-tests

`RunPostgreSqlStorageSelfTests`, `RunCampaignSchemaLockSelfTests`, `RunCampaignProviderWaitSelfTests`,
`RunCampaignBackupSelfTests`, `RunSaveSyncSelfTests` (CLI `--run-save-sync-tests`). Test files compiled into the
server: `PostgreSqlStorageSelfTests.cs`, `CampaignSchemaLockSelfTests.cs`, `CampaignProviderWaitSelfTests.cs`.

## Dependencies

Other modules whose members this module references, most-used first: Platform (129), Dialogue (66),
WorldSimulation (16), Relationships (7), Court (5), Characters (4), Portraits (2), Reputation (1), Rebellion (1).

Counts are approximate: distinct identifiers defined in exactly one other module and referenced per file, summed.
They show coupling, not a layering rule.
