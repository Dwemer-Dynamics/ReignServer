---
name: reign-full-deploy
description: Build, deploy, and update the local Reign Bannerlord client and ReignServer inside DwemerDistro WSL. Use for combined Reign local deployment or client refresh; preserve campaigns, credentials, portraits, dependencies, and prior installations. Do not launch Bannerlord or publish releases.
---

# Reign local deployment

Use the paired Reign and ReignServer checkouts. Read their AGENTS.md, confirm origins and dirty work, and run `ReignRelease/Connect-Repositories.ps1` only if the required sibling junctions are absent. Server-owned source belongs in ReignServer.

## Build and deploy

1. Confirm the selected WSL distro and licensed Bannerlord directory. The standard local distro is `DwemerAI4Skyrim3`; resolve the game directory from the current machine. Inventory module IDs in both the game's `Modules` directory and its Steam Workshop `content/261550` directory before adding dependencies. Reuse existing Workshop modules; do not install a second copy of the same module ID. Use `ReignRelease/dependencies.lock.json` for missing dependencies and report differences from its pinned versions without replacing existing user modules. Do not launch the game.
2. Obtain the canonical validation plan and status. Set `REIGN_BANNERLORD_PATH`. Use `ReignMcp/scripts/reign-validate.ps1 -Profile product -Restore -RequestingTaskId <current task UUID>`, then `-LinuxServer -Restore` under the same canonical lease. Do not call product builds directly. Use the broader `all` profile when validating tooling changes; it also requires the licensed Bannerlord Modding Kit for the editor bridge. Report that environment limitation separately if absent. Record the successful product and Linux reports and fingerprints; do not edit source between validation and deployment.
3. Ensure Core's `ddistro_server`, `ddistro_reign`, and UTF-8 setup are installed. WSL owns PostgreSQL database `reign` on 5432. Public port 8089 proxies to Reign's loopback port 5101; its worker owns 5102. A fresh normal installation uses `sudo ddistro_server install reign --branch reign`. For an unmerged local change, sync the selected reviewed ReignServer source to `/var/www/html/ReignServer`, preserving `.git`, then run `sudo ddistro_reign prepare`. Never point updates at a different branch silently.
4. Run the server-owned `ReignRelease/Deploy-LocalWsl.ps1 -Workspace <Reign checkout> -ValidationReport <successful product or all report> -LinuxBuildReport <successful Linux report> -Distro <name> -BannerlordPath <game directory>`. `-SkipServer` refreshes only the client. The script preserves the previous module and installation record and verifies the deployed client version. It installs the validated Windows portrait helper; it does not execute it.
5. Verify `http://127.0.0.1:8089/health`, version agreement, the worker health at WSL `127.0.0.1:5102/health`, and Windows access to the installation record's WSL cache paths. Exercise `ddistro_reign stop` then `start`; both the server and its owned worker must stop while other distro services remain intact. Check fresh logs at `/var/lib/dwemerdistro/reign/logs/server.log`.

## Preserved state and limits

Runtime versions live under `/opt/dwemerdistro/reign/versions`; dependency/model versions are keyed by their source locks. Player data lives outside them at `/var/lib/dwemerdistro/reign`. The Windows record is `%USERPROFILE%/.reign/installation.json`. Its WSL paths must refer to the same selected local distro; arbitrary network shares are rejected.

Do not overwrite an existing standalone Windows installation or another distro's record. Inventory and migrate that installation deliberately, including its secrets; Windows DPAPI secrets cannot simply be decrypted in Linux. Failed activation retains the previous runtime. Code rollback does not undo database migrations. Do not delete retained databases, versions, module backups, or dependency mods as deployment cleanup.

Report the exact source branches, reports, destination paths, binary versions/hashes, health and lifecycle results, and remaining in-game/native-renderer acceptance. Local deployment does not authorize a merge, release, version bump, or game launch.
