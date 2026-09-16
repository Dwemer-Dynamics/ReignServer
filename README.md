# ReignServer

## DwemerDistro 0.1.0

ReignServer runs inside Debian 12 WSL as a self-contained .NET 10 application. Install it from the launcher's Reign page or use `sudo ddistro_server install reign --branch reign`. Choose `dev` for beta or `unstable` for development. Development flows through `unstable -> dev -> reign`; `reign` is the default production branch and replaces `main`. Promote compatible client and server revisions together through reviewed PRs.

DwemerDistro owns startup, shutdown, PostgreSQL (`reign`, UTF-8, port 5432), and the Python vector worker. Open the Control Center on port 8089. Apache forwards to loopback port 5101; the owned worker uses 5102 so MiniMe keeps 8082. Application files live in `/var/www/html/ReignServer/runtime`, source in `/var/www/html/ReignServer`, and persistent data in `/var/www/html/ReignServer/data`. Updates retain previous executables and pin dependency/model directories by source hashes. Uninstall disables the runtime and retains the checkout, database and persistent data; Repair reinstalls the application.

For local development, see `ReignRelease/skills/reign-full-deploy/SKILL.md` and `reign-server-wsl-deploy/SKILL.md`. The private paired validator builds the client and Linux component; `ReignRelease/Deploy-LocalWsl.ps1` deploys successful reports. Public distro updates use `ReignRelease/Build-Linux.py` from a clean checkout. Neither route launches the game or calls paid providers.

## Release version and date

Database schema versions are separate from release versions. `reign_meta.storage_version` records the applied PostgreSQL schema version (currently 2). Startup runs the embedded `ReignBetaServer/postgresql/reign_meta.sql` migrations under a transaction and advisory lock before accepting requests. For a future schema change, append a version-gated migration and advance `RequiredDatabaseSchemaVersion` in `PostgreSqlStorage.cs` together; advance the stored version only after the migration succeeds. Existing campaign and Save Sync upgrade behavior stays server-owned.

The health endpoint reports applied/required schema versions. DwemerDistro verifies those fields during install/update activation and relays the result to the launcher console; the dashboard displays the same status. An older binary refuses a newer database schema before changing it. Code rollback does not downgrade databases. Builds predating schema reporting remain launchable for retained-runtime compatibility, with an explicit unverified-version message.

Both repositories keep `.version_number.txt` (semantic version, currently `0.1.0`) and `.version.txt` (numeric local release stamp, `yyyyMMddHH`), matching HerikaServer. Set the same release values in both repositories when promoting a paired release. Advance the date stamp for a new build even when the semantic version stays the same. Keep `ReignRelease/release.json`, client `SubModule.xml`, and runtime version constants aligned when changing the semantic version.

Builds derive their assembly version from `.version_number.txt` and include both files in their output. Local deployment preserves these files with each installed version. The launcher reads the active server artifact and displays `branch | MM-DD-YYYY | version`; a newer date at the same semantic version also signals an update. Rollback restores the metadata with the executable.

The Bannerlord module and native portrait renderer remain on Windows. A validated local installation record maps their shared cache paths into the selected WSL distro; only that distro's managed paths are accepted. The renderer is invoked through WSL interoperability with bounded execution. Its game-dependent rendering still requires separate acceptance. Existing standalone Windows installations require deliberate data and credential migration; DPAPI-encrypted credentials are not portable to Linux.

ReignServer runs locally beside Bannerlord Reign. This public repository contains the server, shared contracts, native portrait generator, vector worker and release packaging sources. The game module is maintained in [Reign](https://github.com/Dwemer-Dynamics/Reign). Internal ReignBeta names remain for save compatibility.

For local development, use sibling checkouts named Reign and ReignServer. Run `ReignRelease/Connect-Repositories.ps1`, then use the canonical Reign MCP validator from Reign. The source junctions keep integration tooling working while every file has one Git owner. Commit server changes here and client changes in Reign.


ReignServer supports Linux x64 only. Windows server installers, DPAPI and desktop lifetime management are retired. The Windows client and native portrait renderer remain separate client components.
