# ReignServer

## DwemerDistro 0.1.0

ReignServer runs inside Debian 12 WSL as a self-contained .NET 10 application. Install it from the launcher's Reign page or use `sudo ddistro_server install reign --branch reign`. Choose `dev` for beta or `unstable` for development. Development flows through `unstable -> dev -> reign`; `reign` is the default production branch and replaces `main`. Promote compatible client and server revisions together through reviewed PRs.

DwemerDistro owns startup, shutdown, PostgreSQL (`reign`, UTF-8, port 5432), and the Python vector worker. Open the Control Center on port 8089. Apache forwards to loopback port 5101; the owned worker uses 5102 so MiniMe keeps 8082. Application files live in `/var/www/html/ReignServer/runtime`, source in `/var/www/html/ReignServer`, and persistent data in `/var/www/html/ReignServer/data`. Updates retain previous executables and pin dependency/model directories by source hashes. Uninstall disables the runtime and retains the checkout, database and persistent data; Repair reinstalls the application.

For local development, see `scripts/skills/reign-full-deploy/SKILL.md` and `scripts/skills/reign-server-wsl-deploy/SKILL.md`. The private paired validator builds the client and Linux component; `scripts/Deploy-LocalWsl.ps1` deploys successful reports. Public distro updates use `scripts/Build-Linux.py` from a clean checkout. Neither route launches the game or calls paid providers.

## Release version and date

Database schema versions are separate from release versions. `reign_meta.storage_version` records the applied PostgreSQL schema version (currently 2). Startup runs the embedded `database/reign_meta.sql` migrations under a transaction and advisory lock before accepting requests. For a future schema change, append a version-gated migration and advance `RequiredDatabaseSchemaVersion` in `PostgreSqlStorage.cs` together; advance the stored version only after the migration succeeds. Existing campaign and Save Sync upgrade behavior stays server-owned.

The health endpoint reports applied/required schema versions. DwemerDistro verifies those fields during install/update activation and relays the result to the launcher console; the dashboard displays the same status. An older binary refuses a newer database schema before changing it. Code rollback does not downgrade databases. Builds predating schema reporting remain launchable for retained-runtime compatibility, with an explicit unverified-version message.

Both repositories keep `.version_number.txt` (semantic version, currently `0.1.0`) and `.version.txt` (numeric local release stamp, `yyyyMMddHH`), matching HerikaServer. Set the same release values in both repositories when promoting a paired release. Advance the date stamp for a new build even when the semantic version stays the same. Keep `release/release.json`, client `SubModule.xml`, and runtime version constants aligned when changing the semantic version.

Builds derive their assembly version from `.version_number.txt` and include both files in their output. Local deployment preserves these files with each installed version. The launcher reads the active server artifact and displays `branch | MM-DD-YYYY | version`; a newer date at the same semantic version also signals an update. Rollback restores the metadata with the executable.

The Bannerlord module and native portrait renderer remain on Windows. A validated local installation record maps their shared cache paths into the selected WSL distro; only that distro's managed paths are accepted. The renderer is invoked through WSL interoperability with bounded execution. Its game-dependent rendering still requires separate acceptance. Existing standalone Windows installations require deliberate data and credential migration; DPAPI-encrypted credentials are not portable to Linux.

ReignServer runs locally beside Bannerlord Reign. This public repository contains the server, shared contracts, native portrait generator, vector worker and release packaging sources. The game module is maintained in [Reign](https://github.com/Dwemer-Dynamics/Reign). Internal CLR namespaces and client module identities remain unchanged for compatibility. The Linux executable is named `ReignServer`.

For local development, use sibling checkouts named Reign and ReignServer. Run `scripts/Connect-Repositories.ps1`, then use the canonical Reign MCP validator from Reign. The source junctions keep integration tooling working while every file has one Git owner. Commit server changes here and client changes in Reign.


ReignServer supports Linux x64 only. Windows server installers, DPAPI and desktop lifetime management are retired. The Windows client and native portrait renderer remain separate client components.

Approved first-party shared portraits belong in the private Reign module at ReignBeta/PortraitCache/_shared, with the tracked ReignBeta/PortraitCache/shared-portrait-inventory.json. Deploy and package the verified module inventory; do not rely on a separate or machine-local first-party portrait payload.

## Repository layout

- `ReignServer.csproj` and `src/Modules`: Linux server project and feature modules.
- `ui`: Control Center HTML and assets, packaged with each retained runtime version.
- `prompts`, `profiles`, `database`: AI defaults, narrative profiles and PostgreSQL schema.
- `services/vector-worker`: Python embedding and vector service.
- `shared`: shared contracts and relationship modules.
- `tests`: verification runner, live-test controller and fixtures.
- `tools/portrait-generator`: Windows client-side native portrait tooling.
- `scripts`: canonical public build, local deployment and paired-workspace setup.
- `release`: release metadata, dependency locks and third-party notices.
- `resources`, `docs`: model assets and documentation.
- `data` and `runtime`: ignored, persistent local runtime directories; never source-controlled.

The retired SQLite importer and one-time repository split importer have been removed. PostgreSQL data, save identities and active backup compatibility are unchanged. Upgrade DwemerDistro Core before deploying the renamed executable; the companion Core change accepts both executable names for retained-version rollback.

Paired development uses one `Reign/ReignServer` junction pointing at the sibling public checkout. Run `scripts/Connect-Repositories.ps1` in a fresh paired workspace. Existing obsolete junctions must be reviewed before removal; the setup script never deletes them or replaces an unrelated directory.
