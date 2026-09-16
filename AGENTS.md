# ReignServer local workflow

This public repository owns the local Reign server, shared contracts/helpers, native portrait tooling and release packaging. Its only publishing destination is https://github.com/Dwemer-Dynamics/ReignServer.git. Development targets unstable, beta promotion targets dev, and production/default is reign. Use one draft PR per repository and coordinate paired client/server promotion. Private client source, credentials, campaigns, verification workspaces, built binaries, model downloads and bulk portrait payloads do not belong in Git.

For paired development, clone the private Reign repository beside this checkout and run scripts/Connect-Repositories.ps1. Follow ../Reign/AGENTS.md and obtain the Reign MCP validation plan before code changes. Use the shared canonical validation engine and its OS lease; never start a second server against the same runtime data or bypass provider/campaign gates. DwemerDistro runs the Linux server under its managed lifecycle; the server target is Linux-only; Windows is reserved for the game client and portrait renderer. All development and testing stay local. Preserve source/save identities and edit shared source here only.

Stage explicit reviewed paths only. Validate and audit both repositories before publication. Source publication is authorized when the user requests it; keep implementation PRs as drafts targeting unstable. A source PR does not establish clean-install or in-game acceptance.

Approved first-party shared portraits belong in the private Reign module at ReignBeta/PortraitCache/_shared, with the tracked ReignBeta/PortraitCache/shared-portrait-inventory.json. Deploy and package the verified module inventory; do not rely on a separate or machine-local first-party portrait payload.

## DwemerDistro operations

### Work and build

- Confirm both sibling repositories' origins, branches, HEADs and dirty state before changes. Use the private Reign checkout as the canonical MCP workspace; edit public server source here. Compare current `unstable`, `dev` and `reign` heads before promotion rather than assuming channel parity.
- Read the installed `reign-server-wsl-deploy` skill when available; use `reign-full-deploy` for combined client/server work. The checked-in deployment entry point is `scripts/Deploy-LocalWsl.ps1`. See the [paired client workflow](../Reign/AGENTS.md#working-and-deploying-with-dwemerdistro) for client builds, dependency/module inventory and portrait assets.
- Obtain the canonical validation plan and status, use the current task UUID and manifest-selected scope, then produce the Linux artifact through the same build lease. Do not call `dotnet build`, `dotnet test`, MSBuild or verification executables directly. Documentation-only changes use the Tier 1 no-build plan.

```powershell
# Run from the private Reign checkout after obtaining its validation plan/status.
& .\ReignMcp\scripts\reign-validate.ps1 -Profile changed -ChangedPath $changedPaths -Restore -RequestingTaskId $env:CODEX_THREAD_ID
& .\ReignMcp\scripts\reign-validate.ps1 -LinuxServer -Restore -RequestingTaskId $env:CODEX_THREAD_ID
```

Use `product` for requested product-wide confidence and `all` where the manifest requires it. Select the same WSL distro for validation (`REIGN_WSL_DISTRO`) and deployment (`-Distro`). Never bypass an occupied validation lease or use active player data for verification.

### Deploy local changes

1. Resolve the selected local distro; the deployment script defaults to `DwemerAI4Skyrim3`. Confirm compatible `ddistro_server` and `ddistro_reign` Core helpers, PostgreSQL, Apache and UTF-8 setup exist.
2. Sync only the reviewed server source into `/var/www/html/ReignServer`, preserving its `.git`, ignored `data/` and ignored `runtime/`. Run `sudo ddistro_reign prepare` inside that distro for pinned dependencies and models.
3. Activate the exact successful `reign-linux-build-v1` artifact without rebuilding or changing source:

```powershell
# Set variables to the actual checkout, selected distro and successful Linux report.
& .\scripts\Deploy-LocalWsl.ps1 -Workspace $reignCheckout -LinuxBuildReport $linuxReport -Distro $distro -SkipClient
```

This command is relative to this server checkout. A combined deploy additionally needs the successful product/all report and the licensed Bannerlord directory. Preserve existing Windows installation records and client files during a server-only deploy.

### Install or update a published channel

Run these inside the selected WSL distro. `dev` is the launcher-supported Reign channel; preserve an existing installation's channel unless the user requests a switch. Reign has no `main` channel.

```bash
sudo ddistro_server status reign --json
# Fresh installation only:
sudo ddistro_server install reign --branch dev
# Existing installation on Dev:
sudo ddistro_server update reign --branch dev
```

The manager builds public source through `scripts/Build-Linux.py` under its operation lock. This installed-channel path is distinct from agent development, which uses the paired canonical validator and validated-artifact deployment above.

Installation/activation can health-check a candidate and then restore the previous stopped state. An installed server is not necessarily running. The launcher owns normal distro startup/shutdown. For an authorized command-line service probe, use the corresponding startup sequence:

```bash
sudo ddistro_server startup reign
sudo service apache2 start
sudo ddistro_reign start
curl --fail http://127.0.0.1:8089/health
curl --fail http://127.0.0.1:5102/health
```

An HTTP 503 before managed startup is not by itself an installation failure. Check actual listeners and fresh logs. On shared or clean-test machines, inventory existing services/ports first and avoid changing another distro's services or forwarding configuration.

### Storage, rollback and acceptance

- `/var/www/html/ReignServer/data` owns private settings/API keys, campaigns, logs and vectors. `/var/www/html/ReignServer/runtime` owns retained executables, models and dependency environments; `runtime/current` selects the active version. Preserve both directories during source updates and uninstall. PostgreSQL's lowercase `reign` database remains in PostgreSQL's normal storage, not inside the checkout.
- Default locale is `C.UTF-8`; both `template1` and `reign` must use UTF-8. The database uses port 5432, Apache exposes 8089, Reign binds loopback 5101 and its worker uses loopback 5102. Do not interfere with MiniMe on 8082 or sibling mod services/databases.
- Inspect `sudo ddistro_reign rollback-list`, then use `sudo ddistro_reign rollback <retained-version-id>` for an authorized compatible rollback. Code rollback does not undo database migrations. Preserve retained versions and player data; never restore a database merely to make a binary rollback succeed.
- Use `sudo ddistro_reign stop` / `start` for lifecycle checks. The owned server and vector worker must stop together. `sudo ddistro_reign repair-permissions` repairs inherited campaign access; do not substitute broad world-writable permissions or clear ACLs.
- Verify public health, version agreement, `databaseName: reign`, current schema and the worker's loaded 384-dimensional model after warm-up. Inspect fresh `data/logs/server.log`; server/LLM event logs are also available through `/api/logs` and Distro Debugger. Never print settings or credentials during diagnosis.
- Record exact source commits, artifact/report fingerprints and deployed hashes. A clean-install probe uses a fresh isolated distro and verified artifacts, then restores prior machine state. Report server lifecycle, full installer, launcher UI, paid-provider and in-game/native-renderer coverage separately. No game launch, release or merge is implied by deployment authorization.
