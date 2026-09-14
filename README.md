# ReignServer

ReignServer runs locally beside Bannerlord Reign. This public repository contains the server, shared contracts, native portrait generator, vector worker and release packaging sources. The game module is maintained in [Reign](https://github.com/Dwemer-Dynamics/Reign). Internal ReignBeta names remain for save compatibility.

For local development, use sibling checkouts named Reign and ReignServer. Run `ReignRelease/Connect-Repositories.ps1`, then use the canonical Reign MCP validator from Reign. The source junctions keep integration tooling working while every file has one Git owner. Commit server changes here and client changes in Reign.

Installation packages include separate verified runtime/model/content payloads. These are distributed through a separately configured private host, not committed to this repository. Each user supplies their own AI credentials. Packaging and clean-computer acceptance are still in progress; no launch-ready installer is claimed by this source import.
