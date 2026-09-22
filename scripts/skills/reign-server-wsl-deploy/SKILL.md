---
name: reign-server-wsl-deploy
description: Deploy or update only ReignServer inside its own ReignServer WSL distro, preserving its database, campaigns, settings, secrets, portraits, and Windows client. Use for server-only Reign fixes and service validation.
---

# ReignServer WSL deployment

Read the paired repository AGENTS.md and confirm the exact checkout, branch, and WSL distro. The local distro is `ReignServer`, registered separately with its own virtual disk. Keep edits in ReignServer; the private Reign sibling provides the canonical validator.

For source development, obtain the canonical validation plan/status, run the manifest-selected validation, then `ReignMcp/scripts/reign-validate.ps1 -LinuxServer -Restore -RequestingTaskId <task UUID>`. Use only its successful `reign-linux-build-v1` report. Sync reviewed server source to `/var/www/html/ReignServer`, preserving `.git`, ignored `data/`, and ignored `runtime/`, and run `sudo reignctl prepare` for the pinned dependencies.

Deploy with `scripts/Deploy-LocalWsl.ps1 -Workspace <Reign checkout> -LinuxBuildReport <report path> -Distro ReignServer -BannerlordPath <installed game directory> -SkipClient`. This activates the exact checked artifact, records the installed Windows module path in the private WSL bridge, and checks versioned HTTP health. Keep source updates on the selected channel and validate their artifacts before deployment.

The managed launcher selects `data/save-sync` for migrated Save Sync ledgers and the bridge's `bannerlordModules` path for the installed shared portrait library. Check that the campaign audit reports registered points and matching physical saves, and that a known shared portrait URL returns an image after migration.

Verify public port 8089, internal server 5101, worker 5102, and fresh `/var/www/html/ReignServer/data/logs/server.log`. The worker must report a loaded 384-dimension embedding model. Stop/start through `reignctl` or `reignserver.service`, and verify the owned worker stops too.

Data and secrets remain in `/var/www/html/ReignServer/data`; executables and dependencies live in `/var/www/html/ReignServer/runtime`. Both folders stay inside the checkout and survive source updates and uninstall. Never run campaign/destructive/provider tests on active data. A process/health check is not in-game or native portrait-renderer proof. Report exact source, artifact, version, deployment, validation, and remaining limits.

When a change or deployment affects files shared by startup, the server, the vector worker, maintenance commands, or the client deployment's Windows bridge writer, follow [runtime file access](references/runtime-file-permissions.md). Check affected paths before dependent operations and again afterward.

ReignServer is Linux-only. The client owns Windows portrait-helper builds; no Windows server installer or packaged Windows vector worker is supported. Canonical server verification runs inside WSL with ReignValidation and run-owned data.
