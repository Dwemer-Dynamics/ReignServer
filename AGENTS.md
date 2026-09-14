# ReignServer local workflow

This public repository owns the local Reign server, shared contracts/helpers, native portrait tooling and release packaging. Its only publishing destination is https://github.com/Dwemer-Dynamics/ReignServer.git, main. Private client source, credentials, campaigns, verification workspaces, built binaries, model downloads and bulk portrait payloads do not belong in Git.

For paired development, clone the private Reign repository beside this checkout and run ReignRelease/Connect-Repositories.ps1. Follow ../Reign/AGENTS.md and obtain the Reign MCP validation plan before code changes. Use the shared canonical validation engine and its OS lease; never start a second server or bypass provider/campaign gates. All development and testing stay local. Preserve source/save identities and edit shared source here only.

Use the Project Memory skill for substantive work and register this exact checkout independently. Stage explicit reviewed paths only. Validate and audit both repositories before publication. Initial uploading is held until the complete package has clean-install execution evidence on another computer.
