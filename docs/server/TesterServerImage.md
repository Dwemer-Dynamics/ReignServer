# Tester server image and PostgreSQL major policy

The tester package installs Reign into its own WSL distro, imported from a
prepared, audited image. Reign is a self-contained .NET 10 server; the image
carries no PHP. The image identity is single-sourced in `release/release.json`
under `serverImage`:

- Debian 13 (trixie), from `debian:trixie-slim` linux/amd64 on
  registry-1.docker.io (index, manifest and rootfs layer digests pinned there).
- PostgreSQL 17 (`postgresql-17`), socket-only on port 5432.
- Debian's Python 3.13 for the vector-worker venv, and `libicu76`.

This matches the current DwemerDistro v3.4.14 Debian 13 / PostgreSQL 17
baseline for OS release and database major only; the shared distro is not
imported and no PHP is required. `base.imageCreatedUtc` is the upstream image's
creation date, not an observation or test date.

`prepare-image.sh` checks its constants, the installed Python/PostgreSQL/ICU and
the dependency venv's Python ABI against that section, and writes the
`reign-tester-image-v2` marker at `/etc/reign-tester-image.json`.
`audit_image.py` requires base provenance with matching `ociIndex`,
`ociManifest`, `rootfsLayerSha256`, `rootfsLayerBytes`, `reference` and
`platform`, the v2 marker, exactly one PostgreSQL major and a venv built for the
image Python. It emits a `reign-tester-image-receipt-v2` receipt, and
`build_package.py` refuses any receipt that does not match `serverImage`, so an
older Debian 12 / PostgreSQL 15 image cannot be packaged.

## Updates and existing PostgreSQL 15 installations

Database majors are never migrated in place. For an owned installation,
`-Update` (including `-VerifyOnly` and `-Resume` of an update) reads the distro's
image marker and PostgreSQL cluster directories before the journal, snapshot or
any service stop, and `initialize-server.sh` repeats the guard before ownership,
scripts, services or the database change. Any cluster other than the pinned
major, or a marker with a different schema, Debian release, PostgreSQL major,
Python minor or ICU package, is refused with nothing changed. A same-major
update keeps its installed distro, so it does not require the package's base
digest; fresh and resumed fresh installs import this package's image and must
match it exactly. An update still needs matching dependency locks.

The tester procedure for carrying data from a Debian 12 / PostgreSQL 15 install
is the "Older Debian 12 / PostgreSQL 15 tester installations" section of the
packaged `INSTALL.html`. Its safety rules:

- Stop Reign writes, take a custom-format `pg_dump` with the old distro's own
  PostgreSQL 15 tools, then stop PostgreSQL and terminate the distro before
  `wsl --export`. The old installation is deliberately left stopped.
- Keep the complete private `data` directory (settings and API keys, campaigns,
  save sync, vectors, portraits), the whole-distro export, the old distro
  registration, its server folder, installation record and module. Nothing is
  deleted; the copies stay in the tester's private profile.
- Install fresh into a new distro and empty server folder, and restore only
  before the first server start, after checking that the new `reign` database
  has no relations and `data` has only installer-created files. Restore with
  `pg_restore --exit-on-error --single-transaction --no-owner --role=reign`,
  then the data files except the new `windows-bridge.json` and logs, then
  `reignctl repair-permissions`. The server applies its migrations on first
  start.

Evidence so far is limited to isolated task clusters: a PostgreSQL 15.20 to
17.12 logical metadata-schema fixture, and on 17.12 the real `reign_meta.sql`,
a storage-version migration rerun and a custom dump/restore. That procedure is
manual and unqualified. It is not an automatic migration, a hand copy is not
complete acceptance, and a dump restore does not replace in-game verification.
