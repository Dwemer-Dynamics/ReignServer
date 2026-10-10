#!/usr/bin/python3
"""Apache CGI for the dedicated, managed Reign service. Never reads private data."""
import ipaddress
import json
import os
from pathlib import Path
import socket
import struct
import subprocess
import sys
import urllib.request

UNIT = "reignserver.service"
UI_ROOT = Path("/var/www/html/ReignServer/runtime/current/ui")
HOSTS = {"127.0.0.1:8089", "localhost:8089", "[::1]:8089"}


def local_peers():
    # WSL NAT forwards Windows localhost traffic from its exact default gateway.
    peers = {"127.0.0.1", "::1"}
    for line in Path("/proc/net/route").read_text().splitlines()[1:]:
        fields = line.split()
        if len(fields) >= 4 and fields[1] == "00000000" and int(fields[3], 16) & 2:
            peers.add(socket.inet_ntoa(struct.pack("<I", int(fields[2], 16))))
    return peers


def health():
    try:
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open("http://127.0.0.1:5101/health", timeout=1) as response:
            data = json.loads(response.read(65536))
        return data.get("ok") is True and data.get("service") == "BannerlordReignServer" and data.get("protocolVersion") == 1
    except (OSError, ValueError):
        return False


def service_state(run, probe):
    result = run(["/usr/bin/systemctl", "show", UNIT, "--property=LoadState,ActiveState,SubState,MainPID"],
                 capture_output=True, text=True, timeout=5, check=False)
    if result.returncode:
        raise RuntimeError("Cannot inspect the managed Reign service.")
    fields = dict(line.split("=", 1) for line in result.stdout.splitlines() if "=" in line)
    if fields.get("LoadState") != "loaded":
        raise RuntimeError("The managed Reign service is not installed.")
    active = fields.get("ActiveState")
    if active == "activating":
        return {"ok": True, "state": "starting"}
    if active == "deactivating":
        return {"ok": True, "state": "stopping"}
    ready = probe()
    if active == "active" and int(fields.get("MainPID", "0")) > 1:
        return {"ok": True, "state": "on" if ready else "starting"}
    if active in {"inactive", "failed"} and fields.get("MainPID") == "0" and not ready:
        return {"ok": True, "state": "off", "message": "Server start failed. Retry or inspect server logs." if active == "failed" else "Server off"}
    raise RuntimeError("Server ownership or state could not be verified. Use the ReignServer launcher.")


def handle(environ, body, run=subprocess.run, probe=health, peers=None, ui_root=UI_ROOT):
    headers = {"Cache-Control": "no-store", "X-Content-Type-Options": "nosniff"}

    def reply(status, value):
        return status, {**headers, "Content-Type": "application/json; charset=utf-8"}, json.dumps(value)

    try:
        peer = str(ipaddress.ip_address(environ.get("REMOTE_ADDR", "")))
        if peer not in (local_peers() if peers is None else peers) or environ.get("HTTP_HOST") not in HOSTS:
            return reply(403, {"ok": False, "error": "Server controls require local Control Center access."})
        method, route = environ.get("REQUEST_METHOD"), environ.get("PATH_INFO", "")
        if method == "GET" and route == "/offline":
            html = (ui_root / "server-offline.html").read_text(encoding="utf-8")
            html = html.replace("@SERVER_CONTROL_SCRIPT@", (ui_root / "server-control.js").read_text(encoding="utf-8"))
            return 200, {**headers, "Content-Type": "text/html; charset=utf-8", "Content-Security-Policy": "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'"}, html
        if method == "GET" and route == "/status":
            return reply(200, service_state(run, probe))
        if method != "POST" or route != "/power":
            return reply(404, {"ok": False, "error": "Unknown server control route."})
        if (environ.get("HTTP_ORIGIN") != "http://" + environ.get("HTTP_HOST", "")
                or environ.get("HTTP_X_REIGN_CONTROL") != "1"
                or environ.get("CONTENT_TYPE", "").split(";", 1)[0].strip() != "application/json"):
            return reply(403, {"ok": False, "error": "Use the on/off button in the local Control Center."})
        if not 0 < len(body) <= 1024:
            return reply(400, {"ok": False, "error": "Invalid server control request."})
        payload = json.loads(body)
        if not isinstance(payload, dict) or set(payload) != {"action"} or payload["action"] not in {"start", "stop"}:
            return reply(400, {"ok": False, "error": "Only start and stop are supported."})
        action = payload["action"]
        current = service_state(run, probe)
        if (action == "start" and current["state"] in {"on", "starting"}) or (action == "stop" and current["state"] in {"off", "stopping"}):
            return reply(200, current)
        result = run(["/usr/bin/sudo", "-n", "/usr/bin/systemctl", "--no-block", action, UNIT],
                     capture_output=True, text=True, timeout=10, check=False)
        if result.returncode:
            return reply(503, {"ok": False, "error": "The managed service could not " + action + ". Inspect ReignServer service logs."})
        return reply(202, {"ok": True, "state": "starting" if action == "start" else "stopping"})
    except (ValueError, TypeError):
        return reply(400, {"ok": False, "error": "Invalid server control request or service state."})
    except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
        return reply(503, {"ok": False, "error": str(error) if isinstance(error, RuntimeError) else "Server control is unavailable. Inspect the managed service installation."})


def main():
    try:
        length = int(os.environ.get("CONTENT_LENGTH", "0"))
        body = sys.stdin.buffer.read(length) if 0 < length <= 1024 else b""
    except ValueError:
        body = b""
    status, headers, content = handle(os.environ, body)
    sys.stdout.write("Status: " + str(status) + "\r\n")
    for name, value in headers.items():
        sys.stdout.write(name + ": " + value + "\r\n")
    sys.stdout.write("\r\n" + content)


if __name__ == "__main__":
    main()
