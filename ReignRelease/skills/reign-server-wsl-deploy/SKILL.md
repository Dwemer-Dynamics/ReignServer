---
name: reign-server-wsl-deploy
description: Deploy or update only ReignServer inside the local DwemerDistro WSL runtime, preserving its database, campaigns, settings, secrets, portraits, and Windows client. Use for server-only Reign fixes and service validation.
---

# ReignServer WSL deployment

Read the paired repository AGENTS.md and confirm the exact checkout, branch, and WSL distro. The standard local distro is `DwemerAI4Skyrim3`. Keep edits in ReignServer; the private Reign sibling provides the canonical validator.

For source development, obtain the canonical validation plan/status, run the manifest-selected validation, then `ReignMcp/scripts/reign-validate.ps1 -LinuxServer -Restore -RequestingTaskId <task UUID>`. Use only its successful `reign-linux-build-v1` report. Sync reviewed server source to `/var/www/html/ReignServer`, preserving `.git`, ignored `data/`, and ignored `runtime/`, and run `sudo ddistro_reign prepare` for the pinned dependencies.

Deploy with `ReignRelease/Deploy-LocalWsl.ps1 -Workspace <Reign checkout> -LinuxBuildReport <report path> -Distro <name> -SkipClient`. This activates the exact checked artifact and checks versioned HTTP health. A normal published-channel update instead uses `sudo ddistro_server update reign --branch reign`, `dev`, or `unstable`; preserve the current channel unless the user requests a change. Never substitute `main` as a real Reign branch.

Verify public port 8089, internal server 5101, worker 5102, and fresh `/var/www/html/ReignServer/data/logs/server.log`. The worker must report a loaded 384-dimension embedding model. Stop/start only through `ddistro_reign`, and verify the owned worker stops too. Never stop MiniMe on 8082 or alter unrelated mod databases.

Data and secrets remain in `/var/www/html/ReignServer/data`; executables and dependencies live in `/var/www/html/ReignServer/runtime`. Both folders stay inside the checkout and survive source updates and uninstall. Never run campaign/destructive/provider tests on active data. A process/health check is not in-game or native portrait-renderer proof. Report exact source, artifact, version, deployment, validation, and remaining limits.
