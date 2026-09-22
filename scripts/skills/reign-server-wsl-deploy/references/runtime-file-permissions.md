# ReignServer runtime file access

Use this procedure when server work creates or changes files under `/var/www/html/ReignServer/data` or `runtime` that cross process identities, or when diagnosing an access failure. It also applies to the client deployment's `data/windows-bridge.json` write. Keep the check limited to affected paths. Planning and review are read-only; this procedure does not itself authorize deployment, service restart, or broad permission repair.

## Establish the access contract

For each affected path, identify the creating, reading, writing, and repair processes. Inspect the actual WSL distro's effective users, groups, parent traversal, ownership, modes, ACLs if used, and creation umask. The local deploy script writes the Windows bridge as `dwemer` and invokes `ddistro_reign` as root; discover the managed server and worker identities from the installed Core helpers and running processes. Do not assume a Herika `www-data` owner/group or copy its modes into Reign.

Keep secret vault keys private to their owner: `LinuxSecretVault` requires an owner-only regular `.key` file. A shared-group rule for ordinary state must not relax that contract. Retain an existing valid group rather than replacing it with a parent group. Group write access does not imply that an unprivileged peer may change another account's ownership or mode.

## Verify and repair the affected paths

Before a dependent prepare, migration, bridge write, or activation, check existing paths as the identities that will use them. For live files, use non-destructive read/access probes; test creation and writes only with disposable sibling files on the Linux filesystem. Exercise cross-account creation in both relevant directions and restrictive umask `0077` in isolated fixtures when the change affects creation behavior. Check parent directory execute permission as well as file access. A root-run update or successful HTTP request is insufficient evidence of worker access.

If repair is needed and authorized by the deployment, use the installed `ddistro_reign repair-permissions` route for inherited campaign access when it fits the affected path. For other paths, first establish the installation's ownership contract and repair only those paths through the account that has authority. Perform repair before opening the file. Do not recursively chown/chmod the server tree, make state world-writable, or add executable bits to ordinary files. Preserve `.git`, `data`, `runtime`, settings, vaults, campaigns, and retained versions during source sync.

Preserve active lock-file identity and contents. Do not delete, replace, relocate, or bypass a lock to turn contention into success. Distinguish a missing optional file from an unreadable required file and report an actionable failure when the current identity cannot repair it. A suppressed warning must not turn a failed operation into success.

After sync, prepare, and activation, repeat the affected access probes as the actual server and worker identities because those steps may create files under another account. Report the accounts, path classes, creation order, relevant mode/group, access results, any scoped repair, and untested cases. Do not print file contents, settings, keys, or credentials.
