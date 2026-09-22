#!/bin/sh
# Install reviewed control scripts into the dedicated ReignServer distro.
set -eu
[ "$#" -eq 3 ] || { echo 'Expected control, rollback, and service paths.' >&2; exit 64; }
install -m 0755 "$1" /usr/local/bin/reignctl.next
sed -i 's/\r$//' /usr/local/bin/reignctl.next
mv -f /usr/local/bin/reignctl.next /usr/local/bin/reignctl
install -m 0755 "$2" /usr/local/bin/reign_versions.py.next
mv -f /usr/local/bin/reign_versions.py.next /usr/local/bin/reign_versions.py
install -m 0644 "$3" /etc/systemd/system/reignserver.service.next
sed -i 's/\r$//' /etc/systemd/system/reignserver.service.next
mv -f /etc/systemd/system/reignserver.service.next /etc/systemd/system/reignserver.service
systemctl daemon-reload
