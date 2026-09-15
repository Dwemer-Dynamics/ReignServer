# ReignServer

## DwemerDistro 0.1.0

ReignServer runs inside Debian 12 WSL as a self-contained .NET 10 application. Install it from the launcher's Reign page or use `sudo ddistro_server install reign --branch reign`. Choose `dev` for beta or `unstable` for development. Development flows through `unstable -> dev -> reign`; `reign` is the default production branch and replaces `main`. Promote compatible client and server revisions together through reviewed PRs.

DwemerDistro owns startup, shutdown, PostgreSQL (`Reign`, UTF-8, port 5432), and the Python vector worker. Open the Control Center on port 8089. Apache forwards to loopback port 5101; the owned worker uses 5102 so MiniMe keeps 8082. Application files live in `/opt/dwemerdistro/reign`, source in `/var/www/html/ReignServer`, and persistent data in `/var/lib/dwemerdistro/reign`. Updates retain previous executables and pin dependency/model directories by source hashes. Uninstall retains the database and persistent data; Repair reinstalls the application.

For local development, see `ReignRelease/skills/reign-full-deploy/SKILL.md` and `reign-server-wsl-deploy/SKILL.md`. The private paired validator builds the client and Linux component; `ReignRelease/Deploy-LocalWsl.ps1` deploys successful reports. Public distro updates use `ReignRelease/Build-Linux.py` from a clean checkout. Neither route launches the game or calls paid providers.

## Release version and date

Both repositories keep `.version_number.txt` (semantic version, currently `0.1.0`) and `.version.txt` (numeric local release stamp, `yyyyMMddHH`), matching HerikaServer. Set the same release values in both repositories when promoting a paired release. Advance the date stamp for a new build even when the semantic version stays the same. Keep `ReignRelease/release.json`, client `SubModule.xml`, and runtime version constants aligned when changing the semantic version.

Builds derive their assembly version from `.version_number.txt` and include both files in their output. Local deployment preserves these files with each installed version. The launcher reads the active server artifact and displays `branch | MM-DD-YYYY | version`; a newer date at the same semantic version also signals an update. Rollback restores the metadata with the executable.

The Bannerlord module and native portrait renderer remain on Windows. A validated local installation record maps their shared cache paths into the selected WSL distro; only that distro's managed paths are accepted. The renderer is invoked through WSL interoperability with bounded execution. Its game-dependent rendering still requires separate acceptance. Existing standalone Windows installations require deliberate data and credential migration; DPAPI-encrypted credentials are not portable to Linux.

ReignServer runs locally beside Bannerlord Reign. This public repository contains the server, shared contracts, native portrait generator, vector worker and release packaging sources. The game module is maintained in [Reign](https://github.com/Dwemer-Dynamics/Reign). Internal ReignBeta names remain for save compatibility.

For local development, use sibling checkouts named Reign and ReignServer. Run `ReignRelease/Connect-Repositories.ps1`, then use the canonical Reign MCP validator from Reign. The source junctions keep integration tooling working while every file has one Git owner. Commit server changes here and client changes in Reign.

Installation packages include separate verified runtime/model/content payloads. These are distributed separately from Git through a configurable private host or the complete offline package. Each user supplies their own AI credentials. The complete Windows preview package has passed an isolated local reinstall, save migration and portrait-discovery repair. On 2026-09-14 the user confirmed the installed build was working and authorized the initial source uploads, superseding the earlier upload hold in the initial workflow notes. Private download hosting and execution on a second computer remain separate follow-up work.
