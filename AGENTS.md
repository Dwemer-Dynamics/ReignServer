# ReignServer local workflow

This public repository owns the local Reign server, shared contracts/helpers, native portrait tooling and release packaging. Its only publishing destination is https://github.com/Dwemer-Dynamics/ReignServer.git. Development targets unstable, beta promotion targets dev, and production/default is reign. Use one draft PR per repository and coordinate paired client/server promotion. Private client source, credentials, campaigns, verification workspaces, built binaries, model downloads and bulk portrait payloads do not belong in Git.

For paired development, clone the private Reign repository beside this checkout and run ReignRelease/Connect-Repositories.ps1. Follow ../Reign/AGENTS.md and obtain the Reign MCP validation plan before code changes. Use the shared canonical validation engine and its OS lease; never start a second server against the same runtime data or bypass provider/campaign gates. DwemerDistro runs the Linux server under its managed lifecycle; the server target is Linux-only; Windows is reserved for the game client and portrait renderer. All development and testing stay local. Preserve source/save identities and edit shared source here only.

Stage explicit reviewed paths only. Validate and audit both repositories before publication. Source publication is authorized when the user requests it; keep implementation PRs as drafts targeting unstable. A source PR does not establish clean-install or in-game acceptance.

Approved first-party shared portraits belong in the private Reign module at ReignBeta/PortraitCache/_shared, with the tracked ReignBeta/PortraitCache/shared-portrait-inventory.json. Deploy and package the verified module inventory; do not rely on a separate or machine-local first-party portrait payload.
