#!/bin/sh
# Install reviewed control scripts into the dedicated ReignServer distro.
set -eu
[ "$#" -eq 3 ] || { echo 'Expected control, rollback, and service paths.' >&2; exit 64; }
# Repair minimal imports that omitted recommended login/session dependencies.
# Finish prerequisites before replacing any installed service controls.
install -d -o root -g root -m 0755 /usr/local/lib/reign
install -o root -g root -m 0755 "$(dirname "$1")/prepare-systemd-sessions.sh" /usr/local/lib/reign/prepare-systemd-sessions.sh.next
sed -i 's/\r$//' /usr/local/lib/reign/prepare-systemd-sessions.sh.next
mv -f /usr/local/lib/reign/prepare-systemd-sessions.sh.next /usr/local/lib/reign/prepare-systemd-sessions.sh
/usr/local/lib/reign/prepare-systemd-sessions.sh
install -m 0755 "$1" /usr/local/bin/reignctl.next
sed -i 's/\r$//' /usr/local/bin/reignctl.next
mv -f /usr/local/bin/reignctl.next /usr/local/bin/reignctl
install -m 0755 "$2" /usr/local/bin/reign_versions.py.next
mv -f /usr/local/bin/reign_versions.py.next /usr/local/bin/reign_versions.py
install -m 0644 "$3" /etc/systemd/system/reignserver.service.next
sed -i 's/\r$//' /etc/systemd/system/reignserver.service.next
mv -f /etc/systemd/system/reignserver.service.next /etc/systemd/system/reignserver.service
install -d -o root -g root -m 0755 /usr/local/lib/reign
install -o root -g root -m 0755 "$(dirname "$1")/reign-control.py" /usr/local/lib/reign/reign-control.py.next
sed -i 's/\r$//' /usr/local/lib/reign/reign-control.py.next
mv -f /usr/local/lib/reign/reign-control.py.next /usr/local/lib/reign/reign-control.py
# No shell, arbitrary unit, arguments or general reignctl privilege is granted.
printf '%s\n' 'www-data ALL=(root) NOPASSWD: /usr/bin/systemctl --no-block start reignserver.service, /usr/bin/systemctl --no-block stop reignserver.service' > /etc/sudoers.d/reign-control.next
chmod 0440 /etc/sudoers.d/reign-control.next
visudo -cf /etc/sudoers.d/reign-control.next
mv -f /etc/sudoers.d/reign-control.next /etc/sudoers.d/reign-control
systemctl daemon-reload
/usr/local/bin/reignctl configure-web
