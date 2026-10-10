#!/bin/sh
# WSL login -f needs PAM registration and the systemd user session bus.
# Run only during authorized installation/preparation, never from the launcher.
set -eu
[ "$(id -u)" = 0 ] || { echo 'Reign session preparation requires root.' >&2; exit 1; }
missing=0
for package in libpam-systemd dbus-user-session; do
    [ "$(dpkg-query -W -f='${Status}' "$package" 2>/dev/null || true)" = 'install ok installed' ] || missing=1
done
if [ "$missing" = 1 ]; then
    apt-get update -qq
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends libpam-systemd dbus-user-session
fi
for package in libpam-systemd dbus-user-session; do
    [ "$(dpkg-query -W -f='${Status}' "$package" 2>/dev/null || true)" = 'install ok installed' ] || {
        echo "Reign WSL session prerequisite is not installed: $package" >&2
        exit 1
    }
done
# Do not rewrite an administrator's PAM configuration or hide its failure.
grep -Eq '^[[:space:]]*session[[:space:]]+[^#]*pam_systemd\.so([[:space:]]|$)' /etc/pam.d/common-session || {
    echo 'Reign WSL login sessions require active pam_systemd.so in /etc/pam.d/common-session. Review the PAM package configuration.' >&2
    exit 1
}
