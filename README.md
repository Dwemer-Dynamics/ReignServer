# ReignServer

ReignServer runs locally beside Bannerlord Reign. This public repository contains the server, shared contracts, native portrait generator, vector worker and release packaging sources. The game module is maintained in [Reign](https://github.com/Dwemer-Dynamics/Reign). Internal ReignBeta names remain for save compatibility.

For local development, use sibling checkouts named Reign and ReignServer. Run `ReignRelease/Connect-Repositories.ps1`, then use the canonical Reign MCP validator from Reign. The source junctions keep integration tooling working while every file has one Git owner. Commit server changes here and client changes in Reign.

Installation packages include separate verified runtime/model/content payloads. These are distributed separately from Git through a configurable private host or the complete offline package. Each user supplies their own AI credentials. The complete Windows preview package has passed an isolated local reinstall, save migration and portrait-discovery repair. On 2026-09-14 the user confirmed the installed build was working and authorized the initial source uploads, superseding the earlier upload hold in the initial workflow notes. Private download hosting and execution on a second computer remain separate follow-up work.
