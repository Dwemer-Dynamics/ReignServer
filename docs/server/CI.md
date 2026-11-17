# Server CI

GitHub Actions runs two workflows for this repository. Neither uses repository
secrets, paid model providers or the user's runtime data.

## `reign-channels.yml` (pull requests to `unstable`, `dev`, `reign`)

- Checks the promotion path (`unstable` -> `dev` -> `reign`).
- Builds the public self-contained Linux server with `scripts/Build-Linux.py`.

## `reign-server-tests.yml` (pull requests to `unstable`, `dev`, `reign`; pushes to `unstable`)

`unit-tests` (no database):

- Builds `shared/Reign.Core.Contracts`.
- Runs `dotnet test` for `shared/Reign.Relationships.Tests`.
- Runs the stdlib `unittest` suite in `tests/memory_accuracy`.

`self-tests` (disposable PostgreSQL service container):

- Starts `postgres:16` with a UTF-8 (`C.UTF-8`) cluster and creates a non-superuser
  `reign` login role with `CREATEDB`. The server creates and owns the isolated
  `ReignValidation` database itself, as it does in the canonical validation lane.
- Checks out the public `Dwemer-Dynamics/Reign` client repository and places this
  server checkout inside it as `Reign/ReignServer`, the same paired layout the
  canonical validator uses. The client branch is the PR's head branch name when
  Reign has a branch of that name (paired PRs), otherwise the target branch.
  The client's `Directory.Build.props` therefore enables the packaged
  `verification_contracts` source snapshot that the source-contract checks read.
- Builds `ReignServer.csproj` (Release, `linux-x64`) and runs, with
  `REIGN_VALIDATION_MODE=1`, `REIGN_DB_NAME=ReignValidation` and a per-suite
  `REIGN_DATA_ROOT`:
  - the core `--run-verification --tier quick` gate the validator runs for core changes;
  - the module-manifest self-tests from the client's `reign.modules.json`:
    save sync, relationships, rumors, motive conversation, diplomacy, court,
    rebellion, world test, portrait derivatives, and the `government`,
    `spymaster` and offline `interaction_architecture` verification suites.
- Uploads each suite's JSON report as the `reign-server-self-test-reports` artifact.

## Not covered by CI

- The Bannerlord client mod build (`ReignBeta`), which needs licensed game references.
  Checks that need the built client assembly can only pass in the canonical local lane.
- In-game, native-operation and Verification Lab `game` tier checks.
- The native portrait renderer and other Windows-only portrait tooling.
- `live-llm` tiers and anything that calls a paid or hosted provider.
- The vector worker, Apache proxy, `reignctl` lifecycle, DwemerDistro install and
  deployment flows.
- Reign MCP tests, which live in the client repository.

CI is additional evidence. It does not replace the canonical local validator
(`reign_validate`) required by `AGENTS.md`.
